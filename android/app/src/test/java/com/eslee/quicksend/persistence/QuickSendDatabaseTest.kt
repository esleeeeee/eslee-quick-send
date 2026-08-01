package com.eslee.quicksend.persistence

import android.content.Context
import android.database.sqlite.SQLiteDatabase
import com.eslee.quicksend.engine.TransferHistoryPolicy
import com.eslee.quicksend.engine.TransferState
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import org.robolectric.annotation.SQLiteMode
import java.io.File
import java.util.UUID

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35], manifest = Config.NONE)
@SQLiteMode(SQLiteMode.Mode.NATIVE)
class QuickSendDatabaseTest {
    private lateinit var context: Context
    private var helper: QuickSendDatabase? = null

    @Before
    fun setUp() {
        context = RuntimeEnvironment.getApplication()
        context.deleteDatabase(DATABASE_NAME)
    }

    @After
    fun tearDown() {
        helper?.close()
        context.deleteDatabase(DATABASE_NAME)
    }

    @Test
    fun freshDatabaseAppliesQueryPragmasAndCreatesValidWalSchema() {
        val database = openDatabase()

        assertEquals(2, database.intPragma("PRAGMA synchronous"))
        assertEquals(5_000, database.intPragma("PRAGMA busy_timeout"))
        assertEquals(1, database.intPragma("PRAGMA foreign_keys"))
        assertTrue(database.isWriteAheadLoggingEnabled)
        assertEquals("ok", database.stringPragma("PRAGMA quick_check(1)"))
        assertRequiredSchema(database)
    }

    @Test
    fun existingVersionOneDatabaseUpgradesWithoutLosingSettings() {
        val original = openDatabase()
        original.execSQL(
            "INSERT INTO settings(key,value) VALUES(?,?)",
            arrayOf("migration_marker", "preserved"),
        )
        original.version = 1
        helper?.close()
        helper = null

        val upgraded = openDatabase()

        assertEquals(3, upgraded.version)
        upgraded.rawQuery(
            "SELECT value FROM settings WHERE key=?",
            arrayOf("migration_marker"),
        ).use { cursor ->
            assertTrue(cursor.moveToFirst())
            assertEquals("preserved", cursor.getString(0))
        }
        assertEquals(2, upgraded.intPragma("PRAGMA synchronous"))
        assertEquals(5_000, upgraded.intPragma("PRAGMA busy_timeout"))
        assertEquals("ok", upgraded.stringPragma("PRAGMA quick_check(1)"))
        assertRequiredSchema(upgraded)
    }

    @Test
    fun versionTwoDatabaseGainsDisplayPathWithoutLosingHistory() = runBlocking {
        val repository = openRepository()
        val transferId = UUID.randomUUID()
        val fileId = UUID.randomUUID()
        repository.upsertJob(newJob(transferId, TransferState.COMPLETED))
        repository.upsertFile(newFile(transferId, fileId, TransferState.COMPLETED, "source", "content://legacy/doc"))

        // Simulate the pre-migration shape: version 2 without the display_path column.
        val database = checkNotNull(helper).writableDatabase
        database.execSQL("ALTER TABLE transfer_files DROP COLUMN display_path")
        database.version = 2
        helper?.close()
        helper = null

        val migrated = openRepository()
        val history = migrated.recentHistory()

        assertEquals(3, checkNotNull(helper).writableDatabase.version)
        assertEquals(listOf(fileId), history.map { it.file.fileId })
        assertNull(history.single().file.displayPath)
        assertEquals("content://legacy/doc", history.single().file.finalUri)
    }

