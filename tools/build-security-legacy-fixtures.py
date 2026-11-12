"""Record short R3 keys with both owner-hash conventions; require native checks.

All credentials and keys are public test data. This development-only generator
uses hashlib and an owned RC4 implementation, independent of the managed core.
"""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import struct
import subprocess


PADDING = bytes.fromhex("28bf4e5e4e758a4164004e56fffa01082e2e00b6d0683e802f0ca9fe6453697a")


def rc4(key, data):
    state = list(range(256))
    swap = 0
    for index in range(256):
        swap = (swap + state[index] + key[index % len(key)]) & 255
        state[index], state[swap] = state[swap], state[index]
    position = swap = 0
    result = bytearray()
    for value in data:
        position = (position + 1) & 255
        swap = (swap + state[position]) & 255
        state[position], state[swap] = state[swap], state[position]
        result.append(value ^ state[(state[position] + state[swap]) & 255])
    return bytes(result)


def rounds(data, key):
    for iteration in range(20):
        data = rc4(bytes(value ^ iteration for value in key), data)
    return data


def object_key(key, number, generation):
    seed = key + number.to_bytes(3, "little") + generation.to_bytes(2, "little")
    return hashlib.md5(seed).digest()[:min(len(key) + 5, 16)]


def create(length, full_digest):
    user, owner, identifier = b"reader", b"fixture-owner", bytes(range(16))
    padded_user = (user + PADDING)[:32]
    digest = hashlib.md5((owner + PADDING)[:32]).digest()
    for _ in range(50):
        digest = hashlib.md5(digest if full_digest else digest[:length]).digest()
    owner_entry = rounds(padded_user, digest[:length])
    digest = hashlib.md5(padded_user + owner_entry + struct.pack("<i", -4) + identifier).digest()
    for _ in range(50):
        digest = hashlib.md5(digest[:length]).digest()
    key = digest[:length]
    user_entry = rounds(hashlib.md5(PADDING + identifier).digest(), key) + bytes(16)
    dictionary = f"<< /Filter /Standard /V 2 /R 3 /Length {length * 8} /P -4 /O <{owner_entry.hex()}> /U <{user_entry.hex()}> >>"
    plain = b"BT (Legacy) Tj ET"
    content = rc4(object_key(key, 4, 0), plain)
    title = rc4(object_key(key, 5, 0), b"LegacyTitle")
    objects = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> /Contents 4 0 R >>",
        f"<< /Length {len(content)} >>\nstream\n".encode() + content + b"\nendstream",
        f"<< /Title <{title.hex()}> >>".encode(), dictionary.encode(),
    ]
    data = bytearray(b"%PDF-1.4\n")
    offsets = []
    for number, body in enumerate(objects, 1):
        offsets.append(len(data))
        data += f"{number} 0 obj\n".encode() + body + b"\nendobj\n"
    xref = len(data)
    data += b"xref\n0 7\n0000000000 65535 f \n"
    for offset in offsets:
        data += f"{offset:010} 00000 n \n".encode()
    data += f"trailer\n<< /Size 7 /Root 1 0 R /Info 5 0 R /Encrypt 6 0 R /ID [<{identifier.hex()}> <{identifier.hex()}>] >>\nstartxref\n{xref}\n%%EOF\n".encode()
    vector = dict(userHex=user.hex(), ownerHex=owner.hex(), fileKeyHex=key.hex(), idHex=identifier.hex(),
                  dictionary=dictionary, streams=[dict(number=4, plainBase64=base64.b64encode(plain).decode())],
                  objectKeyHex=object_key(key, 66051, 513).hex(), nativeOwnerMatches=not full_digest)
    return bytes(data), vector


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--qpdf-exe", required=True)
    parser.add_argument("--pdfinfo", required=True)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parent.parent
    native = str(Path(args.qpdf_exe).resolve())
    subprocess.run(["pwsh", "-File", str(repo / "tools/bootstrap-qpdf.ps1"), "-QpdfExe", native], check=True, timeout=120)
    fixtures = repo / "tests/Fixtures"
    manifest_path = fixtures / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    vectors = []
    for length, full_digest in [(n, False) for n in range(5, 17)] + [(5, True), (12, True)]:
        data, vector = create(length, full_digest)
        convention = "full-digest" if full_digest else "short-digest"
        name = f"security-r3-key{length * 8}-{convention}.pdf"
        path = fixtures / name
        path.write_bytes(data)
        for role, password in [("user", "reader"), ("owner", "fixture-owner")]:
            expected = 2 if full_digest and role == "owner" else 0
            result = subprocess.run([native, f"--password={password}", "--check", str(path)], capture_output=True, timeout=60)
            if result.returncode != expected:
                raise RuntimeError(f"{name}: unexpected native {role} status: {result.stderr!r}")
            independent = subprocess.run([args.pdfinfo, "-opw" if role == "owner" else "-upw", password, str(path)], capture_output=True, timeout=60)
            if independent.returncode != (1 if expected else 0):
                raise RuntimeError(f"{name}: unexpected independent {role} status: {independent.stderr!r}")
            if expected == 0 and b"Pages:           1" not in independent.stdout:
                raise RuntimeError(f"{name}: missing independent page count")
        content = subprocess.run([native, "--password=reader", "--show-object=4", "--filtered-stream-data", str(path)], capture_output=True, check=True, timeout=60).stdout
        if content != b"BT (Legacy) Tj ET":
            raise RuntimeError(f"{name}: native plaintext mismatch")
        vector["file"] = name
        vectors.append(vector)
        manifest["fixtures"] = [entry for entry in manifest["fixtures"] if entry["file"] != name]
        manifest["fixtures"].append(dict(file=name, sha256=hashlib.sha256(data).hexdigest(), bytes=len(data),
            expectedNpages=1, headerVersion="1.4", valid=True,
            description=f"Authored R3 RC4 {length * 8}-bit fixture using {convention} owner hashing. Public reader/fixture-owner passwords. Generated by tools/build-security-legacy-fixtures.py; both readers authenticate the user and native qpdf verifies exact content. " +
                        ("The ISO 32000-1 full-digest owner convention is rejected by pinned qpdf and Poppler; managed authentication tests cover it independently."
                         if full_digest else "Pinned qpdf and Poppler also authenticate the owner.")))
    (fixtures / "security-r3-key-vectors.json").write_text(json.dumps(vectors, indent=2) + "\n", encoding="utf-8", newline="\n")
    manifest_path.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    print(f"PASS {len(vectors)} legacy key fixtures with native and independent authentication controls")


if __name__ == "__main__":
    main()
