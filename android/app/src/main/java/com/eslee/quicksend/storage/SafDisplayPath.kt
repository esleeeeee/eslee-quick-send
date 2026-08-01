package com.eslee.quicksend.storage

import android.content.Context
import android.net.Uri
import android.provider.DocumentsContract
import androidx.documentfile.provider.DocumentFile

/**
 * Builds a human-readable location from a Storage Access Framework URI.
 *
 * Android does not expose a real filesystem path for a SAF tree, so nothing here invents
 * an absolute path. The result is a display path assembled from the volume label, the
 * document-id segments and the final file name, and it never contains a raw content URI.
 */
object SafDisplayPath {
    const val UNKNOWN_FOLDER = "선택한 폴더"

    private val VOLUME_LABELS = mapOf(
        "primary" to "내장 저장소",
        "home" to "문서",
        "downloads" to "다운로드",
    )

    private val SEGMENT_LABELS = mapOf(
        "Download" to "다운로드",
        "Downloads" to "다운로드",
        "Documents" to "문서",
        "Pictures" to "사진",
        "Movies" to "동영상",
        "Music" to "음악",
        "Android" to "Android",
        "DCIM" to "DCIM",
    )

    /** Display path of the chosen receive folder, for example `내장 저장소/다운로드/퀵쉐어`. */
    fun forTree(context: Context, treeUri: String?): String {
        if (treeUri.isNullOrBlank()) return UNKNOWN_FOLDER
        val uri = runCatching { Uri.parse(treeUri) }.getOrNull() ?: return UNKNOWN_FOLDER
        fromDocumentId(runCatching { DocumentsContract.getTreeDocumentId(uri) }.getOrNull())
            ?.let { return it }
        // A provider without parseable document ids still exposes a display name.
        runCatching { DocumentFile.fromTreeUri(context, uri)?.name }
            .getOrNull()
            ?.takeIf { it.isNotBlank() }
            ?.let { return it }
        return UNKNOWN_FOLDER
    }

    /**
     * Display path of a received file: the receive folder, any sub-folders carried by the
     * sender's relative path, and the name the file was actually saved under.
     */
    fun forReceivedFile(treeDisplay: String, relativePath: String, finalName: String): String {
        val parents = relativePath.replace('\\', '/')
            .split('/')
            .filter { it.isNotBlank() }
            .dropLast(1)
        return (listOf(treeDisplay) + parents + finalName).joinToString("/")
    }

    /**
     * Best-effort location for a legacy row that only stored the document URI. Returns the
     * document's display name, never the URI itself.
     */
    fun fromDocumentUri(context: Context, documentUri: String?): String? {
        if (documentUri.isNullOrBlank()) return null
        val uri = runCatching { Uri.parse(documentUri) }.getOrNull() ?: return null
        fromDocumentId(runCatching { DocumentsContract.getDocumentId(uri) }.getOrNull())
            ?.let { return it }
        return runCatching { DocumentFile.fromSingleUri(context, uri)?.name }
            .getOrNull()
            ?.takeIf { it.isNotBlank() }
    }

    private fun fromDocumentId(documentId: String?): String? {
        if (documentId.isNullOrBlank()) return null
        val separator = documentId.indexOf(':')
        if (separator < 0) return null
        val volume = documentId.substring(0, separator)
        val path = documentId.substring(separator + 1)
        val segments = path.split('/').filter { it.isNotBlank() }.map { SEGMENT_LABELS[it] ?: it }
        val label = VOLUME_LABELS[volume.lowercase()] ?: if (volume.isBlank()) null else "외부 저장소"
        val parts = listOfNotNull(label) + segments
        return parts.takeIf { it.isNotEmpty() }?.joinToString("/")
    }
}
