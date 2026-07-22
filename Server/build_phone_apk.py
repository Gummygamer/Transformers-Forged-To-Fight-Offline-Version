#!/usr/bin/env python3
"""Build a stock-device APK for a local fake server.

The emulator setup rewrites /system/etc/hosts and the system CA store, which
requires root.  A retail phone cannot do either.  This builder instead:

* rewrites the game's known backend hosts to a configurable server host on
  port 8443.  Use a laptop LAN address for cable-free play, or the default
  127.0.0.1 with adb reverse for the original USB setup.
* embeds the repository's current arm64 runtime hook.  The source APK is a
  convenient build input, but its bundled hook may predate later gameplay
  fixes.

The original target SDK is preserved. The bundled native patches handle the
fake server certificate; lowering the target SDK causes modern Play Protect
to reject the installation.

The resulting APK must be signed after this script finishes.
"""

from __future__ import annotations

import argparse
import ipaddress
import re
import struct
import zipfile
from pathlib import Path


METADATA = "assets/bin/Data/Managed/Metadata/global-metadata.dat"
SPARX_MANIFEST = "res/raw/sparxmanifest"
ENDPOINT_CONFIG = "assets/bin/Data/e1917cd7a6bdb4492a247b8f758df2ae"
ARM64_HOOK = "lib/arm64-v8a/libdothook.so"
CURRENT_HOOK = Path(__file__).resolve().parents[1] / "tools/nativehook/libdothook.so"
DEFAULT_SERVER_HOST = "127.0.0.1"
HOSTNAME_RE = re.compile(
    r"(?=.{1,253}\Z)(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)*"
    r"[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\Z"
)


def validate_server_host(value: str) -> str:
    """Accept a bare IPv4 address or DNS name, never a URL/port/path."""
    value = value.strip()
    if not value or any(char in value for char in "/:[]"):
        raise argparse.ArgumentTypeError(
            "server host must be a bare IPv4 address or DNS name (no scheme, port, or path)"
        )
    try:
        ipaddress.IPv4Address(value)
    except ipaddress.AddressValueError:
        # A dotted decimal typo (for example 192.168.0.999) must not slip
        # through as a syntactically valid DNS name.
        if re.fullmatch(r"[0-9.]+", value) or not HOSTNAME_RE.fullmatch(value):
            raise argparse.ArgumentTypeError(f"invalid server host: {value!r}")
    return value


def literal_replacements(server_host: str) -> tuple[tuple[bytes, bytes], ...]:
    host = server_host.encode("ascii")
    return (
        (b"tf-odr.mcoc-cdn.cn", host + b":8443"),
        (b"tf-static.mcoc-cdn.cn", host + b":8443"),
        (b"words-express.tf-cdn.net", host + b":8443"),
        (b"wss://gametalk.sparx.io:30443", b"wss://" + host + b":30443"),
    )


def patch_metadata(metadata: bytes, server_host: str) -> tuple[bytes, list[str]]:
    """Rewrite IL2CPP string literals and their table lengths without shifting data."""
    out = bytearray(metadata)
    literal_offset, literal_count, data_offset, _ = struct.unpack_from("<4I", out, 8)
    changed: list[str] = []
    pending = dict(literal_replacements(server_host))
    for entry in range(literal_offset, literal_offset + literal_count, 8):
        length, index = struct.unpack_from("<II", out, entry)
        value_start = data_offset + index
        value = bytes(out[value_start : value_start + length])
        replacement = pending.pop(value, None)
        if replacement is None:
            continue
        if len(replacement) > length:
            raise ValueError(f"replacement is longer than literal: {value!r}")
        out[value_start : value_start + len(replacement)] = replacement
        struct.pack_into("<I", out, entry, len(replacement))
        changed.append(value.decode())
    if pending:
        missing = ", ".join(value.decode() for value in pending)
        raise ValueError(f"expected string literals not found: {missing}")
    return bytes(out), changed


def patch_sparx_manifest(data: bytes, server_host: str) -> bytes:
    old = b"https://tform-0901-hzlhiniyfcwf.tf-cdn.net"
    if data.count(old) != 1:
        raise ValueError("unexpected Sparx manifest endpoint count")
    return data.replace(old, f"https://{server_host}:8443".encode("ascii"))


def patch_endpoint_config(data: bytes, server_host: str) -> bytes:
    """Patch fixed-size Unity TextAsset JSON, preserving its serialized byte length."""
    old = b'{"Default":{"Prod":"https://tform-0901-hzlhiniyfcwf.tf-cdn.net"}}'
    new = f'{{"Default":{{"Prod":"https://{server_host}:8443"}}}}'.encode("ascii")
    if data.count(old) != 1:
        raise ValueError("unexpected Unity endpoint config count")
    if len(new) > len(old):
        raise ValueError("server host is too long for the fixed-size Unity endpoint config")
    return data.replace(old, new + b" " * (len(old) - len(new)))


def build(source: Path, destination: Path, server_host: str = DEFAULT_SERVER_HOST) -> list[str]:
    if source.resolve() == destination.resolve():
        raise ValueError("destination must differ from source; the original APK is preserved")
    destination.parent.mkdir(parents=True, exist_ok=True)
    if not CURRENT_HOOK.is_file():
        raise ValueError(f"current runtime hook is missing: {CURRENT_HOOK}")
    hook = CURRENT_HOOK.read_bytes()
    if not hook.startswith(b"\x7fELF"):
        raise ValueError(f"current runtime hook is not an ELF library: {CURRENT_HOOK}")

    with zipfile.ZipFile(source, "r") as zin, zipfile.ZipFile(destination, "w") as zout:
        names = set(zin.namelist())
        for required in ("AndroidManifest.xml", METADATA, SPARX_MANIFEST, ENDPOINT_CONFIG, ARM64_HOOK):
            if required not in names:
                raise ValueError(f"APK is missing {required}")

        changed_hosts: list[str] = []
        for info in zin.infolist():
            # Old JAR signatures are invalid after modification. apksigner will add new ones.
            upper = info.filename.upper()
            if upper.startswith("META-INF/") and upper.endswith((".SF", ".RSA", ".DSA", ".EC", "MANIFEST.MF")):
                continue
            data = zin.read(info)
            if info.filename == METADATA:
                data, metadata_hosts = patch_metadata(data, server_host)
                changed_hosts.extend(metadata_hosts)
            elif info.filename == SPARX_MANIFEST:
                data = patch_sparx_manifest(data, server_host)
                changed_hosts.append("tform-0901-hzlhiniyfcwf.tf-cdn.net")
            elif info.filename == ENDPOINT_CONFIG:
                data = patch_endpoint_config(data, server_host)
            elif info.filename == ARM64_HOOK:
                data = hook
            zout.writestr(info, data)
    return list(dict.fromkeys(changed_hosts))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path)
    parser.add_argument(
        "--server-host",
        type=validate_server_host,
        default=DEFAULT_SERVER_HOST,
        help=(
            "fake-server IPv4 address or DNS name reachable by the phone "
            f"(default: {DEFAULT_SERVER_HOST}, for adb reverse)"
        ),
    )
    args = parser.parse_args()
    hosts = build(args.source, args.destination, args.server_host)
    print(f"wrote unsigned APK: {args.destination}")
    print("redirected: " + ", ".join(hosts))
    print(f"fake server: https://{args.server_host}:8443")
    print(f"embedded runtime hook: {CURRENT_HOOK} ({CURRENT_HOOK.stat().st_size} bytes)")
    print("targetSdkVersion: preserved")


if __name__ == "__main__":
    main()
