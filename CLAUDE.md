# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Current Progress (2026-07-08)

**Done:**
- Python OCR service with FastAPI — 4 endpoints (`/ocr/server_rec`, `/ocr/mobile_rec`, `/ocr/en_mobile_rec`, `/ocr/cross_validate`)
- 3-model cross-validation: PP-OCRv5_server_rec + PP-OCRv5_mobile_rec + en_PP-OCRv5_mobile_rec
- GPU acceleration — RTX 4080 Laptop GPU (12GB), paddlepaddle-gpu 3.3.0, CUDA 12.6
- Paddle2ONNX conversion DONE — all 4 models converted to ONNX
- ONNX OCR engine (`onnx_ocr.py`) — standalone inference, pure NumPy+cv2+pyclipper+onnxruntime
- ONNX server (`server_onnx.py`) — same API as Paddle server, shared port 8080
- **C# ONNX OCR engine** (`OcrClient.Core/Onnx/`) — in-process inference, no Python dependency
  - `OnnxOcrEngine` — auto-discovers models from `det/` `rec/` directory structure
  - `OnnxPreprocess` — letterbox resize, vectorized normalize (Split + Subtract/Divide)
  - `OnnxPostprocess` — DB box extraction, unclip, CTC decode, `Cv2.Mean` scoring
  - `OnnxCharDict` — loads `char_dict.json` (JSON string array, index 0 = blank)
  - GPU via `Microsoft.ML.OnnxRuntime.Gpu` 1.27.0 (CUDA + cuDNN DLLs from TileMind)
  - `Parallel.For` for 3-model cross-validate
  - Dynamic tensor width `[B,3,48,W]` — no fixed 320-pixel limit
  - Only 4 methods marked `unsafe` (one-line Span creation from `Mat.DataPointer`)
- **C# ONNX performance** (RTX 4080): cross-validate ~280ms, single model ~53ms
- **Model directory structure**: `models/det/{name}/model.onnx`, `models/rec/{name}/model.onnx` + `char_dict.json`
- **Engine auto-discovery**: scans `det/` `rec/` subdirectories, loads all models found
- WPF .NET client with image import (dedup), batch recognition (mode-aware skip), real-time progress
- Client auto-starts Python server from venv based on selected engine
- **3 engine sources**: local service / Baidu Cloud API / ONNX For CSharp
- **ONNX For CSharp**: no Python process needed, C# engine runs in-process with async `Task.Run`
- Baidu Cloud OCR with token auto-refresh, dual-model cross-validate
- Weighted cross-validation algorithm with decay coefficient
- ColorLevel system, weighted score display, confirmation workflow
- Copy results, Enter key confirmation, image crop preview, annotated image export
- Environment check — detects GPU, network, models (det/rec/ structure), scripts
- Client: `ILogger` integration throughout, ZLogger rolling file logging

**Not yet working / needs investigation:**
- Popup not opening on TextBox focus — `GotKeyboardFocus` fires but popup doesn't appear
- cuDNN version mismatch warning (Paddle: 9.9, installed: 9.5.1.17) — no functional impact
- HPI (TensorRT) not available on Windows
- C# unclip uses geometric center expansion, differs slightly from Python pyclipper

## Project Structure

```
TextRecognizer/
├── ocr_service/                     # Python OCR server
│   ├── server.py                    # PaddlePaddle FastAPI (3 models + cross_validate)
│   ├── server_onnx.py               # ONNX Runtime FastAPI (DML/CPU)
│   ├── onnx_ocr.py                  # Pure NumPy ONNX engine
│   ├── convert_to_onnx.py           # PIR→ONNX conversion
│   ├── migrate_models.py            # Model migration → det/ rec/ structure
│   ├── batch_ocr.py                 # Batch recognition (GPU + annotated + txt)
│   ├── venv/                        # Python 3.12 virtualenv
│   ├── models/
│   │   ├── det/                     # Detection models: {name}/model.onnx
│   │   ├── rec/                     # Recognition models: {name}/model.onnx + char_dict.json
│   │   ├── official_models/         # PIR models + char dicts (~260MB)
│   │   └── onnx_models/             # Legacy flat ONNX files (~188MB)
│   └── logs/
├── TestDatas/                       # Test images + output
├── OcrClient/                       # .NET desktop client
│   ├── OcrClient.slnx
│   ├── OcrClient.Core/              # Shared library (net10.0)
│   │   ├── Models/                  # AppConfig, OcrResult, CrossValidateGroup, OcrTiming
│   │   ├── Services/                # OcrApiClient, BaiduOcrClient, CrossValidateAligner
│   │   └── Onnx/                    # C# ONNX OCR engine
│   │       ├── OnnxOcrEngine.cs     # Auto-discovery, detect/recognize/cross-validate
│   │       ├── OnnxPreprocess.cs    # Letterbox, normalize, Split + vectorized math
│   │       ├── OnnxPostprocess.cs   # DB boxes, unclip, CTC decode, Cv2.Mean scoring
│   │       └── OnnxCharDict.cs      # char_dict.json loader
│   ├── OcrClient/                   # WPF UI (net10.0-windows)
│   │   ├── Converters/              # OpenCvRectConverter
│   │   ├── ViewModels/              # HomeVM, SettingsVM, MainWindowVM, ImageFileItem
│   │   ├── Views/                   # HomePage, SettingsPage, MainWindow
│   │   └── Services/                # ApplicationHostService, ServerProcessState, AppConfigService
│   └── onnx_test/                   # ONNX engine benchmark
├── CLAUDE.md
└── README.md
```

