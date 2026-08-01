package com.eslee.quicksend.engine

/**
 * Transport encoding for the DNS-SD TXT `name` value.
 *
 * TXT values must stay printable ASCII for the Windows mDNS writer, but QuickSend
 * display names may contain Hangul. The value is percent-encoded UTF-8, so a plain ASCII
 * name is transmitted unchanged and the key keeps its original meaning. The `id` and `fp`
 * keys are unaffected.
 */
object DeviceNameWire {
    fun encode(name: String): String {
        if (name.isEmpty()) return ""
        val builder = StringBuilder(name.length)
        for (byte in name.toByteArray(Charsets.UTF_8)) {
            val value = byte.toInt() and 0xFF
            if (isSafe(value)) builder.append(value.toChar())
            else builder.append('%').append("%02X".format(value))
        }
        return builder.toString()
    }

    fun decode(value: String?): String {
        if (value.isNullOrEmpty()) return ""
        if (!value.contains('%')) return value
        val bytes = ArrayList<Byte>(value.length)
        var index = 0
        while (index < value.length) {
            val character = value[index]
            val high = if (index + 2 < value.length) hex(value[index + 1]) else -1
            val low = if (index + 2 < value.length) hex(value[index + 2]) else -1
            if (character == '%' && high >= 0 && low >= 0) {
                bytes.add(((high shl 4) or low).toByte())
                index += 3
                continue
            }
            character.toString().toByteArray(Charsets.UTF_8).forEach(bytes::add)
            index++
        }
        return String(bytes.toByteArray(), Charsets.UTF_8)
    }

    private fun isSafe(value: Int): Boolean =
        value in 'a'.code..'z'.code || value in 'A'.code..'Z'.code || value in '0'.code..'9'.code ||
            value == ' '.code || value == '-'.code || value == '_'.code || value == '.'.code ||
            value == '('.code || value == ')'.code

    private fun hex(character: Char): Int = when (character) {
        in '0'..'9' -> character - '0'
        in 'a'..'f' -> character - 'a' + 10
        in 'A'..'F' -> character - 'A' + 10
        else -> -1
    }
}
