#!/usr/bin/env python3
"""Port decompiled C# output to Unity 2020's C# 8 and runtime behavior.

This edits decompiled C# sources in place. It only rewrites simple property
patterns (`is T { Field: value }`) and nested non-null property patterns used
by this game's source. It also fixes a non-reflexive asset-cache comparator
that newer Unity/.NET sorting detects when the memory cap triggers unloading.
Ordinary C# 7 type patterns remain unchanged.
"""

from __future__ import annotations

import argparse
import re
from pathlib import Path


PATTERN_START = re.compile(
    r"(?P<expr>[A-Za-z_]\w*(?:(?:\.[A-Za-z_]\w*)|(?:\([^()\n]*\))|(?:\[[^\]\n]*\]))*)\s+is\s+"
    r"(?P<type>[A-Z][\w.]*)\s*\{"
)


def matching_brace(text: str, start: int) -> int:
    depth = 0
    quoted = False
    escaped = False
    for i in range(start, len(text)):
        ch = text[i]
        if quoted:
            if escaped:
                escaped = False
            elif ch == "\\":
                escaped = True
            elif ch == '"':
                quoted = False
            continue
        if ch == '"':
            quoted = True
        elif ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth == 0:
                return i
    raise ValueError("unterminated property pattern")


def split_properties(body: str) -> list[str]:
    parts: list[str] = []
    start = depth = 0
    quoted = escaped = False
    for i, ch in enumerate(body):
        if quoted:
            if escaped:
                escaped = False
            elif ch == "\\":
                escaped = True
            elif ch == '"':
                quoted = False
            continue
        if ch == '"':
            quoted = True
        elif ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
        elif ch == "," and depth == 0:
            parts.append(body[start:i].strip())
            start = i + 1
    tail = body[start:].strip()
    if tail:
        parts.append(tail)
    return parts


def lower_properties(receiver: str, body: str, counter: list[int]) -> list[str]:
    conditions: list[str] = []
    for item in split_properties(body):
        pair = re.match(r"^([A-Za-z_]\w*)\s*:\s*(.+)$", item, re.S)
        if not pair:
            raise ValueError(f"unsupported property pattern member: {item}")
        prop, value = pair.group(1), pair.group(2).strip()
        member = f"{receiver}.{prop}"
        if value.startswith("{"):
            brace_end = matching_brace(value, 0)
            nested = value[1:brace_end].strip()
            tail = value[brace_end + 1 :].strip()
            if nested or tail:
                counter[0] += 1
                local = tail or f"__u2020_{counter[0]}"
                conditions.append(f"({member} is var {local} && {local} != null)")
                if nested:
                    conditions.extend(lower_properties(local, nested, counter))
            continue
        if value.startswith("not "):
            conditions.append(f"{member} != {value[4:].strip()}")
        elif re.match(r"^(?:>=|<=|>|<)\s*", value):
            conditions.append(f"{member} {value}")
        elif value.startswith("var "):
            conditions.append(f"{member} is var {value[4:].strip()}")
        else:
            conditions.append(f"{member} == {value}")
    return conditions


def rewrite_line(line: str, counter: list[int]) -> tuple[str, int]:
    changed = 0
    while True:
        match = PATTERN_START.search(line)
        if not match:
            return line, changed
        brace_open = match.end() - 1
        brace_close = matching_brace(line, brace_open)
        body = line[brace_open + 1 : brace_close]
        after = line[brace_close + 1 :]
        designation = re.match(r"\s+([A-Za-z_]\w*)\b", after)
        counter[0] += 1
        variable = designation.group(1) if designation else f"__u2020_{counter[0]}"
        conditions = lower_properties(variable, body, counter)
        base = f"({match.group('expr')} is {match.group('type')} {variable})"
        replacement = base
        if conditions:
            replacement = "(" + base + " && " + " && ".join(conditions) + ")"
        end = brace_close + 1
        if designation:
            end += designation.end()
        line = line[: match.start()] + replacement + line[end:]
        changed += 1


# Replacement bodies below are written for this project. Methods are located by
# their signature only, so no decompiled method body is stored in this file.
ASSET_COMPARE_BODY = """LoadedObject other = (LoadedObject)o;
// Protected (instanced or held) entries sort first; the rest by priority,
// then age, then path, so the comparison is reflexive and total.
bool thisProtected = Instances.Count > 0 || Hold > 0;
bool otherProtected = other.Instances.Count > 0 || other.Hold > 0;
if (thisProtected != otherProtected)
{
\treturn thisProtected ? -1 : 1;
}
int byPriority = Priority.CompareTo(other.Priority);
if (byPriority != 0)
{
\treturn byPriority;
}
int byAge = Age.CompareTo(other.Age);
return byAge != 0 ? byAge : string.CompareOrdinal(Path, other.Path);"""

