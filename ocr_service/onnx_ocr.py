"""
Standalone OCR engine using ONNX Runtime (DirectML GPU or CPU).

Does NOT import paddle or paddleocr — pure onnxruntime + numpy + cv2.
Pre/post processing replicated from PaddleX/PaddleOCR pipeline.

Usage:
    engine = OnnxOcrEngine(models_dir="models/onnx_models", device="dml")
    results = engine.predict("image.png")  # returns list of (text, confidence, box)
"""

import json
import os
import time
from pathlib import Path
from typing import Optional

import cv2
import numpy as np
import pyclipper
from PIL import Image


# ─── helpers ────────────────────────────────────────────────────────────────

def _letterbox_resize(img, target_long=960, stride=128):
    """Resize image so long side = target_long, pad to stride multiple."""
    h, w = img.shape[:2]
    ratio = target_long / max(h, w)
    new_h = int(round(h * ratio / stride)) * stride
    new_w = int(round(w * ratio / stride)) * stride
    resized = cv2.resize(img, (new_w, new_h))
    return resized, (h, w, new_h, new_w)


def _det_normalize(img_bgr):
    """Apply detection NormalizeImage: (x/255 - mean) / std, HWC -> CHW."""
    scale = 1.0 / 255.0
    mean = np.array([0.485, 0.456, 0.406], dtype=np.float32)
    std = np.array([0.229, 0.224, 0.225], dtype=np.float32)
    img = img_bgr.astype(np.float32) * scale
    img = (img - mean) / std
    img = img.transpose(2, 0, 1)  # HWC -> CHW
    return np.ascontiguousarray(img)


def _rec_resize_norm(img_bgr, rec_shape=(3, 48, 320), max_img_w=3200):
    """Resize/normalize a crop for recognition. Returns CHW float32 [-1, 1]."""
    h, w = img_bgr.shape[:2]
    _, img_h, img_w = rec_shape
    wh_ratio = w / h
    max_wh_ratio = max(img_w / img_h, wh_ratio)
    resized_w = min(int(np.ceil(img_h * max_wh_ratio)), max_img_w)
    resized_w = min(resized_w, int(np.ceil(img_h * wh_ratio)))

    resized = cv2.resize(img_bgr, (resized_w, img_h))
    resized = resized.astype(np.float32)
    resized = resized.transpose(2, 0, 1) / 255.0  # CHW [0, 1]
    resized = (resized - 0.5) / 0.5  # CHW [-1, 1]

    # pad width to img_w
    pad_w = img_w - resized_w
    if pad_w > 0:
        resized = np.pad(resized, ((0, 0), (0, 0), (0, pad_w)), mode="constant")
    return np.ascontiguousarray(resized[:, :, :img_w])


# ─── DB Post-Process ────────────────────────────────────────────────────────

def _boxes_from_bitmap(pred, bitmap, src_shape, resized_shape, thresh=0.3,
                       box_thresh=0.5, unclip_ratio=1.5, min_size=3):
    """Extract text boxes from DB segmentation map. Pure numpy + cv2 + pyclipper."""
    src_h, src_w, new_h, new_w = resized_shape
    ratio_h = src_h / new_h
    ratio_w = src_w / new_w

    contours, _ = cv2.findContours(
        (bitmap * 255).astype(np.uint8), cv2.RETR_LIST, cv2.CHAIN_APPROX_SIMPLE
    )
    boxes = []
    scores = []
    for contour in contours:
        if len(contour) < 4:
            continue
        # minimum area bounding box
        rect = cv2.minAreaRect(contour)
        points = cv2.boxPoints(rect).astype(np.int32)

        # filter tiny boxes
        side_short = min(rect[1])
        if side_short < min_size:
            continue

        # score = mean pred inside box
        mask = np.zeros(bitmap.shape, dtype=np.uint8)
        cv2.fillPoly(mask, [points], 1)
        score = pred[mask.astype(bool)].mean()
        if score < box_thresh:
            continue

        # unclip
        points = _unclip(points, unclip_ratio)
        if points is None or len(points) < 4:
            continue

        # sort corners: TL, TR, BR, BL
        points = _order_points(points)

        # scale to original image coordinates
        points[:, 0] = np.clip(points[:, 0] * ratio_w, 0, src_w - 1)
        points[:, 1] = np.clip(points[:, 1] * ratio_h, 0, src_h - 1)

        boxes.append(points.astype(np.int32))
        scores.append(float(score))

    return boxes, scores


