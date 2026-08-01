package com.eslee.quicksend.security

import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import com.eslee.quicksend.diagnostics.DiagnosticLog
import com.eslee.quicksend.persistence.SettingsRepository
import com.eslee.quicksend.persistence.TrustedDeviceRepository
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.math.BigInteger
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.KeyStoreException
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.X509Certificate
import java.util.Date
import java.util.UUID
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLEngine
import javax.net.ssl.TrustManager
import javax.net.ssl.X509ExtendedTrustManager
import org.json.JSONObject

class DeviceIdentityService(private val settings:SettingsRepository,private val log:DiagnosticLog) {
    companion object {
        private const val ALIAS="eslee-quicksend-device-v2"
        internal val TLS_IDENTITY_DIGESTS=arrayOf(
            KeyProperties.DIGEST_NONE,
            KeyProperties.DIGEST_SHA256,
        )
    }
    @Volatile private var cached:DeviceIdentity?=null

    suspend fun getOrCreate():DeviceIdentity=withContext(Dispatchers.IO) {
        cached?.let{return@withContext it}
        var id=settings.get("device.id")
        if(id==null){id=UUID.randomUUID().toString().replace("-","");settings.set("device.id",id)}
        try {
            loadIdentity(id)
        } catch (first: Exception) {
            val keyStore=KeyStore.getInstance("AndroidKeyStore").apply{load(null)}
            if(!keyStore.containsAlias(ALIAS))throw first
            log.warn(
                "android.identity.key.recreate",
                first,
                JSONObject().put("startupStage","identity.load"),
            )
            keyStore.deleteEntry(ALIAS)
            loadIdentity(id)
        }
    }

    private fun loadIdentity(deviceId:String):DeviceIdentity {
        val keyStore=KeyStore.getInstance("AndroidKeyStore").apply{load(null)}
        if(!keyStore.containsAlias(ALIAS))generateKeyPair()
        val entry=keyStore.getEntry(ALIAS,null) as? KeyStore.PrivateKeyEntry
            ?: throw KeyStoreException("QuickSend key alias is not a private-key entry")
        val certificate=entry.certificate as? X509Certificate
            ?: throw KeyStoreException("QuickSend key alias has no X.509 certificate")
        return DeviceIdentity(deviceId,certificate,fingerprint(certificate),keyStore).also{cached=it}
    }

    private fun generateKeyPair() {
        val now=System.currentTimeMillis()
        val serial=BigInteger(160,SecureRandom()).max(BigInteger.ONE)
        val spec=KeyGenParameterSpec.Builder(ALIAS,KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY)
            .setAlgorithmParameterSpec(java.security.spec.ECGenParameterSpec("secp256r1"))
            // TLS stacks sign a digest that they computed themselves, so the
            // Android Keystore key must authorize NONE in addition to SHA-256.
            .setDigests(*TLS_IDENTITY_DIGESTS)
            .setCertificateSubject(javax.security.auth.x500.X500Principal("CN=eslee QuickSend Device"))
            .setCertificateSerialNumber(serial)
            .setCertificateNotBefore(Date(now-300_000)).setCertificateNotAfter(Date(now+10L*365*86400_000))
            .build()
        KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC,"AndroidKeyStore")
            .apply{initialize(spec)}
            .generateKeyPair()
    }

    fun fingerprint(certificate:X509Certificate):String=MessageDigest.getInstance("SHA-256").digest(certificate.publicKey.encoded).joinToString(""){"%02X".format(it)}

    fun pairingCode(local:String,remote:String,nonce:String):String {
        val joined=listOf(local,remote).sorted().joinToString("|")+"|$nonce"
        val hash=MessageDigest.getInstance("SHA-256").digest(joined.toByteArray())
        val number=(java.nio.ByteBuffer.wrap(hash).int.toLong() and 0xffffffffL)%1_000_000
        return "%03d %03d".format(number/1000,number%1000)
    }
}

data class DeviceIdentity(val deviceId:String,val certificate:X509Certificate,val fingerprint:String,val keyStore:KeyStore)

class TlsContextFactory(private val identityService:DeviceIdentityService,private val trust:TrustedDeviceRepository) {
    suspend fun create(pairingOnly:Boolean):SSLContext {
        val identity=identityService.getOrCreate()
        val trusted=trust.fingerprints()
        val kmf=KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm()).apply{init(identity.keyStore,null)}
        val manager=object:X509ExtendedTrustManager(){
            private fun check(chain:Array<out X509Certificate>?){require(!chain.isNullOrEmpty()){ "Peer certificate missing" };if(!pairingOnly && identityService.fingerprint(chain[0]) !in trusted)throw java.security.cert.CertificateException("Untrusted QuickSend device")}
            override fun checkClientTrusted(c:Array<out X509Certificate>?,a:String?){check(c)};override fun checkServerTrusted(c:Array<out X509Certificate>?,a:String?){check(c)}
            override fun checkClientTrusted(c:Array<out X509Certificate>?,a:String?,s:java.net.Socket?){check(c)};override fun checkServerTrusted(c:Array<out X509Certificate>?,a:String?,s:java.net.Socket?){check(c)}
            override fun checkClientTrusted(c:Array<out X509Certificate>?,a:String?,e:SSLEngine?){check(c)};override fun checkServerTrusted(c:Array<out X509Certificate>?,a:String?,e:SSLEngine?){check(c)}
            override fun getAcceptedIssuers():Array<X509Certificate> = emptyArray()
        }
        return SSLContext.getInstance("TLS").apply{init(kmf.keyManagers,arrayOf<TrustManager>(manager),SecureRandom())}
    }
}
