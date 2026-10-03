using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Eslee.QuickSend.Core.Devices;
using Eslee.QuickSend.Core.Integrity;
using Eslee.QuickSend.Core.Persistence;
using Eslee.QuickSend.Core.Protocol;
using Eslee.QuickSend.Core.Transfers;
using Eslee.QuickSend.Windows.Diagnostics;
using Eslee.QuickSend.Windows.Persistence;
using Eslee.QuickSend.Windows.Security;
using Microsoft.Data.Sqlite;

var bytes = args.Length == 0 ? 64L * 1024 * 1024 : long.Parse(args[0]);
if (bytes < 3L * ProtocolConstants.DefaultChunkSize || bytes > 100L * 1024 * 1024 * 1024)
    throw new ArgumentOutOfRangeException(nameof(args), "Use 24 MiB through 100 GiB.");
var root = Path.Combine(Path.GetTempPath(), "quicksend-network-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var free = new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace;
if (free < checked(bytes * 2 + 4L * 1024 * 1024 * 1024)) throw new IOException("Insufficient free space for bounded I/O check.");
var stopwatch = Stopwatch.StartNew();
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
var token = deadline.Token;
var observations = new Dictionary<string, object> { ["bytes"] = bytes, ["freeBytesBefore"] = free, ["testRoot"] = root };
try
{
    using var sender = await Node.CreateAsync(Path.Combine(root, "sender"));
    using var intended = await Node.CreateAsync(Path.Combine(root, "intended"));
    using var otherTrusted = await Node.CreateAsync(Path.Combine(root, "other"));
    await PairAsync(sender, intended, token);
    await PairAsync(sender, otherTrusted, token);
    Console.WriteLine("PASS new-pairing TLS + matching SAS protocol exchange, isolated SQLite trust");
    var rejected = false;
    var receivedApplicationBytes = 0;
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var rejectServer = Task.Run(async () =>
    {
        using var socket = await listener.AcceptTcpClientAsync(token);
        try
        {
            await using var ssl = await otherTrusted.Tls.AcceptAsync(socket, false, token);
            var data = new byte[1]; receivedApplicationBytes = await ssl.ReadAsync(data, token);
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException) { }
    }, token);
    try
    {
        try { await using var wrong = await sender.Tls.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, false, intended.Identity.Fingerprint, token); }
        catch (TlsHandshakeException) { rejected = true; }
        await rejectServer;
    }
    finally { listener.Stop(); }
    Require(rejected && receivedApplicationBytes == 0, "Trusted B redirected from selected A must fail before application bytes.");
    observations["redirectedTrustedPeerRejectedBeforeManifest"] = true;
    Console.WriteLine("PASS selected trusted A endpoint redirected to trusted B: TLS rejected; no manifest bytes");

    var source = Path.Combine(root, "source.bin");
    var buffer = new byte[ProtocolConstants.DefaultChunkSize]; new Random(741).NextBytes(buffer);
    using var sourceDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    await using (var output = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
    {
        for (long offset = 0; offset < bytes; offset += buffer.Length)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buffer, offset);
            var count = (int)Math.Min(buffer.Length, bytes - offset);
            sourceDigest.AppendData(buffer.AsSpan(0, count));
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            if (offset % (1024L * 1024 * 1024) == 0) Console.WriteLine($"source written {offset}/{bytes}");
        }
        output.Flush(true);
    }
    var expectedHash = Convert.ToHexString(sourceDigest.GetHashAndReset());
    var info = new FileInfo(source);
    var transferId = Guid.NewGuid(); var fileId = Guid.NewGuid();
    var record = new TransferFileRecord(transferId, fileId, "received.bin", source,
        Path.Combine(intended.Directory, "received.part"), Path.Combine(intended.Directory, "received.bin"),
        bytes, info.LastWriteTimeUtc.Ticks, null, buffer.Length, 0, 0, [], TransferState.Transferring);
    var store = new SqliteTransferStore(intended.Database);
    await store.UpsertJobAsync(new(transferId, sender.Identity.DeviceId, intended.Identity.DeviceId, TransferDirection.Receive,
        TransferState.Transferring, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), token);
    await store.UpsertFileAsync(record, token);
    var ledger = new ResumeProgressLedger();
    var firstOffset = await TransferConnectionAsync(sender, intended, source, record, store, ledger, interrupt: true, token);
    Require(firstOffset == 2L * buffer.Length, "Two chunks must be durable before disconnect.");
    Console.WriteLine($"PASS TCP disconnect after SQLite checkpoint {firstOffset}");
    // Same-length disk corruption after closing all receiver streams; reopen the DB/session next.
    using (var partial = new FileStream(record.PartialPath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        partial.Position = buffer.Length + 17;
        var original = partial.ReadByte(); partial.Position--; partial.WriteByte((byte)(original ^ 1)); partial.Flush(true);
    }
    using var restarted = await Node.CreateAsync(intended.Directory);
    var reopenedStore = new SqliteTransferStore(restarted.Database);
    var reopened = await reopenedStore.FindFileAsync(fileId, token) ?? throw new IOException("Checkpoint missing after reopen.");
    var finalOffset = await TransferConnectionAsync(sender, restarted, source, reopened, reopenedStore, ledger, interrupt: false, token);
    Require(finalOffset == bytes, "Transfer must complete.");
    await using var finalFile = File.OpenRead(record.FinalPath!);
    var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(finalFile, token));
    Require(actualHash == expectedHash, "Published on-disk SHA256 must match source.");
    observations["sourceSha256"] = expectedHash; observations["destinationSha256"] = actualHash;
    observations["durableBeforeDisconnect"] = firstOffset; observations["resumeAfterCorruption"] = buffer.Length;
    observations["elapsedSeconds"] = stopwatch.Elapsed.TotalSeconds;
    observations["peakWorkingSetBytes"] = Process.GetCurrentProcess().PeakWorkingSet64;
    Console.WriteLine("PASS reopened identity + SQLite, repaired resume, TLS streaming and final disk SHA256");
    Console.WriteLine(JsonSerializer.Serialize(observations));
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(root, true);
    Console.WriteLine("CLEANUP isolated source, receiver, identities and databases removed");
}

