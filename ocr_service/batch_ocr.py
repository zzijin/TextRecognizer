import os
os.environ["FLAGS_use_onednn"] = "0"
os.environ["PADDLE_PDX_CACHE_HOME"] = os.path.join(os.path.dirname(os.path.abspath(__file__)), "models")

from paddleocr import PaddleOCR, PPStructureV3

images = ["TestDatas/0002.jpg", "TestDatas/0003.jpg", "TestDatas/0004.jpg"]

# ===== Model 1: PP-OCRv5_server_rec =====
print("=" * 60)
print("PP-OCRv5_server_rec")
ocr_server = PaddleOCR(
    use_doc_orientation_classify=False,
    use_doc_unwarping=False,
    use_textline_orientation=False,
    enable_mkldnn=True,
)
for img in images:
    name = img.split("/")[-1].replace(".jpg", "")
    result = ocr_server.predict(img)
    for res in result:
        res.save_to_img(f"TestDatas/server_rec/{name}")
    print(f"  {name} -> TestDatas/server_rec/{name}_ocr_res_img.jpg")

# ===== Model 2: en_PP-OCRv5_mobile_rec =====
print("=" * 60)
print("en_PP-OCRv5_mobile_rec")
ocr_en = PaddleOCR(
    text_detection_model_name="PP-OCRv5_server_det",
    text_recognition_model_name="en_PP-OCRv5_mobile_rec",
    use_doc_orientation_classify=False,
    use_doc_unwarping=False,
    use_textline_orientation=False,
    enable_mkldnn=True,
)
for img in images:
    name = img.split("/")[-1].replace(".jpg", "")
    result = ocr_en.predict(img)
    for res in result:
        res.save_to_img(f"TestDatas/en_mobile_rec/{name}")
    print(f"  {name} -> TestDatas/en_mobile_rec/{name}_ocr_res_img.jpg")

# ===== Model 3: PPStructureV3 =====
print("=" * 60)
print("PPStructureV3")
pipeline = PPStructureV3(
    lang="en",
    use_doc_orientation_classify=False,
    use_doc_unwarping=False,
    enable_mkldnn=True,
)
for img in images:
    name = img.split("/")[-1].replace(".jpg", "")
    result = pipeline.predict(input=img)
    for res in result:
        res.save_to_img(f"TestDatas/ppstructure_v3/{name}")
        res.save_to_json(f"TestDatas/ppstructure_v3/{name}")
        res.save_to_markdown(f"TestDatas/ppstructure_v3/{name}")
    print(f"  {name} -> TestDatas/ppstructure_v3/{name}_*")

print("\nDone.")
