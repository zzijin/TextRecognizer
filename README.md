# NumberRecognizer

基于 PaddleOCR 多模型交叉验证的手写数字表格识别系统。

## 项目目标

对高分辨率手写数字表格照片进行高精度识别，最终交付为 .NET 桌面应用程序。

**当前阶段：** 三识别模型（PP-OCRv5_server_rec + PP-OCRv5_mobile_rec + en_PP-OCRv5_mobile_rec）交叉验证，通过 FastAPI 服务化供 .NET 客户端调用。**支持 GPU 加速（CUDA）。**

## 项目结构

```
NumberRecognizer/
├── ocr_service/                     # Python OCR 服务端
│   ├── server.py                    # FastAPI 服务（主入口，3模型）
│   ├── ocr_server_rec.py            # PP-OCRv5_server_rec 独立脚本
│   ├── ocr_mobile_rec.py            # PP-OCRv5_mobile_rec 独立脚本
│   ├── ocr_en_mobile_rec.py         # en_PP-OCRv5_mobile_rec 独立脚本
│   ├── batch_ocr.py                 # 批量识别（GPU + 标注图 + txt）
│   ├── venv/                        # Python 3.12 虚拟环境
│   ├── models/official_models/      # 本地模型缓存（~260MB）
│   └── doc/                         # PaddleOCR 离线文档
├── TestDatas/                       # 测试图片 + 识别结果
├── OcrClient/                       # .NET 桌面客户端
│   ├── OcrClient.slnx               # 解决方案
│   ├── OcrClient.Core/              # 共享库（Models + Services）
│   └── OcrClient/                   # WPF UI 项目（Views + ViewModels）
├── CLAUDE.md
└── README.md
```

## 环境要求

### Python 服务端（GPU）

- Python 3.12+
- NVIDIA GPU + CUDA 12.6 驱动
- PaddlePaddle-GPU 3.3.0 + PaddleOCR 3.5.0

```bash
cd ocr_service
python -m venv venv
source venv/Scripts/activate
pip install paddleocr==3.5.0 fastapi uvicorn pillow
pip install paddlepaddle-gpu==3.3.0 -i https://www.paddlepaddle.org.cn/packages/stable/cu126/
```

### .NET 客户端

- .NET 10.0 SDK
- VS2026 必需

```bash
dotnet build OcrClient/OcrClient/OcrClient.UI.csproj
```

## 快速开始

### 方式一：客户端自动启动（推荐）

1. 用 VS2022 打开 `OcrClient/OcrClient.slnx`
2. F5 运行
3. 客户端自动启动 Python 服务，等待状态栏变绿

### 方式二：手动启动服务端

```bash
# 终端 1：启动 OCR 服务
cd ocr_service
source venv/Scripts/activate
python server.py                     # → http://localhost:8080

# 终端 2：启动客户端
dotnet run --project OcrClient/OcrClient/OcrClient.UI.csproj
```

## 客户端使用说明

### 操作流程

1. **导入图片** — 点击「导入图片」选择多张图片（支持 jpg/png/bmp）
2. **选择模式** — 工具栏下拉菜单：
   - 交叉验证（三模型）：同时运行 3 个模型，结果对齐对比
   - 单一模型：只运行选中模型
3. **开始识别** — 点击「开始识别」
4. **查看结果** — 点击左侧图片列表查看：
   - 交叉验证：同一位置结果对齐一行，颜色标记一致性
     - 🟢 绿色：三模型一致
     - 🟡 黄色：两模型一致
     - 🔴 红色：不一致或仅单个结果
   - 单一模型：只显示一列结果
5. **确认结果** — 确认列操作：
   - 文本框可编辑，点击 ▸ 按钮查看图片裁剪
   - 点击文本框获取焦点时，悬浮窗显示原图裁剪区域
   - 点击 ○/✓ 切换确认状态
6. **导出** — 全部确认后，点击「导出确认结果」保存 TXT

### 服务状态

| 颜色 | 含义 |
|---|---|
| 黄色 | 连接中 |
| 绿色 | 就绪 |
| 红色 | 连接断开 |

## OCR 服务 API

| 端点 | 方法 | 说明 |
|---|---|---|
| `/health` | GET | 健康检查 |
| `/ocr/server_rec` | POST | PP-OCRv5_server_rec |
| `/ocr/mobile_rec` | POST | PP-OCRv5_mobile_rec |
| `/ocr/en_mobile_rec` | POST | en_PP-OCRv5_mobile_rec |
| `/ocr/cross_validate` | POST | 三模型交叉验证 |

请求格式：`{"image": "<base64>"}`

## GPU 性能（RTX 4080 Laptop, 12GB）

| 模型 | 耗时 | vs CPU |
|---|---|---|
| en_PP-OCRv5_mobile_rec | 0.8s | 50x |
| PP-OCRv5_mobile_rec | 1.0s | — |
| PP-OCRv5_server_rec | 1.4s | 178x |
| 三模型同时（cross_validate） | 2.4s | 120x |

## 已知问题

- 批量识别 4 张图片时，服务端在第 4 张可能卡住（GPU 显存问题）
- 确认列文本框获取焦点时，悬浮窗不弹出（待调试）
- 识别前已选中图片时，识别完成后结果页不自动刷新；需手动切换图片
- 结果识别列表拦截鼠标滚轮，外层无法滚动（ItemsControl 滚轮事件不冒泡）
- 标准 PaddleOCR 不支持纯数字字符集，形近字符可能误认
- Paddle2ONNX 模型转换暂不可用

## License

Apache-2.0 License
