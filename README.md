# Transformers: Forged to Fight, offline revival handoff

This package contains a working local offline revival of Transformers: Forged to Fight,
plus the tools, patches, and reverse-engineering notes used to build it. It is a handoff
for continuing the much larger task: authoring replacement server-side content from
scratch. The original online service data is not included or reconstructed here.

Read this whole file before you touch anything. The "Gotchas" section in particular will
save you days.

For a complete native Windows 7 build of the 32-bit `armeabi-v7a` phone APK,
including server setup, signing, installation, Wi-Fi, and USB operation, see
[`WINDOWS_7_ARMV7.md`](WINDOWS_7_ARMV7.md).


## What actually works right now

The game boots completely offline and reaches the real interactive home screen without a
live Kabam service. The base, roster, fight-mode selection, crystals screen, popups, and
tips navigate without crashing. Login and first-time-experience gates complete locally.
The scripted Optimus-versus-Starscream intro fight is playable through its light-attack
tutorial, with live 3D characters and combat controls.

The local server also supplies a complete, authored STORY 1.1.1 loop: select a squad,
enter the primordial board, move between reachable nodes, trigger the final boss,
choose a bot on the native pre-fight screen, fight the Sharkticon, resolve a win, and
return to the board. Movement from an encounter is gated on a submitted win, so a loss
or quit reopens that same fight. The authored `Light`, `Medium`, `Heavy`, and `Ranged` attack rows
and combat armor tuning allow landed hits to reduce health. The roster, hero details,
team selection, and battle model IDs are generated from the same original data source,
so combat uses the matching 3D mesh instead of a generic placeholder.
Pre-mission squad selection exposes all five client slots; the initial saved squad remains
the original three bots, leaving the fourth and fifth slots available to fill.
During a STORY fight, the special-attack meter is no longer locked: it charges from landed
and received hits, and a special attack can be fired for real damage. Every bot has all three
special-meter segments available immediately.
On arm64, STORY opponents can also fire their basic ranged attack while outside melee range.
Game modes are no longer gated behind profile level: Raids, the Store, Arenas, Special
Missions, Alliance Missions and Daily Missions are reachable from a fresh offline session
regardless of account level, because the native padlock checks are patched out.
On arm64, BOTS roster and detail views suppress residual base geometry, and level-3 cinematics
render and animate the shipped alternate form before restoring robot form. These visual hooks
are not ported to ARMv7.


## What does not work, and why

This is a playable preservation sandbox, not a complete replacement for the original game.
The single STORY mission, `1.1.1` "Arrival", now walks an 11-tile path with ten authored
encounters: nine escalating enemy lineups and a final boss. The mission also carries
authored dialogue sets. Match resolution uses the minimal success response required to return
to the board and only unlocks the next node after a win; completed progression, rewards, and
the broader quest state are not persisted.
Per-bot ability effects, additional missions and enemy lineups beyond Arrival, the economy,
and most progression systems remain to be authored.

Forged to Fight was fully server authoritative. The app on the phone is essentially a
screen with controls. Almost nothing about the game lived in the app. Every mission, every
fight, every enemy lineup, the entire roster's stats and abilities, the economy, and all
the balance lived on Kabam's servers and were streamed to the device each session. When
the servers were shut down in early 2020 that content database went with them, and it was
never released or publicly archived anywhere I can reach.

The client can load art and audio from a copy of the app that the operator supplies. What
is missing is the server data that selected those assets, assembled fights and missions,
and defined stats and abilities. This project supplies only new, hand-authored data in
the shapes the client parses; it does not include the APK, game assets, original binaries,
or captured game audiovisual material. See `COMPLIANCE.md` before extending it.


## How the offline boot works

There are four moving parts. Together they let the unmodified game think it is talking to
Kabam.

1. Native binary patches. The game is Unity IL2CPP, so the logic lives in a compiled ARM
   library, `libil2cpp.so`, not in editable script files. `patches/patch_il2cpp.lbl` rewrites
   twelve functions in that library to get past the dead server checks: it defeats two
   certificate pinning paths so our own TLS cert is accepted, forces the manager
   registration block to run even though the live config is null, lets login succeed with
   our local device session, and silences the subsystem fatal errors that would otherwise
   pop the "failed to log in" dialog. Two more stub the Unity reachability getter and the
   endpoint's connectivity check, so the client will talk to the bundled loopback server on a
   phone with no Wi-Fi access point. The last four stub the profile-level padlock checks, so
   game modes are never locked behind account level. It also re-injects a single dependency entry (see the
   Gotchas section) so the runtime hook actually loads. The output is `libil2cpp.patched.so`.

2. A fake Sparx server. `Server/fakeserver.lbl` stands in for Kabam's backend. It listens on
   TLS 443 and plain HTTP 80 and answers the game's API calls. Canned responses live in
   `Server/responses/`, one file per endpoint, named by method and path, for example
   `GET__account_data.json`. A few endpoints are answered dynamically in code rather than
   from a file, because the game expects them to echo values from the request (the tutorial
   endpoints and the hero detail endpoint). The response envelope is
   `{"error":null,"result": ...}`. Note that inside Sparx error payloads the field is spelled
   `err`, not `error`. That detail matters and is easy to miss.

3. A native runtime hook. `tools/nativehook/` builds `libdothook.so`, a small library that is
   loaded into the game at startup and logs every data key the game reads, plus a couple of
   targeted behavior nudges. This is the feedback loop that made everything else possible: it
   tells you exactly what the game is asking for so you can synthesize a response and verify
   it. It is a pure byte overwrite inline hook installed before execution, because the normal
   tool for this (Frida) crashes under the emulator's ARM translation layer.

