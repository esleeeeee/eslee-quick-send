package com.eslee.quicksend.engine

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.util.concurrent.atomic.AtomicBoolean

enum class PeerLinkState {
    /** No live session, and the peer may connect at any time. */
    IDLE,

    /** At least one live QuickSend session exists with this peer. */
    CONNECTED,

    /** The user pressed 연결 끊기; sessions stay blocked until an explicit reconnect. */
    MANUALLY_DISCONNECTED,
}

data class ConnectedPeer(val deviceId: String, val deviceName: String)

data class PeerConnectionSnapshot(
    val states: Map<String, PeerLinkState> = emptyMap(),
    val connected: List<ConnectedPeer> = emptyList(),
) {
    fun state(deviceId: String): PeerLinkState = states[deviceId] ?: PeerLinkState.IDLE
    val hasConnectedPeer: Boolean get() = connected.isNotEmpty()
}

/**
 * Tracks live QuickSend sessions per peer and remembers a user-requested disconnect.
 *
 * A manual disconnect only terminates sessions. Trusted-device rows, the Keystore
 * identity and its fingerprint are never touched here, so reconnecting never re-runs
 * SAS pairing.
 */
class PeerConnectionRegistry {
    private val gate = Any()
    private val links = HashMap<String, MutableMap<Long, Session>>()
    private val manuallyDisconnected = HashSet<String>()
    private var nextLinkId = 0L
    private val _snapshot = MutableStateFlow(PeerConnectionSnapshot())
    val snapshot: StateFlow<PeerConnectionSnapshot> = _snapshot.asStateFlow()

    fun isManuallyDisconnected(deviceId: String): Boolean = synchronized(gate) {
        manuallyDisconnected.contains(deviceId)
    }

    fun state(deviceId: String): PeerLinkState = synchronized(gate) { stateLocked(deviceId) }

    /**
     * Registers a live session. Returns `null` when the user has manually disconnected
     * this peer, in which case the caller must not start the session.
     */
    fun tryRegisterLink(deviceId: String, deviceName: String, close: () -> Unit): PeerLink? {
        val link = synchronized(gate) {
            if (manuallyDisconnected.contains(deviceId)) return null
            val linkId = ++nextLinkId
            links.getOrPut(deviceId) { LinkedHashMap() }[linkId] = Session(deviceName, close)
            PeerLink(this, deviceId, linkId)
        }
        publish()
        return link
    }

    /**
     * Marks the peer as manually disconnected and returns the close callbacks of every
     * session that must be torn down. Idempotent.
     */
    fun requestManualDisconnect(deviceId: String): List<() -> Unit> {
        val closers = synchronized(gate) {
            manuallyDisconnected.add(deviceId)
            links[deviceId]?.values?.map(Session::close).orEmpty()
        }
        publish()
        return closers
    }

    /** Clears the manual-disconnect block after an explicit user reconnect. */
    fun allowReconnect(deviceId: String): Boolean {
        val removed = synchronized(gate) { manuallyDisconnected.remove(deviceId) }
        if (removed) publish()
        return removed
    }

    internal fun release(deviceId: String, linkId: Long) {
        val changed = synchronized(gate) {
            val sessions = links[deviceId] ?: return@synchronized false
            if (sessions.remove(linkId) == null) return@synchronized false
            if (sessions.isEmpty()) links.remove(deviceId)
            true
        }
        if (changed) publish()
    }

    private fun stateLocked(deviceId: String): PeerLinkState = when {
        manuallyDisconnected.contains(deviceId) -> PeerLinkState.MANUALLY_DISCONNECTED
        links[deviceId]?.isNotEmpty() == true -> PeerLinkState.CONNECTED
        else -> PeerLinkState.IDLE
    }

    private fun publish() {
        _snapshot.value = synchronized(gate) {
            val keys = links.keys + manuallyDisconnected
            PeerConnectionSnapshot(
                states = keys.associateWith(::stateLocked),
                connected = links.entries
                    .mapNotNull { (deviceId, sessions) ->
                        sessions.values.lastOrNull()?.let { ConnectedPeer(deviceId, it.deviceName) }
                    }
                    .sortedBy { it.deviceName.lowercase() },
            )
        }
    }

    private data class Session(val deviceName: String, val close: () -> Unit)
}

class PeerLink(
    private val registry: PeerConnectionRegistry,
    val deviceId: String,
    private val linkId: Long,
) : AutoCloseable {
    private val released = AtomicBoolean(false)

    override fun close() {
        if (released.getAndSet(true)) return
        registry.release(deviceId, linkId)
    }
}
