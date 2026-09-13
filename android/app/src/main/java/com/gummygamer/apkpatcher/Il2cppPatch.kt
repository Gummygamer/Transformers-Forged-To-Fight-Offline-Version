package com.gummygamer.apkpatcher

import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * On-device libil2cpp.so patcher.
 *
 * Ports the byte-level patches from patches/patch_il2cpp.lbl:
 * - 16 fixed patch sites per ABI (arm64/armv7)
 * - DT_NEEDED injection: byte-cave method for arm64, inplace alias method for armv7
 * - Reachability stub validation for bundled-server mode
 *
 * All operations are pure in-memory byte manipulations; no patchelf or NDK required.
 * The arm64 "byte" needed injection uses three hard-coded cave offsets from the
 * pristine 9.2.0 library. The armv7 "inplace" injection reuses the __floatundidf
 * donor symbol slot and rewrites the dynamic table.
 */
object Il2cppPatch {

    /** A single patch site: absolute file offset and the bytes to write there. */
    data class PatchSite(
        val offset: Int,
        val patchBytes: ByteArray,
        val name: String
    )

    /** Reachability site for offline bundled-server mode. */
    data class ReachabilitySite(
        val offset: Int,
        val stub: ByteArray,
        val pristine: ByteArray,
        val name: String
    )

    // ---- arm64 patch sites (from patch_il2cpp.lbl arm64_sites) ----

    val arm64Sites: List<PatchSite> = listOf(
        PatchSite(19113788, hexToBytes("20008052c0035fd6"), "TcpClientSSL.CertificateValidation -> true"),
        PatchSite(21940544, hexToBytes("c0035fd6"), "TcpClientBouncy.NotifyServerCertificate -> noop"),
        PatchSite(16523700, hexToBytes("09000014"), "Hub.InitializeComponents -> register managers past OTAConfig gate"),
        PatchSite(23246480, hexToBytes("1f2003d5"), "LoginManager._PostInit -> skip 'no valid authenticators' fail"),
        PatchSite(16529176, hexToBytes("1f2003d5"), "Hub.SubSystemConnecting -> skip FatalError on subsystem error-state 3"),
        PatchSite(19056256, hexToBytes("c0035fd6"), "SubSystem.FatalError -> ret instead of tail-call Hub.FatalError"),
        PatchSite(28599028, hexToBytes("40008052c0035fd6"), "Application.internetReachability -> ReachableViaLocalAreaNetwork"),
        PatchSite(20135496, hexToBytes("20008052c0035fd6"), "EndPoint.HasInternetConnectivity -> true"),
        PatchSite(15779872, hexToBytes("00008052c0035fd6"), "LevelLockWidget.get_Locked -> false"),
        PatchSite(15782864, hexToBytes("00008052c0035fd6"), "LevelLockManager.IsLocked -> false"),
        PatchSite(15783048, hexToBytes("00008052c0035fd6"), "LevelLockManager.GetMinLevel -> 0"),
        PatchSite(15868548, hexToBytes("00008052c0035fd6"), "PVPRaidFlow.IsLevelLocked -> false"),
        PatchSite(16569924, hexToBytes("20008052c0035fd6"), "AvXModeButtonData.IsInAlliance -> true"),
        PatchSite(15373560, hexToBytes("1f2003d5"), "FightLandingModel.ProcessAvxModeClick -> skip null-alliance branch"),
        PatchSite(16567012, hexToBytes("20008052c0035fd6"), "AvEEvent.IsAvailable -> true"),
        PatchSite(12677752, hexToBytes("400600b4"), "BCGBlueprintBase.get_SynergyBonuses -> return empty list")
    )

    // ---- armv7 patch sites (from patch_il2cpp.lbl armv7_sites) ----

