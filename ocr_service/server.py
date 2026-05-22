"""PaddleOCR FastAPI serving with 3-model cross-validation."""
import base64
import gc
import io
import os
import signal
import sys
import threading
import time
import traceback
import logging
from collections import defaultdict
from contextlib import asynccontextmanager

os.environ["PADDLE_PDX_CACHE_HOME"] = os.path.join(os.path.dirname(os.path.abspath(__file__)), "models")

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    handlers=[
        logging.StreamHandler(sys.stdout),
    ],
)
logger = logging.getLogger("ocr_service")
logger.info(f"======== OCR Service starting ========")
logger.info(f"PID={os.getpid()}, Python={sys.version.split()[0]}")

from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel
import numpy as np
from PIL import Image
import paddle
from paddleocr import PaddleOCR

# ---- paths ----
BASE_DIR = os.path.dirname(os.path.abspath(__file__))
LOG_DIR = os.path.join(BASE_DIR, "logs")
os.makedirs(LOG_DIR, exist_ok=True)
LOG_FILE = os.path.join(LOG_DIR, "server.log")
logger.addHandler(logging.FileHandler(LOG_FILE, encoding="utf-8"))
logger.info(f"Log file: {LOG_FILE}")

MODELS_DIR = os.path.join(BASE_DIR, "models", "official_models")
DET_MODEL_DIR = os.path.join(MODELS_DIR, "PP-OCRv5_server_det")
REC_SERVER_DIR = os.path.join(MODELS_DIR, "PP-OCRv5_server_rec")
REC_MOBILE_CN_DIR = os.path.join(MODELS_DIR, "PP-OCRv5_mobile_rec")
REC_MOBILE_EN_DIR = os.path.join(MODELS_DIR, "en_PP-OCRv5_mobile_rec")

# ---- helpers ----
def _log_gpu_memory(label: str = ""):
    alloc = paddle.device.cuda.memory_allocated() / 1024**3
    reserved = paddle.device.cuda.memory_reserved() / 1024**3
    logger.info(f"GPU [{label}] alloc={alloc:.2f}GB reserved={reserved:.2f}GB")


def _build_pipeline(rec_model_dir: str, rec_model_name: str | None = None) -> PaddleOCR:
    kwargs = dict(
        text_detection_model_dir=DET_MODEL_DIR,
        text_recognition_model_dir=rec_model_dir,
        use_doc_orientation_classify=False,
        use_doc_unwarping=False,
        use_textline_orientation=False,
        device="gpu",
        text_det_limit_side_len=960,
        text_det_thresh=0.3,
        text_det_box_thresh=0.5,
        text_rec_score_thresh=0.5,
    )
    if rec_model_name:
        kwargs["text_recognition_model_name"] = rec_model_name
    return PaddleOCR(**kwargs)


# ---- load models ----
_pipelines: dict[str, PaddleOCR] = {}
_failed_loads: list[str] = []
_request_count = defaultdict(int)
_error_count = defaultdict(int)
_start_time = time.time()


def _server_stats():
    uptime = time.time() - _start_time
    total = sum(_request_count.values())
    errors = sum(_error_count.values())
    return f"uptime={uptime:.0f}s, requests={total}, errors={errors}, by_endpoint={dict(_request_count)}"

def load_models():
    t_start = time.time()
    configs = [
        ("server", REC_SERVER_DIR, None),
        ("mobile_cn", REC_MOBILE_CN_DIR, "PP-OCRv5_mobile_rec"),
        ("en_mobile", REC_MOBILE_EN_DIR, "en_PP-OCRv5_mobile_rec"),
    ]
    for key, rec_dir, rec_name in configs:
        try:
            logger.info(f"Loading {key} ...")
            _pipelines[key] = _build_pipeline(rec_dir, rec_name)
            logger.info(f"  {key}: OK")
        except Exception as e:
            logger.error(f"  {key}: FAILED — {e}")
            _failed_loads.append(key)

    logger.info(f"======== All models loaded ========")
    logger.info(f"  Loaded: {len(_pipelines)}/{len(configs)} pipelines in {time.time()-t_start:.1f}s")
    if _failed_loads:
        logger.warning(f"  Failed: {_failed_loads}")
    _log_gpu_memory("after load")


