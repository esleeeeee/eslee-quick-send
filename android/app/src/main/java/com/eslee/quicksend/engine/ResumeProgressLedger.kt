package com.eslee.quicksend.engine

import android.util.Base64
import com.eslee.quicksend.persistence.TransferFileRecord
import com.eslee.quicksend.protocol.FileCompleteMessage
import com.eslee.quicksend.protocol.ProtocolException
import com.eslee.quicksend.protocol.ResumeInfoMessage
import java.security.MessageDigest
import java.util.UUID

class ResumeProgressLedger {
    private val committedOffsets = mutableMapOf<UUID, Long>()

    fun observeResume(fileId: UUID, committedOffset: Long): Long =
        observe(fileId, committedOffset, "Receiver resume")

    fun observeCheckpoint(fileId: UUID, committedOffset: Long): Long =
        observe(fileId, committedOffset, "Receiver checkpoint")

    fun committedOffset(fileId: UUID): Long = committedOffsets[fileId] ?: 0L

    private fun observe(fileId: UUID, committedOffset: Long, source: String): Long {
        if (committedOffset < 0) throw ProtocolException("$source offset cannot be negative")
        val previous = committedOffsets[fileId]
        if (previous != null && committedOffset < previous) {
            throw ProtocolException(
                "$source moved backwards for $fileId: previously committed $previous, now $committedOffset",
            )
        }
        committedOffsets[fileId] = committedOffset
        return committedOffset
    }

    companion object {
        fun overallCommitted(completedBefore: Long, fileCommittedOffset: Long): Long {
            require(completedBefore >= 0 && fileCommittedOffset >= 0)
            return Math.addExact(completedBefore, fileCommittedOffset)
        }
    }
}

object CompletedFileReplay {
    fun canReplay(record: TransferFileRecord, completedFileExists: Boolean): Boolean =
        completedFileExists &&
            record.state == TransferState.COMPLETED &&
            record.committedOffset == record.size &&
            record.finalUri != null

    fun createResume(record: TransferFileRecord): ResumeInfoMessage {
        if (record.state != TransferState.COMPLETED || record.committedOffset != record.size) {
            throw ProtocolException("Only a durably completed file can be replayed")
        }
        val merkle = MerkleAccumulator.restore(record.merkleLeaves)
        val expectedLeaves =
            if (record.size == 0L) 0 else Math.toIntExact((record.size + record.chunkSize - 1) / record.chunkSize)
        if (merkle.leafCount != expectedLeaves) {
            throw ProtocolException("Completed file Merkle state is inconsistent with its size")
        }
        return ResumeInfoMessage(
            record.transferId,
            record.fileId,
            record.size,
            merkle.leafCount,
            Base64.encodeToString(record.merkleLeaves, Base64.NO_WRAP),
        )
    }

    fun verify(record: TransferFileRecord, complete: FileCompleteMessage) {
        if (complete.fileId != record.fileId || complete.size != record.size) {
            throw ProtocolException("Completed replay metadata does not match the stored file")
        }
        val merkle = MerkleAccumulator.restore(record.merkleLeaves)
        val actualRoot = Base64.decode(complete.merkleRootBase64, Base64.NO_WRAP)
        if (complete.leafCount != merkle.leafCount || !MessageDigest.isEqual(merkle.root(), actualRoot)) {
            throw FileIntegrityException()
        }
    }
}