    val armv7Sites: List<PatchSite> = listOf(
        PatchSite(15919104, hexToBytes("0100a0e31eff2fe1"), "TcpClientSSL.CertificateValidation -> true"),
        PatchSite(19285720, hexToBytes("1eff2fe1"), "TcpClientBouncy.NotifyServerCertificate -> noop"),
        PatchSite(12752884, hexToBytes("0a0000ea"), "Hub.InitializeComponents -> register managers past OTAConfig gate"),
        PatchSite(20867576, hexToBytes("00f020e3"), "LoginManager._PostInit -> skip 'no valid authenticators' fail"),
        PatchSite(12759716, hexToBytes("00f020e3"), "Hub.SubSystemConnecting -> skip FatalError on subsystem error-state 3"),
        PatchSite(15845172, hexToBytes("1eff2fe1"), "SubSystem.FatalError -> ret instead of tail-call Hub.FatalError"),
        PatchSite(27322892, hexToBytes("0200a0e31eff2fe1"), "Application.internetReachability -> ReachableViaLocalAreaNetwork"),
        PatchSite(17149180, hexToBytes("0100a0e31eff2fe1"), "EndPoint.HasInternetConnectivity -> true"),
        PatchSite(11832648, hexToBytes("0000a0e31eff2fe1"), "LevelLockWidget.get_Locked -> false"),
        PatchSite(11836396, hexToBytes("0000a0e31eff2fe1"), "LevelLockManager.IsLocked -> false"),
        PatchSite(11836620, hexToBytes("0000a0e31eff2fe1"), "LevelLockManager.GetMinLevel -> 0"),
        PatchSite(11943044, hexToBytes("0000a0e31eff2fe1"), "PVPRaidFlow.IsLevelLocked -> false"),
        PatchSite(12808412, hexToBytes("0100a0e31eff2fe1"), "AvXModeButtonData.IsInAlliance -> true"),
        PatchSite(11333248, hexToBytes("00f020e3"), "FightLandingModel.ProcessAvxModeClick -> skip null-alliance branch"),
        PatchSite(12804728, hexToBytes("0100a0e31eff2fe1"), "AvEEvent.IsAvailable -> true"),
        PatchSite(8011440, hexToBytes("400000ea"), "BCGBlueprintBase.get_SynergyBonuses -> skip null throw")
    )

    // ---- Reachability sites for bundled offline check ----

    fun reachabilitySites(abi: String): List<ReachabilitySite> {
        return if (abi == PatchRequest.ARM64) {
            listOf(
                ReachabilitySite(28599028,
                    hexToBytes("40008052c0035fd6"),
                    hexToBytes("f30f1ef8fd7b01a9"),
                    "Application.internetReachability"),
                ReachabilitySite(20135496,
                    hexToBytes("20008052c0035fd6"),
                    hexToBytes("fd7bbfa9fd030091"),
                    "EndPoint.HasInternetConnectivity")
            )
        } else {
            listOf(
                ReachabilitySite(27322892,
                    hexToBytes("0200a0e31eff2fe1"),
                    hexToBytes("34009fe500009fe7"),
                    "Application.internetReachability"),
                ReachabilitySite(17149180,
                    hexToBytes("0100a0e31eff2fe1"),
                    hexToBytes("00482de90db0a0e1"),
                    "EndPoint.HasInternetConnectivity")
            )
        }
    }

    // ---- Patch application ----

    private fun sitesForAbi(abi: String): List<PatchSite>? = when (abi) {
        PatchRequest.ARM64 -> arm64Sites
        PatchRequest.ARMV7 -> armv7Sites
        else -> null
    }

    data class PatchResult(
        val data: ByteArray,
        val changed: Boolean,
        val error: String?
    ) {
        val isSuccess: Boolean get() = error == null
    }

