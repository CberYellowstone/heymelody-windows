#!/usr/bin/env python3
"""Meaningful offline integrity/bounds checks for the legacy firmware research tool."""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import lzma
from pathlib import Path
import struct
import sys
import tempfile
import unittest
from unittest import mock

sys.dont_write_bytecode = True
TOOL = Path(__file__).resolve().parents[1] / "fetch_earbud_firmware.py"
SPEC = importlib.util.spec_from_file_location("earbud_firmware_under_test", TOOL)
firmware = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(firmware)
PID = "067C10"
TRAILER = b"CRC32_OF_IMAGE=0x8EF83654\nPRODUCT_ID=0x067C10\n"


def compressed(raw: bytes) -> bytes:
    return lzma.compress(raw, format=lzma.FORMAT_ALONE,
                         filters=[{"id": lzma.FILTER_LZMA1, "dict_size": 1024 * 1024}])


def package(*pieces: bytes, trailer: bytes = TRAILER) -> bytes:
    out = bytearray(b"\xff" * 4)
    for raw in pieces:
        block = compressed(raw)
        out.extend(struct.pack(">I", len(block)))
        out.extend(block)
    out.extend(trailer)
    return bytes(out)


def metadata(blob: bytes, *, name: str = "sample.bin") -> dict:
    return {"code": 0, "msg": "success", "data": {
        "productId": PID, "name": "test fixture", "version": "125", "content": [{
            "name": name, "size": str(len(blob)), "firmwareSHA256": hashlib.sha256(blob).hexdigest(),
            "url": "https://ocs-cn-south2.heytapcs.com/public-fixture-not-downloaded"}]}}


