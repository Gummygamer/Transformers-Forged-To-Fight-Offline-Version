#!/usr/bin/env python3
"""Build the mmap-friendly payload for the in-app offline HTTP server.

The v1 blob is little-endian.  Its 64-byte header is ``char[8] magic`` at 0,
then u32 version (8), total size (12), loopback port (16), entry count/array
offset (20/24), prefix count/array offset (28/32), default body offset/length
(36/40), CRC-32 of ``[64:total_size]`` (44), and four zero u32s (48).  Both
arrays use 16-byte ``u32 key_off, key_len, body_off, body_len`` records.  All
strings/bodies are NUL-terminated and four-byte-aligned; their stored length
excludes the NUL, and all offsets are absolute from byte zero.  The exact table
layout is also recorded in TECHNICAL_NOTES.md; this module's reader is the
executable version of that contract.

The fake server has four response classes.  (A) canned response files, prefix
responses, and the default envelope are fully static.  (B) base/active, quest
detail/begin, and grouped autorefresh are deterministic but have a small,
authored input space, so each result is baked.  (C) tutorial, hero-data, and
saved-team replies echo small pieces of request JSON; their templates and
fragments are baked for memcpy-only splicing.  (D) quest movement is the one
genuinely stateful case: fakeserver._quest_positions is the sole cross-request
state and the payload supplies its start position, legal transition table, and
every corresponding response body.  The eventual C server must preserve the
same dispatch order: dynamic handling, exact canned route, prefix rule, then
the default envelope (with HEAD special-cased to an empty response).
"""

from __future__ import annotations

import argparse
import functools
import json
import struct
import zlib
from dataclasses import dataclass
from pathlib import Path

import fakeserver
import gamedata


PAYLOAD_MAGIC = b"TFTFPAY\0"
PAYLOAD_VERSION = 1
HEADER_SIZE = 64
RECORD_SIZE = 16
HERE = Path(__file__).resolve().parent
RESPONSES = HERE / "responses"
DEFAULT_BODY = b'{"error":null,"result":{}}'
MOVE_DIRECTIONS = ((0, 0), (1, 0), (-1, 0), (0, 1), (0, -1))
MOVE_STARTS = ((0, 1), (1, 1), (2, 1))


@dataclass(frozen=True)
class Payload:
    """Decoded payload fields used by the Python tests and C-server contract."""

    version: int
    listen_port: int
    entries: dict[str, bytes]
    prefixes: list[tuple[str, bytes]]
    default: bytes


def _envelope(result: object) -> bytes:
    return json.dumps({"error": None, "result": result}, separators=(",", ":")).encode()


def _route_from_response(path: Path) -> tuple[str, str]:
    """Reverse resp_path_for, refusing ambiguous underscore-containing paths."""
    stem = path.name[:-5] if path.name.endswith(".json") else ""
    if "__" not in stem:
        raise ValueError(f"response filename is not METHOD__path.json: {path.name}")
    method, encoded_path = stem.split("__", 1)
    route = "/" + encoded_path.replace("_", "/")
    if Path(fakeserver.resp_path_for(method, route)).resolve() != path.resolve():
        raise ValueError(
            f"cannot safely reverse response filename {path.name}; "
            "an underscore-containing path segment would be ambiguous"
        )
    return method, route


def _mission_pairs() -> list[tuple[str, str]]:
    """Read the authored quest list rather than embedding today's set/mission IDs."""
    data = json.loads((RESPONSES / "GET__quests_quest-list.json").read_bytes())
    records = data["result"]
    pairs = {
        (str(record["setId"]), str(quest["id"]))
        for record in records
        for quest in record.get("availableQuests", [])
    }
    if not pairs:
        raise ValueError("quest-list response has no available quests to export")
    return sorted(pairs)


def _tutorial_templates() -> dict[str, bytes]:
    """Ask fakeserver for its own shapes, then leave splice sentinels literal."""
    normal = fakeserver.H._dynamic(
        None, "/tutorial/start-tutorial", '{"tid":"%TID%","bid":"%BID%"}'
    )
    normal_nobid = fakeserver.H._dynamic(None, "/tutorial/start-tutorial", '{"tid":"%TID%"}')
    started = fakeserver.H._dynamic(
        None, "/tutorial/start-tutorial", '{"tid":"FTE","bid":"%BID%"}'
    ).replace(b'"FTE"', b'"%TID%"')
    started_nobid = fakeserver.H._dynamic(
        None, "/tutorial/start-tutorial", '{"tid":"FTE"}'
    ).replace(b'"FTE"', b'"%TID%"')
    blocked = fakeserver.H._dynamic(None, "/tutorial/start-tutorial", '{"tid":"ShieldTutorial"}')
    templates = {
        "@tutorial:completed": normal,
        "@tutorial:completed-nobid": normal_nobid,
        "@tutorial:started": started,
        "@tutorial:started-nobid": started_nobid,
        "@tutorial:error": blocked,
    }
    for key, body in templates.items():
        if body is None:
            raise ValueError(f"fakeserver did not produce tutorial template {key}")
    return templates


