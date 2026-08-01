package com.eslee.quicksend.service

import com.eslee.quicksend.engine.ConnectedPeer
import com.eslee.quicksend.engine.TransferState
import com.eslee.quicksend.engine.TransferUiState
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class TransferNotificationPresenterTest {
    @Test
    fun idleStateNeverShowsAnIndeterminateSpinner() {
        val waiting = TransferNotificationPresenter.present(TransferUiState(), emptyList())

        assertEquals(NotificationPhase.WAITING, waiting.phase)
        assertFalse(waiting.showProgress)
        assertFalse(waiting.indeterminate)
        assertFalse(waiting.showPause)
        assertFalse(waiting.showCancel)
    }

    @Test
    fun connectedIdleShowsAStaticNotificationNamingThePeer() {
        val content = TransferNotificationPresenter.present(TransferUiState(), listOf(PC))

        assertEquals(NotificationPhase.CONNECTED_IDLE, content.phase)
        assertEquals("eslee-PC에 연결됨", content.title)
        assertEquals("파일을 보낼 준비가 되었습니다", content.text)
        assertFalse(content.showProgress)
        assertFalse(content.indeterminate)
    }

    @Test
    fun onlyAnActiveTransferShowsProgressPercentAndSpeed() {
        val content = TransferNotificationPresenter.present(
            TransferUiState(
                status = "전송 중",
                currentFile = "photo.jpg",
                safeBytes = 512,
                totalBytes = 1_024,
                speedBytesPerSecond = 2_048.0,
                etaSeconds = 30,
                state = TransferState.TRANSFERRING,
                canPause = true,
                canCancel = true,
                hasTransfer = true,
            ),
            listOf(PC),
        )

        assertEquals(NotificationPhase.TRANSFERRING, content.phase)
        assertTrue(content.showProgress)
        assertFalse(content.indeterminate)
        assertEquals(500, content.progressPermille)
        assertEquals("photo.jpg", content.title)
        assertTrue(content.subText!!.contains("/s"))
        assertTrue(content.showPause)
        assertTrue(content.showCancel)
    }

    @Test
    fun indeterminateProgressOnlyAppearsWhileRecoveringALiveJob() {
        val recovering = TransferNotificationPresenter.present(
            TransferUiState(
                status = "연결이 끊어졌습니다. 자동으로 다시 연결하는 중...",
                totalBytes = 1_024,
                state = TransferState.RECOVERING,
                hasTransfer = true,
                canCancel = true,
            ),
            listOf(PC),
        )

        assertEquals(NotificationPhase.RECOVERING, recovering.phase)
        assertTrue(recovering.indeterminate)

        // The very same state without a live job must stay static.
        val settled = TransferNotificationPresenter.present(
            TransferUiState(status = "전송이 중단되었습니다", state = TransferState.RECOVERING),
            listOf(PC),
        )
        assertFalse(settled.indeterminate)
        assertFalse(settled.showProgress)
    }

    @Test
    fun pausedAndManualDisconnectShowProgressWithoutSpinning() {
        val held = TransferNotificationPresenter.present(
            TransferUiState(
                status = "연결을 끊었습니다. 다시 연결하면 이어서 전송합니다",
                safeBytes = 256,
                totalBytes = 1_024,
                state = TransferState.PAUSED,
                hasTransfer = true,
                canCancel = true,
            ),
            emptyList(),
        )

        assertEquals(NotificationPhase.HELD, held.phase)
        assertTrue(held.showProgress)
        assertFalse(held.indeterminate)
        assertEquals(250, held.progressPermille)
    }

    @Test
    fun completedTransferReturnsToTheStaticIdleNotification() {
        val settled = TransferUiState(status = "전송 완료", state = TransferState.COMPLETED, settledAtMillis = 1_000)
        val completed = TransferNotificationPresenter.present(settled, listOf(PC), nowMillis = 1_500)

        assertEquals(NotificationPhase.CONNECTED_IDLE, completed.phase)
        assertEquals("전송 완료", completed.text)
        assertFalse(completed.showProgress)
        assertFalse(completed.indeterminate)
        assertFalse(completed.showPause)
        assertFalse(completed.showCancel)
    }

    @Test
    fun completionNoticeExpiresIntoPlainConnectedIdle() {
        val settled = TransferUiState(
            status = "전송 완료",
            currentFile = "photo.jpg",
            state = TransferState.COMPLETED,
            settledAtMillis = 1_000,
        )
        val expiry = 1_000 + TransferNotificationPresenter.COMPLETION_NOTICE_MILLIS + 1

        val after = TransferNotificationPresenter.present(settled, listOf(PC), nowMillis = expiry)

        assertEquals(NotificationPhase.CONNECTED_IDLE, after.phase)
        assertEquals("eslee-PC에 연결됨", after.title)
        assertEquals("파일을 보낼 준비가 되었습니다", after.text)
        // The finished file must not linger anywhere in the notification.
        assertFalse(after.title.contains("photo.jpg"))
        assertFalse(after.text.contains("완료"))
    }

    @Test
    fun reconnectAfterACompletedTransferDoesNotReplayTheOldResult() {
        // TransferRuntime.resetToIdle() drops the settled stamp when the connection changes.
        val reconnected = TransferUiState(status = "다시 연결했습니다")

        val content = TransferNotificationPresenter.present(reconnected, listOf(PC), nowMillis = 1_500)

        assertEquals(NotificationPhase.CONNECTED_IDLE, content.phase)
        assertEquals("파일을 보낼 준비가 되었습니다", content.text)
        assertFalse(content.showProgress)
    }

    @Test
    fun anUnstampedTerminalStateNeverPinsAnOutcome() {
        // Any state restored without a settled stamp is treated as plain idle.
        listOf(TransferState.COMPLETED, TransferState.CANCELLED, TransferState.FAILED_FATAL).forEach { state ->
            val content = TransferNotificationPresenter.present(
                TransferUiState(state = state),
                listOf(PC),
                nowMillis = 9_999_999,
            )
            assertEquals("파일을 보낼 준비가 되었습니다", content.text)
        }
    }

    @Test
    fun noLiveStateGetsStuckOnAnEndlessConnectingSpinner() {
        // Every state is either static or bounded by a live job; nothing spins while idle.
        TransferState.entries.forEach { state ->
            val idle = TransferNotificationPresenter.present(TransferUiState(state = state), emptyList())
            assertFalse("$state spins while idle", idle.indeterminate)
            assertFalse("$state shows a bar while idle", idle.showProgress)
            assertNotEquals(NotificationPhase.RECOVERING, idle.phase)
        }
    }

    private companion object {
        val PC = ConnectedPeer("peer-device-id", "eslee-PC")
    }
}