class DecodeTests(unittest.TestCase):
    def test_valid_multi_block_reconstructs_bytes_and_offsets(self):
        first = bytes(range(256)) * 512
        last = b"CHIP=fixture\0KERNEL=fixture\0SW_VER=0.1.2.5\0" + b"Z" * 300
        blob = package(first, last)
        image, report = firmware.inspect_bytes(blob, expected_product_id=PID,
                                               metadata=metadata(blob), filename="sample.bin")
        self.assertEqual(image, first + last)
        self.assertEqual(report["block_count"], 2)
        self.assertEqual(report["blocks"][1]["raw_offset"], len(first))
        self.assertEqual(report["blocks"][1]["length_field_offset"],
                         report["blocks"][0]["compressed_offset"] + report["blocks"][0]["compressed_size"])
        self.assertEqual(report["container"]["trailer_offset"] + len(TRAILER), len(blob))
        self.assertTrue(report["metadata"]["sha256_verified"])
        self.assertEqual(report["image"]["sha256"], hashlib.sha256(image).hexdigest())

    def test_vendor_crc_is_explicitly_unverified(self):
        _, report = firmware.inspect_bytes(package(b"fixture"), expected_product_id=PID)
        self.assertFalse(report["vendor_crc"]["verified"])
        self.assertEqual(report["vendor_crc"]["status"], "unresolved")
        self.assertNotEqual(report["vendor_crc"]["declared"],
                            report["vendor_crc"]["candidate_zlib_crc32"]["raw_image"])

    def test_wrong_or_unsupported_magic(self):
        blob = package(b"fixture")
        for prefix in (b"OPKG", b"\x00" * 4):
            with self.subTest(prefix=prefix), self.assertRaisesRegex(firmware.FormatError, "magic"):
                firmware.inspect_bytes(prefix + blob[4:])

    def test_truncation_at_each_boundary_is_rejected(self):
        blob = package(b"fixture" * 100)
        for length in (0, 3, 4, 5, 7, 12, len(blob) - len(TRAILER) - 1,
                       len(blob) - len(TRAILER), len(blob) - 1):
            with self.subTest(length=length), self.assertRaises(firmware.FormatError):
                firmware.inspect_bytes(blob[:length])

    def test_declared_length_outside_bounds(self):
        blob = package(b"fixture")
        for length in (0, 12, len(blob), 0xffffffff):
            with self.subTest(length=length), self.assertRaises(firmware.FormatError):
                firmware.inspect_bytes(blob[:4] + struct.pack(">I", length) + blob[8:])

    def test_incomplete_lzma_with_self_consistent_wrapper_is_rejected(self):
        block = compressed(b"fixture" * 50)[:-3]
        blob = b"\xff" * 4 + struct.pack(">I", len(block)) + block + TRAILER
        with self.assertRaisesRegex(firmware.FormatError, "did not finish|decoding failed"):
            firmware.inspect_bytes(blob)

    def test_invalid_lzma_stream_is_rejected(self):
        block = b"\xff" * 20
        with self.assertRaisesRegex(firmware.FormatError, "decoding failed"):
            firmware.inspect_bytes(b"\xff" * 4 + struct.pack(">I", len(block)) + block + TRAILER)

    def test_unused_bytes_inside_block_are_rejected(self):
        block = compressed(b"fixture") + b"extra"
        with self.assertRaisesRegex(firmware.FormatError, "Extra bytes"):
            firmware.inspect_bytes(b"\xff" * 4 + struct.pack(">I", len(block)) + block + TRAILER)

    def test_large_compressed_dictionary_is_rejected(self):
        block = bytearray(compressed(b"fixture"))
        block[1:5] = struct.pack("<I", 256 * 1024 * 1024)
        with self.assertRaisesRegex(firmware.FormatError, "decoding failed"):
            firmware.inspect_bytes(b"\xff" * 4 + struct.pack(">I", len(block)) + block + TRAILER)

    def test_block_output_limit(self):
        with self.assertRaisesRegex(firmware.FormatError, "128 KiB"):
            firmware.inspect_bytes(package(b"X" * (firmware.MAX_BLOCK_OUTPUT_BYTES + 1)))

    def test_only_last_block_may_be_short(self):
        with self.assertRaisesRegex(firmware.FormatError, "final block"):
            firmware.inspect_bytes(package(b"short", b"another"))

    def test_empty_block_is_rejected(self):
        with self.assertRaisesRegex(firmware.FormatError, "Empty"):
            firmware.inspect_bytes(package(b""))

    def test_no_blocks_is_rejected(self):
        with self.assertRaisesRegex(firmware.FormatError, "no compressed blocks"):
            firmware.inspect_bytes(b"\xff" * 4 + TRAILER)

    def test_count_and_image_limits(self):
        blob = package(b"X" * firmware.MAX_BLOCK_OUTPUT_BYTES, b"last")
        with mock.patch.object(firmware, "MAX_BLOCK_COUNT", 1):
            with self.assertRaisesRegex(firmware.FormatError, "block count"):
                firmware.inspect_bytes(blob)
        with mock.patch.object(firmware, "MAX_IMAGE_BYTES", firmware.MAX_BLOCK_OUTPUT_BYTES):
            with self.assertRaisesRegex(firmware.FormatError, "image exceeds"):
                firmware.inspect_bytes(blob)

    def test_unknown_trailer_and_trailing_data_are_rejected(self):
        for trailer in (b"OTHER=fixture\n", TRAILER + b"unexpected", TRAILER.replace(b"\n", b"\r\n")):
            with self.subTest(trailer=trailer), self.assertRaises(firmware.FormatError):
                firmware.inspect_bytes(package(b"fixture", trailer=trailer))

    def test_expected_product_id_mismatch(self):
        with self.assertRaisesRegex(firmware.FormatError, "product ID mismatch"):
            firmware.inspect_bytes(package(b"fixture"), expected_product_id="06EC10")

    def test_metadata_identity_size_hash_and_filename_mismatches(self):
        blob = package(b"fixture")
        for kind in ("product_id", "size", "digest", "filename"):
            value = metadata(blob)
            if kind == "product_id":
                value["data"]["productId"] = "06EC10"
            elif kind == "size":
                value["data"]["content"][0]["size"] = str(len(blob) + 1)
            elif kind == "digest":
                value["data"]["content"][0]["firmwareSHA256"] = "0" * 64
            else:
                value["data"]["content"][0]["name"] = "another.bin"
            with self.subTest(kind=kind), self.assertRaises(firmware.FormatError):
                firmware.inspect_bytes(blob, metadata=value, filename="sample.bin")

    def test_boolean_or_missing_metadata_size_is_rejected(self):
        blob = package(b"fixture")
        for size in (True, None, -1, "0010", "1e6"):
            value = metadata(blob)
            value["data"]["content"][0]["size"] = size
            with self.subTest(size=size), self.assertRaises(firmware.FormatError):
                firmware.inspect_bytes(blob, metadata=value, filename="sample.bin")


