#!/usr/bin/env python3
"""Assemble Legible-exported entry batches into the mmap-friendly payload format."""

from __future__ import annotations

import argparse
import json
import struct
import sys
import zlib
from pathlib import Path

MAGIC = b"TFTFPAY\0"
DEFAULT_BODY = b'{"error":null,"result":{}}'
HEADER_SIZE = 64
ROW_SIZE = 16


def padded_size(size: int) -> int:
    with_terminator = size + 1
    return with_terminator + ((4 - with_terminator % 4) % 4)


def read_batches(paths: list[Path]) -> dict[str, str]:
    entries: dict[str, str] = {}
    for path in paths:
        data = json.loads(path.read_text(encoding="utf-8"))
        keys = data.get("keys")
        bodies = data.get("bodies")
        if not isinstance(keys, list) or not isinstance(bodies, list) or len(keys) != len(bodies):
            raise ValueError(f"invalid payload shard key/body arrays: {path}")
        for key, body in zip(keys, bodies, strict=True):
            if not isinstance(key, str) or not isinstance(body, str):
                raise ValueError(f"payload shard contains a non-text entry: {path}")
            if key in entries:
                raise ValueError(f"duplicate payload key: {key}")
            entries[key] = body
    return entries


def read_prefixes(server_root: Path) -> list[tuple[str, str]]:
    response_root = server_root / "responses"
    rules = json.loads((response_root / "_prefix_rules.json").read_text(encoding="utf-8"))
    prefixes: list[tuple[str, str]] = []
    for rule in rules:
        if not isinstance(rule, list) or len(rule) != 2:
            raise ValueError("invalid prefix rule")
        key, filename = rule
        if not isinstance(key, str) or not isinstance(filename, str):
            raise ValueError("prefix rule key and filename must be text")
        body = (response_root / filename).read_text(encoding="utf-8").replace("\r\n", "\n")
        prefixes.append((key, body))
    return prefixes


def write_value(output: bytearray, offset: int, value: str) -> tuple[int, int]:
    encoded = value.encode("utf-8")
    length = len(encoded)
    output[offset : offset + length] = encoded
    end = offset + padded_size(length)
    return end, length


def assemble(server_root: Path, output_path: Path, listen_port: int, shard_paths: list[Path]) -> None:
    if not 1 <= listen_port <= 65535:
        raise ValueError("listen_port must be in the range 1..65535")

    entries = read_batches(shard_paths)
    ordered_entries = sorted(entries.items())
    prefixes = read_prefixes(server_root)
    total = HEADER_SIZE + ROW_SIZE * (len(ordered_entries) + len(prefixes))
    for key, body in ordered_entries:
        total += padded_size(len(key.encode("utf-8"))) + padded_size(len(body.encode("utf-8")))
    for key, body in prefixes:
        total += padded_size(len(key.encode("utf-8"))) + padded_size(len(body.encode("utf-8")))
    total += padded_size(len(DEFAULT_BODY))

    payload = bytearray(total)
    prefix_offset = HEADER_SIZE + ROW_SIZE * len(ordered_entries)
    values_offset = prefix_offset + ROW_SIZE * len(prefixes)
    cursor = values_offset

    for index, (key, body) in enumerate(ordered_entries):
        key_offset = cursor
        cursor, key_length = write_value(payload, cursor, key)
        body_offset = cursor
        cursor, body_length = write_value(payload, cursor, body)
        struct.pack_into(
            "<IIII",
            payload,
            HEADER_SIZE + index * ROW_SIZE,
            key_offset,
            key_length,
            body_offset,
            body_length,
        )

    for index, (key, body) in enumerate(prefixes):
        key_offset = cursor
        cursor, key_length = write_value(payload, cursor, key)
        body_offset = cursor
        cursor, body_length = write_value(payload, cursor, body)
        struct.pack_into(
            "<IIII",
            payload,
            prefix_offset + index * ROW_SIZE,
            key_offset,
            key_length,
            body_offset,
            body_length,
        )

    default_offset = cursor
    payload[cursor : cursor + len(DEFAULT_BODY)] = DEFAULT_BODY
    cursor += padded_size(len(DEFAULT_BODY))
    if cursor != total:
        raise ValueError(f"payload writer ended at {cursor} instead of {total}")

    payload[:8] = MAGIC
    struct.pack_into(
        "<IIIIIIII",
        payload,
        8,
        1,
        total,
        listen_port,
        len(ordered_entries),
        HEADER_SIZE,
        len(prefixes),
        prefix_offset,
        default_offset,
    )
    struct.pack_into("<I", payload, 40, len(DEFAULT_BODY))
    struct.pack_into("<I", payload, 44, zlib.crc32(payload[HEADER_SIZE:]) & 0xFFFFFFFF)

    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_bytes(payload)
    print(
        f"wrote {output_path}: {total} bytes, {len(ordered_entries)} entries, "
        f"{len(prefixes)} prefixes, port {listen_port}"
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server-root", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--listen-port", type=int, default=8080)
    parser.add_argument("--shard-input", type=Path, action="append", default=[])
    args = parser.parse_args()
    try:
        assemble(args.server_root, args.out, args.listen_port, args.shard_input)
    except (OSError, UnicodeError, ValueError, json.JSONDecodeError, struct.error) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
