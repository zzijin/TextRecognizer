from paddleocr import PaddleOCR

ocr = PaddleOCR(
    text_detection_model_name="PP-OCRv5_server_det",
    text_recognition_model_name="en_PP-OCRv5_mobile_rec",
    use_doc_orientation_classify=False,
    use_doc_unwarping=False,
    use_textline_orientation=False,
    enable_mkldnn=False,
)

result = ocr.predict("TestDatas/0001.jpg")
for res in result:
    res.save_to_img("TestDatas/en_mobile_rec")
print("Saved -> TestDatas/en_mobile_rec_ocr_res_img.jpg")
