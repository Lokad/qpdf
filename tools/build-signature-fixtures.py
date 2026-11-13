"""Build deterministic field/appearance fixtures; no valid CMS signature is claimed."""
import hashlib
import json
from pathlib import Path


def stream(attributes, data):
    return f"<< {attributes} /Length {len(data)} >>\nstream\n{data}\nendstream"


def record_fixture(manifest, entry):
    for index, row in enumerate(manifest["fixtures"]):
        if row["file"] == entry["file"]:
            manifest["fixtures"][index] = entry
            return
    manifest["fixtures"].append(entry)


def build(incremental, private, shared):
    objects = {
        1: "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [8 0 R 9 0 R 17 0 R] /CO [9 0 R 8 0 R] /SigFlags 3 >> /Perms << /DocMDP 16 0 R >> >>",
        2: "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>",
        3: "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 15 0 R >> >> /Contents 12 0 R /Annots [6 0 R]" + (" /Audit 6 0 R" if private else "") + " >>",
        4: "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 15 0 R >> >> /Contents 13 0 R /Annots [9 0 R] >>",
        5: "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 15 0 R >> >> /Contents 14 0 R /Annots [7 0 R] >>",
        6: "<< /Type /Annot /Subtype /Widget /Parent 8 0 R /P 3 0 R /Rect [20 30 100 70] /F 4 /AP << /N 10 0 R >> >>",
        7: "<< /Type /Annot /Subtype /Widget /Parent 8 0 R /P 5 0 R /Rect [40 60 120 100] /F 4 /AP << /N 10 0 R >> >>",
        8: "<< /FT /Sig /T (Signature) /Kids [6 0 R 7 0 R] /V 16 0 R /SV << /Type /SV >> /Lock << /Action /All >> >>",
        9: "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Ordinary) /V (Keep me) /P 4 0 R /Rect [20 20 100 40] /AP << /N 18 0 R >> >>",
        10: stream("/Type /XObject /Subtype /Form /BBox [0 0 80 40] /Matrix [1 0 0 1 0 0] /Resources << /XObject << /Mark 11 0 R >> >>", "q /Mark Do Q"),
        11: stream("/Type /XObject /Subtype /Form /BBox [0 0 80 40] /Resources << >>", "0 0.6 0 RG 5 w 10 20 m 25 5 l 70 35 l S"),
        12: stream("", "BT /F1 18 Tf 20 170 Td (First) Tj ET"),
        13: stream("", "BT /F1 18 Tf 20 170 Td (Middle) Tj ET"),
        14: stream("", "BT /F1 18 Tf 20 170 Td (Last) Tj ET"),
        15: "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        16: "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /Contents <00> /ByteRange [0 0 0 0] >>",
        17: "<< /FT /Sig /T (Invisible) /V 16 0 R >>",
        18: stream("/Type /XObject /Subtype /Form /BBox [0 0 80 20] /Resources << >>", "0.5 g 0 0 80 20 re f"),
    }
    if shared:
        objects[1] = objects[1].replace("/Fields [8 0 R 9 0 R 17 0 R]", "/Fields [19 0 R]")
        for number in (8, 9, 17):
            objects[number] = objects[number].replace("<< ", "<< /Parent 19 0 R ", 1)
        objects[19] = "<< /T (Shared) /Kids [8 0 R 9 0 R 17 0 R] >>"
    return assemble(objects, incremental)


def assemble(objects, incremental):
    size = max(objects) + 1
    data = bytearray(b"%PDF-1.7\n")
    offsets = {}
    for number, body in objects.items():
        offsets[number] = len(data)
        if incremental and number == 8:
            body = body.replace(" /V 16 0 R", "")
        data.extend(f"{number} 0 obj\n{body}\nendobj\n".encode("ascii"))
    previous = len(data)
    data.extend(f"xref\n0 {size}\n0000000000 65535 f \n".encode("ascii"))
    for number in range(1, size):
        data.extend(f"{offsets[number]:010d} 00000 n \n".encode("ascii"))
    data.extend(f"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{previous}\n%%EOF\n".encode("ascii"))
    if incremental:
        offset = len(data)
        data.extend(f"8 0 obj\n{objects[8]}\nendobj\n".encode("ascii"))
        xref = len(data)
        data.extend(f"xref\n8 1\n{offset:010d} 00000 n \ntrailer\n<< /Size {size} /Root 1 0 R /Prev {previous} >>\nstartxref\n{xref}\n%%EOF\n".encode("ascii"))
    return bytes(data)


def build_shared_widgets(count):
    pages = [6 + 2 * index for index in range(count)]
    widgets = [number + 1 for number in pages]
    objects = {
        1: "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [3 0 R] /DA (/F1 12 Tf 0 g) /DR << /Font << /F1 4 0 R >> >> >> >>",
        2: f"<< /Type /Pages /Count {count} /Kids [{' '.join(f'{number} 0 R' for number in pages)}] >>",
        3: f"<< /FT /Tx /T (Shared) /V (Keep me) /Kids [{' '.join(f'{number} 0 R' for number in widgets)}] >>",
        4: "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        5: stream("/Type /XObject /Subtype /Form /BBox [0 0 100 20] /Resources << /Font << /F1 4 0 R >> >>", "BT /F1 12 Tf 2 4 Td (Keep me) Tj ET"),
    }
    for page, widget in zip(pages, widgets):
        objects[page] = f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Annots [{widget} 0 R] >>"
        objects[widget] = f"<< /Type /Annot /Subtype /Widget /Parent 3 0 R /P {page} 0 R /Rect [20 20 120 40] /F 4 /AP << /N 5 0 R >> >>"
    return assemble(objects, False)


def main():
    root = Path(__file__).resolve().parent.parent / "tests" / "Fixtures"
    manifest = json.loads((root / "manifest.json").read_text(encoding="utf-8"))
    for name, incremental, private, shared in [("signature-visible", False, False, False),
                                       ("signature-incremental", True, False, False),
                                       ("signature-private-alias", True, True, False),
                                       ("signature-shared-branch", True, False, True)]:
        filename = name + ".pdf"
        data = build(incremental, private, shared)
        (root / filename).write_bytes(data)
        record_fixture(manifest, {"file": filename, "sha256": hashlib.sha256(data).hexdigest(),
            "bytes": len(data), "expectedNpages": 3, "headerVersion": "1.7", "valid": True,
            "description": "Synthetic signature fields, two shared visible check-mark appearances, an invisible signature and an unrelated text field. Dummy CMS bytes; no cryptographic validity. Generated by tools/build-signature-fixtures.py."
            + (" Incremental field update." if incremental else "")
            + (" A private alias retains the original field role independently." if private else "")
            + (" Signature and text fields share a nonterminal parent." if shared else "")})
    filename = "ordinary-shared-widgets.pdf"
    data = build_shared_widgets(128)
    (root / filename).write_bytes(data)
    record_fixture(manifest, {"file": filename, "sha256": hashlib.sha256(data).hexdigest(),
        "bytes": len(data), "expectedNpages": 128, "headerVersion": "1.7", "valid": True,
        "description": "128 pages with distinct annotation arrays and widgets sharing one ordinary text field and a normal text appearance. Generated by tools/build-signature-fixtures.py; exercises shared field-component discovery."})
    (root / "manifest.json").write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")


if __name__ == "__main__":
    main()