def _unclip(points, unclip_ratio=1.5):
    """Expand polygon using pyclipper."""
    area = cv2.contourArea(points)
    length = cv2.arcLength(points, True)
    if length <= 0:
        return None
    distance = area * unclip_ratio / length
    pco = pyclipper.PyclipperOffset()
    pco.AddPath(points.tolist(), pyclipper.JT_ROUND, pyclipper.ET_CLOSEDPOLYGON)
    expanded = pco.Execute(distance)
    if not expanded:
        return None
    return np.array(expanded[0]).astype(np.int32)


def _order_points(pts):
    """Sort 4 points: top-left, top-right, bottom-right, bottom-left."""
    pts = pts.astype(np.float32)
    rect = np.zeros((4, 2), dtype=np.float32)
    s = pts.sum(axis=1)
    rect[0] = pts[np.argmin(s)]
    rect[2] = pts[np.argmax(s)]
    diff = np.diff(pts, axis=1)
    rect[1] = pts[np.argmin(diff)]
    rect[3] = pts[np.argmax(diff)]
    return rect


# ─── CTC Decode ─────────────────────────────────────────────────────────────

def _ctc_decode(logits, character_list):
    """CTC greedy decode: argmax -> remove duplicates -> remove blank -> map chars."""
    if logits.ndim == 3:
        logits = logits[0]  # take first batch
    indices = logits.argmax(axis=-1)  # [seq_len]
    probs = logits.max(axis=-1)

    # remove consecutive duplicates
    mask = np.ones(len(indices), dtype=bool)
    if len(indices) > 1:
        mask[1:] = indices[1:] != indices[:-1]
    # remove blank (index 0)
    mask &= indices != 0

    filtered_idx = indices[mask]
    filtered_prob = probs[mask]

    text = "".join(character_list[i] for i in filtered_idx if 0 < i < len(character_list))
    conf = float(filtered_prob.mean()) if len(filtered_prob) > 0 else 0.0

    return text, conf


# ─── Character dictionaries ─────────────────────────────────────────────────

def _load_character_dict(config_path):
    """Load character list from a recognition model's config.json."""
    with open(config_path, "r", encoding="utf-8") as f:
        config = json.load(f)
    chars = config.get("PostProcess", {}).get("character_dict", [])
    # PaddleX CTCLabelDecode prepends "blank" at index 0
    return ["blank"] + list(chars)


# ─── Main Engine ────────────────────────────────────────────────────────────

