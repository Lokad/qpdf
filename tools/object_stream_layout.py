"""Validate physical packing in ordinary/linearized writer outputs with native qpdf."""
import argparse
import base64
import json
from pathlib import Path
import re
import subprocess


def inspect_layout(objects, xref_text, data, decoded_stream):
    entries = {}
    for line in xref_text.splitlines():
        if not line.strip():
            continue
        match = re.fullmatch(rb"(\d+)/(\d+): (?:uncompressed; offset = (\d+)|compressed; stream = (\d+), index = (\d+))", line)
        if match is None:
            raise ValueError("Unrecognized native xref entry")
        number, generation = int(match[1]), int(match[2])
        if number in entries:
            raise ValueError("Duplicate native xref identity")
        entries[number] = (1, int(match[3]), generation) if match[3] else (2, int(match[4]), int(match[5]))
        if match[4] and generation != 0:
            raise ValueError("Compressed member has a nonzero generation")
    compressed = {n: e for n, e in entries.items() if e[0] == 2}
    if not compressed:
        raise ValueError("Object streams have no compressed xref members")

    def stream(number, generation):
        value = objects.get(f"obj:{number} {generation} R", {}).get("stream")
        if not isinstance(value, dict) or not isinstance(value.get("dict"), dict):
            raise ValueError("Physical stream is missing its dictionary")
        dictionary = value["dict"]
        if dictionary.get("/Filter") is not None:
            payload = decoded_stream(number, generation)
        else:
            if not isinstance(value.get("data"), str):
                raise ValueError("Stream payload is missing")
            payload = base64.b64decode(value["data"], validate=True)
        return dictionary, payload

    footer = re.search(rb"startxref\s+(\d+)\s+%%EOF\s*\Z", data)
    if footer is None:
        raise ValueError("Missing terminal startxref")
    offset, visited, physical = int(footer[1]), set(), {}
    while True:
        if offset in visited or offset < 0 or offset >= len(data):
            raise ValueError("Invalid physical xref chain")
        visited.add(offset)
        header = re.match(rb"(\d+)\s+(\d+)\s+obj\b", data[offset:offset + 64])
        if header is None:
            raise ValueError("Physical xref offset does not identify a stream object")
        number, generation = int(header[1]), int(header[2])
        if entries.get(number) != (1, offset, generation):
            raise ValueError("Physical xref object disagrees with native offsets")
        dictionary, payload = stream(number, generation)
        widths, size = dictionary.get("/W"), dictionary.get("/Size")
        if (dictionary.get("/Type") != "/XRef" or type(size) is not int or size < 1
                or not isinstance(widths, list) or len(widths) != 3
                or any(type(w) is not int or w < 0 or w > 8 for w in widths) or sum(widths) == 0):
            raise ValueError("Invalid physical xref stream dictionary")
        ranges = dictionary.get("/Index", [0, size])
        if not isinstance(ranges, list) or not ranges or len(ranges) % 2:
            raise ValueError("Invalid physical xref ranges")
        previous, count = 0, 0
        for first, length in zip(ranges[::2], ranges[1::2]):
            if (type(first) is not int or type(length) is not int or first < previous
                    or length < 1 or first + length > size):
                raise ValueError("Invalid physical xref range bounds")
            previous = first + length
            count += length
        if len(payload) != count * sum(widths):
            raise ValueError("Physical xref payload length disagrees with widths/ranges")
        cursor = 0
        for first, length in zip(ranges[::2], ranges[1::2]):
            for member in range(first, first + length):
                fields = []
                for index, width in enumerate(widths):
                    fields.append(int.from_bytes(payload[cursor:cursor + width], "big") if width else (1 if index == 0 else 0))
                    cursor += width
                if fields[0] not in (0, 1, 2):
                    raise ValueError("Unknown physical xref entry kind")
                physical.setdefault(member, tuple(fields))
        previous = dictionary.get("/Prev")
        if previous is None:
            break
        if type(previous) is not int:
            raise ValueError("Invalid physical Prev offset")
        offset = previous
    if any(physical.get(number) != entry for number, entry in entries.items()):
        raise ValueError("Physical xref rows disagree with active native entries")

    owners, filtered_containers = {}, 0
    for _, owner, _ in compressed.values():
        if owner in owners:
            continue
        entry = entries.get(owner)
        if entry is None or entry[0] != 1 or entry[2] != 0:
            raise ValueError("Object-stream container must be an uncompressed generation-zero object")
        dictionary, payload = stream(owner, 0)
        filtered_containers += dictionary.get("/Filter") is not None
        count, first = dictionary.get("/N"), dictionary.get("/First")
        if (dictionary.get("/Type") != "/ObjStm" or type(count) is not int or count < 1
                or type(first) is not int or first < 1 or first >= len(payload)):
            raise ValueError("Invalid object-stream dictionary")
        fields = payload[:first].split()
        if len(fields) != 2 * count or any(not field.isdigit() for field in fields):
            raise ValueError("Invalid object-stream member table")
        numbers, offsets = [int(v) for v in fields[::2]], [int(v) for v in fields[1::2]]
        if (len(set(numbers)) != count or any(number < 1 or number == owner for number in numbers)
                or any(a >= b for a, b in zip(offsets, offsets[1:]))
                or any(first + offset >= len(payload) for offset in offsets)):
            raise ValueError("Invalid object-stream member identities/offsets")
        owners[owner] = numbers
    for member, (_, owner, slot) in compressed.items():
        if slot >= len(owners[owner]) or owners[owner][slot] != member:
            raise ValueError("Compressed xref disagrees with object-stream ownership")
    return dict(containers=len(owners), compressedMembers=len(compressed),
                xrefStreams=len(visited), filteredContainers=filtered_containers)


def validate_object_streams(qpdf, path, run):
    report = json.loads(run([qpdf, "--json=2", "--json-key=qpdf", "--json-stream-data=inline", "--decode-level=none", path]))
    xref = run([qpdf, "--show-xref", path])
    def decoded(number, generation):
        return run([qpdf, f"--show-object={number},{generation}", "--filtered-stream-data", path])
    return inspect_layout(report["qpdf"][1], xref, Path(path).read_bytes(), decoded)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--qpdf-exe", type=Path, required=True)
    parser.add_argument("--input", type=Path, required=True)
    args = parser.parse_args()

    def run(arguments):
        result = subprocess.run([str(a) for a in arguments], capture_output=True, timeout=180)
        if result.returncode:
            raise RuntimeError(f"Native layout inspection failed ({result.returncode}): "
                               + result.stderr.decode("utf8", "replace"))
        return result.stdout

    try:
        if run([args.qpdf_exe, "--version"]).decode("ascii").splitlines()[0] != "qpdf version 12.4.2":
            raise ValueError("Require pinned native qpdf 12.4.2")
        print(json.dumps(validate_object_streams(args.qpdf_exe, args.input, run)))
    except (ValueError, RuntimeError, OSError, subprocess.SubprocessError) as error:
        parser.exit(2, str(error) + "\n")


if __name__ == "__main__":
    main()
