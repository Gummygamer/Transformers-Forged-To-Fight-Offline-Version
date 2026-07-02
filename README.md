# Transformers: Forged to Fight, offline revival handoff

This package contains a working offline boot of Transformers: Forged to Fight, plus
every tool, patch, and reverse engineering note used to get there. It is meant to be
picked up by someone who has the time and energy to take the next and much larger step,
which is rebuilding the game's server side content from scratch. Everything here is
documented so you do not have to start from zero the way I did.

Read this whole file before you touch anything. The "Gotchas" section in particular will
save you days.


## What actually works right now

The game boots completely offline and reaches its real interactive home screen with no
live servers anywhere. From the home screen the menus navigate without crashing: the
base, the bots roster (with an owned bot present in the account), the fight mode select,
the crystals screen, and the usual popups and tips. The full login flow completes, every
online subsystem connects, and the first time experience and tutorial gates are cleared.
The scripted intro fight (Optimus vs Starscream) even gets far enough to begin loading
the battle, and the 3D character models render and animate.

This was the hard part and it is solved. The client itself is alive again offline.


## What does not work, and why

Actual gameplay does not work. Story shows no missions, and fights cannot fully load.
This is not a bug and it is not something a patch can fix.

Forged to Fight was fully server authoritative. The app on the phone is essentially a
screen with controls. Almost nothing about the game lived in the app. Every mission, every
fight, every enemy lineup, the entire roster's stats and abilities, the economy, and all
the balance lived on Kabam's servers and were streamed to the device each session. When
the servers were shut down in early 2020 that content database went with them, and it was
never released or publicly archived anywhere I can reach.

So the situation is split cleanly in two. The art and audio survived, because they ship
inside the app (see `re_notes/ASSET_INVENTORY.txt`). Every character is a full Unity asset
bundle holding the model, textures, rig, animation clips, animator controllers, effects,
and audio. The environments, buildings, UI, portraits, cutscenes, and dialogue are all
there too. What did not survive is the data that told the game which of those assets to
use, how to assemble them into a fight or a mission, and what every bot's numbers actually
were. All the pieces are present. There is just nothing left that knows how to put them
together. Rebuilding that is the whole job that remains.


## How the offline boot works

There are four moving parts. Together they let the unmodified game think it is talking to
Kabam.

1. Native binary patches. The game is Unity IL2CPP, so the logic lives in a compiled ARM
   library, `libil2cpp.so`, not in editable script files. `patches/patch_il2cpp.py` rewrites
   six functions in that library to get past the dead server checks: it defeats two
   certificate pinning paths so our own TLS cert is accepted, forces the manager
   registration block to run even though the live config is null, lets login succeed with
   our local device session, and silences the subsystem fatal errors that would otherwise
   pop the "failed to log in" dialog. It also re-injects a single dependency entry (see the
   Gotchas section) so the runtime hook actually loads. The output is `libil2cpp.patched.so`.

2. A fake Sparx server. `server/fakeserver.py` stands in for Kabam's backend. It listens on
   TLS 443 and plain HTTP 80 and answers the game's API calls. Canned responses live in
   `server/responses/`, one file per endpoint, named by method and path, for example
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
sends it to the PC, the fake server answers with a response from `server/responses/`, the
patched library accepts the cert and the answer, and the hook logs what was read. That loop
is how every screen in this build was brought up.


## What is in this package

```
README.md                     this file
TECHNICAL_NOTES.md            the deeper technical reference: patches, recovered data shapes, findings
patches/
  patch_il2cpp.py             the six native patches plus the dependency re-injection
  disasm_fn.py                helper: disassemble a function at an offset
  find_callers.py             helper: find callers of a function
  find_str_ref.py             helper: find references to a string
server/
  fakeserver.py               the fake Sparx server
  gen_certs.sh                regenerate the TLS cert and CA (run this, see below)
  setup_device.sh             device side network and trust setup reference
  iterate.sh                  quick restart and capture loop
  responses/                  one JSON file per endpoint the game calls
tools/
  provision_ldplayer.sh       one shot re-provision of the emulator to the working state
  setup_arm64.sh              toolchain setup notes
  decompile_targets.py        drive the Ghidra headless decompiler at chosen offsets
  find_xrefs.py               cross reference search over the binary
  apply_labels.py             apply IL2CPP symbol labels
  light_analyze.py            lightweight static analysis helpers
  frida_attach.py             Frida helpers (kept for reference, see the libnb note)
  frida_run.py
  hook_dot.js
  nativehook/
    hook.c                    source of libdothook.so, the runtime hook
    libdothook.so             prebuilt hook, arm64
    deploy.sh                 build and deploy the hook
    relaunch_and_capture.sh   relaunch the game and capture logs
  hook/dothook.c              earlier hook variant, kept for reference
re_notes/
  dump.cs                     the full IL2CPP dump: every class, method, and field in the game
  decomp_out.c                decompiled bodies of key functions
  decompile_targets.txt       the offsets worth decompiling
  ASSET_INVENTORY.txt         what art and audio already ships inside the app
```