## Model Directory Structure

```
.\models\                        ← ModelsDir config (default: ./models)
├── det\                         ← Detection models
│   └── PP-OCRv5_server_det\
│       └── model.onnx
├── rec\                         ← Recognition models
│   ├── PP-OCRv5_server_rec\
│   │   ├── model.onnx
│   │   └── char_dict.json       ← ["blank", "0", "1", ..., "北", "東", ...]
│   ├── PP-OCRv5_mobile_rec\
│   │   ├── model.onnx
│   │   └── char_dict.json
│   └── en_PP-OCRv5_mobile_rec\
│       ├── model.onnx
│       └── char_dict.json
```

OnnxOcrEngine scans `det/` and `rec/` at startup, loads all models found. Add a new model by creating a subdirectory with `model.onnx` (+ `char_dict.json` for rec), no code changes needed.

## Performance (RTX 4080 Laptop, 12GB)

| Mode | C# ONNX GPU (CUDA) | Python ONNX DML | PaddlePaddle GPU |
|------|-------------------|-----------------|------------------|
| Single model | **53ms** | ~600ms | 0.8–1.4s |
| Cross-validate (3 models) | **280ms** | ~1.9s | 2.4s |

C# ONNX GPU is ~7× faster than Python ONNX DML and ~9× faster than PaddlePaddle.

## Key Implementation Details

### C# ONNX Engine (`OcrClient.Core/Onnx/`)

- **OnnxOcrEngine** constructor: `(modelsDir, gpuId, logger)` — auto-scans directories
- Provider: CUDA via `AppendExecutionProvider_CUDA(gpuId)` with CPU fallback
- `DetModels` / `RecModels` properties expose discovered models
- `FindRecIdx(name)` to look up model by directory name
- 4 `InferenceSession` objects (1 det + 3 rec), loaded at startup
- `Predict(mat, recIdx)` — single model: detect → recognize
- `CrossValidate(mat)` — detect once, `Parallel.For` all rec models

### Preprocessing Optimizations

- **Detection**: `Cv2.Split` + `Cv2.Subtract`/`Divide` (vectorized) instead of per-pixel loops
- **Recognition**: Split + vectorized normalize, then Span `CopyTo` for row-wise copy
- **Box scoring**: `Cv2.Mean(predMat, mask)` instead of O(H×W) nested per-pixel loops
- `double` precision for letterbox ratio (avoids float32 rounding at boundary values like 7.5)
- Dynamic tensor width: batch uses max `resizedW` across crops, model supports `[B,3,48,W]`

### Unicode Path Handling

- `Cv2.ImRead(path)` fails on Windows for Chinese filenames
- Fix: `File.ReadAllBytes(path)` + `Cv2.ImDecode(bytes, ImreadModes.Color)`
- Applied in HomeViewModel ONNX path and ExportAnnotatedImage

### Client Engine Selection

Settings page → 引擎来源 dropdown:
| Value | Label | Behavior |
|-------|-------|----------|
| `local_service` | 本地服务 | Starts Python subprocess |
| `baidu_cloud` | PaddleOCR云服务 | Cloud API, no local process |
| `onnx_csharp` | ONNX For CSharp | C# in-process, no Python |

When `onnx_csharp`: `ApplicationHostService` skips Python startup, sets ready immediately. Recognition runs via `Task.Run(() => engine.Predict/CrossValidate)` on background thread.

### Configuration (AppConfig)

Key settings in `settings/appsettings.json`:
- `server.engineSource`: engine source selection
- `server.engine`: local engine (onnx_cpu/onnx_dml/paddle)
- `server.onnxGpuId`: GPU device ID for C# ONNX (-1 = CPU)
- `ocrService.modelsDir`: ONNX model directory (default: `models`, relative to app dir)
- Confirmation thresholds, health check params, logging

### CrossValidateAligner

- YX sort → row cluster → IoU match → weighted decay scoring
- `weighted_score = (sum/count) × (1 - α × (1 - count/modelCount))`
- ColorLevel: 2=green (≥confirm), 1=yellow (≥fill), 0=red

### Build Notes

- Post-build copies CUDA DLLs from `TileMind\Dependency` to output `runtimes\win-x64\native\`
- Post-build copies ONNX model files from `ocr_service\models\` to output `models\`
- `AllowUnsafeBlocks` only in `OcrClient.Core.csproj` (for OpenCV pointer → Span bridge)

## Environment

### Python (ocr_service)

- Python 3.12+ on Windows, dependencies in `ocr_service/venv/`
- `paddlepaddle-gpu==3.3.0`, `paddleocr==3.5.0`, `fastapi`, `uvicorn`

### .NET (OcrClient)

- .NET 10.0 SDK, WPF on Windows
- NuGet: `WPF-UI`, `CommunityToolkit.Mvvm`, `Microsoft.Extensions.Hosting`, `OpenCvSharp4`, `ZLogger`, `Microsoft.ML.OnnxRuntime.Gpu 1.27.0`

## Future Plans

1. **Weighted Algorithm Refinement** — non-linear decay, per-model confidence weighting
2. **Rotated text support** — perspective correction via detection box orientation
3. **Better unclip** — Clipper2 C# port for exact match with Python pyclipper
