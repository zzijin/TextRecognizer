# TextRecognizer

基于 PaddleOCR 多模型交叉验证的手写数字表格识别系统，支持本地推理、C# ONNX 直接推理和百度云 API 三种识别源。

本项目由两部分组成：**Python 服务端**（PaddlePaddle GPU 推理）和 **WPF 桌面客户端**（图像管理、结果对比、确认导出，内置 C# ONNX 引擎）。

## 项目结构

```
TextRecognizer/
├── ocr_service/                     # Python OCR 服务端
│   ├── server.py                    # PaddlePaddle FastAPI 服务（3 模型 + cross_validate）
│   ├── server_onnx.py               # ONNX Runtime FastAPI 服务（同 API，支持 DML/CPU）
│   ├── onnx_ocr.py                  # ONNX OCR 引擎（纯 NumPy+cv2，无 PaddlePaddle 依赖）
│   ├── convert_to_onnx.py           # PIR→ONNX 模型转换脚本
│   ├── migrate_models.py            # 模型迁移脚本 → 新 det/ rec/ 目录结构
│   ├── batch_ocr.py                 # 批量识别（GPU + 标注图 + txt）
│   ├── venv/                        # Python 3.12 虚拟环境
│   ├── models/
│   │   ├── det/                     # 检测模型（新结构：{name}/model.onnx）
│   │   ├── rec/                     # 识别模型（新结构：{name}/model.onnx + char_dict.json）
│   │   ├── official_models/         # PIR 原始模型 + 字符字典 (~260MB)
│   │   └── onnx_models/             # ONNX 旧结构（保留兼容）(~188MB)
│   ├── p2o_test_venv/               # paddle2onnx 转换用 venv（paddle 3.1.0 CPU）
│   ├── logs/                        # 服务端日志
│   └── doc/                         # PaddleOCR 离线文档
├── TestDatas/                       # 测试图片 + 识别结果
├── OcrClient/                       # .NET 桌面客户端
│   ├── OcrClient.slnx               # 解决方案
│   ├── OcrClient.Core/              # 共享库
│   │   ├── Models/                  # AppConfig, OcrResult, CrossValidateGroup
│   │   ├── Services/                # OcrApiClient, BaiduOcrClient, CrossValidateAligner
│   │   └── Onnx/                    # C# ONNX 引擎（OnnxOcrEngine / Preprocess / Postprocess / CharDict）
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

### Python 服务端

- Python 3.12+
- PaddlePaddle GPU 引擎：NVIDIA GPU + CUDA 12.6 + cuDNN 9.x

#### 依赖安装

```bash
cd ocr_service
python -m venv venv
source venv/Scripts/activate  # Windows
pip install paddlepaddle-gpu==3.3.0 -i https://www.paddlepaddle.org.cn/packages/stable/cu126/
pip install paddleocr==3.5.0 fastapi uvicorn pillow
```

### .NET 客户端

- .NET 10.0 SDK，VS2026 推荐
- ONNX C# GPU 模式：NVIDIA GPU + CUDA 12.x + cuDNN 9.x（DLL 从 TileMind 复制）
- ONNX C# CPU 模式：无特殊硬件要求

```bash
dotnet build OcrClient/OcrClient/OcrClient.UI.csproj
```

## 快速开始

### 客户端自动启动（推荐）

1. 用 VS2026 打开 `OcrClient/OcrClient.slnx`，F5 运行
2. 客户端按设置页所选引擎来源自动启动：
   - **本地服务**：启动 Python 子进程（PaddlePaddle 或 ONNX）
   - **ONNX For CSharp**：引擎内置于 C# 进程，无需 Python
   - **百度云**：直接调用云端 API
3. 等待状态栏变绿即可使用
4. 切换引擎后保存设置，重启客户端生效

### 手动启动（Python 服务端）

```bash
cd ocr_service && source venv/Scripts/activate
python server.py                                # PaddlePaddle GPU
ONNX_DEVICE=dml python server_onnx.py           # ONNX DML (GPU)
ONNX_DEVICE=cpu python server_onnx.py           # ONNX CPU
```

## 客户端使用说明

### 操作流程

1. **导入图片** — 点击「导入图片」，支持多选，自动去重
2. **选择模式** — 下拉菜单：
   - 本地服务 / ONNX C#：交叉验证（三模型）/ 单一模型
   - 百度云：交叉验证（双模型）/ 高精度单模型 / 标准单模型
3. **开始识别** — 点击「开始识别」，实时进度 + 计时，ONNX C# 模式下异步不卡 UI
4. **查看结果** — 点击左侧图片列表：
   - 交叉验证：多模型结果对齐，加权评分颜色标记（绿/黄/红）
   - 单一模型：显示识别文本和置信度，颜色基于置信度阈值
5. **确认结果** — 确认列：
   - 绿色行自动确认，黄色行需手动确认，红色行需手动填写
   - 按回车确认当前行，焦点自动跳转到下一未确认行
   - 点击 ▸ 预览原图裁剪区域，点击 ○/✓ 切换确认状态
6. **导出** — 「导出确认结果」「复制确认结果」「导出批注图片」

### 推理引擎对比

设置页 → 引擎来源：

| 来源 | 说明 | GPU | CPU |
|------|------|-----|-----|
| 本地服务 | Python 子进程，PaddlePaddle 或 ONNX | ~2s/图 | ~50s/图 |
| **ONNX For CSharp** | **C# 进程内推理，无 Python 依赖** | **~274ms/图** | **~4.5s/图** |
| PaddleOCR云服务 | 百度云 API，双模型交叉验证 | N/A | N/A |

### 模型目录结构

ONNX C# 引擎的模型目录：

```
.\models\                        ← 配置项 ModelsDir
├── det\                         ← 检测模型
│   └── PP-OCRv5_server_det\
│       └── model.onnx
├── rec\                         ← 识别模型
│   ├── PP-OCRv5_server_rec\
│   │   ├── model.onnx
│   │   └── char_dict.json       ← 字符字典（["blank","0","1",...]）
│   ├── PP-OCRv5_mobile_rec\
│   │   ├── model.onnx
│   │   └── char_dict.json
│   └── en_PP-OCRv5_mobile_rec\
│       ├── model.onnx
│       └── char_dict.json
```

引擎自动扫描 `det/` `rec/` 子目录，加载所有可用模型。添加新模型只需创建子目录放入 `model.onnx` 和 `char_dict.json`，无需修改代码。

### 确认规则配置

| 阈值 | 默认值 | 作用 |
|---|---|---|
| 单模型自动确认 | 0.99 | 置信度 >= 此值自动确认 |
| 单模型自动填写 | 0.95 | 置信度 >= 此值自动填写 |
| 交叉验证加权自动确认 | 0.85 | weighted_score >= 此值自动确认 |
| 交叉验证加权自动填写 | 0.6 | weighted_score >= 此值自动填写 |
| 衰减系数 α | 0.5 | 共识度惩罚强度，越大惩罚越重 |

## OCR 服务 API

Python 服务端提供的 REST API（所有引擎共享）：

| 端点 | 方法 | 说明 |
|---|---|---|
| `/health` | GET | 健康检查 |
| `/ocr/server_rec` | POST | PP-OCRv5_server_rec |
| `/ocr/mobile_rec` | POST | PP-OCRv5_mobile_rec |
| `/ocr/en_mobile_rec` | POST | en_PP-OCRv5_mobile_rec |
| `/ocr/cross_validate` | POST | 三模型交叉验证 |

请求格式：`{"image": "<base64>"}`

## 识别模型

| 模型 | ONNX 大小 | 字符集 | 说明 |
|---|---|---|---|
| PP-OCRv5_server_det | 84MB | — | 检测模型（共享） |
| PP-OCRv5_server_rec | 81MB | 18384 | 服务端识别（最高精度） |
| PP-OCRv5_mobile_rec | 16MB | 18384 | 移动端中文识别 |
| en_PP-OCRv5_mobile_rec | 7.5MB | 437 | 移动端英文识别 |

## 性能对比（RTX 4080 Laptop, 5 张麻将图片）

| 模式 | 交叉验证 (3模型) | 单模型 (server_rec) |
|---|---|---|
| **C# ONNX GPU (CUDA)** | **274ms** | **92ms** |
| Python ONNX GPU (DML) | ~1.9s | ~0.6s |
| Python PaddlePaddle GPU | ~2.4s | ~1.4s |
| C# ONNX CPU | ~4.5s | ~4.7s |

> C# ONNX GPU 比 Python ONNX DML 快约 **7 倍**，比 PaddlePaddle GPU 快约 **9 倍**。

## 交叉验证加权算法

1. **YX排序**：所有模型结果按 Y 中心聚类为行，行内按 X 排序
2. **同位置分组**：行内按 IoU（阈值 0.3）跨模型匹配
3. **加权衰减评分**：`weighted_score = (sum/count) × (1 - α × (1 - count/modelCount))`
4. **颜色标记**：绿(≥0.85) / 黄(≥0.6) / 红(<0.6)
5. **自动确认**：最高 weighted_score 的文本胜出

## 客户端配置

`settings/appsettings.json` 主要配置项：

| 配置项 | 默认值 | 说明 |
|---|---|---|
| `server.engineSource` | `local_service` | 引擎来源：local_service / baidu_cloud / onnx_csharp |
| `server.engine` | `onnx_cpu` | 本地引擎：onnx_cpu / onnx_dml / paddle |
| `server.onnxGpuId` | `0` | ONNX C# GPU 设备 ID（-1=CPU） |
| `ocrService.modelsDir` | `models` | ONNX 模型目录（含 det/ rec/） |

## License

MIT License
