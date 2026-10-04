"""Author deterministic PDF/JPEG test fixtures with zero DC and AC coefficients.

This builds test data, not an image codec: each Huffman alphabet has one symbol,
and every sample is 128. DC and AC scans are separate in a progressive SOF2
frame. The grayscale/RGB PDFs exercise edge blocks and larger coefficient owners.
"""

import hashlib
import json
import struct
from pathlib import Path


def segment(marker: int, payload: bytes) -> bytes:
    return bytes((0xFF, marker)) + struct.pack(">H", len(payload) + 2) + payload


def zero_scan(blocks: int) -> bytes:
    complete, remaining = divmod(blocks, 8)
    return bytes(complete) + (bytes(((1 << (8 - remaining)) - 1,)) if remaining else b"")


def progressive_frame(width: int, height: int, components: int) -> bytes:
    blocks = ((width + 7) // 8) * ((height + 7) // 8)
    parts = [b"\xff\xd8", segment(0xE0, b"JFIF\0\x01\x02\0\0\x01\0\x01\0\0")]
    parts.append(segment(0xDB, b"\0" + bytes((1,)) * 64))
    frame = bytes((8,)) + struct.pack(">HHB", height, width, components)
    frame += b"".join(bytes((component, 0x11, 0)) for component in range(1, components + 1))
    parts.append(segment(0xC2, frame))
    counts = b"\x01" + bytes(15)
    parts.append(segment(0xC4, b"\0" + counts + b"\0" + b"\x10" + counts + b"\0"))
    dc_scan = bytes((components,))
    dc_scan += b"".join(bytes((component, 0)) for component in range(1, components + 1))
    parts.extend((segment(0xDA, dc_scan + bytes(3)), zero_scan(blocks * components)))
    for component in range(1, components + 1):
        parts.extend((segment(0xDA, bytes((1, component, 0, 1, 63, 0))), zero_scan(blocks)))
    parts.append(b"\xff\xd9")
    return b"".join(parts)


def stream(dictionary: bytes, payload: bytes) -> bytes:
    return b"<< " + dictionary + f" /Length {len(payload)} >>\nstream\n".encode() + payload + b"\nendstream"


def image_page(width: int, height: int, components: int) -> bytes:
    jpeg = progressive_frame(width, height, components)
    content = f"q {width} 0 0 {height} 0 0 cm /Im0 Do Q\n".encode()
    color = "DeviceGray" if components == 1 else "DeviceRGB"
    objects = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
        f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>".encode(),
        stream(b"", content),
        stream(f"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /{color} /BitsPerComponent 8 /Filter /DCTDecode".encode(), jpeg),
    ]
    result = bytearray(b"%PDF-1.4\n")
    offsets = []
    for number, body in enumerate(objects, 1):
        offsets.append(len(result))
        result.extend(f"{number} 0 obj\n".encode() + body + b"\nendobj\n")
    xref = len(result)
    result.extend(b"xref\n0 6\n0000000000 65535 f \n")
    for offset in offsets:
        result.extend(f"{offset:010d} 00000 n \n".encode())
    result.extend(f"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode())
    return bytes(result)


def main() -> None:
    directory = Path(__file__).resolve().parents[1] / "tests" / "Fixtures"
    manifest_path = directory / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    cases = (
        ("images-progressive-gray-1page.pdf", 13, 9, 1),
        ("images-progressive-rgb-1page.pdf", 17, 11, 3),
        ("images-progressive-gray-large-1page.pdf", 1024, 1024, 1),
        ("images-progressive-rgb-large-1page.pdf", 512, 512, 3),
    )
    for name, width, height, components in cases:
        data = image_page(width, height, components)
        samples = bytes((128,)) * (width * height * components)
        (directory / name).write_bytes(data)
        entry = {
            "file": name,
            "sha256": hashlib.sha256(data).hexdigest(),
            "bytes": len(data),
            "expectedNpages": 1,
            "headerVersion": "1.4",
            "valid": True,
            "description": f"Authored {width}x{height}, {components}-component progressive JPEG image with separate DC/AC scans; exact decoded samples are uniformly 128.",
            "imageWidth": width,
            "imageHeight": height,
            "imageComponents": components,
            "decodedSamplesBytes": len(samples),
            "decodedSamplesSha256": hashlib.sha256(samples).hexdigest(),
        }
        existing = next((index for index, item in enumerate(manifest["fixtures"]) if item["file"] == name), None)
        if existing is None:
            manifest["fixtures"].append(entry)
        else:
            manifest["fixtures"][existing] = entry
        print(f"{name}: {len(data)} bytes, sha256={entry['sha256']}")
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8", newline="\n")


if __name__ == "__main__":
    main()