CONTACT_SHADOW_GUARD = """// The contact-shadow shader is not part of the recompiled project.
if (Shader.Find("EB/Misc/ContactShadow") == null)
{
\treturn;
}"""

FALLBACK_TUNING_METHOD = """\tprivate QuestNodeTuning CreateFallbackNodeTuning(string objectName)
\t{
\t\tGameObject holder = new GameObject(objectName);
\t\tholder.transform.SetParent(base.transform, false);
\t\tQuestNodeTuning tuning = holder.AddComponent<QuestNodeTuning>();
\t\tAnimationCurve linear = AnimationCurve.Linear(0f, 0f, 1f, 1f);
\t\ttuning.ModPlacedScaleCurve = linear;
\t\ttuning.ModRemovedScaleCurve = linear;
\t\ttuning.RelicPlacedScaleCurve = linear;
\t\ttuning.RelicRemovedScaleCurve = linear;
\t\ttuning.RelicPedestalPlacedScaleCurve = linear;
\t\ttuning.RelicPedestalRemovedScaleCurve = linear;
\t\treturn tuning;
\t}

"""


def tuning_body(field: str, label: str) -> str:
    return f"""{field} = (go != null) ? Util.FindComponent<QuestNodeTuning>(go) : null;
if ({field} == null)
{{
\tEB.Debug.LogWarning("{label} asset is unavailable; using compatibility defaults.");
\t{field} = CreateFallbackNodeTuning("{label}Fallback");
}}
{field}Ready = true;"""


SIMPLE_SCREEN_LOAD_TEXTURE_BODY = """ReleaseTexture(textureName);
if (texture != null)
{
\ttexture.mainTexture = null;
\ttexture.enabled = false;
}
TexturePoolManager.Instance.LoadTexture(assetPath, this, delegate(Texture2D loaded)
{
\tif (texture != null)
\t{
\t\ttexture.mainTexture = loaded;
\t\ttexture.enabled = loaded != null;
\t}
\tif (loaded == null)
\t{
\t\treturn;
\t}
\tif (texture == _primaryImageTexture)
\t{
\t\t_primaryTextureName = loaded.name;
\t}
\telse if (texture == _secondaryImageTexture)
\t{
\t\t_secondaryTextureName = loaded.name;
\t}
});
onComplete?.Invoke();
yield break;"""


UNLOCKED_SHADER_FALLBACK = """if (shader == null)
{
\tshader = Shader.Find("Unlit/Transparent Colored") ?? Shader.Find("Unlit/Transparent") ?? Shader.Find("Hidden/EB/Blit");
}"""


def insert_after_signature_line(text: str, anchor: str, statements: str) -> str:
    """Insert statements after the single line containing anchor, at its indentation."""
    start = text.find(anchor)
    if start < 0:
        raise ValueError(f"anchor not found: {anchor}")
    line_start = text.rfind("\n", 0, start) + 1
    line_end = text.find("\n", start)
    indent = text[line_start:start]
    block = "".join(indent + line + "\n" for line in statements.splitlines())
    return text[: line_end + 1] + block + text[line_end + 1 :]


def _body_span(text: str, signature: str) -> tuple[int, int]:
    """Return the span between a method's opening and closing braces."""
    start = text.find(signature)
    if start < 0:
        raise ValueError(f"method not found: {signature}")
    open_brace = text.index("{", start + len(signature))
    depth = 0
    for index in range(open_brace, len(text)):
        if text[index] == "{":
            depth += 1
        elif text[index] == "}":
            depth -= 1
            if depth == 0:
                return open_brace, index
    raise ValueError(f"unbalanced method body: {signature}")


def _indent(body: str, indent: str) -> str:
    return "".join(indent + "\t" + line if line else line for line in body.splitlines(keepends=True))


def replace_method_body(text: str, signature: str, body: str, indent: str) -> str:
    open_brace, close_brace = _body_span(text, signature)
    return text[: open_brace + 1] + "\n" + _indent(body, indent) + "\n" + indent + text[close_brace:]


def insert_after_signature(text: str, signature: str, statements: str, indent: str) -> str:
    if statements.splitlines()[1] in text:
        return text
    open_brace, _ = _body_span(text, signature)
    return text[: open_brace + 1] + "\n" + _indent(statements, indent) + text[open_brace + 1 :]


