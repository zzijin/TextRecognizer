# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Direction

**Current phase:** High-accuracy handwritten digit table recognition via multi-model PaddleOCR cross-validation.

**Future scope:** May extend to other text recognition with specific formats — e.g., regex-based result filtering. The core architecture will remain multi-model cross-validation for handwritten text.

**Delivery target:** .NET desktop application (WinForms/WPF) wrapping the Python OCR pipeline.

The three working models (`PP-OCRv5_server_rec`, `en_PP-OCRv5_mobile_rec`, `PPStructureV3`) run independently on each image, then results are cross-validated — agreement across models boosts confidence, disagreements flag uncertain regions.

## Environment

- Python 3.12+ on Windows
- `paddlepaddle==3.3.1`, `paddleocr==3.5.0` (PaddleOCR 3.x API, `predict()` method, `OCRResult` dict-like results)
- `enable_mkldnn=False` is **required** on Windows to avoid ONEDNN PIR conversion bug

## OCR Models

| Script | Model | Class | Notes |
|---|---|---|---|
| `ocr_server_rec.py` | PP-OCRv5_server_det + PP-OCRv5_server_rec | `PaddleOCR()` | Best overall digit accuracy, no table structure |
| `ocr_en_mobile_rec.py` | PP-OCRv5_server_det + en_PP-OCRv5_mobile_rec | `PaddleOCR()` | Weaker but catches some items server misses |
| `ocr_ppstructure_v3.py` | PPStructureV3 | `PPStructureV3()` | Table structure + OCR, outputs HTML/MD/JSON |
| `batch_ocr.py` | All 3 working models | — | Batch processing multiple images |
| `ocr_predict.py` | (legacy) | — | Earlier experiment, superseded |

## Running Recognition

```bash
# Single model
python ocr_server_rec.py        # → TestDatas/server_rec/
python ocr_en_mobile_rec.py     # → TestDatas/en_mobile_rec/
python ocr_ppstructure_v3.py    # → TestDatas/ppstructure_v3/

# Batch
python batch_ocr.py
```

## Key PaddleOCR 3.x API Facts

- **Default model (no `lang`):** PP-OCRv5_server_det + PP-OCRv5_server_rec (Chinese-capable, best for digits)
- **`lang="en"` without model names:** auto-selects mobile English rec model (less accurate)
- **Explicit model names override `lang`** — the reliable way to pin a specific model
- **Result format:** `result[0]` is `OCRResult` dict with keys: `rec_texts`, `rec_scores`, `dt_polys`, `rec_polys`, `rec_boxes`
- **Built-in output:** `res.save_to_img(dir)`, `res.save_to_json(dir)`, `res.save_to_markdown(dir)` (PPStructureV3), `res.print()`
- **Always** pass `enable_mkldnn=False` to all pipeline constructors on this machine

## Lessons from This Conversation

### Why PaddleOCR, not CNN

Started with a MNIST CNN classifier. It only handles isolated 28×28 digit images — cannot process a full table photo with 175 multi-digit cells. PaddleOCR handles detection + recognition in one pipeline.

### ONEDNN Bug on Windows

PaddlePaddle 3.3.1 crashes with PIR/ONEDNN conversion error on native Windows CPU. Known issue ([GitHub #17539](https://github.com/PaddlePaddle/PaddleOCR/issues/17539)). Fix: `enable_mkldnn=False`. Setting env vars (`FLAGS_use_onednn=0`) alone is NOT sufficient — PaddleX ignores Paddle-level flags.

### Counterintuitive Model Selection

- **`PaddleOCR()` with no `lang`** → `PP-OCRv5_server_rec` — best for digits despite being a "Chinese" model
- **`lang="en"`** → `en_PP-OCRv5_mobile_rec` — lightweight, less accurate for digits
- **Explicit model names** override `lang` and are the reliable way to pin a specific model
- No single model is perfect: server is best overall, mobile catches some server misses

### PPStructureV3 Table Detection

Detects table structure but has quirks on handwritten grids:
- Column count may differ from actual (detection model trained on printed documents)
- Reading order in HTML table may not exactly match ground truth
- Useful diagnostic images include layout detection, table cell, and overall OCR overlays
- Outputs HTML table in `.md` file; JSON in `_res.json` under `table_res_list[0].pred_html`

### No Digit-Only Restriction

PaddleOCR standard pipeline has NO character-set filter (no `rec_char_dict`, `vocab`, `charset`). Models trained on alphanumeric data confuse similar shapes (0↔D/O, 1↔I/l, 5↔S). Mitigations without retraining: post-processing regex/heuristics, or fine-tuning with a digit-only character dictionary.

### save_to_img Behavior

`res.save_to_img("dir/path")` treats the argument as a directory prefix: output is `dir/{imagename}_ocr_res_img.jpg`. PPStructureV3 generates multiple diagnostic images. `save_to_markdown` and `save_to_json` follow the same directory convention.

### Official Documentation

Key docs in `doc/`:
- `通用OCR产线使用教程.html` — `PaddleOCR` pipeline parameters and defaults
- `PP-StructureV3 产线使用教程.html` — `PPStructureV3` table/document structure pipeline
- `通过OCR实现验证码识别.html` — May be relevant for digit-focused recognition

## Known Issues

- No built-in character-set restriction (e.g., "11D" from "110")
- PPStructureV3 column detection may not exactly match the actual grid on handwritten tables
- Pure CPU inference — no GPU acceleration
- Model files auto-cache; the `models/` directory has unused copies
