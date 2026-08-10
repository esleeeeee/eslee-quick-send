using Eslee.QuickSend.Core.Persistence;
using Eslee.QuickSend.Core.Transfers;
using Eslee.QuickSend.Windows.Persistence;
using Eslee.QuickSend.Windows.Updates;
using Microsoft.Data.Sqlite;

SQLitePCL.Batteries_V2.Init();
var root = Path.Combine(Path.GetTempPath(), "quicksend-history-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var databasePath = Path.Combine(root, "history.db");
var sourcePath = Path.Combine(root, "source.bin");
var finalPath = Path.Combine(root, "received.bin");
await File.WriteAllBytesAsync(sourcePath, [1, 2, 3]);
await File.WriteAllBytesAsync(finalPath, [4, 5, 6]);
IReadOnlySet<Guid> NoneRunning = new HashSet<Guid>();

try
{
    var database = new AppDatabase(databasePath);
    await database.InitializeAsync();
    var store = new SqliteTransferStore(database);
    var history = new HistoryService(database);
    var completedTransferId = Guid.NewGuid();
    var completedFileId = Guid.NewGuid();
    var now = DateTimeOffset.UtcNow;
    await store.UpsertJobAsync(new TransferJobRecord(
        completedTransferId, "source", "destination", TransferDirection.Receive,
        TransferState.Completed, now, now, now), default);
    await store.UpsertFileAsync(new TransferFileRecord(
        completedTransferId, completedFileId, "received.bin", sourcePath, null, finalPath,
        3, 0, null, 8, 3, 3, [], TransferState.Completed), default);

    var completedRow = (await ToListAsync(history.GetRecentAsync(NoneRunning))).Single();
    Assert(completedRow.StateText == "완료", "A completed receive should be listed as 완료.");
    Assert(completedRow.Location == finalPath, "A received file should report its final location.");

    Assert(await history.DeleteFileAsync(completedFileId, NoneRunning), "Terminal file history should be deleted.");
    Assert(File.Exists(sourcePath), "History deletion removed the source file.");
    Assert(File.Exists(finalPath), "History deletion removed the received file.");
    Assert(await CountAsync(history.GetRecentAsync(NoneRunning)) == 0, "Deleted history remained visible.");

    var reopened = new HistoryService(new AppDatabase(databasePath));
    Assert(await CountAsync(reopened.GetRecentAsync(NoneRunning)) == 0, "Deleted history returned after reopening.");

    // A job a worker still owns must never be removed by delete or by clear.
    var activeTransferId = Guid.NewGuid();
    var activeFileId = Guid.NewGuid();
    await store.UpsertJobAsync(new TransferJobRecord(
        activeTransferId, "source", "destination", TransferDirection.Send,
        TransferState.Transferring, now, now), default);
    await store.UpsertFileAsync(new TransferFileRecord(
        activeTransferId, activeFileId, "source.bin", sourcePath, null, null,
        3, 0, null, 8, 0, 0, [], TransferState.Transferring), default);

    var secondTerminalTransferId = Guid.NewGuid();
    var secondTerminalFileId = Guid.NewGuid();
    await store.UpsertJobAsync(new TransferJobRecord(
        secondTerminalTransferId, "source", "destination", TransferDirection.Send,
        TransferState.FailedFatal, now, now, null, "TEST"), default);
    await store.UpsertFileAsync(new TransferFileRecord(
        secondTerminalTransferId, secondTerminalFileId, "source.bin", sourcePath, null, null,
        3, 0, null, 8, 0, 0, [], TransferState.FailedFatal), default);

    // A stuck row from an old failed attempt: never terminal, but nothing is running for it.
    var stuckTransferId = Guid.NewGuid();
    var stuckFileId = Guid.NewGuid();
    var stale = now.AddDays(-30);
    await store.UpsertJobAsync(new TransferJobRecord(
        stuckTransferId, "source", "destination", TransferDirection.Send,
        TransferState.WaitingDevice, stale, stale), default);
    await store.UpsertFileAsync(new TransferFileRecord(
        stuckTransferId, stuckFileId, "source.bin", sourcePath, null, null,
        3, 0, null, 8, 0, 0, [], TransferState.Queued), default);

    var running = new HashSet<Guid> { activeTransferId };
    var listed = await ToListAsync(history.GetRecentAsync(running));
    Assert(listed.Single(item => item.FileId == activeFileId).CanDelete == false, "A running transfer was marked deletable.");
    Assert(listed.Single(item => item.FileId == stuckFileId).CanDelete, "A stuck non-running record was not deletable.");

    Assert(!await history.DeleteFileAsync(activeFileId, running), "Active history was deletable.");
    Assert(await history.DeleteFileAsync(stuckFileId, running), "A stuck record could not be deleted individually.");

    // Recreate the stuck row so the bulk clear path is exercised as well.
    await store.UpsertJobAsync(new TransferJobRecord(
        stuckTransferId, "source", "destination", TransferDirection.Send,
        TransferState.WaitingDevice, stale, stale), default);
    await store.UpsertFileAsync(new TransferFileRecord(
        stuckTransferId, stuckFileId, "source.bin", sourcePath, null, null,
        3, 0, null, 8, 0, 0, [], TransferState.Queued), default);

    var cleared = await history.ClearAsync(running);
    Assert(cleared.DeletedFiles == 2, $"Clear removed {cleared.DeletedFiles} rows instead of the terminal and stuck rows.");
    Assert(cleared.KeptRunningJobs == 1, "Clear did not preserve the running job.");
    var remaining = await ToListAsync(history.GetRecentAsync(running));
    Assert(remaining.Count == 1 && remaining[0].FileId == activeFileId, "Clear removed an active transfer.");
    Assert(File.Exists(sourcePath), "History clear removed the source file.");
    Assert(File.Exists(finalPath), "History clear removed the received file.");

    var afterRestart = await ToListAsync(new HistoryService(new AppDatabase(databasePath)).GetRecentAsync(running));
    Assert(afterRestart.Count == 1 && afterRestart[0].FileId == activeFileId, "Cleared history reappeared after reopening.");

    // A committed write must announce itself so the list refreshes without 새로 고침.
    var eventDbPath = Path.Combine(root, "events.db");
    var eventDatabase = new AppDatabase(eventDbPath);
    await eventDatabase.InitializeAsync();
    var eventStore = new SqliteTransferStore(eventDatabase);
    var eventHistory = new HistoryService(eventDatabase);
    var storeEvents = 0;
    var historyEvents = 0;
    eventStore.Changed += (_, _) => storeEvents++;
    eventHistory.Changed += (_, _) => historyEvents++;

    var sendTransfer = Guid.NewGuid();
    var sendFile = Guid.NewGuid();
    await eventStore.UpsertJobAsync(new TransferJobRecord(
        sendTransfer, "source", "destination", TransferDirection.Send,
        TransferState.Transferring, now, now), default);
    Assert(storeEvents == 1, "Creating a send job raised no change event.");
    await eventStore.UpsertFileAsync(new TransferFileRecord(
        sendTransfer, sendFile, "source.bin", sourcePath, null, null,
        3, 0, null, 8, 0, 0, [], TransferState.Transferring), default);
    Assert(storeEvents == 2, "Writing a file row raised no change event.");

    // Checkpoints fire per chunk and must not stampede the UI.
    await eventStore.SaveCheckpointAsync(sendFile, 1, [], now, default);
    Assert(storeEvents == 2, "A checkpoint raised a history change event.");

    var beforeSendCompletion = storeEvents;
    await eventStore.UpsertJobAsync(new TransferJobRecord(
        sendTransfer, "source", "destination", TransferDirection.Send,
        TransferState.Completed, now, now, now), default);
    Assert(storeEvents == beforeSendCompletion + 1, "Send completion raised no change event.");
    // The event fires after the commit, so a listener re-querying now sees the finished row.
    var sendRow = (await ToListAsync(eventHistory.GetRecentAsync(NoneRunning))).Single(item => item.FileId == sendFile);
    Assert(sendRow.StateText == "완료", $"Send row read back as {sendRow.StateText} right after its event.");

    var receiveTransfer = Guid.NewGuid();
    var receiveFile = Guid.NewGuid();
    await eventStore.UpsertJobAsync(new TransferJobRecord(
        receiveTransfer, "source", "destination", TransferDirection.Receive,
        TransferState.Transferring, now, now), default);
    await eventStore.UpsertFileAsync(new TransferFileRecord(
        receiveTransfer, receiveFile, "received.bin", string.Empty, null, finalPath,
        3, 0, null, 8, 3, 3, [], TransferState.Transferring), default);
    var beforeReceiveCompletion = storeEvents;
    await eventStore.MarkFileCompletedAsync(receiveFile, finalPath, [1], now, default);
    await eventStore.UpsertJobAsync(new TransferJobRecord(
        receiveTransfer, "source", "destination", TransferDirection.Receive,
        TransferState.Completed, now, now, now), default);
    Assert(storeEvents >= beforeReceiveCompletion + 2, "Receive completion raised no change event.");
    var receiveRow = (await ToListAsync(eventHistory.GetRecentAsync(NoneRunning))).Single(item => item.FileId == receiveFile);
    Assert(receiveRow.StateText == "완료", $"Receive row read back as {receiveRow.StateText} right after its event.");

    // Queue wording is applied per row from the live coordinator facts.
    var queued = new Dictionary<Guid, int> { [sendTransfer] = 2 };
    var presentation = new HistoryPresentation(
        id => queued.TryGetValue(id, out var position) ? position : null,
        static _ => true);
    var queuedRow = (await ToListAsync(eventHistory.GetRecentAsync(NoneRunning, presentation))).Single(item => item.FileId == sendFile);
    Assert(queuedRow.StateText == "대기열 2번째", $"Queued send row read as {queuedRow.StateText}.");
    var offlinePresentation = new HistoryPresentation(
        id => queued.TryGetValue(id, out var position) ? position : null,
        static _ => false);
    var offlineRow = (await ToListAsync(eventHistory.GetRecentAsync(NoneRunning, offlinePresentation))).Single(item => item.FileId == sendFile);
    Assert(offlineRow.StateText == "기기 연결 대기", $"Offline queued row read as {offlineRow.StateText}.");
    // An incoming job is never someone else's queue entry.
    var receiveQueueRow = (await ToListAsync(eventHistory.GetRecentAsync(NoneRunning, presentation))).Single(item => item.FileId == receiveFile);
    Assert(receiveQueueRow.StateText == "완료", "A receive row was labelled with a queue position.");

    var beforeDelete = historyEvents;
    Assert(await eventHistory.DeleteFileAsync(sendFile, NoneRunning), "Settled send row should be deletable.");
    Assert(historyEvents == beforeDelete + 1, "History deletion raised no change event.");
    await eventHistory.ClearAsync(NoneRunning);
    Assert(historyEvents == beforeDelete + 2, "History clear raised no change event.");
    Assert(File.Exists(sourcePath) && File.Exists(finalPath), "History events path deleted real files.");

    Console.WriteLine("PASS Windows history deletion persistence and file preservation");
    Console.WriteLine("PASS Windows stuck-record cleanup keeps running transfers");
    Console.WriteLine("PASS Windows history auto-refresh events fire after commit");
    Console.WriteLine("PASS Windows queue wording separates queued from other waiting states");

    // ---- Update check policy -----------------------------------------------------

    // Tag parsing: the repository tags releases as vX.Y.Z.
    Assert(Equals(UpdateCheckPolicy.TryParseTag("v0.0.3"), new Version(0, 0, 3)), "v-prefixed tag did not parse.");
    Assert(Equals(UpdateCheckPolicy.TryParseTag("V1.2.3"), new Version(1, 2, 3)), "Uppercase V tag did not parse.");
    Assert(Equals(UpdateCheckPolicy.TryParseTag(" 0.0.3 "), new Version(0, 0, 3)), "Bare padded tag did not parse.");
    Assert(UpdateCheckPolicy.TryParseTag("release-1") is null, "Junk tag was accepted.");
    Assert(UpdateCheckPolicy.TryParseTag(null) is null, "Null tag was accepted.");
    Assert(UpdateCheckPolicy.TryParseTag("") is null, "Empty tag was accepted.");

    // The SDK appends the source revision to the informational version; it must be stripped.
    Assert(Equals(UpdateCheckPolicy.NormalizeCurrentVersion("0.0.3"), new Version(0, 0, 3)), "Plain informational version did not parse.");
    Assert(Equals(UpdateCheckPolicy.NormalizeCurrentVersion("0.0.3+82d470fabc"), new Version(0, 0, 3)), "+revision suffix was not stripped.");
    Assert(Equals(UpdateCheckPolicy.NormalizeCurrentVersion("0.0.3-preview1"), new Version(0, 0, 3)), "-prerelease suffix was not stripped.");
    Assert(UpdateCheckPolicy.NormalizeCurrentVersion(null) is null, "Null informational version was accepted.");

    // Release payload parsing: drafts and prereleases are excluded even if they leak through.
    var official = UpdateCheckPolicy.ParseLatestRelease(
        """{"tag_name":"v0.0.4","html_url":"https://github.com/esleeeeee/eslee-quick-send/releases/tag/v0.0.4","draft":false,"prerelease":false}""");
    Assert(official is not null && Equals(official.Version, new Version(0, 0, 4)), "Official release payload did not parse.");
    Assert(official!.HtmlUrl.EndsWith("v0.0.4", StringComparison.Ordinal), "Release URL was not captured.");
    Assert(UpdateCheckPolicy.ParseLatestRelease(
        """{"tag_name":"v0.0.9","html_url":"x","prerelease":true}""") is null, "A prerelease payload was accepted.");
    Assert(UpdateCheckPolicy.ParseLatestRelease(
        """{"tag_name":"v0.0.9","html_url":"x","draft":true}""") is null, "A draft payload was accepted.");
    Assert(UpdateCheckPolicy.ParseLatestRelease("""{"html_url":"x"}""") is null, "A payload without tag_name was accepted.");
    Assert(UpdateCheckPolicy.ParseLatestRelease("not json") is null, "Malformed JSON was accepted.");
    Assert(UpdateCheckPolicy.ParseLatestRelease("[1,2]") is null, "A non-object payload was accepted.");

    // Comparison: only a strictly newer official release counts as an update.
    var current = new Version(0, 0, 3);
    Assert(UpdateCheckPolicy.Classify(current, new Version(0, 0, 4)) == UpdateStatusKind.UpdateAvailable, "Newer release was not an update.");
    Assert(UpdateCheckPolicy.Classify(current, new Version(0, 0, 3)) == UpdateStatusKind.UpToDate, "Equal release was not up to date.");
    Assert(UpdateCheckPolicy.Classify(current, new Version(0, 0, 2)) == UpdateStatusKind.UpToDate, "Older release was not up to date.");

    // Interval gate: startup checks reuse a fresh cache and never hammer the API.
    var checkNow = DateTimeOffset.UtcNow;
    Assert(UpdateCheckPolicy.ShouldCheck(null, checkNow), "A never-checked state was gated.");
    Assert(!UpdateCheckPolicy.ShouldCheck(checkNow.AddHours(-1), checkNow), "A one-hour-old check was repeated.");
    Assert(UpdateCheckPolicy.ShouldCheck(checkNow.AddHours(-25), checkNow), "A stale check was not repeated.");
    Assert(UpdateCheckPolicy.ShouldCheck(checkNow.AddHours(2), checkNow), "A future timestamp (clock change) was trusted.");

    // Cache round-trip through the same settings table the service uses.
    var cacheDb = new AppDatabase(Path.Combine(root, "updates.db"));
    await cacheDb.InitializeAsync();
    await cacheDb.SetSettingAsync("update.last_checked_utc", checkNow.ToString("O"));
    await cacheDb.SetSettingAsync("update.latest_tag", "v0.0.4");
    var roundTripped = await cacheDb.GetSettingAsync("update.last_checked_utc");
    Assert(DateTimeOffset.TryParse(roundTripped, null, System.Globalization.DateTimeStyles.RoundtripKind, out var restored)
        && restored == checkNow, "Last-checked timestamp did not round-trip.");
    Assert(Equals(UpdateCheckPolicy.TryParseTag(await cacheDb.GetSettingAsync("update.latest_tag")), new Version(0, 0, 4)),
        "Cached tag did not round-trip.");

    Console.WriteLine("PASS Update tag/version parsing and prerelease exclusion");
    Console.WriteLine("PASS Update interval gate and cache round-trip");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"FAIL Windows history deletion: {error}");
    return 1;
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(root, recursive: true);
}

static async Task<int> CountAsync(IAsyncEnumerable<HistoryListItem> items)
{
    var count = 0;
    await foreach (var _ in items) count++;
    return count;
}

static async Task<List<HistoryListItem>> ToListAsync(IAsyncEnumerable<HistoryListItem> items)
{
    var result = new List<HistoryListItem>();
    await foreach (var item in items) result.Add(item);
    return result;
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
