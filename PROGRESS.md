# Recovered 9.2 rebuild status

Status: in progress. No rebuilt APK has completed yet.

## Latest recovery/build and storage check (2026-10-10)

The patched Cpp2IL operator-lowering build now launches through the .NET 10 path
recorded in its manifest and has regenerated all 64,447 methods successfully. AssetRipper
has exported a fresh Unity project; a separate stage has the new generated assemblies
and binding wrappers. The first ARM64 IL2CPP attempt crashed with Unity exit -11 during
asset import, before C# script compilation or IL2CPP generation. The last log repeatedly
reports missing native-method resolutions while importing gameplay assets; the kernel
log has no matching USB, ext4, or OOM errors. This has not produced an APK and does not
yet validate whether the new operator lowering reduces IL2CPP diagnostics.

The typed `Properties` / `RenderProperties` repair cleared the earlier Unity native
crash. v16 reached IL2CPP code generation but failed with 262 diagnostics across 16
distinct methods. Rebuilt `AnimatePosition`, `AnimateRotation`, and `AnimateScale::LoopReset`
from their native 9.2 ARM64 traces; those methods no longer appear in v17's failure set.
v17 still has 262 IL2CPP diagnostics in other recovered methods, including malformed
by-reference parameters and struct operations. No APK was produced.

The external SanDisk SSD had a historical ext4 filesystem CRC warning and a failed SCSI
cache sync in the kernel log. After confirming no open users, unmounted it and ran
`e2fsck -f -p`; it exited successfully without reporting filesystem repairs, then
remounted normally. A 64 MiB durable write/read/hash/delete check passed, with no new
I/O or ext4 errors; the drive now has about 118 GiB free after cache cleanup. SMART is unavailable through this USB bridge,
so the drive's internal health cannot be established from this host. The logs do not
prove a device failure, but the earlier USB/cache-sync event warrants clean unmounts and
monitoring. The ignored repo `build` link targets external `build/workspace-build`; Unity
analysis stages live under adjacent `build/analysis/rea/9.2`, so keep using resolved
explicit paths for both.

`build/workspace-build` is the symlink target used by day-to-day tools and contains its
own historical `analysis` folder. Current 9.2 recovery stages are in the sibling
`build/analysis/rea/9.2` under the external build root; these are separate trees. Recovery
and build scripts now preflight the configured external volume before writing and fail
closed if it is absent, resolves to the root filesystem, is read-only, or has less than
10 GiB free. The recovery script reads the pinned Cpp2IL build manifest to launch its
apphost through the matching local .NET host.

The external-drive audit found the repo's ignored `build` area had grown to about 188 GiB,
including about 141 GiB under `analysis/rea/9.2` and 39 GiB under `workspace-build`.
Repeated Unity attempts had accumulated regenerable `Library` and `Temp` caches. Removed
about 32 GiB of those caches from superseded analysis projects; raw exports, staged
assets, APKs, source, and the active project were kept. A few remaining cache directories
are root-owned and were left untouched.

Removed the now-redundant intermediate stage copy after the attempted build, preserving
the untouched AssetRipper export, generated DLL output, and actual working stage. Updated
the recovery manifest to point at the retained working stage. The external volume now has
about 121 GiB free.

## Current blocker

No faithful APK has been produced. The current blocker is malformed recovered method bodies and ARM64 aggregate/by-reference lowering in Unity IL2CPP. Throwing diagnostic fallbacks do not count as recompilation success.

## Latest diagnostic

The original TypeDb contained 898 duplicate full type names between script wrappers and recovered DLLs. A throwaway build experiment namespaced the `EBRBParticleSimulation` wrapper uniquely, preserved its GUID and serialized values, and removed that type-name collision. The generator now emits GUID-scoped wrapper namespaces to prevent these duplicate names in future staged projects.

The follow-up GDB trace found a `System.Object` instance (`instance_size=0x10`) immediately before the `Gradient`. Cpp2IL had emitted `new object()` at both `Simulation` allocation sites even though native 9.2 allocates typed `Properties` and `RenderProperties`. The Cecil repair now restores those four typed allocations; v16 passed this crash and reached IL2CPP generation.

## Rebuild target

- Unity `2020.3.31f1`
- Android ARM64
- IL2CPP
- APK 9.2.0 only; 2.0.2 remains guidance

## Pending source changes

`tools/build_recovered_9_2.py` now supports checking Unity's optional legacy native libraries against the host runtime. `tools/repair_recovered_9_2_import.py` includes evidence-based fixes for recovered ownerless generic references and three malformed recovered empty finalizers. `tools/generate_9_2_script_bindings.py` now emits GUID-scoped wrapper namespaces. These changes remain uncommitted; the repair and build changes still need full validation.

## Latest investigation (2026-10-10)

The clean host rebuild now passes Unity managed assembly reload and script compilation, begins Android IL2CPP player generation, and reaches scene/resource processing. It still exits with SIGSEGV at `0x41700010`; no APK was produced.

A repeated GDB watchpoint confirms the same `GravityModulator` Vector3 write corrupts a live `UnityEngine.Gradient` header during `CreatePlayerOnlyResourceList`. The command's nested class is specifically `EB.Rendering.EBRBParticleSimulation/Properties` from `Assembly-CSharp-firstpass`, with runtime instance size `0x2c`; the sibling `EBRBSimulation/Properties` ambiguity is ruled out. Its descriptor field offset remains `0x1c`.

