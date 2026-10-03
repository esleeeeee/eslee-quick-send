using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Eslee.QuickSend.Core.Devices;
using Eslee.QuickSend.Core.Integrity;
using Eslee.QuickSend.Core.Persistence;
using Eslee.QuickSend.Core.Protocol;
using Eslee.QuickSend.Core.Storage;
using Eslee.QuickSend.Core.Transfers;

var tests = new (string Name, Func<Task> Run)[]
{
    ("selected identity binding", SelectedIdentity),
    ("committed corruption retransmitted", CommittedCorruption),
    ("link publication rejected", LinkPublication),
    ("frame roundtrip", FrameRoundtrip),
    ("DNS-SD discovery contract", DiscoveryContract),
    ("chunk roundtrip and hash", ChunkRoundtrip),
    ("oversized frame rejected before allocation", OversizedFrameRejected),
    ("64-bit file metadata", LargeFileMetadata),
    ("state transitions and terminal protection", StateTransitions),
    ("checkpoint byte and time policy", CheckpointThresholds),
    ("retry backoff", RetryBackoff),
    ("Merkle snapshot resume", MerkleSnapshot),
    ("path traversal and collision protection", PathProtection),
    ("drag-drop file and folder expansion", DragDropSourceExpansion),
    ("completed partial chunk remains resumable", FinalPartialResume),
    ("durable disconnect resume roundtrip", DurableResumeRoundtrip),
    ("resume rewind resets session checkpoint boundary", ResumeProgressNeverRegresses),
    ("completed file replay preserves final file", CompletedReplayPreservesFile),
    ("checkpoint reconnect resumes without replay and consumes final checkpoint", FinalCheckpointBeforeAck),
    ("uncommitted bytes roll back after restart", UncommittedRollback),
    ("corrupt chunk is rejected before write", CorruptChunkRejected),
    ("device name normalization and length limit", DeviceNameNormalization),
    ("device name TXT encoding round-trips Hangul", DeviceNameWireRoundtrip),
    ("stuck rows are settled while live jobs stay protected", HistoryPolicySeparatesStuckFromRunning),
    ("manual disconnect blocks reconnect until re-enabled", ManualDisconnectBlocksReconnect),
    ("manual disconnect closes sessions without touching trust", ManualDisconnectClosesSessions),
    ("queued jobs are worded apart from other waiting states", QueueStatusWording),
    ("tray folder pipe name convention", Eslee.QuickSend.Core.Tests.TrayFolderLinkTests.PipeNameConvention),
    ("tray folder link register/menu/action roundtrip", Eslee.QuickSend.Core.Tests.TrayFolderLinkTests.RegisterMenuAndActionRoundtrip)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed");
return failures == 0 ? 0 : 1;

static Task SelectedIdentity()
{
    var trusted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ABC", "DEF" };
    True(PeerIdentity.CanAuthenticate("ABC", "ABC", false, trusted));
    False(PeerIdentity.CanAuthenticate("ABC", "DEF", false, trusted));
    True(PeerIdentity.CanAuthenticate("NEW", "NEW", true, trusted));
    False(PeerIdentity.CanAuthenticate("NEW", "DEF", true, trusted));
    False(PeerIdentity.CanAuthenticate("NEW", "NEW", false, trusted));
    PeerIdentity.ValidateSelected("A", "ABC", "A", "abc");
    Throws<IOException>(() => PeerIdentity.ValidateSelected("A", "ABC", "B", "DEF"));
    Throws<IOException>(() => PeerIdentity.ValidateSelected("A", "ABC", "B", "ABC"));
    False(PeerIdentity.MatchesFingerprint("", ""));
    return Task.CompletedTask;
}

static async Task CommittedCorruption()
{
    var directory = Path.Combine(Path.GetTempPath(), "esq-corruption-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var fileId = Guid.NewGuid();
        var data = Enumerable.Range(0, 24).Select(i => (byte)i).ToArray();
        var record = NewReceiverRecord(directory, fileId, data.Length, 8);
        var store = new MemoryTransferStore(record);
        await using (var receiver = new ReceiverSession(record, store, new CheckpointPolicy(8, TimeSpan.FromHours(1))))
        {
            await receiver.InitializeAsync();
            await receiver.ReceiveChunkAsync(await CreateChunkPayload(fileId, 0, data[..8]), DateTimeOffset.UtcNow);
            await receiver.ReceiveChunkAsync(await CreateChunkPayload(fileId, 8, data[8..16]), DateTimeOffset.UtcNow);
        }
        var bytes = File.ReadAllBytes(record.PartialPath!); bytes[9] ^= 1;
        File.WriteAllBytes(record.PartialPath!, bytes);
        await using var resumed = new ReceiverSession(store.File, store);
        store.FailNextCheckpoint = true;
        await ThrowsAsync<IOException>(() => resumed.InitializeAsync().AsTask());
        await resumed.InitializeAsync(); // failed initialization must not bypass verification/persistence
        Equal(8L, resumed.CommittedOffset); Equal(1, resumed.CreateResumeInfo().CommittedLeaves);
        Equal(8L, new FileInfo(record.PartialPath!).Length);
        await resumed.ReceiveChunkAsync(await CreateChunkPayload(fileId, 8, data[8..16]), DateTimeOffset.UtcNow);
        await resumed.ReceiveChunkAsync(await CreateChunkPayload(fileId, 16, data[16..]), DateTimeOffset.UtcNow);
        var merkle = new MerkleAccumulator();
        for (var offset = 0; offset < data.Length; offset += 8) merkle.AddChunk(data.AsSpan(offset, 8));
        var completed = await resumed.CompleteAsync(new FileCompleteMessage(fileId, data.Length, 3, Convert.ToBase64String(merkle.ComputeRoot())), DateTimeOffset.UtcNow);
        True(System.Security.Cryptography.SHA256.HashData(data).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(completed))));
    }
    finally { Directory.Delete(directory, true); }
}

