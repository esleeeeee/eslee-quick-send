package com.eslee.quicksend.persistence

import android.content.ContentValues
import android.content.Context
import android.database.Cursor
import android.database.sqlite.SQLiteDatabaseCorruptException
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteException
import android.database.sqlite.SQLiteOpenHelper
import com.eslee.quicksend.diagnostics.DiagnosticLog
import com.eslee.quicksend.engine.DeviceNameRules
import com.eslee.quicksend.engine.TransferHistoryPolicy
import com.eslee.quicksend.engine.TransferState
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.File
import java.util.UUID

class QuickSendDatabase(private val appContext: Context) : SQLiteOpenHelper(appContext, DATABASE_NAME, null, DATABASE_VERSION) {
    init {
        setWriteAheadLoggingEnabled(true)
    }

    override fun onConfigure(db: SQLiteDatabase) {
        db.setForeignKeyConstraintsEnabled(true)
        setAndVerifyPragma(db, "PRAGMA synchronous=FULL", "PRAGMA synchronous", 2)
        setAndVerifyPragma(db, "PRAGMA busy_timeout=5000", "PRAGMA busy_timeout", 5_000)
    }

    override fun onCreate(db: SQLiteDatabase) {
        createSchema(db)
        applyAdditiveMigrations(db)
    }

    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) {
        // Version 2 validates early development installs that may have carried a
        // different version-1 schema. Complete schemas are preserved unchanged.
        createSchema(db)
        // Version 3 adds the human-readable receive location. Existing rows keep their
        // data and simply carry a NULL display path until the UI recomputes one.
        applyAdditiveMigrations(db)
    }

    override fun onDowngrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) {
        error("Database downgrade is unsafe ($oldVersion -> $newVersion)")
    }

    suspend fun initialize(log: DiagnosticLog) = withContext(Dispatchers.IO) {
        try {
            validate(writableDatabase)
            log.info("android.db.init.complete")
        } catch (error: Throwable) {
            if (!isRecoverableDatabaseFailure(error)) throw error

            log.error(
                "android.db.init.failed",
                error,
                JSONObject().put("startupStage", "database.validation"),
            )
            quarantineInvalidDatabase(log, error)
            validate(writableDatabase)
            log.info("android.db.recreated")
        }
    }

    private fun createSchema(db: SQLiteDatabase) {
        db.execSQL("CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY NOT NULL,value TEXT NOT NULL)")
        db.execSQL("""CREATE TABLE IF NOT EXISTS trusted_devices(
            device_id TEXT PRIMARY KEY NOT NULL,display_name TEXT NOT NULL,fingerprint TEXT NOT NULL UNIQUE,
            paired_utc INTEGER NOT NULL,last_seen_utc INTEGER NOT NULL,revoked_utc INTEGER NULL)""")
        db.execSQL("""CREATE TABLE IF NOT EXISTS transfer_jobs(
            transfer_id TEXT PRIMARY KEY NOT NULL,source_device_id TEXT NOT NULL,destination_device_id TEXT NOT NULL,
            direction INTEGER NOT NULL,state INTEGER NOT NULL,created_utc INTEGER NOT NULL,updated_utc INTEGER NOT NULL,
            completed_utc INTEGER NULL,error_code TEXT NULL)""")
        db.execSQL("""CREATE TABLE IF NOT EXISTS transfer_files(
            file_id TEXT PRIMARY KEY NOT NULL,transfer_id TEXT NOT NULL REFERENCES transfer_jobs(transfer_id) ON DELETE CASCADE,
            relative_path TEXT NOT NULL,source_uri TEXT NOT NULL,partial_uri TEXT NULL,final_uri TEXT NULL,
            size INTEGER NOT NULL CHECK(size>=0),modified_ticks INTEGER NOT NULL,stable_source_id TEXT NULL,
            chunk_size INTEGER NOT NULL,received_offset INTEGER NOT NULL DEFAULT 0,committed_offset INTEGER NOT NULL DEFAULT 0,
            merkle_leaves BLOB NOT NULL,merkle_root BLOB NULL,state INTEGER NOT NULL,retry_count INTEGER NOT NULL DEFAULT 0,
            error_code TEXT NULL,updated_utc INTEGER NOT NULL)""")
        db.execSQL("CREATE INDEX IF NOT EXISTS ix_jobs_state ON transfer_jobs(state,updated_utc)")
        db.execSQL("CREATE INDEX IF NOT EXISTS ix_files_transfer ON transfer_files(transfer_id)")
    }

    /** Adds columns introduced after version 1 without rewriting existing rows. */
    private fun applyAdditiveMigrations(db: SQLiteDatabase) {
        if (!hasColumn(db, "transfer_files", "display_path")) {
            db.execSQL("ALTER TABLE transfer_files ADD COLUMN display_path TEXT NULL")
        }
    }

    private fun hasColumn(db: SQLiteDatabase, table: String, column: String): Boolean =
        db.rawQuery("PRAGMA table_info($table)", null).use { cursor ->
            val nameIndex = cursor.getColumnIndex("name")
            if (nameIndex < 0) return false
            while (cursor.moveToNext()) if (cursor.getString(nameIndex) == column) return true
            false
        }

    private fun setAndVerifyPragma(
        db: SQLiteDatabase,
        assignmentSql: String,
        readbackSql: String,
        expectedValue: Int,
    ) {
        // Android classifies PRAGMA statements as queries even when they assign a
        // value, so execSQL() rejects them before SQLite can apply the setting.
        db.rawQuery(assignmentSql, null).use { cursor ->
            cursor.moveToFirst()
        }
        db.rawQuery(readbackSql, null).use { cursor ->
            if (!cursor.moveToFirst()) {
                throw SQLiteException("$readbackSql did not return a value")
            }
            val actualValue = cursor.getInt(0)
            if (actualValue != expectedValue) {
                throw SQLiteException(
                    "$assignmentSql was not applied (expected $expectedValue, actual $actualValue)",
                )
            }
        }
    }

    private fun validate(db: SQLiteDatabase) {
        db.rawQuery("PRAGMA quick_check(1)", null).use { cursor ->
            if (!cursor.moveToFirst() || cursor.getString(0) != "ok") {
                throw SchemaValidationException("SQLite quick_check did not return ok")
            }
        }

        REQUIRED_COLUMNS.forEach { (table, expected) ->
            val actual = mutableSetOf<String>()
            db.rawQuery("PRAGMA table_info($table)", null).use { cursor ->
                val nameIndex = cursor.getColumnIndexOrThrow("name")
                while (cursor.moveToNext()) actual += cursor.getString(nameIndex)
            }
            val missing = expected - actual
            if (missing.isNotEmpty()) {
                throw SchemaValidationException("$table is missing columns: ${missing.sorted().joinToString()}")
            }
        }
    }

    private fun isRecoverableDatabaseFailure(error: Throwable): Boolean {
        if (error is SchemaValidationException || error is SQLiteDatabaseCorruptException) return true
        if (error !is SQLiteException) return false
        val message = error.message.orEmpty().lowercase()
        return listOf(
            "malformed",
            "not a database",
            "file is encrypted",
            "database disk image is malformed",
            "no such table",
            "no such column",
            "has no column named",
        )
            .any(message::contains)
    }

    private fun quarantineInvalidDatabase(log: DiagnosticLog, cause: Throwable) {
        runCatching { close() }
        val timestamp = System.currentTimeMillis()
        val databaseFile = appContext.getDatabasePath(DATABASE_NAME)
        val candidates = listOf(
            databaseFile,
            File(databaseFile.path + "-wal"),
            File(databaseFile.path + "-shm"),
        )

        candidates.filter(File::exists).forEach { source ->
            val backup = File(source.parentFile, "${source.name}.invalid-$timestamp")
            if (!source.renameTo(backup)) {
                source.copyTo(backup, overwrite = false)
                check(source.delete()) { "Unable to remove invalid database file ${source.name}" }
            }
        }
        log.warn(
            "android.db.quarantined",
            cause,
            JSONObject()
                .put("startupStage", "database.recovery")
                .put("backupSuffix", ".invalid-$timestamp"),
        )
    }

    private class SchemaValidationException(message: String) : SQLiteException(message)

    companion object {
        private const val DATABASE_NAME = "quicksend.db"
        private const val DATABASE_VERSION = 3

        private val REQUIRED_COLUMNS = mapOf(
            "settings" to setOf("key", "value"),
            "trusted_devices" to setOf("device_id", "display_name", "fingerprint", "paired_utc", "last_seen_utc", "revoked_utc"),
            "transfer_jobs" to setOf("transfer_id", "source_device_id", "destination_device_id", "direction", "state", "created_utc", "updated_utc", "completed_utc", "error_code"),
            "transfer_files" to setOf("file_id", "transfer_id", "relative_path", "source_uri", "partial_uri", "final_uri", "size", "modified_ticks", "stable_source_id", "chunk_size", "received_offset", "committed_offset", "merkle_leaves", "merkle_root", "state", "retry_count", "error_code", "updated_utc", "display_path"),
        )
    }
}

