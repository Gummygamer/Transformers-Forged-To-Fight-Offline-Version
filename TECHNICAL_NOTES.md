# Technical notes

This is the deeper technical reference behind the offline boot. The README explains the
overall shape and how to run things. This file records the specific findings: the binary
patches, the data structures the client expects, and the gotchas, so the next person does
not have to rediscover them.

## The app

Package `com.kabam.bigrobot`, version name 9.2.0, version code 123129100. Minimum SDK 23,
target SDK 30. Launcher activity `com.explodingbarrel.Activity`. The shipped build is a
third party repackage with injected ad SDKs and a packer present in the lib folder, but the
app entry itself is not wrapped by the packer, so the core is patchable.

## Engine and binary

The game is Unity IL2CPP. The game logic is compiled into a native library,
`lib/arm64-v8a/libil2cpp.so`, and the type information lives in
`assets/bin/Data/Managed/Metadata/global-metadata.dat`. There is no managed assembly to
edit, so all behavior changes are made by patching the native library.

For this binary the runtime virtual address equals the file offset, which makes patching by
offset straightforward.

The metadata is version 27, which hides string cross references from static tools. The type
model is fully intact though. Decompiling a class constructor that takes a dictionary
reveals the exact keys that response is parsed for. This is how most of the data shapes
below were recovered without running anything. When you need to know the shape of a
response, find the constructor or the parser for it and read the keys off the decompiled
body.

## The native patches

All six are in `patches/patch_il2cpp.py`, applied by file offset, and the script prints a
before and after disassembly of each so you can confirm them. In short:

1 and 2. Certificate validation. Two TLS code paths are forced to accept any certificate, so
the fake server's self signed certificate is trusted.

3. Manager registration. The block that registers around forty managers is gated behind a
live config object that is null when there is no server. The branch is redirected so the
registration runs anyway.

4. Login authenticators. The login coroutine fails out if zero authenticators are
registered, which is exactly the offline state. The failure branch is skipped so login
completes on the local device session token.

5 and 6. Subsystem fatal errors. A subsystem that cannot reach its data would otherwise pop
the failed to log in dialog. Both the polling check and the fatal tail call are neutralized,
so a failed subsystem is treated as connected and the boot continues.

The script also re-injects the dependency that loads the runtime hook. See the gotcha below.

## The login fix that unblocked everything

The single most important data finding. The login response user object must carry a valid,
non zero `uid` field. Not `id`, `uid`. The user lookup reads the id through the `uid` key,
and if the resulting id is not valid it returns a null user, which then makes the client
throw a null reference during login. The working shape is in
`server/responses/POST__auth_login.json`. The user object also reads `auth_data`,
`server_tag`, and `userToken`.

## Response envelope

The top level envelope is `{"error": null, "result": ...}`. Note that inside Sparx error
payloads the field is spelled `err`, not `error`. Using the wrong one produces a response
the client silently mishandles.

## Recovered data structures

These are the shapes the client parses. They are served from `server/responses/` or
synthesized in `server/fakeserver.py`.

`/bcg/getUserData` is `{userData: {maxes...}, updates: {heroes: [...], savedTeams: [],
activeTeams: []}, deletes: {}}`. The update handler reads `userData.blueprintsMax`,
`teamSizeMax`, and `teamCountMax`. Owned heroes arrive through `updates` and are processed
by the user data update handler, not through `userData` directly.

`/bcg/getLoginData` carries the config blob: `characters`, `blueprints`, `heroes`, and
`evoBlueprints`.

`/account/data` is the large player state object. Its top level keys are siblings, not
nested: `privacySettings`, `invitesDB`, `invites`, `sentInvites`, `invite_max_helps`,
`imri`, `featuredhero`, and `res`. The player resources live under `res.user_info`, for
example `rt` for raid tickets, `energy`, and `gold`, each an object with `v` and `max`.

The home screen is the base. It is built from `/base/active` with the keys
`user.AvailableBuildings`, `user.Buildings`, `user.Sockets`, and `user.Base`, where
`user.Base` is an active mission structure.

## Roster entity type gotcha

The roster reads entities of type `bot`. For an owned character to appear in the roster,
both the user data hero entity type and the blueprint entity type must be `bot`, not `hero`.
This is what fixed the empty roster grid.

## Why combat is the wall

The damage table, called `attackValues`, was server side balance data and is gone. The move
asset bundles inside the client hold only animation and timing data, not damage numbers. So
even with a character fully loaded, a fight has no numbers to run on. Recreating
`attackValues` and the ability definitions is central to any real revival.

## The first time experience fight

The scripted intro fight needs its participant blueprints present in the config, both the
player bot and the enemy. The blueprint is referenced from the intro prefab, but the actual
blueprint data has to exist in the config or the blueprint lookup returns null and the fight
fails to load. This is the smallest possible end to end fight and is the best first
milestone for anyone continuing the work.

## Tutorials freeze offline

Interactive tutorial prompt states loop forever with no server to answer them. Do not try to
satisfy a tutorial by answering its request. Instead remove the condition that triggers it.
The shield tutorial freeze was fixed by giving the player raid tickets, because that
tutorial triggers when raid tickets drop below one.

## The runtime hook

Source is in `tools/nativehook/`. It is a plain inline hook, a sixteen byte overwrite plus a
trampoline, installed by a worker thread before login runs. The emulator translates ARM to
x86 lazily, so the patched code is translated on first call and the hook takes effect.
Frida does not survive this translation and crashes on attach, which is why a hand written
inline hook is used instead.

The hook is loaded through a dependency on `libdothook.so` that is added to the patched
library. The original library does not have this dependency, so the patch script re-adds it
on every build (see the gotcha below).

The hook logs every key the client reads. Important detail: the deep parsers use a fast
path whose key argument is a path structure, not a plain string. The key name is the single
path field inside that structure. The hook is the discovery loop. Seed a response, run,
read which keys the client asked for, synthesize the next piece, verify, and repeat. Every
screen in this build was brought up that way.

## Decompile workflow

Use a headless Ghidra project over the library. Import once with the function name labels
and string labels from the dumper output, with analysis disabled to keep the import fast.
Then decompile specific offsets listed in a targets file. Reading a decompiled body, a
pattern where `if (X != 0)` guards a path and there is a throw at the end is the IL2CPP null
check. X being null is what throws. String keys appear as placeholder symbols whose index
resolves in the dumper's string literal output.

## Known remaining frontiers

1. Live 3D content rendering under an emulator. The models do render, but this area is
sensitive to the emulator graphics backend and to texture compression settings. This is a
graphics matter, not a data matter.

2. The server content database. Quests, maps, opponents, the full roster numbers, the
abilities, and the economy. This is the large reconstruction effort, and it is the actual
revival.
