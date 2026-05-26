# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Current Progress (2026-05-26)

**Done:**
- Python OCR service with FastAPI — 4 endpoints (`/ocr/server_rec`, `/ocr/mobile_rec`, `/ocr/en_mobile_rec`, `/ocr/cross_validate`)
- 3-model cross-validation: PP-OCRv5_server_rec + PP-OCRv5_mobile_rec + en_PP-OCRv5_mobile_rec
- **GPU acceleration working** — RTX 4080 Laptop GPU (12GB), paddlepaddle-gpu 3.3.0, CUDA 12.6
- **Paddle2ONNX conversion DONE** — all 4 models converted to ONNX (187.9 MB total)
- **ONNX OCR engine** (`onnx_ocr.py`) — standalone inference (no PaddlePaddle), pure NumPy+cv2+pyclipper+onnxruntime
- **ONNX server** (`server_onnx.py`) — same API as Paddle server, shared port 8080
- **DirectML GPU working** — `onnxruntime-directml` 1.24.4, ~2x faster than CPU
- **3-engine architecture** — client Settings page selects: ONNX CPU / ONNX DML (GPU) / PaddlePaddle (GPU), restart-to-apply
- WPF .NET client with image import (dedup), batch recognition (mode-aware skip), real-time progress with elapsed timer, result display
- Client auto-starts Python server from venv based on selected engine, restarts on disconnect, kills stale processes on port 8080
- **Environment check varies by engine** — Settings page detects venv, pip packages, scripts, models per selected engine
- Recognition rate with ONNX equal to or better than PaddlePaddle
- Client continuously monitors server health (poll `/health` every 5s based on config)
- Models cached locally at `ocr_service/models/official_models/` (4 models, ~260MB)
- batch_ocr.py outputs annotated images + txt result files
- **Recognition mode selector** — UI supports Cross-Validate (3-model) or single model (server/mobile/en)
- **Result alignment with color coding** — Y/X coordinate-based grouping on client side, per-cell agreement coloring (green=3 agree, yellow=2 agree, red=disagree)
- **Confirmation column** — editable text, auto-fill from agreement, confirm toggle, TXT export
- **Single model export** — export raw results without confirmation
- **Image crop popup** — shows cropped region of original image at detected position via ▸ button or TextBox focus
- **Annotated debug images** — OpenCvSharp draws all model boxes on images, saved to OutData/
- Server: `rec_polys` instead of `dt_polys` for correct text-box alignment
- Server: GPU memory cleanup (`empty_cache` + `gc.collect`), `threading.Lock` serialization
- Server: auto pipeline reload on CUDA errors, warmup validation on startup
- All UI text in Chinese
- **Settings page** — JSON config (`settings/appsettings.json`), all parameters editable
- **Environment check** — Settings page can verify Python/venv/script/models and generate setup bat files
- Client: `ILogger` integration throughout, ZLogger rolling file logging

**Not yet working / needs investigation:**
- **Popup not opening on TextBox focus** — `GotKeyboardFocus` event fires but popup doesn't appear. ▸ button works.
- cuDNN version mismatch warning — Paddle compiled with 9.9, installed 9.5.1.17
- HPI (high-performance inference with TensorRT) not available on Windows
- ONNX model conversion blocked by missing `.pdmodel` files in PaddleX cache format

## Client Usage Guide

### Startup

1. Ensure Python 3.12+ is installed, venv is ready
2. Open `OcrClient/OcrClient.slnx` in VS2026, F5 to run
3. Client auto-starts Python server (no manual steps needed)
4. Wait for status bar to turn green: "OCR service ready"

### Workflow

1. **Import Images** — Click "导入图片", select multiple images (auto-dedup)
2. **Select Mode** — ComboBox in toolbar:
   - Cross-Validate (3 models) — all 3 models simultaneously, results aligned for comparison
   - Single model — only the selected one
3. **Start Recognition** — Click "开始识别" (disabled when service is not ready or already busy), real-time progress bar + elapsed timer
4. **View Results** — Click an image in the left panel:
   - Cross-Validate: results aligned by position, color-coded
   - Single model: single-column display of text and confidence
5. **Confirm Results** — In the "确认" column:
   - Green rows auto-confirmed
   - Yellow rows need manual confirmation (click ○/✓ button)
   - Editable text box to modify results
   - Click ▸ button for image crop preview at that position
   - Click/focus on the confirmation text box to show crop popup at mouse position
6. **Export** — All confirmed → "导出确认结果" saves TXT; single model → "导出结果" directly

### Status Indicators

| Color | Meaning |
|---|---|
| Yellow | Connecting / Starting |
| Green | Service ready |
| Red | Connection lost (click "重新连接服务" to restart) |

