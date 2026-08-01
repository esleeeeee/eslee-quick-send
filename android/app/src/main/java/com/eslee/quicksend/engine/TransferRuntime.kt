package com.eslee.quicksend.engine

import com.eslee.quicksend.discovery.DiscoveredDevice
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.withTimeoutOrNull
import java.util.UUID

data class TransferUiState(
    val status:String="전송할 파일을 선택하세요",val currentFile:String="대기 중",val safeBytes:Long=0,val totalBytes:Long=0,
    val speedBytesPerSecond:Double=0.0,val etaSeconds:Long?=null,val state:TransferState=TransferState.QUEUED,
    val autoRecovery:Boolean=true,val canPause:Boolean=false,val canCancel:Boolean=false,
    /**
     * True only while a job is genuinely in flight. The idle notification depends on this
     * to stay static instead of showing an endless indeterminate spinner.
     */
    val hasTransfer:Boolean=false,
    /**
     * When a job settled, in [System.currentTimeMillis]. A finished transfer is a moment,
     * not a mode: the notification shows the outcome only briefly and then falls back to
     * the plain connected-idle text instead of pinning the last result forever.
     */
    val settledAtMillis:Long?=null,
){val progress:Float get()=if(totalBytes<=0)0f else (safeBytes.toDouble()/totalBytes).toFloat().coerceIn(0f,1f)}

data class PairingPrompt(val id:UUID,val deviceId:String,val deviceName:String,val fingerprint:String,val code:String)

sealed interface ManualCoordinatorAvailability {
    data class Ready(
        val coordinator:AndroidTransferCoordinator,
        val service:TransferServiceSnapshot,
    ):ManualCoordinatorAvailability
    data class Failed(val service:TransferServiceSnapshot):ManualCoordinatorAvailability
    data class TimedOut(val service:TransferServiceSnapshot):ManualCoordinatorAvailability
}

object TransferRuntime {
    private val _ui=MutableStateFlow(TransferUiState())
    val ui=_ui.asStateFlow()
    private val _pairing=MutableSharedFlow<PairingPrompt>(extraBufferCapacity=2)
    val pairing=_pairing.asSharedFlow()
    val readiness=TransferServiceReadiness()
    val serviceState=readiness.state
    private val _coordinator=MutableStateFlow<AndroidTransferCoordinator?>(null)
    val coordinator:AndroidTransferCoordinator? get()=_coordinator.value
    val coordinatorFlow=_coordinator.asStateFlow()

    /** Stand-ins the UI can collect before the transfer service has been bound. */
    val emptyPeerConnections=MutableStateFlow(PeerConnectionSnapshot()).asStateFlow()
    val emptyRunningTransfers=MutableStateFlow(emptySet<UUID>()).asStateFlow()
    /** Terminal outcomes are stamped so the notification can expire them. */
    private val SETTLED_STATES=setOf(
        TransferState.COMPLETED,
        TransferState.CANCELLED,
        TransferState.FAILED_FATAL,
        TransferState.USER_ACTION_REQUIRED,
    )

    fun update(state:TransferUiState){
        _ui.value=if(!state.hasTransfer&&state.state in SETTLED_STATES&&state.settledAtMillis==null){
            state.copy(settledAtMillis=System.currentTimeMillis())
        } else {
            state
        }
    }

    /**
     * Drops any finished-transfer result and returns to a plain idle state. Called when the
     * connection itself changes, so a reconnect never re-displays the previous outcome.
     */
    fun resetToIdle(status:String="전송할 파일을 선택하세요"){_ui.value=TransferUiState(status=status)}

    fun startupIssue(message:String){_ui.value=_ui.value.copy(status=message)}
    fun prompt(value:PairingPrompt){_pairing.tryEmit(value)}
    fun attachCoordinator(value:AndroidTransferCoordinator){_coordinator.value=value}
    fun detachCoordinator(value:AndroidTransferCoordinator?=null){
        if(value==null||_coordinator.value===value)_coordinator.value=null
    }

    suspend fun awaitManualCoordinator(timeoutMillis:Long=10_000):ManualCoordinatorAvailability {
        val immediateState=serviceState.value
        val immediateCoordinator=_coordinator.value
        if(immediateState.phase==TransferServicePhase.FAILED_FATAL){
            return ManualCoordinatorAvailability.Failed(immediateState)
        }
        if(immediateState.manualConnectAllowed&&immediateCoordinator!=null){
            return ManualCoordinatorAvailability.Ready(immediateCoordinator,immediateState)
        }

        val value=withTimeoutOrNull(timeoutMillis) {
            combine(serviceState,_coordinator){state,coordinator->state to coordinator}
                .first{(state,coordinator)->
                    state.phase==TransferServicePhase.FAILED_FATAL||
                        (state.manualConnectAllowed&&coordinator!=null)
                }
        } ?: return ManualCoordinatorAvailability.TimedOut(serviceState.value)

        val state=value.first
        val coordinator=value.second
        return if(state.phase==TransferServicePhase.FAILED_FATAL||coordinator==null){
            ManualCoordinatorAvailability.Failed(state)
        } else {
            ManualCoordinatorAvailability.Ready(coordinator,state)
        }
    }
}
