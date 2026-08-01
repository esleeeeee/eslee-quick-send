package com.eslee.quicksend.engine

import android.util.Base64
import com.eslee.quicksend.persistence.TransferFileRecord
import com.eslee.quicksend.protocol.FileCompleteMessage
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.io.File
import java.util.UUID

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35], manifest = Config.NONE)
class CompletedFileReplayTest {
    @Test
    fun completedReceiverReplaysDurableSizeWithoutReplacingFinalFile() {
        val finalFile = File.createTempFile("quicksend-completed-", ".bin")
        val content = ByteArray(10) { it.toByte() }
        finalFile.writeBytes(content)
        val merkle = MerkleAccumulator().apply {
            addChunk(content.copyOfRange(0, 8))
            addChunk(content.copyOfRange(8, 10))
        }
        val record = TransferFileRecord(
            transferId = UUID.randomUUID(),
            fileId = UUID.randomUUID(),
            relativePath = "finished.bin",
            sourceUri = "",
            partialUri = null,
            finalUri = finalFile.toURI().toString(),
            size = content.size.toLong(),
            modifiedTicks = 0,
            stableSourceId = null,
            chunkSize = 8,
            receivedOffset = content.size.toLong(),
            committedOffset = content.size.toLong(),
            merkleLeaves = merkle.snapshot(),
            state = TransferState.COMPLETED,
        )
        try {
            assertTrue(CompletedFileReplay.canReplay(record, finalFile.exists()))
            val resume = CompletedFileReplay.createResume(record)
            assertEquals(content.size.toLong(), resume.committedOffset)
            assertEquals(2, resume.committedLeaves)
            CompletedFileReplay.verify(
                record,
                FileCompleteMessage(
                    record.fileId,
                    record.size,
                    merkle.leafCount,
                    Base64.encodeToString(merkle.root(), Base64.NO_WRAP),
                ),
            )
            assertArrayEquals(content, finalFile.readBytes())
        } finally {
            finalFile.delete()
        }
    }
}
