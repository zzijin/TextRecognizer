"""PaddleOCR FastAPI serving with cross-validation."""
import base64
import io
import os
from contextlib import asynccontextmanager

os.environ["FLAGS_use_onednn"] = "0"

from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel
import numpy as np
from PIL import Image
from paddleocr import PaddleOCR

# ---- load models once at startup ----
def load_models():
    print("Loading PP-OCRv5_server_rec ...")
    server = PaddleOCR(
        use_doc_orientation_classify=False,
        use_doc_unwarping=False,
        use_textline_orientation=False,
        enable_mkldnn=False,
    )
    print("Loading en_PP-OCRv5_mobile_rec ...")
    en = PaddleOCR(
        text_detection_model_name="PP-OCRv5_server_det",
        text_recognition_model_name="en_PP-OCRv5_mobile_rec",
        use_doc_orientation_classify=False,
        use_doc_unwarping=False,
        use_textline_orientation=False,
        enable_mkldnn=False,
    )
    print("Models loaded.")
    return server, en

pipeline_server, pipeline_en = load_models()


@asynccontextmanager
async def lifespan(app: FastAPI):
    yield  # models already loaded above

app = FastAPI(title="OCR Service", version="1.0.0", lifespan=lifespan)
app.add_middleware(CORSMiddleware, allow_origins=["*"], allow_methods=["*"], allow_headers=["*"])


def _decode_image(b64: str) -> np.ndarray:
    try:
        img_bytes = base64.b64decode(b64)
        img = Image.open(io.BytesIO(img_bytes))
        return np.array(img.convert("RGB"))
    except Exception as e:
        raise HTTPException(status_code=400, detail=f"Invalid image: {e}")


def _predict(pipeline, image: np.ndarray) -> list[dict]:
    results = pipeline.predict(image)
    r = results[0]
    items = []
    for i in range(len(r["rec_texts"])):
        items.append({
            "text": r["rec_texts"][i],
            "score": round(float(r["rec_scores"][i]), 4),
            "box": r["dt_polys"][i].tolist() if hasattr(r["dt_polys"][i], "tolist") else r["dt_polys"][i],
        })
    return items


class OCRRequest(BaseModel):
    image: str  # base64-encoded JPEG/PNG


@app.get("/health")
def health():
    return {"status": "ok"}


@app.post("/ocr/server_rec")
def ocr_server_rec(req: OCRRequest):
    img = _decode_image(req.image)
    items = _predict(pipeline_server, img)
    return {"model": "PP-OCRv5_server_rec", "count": len(items), "items": items}


@app.post("/ocr/en_mobile_rec")
def ocr_en_mobile_rec(req: OCRRequest):
    img = _decode_image(req.image)
    items = _predict(pipeline_en, img)
    return {"model": "en_PP-OCRv5_mobile_rec", "count": len(items), "items": items}


@app.post("/ocr/cross_validate")
def ocr_cross_validate(req: OCRRequest):
    img = _decode_image(req.image)
    r1 = _predict(pipeline_server, img)
    r2 = _predict(pipeline_en, img)
    return {
        "server_rec": {"model": "PP-OCRv5_server_rec", "count": len(r1), "items": r1},
        "en_mobile_rec": {"model": "en_PP-OCRv5_mobile_rec", "count": len(r2), "items": r2},
    }


if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8080)
