#!/usr/bin/env python3
import argparse
import json
import sys
import time
from typing import Any


def normalize_code(text: str) -> str:
    return "".join(ch for ch in text.strip() if ch.isalnum())


def normalize_type(code_type: str) -> str:
    ctype = (code_type or "").strip().upper().replace("-", "_")
    if ctype in {"EAN13", "EAN_13", "ISBN13", "JAN"}:
        return "EAN_13"
    if ctype in {"EAN8", "EAN_8"}:
        return "EAN_8"
    if ctype in {"UPCA", "UPC_A"}:
        return "UPC_A"
    if ctype in {"UPCE", "UPC_E"}:
        return "UPC_E"
    return ctype or "UNKNOWN"


def ean_checksum_ok(code: str) -> bool:
    if not code.isdigit():
        return False

    if len(code) == 13:
        digits = [int(ch) for ch in code]
        expected = digits[-1]
        body = digits[:-1]
        total = 0
        for idx, digit in enumerate(body):
            if idx % 2 == 0:
                total += digit
            else:
                total += digit * 3
        check = (10 - (total % 10)) % 10
        return check == expected

    if len(code) == 8:
        digits = [int(ch) for ch in code]
        expected = digits[-1]
        body = digits[:-1]
        total = 0
        for idx, digit in enumerate(body):
            if idx % 2 == 0:
                total += digit * 3
            else:
                total += digit
        check = (10 - (total % 10)) % 10
        return check == expected

    return False


def upc_a_checksum_ok(code: str) -> bool:
    if not code.isdigit() or len(code) != 12:
        return False

    digits = [int(ch) for ch in code]
    expected = digits[-1]
    body = digits[:-1]
    odd_sum = sum(body[0::2]) * 3
    even_sum = sum(body[1::2])
    check = (10 - ((odd_sum + even_sum) % 10)) % 10
    return check == expected


def upc_e_to_upc_a(code: str) -> str:
    if not code.isdigit() or len(code) != 8:
        return ""

    ns = code[0]
    d1 = code[1]
    d2 = code[2]
    d3 = code[3]
    d4 = code[4]
    d5 = code[5]
    d6 = code[6]
    check = code[7]

    if d6 in ("0", "1", "2"):
        body = f"{ns}{d1}{d2}{d6}0000{d3}{d4}{d5}"
    elif d6 == "3":
        body = f"{ns}{d1}{d2}{d3}00000{d4}{d5}"
    elif d6 == "4":
        body = f"{ns}{d1}{d2}{d3}{d4}00000{d5}"
    else:
        body = f"{ns}{d1}{d2}{d3}{d4}{d5}0000{d6}"

    upca = f"{body}{check}"
    return upca if upc_a_checksum_ok(upca) else ""


def is_valid_for_type(code: str, code_type: str) -> bool:
    ctype = normalize_type(code_type)
    if ctype == "EAN_13":
        if len(code) == 13 and ean_checksum_ok(code):
            return True
        # Some decoders tag UPC-A as EAN13 while returning 12 digits.
        return len(code) == 12 and upc_a_checksum_ok(code)
    if ctype == "EAN_8":
        return len(code) == 8 and ean_checksum_ok(code)
    if ctype == "UPC_A":
        if len(code) == 12 and upc_a_checksum_ok(code):
            return True
        # Some decoders return leading-zero EAN13 while tagging UPC_A.
        return len(code) == 13 and code.startswith("0") and ean_checksum_ok(code)
    if ctype == "UPC_E":
        return len(code) == 8 and bool(upc_e_to_upc_a(code))

    # Unknown type: keep only plausible UPC/EAN lengths.
    if code.isdigit() and len(code) in (8, 12, 13):
        if len(code) == 12:
            return upc_a_checksum_ok(code)
        if len(code) == 13:
            return ean_checksum_ok(code)
        return ean_checksum_ok(code) or bool(upc_e_to_upc_a(code))
    return False