enum class TransferDirection { SEND, RECEIVE }

data class TransferJobRecord(
    val transferId: UUID,val sourceDeviceId:String,val destinationDeviceId:String,val direction:TransferDirection,
    val state:TransferState,val createdUtc:Long,val updatedUtc:Long,val completedUtc:Long?=null,val errorCode:String?=null,
)

data class TransferFileRecord(
    val transferId:UUID,val fileId:UUID,val relativePath:String,val sourceUri:String,val partialUri:String?,val finalUri:String?,
    val size:Long,val modifiedTicks:Long,val stableSourceId:String?,val chunkSize:Int,val receivedOffset:Long,
    val committedOffset:Long,val merkleLeaves:ByteArray,val state:TransferState,val retryCount:Int=0,val errorCode:String?=null,
    /** Human-readable destination, e.g. `내장 저장소/다운로드/퀵쉐어/사진.jpg`. Never a raw content URI. */
    val displayPath:String?=null,
)

data class TransferHistoryItem(
    val file: TransferFileRecord,
    val jobState: TransferState,
    val direction: TransferDirection,
    val jobErrorCode: String? = null,
    /** True while a worker still owns the job; such a row must not be deleted. */
    val isRunning: Boolean = false,
) {
    val canDelete: Boolean
        get() = TransferHistoryPolicy.canDeleteHistory(jobState, isRunning)
}