    /**
     * Apply the 16 fixed patch sites to [data].
     * Each site is written unconditionally; if the bytes already match the stub,
     * the write is a no-op (idempotent).
     */
    fun applySites(data: ByteArray, abi: String): PatchResult {
        val sites = sitesForAbi(abi) ?: return PatchResult(
            data.copyOf(), false, "unsupported ABI '$abi'"
        )
        val out = data.copyOf()

        for (site in sites) {
            if (site.offset + site.patchBytes.size > out.size) {
                return PatchResult(out, false,
                    "libil2cpp is too small for patch site '${site.name}' " +
                    "(offset ${site.offset}, file size ${out.size})")
            }
            // Write patch bytes (idempotent)
            for (i in site.patchBytes.indices) {
                out[site.offset + i] = site.patchBytes[i]
            }
        }

        return PatchResult(out, true, null)
    }

    /**
     * Apply reachability stubs for bundled-server mode.
     * If the site already contains the stub bytes, it's left alone.
     * If it contains the expected pristine bytes, the stub is written.
     * Otherwise an error is returned (not the expected Transformers 9.2 build).
     */
    fun applyReachabilityStubs(data: ByteArray, abi: String): PatchResult {
        if (abi != PatchRequest.ARM64 && abi != PatchRequest.ARMV7) {
            return PatchResult(data.copyOf(), false, "unsupported ABI '$abi'")
        }
        val sites = reachabilitySites(abi)
        val out = data.copyOf()

        for (site in sites) {
            if (site.offset + site.stub.size > out.size) {
                return PatchResult(out, false,
                    "libil2cpp is too small to be the expected Transformers 9.2 library " +
                    "(offset ${site.offset}, file size ${out.size})")
            }

            val currentBytes = out.sliceArray(site.offset until site.offset + site.stub.size)

            if (!currentBytes.contentEquals(site.stub)) {
                if (currentBytes.contentEquals(site.pristine)) {
                    // Apply stub
                    for (i in site.stub.indices) {
                        out[site.offset + i] = site.stub[i]
                    }
                } else {
                    return PatchResult(out, false,
                        "libil2cpp is not the expected Transformers 9.2 build: " +
                        "offset ${site.offset} holds unrecognised bytes; " +
                        "patch it first with the desktop patcher for this ABI")
                }
            }
        }

        return PatchResult(out, true, null)
    }

    /**
     * Check that the library is ready for bundled offline mode:
     * must contain "libdothook.so" reference and have reachability stubs applied.
     */
    fun checkOfflineReady(data: ByteArray, abi: String): PatchResult {
        if (abi != PatchRequest.ARM64 && abi != PatchRequest.ARMV7) {
            return PatchResult(data.copyOf(), false, "unsupported ABI '$abi'")
        }
        // Check for libdothook.so reference
        if (!hasHookReference(data)) {
            return PatchResult(data, false,
                "bundled server hook is not wired into libil2cpp; " +
                "it needs the desktop patcher's --needed step first")
        }
        return applyReachabilityStubs(data, abi)
    }

    /** Return whether the ELF contains the DT_NEEDED/string-table hook name. */
    fun hasHookReference(data: ByteArray): Boolean =
        indexOfBytes(data, "libdothook.so".toByteArray(Charsets.UTF_8)) >= 0

    // ---- DT_NEEDED injection ----

