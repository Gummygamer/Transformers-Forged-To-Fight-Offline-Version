# 2.0.2 Mono substitution plan

This began as a design record. A disposable candidate has since been packaged and
installed for direct evidence; the candidate is not a release artifact and this document
does not claim playability or runtime compatibility.

The input APK was checked before analysis:

`com.kabam.bigrobot_2.0.2-812553_minAPI19(armeabi-v7a,x86)(nodpi)_apkmirror.com.apk`
has SHA-256
`61c1860df9d5bb64ab28934b0fd6954c71c5410887f66260917351820b08aca4`.

The managed payload is under `assets/bin/Data/Managed/` and contains 17 DLLs. The native
payload has `armeabi-v7a` and `x86` variants, including `libmono.so`, `libunity.so`, and
`libmain.so`. A managed substitution must leave those native libraries, ABI directories,
Android manifest, resources, and Unity data files intact.

## Smallest source replacement set

The metadata-backed replacement report uses `Assembly-CSharp` and
`Assembly-CSharp-firstpass` as explicit roots. They are the two game-source assemblies in
the recovered project graph. Their retained managed reference closure is:

```text
Assembly-CSharp
Assembly-CSharp-firstpass
Fabric.Core
Facebook.Unity
Facebook.Unity.Settings
ICSharpCode.SharpZipLib
Mono.Security
NBidi
System
System.Core
UnityEngine
crypto
mscorlib
```

This closure is a dependency inventory, not a rebuild list. Existing dependency DLLs may
be preserved if their assembly identities and contracts remain compatible. In particular,
the original `crypto` and SharpZipLib IL does not contain the constructors or modern
interface members that Roslyn requires, so adding those members to a rebuilt DLL would
change its API. Their original binaries remain the current preservation choice.

## Identity and file requirements

Any future replacement must preserve the original managed path and filename, assembly name,
version, culture, public-key identity, referenced assembly names/versions, embedded resource
names, and Unity serialization-facing type and field layout. The PE metadata profile observed
in the APK is `v2.0.50727`; `mscorlib`, `System`, and `System.Core` identify as version
`2.0.5.0`. The current Roslyn audit against .NET 2.0/3.5 reference assemblies is only a
source check and does not prove Mono loader compatibility.

The audit itself uses Roslyn from .NET SDK 8.0.422 with `/langversion:12`, deterministic
output, and Microsoft .NET Framework 2.0/3.5 reference assemblies. This is not the
historical Unity/Mono compiler profile. The APK evidence establishes `v2.0.50727`
metadata and `I386`/`ILOnly` managed PE images, but does not identify the original C#
compiler or prove that Roslyn output loads in the embedded Unity Mono runtime.

The substitution process must also preserve the APK's native ABI set, `libmono` and Unity
runtime pairing, native plugin names, Android manifest/package identity, resource table,
asset paths, ZIP alignment, and signing/re-signing requirements. No framework reference
assembly used for compilation may be copied into the APK as a runtime replacement.

## Safe future validation sequence

1. Freeze the original APK hash and export manifest hashes.
2. Compile only an isolated source snapshot and record the output PE metadata report.
3. Compare public/internal declarations, inheritance, fields, attributes, method signatures,
   resources, and assembly references against the original metadata report.
4. Make a disposable APK working copy; substitute only the explicitly approved DLLs under
   `assets/bin/Data/Managed/` and leave native/data entries byte-for-byte unchanged.
5. Verify ZIP structure, ABI entries, manifest/package identity, and signatures before any
   device operation.
6. Run the original Mono client and capture loader/startup failures before testing server
   requests. Then validate the 2.0.2 request/response contracts independently; the 9.2
   IL2CPP server path is not evidence of Mono protocol compatibility.

The current disposable validation uses only the two approved game DLL replacements and
can route the isolated `Setup.ApiEndPoint` to the reconstructed LAN server. Because the
physical test device cannot authenticate this APK's debug certificate with Google Play
Games, the isolated compile option `--disable-google-play-games` suppresses optional
`GooglePlayGamesManager` registration; it does not modify device Google accounts. The
candidate reached the reconstructed server's authentication, account, BCG, tutorial,
inventory, quest, base, and PVP-login routes, but still stopped at the client's generic
connection-failure screen. This remains runtime evidence, not a playability claim.

Until those steps succeed, the result is a source/metadata reconstruction and not a
playable or runtime-compatible client.
