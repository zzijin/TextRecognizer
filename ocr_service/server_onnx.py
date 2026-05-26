"""
ONNX-based OCR FastAPI server.

Same API as server.py but uses ONNX Runtime instead of PaddlePaddle.
Run on port 8081 to avoid conflict with the Paddle server on 8080.

Device selection:
    Set ONNX_DEVICE env var: "dml" (DirectML GPU, default), "cpu", or "cuda".
    Example: ONNX_DEVICE=cpu python server_onnx.py

Endpoints:
    GET  /health              — health check (includes device info)
    POST /ocr/server_rec       — single model: PP-OCRv5_server_rec
    POST /ocr/mobile_rec       — single model: PP-OCRv5_mobile_rec
    POST /ocr/en_mobile_rec    — single model: en_PP-OCRv5_mobile_rec
    POST /ocr/cross_validate   — 3-model cross-validation
"""

import base64
import io
import os
import sys
import time
import logging
from collections import defaultdict
from contextlib import asynccontextmanager

# ── logging ─────────────────────────────────────────────────────────────────

LOG_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "logs")
os.makedirs(LOG_DIR, exist_ok=True)

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    handlers=[
        logging.StreamHandler(sys.stdout),
        logging.FileHandler(os.path.join(LOG_DIR, "server_onnx.log"), encoding="utf-8"),
    ],
)
logger = logging.getLogger("onnx_ocr_service")
logger.info(f"======== ONNX OCR Service starting ========")
logger.info(f"PID={os.getpid()}, Python={sys.version.split()[0]}")

from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel
import numpy as np
from PIL import Image

from onnx_ocr import OnnxOcrEngine

# ── paths ───────────────────────────────────────────────────────────────────

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
MODELS_DIR = os.path.join(BASE_DIR, "models", "onnx_models")
OFFICIAL_DIR = os.path.join(BASE_DIR, "models", "official_models")

# ── engine ──────────────────────────────────────────────────────────────────

engine: OnnxOcrEngine = None
_request_count = defaultdict(int)
_error_count = defaultdict(int)
_start_time = time.time()
_lock = __import__("threading").Lock()


def _server_stats():
    uptime = time.time() - _start_time
    total = sum(_request_count.values())
    errors = sum(_error_count.values())
    return {
        "uptime_s": round(uptime, 0),
        "requests": total,
        "errors": errors,
        "device": engine.device_name if engine else "not_initialized",
    }


# ── lifespan ────────────────────────────────────────────────────────────────

@asynccontextmanager
async def lifespan(app: FastAPI):
    global engine
    device = os.environ.get("ONNX_DEVICE", "dml")
    logger.info(f"Loading ONNX OCR engine (device={device}) ...")
    engine = OnnxOcrEngine(
        models_dir=MODELS_DIR,
        official_dir=OFFICIAL_DIR,
        device=device,
    )
    logger.info(f"Engine ready: {engine.is_ready()}, device: {engine.device_name}")
    yield
    logger.info(f"Shutting down, {_server_stats()}")


app = FastAPI(title="ONNX OCR Service", lifespan=lifespan)
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)


# ── helpers ─────────────────────────────────────────────────────────────────

class OCRRequest(BaseModel):
    image: str  # base64-encoded JPEG/PNG


def _decode_image(b64: str) -> np.ndarray:
    """Decode base64 image to BGR numpy array."""
    if "," in b64:
        b64 = b64.split(",", 1)[1]
    data = base64.b64decode(b64)
    img = Image.open(io.BytesIO(data)).convert("RGB")
    arr = np.array(img)
    return arr[:, :, ::-1].copy()  # RGB -> BGR


def _box_to_list(box) -> list:
    """Convert box (N,2) array to list of [x,y] pairs."""
    if hasattr(box, "tolist"):
        return box.tolist()
    if isinstance(box, list):
        return box
    return box.tolist()


# ── endpoints ───────────────────────────────────────────────────────────────

@app.get("/health")
def health():
    """Health check. Returns device info and request stats."""
    return {
        "status": "ok" if (engine and engine.is_ready()) else "loading",
        "device": engine.device_name if engine else "not_initialized",
        "models_ready": engine.is_ready() if engine else False,
        "stats": _server_stats(),
    }


def _run_single(pipeline_key: str, model_name: str, req: OCRRequest):
    """Run single-model recognition, same response format as Paddle server."""
    _request_count[pipeline_key] += 1
    t0 = time.time()
    logger.info(
        f"[{pipeline_key}] request #{_request_count[pipeline_key]}, "
        f"b64={len(req.image)/1024:.0f}KB"
    )

    try:
        img = _decode_image(req.image)
    except Exception as e:
        _error_count[pipeline_key] += 1
        raise HTTPException(status_code=400, detail=f"Image decode error: {e}")

    with _lock:
        t_lock = time.time()
        result = engine.predict_image(img, rec_key=pipeline_key)
        t_predict = time.time()

    # convert to same item format as Paddle server:
    #   {"text": str, "score": float, "box": [[x,y], [x,y], [x,y], [x,y]]}
    items = []
    for det in result["detections"]:
        items.append({
            "text": det.get("text", ""),
            "score": round(det.get("confidence", 0.0), 4),
            "box": det["box"],
        })

    elapsed = time.time() - t0
    predict_ms = (t_predict - t_lock) * 1000
    logger.info(
        f"[{pipeline_key}] #{_request_count[pipeline_key]} done: "
        f"{len(items)} items, predict={predict_ms:.0f}ms, total={elapsed:.1f}s"
    )
    return {"model": model_name, "count": len(items), "items": items}