def unique_codes(rows: list[dict[str, str]]) -> list[dict[str, str]]:
    counts: dict[str, dict[str, int]] = {}
    for row in rows:
        raw_text = row.get("text") or ""
        text = normalize_code(raw_text)
        if len(text) < 7:
            continue
        code_type = normalize_type(str(row.get("type", "UNKNOWN") or "UNKNOWN"))
        if not is_valid_for_type(text, code_type):
            continue

        # Canonicalize UPC-E to UPC-A for reliable matching in local/external banks.
        if code_type == "UPC_E":
            expanded = upc_e_to_upc_a(text)
            if not expanded:
                continue
            text = expanded
            code_type = "UPC_A"
        elif code_type == "EAN_13" and len(text) == 12 and upc_a_checksum_ok(text):
            code_type = "UPC_A"
        elif code_type == "UPC_A" and len(text) == 13 and text.startswith("0") and ean_checksum_ok(text):
            text = text[1:]

        key = text.upper()
        if key not in counts:
            counts[key] = {}
        type_counts = counts[key]
        type_counts[code_type] = type_counts.get(code_type, 0) + 1

        # Also keep the UPC/EAN sister representation to maximize downstream hits.
        if len(text) == 12 and upc_a_checksum_ok(text):
            ean13 = f"0{text}"
            if ean_checksum_ok(ean13):
                ean_counts = counts.setdefault(ean13, {})
                ean_counts["EAN_13"] = ean_counts.get("EAN_13", 0) + 1
        elif len(text) == 13 and text.startswith("0") and ean_checksum_ok(text):
            upca = text[1:]
            if upc_a_checksum_ok(upca):
                upc_counts = counts.setdefault(upca, {})
                upc_counts["UPC_A"] = upc_counts.get("UPC_A", 0) + 1

    if not counts:
        return []

    collapsed: list[tuple[str, str, int]] = []
    for text, type_counts in counts.items():
        total_hits = sum(type_counts.values())
        best_type = sorted(
            type_counts.items(),
            key=lambda item: (item[1], item[0] != "UNKNOWN"),
            reverse=True,
        )[0][0]
        collapsed.append((text, best_type, total_hits))

    ranked = sorted(
        collapsed,
        key=lambda item: (
            item[2],  # hit count
            1 if len(item[0]) in (12, 13) else 0,  # prefer UPC/EAN13
            len(item[0]),  # longer codes are often better
        ),
        reverse=True,
    )

    # Keep only strong consensus codes when one clear winner exists.
    top_hits = ranked[0][2]
    consensus_threshold = max(2, int(top_hits * 0.35))
    repeated = [row for row in ranked if row[2] >= consensus_threshold]
    selected = repeated if repeated else ranked[:4]

    # For retail game media, 12/13-digit UPC/EAN are much more reliable than EAN-8.
    long_selected = [row for row in selected if len(row[0]) in (12, 13)]
    if long_selected:
        selected = long_selected
    else:
        selected = [row for row in selected if len(row[0]) != 8 or row[2] >= 3]

    output: list[dict[str, str]] = []
    for text, code_type, hits in selected:
        output.append({"text": text, "type": code_type, "hits": str(hits)})
    return output


def safe_crop(image: Any, x: int, y: int, w: int, h: int) -> Any | None:
    height, width = image.shape[:2]
    x0 = max(0, x)
    y0 = max(0, y)
    x1 = min(width, x + w)
    y1 = min(height, y + h)
    if x1 - x0 < 30 or y1 - y0 < 30:
        return None
    return image[y0:y1, x0:x1]


def rotate_image(cv2: Any, image: Any, angle: float) -> Any:
    if abs(angle) < 0.001:
        return image

    h, w = image.shape[:2]
    center = (w / 2.0, h / 2.0)
    matrix = cv2.getRotationMatrix2D(center, angle, 1.0)
    return cv2.warpAffine(
        image,
        matrix,
        (w, h),
        flags=cv2.INTER_CUBIC,
        borderMode=cv2.BORDER_REPLICATE,
    )