class ArtifactAndOnlineBoundaryTests(unittest.TestCase):
    def test_unpack_preserves_source_and_refuses_existing_output(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "sample.bin"
            blob = package(b"fixture")
            source.write_bytes(blob)
            dest = root / "new-run"
            report = firmware.unpack(source, dest, PID)
            self.assertEqual(source.read_bytes(), blob)
            self.assertEqual((dest / "image.raw.bin").read_bytes(), b"fixture")
            original_report = (dest / "inspection.json").read_bytes()
            with self.assertRaises(FileExistsError):
                firmware.unpack(source, dest, PID)
            self.assertEqual((dest / "inspection.json").read_bytes(), original_report)
            self.assertEqual(json.loads(original_report)["image"]["sha256"], report["image"]["sha256"])

    def test_invalid_input_creates_no_output_directory(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "broken.bin"
            source.write_bytes(b"broken")
            dest = Path(directory) / "unpack"
            with self.assertRaises(firmware.FormatError):
                firmware.unpack(source, dest, PID)
            self.assertFalse(dest.exists())

    def test_reserved_or_escaping_image_name_creates_no_output(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "sample.bin"
            source.write_bytes(package(b"fixture"))
            for name in ("../outside.bin", "inspection.json", "blocks.json", "x\\y.bin"):
                dest = Path(directory) / "unpack"
                with self.subTest(name=name), self.assertRaises(firmware.FormatError):
                    firmware.unpack(source, dest, PID, image_name=name)
                self.assertFalse(dest.exists())

    def test_online_requires_flag_before_importing_authentication(self):
        with mock.patch.object(firmware, "online_helpers", side_effect=AssertionError("must not load auth")):
            with self.assertRaisesRegex(firmware.FormatError, "--online"):
                firmware.fetch(argparse.Namespace(online=False))

    def test_download_host_credentials_port_and_scheme_are_checked(self):
        self.assertTrue(firmware.approved_download("https://ocs-cn-south2.heytapcs.com/public"))
        for url in ("http://ocs-cn-south2.heytapcs.com/public", "https://heytapcs.com.attacker.test/public",
                    "https://attackerheytapcs.com/public", "https://secret@heytapcs.com/public",
                    "https://heytapcs.com:444/public", "https://heytapcs.com:bad/public"):
            with self.subTest(url=url):
                self.assertFalse(firmware.approved_download(url))

    def test_metadata_projection_drops_unknown_identifier_and_secret_fields(self):
        blob = package(b"fixture")
        value = metadata(blob)
        value["trace"] = "private-trace"
        value["data"]["macAddress"] = "private-address"
        value["data"]["content"][0]["clientKey"] = "private-key"
        projected = firmware.sanitized_metadata(value)
        serialized = json.dumps(projected)
        self.assertNotIn("private-", serialized)
        self.assertNotIn("clientKey", serialized)
        self.assertEqual(projected["data"]["productId"], PID)

    def test_metadata_projection_rejects_nested_public_field_values(self):
        value = metadata(package(b"fixture"))
        value["data"]["version"] = {"unexpected_private_field": "value"}
        with self.assertRaisesRegex(firmware.FormatError, "must be strings"):
            firmware.sanitized_metadata(value)

    def test_nested_response_message_is_never_preserved(self):
        value = metadata(package(b"fixture"))
        value["msg"] = {"unexpected_private_field": "private-value"}
        with self.assertRaisesRegex(firmware.FormatError, "message must be"):
            firmware.sanitized_metadata(value)

    def test_offline_metadata_nested_values_are_rejected(self):
        blob = package(b"fixture")
        value = metadata(blob)
        value["data"]["version"] = {"unexpected_private_field": "private-value"}
        with self.assertRaisesRegex(firmware.FormatError, "must be strings"):
            firmware.inspect_bytes(blob, metadata=value, filename="sample.bin")

    def test_complete_metadata_stays_in_report_but_stdout_summary_is_bounded(self):
        blob = package(b"fixture")
        value = metadata(blob)
        value["data"]["version"] = "1" * 15000
        _, report = firmware.inspect_bytes(blob, metadata=value, filename="sample.bin")
        self.assertEqual(len(report["metadata"]["version"]), 15000)
        summary = firmware.brief(report)
        self.assertLess(len(summary["metadata"]["version"]), 240)
        self.assertLessEqual(len(firmware.bounded_json(summary)), 6000)
        self.assertLessEqual(len(firmware.bounded_json({"message": "x" * 15000})), 6000)

    def test_acquisition_reserved_package_names_are_rejected_before_writing(self):
        blob = package(b"fixture")
        for name in ("metadata.sanitized.json", "acquisition.json", "ACQUISITION.JSON"):
            with self.subTest(name=name), self.assertRaisesRegex(firmware.FormatError, "reserved"):
                firmware.metadata_entry(metadata(blob, name=name))


if __name__ == "__main__":
    unittest.main()
