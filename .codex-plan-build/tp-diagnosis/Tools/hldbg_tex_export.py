#!/usr/bin/env python3
from __future__ import annotations

import argparse
import importlib
import io
import json
import os
import struct
import subprocess
import sys
from typing import Any


TEX_HEADER_SIZE = 80
TEX_FORMAT_A8R8G8B8 = 5200
TEX_FORMAT_DXT1 = 13344
TEX_FORMAT_DXT3 = 13360
TEX_FORMAT_DXT5 = 13361
DDS_MAGIC = 0x20534444
DDS_HEADER_SIZE = 124
DDS_PIXEL_FORMAT_SIZE = 32
DDS_CAPS_TEXTURE = 0x1000
DDS_CAPS_MIPMAP = 0x400000
DDSD_CAPS = 0x1
DDSD_HEIGHT = 0x2
DDSD_WIDTH = 0x4
DDSD_PIXELFORMAT = 0x1000
DDSD_MIPMAPCOUNT = 0x20000
DDSD_LINEARSIZE = 0x80000
DDPF_FOURCC = 0x4


def ensure_pillow(dep_root: str):
    if dep_root and dep_root not in sys.path:
        sys.path.insert(0, dep_root)
    importlib.invalidate_caches()

    try:
        from PIL import Image  # type: ignore

        return Image
    except ModuleNotFoundError:
        pass

    os.makedirs(dep_root, exist_ok=True)
    subprocess.run(
        [
            sys.executable,
            "-m",
            "pip",
            "install",
            "--disable-pip-version-check",
            "--no-input",
            "--target",
            dep_root,
            "Pillow>=11,<12",
        ],
        check=True,
        capture_output=True,
        text=True,
        timeout=180,
    )

    importlib.invalidate_caches()
    from PIL import Image  # type: ignore

    return Image


def read_tex_header(raw_data: bytes) -> tuple[int, int, int, int]:
    if len(raw_data) < TEX_HEADER_SIZE:
        raise ValueError("TEX file is smaller than 80-byte header")

    format_code = int.from_bytes(raw_data[4:8], "little", signed=False)
    width = int.from_bytes(raw_data[8:10], "little", signed=False)
    height = int.from_bytes(raw_data[10:12], "little", signed=False)
    mip_count = raw_data[14] & 0x0F
    if width <= 0 or height <= 0:
        raise ValueError("TEX file reported invalid dimensions")
    return format_code, width, height, max(1, mip_count)


def build_dds_payload(format_code: int, width: int, height: int, mip_count: int, pixel_data: bytes) -> bytes:
    four_cc_map = {
        TEX_FORMAT_DXT1: b"DXT1",
        TEX_FORMAT_DXT3: b"DXT3",
        TEX_FORMAT_DXT5: b"DXT5",
    }
    four_cc = four_cc_map.get(format_code)
    if four_cc is None:
        raise ValueError(f"Unsupported TEX image format {format_code}")

    linear_size = (width * height) // 2 if format_code == TEX_FORMAT_DXT1 else width * height
    caps = DDS_CAPS_TEXTURE | (DDS_CAPS_MIPMAP if mip_count > 1 else 0)
    header = bytearray()
    header += struct.pack("<I", DDS_MAGIC)
    header += struct.pack("<I", DDS_HEADER_SIZE)
    header += struct.pack("<I", DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PIXELFORMAT | DDSD_MIPMAPCOUNT | DDSD_LINEARSIZE)
    header += struct.pack("<I", height)
    header += struct.pack("<I", width)
    header += struct.pack("<I", linear_size)
    header += struct.pack("<I", 0)
    header += struct.pack("<I", mip_count)
    header += bytes(44)
    header += struct.pack("<I", DDS_PIXEL_FORMAT_SIZE)
    header += struct.pack("<I", DDPF_FOURCC)
    header += four_cc
    header += struct.pack("<I", 0)
    header += struct.pack("<I", 0)
    header += struct.pack("<I", 0)
    header += struct.pack("<I", 0)
    header += struct.pack("<I", 0)
    header += struct.pack("<I", caps)
    header += struct.pack("<I", 0)
    header += struct.pack("<I", 0)
    header += struct.pack("<I", 0)
    header += struct.pack("<I", 0)
    return bytes(header) + pixel_data


