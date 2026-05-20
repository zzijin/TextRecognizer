from paddleocr import PaddleOCR

ocr = PaddleOCR(
    use_doc_orientation_classify=False,
    use_doc_unwarping=False,
    use_textline_orientation=False,
    enable_mkldnn=False,
)

result = ocr.predict("TestDatas/0001.jpg")
for res in result:
    res.save_to_img("TestDatas/server_rec")
print("Saved -> TestDatas/server_rec_ocr_res_img.jpg")