static async Task PairAsync(Node client, Node server, CancellationToken token)
{
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    var clientCode = DeviceIdentityService.PairingCode(client.Identity.Fingerprint, server.Identity.Fingerprint, nonce);
    var serving = Task.Run(async () =>
    {
        using var socket = await listener.AcceptTcpClientAsync(token);
        await using var ssl = await server.Tls.AcceptAsync(socket, true, token);
        var reader = new ProtocolReader(ssl); await using var writer = new ProtocolWriter(ssl);
        var hello = await ReadAsync<HelloMessage>(reader, MessageType.Hello, token);
        Require(hello.IdentityFingerprint == client.Identity.Fingerprint, "Client HELLO");
        await writer.WriteControlAsync(MessageType.Hello, Hello(server), cancellationToken: token);
        var request = await ReadAsync<PairRequestMessage>(reader, MessageType.PairRequest, token);
        Require(DeviceIdentityService.PairingCode(server.Identity.Fingerprint, hello.IdentityFingerprint, request.Nonce) == clientCode, "SAS mismatch");
        await server.Trust.TrustAsync(hello.DeviceId, hello.DeviceName, hello.IdentityFingerprint, token);
        await writer.WriteControlAsync(MessageType.PairAccept, new PairAcceptMessage(server.Identity.DeviceId, request.Nonce, true), cancellationToken: token);
    }, token);
    try
    {
        await using var ssl = await client.Tls.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, true, server.Identity.Fingerprint, token);
        var reader = new ProtocolReader(ssl); await using var writer = new ProtocolWriter(ssl);
        await writer.WriteControlAsync(MessageType.Hello, Hello(client), cancellationToken: token);
        var hello = await ReadAsync<HelloMessage>(reader, MessageType.Hello, token);
        PeerIdentity.ValidateSelected(server.Identity.DeviceId, server.Identity.Fingerprint, hello.DeviceId, hello.IdentityFingerprint);
        await writer.WriteControlAsync(MessageType.PairRequest, new PairRequestMessage(client.Identity.DeviceId, "QA", nonce, client.Identity.Fingerprint), cancellationToken: token);
        var accepted = await ReadAsync<PairAcceptMessage>(reader, MessageType.PairAccept, token);
        Require(accepted.Accepted && accepted.Nonce == nonce, "Pairing accept");
        await client.Trust.TrustAsync(hello.DeviceId, hello.DeviceName, hello.IdentityFingerprint, token);
        await serving;
    }
    catch
    {
        try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { Console.Error.WriteLine("Pair server: " + ex); }
        throw;
    }
    finally { listener.Stop(); }
}

