# Cpp2IL compatibility patch

`cpp2il-b5ad444-ilgenerator.patch` modifies the MIT-licensed Cpp2IL
`Cpp2IL.Core/IlGenerator.cs` at commit
`b5ad444b82267cb1e4b88b8b373c008105bdea52` from
[SamboyCoding/Cpp2IL](https://github.com/SamboyCoding/Cpp2IL). It contains generic
IL-generation fixes only; no APK, game asset, game-authored value, or recovered game code.

The upstream MIT license and copyright notice are included in
[`CPP2IL-MIT-LICENSE`](CPP2IL-MIT-LICENSE). The patch SHA-256 is pinned by
[`build_patched_cpp2il.py`](../build_patched_cpp2il.py).
