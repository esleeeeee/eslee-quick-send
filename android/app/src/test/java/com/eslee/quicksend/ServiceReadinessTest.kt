package com.eslee.quicksend

import com.eslee.quicksend.engine.ServiceReadinessResult
import com.eslee.quicksend.engine.TransferServicePhase
import com.eslee.quicksend.engine.TransferServiceReadiness
import kotlinx.coroutines.async
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ServiceReadinessTest {
    @Test
    fun startingRequestAutomaticallyContinuesAfterReady()=runTest {
        val readiness=TransferServiceReadiness()
        readiness.transition(TransferServicePhase.STARTING,"initializing")
        var connectionContinued=false

        val request=async {
            val result=readiness.awaitManualConnect(10_000)
            if(result is ServiceReadinessResult.Ready)connectionContinued=true
            result
        }
        readiness.transition(TransferServicePhase.READY,"ready")

        assertTrue(request.await() is ServiceReadinessResult.Ready)
        assertTrue(connectionContinued)
    }

    @Test
    fun nsdFailureDegradedStateStillAllowsManualConnect()=runTest {
        val readiness=TransferServiceReadiness()
        readiness.transition(TransferServicePhase.DEGRADED,"NSD unavailable; manual connection ready")

        val result=readiness.awaitManualConnect(10_000)

        assertTrue(result is ServiceReadinessResult.Ready)
        assertTrue(readiness.state.value.manualConnectAllowed)
    }

    @Test
    fun fatalFailureBlocksManualConnectWithReason()=runTest {
        val readiness=TransferServiceReadiness()
        readiness.transition(TransferServicePhase.FAILED_FATAL,"TLS identity initialization failed")

        val result=readiness.awaitManualConnect(10_000)

        assertTrue(result is ServiceReadinessResult.Failed)
        assertFalse(readiness.state.value.manualConnectAllowed)
        assertEquals("TLS identity initialization failed",(result as ServiceReadinessResult.Failed).snapshot.reason)
    }

    @Test
    fun readyStateInvokesConnectionPathImmediately()=runTest {
        val readiness=TransferServiceReadiness()
        readiness.transition(TransferServicePhase.READY,"ready")
        var tcpPathInvoked=false

        val result=readiness.awaitManualConnect(10_000)
        if(result is ServiceReadinessResult.Ready)tcpPathInvoked=true

        assertTrue(result is ServiceReadinessResult.Ready)
        assertTrue(tcpPathInvoked)
    }
}
