package com.gummygamer.tftfpvphost.server

import java.io.OutputStream

/**
 * Response writer. Every routed answer is `200 OK` with `Content-Type: application/json`
 * and an exact `Content-Length`, as Server/fakeserver.lbl:2002-2008 does; a HEAD request
 * gets `200` with `Content-Length: 0` and no body (`:2010-2018`).
 */
object HttpResponse {
    fun writeOk(out: OutputStream, body: ByteArray, head: Boolean, close: Boolean) {
        val length = if (head) 0 else body.size
        val header = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" +
            "Content-Length: $length\r\n" + connectionLine(close) + "\r\n"
        out.write(header.toByteArray(Charsets.ISO_8859_1))
        if (!head) out.write(body)
        out.flush()
    }

    /** Short plain-text rejection for requests that never reached routing. */
    fun writeRejection(out: OutputStream, status: Int, reason: String) {
        val text = reason.filter { it.code in 32..126 }
        val header = "HTTP/1.1 $status ${statusText(status)}\r\nContent-Type: text/plain\r\n" +
            "Content-Length: ${text.length}\r\nConnection: close\r\n\r\n"
        out.write((header + text).toByteArray(Charsets.ISO_8859_1))
        out.flush()
    }

    fun writeContinue(out: OutputStream) {
        out.write("HTTP/1.1 100 Continue\r\n\r\n".toByteArray(Charsets.ISO_8859_1))
        out.flush()
    }

    private fun connectionLine(close: Boolean): String = if (close) "Connection: close\r\n" else ""

    private fun statusText(status: Int): String = when (status) {
        400 -> "Bad Request"
        413 -> "Payload Too Large"
        431 -> "Request Header Fields Too Large"
        501 -> "Not Implemented"
        else -> "Error"
    }
}
