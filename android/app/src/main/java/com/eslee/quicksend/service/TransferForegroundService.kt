package com.eslee.quicksend.service

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.wifi.WifiManager
import android.os.Binder
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import com.eslee.quicksend.AppServices
import com.eslee.quicksend.MainActivity
import com.eslee.quicksend.QuickSendApplication
import com.eslee.quicksend.R
import com.eslee.quicksend.discovery.NsdHealth
import com.eslee.quicksend.engine.AndroidTransferCoordinator
import com.eslee.quicksend.engine.ConnectedPeer
import com.eslee.quicksend.engine.PeerConnectionSnapshot
import com.eslee.quicksend.engine.TransferRuntime
import com.eslee.quicksend.engine.TransferServicePhase
import com.eslee.quicksend.engine.TransferState
import com.eslee.quicksend.engine.TransferUiState
import kotlinx.coroutines.CoroutineExceptionHandler
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject

class TransferForegroundService:Service() {
    companion object {
        const val CHANNEL="quicksend-transfer"
        const val NOTIFICATION=41231
        const val ACTION_PAUSE="com.eslee.quicksend.PAUSE"
        const val ACTION_CANCEL="com.eslee.quicksend.CANCEL"
    }

    private lateinit var scope:CoroutineScope
    private var services:AppServices?=null
    private var coordinator:AndroidTransferCoordinator?=null
    private var wakeLock:PowerManager.WakeLock?=null
    private var wifiLock:WifiManager.WifiLock?=null
    private var multicastLock:WifiManager.MulticastLock?=null
    @Volatile private var runtimeReady=false
    @Volatile private var foregroundStarted=false
    private var fatalFailureReason:String?=null
    private val binder=LocalBinder()

    inner class LocalBinder:Binder() {
        fun coordinator():AndroidTransferCoordinator?=this@TransferForegroundService.coordinator
    }

    override fun onCreate(){
        super.onCreate()
        val app=application as QuickSendApplication
        val log=app.log
        log.info("android.transfer_service.onCreate.begin")
        changeServiceState(TransferServicePhase.STARTING,"TransferForegroundService.onCreate entered")
        log.info("android.service.init.begin")
        val appServices=app.services
        if(appServices==null){
            val error=app.startupFailure ?: IllegalStateException("App services are unavailable")
            log.error("android.service.init.failed",error,JSONObject().put("startupStage","service.dependencies"))
            markFatal("필수 앱 서비스를 초기화하지 못했습니다",error)
            log.info("android.transfer_service.onCreate.complete",JSONObject().put("success",false))
            stopSelf()
            return
        }
        services=appServices
        val handler=CoroutineExceptionHandler{_,error->
            log.error("android.service.coroutine.failed",error,JSONObject().put("startupStage","service.coroutine"))
            TransferRuntime.startupIssue("백그라운드 작업에서 오류가 발생했습니다. 진단 로그를 확인해 주세요.")
        }
        scope=CoroutineScope(SupervisorJob()+Dispatchers.IO+handler)

        log.info("android.notification.init.begin")
        try {
            createChannel()
            startAsForeground(TransferRuntime.ui.value)
            foregroundStarted=true
            log.info("android.notification.init.complete")
            log.info("android.transfer_service.foreground.success")
        } catch(error:Throwable) {
            log.error("android.notification.init.failed",error,JSONObject().put("startupStage","notification.foreground"))
            log.error("android.service.init.failed",error,JSONObject().put("startupStage","notification.foreground"))
            markFatal("포그라운드 알림 서비스를 시작하지 못했습니다",error)
            log.info("android.transfer_service.onCreate.complete",JSONObject().put("success",false))
            stopSelf()
            return
        }

        try {
            coordinator=AndroidTransferCoordinator(appServices,scope)
        } catch(error:Throwable) {
            log.error("android.coordinator.init.failed",error,JSONObject().put("startupStage","coordinator.constructor"))
            markFatal("연결 coordinator를 생성하지 못했습니다",error)
            log.info("android.transfer_service.onCreate.complete",JSONObject().put("success",false))
            stopSelf()
            return
        }
        scope.launch{initializeRuntime(appServices)}
        scope.launch{
            // The notification depends on both the transfer state and which peers are
            // connected, so both sources drive the same render.
            val peers=coordinator?.peers?.snapshot ?: MutableStateFlow(PeerConnectionSnapshot())
            combine(TransferRuntime.ui,peers){state,snapshot->state to snapshot}.collectLatest{(state,snapshot)->
                if(runtimeReady){
                    try { updateResourceLocks(state) }
                    catch(error:Throwable){log.warn("android.service.resource_locks.failed",error,JSONObject().put("startupStage","service.locks"))}
                }
                if(foregroundStarted){
                    try { startAsForeground(state,snapshot.connected) }
                    catch(error:Throwable){log.warn("android.notification.update.failed",error,JSONObject().put("startupStage","notification.update"))}
                }
                // A completion notice expires on a timer rather than on the next state
                // change, so re-render once the window closes and let it fall back to idle.
                val settledAt=state.settledAtMillis
                if(foregroundStarted&&!state.hasTransfer&&settledAt!=null){
                    val remaining=TransferNotificationPresenter.COMPLETION_NOTICE_MILLIS-(System.currentTimeMillis()-settledAt)
                    if(remaining>0){
                        delay(remaining+250)
                        try { startAsForeground(state,snapshot.connected) }
                        catch(error:Throwable){log.warn("android.notification.update.failed",error,JSONObject().put("startupStage","notification.expire"))}
                    }
                }
            }
        }
        scope.launch{
            appServices.deviceNames.name.collectLatest{name->
                // Only the TXT name is refreshed; device id and fingerprint are unchanged.
                runCatching{appServices.discovery.updateDeviceName(name)}
                    .onFailure{log.warn("android.nsd.rename.failed",it,JSONObject().put("name",name))}
            }
        }
        log.info("android.transfer_service.onCreate.complete",JSONObject().put("success",true))
    }

