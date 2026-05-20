# NumberRecognizer

基于 PaddleOCR 多模型交叉验证的手写数字表格识别系统。

## 项目目标

对高分辨率手写数字表格照片进行高精度识别，最终交付为 .NET 桌面应用程序。

**当前阶段：** 双识别模型（PP-OCRv5_server_rec + en_PP-OCRv5_mobile_rec）交叉验证，通过 FastAPI 服务化供 .NET 客户端调用。

## 项目结构

```
NumberRecognizer/
├── ocr_service/                     # Python OCR 服务端
│   ├── server.py                    # FastAPI 服务（主入口）
│   ├── ocr_server_rec.py            # PP-OCRv5_server_rec 独立脚本
│   ├── ocr_en_mobile_rec.py         # en_PP-OCRv5_mobile_rec 独立脚本
│   ├── ocr_ppstructure_v3.py        # PPStructureV3（暂不使用）
│   ├── batch_ocr.py                 # 批量识别脚本
│   ├── venv/                        # Python 虚拟环境
│   ├── TestDatas/                   # 测试图片 + 基准数据
│   └── doc/                         # PaddleOCR 离线文档
├── OcrClient/                       # .NET 桌面客户端
│   ├── OcrClient.slnx               # 解决方案
│   ├── OcrClient.Core/              # 共享库
│   │   ├── Models/OcrResult.cs      # JSON 响应模型
│   │   └── Services/OcrApiClient.cs # OCR 服务 HTTP 调用
│   └── OcrClient/                   # WPF UI 项目
│       ├── App.xaml.cs              # DI 注册 + 启动
│       ├── MainWindow.xaml          # FluentWindow + 导航
│       ├── ViewModels/              # MVVM ViewModel 层
│       ├── Views/                   # WPF 页面
│       └── Services/                # ApplicationHostService
├── CLAUDE.md
└── README.md
```

## 环境要求

### Python 服务端

- Python 3.12+
- PaddlePaddle 3.3.1 + PaddleOCR 3.5.0

```bash
cd ocr_service
source venv/Scripts/activate
pip install paddlepaddle==3.3.1 paddleocr==3.5.0 fastapi uvicorn pillow
```

### .NET 客户端

- .NET 10.0 SDK
- VS2022 / VS Code

## 快速开始

```bash
# 1. 启动 OCR 服务
cd ocr_service
source venv/Scripts/activate
python server.py                     # → http://localhost:8080

# 2. 打开 .NET 客户端
# 用 VS2022 打开 OcrClient/OcrClient.slnx，F5 运行
```

### OCR 服务 API

| 端点 | 方法 | 说明 |
|---|---|---|
| `/health` | GET | 健康检查 |
| `/ocr/server_rec` | POST | PP-OCRv5_server_rec 识别 |
| `/ocr/en_mobile_rec` | POST | en_PP-OCRv5_mobile_rec 识别 |
| `/ocr/cross_validate` | POST | 双模型交叉验证 |

请求格式：`{"image": "<base64>"}`

## 已知局限

- 标准 PaddleOCR 不支持将字符集限制为纯数字，形近字符（0/D、1/I）可能误认
- PPStructureV3 在本数据集上检测为 8 列（实际 7 列），存在列偏移
- 纯 CPU 推理（后续计划支持 GPU 加速）

## License

Apache-2.0 License
