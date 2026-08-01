package com.eslee.quicksend.service

import com.eslee.quicksend.engine.ConnectedPeer
import com.eslee.quicksend.engine.TransferState
import com.eslee.quicksend.engine.TransferUiState

enum class NotificationPhase {
    /** The foreground service is up but no peer is connected. */
    WAITING,

    /** Connected to at least one peer with nothing to transfer. */
    CONNECTED_IDLE,

    /** Bytes are moving. */
    TRANSFERRING,

    /** A real reconnect/resume attempt is in progress for a live job. */
    RECOVERING,

    /** A live job is held by the user (pause or manual disconnect). */
    HELD,
}

data class NotificationContent(
    val phase: NotificationPhase,
    val title: String,
    val text: String,
    val subText: String?,
    val showProgress: Boolean,
    val indeterminate: Boolean,
    val progressPermille: Int,
    val showPause: Boolean,
    val showCancel: Boolean,
)

/**
 * Maps the runtime state onto the ongoing foreground notification.
 *
 * The service must keep an ongoing notification alive, but a connected-and-idle app has
 * no work to report: those phases render a static notification with no progress bar at
 * all. Only a live transfer shows a determinate bar, and only a real reconnect shows the
 * indeterminate one.
 */
object TransferNotificationPresenter {
    private val TRANSFER_STATES = setOf(TransferState.TRANSFERRING, TransferState.VERIFYING)
    private val RECOVERY_STATES = setOf(
        TransferState.RECOVERING,
        TransferState.RETRYING,
        TransferState.CONNECTING,
        TransferState.PAIRING,
        TransferState.DISCOVERING,
        TransferState.WAITING_DEVICE,
        TransferState.WAITING_CONDITION,
    )
    private val HELD_STATES = setOf(TransferState.PAUSED)

    /** How long a finished transfer keeps reporting its outcome before going quiet. */
    const val COMPLETION_NOTICE_MILLIS = 6_000L

    fun present(
        ui: TransferUiState,
        connected: List<ConnectedPeer>,
        nowMillis: Long = System.currentTimeMillis(),
    ): NotificationContent {
        if (!ui.hasTransfer) return idle(ui, connected, nowMillis)

        val permille = (ui.progress * 1_000).toInt().coerceIn(0, 1_000)
        val metrics = if (ui.totalBytes > 0) {
            "${formatBytes(ui.safeBytes)} / ${formatBytes(ui.totalBytes)}" +
                if (ui.speedBytesPerSecond > 0) " · ${formatBytes(ui.speedBytesPerSecond.toLong())}/s" else ""
        } else {
            null
        }

        return when (ui.state) {
            in TRANSFER_STATES -> NotificationContent(
                phase = NotificationPhase.TRANSFERRING,
                title = ui.currentFile,
                text = ui.etaSeconds?.let { "${ui.status} · 약 ${formatEta(it)} 남음" } ?: ui.status,
                subText = metrics,
                showProgress = true,
                indeterminate = false,
                progressPermille = permille,
                showPause = ui.canPause,
                showCancel = ui.canCancel,
            )
            in HELD_STATES -> NotificationContent(
                phase = NotificationPhase.HELD,
                title = ui.currentFile,
                text = ui.status,
                subText = metrics,
                showProgress = true,
                indeterminate = false,
                progressPermille = permille,
                showPause = ui.canPause,
                showCancel = ui.canCancel,
            )
            in RECOVERY_STATES -> NotificationContent(
                phase = NotificationPhase.RECOVERING,
                title = ui.currentFile,
                text = ui.status,
                subText = metrics,
                showProgress = true,
                indeterminate = true,
                progressPermille = permille,
                showPause = ui.canPause,
                showCancel = ui.canCancel,
            )
            // Anything else that still claims a live job is treated as held rather than
            // spinning forever.
            else -> NotificationContent(
                phase = NotificationPhase.HELD,
                title = ui.currentFile,
                text = ui.status,
                subText = metrics,
                showProgress = true,
                indeterminate = false,
                progressPermille = permille,
                showPause = ui.canPause,
                showCancel = ui.canCancel,
            )
        }
    }

    private fun idle(ui: TransferUiState, connected: List<ConnectedPeer>, nowMillis: Long): NotificationContent {
        // A finished transfer is reported only inside its notice window. Without this the
        // last outcome stayed pinned to the notification for the rest of the session and
        // reappeared after every reconnect.
        val noticeFresh = ui.settledAtMillis?.let { nowMillis - it in 0..COMPLETION_NOTICE_MILLIS } == true
        val finishedNote = if (!noticeFresh) null else when (ui.state) {
            TransferState.COMPLETED -> "전송 완료"
            TransferState.CANCELLED -> "전송 취소됨"
            TransferState.USER_ACTION_REQUIRED -> "확인이 필요한 전송이 있습니다"
            TransferState.FAILED_FATAL -> "전송에 실패했습니다"
            else -> null
        }
        val title = when {
            connected.isEmpty() -> "eslee QuickSend"
            connected.size == 1 -> "${connected.single().deviceName}에 연결됨"
            else -> "기기 ${connected.size}대에 연결됨"
        }
        return NotificationContent(
            phase = if (connected.isEmpty()) NotificationPhase.WAITING else NotificationPhase.CONNECTED_IDLE,
            title = title,
            text = when {
                finishedNote != null -> finishedNote
                connected.isNotEmpty() -> "파일을 보낼 준비가 되었습니다"
                else -> "같은 네트워크의 기기를 기다리는 중"
            },
            subText = if (connected.isEmpty()) null else "파일은 인터넷을 거치지 않습니다",
            showProgress = false,
            indeterminate = false,
            progressPermille = 0,
            showPause = false,
            showCancel = false,
        )
    }

    private fun formatBytes(value: Long): String {
        var amount = value.coerceAtLeast(0).toDouble()
        val units = arrayOf("B", "KB", "MB", "GB", "TB")
        var index = 0
        while (amount >= 1024 && index < units.lastIndex) {
            amount /= 1024
            index++
        }
        return "%.1f %s".format(amount, units[index])
    }

    private fun formatEta(seconds: Long): String = "%d분 %02d초".format(seconds / 60, seconds % 60)
}
