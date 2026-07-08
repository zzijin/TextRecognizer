# TextRecognizer

基于 PaddleOCR 多模型交叉验证的文字识别系统，支持本地 Python 服务、C# ONNX 直接推理和百度云 API 三种识别源。

## 项目结构

```
TextRecognizer/
├── ocr_service/                     # Python OCR 服务端
│   ├── server.py                    # PaddlePaddle FastAPI 服务（3 模型 + cross_validate）
│   ├── server_onnx.py               # ONNX Runtime FastAPI 服务（同 API，DML/CPU）
│   ├── onnx_ocr.py                  # ONNX OCR 引擎（纯 NumPy+cv2，无 PaddlePaddle 依赖）
│   ├── convert_to_onnx.py           # PIR→ONNX 模型转换脚本
│   ├── ocr_server_rec.py            # PP-OCRv5_server_rec 独立脚本
│   ├── ocr_mobile_rec.py            # PP-OCRv5_mobile_rec 独立脚本
│   ├── ocr_en_mobile_rec.py         # en_PP-OCRv5_mobile_rec 独立脚本
│   ├── batch_ocr.py                 # 批量识别（GPU + 标注图 + txt）
│   └── models/
│       ├── det/                     # 检测模型：{name}/model.onnx
│       └── rec/                     # 识别模型：{name}/model.onnx + char_dict.json
├── OcrClient/                       # .NET 桌面客户端
│   ├── OcrClient.slnx               # 解决方案
│   ├── OcrClient.Core/              # 共享库
│   │   ├── Models/                  # AppConfig, OcrResult, CrossValidateGroup, OcrTiming
│   │   ├── Services/                # OcrApiClient, BaiduOcrClient, CrossValidateAligner
│   │   └── Onnx/                    # C# ONNX 引擎（Engine / Preprocess / Postprocess / CharDict）
│   ├── OcrClient/                   # WPF UI 项目
│   │   ├── Converters/              # 值转换器
│   │   ├── ViewModels/              # MVVM ViewModel 层
│   │   ├── Views/                   # WPF 页面
│   │   └── Services/                # ApplicationHostService, ServerProcessState, AppConfigService
│   └── onnx_test/                   # ONNX 引擎验证测试
├── CLAUDE.md
└── README.md
```

## 环境要求

### Python 服务端（仅本地服务模式需要）

- Python 3.12+，NVIDIA GPU + CUDA 12.6 + cuDNN 9.x

```bash
cd ocr_service
python -m venv venv
source venv/Scripts/activate
pip install paddlepaddle-gpu==3.3.0 -i https://www.paddlepaddle.org.cn/packages/stable/cu126/
pip install paddleocr==3.5.0 fastapi uvicorn pillow
```

### .NET 客户端

- .NET 10.0 SDK
- ONNX C# GPU：NVIDIA GPU + cuDNN 9.x DLL（构建时自动复制）
- ONNX C# CPU：无特殊硬件要求

```bash
dotnet build OcrClient/OcrClient/OcrClient.UI.csproj
```

## 快速开始

1. 用 VS2026 打开 `OcrClient/OcrClient.slnx`，F5 运行
2. 设置页选择引擎来源：
   - **ONNX For CSharp**：C# 进程内推理，无需 Python
   - **本地服务**：启动 Python 子进程
   - **百度云**：云端 API
3. 等待状态栏变绿，导入图片，开始识别

## 使用说明

1. **导入图片** — 多选，自动去重
2. **选择模式** — 交叉验证（多模型）/ 单一模型
3. **开始识别** — 实时进度 + 计时，ONNX C# 模式异步不卡 UI
4. **确认结果** — 绿/黄/红颜色标记，自动/手动确认，编辑文字
5. **导出** — 确认结果 / 复制 / 批注图片

## 推理引擎

| 来源 | 说明 | GPU | CPU |
|------|------|-----|-----|
| **ONNX For CSharp** | C# 进程内，无 Python 依赖 | **~280ms** | ~4.5s |
| 本地服务 | Python 子进程，PaddlePaddle 或 ONNX DML | ~2s | ~50s |
| PaddleOCR云服务 | 百度云 API，双模型交叉验证 | — | — |

> 性能数据为 5 张麻将图片的交叉验证平均耗时（RTX 4080 Laptop）。

## 模型目录结构

```
.\models\                        ← 配置项 ModelsDir（默认 ./models）
├── det\                         ← 检测模型
│   └── PP-OCRv5_server_det\
│       └── model.onnx
├── rec\                         ← 识别模型
│   ├── PP-OCRv5_server_rec\
│   │   ├── model.onnx
│   │   └── char_dict.json       ← ["blank","0","1",...,"北","東",...]
│   ├── PP-OCRv5_mobile_rec\
│   │   ├── model.onnx
│   │   └── char_dict.json
│   └── en_PP-OCRv5_mobile_rec\
│       ├── model.onnx
│       └── char_dict.json
```

引擎自动扫描 `det/` `rec/` 子目录，添加新模型只需放入子目录即可。

## 识别模型

| 模型 | 大小 | 字符集 |
|------|------|--------|
| PP-OCRv5_server_det | 84MB | — |
| PP-OCRv5_server_rec | 81MB | 18384 |
| PP-OCRv5_mobile_rec | 16MB | 18384 |
| en_PP-OCRv5_mobile_rec | 7.5MB | 437 |

## 交叉验证算法

1. 所有模型结果按 YX 排序聚类为行
2. 行内按 IoU（阈值 0.3）跨模型匹配
3. 加权衰减评分：`weighted_score = (sum/count) × (1 - α × (1 - count/modelCount))`
4. 颜色标记：绿(≥0.85) / 黄(≥0.6) / 红(<0.6)

## 配置项

`settings/appsettings.json` 主要配置：

| 配置项 | 默认值 | 说明 |
|---|---|---|
| `server.engineSource` | `local_service` | 引擎来源 |
| `server.engine` | `onnx_cpu` | 本地引擎 |
| `server.onnxGpuId` | `0` | GPU ID（-1=CPU） |
| `ocrService.modelsDir` | `models` | ONNX 模型目录 |
| `server.crossValidateAutoConfirmThreshold` | 0.85 | 交叉验证自动确认阈值 |

## License

MIT License