`re_notes/dump.cs` is the single most valuable file for the work that remains. It is the
complete type model of the game: every class, every method, and crucially every data field
the client reads from the server. It is your map of the entire backend API. When you need
to know what shape a response should be, the answer is in there.


## What is not in this package, and where to get it

These were left out on purpose, because they are large, or copyrighted, or secret, or you
should generate your own.

- The APK itself (`com.kabam.bigrobot`, version 9.2.0). It is about 800 MB. Source your own
  copy. The package name and version are in `TECHNICAL_NOTES.md`.
- The original `libil2cpp.so` and the game assets. Both come straight out of the APK. Unzip
  the APK, the library is under `lib/arm64-v8a/`, the assets are under `assets/`.
- The TLS cert and CA. Do not ship private keys. Run `server/gen_certs.sh` to make your own
  matching pair, then point the device trust store at the new CA.
- The patched library. Regenerate it: run `patches/patch_il2cpp.py` against the original
  `libil2cpp.so` from the APK.
- Frida server and Il2CppDumper. Both are public tools. Il2CppDumper is what produced
  `re_notes/dump.cs` from the APK's library and global metadata.
- The Android NDK (r26 was used) and JDK 21, needed to build the hook and to run the Ghidra
  headless decompiler.


## How to run what exists today

You need the APK installed on an ARM translation capable emulator (LDPlayer 9 was used, with
root and writable system), Python on the PC, and the items from the section above.

1. Generate certs once: `bash server/gen_certs.sh`.
2. Build the patched library once: `python patches/patch_il2cpp.py path/to/original/libil2cpp.so --apply`.
3. Build the hook once if you want to rebuild it, otherwise use the prebuilt one. See
   `tools/nativehook/deploy.sh`.
4. Start the fake server on the PC: `python server/fakeserver.py`. It needs to be reachable
   on ports 443 and 80 from the emulator.
5. Provision the device: `bash tools/provision_ldplayer.sh <your-PC-LAN-IP>`. Re-run this
   after every emulator reboot.
6. Wait about 45 seconds, then tap the title screen to log in. You should reach the home
   screen.

If it hangs at login, check the very first item in the Gotchas section before anything else.


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
right shape, drop it in `server/responses/` or add it to the dynamic handler in
`fakeserver.py`, restart, and verify the client accepts it and moves forward. Repeat. Every
screen in the current build was brought up this exact way. `re_notes/dump.cs` tells you the
shape of each structure before you even run, because it lists every field the client reads.

A sane order to attack it:

1. Get a single complete fight to load and run end to end. This is the highest value target
   because combat is the core of the game and it exercises the most server data at once. You
   need the participant definitions, their stats and abilities, and whatever the fight init
   path asks for. The intro fight already starts loading, so that is the place to push first.
   Decompile the fight init and combat data paths (use `decompile_targets.py`) and read off
   the exact fields.
2. Reconstruct the roster data model fully, one bot at a time, stats and abilities included.
   The art for each bot already exists in the bundles listed in `ASSET_INVENTORY.txt`, so you
   are only authoring numbers and ability definitions, not assets.
3. Rebuild the quest and map structures so Story stops being empty. The base structure was
   partially cracked already, see `TECHNICAL_NOTES.md` for the keys.
4. Fill in the economy and progression last, once fights and missions exist to spend it on.

Be realistic about scale. Even for games where fans saved the live server data before
shutdown, standing up a private server is a long project. Here there is no saved data to
start from, so every number and every ability has to be researched or reinvented and then
verified against the client. This is a multi-person, multi-year effort if the goal is the
real game. That said, the path is no longer a mystery. The boot is solved, the feedback loop
exists, the type model is dumped, and the assets are intact. What remains is a very large
amount of careful data reconstruction, not more reverse engineering of the unknown.

Start with `TECHNICAL_NOTES.md`. It is the deeper technical reference, with the exact
patches, the recovered data shapes, and the specific findings, in more detail than this
README. Then run the loop.

Good luck. It is a real machine now. It just needs its content rebuilt.
