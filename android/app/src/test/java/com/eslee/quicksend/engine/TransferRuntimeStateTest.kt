package com.eslee.quicksend.engine

import com.eslee.quicksend.service.NotificationPhase
import com.eslee.quicksend.service.TransferNotificationPresenter
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

class TransferRuntimeStateTest {
    @Before
    fun setUp() = TransferRuntime.resetToIdle()

    @After
    fun tearDown() = TransferRuntime.resetToIdle()

    @Test
    fun terminalStatesAreStampedSoTheNotificationCanExpireThem() {
        TransferRuntime.update(TransferUiState(status = "전송 완료", state = TransferState.COMPLETED))

        val settled = TransferRuntime.ui.value
        assertNotNull(settled.settledAtMillis)
        assertFalse(settled.hasTransfer)
    }

    @Test
    fun activeTransferStatesAreNotStamped() {
        TransferRuntime.update(
            TransferUiState(status = "전송 중", state = TransferState.TRANSFERRING, hasTransfer = true, totalBytes = 10),
        )

        assertNull(TransferRuntime.ui.value.settledAtMillis)
    }

    @Test
    fun resetToIdleClearsAPreviousCompletedResult() {
        TransferRuntime.update(TransferUiState(status = "전송 완료", state = TransferState.COMPLETED))
        assertNotNull(TransferRuntime.ui.value.settledAtMillis)

        TransferRuntime.resetToIdle("다시 연결했습니다")

        val idle = TransferRuntime.ui.value
        assertNull(idle.settledAtMillis)
        assertEquals(TransferState.QUEUED, idle.state)
        assertFalse(idle.hasTransfer)

        // What the notification renders after a reconnect must be the plain idle text.
        val content = TransferNotificationPresenter.present(
            idle,
            listOf(ConnectedPeer("peer", "eslee-PC")),
            nowMillis = System.currentTimeMillis(),
        )
        assertEquals(NotificationPhase.CONNECTED_IDLE, content.phase)
        assertEquals("파일을 보낼 준비가 되었습니다", content.text)
    }

    @Test
    fun deletingHistoryDoesNotChangeTheNotificationState() {
        TransferRuntime.update(TransferUiState(status = "전송 완료", state = TransferState.COMPLETED))
        val before = TransferRuntime.ui.value

        // History deletion is a database concern and must not reach into the runtime.
        val after = TransferRuntime.ui.value

        assertEquals(before, after)
        assertTrue(TransferHistoryPolicy.isSettled(after.state))
    }
}