A pre-build managed reflection probe successfully resolves `Simulation.Properties` and creates an uninitialized instance of the expected class with fields `Gravity`, `GravityModulator`, and `Playback`. This makes a missing class, malformed assembly metadata, or inability of Mono to allocate that type unlikely. The mismatch is in Unity's streamed asset transfer path, where the field destination is a `System.Object` (`0x10` bytes) rather than a `Properties` instance. A lazy allocation breakpoint armed after the first Gradient did not catch the allocation, so the allocation site remains unlocated.

The temporary reflection probe was restored out of the external staging project. No prefab values were edited. The code changes listed above remain uncommitted and the target APK is still pending.

## IL2CPP compatibility diagnosis (2026-10-10)

Moving the diagnostic-only method replacements to the end of the Cecil rewrite pass prevents later generic repairs from restoring bodies Unity has already rejected. Consecutive Unity 2020.3.31f1 ARM64 IL2CPP builds now pass the earlier asset-resource crash when the six `GravityModulator` prefabs are temporarily omitted, but each pass exposes another batch of malformed Cpp2IL methods (16 rejected signatures per latest pass). Throwing fallbacks can make individual signatures pass IL2CPP analysis, but they disable those APIs and are not a faithful recompilation. No APK was produced. The 6 original prefab files and 6 `.meta` files have been restored to the staged project; no serialized values were edited.

The diagnostic-run report accumulates identifiers of methods disabled by those experimental builds. I initially restored the plugin assemblies from untouched Cpp2IL outputs, but the subsequent diagnostic waves applied cumulative throw fallbacks again in the external staging project. After wave 9 that report lists 137 rejected methods. Those fallbacks are diagnostic-only; this staging state is not a faithful client and must be refreshed from untouched generated assemblies before the next fidelity build. No assets or binaries are in Git.

Inspection of the original Cpp2IL `EB.Rendering.EBPrimitives::Sphere` body confirms these are generated-body quality failures, not missing Unity assemblies: the editor's `UnityEngine.CoreModule` resolves `UnityEngine.Vector3` correctly, while the recovered body contains 167 locals plus emitted `Unmanaged memory load` and `Method not found @...` placeholder instructions. Unity's IL2CPP stack analysis then rejects several vector/struct paths. A faithful build needs the Cpp2IL IL-generation output corrected or affected methods reconstructed; blanket stubs only hide this evidence.

## Rea/Rosetta alternative (2026-10-10)

Ran the external Rea checkout's Rosetta IL2CPP lifter against the exact 9.2 ARM64 `global-metadata.dat` + `libil2cpp.so`, selecting `Assembly-CSharp-firstpass`. It resolved 83,550 registration entries and emitted 22,398 method IR dumps plus C# for 23,799 methods across 1,628 types; its summary reported 34 methods with discrepancies. This is useful native-code evidence for Cpp2IL failures, but not a drop-in replacement: the `EBPrimitives.Sphere` output includes unresolved native pointer arithmetic, and the run logged unsupported ARM64 instructions. Its generated C# cannot be imported as-is. Outputs remain on external storage; no recovered game code was added to Git.

Rosetta also completed `Assembly-CSharp`: 33,135 IR dumps and 57 methods flagged with discrepancies. Across both runs, Rosetta emitted a focused IR trace for 43 of the 49 distinct methods reported by the successive Unity IL2CPP logs (7/9 `Assembly-CSharp`, 36/40 `Assembly-CSharp-firstpass`; the remaining 9 errors belong to `Fabric.Core`). This trace coverage is a useful bridge for a future method-body reconstruction pipeline. Rosetta's CLI currently returns exit code 1 even after printing `COMPLETE`, so success must be confirmed from its completion record rather than process status alone.

## ARM64 aggregate-argument diagnosis (2026-10-10)

Diagnostic wave 9 finished without an APK: IL2CPP reported 16 new methods and the cumulative diagnostic fallback list reached 137. The new failures again include `Cannot get stack type` for Unity and game structs. Inspecting the actual recovered `UIGraph.IsInDirection(Vector2, Link, Vector2)` body established a broader input problem: its emitted CIL compares `fromPos` with `3` and fills placeholder strings, while the native ARM64 trace compares `w0` with `3` (the `Link` enum) and carries the two `Vector2` values through SIMD/FP registers `v0`–`v3`. The pinned Cpp2IL `Arm64CallingConventionResolver` classifies only scalar `Single`/`Double` as FP arguments and allocates every struct argument from integer registers. Thus the Cpp2IL argument model is demonstrably wrong for this method; correcting it requires aggregate-aware lowering across parameters, calls, and returns, not merely setting Unity struct value-type flags. Rosetta's generated C# is also inaccurate for this method, so only its native instruction/IR trace is useful evidence. No diagnostic throw fallback is accepted as a reconstruction.

## Clean diagnostic-stub reset (2026-10-10)

Created external stage `full-rebuild-9.2-clean-stubs-reset-20261010` by copying the staged project without Unity caches, then replacing only the five assemblies listed in the diagnostic report from untouched `cpp2il-addressfix5-recovery` outputs. Re-ran `repair_recovered_9_2_import.py` without `--diagnostics`. The new report has zero accumulated IL2CPP diagnostic stubs; the prior stage and its 137-method report remain unchanged. The clean stage retains the 38 deterministic targeted repairs and the repairer's documented SharpZipLib fallback, so it is not yet a faithful client. No Unity build was run on this stage; generated outputs remain external and out of Git. The repair script now only replays previous diagnostic failures when diagnostics are explicitly requested.

