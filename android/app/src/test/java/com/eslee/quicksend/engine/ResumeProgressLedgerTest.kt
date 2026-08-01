package com.eslee.quicksend.engine

import com.eslee.quicksend.protocol.ProtocolException
import org.junit.Assert.assertEquals
import org.junit.Assert.fail
import org.junit.Test
import java.util.UUID

class ResumeProgressLedgerTest {
    @Test
    fun reconnectCannotMoveProgressBehindDurableCheckpoint() {
        val fileId = UUID.randomUUID()
        val ledger = ResumeProgressLedger()

        assertEquals(64L, ledger.observeResume(fileId, 64L))
        assertEquals(128L, ledger.observeCheckpoint(fileId, 128L))
        assertEquals(
            1_128L,
            ResumeProgressLedger.overallCommitted(1_000L, ledger.committedOffset(fileId)),
        )

        try {
            ledger.observeResume(fileId, 0L)
            fail("Expected a backwards receiver resume to be rejected")
        } catch (_: ProtocolException) {
            assertEquals(128L, ledger.committedOffset(fileId))
        }
    }
}
