using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Eslee.QuickSend.Core.Persistence;
using Eslee.QuickSend.Core.Protocol;
using Eslee.QuickSend.Core.Storage;
using Eslee.QuickSend.Core.Transfers;
using Eslee.QuickSend.Windows.Diagnostics;
using Eslee.QuickSend.Windows.Discovery;
using Eslee.QuickSend.Windows.Persistence;
using Eslee.QuickSend.Windows.Security;

namespace Eslee.QuickSend.Windows.Transfers;

public sealed class TransferCoordinator : IDisposable
{
    private readonly SqliteTransferStore _store;
    private readonly TrustedDeviceStore _trust;
    private readonly DeviceIdentityService _identity;
    private readonly DeviceNameService _deviceName;
    private readonly MdnsDiscoveryService _discovery;
    private readonly DiagnosticLog _log;
    private readonly string _receiveDirectory;
    private readonly TlsChannelFactory _tls;
    private readonly PowerRequestService _power = new();
    private readonly RetryPolicy _retryPolicy = new();
    private readonly SemaphoreSlim _retrySignal = new(0, 1);
    private readonly SemaphoreSlim _outgoingGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeTransfers = new();
    private readonly object _queueGate = new();
    private readonly List<Guid> _outgoingQueue = [];
    private readonly object _pairingGate = new();
    private readonly Dictionary<Guid, TaskCompletionSource<bool>> _pairingDecisions = [];
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private bool _paused;
    private int _recoveryStarted;
    private TransferUiState _lastUiState = TransferUiState.NoTransfer("전송할 항목을 선택하세요");

    public TransferCoordinator(
        SqliteTransferStore store,
        TrustedDeviceStore trust,
        DeviceIdentityService identity,
        DeviceNameService deviceName,
        MdnsDiscoveryService discovery,
        DiagnosticLog log,
        string receiveDirectory)
    {
        _store = store;
        _trust = trust;
        _identity = identity;
        _deviceName = deviceName;
        _discovery = discovery;
        _log = log;
        _receiveDirectory = receiveDirectory;
        _tls = new TlsChannelFactory(trust, identity, log);
    }

    public event EventHandler<TransferUiState>? ProgressChanged;
    public event EventHandler<PairingPrompt>? PairingRequested;

    /// <summary>Raised when the running set or the outgoing queue order changes.</summary>
    public event EventHandler? RunningTransfersChanged;

    public bool HasActiveTransfer => !_activeTransfers.IsEmpty;

    /// <summary>Live sessions and user-requested disconnects, keyed by peer device id.</summary>
    public PeerConnectionRegistry Peers { get; } = new();

    /// <summary>Transfer ids a worker currently owns; their history rows must stay protected.</summary>
    public IReadOnlySet<Guid> RunningTransferIds => _activeTransfers.Keys.ToHashSet();

    /// <summary>
    /// 1-based position of every outgoing job that is waiting for an earlier job to finish.
    /// A job that already owns its turn is absent, so it is never labelled as queued.
    /// </summary>
    public IReadOnlyDictionary<Guid, int> QueuePositions
    {
        get
        {
            lock (_queueGate)
                return _outgoingQueue
                    .Select(static (transferId, index) => (transferId, position: index + 1))
                    .ToDictionary(static entry => entry.transferId, static entry => entry.position);
        }
    }

    /// <summary>True when the job's peer is currently discoverable and online.</summary>
    public bool IsDestinationOnline(string destinationDeviceId) =>
        _discovery.Find(destinationDeviceId)?.IsOnline == true;