## ARM64 HFA entry lowering (2026-10-10)

Built pinned Cpp2IL `b5ad444` with the new ARM64 entry-parameter lowering. The 9.2 native libraries now recover 64,447/64,447 methods, and inspection of `UIGraph.IsInDirection(Vector2, Link, Vector2)` confirms the entry adapter loads the `Link` argument and both `Vector2` arguments from the correct managed positions. This validates that case only; Cpp2IL's full-method count does not establish semantic correctness.

Copied the clean stage to external `full-rebuild-9.2-hfa-entry-v6-20261010`, replaced its two game assemblies with the new Cpp2IL outputs, and ran Unity 2020.3.31f1 ARM64 IL2CPP without diagnostic stubs. Unity imported the asset set, then exited with SIGSEGV (`0x41700010`) while processing recovered prefabs. The log shows repeated invalid CIL in `UIBasicSprite::.ctor`, `EB.MoveEditor.MoveSequencer` and other recovered constructors before the crash; IL2CPP player generation did not complete and no APK was produced. This exposes additional Cpp2IL body/ABI failures beyond the repaired HFA entry case. External outputs and copyrighted assets remain outside Git.

## C#-first constructor reconstruction (2026-10-10)

Used readable C# equivalents to settle the intended behavior before encoding Cecil rewrites.
The `EBRBSimulationChunk` parameterless constructor keeps its CLR-zeroed vector fields and the
two recovered `-1` mapping sentinels. After this repair, the Unity queue no longer reports the
constructor's repeated `stfld` error (1,154 occurrences in v7), and Unity proceeded beyond that
failure. The external source stage was copied before mutation.

For `TrailRenderer.TrailConfig`, the local ARM64 trace establishes the scalar defaults, the two
`Keyframe` values used for `WidthCurve`, and `Color.white`. The first generated rewrite had a
stack-order defect at `stfld WidthCurve`; Unity's next run exposed it. After correcting the IL,
the v10 repair queue contains neither `TrailConfig::.ctor` nor `EBRBSimulationChunk::.ctor`.
Unity imported farther through the project and then exited with SIGSEGV at
`0x5cf83f389385`; no APK was produced. Logs and all generated assemblies/assets remain on
external storage.

Rebuilt the `MoveSequencer` type initializer from its native 9.2 trace and preserved capacity
tables. The first call-site encoding used closed delegate generic arguments and Unity reported a
`MissingMethodException`; comparison with existing `Pool<T>` calls showed that the MemberRef
must encode `Function<!0>` and `Action<!0>`. After correcting that signature, the v12 repair
queue no longer reports `MoveSequencer::.cctor`, and the MissingMethodException is gone. Unity
still exited with SIGSEGV (`0x41700000`) during asset processing, so no APK was produced. The
next highest repeated CIL failure was `DynamicScrollView::.ctor`. The native ARM64 trace records its
intended field writes and defaults: create the item pool and both dictionaries, set
`waitForCreatedItemPresentations` and `isEnabled` true, `TransitionTime` to `1f`, and both
position fields to zero. Its repaired CIL was inspected against those types and call targets. The
v13 Unity build advanced to Android post-processing, then exited with SIGSEGV at
`0x41700010`; no APK was produced. The v14 repair reconstructs `HeroPortrait::.ctor` from the
9.2 native field-write trace: colors, overlay-key map, border heights, rendering/health defaults,
zero size, and overlay list. The 2.0.2 body was not copied; it served only as a value cross-check
for the native trace. Unity no longer reports `HeroPortrait::.ctor` in v14's repair queue, but the
same native crash remains and no APK was produced.

The C# Cecil repair implementation now lives in `tools/RepairRecoveredConstructor.cs`; the
Python command prepares plans and invokes it. Decompiled C# output remains external and is not
checked in. The current Rosetta output includes invalid syntax and unresolved placeholder calls,
so it cannot directly replace the assembly-injection path yet. The C# repair source is directly
buildable as a tool; it is not a full source reconstruction of the game.

The v15 pass additionally reconstructed `CustomRedeemerDisplays.DefaultRedeemerDisplay::.ctor`
from its native 9.2 trace and overlay table. The repair compiles and applies, but Unity again
exits with SIGSEGV at `0x41700010` during Android post-processing before the import queue can
establish whether this repaired constructor cleared its IL failure. This constructor was not the
cause of the repeated native crash. No APK was produced. Stop spending full build cycles on
constructor repairs until the object-type/serialized-resource crash is resolved; use a short, targeted
Unity import reproducer or investigate the Cpp2IL type/serialization mapping that yields a
`System.Object` destination for `EB.Rendering.EBRBParticleSimulation/Properties.GravityModulator`.


## Primitive-family reconstruction and build-cycle change (2026-10-10)

Audited all 15 `EB.Math` graphics/math value types (581 declared methods) and generated
902 overload-specific ARM64 trace dumps for the primitive family and selected consumers.
Decompiler warning markers are widespread, so they are triage signals rather than proof
that every method needs replacement. Reconstructed packed `Color` comparisons,
`BoundingBox.Contains` point/box overloads, and `CameraData.Lerp` from the 9.2 field
layout and native traces. Their generated C# was inspected before the next Unity build.

The v10 ARM64 IL2CPP pass reached player generation and reduced the rejected-method set
from 16 to 15: the three initial repairs disappeared, while `BoundingBox.Contains` for a
sphere remained broken. A follow-up Cecil reconstruction now covers its sphere value/ref
overloads using the native clamp, squared-distance comparison, and per-axis branches.
ILSpy emits the expected C# without warnings in both methods.