## Known Issues (today's session)

### 1. Popup not opening on TextBox focus
- `GotKeyboardFocus` → `ShowCropPreview` is called but `IsCropPreviewVisible` may not trigger UI update
- `HideCropPreview` breakpoint not hit, ruling out LostFocus interference
- ▸ button trigger works normally, proving Popup binding and CropPreviewSource logic is correct
- Suspect: DataTemplate GotKeyboardFocus event routed to code-behind, ViewModel's ObservableProperty notification may not propagate back to UI
- Debug direction: check `OnPropertyChanged` call chain

### 2. Server hangs on 4th image (FIXED)
- **Root cause:** `ApplicationHostService` set `RedirectStandardOutput/RedirectStandardError = true` but never read the pipes. Python stdout buffer (~4KB) filled up after ~3 images worth of logs, blocking the process.
- **Fix:** Added `BeginOutputReadLine()` / `BeginErrorReadLine()` to continuously drain pipe buffers.
- **Also:** Server-side `threading.Lock` ensures GPU operations serialize properly.

### 3. Recognition result page doesn't refresh (FIXED)
- If an image was already selected before recognition, result area didn't auto-refresh after completion.
- **Fix:** After each image completes in `StartRecognitionAsync`, check if it's the selected image and call `RebuildCachedGroups()`.

### 4. Result list intercepts mouse wheel events (FIXED)
- ItemsControl captures mouse wheel events, preventing outer ScrollViewer from scrolling.
- **Fix:** `PreviewMouseWheel` handler on ItemsControls forwards events to parent ScrollViewer.

## Project Structure

```
TextRecognizer/                       # Repo root
├── ocr_service/                     # Python OCR server
│   ├── server.py                    # FastAPI service (main entry, 3 models + cross_validate)
│   ├── ocr_server_rec.py            # PP-OCRv5_server_rec standalone script
│   ├── ocr_mobile_rec.py            # PP-OCRv5_mobile_rec standalone script
│   ├── ocr_en_mobile_rec.py         # en_PP-OCRv5_mobile_rec standalone script
│   ├── ocr_ppstructure_v3.py        # PPStructureV3 (deferred)
│   ├── batch_ocr.py                 # Batch recognition (3 models GPU + annotated images + txt)
│   ├── venv/                        # Python 3.12 virtualenv
│   ├── models/official_models/      # PaddleX inference models (~260MB)
│   │   ├── PP-OCRv5_server_det/     # Shared detection model (85MB)
│   │   ├── PP-OCRv5_server_rec/     # Server recognition (82MB)
│   │   ├── PP-OCRv5_mobile_rec/     # Mobile Chinese recognition (13MB)
│   │   └── en_PP-OCRv5_mobile_rec/  # Mobile English recognition (7.7MB)
│   ├── logs/                        # Server logs
│   └── doc/                         # PaddleOCR offline docs
├── TestDatas/                       # Test images + recognition output
├── OcrClient/                       # .NET desktop client
│   ├── OcrClient.slnx               # Solution
│   ├── OcrClient.Core/              # Shared library (net10.0)
│   │   ├── Models/                  # AppConfig, OcrResult DTOs, CrossValidateGroup, RecognitionMode
│   │   └── Services/                # OcrApiClient, CrossValidateAligner, LoggingExtensions
│   └── OcrClient/                   # WPF UI project (net10.0-windows)
│       ├── Converters/              # OpenCvRectConverter
│       ├── ViewModels/              # HomeVM, SettingsVM, MainWindowVM, ImageFileItem
│       ├── Views/                   # HomePage, SettingsPage, MainWindow
│       └── Services/                # ApplicationHostService, ServerProcessState, AppConfigService
├── CLAUDE.md
└── README.md
```

## Environment

### Python (ocr_service)

- Python 3.12+ on Windows, dependencies in `ocr_service/venv/`
- `paddlepaddle-gpu==3.3.0` (CUDA 12.6), `paddleocr==3.5.0`, `fastapi`, `uvicorn`, `pillow`
- **GPU:** `device="gpu"`, `text_det_limit_side_len=960`
- `PADDLE_PDX_CACHE_HOME` env var → `ocr_service/models/`
- **cuDNN warning:** Compiled with 9.9, installed 9.5.1.17 (no functional impact)
- **Logs:** `ocr_service/logs/server.log` + stdout

### .NET (OcrClient)

- .NET 10.0 SDK, WPF on Windows (VS2026 recommended)
- NuGet: `WPF-UI` 4.3.0, `CommunityToolkit.Mvvm` 8.4.2, `Microsoft.Extensions.Hosting`, `OpenCvSharp4`, `ZLogger`
- UI pattern: FluentWindow + NavigationView, MVVM with DI

