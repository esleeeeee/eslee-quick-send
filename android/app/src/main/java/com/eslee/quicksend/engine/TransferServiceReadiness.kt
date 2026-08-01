package com.eslee.quicksend.engine

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.withTimeoutOrNull

enum class TransferServicePhase {
    NOT_STARTED,
    STARTING,
    READY,
    DEGRADED,
    FAILED_FATAL,
    STOPPING,
}

data class TransferServiceSnapshot(
    val phase:TransferServicePhase,
    val reason:String,
    val changedAtMillis:Long,
) {
    val manualConnectAllowed:Boolean
        get()=phase==TransferServicePhase.READY||phase==TransferServicePhase.DEGRADED
}

data class TransferServiceStateChange(
    val from:TransferServiceSnapshot,
    val to:TransferServiceSnapshot,
)

sealed interface ServiceReadinessResult {
    data class Ready(val snapshot:TransferServiceSnapshot):ServiceReadinessResult
    data class Failed(val snapshot:TransferServiceSnapshot):ServiceReadinessResult
    data class TimedOut(val snapshot:TransferServiceSnapshot):ServiceReadinessResult
}

class TransferServiceReadiness {
    private val _state=MutableStateFlow(
        TransferServiceSnapshot(
            phase=TransferServicePhase.NOT_STARTED,
            reason="Service has not been started",
            changedAtMillis=System.currentTimeMillis(),
        ),
    )
    val state=_state.asStateFlow()

    fun transition(phase:TransferServicePhase,reason:String):TransferServiceStateChange {
        val from=_state.value
        val to=TransferServiceSnapshot(phase,reason,System.currentTimeMillis())
        _state.value=to
        return TransferServiceStateChange(from,to)
    }

    suspend fun awaitManualConnect(timeoutMillis:Long):ServiceReadinessResult {
        val initial=_state.value
        if(initial.manualConnectAllowed)return ServiceReadinessResult.Ready(initial)
        if(initial.phase==TransferServicePhase.FAILED_FATAL)return ServiceReadinessResult.Failed(initial)

        val terminal=withTimeoutOrNull(timeoutMillis) {
            state.first { snapshot->
                snapshot.manualConnectAllowed||snapshot.phase==TransferServicePhase.FAILED_FATAL
            }
        } ?: return ServiceReadinessResult.TimedOut(_state.value)

        return if(terminal.manualConnectAllowed)ServiceReadinessResult.Ready(terminal)
        else ServiceReadinessResult.Failed(terminal)
    }
}
