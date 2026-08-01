namespace Eslee.QuickSend.Core.Transfers;

public enum PeerLinkState
{
    /// <summary>No live session, and the peer may connect at any time.</summary>
    Idle,

    /// <summary>At least one live QuickSend session exists with this peer.</summary>
    Connected,

    /// <summary>The user pressed "연결 끊기"; sessions stay blocked until an explicit reconnect.</summary>
    ManuallyDisconnected
}

/// <summary>
/// Tracks live QuickSend sessions per peer and remembers a user-requested disconnect.
/// </summary>
/// <remarks>
/// A manual disconnect only terminates sessions. Trusted-device rows, certificates and
/// identity fingerprints are never touched here, so reconnecting never re-runs SAS pairing.
/// </remarks>
public sealed class PeerConnectionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<long, Action>> _links = new(StringComparer.Ordinal);
    private readonly HashSet<string> _manuallyDisconnected = new(StringComparer.Ordinal);
    private long _nextLinkId;

    /// <summary>Raised with the peer device id whenever its link state may have changed.</summary>
    public event EventHandler<string>? Changed;

    public bool IsManuallyDisconnected(string deviceId)
    {
        lock (_gate) return _manuallyDisconnected.Contains(deviceId);
    }

    public bool IsConnected(string deviceId)
    {
        lock (_gate) return _links.TryGetValue(deviceId, out var links) && links.Count > 0;
    }

    public PeerLinkState GetState(string deviceId)
    {
        lock (_gate)
        {
            if (_manuallyDisconnected.Contains(deviceId)) return PeerLinkState.ManuallyDisconnected;
            return _links.TryGetValue(deviceId, out var links) && links.Count > 0
                ? PeerLinkState.Connected
                : PeerLinkState.Idle;
        }
    }

    public IReadOnlyCollection<string> ConnectedDeviceIds
    {
        get { lock (_gate) return _links.Where(static entry => entry.Value.Count > 0).Select(static entry => entry.Key).ToArray(); }
    }

    /// <summary>
    /// Registers a live session. Returns <c>null</c> when the user has manually
    /// disconnected this peer, in which case the caller must not start the session.
    /// </summary>
    public PeerLink? TryRegisterLink(string deviceId, Action close)
    {
        PeerLink link;
        lock (_gate)
        {
            if (_manuallyDisconnected.Contains(deviceId)) return null;
            var linkId = ++_nextLinkId;
            if (!_links.TryGetValue(deviceId, out var links))
            {
                links = [];
                _links[deviceId] = links;
            }
            links[linkId] = close;
            link = new PeerLink(this, deviceId, linkId);
        }
        RaiseChanged(deviceId);
        return link;
    }

    /// <summary>
    /// Marks the peer as manually disconnected and returns the close callbacks of every
    /// session that must be torn down. Idempotent.
    /// </summary>
    public IReadOnlyList<Action> RequestManualDisconnect(string deviceId)
    {
        Action[] closers;
        lock (_gate)
        {
            _manuallyDisconnected.Add(deviceId);
            closers = _links.TryGetValue(deviceId, out var links) ? links.Values.ToArray() : [];
        }
        RaiseChanged(deviceId);
        return closers;
    }

    /// <summary>Clears the manual-disconnect block after an explicit user reconnect.</summary>
    public bool AllowReconnect(string deviceId)
    {
        bool removed;
        lock (_gate) removed = _manuallyDisconnected.Remove(deviceId);
        if (removed) RaiseChanged(deviceId);
        return removed;
    }

    internal void Release(string deviceId, long linkId)
    {
        lock (_gate)
        {
            if (!_links.TryGetValue(deviceId, out var links)) return;
            if (!links.Remove(linkId)) return;
            if (links.Count == 0) _links.Remove(deviceId);
        }
        RaiseChanged(deviceId);
    }

    private void RaiseChanged(string deviceId) => Changed?.Invoke(this, deviceId);
}

public sealed class PeerLink(PeerConnectionRegistry registry, string deviceId, long linkId) : IDisposable
{
    private int _released;

    public string DeviceId => deviceId;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        registry.Release(deviceId, linkId);
    }
}
