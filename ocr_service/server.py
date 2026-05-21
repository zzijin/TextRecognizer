"""PaddleOCR FastAPI serving with cross-validation."""
import base64
import io
import os
import time
import logging
from contextlib import asynccontextmanager

os.environ["FLAGS_use_onednn"] = "0"

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
logger = logging.getLogger("ocr_service")

from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel
import numpy as np
from PIL import Image
from paddleocr import PaddleOCR

# ---- paths ----
BASE_DIR = os.path.dirname(os.path.abspath(__file__))
MODELS_DIR = os.path.join(BASE_DIR, "models", "official_models")
DET_MODEL_DIR = os.path.join(MODELS_DIR, "PP-OCRv5_server_det")
REC_SERVER_DIR = os.path.join(MODELS_DIR, "PP-OCRv5_server_rec")
REC_MOBILE_DIR = os.path.join(MODELS_DIR, "en_PP-OCRv5_mobile_rec")

# ---- load models at startup using local model dirs ----
def load_models():
    print("Loading PP-OCRv5_server_rec ...")
    server = PaddleOCR(
        text_detection_model_dir=DET_MODEL_DIR,
        text_recognition_model_dir=REC_SERVER_DIR,
        use_doc_orientation_classify=False,
        use_doc_unwarping=False,
        use_textline_orientation=False,
        enable_mkldnn=True,
        text_det_thresh=0.3,
        text_det_box_thresh=0.5,
        text_rec_score_thresh=0.5,
    )
    print("Loading en_PP-OCRv5_mobile_rec ...")
    en = PaddleOCR(
        text_detection_model_dir=DET_MODEL_DIR,
        text_recognition_model_name="en_PP-OCRv5_mobile_rec",
        text_recognition_model_dir=REC_MOBILE_DIR,
        use_doc_orientation_classify=False,
        use_doc_unwarping=False,
        use_textline_orientation=False,
        enable_mkldnn=True,
        text_det_thresh=0.3,
        text_det_box_thresh=0.5,
        text_rec_score_thresh=0.5,
    )
    print("Models loaded.")
    return server, en

pipeline_server, pipeline_en = load_models()


@asynccontextmanager
async def lifespan(app: FastAPI):
    yield  # models already loaded above

app = FastAPI(title="OCR Service", version="1.0.0", lifespan=lifespan)
app.add_middleware(CORSMiddleware, allow_origins=["*"], allow_methods=["*"], allow_headers=["*"])


def _preprocess_image(img: np.ndarray, max_side: int = 1024) -> np.ndarray:
    """Resize image so the longer side does not exceed max_side pixels."""
    h, w = img.shape[:2]
    scale = max_side / max(h, w)
    if scale < 1.0:
        new_w, new_h = int(w * scale), int(h * scale)
        img = np.array(Image.fromarray(img).resize((new_w, new_h), Image.LANCZOS))
        logger.info(f"Image resized: {w}x{h} -> {new_w}x{new_h}")
    return img


def _decode_image(b64: str) -> np.ndarray:
    try:
        t0 = time.time()
        img_bytes = base64.b64decode(b64)
        img = Image.open(io.BytesIO(img_bytes))
        arr = np.array(img.convert("RGB"))
        arr = _preprocess_image(arr)
        logger.info(f"Image decoded: {arr.shape[1]}x{arr.shape[0]}, {len(b64)/1024:.0f}KB b64 ({time.time()-t0:.2f}s)")
        return arr
    except Exception as e:
        raise HTTPException(status_code=400, detail=f"Invalid image: {e}")


def _predict(pipeline, image: np.ndarray) -> list[dict]:
    t0 = time.time()
    results = pipeline.predict(image)
    r = results[0]
    items = []
    for i in range(len(r["rec_texts"])):
        items.append({
            "text": r["rec_texts"][i],
            "score": round(float(r["rec_scores"][i]), 4),
            "box": r["dt_polys"][i].tolist() if hasattr(r["dt_polys"][i], "tolist") else r["dt_polys"][i],
        })
    logger.info(f"Predict done: {len(items)} regions ({time.time()-t0:.2f}s)")
    return items


class OCRRequest(BaseModel):
    image: str  # base64-encoded JPEG/PNG


@app.get("/health")
def health():
    return {"status": "ok"}


@app.post("/ocr/server_rec")
def ocr_server_rec(req: OCRRequest):
    t0 = time.time()
    logger.info("[server_rec] Request received")
    img = _decode_image(req.image)
    items = _predict(pipeline_server, img)
    logger.info(f"[server_rec] Done: {len(items)} items ({time.time()-t0:.2f}s)")
    return {"model": "PP-OCRv5_server_rec", "count": len(items), "items": items}


@app.post("/ocr/en_mobile_rec")
def ocr_en_mobile_rec(req: OCRRequest):
    t0 = time.time()
    logger.info("[en_mobile_rec] Request received")
    img = _decode_image(req.image)
    items = _predict(pipeline_en, img)
    logger.info(f"[en_mobile_rec] Done: {len(items)} items ({time.time()-t0:.2f}s)")
    return {"model": "en_PP-OCRv5_mobile_rec", "count": len(items), "items": items}


@app.post("/ocr/cross_validate")
def ocr_cross_validate(req: OCRRequest):
    t0 = time.time()
    logger.info("[cross_validate] Request received")
    img = _decode_image(req.image)
    r1 = _predict(pipeline_server, img)
    r2 = _predict(pipeline_en, img)
    logger.info(f"[cross_validate] Done: server={len(r1)}, mobile={len(r2)} ({time.time()-t0:.2f}s)")
    return {
        "server_rec": {"model": "PP-OCRv5_server_rec", "count": len(r1), "items": r1},
        "en_mobile_rec": {"model": "en_PP-OCRv5_mobile_rec", "count": len(r2), "items": r2},
    }


if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8080)
