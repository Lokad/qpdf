"""Offline ownership/physical-layout controls; no native tools are required."""
import base64
import unittest

import object_stream_layout as layout


def fixture():
    payload = b"2 0 3 5 null true"
    entries = [(0, 0, 65535), (1, 16, 0), (2, 1, 0), (2, 1, 1), (0, 0, 0), (1, 64, 0)]
    rows = b"".join(bytes([kind]) + first.to_bytes(2, "big") + second.to_bytes(2, "big") for kind, first, second in entries)
    objects = {
        "obj:1 0 R": {"stream": {"dict": {"/Type": "/ObjStm", "/N": 2, "/First": 8}, "data": base64.b64encode(payload).decode()}},
        "obj:5 0 R": {"stream": {"dict": {"/Type": "/XRef", "/Size": 6, "/W": [1, 2, 2]}, "data": base64.b64encode(rows).decode()}},
    }
    xrefs = b"1/0: uncompressed; offset = 16\n2/0: compressed; stream = 1, index = 0\n3/0: compressed; stream = 1, index = 1\n5/0: uncompressed; offset = 64\n"
    data = b"%PDF-1.5\n".ljust(16, b" ") + b"1 0 obj\n".ljust(48, b" ") + b"5 0 obj\n" + b"startxref\n64\n%%EOF\n"
    return objects, xrefs, data


def reject_decode(number, generation):
    raise AssertionError("Unfiltered payload should not call the native decoder")


class LayoutTests(unittest.TestCase):
    def test_raw_and_compressed_payloads_share_ownership_checks(self):
        for filtered in [False, True]:
            objects, xrefs, data = fixture()
            calls = []
            def decoded(number, generation):
                calls.append((number, generation))
                return base64.b64decode(objects[f"obj:{number} {generation} R"]["stream"]["data"])
            if filtered:
                for value in objects.values():
                    value["stream"]["dict"]["/Filter"] = "/FlateDecode"
            for ending in [b"\n", b"\r\r\n"]:
                self.assertEqual(layout.inspect_layout(objects, xrefs.replace(b"\n", ending), data, decoded),
                                 dict(containers=1, compressedMembers=2, xrefStreams=1, filteredContainers=int(filtered)))
            self.assertEqual(len(calls), 4 if filtered else 0)

    def test_member_dictionary_and_table_failures(self):
        for field, value in [("/Type", "/XRef"), ("/N", True), ("/N", 3), ("/First", -1), ("/First", 99)]:
            with self.subTest(field=field, value=value):
                objects, xrefs, data = fixture()
                objects["obj:1 0 R"]["stream"]["dict"][field] = value
                with self.assertRaises(ValueError):
                    layout.inspect_layout(objects, xrefs, data, reject_decode)
        for payload in [b"2 0 2 5 null true", b"2 5 3 0 null true", b"1 0 3 5 null true", b"2 0 3 9 null true"]:
            objects, xrefs, data = fixture()
            objects["obj:1 0 R"]["stream"]["data"] = base64.b64encode(payload).decode()
            with self.assertRaises(ValueError):
                layout.inspect_layout(objects, xrefs, data, reject_decode)

    def test_active_xrefs_and_container_roles_must_correspond(self):
        for before, after in [(b"index = 1", b"index = 2"), (b"stream = 1", b"stream = 4"), (b"2/0:", b"2/1:")]:
            objects, xrefs, data = fixture()
            with self.assertRaises(ValueError):
                layout.inspect_layout(objects, xrefs.replace(before, after), data, reject_decode)
        objects, xrefs, data = fixture()
        objects["obj:1 0 R"] = {"value": {"/Type": "/ObjStm"}}
        with self.assertRaises(ValueError):
            layout.inspect_layout(objects, xrefs, data, reject_decode)

    def test_physical_xref_shape_and_rows_are_independent_of_dictionary_tags(self):
        for field, value in [("/Type", "/ObjStm"), ("/W", [1, True, 2]), ("/W", [1, 2]),
                             ("/Index", [0, 6, 5, 1]), ("/Index", [0, 7]), ("/Prev", 64)]:
            with self.subTest(field=field, value=value):
                objects, xrefs, data = fixture()
                objects["obj:5 0 R"]["stream"]["dict"][field] = value
                with self.assertRaises(ValueError):
                    layout.inspect_layout(objects, xrefs, data, reject_decode)
        objects, xrefs, data = fixture()
        raw = bytearray(base64.b64decode(objects["obj:5 0 R"]["stream"]["data"]))
        raw[10:15] = bytes([2, 0, 1, 0, 1])
        objects["obj:5 0 R"]["stream"]["data"] = base64.b64encode(raw).decode()
        with self.assertRaises(ValueError):
            layout.inspect_layout(objects, xrefs, data, reject_decode)
        objects, xrefs, data = fixture()
        with self.assertRaises(ValueError):
            layout.inspect_layout(objects, xrefs, data.replace(b"5 0 obj", b"xref   "), reject_decode)

    def test_inactive_tagged_streams_do_not_become_physical_containers(self):
        objects, xrefs, data = fixture()
        objects["obj:99 0 R"] = {"stream": {"dict": {"/Type": "/ObjStm", "/N": False}}}
        self.assertEqual(layout.inspect_layout(objects, xrefs, data, reject_decode)["containers"], 1)

    def test_marker_only_classic_layout_has_no_members(self):
        objects, _, _ = fixture()
        with self.assertRaisesRegex(ValueError, "no compressed"):
            layout.inspect_layout(objects, b"1/0: uncompressed; offset = 16\n", b"% /Type /ObjStm /Type /XRef\n", reject_decode)

    def test_previous_section_and_implicit_entry_types_keep_active_ownership(self):
        objects, xrefs, data = fixture()
        rows = b"\x00\x40\x00\x00\x00\xa0\x00\x00"
        objects["obj:6 0 R"] = {"stream": {"dict": {"/Type": "/XRef", "/Size": 7,
            "/W": [0, 2, 2], "/Index": [5, 2], "/Prev": 64}, "data": base64.b64encode(rows).decode()}}
        xrefs += b"6/0: uncompressed; offset = 160\n"
        data = data.ljust(160, b" ") + b"6 0 obj\nstartxref\n160\n%%EOF\n"
        self.assertEqual(layout.inspect_layout(objects, xrefs, data, reject_decode),
                         dict(containers=1, compressedMembers=2, xrefStreams=2, filteredContainers=0))


if __name__ == "__main__":
    unittest.main()