def tex_to_image(image_type: Any, raw_tex_path: str):
    with open(raw_tex_path, "rb") as handle:
        raw_data = handle.read()

    format_code, width, height, mip_count = read_tex_header(raw_data)
    pixel_data = raw_data[TEX_HEADER_SIZE:]
    if format_code == TEX_FORMAT_A8R8G8B8:
        required_bytes = width * height * 4
        if len(pixel_data) < required_bytes:
            raise ValueError(f"TEX pixel payload truncated: expected {required_bytes}, got {len(pixel_data)}")
        return image_type.frombytes("RGBA", (width, height), pixel_data[:required_bytes], "raw", "BGRA")

    dds_payload = build_dds_payload(format_code, width, height, mip_count, pixel_data)
    image = image_type.open(io.BytesIO(dds_payload))
    image.load()
    return image


def get_rect(entry: dict[str, Any]) -> tuple[int, int, int, int] | None:
    rect = entry.get("Rect")
    if not isinstance(rect, dict):
        return None

    u = int(rect.get("U", rect.get("u", 0)) or 0)
    v = int(rect.get("V", rect.get("v", 0)) or 0)
    width = int(rect.get("Width", rect.get("width", 0)) or 0)
    height = int(rect.get("Height", rect.get("height", 0)) or 0)
    if width <= 0 or height <= 0:
        return None
    return u, v, width, height


def save_png(image: Any, path: str) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    image.save(path, format="PNG")


def main() -> int:
    parser = argparse.ArgumentParser(description="Convert ADS Higher/Lower raw TEX exports to PNG crops.")
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--dep-root", default="")
    args = parser.parse_args()

    dep_root = args.dep_root or os.path.join(os.path.dirname(os.path.abspath(args.manifest)), "_pydeps")
    image_type = ensure_pillow(dep_root)

    with open(args.manifest, "r", encoding="utf-8-sig") as handle:
        manifest = json.load(handle)

    entries = manifest.get("Entries") or manifest.get("entries") or []
    converted = 0
    failed = 0
    image_cache: dict[str, Any] = {}
    resample_nearest = getattr(getattr(image_type, "Resampling", image_type), "NEAREST")

    for entry in entries:
        if not isinstance(entry, dict):
            continue
        if entry.get("Error"):
            continue

        raw_path = str(entry.get("RawTexPath") or "")
        atlas_path = str(entry.get("AtlasPngPath") or "")
        crop_path = str(entry.get("CropPngPath") or "")
        if not raw_path or not os.path.isfile(raw_path):
            entry["Error"] = "raw TEX path missing"
            failed += 1
            continue

        try:
            image = image_cache.get(raw_path)
            if image is None:
                image = tex_to_image(image_type, raw_path)
                image_cache[raw_path] = image
            if atlas_path and not os.path.isfile(atlas_path):
                save_png(image, atlas_path)

            rect = get_rect(entry)
            if crop_path:
                crop = image
                if rect is not None:
                    u, v, width, height = rect
                    crop = image.crop((u, v, u + width, v + height))
                if crop.width < 160 or crop.height < 160:
                    scale = max(1, min(8, int(max(160 / max(1, crop.width), 160 / max(1, crop.height)))))
                    crop = crop.resize((crop.width * scale, crop.height * scale), resample_nearest)
                save_png(crop, crop_path)
            entry["Converted"] = True
            converted += 1
        except Exception as exc:
            entry["Converted"] = False
            entry["Error"] = str(exc)
            failed += 1

    converted_path = os.path.join(os.path.dirname(os.path.abspath(args.manifest)), "manifest.converted.json")
    with open(converted_path, "w", encoding="utf-8") as handle:
        json.dump(manifest, handle, indent=2)

    print(f"converted={converted} failed={failed} manifest={converted_path}")
    return 0 if converted > 0 and failed == 0 else (0 if converted > 0 else 1)


if __name__ == "__main__":
    raise SystemExit(main())