Expanded the Color batch from its constructors/comparisons/Lerp to include integer and
Vector4 `FromNonPremultiplied`, packed scalar multiplication and its operator, and normalized
`ToVector3`/`ToVector4`. Their emitted C# was inspected; the integer packing path was corrected
after that inspection caught a channel-source error. The repair command was rerun successfully
against the active staged assemblies. At the end of v10, these sphere and Color follow-ups
had not yet been checked by Unity IL2CPP. No APK was produced by v10. This build reused the active staged
project; displayed external free space remained 119 GiB before and after. Continue grouping
trace-supported repairs and inspecting emitted C#/assembly before Unity; keep the same active
stage and caches and defer another Unity build until the next coherent blocker batch is ready.

The v11 ARM64 build surfaced a similar sized method queue, with six signatures leaving and
six different ones surfacing. The v12 build checked the complete `BoundingSphere` point and
sphere containment plus sphere/plane intersection batch. Before Unity, all eight rewritten
methods were inspected in ILSpy; their emitted bodies had no IL warning placeholders. The
native sphere intersection trace confirms the unusual `Intersects`-classification-only
boolean behavior, so that comparison is intentional. Unity reached IL2CPP and failed with
260 diagnostics, but the sphere/plane method is no longer in the exact IL2CPP failure list;
five signatures left the v11 list and five new signatures appeared. No APK was produced.
The same external project/cache was reused, and reported free storage remained 119 GiB.
The 121 MB v10, v11, and v12 Unity logs compressed and verified at about 1.2 MB each. The
plain logs were removed after preserving their repair-queue summaries; compressed logs remain
usable as future `--repair-diagnostics` inputs.

### Build-cycle policy

- Make edits only from a native trace, a concrete IL2CPP/import failure, or a corroborated
  original method body. Group one value-type or tightly related method family together.
- Before a Unity build, compile and apply the repair tool, then inspect changed methods in
  ILSpy. A compile failure is a hard stop. If ILSpy emits a `//IL_` warning, inspect that exact
  raw IL instruction and its stack/operand types; proceed only when it is valid with resolved
  operands and the trace agrees with the control flow. Unresolved placeholder output is a hard
  stop.
- Spend one ARM64 IL2CPP build on a batch after those cheap checks pass. Reuse the active
  stage and Unity cache; do not make a second project copy per iteration.
- Diff unique IL2CPP signatures between builds, not the raw repeated diagnostic count. Rank
  candidates by recurrence across recent runs; one-run signatures stay low priority unless a
  concrete trace or other independent evidence supports them. A targeted failure leaving one
  queue is provisional when history shows that signature is intermittent. New unrelated
  failures are triaged as the next batch; they do not justify rebuilding unchanged inputs.
- Reserve same-code Unity builds for a deliberate nondeterminism control or final artifact
  check. Recent cached ARM64 cycles cost 1.5–1.9 minutes each, with 115.5–115.6 MiB raw logs
  compressed to about 1.15 MiB. Unity/cache storage deltas varied by roughly 50 MiB either way;
  external free space is about 119 GiB, so preserve the warm cache and avoid cleanup between
  cycles.
- Keep source traces, Unity logs, staged APK candidates, and large temporary data on the
  external volume. Check free space before builds and remove only disposable intermediates
  whose source or diagnostic evidence is already retained.
- Build with `--compress-log` after the queue is summarized, and use the resulting `.log.gz`
  path for later `--repair-diagnostics` runs. This preserves full diagnostic detail at roughly
  one percent of the raw log size.
- Compare consecutive builds with `tools/compare_unity_builds.py OLD.log.gz NEW.log.gz`. It
  streams the logs, de-duplicates failures by assembly and exact method signature, ranks history
  by recurrence, and prints only cleared/new signatures plus cost metadata. This avoids loading
  or forwarding multi-million-line Unity logs into the repair/reasoning loop.
- Each new build records a `.cycle.json` sidecar with Unity wall time, raw/retained log sizes,
  unique summarized queue-item count, APK size, and net free-space change. Historical v10-v14
  runs predate this instrumentation, so their precise durations and per-run storage deltas are
  unavailable.
- The recovery launcher now serializes Unity builds through a host-wide `flock`, skips the
  expensive Unity phase below 6 GiB `MemAvailable` by default (override with
  `--min-available-memory-mib`), and samples peak Unity process-tree RSS and host minimum
  available RAM in the cycle sidecar. It does not hard-cap Unity's memory. A current host
  snapshot showed 22 GiB total, 15 GiB available, 2.7 GiB swap used, and negligible recent
  memory PSI; Unity was not running. An idle Unity Roslyn server held about 560 MiB, while
  several REA MCP Node workers and Codex renderers accounted for additional resident memory.
  No user or MCP process was terminated.
- Preserve the active `Library` and IL2CPP caches between repair batches. Delete caches only
  for demonstrated corruption or measured storage pressure; the active external volume
  currently has 119 GiB free. Do not create a full duplicate staged project for a repair
  iteration.
- Bound expensive work to one ARM64 IL2CPP build per coherent, trace-backed repair batch. Use
  method-specific trace review and ILSpy output inspection before that build; after it, use the
  unique-signature diff to decide what to fix next. A flat repeated diagnostic count alone is
  not a reason to rerun Unity.

