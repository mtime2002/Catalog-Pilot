#!/usr/bin/env python3
import argparse
import contextlib
import json
import os
import re
import sys
from typing import Any


def normalize(text: str) -> str:
    text = text or ""
    text = re.sub(r"\s+", " ", text)
    return text.strip()


def extract_lines(ocr_result: Any) -> list[dict[str, Any]]:
    lines: list[dict[str, Any]] = []
    if not isinstance(ocr_result, list):
        return lines

    for page in ocr_result:
        if not isinstance(page, list):
            continue

        for row in page:
            if not isinstance(row, (list, tuple)) or len(row) < 2:
                continue

            rec = row[1]
            if isinstance(rec, (list, tuple)) and len(rec) >= 2:
                text = normalize(str(rec[0]))
                try:
                    score = float(rec[1])
                except Exception:
                    score = 0.0
            else:
                text = normalize(str(rec))
                score = 0.0

            if text:
                lines.append({"text": text, "score": score})

    return lines


def extract_lines_from_predict(pred_result: Any) -> list[dict[str, Any]]:
    lines: list[dict[str, Any]] = []
    if not isinstance(pred_result, list):
        return lines

    for row in pred_result:
        if not isinstance(row, dict):
            continue

        texts = row.get("rec_texts") or row.get("texts") or []
        scores = row.get("rec_scores") or row.get("scores") or []
        if isinstance(texts, list):
            for idx, text in enumerate(texts):
                norm_text = normalize(str(text))
                if not norm_text:
                    continue

                score = 0.0
                if isinstance(scores, list) and idx < len(scores):
                    try:
                        score = float(scores[idx])
                    except Exception:
                        score = 0.0
                lines.append({"text": norm_text, "score": score})

    return lines


def run_ocr(ocr: Any, image_path: str) -> list[dict[str, Any]]:
    # Newer PaddleOCR versions expose predict(); prefer it first for speed.
    if hasattr(ocr, "predict"):
        try:
            pred = ocr.predict(image_path)
            lines = extract_lines_from_predict(pred)
            if lines:
                return lines
        except Exception:
            pass

    # Legacy API path first (more stable across Paddle versions on Windows)
    try:
        raw = ocr.ocr(image_path, cls=True)
    except TypeError:
        raw = ocr.ocr(image_path)
    except Exception:
        raw = None

    lines = extract_lines(raw)
    if lines:
        return lines

    return []


def main() -> int:
    os.environ.setdefault("DISABLE_MODEL_SOURCE_CHECK", "True")
    os.environ.setdefault("PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK", "True")

    parser = argparse.ArgumentParser(description="Run PaddleOCR on one or more images.")
    parser.add_argument("--image", action="append", required=True, help="Image path. Pass multiple times.")
    parser.add_argument("--out", default="", help="Optional output JSON file path.")
    args = parser.parse_args()

    try:
        from paddleocr import PaddleOCR
    except Exception as exc:
        print(json.dumps({
            "ok": False,
            "error": f"Failed to import paddleocr: {exc}",
            "hint": "Install with: pip install paddleocr paddlepaddle"
        }))
        return 0

    try:
        with contextlib.redirect_stdout(sys.stderr):
            try:
                ocr = PaddleOCR(
                    text_detection_model_name="PP-OCRv5_mobile_det",
                    text_recognition_model_name="en_PP-OCRv5_mobile_rec",
                    lang="en",
                    use_textline_orientation=True,
                    use_doc_orientation_classify=False,
                    use_doc_unwarping=False,
                    text_det_limit_side_len=1280,
                )
            except TypeError:
                try:
                    ocr = PaddleOCR(use_angle_cls=False, lang="en")
                except TypeError:
                    ocr = PaddleOCR(lang="en")
    except Exception as exc:
        print(json.dumps({"ok": False, "error": f"Failed to initialize PaddleOCR: {exc}"}))
        return 0

    results: list[dict[str, Any]] = []
    for image_path in args.image:
        try:
            with contextlib.redirect_stdout(sys.stderr):
                lines = run_ocr(ocr, image_path)
            text = normalize(" ".join(line["text"] for line in lines))
            results.append({
                "image": image_path,
                "text": text,
                "lines": lines,
            })
        except Exception as exc:
            results.append({
                "image": image_path,
                "text": "",
                "lines": [],
                "error": str(exc),
            })

    payload = json.dumps({"ok": True, "results": results}, ensure_ascii=False)
    out_path = (args.out or "").strip()
    if out_path:
        try:
            with open(out_path, "w", encoding="utf-8") as handle:
                handle.write(payload)
        except Exception as exc:
            print(json.dumps({"ok": False, "error": f"Failed to write output file: {exc}"}))
            return 0
    else:
        print(payload)
    return 0


if __name__ == "__main__":
    sys.exit(main())
