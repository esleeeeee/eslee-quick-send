package com.eslee.quicksend.engine

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class PeerConnectionRegistryTest {
    @Test
    fun manualDisconnectBlocksAutomaticReconnectUntilTheUserReconnects() {
        val registry = PeerConnectionRegistry()

        val initial = registry.tryRegisterLink(PEER, "내 PC") {}
        assertNotNull(initial)
        assertEquals(PeerLinkState.CONNECTED, registry.state(PEER))
        initial?.close()
        assertEquals(PeerLinkState.IDLE, registry.state(PEER))

        registry.requestManualDisconnect(PEER)

        assertTrue(registry.isManuallyDisconnected(PEER))
        assertEquals(PeerLinkState.MANUALLY_DISCONNECTED, registry.state(PEER))
        // Automatic reconnection must not slip back in behind the user's decision.
        assertNull(registry.tryRegisterLink(PEER, "내 PC") {})
        assertNotNull(registry.tryRegisterLink(OTHER, "다른 PC") {})

        assertTrue(registry.allowReconnect(PEER))
        assertFalse(registry.isManuallyDisconnected(PEER))
        assertNotNull(registry.tryRegisterLink(PEER, "내 PC") {})
        assertEquals(PeerLinkState.CONNECTED, registry.state(PEER))
    }

    @Test
    fun manualDisconnectClosesOnlyThatPeersSessions() {
        val registry = PeerConnectionRegistry()
        var closedPeer = 0
        var closedOther = 0
        val first = registry.tryRegisterLink(PEER, "내 PC") { closedPeer++ }
        val second = registry.tryRegisterLink(PEER, "내 PC") { closedPeer++ }
        val other = registry.tryRegisterLink(OTHER, "다른 PC") { closedOther++ }

        registry.requestManualDisconnect(PEER).forEach { it() }

        assertEquals(2, closedPeer)
        assertEquals(0, closedOther)
        assertEquals(PeerLinkState.CONNECTED, registry.state(OTHER))

        first?.close()
        second?.close()
        // The block survives the sessions ending; only an explicit reconnect clears it.
        assertEquals(PeerLinkState.MANUALLY_DISCONNECTED, registry.state(PEER))
        other?.close()
    }

    @Test
    fun snapshotReportsConnectedPeersForTheIdleNotification() {
        val registry = PeerConnectionRegistry()
        assertFalse(registry.snapshot.value.hasConnectedPeer)

        val link = registry.tryRegisterLink(PEER, "eslee-PC") {}

        assertTrue(registry.snapshot.value.hasConnectedPeer)
        assertEquals(listOf(ConnectedPeer(PEER, "eslee-PC")), registry.snapshot.value.connected)

        link?.close()
        assertFalse(registry.snapshot.value.hasConnectedPeer)
    }

    @Test
    fun reconnectLeavesNoDisconnectedStateForTheUiToRender() {
        val registry = PeerConnectionRegistry()
        registry.requestManualDisconnect(PEER)
        assertEquals(PeerLinkState.MANUALLY_DISCONNECTED, registry.snapshot.value.state(PEER))

        registry.allowReconnect(PEER)
        val link = registry.tryRegisterLink(PEER, "eslee-PC") {}

        // The card's colours are derived from this snapshot, so a stale
        // MANUALLY_DISCONNECTED entry here is what would keep the card looking disconnected.
        val snapshot = registry.snapshot.value
        assertEquals(PeerLinkState.CONNECTED, snapshot.state(PEER))
        assertFalse(snapshot.states.containsValue(PeerLinkState.MANUALLY_DISCONNECTED))
        assertTrue(snapshot.hasConnectedPeer)

        link?.close()
        assertEquals(PeerLinkState.IDLE, registry.snapshot.value.state(PEER))
    }

    private companion object {
        const val PEER = "peer-device-id"
        const val OTHER = "other-device-id"
    }
}
