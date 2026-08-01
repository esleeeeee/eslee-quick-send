package com.eslee.quicksend.engine

/**
 * Normalizes the QuickSend display name a user types for this phone.
 *
 * The display name never participates in identity: the stable device id, the Keystore
 * key and its fingerprint are untouched when the name changes, so renaming never
 * triggers SAS re-pairing.
 */
object DeviceNameRules {
    const val MAX_LENGTH = 32

    fun normalize(candidate: String?): DeviceNameResult {
        if (candidate == null) return DeviceNameResult.Invalid("기기 이름을 입력하세요.")

        val builder = StringBuilder(candidate.length)
        var pendingSpace = false
        var index = 0
        while (index < candidate.length) {
            val codePoint = candidate.codePointAt(index)
            val charCount = Character.charCount(codePoint)
            index += charCount
            // Tabs and newlines are control characters but still separate words, so they
            // collapse into a single space instead of being dropped outright.
            if (Character.isWhitespace(codePoint) || Character.isSpaceChar(codePoint)) {
                if (builder.isNotEmpty()) pendingSpace = true
                continue
            }
            if (Character.isISOControl(codePoint)) continue
            when (Character.getType(codePoint).toByte()) {
                Character.CONTROL, Character.FORMAT, Character.PRIVATE_USE, Character.SURROGATE -> continue
                else -> Unit
            }
            if (pendingSpace) {
                builder.append(' ')
                pendingSpace = false
            }
            builder.appendCodePoint(codePoint)
        }

        val value = builder.toString()
        if (value.isEmpty()) return DeviceNameResult.Invalid("기기 이름은 공백만으로 지정할 수 없습니다.")
        if (value.codePointCount(0, value.length) > MAX_LENGTH) {
            return DeviceNameResult.Invalid("기기 이름은 ${MAX_LENGTH}자 이하로 입력하세요.")
        }
        return DeviceNameResult.Valid(value)
    }

    /** Returns the stored name when it is still valid, otherwise the platform fallback. */
    fun normalizeOrFallback(candidate: String?, fallback: String): String =
        (normalize(candidate) as? DeviceNameResult.Valid)?.name
            ?: (normalize(fallback) as? DeviceNameResult.Valid)?.name
            ?: "QuickSend"
}

sealed interface DeviceNameResult {
    data class Valid(val name: String) : DeviceNameResult
    data class Invalid(val message: String) : DeviceNameResult
}