def preprocess_variants(cv2: Any, image: Any, aggressive: bool = False) -> list[Any]:
    variants: list[Any] = [image]
    gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    variants.append(gray)
    variants.append(cv2.equalizeHist(gray))

    _, otsu = cv2.threshold(gray, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    variants.append(otsu)

    if aggressive:
        try:
            clahe = cv2.createCLAHE(clipLimit=2.2, tileGridSize=(8, 8))
            variants.append(clahe.apply(gray))
        except Exception:
            pass

        adaptive = cv2.adaptiveThreshold(
            gray,
            255,
            cv2.ADAPTIVE_THRESH_GAUSSIAN_C,
            cv2.THRESH_BINARY,
            35,
            7,
        )
        variants.append(adaptive)

        # Add sharpened and inverted variants for faint/washed bars.
        sharpened = cv2.addWeighted(gray, 1.6, cv2.GaussianBlur(gray, (0, 0), 1.2), -0.6, 0)
        variants.append(sharpened)
        variants.append(cv2.bitwise_not(otsu))
        variants.append(cv2.bitwise_not(adaptive))
    return variants


def scale_variants(cv2: Any, image: Any, aggressive: bool = False) -> list[Any]:
    h, w = image.shape[:2]
    variants = [image]
    # 2x usually captures retail UPC/EAN while keeping runtime reasonable.
    if max(h, w) < 1800:
        variants.append(cv2.resize(image, None, fx=2.0, fy=2.0, interpolation=cv2.INTER_CUBIC))
    # Small barcode crops often need a second upscale pass.
    if aggressive and (max(h, w) < 1100 or min(h, w) < 220):
        variants.append(cv2.resize(image, None, fx=3.0, fy=3.0, interpolation=cv2.INTER_CUBIC))
    return variants


def propose_barcode_regions(cv2: Any, image: Any) -> list[Any]:
    h, w = image.shape[:2]
    regions: list[Any] = []

    # Heuristic crops where UPC/EAN typically live on game box backs.
    presets = [
        (0, int(h * 0.54), int(w * 0.52), int(h * 0.46)),  # bottom-left
        (0, int(h * 0.62), int(w * 0.58), int(h * 0.38)),  # lower-left tighter
        (int(w * 0.18), int(h * 0.70), int(w * 0.64), int(h * 0.30)),  # center-bottom strip
        (int(w * 0.52), int(h * 0.54), int(w * 0.48), int(h * 0.46)),  # bottom-right
        (int(w * 0.42), int(h * 0.62), int(w * 0.58), int(h * 0.38)),  # lower-right tighter
        (0, int(h * 0.55), w, int(h * 0.45)),  # bottom half
        (int(w * 0.35), int(h * 0.45), int(w * 0.65), int(h * 0.55)),  # right-side block
    ]
    for x, y, cw, ch in presets:
        crop = safe_crop(image, x, y, cw, ch)
        if crop is not None:
            regions.append(crop)

    gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    grad_x = cv2.Sobel(gray, cv2.CV_32F, 1, 0, ksize=-1)
    grad_x = cv2.convertScaleAbs(grad_x)
    blurred = cv2.GaussianBlur(grad_x, (9, 9), 0)
    _, thresh = cv2.threshold(blurred, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)

    kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (25, 7))
    closed = cv2.morphologyEx(thresh, cv2.MORPH_CLOSE, kernel)
    closed = cv2.erode(closed, None, iterations=2)
    closed = cv2.dilate(closed, None, iterations=2)

    contours, _ = cv2.findContours(closed, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    contours = sorted(contours, key=cv2.contourArea, reverse=True)[:12]
    min_area = max(1200, int(0.0015 * w * h))

    for contour in contours:
        area = cv2.contourArea(contour)
        if area < min_area:
            continue

        x, y, cw, ch = cv2.boundingRect(contour)
        if ch <= 0:
            continue

        aspect = cw / float(ch)
        if aspect < 1.2:
            continue

        pad_x = int(max(12, cw * 0.08))
        pad_y = int(max(12, ch * 0.2))
        crop = safe_crop(image, x - pad_x, y - pad_y, cw + (2 * pad_x), ch + (2 * pad_y))
        if crop is not None:
            regions.append(crop)

    # Blackhat pipeline catches dark barcode bars on brighter labels.
    rect_kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (31, 9))
    blackhat = cv2.morphologyEx(gray, cv2.MORPH_BLACKHAT, rect_kernel)
    bh_grad_x = cv2.Sobel(blackhat, cv2.CV_32F, 1, 0, ksize=-1)
    bh_grad_x = cv2.convertScaleAbs(bh_grad_x)
    bh_blurred = cv2.GaussianBlur(bh_grad_x, (7, 7), 0)
    _, bh_thresh = cv2.threshold(bh_blurred, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    bh_closed = cv2.morphologyEx(bh_thresh, cv2.MORPH_CLOSE, rect_kernel)
    bh_closed = cv2.erode(bh_closed, None, iterations=1)
    bh_closed = cv2.dilate(bh_closed, None, iterations=2)

    bh_contours, _ = cv2.findContours(bh_closed, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    bh_contours = sorted(bh_contours, key=cv2.contourArea, reverse=True)[:16]
    bh_min_area = max(1000, int(0.0012 * w * h))

    for contour in bh_contours:
        area = cv2.contourArea(contour)
        if area < bh_min_area:
            continue

        x, y, cw, ch = cv2.boundingRect(contour)
        if ch <= 0:
            continue

        aspect = cw / float(ch)
        if aspect < 1.1:
            continue

        pad_x = int(max(10, cw * 0.10))
        pad_y = int(max(8, ch * 0.22))
        crop = safe_crop(image, x - pad_x, y - pad_y, cw + (2 * pad_x), ch + (2 * pad_y))
        if crop is not None:
            regions.append(crop)

    return regions


def build_decode_variants(cv2: Any, image: Any, aggressive: bool = False) -> list[Any]:
    variants: list[Any] = []
    regions = [image]
    regions.extend(propose_barcode_regions(cv2, image))

    # Avoid excessive runtime on very cluttered images.
    regions = regions[: (6 if aggressive else 5)]
    for region in regions:
        for scaled in scale_variants(cv2, region, aggressive=aggressive):
            angles = [0.0]
            if aggressive:
                angles.extend([-4.0, 4.0])

            for angle in angles:
                oriented = scaled if abs(angle) < 0.001 else rotate_image(cv2, scaled, angle)
                variants.extend(preprocess_variants(cv2, oriented, aggressive=aggressive))

    return variants


def scan_with_zbar(image_path: str) -> tuple[list[dict[str, str]], str]:
    try:
        import cv2  # type: ignore
    except Exception as exc:
        return ([], f"opencv import failed: {exc}")

    try:
        from pyzbar.pyzbar import ZBarSymbol, decode as zbar_decode  # type: ignore
    except Exception as exc:
        return ([], f"pyzbar import failed: {exc} (python: {sys.executable})")

    image = cv2.imread(image_path)
    if image is None:
        return ([], "unable to read image")

    def decode_variants(variants: list[Any], deadline: float) -> list[dict[str, str]]:
        rows: list[dict[str, str]] = []
        for variant in variants:
            if time.monotonic() >= deadline:
                break
            try:
                decoded = zbar_decode(
                    variant,
                    symbols=[
                        ZBarSymbol.EAN13,
                        ZBarSymbol.EAN8,
                        ZBarSymbol.UPCA,
                        ZBarSymbol.UPCE,
                    ],
                )
            except Exception:
                continue

            for row in decoded:
                raw = row.data.decode("utf-8", errors="ignore").strip() if row.data else ""
                if not raw:
                    continue
                code_type = str(getattr(row, "type", "UNKNOWN"))
                rows.append({"text": raw, "type": code_type})
        return rows

    start = time.monotonic()
    fast_deadline = start + 2.0
    hard_deadline = start + 7.0

    codes = decode_variants(build_decode_variants(cv2, image, aggressive=False), fast_deadline)
    unique = unique_codes(codes)
    if unique:
        return (unique, "")

    if time.monotonic() >= hard_deadline:
        return ([], "")

    codes = decode_variants(build_decode_variants(cv2, image, aggressive=True), hard_deadline)
    return (unique_codes(codes), "")


def decode_cv2_barcode(cv2: Any, detector: Any, variant: Any) -> list[dict[str, str]]:
    rows: list[dict[str, str]] = []

    try:
        ok, decoded_info, decoded_types, _ = detector.detectAndDecodeWithType(variant)
        if ok:
            for idx, value in enumerate(decoded_info or []):
                text = str(value or "").strip()
                if not text:
                    continue
                code_type = "UNKNOWN"
                if decoded_types and idx < len(decoded_types):
                    code_type = str(decoded_types[idx] or "UNKNOWN")
                rows.append({"text": text, "type": code_type})
    except Exception:
        pass

    # Some OpenCV builds decode better through detectAndDecode fallback.
    try:
        decoded, _, _ = detector.detectAndDecode(variant)
        text = str(decoded or "").strip()
        if text:
            rows.append({"text": text, "type": "UNKNOWN"})
    except Exception:
        pass

    return rows


def scan_with_cv2_detector(image_path: str) -> tuple[list[dict[str, str]], str]:
    try:
        import cv2  # type: ignore
    except Exception as exc:
        return ([], f"opencv import failed: {exc}")

    image = cv2.imread(image_path)
    if image is None:
        return ([], "unable to read image")

    try:
        detector = cv2.barcode_BarcodeDetector()
    except Exception as exc:
        return ([], f"opencv barcode detector unavailable: {exc}")

    def decode_variants(variants: list[Any], deadline: float) -> list[dict[str, str]]:
        rows: list[dict[str, str]] = []
        for variant in variants:
            if time.monotonic() >= deadline:
                break
            rows.extend(decode_cv2_barcode(cv2, detector, variant))
        return rows

    start = time.monotonic()
    fast_deadline = start + 2.5
    hard_deadline = start + 8.0

    codes = decode_variants(build_decode_variants(cv2, image, aggressive=False), fast_deadline)
    unique = unique_codes(codes)
    if unique:
        return (unique, "")

    if time.monotonic() >= hard_deadline:
        return ([], "")

    codes = decode_variants(build_decode_variants(cv2, image, aggressive=True), hard_deadline)
    return (unique_codes(codes), "")


def main() -> int:
    parser = argparse.ArgumentParser(description="Scan barcode/UPC/EAN codes from images.")
    parser.add_argument("--image", action="append", required=True, help="Image path. Pass multiple times.")
    args = parser.parse_args()

    results: list[dict[str, Any]] = []
    warnings: list[str] = []

    for image_path in args.image:
        codes, warning = scan_with_zbar(image_path)
        if not codes:
            cv_codes, cv_warning = scan_with_cv2_detector(image_path)
            if cv_codes:
                codes = cv_codes
                warning = ""
            elif cv_warning and not warning:
                warning = cv_warning
        if warning:
            warnings.append(f"{image_path}: {warning}")
        results.append({"image": image_path, "codes": codes})

    payload: dict[str, Any] = {"ok": True, "results": results}
    if warnings:
        payload["warning"] = " | ".join(warnings[:5])

    print(json.dumps(payload, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
