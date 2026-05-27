# TextRecognizer

基于 PaddleOCR 多模型交叉验证的手写数字表格识别系统，支持本地推理和百度云 API 两种识别源。

本项目由两部分组成：**Python 服务端**（OCR GPU/CPU 推理）和 **WPF 桌面客户端**（图像管理、结果对比、确认导出）。

## 项目结构

```
TextRecognizer/
├── ocr_service/                     # Python OCR 服务端
│   ├── server.py                    # PaddlePaddle FastAPI 服务（3 模型 + cross_validate）
│   ├── server_onnx.py               # ONNX Runtime FastAPI 服务（同 API，支持 DML/CPU）
│   ├── onnx_ocr.py                  # ONNX OCR 引擎（纯 NumPy+cv2，无 PaddlePaddle 依赖）
│   ├── convert_to_onnx.py           # PIR→ONNX 模型转换脚本
│   ├── ocr_server_rec.py            # PP-OCRv5_server_rec 独立脚本
│   ├── ocr_mobile_rec.py            # PP-OCRv5_mobile_rec 独立脚本
│   ├── ocr_en_mobile_rec.py         # en_PP-OCRv5_mobile_rec 独立脚本
│   ├── batch_ocr.py                 # 批量识别（GPU + 标注图 + txt）
│   ├── venv/                        # Python 3.12 虚拟环境
│   ├── models/
│   │   ├── official_models/          # PIR 模型 + 字符字典 (~260MB)
│   │   └── onnx_models/             # ONNX 转换后模型 (~188MB)
│   ├── p2o_test_venv/               # paddle2onnx 转换用 venv（paddle 3.1.0 CPU）
│   ├── logs/                        # 服务端日志
│   └── doc/                         # PaddleOCR 离线文档
├── TestDatas/                       # 测试图片 + 识别结果
├── OcrClient/                       # .NET 桌面客户端
│   ├── OcrClient.slnx               # 解决方案
│   ├── OcrClient.Core/              # 共享库 (Models + Services)
│   └── OcrClient/                   # WPF UI 项目
│       ├── Converters/              # 值转换器
│       ├── ViewModels/              # MVVM ViewModel 层
│       ├── Views/                   # WPF 页面
│       └── Services/                # ApplicationHostService, ServerProcessState, AppConfigService
├── CLAUDE.md
└── README.md
```

## 环境要求

### Python 服务端

- Python 3.12+
- 若使用 PaddlePaddle GPU 引擎：NVIDIA GPU + CUDA 12.6
- 若使用 ONNX DML 引擎：Windows 10 1903+，DirectX 12 兼容 GPU（NVIDIA/AMD/Intel）
- ONNX CPU 引擎无特殊硬件要求

#### 依赖安装

**PaddlePaddle GPU 引擎：**
```bash
cd ocr_service
python -m venv venv
source venv/Scripts/activate  # Windows
pip install paddlepaddle-gpu==3.3.0 -i https://www.paddlepaddle.org.cn/packages/stable/cu126/
pip install paddleocr==3.5.0 fastapi uvicorn pillow
```

**ONNX DML (GPU) 引擎：**
```bash
cd ocr_service
python -m venv venv
source venv/Scripts/activate
pip install fastapi uvicorn pillow opencv-python pyclipper numpy onnxruntime-directml
```

**ONNX CPU 引擎：**
```bash
cd ocr_service
python -m venv venv
source venv/Scripts/activate
pip install fastapi uvicorn pillow opencv-python pyclipper numpy onnxruntime
```

### .NET 客户端

- .NET 10.0 SDK，VS2026 推荐

```bash
dotnet build OcrClient/OcrClient/OcrClient.UI.csproj
```

## 快速开始

### 客户端自动启动（推荐）

1. 用 VS2026 打开 `OcrClient/OcrClient.slnx`，F5 运行
2. 客户端按设置页所选引擎自动启动对应 Python 服务
3. 等待状态栏变绿即可使用
4. 如需切换引擎，在设置页选择后保存，重启客户端生效

### 手动启动

```bash
# PaddlePaddle GPU
cd ocr_service && source venv/Scripts/activate && python server.py

# ONNX DML (GPU)
cd ocr_service && source venv/Scripts/activate && ONNX_DEVICE=dml python server_onnx.py

# ONNX CPU
cd ocr_service && source venv/Scripts/activate && ONNX_DEVICE=cpu python server_onnx.py
```

## 客户端使用说明

### 操作流程

1. **导入图片** — 点击「导入图片」，支持多选，自动去重
2. **选择模式** — 下拉菜单：
   - 本地服务：交叉验证（三模型）/ 单一模型
   - 云服务：百度云交叉验证（双模型）/ 高精度单模型 / 标准单模型
3. **开始识别** — 点击「开始识别」（服务就绪后才可用），实时进度 + 计时
4. **查看结果** — 点击左侧图片列表：
   - 交叉验证：多模型结果对齐，加权评分颜色标记（绿/黄/红）
   - 单一模型：显示识别文本和置信度，颜色基于置信度阈值
