#!/usr/bin/env python3
"""Summarize unique Unity/Mono import blockers with source log locations."""

from __future__ import annotations

import argparse
import gzip
import json
import re
from pathlib import Path


INVALID_IL = re.compile(r"InvalidProgramException: Invalid IL code in (.+?):\s*(.*)$")
ROOT_EXCEPTION = re.compile(
    r"^(NullReferenceException|MissingMethodException|BadImageFormatException|"
    r"TypeLoadException|FileNotFoundException|MissingFieldException|"
    r"InvalidOperationException|ArgumentException|ArgumentNullException|"
    r"ArgumentOutOfRangeException|FieldAccessException|UnityException):\s*(.*)$"
)
TYPE_INITIALIZER = re.compile(r"Rethrow as TypeInitializationException: The type initializer for '([^']+)'")
PLAYER_LAYOUT_ERROR = re.compile(r"Error building player because script class layout is incompatible")
LAYOUT_CLASS = re.compile(r"Fields serialized in (Editor|target platform), class '([^']+)'")
IL2CPP_FAILURE = re.compile(
    r"IL2CPP error for method '([^']+)' in assembly '([^']+)'"
)


def build_queue(log_path: Path) -> list[dict[str, object]]:
    opener = gzip.open if log_path.suffix == ".gz" else open
    with opener(log_path, "rt", errors="replace") as stream:
        lines = stream.read().splitlines()
    items: dict[str, dict[str, object]] = {}
    layout_reports: dict[str, dict[str, list[str] | int]] = {}
    for index, line in enumerate(lines):
        invalid = INVALID_IL.search(line)
        root = ROOT_EXCEPTION.match(line)
        il2cpp = IL2CPP_FAILURE.search(line)
        fatal = "Caught fatal signal" in line
        if invalid:
            category = "invalid-il"
            target = invalid.group(1)
            detail = invalid.group(2)
            key = f"{category}\t{target}\t{detail}"
        elif root:
            category = "managed-exception"
            detail = f"{root.group(1)}: {root.group(2)}"
            target = ""
            for frame in lines[index + 1 : index + 8]:
                match = re.match(r"\s*at (.+?)(?: \[|$)", frame)
                if match:
                    target = match.group(1)
                    break
            key = f"{category}\t{target}\t{detail}"
        elif il2cpp:
            signature, assembly = il2cpp.groups()
            category = "il2cpp-method-failure"
            target = f"{Path(assembly).name}: {signature}"
            detail = "IL2CPP could not translate this managed method body."
            key = f"{category}\t{target}\t{detail}"
        elif fatal:
            category = "unity-process-crash"
            detail = line.strip()
            target = ""
            for frame in lines[index + 1 : index + 12]:
                if "mono_callspec_cleanup" in frame:
                    target = frame.strip()
                    break
            key = f"{category}\t{target}\t{detail}"
        else:
            layout = LAYOUT_CLASS.search(line)
            if layout:
                platform, class_name = layout.groups()
                fields: list[str] = []
                for field_line in lines[index + 1 : index + 8]:
                    field = re.match(r"\s+'([^']+)' of type '([^']+)'", field_line)
                    if not field:
                        break
                    fields.append(f"{field.group(1)}: {field.group(2)}")
                report = layout_reports.setdefault(class_name, {"count": 0})
                report[platform] = fields
                report["count"] = int(report["count"]) + 1
                continue
            if PLAYER_LAYOUT_ERROR.search(line):
                target = next(
                    (name for name in layout_reports),
                    "Unity serialized class layout",
                )
                key = f"player-class-layout\t{target}\tincompatible editor/player fields"
                item = items.setdefault(key, {
                    "category": "player-class-layout",
                    "target": target,
                    "detail": "",
                    "count": 0,
                    "log_lines": [],
                })
                item["count"] = int(item["count"]) + 1
                item["log_lines"].append(index + 1)  # type: ignore[union-attr]
                continue
            continue

        item = items.setdefault(key, {
            "category": category,
            "target": target,
            "detail": detail,
            "count": 0,
            "log_lines": [],
        })
        item["count"] = int(item["count"]) + 1
        item["log_lines"].append(index + 1)  # type: ignore[union-attr]

    for class_name, report in layout_reports.items():
        editor = report.get("Editor", [])
        player = report.get("target platform", [])
        editor_set = set(editor)  # type: ignore[arg-type]
        player_set = set(player)  # type: ignore[arg-type]
        differences = [f"Editor fields: {', '.join(editor) or '(none)'}",
                       f"Target fields: {', '.join(player) or '(none)'}"]
        extra = sorted(player_set - editor_set)
        missing = sorted(editor_set - player_set)
        if extra:
            differences.append(f"Target-only fields: {', '.join(extra)}")
        if missing:
            differences.append(f"Editor-only fields: {', '.join(missing)}")
        key = f"player-class-layout\t{class_name}\tincompatible editor/player fields"
        item = items.setdefault(key, {
            "category": "player-class-layout",
            "target": class_name,
            "detail": "",
            "count": 0,
            "log_lines": [],
        })
        item["detail"] = "; ".join(differences)
        # One editor/player mismatch is one blocker; the diagnostic prints both
        # platform layouts, so counting those blocks would incorrectly report 2.
        item["count"] = max(int(item["count"]), 1)
        item["log_lines"].extend(
            i + 1 for i, line in enumerate(lines)
            if LAYOUT_CLASS.search(line) and class_name in line
        )  # type: ignore[union-attr]

    # The build-level wrapper exception is not an additional repair target when
    # Unity has already listed the method bodies that caused the IL2CPP failure.
    if any(item["category"] == "il2cpp-method-failure" for item in items.values()):
        items = {
            key: item for key, item in items.items()
            if item["category"] not in {"managed-exception", "unity-build-error"}
        }

    for index, line in enumerate(lines):
        if not any(pattern in line for pattern in (
            "Error building player because", "Android build failed:", "Build completed with a result of",
        )):
            continue
        if any(item["category"] == "il2cpp-method-failure" for item in items.values()):
            continue
        detail = line.strip()
        key = f"unity-build-error\tUnity\t{detail}"
        item = items.setdefault(key, {
            "category": "unity-build-error",
            "target": "Unity",
            "detail": detail,
            "count": 0,
            "log_lines": [],
        })
        item["count"] = int(item["count"]) + 1
        item["log_lines"].append(index + 1)  # type: ignore[union-attr]

    for index, line in enumerate(lines):
        match = TYPE_INITIALIZER.search(line)
        if not match:
            continue
        key = f"type-initializer\t{match.group(1)}\twrapper"
        item = items.setdefault(key, {
            "category": "type-initializer-wrapper",
            "target": match.group(1),
            "detail": "Underlying exception is listed separately when present.",
            "count": 0,
            "log_lines": [],
        })
        item["count"] = int(item["count"]) + 1
        item["log_lines"].append(index + 1)  # type: ignore[union-attr]

    queue = list(items.values())
    queue.sort(key=lambda item: (-int(item["count"]), str(item["category"]), str(item["target"])))
    return queue


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("log", type=Path, help="Unity editor log to summarize")
    parser.add_argument("--format", choices=("json", "markdown"), default="markdown")
    parser.add_argument("--output", type=Path, help="write report here instead of stdout")
    args = parser.parse_args()
    if not args.log.is_file():
        parser.error(f"Unity log does not exist: {args.log}")
    queue = build_queue(args.log)
    if args.format == "json":
        report = json.dumps({"log": str(args.log.resolve()), "items": queue}, indent=2) + "\n"
    else:
        report_lines = [f"# Unity repair queue: {args.log.name}", ""]
        if not queue:
            report_lines.append("No recognized managed exceptions or process crashes.")
        for item in queue:
            where = ", ".join(str(number) for number in item["log_lines"][:6])
            report_lines.extend([
                f"- **{item['category']}** · {item['target'] or 'Unity'} · {item['count']} occurrence(s)",
                f"  {item['detail']}",
                f"  Log lines: {where}",
            ])
        report = "\n".join(report_lines) + "\n"
    if args.output:
        args.output.write_text(report)
    else:
        print(report, end="")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
