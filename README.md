# Transformers: Forged to Fight, offline revival (3.1.0 Unity 2020 recompile)

This repository rebuilds the operator's own 3.1.0 (Mono, `com.kabam.bigrobot` build 822754)
Android APK with the Unity 2020 toolchain, against a local revival server.

- [`mono310/README.md`](mono310/README.md): the recompile pipeline, requirements and status.
- [`mono310/ANDROID_LAUNCHER.md`](mono310/ANDROID_LAUNCHER.md) and [`mono310/launcher/README.md`](mono310/launcher/README.md): the assetless launcher.
- `Server/`: the local revival server (Legible scripts, profile `mono310`). Authoring notes: `ABILITY_AUTHORING.md`, `BOARD_AUTHORING.md`; wire contracts: `MONO_SERVER_CONTRACTS.md`.
- `tools/`: build helpers used by `mono310/run.sh`.
- `COMPLIANCE.md`: no game bytes are stored here; everything is generated from APKs you supply.

Earlier tracks (9.2 IL2CPP native-hook patcher, Android patcher app, PVP host, StoryPort,
net relays, Ghidra/RE notes) were retired from the tree and remain in git history before the
commit that removed them.
