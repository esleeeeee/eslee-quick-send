package com.eslee.quicksend.storage

import android.content.Context
import android.net.Uri
import android.provider.DocumentsContract
import androidx.documentfile.provider.DocumentFile
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.nio.channels.FileChannel
import java.util.UUID

class DocumentTreeStore(private val context:Context) {
    suspend fun openPartial(rootUri:String,relativePath:String,fileId:UUID):PartialDocument=withContext(Dispatchers.IO) {
        val segments=safeSegments(relativePath)
        require(segments.isNotEmpty())
        var directory=DocumentFile.fromTreeUri(context,Uri.parse(rootUri)) ?: error("수신 폴더에 접근할 수 없습니다")
        for(segment in segments.dropLast(1)) directory=directory.findFile(segment)?.takeIf{it.isDirectory} ?: directory.createDirectory(segment) ?: error("폴더를 만들 수 없습니다: $segment")
        val partialName=".${segments.last()}.${fileId.toString().take(8)}.esqpart"
        val document=directory.findFile(partialName) ?: directory.createFile("application/octet-stream",partialName) ?: error("부분 파일을 만들 수 없습니다")
        val descriptor=context.contentResolver.openFileDescriptor(document.uri,"rw") ?: error("부분 파일을 열 수 없습니다")
        PartialDocument(document,directory,segments.last(),java.io.FileOutputStream(descriptor.fileDescriptor).channel,descriptor)
    }

    suspend fun finalize(partial:PartialDocument):FinalizedDocument=withContext(Dispatchers.IO) {
        partial.force()
        partial.close()
        var candidate=partial.finalName
        val base=candidate.substringBeforeLast('.',candidate)
        val extension=candidate.substringAfterLast('.',"").let{if(it.isEmpty())"" else ".$it"}
        var index=1
        while(partial.parent.findFile(candidate)!=null){candidate="$base ($index)$extension";index++}
        check(partial.document.renameTo(candidate)) { "최종 파일명으로 확정하지 못했습니다" }
        // The saved name can differ from the sender's name after collision handling, so the
        // display path is built from the name the file actually got.
        FinalizedDocument(partial.document.uri,candidate)
    }

    /** Human-readable location of the receive folder; never a raw content URI. */
    fun displayPathForTree(rootUri:String?):String = SafDisplayPath.forTree(context,rootUri)

    /** Human-readable location of a received file inside the receive folder. */
    fun displayPathForFile(rootUri:String?,relativePath:String,finalName:String):String =
        SafDisplayPath.forReceivedFile(displayPathForTree(rootUri),relativePath,finalName)

    fun availableBytes(rootUri:String):Long {
        val uri=Uri.parse(rootUri)
        return try {
            val pfd=context.contentResolver.openFileDescriptor(uri,"r") ?: return -1
            pfd.use { android.system.Os.fstatvfs(it.fileDescriptor).f_bavail * android.system.Os.fstatvfs(it.fileDescriptor).f_bsize }
        } catch(_:Exception){-1}
    }

    fun exists(documentUri:String):Boolean =
        runCatching {
            DocumentFile.fromSingleUri(context,Uri.parse(documentUri))?.exists() == true
        }.getOrDefault(false)

    private fun safeSegments(relativePath:String):List<String> {
        require(!relativePath.startsWith('/') && !relativePath.startsWith('\\')) { "Absolute remote path rejected" }
        val segments=relativePath.replace('\\','/').split('/').filter{it.isNotBlank()}
        require(segments.none{it=="."||it==".."||it.contains('\u0000')}) { "Unsafe remote path rejected" }
        return segments
    }
}

data class FinalizedDocument(val uri:Uri,val name:String)

class PartialDocument(
    val document:DocumentFile,
    val parent:DocumentFile,
    val finalName:String,
    private val channel:FileChannel,
    private val descriptor:android.os.ParcelFileDescriptor,
) : AutoCloseable {
    val length:Long get()=channel.size()
    fun truncate(size:Long){channel.truncate(size);channel.position(size)}
    fun write(offset:Long,data:ByteArray){channel.position(offset);var buffer=java.nio.ByteBuffer.wrap(data);while(buffer.hasRemaining())channel.write(buffer)}
    fun force(){channel.force(true)}
    override fun close(){channel.close();descriptor.close()}
}
