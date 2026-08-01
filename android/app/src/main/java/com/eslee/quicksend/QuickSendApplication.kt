package com.eslee.quicksend

import android.app.Application
import android.content.Context
import android.os.Process
import com.eslee.quicksend.diagnostics.DiagnosticLog
import com.eslee.quicksend.discovery.NsdDiscoveryService
import com.eslee.quicksend.persistence.DeviceNameRepository
import com.eslee.quicksend.persistence.QuickSendDatabase
import com.eslee.quicksend.persistence.SettingsRepository
import com.eslee.quicksend.persistence.TransferRepository
import com.eslee.quicksend.persistence.TrustedDeviceRepository
import com.eslee.quicksend.security.DeviceIdentityService
import com.eslee.quicksend.security.TlsContextFactory
import com.eslee.quicksend.storage.DocumentTreeStore
import org.json.JSONObject
import kotlin.system.exitProcess

class QuickSendApplication:Application() {
    lateinit var log:DiagnosticLog
        private set
    @Volatile var services:AppServices?=null
        private set
    @Volatile var startupFailure:Throwable?=null
        private set

    override fun onCreate(){
        super.onCreate()
        log=DiagnosticLog(this)
        installUncaughtExceptionLogger()
        log.info("android.process.start",JSONObject().put("processId",Process.myPid()))
        log.info("android.application.onCreate.begin")
        try {
            val db=QuickSendDatabase(this)
            val settings=SettingsRepository(db)
            val trust=TrustedDeviceRepository(db)
            val identity=DeviceIdentityService(settings,log)
            services=AppServices(
                this,db,settings,trust,TransferRepository(db),identity,TlsContextFactory(identity,trust),
                NsdDiscoveryService(this,log),DocumentTreeStore(this),
                DeviceNameRepository(settings,android.os.Build.MODEL),log,
            )
            log.info("android.application.onCreate.complete",JSONObject().put("servicesReady",true))
        } catch(error:Throwable) {
            startupFailure=error
            log.error("android.application.onCreate.failed",error,JSONObject().put("startupStage","application.services"))
            log.info("android.application.onCreate.complete",JSONObject().put("servicesReady",false))
        }
    }

    private fun installUncaughtExceptionLogger(){
        val prior=Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler{thread,error->
            log.error(
                "android.uncaught.exception",error,
                JSONObject().put("startupStage","uncaught").put("thread",thread.name),
            )
            if(prior!=null)prior.uncaughtException(thread,error)
            else {
                Process.killProcess(Process.myPid())
                exitProcess(10)
            }
        }
    }
}

data class AppServices(
    val context:Context,
    val database:QuickSendDatabase,
    val settings:SettingsRepository,
    val trust:TrustedDeviceRepository,
    val transfers:TransferRepository,
    val identity:DeviceIdentityService,
    val tls:TlsContextFactory,
    val discovery:NsdDiscoveryService,
    val documents:DocumentTreeStore,
    val deviceNames:DeviceNameRepository,
    val log:DiagnosticLog,
)

val Context.quickSendApplication:QuickSendApplication
    get()=applicationContext as? QuickSendApplication ?: error("QuickSendApplication is not installed")

val Context.quickSendServices:AppServices
    get()=quickSendApplication.services ?: error("QuickSend services are unavailable")