data class HistoryClearResult(val deletedFiles: Int, val deletedJobs: Int, val keptRunningJobs: Int)

class TransferRepository(private val database: QuickSendDatabase) {
    private val _revision = MutableStateFlow(0L)

    /**
     * Increments whenever a row that can appear in the history list is written. The UI
     * collects this so a finished transfer shows up without a manual refresh.
     */
    val revision: StateFlow<Long> = _revision.asStateFlow()

    private fun bumpRevision() { _revision.value = _revision.value + 1 }

    suspend fun upsertJob(job: TransferJobRecord) = withContext(Dispatchers.IO) {
        val values=ContentValues().apply { put("transfer_id",job.transferId.toString());put("source_device_id",job.sourceDeviceId);put("destination_device_id",job.destinationDeviceId);put("direction",job.direction.ordinal);put("state",job.state.ordinal);put("created_utc",job.createdUtc);put("updated_utc",job.updatedUtc);putNullable("completed_utc",job.completedUtc);putNullable("error_code",job.errorCode) }
        val db=database.writableDatabase
        db.beginTransaction()
        try {
            if(db.insertWithOnConflict("transfer_jobs",null,values,SQLiteDatabase.CONFLICT_IGNORE)==-1L) {
                val updates=ContentValues(values).apply { remove("transfer_id"); remove("created_utc") }
                db.update("transfer_jobs",updates,"transfer_id=?",arrayOf(job.transferId.toString()))
            }
            db.setTransactionSuccessful()
        } finally { db.endTransaction() }
        bumpRevision()
    }

    suspend fun upsertFile(file: TransferFileRecord) = withContext(Dispatchers.IO) {
        val values=ContentValues().apply { put("file_id",file.fileId.toString());put("transfer_id",file.transferId.toString());put("relative_path",file.relativePath);put("source_uri",file.sourceUri);putNullable("partial_uri",file.partialUri);putNullable("final_uri",file.finalUri);put("size",file.size);put("modified_ticks",file.modifiedTicks);putNullable("stable_source_id",file.stableSourceId);put("chunk_size",file.chunkSize);put("received_offset",file.receivedOffset);put("committed_offset",file.committedOffset);put("merkle_leaves",file.merkleLeaves);put("state",file.state.ordinal);put("retry_count",file.retryCount);putNullable("error_code",file.errorCode);putNullable("display_path",file.displayPath);put("updated_utc",System.currentTimeMillis()) }
        database.writableDatabase.insertWithOnConflict("transfer_files",null,values,SQLiteDatabase.CONFLICT_REPLACE)
        bumpRevision()
    }