## OCR Models

| Script | Recognition Model | Notes |
|---|---|---|
| `ocr_server_rec.py` | PP-OCRv5_server_rec | Best digit accuracy |
| `ocr_mobile_rec.py` | PP-OCRv5_mobile_rec | Chinese mobile model |
| `ocr_en_mobile_rec.py` | en_PP-OCRv5_mobile_rec | English mobile model |
| `batch_ocr.py` | All 3 above | Batch processing |

All share detection model (`PP-OCRv5_server_det`).

**GPU Performance** (RTX 4080 Laptop, 1307x1920 image, ~180 regions):

| Model | GPU Time | CPU Time | Speedup |
|---|---|---|---|
| en_PP-OCRv5_mobile_rec | 0.8s | 41.4s | 50x |
| PP-OCRv5_mobile_rec | 1.0s | — | — |
| PP-OCRv5_server_rec | 1.4s | 247.7s | 178x |
| cross_validate (3 models) | 2.4s | ~289s | 120x |

## Key Implementation Details

### Server (server.py)

- **GPU Lock:** `threading.Lock` around all predict calls prevents FastAPI thread pool from concurrent GPU access
- **Pipeline Recovery:** `_predict_with_recovery` catches CUDA errors, reloads the pipeline, and retries
- **Warmup:** Runs a 32x32 dummy predict on each pipeline at startup, reloads failed ones
- **Box Fix:** Uses `rec_polys` (not `dt_polys`) to correctly align recognition text with boxes
- **No manual resize:** Removed `_preprocess_image`, rely on PaddleOCR's internal `text_det_limit_side_len=960`

### Client (CrossValidateAligner)

- **Position-based grouping:** All items from 3 models sorted by Y (top to bottom), clustered into rows, then matched by IoU within rows
- **Agreement:** Per-item — 3=all models agree, 2=two agree, 1=unique
- **AutoFill:** All green → auto-confirm; has yellow → fill text but don't confirm; all red → leave empty
- **MergeResult:** Single model results merge into existing CrossValidateResult (additive, not replacement)

### Client (HomeViewModel)

- **Mode-aware skip:** Cross-validate completion marks all 4 modes (CV + 3 singles); single modes only mark themselves
- **Import dedup:** Checks `Images.Select(i => i.FilePath).ToHashSet()`
- **CanStartRecognition:** `IsServerReady && !IsBusy && Images.Count > 0`
- **Elapsed timer:** Updates every second during recognition, displayed as MM:SS
- **Server restart:** Disconnects → red status → "重新连接服务" button → kills + restarts Python process

### Configuration (AppConfig)

- JSON file at `{exe}/settings/appsettings.json`, auto-generated on first run
- Three sections: `server` (URL, timeout, health params), `ocrService` (paths, flags), `logging` (level, outputs, rolling)
- Settings page allows editing all values; environment check generates `setup_venv.bat` / `download_models.bat`

## Lessons from This Conversation

### GPU Acceleration

- RTX 4080 Laptop (12GB) delivers 50–178x speedup over CPU
- ONEDNN PIR bug (3.3.0+) is CPU-only; GPU unaffected
- 3 pipelines loaded simultaneously consume ~0.35GB GPU memory
- `paddle.device.cuda.empty_cache()` + `gc.collect()` after each predict

### Pipe Buffer Deadlock

- `RedirectStandardOutput = true` without reading the pipe caused server to hang after ~3 images
- Fix: `BeginOutputReadLine()` / `BeginErrorReadLine()` to drain pipes

### Coordinate Systems

- PaddleOCR's `dt_polys` (detection raw) and `rec_texts` can be in different order
- Use `rec_polys` to match recognition text with correct box
- PaddleOCR returns boxes in input image coordinates; don't manually resize before passing to pipeline

### CrossValidateAligner Evolution

- v1: anchor-based (server_rec as master) → v2: added mobile↔en cross-match for unmatched → v3: full Y/X coordinate sort with clustering
- IoU threshold 0.3 for box matching; Y-row threshold = avg box height / 2

### Paddle2ONNX Conversion (2026-05-26)

- **ABI issue:** `paddle2onnx` 2.1.0 DLL incompatible with `paddlepaddle-gpu` 3.3.0 (PIR C++ API: `pir::OpBase` → `pir::Operation`)
- **Solution:** Separate venv `p2o_test_venv` with `paddlepaddle==3.1.0` (CPU) + `paddle2onnx==2.1.0`
- **CLI command:** `p2o_test_venv/Scripts/paddle2onnx.exe --model_dir <dir> --model_filename inference.json --params_filename inference.pdiparams --save_file <out>.onnx --opset_version 14`
- **Conversion script:** `ocr_service/convert_to_onnx.py` for reproducible conversion
- **Output:** `ocr_service/models/onnx_models/*.onnx` (4 files, 187.9 MB total)

