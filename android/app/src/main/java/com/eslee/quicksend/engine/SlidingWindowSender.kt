package com.eslee.quicksend.engine

import android.content.ContentResolver
import android.net.Uri
import android.provider.OpenableColumns
import android.provider.DocumentsContract
import android.util.Base64
import com.eslee.quicksend.protocol.*
import kotlinx.coroutines.*
import kotlinx.coroutines.sync.Semaphore
import org.json.JSONObject
import java.io.FileInputStream
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong

data class SourceMetadata(val size:Long,val modifiedTicks:Long)
data class SenderProgress(val safeOffset:Long,val receivedOffset:Long,val inFlight:Int)

class SlidingWindowSender(
    private val resolver:ContentResolver,
    private val reader:ProtocolReader,
    private val writer:ProtocolWriter,
    private val chunkSize:Int=ProtocolConstants.DEFAULT_CHUNK_SIZE,
    windowChunks:Int=ProtocolConstants.DEFAULT_WINDOW_CHUNKS,
) {
    private val permits=Semaphore(windowChunks)
    private val inFlight=ConcurrentHashMap<Long,InFlightChunk>()
    private val safe=AtomicLong()
    private val received=AtomicLong()
    private val finished=AtomicBoolean()
    var onProgress:((SenderProgress)->Unit)?=null
    var onChunkSent:((Long,Int)->Unit)?=null
    var onChunkAcknowledged:((Long,Int,Long)->Unit)?=null
    var onCheckpoint:((CheckpointMessage)->Unit)?=null

    suspend fun send(source:Uri,start:FileStartMessage,resume:ResumeInfoMessage,expected:SourceMetadata):FileCompleteMessage=coroutineScope {
        require(start.fileId==resume.fileId&&start.transferId==resume.transferId)
        if(resume.committedOffset<0||resume.committedOffset>start.size||(resume.committedOffset!=start.size&&resume.committedOffset%chunkSize!=0L))throw ProtocolException("Invalid resume offset")
        ensureUnchanged(source,expected)
        val merkle=MerkleAccumulator.restore(Base64.decode(resume.merkleSnapshotBase64,Base64.NO_WRAP))
        val expectedLeaves=if(resume.committedOffset==0L)0 else ((resume.committedOffset+chunkSize-1)/chunkSize).toInt()
        if(merkle.leafCount!=resume.committedLeaves||merkle.leafCount!=expectedLeaves)throw ProtocolException("Invalid resume Merkle state")
        safe.set(resume.committedOffset);received.set(resume.committedOffset)
        val drained=CompletableDeferred<Unit>()
        val heartbeatJob=launch(Dispatchers.IO){while(isActive){delay(20_000);writer.writeControl(MessageType.PING,JSONObject().put("monotonicTicks",System.nanoTime()))}}
        val ackJob=launch(Dispatchers.IO){
            while(isActive){
                val frame=reader.read()
                when(frame.header.type){
                    MessageType.CHUNK_ACK->{val ack=ChunkAckMessage.parse(frame.json());if(ack.fileId!=start.fileId)throw ProtocolException("ACK file mismatch");if(inFlight.remove(ack.offset)!=null)permits.release();received.updateAndGet{maxOf(it,ack.receivedOffset)};onChunkAcknowledged?.invoke(ack.offset,ack.length,ack.receivedOffset);onProgress?.invoke(SenderProgress(safe.get(),received.get(),inFlight.size));if(finished.get()&&inFlight.isEmpty()){drained.complete(Unit);return@launch}}
                    MessageType.CHECKPOINT->{val cp=CheckpointMessage.parse(frame.json());if(cp.committedOffset<safe.get())throw ProtocolException("Checkpoint moved backwards");safe.set(cp.committedOffset);onCheckpoint?.invoke(cp);onProgress?.invoke(SenderProgress(safe.get(),received.get(),inFlight.size))}
                    MessageType.PING->writer.writeControl(MessageType.PONG,frame.json(),FrameFlags.RESPONSE)
                    MessageType.PONG->Unit
                    MessageType.ERROR->{
                        val error=frame.json()
                        val offset=error.optLong("offset",-1)
                        val pending=inFlight[offset]
                        if(error.optString("code")=="CHUNK_RETRY"&&pending!=null&&pending.retries++<2){
                            writer.writeChunk(start.fileId,offset,pending.data,pending.data.size,FrameFlags.RETRY)
                        }else throw java.io.IOException(error.optString("userMessage","Remote transfer failed"))
                    }
                    else->throw ProtocolException("Unexpected ${frame.header.type} while sending")
                }
            }
        }
        try {
            val descriptor=resolver.openFileDescriptor(source,"r")?:error("원본 파일을 열 수 없습니다")
            descriptor.use { pfd->FileInputStream(pfd.fileDescriptor).channel.use { channel->
                channel.position(resume.committedOffset)
                var offset=resume.committedOffset
                while(offset<start.size){
                    ensureUnchanged(source,expected)
                    permits.acquire()
                    val wanted=minOf(chunkSize.toLong(),start.size-offset).toInt()
                    val buffer=ByteArray(wanted)
                    var read=0
                    while(read<wanted){val n=channel.read(java.nio.ByteBuffer.wrap(buffer,read,wanted-read));if(n<0)throw java.io.EOFException("Source ended early");read+=n}
                    merkle.addChunk(buffer)
                    inFlight[offset]=InFlightChunk(buffer)
                    try{writer.writeChunk(start.fileId,offset,buffer,wanted);onChunkSent?.invoke(offset,wanted)}catch(t:Throwable){inFlight.remove(offset);permits.release();throw t}
                    offset+=wanted
                }
            }}
            finished.set(true)
            if(inFlight.isEmpty())drained.complete(Unit)
            drained.await()
            ensureUnchanged(source,expected)
            FileCompleteMessage(start.fileId,start.size,merkle.leafCount,Base64.encodeToString(merkle.root(),Base64.NO_WRAP)).also{writer.writeControl(MessageType.FILE_COMPLETE,it.json(),FrameFlags.FINAL)}
        } finally {heartbeatJob.cancelAndJoin();ackJob.cancelAndJoin()}
    }

    private fun ensureUnchanged(uri:Uri,expected:SourceMetadata){val current=queryMetadata(resolver,uri);if(current.size!=expected.size||(expected.modifiedTicks>0&&current.modifiedTicks>0&&current.modifiedTicks!=expected.modifiedTicks))throw SourceChangedException()}

    private data class InFlightChunk(val data:ByteArray,var retries:Int=0)

    companion object {
        fun queryMetadata(resolver:ContentResolver,uri:Uri):SourceMetadata {
            var size=-1L;var modified=0L
            runCatching{resolver.query(uri,arrayOf(OpenableColumns.SIZE,DocumentsContract.Document.COLUMN_LAST_MODIFIED),null,null,null)?.use{if(it.moveToFirst()){if(!it.isNull(0))size=it.getLong(0);if(!it.isNull(1))modified=it.getLong(1)}}}
            if(size<0)resolver.openAssetFileDescriptor(uri,"r")?.use{size=it.length}
            return SourceMetadata(size,modified)
        }
    }
}

class SourceChangedException:java.io.IOException("원본 파일이 전송 도중 변경되었습니다")