    suspend fun checkpoint(fileId: UUID, offset: Long, leaves: ByteArray) = withContext(Dispatchers.IO) {
        val db=database.writableDatabase
        db.beginTransaction()
        try {
            val values=ContentValues().apply { put("received_offset",offset);put("committed_offset",offset);put("merkle_leaves",leaves);put("state",TransferState.TRANSFERRING.ordinal);put("updated_utc",System.currentTimeMillis()) }
            val updated=db.update("transfer_files",values,"file_id=? AND committed_offset<=?",arrayOf(fileId.toString(),offset.toString()))
            check(updated==1) { "Checkpoint target is missing or moved backwards" }
            db.setTransactionSuccessful()
        } finally { db.endTransaction() }
    }

    suspend fun complete(fileId:UUID, finalUri:String, displayPath:String?, root:ByteArray)=withContext(Dispatchers.IO) {
        val values=ContentValues().apply { put("final_uri",finalUri);putNull("partial_uri");put("merkle_root",root);put("state",TransferState.COMPLETED.ordinal);putNullable("display_path",displayPath);put("updated_utc",System.currentTimeMillis()) }
        database.writableDatabase.update("transfer_files",values,"file_id=?",arrayOf(fileId.toString()))
        bumpRevision()
    }

    suspend fun file(fileId:UUID):TransferFileRecord?=withContext(Dispatchers.IO) {
        database.readableDatabase.query("transfer_files",null,"file_id=?",arrayOf(fileId.toString()),null,null,null).use { if(it.moveToFirst()) it.toFile() else null }
    }

    suspend fun recoverableJobs():List<TransferJobRecord> = withContext(Dispatchers.IO) {
        recoverableJobsInternal(database.readableDatabase)
    }

    private fun recoverableJobsInternal(db: SQLiteDatabase): List<TransferJobRecord> {
        val settled = TransferHistoryPolicy.SETTLED_STATES.joinToString(",") { it.ordinal.toString() }
        return db.rawQuery("SELECT * FROM transfer_jobs WHERE state NOT IN ($settled) ORDER BY updated_utc", null)
            .use { cursor -> buildList { while (cursor.moveToNext()) add(cursor.toJob()) } }
    }

    suspend fun recentFiles():List<TransferFileRecord> = withContext(Dispatchers.IO) {
        database.readableDatabase.rawQuery("SELECT * FROM transfer_files WHERE updated_utc>=? ORDER BY updated_utc DESC LIMIT 100",arrayOf((System.currentTimeMillis()-90L*86400_000).toString())).use { c->buildList{while(c.moveToNext())add(c.toFile())} }
    }

    /**
     * Recent history for the UI. Ordering follows the job so a finished transfer sorts to
     * the top even when the per-file row was written earlier.
     */
    suspend fun recentHistory(runningTransferIds: Set<UUID> = emptySet()): List<TransferHistoryItem> = withContext(Dispatchers.IO) {
        database.readableDatabase.rawQuery(
            """
                SELECT f.*,j.state AS job_state,j.direction AS job_direction,
                       j.error_code AS job_error_code,j.updated_utc AS job_updated_utc
                FROM transfer_files f JOIN transfer_jobs j ON j.transfer_id=f.transfer_id
                WHERE j.updated_utc>=?
                ORDER BY j.updated_utc DESC,f.rowid
                LIMIT 100
            """.trimIndent(),
            arrayOf((System.currentTimeMillis() - 90L * 86_400_000).toString()),
        ).use { cursor ->
            buildList {
                while (cursor.moveToNext()) {
                    val file = cursor.toFile()
                    val jobState = TransferState.entries[cursor.int("job_state")]
                    add(
                        TransferHistoryItem(
                            file = file,
                            jobState = jobState,
                            direction = TransferDirection.entries[cursor.int("job_direction")],
                            jobErrorCode = cursor.nullableString("job_error_code"),
                            isRunning = file.transferId in runningTransferIds,
                        ),
                    )
                }
            }
        }
    }

