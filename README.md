# TextRecognizer

基于 PaddleOCR 三模型交叉验证的手写数字表格识别系统，GPU 加速。

本项目由两部分组成：**Python 服务端**（PaddleOCR GPU 推理）和 **WPF 桌面客户端**（图像管理、结果对比、确认导出）。

## 项目结构

```
TextRecognizer/
├── ocr_service/                     # Python OCR 服务端
│   ├── server.py                    # FastAPI 服务（主入口，3 模型 + cross_validate）
│   ├── ocr_server_rec.py            # PP-OCRv5_server_rec 独立脚本
│   ├── ocr_mobile_rec.py            # PP-OCRv5_mobile_rec 独立脚本
│   ├── ocr_en_mobile_rec.py         # en_PP-OCRv5_mobile_rec 独立脚本
│   ├── batch_ocr.py                 # 批量识别（GPU + 标注图 + txt）
│   ├── venv/                        # Python 3.12 虚拟环境
│   ├── models/official_models/      # 本地模型缓存 (~260MB)
│   ├── logs/                        # 服务端日志
│   └── doc/                         # PaddleOCR 离线文档
├── TestDatas/                       # 测试图片 + 识别结果
├── OcrClient/                       # .NET 桌面客户端
│   ├── OcrClient.slnx               # 解决方案
│   ├── OcrClient.Core/              # 共享库 (Models + Services)
│   └── OcrClient/                   # WPF UI 项目
│       ├── Converters/              # 值转换器
│       ├── Models/                  # AppConfig 配置模型
│       ├── ViewModels/              # MVVM ViewModel 层
│       ├── Views/                   # WPF 页面 + code-behind
│       └── Services/                # ApplicationHostService, ServerProcessState, AppConfigService
├── CLAUDE.md
└── README.md
```

## 环境要求

### Python 服务端

- Python 3.12+，NVIDIA GPU + CUDA 12.6 驱动

```bash
cd ocr_service
python -m venv venv
source venv/Scripts/activate
pip install paddleocr==3.5.0 fastapi uvicorn pillow
pip install paddlepaddle-gpu==3.3.0 -i https://www.paddlepaddle.org.cn/packages/stable/cu126/
```

### .NET 客户端

- .NET 10.0 SDK，VS2026 推荐

```bash
dotnet build OcrClient/OcrClient/OcrClient.UI.csproj
```

## 快速开始

### 客户端自动启动（推荐）

1. 用 VS2026 打开 `OcrClient/OcrClient.slnx`，F5 运行
2. 客户端自动启动 Python 服务，等待状态栏变绿

### 手动启动

```bash
# 终端 1：启动 OCR 服务
cd ocr_service && source venv/Scripts/activate && python server.py

# 终端 2：启动客户端
dotnet run --project OcrClient/OcrClient/OcrClient.UI.csproj
```

## 客户端使用说明

### 操作流程

1. **导入图片** — 点击「导入图片」，支持多选，自动去重
2. **选择模式** — 下拉菜单：
   - 交叉验证（三模型）：三模型同时识别
   - 单一模型：仅运行选中模型
3. **开始识别** — 点击「开始识别」（服务就绪后才可用），实时进度 + 计时
4. **查看结果** — 点击左侧图片列表：
   - 交叉验证：同位置结果对齐一行，颜色标记（绿=一致，黄=部分一致，红=不一致）
   - 单一模型：显示识别文本和置信度
5. **确认结果** — 确认列（仅交叉验证）：
   - 绿色行自动确认，黄色行需手动确认
   - 文本框可编辑，点击 ▸ 预览原图裁剪区域
   - 文本框获焦 / 点击 ▸ 按钮显示悬浮窗
   - 点击 ○/✓ 切换确认状态
6. **导出** — 全部确认后「导出确认结果」可用；单模型模式直接「导出结果」
7. **环境检测** — 设置页面可一键检测 Python/venv/脚本/模型并生成安装脚本

### 服务状态

| 颜色 | 含义 |
|---|---|
| 黄色 | 连接中 / 启动中 |
| 绿色 | 就绪 |
| 红色 | 连接断开（点击「重新连接服务」） |

## OCR 服务 API

| 端点 | 方法 | 说明 |
|---|---|---|
| `/health` | GET | 健康检查（含模型状态和请求统计） |
| `/ocr/server_rec` | POST | PP-OCRv5_server_rec |
| `/ocr/mobile_rec` | POST | PP-OCRv5_mobile_rec |
| `/ocr/en_mobile_rec` | POST | en_PP-OCRv5_mobile_rec |
| `/ocr/cross_validate` | POST | 三模型交叉验证 |

请求格式：`{"image": "<base64>"}`

## 识别模型

| 模型 | 大小 | 说明 |
|---|---|---|
| PP-OCRv5_server_det | 85MB | 检测模型（三个 pipeline 共享） |
| PP-OCRv5_server_rec | 82MB | 服务端识别，精度最高 |
| PP-OCRv5_mobile_rec | 13MB | 移动端中文识别 |
| en_PP-OCRv5_mobile_rec | 7.7MB | 移动端英文识别 |

## GPU 性能（RTX 4080 Laptop, 12GB）

| 模型 | 耗时 | vs CPU |
|---|---|---|
| en_PP-OCRv5_mobile_rec | 0.8s | 50x |
| PP-OCRv5_mobile_rec | 1.0s | — |
| PP-OCRv5_server_rec | 1.4s | 178x |
| 三模型交叉验证 | 2.4s | 120x |

## 客户端配置

首次运行自动生成 `settings/appsettings.json`，支持通过设置界面修改：
- 服务连接（地址、超时、重试参数）
- OCR 服务（目录路径、脚本名、启动行为）
- 日志（级别、输出目标、轮转参数）

## 未来计划

- Paddle2ONNX 模型转换，进一步提升推理性能

## License

MIT License