def _hero_detail(bid: str, rank: int, level: int) -> bytes:
    sentinel = 987654321
    detail = gamedata.build_base_hero_details(
        [{"bid": bid, "rank": rank, "level": level, "sig_lvl": sentinel}]
    )[0]
    encoded = json.dumps(detail, separators=(",", ":")).encode()
    token = str(sentinel).encode()
    if encoded.count(token) != 1:
        raise ValueError(f"could not replace sig_lvl in hero template for {bid}")
    return encoded.replace(token, b"%SIG%")


def _move_body(qid: str, start: tuple[int, int], dx: int, dy: int) -> bytes:
    nx, ny = start[0] + dx, start[1] + dy
    if not (0 <= nx < 3 and ny == 1):
        dx = dy = 0
    return _envelope(gamedata.build_quest_movedir(qid, dx, dy, start=start))


def build_entries(listen_port: int = 8080) -> dict[str, bytes]:
    """Return every exact route and helper key in deterministic insertion order."""
    if not 1 <= listen_port <= 65535:
        raise ValueError("listen_port must be in the range 1..65535")
    entries: dict[str, bytes] = {}

    def add(key: str, body: bytes) -> None:
        if key in entries:
            raise ValueError(f"duplicate payload key: {key}")
        entries[key] = body

    # Canned files deliberately remain raw bytes: whitespace in an authored canned
    # response is part of its reproducible wire payload.
    for response in sorted(RESPONSES.glob("*.json")):
        if response.name.startswith("_"):
            continue
        method, route = _route_from_response(response)
        add(f"{method} {route}", response.read_bytes())

    prefix_rules = json.loads((RESPONSES / "_prefix_rules.json").read_bytes())
    for prefix, filename in prefix_rules:
        # Prefix records themselves are built by build_payload, but reading them here
        # catches a missing referenced body before an APK is produced.
        body = (RESPONSES / filename).read_bytes()
        if not isinstance(prefix, str) or not body:
            raise ValueError("invalid prefix rule")

    add("GET /base/active", _envelope(gamedata.build_base_active()))
    for set_id, qid in _mission_pairs():
        add(f"POST /quests/quest-detail/{qid}", _envelope(gamedata.build_quest_detail(qid, set_id)))
        add(f"POST /quests/quest-begin/{qid}", _envelope(gamedata.build_quest_begin(qid, set_id)))

        add(f"@quest:start:{qid}", b"0 1")
        legal_lines = []
        for start in MOVE_STARTS:
            for dx, dy in MOVE_DIRECTIONS:
                nx, ny = start[0] + dx, start[1] + dy
                if 0 <= nx < 3 and ny == 1:
                    legal_lines.append((start[0], start[1], dx, dy, nx, ny))
                add(
                    f"@movedir:{qid}:{start[0]}:{start[1]}:{dx}:{dy}",
                    _move_body(qid, start, dx, dy),
                )
        moves = b"".join(
            ("%d %d %d %d %d %d\n" % line).encode() for line in sorted(legal_lines)
        )
        add(f"@quest:moves:{qid}", moves)

    add("@grouprefresh:missionsconfig", _envelope({"updates": [gamedata.build_missions_autorefresh_update()]}))
    add("@grouprefresh:", _envelope({"updates": []}))
    add("@tutorial:blocked", "\n".join(sorted(fakeserver.BLOCK_TUTORIALS)).encode())
    add("@tutorial:live", "\n".join(sorted(fakeserver.LIVE_TUTORIALS)).encode())
    for key, body in _tutorial_templates().items():
        add(key, body)

    add("@herodata:open", b'{"error":null,"result":[')
    add("@herodata:close", b"]}")
    for bid in sorted(gamedata.OWNED):
        for rank in range(1, 6):
            for level in range(1, 31):
                add(f"@hero:{bid}:{rank}:{level}", _hero_detail(bid, rank, level))
    add("@hero:*:1:1", _hero_detail("__unknown_bid__", 1, 1))

    add("@savedteam:open", b'{"error":null,"result":{"updates":{"savedTeams":[{"sid":"%TID%","heroes":[')
    add("@savedteam:close", b"]}]},\"deletes\":{}}}")
    for bid in sorted(gamedata.OWNED):
        add(f"@savedteam:hero:{bid}", json.dumps(gamedata.build_hero_entry(bid), separators=(",", ":")).encode())

    return entries


def _append_string(buf: bytearray, value: bytes) -> int:
    offset = len(buf)
    if offset % 4:
        raise AssertionError("payload value offset is not aligned")
    buf.extend(value)
    buf.append(0)
    buf.extend(b"\0" * ((-len(buf)) % 4))
    return offset


