#!/usr/bin/env python3
"""Compare Unity recovery logs using only unique IL2CPP method failures."""

from __future__ import annotations

import argparse
import gzip
import json
import re
import math
from collections import Counter
from pathlib import Path


IL2CPP_FAILURE = re.compile(
    r"IL2CPP error for method '([^']+)' in assembly '([^']+)'"
)


def failures(path: Path) -> set[tuple[str, str]]:
    opener = gzip.open if path.suffix == ".gz" else open
    found: set[tuple[str, str]] = set()
    with opener(path, "rt", errors="replace") as stream:
        for line in stream:
            match = IL2CPP_FAILURE.search(line)
            if match:
                signature, assembly = match.groups()
                found.add((Path(assembly).name, signature))
    return found


def cycle_metadata(path: Path) -> dict[str, object] | None:
    candidate = Path(str(path) + ".cycle.json")
    if not candidate.is_file():
        return None
    try:
        return json.loads(candidate.read_text())
    except (OSError, json.JSONDecodeError):
        return None


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("logs", type=Path, nargs="+", help="Unity logs, plain or gzip")
    parser.add_argument(
        "--history", action="store_true",
        help="report recurring signatures across all supplied logs (requires at least two)",
    )
    parser.add_argument("--limit", type=int, default=30, help="maximum signatures shown per section")
    args = parser.parse_args()
    if args.history and len(args.logs) < 2:
        parser.error("--history requires at least two Unity logs")
    if not args.history and len(args.logs) != 2:
        parser.error("supply exactly two logs unless --history is used")
    for path in args.logs:
        if not path.is_file():
            parser.error(f"Unity log does not exist: {path}")

    observed = [failures(path) for path in args.logs]
    if args.history:
        frequency = Counter(signature for run in observed for signature in run)
        priority = lambda signature: (-frequency[signature], signature)
        stable = sorted(
            (signature for signature, count in frequency.items() if count == len(observed)),
            key=priority,
        )
        recurring_threshold = math.ceil(len(observed) * 0.75)
        recurring = sorted(
            (signature for signature, count in frequency.items()
             if recurring_threshold <= count < len(observed)),
            key=priority,
        )
        transient = sorted(
            (signature for signature, count in frequency.items() if count < recurring_threshold),
            key=priority,
        )
        print(f"Unique IL2CPP failures per run: {' → '.join(str(len(run)) for run in observed)}")
        print(f"Present in every run: {len(stable)}; recurrent: {len(recurring)}; intermittent: {len(transient)}")
        print("Within each group, signatures are ordered by recurrence count (highest first).")
        groups = (("Stable", stable), (f"Recurrent ({recurring_threshold}+ runs)", recurring),
                  ("Intermittent", transient))
        for title, entries in groups:
            print(f"\n{title}:")
            if not entries:
                print("  (none)")
            for assembly, signature in entries[:args.limit]:
                print(f"  {frequency[(assembly, signature)]}/{len(observed)} {assembly}: {signature}")
            if len(entries) > args.limit:
                print(f"  … {len(entries) - args.limit} more")
    else:
        before, after = observed
        fixed = sorted(before - after)
        introduced = sorted(after - before)
        print(f"Unique IL2CPP failures: {len(before)} → {len(after)} ({len(after) - len(before):+d})")
        print(f"Cleared: {len(fixed)}; newly surfaced: {len(introduced)}")
        for title, entries in (("Cleared", fixed), ("New or changed", introduced)):
            print(f"\n{title}:")
            if not entries:
                print("  (none)")
            for assembly, signature in entries[:args.limit]:
                print(f"  {assembly}: {signature}")
            if len(entries) > args.limit:
                print(f"  … {len(entries) - args.limit} more")

    for label, path in zip(("Previous", "Current"), args.logs) if not args.history else (
        (f"Run {index + 1}", path) for index, path in enumerate(args.logs)
    ):
        size = path.stat().st_size
        metadata = cycle_metadata(path)
        print(f"\n{label} log: {path.name}, {size / 1024**2:.2f} MiB retained")
        if metadata:
            seconds = float(metadata.get("unity_seconds", 0))
            preflight_seconds = float(metadata.get("preflight_seconds", 0))
            raw_bytes = int(metadata.get("raw_log_bytes", size))
            if "net_free_space_delta_bytes" in metadata:
                free_delta = int(metadata["net_free_space_delta_bytes"])
            else:
                # Older sidecars stored free-space consumption with the inverse sign.
                free_delta = -int(metadata.get("net_free_space_change_bytes", 0))
            if abs(free_delta) < 1024**3:
                free_delta_label = f"{free_delta / 1024**2:+.2f} MiB"
            else:
                free_delta_label = f"{free_delta / 1024**3:+.2f} GiB"
            print(
                f"  preflight {preflight_seconds:.1f} s; Unity {seconds / 60:.1f} min; "
                f"raw log {raw_bytes / 1024**2:.1f} MiB; "
                f"free-space delta {free_delta_label}"
            )
        else:
            print("  timing/storage sidecar unavailable for this historical build")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