    /**
     * Deletes a single history row. Only bookkeeping is removed; the sent source file and
     * the received file are untouched.
     */
    suspend fun deleteHistoryFile(fileId: UUID, runningTransferIds: Set<UUID> = emptySet()): Boolean = withContext(Dispatchers.IO) {
        val db = database.writableDatabase
        db.beginTransaction()
        try {
            val owner = db.rawQuery(
                """
                    SELECT f.transfer_id,j.state
                    FROM transfer_files f JOIN transfer_jobs j ON j.transfer_id=f.transfer_id
                    WHERE f.file_id=?
                """.trimIndent(),
                arrayOf(fileId.toString()),
            ).use { cursor ->
                if (cursor.moveToFirst()) UUID.fromString(cursor.getString(0)) to TransferState.entries[cursor.getInt(1)]
                else null
            } ?: return@withContext false

            val (transferId, jobState) = owner
            if (!TransferHistoryPolicy.canDeleteHistory(jobState, transferId in runningTransferIds)) {
                return@withContext false
            }

            val deleted = db.delete(
                "transfer_files",
                "file_id=?",
                arrayOf(fileId.toString()),
            ) == 1
            db.delete(
                "transfer_jobs",
                "transfer_id=? AND NOT EXISTS(SELECT 1 FROM transfer_files WHERE transfer_id=?)",
                arrayOf(transferId.toString(), transferId.toString()),
            )
            db.setTransactionSuccessful()
            deleted
        } finally {
            db.endTransaction()
            bumpRevision()
        }
    }

    /**
     * Removes finished history plus stale rows that never reached a terminal state. Jobs a
     * worker still owns are kept, and no file on disk is touched.
     */
    suspend fun clearHistory(runningTransferIds: Set<UUID> = emptySet()): HistoryClearResult = withContext(Dispatchers.IO) {
        val db = database.writableDatabase
        db.beginTransaction()
        try {
            val removable = mutableListOf<String>()
            var kept = 0
            db.rawQuery("SELECT transfer_id,state FROM transfer_jobs", null).use { cursor ->
                while (cursor.moveToNext()) {
                    val transferId = UUID.fromString(cursor.getString(0))
                    val state = TransferState.entries[cursor.getInt(1)]
                    if (TransferHistoryPolicy.canDeleteHistory(state, transferId in runningTransferIds)) {
                        removable += transferId.toString()
                    } else {
                        kept++
                    }
                }
            }

            var deletedFiles = 0
            removable.forEach { transferId ->
                deletedFiles += db.delete("transfer_files", "transfer_id=?", arrayOf(transferId))
                db.delete("transfer_jobs", "transfer_id=?", arrayOf(transferId))
            }
            db.setTransactionSuccessful()
            HistoryClearResult(deletedFiles, removable.size, kept)
        } finally {
            db.endTransaction()
            bumpRevision()
        }
    }

    /**
     * Settles jobs that are too old to resume so they stop looking like active transfers.
     * Returns the number of rows that were normalized.
     */
    suspend fun settleExpiredJobs(now: Long = System.currentTimeMillis()): Int = withContext(Dispatchers.IO) {
        val db = database.writableDatabase
        val expired = recoverableJobsInternal(db).filter {
            TransferHistoryPolicy.isResumeExpired(it.state, it.updatedUtc, now)
        }
        expired.forEach { job ->
            val values = ContentValues().apply {
                put("state", TransferState.USER_ACTION_REQUIRED.ordinal)
                put("updated_utc", now)
                put("error_code", TransferHistoryPolicy.RESUME_EXPIRED_ERROR_CODE)
            }
            db.update("transfer_jobs", values, "transfer_id=?", arrayOf(job.transferId.toString()))
        }
        if (expired.isNotEmpty()) bumpRevision()
        expired.size
    }

    suspend fun files(transferId:UUID):List<TransferFileRecord> = withContext(Dispatchers.IO) {
        database.readableDatabase.query("transfer_files",null,"transfer_id=?",arrayOf(transferId.toString()),null,null,"rowid").use { c->buildList{while(c.moveToNext())add(c.toFile())} }
    }

    private fun Cursor.toJob()=TransferJobRecord(UUID.fromString(string("transfer_id")),string("source_device_id"),string("destination_device_id"),TransferDirection.entries[int("direction")],TransferState.entries[int("state")],long("created_utc"),long("updated_utc"),nullableLong("completed_utc"),nullableString("error_code"))
    private fun Cursor.toFile()=TransferFileRecord(UUID.fromString(string("transfer_id")),UUID.fromString(string("file_id")),string("relative_path"),string("source_uri"),nullableString("partial_uri"),nullableString("final_uri"),long("size"),long("modified_ticks"),nullableString("stable_source_id"),int("chunk_size"),long("received_offset"),long("committed_offset"),getBlob(getColumnIndexOrThrow("merkle_leaves")),TransferState.entries[int("state")],int("retry_count"),nullableString("error_code"),nullableString("display_path"))
}

