# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Current Progress (2026-05-21)

**Done:**
- Python OCR service with FastAPI — 4 endpoints (`/ocr/server_rec`, `/ocr/mobile_rec`, `/ocr/en_mobile_rec`, `/ocr/cross_validate`)
- 3-model cross-validation: PP-OCRv5_server_rec + PP-OCRv5_mobile_rec + en_PP-OCRv5_mobile_rec
- **GPU acceleration working** — RTX 4080 Laptop GPU (12GB), paddlepaddle-gpu 3.3.0, CUDA 12.6
- WPF .NET client with image import, batch recognition, real-time progress, result display
- Client auto-starts Python server from venv, kills stale processes on port 8080
- Client continuously monitors server health (poll `/health` every 5s)
- Models cached locally at `ocr_service/models/official_models/` (4 models, ~260MB)
- batch_ocr.py outputs annotated images + txt result files
- **Recognition mode selector** — UI supports Cross-Validate (3-model) or single model (server/mobile/en)
- **Result alignment with color coding** — IoU-based grouping on client side, per-cell agreement coloring (green=3 agree, yellow=2 agree, red=disagree)
- **Confirmation column** — editable text, auto-fill from agreement, confirm toggle, TXT export
- **Image crop popup** — shows cropped region of original image at detected position
- Server: GPU memory cleanup (`empty_cache` + `gc.collect`) after each predict
- All UI text in Chinese

**Not yet working / needs investigation:**
- **Popup not opening on TextBox focus** — `GotKeyboardFocus` event fires but popup doesn't appear. Breakpoint shows `ShowCropPreview` is called but `IsCropPreviewVisible` may not update correctly. Needs debugging.
- **Server hangs on 4th image** — when batch recognizing 4 images, the server reliably hangs/stalls on the last one. GPU utilization drops after image 3. May be GPU memory fragmentation or cuDNN issue.
- ONNX model conversion blocked by missing `.pdmodel` files in PaddleX cache format
- cuDNN version mismatch warning — Paddle compiled with 9.9, installed 9.5.1.17
- HPI (high-performance inference with TensorRT) not available on Windows

## Client Usage Guide

### 启动

1. 确保 Python 3.12+ 已安装，venv 已就绪
2. 用 VS2022 打开 `OcrClient/OcrClient.slnx`，F5 运行
3. 客户端自动启动 Python server（无需手动操作）
4. 等待状态栏变绿："OCR service ready"

### 操作流程

1. **导入图片** — 点击「导入图片」，选择多张图片
2. **选择识别模式** — 工具栏下拉菜单：
   - 交叉验证（三模型）— 同时运行 3 个模型，结果对比显示
   - 单一模型 — 只运行选中的一个模型
3. **开始识别** — 点击「开始识别」，实时进度条显示
4. **查看结果** — 点击左侧图片列表中的图片：
   - 交叉验证模式：结果以行对齐显示，颜色标记一致性
   - 单一模式：只显示一列结果
5. **确认结果** — 在「确认」列中：
   - 绿色行自动确认
   - 黄色行需手动确认（点击 ○/✓ 按钮）
   - 可编辑文本框修改结果
   - 点击 ▸ 按钮查看该位置的图片裁剪
   - 点击确认列文本框聚焦时，悬浮窗显示原图裁剪区域
6. **导出** — 所有行确认后，「导出确认结果」按钮可用，保存为 TXT

### 状态指示

| 颜色 | 含义 |
|---|---|
| 黄色 | 正在连接服务 |
| 绿色 | 服务就绪 |
| 红色 | 连接失败 |

## Known Issues (today's session)

### 1. 悬浮窗在 TextBox 获取焦点时不弹出
- `GotKeyboardFocus` → `ShowCropPreview` 被调用，但 `IsCropPreviewVisible` 没有触发 UI 更新
- `HideCropPreview` 的断点没有被命中，排除 LostFocus 干扰
- ▸ 按钮触发正常，说明 Popup 绑定和 CropPreviewSource 生成逻辑没问题
- 疑点：DataTemplate 内的 GotKeyboardFocus 事件路由到 code-behind 后，ViewModel 的 ObservableProperty 变更通知可能没传回 UI
- 待调试方向：检查 `OnPropertyChanged` 调用链，或考虑用 `Application.Current.Dispatcher.BeginInvoke` 延迟设置

