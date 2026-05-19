from paddleocr import PaddleOCR

image_path = "TestDatas/0001.jpg"

# --- Model A: PP-OCRv5_server_rec (default Chinese server) ---
print("=" * 50)
print("Model: PP-OCRv5_server_rec")
ocr_server = PaddleOCR(
    use_doc_orientation_classify=False,
    use_doc_unwarping=False,
    use_textline_orientation=False,
    enable_mkldnn=False,
)
result_server = ocr_server.predict(image_path)
for res in result_server:
    res.save_to_img("TestDatas/server_rec")
print("Saved -> TestDatas/server_rec_ocr_res_img.jpg\n")

# --- Model B: en_PP-OCRv5_mobile_rec (English mobile, better handwritten) ---
print("=" * 50)
print("Model: en_PP-OCRv5_mobile_rec")
ocr_en = PaddleOCR(
    lang="en",
    text_detection_model_name="PP-OCRv5_server_det",
    text_recognition_model_name="en_PP-OCRv5_mobile_rec",
    use_doc_orientation_classify=False,
    use_doc_unwarping=False,
    use_textline_orientation=False,
    enable_mkldnn=False,
)
result_en = ocr_en.predict(image_path)
for res in result_en:
    res.save_to_img("TestDatas/en_mobile_rec")
print("Saved -> TestDatas/en_mobile_rec_ocr_res_img.jpg")