class TrustedDeviceRepository(private val database:QuickSendDatabase) {
    suspend fun trust(deviceId:String,name:String,fingerprint:String)=withContext(Dispatchers.IO){val now=System.currentTimeMillis();val v=ContentValues().apply{put("device_id",deviceId);put("display_name",name);put("fingerprint",fingerprint);put("paired_utc",now);put("last_seen_utc",now);putNull("revoked_utc")};database.writableDatabase.insertWithOnConflict("trusted_devices",null,v,SQLiteDatabase.CONFLICT_REPLACE)}
    suspend fun isTrusted(fingerprint:String):Boolean=withContext(Dispatchers.IO){database.readableDatabase.rawQuery("SELECT EXISTS(SELECT 1 FROM trusted_devices WHERE fingerprint=? AND revoked_utc IS NULL)",arrayOf(fingerprint)).use{it.moveToFirst()&&it.getInt(0)==1}}
    suspend fun fingerprints():Set<String> = withContext(Dispatchers.IO){database.readableDatabase.rawQuery("SELECT fingerprint FROM trusted_devices WHERE revoked_utc IS NULL",null).use{c->buildSet{while(c.moveToNext())add(c.getString(0))}}}
    suspend fun revoke(deviceId:String)=withContext(Dispatchers.IO){val v=ContentValues().apply{put("revoked_utc",System.currentTimeMillis())};database.writableDatabase.update("trusted_devices",v,"device_id=?",arrayOf(deviceId))}
}

class SettingsRepository(private val database:QuickSendDatabase) {
    suspend fun get(key:String):String?=withContext(Dispatchers.IO){database.readableDatabase.rawQuery("SELECT value FROM settings WHERE key=?",arrayOf(key)).use{if(it.moveToFirst())it.getString(0) else null}}
    suspend fun set(key:String,value:String)=withContext(Dispatchers.IO){val v=ContentValues().apply{put("key",key);put("value",value)};database.writableDatabase.insertWithOnConflict("settings",null,v,SQLiteDatabase.CONFLICT_REPLACE)}
}

/**
 * Stores the QuickSend display name for this phone.
 *
 * Only the display name is persisted. `device.id` and the Keystore identity are never
 * rewritten here, so renaming keeps the fingerprint, trusted-device rows and the existing
 * SAS pairing intact.
 */
class DeviceNameRepository(
    private val settings: SettingsRepository,
    private val platformFallback: String,
) {
    private val _name = MutableStateFlow(DeviceNameRules.normalizeOrFallback(platformFallback, "QuickSend"))
    val name: StateFlow<String> = _name.asStateFlow()

    val current: String get() = _name.value

    suspend fun load(): String {
        val stored = runCatching { settings.get(SETTING_KEY) }.getOrNull()
        val resolved = DeviceNameRules.normalizeOrFallback(stored, platformFallback)
        _name.value = resolved
        return resolved
    }

    /** Returns the stored name, or an error message when the candidate is rejected. */
    suspend fun set(candidate: String?): DeviceNameUpdate {
        return when (val result = DeviceNameRules.normalize(candidate)) {
            is com.eslee.quicksend.engine.DeviceNameResult.Invalid -> DeviceNameUpdate.Rejected(result.message)
            is com.eslee.quicksend.engine.DeviceNameResult.Valid -> {
                settings.set(SETTING_KEY, result.name)
                _name.value = result.name
                DeviceNameUpdate.Accepted(result.name)
            }
        }
    }

    companion object {
        const val SETTING_KEY = "device.name"
    }
}

sealed interface DeviceNameUpdate {
    data class Accepted(val name: String) : DeviceNameUpdate
    data class Rejected(val message: String) : DeviceNameUpdate
}

private fun ContentValues.putNullable(key:String,value:String?) { if(value==null)putNull(key) else put(key,value) }
private fun ContentValues.putNullable(key:String,value:Long?) { if(value==null)putNull(key) else put(key,value) }
private fun Cursor.string(name:String)=getString(getColumnIndexOrThrow(name))
private fun Cursor.long(name:String)=getLong(getColumnIndexOrThrow(name))
private fun Cursor.int(name:String)=getInt(getColumnIndexOrThrow(name))
private fun Cursor.nullableString(name:String)=getColumnIndexOrThrow(name).let{if(isNull(it))null else getString(it)}
private fun Cursor.nullableLong(name:String)=getColumnIndexOrThrow(name).let{if(isNull(it))null else getLong(it)}