class OnnxOcrEngine:
    """ONNX-based OCR engine supporting cross-validation with 3 recognition models."""

    REC_MODELS = {
        "server": "PP-OCRv5_server_rec",
        "mobile_cn": "PP-OCRv5_mobile_rec",
        "en_mobile": "en_PP-OCRv5_mobile_rec",
    }
    DET_MODEL = "PP-OCRv5_server_det"

    def __init__(self, models_dir="models/onnx_models",
                 official_dir="models/official_models", device="dml"):
        """
        Args:
            models_dir: path to directory containing .onnx files
            official_dir: path to directory containing original model configs (for char dicts)
            device: 'dml' (DirectML GPU), 'cuda', or 'cpu'
        """
        import onnxruntime as ort

        self.models_dir = Path(models_dir)
        self.official_dir = Path(official_dir)
        self._device = device

        # select provider
        providers = ort.get_available_providers()
        if device == "dml" and "DmlExecutionProvider" in providers:
            self._providers = ["DmlExecutionProvider"]
        elif device == "cuda" and "CUDAExecutionProvider" in providers:
            self._providers = ["CUDAExecutionProvider"]
        else:
            self._providers = ["CPUExecutionProvider"]

        # load char dicts
        self._char_lists: dict[str, list[str]] = {}
        self._load_char_dicts()

        # load ONNX sessions
        self._det_sess: Optional[ort.InferenceSession] = None
        self._rec_sessions: dict[str, ort.InferenceSession] = {}
        self._load_models()

    @property
    def device_name(self) -> str:
        return self._providers[0] if self._providers else "CPUExecutionProvider"

    # ── internal: load ──────────────────────────────────────────────────

    def _load_char_dicts(self):
        for key, name in self.REC_MODELS.items():
            config_path = self.official_dir / name / "config.json"
            if config_path.exists():
                self._char_lists[key] = _load_character_dict(str(config_path))
            else:
                self._char_lists[key] = ["blank"]

    def _load_models(self):
        import onnxruntime as ort

        # detection model
        det_path = self.models_dir / f"{self.DET_MODEL}.onnx"
        if det_path.exists():
            self._det_sess = ort.InferenceSession(str(det_path), providers=self._providers)

        # recognition models
        for key, name in self.REC_MODELS.items():
            rec_path = self.models_dir / f"{name}.onnx"
            if rec_path.exists():
                self._rec_sessions[key] = ort.InferenceSession(
                    str(rec_path), providers=self._providers
                )

    def is_ready(self) -> bool:
        return self._det_sess is not None and len(self._rec_sessions) > 0

    # ── detection ───────────────────────────────────────────────────────

    def _detect(self, image_bgr: np.ndarray):
        """Run detection on BGR image. Returns (boxes, scores)."""
        import time as _time
        t0 = _time.perf_counter()
        resized, shape_info = _letterbox_resize(image_bgr)
        tensor = _det_normalize(resized)
        tensor = np.expand_dims(tensor, axis=0)  # [1, 3, H, W]
        t_prep = _time.perf_counter()

        # inference
        outputs = self._det_sess.run(None, {"x": tensor})
        pred = outputs[0][0, 0]  # [H, W] probability map
        t_infer = _time.perf_counter()

        # postprocess: threshold -> boxes
        bitmap = (pred > 0.3).astype(np.uint8)
        boxes, scores = _boxes_from_bitmap(
            pred, bitmap, image_bgr.shape, shape_info,
            thresh=0.3, box_thresh=0.5, unclip_ratio=1.5,
        )
        t_post = _time.perf_counter()

        # sort by Y coordinate (top to bottom), then X (left to right)
        if boxes:
            boxes_scores = list(zip(boxes, scores))
            boxes_scores.sort(key=lambda bs: (bs[0][:, 1].mean(), bs[0][:, 0].mean()))
            boxes, scores = zip(*boxes_scores, strict=False)
            boxes, scores = list(boxes), list(scores)

        prep_ms = (t_prep - t0) * 1000
        infer_ms = (t_infer - t_prep) * 1000
        post_ms = (t_post - t_infer) * 1000
        print(f"[ONNX detect] prep={prep_ms:.0f}ms infer={infer_ms:.0f}ms post={post_ms:.0f}ms boxes={len(boxes)} input={tensor.shape}")

        return boxes, scores

    def _detect_and_crop(self, image_bgr: np.ndarray):
        """Detect text boxes and crop regions. Returns (boxes, scores, crops)."""
        boxes, scores = self._detect(image_bgr)
        crops = []
        for box in boxes:
            x_min = max(0, int(box[:, 0].min()))
            x_max = min(image_bgr.shape[1], int(box[:, 0].max()))
            y_min = max(0, int(box[:, 1].min()))
            y_max = min(image_bgr.shape[0], int(box[:, 1].max()))
            if x_max > x_min and y_max > y_min:
                crops.append(image_bgr[y_min:y_max, x_min:x_max])
            else:
                crops.append(np.zeros((48, 48, 3), dtype=np.uint8))
        return boxes, scores, crops

    # ── recognition ─────────────────────────────────────────────────────

    def _recognize_batch(self, crops: list[np.ndarray], rec_key: str):
        """Run recognition on a batch of image crops. Returns list of (text, conf)."""
        if not crops:
            return []

        import time as _time
        t0 = _time.perf_counter()
        session = self._rec_sessions.get(rec_key)
        char_list = self._char_lists.get(rec_key, ["blank"])

        if session is None:
            return [("", 0.0)] * len(crops)

        # preprocess all crops
        batch = np.stack([_rec_resize_norm(c) for c in crops], axis=0)
        batch = batch.astype(np.float32)
        t_prep = _time.perf_counter()

        # inference
        outputs = session.run(None, {"x": batch})
        logits = outputs[0]  # [batch, seq_len, num_classes]
        t_infer = _time.perf_counter()

        # decode each
        results = []
        for i in range(logits.shape[0]):
            text, conf = _ctc_decode(logits[i], char_list)
            results.append((text, conf))
        t_decode = _time.perf_counter()

        prep_ms = (t_prep - t0) * 1000
        infer_ms = (t_infer - t_prep) * 1000
        decode_ms = (t_decode - t_infer) * 1000
        print(f"[ONNX rec:{rec_key}] prep={prep_ms:.0f}ms infer={infer_ms:.0f}ms decode={decode_ms:.0f}ms batch={len(crops)} crops={batch.shape} output={logits.shape}")

        return results

    def _recognize_single(self, crop: np.ndarray, rec_key: str):
        """Run recognition on a single image crop."""
        results = self._recognize_batch([crop], rec_key)
        return results[0] if results else ("", 0.0)

    # ── public API ──────────────────────────────────────────────────────

    def predict_image(self, image_bgr: np.ndarray, rec_key: str = "server"):
        """
        Single-model OCR on an already-loaded BGR image.

        Returns:
            dict with: detections, num_detections, time_ms
        """
        t0 = time.perf_counter()

        boxes, det_scores, crops = self._detect_and_crop(image_bgr)
        if not boxes:
            return {"detections": [], "num_detections": 0, "time_ms": 0}

        t_rec_start = time.perf_counter()
        results = self._recognize_batch(crops, rec_key)
        rec_ms = (time.perf_counter() - t_rec_start) * 1000

        detections = []
        for i, box in enumerate(boxes):
            text, conf = results[i] if i < len(results) else ("", 0.0)
            detections.append({
                "box": box.tolist(),
                "det_score": round(det_scores[i], 4) if i < len(det_scores) else 0.0,
                "text": text,
                "confidence": round(conf, 4),
            })

        return {
            "detections": detections,
            "num_detections": len(boxes),
            "time_ms": round((time.perf_counter() - t0) * 1000, 1),
            "rec_time_ms": round(rec_ms, 1),
        }

    def predict(self, image_path: str, mode: str = "cross_validate",
                rec_key: str = "server"):
        """
        Run OCR on an image.

        Args:
            image_path: path to input image
            mode: 'cross_validate' (3 models), or 'single' (one model)
            rec_key: for single mode, which rec model: 'server', 'mobile_cn', 'en_mobile'

        Returns:
            dict with keys: image_path, mode, detections (list of box dicts), time_ms
        """
        t0 = time.perf_counter()

        # load image
        img = cv2.imread(image_path)
        if img is None:
            raise FileNotFoundError(f"Cannot read image: {image_path}")

        # detect
        boxes, det_scores, crops = self._detect_and_crop(img)
        if not boxes:
            return {
                "image_path": image_path,
                "mode": mode,
                "detections": [],
                "time_ms": (time.perf_counter() - t0) * 1000,
            }

        t_rec_start = time.perf_counter()

        if mode == "cross_validate":
            # run all 3 models
            all_results = {}
            for key in self.REC_MODELS:
                if key in self._rec_sessions:
                    all_results[key] = self._recognize_batch(crops, key)

            # build result
            detections = []
            for i, box in enumerate(boxes):
                texts = {}
                for key in self.REC_MODELS:
                    if key in all_results and i < len(all_results[key]):
                        texts[key] = all_results[key][i]
                # agreement: count how many models give the same text
                text_values = [t[0] for t in texts.values() if t[0]]
                if text_values:
                    from collections import Counter
                    most_common = Counter(text_values).most_common(1)[0]
                    agree_count = most_common[1]
                else:
                    agree_count = 0
                detections.append({
                    "box": box.tolist(),
                    "det_score": round(det_scores[i], 4) if i < len(det_scores) else 0.0,
                    "texts": {k: {"text": t[0], "conf": round(t[1], 4)}
                              for k, t in texts.items()},
                    "agree_count": agree_count,
                })
        else:
            # single model
            results = self._recognize_batch(crops, rec_key)
            detections = []
            for i, box in enumerate(boxes):
                text, conf = results[i] if i < len(results) else ("", 0.0)
                detections.append({
                    "box": box.tolist(),
                    "det_score": round(det_scores[i], 4) if i < len(det_scores) else 0.0,
                    "text": text,
                    "confidence": round(conf, 4),
                })

        total_ms = (time.perf_counter() - t0) * 1000
        rec_ms = (time.perf_counter() - t_rec_start) * 1000

        return {
            "image_path": image_path,
            "mode": mode,
            "detections": detections,
            "num_detections": len(boxes),
            "time_ms": round(total_ms, 1),
            "rec_time_ms": round(rec_ms, 1),
        }


# ─── Quick test ─────────────────────────────────────────────────────────────

if __name__ == "__main__":
    import sys

    os.environ.setdefault("PADDLE_PDX_CACHE_HOME",
                          os.path.join(os.path.dirname(__file__), "models"))

    print("Loading ONNX OCR engine...")
    engine = OnnxOcrEngine(
        models_dir=os.path.join(os.path.dirname(__file__), "models", "onnx_models"),
        official_dir=os.path.join(os.path.dirname(__file__), "models", "official_models"),
        device="dml",
    )
    print(f"Device: {engine.device_name}")
    print(f"Ready: {engine.is_ready()}")

    if len(sys.argv) > 1:
        image_path = sys.argv[1]
        print(f"\nProcessing: {image_path}")
        result = engine.predict(image_path, mode="cross_validate")
        print(f"Time: {result['time_ms']:.0f} ms")
        print(f"Detections: {result['num_detections']}")
        for det in result["detections"][:5]:
            if "texts" in det:
                for k, v in det["texts"].items():
                    print(f"  [{k}] {v['text']} ({v['conf']:.3f})")
            else:
                print(f"  {det['text']} ({det['confidence']:.3f})")
            print(f"    agree={det.get('agree_count', 'N/A')}")
