# Third-party components of the assetless shell

## Table of contents

- [Scope](#scope)
- [Components](#components)
- [Not included](#not-included)

## Scope

The shell APK (`launcher/`) bundles the components below in addition to the recompiled game scripts.
They are fetched at build time from their publishers (NuGet, the Legible repository) and are not
committed to this repository. Licence terms belong to each publisher; this list is a record, not legal
advice. See [`COMPLIANCE.md`](../COMPLIANCE.md) ("Assetless launcher") for the open question about
distributing the APK itself.

## Components

| component | version | source | licence (as published) | used for |
|---|---|---|---|---|
| ICSharpCode.SharpZipLib | 0.86.0 (assembly 0.86.0.518) | NuGet package `SharpZipLib` 0.86.0, `lib/20` | GNU GPL v2 or later with the library linking exception | the game's zip and gzip handling |
| BouncyCastle.Crypto | 1.8.5 | NuGet package `BouncyCastle` 1.8.5 | Bouncy Castle licence (MIT style) | the game's hashing, HMAC and cipher calls |
| Legible interpreter | built from `Gummygamer/legible-lang` | local build for `armv7-linux-androideabi`, shipped as `liblegible.so` | see that repository | runs the authored `Server/*.lbl` files |
| Unity player runtime | 2020.3.31f1 (Mono, armv7) | Unity Editor build output | Unity terms of service | the runtime itself |

## Not included

The shell does not ship any library, native code, Java code, asset or manifest copied from the
original game APKs: the third-party libraries above replace the copies that the original APK carried,
and the Java classes and the three native libraries in `launcher/shell/android/` are newly authored stand-ins.
