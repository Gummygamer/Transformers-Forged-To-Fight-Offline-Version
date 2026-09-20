#!/usr/bin/env python3
"""Reproducible, local-only managed-client recovery and compilation diagnostics."""
import argparse
from collections import Counter
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import uuid
import zipfile
import xml.etree.ElementTree as ET

REPO = Path(__file__).resolve().parents[1]
MANAGED = "assets/bin/Data/Managed/"


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def save(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n")


def assembly_entries(archive):
    entries = []
    seen = set()
    for info in archive.infolist():
        if not info.filename.startswith(MANAGED) or not info.filename.endswith(".dll"):
            continue
        name = info.filename[len(MANAGED):]
        if not re.fullmatch(r"[A-Za-z0-9_][A-Za-z0-9_.-]*\.dll", name):
            raise ValueError(f"Unsupported managed assembly path: {info.filename}")
        if name.casefold() in seen:
            raise ValueError(f"Duplicate managed assembly: {name}")
        seen.add(name.casefold())
        entries.append(info)
    if "assembly-csharp.dll" not in seen:
        raise ValueError("No Mono Assembly-CSharp.dll; IL2CPP APKs need the native analysis track")
    return sorted(entries, key=lambda item: item.filename)


def solution(out, projects):
    lines = ["Microsoft Visual Studio Solution File, Format Version 12.00"]
    ids = []
    for project in projects:
        relative = project.relative_to(out).as_posix()
        guid = str(uuid.uuid5(uuid.NAMESPACE_URL, relative)).upper()
        ids.append(guid)
        lines.extend([
            f'Project("{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}") = '
            f'"{project.stem}", "{relative}", "{{{guid}}}"', "EndProject"])
    lines += ["Global", "\tGlobalSection(SolutionConfigurationPlatforms) = preSolution",
              "\t\tDebug|Any CPU = Debug|Any CPU", "\tEndGlobalSection",
              "\tGlobalSection(ProjectConfigurationPlatforms) = postSolution"]
    for guid in ids:
        lines.append(f"\t\t{{{guid}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU")
    lines += ["\tEndGlobalSection", "EndGlobal"]
    (out / "Recovered.sln").write_text("\n".join(lines) + "\n")


def recover(apk, ilspy):
    tool = shutil.which(ilspy)
    if tool is None:
        raise ValueError(f"ILSpy executable not found: {ilspy}")
    version = subprocess.check_output([tool, "--version"], text=True).strip()
    with zipfile.ZipFile(apk) as archive:
        entries = assembly_entries(archive)
        parent = REPO / "build/decompilation"
        parent.mkdir(parents=True, exist_ok=True)
        out = Path(tempfile.mkdtemp(prefix="mono-", dir=parent))
        for folder in ("managed", "source", "logs", "types"):
            (out / folder).mkdir()
        manifest = {"schema": 1, "input": {"path": str(apk), "sha256": digest(apk)},
                    "backend": "mono", "tool": version, "status": "running",
                    "compilation": "not-tested", "assemblies": []}
        save(out / "manifest.json", manifest)
        print(f"Workspace: {out}", flush=True)
        for info in entries:
            name = Path(info.filename).name
            target = out / "managed" / name
            with archive.open(info) as source, target.open("wb") as dest:
                shutil.copyfileobj(source, dest)
            manifest["assemblies"].append({"name": name, "apk_entry": info.filename,
                                            "sha256": digest(target), "status": "pending"})
        save(out / "manifest.json", manifest)
    for row in manifest["assemblies"]:
        name = Path(row["name"]).stem
        assembly = out / "managed" / row["name"]
        print(f"Decompiling {row['name']}...", flush=True)
        with (out / "types" / f"{name}.txt").open("w") as inventory, \
                (out / "logs" / f"{name}.log").open("w") as log:
            listed = subprocess.run([tool, "--disable-updatecheck", "-l", "c,i,s,d,e",
                                     str(assembly)], stdout=inventory, stderr=log)
            result = subprocess.run([tool, "--disable-updatecheck", "-p", "-r",
                                     str(out / "managed"), "-o", str(out / "source" / name),
                                     str(assembly)], stdout=log, stderr=log)
        projects = sorted((out / "source" / name).glob("*.csproj"))
        row.update(status="exported" if result.returncode == 0 and projects else "failed",
                   exit_code=result.returncode, inventory_exit_code=listed.returncode,
                   source_files=len(list((out / "source" / name).rglob("*.cs"))),
                   projects=[p.relative_to(out).as_posix() for p in projects])
        if listed.returncode:
            row["status"] = "failed"
        save(out / "manifest.json", manifest)
    solution(out, sorted((out / "source").glob("*/*.csproj")))
    manifest["status"] = ("exported" if all(r["status"] == "exported"
                                          for r in manifest["assemblies"]) else "partial")
    save(out / "manifest.json", manifest)
    print(f"{manifest['status']}: {out / 'Recovered.sln'}", flush=True)
    print("Export coverage does not establish compilation or runtime equivalence.")
    return 0 if manifest["status"] == "exported" else 1


def normalize_accessors(source):
    """Repair only standalone explicit getter methods with one simple return.

    This deliberately does not rewrite setters, attributes, complex bodies, or
    other virtual methods. Input is an isolated copy of decompiler output.
    """
    pattern = re.compile(
        r"(?m)^(?P<indent>[ \t]+)virtual (?P<type>[\w.<>]+) "
        r"(?P<interface>[\w.<>]+)\.get_(?P<property>\w+)\(\)\n"
        r"(?P=indent)\{\n(?P=indent)\treturn (?P<value>[\w.]+);\n"
        r"(?P=indent)\}")

    def replace(match):
        g = match.groupdict()
        return (f"{g['indent']}{g['type']} {g['interface']}.{g['property']}\n"
                f"{g['indent']}{{\n{g['indent']}\tget {{ return {g['value']}; }}\n"
                f"{g['indent']}}}")

    return pattern.subn(replace, source)


def normalize_source_contracts(path, source):
    """Apply narrow, IL-backed repairs to declarations rejected by Roslyn.

    ILSpy exposes a few metadata contracts that C# cannot spell directly: a
    type-local ``BindingFlags`` constant, a stripped Unity attribute getter,
    and optional flags on parameters without default constants.  These edits
    are made only in the isolated audit snapshot and are recorded by path.
    """
    changes = []
    if path.name in {"Hash128.cs", "NetworkSceneId.cs"} and "operator ==" in source:
        type_name = "Hash128" if path.name == "Hash128.cs" else "NetworkSceneId"
        if f"operator !=({type_name}" not in source:
            equality = re.search(
                rf"(?ms)(\tpublic static bool operator ==\({type_name} [^{{]+\{{.*?\n\t\}})\n",
                source)
            if equality:
                source = source[:equality.end()] + (
                    f"\n\tpublic static bool operator !=({type_name} left, {type_name} right)\n"
                    "\t{\n"
                    "\t\treturn !(left == right);\n"
                    "\t}\n"
                ) + source[equality.end():]
                changes.append(f"restore {type_name} inequality operator for C# contract")
    if (path.name in {"InputField.cs", "ScrollRect.cs", "Graphic.cs", "Slider.cs",
                      "Toggle.cs", "Scrollbar.cs"}
            and "namespace UnityEngine.UI;" in source):
        source, count = re.subn(r"(?m)^\tvirtual (bool ICanvasElement\.|Transform ICanvasElement\.)",
                                r"\t\1", source)
        if count:
            changes.append(f"remove {count} invalid virtual explicit interface modifiers")
    if path.name == "MethodCall.cs" and "namespace Facebook.Unity;" in source:
        # Original MethodCall<T> metadata retains these private fields and
        # setter-only properties (setters at RVAs 0x65b8 / 0x65c4). ILSpy
        # suppresses the fields as if it had emitted complete auto-properties.
        # Keep the setters intact; adding getters would invent an API.
        for property_name, field_type in (("FacebookImpl", "FacebookBase"),
                                          ("Parameters", "MethodArguments")):
            field = f"_003C{property_name}_003Ek__BackingField"
            declaration = f"private {field_type} {field};"
            setter = (f"\tprotected {field_type} {property_name}\n\t{{\n"
                      f"\t\t[CompilerGenerated]\n\t\tset\n\t\t{{\n"
                      f"\t\t\t{field} = value;\n\t\t}}\n\t}}")
            if source.count(setter) == 1 and declaration not in source:
                source = source.replace(
                    setter, "\t[CompilerGenerated]\n"
                    "\t[System.Diagnostics.DebuggerBrowsable("
                    "System.Diagnostics.DebuggerBrowsableState.Never)]\n"
                    f"\t{declaration}\n\n" + setter, 1)
                changes.append(f"restore private {property_name} backing field from IL")
    if path.as_posix().endswith("EB/DownloadExtractor.cs"):
        old = "using EB.Net;"
        new = old + "\nusing WebRequest = EB.Net.WebRequest;"
        if old in source and "using WebRequest = EB.Net.WebRequest;" not in source:
            source = source.replace(old, new, 1)
            changes.append("alias EB.Net.WebRequest")
    if path.name == "ProcessMemberBinding.cs" and "private const BindingFlags BindingFlags" in source:
        source = source.replace(
            "private const BindingFlags BindingFlags = BindingFlags.Static | BindingFlags.Public;",
            "private const System.Reflection.BindingFlags BindingFlags = "
            "System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public;")
        changes.append("qualify BindingFlags constant")

    if "using EB.Sequence.Runtime;" in source and "using SequenceRuntimeTrigger = EB.Sequence.Runtime.Trigger;" not in source:
        source = source.replace("using EB.Sequence.Runtime;",
                                "using EB.Sequence.Runtime;\nusing SequenceRuntimeTrigger = EB.Sequence.Runtime.Trigger;", 1)
        source = re.sub(r"(?<![\[.])\bTrigger\b", "SequenceRuntimeTrigger", source)
        if path.as_posix().endswith("EB.Sequence/Utils.cs"):
            source = re.sub(r"(enum LinkType\s*\{[^}]*)\bSequenceRuntimeTrigger\b",
                            r"\1Trigger", source, count=1, flags=re.S)
        changes.append("qualify EB.Sequence.Runtime.Trigger")
    if (path.as_posix().startswith("EB.Base/") and "using EB.Sparx;" in source
            and "using SparxBuff = EB.Sparx.Buff;" not in source):
        source = source.replace("using EB.Sparx;",
                                "using EB.Sparx;\nusing SparxBuff = EB.Sparx.Buff;", 1)
        source = re.sub(r"(?<![\[.])\bBuff\b", "SparxBuff", source)
        changes.append("qualify EB.Sparx.Buff")
    if path.name in {"PropertyReference.cs", "NGUIMath.cs", "NGUITools.cs"}:
        source, count = re.subn(r"(?<![.\w])Debug\.", "UnityEngine.Debug.", source)
        if count:
            changes.append(f"qualify {count} UnityEngine.Debug calls")
    if path.name == "BuffValidationLogger.cs":
        source, count = re.subn(r"(?<![.\w])Debug\.", "EB.Debug.", source)
        if count:
            changes.append(f"qualify {count} EB.Debug calls")
    if path.name in {"UIPlayAnimation.cs", "UIPlayTween.cs"} and "using AnimationOrTween;" in source:
        source = source.replace("using AnimationOrTween;",
                                "using AnimationOrTween;\nusing NGUITrigger = AnimationOrTween.Trigger;", 1)
        source = re.sub(r"\bTrigger\b", "NGUITrigger", source)
        source = source.replace("AnimationOrTween.NGUITrigger", "AnimationOrTween.Trigger")
        changes.append("qualify AnimationOrTween.Trigger")
    if path.name == "TcpClientMono.cs":
        source, count = source.replace("leaveStreamOpen: true", "true"), source.count("leaveStreamOpen: true")
        if count:
            changes.append("use positional SslStream leave-open argument")
    if path.name == "GachaManager.cs" and "int num;" in source:
        old = '''\t\t\tdefault:\n\t\t\t{\n\t\t\t\tint num;\n\t\t\t\tif (num == 1)\n\t\t\t\t{\n\t\t\t\t\tresult = GetBox(data);\n\t\t\t\t\tbreak;\n\t\t\t\t}\n\t\t\t\tDebug.LogError("Unknown Gacha CallToAction Type {0}", type);\n\t\t\t\tbreak;\n\t\t\t}\n\t\t\tcase "token":\n\t\t\t\tresult = GetBoxForToken(data);\n\t\t\t\tbreak;'''
        new = '''\t\t\tcase "token":\n\t\t\t\tresult = GetBoxForToken(data);\n\t\t\t\tbreak;\n\t\t\tcase "box":\n\t\t\t\tresult = GetBox(data);\n\t\t\t\tbreak;\n\t\t\tdefault:\n\t\t\t\tEB.Debug.LogError("Unknown Gacha CallToAction Type {0}", type);\n\t\t\t\tbreak;'''
        if old in source:
            source = source.replace(old, new, 1)
            changes.append("restore IL switch cases in GachaManager")
    if path.name == "AvxQuestExpirationManager.cs":
        old = '''\t\tswitch (category)\n\t\t{\n\t\tdefault:\n\t\t{\n\t\t\tint num;\n\t\t\tif (num == 1)\n\t\t\t{\n\t\t\t\tempty = "ID_UI_AVESELECTION_QUEST_EXPIRED_HEADER";\n\t\t\t\tempty2 = "ID_UI_AVESELECTION_QUEST_EXPIRED_SPECTATE_ONLY";\n\t\t\t\tbreak;\n\t\t\t}\n\t\t\tonClose.SafeInvoke();\n\t\t\treturn;\n\t\t}\n\t\tcase "AvA":\n\t\t{\n\t\t\tActiveQuest activeQuest = ((!QuestsManager.Instance.avaPlacementQuests.IsNullOrEmpty()) ? QuestsManager.Instance.avaPlacementQuests[0] : null);\n\t\t\tif (activeQuest != null)\n\t\t\t{\n\t\t\t\tempty = "ID_UI_AVA_PLACEMENT_EXPIRING_SOON_HEADER";\n\t\t\t\tempty2 = "ID_UI_AVA_PLACEMENT_EXPIRES_SOON";\n\t\t\t}\n\t\t\telse\n\t\t\t{\n\t\t\t\tempty = "ID_UI_AVA_QUEST_EXPIRED_HEADER";\n\t\t\t\tempty2 = "ID_UI_AVA_QUEST_EXPIRED_SPECTATE_ONLY";\n\t\t\t}\n\t\t\tbreak;\n\t\t}\n\t\t}'''
        new = '''\t\tswitch (category)\n\t\t{\n\t\tcase "AvA":\n\t\t{\n\t\t\tActiveQuest activeQuest = ((!QuestsManager.Instance.avaPlacementQuests.IsNullOrEmpty()) ? QuestsManager.Instance.avaPlacementQuests[0] : null);\n\t\t\tif (activeQuest != null)\n\t\t\t{\n\t\t\t\tempty = "ID_UI_AVA_PLACEMENT_EXPIRING_SOON_HEADER";\n\t\t\t\tempty2 = "ID_UI_AVA_PLACEMENT_EXPIRES_SOON";\n\t\t\t}\n\t\t\telse\n\t\t\t{\n\t\t\t\tempty = "ID_UI_AVA_QUEST_EXPIRED_HEADER";\n\t\t\t\tempty2 = "ID_UI_AVA_QUEST_EXPIRED_SPECTATE_ONLY";\n\t\t\t}\n\t\t\tbreak;\n\t\t}\n\t\tcase "AvE":\n\t\t\tempty = "ID_UI_AVESELECTION_QUEST_EXPIRED_HEADER";\n\t\t\tempty2 = "ID_UI_AVESELECTION_QUEST_EXPIRED_SPECTATE_ONLY";\n\t\t\tbreak;\n\t\tdefault:\n\t\t\tonClose.SafeInvoke();\n\t\t\treturn;\n\t\t}'''
        if old in source:
            source = source.replace(old, new, 1)
            changes.append("restore AvA/AvE expiry switch cases")
        old = '''\t\tswitch (category)\n\t\t{\n\t\tdefault:\n\t\t{\n\t\t\tint num;\n\t\t\tif (num != 1)\n\t\t\t{\n\t\t\t}\n\t\t\tempty = "ID_UI_AVESELECTION_QUEST_EXPIRING_SOON_HEADER";\n\t\t\tempty2 = "ID_UI_AVESELECTION_QUEST_EXPIRES_SOON";\n\t\t\tbreak;\n\t\t}\n\t\tcase "AvA":\n\t\t{\n\t\t\tActiveQuest activeQuest = ((!QuestsManager.Instance.avaPlacementQuests.IsNullOrEmpty()) ? QuestsManager.Instance.avaPlacementQuests[0] : null);\n\t\t\tif (activeQuest != null)\n\t\t\t{\n\t\t\t\tempty = "ID_UI_AVA_PLACEMENT_EXPIRING_SOON_HEADER";\n\t\t\t\tempty2 = "ID_UI_AVA_PLACEMENT_EXPIRES_SOON";\n\t\t\t}\n\t\t\telse\n\t\t\t{\n\t\t\t\tempty = "ID_UI_AVA_QUEST_EXPIRING_SOON_HEADER";\n\t\t\t\tempty2 = "ID_UI_AVA_QUEST_EXPIRES_SOON";\n\t\t\t}\n\t\t\tbreak;\n\t\t}\n\t\t}'''
        new = '''\t\tswitch (category)\n\t\t{\n\t\tcase "AvA":\n\t\t{\n\t\t\tActiveQuest activeQuest = ((!QuestsManager.Instance.avaPlacementQuests.IsNullOrEmpty()) ? QuestsManager.Instance.avaPlacementQuests[0] : null);\n\t\t\tif (activeQuest != null)\n\t\t\t{\n\t\t\t\tempty = "ID_UI_AVA_PLACEMENT_EXPIRING_SOON_HEADER";\n\t\t\t\tempty2 = "ID_UI_AVA_PLACEMENT_EXPIRES_SOON";\n\t\t\t}\n\t\t\telse\n\t\t\t{\n\t\t\t\tempty = "ID_UI_AVA_QUEST_EXPIRING_SOON_HEADER";\n\t\t\t\tempty2 = "ID_UI_AVA_QUEST_EXPIRES_SOON";\n\t\t\t}\n\t\t\tbreak;\n\t\t}\n\t\tcase "AvE":\n\t\tdefault:\n\t\t\tempty = "ID_UI_AVESELECTION_QUEST_EXPIRING_SOON_HEADER";\n\t\t\tempty2 = "ID_UI_AVESELECTION_QUEST_EXPIRES_SOON";\n\t\t\tbreak;\n\t\t}'''
        if old in source:
            source = source.replace(old, new, 1)
            changes.append("restore AvA/AvE expiring switch cases")
    if path.name == "AllianceMembersState.cs":
        old = '''switch (err)\n\t\t\t\t{\n\t\t\t\tdefault:\n\t\t\t\t{\n\t\t\t\t\tint num;\n\t\t\t\t\tif (num == 1)\n\t\t\t\t\t{\n\t\t\t\t\t\tAllianceUtil.GoToAlliance();\n\t\t\t\t\t}\n\t\t\t\t\tbreak;\n\t\t\t\t}\n\t\t\t\tcase "privateAlliance":\n\t\t\t\tcase "nonJoinableAlliance":\n\t\t\t\tcase "full":\n\t\t\t\t\tbreak;\n\t\t\t\t}'''
        new = '''switch (err)\n\t\t\t\t{\n\t\t\t\tcase "privateAlliance":\n\t\t\t\tcase "nonJoinableAlliance":\n\t\t\t\tcase "full":\n\t\t\t\t\tbreak;\n\t\t\t\tcase "notFound":\n\t\t\t\t\tAllianceUtil.GoToAlliance();\n\t\t\t\t\tbreak;\n\t\t\t\tdefault:\n\t\t\t\t\tbreak;\n\t\t\t\t}'''
        if old in source:
            source = source.replace(old, new, 1)
            changes.append("restore AllianceMembersState error switch cases")
        elif "notFound" not in source and "switch (err)" in source:
            match = re.search(r"(?ms)(?P<indent>\t+)switch \(err\)\n.*?(?=\n\t+\}\)\);)", source)
            if match:
                indent = match.group("indent")
                body_indent = indent + "\t"
                source = source[:match.start()] + (
                    f'{indent}switch (err)\n'
                    f'{body_indent}{{\n'
                    f'{body_indent}case "privateAlliance":\n'
                    f'{body_indent}case "nonJoinableAlliance":\n'
                    f'{body_indent}case "full":\n'
                    f'{body_indent}\tbreak;\n'
                    f'{body_indent}case "notFound":\n'
                    f'{body_indent}\tAllianceUtil.GoToAlliance();\n'
                    f'{body_indent}\tbreak;\n'
                    f'{body_indent}default:\n'
                    f'{body_indent}\tbreak;\n'
                    f'{body_indent}}}'
                ) + source[match.end():]
                changes.append("restore AllianceMembersState error switch cases")
    if path.name == "BattleArbiter.cs":
        old = '''\t\tswitch (base.CurrentStateName)\n\t\t{\n\t\tcase "Init":\n\t\t\treturn;\n\t\t}\n\t\tint num;\n\t\tif (num != 1)\n\t\t{\n\t\t\tExit();\n\t\t}'''
        new = '''\t\tswitch (base.CurrentStateName)\n\t\t{\n\t\tcase "Init":\n\t\t\treturn;\n\t\tcase "Exit":\n\t\t\treturn;\n\t\tdefault:\n\t\t\tExit();\n\t\t\treturn;\n\t\t}'''
        if old in source:
            source = source.replace(old, new, 1)
            changes.append("restore BattleArbiter Disconnect switch cases")
    if path.name == "BuffConditionFactory.cs":
        old = '''\t\tswitch (key)\n\t\t{\n\t\tdefault:\n\t\t{\n\t\t\tint num;\n\t\t\tif (num == 1)\n\t\t\t{\n\t\t\t\tbuffCondition = new ActiveId_BuffCondition(target, rhs, op);\n\t\t\t\tbreak;\n\t\t\t}\n\t\t\tfor (int i = 0; i < _customFactories.Count; i++)\n\t\t\t{\n\t\t\t\tbuffCondition = _customFactories[i].CreateCondition(target, key, op, rhs);\n\t\t\t\tif (buffCondition != null)\n\t\t\t\t{\n\t\t\t\t\tbreak;\n\t\t\t\t}\n\t\t\t}\n\t\t\tif (buffCondition == null)\n\t\t\t{\n\t\t\t\tbuffCondition = new BuffFloatCondition(BuffValueFactory.Instance.CreateFloatValue(lhs), BuffValueFactory.Instance.CreateFloatValue(rhs), op);\n\t\t\t}\n\t\t\tbreak;\n\t\t}\n\t\tcase "status":\n\t\t\tbuffCondition = new ContainsBuff_BuffCondition(target, rhs, op);\n\t\t\tbreak;\n\t\t}'''
        new = '''\t\tswitch (key)\n\t\t{\n\t\tcase "status":\n\t\t\tbuffCondition = new ContainsBuff_BuffCondition(target, rhs, op);\n\t\t\tbreak;\n\t\tcase "activeId":\n\t\t\tbuffCondition = new ActiveId_BuffCondition(target, rhs, op);\n\t\t\tbreak;\n\t\tdefault:\n\t\t\tfor (int i = 0; i < _customFactories.Count; i++)\n\t\t\t{\n\t\t\t\tbuffCondition = _customFactories[i].CreateCondition(target, key, op, rhs);\n\t\t\t\tif (buffCondition != null)\n\t\t\t\t{\n\t\t\t\t\tbreak;\n\t\t\t\t}\n\t\t\t}\n\t\t\tif (buffCondition == null)\n\t\t\t{\n\t\t\t\tbuffCondition = new BuffFloatCondition(BuffValueFactory.Instance.CreateFloatValue(lhs), BuffValueFactory.Instance.CreateFloatValue(rhs), op);\n\t\t\t}\n\t\t\tbreak;\n\t\t}'''
        if old in source:
            source = source.replace(old, new, 1)
            changes.append("restore BuffConditionFactory switch cases")
    if path.name == "CustomStoreScreenPresentation.cs":
        old = '''\t\tswitch (_currentTabId)\n\t\t{\n\t\tdefault:\n\t\t{\n\t\t\tint num;\n\t\t\tif (num == 1)\n\t\t\t{\n\t\t\t\tTransformersMenuBar.Instance.SetSoftCurrencyTab("Raidchips");\n\t\t\t}\n\t\t\telse\n\t\t\t{\n\t\t\t\tTransformersMenuBar.Instance.SetSoftCurrencyTab(string.Empty);\n\t\t\t}\n\t\t\tbreak;\n\t\t}\n\t\tcase "loyalty":\n\t\t\tTransformersMenuBar.Instance.SetSoftCurrencyTab("Loyalty");\n\t\t\tbreak;\n\t\t}'''
        new = '''\t\tswitch (_currentTabId)\n\t\t{\n\t\tcase "loyalty":\n\t\t\tTransformersMenuBar.Instance.SetSoftCurrencyTab("Loyalty");\n\t\t\tbreak;\n\t\tcase "vs":\n\t\t\tTransformersMenuBar.Instance.SetSoftCurrencyTab("Raidchips");\n\t\t\tbreak;\n\t\tdefault:\n\t\t\tTransformersMenuBar.Instance.SetSoftCurrencyTab(string.Empty);\n\t\t\tbreak;\n\t\t}'''
        if old in source:
            source = source.replace(old, new, 1)
            changes.append("restore CustomStoreScreenPresentation switch cases")
    if path.as_posix().endswith("CustomRedeemerMappings/ResourceRedeemerMapping.cs"):
        old = '''\t\tGameObject gameObject = null;\n\t\tint num;\n\t\tgameObject = resourceType switch\n\t\t{\n\t\t\t"sc" => Resources.Load("UI/Misc/SoftCurrencyResourceTrail") as GameObject, \n\t\t\t_ => (num != 1) ? (Resources.Load("UI/Misc/GenericResourceTrail") as GameObject) : (Resources.Load("UI/Misc/HardCurrencyResourceTrail") as GameObject), \n\t\t};'''
        new = '''\t\tGameObject gameObject = null;\n\t\tif (resourceType == "sc")\n\t\t{\n\t\t\tgameObject = Resources.Load("UI/Misc/SoftCurrencyResourceTrail") as GameObject;\n\t\t}\n\t\telse if (resourceType == "hc")\n\t\t{\n\t\t\tgameObject = Resources.Load("UI/Misc/HardCurrencyResourceTrail") as GameObject;\n\t\t}\n\t\telse\n\t\t{\n\t\t\tgameObject = Resources.Load("UI/Misc/GenericResourceTrail") as GameObject;\n\t\t}'''
        if old in source:
            source = source.replace(old, new, 1)
            changes.append("restore ResourceRedeemerMapping resource switch cases")
    if path.name == "FlashSaleItem.cs":
        old = '''\t\tswitch (offer.Type)\n\t\t{\n\t\tdefault:\n\t\t{\n\t\t\tint num3;\n\t\t\tif (num3 == 1)\n\t\t\t{\n\t\t\t\tBCGEvoBlueprintBase evoBlueprint = BCGHelper.GetEvoBlueprint(offer.Data);\n\t\t\t\tif (evoBlueprint != null && BCGHelper.IsCatalyst(evoBlueprint))\n\t\t\t\t{\n\t\t\t\t\tnum = BCGHelper.GetNumBlueprints(evoBlueprint.EvoBlueprint);\n\t\t\t\t\tif (BCGHelper.IsConnected())\n\t\t\t\t\t{\n\t\t\t\t\t\tnum2 = BCGManager.Instance.UserData.GetInventoryMax(evoBlueprint.EvoBlueprint);\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n\t\t\telse\n\t\t\t{\n\t\t\t\tGameStoreItem item = Hub.Instance.GameStoreManager.GetItem(offer.Data);\n\t\t\t\tif (item != null)\n\t\t\t\t{\n\t\t\t\t\tnum = Hub.Instance.InventoryManager.GetCount(item.Name);\n\t\t\t\t\tnum2 = item.InventoryMax;\n\t\t\t\t}\n\t\t\t}\n\t\t\tbreak;\n\t\t}\n\t\tcase "skl":\n\t\tcase "hero":\n\t\tcase "bp":\n\t\tcase "rhero":\n\t\t\tbreak;\n\t\t}'''
        new = '''\t\tswitch (offer.Type)\n\t\t{\n\t\tcase "skl":\n\t\tcase "hero":\n\t\tcase "bp":\n\t\tcase "rhero":\n\t\t\tbreak;\n\t\tcase "ebp":\n\t\tcase "ebpf":\n\t\tcase "autoconvert_ebp":\n\t\tcase "overflow_ebp":\n\t\t{\n\t\t\tBCGEvoBlueprintBase evoBlueprint = BCGHelper.GetEvoBlueprint(offer.Data);\n\t\t\tif (evoBlueprint != null && BCGHelper.IsCatalyst(evoBlueprint))\n\t\t\t{\n\t\t\t\tnum = BCGHelper.GetNumBlueprints(evoBlueprint.EvoBlueprint);\n\t\t\t\tif (BCGHelper.IsConnected())\n\t\t\t\t{\n\t\t\t\t\tnum2 = BCGManager.Instance.UserData.GetInventoryMax(evoBlueprint.EvoBlueprint);\n\t\t\t\t}\n\t\t\t}\n\t\t\tbreak;\n\t\t}\n\t\tdefault:\n\t\t{\n\t\t\tGameStoreItem item = Hub.Instance.GameStoreManager.GetItem(offer.Data);\n\t\t\tif (item != null)\n\t\t\t{\n\t\t\t\tnum = Hub.Instance.InventoryManager.GetCount(item.Name);\n\t\t\t\tnum2 = item.InventoryMax;\n\t\t\t}\n\t\t\tbreak;\n\t\t}\n\t\t}'''
        if old in source:
            source = source.replace(old, new, 1)
            changes.append("restore FlashSaleItem offer switch cases")
    if path.name == "HeroConsumablesPopupPresentation.cs":
        old = '''\t\t\t\tswitch (_mode)\n\t\t\t\t{\n\t\t\t\tdefault:\n\t\t\t\t{\n\t\t\t\t\tint num;\n\t\t\t\t\tif (num != 1)\n\t\t\t\t\t{\n\t\t\t\t\t}\n\t\t\t\t\tif (activeTeam != null && _config.dataProvder.GetCurrentHero() != null)\n\t\t\t\t\t{\n\t\t\t\t\t\tfloat currentHeroHealth = _config.dataProvder.GetCurrentHeroHealth();\n\t\t\t\t\t\tflag = currentHeroHealth > 0f && currentHeroHealth < 1f && gameStoreInventoryItem != null && gameStoreInventoryItem.GetGameStoreItem().IsHeal();\n\t\t\t\t\t\tflag2 = currentHeroHealth == 0f && gameStoreInventoryItem != null && gameStoreInventoryItem.GetGameStoreItem().IsRevive();\n\t\t\t\t\t}\n\t\t\t\t\tbreak;\n\t\t\t\t}\n\t\t\t\tcase "kTeam":\n\t\t\t\t{\n\t\t\t\t\tfor (int i = 0; i < activeTeam.size; i++)\n\t\t\t\t\t{\n\t\t\t\t\t\tfloat hP = activeTeam.GetHP(i);\n\t\t\t\t\t\tflag |= hP > 0f && hP < 1f && gameStoreInventoryItem != null && gameStoreInventoryItem.GetGameStoreItem().IsHeal();\n\t\t\t\t\t\tflag2 |= hP == 0f && gameStoreInventoryItem != null && gameStoreInventoryItem.GetGameStoreItem().IsRevive();\n\t\t\t\t\t}\n\t\t\t\t\tbreak;\n\t\t\t\t}\n\t\t\t\t}'''
        new = '''\t\t\t\tswitch (_mode)\n\t\t\t\t{\n\t\t\t\tcase "kTeam":\n\t\t\t\t{\n\t\t\t\t\tfor (int i = 0; i < activeTeam.size; i++)\n\t\t\t\t\t{\n\t\t\t\t\t\tfloat hP = activeTeam.GetHP(i);\n\t\t\t\t\t\tflag |= hP > 0f && hP < 1f && gameStoreInventoryItem != null && gameStoreInventoryItem.GetGameStoreItem().IsHeal();\n\t\t\t\t\t\tflag2 |= hP == 0f && gameStoreInventoryItem != null && gameStoreInventoryItem.GetGameStoreItem().IsRevive();\n\t\t\t\t\t}\n\t\t\t\t\tbreak;\n\t\t\t\t}\n\t\t\t\tcase "kSingle":\n\t\t\t\tdefault:\n\t\t\t\t\tif (activeTeam != null && _config.dataProvder.GetCurrentHero() != null)\n\t\t\t\t\t{\n\t\t\t\t\t\tfloat currentHeroHealth = _config.dataProvder.GetCurrentHeroHealth();\n\t\t\t\t\t\tflag = currentHeroHealth > 0f && currentHeroHealth < 1f && gameStoreInventoryItem != null && gameStoreInventoryItem.GetGameStoreItem().IsHeal();\n\t\t\t\t\t\tflag2 = currentHeroHealth == 0f && gameStoreInventoryItem != null && gameStoreInventoryItem.GetGameStoreItem().IsRevive();\n\t\t\t\t\t}\n\t\t\t\t\tbreak;\n\t\t\t\t}'''
        if old in source:
            source = source.replace(old, new, 1)
            changes.append("restore HeroConsumablesPopupPresentation mode switch cases")
    if path.as_posix().endswith("Legacy/ActiveQuest.cs"):
        old = '''\t\tswitch (text3)\n\t\t{\n\t\tdefault:\n\t\t{\n\t\t\tint num12;\n\t\t\tif (num12 == 1)\n\t\t\t{\n\t\t\t\tphase = Phase.Defend;\n\t\t\t}\n\t\t\telse\n\t\t\t{\n\t\t\t\tphase = Phase.Attack;\n\t\t\t}\n\t\t\tbreak;\n\t\t}\n\t\tcase "placement":\n\t\t\tphase = Phase.Placement;\n\t\t\tbreak;\n\t\t}'''
        new = '''\t\tswitch (text3)\n\t\t{\n\t\tcase "placement":\n\t\t\tphase = Phase.Placement;\n\t\t\tbreak;\n\t\tcase "defend":\n\t\t\tphase = Phase.Defend;\n\t\t\tbreak;\n\t\tdefault:\n\t\t\tphase = Phase.Attack;\n\t\t\tbreak;\n\t\t}'''
        if old in source:
            source = source.replace(old, new, 1)
            changes.append("restore ActiveQuest phase switch cases")
    if path.as_posix().endswith("EB.UI.Gacha/GachaPurchasePresentation.cs"):
        old = '''\t\tswitch (tabId)\n\t\t{\n\t\tdefault:\n\t\t{\n\t\t\tint num;\n\t\t\tif (num == 1)\n\t\t\t{\n\t\t\t\t_currentTab = GachaTab.SPECIAL;\n\t\t\t}\n\t\t\telse\n\t\t\t{\n\t\t\t\t_currentTab = GachaTab.CRYSTAL;\n\t\t\t}\n\t\t\tbreak;\n\t\t}\n\t\tcase "shards":\n\t\t\t_currentTab = GachaTab.SHARDS;\n\t\t\tbreak;\n\t\t}'''
        new = '''\t\tswitch (tabId)\n\t\t{\n\t\tcase "shards":\n\t\t\t_currentTab = GachaTab.SHARDS;\n\t\t\tbreak;\n\t\tcase "special":\n\t\t\t_currentTab = GachaTab.SPECIAL;\n\t\t\tbreak;\n\t\tdefault:\n\t\t\t_currentTab = GachaTab.CRYSTAL;\n\t\t\tbreak;\n\t\t}'''
        if old in source:
            source = source.replace(old, new, 1)
            changes.append("restore GachaPurchasePresentation tab switch cases")
    if path.name == "StateMachine.cs" and "AddState(new T" in source:
        source = source.replace(
            "AddState(new T\n\t\t{\n\t\t\tStateName = stateName\n\t\t});",
            "State state = System.Activator.CreateInstance<T>();\n"
            "\t\tstate.StateName = stateName;\n\t\tAddState(state);", 1)
        changes.append("restore Activator.CreateInstance from IL")
    if path.name == "MOTDWidget.cs" and "new EB.Action<TransformersMOTDItem, MOTDTemplate>(HandleMOTDClick)" in source:
        source = source.replace(
            "new EB.Action<TransformersMOTDItem, MOTDTemplate>(HandleMOTDClick)",
            "new EB.Action<TransformersMOTDItem, MOTDTemplate>((item, data) => "
            "HandleMOTDClick(item, data, null))", 1)
        changes.append("adapt optional MOTD callback to two-argument delegate")
    if path.name == "HeroesScreen.cs" and "delegate(Building building)" in source:
        source = source.replace("delegate(Building building)", "delegate(Building callbackBuilding)", 1)
        source = source.replace("\t\t\tif (building != null)", "\t\t\tif (callbackBuilding != null)", 1)
        source = source.replace("BuildingDetailsScreen.Show(building, list);", "BuildingDetailsScreen.Show(callbackBuilding, list);", 1)
        changes.append("rename shadowed HeroesScreen callback parameter")

    # The original methods mark the later value as Optional in metadata.  The
    # decompiler prints that flag without a C# default, which makes all prior
    # defaults illegal and makes callers that omit the value fail.  A default
    # value is a source-only spelling of the same metadata contract.
    optional_value = re.compile(
        r"\[Optional\]\s+(?P<type>[^,\s]+(?:<[^>]+>)?(?:\[\])?)\s+(?P<name>\w+)"
        r"(?=\s*[,\)])")
    source, count = optional_value.subn(
        lambda m: f"{m.group('type')} {m.group('name')} = default", source)
    if count:
        changes.append(f"add {count} defaults for metadata-optional parameters")

    # Unity's stripped reference exposes setter-only attribute properties. A
    # named attribute argument is therefore not a legal C# declaration even
    # though the setter is present in the IL. Keep the attribute itself while
    # omitting only the unverifiable named argument in the compile snapshot.
    source, count = re.subn(r"\[CreateAssetMenu\(menuName\s*=\s*\"[^\"]*\"\)\]",
                            "[CreateAssetMenu]", source)
    if count:
        changes.append(f"remove {count} stripped CreateAssetMenu named arguments")
    source, count = re.subn(r"\[Header\((\"[^\"]*\"),\s*order\s*=\s*\d+\)\]",
                            r"[Header(\1)]", source)
    if count:
        changes.append(f"remove {count} stripped Header named arguments")
    return source, changes


def project_references(workspace, name):
    """Return assembly names referenced by an exported project file."""
    project = workspace / "source" / name / f"{name}.csproj"
    if not project.is_file():
        return set()
    try:
        root = ET.fromstring(project.read_text())
    except ET.ParseError:
        return set()
    refs = set()
    for item in root.findall(".//Reference"):
        include = item.attrib.get("Include", "").split(",", 1)[0]
        if include:
            refs.add(include)
    return refs


def dependency_order(workspace, names):
    """Topologically order selected projects using ILSpy project references."""
    selected = set(names)
    visiting = set()
    visited = set()
    ordered = []

    def visit(name):
        if name in visited:
            return
        if name in visiting:
            raise ValueError(f"Circular selected assembly dependency involving {name}")
        visiting.add(name)
        for dependency in sorted(project_references(workspace, name) & selected):
            visit(dependency)
        visiting.remove(name)
        visited.add(name)
        ordered.append(name)

    for name in names:
        visit(name)
    return ordered


def compile_audit(workspace, dotnet, csc, reference_dirs=(), assemblies=None,
                  repair_accessors=False, repair_contracts=False):
    """Compile recovered game code against the APK's own framework, without NuGet."""
    manifest = json.loads((workspace / "manifest.json").read_text())
    # Verify input provenance before using assemblies as compiler references.
    for row in manifest["assemblies"]:
        if digest(workspace / "managed" / row["name"]) != row["sha256"]:
            raise ValueError(f"Assembly changed since export: {row['name']}")
    names = assemblies or ["Assembly-CSharp-firstpass", "Assembly-CSharp"]
    available = {Path(row["name"]).stem for row in manifest["assemblies"]}
    if not names or any(name not in available for name in names):
        raise ValueError("Select assembly names present in the export manifest")
    names = dependency_order(workspace, names)
    references = {p.name: p for p in (workspace / "managed").glob("*.dll")}
    overrides = {}
    for directory in reference_dirs:
        dlls = sorted(directory.resolve().glob("*.dll"))
        if not dlls:
            raise ValueError(f"No reference assemblies in {directory}")
        for path in dlls:
            if path.name in references:
                references[path.name] = path
                overrides[path.name] = {"path": str(path), "sha256": digest(path)}
    audit = Path(tempfile.mkdtemp(prefix="compile-", dir=workspace))
    output_dir = audit / "bin"
    output_dir.mkdir()
    report = {"scope": "game assemblies against original APK references; not a Unity build",
              "compiler": str(csc), "compiler_sha256": digest(csc),
              "reference_overrides": overrides, "runtime_verified": False, "assemblies": []}
    compiled_refs = {}
    for name in names:
        sources = sorted((workspace / "source" / name).rglob("*.cs"))
        if not sources:
            report["assemblies"].append({"name": name, "status": "missing-source"})
            continue
        source_hashes = {p.relative_to(workspace).as_posix(): digest(p) for p in sources}
        changes = []
        snapshot = audit / "source" / name
        for path in sources:
            relative = path.relative_to(workspace / "source" / name)
            target = snapshot / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            text = path.read_text()
            if repair_accessors:
                text, count = normalize_accessors(text)
                if count:
                    changes.append({"path": relative.as_posix(), "explicit_getters": count})
            if repair_contracts:
                text, contract_changes = normalize_source_contracts(relative, text)
                if contract_changes:
                    changes.append({"path": relative.as_posix(), "contracts": contract_changes})
            target.write_text(text)
        sources = sorted(snapshot.rglob("*.cs"))
        flags = ["-nologo", "-target:library", "-unsafe", "-nostdlib+", "-langversion:12",
                 "-deterministic+", f'-pathmap:"{audit}=/_/reconstruction"',
                 f'-out:"{audit / (name + ".dll")}"']
        reference_paths = []
        for p in sorted(references.values()):
            if p.stem == name:
                continue
            reference_paths.append(compiled_refs.get(p.stem, p))
        flags += [f'-reference:"{p}"' for p in reference_paths]
        flags += [f'"{p}"' for p in sources]
        output_path = output_dir / f"{name}.dll"
        flags[7] = f'-out:"{output_path}"'
        rsp = audit / f"{name}.rsp"
        rsp.write_text("\n".join(flags) + "\n")
        log = audit / f"{name}.log"
        with log.open("w") as output:
            result = subprocess.run([dotnet, str(csc), f"@{rsp}"], stdout=output,
                                    stderr=subprocess.STDOUT)
        errors = Counter(re.findall(r"\berror (CS\d+):", log.read_text()))
        report["assemblies"].append({"name": name, "exit_code": result.returncode,
                                     "status": "compiled" if result.returncode == 0 else "failed",
                                     "errors": dict(sorted(errors.items())),
                                     "source_hashes": source_hashes,
                                     "compiled_source_hashes": {
                                         p.relative_to(audit).as_posix(): digest(p) for p in sources},
                                     "repairs": changes,
                                     "reference_paths": [str(p) for p in reference_paths],
                                     "output_sha256": digest(output_path)
                                     if result.returncode == 0 else None,
                                     "output_path": output_path.relative_to(audit).as_posix()})
        if result.returncode == 0:
            compiled_refs[name] = output_path
    save(audit / "report.json", report)
    success = all(r["status"] == "compiled" for r in report["assemblies"])
    manifest["compilation"] = "selected-assemblies-compiled" if success else "failed"
    manifest["latest_compile_report"] = (audit / "report.json").relative_to(workspace).as_posix()
    save(workspace / "manifest.json", manifest)
    print(json.dumps([{k: v for k, v in row.items()
                       if k not in ("source_hashes", "compiled_source_hashes")}
                      for row in report["assemblies"]], indent=2))
    print(f"Report: {audit / 'report.json'}")
    return 0 if success else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    export = commands.add_parser("export", help="Recover every managed DLL into an isolated workspace")
    export.add_argument("apk", type=Path)
    export.add_argument("--ilspy", default=os.environ.get("ILSPYCMD", "ilspycmd"))
    audit = commands.add_parser("compile-audit", help="Measure game-source compiler blockers")
    audit.add_argument("workspace", type=Path)
    audit.add_argument("--dotnet", default="dotnet")
    audit.add_argument("--csc", required=True, type=Path, help="SDK Roslyn/bincore/csc.dll")
    audit.add_argument("--reference-dir", action="append", default=[], type=Path,
                       help="Override matching APK references with unstripped SDK DLLs (repeatable)")
    audit.add_argument("--assembly", action="append", help="Assembly name without .dll (repeatable)")
    audit.add_argument("--repair-accessors", action="store_true",
                       help="Normalize simple explicit getters in a local source snapshot")
    audit.add_argument("--repair-contracts", action="store_true",
                       help="Apply IL-backed compiler-contract repairs in a local source snapshot")
    args = parser.parse_args()
    try:
        if args.command == "export":
            return recover(args.apk.resolve(), args.ilspy)
        return compile_audit(args.workspace.resolve(), args.dotnet, args.csc.resolve(),
                             args.reference_dir, args.assembly, args.repair_accessors,
                             args.repair_contracts)
    except (OSError, ValueError, zipfile.BadZipFile, subprocess.CalledProcessError) as error:
        parser.exit(1, f"Error: {error}\n")


if __name__ == "__main__":
    raise SystemExit(main())
