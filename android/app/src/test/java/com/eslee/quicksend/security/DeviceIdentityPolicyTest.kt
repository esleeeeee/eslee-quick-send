package com.eslee.quicksend.security

import android.security.keystore.KeyProperties
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class DeviceIdentityPolicyTest {
    @Test
    fun tlsIdentityAuthorizesPreHashedAndSha256Signatures() {
        val digests = DeviceIdentityService.TLS_IDENTITY_DIGESTS

        assertArrayEquals(
            arrayOf(KeyProperties.DIGEST_NONE, KeyProperties.DIGEST_SHA256),
            digests,
        )
        assertTrue(digests.contains(KeyProperties.DIGEST_NONE))
    }
}
