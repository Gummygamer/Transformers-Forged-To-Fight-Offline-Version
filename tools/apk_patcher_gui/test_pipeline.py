"""Unit tests for the side-effect-free APK patcher planning module."""

from __future__ import annotations

from dataclasses import replace
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock


HERE = Path(__file__).resolve().parent
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))
import pipeline


class PipelineTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        root = Path(self.temp.name)
        self.source = root / "source with spaces.apk"
        self.source.touch()
        self.destination = root / "patched.apk"
        self.keystore = root / "debug.keystore"
        self.keystore.touch()
        self.pristine = root / "libil2cpp-pristine.so"
        self.pristine.touch()
        self.patched = root / "libil2cpp-patched.so"
        self.patched.touch()
        self.req = pipeline.BuildRequest(
            source_apk=str(self.source), dest_apk=str(self.destination), abi=pipeline.ABI_ARM64,
            server_mode=pipeline.MODE_BUNDLED, server_host="127.0.0.1", server_port=8080,
            scheme="http", keep_other_abi=False, patched_il2cpp="", auto_patch_il2cpp=False,
            il2cpp_source=str(self.pristine), keystore=str(self.keystore), ks_pass="android",
            key_pass="android", do_install=False, legible="legible", zipalign="zipalign",
            apksigner="apksigner", java="java", adb="adb",
        )

    def tearDown(self) -> None:
        self.temp.cleanup()

    def test_bundled_build_command_has_positionals_before_flags(self) -> None:
        unsigned, _ = pipeline.intermediate_paths(self.req)
        argv = pipeline.build_apk_command(self.req, unsigned)
        self.assertEqual(argv, [
            "legible", "run", str(pipeline.BUILD_SCRIPT), str(self.source), unsigned,
            "--scheme", "http", "--server-host", "127.0.0.1", "--server-port", "8080",
            "--abi", "arm64-v8a", "--bundle-server",
        ])
        self.assertEqual(argv[3:5], [str(self.source), unsigned])
        self.assertEqual(argv.count(str(self.source)), 1)

    def test_separate_server_and_optional_build_flags(self) -> None:
        req = replace(self.req, server_mode=pipeline.MODE_SEPARATE, server_host="192.168.1.5", server_port=8443, scheme="https")
        argv = pipeline.build_apk_command(req, "unsigned.apk")
        self.assertIn("--scheme", argv)
        self.assertEqual(argv[argv.index("--scheme") + 1], "https")
        self.assertEqual(argv[argv.index("--server-host") + 1], "192.168.1.5")
        self.assertNotIn("--bundle-server", argv)
        self.assertNotIn("--keep-other-abi", argv)
        self.assertNotIn("--patched-il2cpp", argv)
        optional = pipeline.build_apk_command(replace(req, keep_other_abi=True, patched_il2cpp=str(self.patched)), "unsigned.apk")
        self.assertIn("--keep-other-abi", optional)
        self.assertEqual(optional[optional.index("--patched-il2cpp") + 1], str(self.patched))

    def test_bundled_mode_validation(self) -> None:
        errors, _ = pipeline.validate(replace(self.req, scheme="https"))
        self.assertTrue(any("only supports http" in error for error in errors))
        errors, _ = pipeline.validate(replace(self.req, server_host="192.168.1.7"))
        self.assertTrue(any("loopback only" in error for error in errors))
        errors, _ = pipeline.validate(self.req)
        self.assertEqual(errors, [])

    def test_armv7_requires_or_creates_patched_library(self) -> None:
        armv7 = replace(self.req, abi=pipeline.ABI_ARMV7)
        errors, _ = pipeline.validate(armv7)
        self.assertTrue(any("TFTFHOOK" in error for error in errors))
        errors, _ = pipeline.validate(replace(armv7, patched_il2cpp=str(self.patched)))
        self.assertEqual(errors, [])
        auto = replace(armv7, auto_patch_il2cpp=True)
        errors, _ = pipeline.validate(auto)
        self.assertEqual(errors, [])
        steps = pipeline.plan_steps(auto)
        self.assertEqual(steps[0].argv[2], str(pipeline.PATCH_SCRIPT))
        self.assertIn("--abi", steps[0].argv)
        self.assertEqual(steps[0].argv[steps[0].argv.index("--abi") + 1], pipeline.ABI_ARMV7)
        for flag in ("--needed", "--apply", "-o"):
            self.assertIn(flag, steps[0].argv)
        self.assertEqual(steps[0].argv[steps[0].argv.index("--needed") + 1], "inplace")

    def test_tool_argv_and_plan_order(self) -> None:
        unsigned, aligned = pipeline.intermediate_paths(self.req)
        self.assertEqual(pipeline.zipalign_command(self.req, unsigned, aligned), ["zipalign", "-f", "-p", "4", unsigned, aligned])
        signed = pipeline.apksigner_command(self.req, aligned, self.req.dest_apk)
        self.assertEqual(signed, [
            "apksigner", "sign", "--ks", str(self.keystore), "--ks-pass", "pass:android",
            "--key-pass", "pass:android", "--out", str(self.destination), aligned,
        ])
        install = pipeline.adb_install_command(self.req, self.req.dest_apk)
        self.assertEqual(install, ["adb", "install", "-r", "--no-incremental", "--abi", pipeline.ABI_ARM64, str(self.destination)])
        self.assertNotIn("install APK", [step.name for step in pipeline.plan_steps(self.req)])
        full = pipeline.plan_steps(replace(self.req, do_install=True))
        self.assertEqual([step.name for step in full], ["build APK", "zipalign APK", "sign APK", "install APK"])

    def test_default_arm64_omits_patched_libil2cpp(self) -> None:
        request = pipeline.default_request()
        self.assertEqual(request.abi, pipeline.ABI_ARM64)
        self.assertEqual(request.patched_il2cpp, "")
        self.assertNotIn("--patched-il2cpp", pipeline.build_apk_command(request, "unsigned.apk"))

    def test_default_armv7_prefills_patched_libil2cpp_when_available(self) -> None:
        request = pipeline.default_request(pipeline.ABI_ARMV7)
        if pipeline.DEFAULT_PATCHED_IL2CPP.is_file():
            self.assertEqual(request.patched_il2cpp, str(pipeline.DEFAULT_PATCHED_IL2CPP))
        else:
            self.assertEqual(request.patched_il2cpp, "")

    def test_elf_abi_recognises_arm_headers(self) -> None:
        def elf(machine: int) -> bytes:
            fixture = bytearray(64)
            fixture[:4] = b"\x7fELF"
            fixture[18:20] = machine.to_bytes(2, "little")
            return bytes(fixture)

        arm64 = Path(self.temp.name) / "arm64.so"
        armv7 = Path(self.temp.name) / "armv7.so"
        non_elf = Path(self.temp.name) / "not-elf.so"
        arm64.write_bytes(elf(0xB7))
        armv7.write_bytes(elf(0x28))
        non_elf.write_bytes(b"not an ELF")
        self.assertEqual(pipeline.elf_abi(str(arm64)), pipeline.ABI_ARM64)
        self.assertEqual(pipeline.elf_abi(str(armv7)), pipeline.ABI_ARMV7)
        self.assertEqual(pipeline.elf_abi(str(non_elf)), "")
        self.assertEqual(pipeline.elf_abi(str(Path(self.temp.name) / "missing.so")), "")

    def test_validation_reports_patched_library_abi_mismatch(self) -> None:
        armv7 = Path(self.temp.name) / "armv7.so"
        fixture = bytearray(64)
        fixture[:4] = b"\x7fELF"
        fixture[18:20] = (0x28).to_bytes(2, "little")
        armv7.write_bytes(fixture)

        errors, _ = pipeline.validate(replace(self.req, patched_il2cpp=str(armv7)))
        mismatch = [error for error in errors if "patched libil2cpp.so at" in error]
        self.assertEqual(len(mismatch), 1)
        self.assertIn(pipeline.ABI_ARMV7, mismatch[0])
        self.assertIn(pipeline.ABI_ARM64, mismatch[0])
        self.assertIn(str(armv7), mismatch[0])

        errors, _ = pipeline.validate(replace(self.req, abi=pipeline.ABI_ARMV7, patched_il2cpp=str(armv7)))
        self.assertFalse(any("patched libil2cpp.so at" in error for error in errors))

    def test_find_java_honours_java_home(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            java = Path(temporary) / "bin" / "java"
            java.parent.mkdir()
            java.touch()
            java.chmod(0o755)
            with mock.patch.dict(os.environ, {"JAVA_HOME": temporary}, clear=False):
                self.assertEqual(pipeline.find_java(), str(java))

    def test_validation_requires_java(self) -> None:
        errors, _ = pipeline.validate(replace(self.req, java=""))
        self.assertTrue(any("JAVA_HOME" in error for error in errors))

    def test_signing_step_has_java_environment_only(self) -> None:
        java = Path(self.temp.name) / "jdk" / "bin" / "java"
        java.parent.mkdir(parents=True)
        java.touch()
        steps = pipeline.plan_steps(replace(self.req, java=str(java)))
        signing_steps = [step for step in steps if step.name == "sign APK"]
        self.assertEqual(len(signing_steps), 1)
        self.assertIsNotNone(signing_steps[0].env)
        assert signing_steps[0].env is not None
        self.assertEqual(signing_steps[0].env["JAVA_HOME"], str(java.parent.parent.resolve()))
        self.assertIn(str(java.parent.resolve()), signing_steps[0].env["PATH"])
        self.assertTrue(all(step.env is None for step in steps if step.name != "sign APK"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
