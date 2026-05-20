from paddleocr import PPStructureV3

pipeline = PPStructureV3(
    lang="en",
    use_doc_orientation_classify=False,
    use_doc_unwarping=False,
    enable_mkldnn=False,
)

output = pipeline.predict(input="TestDatas/0001.jpg")
for res in output:
    res.print()
    res.save_to_img("TestDatas/ppstructure_v3")
    res.save_to_json("TestDatas/ppstructure_v3")
    res.save_to_markdown("TestDatas/ppstructure_v3")
print("Saved -> TestDatas/ppstructure_v3/")
