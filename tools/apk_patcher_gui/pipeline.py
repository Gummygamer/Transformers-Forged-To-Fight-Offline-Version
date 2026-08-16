"""Pure planning and validation logic for the APK patcher web UI.

The executor intentionally lives elsewhere: ``plan_steps`` is its hand-off seam.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import os
import re
import shutil
from typing import Mapping


PROJECT_ROOT = Path(__file__).resolve().parents[2]
ABI_ARM64 = "arm64-v8a"
ABI_ARMV7 = "armeabi-v7a"
MODE_BUNDLED = "bundled"
MODE_SEPARATE = "separate"
PACKAGE_NAME = "com.kabam.bigrobot"

BUILD_SCRIPT = PROJECT_ROOT / "Server" / "build_phone_apk.lbl"
PATCH_SCRIPT = PROJECT_ROOT / "patches" / "patch_il2cpp.lbl"
DEFAULT_SOURCE_APK = PROJECT_ROOT / "Transformers 9.2 offline.apk"
DEFAULT_OUTPUT_DIR = PROJECT_ROOT / "build"
DEFAULT_IL2CPP_SOURCE = (
    PROJECT_ROOT
    / "Transformers 9.2 extracted"
    / "lib"
    / ABI_ARMV7
    / "libil2cpp.so"
)
DEFAULT_PATCHED_IL2CPP = DEFAULT_OUTPUT_DIR / "libil2cpp-armv7-patched.so"
DEFAULT_KEYSTORE = Path("/home/hermes/.android/debug.keystore")


@dataclass
class BuildRequest:
    source_apk: str
    dest_apk: str
    abi: str
    server_mode: str
    server_host: str
    server_port: int
    scheme: str
    keep_other_abi: bool
    patched_il2cpp: str
    auto_patch_il2cpp: bool
    il2cpp_source: str
    keystore: str
    ks_pass: str
    key_pass: str
    do_install: bool
    legible: str
    zipalign: str
    apksigner: str
    java: str
    adb: str


@dataclass
class Step:
    name: str
    argv: list[str]
    cwd: str | None = None
    env: dict[str, str] | None = None


def _version_key(path: Path) -> tuple:
    """Sort Android build-tools directories in a useful version order."""
    pieces = re.findall(r"\d+|[A-Za-z]+", path.name)
    return tuple(int(part) if part.isdigit() else part.lower() for part in pieces)


def _sdk_roots() -> list[Path]:
    roots: list[Path] = []
    for value in (os.environ.get("ANDROID_HOME"), os.environ.get("ANDROID_SDK_ROOT")):
        if value:
            roots.append(Path(value))
    opt_root = Path("/opt/android-sdk")
    if opt_root not in roots:
        roots.append(opt_root)
    return roots


def find_build_tool(name: str) -> str:
    """Find an Android build tool without raising when the SDK is absent."""
    for root in _sdk_roots():
        build_tools = root / "build-tools"
        if not build_tools.is_dir():
            continue
        versions = sorted(
            (entry for entry in build_tools.iterdir() if entry.is_dir()),
            key=_version_key,
            reverse=True,
        )
        for version in versions:
            candidate = version / name
            if candidate.is_file() and os.access(candidate, os.X_OK):
                return str(candidate)
    return shutil.which(name) or ""


def find_adb() -> str:
    """Find adb from configured SDK roots or PATH."""
    for root in _sdk_roots():
        candidate = root / "platform-tools" / "adb"
        if candidate.is_file() and os.access(candidate, os.X_OK):
            return str(candidate)
    return shutil.which("adb") or ""


def find_java() -> str:
    """Return an executable java path, or an empty string when none is found."""
    candidates: list[Path] = []
    java_home = os.environ.get("JAVA_HOME")
    if java_home:
        candidates.append(Path(java_home) / "bin" / "java")
    path_java = shutil.which("java")
    if path_java:
        candidates.append(Path(path_java))
    candidates.append(Path.home() / "jdk17" / "bin" / "java")
    candidates.extend(sorted(Path("/usr/lib/jvm").glob("*/bin/java")))
    for candidate in candidates:
        if candidate.is_file() and os.access(candidate, os.X_OK):
            return str(candidate)
    return ""


def elf_abi(path: str) -> str:
    """Return a recognised ARM ABI from an ELF header, or an empty string."""
    try:
        with open(path, "rb") as file:
            header = file.read(20)
    except OSError:
        return ""
    if len(header) < 20 or header[:4] != b"\x7fELF" or header[5] == 2:
        return ""
    machine = int.from_bytes(header[18:20], "little")
    if machine == 0xB7:
        return ABI_ARM64
    if machine == 0x28:
        return ABI_ARMV7
    return ""


def default_request(abi: str = ABI_ARM64) -> BuildRequest:
    patched = (
        str(DEFAULT_PATCHED_IL2CPP)
        if abi == ABI_ARMV7 and DEFAULT_PATCHED_IL2CPP.is_file()
        else ""
    )
    return BuildRequest(
        source_apk=str(DEFAULT_SOURCE_APK),
        dest_apk=str(DEFAULT_OUTPUT_DIR / "Transformers 9.2 offline-revival.apk"),
        abi=abi,
        server_mode=MODE_BUNDLED,
        server_host="127.0.0.1",
        server_port=8080,
        scheme="http",
        keep_other_abi=False,
        patched_il2cpp=patched,
        auto_patch_il2cpp=False,
        il2cpp_source=str(DEFAULT_IL2CPP_SOURCE),
        keystore=str(DEFAULT_KEYSTORE),
        ks_pass="android",
        key_pass="android",
        do_install=False,
        legible=shutil.which("legible") or "",
        zipalign=find_build_tool("zipalign"),
        apksigner=find_build_tool("apksigner"),
        java=find_java(),
        adb=find_adb(),
    )


def request_from_mapping(values: Mapping[str, object]) -> BuildRequest:
    """Overlay JSON form values on defaults, with conservative type coercion."""
    requested_abi = values.get("abi")
    abi = requested_abi if requested_abi in {ABI_ARM64, ABI_ARMV7} else ABI_ARM64
    request = default_request(abi)
    for field in request.__dataclass_fields__:
        if field not in values:
            continue
        value = values[field]
        if field == "server_port":
            try:
                setattr(request, field, int(value))
            except (TypeError, ValueError):
                setattr(request, field, 0)
        elif field in {"keep_other_abi", "auto_patch_il2cpp", "do_install"}:
            setattr(request, field, value is True or value == "true" or value == "on" or value == 1)
        elif value is None:
            setattr(request, field, "")
        else:
            setattr(request, field, str(value))
    return request


def auto_patch_output(req: BuildRequest) -> str:
    return str(DEFAULT_OUTPUT_DIR / f"libil2cpp-{req.abi}-patched.so")


def _path_is_file(value: str) -> bool:
    return bool(value.strip()) and Path(value).is_file()


def validate(req: BuildRequest) -> tuple[list[str], list[str]]:
    """Return user-facing blocking errors and non-blocking warnings."""
    errors: list[str] = []
    warnings: list[str] = []
    source = req.source_apk.strip()
    destination = req.dest_apk.strip()

    if not _path_is_file(source):
        errors.append("Source APK is missing or is not a file; choose the APK to patch.")
    if not destination:
        errors.append("Final destination APK is empty; choose a separate signed output path.")
    elif source and Path(destination).resolve() == Path(source).resolve():
        errors.append("Destination must differ from source so the original APK is not overwritten.")
    if req.abi not in {ABI_ARM64, ABI_ARMV7}:
        errors.append("ABI must be exactly arm64-v8a or armeabi-v7a.")
    if req.server_mode not in {MODE_BUNDLED, MODE_SEPARATE}:
        errors.append("Server mode must be either bundled or separate.")
    if req.server_mode == MODE_BUNDLED:
        if req.scheme != "http":
            errors.append("A bundled server only supports http because the builder cannot bundle https.")
        if req.server_host != "127.0.0.1":
            errors.append("A bundled server binds loopback only, so its server host must be exactly 127.0.0.1.")
    elif req.server_mode == MODE_SEPARATE:
        if not req.server_host.strip():
            errors.append("A separate PC-hosted server needs a non-empty server host.")
        elif req.server_host.strip() == "127.0.0.1":
            warnings.append("127.0.0.1 points at the phone itself; use your PC's reachable address for a separate server.")
    if req.scheme not in {"http", "https"}:
        errors.append("Scheme must be http or https.")
    if not 1 <= req.server_port <= 65535:
        errors.append("Server port must be between 1 and 65535.")

    if req.abi == ABI_ARMV7 and not req.patched_il2cpp.strip() and not req.auto_patch_il2cpp:
        errors.append(
            "32-bit builds need a patched libil2cpp.so: without it the APK installs, but TFTFHOOK "
            "never logs and nothing listens on port 8080. Supply a patched library or enable auto-patching."
        )
    if req.patched_il2cpp.strip() and not _path_is_file(req.patched_il2cpp):
        errors.append("The supplied patched libil2cpp.so path does not exist or is not a file.")
    elif req.patched_il2cpp.strip():
        patched_abi = elf_abi(req.patched_il2cpp)
        if patched_abi and patched_abi != req.abi:
            errors.append(
                f"The supplied patched libil2cpp.so at {req.patched_il2cpp} is {patched_abi}, "
                f"but this build targets {req.abi}. Supply a patched library built for {req.abi}, "
                "or change the ABI."
            )
    if req.auto_patch_il2cpp and not _path_is_file(req.il2cpp_source):
        errors.append("Auto-patching is enabled, but the pristine libil2cpp.so source is missing or is not a file.")

    for label, value in (("legible", req.legible), ("zipalign", req.zipalign), ("apksigner", req.apksigner)):
        if not value.strip():
            errors.append(f"Required tool {label} was not found; install it or provide its path.")
    if not req.java.strip():
        errors.append("apksigner requires Java, but no JRE/JDK was found; install one or set JAVA_HOME.")
    if req.do_install and not req.adb.strip():
        errors.append("adb was not found, but installation to a connected device was requested.")
    if not _path_is_file(req.keystore):
        errors.append("Signing keystore is missing or is not a file.")
    if req.abi == ABI_ARMV7 and not req.keep_other_abi:
        warnings.append("32-bit-only output drops the arm64 libraries; enable keeping the other ABI if you need both architectures.")
    return errors, warnings


def patch_il2cpp_command(req: BuildRequest) -> list[str]:
    return [
        req.legible,
        "run",
        str(PATCH_SCRIPT),
        "--abi",
        req.abi,
        "--needed",
        "inplace",
        "--apply",
        "-o",
        auto_patch_output(req),
        req.il2cpp_source,
    ]


def build_apk_command(req: BuildRequest, unsigned_path: str) -> list[str]:
    argv = [
        req.legible,
        "run",
        str(BUILD_SCRIPT),
        req.source_apk,
        unsigned_path,
        "--scheme",
        req.scheme,
        "--server-host",
        req.server_host,
        "--server-port",
        str(req.server_port),
        "--abi",
        req.abi,
    ]
    if req.keep_other_abi:
        argv.append("--keep-other-abi")
    patched = auto_patch_output(req) if req.auto_patch_il2cpp else req.patched_il2cpp.strip()
    if patched:
        argv.extend(["--patched-il2cpp", patched])
    if req.server_mode == MODE_BUNDLED:
        argv.append("--bundle-server")
    return argv


def zipalign_command(req: BuildRequest, unsigned_path: str, aligned_path: str) -> list[str]:
    return [req.zipalign, "-f", "-p", "4", unsigned_path, aligned_path]


def apksigner_command(req: BuildRequest, aligned_path: str, out_path: str) -> list[str]:
    return [
        req.apksigner,
        "sign",
        "--ks",
        req.keystore,
        "--ks-pass",
        f"pass:{req.ks_pass}",
        "--key-pass",
        f"pass:{req.key_pass}",
        "--out",
        out_path,
        aligned_path,
    ]


def adb_install_command(req: BuildRequest, apk: str) -> list[str]:
    return [req.adb, "install", "-r", "--no-incremental", "--abi", req.abi, apk]


def intermediate_paths(req: BuildRequest) -> tuple[str, str]:
    destination = Path(req.dest_apk)
    stem = destination.stem
    parent = destination.parent
    return str(parent / f"{stem}-unsigned.apk"), str(parent / f"{stem}-aligned.apk")


def plan_steps(req: BuildRequest) -> list[Step]:
    unsigned, aligned = intermediate_paths(req)
    steps: list[Step] = []
    if req.auto_patch_il2cpp:
        steps.append(Step("patch il2cpp", patch_il2cpp_command(req), str(PROJECT_ROOT)))
    steps.extend(
        [
            Step("build APK", build_apk_command(req, unsigned), str(PROJECT_ROOT)),
            Step("zipalign APK", zipalign_command(req, unsigned, aligned)),
            Step(
                "sign APK",
                apksigner_command(req, aligned, req.dest_apk),
                env=(
                    {
                        "JAVA_HOME": str(Path(req.java).resolve().parent.parent),
                        "PATH": str(Path(req.java).resolve().parent) + os.pathsep + os.environ.get("PATH", ""),
                    }
                    if req.java
                    else None
                ),
            ),
        ]
    )
    if req.do_install:
        steps.append(Step("install APK", adb_install_command(req, req.dest_apk)))
    return steps