    /**
     * Inject DT_NEEDED libdothook.so into an arm64 library using the byte-cave method.
     * The 9.2.0 library has a zero-filled cave and spare dynamic-table entries at
     * fixed offsets. The name is outside the original .dynstr section, so DT_STRSZ
     * must be extended as well as writing DT_NEEDED; omitting that update makes
     * Android's linker abort while reading the dependency name before Unity starts.
     */
    fun injectNeededArm64(data: ByteArray): PatchResult {
        val out = data.copyOf()
        val hookName = "libdothook.so".toByteArray(Charsets.UTF_8)
        val hookFileOffset = 8204340
        val dynamicOffset = 45781192
        val dynamicEntrySize = 16
        val strtabValueOffset = dynamicOffset + 10 * dynamicEntrySize + 8
        val strszValueOffset = dynamicOffset + 12 * dynamicEntrySize + 8
        val neededTagOffset = dynamicOffset + 25 * dynamicEntrySize
        val neededValueOffset = neededTagOffset + 8
        val requiredSize = maxOf(
            hookFileOffset + hookName.size + 1,
            strtabValueOffset + 8,
            strszValueOffset + 8,
            neededValueOffset + 8,
            neededTagOffset + 2 * dynamicEntrySize + 16
        )

        if (requiredSize > out.size) {
            return PatchResult(out, false, "arm64 library too small for DT_NEEDED byte-cave injection")
        }

        val buf = ByteBuffer.wrap(out).order(ByteOrder.LITTLE_ENDIAN)
        val strtabFileOffset = buf.getLong(strtabValueOffset).toInt()
        val oldStrsz = buf.getLong(strszValueOffset)
        if (strtabFileOffset <= 0 || oldStrsz <= 0 || strtabFileOffset >= hookFileOffset) {
            return PatchResult(out, false, "arm64 dynamic string-table metadata is invalid")
        }

        val nameOffset = hookFileOffset - strtabFileOffset
        val newStrsz = nameOffset.toLong() + hookName.size + 1L
        if (newStrsz < oldStrsz || newStrsz > 0xFFFFFFFFL) {
            return PatchResult(out, false, "arm64 DT_STRSZ cannot cover the hook dependency name")
        }

        // Android's linker maps .dynstr from its ELF section header and uses
        // that mapped fragment's size for bounds checks.  Extend the matching
        // SHT_STRTAB header as well as DT_STRSZ, otherwise get_string() still
        // sees the original (short) table and aborts on the new DT_NEEDED.
        if (out.size < 64 || out[0].toInt() != 0x7f || out[1].toInt() != 'E'.code ||
            out[2].toInt() != 'L'.code || out[3].toInt() != 'F'.code || out[4].toInt() != 2 ||
            out[5].toInt() != 1
        ) {
            return PatchResult(out, false, "arm64 section-header metadata is missing")
        }
        val sectionHeaderOffset = buf.getLong(40)
        val sectionHeaderEntrySize = buf.getShort(58).toInt() and 0xFFFF
        val sectionHeaderCount = buf.getShort(60).toInt() and 0xFFFF
        if (sectionHeaderOffset <= 0L || sectionHeaderEntrySize < 64 || sectionHeaderCount <= 0 ||
            sectionHeaderOffset > Int.MAX_VALUE.toLong() ||
            sectionHeaderOffset + sectionHeaderEntrySize.toLong() * sectionHeaderCount > out.size
        ) {
            return PatchResult(out, false, "arm64 section-header table is invalid")
        }
        var dynstrHeaderOffset = -1
        for (i in 0 until sectionHeaderCount) {
            val headerOffset = sectionHeaderOffset.toInt() + i * sectionHeaderEntrySize
            val sectionType = buf.getInt(headerOffset + 4)
            val sectionOffset = buf.getLong(headerOffset + 24)
            if (sectionType == 3 && sectionOffset == strtabFileOffset.toLong()) {
                dynstrHeaderOffset = headerOffset
                break
            }
        }
        if (dynstrHeaderOffset < 0) {
            return PatchResult(out, false, "arm64 .dynstr section header was not found")
        }

        // The cave must be unused data, or already contain the injected name
        // when this operation is re-run on an APK that was partially patched.
        val existingCave = out.copyOfRange(hookFileOffset, hookFileOffset + hookName.size)
        val caveIsEmpty = existingCave.all { it == 0.toByte() }
        if (!caveIsEmpty && !existingCave.contentEquals(hookName)) {
            return PatchResult(out, false, "arm64 DT_NEEDED string cave is not empty")
        }

        // Treat a complete prior injection as a no-op.  This keeps autoPatch
        // safe when a user selects an APK that was already processed.
        if (!caveIsEmpty && oldStrsz >= newStrsz &&
            buf.getLong(neededTagOffset) == 1L &&
            buf.getLong(neededValueOffset) == nameOffset.toLong()
        ) {
            return PatchResult(out, false, null)
        }

        // Require two consecutive DT_NULL entries: one becomes DT_NEEDED and the
        // following one remains the dynamic-table terminator.
        if (buf.getLong(neededTagOffset) != 0L || buf.getLong(neededTagOffset + dynamicEntrySize) != 0L) {
            return PatchResult(out, false, "arm64 dynamic table has no spare DT_NEEDED entry")
        }

        // Write "libdothook.so\0" at the cave offset.
        for (i in hookName.indices) {
            out[hookFileOffset + i] = hookName[i]
        }
        out[hookFileOffset + hookName.size] = 0

        // DT_NEEDED's value is an offset into .dynstr. Because the cave is beyond
        // the original table, extend both string-table size fields so bionic's
        // linker maps and accepts it.
        buf.putLong(strszValueOffset, newStrsz)
        buf.putLong(dynstrHeaderOffset + 32, newStrsz)
        buf.putLong(neededTagOffset, 1L)
        buf.putLong(neededValueOffset, nameOffset.toLong())

        return PatchResult(out, true, null)
    }

