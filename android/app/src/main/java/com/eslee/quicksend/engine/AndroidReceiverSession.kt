package com.eslee.quicksend.engine

import android.util.Base64
import com.eslee.quicksend.persistence.TransferFileRecord
import com.eslee.quicksend.persistence.TransferRepository
import com.eslee.quicksend.protocol.*
import com.eslee.quicksend.storage.DocumentTreeStore
import com.eslee.quicksend.storage.PartialDocument
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.security.MessageDigest

class AndroidReceiverSession(
    private var record:TransferFileRecord,
    private val repository:TransferRepository,
    private val documents:DocumentTreeStore,
    private val receiveRoot:String,
    private val checkpointPolicy:CheckpointPolicy=CheckpointPolicy(),
) : AutoCloseable {
    private val mutex=Mutex()
    private var partial:PartialDocument?=null
    private var merkle=MerkleAccumulator.restore(record.merkleLeaves)
    val receivedOffset:Long get()=record.receivedOffset

    suspend fun initialize()=mutex.withLock {
        if(partial!=null)return@withLock
        val opened=documents.openPartial(receiveRoot,record.relativePath,record.fileId)
        var safe=minOf(record.committedOffset,opened.length)
        if(safe!=record.size)safe-=safe%record.chunkSize
        var leaves=if(safe==0L)0 else ((safe+record.chunkSize-1)/record.chunkSize).toInt()
        if(leaves>merkle.leafCount){leaves=merkle.leafCount;safe=leaves.toLong()*record.chunkSize}
        if(leaves<merkle.leafCount)merkle=MerkleAccumulator.restore(record.merkleLeaves.copyOf(leaves*32))
        if(opened.length!=safe){opened.truncate(safe);opened.force()}
        record=record.copy(partialUri=opened.document.uri.toString(),receivedOffset=safe,committedOffset=safe,merkleLeaves=merkle.snapshot())
        repository.upsertFile(record)
        repository.checkpoint(record.fileId,safe,record.merkleLeaves)
        checkpointPolicy.restore(safe,System.currentTimeMillis())
        partial=opened
    }

    fun resumeInfo()=ResumeInfoMessage(record.transferId,record.fileId,record.committedOffset,merkle.leafCount,Base64.encodeToString(merkle.snapshot(),Base64.NO_WRAP))

    suspend fun receive(raw:ByteArray,now:Long=System.currentTimeMillis()):ReceiveResult=mutex.withLock {
        val chunk=ChunkPayload(raw)
        if(chunk.fileId!=record.fileId)throw ProtocolException("Chunk belongs to another file")
        if(chunk.offset<0||chunk.offset+chunk.length>record.size)throw ProtocolException("Chunk range exceeds file")
        if(chunk.offset<record.receivedOffset){
            if(chunk.offset+chunk.length<=record.committedOffset)return@withLock ReceiveResult(ChunkAckMessage(record.fileId,chunk.offset,chunk.length,record.receivedOffset),null,true)
            throw ProtocolException("Chunk overlaps uncommitted data")
        }
        if(chunk.offset!=record.receivedOffset)throw ProtocolException("Chunk gap")
        if(!chunk.verify())throw ChunkIntegrityException(chunk.offset)
        val data=chunk.data()
        partial?.write(chunk.offset,data)?:error("Receiver is not initialized")
        merkle.addChunk(data)
        record=record.copy(receivedOffset=chunk.offset+chunk.length,merkleLeaves=merkle.snapshot())
        var checkpoint:CheckpointMessage?=null
        if(checkpointPolicy.due(record.receivedOffset,now)||record.receivedOffset==record.size){
            partial!!.force()
            repository.checkpoint(record.fileId,record.receivedOffset,record.merkleLeaves)
            record=record.copy(committedOffset=record.receivedOffset)
            checkpointPolicy.committed(record.committedOffset,now)
            checkpoint=CheckpointMessage(record.fileId,record.committedOffset,merkle.leafCount,Base64.encodeToString(record.merkleLeaves,Base64.NO_WRAP))
        }
        ReceiveResult(ChunkAckMessage(record.fileId,chunk.offset,chunk.length,record.receivedOffset),checkpoint,false)
    }

    suspend fun complete(message:FileCompleteMessage):String=mutex.withLock {
        if(message.fileId!=record.fileId||message.size!=record.size||record.receivedOffset!=record.size)throw ProtocolException("Premature or mismatched completion")
        if(record.committedOffset!=record.size){partial!!.force();repository.checkpoint(record.fileId,record.size,merkle.snapshot());record=record.copy(committedOffset=record.size)}
        val root=merkle.root()
        if(message.leafCount!=merkle.leafCount||!MessageDigest.isEqual(root,Base64.decode(message.merkleRootBase64,Base64.NO_WRAP)))throw FileIntegrityException()
        val finalized=documents.finalize(partial!!)
        partial=null
        val displayPath=runCatching{documents.displayPathForFile(receiveRoot,record.relativePath,finalized.name)}.getOrNull()
        repository.complete(record.fileId,finalized.uri.toString(),displayPath,root)
        record=record.copy(finalUri=finalized.uri.toString(),displayPath=displayPath,state=TransferState.COMPLETED)
        displayPath ?: finalized.name
    }

    override fun close(){partial?.close();partial=null}
}

data class ReceiveResult(val ack:ChunkAckMessage,val checkpoint:CheckpointMessage?,val duplicate:Boolean)
class ChunkIntegrityException(val offset:Long):java.io.IOException("Chunk integrity failed at $offset")
class FileIntegrityException:java.io.IOException("Final file integrity failed")
