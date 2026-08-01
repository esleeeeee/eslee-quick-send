package com.eslee.quicksend.engine

import com.eslee.quicksend.protocol.ProtocolConstants

enum class TransferState { QUEUED, DISCOVERING, CONNECTING, PAIRING, TRANSFERRING, RECOVERING, RETRYING, WAITING_DEVICE, WAITING_CONDITION, VERIFYING, PAUSED, COMPLETED, CANCELLED, USER_ACTION_REQUIRED, FAILED_FATAL }

class RetryPolicy {
    private val schedule = longArrayOf(2_000, 5_000, 10_000, 20_000, 30_000)
    fun delayMillis(attempt: Int): Long = if (attempt in schedule.indices) schedule[attempt] else 60_000
}

class CheckpointPolicy(private val bytes: Long = ProtocolConstants.CHECKPOINT_BYTES, private val millis: Long = ProtocolConstants.CHECKPOINT_MILLIS) {
    private var lastOffset = 0L
    private var lastAt = System.currentTimeMillis()
    fun due(offset: Long, now: Long): Boolean = offset - lastOffset >= bytes || now - lastAt >= millis
    fun committed(offset: Long, now: Long) { require(offset >= lastOffset); lastOffset = offset; lastAt = now }
    fun restore(offset: Long, at: Long) { require(offset >= 0); lastOffset = offset; lastAt = at }
}