### 2. 服务端在识别第 4 张图片时卡住
- 选择 4 张图片后批量识别，前三张正常，第四张 server 卡住
- GPU 利用率在第 3 张后下降
- 可能原因：GPU 显存碎片化、三模型 pipeline 的 detection model 各占一份显存（3×85MB），多次 predict 后未完全释放
- 已做的修复：每次 predict 后调 `paddle.device.cuda.empty_cache()` + `gc.collect()`
- 待调试方向：在 server 日志中增加 GPU 内存监控，或改为共享 detection model（只加载一份）

### 3. 识别完成后结果页面不刷新（已选中图片时）
- 如果在识别开始前已点击选中某张图片，识别完成后该图片的结果区不自动刷新
- 原因：`CrossValidateGroups` / `SingleResultItems` 只在 `SelectedImage` 切换时重新计算，但 `ImageFileItem.Result` 变化后 `StartRecognitionAsync` 没有通知这些属性变更
- 临时方案：识别完成后手动切换图片（点击其他图片再切回来）
- 修复方向：在 `StartRecognitionAsync` 每张图片识别完成后（item.Result 赋值后），检查是否为当前选中图片，若是则触发 `CrossValidateGroups` / `SingleResultItems` 的 PropertyChanged 通知

### 4. 结果列表拦截鼠标滚轮事件
- 交叉验证结果区的 `ItemsControl` 会捕获鼠标滚轮事件，导致在外层 `ScrollViewer` 上滚动鼠标无效
- 原因：`ItemsControl` 本身不处理滚轮，但也不会将滚轮事件向上冒泡给父级 `ScrollViewer`
- 修复方向：在 `ItemsControl` 上添加 `PreviewMouseWheel` 事件处理，手动将事件传递给外层 `ScrollViewer`，或给 `ItemsControl` 套一层 `ScrollViewer` 并限制高度

## Project Direction

**Current phase:** High-accuracy handwritten digit table recognition via multi-model PaddleOCR cross-validation.

**Future scope:** May extend to other text recognition with specific formats — e.g., regex-based result filtering. The core architecture will remain multi-model cross-validation for handwritten text.

**Delivery target:** .NET desktop application (WPF) wrapping the Python OCR pipeline.

Three models (`PP-OCRv5_server_rec`, `PP-OCRv5_mobile_rec`, `en_PP-OCRv5_mobile_rec`) run independently on each image, then results are cross-validated — agreement across models boosts confidence, disagreements flag uncertain regions.

PPStructureV3 is deferred. Paddle2ONNX is noted for future investigation.

## Project Structure

```
NumberRecognizer/                       # Repo root
├── ocr_service/                     # Python OCR 服务端
│   ├── server.py                    # FastAPI 服务（主入口，3模型 + cross_validate）
│   ├── ocr_server_rec.py            # PP-OCRv5_server_rec 独立脚本
│   ├── ocr_mobile_rec.py            # PP-OCRv5_mobile_rec 独立脚本
│   ├── ocr_en_mobile_rec.py         # en_PP-OCRv5_mobile_rec 独立脚本
│   ├── ocr_ppstructure_v3.py        # PPStructureV3（暂不使用）
│   ├── ocr_predict.py               # Legacy, superseded
│   ├── batch_ocr.py                 # 批量识别（3模型 GPU + 标注图 + txt）
│   ├── venv/                        # Python 3.12 虚拟环境
│   ├── models/                      # 模型缓存
│   │   └── official_models/         # PaddleX 推理模型（~260MB）
│   │       ├── PP-OCRv5_server_det/ # 共享检测模型（85MB）
│   │       ├── PP-OCRv5_server_rec/ # 服务端识别（82MB）
│   │       ├── PP-OCRv5_mobile_rec/ # 移动端中文识别（13MB）
│   │       └── en_PP-OCRv5_mobile_rec/ # 移动端英文识别（7.7MB）
│   └── doc/                         # PaddleOCR 离线文档
├── TestDatas/                       # 测试图片 + 识别结果输出
├── OcrClient/                       # .NET 桌面客户端
│   ├── OcrClient.slnx               # 解决方案
│   ├── OcrClient.Core/              # 共享库 (net10.0)
│   │   ├── Models/OcrResult.cs      # JSON 响应模型 + UI 模型
│   │   └── Services/                # OcrApiClient, CrossValidateAligner
│   └── OcrClient/                   # WPF UI 项目 (net10.0-windows)
│       ├── Converters/              # 值转换器
│       ├── ViewModels/              # MVVM ViewModel 层
│       ├── Views/                   # WPF 页面 + code-behind
│       └── Services/                # ApplicationHostService, ServerProcessState
├── CLAUDE.md
└── README.md
```

