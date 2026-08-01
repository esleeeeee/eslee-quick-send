package com.eslee.quicksend.storage

import android.content.Context
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35], manifest = Config.NONE)
class SafDisplayPathTest {
    private lateinit var context: Context

    @Before
    fun setUp() {
        context = RuntimeEnvironment.getApplication()
    }

    @Test
    fun treeUriBecomesAReadableLocation() {
        val display = SafDisplayPath.forTree(context, DOWNLOAD_TREE)

        assertEquals("내장 저장소/다운로드/퀵쉐어", display)
        assertNoRawUri(display)
    }

    @Test
    fun receivedFileLocationIncludesTheFolderAndTheSavedFileName() {
        val display = SafDisplayPath.forReceivedFile(
            SafDisplayPath.forTree(context, DOWNLOAD_TREE),
            "사진/휴가/photo.jpg",
            "photo (1).jpg",
        )

        assertEquals("내장 저장소/다운로드/퀵쉐어/사진/휴가/photo (1).jpg", display)
        assertNoRawUri(display)
    }

    @Test
    fun unknownOrMissingTreeFallsBackWithoutLeakingTheUri() {
        assertEquals(SafDisplayPath.UNKNOWN_FOLDER, SafDisplayPath.forTree(context, null))
        assertEquals(SafDisplayPath.UNKNOWN_FOLDER, SafDisplayPath.forTree(context, ""))

        val opaque = SafDisplayPath.forTree(context, "content://com.example.provider/tree/opaque")
        assertNoRawUri(opaque)
    }

    @Test
    fun legacyDocumentUriResolvesToAReadableNameNotTheUri() {
        val display = SafDisplayPath.fromDocumentUri(
            context,
            "content://com.android.externalstorage.documents/tree/primary%3ADownload%2F%ED%80%B5%EC%89%90%EC%96%B4" +
                "/document/primary%3ADownload%2F%ED%80%B5%EC%89%90%EC%96%B4%2Fphoto.jpg",
        )

        assertEquals("내장 저장소/다운로드/퀵쉐어/photo.jpg", display)
        assertNoRawUri(display!!)
    }

    private fun assertNoRawUri(value: String) {
        assertFalse("display path leaked a content URI: $value", value.contains("content://"))
        assertFalse("display path leaked a provider authority: $value", value.contains("com.android.externalstorage"))
        assertTrue(value.isNotBlank())
    }

    private companion object {
        const val DOWNLOAD_TREE =
            "content://com.android.externalstorage.documents/tree/primary%3ADownload%2F%ED%80%B5%EC%89%90%EC%96%B4"
    }
}