    private suspend fun initializeRuntime(appServices:AppServices){
        val log=appServices.log
        log.info("android.coordinator.init.begin")
        try {
            log.info("android.db.init.begin")
            try {
                appServices.database.initialize(log)
                log.info("android.db.init.complete")
            } catch(error:Throwable) {
                log.error("android.db.init.failed",error,JSONObject().put("startupStage","database"))
                throw error
            }

            log.info("android.identity.init.begin")
            try {
                appServices.identity.getOrCreate()
                log.info("android.identity.init.complete")
            } catch(error:Throwable) {
                log.error("android.identity.init.failed",error,JSONObject().put("startupStage","identity"))
                throw error
            }

            // The stored display name must be known before the service is advertised.
            log.info("android.device_name.init.begin")
            val deviceName=try {
                appServices.deviceNames.load().also{log.info("android.device_name.init.complete",JSONObject().put("name",it))}
            } catch(error:Throwable) {
                log.warn("android.device_name.init.failed",error,JSONObject().put("startupStage","device-name"))
                appServices.deviceNames.current
            }

            log.info("android.tls.init.begin")
            try {
                appServices.tls.create(pairingOnly=true)
                log.info("android.tls.init.complete")
            } catch(error:Throwable) {
                log.error("android.tls.init.failed",error,JSONObject().put("startupStage","tls.manual-connect"))
                throw error
            }

            val baseDegradedReasons=mutableListOf<String>()
            log.info("android.startup.device_name",JSONObject().put("name",deviceName))
            // Older NSD implementations require the multicast filter to be opened before browsing starts.
            if(!acquireMulticastLock())baseDegradedReasons += "Multicast lock is unavailable"
            val init=coordinator?.start() ?: error("Transfer coordinator was not created")
            if(!init.listenerReady)baseDegradedReasons += "Android incoming listener is unavailable"
            runtimeReady=true
            updateResourceLocks(TransferRuntime.ui.value)
            updateOperationalState(baseDegradedReasons,appServices.discovery.health.value)
            scope.launch {
                appServices.discovery.health.collectLatest { health->
                    updateOperationalState(baseDegradedReasons,health)
                }
            }
            val currentReasons=operationalReasons(baseDegradedReasons,appServices.discovery.health.value)
            log.info(
                "android.coordinator.init.complete",
                JSONObject()
                    .put("discoveryReady",appServices.discovery.health.value==NsdHealth.READY)
                    .put("listenerReady",init.listenerReady)
                    .put("manualConnectReady",true)
                    .put("degradedReasons",currentReasons.joinToString("; ")),
            )
            log.info("android.service.init.complete")
            log.info("android.startup.complete",JSONObject().put("mode","ui-and-background-runtime"))
        } catch(error:Throwable) {
            log.error("android.coordinator.init.failed",error,JSONObject().put("startupStage","coordinator.runtime"))
            log.error("android.service.init.failed",error,JSONObject().put("startupStage","service.runtime"))
            markFatal("수동 연결에 필요한 identity/TLS 초기화에 실패했습니다",error)
            withContext(Dispatchers.Main.immediate){
                foregroundStarted=false
                ServiceCompat.stopForeground(this@TransferForegroundService,ServiceCompat.STOP_FOREGROUND_REMOVE)
                stopSelf()
            }
        }
    }