## Environment

### Python (ocr_service)

- Python 3.12+ on Windows, dependencies in `ocr_service/venv/`
- **Activate:** `source ocr_service/venv/Scripts/activate` (bash) or `ocr_service\venv\Scripts\activate` (cmd)
- `paddlepaddle-gpu==3.3.0` (CUDA 12.6), `paddleocr==3.5.0`, `fastapi`, `uvicorn`, `pillow`
- **GPU:** `device="gpu"` in all pipeline constructors
- `PADDLE_PDX_CACHE_HOME` env var set in all scripts to `ocr_service/models/`
- **cuDNN warning:** Paddle 3.3.0 compiled with cuDNN 9.9, installed 9.5.1.17

### .NET (OcrClient)

- .NET 10.0 SDK, WPF on Windows
- NuGet: `WPF-UI` 4.3.0, `CommunityToolkit.Mvvm` 8.4.2, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Http`, `OpenCvSharp4`
- UI pattern: FluentWindow + NavigationView side-nav, MVVM with DI

## OCR Models

**Active (3-model cross-validation):**

| Script | Recognition Model | Notes |
|---|---|---|
| `ocr_server_rec.py` | PP-OCRv5_server_rec | Best digit accuracy |
| `ocr_mobile_rec.py` | PP-OCRv5_mobile_rec | Chinese mobile model |
| `ocr_en_mobile_rec.py` | en_PP-OCRv5_mobile_rec | English mobile model |
| `batch_ocr.py` | All 3 above | Batch processing |

All share detection model (`PP-OCRv5_server_det`).

**GPU Performance** (RTX 4080 Laptop, 1307×1920 image, ~180 regions):

| Model | GPU Time | CPU Time | Speedup |
|---|---|---|---|
| en_PP-OCRv5_mobile_rec | 0.8s | 41.4s | 50x |
| PP-OCRv5_mobile_rec | 1.0s | — | — |
| PP-OCRv5_server_rec | 1.4s | 247.7s | 178x |
| cross_validate (3 models) | 2.4s | ~289s | 120x |

## Key PaddleOCR 3.x API Facts

- **Device:** `device="gpu"` enables CUDA GPU inference
- **Explicit model names** override `lang` — the reliable way to pin a specific model
- **Result format:** `result[0]` dict keys: `rec_texts`, `rec_scores`, `dt_polys`, `rec_polys`, `rec_boxes`
- **Built-in output:** `res.save_to_img(dir)`, `res.save_to_json(dir)`, `res.print()`

## External References

- GPU install: https://www.paddlepaddle.org.cn/documentation/docs/zh/install/pip/windows-pip.html
- HPI inference: https://github.com/PaddlePaddle/PaddleX/blob/release/3.5/docs/pipeline_deploy/high_performance_inference.md
- Serving: https://github.com/PaddlePaddle/PaddleX/blob/release/3.5/docs/pipeline_deploy/serving.md
- Paddle2ONNX: https://github.com/PaddlePaddle/PaddleOCR/blob/main/deploy/paddle2onnx/readme.md and https://github.com/PaddlePaddle/Paddle2ONNX/blob/develop/README.md