static async Task LinkPublication()
{
    var directory = Path.Combine(Path.GetTempPath(), "esq-link-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var outside = Directory.CreateDirectory(Path.Combine(directory, "outside")).FullName;
    var receive = Directory.CreateDirectory(Path.Combine(directory, "receive")).FullName;
    var link = Path.Combine(receive, "linked");
    try
    {
        foreach (var unsafePath in new[] { "a:stream", "CON.txt", "COM\u00B9.txt", "LPT\u00B2", "a./file", "a /file", "../escape" })
            Throws<IOException>(() => SafePath.ResolveUnderRoot(receive, unsafePath));
        Equal(Path.Combine(receive, "nested", "file.txt"), SafePath.ResolveUnderRoot(receive, "nested/file.txt"));
        // Junction creation does not require Developer Mode or symbolic-link privilege.
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
        await process!.WaitForExitAsync(); Equal(0, process.ExitCode);
        Throws<IOException>(() => SafePath.ResolveUnderRoot(receive, "linked/escaped.txt"));
        var record = NewReceiverRecord(directory, Guid.NewGuid(), 0, 8) with { FinalPath = Path.Combine(link, "escaped.txt") };
        await using var receiver = new ReceiverSession(record, new MemoryTransferStore(record));
        await receiver.InitializeAsync();
        await ThrowsAsync<IOException>(() => receiver.CompleteAsync(new FileCompleteMessage(record.FileId, 0, 0, Convert.ToBase64String(new MerkleAccumulator().ComputeRoot())), DateTimeOffset.UtcNow).AsTask());
        False(File.Exists(Path.Combine(outside, "escaped.txt")));
    }
    finally { if (Directory.Exists(link)) Directory.Delete(link); Directory.Delete(directory, true); }
}

static Task DiscoveryContract()
{
    Equal("_eslee-quicksend._tcp", ProtocolConstants.ServiceType);
    Equal(41231, ProtocolConstants.DefaultPort);
    Equal((ushort)1, ProtocolConstants.Version);
    return Task.CompletedTask;
}

static Task DragDropSourceExpansion()
{
    var root = Path.Combine(Path.GetTempPath(), "quicksend-source-expansion-" + Guid.NewGuid().ToString("N"));
    var folder = Path.Combine(root, "Folder");
    var nested = Path.Combine(folder, "Nested");
    Directory.CreateDirectory(nested);
    var first = Path.Combine(folder, "first.txt");
    var second = Path.Combine(nested, "second.bin");
    File.WriteAllText(first, "first");
    File.WriteAllBytes(second, [1, 2, 3, 4]);
    try
    {
        var missing = Path.Combine(root, "missing.txt");
        var expansion = SourcePathExpander.Expand([folder, first, folder, missing]);

        Equal(2, expansion.Files.Count);
        Equal(1, expansion.Issues.Count);
        True(expansion.Files.Any(file => file.RelativePath == "Folder/first.txt" && file.Size == 5));
        True(expansion.Files.Any(file => file.RelativePath == "Folder/Nested/second.bin" && file.Size == 4));
        True(expansion.Issues[0].Reason.Contains("존재하지", StringComparison.Ordinal));
        return Task.CompletedTask;
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task FrameRoundtrip()
{
    await using var stream = new MemoryStream();
    await using (var writer = new ProtocolWriter(stream))
        await writer.WriteControlAsync(MessageType.Ping, new PingMessage(123456));
    stream.Position = 0;
    using var frame = await new ProtocolReader(stream).ReadAsync();
    Equal(MessageType.Ping, frame.Header.Type);
    Equal(123456L, ControlFrameCodec.Deserialize<PingMessage>(frame.Payload.Span).MonotonicTicks);
}

static async Task ChunkRoundtrip()
{
    var fileId = Guid.NewGuid();
    var data = Enumerable.Range(0, 130_001).Select(i => (byte)(i % 251)).ToArray();
    await using var stream = new MemoryStream();
    await using (var writer = new ProtocolWriter(stream))
        await writer.WriteChunkAsync(fileId, 5_000_000_000, data);
    stream.Position = 0;
    using var frame = await new ProtocolReader(stream).ReadAsync();
    var chunk = new ChunkPayload(frame.Payload.Span);
    Equal(fileId, chunk.FileId);
    Equal(5_000_000_000L, chunk.Offset);
    Equal(data.Length, chunk.Length);
    True(chunk.VerifyHash());
    True(data.AsSpan().SequenceEqual(chunk.Data));
}

static async Task OversizedFrameRejected()
{
    await using var stream = new MemoryStream();
    var raw = new byte[ProtocolConstants.HeaderSize];
    new FrameHeader(MessageType.Hello, FrameFlags.None, 1, ProtocolConstants.MaxControlPayload + 1).Write(raw);
    await stream.WriteAsync(raw);
    stream.Position = 0;
    await ThrowsAsync<ProtocolException>(() => new ProtocolReader(stream).ReadAsync().AsTask());
}

static Task LargeFileMetadata()
{
    const long size = 150L * 1024 * 1024 * 1024;
    var message = new FileStartMessage(Guid.NewGuid(), Guid.NewGuid(), "large.bin", size, 1, ProtocolConstants.DefaultChunkSize, null);
    var decoded = ControlFrameCodec.Deserialize<FileStartMessage>(ControlFrameCodec.Serialize(message));
    Equal(size, decoded.Size);
    True(decoded.Size > uint.MaxValue);
    return Task.CompletedTask;
}

static Task StateTransitions()
{
    var machine = new TransferStateMachine();
    machine.Transition(TransferState.Discovering);
    machine.Transition(TransferState.Connecting);
    machine.Transition(TransferState.Transferring);
    machine.Transition(TransferState.Recovering);
    machine.Transition(TransferState.Retrying);
    machine.Transition(TransferState.Transferring);
    machine.Transition(TransferState.Verifying);
    machine.Transition(TransferState.Completed);
    Throws<InvalidOperationException>(() => machine.Transition(TransferState.Transferring));
    return Task.CompletedTask;
}

static Task CheckpointThresholds()
{
    var start = DateTimeOffset.UtcNow;
    var policy = new CheckpointPolicy(64, TimeSpan.FromSeconds(1));
    policy.Restore(0, start);
    True(!policy.IsDue(63, start.AddMilliseconds(999)));
    True(policy.IsDue(64, start.AddMilliseconds(1)));
    policy.MarkCommitted(64, start.AddMilliseconds(1));
    True(policy.IsDue(65, start.AddSeconds(2)));
    return Task.CompletedTask;
}

static Task RetryBackoff()
{
    var retry = new RetryPolicy();
    var seconds = Enumerable.Range(0, 8).Select(i => retry.DelayForAttempt(i).TotalSeconds).ToArray();
    True(seconds.SequenceEqual([2, 5, 10, 20, 30, 60, 60, 60]));
    return Task.CompletedTask;
}

static Task MerkleSnapshot()
{
    var first = new MerkleAccumulator();
    first.AddChunk("first"u8);
    first.AddChunk("second"u8);
    var resumed = MerkleAccumulator.ImportLeaves(first.ExportLeaves());
    first.AddChunk("third"u8);
    resumed.AddChunk("third"u8);
    True(first.ComputeRoot().SequenceEqual(resumed.ComputeRoot()));
    return Task.CompletedTask;
}

static Task PathProtection()
{
    var root = Path.Combine(Path.GetTempPath(), "esq-root");
    var good = SafePath.ResolveUnderRoot(root, "folder/file.bin");
    True(good.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase));
    Throws<IOException>(() => SafePath.ResolveUnderRoot(root, "../escape.bin"));
    return Task.CompletedTask;
}

static async Task FinalPartialResume()
{
    var directory = Path.Combine(Path.GetTempPath(), "esq-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var partialPath = Path.Combine(directory, "partial.bin");
    var data = Enumerable.Range(0, 10).Select(static value => (byte)value).ToArray();
    await File.WriteAllBytesAsync(partialPath, data);
    var merkle = new MerkleAccumulator();
    merkle.AddChunk(data.AsSpan(0, 8));
    merkle.AddChunk(data.AsSpan(8, 2));
    var record = new TransferFileRecord(
        Guid.NewGuid(), Guid.NewGuid(), "partial.bin", string.Empty, partialPath,
        Path.Combine(directory, "final.bin"), data.Length, 0, null, 8,
        data.Length, data.Length, merkle.ExportLeaves(), TransferState.Transferring);
    var store = new MemoryTransferStore(record);
    try
    {
        await using var receiver = new ReceiverSession(record, store);
        await receiver.InitializeAsync();
        Equal((long)data.Length, receiver.CommittedOffset);
        Equal(2, receiver.CreateResumeInfo().CommittedLeaves);
        Equal((long)data.Length, new FileInfo(partialPath).Length);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static async Task DurableResumeRoundtrip()
{
    var directory = Path.Combine(Path.GetTempPath(), "esq-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var fileId = Guid.NewGuid();
    var data = Enumerable.Range(0, 20).Select(static value => (byte)(value * 7)).ToArray();
    var record = NewReceiverRecord(directory, fileId, data.Length, chunkSize: 8);
    var store = new MemoryTransferStore(record);
    try
    {
        await using (var first = new ReceiverSession(record, store, new CheckpointPolicy(8, TimeSpan.FromHours(1))))
        {
            await first.InitializeAsync();
            var result = await first.ReceiveChunkAsync(await CreateChunkPayload(fileId, 0, data[..8]), DateTimeOffset.UtcNow);
            Equal(8L, result.Checkpoint!.CommittedOffset);
        }

        await using (var resumed = new ReceiverSession(store.File, store, new CheckpointPolicy(8, TimeSpan.FromHours(1))))
        {
            await resumed.InitializeAsync();
            Equal(8L, resumed.CreateResumeInfo().CommittedOffset);
            await resumed.ReceiveChunkAsync(await CreateChunkPayload(fileId, 8, data[8..16]), DateTimeOffset.UtcNow);
            await resumed.ReceiveChunkAsync(await CreateChunkPayload(fileId, 16, data[16..]), DateTimeOffset.UtcNow);
            var merkle = new MerkleAccumulator();
            merkle.AddChunk(data[..8]);
            merkle.AddChunk(data[8..16]);
            merkle.AddChunk(data[16..]);
            var path = await resumed.CompleteAsync(
                new FileCompleteMessage(fileId, data.Length, merkle.LeafCount, Convert.ToBase64String(merkle.ComputeRoot())),
                DateTimeOffset.UtcNow);
            var completedData = await File.ReadAllBytesAsync(path);
            True(data.AsSpan().SequenceEqual(completedData));
        }
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static Task ResumeProgressNeverRegresses()
{
    var fileId = Guid.NewGuid();
    var ledger = new ResumeProgressLedger();
    Equal(64L, ledger.ObserveResume(fileId, 64));
    Equal(128L, ledger.ObserveCheckpoint(fileId, 128));
    Equal(1_128L, ResumeProgressLedger.OverallCommitted(1_000, ledger.GetCommittedOffset(fileId)));
    Equal(64L, ledger.ObserveResume(fileId, 64));
    Equal(64L, ledger.GetCommittedOffset(fileId));
    Throws<ProtocolException>(() => ledger.ObserveCheckpoint(fileId, 0));
    Equal(128L, ledger.ObserveCheckpoint(fileId, 128));
    return Task.CompletedTask;
}

static async Task CompletedReplayPreservesFile()
{
    var directory = Path.Combine(Path.GetTempPath(), "esq-completed-replay-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var finalPath = Path.Combine(directory, "finished.bin");
    var data = Enumerable.Range(0, 10).Select(static value => (byte)value).ToArray();
    await File.WriteAllBytesAsync(finalPath, data);
    var merkle = new MerkleAccumulator();
    merkle.AddChunk(data.AsSpan(0, 8));
    merkle.AddChunk(data.AsSpan(8));
    var record = new TransferFileRecord(
        Guid.NewGuid(), Guid.NewGuid(), "finished.bin", "source.bin", null, finalPath,
        data.Length, 0, null, 8, data.Length, data.Length, merkle.ExportLeaves(),
        TransferState.Completed);
    try
    {
        True(CompletedFileReplay.CanReplay(record, File.Exists(finalPath)));
        var resume = CompletedFileReplay.CreateResume(record);
        Equal((long)data.Length, resume.CommittedOffset);
        Equal(2, resume.CommittedLeaves);
        CompletedFileReplay.VerifyCompletion(
            record,
            new FileCompleteMessage(
                record.FileId,
                record.Size,
                merkle.LeafCount,
                Convert.ToBase64String(merkle.ComputeRoot())));
        True(File.Exists(finalPath));
        var persisted = await File.ReadAllBytesAsync(finalPath);
        True(data.AsSpan().SequenceEqual(persisted));
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static async Task FinalCheckpointBeforeAck()
{
    var directory = Path.Combine(Path.GetTempPath(), "esq-frame-order-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var sourcePath = Path.Combine(directory, "source.bin");
    var data = Enumerable.Range(0, 20).Select(static value => (byte)(value * 3)).ToArray();
    await File.WriteAllBytesAsync(sourcePath, data);
    var info = new FileInfo(sourcePath);
    var transferId = Guid.NewGuid();
    var fileId = Guid.NewGuid();
    var start = new FileStartMessage(
        transferId, fileId, "source.bin", info.Length, info.LastWriteTimeUtc.Ticks, 8, null);
    var resumedMerkle = new MerkleAccumulator();
    resumedMerkle.AddChunk(data.AsSpan(0, 8));
    var resume = new ResumeInfoMessage(
        transferId,
        fileId,
        8,
        resumedMerkle.LeafCount,
        Convert.ToBase64String(resumedMerkle.ExportLeaves()));
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    try
    {
        var receiverTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            await using var stream = socket.GetStream();
            var reader = new ProtocolReader(stream);
            await using var writer = new ProtocolWriter(stream);
            var received = 8L;
            var merkle = new MerkleAccumulator();
            merkle.AddChunk(data.AsSpan(0, 8));
            while (received < data.Length)
            {
                long chunkOffset;
                int chunkLength;
                {
                    using var frame = await reader.ReadAsync();
                    Equal(MessageType.ChunkData, frame.Header.Type);
                    var chunk = new ChunkPayload(frame.Payload.Span);
                    Equal(received, chunk.Offset);
                    True(chunk.VerifyHash());
                    merkle.AddChunk(chunk.Data);
                    chunkOffset = chunk.Offset;
                    chunkLength = chunk.Length;
                    received += chunk.Length;
                }
                await writer.WriteControlAsync(
                    MessageType.Checkpoint,
                    new CheckpointMessage(
                        fileId,
                        received,
                        merkle.LeafCount,
                        Convert.ToBase64String(merkle.ExportLeaves())),
                    FrameFlags.Response);
                await writer.WriteControlAsync(
                    MessageType.ChunkAck,
                    new ChunkAckMessage(fileId, chunkOffset, chunkLength, received),
                    FrameFlags.Response);
            }

            using var completeFrame = await reader.ReadAsync();
            Equal(MessageType.FileComplete, completeFrame.Header.Type);
            var complete = ControlFrameCodec.Deserialize<FileCompleteMessage>(completeFrame.Payload.Span);
            await writer.WriteControlAsync(
                MessageType.FileVerify,
                new FileVerifyMessage(complete.FileId, true),
                FrameFlags.Response | FrameFlags.Final);
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        await using var stream = client.GetStream();
        var reader = new ProtocolReader(stream);
        await using var writer = new ProtocolWriter(stream);
        var sender = new SlidingWindowSender(reader, writer, chunkSize: 8, windowChunks: 2);
        var checkpoints = new List<long>();
        sender.CheckpointReceived += checkpoint => checkpoints.Add(checkpoint.CommittedOffset);
        await sender.SendFileAsync(
            info,
            start,
            resume,
            new SourceFingerprint(info.Length, info.LastWriteTimeUtc.Ticks, null));

        using var verifyFrame = await reader.ReadAsync();
        Equal(MessageType.FileVerify, verifyFrame.Header.Type);
        Equal((long)data.Length, checkpoints[^1]);
        await receiverTask;
    }
    finally
    {
        listener.Stop();
        Directory.Delete(directory, recursive: true);
    }
}

static async Task UncommittedRollback()
{
    var directory = Path.Combine(Path.GetTempPath(), "esq-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var fileId = Guid.NewGuid();
    var record = NewReceiverRecord(directory, fileId, size: 16, chunkSize: 8);
    var store = new MemoryTransferStore(record);
    try
    {
        await using (var first = new ReceiverSession(record, store, new CheckpointPolicy(64, TimeSpan.FromHours(1))))
        {
            await first.InitializeAsync();
            var result = await first.ReceiveChunkAsync(await CreateChunkPayload(fileId, 0, new byte[8]), DateTimeOffset.UtcNow);
            True(result.Checkpoint is null);
            Equal(0L, first.CommittedOffset);
        }
        await using (var restarted = new ReceiverSession(store.File, store))
        {
            await restarted.InitializeAsync();
            Equal(0L, restarted.CommittedOffset);
            Equal(0L, new FileInfo(record.PartialPath!).Length);
        }
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static async Task CorruptChunkRejected()
{
    var directory = Path.Combine(Path.GetTempPath(), "esq-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var fileId = Guid.NewGuid();
    var record = NewReceiverRecord(directory, fileId, size: 8, chunkSize: 8);
    var store = new MemoryTransferStore(record);
    try
    {
        await using var receiver = new ReceiverSession(record, store);
        await receiver.InitializeAsync();
        var payload = await CreateChunkPayload(fileId, 0, new byte[8]);
        payload[^1] ^= 0xFF;
        await ThrowsAsync<ChunkIntegrityException>(() => receiver.ReceiveChunkAsync(payload, DateTimeOffset.UtcNow).AsTask());
        Equal(0L, receiver.CommittedOffset);
        Equal(0L, new FileInfo(record.PartialPath!).Length);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static TransferFileRecord NewReceiverRecord(string directory, Guid fileId, long size, int chunkSize) => new(
    Guid.NewGuid(), fileId, "payload.bin", string.Empty, Path.Combine(directory, "payload.part"),
    Path.Combine(directory, "payload.bin"), size, 0, null, chunkSize, 0, 0, [], TransferState.Transferring);

static async Task<byte[]> CreateChunkPayload(Guid fileId, long offset, byte[] data)
{
    await using var stream = new MemoryStream();
    await using (var writer = new ProtocolWriter(stream))
        await writer.WriteChunkAsync(fileId, offset, data);
    stream.Position = 0;
    using var frame = await new ProtocolReader(stream).ReadAsync();
    return frame.Payload.ToArray();
}

static void True(bool condition)
{
    if (!condition) throw new InvalidOperationException("Assertion failed.");
}

static void False(bool condition)
{
    if (condition) throw new InvalidOperationException("Assertion failed.");
}

static void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

static Task DeviceNameNormalization()
{
    True(DeviceNameRules.TryNormalize("  이슬이   노트북 ", out var normalized, out _));
    Equal("이슬이 노트북", normalized);

    // Control characters are dropped; tabs and newlines collapse into a single space.
    True(DeviceNameRules.TryNormalize("Desk\u0000top\tPC", out var stripped, out _));
    Equal("Desktop PC", stripped);
    True(DeviceNameRules.TryNormalize("Living\r\nRoom", out var multiline, out _));
    Equal("Living Room", multiline);

    False(DeviceNameRules.TryNormalize("   ", out _, out var blankError));
    True(blankError is not null);
    False(DeviceNameRules.TryNormalize("\t\r\n", out _, out _));
    False(DeviceNameRules.TryNormalize(null, out _, out _));
    False(DeviceNameRules.TryNormalize(new string('가', DeviceNameRules.MaxLength + 1), out _, out var longError));
    True(longError is not null);
    True(DeviceNameRules.TryNormalize(new string('가', DeviceNameRules.MaxLength), out _, out _));

    // An unusable stored value falls back to the platform name without throwing.
    Equal("MY-PC", DeviceNameRules.NormalizeOrFallback("   ", "MY-PC"));
    Equal("사무실 PC", DeviceNameRules.NormalizeOrFallback("사무실 PC", "MY-PC"));
    return Task.CompletedTask;
}

static Task DeviceNameWireRoundtrip()
{
    foreach (var name in new[] { "MY-PC", "이슬이 노트북", "Galaxy S24 (거실)", "100% 노트북" })
    {
        var encoded = DeviceNameWire.Encode(name);
        True(encoded.All(static character => character is >= ' ' and <= '~'));
        Equal(name, DeviceNameWire.Decode(encoded));
    }

    // Plain ASCII must stay byte-identical so the TXT value keeps its original meaning.
    Equal("MY-PC", DeviceNameWire.Encode("MY-PC"));
    Equal(string.Empty, DeviceNameWire.Decode(null));
    return Task.CompletedTask;
}

static Task HistoryPolicySeparatesStuckFromRunning()
{
    var now = DateTimeOffset.UtcNow;
    var fresh = now.AddMinutes(-5);
    var stale = now - TransferHistoryPolicy.ResumeWindow - TimeSpan.FromDays(1);

    True(TransferHistoryPolicy.IsResumable(TransferState.WaitingDevice, fresh, now));
    False(TransferHistoryPolicy.IsResumeExpired(TransferState.WaitingDevice, fresh, now));
    False(TransferHistoryPolicy.IsResumable(TransferState.WaitingDevice, stale, now));
    True(TransferHistoryPolicy.IsResumeExpired(TransferState.WaitingDevice, stale, now));

    // Settled jobs are never resumed and never counted as active work.
    foreach (var settled in new[] { TransferState.Completed, TransferState.Cancelled, TransferState.FailedFatal, TransferState.UserActionRequired })
    {
        True(TransferHistoryPolicy.IsSettled(settled));
        False(TransferHistoryPolicy.IsResumable(settled, fresh, now));
        False(TransferHistoryPolicy.IsResumeExpired(settled, fresh, now));
        True(TransferHistoryPolicy.CanDeleteHistory(settled, isRunning: false));
    }

    False(TransferHistoryPolicy.IsSettled(TransferState.Transferring));
    False(TransferHistoryPolicy.CanDeleteHistory(TransferState.Transferring, isRunning: true));
    True(TransferHistoryPolicy.CanDeleteHistory(TransferState.WaitingDevice, isRunning: false));
    return Task.CompletedTask;
}

static Task ManualDisconnectBlocksReconnect()
{
    var registry = new PeerConnectionRegistry();
    const string peer = "peer-device-id";

    using (var link = registry.TryRegisterLink(peer, static () => { }))
    {
        True(link is not null);
        Equal(PeerLinkState.Connected, registry.GetState(peer));
    }
    Equal(PeerLinkState.Idle, registry.GetState(peer));

    registry.RequestManualDisconnect(peer);
    True(registry.IsManuallyDisconnected(peer));
    Equal(PeerLinkState.ManuallyDisconnected, registry.GetState(peer));
    // Automatic reconnection must not slip back in behind the user's decision.
    True(registry.TryRegisterLink(peer, static () => { }) is null);
    True(registry.TryRegisterLink("other-device", static () => { }) is not null);

    True(registry.AllowReconnect(peer));
    False(registry.IsManuallyDisconnected(peer));
    using var resumed = registry.TryRegisterLink(peer, static () => { });
    True(resumed is not null);
    Equal(PeerLinkState.Connected, registry.GetState(peer));
    return Task.CompletedTask;
}

static Task ManualDisconnectClosesSessions()
{
    var registry = new PeerConnectionRegistry();
    const string peer = "peer-device-id";
    var closedFirst = 0;
    var closedOther = 0;
    var first = registry.TryRegisterLink(peer, () => closedFirst++);
    var second = registry.TryRegisterLink(peer, () => closedFirst++);
    var other = registry.TryRegisterLink("other-device", () => closedOther++);
    True(first is not null && second is not null && other is not null);

    var closers = registry.RequestManualDisconnect(peer);
    foreach (var close in closers) close();
    Equal(2, closedFirst);
    Equal(0, closedOther);
    Equal(PeerLinkState.Connected, registry.GetState("other-device"));

    first!.Dispose();
    second!.Dispose();
    // The block survives the sessions ending; only an explicit reconnect clears it.
    Equal(PeerLinkState.ManuallyDisconnected, registry.GetState(peer));
    other!.Dispose();
    return Task.CompletedTask;
}

static Task QueueStatusWording()
{
    // Only a job genuinely waiting behind another job is called 대기열, and its place is shown.
    Equal("대기열 1번째", TransferStatusText.ForJob(TransferState.Queued, null, queuePosition: 1));
    Equal("대기열 2번째", TransferStatusText.ForJob(TransferState.Queued, null, queuePosition: 2));

    // A queued job whose peer is unreachable is blocked by the device, not by its turn.
    Equal(TransferStatusText.WaitingDevice,
        TransferStatusText.ForJob(TransferState.Queued, null, queuePosition: 1, destinationOnline: false));

    // The other waiting-like states stay distinct from the queue.
    Equal(TransferStatusText.WaitingDevice, TransferStatusText.ForJob(TransferState.WaitingDevice, null));
    Equal(TransferStatusText.Recovering, TransferStatusText.ForJob(TransferState.Recovering, null));
    Equal(TransferStatusText.Recovering, TransferStatusText.ForJob(TransferState.Retrying, null));
    Equal(TransferStatusText.Paused, TransferStatusText.ForJob(TransferState.Paused, null));
    Equal(TransferStatusText.ManuallyDisconnected,
        TransferStatusText.ForJob(TransferState.Paused, TransferHistoryPolicy.ManualDisconnectErrorCode));
    Equal(TransferStatusText.Transferring, TransferStatusText.ForJob(TransferState.Transferring, null));
    Equal(TransferStatusText.Completed, TransferStatusText.ForJob(TransferState.Completed, null));
    Equal(TransferStatusText.Cancelled, TransferStatusText.ForJob(TransferState.Cancelled, null));
    Equal(TransferStatusText.Failed, TransferStatusText.ForJob(TransferState.FailedFatal, null));
    Equal(TransferStatusText.ResumeExpired,
        TransferStatusText.ForJob(TransferState.UserActionRequired, TransferHistoryPolicy.ResumeExpiredErrorCode));

    // A job that owns its turn is never labelled as queued, whatever its state says.
    False(TransferStatusText.ForJob(TransferState.Transferring, null).StartsWith(TransferStatusText.QueuedPrefix, StringComparison.Ordinal));
    False(TransferStatusText.ForJob(TransferState.WaitingDevice, null).StartsWith(TransferStatusText.QueuedPrefix, StringComparison.Ordinal));
    False(TransferStatusText.ForJob(TransferState.Recovering, null).StartsWith(TransferStatusText.QueuedPrefix, StringComparison.Ordinal));
    return Task.CompletedTask;
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

sealed class MemoryTransferStore(TransferFileRecord file) : ITransferStore
{
    private TransferFileRecord _file = file;
    public TransferFileRecord File => _file;
    public bool FailNextCheckpoint { get; set; }

    public ValueTask UpsertJobAsync(TransferJobRecord job, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask UpsertFileAsync(TransferFileRecord value, CancellationToken cancellationToken)
    {
        _file = value;
        return ValueTask.CompletedTask;
    }
    public ValueTask SaveCheckpointAsync(Guid fileId, long committedOffset, byte[] merkleLeaves, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (FailNextCheckpoint) { FailNextCheckpoint = false; throw new IOException("injected checkpoint failure"); }
        _file = _file with { CommittedOffset = committedOffset, MerkleLeaves = merkleLeaves };
        return ValueTask.CompletedTask;
    }
    public ValueTask RestoreCheckpointAsync(Guid fileId, long expectedCommittedOffset, long committedOffset, byte[] merkleLeaves, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (_file.CommittedOffset != expectedCommittedOffset) throw new IOException("Stale recovery checkpoint");
        return SaveCheckpointAsync(fileId, committedOffset, merkleLeaves, at, cancellationToken);
    }
    public ValueTask MarkFileCompletedAsync(Guid fileId, string finalPath, byte[] merkleRoot, DateTimeOffset at, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask<TransferFileRecord?> FindFileAsync(Guid fileId, CancellationToken cancellationToken) => ValueTask.FromResult<TransferFileRecord?>(_file);
    public async IAsyncEnumerable<TransferJobRecord> FindRecoverableJobsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }
}
