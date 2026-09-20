package com.gummygamer.tftfpvphost.api

import com.gummygamer.tftfpvphost.json.JsonEncoder
import com.gummygamer.tftfpvphost.json.JsonValue

/** Envelope writers (Server/fakeserver.lbl:1108-1136), returning ready-to-send bytes. */
internal object Reply {
    /** `spaced_envelope`: `, ` and `: ` separators. */
    fun spaced(result: JsonValue): ByteArray = JsonEncoder.spacedEnvelope(result).toByteArray(Charsets.UTF_8)

    /** `compact_envelope`: `,` and `:` separators. */
    fun compact(result: JsonValue): ByteArray = JsonEncoder.compactEnvelope(result).toByteArray(Charsets.UTF_8)

    /** `spaced_envelope_async`. */
    fun spacedAsync(result: JsonValue, asyncItems: List<JsonValue>): ByteArray =
        JsonEncoder.spacedEnvelopeAsync(result, asyncItems).toByteArray(Charsets.UTF_8)
}