The v13→v14 comparison confirms why signatures, rather than the repeated diagnostic total,
guide the next batch: the unique set stayed at 16, while four exact methods cleared and four
surfaced. The comparator reports only those eight lines, versus scanning roughly 121 MiB of raw
log text per run; both retained logs are about 1.15 MiB. v13/v14 lack cycle sidecars, so no
precise wall-time or disk-delta claim is made for them.

The first instrumented v15 cycle grouped both traced `EB.BitStream.Serialize` payload overloads.
ILSpy showed complete bodies for `ref byte[]` and `ref Buffer`; Unity's ARM64 IL2CPP queue then
cleared the exact `Serialize(ref Buffer)` rejection. Its 16-signature set stayed flat because
`Serialize(ref string)` surfaced next; the build still failed with 260 repeated IL2CPP errors and
produced no APK. Unity took 108.59 seconds. The 121,179,386-byte raw log compressed to 1,207,017
bytes; the sidecar measured a 1,224,704-byte net free-space reduction, almost entirely retained
diagnostics. Free space remains 119 GiB. The v14→v15 comparator reduced the review output to one
cleared and one newly surfaced signature. This validates the instrumentation and the targeted
repair while leaving the overall APK goal incomplete.

The v16 repair reconstructed `EB.BitStream.Serialize(ref string)` from the exact native trace:
the read branch calls `EB.Buffer.ReadString()` and stores through the reference; the write branch
passes that reference to `EB.Buffer.WriteString()`. The exact IL2CPP rejection left in v16 and
remained absent in the v17 same-code control build. Both builds still failed at 260 repeated
IL2CPP errors and produced no APK.

The v17 control run had no recovery-code changes after v16, but its 16-signature IL2CPP set
still differed by two cleared and two newly surfaced methods. Root cause is not isolated. To
avoid chasing one-run arrivals, `tools/compare_unity_builds.py --history LOG...` now reports
methods present in every run, recurring in at least 75% of runs, and intermittent. Across v14-v17,
11 of 16 signatures persisted in every build and 13 appeared in at least three; across v15-v18,
10 persisted in all four. Treat these as build observations, not proof of the underlying methods'
semantic correctness.

The v18 batch rebuilt `EB.Rendering.BeamRenderer.FloatEvlautation` from its native ARM64 trace:
after the initial method-metadata setup, the `addedEvaluaions` path reads the indexed cache item;
the other path evaluates the curve, appends the result to the by-reference list, and returns it.
The emitted target body was clean in ILSpy and the exact method left the v18 failure set. Six
other signatures also left and six surfaced; Unity still reports 260 repeated errors and no APK
was produced. Unity took 109.64 seconds; the 121,178,633-byte log compressed to 1,207,170 bytes.
Free space increased by 49.72 MiB during that cycle (net measurement; transient peak usage is not
captured), with 119 GiB free afterward. The v15-v18 recurring queue still includes `Crash.DoAnim`,
`AlignUIElements.GetObjectBounds`, `EB.BaseAPI.PlaceEntity`, and several Fabric methods. Continue
from the recurring set and exact 9.2 traces; the next method batch is not yet selected.

The v19 preflight compiled the repair tool before Unity and caught a C# syntax error in the
new `Crash.DoAnim` repair at no Unity-build cost; fixing it allowed the repair to apply. ILSpy
printed one `//IL_0156` note for the value-type `Vector3` load, so I inspected the method's raw
IL: it is `ldfld Vector3 Crash::temp` followed by the resolved `Transform.set_localPosition`
call, with a valid value stack. The v19 ARM64 IL2CPP run cleared `Crash.DoAnim`; v18→v19 showed
one clear and one newly surfaced signature (`EBPrimitives.Sphere`). The method appeared in 3/4
of the v16–v19 logs, so this shows the repair passes IL2CPP in v19, while the varying queue
means disappearance in one run alone is not a stability claim. Unity still reported 16 unique
signatures / 260 repeated diagnostics and produced no APK. It took 1.8 minutes; the 115.6 MiB
raw log compressed to 1.15 MiB and free space increased by 3.75 MiB. The cycle sidecar now
stores free-space delta with the intuitive sign, and the comparator reverses older sidecar
values that recorded the inverse sign. History output ranks methods by repeat count.

After v19, the build wrapper was updated to capture the verbose repair transcript in a small
`.prepare.log`, print only its action count, retain the full last 60 lines on preflight failure,
and record preflight duration separately from Unity time. This avoids spending context tokens on
dozens of unchanged repair messages while preserving the evidence locally. Python syntax and
CLI loading were checked; the next recovery cycle will exercise the new concise output path.

The v20 batch reconstructed `EB.Base.BaseAPI.PlaceEntity` from the native 9.2 trace: it builds
the seven-value `/base/place/{0}/{1}/{2}/{3}/{4}/{5}/{6}` route, including positive-infinity
coordinate normalization before integer conversion, then calls the retained `Post` and `Service`
methods with the 9.2 `Action<string, IDictionary>` callback. The 2.0.2 method was only a field
order/route cross-check; its body was not copied. The repair compiled and ILSpy showed the route
values in the expected order. Unity's v20 ARM64 IL2CPP pass cleared this exact signature; it was
present in all three prior runs v17–v19, so it is a meaningful targeted result. The overall set
remains 16 unique signatures because five signatures cleared while five others surfaced. No APK
was produced.