5. **确认结果** — 确认列（所有模式）：
   - 绿色行自动确认，黄色行需手动确认，红色行需手动填写
   - 文本框获焦时全选文字，可直接输入覆盖
   - 按回车确认当前行，焦点自动跳转到下一未确认行
   - 点击 ▸ 预览原图裁剪区域，点击 ○/✓ 切换确认状态
6. **导出/复制** — 全部确认后「导出确认结果」和「复制确认结果」可用
7. **环境检测** — 设置页面一键检测：本地服务检测 Python/venv/GPU/脚本/模型；云服务检测网络连接

### 推理引擎选择

设置页 → 引擎来源：

| 来源 | 说明 |
|---|---|
| 本地服务 | ONNX CPU / ONNX DML (GPU) / PaddlePaddle (GPU)，支持三模型交叉验证 |
| PaddleOCR云服务 | 百度云通用文字识别高精度版 + 标准版，支持双模型交叉验证 |

本地服务性能：

| 引擎 | 速度 |
|---|---|
| ONNX CPU | ~50s/图 |
| ONNX DML (GPU) | ~2s/图 |
| PaddlePaddle (GPU) | ~2s/图 |

百度云模式：首页可选择交叉验证（双模型加权）、高精度单模型、标准单模型。

### 确认规则配置

设置页可配置所有模式的确认阈值（需重启生效）：

| 阈值 | 默认值 | 作用 |
|---|---|---|
| 单模型自动确认 | 0.99 | 置信度 ≥ 此值自动确认 |
| 单模型自动填写 | 0.95 | 置信度 ≥ 此值自动填写 |
| 交叉验证加权自动确认 | 0.85 | weighted_score ≥ 此值自动确认 |
| 交叉验证加权自动填写 | 0.6 | weighted_score ≥ 此值自动填写 |

切换引擎后需保存设置并重启客户端。

### 服务状态

| 颜色 | 含义 |
|---|---|
| 黄色 | 连接中 / 启动中 |
| 绿色 | 就绪 |
| 红色 | 连接断开（点击「重新连接服务」） |

## OCR 服务 API

所有引擎共享相同 API：

| 端点 | 方法 | 说明 |
|---|---|---|
| `/health` | GET | 健康检查（含设备类型和请求统计） |
| `/ocr/server_rec` | POST | PP-OCRv5_server_rec |
| `/ocr/mobile_rec` | POST | PP-OCRv5_mobile_rec |
| `/ocr/en_mobile_rec` | POST | en_PP-OCRv5_mobile_rec |
| `/ocr/cross_validate` | POST | 三模型交叉验证 |

请求格式：`{"image": "<base64>"}`

## 交叉验证加权算法

多模型交叉验证使用置信度加权算法：

1. **YX排序与行聚类**：所有模型结果按 Y 中心排序，聚类为行，行内按 X 排序
2. **同位置分组**：行内按 IoU（阈值 0.3）跨模型匹配
3. **加权评分**：
   - 按文本分类，计算每类的置信度和（sum）与模型数（count）
   - **选择最佳文本**：取 sum 最高的（共识优先）
   - **加权分数**：`weighted_score = sum / count`（该文本的平均置信度）
4. **自动确认规则**（阈值可配置）：
   - weighted_score ≥ 0.85 → 自动确认（绿）
   - weighted_score ≥ 0.6 → 自动填写（黄）
   - < 0.5 → 不填写（红）

| 场景 | 最佳文本 | Sum | Count | Score | 结果 |
|---|---|---|---|---|---|
| 三个模型一致高置信度 | "369" | 2.84 | 3 | 0.947 | 绿→自动确认 |
| 两个一致，一个分歧 | "369" | 1.75 | 2 | 0.875 | 绿→自动确认 |
| 单模型高置信，另两个一致不同 | "B"(两个) | 1.00 | 2 | 0.500 | 黄→自动填写 |

## 识别模型

| 模型 | PIR 大小 | ONNX 大小 | 说明 |
|---|---|---|---|
| PP-OCRv5_server_det | 85MB | 84MB | 检测模型（共享） |
| PP-OCRv5_server_rec | 82MB | 81MB | 服务端识别（18385 类） |
| PP-OCRv5_mobile_rec | 13MB | 16MB | 移动端中文识别（18385 类） |
| en_PP-OCRv5_mobile_rec | 7.7MB | 7.5MB | 移动端英文识别（438 类） |

## 性能（RTX 4080 Laptop, 12GB）

| 模式 | 单模型 | 三模型交叉验证 |
|---|---|---|
| PaddlePaddle GPU | 0.8–1.4s | 2.4s |
| ONNX DML (GPU) | ~0.6s | ~1.9s |
| ONNX CPU | ~1.2s | ~3.7s（理论） |

## 客户端配置

首次运行自动生成 `settings/appsettings.json`，支持通过设置界面修改：
- 推理引擎（ONNX CPU / ONNX DML / PaddlePaddle）
- 服务连接（地址、超时、重试参数）
- OCR 服务（目录路径、venv 路径、启动行为）
- 日志（级别、输出目标、轮转参数）

## 未来计划

客户端直接支持ONNX模型，取消BS架构。

## License

MIT License