4. Device wiring. The emulator has to send Kabam's domains to the PC and trust the fake
   cert. `tools/provision_ldplayer.sh` does this in one shot: it pushes the patched library
   and the hook, redirects the Kabam hostnames to the PC's LAN address via the hosts file,
   mounts the fake CA into the system trust store, and relaxes SELinux. Run it after every
   emulator restart, because those mounts do not survive a reboot.

The data flow at runtime is: game makes an HTTPS call to a Kabam domain, the hosts file
sends it to the PC, the fake server answers with a response from `Server/responses/`, the
patched library accepts the cert and the answer, and the hook logs what was read. That loop
is how every screen in this build was brought up.


## What is in this package

```
README.md                     this file
COMPLIANCE.md                 copyright, trademark, and security boundaries for the project
TECHNICAL_NOTES.md            the deeper technical reference: patches, recovered data shapes, findings
patches/
  patch_il2cpp.lbl            the twelve native patches plus the dependency re-injection
  abi_map.lbl                 translate arm64 addresses and field offsets to armeabi-v7a
  disasm_fn.lbl               helper: disassemble a function at an offset
  find_callers.lbl            helper: find callers of a function
  find_str_ref.lbl            helper: find references to a string
Server/
  fakeserver.lbl              the fake Sparx server (request synthesis, HTTP and HTTPS listeners)
  gamedata.lbl                hand-authored roster, battle balance, mission, and tuning data
  test_gamedata.lbl           verifies generated roster, combat, tuning, and mesh mappings
  gen_certs.sh                regenerate the TLS cert and CA (run this, see below)
  run_local.lbl               run the Legible plain-HTTP server on an unprivileged port
  build_phone_apk.lbl         create an unsigned local-server APK for a stock phone
  provision_phone.sh          install, launch, and configure USB or Wi-Fi phone use
  setup_device.sh             device side network and trust setup reference
  iterate.sh                  quick restart and capture loop
  responses/                  one JSON file per endpoint the game calls
tools/
  provision_ldplayer.sh       one shot re-provision of the emulator to the working state
  setup_arm64.sh              toolchain setup notes
  apply_labels.lbl            build the portable Ghidra label input
  find_xrefs.lbl              normalize and format portable Ghidra xref data
  decompile_targets.lbl       normalize portable Ghidra decompiler targets
  ghidra_run.lbl              run the Ghidra headless workflows
  test_ghidra_tools.lbl       test the Ghidra Legible data halves
  ghidra/
    ApplyLabels.java          JVM GhidraScript that applies prepared labels
    LightAnalyze.java         JVM GhidraScript that disables heavy analyzers
    FindXrefs.java            JVM GhidraScript that collects raw references
    DecompileTargets.java     JVM GhidraScript that writes decompiled C
  frida_attach.lbl            Legible Frida attach-and-capture driver
  frida_run.lbl               Legible Frida spawn-and-capture driver
  hook_dot.js
  nativehook/
    hook.c                    source of libdothook.so, the runtime hook (arm64)
    libdothook.so             locally built hook, arm64 (ignored)
    hook_arm32.c              armeabi-v7a hook: the behaviour fixes, no key logging
    libdothook-armeabi-v7a.so locally built hook, armeabi-v7a (ignored)
    deploy.sh                 build and deploy the hook
    relaunch_and_capture.sh   relaunch the game and capture logs
  hook/dothook.c              earlier hook variant, kept for reference
re_notes/
  dump.cs                     locally generated IL2CPP type dump (ignored; see below)
  decomp_out.c                decompiled bodies of key functions
  decompile_targets.txt       the offsets worth decompiling
  ASSET_INVENTORY.txt         inventory of asset identifiers in an operator-supplied app
```

`re_notes/dump.cs` is deliberately not versioned. Generate it locally from an entitled
Kabam 9.2 APK with Il2CppDumper; it needs the matching
`lib/arm64-v8a/libil2cpp.so` and
`assets/bin/Data/Managed/Metadata/global-metadata.dat` from that APK:

```bash
mkdir -p /tmp/tftf-il2cpp/{lib,assets/bin/Data/Managed/Metadata}
unzip -p "Transformers 9.2 offline.apk" lib/arm64-v8a/libil2cpp.so \
  > /tmp/tftf-il2cpp/lib/libil2cpp.so
unzip -p "Transformers 9.2 offline.apk" assets/bin/Data/Managed/Metadata/global-metadata.dat \
  > /tmp/tftf-il2cpp/assets/bin/Data/Managed/Metadata/global-metadata.dat
Il2CppDumper /tmp/tftf-il2cpp/lib/libil2cpp.so \
  /tmp/tftf-il2cpp/assets/bin/Data/Managed/Metadata/global-metadata.dat \
  /tmp/tftf-il2cpp-out
cp /tmp/tftf-il2cpp-out/dump.cs re_notes/dump.cs
```

The generated file is the complete type model of the game: every class, method, and data
field the client reads from the server. It is ignored by Git so it remains a local,
reproducible analysis artifact.


## What is not in this package, and where to get it

These were left out on purpose, because they are large, or copyrighted, or secret, or you
should generate your own.

- The APK itself (`com.kabam.bigrobot`, version 9.2.0). Source and use only a copy you are
  entitled to use. The package name and version are in `TECHNICAL_NOTES.md`.
- The original `libil2cpp.so` and the game assets. Both come straight out of the APK. Unzip
  the APK, the library is under `lib/arm64-v8a/`, the assets are under `assets/`.
- The TLS cert and CA. Do not ship private keys. Run `Server/gen_certs.sh` to make your own
  matching pair, then point the device trust store at the new CA.
- The patched library. Regenerate it: run `patches/patch_il2cpp.lbl` against the original
  `libil2cpp.so` from the APK.
- Frida server and Il2CppDumper. Both are public tools. Il2CppDumper is what produced
  `re_notes/dump.cs` from the APK's library and global metadata.
- The Android NDK (r26 was used) and JDK 21, needed to build the hook and to run the Ghidra
  headless decompiler.
