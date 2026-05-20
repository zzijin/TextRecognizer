# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Direction

**Current phase:** High-accuracy handwritten digit table recognition via multi-model PaddleOCR cross-validation.

**Future scope:** May extend to other text recognition with specific formats — e.g., regex-based result filtering. The core architecture will remain multi-model cross-validation for handwritten text.

**Delivery target:** .NET desktop application (WinForms/WPF) wrapping the Python OCR pipeline.

The two working models (`PP-OCRv5_server_rec`, `en_PP-OCRv5_mobile_rec`) run independently on each image, then results are cross-validated — agreement across models boosts confidence, disagreements flag uncertain regions.

PPStructureV3 is deferred — may revisit later if table structure detection is needed.

## Project Structure

```
NumberRecognizer/
├── ocr_service/                     # Python OCR 服务端
│   ├── server.py                    # FastAPI 服务（主入口，生产调用）
│   ├── ocr_server_rec.py            # PP-OCRv5_server_rec 独立脚本
│   ├── ocr_en_mobile_rec.py         # en_PP-OCRv5_mobile_rec 独立脚本
│   ├── ocr_ppstructure_v3.py        # PPStructureV3（暂不使用）
│   ├── ocr_config.yaml              # PaddleX 服务化配置
│   ├── batch_ocr.py                 # 批量识别脚本
│   ├── venv/                        # Python 虚拟环境
│   ├── TestDatas/                   # 测试图片 + 基准数据
│   └── doc/                         # PaddleOCR 离线文档
├── OcrClient/                       # .NET 桌面客户端
│   ├── OcrClient.slnx               # 解决方案
│   ├── OcrClient.Core/              # 共享库 (net10.0)
│   │   ├── Models/OcrResult.cs      # JSON 响应模型 (OcrItem, CrossValidateResult)
│   │   └── Services/OcrApiClient.cs # HTTP 调用，3 个端点方法
│   └── OcrClient/                   # WPF UI 项目 (net10.0-windows)
│       ├── App.xaml.cs              # IHost + DI 注册 (CommunityToolkit.Mvvm, WPF-UI)
│       ├── MainWindow.xaml / .cs    # FluentWindow + INavigationWindow
│       ├── ViewModels/              # ViewModel (base), MainWindowViewModel, HomeViewModel, SettingsViewModel
│       ├── Views/                   # HomePage, SettingsPage (INavigableView<T>)
│       └── Services/                # ApplicationHostService (IHostedService)
├── PaddleX/                         # PaddleX 源码（参考用）
├── CLAUDE.md
└── README.md
```

## Environment

### Python (ocr_service)

- Python 3.12+ on Windows, dependencies in `ocr_service/venv/`
- **Activate:** `source ocr_service/venv/Scripts/activate` (bash) or `ocr_service\venv\Scripts\activate` (cmd)
- `paddlepaddle==3.3.1`, `paddleocr==3.5.0`, `fastapi`, `uvicorn`, `pillow`
- `enable_mkldnn=False` is **required** on Windows to avoid ONEDNN PIR conversion bug
- **GPU support is planned** — currently CPU-only; will need CUDA paddlepaddle and `device="gpu"` configuration later

### .NET (OcrClient)

- .NET 10.0 SDK, WPF on Windows
- NuGet: `WPF-UI` 4.3.0, `CommunityToolkit.Mvvm` 8.4.2, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Http`, `OpenCvSharp4`
- UI pattern: FluentWindow + NavigationView side-nav, MVVM with DI, `INavigationWindow.Navigate(Type)` for page routing

## OCR Models

**Active (cross-validation):**

| Script | Recognition Model | Notes |
|---|---|---|
| `ocr_server_rec.py` | PP-OCRv5_server_rec | Best overall digit accuracy |
| `ocr_en_mobile_rec.py` | en_PP-OCRv5_mobile_rec | Catches some items server misses |
| `batch_ocr.py` | Both above | Batch processing |

Both share the same detection model (`PP-OCRv5_server_det`) — the difference is in the recognition model.

**Deferred (may revisit later for table structure):**

| Script | Notes |
|---|---|
| `ocr_ppstructure_v3.py` | PPStructureV3 pipeline — adds layout analysis + table structure, but internally reuses PP-OCRv5_server_rec for recognition. Not part of current cross-validation. |
| `ocr_predict.py` | Legacy, superseded |

## Running Recognition

### FastAPI service (primary)

```bash
cd ocr_service
source venv/Scripts/activate
python server.py                     # → http://localhost:8080, docs at /docs
```

Endpoints: `/health` (GET), `/ocr/server_rec` (POST), `/ocr/en_mobile_rec` (POST), `/ocr/cross_validate` (POST). All POST endpoints accept `{"image": "<base64>"}`.

### Standalone scripts

```bash
cd ocr_service && source venv/Scripts/activate
python ocr_server_rec.py        # → TestDatas/server_rec/
python ocr_en_mobile_rec.py     # → TestDatas/en_mobile_rec/
python ocr_ppstructure_v3.py    # → TestDatas/ppstructure_v3/
python batch_ocr.py             # All images
```

### .NET client

Open `OcrClient/OcrClient.slnx` in VS2022, ensure `ocr_service/server.py` is running on port 8080, then F5.

## .NET Architecture

- **OcrClient.Core**: `OcrResult`/`CrossValidateResult` DTOs with `System.Text.Json`; `OcrApiClient` wrapping `HttpClient` calls to the FastAPI service.
- **OcrClient.UI**: WPF-UI `FluentWindow` + `NavigationView` side-nav. `MainWindow : FluentWindow, INavigationWindow` delegates `Navigate(Type)` to `RootNavigation.Navigate()`. Pages implement `INavigableView<T>`.
- **DI**: `App.xaml.cs` builds `IHost`, registers `ApplicationHostService`, all ViewModels/Pages as singletons, `OcrApiClient` with typed `HttpClient` via `AddHttpClient`.
- **Startup flow**: `IHost.StartAsync()` → `ApplicationHostService.StartAsync()` → create `MainWindow` via DI → `Navigate(typeof(HomePage))`.

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

Key docs in `ocr_service/doc/`:
- `通用OCR产线使用教程.html` — `PaddleOCR` pipeline parameters and defaults
- `PP-StructureV3 产线使用教程.html` — `PPStructureV3` table/document structure pipeline
- `服务化部署 - PaddleOCR 文档.html` — PaddleX serving deployment (CLI-based, had issues on Windows)
- `PaddleOCR 与 PaddleX - PaddleOCR 文档 .html` — Relationship between PaddleOCR and PaddleX
- `通过OCR实现验证码识别.html` — May be relevant for digit-focused recognition

PaddleX source at `PaddleX/` — reference for serving implementation details (request schema, fileType mapping: 0=PDF, 1=IMAGE).

## Known Issues

- No built-in character-set restriction (e.g., "11D" from "110")
- PPStructureV3 column detection may not exactly match the actual grid on handwritten tables
- Pure CPU inference — GPU acceleration is planned but not yet configured
- Model files auto-cache; the `models/` directory has unused copies
