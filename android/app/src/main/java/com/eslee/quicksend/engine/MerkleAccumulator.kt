package com.eslee.quicksend.engine

import java.security.MessageDigest

class MerkleAccumulator private constructor(private val leaves: MutableList<ByteArray>) {
    constructor() : this(mutableListOf())
    val leafCount: Int get() = leaves.size

    fun addChunk(data: ByteArray) {
        val digest = MessageDigest.getInstance("SHA-256")
        digest.update(byteArrayOf(0))
        leaves += digest.digest(data)
    }

    fun root(): ByteArray {
        if (leaves.isEmpty()) return MessageDigest.getInstance("SHA-256").digest(byteArrayOf(0))
        var level = leaves.map(ByteArray::clone)
        while (level.size > 1) {
            level = level.indices.step(2).map { index ->
                val left = level[index]
                val right = level.getOrElse(index + 1) { left }
                MessageDigest.getInstance("SHA-256").apply { update(byteArrayOf(1)); update(left); update(right) }.digest()
            }
        }
        return level.single()
    }

    fun snapshot(): ByteArray = ByteArray(leaves.size * 32).also { output -> leaves.forEachIndexed { i, leaf -> leaf.copyInto(output, i * 32) } }

    companion object {
        fun restore(snapshot: ByteArray): MerkleAccumulator {
            require(snapshot.size % 32 == 0)
            return MerkleAccumulator(snapshot.asList().chunked(32).map { it.toByteArray() }.toMutableList())
        }
    }
}