    /**
     * Inject DT_NEEDED libdothook.so into an armv7 library using the inplace alias method.
     * This is a port of inject_needed_armv7_inplace from patch_il2cpp.lbl:
     * 1. Parse ELF32 section headers to find .dynsym, .dynamic, .dynstr
     * 2. Find donor symbol (__floatundidf) and alias (__aeabi_ul2d)
     * 3. Rewrite donor's name offset to point to alias
     * 4. Write "libdothook.so\0" over "__floatundidf" in the string table
     * 5. Find first DT_NULL entry in .dynamic and convert it to DT_NEEDED
     */
    fun injectNeededArmv7Inplace(data: ByteArray): PatchResult {
        val out = data.copyOf()

        // Validate ELF32
        if (out.size < 52 ||
            out[0].toInt() != 0x7f || out[1].toInt() != 'E'.code ||
            out[2].toInt() != 'L'.code || out[3].toInt() != 'F'.code ||
            out[4].toInt() != 1 || out[5].toInt() != 1
        ) {
            return PatchResult(out, false, "in-place DT_NEEDED injection requires a little-endian ELF32 library")
        }

        val buf = ByteBuffer.wrap(out).order(ByteOrder.LITTLE_ENDIAN)

        // Read section headers
        val shoff = buf.getInt(32)
        val shentsize = buf.getShort(46).toInt() and 0xFFFF
        val shnum = buf.getShort(48).toInt() and 0xFFFF

        if (shentsize < 40 || shoff == 0 || shnum == 0) {
            return PatchResult(out, false, "ELF32 section headers are missing")
        }
        if (shoff + shentsize * shnum > out.size) {
            return PatchResult(out, false, "ELF32 section header table is outside the file")
        }

        data class Section(
            val index: Int, val kind: Int, val offset: Int, val size: Int,
            val link: Int, val entsize: Int
        )

        val sections = mutableListOf<Section>()
        for (i in 0 until shnum) {
            val off = shoff + i * shentsize
            sections += Section(
                index = i,
                kind = buf.getInt(off + 4),
                offset = buf.getInt(off + 16),
                size = buf.getInt(off + 20),
                link = buf.getInt(off + 24),
                entsize = buf.getInt(off + 36)
            )
        }

        // Find .dynsym (SHT_DYNSYM=11) and .dynamic (SHT_DYNAMIC=6)
        val dynsym = sections.find { it.kind == 11 }
            ?: return PatchResult(out, false, "ELF32 .dynsym section not found")
        val dynamic = sections.find { it.kind == 6 }
            ?: return PatchResult(out, false, "ELF32 .dynamic section not found")

        if (dynsym.entsize != 16 || dynamic.entsize != 8) {
            return PatchResult(out, false, "unexpected ELF32 dynamic table entry size")
        }

        val dynstrIdx = dynsym.link
        if (dynstrIdx < 0 || dynstrIdx >= sections.size) {
            return PatchResult(out, false, "ELF32 .dynsym has invalid string-table link")
        }
        val dynstr = sections[dynstrIdx]
        val strStart = dynstr.offset
        val strLimit = dynstr.offset + dynstr.size
        val dynamicEnd = dynamic.offset + dynamic.size

        if (strLimit > out.size) {
            return PatchResult(out, false, "ELF32 dynamic string table is outside the file")
        }

        // Check if DT_NEEDED libdothook.so already present
        var neededOffset = dynamic.offset
        var alreadyPresent = false
        while (neededOffset < dynamicEnd) {
            val tag = buf.getInt(neededOffset)
            if (tag == 0) break
            if (tag == 1) { // DT_NEEDED
                val value = buf.getInt(neededOffset + 4)
                if (value < dynstr.size) {
                    val name = readCString(out, strStart + value, strLimit)
                    if (name == "libdothook.so") {
                        alreadyPresent = true
                        break
                    }
                }
            }
            neededOffset += dynamic.entsize
        }

        if (alreadyPresent) {
            return PatchResult(out, false, "") // Not an error, just unchanged
        }

        // Find donor (__floatundidf) and alias (__aeabi_ul2d) symbols
        data class Symbol(
            val index: Int, val entryOffset: Int, val nameOffset: Int,
            val value: Int, val size: Int, val kind: Int, val section: Int
        )

        var donor: Symbol? = null
        var alias: Symbol? = null

        val symCount = dynsym.size / dynsym.entsize
        for (i in 0 until symCount) {
            val symOff = dynsym.offset + i * dynsym.entsize
            if (symOff + 16 > out.size) break

            val nameOff = buf.getInt(symOff)
            if (nameOff >= dynstr.size) continue

            val sym = Symbol(
                index = i,
                entryOffset = symOff,
                nameOffset = nameOff,
                value = buf.getInt(symOff + 4),
                size = buf.getInt(symOff + 8),
                kind = out[symOff + 12].toInt() and 0x0F,
                section = buf.getShort(symOff + 14).toInt() and 0xFFFF
            )

            val symName = readCString(out, strStart + nameOff, strLimit)
            if (symName == "__floatundidf") donor = sym
            else if (symName == "__aeabi_ul2d") alias = sym
        }

        if (donor == null || alias == null) {
            return PatchResult(out, false,
                "expected ARMv7 alias symbols were not found; is this the 9.2.0 library?")
        }

        // Validate donor/alias describe the same defined function
        if (donor!!.value != alias!!.value ||
            donor!!.size != alias!!.size ||
            donor!!.kind != alias!!.kind ||
            donor!!.section != alias!!.section ||
            donor!!.section == 0
        ) {
            return PatchResult(out, false,
                "ARMv7 alias symbols no longer describe the same defined function")
        }

        // Check no relocation references the donor
        for (reloc in sections) {
            if (reloc.kind == 4 || reloc.kind == 9) { // SHT_REL or SHT_RELA
                val expectedEntSize = if (reloc.kind == 4) 8 else 12
                val entSize = if (reloc.entsize == 0) expectedEntSize else reloc.entsize
                if (entSize != expectedEntSize) continue

                var relOff = reloc.offset
                while (relOff < reloc.offset + reloc.size) {
                    if (relOff + entSize > out.size) break
                    val symIdx = (buf.getInt(relOff + 4) ushr 8)
                    if (symIdx == donor!!.index) {
                        return PatchResult(out, false,
                            "the ARMv7 donor alias is referenced by a relocation")
                    }
                    relOff += entSize
                }
            }
        }

        // 1. Rewrite donor's name offset to alias
        buf.putInt(donor!!.entryOffset, alias!!.nameOffset)

        // 2. Write "libdothook.so\0" over "__floatundidf" in string table
        val donorSlot = strStart + donor!!.nameOffset
        val hookName = "libdothook.so".toByteArray(Charsets.UTF_8)
        val donorName = readCString(out, donorSlot, strLimit)
        if (donorName != "__floatundidf") {
            return PatchResult(out, false, "ARMv7 donor string changed unexpectedly")
        }
        if (hookName.size > donorName.length) {
            return PatchResult(out, false, "ARMv7 donor string slot is too short")
        }
        for (i in hookName.indices) {
            out[donorSlot + i] = hookName[i]
        }
        out[donorSlot + hookName.size] = 0

        // 3. Find first DT_NULL entry with a spare slot after it
        var nullOff = dynamic.offset
        var foundNull = -1
        while (nullOff < dynamicEnd) {
            if (nullOff + 8 > out.size) break
            if (buf.getInt(nullOff) == 0) {
                foundNull = nullOff
                break
            }
            nullOff += dynamic.entsize
        }

        if (foundNull < 0 || foundNull + 16 > dynamicEnd) {
            return PatchResult(out, false, "ELF32 .dynamic has no spare entry for DT_NEEDED")
        }

        // Verify the slot after the DT_NULL is also zero (terminator)
        if (buf.getInt(foundNull + 8) != 0) {
            return PatchResult(out, false,
                "ELF32 .dynamic has no terminating DT_NULL after the spare entry")
        }

        // Write DT_NEEDED (tag=1) with donor's name offset
        buf.putInt(foundNull, 1)
        buf.putInt(foundNull + 4, donor!!.nameOffset)

        return PatchResult(out, true, null)
    }