    private void EnterQueue(Guid transferId)
    {
        lock (_queueGate) _outgoingQueue.Add(transferId);
        RunningTransfersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LeaveQueue(Guid transferId)
    {
        bool removed;
        lock (_queueGate) removed = _outgoingQueue.Remove(transferId);
        if (removed) RunningTransfersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Ends every live session with the peer and blocks automatic reconnection until the
    /// user reconnects explicitly. Trusted-device rows and certificates are left untouched.
    /// </summary>
    public async Task DisconnectPeerAsync(string deviceId)
    {
        var closers = Peers.RequestManualDisconnect(deviceId);
        foreach (var close in closers)
        {
            try { close(); }
            catch (Exception ex) { _log.Warn("peer.disconnect.session_close.failed", new { deviceId, error = ex.GetType().Name }); }
        }
        SignalImmediateRetry();
        await _log.InfoAsync("peer.disconnect.manual", new
        {
            deviceId,
            closedSessions = closers.Count,
            initiator = "local_user",
            trustPreserved = true
        }).ConfigureAwait(false);
    }

    /// <summary>Clears a manual disconnect so transfers and incoming sessions are allowed again.</summary>
    public async Task ReconnectPeerAsync(string deviceId)
    {
        var cleared = Peers.AllowReconnect(deviceId);
        SignalImmediateRetry();
        await _log.InfoAsync("peer.reconnect.manual", new { deviceId, cleared, initiator = "local_user" }).ConfigureAwait(false);
    }

    public async Task StartAsync()
    {
        if (_listener is not null) return;
        await _log.InfoAsync("listener.start.begin", new { port = ProtocolConstants.DefaultPort, protocol = "TCP/TLS", dualMode = true }).ConfigureAwait(false);
        try
        {
            _listener = new TcpListener(IPAddress.IPv6Any, ProtocolConstants.DefaultPort);
            _listener.Server.DualMode = true;
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start(32);
            _acceptLoop = AcceptLoopAsync(_lifetime.Token);
            NetworkChange.NetworkAddressChanged += NetworkAddressChanged;
            _discovery.DeviceChanged += DiscoveryChanged;
            await _log.InfoAsync("listener.start.success", new
            {
                port = ProtocolConstants.DefaultPort,
                endpoint = _listener.LocalEndpoint.ToString(),
                dualMode = _listener.Server.DualMode
            }).ConfigureAwait(false);
            await _log.InfoAsync("listener.bound.address", new
            {
                socketAddress = ((IPEndPoint)_listener.LocalEndpoint).Address.ToString(),
                lanIpv4 = LanIpv4Addresses()
            }).ConfigureAwait(false);
            await _log.InfoAsync("listener.bound.port", new { port = ((IPEndPoint)_listener.LocalEndpoint).Port }).ConfigureAwait(false);
            await _log.InfoAsync("listener.dual_mode", new { enabled = _listener.Server.DualMode }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _listener?.Stop();
            _listener = null;
            await _log.ErrorAsync("listener.start.failed", ex, new { port = ProtocolConstants.DefaultPort }).ConfigureAwait(false);
            throw;
        }
    }

    public void StartRecovery()
    {
        if (Interlocked.Exchange(ref _recoveryStarted, 1) != 0) return;
        _ = RecoverPersistedJobsAsync(_lifetime.Token).ContinueWith(
            task => _log.Error("recovery.jobs.failed", task.Exception!.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    public async Task<QueueFilesResult> QueueFilesAsync(string destinationDeviceId, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return new QueueFilesResult(0, 0, []);
        var device = _discovery.Find(destinationDeviceId) ?? throw new InvalidOperationException("선택한 기기를 현재 찾을 수 없습니다.");
        var expansion = SourcePathExpander.Expand(paths);
        foreach (var issue in expansion.Issues)
            _log.Warn("transfer.source.skipped", new
            {
                issue.Reason,
                pathToken = DiagnosticLog.PrivateFileToken(issue.Path ?? string.Empty)
            });
        if (expansion.Files.Count == 0)
            return new QueueFilesResult(0, expansion.Issues.Count, expansion.Issues.Select(static issue => issue.Reason).ToArray());

        var local = await _identity.GetOrCreateAsync(_lifetime.Token);
        var transferId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var job = new TransferJobRecord(transferId, local.DeviceId, destinationDeviceId, TransferDirection.Send, TransferState.Queued, now, now);
        await _store.UpsertJobAsync(job, _lifetime.Token);
        var records = new List<TransferFileRecord>(expansion.Files.Count);
        foreach (var entry in expansion.Files)
        {
            var record = new TransferFileRecord(
                transferId, Guid.NewGuid(), entry.RelativePath, entry.Path, null, null,
                entry.Size, entry.ModifiedUtcTicks, null, ProtocolConstants.DefaultChunkSize,
                0, 0, [], TransferState.Queued);
            records.Add(record);
            await _store.UpsertFileAsync(record, _lifetime.Token);
        }

        StartOutgoing(job, records, device.DeviceId, _lifetime.Token);
        await _log.InfoAsync("transfer.job.queued", new
        {
            transferId,
            destinationDeviceId,
            fileCount = records.Count,
            totalBytes = SafeTotalBytes(records),
            skippedItems = expansion.Issues.Count
        }).ConfigureAwait(false);
        return new QueueFilesResult(records.Count, expansion.Issues.Count, expansion.Issues.Select(static issue => issue.Reason).ToArray());
    }

    public void TogglePause()
    {
        _paused = !_paused;
        if (!_paused) SignalImmediateRetry();
        Publish(_lastUiState.HasTransfer
            ? _lastUiState with
            {
                StatusText = _paused ? "일시정지됨" : "이어 전송 준비 중",
                BytesPerSecond = 0,
                EstimatedRemaining = null
            }
            : TransferUiState.NoTransfer("전송할 항목을 선택하세요"));
    }

    public async Task CancelAsync(bool deletePartial)
    {
        var transfers = _activeTransfers.Values.ToArray();
        if (transfers.Length == 0) return;
        foreach (var transfer in transfers)
            await transfer.CancelAsync();
        Publish(TransferUiState.NoTransfer("전송이 취소되었습니다"));
    }

    public void CompletePairing(Guid requestId, bool accepted)
    {
        lock (_pairingGate)
            if (_pairingDecisions.Remove(requestId, out var decision)) decision.TrySetResult(accepted);
    }

    private async Task RunOutgoingWithRecoveryAsync(TransferJobRecord job, IReadOnlyList<TransferFileRecord> files, string destinationId, CancellationToken cancellationToken)
    {
        var gateAcquired = false;
        var attempt = 0;
        var totalBytes = SafeTotalBytes(files);
        var resumeLedger = new ResumeProgressLedger();
        try
        {
            await _outgoingGate.WaitAsync(cancellationToken);
            gateAcquired = true;
            // The job now owns its turn, so it stops being reported as queued and the
            // remaining queued jobs move up a place.
            LeaveQueue(job.TransferId);
            using var power = _power.Acquire();
            while (!cancellationToken.IsCancellationRequested)
            {
                while (_paused) await WaitForRetryOrDelayAsync(TimeSpan.FromDays(1), cancellationToken);
                if (Peers.IsManuallyDisconnected(destinationId))
                {
                    await ParkForManualDisconnectAsync(job, destinationId, totalBytes, cancellationToken);
                    continue;
                }
                var device = _discovery.Find(destinationId);
                if (device is null || !device.IsOnline)
                {
                    await UpdateJobStateAsync(job, TransferState.WaitingDevice, cancellationToken);
                    Publish(TransferUiState.Active("기기를 기다리는 중...", totalBytes, canCancel: true));
                    await WaitForRetryOrDelayAsync(_retryPolicy.DelayForAttempt(attempt++), cancellationToken);
                    continue;
                }

                using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                using var link = Peers.TryRegisterLink(destinationId, () => sessionCancellation.Cancel());
                if (link is null)
                {
                    await ParkForManualDisconnectAsync(job, destinationId, totalBytes, cancellationToken);
                    continue;
                }

                try
                {
                    await UpdateJobStateAsync(job, attempt == 0 ? TransferState.Connecting : TransferState.Retrying, cancellationToken);
                    Publish(TransferUiState.Active(
                        attempt == 0 ? "기기에 연결하는 중..." : "연결을 복구하는 중...",
                        totalBytes,
                        canCancel: true));
                    var skippedFiles = await RunOutgoingSessionAsync(job, files, device, resumeLedger, sessionCancellation.Token);
                    await UpdateJobStateAsync(job, skippedFiles ? TransferState.UserActionRequired : TransferState.Completed,
                        cancellationToken, skippedFiles ? null : DateTimeOffset.UtcNow,
                        skippedFiles ? "SOURCE_CHANGED" : null);
                    Publish(TransferUiState.NoTransfer(skippedFiles
                        ? "일부 파일은 원본이 변경되어 확인이 필요합니다"
                        : "전송이 완료되었습니다"));
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException && sessionCancellation.IsCancellationRequested)
                {
                    // The session was torn down locally, not by the network. The durable
                    // checkpoint already on disk is what a later reconnect resumes from.
                    await _log.InfoAsync("transfer.session.closed.manual", new
                    {
                        job.TransferId,
                        destinationId,
                        closeInitiator = "local_user"
                    }).ConfigureAwait(false);
                    await ParkForManualDisconnectAsync(job, destinationId, totalBytes, cancellationToken);
                }
                catch (SourceChangedException ex)
                {
                    _log.Error("transfer.source.changed", ex);
                    await UpdateJobStateAsync(job, TransferState.Recovering, cancellationToken, errorCode: "SOURCE_CHANGED");
                    Publish(TransferUiState.Active("변경된 파일을 제외하고 나머지를 계속 전송합니다", totalBytes, canCancel: true));
                    continue;
                }
                catch (TlsHandshakeException ex)
                {
                    await _log.ErrorAsync("transfer.tls.failed", ex, new
                    {
                        attempt,
                        job.TransferId,
                        destinationId
                    }).ConfigureAwait(false);
                    if (attempt >= 2)
                    {
                        await UpdateJobStateAsync(job, TransferState.FailedFatal, cancellationToken, errorCode: "TLS_HANDSHAKE_FAILED");
                        Publish(TransferUiState.NoTransfer("보안 연결에 실패했습니다. 두 기기의 QuickSend를 업데이트한 뒤 다시 시도하세요."));
                        return;
                    }
                    await UpdateJobStateAsync(job, TransferState.Recovering, cancellationToken, errorCode: "TLS_HANDSHAKE_FAILED");
                    Publish(TransferUiState.Active("보안 연결을 다시 시도하는 중...", totalBytes, canCancel: true));
                    await WaitForRetryOrDelayAsync(_retryPolicy.DelayForAttempt(attempt++), cancellationToken);
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    await _log.ErrorAsync("transfer.connection.recover", ex, new
                    {
                        attempt,
                        job.TransferId,
                        destinationId,
                        fileCount = files.Count
                    }).ConfigureAwait(false);
                    await UpdateJobStateAsync(job, TransferState.Recovering, cancellationToken);
                    Publish(TransferUiState.Active("연결이 끊어져 다시 연결하는 중...", totalBytes, canCancel: true));
                    await WaitForRetryOrDelayAsync(_retryPolicy.DelayForAttempt(attempt++), cancellationToken);
                }
                catch (Exception ex)
                {
                    await _log.ErrorAsync("transfer.failed.fatal", ex, new
                    {
                        attempt,
                        job.TransferId,
                        destinationId
                    }).ConfigureAwait(false);
                    await UpdateJobStateAsync(job, TransferState.FailedFatal, CancellationToken.None, errorCode: ex.GetType().Name);
                    Publish(TransferUiState.NoTransfer("전송을 계속할 수 없습니다. 진단 로그를 확인하세요."));
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            await UpdateJobStateAsync(job, TransferState.Cancelled, CancellationToken.None);
        }
        finally
        {
            if (gateAcquired) _outgoingGate.Release();
        }
    }

    private async Task<bool> RunOutgoingSessionAsync(
        TransferJobRecord job,
        IReadOnlyList<TransferFileRecord> files,
        DiscoveredDevice device,
        ResumeProgressLedger resumeLedger,
        CancellationToken cancellationToken)
    {
        var connectionId = Guid.NewGuid();
        var sendable = new List<TransferFileRecord>(files.Count);
        var skippedFiles = false;
        foreach (var file in files)
        {
            var source = new FileInfo(file.SourceLocation);
            var expected = new SourceFingerprint(file.Size, file.ModifiedUtcTicks, file.StableSourceId);
            if (expected.Matches(source, file.StableSourceId))
            {
                sendable.Add(file);
                continue;
            }
            skippedFiles = true;
            await _store.UpsertFileAsync(file with
            {
                State = TransferState.UserActionRequired,
                ErrorCode = "SOURCE_CHANGED"
            }, cancellationToken);
        }
        var local = await _identity.GetOrCreateAsync(cancellationToken);
        var trusted = await _trust.IsTrustedAsync(device.IdentityFingerprint, cancellationToken);
        await _log.InfoAsync("outgoing.session.begin", new
        {
            job.TransferId,
            remoteDeviceId = device.DeviceId,
            address = device.Address.ToString(),
            device.Port,
            trusted,
            fileCount = files.Count,
            connectionId
        }).ConfigureAwait(false);
        await using var stream = await _tls.ConnectAsync(device.Address.ToString(), device.Port, pairingOnly: !trusted, cancellationToken);
        await _log.InfoAsync("outgoing.connection.established", new
        {
            connectionId,
            job.TransferId,
            remoteDeviceId = device.DeviceId,
            tls = true
        }).ConfigureAwait(false);
        await using var writer = new ProtocolWriter(stream);
        var reader = new ProtocolReader(stream);
        await _log.InfoAsync("outgoing.hello.send", new { local.DeviceId, remoteDeviceId = device.DeviceId }).ConfigureAwait(false);
        await writer.WriteControlAsync(MessageType.Hello, CreateHello(local), cancellationToken: cancellationToken);
        await _log.InfoAsync("outgoing.hello.await", new { remoteDeviceId = device.DeviceId }).ConfigureAwait(false);
        var remoteHello = await ReadControlAsync<HelloMessage>(reader, MessageType.Hello, writer, cancellationToken);
        await _log.InfoAsync("outgoing.hello.received", new
        {
            remoteHello.DeviceId,
            remoteHello.DeviceName,
            remoteHello.ProtocolVersion,
            remoteHello.IdentityFingerprint
        }).ConfigureAwait(false);
        ValidatePeer(stream, remoteHello);
        await _log.InfoAsync("outgoing.peer.validated", new { remoteHello.DeviceId, trusted }).ConfigureAwait(false);
        await EnsurePairedAsClientAsync(reader, writer, local, remoteHello, trusted, cancellationToken);
        await _log.InfoAsync("outgoing.session.ready", new { remoteHello.DeviceId, job.TransferId, connectionId }).ConfigureAwait(false);

        var manifestFiles = sendable.Select(static f => new ManifestFile(f.FileId, f.RelativePath, f.Size, f.ModifiedUtcTicks, f.ChunkSize, f.StableSourceId)).ToArray();
        await writer.WriteControlAsync(MessageType.JobManifest,
            new ManifestMessage(job.TransferId, job.SourceDeviceId, job.DestinationDeviceId, manifestFiles), cancellationToken: cancellationToken);

        var total = SafeTotalBytes(sendable);
        long completedBefore = 0;
        foreach (var file in sendable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file.SourceLocation);
            var fingerprint = new SourceFingerprint(file.Size, file.ModifiedUtcTicks, file.StableSourceId);
            var start = new FileStartMessage(file.TransferId, file.FileId, file.RelativePath, file.Size, file.ModifiedUtcTicks, file.ChunkSize, file.StableSourceId);
            await writer.WriteControlAsync(MessageType.FileStart, start, cancellationToken: cancellationToken);
            var resume = await ReadControlAsync<ResumeInfoMessage>(reader, MessageType.ResumeInfo, writer, cancellationToken);
            var committedOffset = resumeLedger.ObserveResume(file.FileId, resume.CommittedOffset);
            await _log.InfoAsync("outgoing.receiver.resume", new
            {
                connectionId,
                job.TransferId,
                file.FileId,
                resume.CommittedOffset,
                resume.CommittedLeaves
            }).ConfigureAwait(false);
            var stopwatch = Stopwatch.StartNew();
            var sender = new SlidingWindowSender(reader, writer, file.ChunkSize);
            sender.ChunkSent += range => _log.Info("outgoing.chunk.sent", new
            {
                connectionId,
                job.TransferId,
                range.FileId,
                range.Offset,
                range.Length
            });
            sender.ChunkAcknowledged += range => _log.Info("outgoing.chunk.acknowledged", new
            {
                connectionId,
                job.TransferId,
                range.FileId,
                range.Offset,
                range.Length
            });
            sender.CheckpointReceived += checkpoint =>
            {
                resumeLedger.ObserveCheckpoint(file.FileId, checkpoint.CommittedOffset);
                _log.Info("outgoing.checkpoint.received", new
                {
                    connectionId,
                    job.TransferId,
                    checkpoint.FileId,
                    checkpoint.CommittedOffset,
                    checkpoint.CommittedLeaves
                });
            };
            var resumeSafe = ResumeProgressLedger.OverallCommitted(completedBefore, committedOffset);
            Publish(new TransferUiState(
                "전송 중", Path.GetFileName(file.RelativePath), resumeSafe, total,
                total == 0 ? 100 : resumeSafe * 100d / total,
                0, null, true, true, true));
            sender.Progress += progress =>
            {
                var durable = resumeLedger.ObserveCheckpoint(file.FileId, progress.SafeOffset);
                var safe = ResumeProgressLedger.OverallCommitted(completedBefore, durable);
                var speed = stopwatch.Elapsed.TotalSeconds <= 0 ? 0 : Math.Max(0, progress.ReceiverWrittenOffset - resume.CommittedOffset) / stopwatch.Elapsed.TotalSeconds;
                Publish(new TransferUiState(
                    "전송 중", Path.GetFileName(file.RelativePath), safe, total, total == 0 ? 100 : safe * 100d / total,
                    speed, speed > 0 ? TimeSpan.FromSeconds((total - safe) / speed) : null, true, true, true));
            };
            await sender.SendFileAsync(info, start, resume, fingerprint, cancellationToken);
            var verify = await ReadControlAsync<FileVerifyMessage>(reader, MessageType.FileVerify, writer, cancellationToken);
            if (!verify.Verified) throw new FileIntegrityException(file.FileId);
            // Persist the per-file outcome so the history row reflects the verified send
            // instead of staying at its queued state forever.
            await _store.UpsertFileAsync(file with
            {
                CommittedOffset = file.Size,
                ReceivedOffset = file.Size,
                State = TransferState.Completed,
                ErrorCode = null
            }, cancellationToken);
            await _log.InfoAsync("outgoing.file.verified", new
            {
                connectionId,
                job.TransferId,
                file.FileId,
                file.Size
            }).ConfigureAwait(false);
            completedBefore += file.Size;
        }
        await writer.WriteControlAsync(MessageType.JobComplete, new TransferControlMessage(job.TransferId), FrameFlags.Final, cancellationToken);
        await _log.InfoAsync("outgoing.session.completed", new { connectionId, job.TransferId }).ConfigureAwait(false);
        return skippedFiles;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _log.Info("listener.accept.begin", new { endpoint = _listener?.LocalEndpoint.ToString() });
                var client = await _listener!.AcceptTcpClientAsync(cancellationToken);
                _log.Info("listener.accept.success", new
                {
                    remoteEndpoint = client.Client.RemoteEndPoint?.ToString(),
                    localEndpoint = client.Client.LocalEndPoint?.ToString()
                });
                _ = HandleIncomingClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _log.Error("listener.accept.failed", ex, new { endpoint = _listener?.LocalEndpoint.ToString() });
                await Task.Delay(1000, cancellationToken);
            }
        }
    }

    private async Task HandleIncomingClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var power = _power.Acquire();
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        PeerLink? link = null;
        try
        {
            var sessionToken = sessionCancellation.Token;
            await using var stream = await _tls.AcceptAsync(client, allowPairing: true, sessionToken);
            await using var writer = new ProtocolWriter(stream);
            var reader = new ProtocolReader(stream);
            var remote = await ReadControlAsync<HelloMessage>(reader, MessageType.Hello, writer, sessionToken);
            ValidatePeer(stream, remote);

            link = Peers.TryRegisterLink(remote.DeviceId, () => sessionCancellation.Cancel());
            if (link is null)
            {
                await _log.InfoAsync("incoming.session.refused.manual_disconnect", new
                {
                    remote.DeviceId,
                    remote.DeviceName,
                    remoteEndpoint = client.Client.RemoteEndPoint?.ToString()
                }).ConfigureAwait(false);
                await writer.WriteControlAsync(MessageType.Error,
                    new ErrorMessage("PEER_DISCONNECTED", "상대 PC에서 연결을 끊었습니다. 다시 연결한 뒤 시도하세요.", RecoveryClass.UserActionRequired),
                    FrameFlags.Response | FrameFlags.Final, cancellationToken);
                return;
            }

            var local = await _identity.GetOrCreateAsync(sessionToken);
            await writer.WriteControlAsync(MessageType.Hello, CreateHello(local), FrameFlags.Response, sessionToken);
            await EnsurePairedAsServerAsync(reader, writer, local, remote, sessionToken);

            ManifestMessage manifest;
            try
            {
                manifest = await ReadControlAsync<ManifestMessage>(reader, MessageType.JobManifest, writer, sessionToken);
            }
            catch (EndOfStreamException)
            {
                _log.Info("listener.handshake_only.complete", new
                {
                    remote.DeviceId,
                    remote.DeviceName,
                    remoteEndpoint = client.Client.RemoteEndPoint?.ToString()
                });
                return;
            }
            var now = DateTimeOffset.UtcNow;
            var job = new TransferJobRecord(manifest.TransferId, manifest.SourceDeviceId, manifest.DestinationDeviceId, TransferDirection.Receive, TransferState.Transferring, now, now);
            await _store.UpsertJobAsync(job, cancellationToken);
            var manifestTotal = manifest.Files.Sum(static file => file.Size);
            long receivedBefore = 0;
            Publish(TransferUiState.Active(
                $"{remote.DeviceName}에서 받는 중",
                manifestTotal,
                manifest.Files.Count == 1 ? Path.GetFileName(manifest.Files[0].RelativePath) : "수신 준비 중",
                canCancel: true));

            foreach (var expected in manifest.Files)
            {
                var start = await ReadControlAsync<FileStartMessage>(reader, MessageType.FileStart, writer, sessionToken);
                if (start.FileId != expected.FileId || start.Size != expected.Size)
                    throw new ProtocolException("Manifest and file start metadata differ.");
                await ReceiveOneFileAsync(reader, writer, start, receivedBefore, manifestTotal, remote.DeviceName, sessionToken);
                receivedBefore += expected.Size;
            }
            await ReadControlAsync<TransferControlMessage>(reader, MessageType.JobComplete, writer, cancellationToken);
            await UpdateJobStateAsync(job, TransferState.Completed, cancellationToken, DateTimeOffset.UtcNow);
            // The receive path published no completion before, so the history list had no
            // event to react to and only a manual refresh revealed the new row.
            Publish(TransferUiState.NoTransfer($"{remote.DeviceName}에서 받기를 완료했습니다"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException && sessionCancellation.IsCancellationRequested)
        {
            _log.Info("incoming.session.closed.manual", new { closeInitiator = "local_user" });
        }
        catch (Exception ex)
        {
            _log.Warn("incoming.session.interrupted", new { error = ex.GetType().Name, closeInitiator = "network_or_remote" });
        }
        finally
        {
            link?.Dispose();
            client.Dispose();
        }
    }

    private async Task ReceiveOneFileAsync(
        ProtocolReader reader,
        ProtocolWriter writer,
        FileStartMessage start,
        long priorBytes,
        long totalBytes,
        string senderName,
        CancellationToken cancellationToken)
    {
        var existing = await _store.FindFileAsync(start.FileId, cancellationToken);
        if (existing is not null &&
            CompletedFileReplay.CanReplay(existing, existing.FinalPath is not null && File.Exists(existing.FinalPath)))
        {
            await ReplayCompletedFileAsync(reader, writer, existing, cancellationToken);
            return;
        }
        var driveRoot = Path.GetPathRoot(Path.GetFullPath(_receiveDirectory));
        if (driveRoot is not null)
        {
            var remaining = Math.Max(0, start.Size - (existing?.CommittedOffset ?? 0));
            if (new DriveInfo(driveRoot).AvailableFreeSpace < remaining)
            {
                Publish(TransferUiState.Active("저장 공간이 부족합니다. 공간을 확보한 뒤 다시 시도하세요.", start.Size));
                await writer.WriteControlAsync(MessageType.Error,
                    new ErrorMessage("NO_SPACE", "저장 공간이 부족합니다.", RecoveryClass.WaitForCondition, start.FileId),
                    FrameFlags.Response, cancellationToken);
                throw new IOException("Insufficient receive storage.");
            }
        }
        TransferFileRecord record;
        if (existing is null)
        {
            var finalPath = SafePath.ResolveUnderRoot(_receiveDirectory, start.RelativePath);
            var partialDirectory = Path.Combine(_receiveDirectory, ".eslee-quicksend", "partials");
            record = new TransferFileRecord(start.TransferId, start.FileId, start.RelativePath, string.Empty,
                Path.Combine(partialDirectory, start.FileId.ToString("N") + ".part"), finalPath,
                start.Size, start.ModifiedUtcTicks, start.StableSourceId, start.ChunkSize,
                0, 0, [], TransferState.Transferring);
            await _store.UpsertFileAsync(record, cancellationToken);
        }
        else if (existing.State == TransferState.Completed)
        {
            var finalPath = existing.FinalPath ?? SafePath.ResolveUnderRoot(_receiveDirectory, start.RelativePath);
            var partialDirectory = Path.Combine(_receiveDirectory, ".eslee-quicksend", "partials");
            record = existing with
            {
                PartialPath = Path.Combine(partialDirectory, start.FileId.ToString("N") + ".part"),
                FinalPath = finalPath,
                ReceivedOffset = 0,
                CommittedOffset = 0,
                MerkleLeaves = [],
                State = TransferState.Transferring,
                ErrorCode = null
            };
            await _store.UpsertFileAsync(record, cancellationToken);
        }
        else
        {
            if (existing.Size != start.Size || existing.ModifiedUtcTicks != start.ModifiedUtcTicks)
                throw new ProtocolException("Existing partial file metadata does not match the sender.");
            record = existing;
        }

        await using var receiver = new ReceiverSession(record, _store);
        await receiver.InitializeAsync(cancellationToken);
        await writer.WriteControlAsync(MessageType.ResumeInfo, receiver.CreateResumeInfo(), FrameFlags.Response, cancellationToken);
        var receiveClock = Stopwatch.StartNew();
        var startOffset = receiver.ReceivedOffset;
        var chunkRetries = new Dictionary<long, int>();
        var reorderBuffer = new SortedDictionary<long, byte[]>();
        long? retryOffset = null;

        async ValueTask AcceptPayloadAsync(ReadOnlyMemory<byte> payload)
        {
            var result = await receiver.ReceiveChunkAsync(payload, DateTimeOffset.UtcNow, cancellationToken);
            if (result.Checkpoint is not null)
            {
                await writer.WriteControlAsync(MessageType.Checkpoint, result.Checkpoint, FrameFlags.Response, cancellationToken);
                _log.Info("incoming.checkpoint.sent", new
                {
                    start.TransferId,
                    start.FileId,
                    result.Checkpoint.CommittedOffset,
                    result.Checkpoint.CommittedLeaves
                });
            }
            await writer.WriteControlAsync(MessageType.ChunkAck, result.Ack, FrameFlags.Response, cancellationToken);
            // Publishing here keeps the determinate bar tied to bytes that really landed.
            var safe = priorBytes + receiver.ReceivedOffset;
            var elapsed = receiveClock.Elapsed.TotalSeconds;
            var speed = elapsed <= 0 ? 0 : Math.Max(0, receiver.ReceivedOffset - startOffset) / elapsed;
            Publish(new TransferUiState(
                $"{senderName}에서 받는 중", Path.GetFileName(start.RelativePath), safe, totalBytes,
                totalBytes == 0 ? 100 : safe * 100d / totalBytes,
                speed, speed > 0 ? TimeSpan.FromSeconds((totalBytes - safe) / speed) : null,
                false, true, true));
            _log.Info("incoming.chunk.acknowledged", new
            {
                start.TransferId,
                start.FileId,
                result.Ack.Offset,
                result.Ack.Length,
                result.Ack.ReceivedOffset
            });
        }

        while (true)
        {
            using var frame = await reader.ReadAsync(cancellationToken);
            switch (frame.Header.Type)
            {
                case MessageType.ChunkData:
                    var metadata = new ChunkPayload(frame.Payload.Span);
                    if (retryOffset is { } missing && metadata.Offset != missing)
                    {
                        if (reorderBuffer.Count >= ProtocolConstants.DefaultWindowChunks ||
                            !reorderBuffer.TryAdd(metadata.Offset, frame.Payload.ToArray()))
                            throw new ProtocolException("Retry reorder window exceeded or duplicated.");
                        break;
                    }
                    try
                    {
                        await AcceptPayloadAsync(frame.Payload);
                        retryOffset = null;
                        while (reorderBuffer.Remove(receiver.ReceivedOffset, out var buffered))
                            await AcceptPayloadAsync(buffered);
                    }
                    catch (ChunkIntegrityException ex)
                    {
                        retryOffset = ex.Offset;
                        var retries = chunkRetries.GetValueOrDefault(ex.Offset) + 1;
                        chunkRetries[ex.Offset] = retries;
                        await writer.WriteControlAsync(MessageType.Error,
                            new ErrorMessage(retries < 3 ? "CHUNK_RETRY" : "CONNECTION_REBUILD",
                                "청크 확인에 실패해 자동으로 다시 시도합니다.", RecoveryClass.Recoverable,
                                start.FileId, ex.Offset),
                            FrameFlags.Response | FrameFlags.Retry, cancellationToken);
                        if (retries >= 3) throw;
                    }
                    break;
                case MessageType.FileComplete:
                    var complete = ControlFrameCodec.Deserialize<FileCompleteMessage>(frame.Payload.Span);
                    await receiver.CompleteAsync(complete, DateTimeOffset.UtcNow, cancellationToken);
                    await writer.WriteControlAsync(MessageType.FileVerify, new FileVerifyMessage(start.FileId, true), FrameFlags.Response | FrameFlags.Final, cancellationToken);
                    return;
                case MessageType.Ping:
                    await writer.WriteControlAsync(MessageType.Pong, ControlFrameCodec.Deserialize<PingMessage>(frame.Payload.Span), FrameFlags.Response, cancellationToken);
                    break;
                case MessageType.Pong:
                    break;
                case MessageType.Pause:
                    Publish(_lastUiState with
                    {
                        StatusText = "상대 기기에서 일시정지됨",
                        BytesPerSecond = 0,
                        EstimatedRemaining = null
                    });
                    break;
                case MessageType.Cancel:
                    throw new OperationCanceledException("Remote user cancelled the transfer.");
                default:
                    throw new ProtocolException($"Unexpected {frame.Header.Type} during file receive.");
            }
        }
    }

    private async Task ReplayCompletedFileAsync(
        ProtocolReader reader,
        ProtocolWriter writer,
        TransferFileRecord record,
        CancellationToken cancellationToken)
    {
        var resume = CompletedFileReplay.CreateResume(record);
        _log.Info("incoming.receiver.resume.completed", new
        {
            record.TransferId,
            record.FileId,
            resume.CommittedOffset,
            resume.CommittedLeaves
        });
        await writer.WriteControlAsync(MessageType.ResumeInfo, resume, FrameFlags.Response, cancellationToken);
        var complete = await ReadControlAsync<FileCompleteMessage>(
            reader, MessageType.FileComplete, writer, cancellationToken);
        CompletedFileReplay.VerifyCompletion(record, complete);
        await writer.WriteControlAsync(
            MessageType.FileVerify,
            new FileVerifyMessage(record.FileId, true),
            FrameFlags.Response | FrameFlags.Final,
            cancellationToken);
        _log.Info("incoming.file.verify.replayed", new
        {
            record.TransferId,
            record.FileId,
            record.Size
        });
    }

    private async Task EnsurePairedAsClientAsync(ProtocolReader reader, ProtocolWriter writer, DeviceIdentity local, HelloMessage remote, bool trusted, CancellationToken cancellationToken)
    {
        if (trusted) return;
        var nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var request = new PairRequestMessage(local.DeviceId, _deviceName.Current, nonce, local.Fingerprint);
        await writer.WriteControlAsync(MessageType.PairRequest, request, cancellationToken: cancellationToken);
        var code = DeviceIdentityService.PairingCode(local.Fingerprint, remote.IdentityFingerprint, nonce);
        if (!await AskPairingAsync(remote.DeviceId, remote.DeviceName, remote.IdentityFingerprint, code, cancellationToken))
            throw new UnauthorizedAccessException("Pairing was rejected locally.");
        var response = await ReadControlAsync<PairAcceptMessage>(reader, MessageType.PairAccept, writer, cancellationToken);
        if (!response.Accepted || response.Nonce != nonce) throw new UnauthorizedAccessException("Pairing was rejected by the remote device.");
        await _trust.TrustAsync(remote.DeviceId, remote.DeviceName, remote.IdentityFingerprint, cancellationToken);
    }

    private async Task EnsurePairedAsServerAsync(ProtocolReader reader, ProtocolWriter writer, DeviceIdentity local, HelloMessage remote, CancellationToken cancellationToken)
    {
        if (await _trust.IsTrustedAsync(remote.IdentityFingerprint, cancellationToken)) return;
        var request = await ReadControlAsync<PairRequestMessage>(reader, MessageType.PairRequest, writer, cancellationToken);
        if (request.DeviceId != remote.DeviceId || request.IdentityFingerprint != remote.IdentityFingerprint)
            throw new ProtocolException("Pairing identity does not match HELLO.");
        var code = DeviceIdentityService.PairingCode(local.Fingerprint, remote.IdentityFingerprint, request.Nonce);
        var accepted = await AskPairingAsync(remote.DeviceId, remote.DeviceName, remote.IdentityFingerprint, code, cancellationToken);
        if (accepted) await _trust.TrustAsync(remote.DeviceId, remote.DeviceName, remote.IdentityFingerprint, cancellationToken);
        await writer.WriteControlAsync(MessageType.PairAccept, new PairAcceptMessage(local.DeviceId, request.Nonce, accepted), FrameFlags.Response, cancellationToken);
        if (!accepted) throw new UnauthorizedAccessException("Pairing was rejected.");
    }

    private async Task<bool> AskPairingAsync(string deviceId, string name, string fingerprint, string code, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pairingGate) _pairingDecisions[id] = decision;
        PairingRequested?.Invoke(this, new PairingPrompt(id, deviceId, name, fingerprint, code));
        try { return await decision.Task.WaitAsync(cancellationToken); }
        finally { lock (_pairingGate) _pairingDecisions.Remove(id); }
    }

    private HelloMessage CreateHello(DeviceIdentity identity) => new(
        ProtocolConstants.Version, typeof(TransferCoordinator).Assembly.GetName().Version?.ToString() ?? "1.0.0",
        identity.DeviceId, _deviceName.Current, "windows", identity.Fingerprint,
        ["chunk-sha256", "merkle-sha256", "resume-v1", "sliding-window", "folders"]);

    private static void ValidatePeer(SslStream stream, HelloMessage hello)
    {
        if (hello.ProtocolVersion != ProtocolConstants.Version)
            throw new ProtocolException($"상대 기기의 프로토콜 v{hello.ProtocolVersion}은 호환되지 않습니다.");
        if (string.IsNullOrWhiteSpace(hello.DeviceId) || hello.IdentityFingerprint.Length != 64)
            throw new ProtocolException("Remote HELLO identity is invalid.");
        if (stream.RemoteCertificate is null)
            throw new ProtocolException("TLS peer certificate is missing.");
        var certificateFingerprint = DeviceIdentityService.Fingerprint(new X509Certificate2(stream.RemoteCertificate));
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(certificateFingerprint),
                Convert.FromHexString(hello.IdentityFingerprint)))
            throw new ProtocolException("TLS certificate identity does not match HELLO.");
    }

    private static async ValueTask<T> ReadControlAsync<T>(ProtocolReader reader, MessageType expected, ProtocolWriter writer, CancellationToken cancellationToken) where T : notnull
    {
        while (true)
        {
            using var frame = await reader.ReadAsync(cancellationToken);
            if (frame.Header.Type == MessageType.Ping)
            {
                await writer.WriteControlAsync(MessageType.Pong, ControlFrameCodec.Deserialize<PingMessage>(frame.Payload.Span), FrameFlags.Response, cancellationToken);
                continue;
            }
            if (frame.Header.Type == MessageType.Pong)
                continue;
            if (frame.Header.Type == MessageType.Error)
                throw new RemoteTransferException(ControlFrameCodec.Deserialize<ErrorMessage>(frame.Payload.Span));
            if (frame.Header.Type != expected)
                throw new ProtocolException($"Expected {expected}, received {frame.Header.Type}.");
            return ControlFrameCodec.Deserialize<T>(frame.Payload.Span);
        }
    }

    private static string[] LanIpv4Addresses()
    {
        try
        {
            var active = NetworkInterface.GetAllNetworkInterfaces()
                .Where(static nic => nic.OperationalStatus == OperationalStatus.Up)
                .Where(static nic => nic.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
                .Where(static nic => nic.SupportsMulticast)
                .ToArray();
            var routed = active.Where(static nic => nic.GetIPProperties().GatewayAddresses.Any(static gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any))).ToArray();
            var selected = routed.Length > 0 ? routed : active;
            return selected
                .SelectMany(static nic => nic.GetIPProperties().UnicastAddresses)
                .Select(static address => address.Address)
                .Where(static address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                .Where(static address =>
                {
                    var bytes = address.GetAddressBytes();
                    return bytes.Length != 4 || bytes[0] != 169 || bytes[1] != 254;
                })
                .Select(static address => address.ToString())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private async Task UpdateJobStateAsync(TransferJobRecord job, TransferState state, CancellationToken cancellationToken, DateTimeOffset? completed = null, string? errorCode = null) =>
        await _store.UpsertJobAsync(job with { State = state, UpdatedAt = DateTimeOffset.UtcNow, CompletedAt = completed, ErrorCode = errorCode }, cancellationToken);

    /// <summary>
    /// Holds a job in a resumable paused state after the user disconnected the peer.
    /// Nothing is deleted: the committed offset on the receiver is what the next
    /// explicit reconnect resumes from.
    /// </summary>
    private async Task ParkForManualDisconnectAsync(TransferJobRecord job, string destinationId, long totalBytes, CancellationToken cancellationToken)
    {
        await UpdateJobStateAsync(job, TransferState.Paused, cancellationToken, errorCode: ManualDisconnectErrorCode);
        Publish(TransferUiState.Active("연결을 끊었습니다. 다시 연결하면 이어서 전송합니다", totalBytes, canCancel: true));
        _log.Info("transfer.parked.manual_disconnect", new { job.TransferId, destinationId });
        await WaitForRetryOrDelayAsync(TimeSpan.FromMinutes(5), cancellationToken);
    }

    private async Task RecoverPersistedJobsAsync(CancellationToken cancellationToken)
    {
        var recoverableJobs = new List<TransferJobRecord>();
        await foreach (var job in _store.FindRecoverableJobsAsync(cancellationToken))
            recoverableJobs.Add(job);

        var now = DateTimeOffset.UtcNow;
        foreach (var job in recoverableJobs)
        {
            _log.Info("recovery.job.detected", new { job.TransferId, job.Direction, job.State, job.UpdatedAt });
            if (TransferHistoryPolicy.IsResumeExpired(job.State, job.UpdatedAt, now))
            {
                // An old row that never reached a terminal state is not an active transfer.
                // Settle it so the user can clean it up instead of resuming it forever.
                await _store.UpsertJobAsync(
                    job with
                    {
                        State = TransferState.UserActionRequired,
                        UpdatedAt = now,
                        ErrorCode = TransferHistoryPolicy.ResumeExpiredErrorCode
                    },
                    cancellationToken);
                _log.Info("recovery.job.expired", new { job.TransferId, job.State, job.UpdatedAt });
                continue;
            }
            if (!TransferHistoryPolicy.IsResumable(job.State, job.UpdatedAt, now)) continue;
            if (job.Direction != TransferDirection.Send) continue;
            var files = await _store.FindFilesAsync(job.TransferId, cancellationToken);
            if (files.Count == 0) continue;
            StartOutgoing(job, files, job.DestinationDeviceId, cancellationToken);
        }
    }

    private async Task WaitForRetryOrDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timer = Task.Delay(delay, race.Token);
        var signal = _retrySignal.WaitAsync(race.Token);
        var winner = await Task.WhenAny(timer, signal);
        await winner;
        await race.CancelAsync();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void StartOutgoing(
        TransferJobRecord job,
        IReadOnlyList<TransferFileRecord> files,
        string destinationDeviceId,
        CancellationToken parentToken)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        if (!_activeTransfers.TryAdd(job.TransferId, cancellation))
        {
            cancellation.Dispose();
            _log.Warn("transfer.job.duplicate.skipped", new { job.TransferId, destinationDeviceId });
            return;
        }
        // Registered before the worker starts so the UI can show its place in line
        // immediately, even while an earlier job still owns the outgoing gate.
        EnterQueue(job.TransferId);
        RunningTransfersChanged?.Invoke(this, EventArgs.Empty);
        _ = RunAndCleanupAsync();

        async Task RunAndCleanupAsync()
        {
            try
            {
                await RunOutgoingWithRecoveryAsync(job, files, destinationDeviceId, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                await _log.ErrorAsync("transfer.job.unhandled", ex, new
                {
                    job.TransferId,
                    destinationDeviceId
                }).ConfigureAwait(false);
            }
            finally
            {
                _activeTransfers.TryRemove(job.TransferId, out _);
                LeaveQueue(job.TransferId);
                cancellation.Dispose();
                // Raised after the running set shrinks so a refresh triggered here sees the
                // job as finished rather than still owned by a worker.
                RunningTransfersChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void Publish(TransferUiState state)
    {
        _lastUiState = state;
        ProgressChanged?.Invoke(this, state);
    }

    private static long SafeTotalBytes(IEnumerable<TransferFileRecord> files)
    {
        long total = 0;
        foreach (var file in files)
            total = file.Size > long.MaxValue - total ? long.MaxValue : total + file.Size;
        return total;
    }

    public const string ManualDisconnectErrorCode = TransferHistoryPolicy.ManualDisconnectErrorCode;

    private static bool IsRecoverable(Exception exception) => exception is IOException or SocketException or TimeoutException or RemoteTransferException;
    private void SignalImmediateRetry() { if (_retrySignal.CurrentCount == 0) _retrySignal.Release(); }
    private void NetworkAddressChanged(object? sender, EventArgs e) => SignalImmediateRetry();
    private void DiscoveryChanged(object? sender, EventArgs e) => SignalImmediateRetry();

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
        _discovery.DeviceChanged -= DiscoveryChanged;
        _lifetime.Cancel();
        _listener?.Stop();
        _lifetime.Dispose();
        _power.Dispose();
        _retrySignal.Dispose();
        _outgoingGate.Dispose();
    }
}

public sealed record TransferUiState(
    string StatusText,
    string CurrentFile,
    long SafeBytes,
    long TotalBytes,
    double Percent,
    double BytesPerSecond,
    TimeSpan? EstimatedRemaining,
    bool CanPause,
    bool CanCancel,
    bool HasTransfer)
{
    public static TransferUiState NoTransfer(string status) =>
        new(status, string.Empty, 0, 0, 0, 0, null, false, false, false);

    public static TransferUiState Active(
        string status,
        long totalBytes,
        string currentFile = "전송 준비 중",
        bool canPause = false,
        bool canCancel = false) =>
        new(status, currentFile, 0, totalBytes, 0, 0, null, canPause, canCancel, true);
}

public sealed record PairingPrompt(Guid RequestId, string DeviceId, string DeviceName, string Fingerprint, string Code);
public sealed record QueueFilesResult(int QueuedFiles, int SkippedItems, IReadOnlyList<string> Errors);
