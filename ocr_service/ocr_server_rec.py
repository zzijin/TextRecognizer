import os
os.environ["FLAGS_use_onednn"] = "0"
os.environ["PADDLE_PDX_CACHE_HOME"] = os.path.join(os.path.dirname(os.path.abspath(__file__)), "models")

from paddleocr import PaddleOCR

ocr = PaddleOCR(
    use_doc_orientation_classify=False,
    use_doc_unwarping=False,
    use_textline_orientation=False,
    enable_mkldnn=True,
)

result = ocr.predict("TestDatas/0001.jpg")
for res in result:
    res.save_to_img("TestDatas/server_rec")
print("Saved -> TestDatas/server_rec_ocr_res_img.jpg")
