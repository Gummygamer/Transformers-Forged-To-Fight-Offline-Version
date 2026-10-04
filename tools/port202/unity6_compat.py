#!/usr/bin/env python3
"""Apply narrow Unity 6 compatibility repairs to decompiled 2.0.2 C# sources.

The source is generated from the original Mono assemblies and remains under
build/. This script records the authored compatibility layer separately so a
fresh decompile can be prepared reproducibly without editing or committing the
generated game sources.
"""

from __future__ import annotations

import argparse
import re
from pathlib import Path


def _read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def _write(path: Path, text: str) -> None:
    path.write_text(text, encoding="utf-8")


def modernize(source: Path) -> int:
    """Patch one Assembly-CSharp source directory. Safe to run more than once."""
    files = sorted(source.rglob("*.cs"))
    changed = 0
    for path in files:
        before = _read(path)
        relative = path.relative_to(source).as_posix()
        namespace = re.search(r"(?m)^namespace\s+([^;\n]+);\s*$", before)
        text = before
        if relative == "-PrivateImplementationDetails-.cs":
            # ILSpy emits this unused compiler blob table, but cannot faithfully
            # represent its embedded data as C# source.
            text = "// Unused decompiler-generated implementation detail omitted.\n"
        if relative == "Fabric/ParameterData.cs":
            text = text.replace(
                "namespace Fabric\n{\n\t[UnityEngine.SerializeField]\n\tpublic class ParameterData",
                "namespace Fabric\n{\n\tpublic class ParameterData",
            )
        if namespace:
            text = (before[:namespace.start()] + f"namespace {namespace.group(1)}\n{{"
                    + before[namespace.end():].rstrip() + "\n}\n")
        text = text.replace(
            "using UnityEngine.Experimental.Networking;",
            "using UnityEngine.Networking;",
        )
        if relative != "EB.Collections/Tuple.cs":
            text = re.sub(r"(?<![\w.])Tuple<", "EB.Collections.Tuple<", text)
        if relative == "PerformanceManager.cs" and "using TrailRenderer = " not in text:
            match = re.search(r"(?m)^(?:using [^\n]+;\n)+", text)
            if match:
                text = text[:match.end()] + "using TrailRenderer = EB.Rendering.TrailRenderer;\n" + text[match.end():]

        if path.name in {
            "MatineePlayCharacterAudioEvent.cs",
            "MatineePlayFabricEvent.cs",
            "MatineePlayFabricLvl3Event.cs",
        }:
            head = text[: text.find("public class")]
            text = head.replace("[SerializeField]\n", "", 1) + text[len(head):]

        if path.name == "TutorialBranch.cs":
            text = text.replace("using System.Runtime.Remoting;\n", "")
            text = text.replace(
                "ObjectHandle objectHandle = Activator.CreateInstance(null, StateName);\n"
                "\t\tState state = (State)objectHandle.Unwrap();",
                "State state = (State)Activator.CreateInstance("
                "typeof(TutorialBranch).Assembly.GetType(StateName, throwOnError: true));",
            )

        if relative in {"BadgePriorityQueue.cs", "BT/BehaviorTree.cs", "EB.MoveEditor/MoveEvent.cs"}:
            text = text.replace("GetInstanceID()", "GetHashCode()")
        if relative == "PlayerController.cs":
            text = text.replace("AnimatorUpdateMode.AnimatePhysics", "AnimatorUpdateMode.Fixed")
        if relative == "Quests.Presentation/GameboardBuilder.cs":
            text = text.replace("UploadMeshData(markNoLogerReadable: ", "UploadMeshData(")
        if relative == "BuffTriggerParams.cs":
            text = text.replace(
                "public BuffTriggerParams(object param)\n\t{\n\t\tParam = param;",
                "public BuffTriggerParams(object param)\n\t{\n\t\tBuff = null;\n\t\tParam = param;",
            )
            text = text.replace(
                "public BuffTriggerParams(Buff buff)\n\t{\n\t\tBuff = buff;",
                "public BuffTriggerParams(Buff buff)\n\t{\n\t\tParam = null;\n\t\tBuff = buff;",
            )
        if relative == "BadgeManager.cs":
            # Move the Tuning-dependent field initializer into Start().
            text = re.sub(r"private string\[\] tokens = new string\[3\]\s*\{[^}]*\};",
                          "private string[] tokens = new string[3];", text, count=1)
            text = text.replace(
                "private void Start()\n\t{\n\t\tnewBadgeLocString =",
                "private void Start()\n\t{\n"
                "\t\ttokens = new string[] { Tuning.Instance.BaseCrystal01, "
                "Tuning.Instance.BaseCrystal02, Tuning.Instance.BaseCrystal03 };\n"
                "\t\tnewBadgeLocString =",
            )
        if relative == "CustomRedeemerDisplays/HeroRedeemerDisplay.cs":
            text = text.replace(
                "private int _premiumRarityThreshold = Tuning.Instance.PremiumItemRarity;",
                "private int PremiumRarityThreshold => Tuning.Instance != null "
                "? Tuning.Instance.PremiumItemRarity : 0;",
            ).replace("_heroData.blueprint.Rarity >= _premiumRarityThreshold",
                      "_heroData.blueprint.Rarity >= PremiumRarityThreshold")
        if relative == "TutorialAreaIndicator.cs":
            text = text.replace(
                "private WindowManager.WindowLayer baseLayer = RobotsWindowLayer.Social;",
                "private WindowManager.WindowLayer baseLayer;",
            )
            text = text.replace(
                "protected override void Awake()\n\t{\n\t\tbase.Awake();",
                "protected override void Awake()\n\t{\n\t\tbase.Awake();\n"
                "\t\tbaseLayer = RobotsWindowLayer.Social;",
            )
        if relative == "EB.MoveEditor/MoveEvent.cs":
            text = text.replace(
                "return MemberwiseClone() as MoveEvent;",
                "return UnityEngine.Object.Instantiate(this);",
            )
        if relative == "Fabric/AudioComponent.cs":
            text = text.replace("_003Cwww_003E__1.audioClip", "_003Cwww_003E__1.GetAudioClip()")
        if relative == "Fabric/DynamicMixer.cs":
            text = text.replace("groupComponent.GetInstanceID()", "groupComponent.GetHashCode()")
        if relative == "Fabric/Preset.cs":
            text = text.replace(".GroupComponent.GetInstanceID()", ".GroupComponent.GetHashCode()")
        if relative == "Fabric/FabricManager.cs":
            text = text.replace("UnityEngine.Profiler.usedHeapSize", "0u")
        if relative == "Fabric/SamplePlayerComponent.cs":
            text = text.replace(
                "public float[] _channelGains;",
                "public float[] _channelGains = new float[] { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f };",
            )
            text = re.sub(
                r"\n\t\tpublic SamplePlayerComponent\(\)\n\t\t\{.*?\n\t\t\}\n",
                "\n",
                text,
                count=1,
                flags=re.S,
            )
        if relative == "ReplicatedSequence.cs":
            if "#if !UNITY_6000_0_OR_NEWER" not in text:
                text = text.replace(
                    "\t\t_view.stateSynchronization = NetworkStateSynchronization.ReliableDeltaCompressed;\n",
                    "#if !UNITY_6000_0_OR_NEWER\n"
                    "\t\t_view.stateSynchronization = NetworkStateSynchronization.ReliableDeltaCompressed;\n"
                    "#endif\n",
                )
            if "#if UNITY_6000_0_OR_NEWER" not in text:
                text = text.replace(
                    "\t\tuint localPlayerId = Manager.LocalPlayerId;\n"
                    "\t\tManager.RPC(\"Activate\", RPCMode.Server, localPlayerId, target, eventType.Name);",
                    "#if UNITY_6000_0_OR_NEWER\n"
                    "\t\tEB.Sequence.Component.Activate(null, target, eventType);\n"
                    "#else\n"
                    "\t\tuint localPlayerId = Manager.LocalPlayerId;\n"
                    "\t\tManager.RPC(\"Activate\", RPCMode.Server, localPlayerId, target, eventType.Name);\n"
                    "#endif",
                )

        if text != before:
            _write(path, text)
            changed += 1
    return changed


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="path to generated Assembly-CSharp sources")
    args = parser.parse_args()
    if not args.source.is_dir():
        parser.error(f"source directory does not exist: {args.source}")
    print(f"Unity 6 compatibility repairs: {modernize(args.source)} files changed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
