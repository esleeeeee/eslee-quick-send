package com.eslee.quicksend.engine

/**
 * Separates jobs that are genuinely recoverable from rows that only look active because
 * an old attempt never reached a terminal state.
 *
 * Deleting a history row never touches the sent source file or the received file; it only
 * removes the database bookkeeping for a job that is not running.
 */
object TransferHistoryPolicy {
    /** How long an interrupted job stays eligible for automatic resume. */
    const val RESUME_WINDOW_MILLIS = 7L * 24 * 60 * 60 * 1000

    const val RESUME_EXPIRED_ERROR_CODE = "RESUME_EXPIRED"

    /** Error code stored when the user, not the network, ended the session. */
    const val MANUAL_DISCONNECT_ERROR_CODE = "MANUAL_DISCONNECT"

    /** States in which no worker owns the job, so its history row is safe to remove. */
    val SETTLED_STATES = setOf(
        TransferState.COMPLETED,
        TransferState.CANCELLED,
        TransferState.FAILED_FATAL,
        // The job stopped and is waiting for the user; nothing is running for it.
        TransferState.USER_ACTION_REQUIRED,
    )

    fun isSettled(state: TransferState): Boolean = state in SETTLED_STATES

    /**
     * True when a persisted job should be picked up again by automatic resume. Settled
     * jobs are never resumed, and an untouched job eventually ages out so a stale row
     * cannot pretend to be an active transfer forever.
     */
    fun isResumable(state: TransferState, updatedUtc: Long, now: Long): Boolean =
        !isSettled(state) && now - updatedUtc <= RESUME_WINDOW_MILLIS

    /** True when the row is stale enough that resume must be abandoned. */
    fun isResumeExpired(state: TransferState, updatedUtc: Long, now: Long): Boolean =
        !isSettled(state) && now - updatedUtc > RESUME_WINDOW_MILLIS

    /**
     * A history row can be deleted when the job is settled, or when nothing is currently
     * running for it. Live transfers stay protected.
     */
    fun canDeleteHistory(state: TransferState, isRunning: Boolean): Boolean =
        isSettled(state) || !isRunning
}