def reload_pipeline(key: str):
    configs = {
        "server": (REC_SERVER_DIR, None),
        "mobile_cn": (REC_MOBILE_CN_DIR, "PP-OCRv5_mobile_rec"),
        "en_mobile": (REC_MOBILE_EN_DIR, "en_PP-OCRv5_mobile_rec"),
    }
    if key not in configs:
        return
    rec_dir, rec_name = configs[key]
    logger.warning(f"Reloading pipeline: {key}")
    old = _pipelines.get(key)
    del old
    gc.collect()
    paddle.device.cuda.empty_cache()
    _pipelines[key] = _build_pipeline(rec_dir, rec_name)
    logger.info(f"  {key}: reloaded")


def _warmup():
    """Run a tiny dummy predict to verify all loaded pipelines work."""
    logger.info("======== Warmup start ========")
    dummy = np.zeros((32, 32, 3), dtype=np.uint8)
    t0 = time.time()
    for key in list(_pipelines.keys()):
        try:
            _pipelines[key].predict(dummy)
            logger.info(f"  [{key}]: OK ({time.time()-t0:.1f}s)")
        except Exception as e:
            logger.warning(f"  [{key}]: FAILED — {e}, will reload")
            reload_pipeline(key)
            try:
                _pipelines[key].predict(dummy)
                logger.info(f"  [{key}]: reloaded & OK ({time.time()-t0:.1f}s)")
            except Exception as e2:
                logger.error(f"  [{key}]: reload also FAILED — {e2}")
    _log_gpu_memory("after warmup")
    logger.info(f"======== Warmup done: {time.time()-t0:.1f}s ========")


load_models()
_warmup()
gpu_lock = threading.Lock()

# ---- FastAPI app ----
@asynccontextmanager
async def lifespan(app: FastAPI):
    yield
    logger.info("Server shutting down")

app = FastAPI(title="OCR Service", version="1.0.0", lifespan=lifespan)
app.add_middleware(CORSMiddleware, allow_origins=["*"], allow_methods=["*"], allow_headers=["*"])


# ---- image processing ----
def _decode_image(b64: str) -> np.ndarray:
    if not b64 or len(b64) < 32:
        raise HTTPException(status_code=400, detail="Image data too short or empty")
    try:
        t0 = time.time()
        img_bytes = base64.b64decode(b64)
        img = Image.open(io.BytesIO(img_bytes))
        arr = np.array(img.convert("RGB"))
        logger.info(f"Image decoded: {arr.shape[1]}x{arr.shape[0]}, {len(b64)/1024:.0f}KB b64 ({time.time()-t0:.2f}s)")
        return arr
    except HTTPException:
        raise
    except Exception as e:
        raise HTTPException(status_code=400, detail=f"Invalid image: {e}")


# ---- predict ----
def _predict(pipeline: PaddleOCR, image: np.ndarray, label: str = "") -> list[dict]:
    t0 = time.time()
    try:
        results = pipeline.predict(image)
        r = results[0]
        items = []
        for i in range(len(r["rec_texts"])):
            items.append({
                "text": r["rec_texts"][i],
                "score": round(float(r["rec_scores"][i]), 4),
                "box": r["rec_polys"][i].tolist() if hasattr(r["rec_polys"][i], "tolist") else r["rec_polys"][i],
            })
        logger.info(f"Predict [{label}] done: {len(items)} regions ({time.time()-t0:.2f}s)")
        return items
    except Exception as e:
        logger.error(f"Predict [{label}] FAILED: {type(e).__name__}: {e}")
        raise
    finally:
        paddle.device.cuda.empty_cache()
        gc.collect()


def _predict_with_recovery(pipeline_key: str, image: np.ndarray, label: str = "") -> list[dict]:
    """Predict with automatic pipeline reload on CUDA errors."""
    try:
        return _predict(_pipelines[pipeline_key], image, label)
    except Exception as e:
        logger.warning(f"[{label}] Error: {type(e).__name__}: {e}")
        logger.warning(f"[{label}] Attempting pipeline recovery ...")
        _log_gpu_memory(f"{label}_before_recovery")
        try:
            reload_pipeline(pipeline_key)
            _log_gpu_memory(f"{label}_after_reload")
            result = _predict(_pipelines[pipeline_key], image, f"{label}_retry")
            logger.info(f"[{label}] Recovery successful")
            return result
        except Exception as e2:
            logger.error(f"[{label}] Recovery failed: {type(e2).__name__}: {e2}")
            logger.error(traceback.format_exc())
            _log_gpu_memory(f"{label}_after_failure")
            raise HTTPException(status_code=500, detail=f"OCR predict error: {type(e2).__name__}")