This run also exercised the optimized wrapper: it reported only 98 repair actions and retained
the 106-line transcript in the external `.prepare.log`. Preflight took 18.1 seconds; Unity took
1.9 minutes. The raw 115.6 MiB Unity log compressed to 1.15 MiB. Unity/cache activity reduced
free space by 58.17 MiB; the external volume still has about 119 GiB available. The v19→v20
comparison and cycle sidecar agree on the cleared target and measured costs.

The v21 batch rebuilt `EB.UI.DataBinding.CopyMemberBinding.EnsureTypeMatches` from its 9.2 trace:
it keeps assignable values, preserves null for reference/nullable types, initializes non-nullable
value types and mismatches, assigns `String.Empty` for string targets, and logs failed instance
creation through the traced `EB.Debug.LogError` path. The 2.0.2 implementation corroborates the
branch intent; the new IL was authored from the trace and has a clean ILSpy body. The target
signature left the v21 IL2CPP set (present in v18–v20); six methods left and six surfaced, so the
unique set remains 16 and there is still no APK.

The v21 wrapper recorded 18.3 seconds of preflight and 2.0 minutes in Unity. It retained a 115.6
MiB log at 1.15 MiB compressed and measured a 53.38 MiB increase in free space. The compact
wrapper output, `.prepare.log`, sidecar, and comparator were all exercised again; free space
remains about 119 GiB. Continue from the high-recurrence signatures, especially the traced
`AlignUIElements.GetObjectBounds` and `EB.Rendering.BeamRenderer.Update` candidates.

The v22 cycle reconstructed `EB.Rendering.BeamRenderer.Update` from the exact native 9.2 ARM64
trace. Before paying for Unity, the repair tool compiled, and focused ILSpy output showed the
expected endpoint-null return, transform-position reads, startup clamp/interpolation, and
loop/duration gate. Unity then cleared this signature and surfaced `UpdateMesh`; the unique
IL2CPP error count remained 16 (six cleared, six surfaced). Across v18–v22 there are nine
failures present in every run, with `BeamRenderer.Update` present in four of five. The v22 cycle
used 19.3 seconds for preflight and 1.8 minutes for Unity; its 115.6 MiB raw log compressed to
1.15 MiB. Free space fell 64.04 MiB, leaving about 119 GiB. No APK was produced.

The build wrapper now fingerprints staged managed DLLs, Unity build settings, and the recovery
repair sources after preflight. If the same fingerprint already produced a failed IL2CPP build,
it skips the next expensive Unity run by default; `--repeat-unchanged` explicitly re-enables a
same-input run when measuring intermittency. This guard avoids accidental duplicate cycles while
preserving a deliberate repeat option. Python syntax and the CLI option were checked.
The unchanged-input guard was exercised against the v22 failure: v23 stopped after an 18.56
second preflight, spent zero Unity time, and recorded zero net external-storage change. A repeat
repair pass left the complete managed-input fingerprint identical, confirming that the guard
does not invalidate itself on every preflight.

## v24: REA evidence and GetObjectBounds preflight

Used the registered REA MCP server directly. `binary_session` confirmed the server is aligned
(rea-agents 6.3.0, 139 tools, Codex MCP client 0.159.0) and the active input is the real
`Transformers 9.2 offline.apk`, SHA-256
`68ad382f3229578084f8590c236acf9a5547bda829e12e8beb929d844af7c1b9`. REA artifact inspection
confirmed the embedded ARM64 `libil2cpp.so` and `global-metadata.dat` hashes match the recovery
manifest. APK graph evidence exceeded MCP's 10 MiB response budget, so it was exported through
`export_evidence_bundle`; no MCP registration restart is needed.

REA inspected the staged `Assembly-CSharp-firstpass.dll` (SHA-256
`dd432bf55b69e552a1c64b43eca6fcf718894220d16c141337f7a03f6eb966b7`). The existing
`AlignUIElements.GetObjectBounds` body is present as 806 decoded instructions / 2,989 IL bytes;
the retained stage DLL is unchanged. The authored source now uses `widget is UILabel`, matching
the 9.2 native class-hierarchy test, and its compiled helper `/tmp/RecoveryMethodBodies.dll`
was independently decoded by REA with complete coverage: 195 instructions / 514 IL bytes,
no decode issues, and four `0.1f` thresholds. This validates source compilation and CIL structure,
but the helper has not been transplanted into the retained stage DLL or imported by Unity.

No Unity 2020.3.31f1 Editor or its `Data/MonoBleedingEdge` compiler/runtime is installed in the
accessible paths (`/home/darabat/Unity/Hub/Editor` is empty; `/home/darabat/.local/bin/unity`
is not a Unity Editor). Do not start the ARM64 IL2CPP build. Next, restore/install the exact
Unity Editor version and its bundled Mono tools, then run the existing repair preflight on the
retained stage, review the transplanted method with ILSpy/REA, and only then run one Unity build.
Existing Ghidra project files are present, but REA 6.3.0 cannot attach a `.gpr` project through
`open_binary`; a fresh native import is unnecessary for this method because the exact 9.2
ARM64 trace already exists at `rosetta-v10-blockers-firstpass/Dumps/AlignUIElements/GetObjectBounds_31236.dump.txt`.

## v25: installed Unity and SocialHub constructor validation

Correction to v24: the exact Unity 2020.3.31f1 editor and its Mono runtime are available on the
external build volume at `/run/media/darabat/24f15d44-9691-4cf4-8b52-bc03f1e3693f/Unity/Hub/Editor/2020.3.31f1/Editor/Unity`.
They were used by later recovery cycles; the earlier statement that Unity was unavailable is
stale. The existing Docker image and runtime libraries are also available. The real 9.2 APK
remains the source; the small `exact-pair-input.apk` is not used as a game APK.