static async Task<long> TransferConnectionAsync(Node client, Node server, string source, TransferFileRecord record,
    SqliteTransferStore store, ResumeProgressLedger ledger, bool interrupt, CancellationToken token)
{
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var receiving = Task.Run(async () =>
    {
        using var socket = await listener.AcceptTcpClientAsync(token);
        await using var ssl = await server.Tls.AcceptAsync(socket, false, token);
        var reader = new ProtocolReader(ssl); await using var writer = new ProtocolWriter(ssl);
        await using var receiver = new ReceiverSession(record, store, new CheckpointPolicy(record.ChunkSize, TimeSpan.FromHours(1)));
        await receiver.InitializeAsync(token);
        await writer.WriteControlAsync(MessageType.ResumeInfo, receiver.CreateResumeInfo(), cancellationToken: token);
        while (true)
        {
            using var frame = await reader.ReadAsync(token);
            if (frame.Header.Type == MessageType.Ping)
            {
                await writer.WriteControlAsync(MessageType.Pong, ControlFrameCodec.Deserialize<PingMessage>(frame.Payload.Span), cancellationToken: token); continue;
            }
            if (frame.Header.Type == MessageType.FileComplete)
            {
                await receiver.CompleteAsync(ControlFrameCodec.Deserialize<FileCompleteMessage>(frame.Payload.Span), DateTimeOffset.UtcNow, token);
                await writer.WriteControlAsync(MessageType.FileVerify, new FileVerifyMessage(record.FileId, true), cancellationToken: token);
                return receiver.CommittedOffset;
            }
            Require(frame.Header.Type == MessageType.ChunkData, "Expected chunk");
            var result = await receiver.ReceiveChunkAsync(frame.Payload, DateTimeOffset.UtcNow, token);
            if (result.Checkpoint is not null) await writer.WriteControlAsync(MessageType.Checkpoint, result.Checkpoint, cancellationToken: token);
            await writer.WriteControlAsync(MessageType.ChunkAck, result.Ack, cancellationToken: token);
            if (interrupt && receiver.CommittedOffset == 2L * record.ChunkSize) return receiver.CommittedOffset;
        }
    }, token);
    try
    {
        await using var ssl = await client.Tls.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, false, server.Identity.Fingerprint, token);
        var reader = new ProtocolReader(ssl); await using var writer = new ProtocolWriter(ssl);
        var resume = await ReadAsync<ResumeInfoMessage>(reader, MessageType.ResumeInfo, token);
        ledger.ObserveResume(record.FileId, resume.CommittedOffset);
        Require(resume.CommittedOffset == (interrupt ? 0 : record.ChunkSize), "Expected durable/repaired resume offset.");
        if (interrupt)
        {
            await using var input = File.OpenRead(source); var chunk = new byte[record.ChunkSize];
            for (var index = 0; index < 2; index++)
            {
                await input.ReadExactlyAsync(chunk, token);
                await writer.WriteChunkAsync(record.FileId, (long)index * chunk.Length, chunk, cancellationToken: token);
                var checkpoint = await ReadAsync<CheckpointMessage>(reader, MessageType.Checkpoint, token);
                ledger.ObserveCheckpoint(record.FileId, checkpoint.CommittedOffset);
                await ReadAsync<ChunkAckMessage>(reader, MessageType.ChunkAck, token);
            }
        }
        else
        {
            var sender = new SlidingWindowSender(reader, writer, record.ChunkSize, 4);
            long lastReport = 0;
            sender.CheckpointReceived += cp =>
            {
                ledger.ObserveCheckpoint(record.FileId, cp.CommittedOffset);
                if (cp.CommittedOffset - lastReport >= 1024L * 1024 * 1024)
                { lastReport = cp.CommittedOffset; Console.WriteLine($"TLS committed {lastReport}/{record.Size}"); }
            };
            var info = new FileInfo(source);
            await sender.SendFileAsync(info, new(record.TransferId, record.FileId, record.RelativePath, record.Size, record.ModifiedUtcTicks, record.ChunkSize, null),
                resume, new SourceFingerprint(info.Length, info.LastWriteTimeUtc.Ticks, null), token);
            Require((await ReadAsync<FileVerifyMessage>(reader, MessageType.FileVerify, token)).Verified, "Final verify");
        }
        return await receiving;
    }
    catch
    {
        try { await receiving.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { Console.Error.WriteLine("Transfer server: " + ex); }
        throw;
    }
    finally { listener.Stop(); }
}

static HelloMessage Hello(Node node) => new(1, "qa", node.Identity.DeviceId, "QA", "windows", node.Identity.Fingerprint, ["resume-v1"]);
static async Task<T> ReadAsync<T>(ProtocolReader reader, MessageType type, CancellationToken token) where T : notnull
{
    using var frame = await reader.ReadAsync(token); Require(frame.Header.Type == type, $"Expected {type}, got {frame.Header.Type}");
    return ControlFrameCodec.Deserialize<T>(frame.Payload.Span);
}
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

sealed class Node : IDisposable
{
    public required string Directory { get; init; }
    public required AppDatabase Database { get; init; }
    public required DeviceIdentity Identity { get; init; }
    public required TrustedDeviceStore Trust { get; init; }
    public required TlsChannelFactory Tls { get; init; }
    public static async Task<Node> CreateAsync(string directory)
    {
        System.IO.Directory.CreateDirectory(directory);
        var database = new AppDatabase(Path.Combine(directory, "qa.db")); await database.InitializeAsync();
        var idPath = Path.Combine(directory, "id.txt");
        var id = File.Exists(idPath) ? File.ReadAllText(idPath) : Guid.NewGuid().ToString("N"); File.WriteAllText(idPath, id);
        var keyPath = Path.Combine(directory, "ephemeral-qa.pfx");
        if (!File.Exists(keyPath))
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=QuickSend isolated QA", key, HashAlgorithmName.SHA256);
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllBytes(keyPath, generated.Export(X509ContentType.Pfx));
        }
        // Schannel requires an imported key container. No PersistKeySet and no X509Store:
        // disposing this generated QA certificate removes its temporary key container.
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(keyPath, null, X509KeyStorageFlags.DefaultKeySet);
        var identity = new DeviceIdentity(id, certificate, DeviceIdentityService.Fingerprint(certificate));
        var trust = new TrustedDeviceStore(database);
        return new() { Directory = directory, Database = database, Identity = identity, Trust = trust,
            Tls = new(trust, new(database, identity), new DiagnosticLog(Path.Combine(directory, "logs"))) };
    }
    public void Dispose() => Identity.Certificate.Dispose();
}