    override fun onStartCommand(intent:Intent?,flags:Int,startId:Int):Int {
        (application as QuickSendApplication).log.info(
            "android.transfer_service.onStartCommand",
            JSONObject().put("action",intent?.action).put("startId",startId),
        )
        when(intent?.action){
            ACTION_PAUSE->coordinator?.togglePause()
            ACTION_CANCEL->coordinator?.cancel()
        }
        return START_STICKY
    }

    override fun onBind(intent:Intent?):IBinder {
        (application as QuickSendApplication).log.info("android.transfer_service.onBind")
        return binder
    }

    override fun onUnbind(intent:Intent?):Boolean {
        (application as QuickSendApplication).log.info("android.transfer_service.onUnbind")
        return super.onUnbind(intent)
    }

    /**
     * Renders the ongoing notification. The service must keep a notification alive, but an
     * idle app has nothing to report: those phases render statically with no progress bar,
     * so the status area never spins while nothing is happening.
     */
    private fun startAsForeground(state:TransferUiState,connected:List<ConnectedPeer> = peerSnapshot()){
        val open=PendingIntent.getActivity(this,0,Intent(this,MainActivity::class.java),PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val pause=PendingIntent.getService(this,1,Intent(this,TransferForegroundService::class.java).setAction(ACTION_PAUSE),PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val cancel=PendingIntent.getService(this,2,Intent(this,TransferForegroundService::class.java).setAction(ACTION_CANCEL),PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val content=TransferNotificationPresenter.present(state,connected)
        val notification=NotificationCompat.Builder(this,CHANNEL)
            // Status-bar icons must be a single-colour mask, so this is the white
            // arrow-only silhouette derived from the master artwork.
            .setSmallIcon(R.drawable.ic_quicksend_status)
            .setColor(getColor(R.color.ic_launcher_background))
            .setColorized(false)
            .setContentTitle(content.title)
            .setContentText(content.text)
            .setContentIntent(open)
            // The notification stays ongoing for as long as the foreground service runs.
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setShowWhen(false)
            .setCategory(
                if(content.showProgress)NotificationCompat.CATEGORY_PROGRESS
                else NotificationCompat.CATEGORY_STATUS,
            )
            .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
            .apply{
                content.subText?.let(::setSubText)
                if(content.showProgress)setProgress(1_000,content.progressPermille,content.indeterminate)
                else setProgress(0,0,false)
                if(content.showPause)addAction(R.drawable.ic_quicksend,"일시정지",pause)
                if(content.showCancel)addAction(R.drawable.ic_quicksend,"취소",cancel)
            }
            .build()
        val foregroundType=if(Build.VERSION.SDK_INT>=29)ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE else 0
        ServiceCompat.startForeground(this,NOTIFICATION,notification,foregroundType)
    }

    private fun peerSnapshot():List<ConnectedPeer> =
        coordinator?.peers?.snapshot?.value?.connected.orEmpty()

    @Suppress("DEPRECATION")
    private fun updateResourceLocks(state:TransferUiState){
        val active=state.state in setOf(TransferState.TRANSFERRING,TransferState.RECOVERING,TransferState.RETRYING,TransferState.VERIFYING)
        if(active){
            if(wakeLock?.isHeld!=true)wakeLock=getSystemService(PowerManager::class.java).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK,"eslee:QuickSendTransfer").apply{setReferenceCounted(false);acquire(10*60_000L)}
            if(wifiLock?.isHeld!=true)wifiLock=getSystemService(WifiManager::class.java).createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF,"eslee:QuickSendHighPerf").apply{setReferenceCounted(false);acquire()}
        } else {
            releaseTransferLocks()
        }
    }

    private fun acquireMulticastLock():Boolean {
        val log=(application as QuickSendApplication).log
        log.info("android.multicast_lock.acquire.begin")
        return try {
            if(multicastLock?.isHeld!=true){
                multicastLock=getSystemService(WifiManager::class.java)
                    .createMulticastLock("eslee:QuickSendMdns")
                    .apply{setReferenceCounted(false);acquire()}
            }
            val held=multicastLock?.isHeld==true
            log.info("android.multicast_lock.acquire.success",JSONObject().put("held",held))
            held
        } catch(error:Throwable) {
            log.error("android.multicast_lock.failed",error,JSONObject().put("startupStage","multicast-lock"))
            false
        }
    }

    private fun createChannel(){
        getSystemService(NotificationManager::class.java).createNotificationChannel(
            NotificationChannel(CHANNEL,getString(R.string.notification_channel_transfer),NotificationManager.IMPORTANCE_LOW).apply{
                description=getString(R.string.notification_channel_description)
                setShowBadge(false)
            },
        )
    }

    private fun releaseTransferLocks(){
        if(wakeLock?.isHeld==true)wakeLock?.release()
        if(wifiLock?.isHeld==true)wifiLock?.release()
    }

    private fun changeServiceState(phase:TransferServicePhase,reason:String){
        val change=TransferRuntime.readiness.transition(phase,reason)
        (application as QuickSendApplication).log.info(
            "android.service_state.changed",
            JSONObject()
                .put("from",change.from.phase.name)
                .put("to",change.to.phase.name)
                .put("reason",reason),
        )
    }

    private fun operationalReasons(baseReasons:List<String>,health:NsdHealth):List<String> = buildList {
        addAll(baseReasons)
        when(health){
            NsdHealth.READY->Unit
            NsdHealth.STARTING->add("NSD automatic discovery is starting")
            NsdHealth.NOT_STARTED,NsdHealth.FAILED->add("NSD automatic discovery is unavailable")
        }
    }

    private fun updateOperationalState(baseReasons:List<String>,health:NsdHealth){
        if(!runtimeReady)return
        if(TransferRuntime.serviceState.value.phase in setOf(TransferServicePhase.FAILED_FATAL,TransferServicePhase.STOPPING))return
        val reasons=operationalReasons(baseReasons,health)
        if(reasons.isEmpty()){
            changeServiceState(TransferServicePhase.READY,"Manual connection and automatic discovery are ready")
        } else {
            changeServiceState(
                TransferServicePhase.DEGRADED,
                "Manual connection is ready; ${reasons.joinToString("; ")}",
            )
        }
    }

    private fun markFatal(reason:String,error:Throwable){
        val detail=error.message?.takeIf{it.isNotBlank()}?.let{"$reason: $it"} ?: reason
        fatalFailureReason=detail
        runtimeReady=false
        changeServiceState(TransferServicePhase.FAILED_FATAL,detail)
        TransferRuntime.startupIssue(detail)
    }

    override fun onDestroy(){
        val preservedFatal=fatalFailureReason ?: TransferRuntime.serviceState.value
            .takeIf{it.phase==TransferServicePhase.FAILED_FATAL}
            ?.reason
        changeServiceState(TransferServicePhase.STOPPING,"TransferForegroundService.onDestroy entered")
        runtimeReady=false
        foregroundStarted=false
        TransferRuntime.detachCoordinator(coordinator)
        coordinator?.stop()
        coordinator=null
        if(::scope.isInitialized)scope.cancel()
        releaseTransferLocks()
        val log=(application as QuickSendApplication).log
        if(multicastLock?.isHeld==true){
            multicastLock?.release()
            log.info("android.multicast_lock.release")
        }
        multicastLock=null
        if(preservedFatal!=null)changeServiceState(TransferServicePhase.FAILED_FATAL,preservedFatal)
        else changeServiceState(TransferServicePhase.NOT_STARTED,"TransferForegroundService was destroyed")
        log.info("android.service.destroy.complete")
        super.onDestroy()
    }

    private fun format(value:Long):String{
        var amount=value.coerceAtLeast(0).toDouble()
        val units=arrayOf("B","KB","MB","GB","TB")
        var i=0
        while(amount>=1024&&i<units.lastIndex){amount/=1024;i++}
        return "%.1f %s".format(amount,units[i])
    }
}
