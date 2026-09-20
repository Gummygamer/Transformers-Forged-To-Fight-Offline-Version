package com.gummygamer.tftfpvphost.state

/**
 * Wall-clock source. Every time-dependent store operation takes an explicit `nowMs` and has a
 * wrapper that reads a [Clock], mirroring `touch_presence_at` / `touch_presence`
 * (Server/fakeserver.lbl:403-427) so TTL and expiry stay testable without sleeping.
 */
fun interface Clock {
    fun nowMs(): Long
}

/** [Clock] backed by [System.currentTimeMillis]. */
object SystemClock : Clock {
    override fun nowMs(): Long = System.currentTimeMillis()
}