class OCRRequest(BaseModel):
    image: str  # base64-encoded JPEG/PNG


# ---- endpoints ----
@app.get("/health")
def health():
    total = 3
    ok = len(_pipelines) - len(_failed_loads)
    return {
        "status": "ok",
        "gpus_available": paddle.device.cuda.device_count(),
        "models_loaded": f"{ok}/{total}",
        "stats": _server_stats(),
    }


def _run_single_endpoint(pipeline_key: str, model_name: str, req: OCRRequest):
    _request_count[pipeline_key] += 1
    t0 = time.time()
    logger.info(f"[{pipeline_key}] request #{_request_count[pipeline_key]}, b64={len(req.image)/1024:.0f}KB")
    t_decode = time.time()
    img = _decode_image(req.image)
    t_decoded = time.time()
    with gpu_lock:
        t_lock = time.time()
        items = _predict_with_recovery(pipeline_key, img, pipeline_key)
        t_predict = time.time()
    elapsed = time.time() - t0
    decode_ms = (t_decoded - t_decode) * 1000
    predict_ms = (t_predict - t_lock) * 1000
    logger.info(f"[{pipeline_key}] #{_request_count[pipeline_key]} done: {len(items)} items, "
                f"decode={decode_ms:.0f}ms, predict={predict_ms:.0f}ms, total={elapsed:.1f}s")
    _log_gpu_memory(f"after {pipeline_key}")
    return {"model": model_name, "count": len(items), "items": items}


@app.post("/ocr/server_rec")
def ocr_server_rec(req: OCRRequest):
    return _run_single_endpoint("server", "PP-OCRv5_server_rec", req)


@app.post("/ocr/mobile_rec")
def ocr_mobile_rec(req: OCRRequest):
    return _run_single_endpoint("mobile_cn", "PP-OCRv5_mobile_rec", req)


@app.post("/ocr/en_mobile_rec")
def ocr_en_mobile_rec(req: OCRRequest):
    return _run_single_endpoint("en_mobile", "en_PP-OCRv5_mobile_rec", req)


@app.post("/ocr/cross_validate")
def ocr_cross_validate(req: OCRRequest):
    _request_count["cross_validate"] += 1
    t0 = time.time()
    logger.info(f"[cross_validate] request #{_request_count['cross_validate']}, b64={len(req.image)/1024:.0f}KB")
    img = _decode_image(req.image)
    with gpu_lock:
        r1 = _predict_with_recovery("server", img, "cv_server")
        r2 = _predict_with_recovery("mobile_cn", img, "cv_mobile")
        r3 = _predict_with_recovery("en_mobile", img, "cv_en")
    elapsed = time.time() - t0
    logger.info(f"[cross_validate] #{_request_count['cross_validate']} done: server={len(r1)}, mobile_cn={len(r2)}, en_mobile={len(r3)} ({elapsed:.1f}s)")
    _log_gpu_memory("after cross_validate")
    return {
        "server_rec": {"model": "PP-OCRv5_server_rec", "count": len(r1), "items": r1},
        "mobile_rec": {"model": "PP-OCRv5_mobile_rec", "count": len(r2), "items": r2},
        "en_mobile_rec": {"model": "en_PP-OCRv5_mobile_rec", "count": len(r3), "items": r3},
    }


if __name__ == "__main__":
    import uvicorn
    def _shutdown(signum, frame):
        logger.info(f"Shutdown signal received, {_server_stats()}")
        _log_gpu_memory("final")
        paddle.device.cuda.empty_cache()
        sys.exit(0)
    signal.signal(signal.SIGINT, _shutdown)
    signal.signal(signal.SIGTERM, _shutdown)
    logger.info(f"Starting uvicorn on 0.0.0.0:8080")
    uvicorn.run(app, host="0.0.0.0", port=8080)