@functools.lru_cache(maxsize=None)
def build_payload(listen_port: int = 8080) -> bytes:
    """Build and memoise the byte-identical v1 payload for one listen port."""
    entries = build_entries(listen_port)
    ordered_entries = sorted(entries.items(), key=lambda item: item[0].encode())
    keys = [key.encode() for key, _ in ordered_entries]
    if len(keys) != len(set(keys)):
        raise AssertionError("payload keys are not unique")

    prefix_rules = json.loads((RESPONSES / "_prefix_rules.json").read_bytes())
    prefixes = [(str(prefix), (RESPONSES / filename).read_bytes()) for prefix, filename in prefix_rules]
    buffer = bytearray(HEADER_SIZE + RECORD_SIZE * len(ordered_entries) + RECORD_SIZE * len(prefixes))
    entry_offset = HEADER_SIZE
    prefix_offset = entry_offset + RECORD_SIZE * len(ordered_entries)

    for index, (key, body) in enumerate(ordered_entries):
        key_off = _append_string(buffer, key.encode())
        body_off = _append_string(buffer, body)
        struct.pack_into("<4I", buffer, entry_offset + index * RECORD_SIZE, key_off, len(key.encode()), body_off, len(body))
    for index, (key, body) in enumerate(prefixes):
        key_bytes = key.encode()
        key_off = _append_string(buffer, key_bytes)
        body_off = _append_string(buffer, body)
        struct.pack_into("<4I", buffer, prefix_offset + index * RECORD_SIZE, key_off, len(key_bytes), body_off, len(body))
    default_off = _append_string(buffer, DEFAULT_BODY)
    total_size = len(buffer)
    crc = zlib.crc32(buffer[HEADER_SIZE:]) & 0xFFFFFFFF
    struct.pack_into(
        "<8s14I", buffer, 0, PAYLOAD_MAGIC, PAYLOAD_VERSION, total_size, listen_port,
        len(ordered_entries), entry_offset, len(prefixes), prefix_offset,
        default_off, len(DEFAULT_BODY), crc, 0, 0, 0, 0,
    )
    return bytes(buffer)


def load_payload(blob: bytes) -> Payload:
    """Strict reference decoder for the v1 C-server payload contract."""
    if len(blob) < HEADER_SIZE:
        raise ValueError("payload is shorter than the 64-byte header")
    fields = struct.unpack_from("<8s14I", blob)
    magic, version, total_size, port, count, entry_off, prefix_count, prefix_off, default_off, default_len, crc, *reserved = fields
    if magic != PAYLOAD_MAGIC:
        raise ValueError("invalid payload magic")
    if version != PAYLOAD_VERSION:
        raise ValueError(f"unsupported payload version: {version}")
    if total_size != len(blob):
        raise ValueError("payload total size does not match blob length")
    if any(reserved):
        raise ValueError("payload reserved header fields are non-zero")
    if zlib.crc32(blob[HEADER_SIZE:]) & 0xFFFFFFFF != crc:
        raise ValueError("payload CRC-32 mismatch")

    def read_value(offset: int, length: int, label: str) -> bytes:
        if offset % 4:
            raise ValueError(f"{label} offset is not four-byte aligned")
        if offset < HEADER_SIZE or offset + length >= len(blob):
            raise ValueError(f"{label} is outside the payload")
        if blob[offset + length] != 0:
            raise ValueError(f"{label} is not NUL-terminated")
        padded_end = (offset + length + 1 + 3) & ~3
        if padded_end > len(blob) or any(blob[offset + length + 1:padded_end]):
            raise ValueError(f"{label} padding is invalid")
        return blob[offset:offset + length]

    def read_table(offset: int, length: int, label: str) -> list[tuple[str, bytes]]:
        if offset % 4 or offset < HEADER_SIZE or offset + length * RECORD_SIZE > len(blob):
            raise ValueError(f"{label} table is outside the payload")
        result = []
        for index in range(length):
            key_off, key_len, body_off, body_len = struct.unpack_from("<4I", blob, offset + index * RECORD_SIZE)
            try:
                key = read_value(key_off, key_len, f"{label} key {index}").decode("utf-8")
            except UnicodeDecodeError as exc:
                raise ValueError(f"{label} key {index} is not UTF-8") from exc
            result.append((key, read_value(body_off, body_len, f"{label} body {index}")))
        return result

    entry_pairs = read_table(entry_off, count, "entry")
    entry_keys = [key.encode() for key, _ in entry_pairs]
    if entry_keys != sorted(entry_keys) or len(entry_keys) != len(set(entry_keys)):
        raise ValueError("entry table is not strictly sorted by unique raw key bytes")
    prefix_pairs = read_table(prefix_off, prefix_count, "prefix")
    return Payload(version, port, dict(entry_pairs), prefix_pairs, read_value(default_off, default_len, "default body"))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True, type=Path, help="destination payload file")
    parser.add_argument("--listen-port", type=int, default=8080, help="loopback HTTP port (default: 8080)")
    args = parser.parse_args()
    blob = build_payload(args.listen_port)
    args.out.write_bytes(blob)
    payload = load_payload(blob)
    print(f"wrote {args.out}: {len(blob)} bytes, {len(payload.entries)} entries, {len(payload.prefixes)} prefixes, port {payload.listen_port}")


if __name__ == "__main__":
    main()