@app.post("/ocr/server_rec")
def ocr_server_rec(req: OCRRequest):
    return _run_single("server", "PP-OCRv5_server_rec", req)


@app.post("/ocr/mobile_rec")
def ocr_mobile_rec(req: OCRRequest):
    return _run_single("mobile_cn", "PP-OCRv5_mobile_rec", req)


@app.post("/ocr/en_mobile_rec")
def ocr_en_mobile_rec(req: OCRRequest):
    return _run_single("en_mobile", "en_PP-OCRv5_mobile_rec", req)


@app.post("/ocr/cross_validate")
def ocr_cross_validate(req: OCRRequest):
    """3-model cross-validation. Same response format as Paddle server."""
    _request_count["cross_validate"] += 1
    t0 = time.time()
    logger.info(
        f"[cross_validate] request #{_request_count['cross_validate']}, "
        f"b64={len(req.image)/1024:.0f}KB"
    )

    try:
        img = _decode_image(req.image)
    except Exception as e:
        _error_count["cross_validate"] += 1
        raise HTTPException(status_code=400, detail=f"Image decode error: {e}")

    with _lock:
        t_lock = time.time()
        # run detection once
        boxes, det_scores, crops = engine._detect_and_crop(img)
        t_detect = time.time()

        if not boxes:
            elapsed = time.time() - t0
            logger.info(f"[cross_validate] no text detected ({elapsed:.1f}s)")
            return {
                "server_rec": {"model": "PP-OCRv5_server_rec", "count": 0, "items": []},
                "mobile_rec": {"model": "PP-OCRv5_mobile_rec", "count": 0, "items": []},
                "en_mobile_rec": {"model": "en_PP-OCRv5_mobile_rec", "count": 0, "items": []},
            }

        # run 3 recognition models
        rec_results = {}
        for key in ["server", "mobile_cn", "en_mobile"]:
            t = time.time()
            rec_results[key] = engine._recognize_batch(crops, key)
            logger.info(
                f"[cross_validate] {key} rec: {len(rec_results[key])} items "
                f"({(time.time()-t)*1000:.0f}ms)"
            )

    elapsed = time.time() - t0
    detect_ms = (t_detect - t_lock) * 1000
    logger.info(
        f"[cross_validate] #{_request_count['cross_validate']} done: "
        f"detect={detect_ms:.0f}ms, total={elapsed:.1f}s"
    )

    def _make_items(key, model_name):
        results = rec_results.get(key, [])
        items = []
        for i, (text, conf) in enumerate(results):
            box = boxes[i] if i < len(boxes) else [[0, 0]] * 4
            items.append({
                "text": text,
                "score": round(conf, 4),
                "box": _box_to_list(box),
            })
        return {"model": model_name, "count": len(items), "items": items}

    return {
        "server_rec": _make_items("server", "PP-OCRv5_server_rec"),
        "mobile_rec": _make_items("mobile_cn", "PP-OCRv5_mobile_rec"),
        "en_mobile_rec": _make_items("en_mobile", "en_PP-OCRv5_mobile_rec"),
    }


# ── legacy endpoint (compatibility with existing client) ────────────────────

@app.post("/ocr/recognize")
def ocr_recognize(req: OCRRequest, mode: str = "cross_validate", rec_key: str = "server"):
    """
    Combined endpoint that supports mode selection via query params.
    Used by OcrClient's recognition mode selector.
    """
    if mode == "cross_validate":
        return ocr_cross_validate(req)
    elif rec_key in ("server", "mobile_cn", "en_mobile"):
        return _run_single(rec_key, rec_key, req)
    else:
        raise HTTPException(status_code=400, detail=f"Invalid mode/rec_key: {mode}/{rec_key}")


# ── main ────────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    import uvicorn
    import signal as _signal

    port = int(os.environ.get("ONNX_PORT", "8080"))

    def _shutdown(signum, frame):
        logger.info(f"Shutdown signal received, {_server_stats()}")
        sys.exit(0)

    _signal.signal(_signal.SIGINT, _shutdown)
    _signal.signal(_signal.SIGTERM, _shutdown)
    logger.info(f"Starting uvicorn on 0.0.0.0:{port}")
    uvicorn.run(app, host="0.0.0.0", port=port)
