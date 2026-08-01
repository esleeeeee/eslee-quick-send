package com.eslee.quicksend.protocol

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.util.UUID
import java.util.concurrent.atomic.AtomicLong

object ProtocolConstants {
    const val MAGIC = 0x45535131
    const val HEADER_SIZE = 32
    const val VERSION = 1
    const val DEFAULT_PORT = 41231
    const val DEFAULT_CHUNK_SIZE = 8 * 1024 * 1024
    const val DEFAULT_WINDOW_CHUNKS = 8
    const val CHECKPOINT_BYTES = 64L * 1024 * 1024
    const val CHECKPOINT_MILLIS = 1_000L
    const val MAX_CONTROL_PAYLOAD = 4 * 1024 * 1024
    const val MAX_CHUNK_DATA = 64 * 1024 * 1024
    const val CHUNK_METADATA_SIZE = 60
    const val SERVICE_TYPE = "_eslee-quicksend._tcp."
}

enum class MessageType(val wire: Int) {
    HELLO(1), PAIR_REQUEST(2), PAIR_ACCEPT(3), JOB_MANIFEST(4), FILE_START(5), RESUME_INFO(6),
    CHUNK_DATA(7), CHUNK_ACK(8), CHECKPOINT(9), FILE_COMPLETE(10), FILE_VERIFY(11), JOB_COMPLETE(12),
    PAUSE(13), RESUME(14), CANCEL(15), PING(16), PONG(17), ERROR(18);

    companion object {
        fun fromWire(value: Int): MessageType = entries.firstOrNull { it.wire == value }
            ?: throw ProtocolException("Unknown message type $value")
    }
}

object FrameFlags {
    const val NONE = 0
    const val RESPONSE = 1
    const val FINAL = 2
    const val RETRY = 4
}

data class FrameHeader(
    val type: MessageType,
    val flags: Int,
    val sequence: Long,
    val payloadLength: Long,
) {
    fun encode(): ByteArray = ByteBuffer.allocate(ProtocolConstants.HEADER_SIZE).order(ByteOrder.BIG_ENDIAN)
        .putInt(ProtocolConstants.MAGIC)
        .putShort(ProtocolConstants.HEADER_SIZE.toShort())
        .putShort(ProtocolConstants.VERSION.toShort())
        .putShort(type.wire.toShort())
        .putShort(flags.toShort())
        .putLong(sequence)
        .putLong(payloadLength)
        .putInt(0)
        .array()

    companion object {
        fun decode(raw: ByteArray): FrameHeader {
            if (raw.size != ProtocolConstants.HEADER_SIZE) throw ProtocolException("Truncated frame header")
            val buffer = ByteBuffer.wrap(raw).order(ByteOrder.BIG_ENDIAN)
            if (buffer.int != ProtocolConstants.MAGIC) throw ProtocolException("Invalid frame magic")
            if (buffer.short.toInt() and 0xffff != ProtocolConstants.HEADER_SIZE) throw ProtocolException("Unsupported header size")
            val version = buffer.short.toInt() and 0xffff
            if (version != ProtocolConstants.VERSION) throw ProtocolException("Unsupported protocol version $version")
            val type = MessageType.fromWire(buffer.short.toInt() and 0xffff)
            val flags = buffer.short.toInt() and 0xffff
            val sequence = buffer.long
            val length = buffer.long
            if (buffer.int != 0) throw ProtocolException("Reserved header field must be zero")
            if (sequence <= 0 || length < 0) throw ProtocolException("Invalid sequence or length")
            return FrameHeader(type, flags, sequence, length)
        }
    }
}

data class InboundFrame(val header: FrameHeader, val payload: ByteArray)

class ProtocolReader(private val input: InputStream) {
    private var lastSequence = 0L