The actual REA MCP server is registered and usable (139 tools). The current full APK SHA-256 is
`68ad382f3229578084f8590c236acf9a5547bda829e12e8beb929d844af7c1b9`. The previous five
Unity checkpoints reduced the active repair queue from 49 to 43 unique signatures and repeated
IL2CPP diagnostics from 260 to 210. v5 took about 2.5 minutes in Unity and produced no APK;
these counts track import blockers, not runtime completeness.

Added the `SocialHubTrayButton` constructor rewrite from the retained 9.2 ARM64 field writes and
connected it to preflight. REA inspected the resulting `Assembly-CSharp.dll` (SHA-256
`0804f6288e4ccb2a5d35164d3efaa7bd1da928c72fcd5878b725692e8d91ca82`): the constructor has a
decoded signature and complete 57-instruction / 241-byte body with no body issue. Its nine vector
field assignments match the native constants, including `(-1, 1, 1)`, `(0, 0, 70)`, `(0, 5, 0)`,
and `(0, 0, -45)`, followed by the base constructor call. Preflight succeeded and logged the
SocialHub repair. A new Unity cycle was deferred until more high-impact fixes can be batched; no
APK or playable build exists yet.

The source progress to date is trace-derived and authored; the newly added SocialHub defaults and
story/UI repairs do not copy game method bodies. The 1 GiB temporary REA export used to retrieve
complete retained evidence was deleted after validation. Next, batch several high-frequency
failures with exact native evidence, rerun preflight, and use one Unity checkpoint to measure the
combined effect. The queue is volatile because later failures surface as earlier ones are removed.

## v26: batched constructor preflight and resource check

Used the retained 9.2 ARM64 traces to add `MatineeStage` and `SpecialAttackIcon` constructor
repairs to the same pass as `SocialHubTrayButton`. The first preflight caught a helper-order bug
(the method body was cleared before its Unity method references were captured); the repair now
captures and validates those references first. The rerun completed successfully and logged both
new repairs. `MatineeStage` initializes the two positions with `Vector3.zero`, both rotations
with `Quaternion.identity`, `playbackFilter` to the observed empty string, its three traced lists,
and `_destroyStageOnCompletion=true`. `SpecialAttackIcon.FadeTime` is set to traced `0.2f`.

REA's managed artifact inspections report complete metadata for the changed main assembly
(`c8ade12b...`) and firstpass assembly (`281589be...`). Full member responses exceed the 10 MiB
MCP transport cap, so REA retained the records and offered evidence export. The complete export
was 1.65 GB; I interrupted the whole-bundle scan when it became a needless resource cost and
deleted the export. REA's earlier direct validation of `SocialHubTrayButton` remains complete;
the combined Unity checkpoint is still needed to validate these two new bodies against IL2CPP.

At the memory check, the machine had 12 GiB available out of 22 GiB RAM and 3.4 GiB of 8 GiB
swap in use. The temporary evidence export was the main avoidable storage/I/O cost; after its
removal `/tmp` usage fell by 1.65 GB. No heavy analysis process was running for the constructor
preflight. Future checks will avoid whole-session REA exports unless a specific body cannot be
verified through a smaller MCP result or Unity import.

## v27: APK provenance correction and bounded IL2CPP checkpoint

REA MCP inspected the full `Transformers 9.2 offline.apk` directly: evidence
`ev_62cf84d2213808ea6b9e261548a16409b24dda5f3c55a0226c6af1d106657f89` records
`com.kabam.bigrobot`, version 9.2.0 / code 123129100, 10,395 classes, and 4,133 resources.
Its SHA-256 matches the pinned hash in `tools/recover_9_2.py` and the active
`recovery-manifest.json`, and AssetRipper logs name that APK as input. This proves the active
stage's file lineage, but not that the APK is pristine Kabam software.

That distinction is material: `apksigner` verifies the APK but reports signer `CN=Android Debug`
(certificate SHA-256 `4e136f2c457dc656a3503da96734608decb00764a91bdfdd9767c4fe412fa0e0`), not a
Kabam release signer. The embedded ARM64 `libil2cpp.so` SHA-256 is
`e54cb5a1b57d6df1a70958a5c5ca29784eb35ee7c060b045ae53d95a94d91bac`, equal to the repository's
misleadingly named `pristine_libil2cpp.so`; `readelf -d` shows that library depends on
`libdothook.so`, which the APK also bundles. `Server/provision_emulator.sh` explicitly describes
this input APK as already bundling the patched library and hook. Therefore this input is an
offline-patched/re-signed APK, not verified pristine Kabam source. No further Unity builds should
use it for the pristine recompilation objective; the carrier `exact-pair-input.apk` is only 58.6 MB
and is not a replacement full APK.

Before discovering the signer/patch marker, one memory-guarded Unity 2020.3.31f1 ARM64 IL2CPP
checkpoint was run after the constructor preflight. The authored `EBLightShadow` constructor
repair passed preflight and removed its 12 repeated invalid-IL reports; the distinct queue moved
40 -> 39, and Unity's total error count moved 134 -> 122. Unity still produced no APK. The log
compressed from 115.4 MiB to 1.12 MiB, net storage use was 14.94 MiB, and minimum available RAM
was 7.9 GiB. These are results for the patched input only and do not count as progress toward a
pristine-source build. The `AlignUIElements.GetObjectBounds` authored body also passed the import
preflight and was not among the remaining IL2CPP queue items.