    @Test
    fun completedSendAndReceiveRowsSurviveRestartAndExposeDisplayPath() = runBlocking {
        val repository = openRepository()
        val sentTransfer = UUID.randomUUID()
        val sentFile = UUID.randomUUID()
        val receivedTransfer = UUID.randomUUID()
        val receivedFile = UUID.randomUUID()

        repository.upsertJob(newJob(sentTransfer, TransferState.COMPLETED, TransferDirection.SEND))
        repository.upsertFile(newFile(sentTransfer, sentFile, TransferState.COMPLETED, "content://source/doc", null))
        repository.upsertJob(newJob(receivedTransfer, TransferState.COMPLETED, TransferDirection.RECEIVE))
        repository.upsertFile(newFile(receivedTransfer, receivedFile, TransferState.TRANSFERRING, "", null))
        repository.complete(receivedFile, "content://tree/doc", "내장 저장소/다운로드/퀵쉐어/payload.bin", byteArrayOf(9))

        val before = repository.recentHistory()
        assertEquals(setOf(sentFile, receivedFile), before.map { it.file.fileId }.toSet())
        assertEquals(
            TransferDirection.SEND,
            before.single { it.file.fileId == sentFile }.direction,
        )
        assertEquals(
            "내장 저장소/다운로드/퀵쉐어/payload.bin",
            before.single { it.file.fileId == receivedFile }.file.displayPath,
        )

        helper?.close()
        helper = null
        val afterRestart = openRepository().recentHistory()
        assertEquals(setOf(sentFile, receivedFile), afterRestart.map { it.file.fileId }.toSet())
        assertTrue(afterRestart.all { it.canDelete })
    }

    @Test
    fun repositoryRevisionAdvancesOnEveryHistoryWrite() = runBlocking {
        val repository = openRepository()
        val transferId = UUID.randomUUID()
        val fileId = UUID.randomUUID()
        val start = repository.revision.value

        repository.upsertJob(newJob(transferId, TransferState.TRANSFERRING))
        val afterJob = repository.revision.value
        repository.upsertFile(newFile(transferId, fileId, TransferState.TRANSFERRING, "source", null))
        val afterFile = repository.revision.value
        repository.complete(fileId, "content://tree/doc", "내장 저장소/다운로드/퀵쉐어/payload.bin", byteArrayOf(1))

        assertTrue(afterJob > start)
        assertTrue(afterFile > afterJob)
        assertTrue(repository.revision.value > afterFile)
    }

    @Test
    fun stuckLegacyRecordIsCleanableWhileRunningTransferIsPreserved() = runBlocking {
        val repository = openRepository()
        val stuckTransfer = UUID.randomUUID()
        val stuckFile = UUID.randomUUID()
        val runningTransfer = UUID.randomUUID()
        val runningFile = UUID.randomUUID()
        val sourceFile = File(context.cacheDir, "stuck-source-${UUID.randomUUID()}.bin").apply {
            writeBytes(byteArrayOf(1))
        }
        try {
            // A row from an old failed attempt: never terminal, but nothing runs for it.
            repository.upsertJob(newJob(stuckTransfer, TransferState.WAITING_DEVICE))
            repository.upsertFile(newFile(stuckTransfer, stuckFile, TransferState.QUEUED, sourceFile.absolutePath, null))
            repository.upsertJob(newJob(runningTransfer, TransferState.TRANSFERRING))
            repository.upsertFile(newFile(runningTransfer, runningFile, TransferState.TRANSFERRING, sourceFile.absolutePath, null))

            val running = setOf(runningTransfer)
            val listed = repository.recentHistory(running)
            assertTrue(listed.single { it.file.fileId == stuckFile }.canDelete)
            assertFalse(listed.single { it.file.fileId == runningFile }.canDelete)

            assertFalse(repository.deleteHistoryFile(runningFile, running))
            val cleared = repository.clearHistory(running)

            assertEquals(1, cleared.deletedFiles)
            assertEquals(1, cleared.keptRunningJobs)
            assertEquals(listOf(runningFile), repository.recentHistory(running).map { it.file.fileId })
            assertTrue(sourceFile.exists())

            // A cleaned record must not come back on the next launch.
            helper?.close()
            helper = null
            assertEquals(listOf(runningFile), openRepository().recentHistory(running).map { it.file.fileId })
        } finally {
            sourceFile.delete()
        }
    }

