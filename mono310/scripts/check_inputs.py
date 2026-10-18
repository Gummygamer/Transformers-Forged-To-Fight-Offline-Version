"""Reject cached exports from another game track before compiling mono310."""
import os
import re
import hashlib
import json
import sys
from pathlib import Path


def check(ripped, decompiled):
    export = Path(ripped) / "ExportedProject"
    settings = (export / "ProjectSettings/ProjectSettings.asset").read_text()
    match = re.search(r"(?m)^\s*bundleVersion:\s*(\S+)", settings)
    version = match[1].strip("\"'") if match else "unknown"
    if version != "3.1.0":
        raise ValueError(f"mono310 requires a 3.1.0 export; RIPPED identifies {version}: {ripped}")
    # The 3.1.0 UI serializes this field; 2.0.2's identically named SCI<T>
    # does not. Mixing the caches compiles but breaks every shared UI widget.
    sci = Path(decompiled) / "Assembly-CSharp/SCI.cs"
    if not sci.is_file() or not re.search(r"\bGameObject\s+ContainerOverride\s*;", sci.read_text()):
        raise ValueError(f"ILSPY_OUT lacks the 3.1.0 shared UI layout: {decompiled}. Decompile the matching 3.1.0 export.")
    return version


def identity(ripped):
    assemblies = Path(ripped) / "AuxiliaryFiles/GameAssemblies"
    return {name: hashlib.sha256((assemblies / (name + ".dll")).read_bytes()).hexdigest()
            for name in ("Assembly-CSharp", "Assembly-CSharp-firstpass")}


def check_project(project, ripped):
    project = Path(project)
    record = json.loads((project / "mono310-source.json").read_text())
    if record != {"game_version": "3.1.0", "assemblies": identity(ripped)}:
        raise ValueError("The assembled project uses different source inputs; run mono310/run.sh assemble.")
    sci = project / "Assets/Scripts/Assembly-CSharp/SCI.cs"
    if "ContainerOverride" not in sci.read_text():
        raise ValueError("The assembled UI scripts do not match 3.1.0; reassemble before building.")


if __name__ == "__main__":
    try:
        print("source inputs: " + check(os.environ["RIPPED"], os.environ["ILSPY_OUT"]))
        if len(sys.argv) == 3 and sys.argv[1] == "--project":
            check_project(sys.argv[2], os.environ["RIPPED"])
    except (OSError, ValueError) as error:
        raise SystemExit(str(error))