def lower_tree(root: Path) -> int:
    changed_files = 0
    for path in sorted(root.rglob("*.cs")):
        original = path.read_text(encoding="utf-8-sig")
        text = original
        counter = [0]
        lines = []
        substitutions = 0
        for number, line in enumerate(text.splitlines(keepends=True), 1):
            try:
                rewritten, count = rewrite_line(line, counter)
            except ValueError as error:
                raise ValueError(f"{path}:{number}: {error}") from error
            lines.append(rewritten)
            substitutions += count
        text = "".join(lines)
        relative = path.relative_to(root).as_posix()
        if relative == "PlayerController.cs":
            text = text.replace("AnimatorUpdateMode.Fixed", "AnimatorUpdateMode.AnimatePhysics")
        elif relative == "ReplicatedSequence.cs":
            text = text.replace("#if !UNITY_6000_0_OR_NEWER", "#if !UNITY_2018_2_OR_NEWER")
            text = text.replace("#if UNITY_6000_0_OR_NEWER", "#if UNITY_2018_2_OR_NEWER")
        elif relative == "EB/AssetManager.cs":
            text = replace_method_body(text, "public int CompareTo(object o)", ASSET_COMPARE_BODY, indent="\t\t")
        elif relative == "ContactShadow.cs":
            text = insert_after_signature(text, "public void ShowShadows()", CONTACT_SHADOW_GUARD, indent="\t")
        elif relative == "Quests.Presentation/GameboardManager.cs":
            text = replace_method_body(text, "private void OnQuestTuningLoaded(GameObject go)", tuning_body("_questNodeTuning", "QuestNodeTuning"), indent="\t")
            text = replace_method_body(text, "private void OnBaseTuningLoaded(GameObject go)", tuning_body("_baseNodeTuning", "BaseNodeTuning"), indent="\t")
            if "CreateFallbackNodeTuning(" not in text.split("private void OnQuestTuningLoaded", 1)[0]:
                text = text.replace("\tprivate void OnQuestTuningLoaded(GameObject go)", FALLBACK_TUNING_METHOD + "\tprivate void OnQuestTuningLoaded(GameObject go)", 1)
        elif relative == "SimpleScreen.cs":
            text = replace_method_body(
                text,
                "private IEnumerator LoadTexture(string assetPath, UITexture texture, string textureName, completeCallback onComplete)",
                SIMPLE_SCREEN_LOAD_TEXTURE_BODY,
                indent="\t",
            )
        elif relative == "EnumComparer.cs":
            text = '''using System.Collections.Generic;

public class EnumComparer<T> : IEqualityComparer<T> where T : struct
{
    public bool Equals(T x, T y)
    {
        return EqualityComparer<T>.Default.Equals(x, y);
    }

    public int GetHashCode(T value)
    {
        return EqualityComparer<T>.Default.GetHashCode(value);
    }
}
'''
        elif relative == "LevelLockFightLandingWidget.cs" and "shader != null)" not in text:
            # Converted 9.2 prefabs may not carry the unlocked shader reference:
            # fall back to a built-in shader and skip the swap if none exists.
            text = insert_after_signature_line(
                text, "Shader shader = ((!(_prevShader != null)) ? UnlockedShader : _prevShader);", UNLOCKED_SHADER_FALLBACK)
            text = text.replace("if (MainTextures != null && MainTextures.Length > 0)",
                                "if (MainTextures != null && MainTextures.Length > 0 && shader != null)", 1)
        if text != original:
            path.write_text(text, encoding="utf-8")
            if substitutions:
                print(f"{path}: lowered {substitutions} property pattern(s)")
            elif relative == "EB/AssetManager.cs":
                print(f"{path}: made asset eviction ordering reflexive and deterministic")
            elif relative == "ContactShadow.cs":
                print(f"{path}: skip contact shadows when their shader is unavailable")
            elif relative == "Quests.Presentation/GameboardManager.cs":
                print(f"{path}: provide node-tuning defaults when legacy tuning prefabs are absent")
            elif relative == "SimpleScreen.cs":
                print(f"{path}: restore texture assignment in the decompiled async callback")
            elif relative in {"PlayerController.cs", "ReplicatedSequence.cs"}:
                print(f"{path}: adjusted Unity 6 compatibility branch for Unity 2020")
            changed_files += 1
    return changed_files


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="directory containing decompiled C# sources")
    args = parser.parse_args()
    if not args.source.is_dir():
        parser.error(f"source directory does not exist: {args.source}")
    print(f"Unity 2020 C# compatibility: {lower_tree(args.source)} file(s) changed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