    @Test
    fun expiredResumeRowIsSettledInsteadOfResumingForever() = runBlocking {
        val repository = openRepository()
        val transferId = UUID.randomUUID()
        val fileId = UUID.randomUUID()
        val stale = System.currentTimeMillis() - TransferHistoryPolicy.RESUME_WINDOW_MILLIS - 86_400_000
        repository.upsertJob(
            TransferJobRecord(transferId, "source", "destination", TransferDirection.SEND, TransferState.WAITING_DEVICE, stale, stale),
        )
        repository.upsertFile(newFile(transferId, fileId, TransferState.QUEUED, "content://source/doc", null))

        assertEquals(1, repository.settleExpiredJobs())
        assertTrue(repository.recoverableJobs().none { it.transferId == transferId })

        val settled = repository.recentHistory().single { it.file.fileId == fileId }
        assertEquals(TransferState.USER_ACTION_REQUIRED, settled.jobState)
        assertEquals(TransferHistoryPolicy.RESUME_EXPIRED_ERROR_CODE, settled.jobErrorCode)
        assertTrue(settled.canDelete)
        assertTrue(repository.deleteHistoryFile(fileId))
    }

    @Test
    fun deviceNameChangePersistsAndRejectsBlankNames() = runBlocking {
        openDatabase()
        val settings = SettingsRepository(checkNotNull(helper))
        val names = DeviceNameRepository(settings, "SM-S926N")

        assertEquals("SM-S926N", names.load())
        assertTrue(names.set("   ") is DeviceNameUpdate.Rejected)
        assertEquals("SM-S926N", names.current)

        val accepted = names.set("  이슬이   휴대폰 ")
        assertTrue(accepted is DeviceNameUpdate.Accepted)
        assertEquals("이슬이 휴대폰", names.current)

        // The stable device id and any identity material are untouched by a rename.
        settings.set("device.id", "stable-id")
        assertEquals("이슬이 휴대폰", DeviceNameRepository(settings, "SM-S926N").load())
        assertEquals("stable-id", settings.get("device.id"))
    }

    @Test
    fun deletingHistoryPersistsWithoutDeletingSourceOrReceivedFile() = runBlocking {
        val repository = openRepository()
        val transferId = UUID.randomUUID()
        val fileId = UUID.randomUUID()
        val source = File(context.cacheDir, "history-source-${UUID.randomUUID()}.bin").apply {
            writeBytes(byteArrayOf(1, 2, 3))
        }
        val received = File(context.cacheDir, "history-received-${UUID.randomUUID()}.bin").apply {
            writeBytes(byteArrayOf(4, 5, 6))
        }
        try {
            repository.upsertJob(newJob(transferId, TransferState.COMPLETED))
            repository.upsertFile(
                newFile(
                    transferId,
                    fileId,
                    TransferState.COMPLETED,
                    source.absolutePath,
                    received.absolutePath,
                ),
            )

            assertTrue(repository.deleteHistoryFile(fileId))
            assertTrue(repository.recentHistory().isEmpty())
            assertTrue(source.exists())
            assertTrue(received.exists())

            helper?.close()
            helper = null
            assertTrue(openRepository().recentHistory().isEmpty())
            assertTrue(source.exists())
            assertTrue(received.exists())
        } finally {
            source.delete()
            received.delete()
        }
    }