    /**
     * Full auto-patch: apply 16 sites + inject DT_NEEDED.
     * Returns patched data or error.
     */
    fun autoPatch(data: ByteArray, abi: String): PatchResult {
        // First apply the 16 patch sites
        val siteResult = applySites(data, abi)
        if (siteResult.error != null) return siteResult

        // Then inject DT_NEEDED
        return if (abi == PatchRequest.ARM64) {
            injectNeededArm64(siteResult.data)
        } else {
            injectNeededArmv7Inplace(siteResult.data)
        }
    }

    // ---- ELF identification ----

    fun elfAbi(data: ByteArray): String? {
        if (data.size < 20) return null
        if (data[0].toInt() != 0x7f || data[1].toInt() != 'E'.code ||
            data[2].toInt() != 'L'.code || data[3].toInt() != 'F'.code
        ) return null

        // ELF class at offset 4
        if (data[4] == 1.toByte()) {
            // 32-bit, check machine at offset 18
            val machine = (data[18].toInt() and 0xFF) or ((data[19].toInt() and 0xFF) shl 8)
            return if (machine == 40) PatchRequest.ARMV7 else null
        } else if (data[4] == 2.toByte()) {
            // 64-bit, check machine at offset 18
            val machine = (data[18].toInt() and 0xFF) or ((data[19].toInt() and 0xFF) shl 8)
            return if (machine == 183) PatchRequest.ARM64 else null
        }
        return null
    }

    // ---- Internal helpers ----

    private fun readCString(data: ByteArray, start: Int, limit: Int): String {
        var end = start
        while (end < limit && data[end] != 0.toByte()) end++
        if (end >= limit) return ""
        return String(data, start, end - start, Charsets.UTF_8)
    }

    private fun indexOfBytes(data: ByteArray, needle: ByteArray): Int {
        val max = data.size - needle.size
        for (i in 0..max) {
            var match = true
            for (j in needle.indices) {
                if (data[i + j] != needle[j]) { match = false; break }
            }
            if (match) return i
        }
        return -1
    }

    fun hexToBytes(hex: String): ByteArray {
        val len = hex.length
        require(len % 2 == 0) { "hex patch bytes must contain complete bytes" }
        val data = ByteArray(len / 2)
        var i = 0
        while (i < len) {
            val high = hex[i].digitToIntOrNull(16)
                ?: throw IllegalArgumentException("invalid hex patch byte at index $i")
            val low = hex[i + 1].digitToIntOrNull(16)
                ?: throw IllegalArgumentException("invalid hex patch byte at index ${i + 1}")
            data[i / 2] = (high shl 4 or low).toByte()
            i += 2
        }
        return data
    }
}
