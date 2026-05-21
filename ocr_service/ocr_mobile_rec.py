import os
os.environ["PADDLE_PDX_CACHE_HOME"] = os.path.join(os.path.dirname(os.path.abspath(__file__)), "models")

import time
from paddleocr import PaddleOCR

t0 = time.time()
ocr = PaddleOCR(
    device="gpu",
    text_detection_model_name="PP-OCRv5_server_det",
    text_recognition_model_name="PP-OCRv5_mobile_rec",
    use_doc_orientation_classify=False,
    use_doc_unwarping=False,
    use_textline_orientation=False,
)
print(f"Model loaded: {time.time() - t0:.1f}s")

result = ocr.predict("TestDatas/0001.jpg")
for res in result:
    res.save_to_img("TestDatas/mobile_rec")
print("Saved -> TestDatas/mobile_rec_ocr_res_img.jpg")