    @Test
    fun clearHistoryRemovesOnlyTerminalRowsAndNeverTouchesFiles() = runBlocking {
        val repository = openRepository()
        val completedTransfer = UUID.randomUUID()
        val activeTransfer = UUID.randomUUID()
        val completedFileId = UUID.randomUUID()
        val activeFileId = UUID.randomUUID()
        val completedFile = File(context.cacheDir, "completed-${UUID.randomUUID()}.bin").apply {
            writeBytes(byteArrayOf(7))
        }
        val activeFile = File(context.cacheDir, "active-${UUID.randomUUID()}.bin").apply {
            writeBytes(byteArrayOf(8))
        }
        try {
            repository.upsertJob(newJob(completedTransfer, TransferState.COMPLETED))
            repository.upsertFile(
                newFile(
                    completedTransfer,
                    completedFileId,
                    TransferState.COMPLETED,
                    completedFile.absolutePath,
                    completedFile.absolutePath,
                ),
            )
            repository.upsertJob(newJob(activeTransfer, TransferState.TRANSFERRING))
            repository.upsertFile(
                newFile(
                    activeTransfer,
                    activeFileId,
                    TransferState.TRANSFERRING,
                    activeFile.absolutePath,
                    null,
                ),
            )

            val running = setOf(activeTransfer)
            assertFalse(repository.deleteHistoryFile(activeFileId, running))
            assertEquals(1, repository.clearHistory(running).deletedFiles)
            val remaining = repository.recentHistory(running)
            assertEquals(listOf(activeFileId), remaining.map { it.file.fileId })
            assertTrue(completedFile.exists())
            assertTrue(activeFile.exists())
        } finally {
            completedFile.delete()
            activeFile.delete()
        }
    }

    private fun newJob(
        transferId: UUID,
        state: TransferState,
        direction: TransferDirection = TransferDirection.RECEIVE,
    ): TransferJobRecord {
        val now = System.currentTimeMillis()
        return TransferJobRecord(
            transferId,
            "source",
            "destination",
            direction,
            state,
            now,
            now,
            if (state == TransferState.COMPLETED) now else null,
        )
    }

    private fun newFile(
        transferId: UUID,
        fileId: UUID,
        state: TransferState,
        sourcePath: String,
        finalPath: String?,
    ) = TransferFileRecord(
        transferId,
        fileId,
        "payload.bin",
        sourcePath,
        null,
        finalPath,
        1,
        0,
        null,
        8,
        if (state == TransferState.COMPLETED) 1 else 0,
        if (state == TransferState.COMPLETED) 1 else 0,
        byteArrayOf(),
        state,
    )

    private fun openRepository(): TransferRepository {
        openDatabase()
        return TransferRepository(checkNotNull(helper))
    }

    private fun openDatabase(): SQLiteDatabase {
        val openedHelper = QuickSendDatabase(context)
        helper = openedHelper
        val database = openedHelper.writableDatabase
        // Robolectric does not invoke SQLiteOpenHelper.onConfigure automatically.
        // Call the real callback so this test exercises the same PRAGMA API path
        // that failed on the physical Android device.
        openedHelper.onConfigure(database)
        return database
    }

    private fun assertRequiredSchema(database: SQLiteDatabase) {
        val expectedTables = setOf(
            "settings",
            "trusted_devices",
            "transfer_jobs",
            "transfer_files",
        )
        val actualTables = mutableSetOf<String>()
        database.rawQuery(
            "SELECT name FROM sqlite_master WHERE type='table'",
            null,
        ).use { cursor ->
            while (cursor.moveToNext()) actualTables += cursor.getString(0)
        }
        assertTrue(actualTables.containsAll(expectedTables))
    }

    private fun SQLiteDatabase.intPragma(sql: String): Int =
        rawQuery(sql, null).use { cursor ->
            assertTrue("$sql did not return a row", cursor.moveToFirst())
            cursor.getInt(0)
        }

    private fun SQLiteDatabase.stringPragma(sql: String): String =
        rawQuery(sql, null).use { cursor ->
            assertTrue("$sql did not return a row", cursor.moveToFirst())
            cursor.getString(0)
        }

    private companion object {
        const val DATABASE_NAME = "quicksend.db"
    }
}