- Anything under `media/` or `probe/`. Those are local screenshots and recordings, may contain
  copyrighted game audiovisual content, are ignored by Git, and must never be added or committed.


## How to run what exists today

You need the APK installed on an ARM translation capable emulator (LDPlayer 9 was used, with
root and writable system), the `legible` interpreter on the PC, and the items from the section above.

To get the `legible` interpreter and launch the [APK patcher GUI](#patching-the-apk-from-a-gui)
without doing any of that setup by hand, run `./launch_linux.sh` (Linux) or
`launch_windows.bat` (Windows) from the repository root. Either script installs `legible`
and the build tools it needs (Rust via rustup, git, a C compiler) only if they are missing,
then launches the GUI; re-running it later is fast because it finds `legible` already
installed. This does not replace the manual steps below, which are still needed for
everything the GUI does not cover (certs, the native hook, the emulator/device setup).

1. Generate certs once: `bash Server/gen_certs.sh`. This is a **bash** script, not
   Python — run it with `bash` (or `./Server/gen_certs.sh` after `chmod +x`) in a
   real shell (Git Bash on Windows). Do not run it with `python`/`python3` and do not
   paste its contents into a Python interactive prompt: the file is an OpenSSL
   config generator, not Python source, and a Python REPL will fail to parse it
   (for example, tripping over the `CN = tform-0901-...` line in the embedded
   config with a "leading zeros in decimal integer literals" error). That error
   means the wrong interpreter was used, not a bug in the script.
2. Build the patched library once: `legible run patches/patch_il2cpp.lbl path/to/original/libil2cpp.so --apply`.
   That patches the arm64 library; pass `--abi armeabi-v7a` for the 32-bit one (see below).
3. Build the arm64 hook locally:
   `~/Android/Sdk/ndk/26.3.11579264/toolchains/llvm/prebuilt/linux-x86_64/bin/aarch64-linux-android28-clang -shared -O2 -fPIC -Wl,-soname,libdothook.so -o tools/nativehook/libdothook.so tools/nativehook/hook.c tools/nativehook/inapk_server.c -llog`.
   `tools/nativehook/deploy.sh` has historical Windows paths and is not the current command.
4. Start the fake server on the PC: `legible run Server/fakeserver.lbl --https 443` and
   `legible run Server/fakeserver.lbl --http 80` (one process per listener). They need to be reachable
   on ports 443 and 80 from the emulator.
5. Provision the device: `bash tools/provision_ldplayer.sh <your-PC-LAN-IP>`. Re-run this
   after every emulator reboot.
6. Wait about 45 seconds, then tap the title screen to log in. You should reach the home
   screen.

### Running on a non-rooted phone over Wi-Fi (no USB while playing)

The phone and laptop can communicate directly over the same Wi-Fi/LAN. The APK must be
built with the laptop's LAN IPv4 address because a stock phone cannot override the dead
Kabam DNS names. For example, if the laptop is `192.168.0.139`:

1. Build the LAN variant:
   `legible run Server/build_phone_apk.lbl --server-host 192.168.0.139 "Transformers 9.2 offline.apk" build/phone-wifi-unsigned.apk`.
2. Align and sign it with Android build-tools:
   `zipalign -f -p 4 build/phone-wifi-unsigned.apk build/phone-wifi-aligned.apk`, then
   `apksigner sign --ks ~/.android/debug.keystore --ks-pass pass:android --key-pass pass:android --out build/Transformers-9.2-offline-phone-wifi.apk build/phone-wifi-aligned.apk`.
3. Start `legible run Server/run_local.lbl` on the laptop, and in a second terminal
   `legible run Server/run_local.lbl --https`. One Legible process holds one listener, so
   the two together listen on every network interface at HTTP port 8080 and HTTPS port
   8443. Allow those two TCP ports through the laptop's firewall for the private LAN if a
   firewall is enabled.
4. Install once over USB with
   `CONNECTION_MODE=wifi UPDATE_APK=1 APK=build/Transformers-9.2-offline-phone-wifi.apk Server/provision_phone.sh`.
   The update preserves game data when the installed app uses the same signing key. You can
   instead transfer and install the signed APK by another trusted local method.
5. Disconnect USB. Keep the fake server running and keep the phone and laptop on the same
   LAN while playing.

The embedded address must remain assigned to the laptop. A DHCP reservation is recommended;
if the address changes, rebuild and update the APK with the new address. Guest Wi-Fi networks
often isolate clients from one another and will not work. The native validation patches accept
the local server certificate, so installing a CA on the phone is not required.

### Playing over a virtual LAN or tunnel

Players who are not on the same physical network can join one virtual LAN with a tunnel such
as Hamachi, ZeroTier, Tailscale, or WireGuard. This project does not bundle or ship any of
those tools; the operator must install and configure one separately. One player is the host and
runs the fake server. The other players are clients.

The host finds the IPv4 address assigned to their tunnel adapter and gives it to the clients.
Hamachi addresses are in the `25.x.x.x` range, ZeroTier commonly uses `10.147.x.x`, and
Tailscale uses `100.x.x.x`. `Server/run_local.lbl` prints the machine's non-loopback IPv4
addresses at startup to help identify it. Start the server in two terminals:
`legible run Server/run_local.lbl` and `legible run Server/run_local.lbl --https`.
Both listeners bind all interfaces, so no additional bind configuration is needed. The host's
firewall must allow TCP 8080 and 8443 on the tunnel adapter. On Windows, tunnel adapters are
often classified as a Public network and blocked by default; this is the most common failure.

Each client builds an APK against the host's tunnel address, for example:
`legible run Server/build_phone_apk.lbl --server-host 25.13.240.7 "Transformers 9.2 offline.apk" build/phone-tunnel-unsigned.apk`.
Then align and sign it exactly as described in the preceding Wi-Fi section.

A phone cannot normally join a Hamachi network directly. The usual arrangement is for each
player's phone to use the same physical Wi-Fi as that player's PC while the PCs join the
tunnel. Alternatively, use a tunnel client that runs on Android; ZeroTier and Tailscale both
provide Android apps, while Hamachi does not.

`--bundle-server` cannot be combined with a tunnel host: the bundled in-APK server binds only
to loopback, and `build_phone_apk.lbl` enforces `--server-host 127.0.0.1` in that mode. If the
host needs served content to advertise the tunnel address, set `TFTF_SERVER_HOST`, optionally
with `TFTF_SERVER_SCHEME` and `TFTF_SERVER_PORT`, before starting the server.

### Online modes over the tunnel

The fake server can serve the game's online PVP/Arena screens over that tunnel. Two devices that
are active in the same arena become live peers: the server records their presence, pairs them
into one shared match ID, and serves each player the other player's currently saved team. Their
reported results are reconciled by the host, so both devices read the same final result.
The server reports a level-30 Commander account, so a fresh offline session can reach Raids, the
Store, Arenas, Special Missions, Alliance Missions, and Daily Missions immediately.

1. The host starts the server with the tunnel address advertised, in two terminals:
   `TFTF_SERVER_HOST=25.13.240.7 legible run Server/run_local.lbl` and
   `TFTF_SERVER_HOST=25.13.240.7 legible run Server/run_local.lbl --https`.
   Substitute the host's own tunnel address for `25.13.240.7`.
2. Each player builds and signs an APK against that same address with
   `legible run Server/build_phone_apk.lbl --server-host 25.13.240.7 ...`, exactly as in the
   preceding subsection.
3. Each player opens the team screen once and saves a team. The client posts it to
   `/bcg/setSavedTeam`, and that is what registers the player's roster on the host; no other
   action is required. Rosters are kept in `Server/logs/.fakeserver-rosters`, one line per
   peer, and survive a server restart. Saving again replaces that player's entry.
4. Both players open the Versus/Arena screen at about the same time. The server refreshes their
   presence and, when it sees two available peers in the same arena, gives both one shared
   `matchID`. The opponent endpoints then serve the paired player's actual saved team rather
   than a stale roster. Until another live player has saved a team, a built-in house team called
   `Autobot Garrison` is served instead, so single-player play is unaffected.
5. Play the offered fight and report the result through the Arena flow. The server keeps the two
   reports and reconciles them authoritatively: complementary win/loss reports and matching draws
   agree; conflicting reports are resolved deterministically, so both players see the same
   winner. Presence expires after 15 seconds by default. Set `TFTF_PRESENCE_TTL_MS` on the host
   to use a different positive timeout; a player idle past it is no longer eligible for a new
   live pairing until the client next touches the Arena service.

Two limits are worth understanding before trying this.

First, retail TFTF Arena is asynchronous by design. The client fights a local AI copy of the
opponent's stored team; it has no realtime fight netcode. Relaying real-time input would require
rewriting the IL2CPP fight simulation, which this project does not do. The genuinely reachable
LAN path on an unmodified retail client is live presence, two-device matchmaking into a shared
`matchID`, a live opponent team, and two-sided result reconciliation--not a frame-by-frame fight
between phones.

Second, `PVPAPI` (`re_notes/dump.cs` line 416801) is the client's whole PVP network surface.
Only `GET /pvp/get-login-data` is confirmed against a real captured client session; the other
PVP paths are derived as unconfirmed kebab-case mappings from its API methods and may need
adjustment after further device captures. The server also exposes heartbeat, lobby, fight-relay,
and result endpoints for tools and companion clients, but a retail device will never call those
invented endpoints. `TECHNICAL_NOTES.md` records the endpoint split and derivation.

Finally, `POST /auth/login` identifies each device by `credentials.udid` and gives it a stable,
device-specific session token. Two phones therefore become separate peers automatically, and
the manual `?peer=alice` curl workaround is no longer required. The `peer` query override
remains available as a testing escape hatch.

### Running on a non-rooted phone over USB

Retail phones cannot use the emulator's root-only hosts and system-CA bind mounts. Build a
phone variant that redirects the embedded backend names to loopback and uses Android's
owner-installed CA trust, then use ADB reverse to carry the traffic over USB:

1. Run `legible run Server/build_phone_apk.lbl "Transformers 9.2 offline.apk" build/phone-unsigned.apk`.
   The builder also injects the current `tools/nativehook/libdothook.so`, so phone builds
   include the same gameplay fixes tested on the emulator.
2. Sign the result with Android `apksigner`, writing
   `build/Transformers-9.2-offline-phone.apk`. The generated local build uses your Android
   debug key. If a differently signed build of the same package is already installed, it
   must be uninstalled first; doing so erases that installation's local game data.
3. Start `legible run Server/run_local.lbl` on the laptop, plus
   `legible run Server/run_local.lbl --https` in a second terminal.
4. Connect and authorize exactly one physical phone, then run `Server/provision_phone.sh`.
   If Play Protect rejects this locally modified APK with
   `INSTALL_FAILED_VERIFICATION_FAILURE`, review the APK and rerun the first installation as
   `ALLOW_UNVERIFIED_ADB=1 Server/provision_phone.sh`. This temporarily disables verification
   for that ADB install only and restores both settings immediately. Later runs detect the
   installed package and only restore forwarding/launch it. After rebuilding the APK, use
   `UPDATE_APK=1 Server/provision_phone.sh` to update it in place while preserving game data.

The phone APK preserves the original target SDK. Its bundled native validation patches accept
the local server certificate; lowering the target SDK to inherit user-installed CA trust causes
modern Play Protect to reject the APK.

For a self-contained backend, see the
[bundled-server recipe](#building-a-self-contained-apk-bundled-server-no-pc).

The USB connection must remain active: phone ports 8443 and 8080 are forwarded to the same
laptop ports. The explicit unprivileged ports are necessary because stock Android's ADB cannot
bind device ports 443 or 80. Re-run `provision_phone.sh` after a reboot or USB-debugging
reconnection.

If it hangs at login, check the very first item in the Gotchas section before anything else.

### Patching the APK from a GUI

From the repository root, launch the local browser UI with:

```sh
legible run tools/apk_patcher_gui/server.lbl
```

It prints a token-protected `http://127.0.0.1:<port>/` URL and opens it in the default
browser. The GUI, planner, background worker, and tests are all Legible; Python is not
required. Use `--port N` to choose a port instead of the default random free port, or
`--no-browser` when working headlessly. Legible's HTTP listener binds all interfaces, so
the GUI requires an unguessable per-launch token on every page and API request; keep the
printed URL private.

The page exposes the APK source and destination, signing keystore and passwords, and an
optional install-to-device step. Choose `arm64-v8a` (64-bit, the default) or
`armeabi-v7a` (32-bit), and choose whether to keep the other ABI's libraries. The server
mode is either bundled (self-contained, no PC, fixed to `http` and `127.0.0.1`) or
separate (a PC-hosted fake server, with a host, port, and `http`/`https` supplied by you).
It also accepts a patched `libil2cpp.so` path, or can auto-patch one with
`patches/patch_il2cpp.lbl` from the pristine library.

Pressing **Build APK** runs the same pipeline documented below: native-hook compilation
from `hook.c`/`hook_arm32.c` plus `inapk_server.c` when its default checkbox is enabled, optional
`patch_il2cpp.lbl`, then `legible run Server/build_phone_apk.lbl`, then
`zipalign -f -p 4`, then `apksigner sign` with the debug keystore, followed by
`zipalign -c -p 4` and `apksigner verify --verbose` against the final signed APK, and
optionally `adb install -r --no-incremental` followed by `adb shell pm path` to confirm
the package is installed. Each command's output streams live into
the page, and the build can be cancelled. **Build succeeded** means the final signed APK
passed both Android signature and page-alignment checks.

Before starting the multi-minute, multi-hundred-megabyte build, the UI catches a bundled
server configured with `https` or a non-loopback host (that mode only accepts `http` plus
`127.0.0.1`) and a 32-bit build with no patched `libil2cpp.so`. The latter is the failure
described in the 32-bit section below: the APK installs cleanly, but `TFTFHOOK` never
appears in the log and nothing listens on port 8080.

The manual recipes below remain the ground truth and are the fallback if the GUI is not
available.

### Building a self-contained APK (bundled server, no PC)

Build, align, sign, and install an arm64 APK with the fake-server response payload embedded:

1. `legible run Server/build_phone_apk.lbl "Transformers 9.2 offline.apk" build/phone-unsigned.apk --scheme http --server-host 127.0.0.1 --server-port 8080 --bundle-server`
2. `~/Android/Sdk/build-tools/35.0.0/zipalign -f -p 4 build/phone-unsigned.apk build/phone-aligned.apk`
3. `~/Android/Sdk/build-tools/35.0.0/apksigner sign --ks ~/.android/debug.keystore --ks-pass pass:android --key-pass pass:android --out build/bundled.apk build/phone-aligned.apk`
4. `adb uninstall com.kabam.bigrobot` then `adb install --no-incremental --abi arm64-v8a build/bundled.apk`

On a retail phone, this install can fail with `INSTALL_FAILED_VERIFICATION_FAILURE` until ADB
install verification is disabled. On the Galaxy A52, the required settings were:

```
adb shell settings put global package_verifier_enable 0
adb shell settings put global verifier_verify_adb_installs 0
adb shell settings put global package_verifier_user_consent -1
```

These settings may reset. The roughly 930 MB APK then takes about 35 seconds to install; see the
host-server provisioning note above for the analogous scripted flow.

Nothing else is needed: no `run_local.lbl`, no `adb reverse`, no hosts file edits, no CA install,
no root, and no `provision_*.sh`. Install and play. The existing host-server workflows above are
unchanged and remain the default.

`--bundle-server` supports both `arm64-v8a` (the default and primary tested path) and
`armeabi-v7a`. It requires plain HTTP on loopback: `--scheme https` and non-loopback
`--server-host` values are rejected. The baked payload is a snapshot of the authored data at build
time, so changing `Server/gamedata.lbl` requires rebuilding the APK. Its responses are the same
ones served by `Server/fakeserver.lbl`.

Because its two reachability patch sites are arm64 only, the bundled `armeabi-v7a` build requires
Android to report any active network; loopback-only airplane mode is not sufficient.

For an ARMv7 bundled build, use the same align and signing steps with ARMv7 output names.
Unlike the arm64 one, this build must also supply a patched library: `Transformers 9.2
offline.apk` ships an **already patched** `lib/arm64-v8a/libil2cpp.so` but a **pristine**
`lib/armeabi-v7a/libil2cpp.so`. Skip step 1 below and the APK installs and launches fine
while `adb logcat -s TFTFHOOK` stays completely silent and nothing listens on 8080, because
the hook is never loaded — which looks exactly like a broken server but is not one.

1. `legible run patches/patch_il2cpp.lbl --abi armeabi-v7a --needed inplace --apply \
   -o build/libil2cpp-armv7-patched.so "Transformers 9.2 extracted/lib/armeabi-v7a/libil2cpp.so"`
   (`--needed inplace` avoids `patchelf`, which is not always on PATH.)
2. `legible run Server/build_phone_apk.lbl --abi armeabi-v7a --bundle-server --scheme http \
   --server-host 127.0.0.1 --server-port 8080 --patched-il2cpp build/libil2cpp-armv7-patched.so \
   "Transformers 9.2 offline.apk" build/phone-armv7-unsigned.apk`
3. `~/Android/Sdk/build-tools/35.0.0/zipalign -f -p 4 build/phone-armv7-unsigned.apk build/phone-armv7-aligned.apk`
4. `~/Android/Sdk/build-tools/35.0.0/apksigner sign --ks ~/.android/debug.keystore --ks-pass pass:android --key-pass pass:android --out build/bundled-armv7.apk build/phone-armv7-aligned.apk`
5. `adb install -r --no-incremental --abi armeabi-v7a build/bundled-armv7.apk`

On 2026-08-17, bundled `armeabi-v7a` was live-verified with no host server and `adb reverse
--list` empty: it reached STORY 1.1.1, won its intermediate Patrol fight while the in-APK server
served requests, and the final Boss remained reachable (33% to 66% explored).

`--no-incremental` is mandatory: incremental-fs mounts the native library directory read-only,
even to root.

For debugging, `adb logcat -s TFTFHOOK` should show `in-apk server listening on 127.0.0.1:8080`
and `in-apk server start: 0`.

`in-apk candidate rejected <path>: <reason>` lines are normal: the server enumerates every mapped
APK in the process, and retail devices map other packages' APKs, notably Google Play Services',
into the game process. A run that logs `in-apk server start: -1` with no accepted candidate did
not find the payload in any mapped APK, meaning the APK was built without `--bundle-server` or its
`libdothook.so` was built without `inapk_server.c`.


### Building for 32-bit ARM (armeabi-v7a)

The APK ships native libraries for both `arm64-v8a` and `armeabi-v7a`. Most workflows above
target arm64, which remains the default. A 32-bit build exists for 32-bit devices and 32-bit
emulator instances, and is selected with `--abi`.

The 32-bit build has been run end to end: it boots offline to the home screen, selects a
STORY squad, enters the board, moves between nodes, opens the pre-fight screen, fights the
Sharkticon in the live 3D arena, and resolves the match `WON` with the enemy at zero
health, with no crash and all twelve hooks installed. Both of the fixes described below
were verified firing during that run.

1. Patch the 32-bit library:
   `legible run patches/patch_il2cpp.lbl --abi armeabi-v7a path/to/lib/armeabi-v7a/libil2cpp.so --apply`.
   The same six original patches apply; only the addresses and encodings differ. The two newer
   reachability patches and the four profile-level padlock patches are arm64 only for now, because
   `abi_map.lbl` needs an Il2CppDumper dump
   of the 32-bit build to translate them and this repo only carries the arm64 dump; addresses
   must never be hand-translated. A 32-bit bundled APK therefore still needs a network interface
   to be up, and its game modes stay padlocked until the profile level is reached. Linux continues to
   use `patchelf` by default. Windows uses the strict in-place injector, which reuses a
   verified alias string and spare dynamic-table slot without moving code or changing any
   patch offset. Either route can be selected explicitly with `--needed patchelf` or
   `--needed inplace`.
2. Build the hook: `armv7a-linux-androideabi21-clang -shared -O2 -fPIC -Wl,-soname,libdothook.so
   -o tools/nativehook/libdothook-armeabi-v7a.so tools/nativehook/hook_arm32.c tools/nativehook/inapk_server.c -llog`.
   Keep API level 21 for old 32-bit phones and `-Wl,-soname,libdothook.so` because
   `libil2cpp.so`'s `DT_NEEDED` names `libdothook.so`. The resulting `.so` is a local build
   artifact and must not be committed.
3. Build the APK: `legible run Server/build_phone_apk.lbl --abi armeabi-v7a ...`. It embeds the
   matching hook and, by default, drops the arm64 libraries. That last part matters: Android
   prefers arm64 whenever it is present, so an APK containing both would load the unpatched
   64-bit library and ignore the offline build entirely.

One difference from the arm64 build is deliberate: `hook_arm32.c` carries only the
behaviour fixes, not the `EB.Dot.*` key logging. The data authoring loop runs on arm64,
and both builds parse the same JSON, so keys discovered there apply unchanged.
The arm64 distant-opponent ranged-attack nudge is also not yet ported to ARMv7: its mapped
methods, fields, A32 prologue relocation, and hard-float `dT` ABI still require verification.

A [self-contained bundled-server build](#building-a-self-contained-apk-bundled-server-no-pc)
also works for ARMv7.

Two of the fixes could not be ported by translating an address, because neither one is a
method address. Both were re-derived against the 32-bit binary instead, and the derivation
is written out where the fix lives so it can be re-checked:

- `SETACTFIX` (the combat input-buffer window) reaches the game clock through a `.got`
  slot, and the two builds lay out their GOTs differently. Rather than translate it, the
  chain is read back out of `QueuedAction.HasAction`, which necessarily reads the same
  clock it compares against. Two independent sites in the 32-bit binary agree on the slot.
- `FIXSYN` (the synergy-bonus null branch) has no throw block to re-point on ARM32: the
  compiler emits the null check as a call to the throw helper that only falls through. The
  call site is reachable only when the field is null, so overwriting it with a jump to the
  empty-list return is the same fix.

`patches/abi_map.lbl` is what makes this tractable. Both libraries are generated from the
same `global-metadata.dat`, so it can pair the two Il2CppDumper dumps and translate any
arm64 address or field offset to its armv7 equivalent. Run its `verify` mode first: it
cross-checks two independent pairings (method name, and metadata order) against each other.
Do not translate addresses by hand, and do not trust a nearest-symbol guess for generic
methods -- several reference-type instantiations share one body.

Run it with `legible run
patches/abi_map.lbl <a64_dir> <v7_dir> <verify|method|fields> [<address> ...|<type> ...]`, where
`method` takes addresses and `fields` takes type names. This requires a `legible` interpreter
built after the argument-passthrough change; earlier builds reject arguments after the file name.

`find_callers.lbl` and `find_str_ref.lbl` find callers and string references, respectively.
Run them from the directory that holds `extracted/` and
`il2cpp_out/`, as `legible run patches/find_callers.lbl <0xADDR> [<0xADDR> ...]` and
`legible run patches/find_str_ref.lbl <0xADDR>`. `patches/disasm_fn.lbl` is the annotated ARM64
function disassembler; run it from that same directory as `legible run patches/disasm_fn.lbl
<offset_hex> [num_bytes_hex]`. Its two script.json progress lines go to stdout because Legible
has no stderr builtin. Run the native patcher as
`legible run patches/patch_il2cpp.lbl <so> [--abi ...] [--apply] [--needed ...] [-o ...]`.
It accepts the same flags, but its error messages go to stdout because Legible has no stderr builtin, and it does not reproduce argparse's `--help` or usage-error text. The four former Jython Ghidra scripts are split into Legible data halves (`tools/apply_labels.lbl`, `tools/find_xrefs.lbl`, and `tools/decompile_targets.lbl`) and Java `GhidraScript` shims under `tools/ghidra/`, because Ghidra executes only JVM-hosted scripts. Run each workflow as `legible run tools/ghidra_run.lbl <labels|xrefs|decompile>` with `GHIDRA_HOME` set to the Ghidra installation; `GHIDRA_PROJECT_DIR`, `GHIDRA_PROJECT_NAME`, and `GHIDRA_BINARY` optionally override the project directory, project name, and imported binary. The workflows use `il2cpp_out/labels.tsv`, `xref_targets.norm.tsv`, `xrefs_raw.tsv`, and `decompile_targets.norm.txt` as intermediate files; their final `il2cpp_out/xrefs_out.txt` and `il2cpp_out/decomp_out.c` artifacts retain the Jython scripts' exact byte formats. Ghidra is not installed on this development machine, so the Java halves are unexecuted and uncompiled here; the Legible data halves are covered by `tools/test_ghidra_tools.lbl`.
The Frida drivers are `tools/frida_attach.lbl` and `tools/frida_run.lbl` (ported from the former `tools/frida_attach.py` / `tools/frida_run.py`); they require a `legible` binary
built with `--features frida`. Run them as `legible run tools/frida_attach.lbl [script.js] [out.log]
[boot_s] [run_s]` or `legible run tools/frida_run.lbl [script.js] [out.log] [boot_s] [run_s]`. Their
adb path defaults to `/home/darabat/Android/Sdk/platform-tools/adb` and can be overridden with a
non-empty `ADB` environment variable. Frida message delivery is polled rather than callback-driven,
and non-string `send` payloads are rendered as JSON rather than Python `str()`; the default
`tools/hook_dot.js` only sends console-log messages, so the latter difference is theoretical for
its normal use.
The Python originals of the Ghidra and Frida tools were removed once their Legible and Java
replacements were verified; they remain recoverable from git history at commit `d636dae`.
`Server/build_phone_apk.lbl` ports the phone APK builder: run
`legible run Server/build_phone_apk.lbl <source.apk> <destination.apk> [--server-host H] [--scheme https|http] [--server-port N] [--abi arm64-v8a|armeabi-v7a] [--keep-other-abi] [--patched-il2cpp PATH] [--bundle-server]`.
The arm64, armv7, and bundled outputs have been compared byte-for-byte with the Python builder. The source APK's non-signature entries are all `ZIP_STORED`, so the Legible ZIP reader/writer needs no compressor. A newly added ZIP entry uses UTC rather than Python's local-time timestamp; set `SOURCE_DATE_EPOCH` (and `TZ=UTC` for the Python comparison) for deterministic byte-identical output. The Python builder `Server/build_phone_apk.py` was removed once the Legible port was verified against it; it remains recoverable from git history at commit `55a5a6b`, for example `git show 55a5a6b:Server/build_phone_apk.py`. The request-synthesis layer and both listeners are ported as
`Server/fakeserver.lbl` and `Server/run_local.lbl`: run
`legible run Server/fakeserver.lbl --http 80` or `legible run Server/fakeserver.lbl --https 443`,
or use `legible run Server/run_local.lbl` / `legible run Server/run_local.lbl --https`.
The TLS listener uses `Server/certs/server.pem` through Legible's `http_start_https`.
Each Legible process holds one listener and has no threads, so HTTP and HTTPS run as two
processes rather than Python's two threads; this is a design difference, not a port limitation.
The Python original `Server/fakeserver.py` was removed once the Legible listeners were
verified; it remains recoverable from git history at commit `23950a3`, for example
`git show 23950a3:Server/fakeserver.py`.
The Python original `Server/gamedata.py` was removed once `Server/gamedata.lbl` was
verified to regenerate `Server/responses/` byte-identically; it remains recoverable from
git history at commit `23950a3`, for example `git show 23950a3:Server/gamedata.py`. With
the Python gone, the committed artifacts are the oracle: `Server/responses/` and the baked
payload (4,571,404 bytes, 9366 entries at listen port 8080).
`Server/export_payload.lbl` now builds the byte-identical in-APK payload from the
response data and Legible server modules: run
`legible run Server/export_payload.lbl --out <file> [--listen-port N]`. Its Python
counterpart `Server/export_payload.py` was removed once the port produced a byte-identical
payload; it remains recoverable from git history at commit `3488449`. `Server/run_local.py`
was removed the same way and is recoverable at commit `f8e2f22`. As elsewhere in Legible,
errors go to stdout (there is no stderr builtin),
and it does not reproduce argparse's `--help` or usage-error text.
`Server/test_inapk_server.lbl` ports the native in-APK wire regression: run
`legible run Server/test_inapk_server.lbl`. It compiles the two C harnesses with `cc` and
drives the native server with `curl`, because Legible has no socket builtin and its HTTP client
builtins do not support HEAD, custom headers, or connection reuse, so the test drives `curl`
through `shell_exec`. The test
builds the payload once and restarts the harness between its two server tests (rather than
Python's per-test rebuild), because `build_payload` costs roughly 50--60 seconds in Legible.
The Python test suite (the former game-data, fake-server, payload, APK-builder, in-APK-server,
and quest-walk tests) was removed once its Legible replacements were verified; the files
remain recoverable from git history at commit `23950a3`, for example
`git show 23950a3:Server/test_gamedata.py`. The equivalent tests now run as
`legible run Server/test_<name>.lbl`.

## The gotchas that will eat your time

These are the ones that cost me hours. They are written down so they do not cost you
the same.

- The runtime hook loads only through a dependency entry that the pristine library does not
  have. The patch script builds from the pristine library, so without re-adding that entry
  the hook is silently never loaded and login just hangs. The patch script now re-injects it
  on every build. If the hook ever seems dead, the first thing to check is that the patched
  library actually references `libdothook.so`. The exact bytes and offsets are documented in
  the patch script and in `TECHNICAL_NOTES.md`.
- Frida does not work if you are using LDPlayer9/Bluestacks for testing. The emulator translates ARM to x86, and Frida crashes under that translation. The whole reason the project uses a pure byte overwrite inline hook is that it survives where Frida does not. Do not waste time trying to make Frida behave.
- The device network mounts do not survive an emulator reboot. The hosts redirect and the CA
  trust are bind mounts. After any restart of the emulator you must re-run
  `provision_ldplayer.sh` or nothing will connect.
- Install with `adb install --no-incremental`. Modern `adb` prefers an incremental install,
  which mounts the app's native library directory from an `incremental-fs` image. That
  directory is then read-only even to root, so pushing the patched `libil2cpp.so` and the
  hook into it fails with "Permission denied" while everything else looks normal. This
  matters most when switching ABIs, because that requires a reinstall
  (`adb install -r --no-incremental --abi armeabi-v7a ...`).
- Inside Sparx error payloads the field is `err`, not `error`. Using the wrong one produces
  responses the client silently ignores or mishandles.
- Interactive tutorial prompt states loop forever offline. Do not try to answer a tutorial
  request to satisfy it. Instead remove the condition that triggers the tutorial in the first
  place. The shield tutorial freeze was fixed this way, by giving the player the resource
  whose absence triggered it, rather than by answering the tutorial.
- Live 3D content rendering under the emulator is fragile. Models do render, but this is the
  shakiest area and is sensitive to the emulator's graphics backend and texture settings.
  This is an emulator graphics issue, not a data issue.


## If you want to actually revive it: rebuilding the backend

This is the real work, and it is large. Here is the shape of it and where to start.

The goal is to recreate, by hand, the server side content that used to be streamed to the
client: the quests and missions, the maps and their enemy lineups, the full roster with each
bot's stats and abilities, the combat formulas, and the economy. None of this exists anymore,
so all of it has to be authored fresh, in the exact shapes the client expects.

The method that works is the loop this project is built around. Run the game with the hook
attached. The hook logs every key the client reads. When the client asks for something you
have not provided, you see exactly what it wanted. You then synthesize a response in the
right shape, drop it in `Server/responses/` or add it to the dynamic handler in
`Server/fakeserver.lbl`, restart, and verify the client accepts it and moves forward. Repeat. Every
screen in the current build was brought up this exact way. `re_notes/dump.cs` tells you the
shape of each structure before you even run, because it lists every field the client reads.

A sane order to attack it:

1. Persist quest completion and author the reward contract for the ten authored STORY
   encounters.
2. Add original per-bot ability definitions and test their in-fight effects. The normal attack
   and special-damage data already have a working generated path in `Server/gamedata.lbl`.
3. Add missions, maps, and opponent lineups one small path at a time, using the runtime hook
   and `re_notes/dump.cs` to establish the client contract.
4. Add the economy and wider progression only after the missions and rewards that use them.

Be realistic about scale. Even for games where fans saved the live server data before
shutdown, standing up a private server is a long project. Here there is no saved data to
start from, so every number and every ability has to be researched or reinvented and then
verified against the client. This is a multi-person, multi-year effort if the goal is the
real game. That said, the path is no longer a mystery. The offline boot, a first STORY fight,
and the feedback loop are working; the type model is dumped. What remains is a large amount
of careful, original data authoring and verification.

Start with `TECHNICAL_NOTES.md`. It is the deeper technical reference, with the exact
patches, the recovered data shapes, and the specific findings, in more detail than this
README. Then run the loop.

Good luck. It is a real machine now. It just needs its content rebuilt.

## STORY board: varied encounters and the Nemesis Prime boss

The ten fights on the `1.1.1` STORY path are no longer a wall of Sharkticons. Only rows 1
and 4 keep one; the rest of the path is Kickback, Waspinator, Soundwave, Cyclonus,
Motormaster, Bludgeon, and Necrotronus, in that order, each with its own tile label. Every
one of those blueprints was already in the shipped roster with working art, so the variety
needed no new assets.

Row 10 is now **Nemesis Prime** (`nemesisprime_gs_voyager2015`) rather than a Sharkticon
Brawler, authored with 28000 max HP and 3000 attack so the pre-fight screen shows a power
of 31000. `TECHNICAL_NOTES.md` explains which server field that number comes from and why
that is the field the client actually displays.

The mission's four authored dialogue sets ship under the `dialogueTable` key, and each set
is a bare array of entries. Tiles carry the ids: row 1 plays `arrival_intro`, row 5 plays
`midpoint_rally`, and row 10 plays `final_stand` before the boss and `shore_secured` after
it. Entering a tile that carries an id opens the cinematic overlay in game.