    suspend fun read(): InboundFrame = withContext(Dispatchers.IO) {
        val header = FrameHeader.decode(input.readExactly(ProtocolConstants.HEADER_SIZE))
        if (header.sequence <= lastSequence) throw ProtocolException("Duplicate or out-of-order frame")
        lastSequence = header.sequence
        val maximum = if (header.type == MessageType.CHUNK_DATA) {
            ProtocolConstants.MAX_CHUNK_DATA.toLong() + ProtocolConstants.CHUNK_METADATA_SIZE
        } else ProtocolConstants.MAX_CONTROL_PAYLOAD.toLong()
        if (header.payloadLength > maximum || header.payloadLength > Int.MAX_VALUE) {
            throw ProtocolException("Payload exceeds protocol limit")
        }
        val payload = input.readExactly(header.payloadLength.toInt())
        if (header.type == MessageType.CHUNK_DATA) ChunkPayload(payload)
        InboundFrame(header, payload)
    }
}

class ProtocolWriter(private val output: OutputStream) {
    private val mutex = Mutex()
    private val sequence = AtomicLong()

    suspend fun writeControl(type: MessageType, json: JSONObject, flags: Int = FrameFlags.NONE) {
        require(type != MessageType.CHUNK_DATA)
        val payload = json.toString().toByteArray(Charsets.UTF_8)
        if (payload.size > ProtocolConstants.MAX_CONTROL_PAYLOAD) throw ProtocolException("Control payload is too large")
        mutex.withLock { writeFrame(type, flags, payload) }
    }

    suspend fun writeChunk(fileId: UUID, offset: Long, data: ByteArray, length: Int, flags: Int = FrameFlags.NONE) {
        require(offset >= 0 && length in 0..data.size && length <= ProtocolConstants.MAX_CHUNK_DATA)
        val metadata = ByteBuffer.allocate(ProtocolConstants.CHUNK_METADATA_SIZE).order(ByteOrder.BIG_ENDIAN)
            .putLong(fileId.mostSignificantBits).putLong(fileId.leastSignificantBits)
            .putLong(offset).putInt(length)
            .put(MessageDigest.getInstance("SHA-256").digest(data.copyOfRange(0, length)))
            .array()
        mutex.withLock {
            withContext(Dispatchers.IO) {
                output.write(FrameHeader(MessageType.CHUNK_DATA, flags, sequence.incrementAndGet(), (metadata.size + length).toLong()).encode())
                output.write(metadata)
                output.write(data, 0, length)
                output.flush()
            }
        }
    }

    private suspend fun writeFrame(type: MessageType, flags: Int, payload: ByteArray) = withContext(Dispatchers.IO) {
        output.write(FrameHeader(type, flags, sequence.incrementAndGet(), payload.size.toLong()).encode())
        output.write(payload)
        output.flush()
    }
}

class ChunkPayload(private val raw: ByteArray) {
    val fileId: UUID
    val offset: Long
    val length: Int
    val expectedHash: ByteArray
    val dataOffset = ProtocolConstants.CHUNK_METADATA_SIZE

    init {
        if (raw.size < ProtocolConstants.CHUNK_METADATA_SIZE) throw ProtocolException("Truncated chunk")
        val buffer = ByteBuffer.wrap(raw).order(ByteOrder.BIG_ENDIAN)
        fileId = UUID(buffer.long, buffer.long)
        offset = buffer.long
        length = buffer.int
        expectedHash = ByteArray(32).also(buffer::get)
        if (length < 0 || raw.size != ProtocolConstants.CHUNK_METADATA_SIZE + length) throw ProtocolException("Invalid chunk length")
    }

    fun data(): ByteArray = raw.copyOfRange(dataOffset, raw.size)
    fun verify(): Boolean {
        val digest = MessageDigest.getInstance("SHA-256")
        digest.update(raw, dataOffset, length)
        return MessageDigest.isEqual(expectedHash, digest.digest())
    }
}

class ProtocolException(message: String) : java.io.IOException(message)

private fun InputStream.readExactly(length: Int): ByteArray {
    val result = ByteArray(length)
    var offset = 0
    while (offset < length) {
        val read = read(result, offset, length - offset)
        if (read < 0) throw EOFException("Connection ended with ${length - offset} bytes remaining")
        offset += read
    }
    return result
}

fun JSONObject.stringList(name: String): List<String> {
    val array = getJSONArray(name)
    return List(array.length()) { array.getString(it) }
}

fun jsonArray(values: Iterable<String>): JSONArray = JSONArray().apply { values.forEach(::put) }