**ONNX Models:**

| Model | Size | Nodes | Input | Output |
|---|---|---|---|---|
| en_PP-OCRv5_mobile_rec | 7.5 MB | 549 | `[B,3,48,W]` | `[B,seq,438]` |
| PP-OCRv5_mobile_rec | 15.8 MB | 549 | `[B,3,48,W]` | `[B,seq,18385]` |
| PP-OCRv5_server_rec | 80.6 MB | 511 | `[B,3,48,W]` | `[B,seq,18385]` |
| PP-OCRv5_server_det | 84.0 MB | 595 | `[B,3,H,W]` | `[1,1,H,W]` |

**ONNX Inference Performance** (en_mobile_rec, 1×3×48×320):

| Provider | Time | FPS |
|---|---|---|
| DML GPU (DirectX) | 3.49 ms | 286 |
| CPU | 6.77 ms | 148 |

**Estimated per-image (~180 regions):**

| Mode | DML GPU | CPU | Paddle GPU (ref) |
|---|---|---|---|
| Single rec model | 0.6s | 1.2s | 0.8–1.4s |
| Cross-validate (3 models) | 1.9s | 3.7s | 2.4s |

**ONNX OCR Engine** (`ocr_service/onnx_ocr.py`):
- Pure NumPy + cv2 + pyclipper + onnxruntime — no PaddlePaddle dependency
- Pre/post processing replicated from PaddleX/PaddleOCR
- `OnnxOcrEngine.predict(image_path)` → same item format as Paddle pipeline
- `OnnxOcrEngine.predict_image(bgr_array)` → for use in server
- Supports `mode="cross_validate"` with 3 models or `mode="single"` with specific rec_key

**ONNX Server** (`ocr_service/server_onnx.py`):
- Same API as `server.py` (`/ocr/server_rec`, `/ocr/mobile_rec`, `/ocr/en_mobile_rec`, `/ocr/cross_validate`)
- Runs on port 8080 (configurable via `ONNX_PORT` env var)
- Shares port with Paddle server (only one engine active at a time)

**Client engine selection:**
- Settings page → 推理引擎 dropdown: ONNX CPU / ONNX DML (GPU) / PaddlePaddle (GPU)
- Changes take effect after restart (not runtime)
- All engines share `BaseUrl` (port 8080)
- `ApplicationHostService` reads `Server.Engine` from config, starts corresponding script:
  - `"onnx_cpu"` → `server_onnx.py` with `ONNX_DEVICE=cpu`
  - `"onnx_dml"` → `server_onnx.py` with `ONNX_DEVICE=dml`
  - `"paddle"` → `server.py`

**Dependencies for ONNX inference:**
- `onnxruntime-directml` (GPU via DirectX) or `onnxruntime` (CPU)
- `opencv-python`, `pyclipper`, `numpy`, `Pillow`
- Conversion venv (`p2o_test_venv`) only needed for re-converting models

### Future Plan: Client-side ONNX (2026-05-26 planned)

Move ONNX inference from Python server into the WPF client process:
- Eliminate Python dependency entirely for ONNX mode
- Use `Microsoft.ML.OnnxRuntime.DirectML` NuGet (proven in TileMind project)
- Reimplement pre/post processing from `onnx_ocr.py` in C# with OpenCvSharp
- Bundle .onnx files (~188 MB) with the app
- PaddlePaddle mode remains via server.py (HTTP)

**Current fallback (2026-05-26):** Use `server_onnx.py` as HTTP server, replacing `server.py`.
Client engine selector supports ONNX DML (GPU, port 8081) and ONNX CPU (port 8082).
Restart server process on engine switch with `ONNX_DEVICE` env var.

## External References

- GPU install: https://www.paddlepaddle.org.cn/documentation/docs/zh/install/pip/windows-pip.html
- HPI inference: https://github.com/PaddlePaddle/PaddleX/blob/release/3.5/docs/pipeline_deploy/high_performance_inference.md
- Serving: https://github.com/PaddlePaddle/PaddleX/blob/release/3.5/docs/pipeline_deploy/serving.md
- Paddle2ONNX: https://github.com/PaddlePaddle/PaddleOCR/blob/main/deploy/paddle2onnx/readme.md
- Repository: https://github.com/zzijin/TextRecognizer
