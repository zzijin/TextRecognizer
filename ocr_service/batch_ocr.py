import os
import time
os.environ["PADDLE_PDX_CACHE_HOME"] = os.path.join(os.path.dirname(os.path.abspath(__file__)), "models")

from paddleocr import PaddleOCR

images = ["TestDatas/0001.jpg", "TestDatas/0002.jpg", "TestDatas/0003.jpg", "TestDatas/0004.jpg"]

MODELS = [
    ("server_rec", "PP-OCRv5_server_rec"),
    ("mobile_rec", "PP-OCRv5_mobile_rec"),
    ("en_mobile_rec", "en_PP-OCRv5_mobile_rec"),
]

for model_key, model_name in MODELS:
    print("=" * 60)
    print(f"{model_name} (GPU)")
    t0 = time.time()

    ocr = PaddleOCR(
        device="gpu",
        text_detection_model_name="PP-OCRv5_server_det",
        text_recognition_model_name=model_name,
        use_doc_orientation_classify=False,
        use_doc_unwarping=False,
        use_textline_orientation=False,
    )
    print(f"  Model loaded: {time.time() - t0:.1f}s")

    out_dir = f"TestDatas/{model_key}"
    os.makedirs(out_dir, exist_ok=True)

    for img_path in images:
        t1 = time.time()
        name = os.path.splitext(os.path.basename(img_path))[0]

        result = ocr.predict(img_path)
        for res in result:
            # Annotated image
            res.save_to_img(f"{out_dir}/{name}")

            # TXT results
            txt_path = f"{out_dir}/{name}.txt"
            with open(txt_path, "w", encoding="utf-8") as f:
                for i in range(len(res["rec_texts"])):
                    text = res["rec_texts"][i]
                    score = float(res["rec_scores"][i])
                    f.write(f"{text}\t{score:.4f}\n")

        n = len(result[0]["rec_texts"])
        print(f"  {name}: {n} regions ({time.time() - t1:.1f}s) -> {out_dir}/")

    print(f"  Total: {time.time() - t0:.1f}s")

print("\nDone.")
