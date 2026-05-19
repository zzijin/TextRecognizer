# NumberRecognizer

基于 PaddleOCR 多模型交叉验证的手写数字表格识别系统。

## 项目目标

对高分辨率手写数字表格照片进行高精度识别。最终实现为 .NET 桌面应用程序。

## 技术路线

使用多个 PaddleOCR 模型独立识别，交叉验证以提高准确率：

| 模型 | 用途 | 特点 |
|---|---|---|
| PP-OCRv5_server_rec | 文本检测 + 数字识别 | 综合精度最高 |
| en_PP-OCRv5_mobile_rec | 文本检测 + 英文/数字识别 | 补充 server 漏检项 |
| PPStructureV3 | 表格结构检测 + OCR | 输出 HTML/Markdown 表格 |

## 环境要求

- Python 3.12+
- Windows 11（开发环境）
- PaddlePaddle 3.3.1 + PaddleOCR 3.5.0

```bash
pip install paddlepaddle==3.3.1 paddleocr==3.5.0
```

> **注意：** Windows 上需要 `enable_mkldnn=False` 以避免 ONEDNN 推理引擎 bug。

# 单模型识别 0001.jpg
python ocr_server_rec.py          # → TestDatas/server_rec/
python ocr_en_mobile_rec.py       # → TestDatas/en_mobile_rec/
python ocr_ppstructure_v3.py      # → TestDatas/ppstructure_v3/

# 批量识别 0002~0004.jpg
python batch_ocr.py
```

## 项目结构

```
NumberRecognizer/
├── README.md
├── CLAUDE.md                         # AI 辅助开发指南
├── ocr_server_rec.py                 # PP-OCRv5_server_rec 模型脚本
├── ocr_en_mobile_rec.py              # en_PP-OCRv5_mobile_rec 模型脚本
├── ocr_ppstructure_v3.py             # PPStructureV3 表格结构模型脚本
├── batch_ocr.py                      # 批量识别脚本
├── doc/                              # PaddleOCR 官方文档（离线参考）
```

## 已知局限

- 标准 PaddleOCR 不支持将字符集限制为纯数字，形近字符（0/D、1/I）可能误认
- PPStructureV3 在本数据集上检测为 8 列（实际 7 列），存在列偏移
- 不依赖 GPU，纯 CPU 推理（需要支持GPU加速）

## License

Apache-2.0 License
