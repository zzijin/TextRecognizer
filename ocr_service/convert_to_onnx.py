#!/usr/bin/env python3
"""
Convert PaddlePaddle PIR inference models (PaddleX format) to ONNX.

Uses paddle2onnx CLI from a separate venv with a compatible paddlepaddle version
(3.1.0 CPU). The project's main venv uses paddlepaddle-gpu 3.3.0 which has an
ABI-incompatible libpaddle.pyd (PIR API renamed OpBase → Operation).

Usage:
    python convert_to_onnx.py                    # Convert all 4 models
    python convert_to_onnx.py --model en_mobile  # Convert specific model
"""

import argparse
import os
import subprocess
import sys


PROJECT_ROOT = os.path.dirname(os.path.abspath(__file__))
MODELS_DIR = os.path.join(PROJECT_ROOT, "models")
OFFICIAL_MODELS = os.path.join(MODELS_DIR, "official_models")
ONNX_OUTPUT = os.path.join(MODELS_DIR, "onnx_models")

MODELS = [
    {
        "name": "en_PP-OCRv5_mobile_rec",
        "dir": os.path.join(OFFICIAL_MODELS, "en_PP-OCRv5_mobile_rec"),
    },
    {
        "name": "PP-OCRv5_mobile_rec",
        "dir": os.path.join(OFFICIAL_MODELS, "PP-OCRv5_mobile_rec"),
    },
    {
        "name": "PP-OCRv5_server_rec",
        "dir": os.path.join(OFFICIAL_MODELS, "PP-OCRv5_server_rec"),
    },
    {
        "name": "PP-OCRv5_server_det",
        "dir": os.path.join(OFFICIAL_MODELS, "PP-OCRv5_server_det"),
    },
]

# The paddle2onnx CLI must be run from a venv with a compatible paddle version.
# paddle2onnx 2.1.0 + paddlepaddle 3.1.0 CPU is known to work.
P2O_VENV_PYTHON = os.path.join(PROJECT_ROOT, "p2o_test_venv", "Scripts", "python.exe")
P2O_CLI = os.path.join(PROJECT_ROOT, "p2o_test_venv", "Scripts", "paddle2onnx.exe")


def check_p2o_venv():
    if not os.path.exists(P2O_CLI):
        print("ERROR: paddle2onnx venv not found at:", P2O_VENV_PYTHON)
        print()
        print("Setup instructions:")
        print("  python -m venv ocr_service/p2o_test_venv")
        print("  ocr_service/p2o_test_venv/Scripts/pip.exe install paddlepaddle==3.1.0")
        print("  ocr_service/p2o_test_venv/Scripts/pip.exe install paddle2onnx==2.1.0")
        print("  ocr_service/p2o_test_venv/Scripts/pip.exe install setuptools packaging")
        return False
    return True


def convert_model(model_info, opset=14):
    name = model_info["name"]
    model_dir = model_info["dir"]
    save_path = os.path.join(ONNX_OUTPUT, name + ".onnx")

    os.makedirs(ONNX_OUTPUT, exist_ok=True)

    print("=" * 60)
    print("Converting: {}".format(name))
    print("  Model dir: {}".format(model_dir))
    print("  Output:    {}".format(save_path))

    cmd = [
        P2O_CLI,
        "--model_dir", model_dir,
        "--model_filename", "inference.json",
        "--params_filename", "inference.pdiparams",
        "--save_file", save_path,
        "--opset_version", str(opset),
    ]

    result = subprocess.run(cmd, capture_output=True, text=True, cwd=PROJECT_ROOT)

    if result.returncode == 0:
        size_mb = os.path.getsize(save_path) / (1024 * 1024)
        print("  OK ({:.1f} MB)".format(size_mb))
        return True
    else:
        print("  FAILED")
        print("  stdout:", result.stdout)
        print("  stderr:", result.stderr)
        return False


def verify_models():
    try:
        import onnx
    except ImportError:
        print("onnx not installed in current venv, skipping verification")
        return

    print()
    print("=" * 60)
    print("Verifying ONNX models")
    print()

    for model_info in MODELS:
        path = os.path.join(ONNX_OUTPUT, model_info["name"] + ".onnx")
        if not os.path.exists(path):
            print("  {}: MISSING".format(model_info["name"]))
            continue

        model = onnx.load(path)
        onnx.checker.check_model(model)
        size_mb = os.path.getsize(path) / (1024 * 1024)
        print("  {}: VALID ({:.1f} MB, {} nodes)".format(
            model_info["name"], size_mb, len(model.graph.node)))


def main():
    parser = argparse.ArgumentParser(description="Convert PaddleOCR models to ONNX")
    parser.add_argument(
        "--model", "-m", type=str, default=None,
        help="Convert specific model (en_mobile, mobile_rec, server_rec, server_det)",
    )
    parser.add_argument(
        "--opset", "-o", type=int, default=14,
        help="ONNX opset version (default: 14)",
    )
    parser.add_argument(
        "--verify-only", action="store_true",
        help="Only verify existing ONNX models",
    )
    args = parser.parse_args()

    if args.verify_only:
        verify_models()
        return

    if not check_p2o_venv():
        sys.exit(1)

    model_map = {
        "en_mobile": "en_PP-OCRv5_mobile_rec",
        "mobile_rec": "PP-OCRv5_mobile_rec",
        "server_rec": "PP-OCRv5_server_rec",
        "server_det": "PP-OCRv5_server_det",
    }

    if args.model:
        target = model_map.get(args.model, args.model)
        models_to_convert = [m for m in MODELS if m["name"] == target]
        if not models_to_convert:
            print("Unknown model: {}".format(args.model))
            print("Available: {}".format(", ".join(model_map.keys())))
            sys.exit(1)
    else:
        models_to_convert = MODELS

    success = 0
    for m in models_to_convert:
        if convert_model(m, args.opset):
            success += 1

    print()
    print("Converted {}/{} models".format(success, len(models_to_convert)))

    if success == len(models_to_convert):
        verify_models()


if __name__ == "__main__":
    main()