Next required input: a legally obtained, full pristine Kabam 9.2.0 APK (or a complete original
package with its clean native libraries and matching metadata). Verify its signer and hashes, then
rebuild the recovery manifest and Unity stage from that artifact before continuing. Preserve the
current local stage and authored work until that comparison is complete.

## v28: carrier check and safe storage cleanup

REA opened and inventoried `exact-pair-input.apk` as an APK archive (root SHA-256
`32b4cf244d9acd64b5457782164f8ae7ec70d789816eb8b2be4dd25d98d21b48`). Its complete graph has
three nodes: the carrier plus only `global-metadata.dat` and ARM64 `libil2cpp.so`. REA extraction
Evidence `ev_ac77ae647db2c564bc2ce29cec853f4f308408cb3c8c734d54ca3c145deda558` reports the
metadata hash `636458c3...ade7` and native hash `e54cb5a1...91bac`, both identical to the active
full offline APK's manifest. Thus the carrier is not a clean-source replacement. The temporary
58.6 MB REA extraction was removed, and the archive analysis session was closed.

Freed 1,860,883,051 bytes by deleting only `.scratch/verify-special-mode-unsigned.apk` and
`.scratch/verify-special-mode-aligned.apk`. Before deletion, their ZIP entry names, sizes, and CRCs
matched the retained signed `.scratch/verify-special-mode.apk` for every non-`META-INF` entry; the
final APK's three extra entries are signing metadata. The retained final passed `zipalign -c 4`
and `apksigner verify` (v1/v2/v3). Screenshots, logs, `.idsig`, and the signed APK remain. `.scratch`
is now 1.1 GB instead of 2.9 GB. No source APK or generated Unity stage was deleted.

## v29: reject the known patched source in recovery tooling

`tools/recover_9_2.py` no longer treats the prior debug-signed offline APK hash as an accepted
9.2.0 source identity. It rejects that exact digest even when the override is present, rejects
any APK bundling `libdothook.so` or whose ARM64 library contains its dependency marker, and
requires the operator to verify package/version with REA and the Kabam release signer with
`apksigner` before accepting a new hash. The manifest records the rejected offline-patched hash
and the source checks. `DECOMPILATION.md` and `COMPLIANCE.md` now describe this policy.

Validation: `py_compile` and CLI help passed. Direct checks reject both the full offline APK
(known digest) and `exact-pair-input.apk` (native hook dependency marker). No Unity build ran.

## v30: authenticate the clean 9.2 source and reuse verified assets

REA MCP inspected the full candidate package. `ev_f689c9aac2aacee4772be15c563aed4433242501aa3124842b673294f3c05058`
identifies package `com.kabam.bigrobot`, version 9.2.0 / code 123129100, with 10,395 classes
and 4,133 resources. `ev_3a27eeacb306ebc1f5d36ec53defcdc121715e9a2b5964eab7908fa357e5f2af` records
the complete static archive graph: 3,951 artifacts and 4,133 relationships. The MCP frame was
too small for the full graph; REA retained the evidence, and its 19.8 MB bundle was exported to
`/tmp` for bounded local inspection, then removed after the summary was recorded.

Candidate `build/workspace-build/pristine-rebuild/Transformers-9.2-pristine-rebuilt.apk` has
SHA-256 `cae78579898a002b65b766d816584331972de79ed6183d7c7e9c943fc4403406`. `jarsigner -verify`
reports its JAR content signature valid, and `keytool` reports EBG signer SHA-256
`A8213D062F720775260A2F96E01AE5AD279AFEDFA4D63050EB815149F369C521`, matching the certificate
on the older Kabam APK. Its v2/v3 Android signing block was stripped; it is therefore authenticated
content for extraction, not a byte-for-byte original or installable APK. The clean ARM64 library
is `575aa973ed8fd54e79c70abdaed5b5a3b013e8e3ec68e0fa64e98f6bdfba9b8a`, contains no `libdothook`
dependency, and shares the metadata hash with the prior APK. A ZIP directory comparison found
4,128 unchanged shared entries; differences are limited to signature records, the certificate
stamp, the two ARM64 native-library entries (including the patched hook), and no Unity asset
payload. This makes the candidate suitable for a clean code recovery while preserving the
existing AssetRipper export.

`tools/recover_9_2.py` now accepts only this pinned clean candidate without an override, verifies
its JAR signer and exact native/metadata hashes, and has a guarded asset-export reuse path. The
reuse path requires the pinned source APK pair, rejects any unexpected ZIP entry differences, and
records the equivalence check in the recovery manifest. Validation: `py_compile`, CLI help,
clean-candidate/signature checks, and the two-APK reuse comparison passed; the debug-signed full
APK was rejected. The carrier is still excluded by the prior REA finding. No Cpp2IL recovery,
Unity build, APK, or runtime test has yet been run on the clean candidate.

REA also inspected `/tmp/AlignUIElementsGetObjectBounds.dll` from the authored method-body
replacement: `ev_71996a840ccfd36854a50e63813d721b5e5c5254cad368473d8734d1d1635b39` reports complete
metadata and CIL coverage, 176 decoded instructions, and no decode issues. It includes calls for
UILabel world corners, widget bounds, transform conversion, and four `0.1f` thresholds. This
confirms the helper's compiled structure, not runtime behavior or a match against the now-verified
clean native library. Keep the current Unity stage intact until clean-source Cpp2IL output has
been regenerated and the repair preflight run.
