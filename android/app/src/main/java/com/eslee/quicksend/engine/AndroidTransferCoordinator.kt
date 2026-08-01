package com.eslee.quicksend.engine

import android.content.ContentResolver
import android.net.Uri
import android.provider.DocumentsContract
import android.provider.OpenableColumns
import android.util.Base64
import com.eslee.quicksend.BuildConfig
import com.eslee.quicksend.AppServices
import com.eslee.quicksend.discovery.DiscoveredDevice
import com.eslee.quicksend.discovery.NsdHealth
import com.eslee.quicksend.persistence.*
import com.eslee.quicksend.protocol.*
import com.eslee.quicksend.security.DeviceIdentity
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import org.json.JSONArray
import org.json.JSONObject
import java.io.EOFException
import java.io.IOException
import java.net.ConnectException
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketException
import java.net.SocketTimeoutException
import java.security.cert.X509Certificate
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import javax.net.ssl.SSLException
import javax.net.ssl.SSLServerSocket
import javax.net.ssl.SSLSocket

data class SelectedSource(val uri:Uri,val relativePath:String,val size:Long,val modifiedTicks:Long)

data class CoordinatorInitResult(
    val discoveryReady:Boolean,
    val listenerReady:Boolean,
    val degradedReasons:List<String>,
)

class AndroidTransferCoordinator(private val services:AppServices,private val scope:CoroutineScope) {
    private val retry=RetryPolicy()
    private val sendMutex=Mutex()
    private val manualConnectMutex=Mutex()
    private val receiveMutex=Mutex()
    private val pairing=ConcurrentHashMap<UUID,CompletableDeferred<Boolean>>()
    private val runningTransfers=ConcurrentHashMap.newKeySet<UUID>()
    private val _runningTransferIds=MutableStateFlow<Set<UUID>>(emptySet())
    private var server:SSLServerSocket?=null
    private var active:Job?=null
    @Volatile private var paused=false

    /** Live sessions and user-requested disconnects, keyed by peer device id. */
    val peers=PeerConnectionRegistry()

    /** Transfer ids a worker currently owns; their history rows must stay protected. */
    val runningTransferIds:StateFlow<Set<UUID>> = _runningTransferIds.asStateFlow()

    /**
     * Ends every live session with the peer and blocks automatic reconnection until the
     * user reconnects explicitly. Trusted-device rows and the Keystore identity are left
     * untouched, so no SAS re-pairing is triggered later.
     */
    fun disconnectPeer(deviceId:String) {
        val closers=peers.requestManualDisconnect(deviceId)
        closers.forEach{close->runCatching{close()}.onFailure{
            services.log.warn("peer.disconnect.session_close.failed",it,JSONObject().put("deviceId",deviceId))
        }}
        // The previous transfer's outcome belongs to the session that just ended; drop it so
        // it cannot resurface on the notification after reconnecting.
        if(!TransferRuntime.ui.value.hasTransfer)TransferRuntime.resetToIdle("연결을 끊었습니다")
        services.log.info(
            "peer.disconnect.manual",
            JSONObject()
                .put("deviceId",deviceId)
                .put("closedSessions",closers.size)
                .put("initiator","local_user")
                .put("trustPreserved",true),
        )
    }

    /** Clears a manual disconnect so transfers and incoming sessions are allowed again. */
    fun reconnectPeer(deviceId:String) {
        val cleared=peers.allowReconnect(deviceId)
        // Reconnecting starts a fresh session; it must never restore the last result.
        if(!TransferRuntime.ui.value.hasTransfer)TransferRuntime.resetToIdle("다시 연결했습니다")
        services.log.info(
            "peer.reconnect.manual",
            JSONObject().put("deviceId",deviceId).put("cleared",cleared).put("initiator","local_user"),
        )
    }

    private fun markRunning(transferId:UUID,running:Boolean) {
        if(running)runningTransfers.add(transferId) else runningTransfers.remove(transferId)
        _runningTransferIds.value=runningTransfers.toSet()
    }

    private suspend fun localDeviceName():String = services.deviceNames.current

    suspend fun start():CoordinatorInitResult {
        if(server!=null)return CoordinatorInitResult(
            discoveryReady=true,
            listenerReady=true,
            degradedReasons=emptyList(),
        )
        val identity=services.identity.getOrCreate()
        val degradedReasons=mutableListOf<String>()
        var discoveryReady=false
        var listenerReady=false
        services.log.info("android.discovery.init.begin")
        try {
            services.discovery.start(identity.deviceId,localDeviceName(),identity.fingerprint,ProtocolConstants.DEFAULT_PORT)
            discoveryReady=services.discovery.health.value==NsdHealth.READY
            if(!discoveryReady)degradedReasons += "NSD automatic discovery is starting"
            services.log.info("android.discovery.init.complete")
        } catch(error:Throwable) {
            services.log.error("android.discovery.init.failed",error,JSONObject().put("startupStage","coordinator.discovery"))
            services.discovery.stop()
            degradedReasons += "NSD automatic discovery is unavailable"
            TransferRuntime.startupIssue("Nearby-device discovery is unavailable. The app will remain open.")
        }

        services.log.info("android.listener.init.begin")
        try {
            val context=services.tls.create(pairingOnly=true)
            server=(context.serverSocketFactory.createServerSocket(ProtocolConstants.DEFAULT_PORT) as SSLServerSocket).apply{needClientAuth=true;reuseAddress=true;enabledProtocols=enabledProtocols.filter{it=="TLSv1.3"||it=="TLSv1.2"}.toTypedArray()}
            listenerReady=true
            services.log.info("android.listener.init.complete",JSONObject().put("port",ProtocolConstants.DEFAULT_PORT))
        } catch(error:Throwable) {
            services.log.error("android.listener.init.failed",error,JSONObject().put("startupStage","coordinator.listener"))
            server=null
            degradedReasons += "Android incoming listener is unavailable"
        }
        if(listenerReady)scope.launch(Dispatchers.IO){acceptLoop()}
        try {
            // An old row that never reached a terminal state is not an active transfer.
            // Settle it first so it stops resuming forever and becomes cleanable.
            val settled=services.transfers.settleExpiredJobs()
            if(settled>0)services.log.info("recovery.jobs.expired",JSONObject().put("count",settled))
            val now=System.currentTimeMillis()
            services.transfers.recoverableJobs().forEach{job->
                services.log.info("recovery.job.detected",JSONObject().put("transferId",job.transferId.toString()).put("state",job.state.name).put("updatedUtc",job.updatedUtc))
                if(!TransferHistoryPolicy.isResumable(job.state,job.updatedUtc,now))return@forEach
                if(job.direction==TransferDirection.SEND){
                    val records=services.transfers.files(job.transferId)
                    if(records.isNotEmpty())scope.launch{
                        val sources=records.map{SelectedSource(Uri.parse(it.sourceUri),it.relativePath,it.size,it.modifiedTicks)}
                        runOutgoing(job,records,sources)
                    }
                }
            }
        } catch(error:Throwable) {
            services.log.error("android.recovery.init.failed",error,JSONObject().put("startupStage","coordinator.recovery"))
            TransferRuntime.startupIssue("Previous transfer history could not be restored. The app will remain open.")
        }
        return CoordinatorInitResult(discoveryReady,listenerReady,degradedReasons)
    }

    fun stop(){
        active?.cancel()
        active=null
        runCatching{server?.close()}
        server=null
        services.discovery.stop()
    }

    fun queue(deviceId:String,sources:List<SelectedSource>) {
        require(sources.isNotEmpty())
        active?.cancel()
        active=scope.launch{runOutgoing(deviceId,sources)}
    }

    suspend fun manualConnect(hostText:String,portText:String):ManualConnectResult=manualConnectMutex.withLock {
        val input=JSONObject().put("host",hostText.trim()).put("port",portText.trim())
        services.log.info("manual_connect.begin",input)
        val endpoint=try {
            ManualEndpoint.parse(hostText,portText)
        } catch(error:ManualEndpointValidationException) {
            services.log.error(
                "manual_connect.input.failed",
                error,
                input.put("reason",error.reason.name),
            )
            return@withLock ManualConnectResult.Failure(error.reason,error.userMessage)
        }

        performManualConnect(endpoint)
    }

    private suspend fun performManualConnect(endpoint:ManualEndpoint):ManualConnectResult=withContext(Dispatchers.IO) {
        var stage=ManualConnectStage.TCP
        var tcp:Socket?=null
        val fields=JSONObject().put("endpoint",endpoint.display)
        try {
            services.log.info("manual_connect.tcp.begin",JSONObject(fields.toString()))
            tcp=Socket().apply{
                tcpNoDelay=true
                sendBufferSize=4*1024*1024
                receiveBufferSize=4*1024*1024
                connect(InetSocketAddress(endpoint.address,endpoint.port),10_000)
                soTimeout=45_000
            }
            services.log.info(
                "manual_connect.tcp.success",
                JSONObject(fields.toString())
                    .put("localAddress",tcp.localAddress.hostAddress)
                    .put("localPort",tcp.localPort),
            )

            stage=ManualConnectStage.TLS
            services.log.info("manual_connect.tls.begin",JSONObject(fields.toString()))
            val context=services.tls.create(pairingOnly=true)
            val ssl=(context.socketFactory.createSocket(
                tcp,
                endpoint.address.hostAddress ?: endpoint.address.toString(),
                endpoint.port,
                true,
            ) as SSLSocket).apply{
                useClientMode=true
                soTimeout=45_000
                enabledProtocols=enabledProtocols.filter{it=="TLSv1.3"||it=="TLSv1.2"}.toTypedArray()
            }
            tcp=null
            ssl.use { socket->
                socket.startHandshake()
                services.log.info(
                    "manual_connect.tls.success",
                    JSONObject(fields.toString()).put("protocol",socket.session.protocol),
                )

                stage=ManualConnectStage.PROTOCOL
                services.log.info("manual_connect.protocol.begin",JSONObject(fields.toString()))
                val session=establishClientProtocol(socket,manual=true)
                session.writer.writeControl(
                    MessageType.PING,
                    JSONObject().put("monotonicTicks",System.nanoTime()),
                )
                readControl(session.reader,session.writer,MessageType.PONG)
                val device=services.discovery.upsertManualPeer(
                    session.remote.deviceId,
                    session.remote.deviceName,
                    endpoint.address,
                    endpoint.port,
                    session.remote.identityFingerprint,
                )
                try {
                    services.settings.set(ManualEndpoint.LAST_HOST_SETTING,endpoint.address.hostAddress.orEmpty())
                    services.settings.set(ManualEndpoint.LAST_PORT_SETTING,endpoint.port.toString())
                } catch(settingsError:Throwable) {
                    services.log.warn(
                        "manual_connect.settings.failed",
                        settingsError,
                        JSONObject(fields.toString()),
                    )
                }
                TransferRuntime.update(
                    TransferRuntime.ui.value.copy(status="${device.name}에 연결되었습니다. 전송할 파일을 선택하세요."),
                )
                services.log.info(
                    "manual_connect.success",
                    JSONObject(fields.toString())
                        .put("deviceId",device.deviceId)
                        .put("deviceName",device.name)
                        .put("protocolVersion",ProtocolConstants.VERSION),
                )
                ManualConnectResult.Success(device)
            }
        } catch(error:Throwable) {
            if(error is CancellationException)throw error
            val reason=manualFailure(stage,error)
            val failureFields=JSONObject(fields.toString()).put("stage",stage.name).put("reason",reason.name)
            val event=when(stage){
                ManualConnectStage.TCP->"manual_connect.tcp.failed"
                ManualConnectStage.TLS->"manual_connect.tls.failed"
                ManualConnectStage.PROTOCOL->"manual_connect.protocol.failed"
            }
            services.log.error(event,error,failureFields)
            if(reason==ManualConnectFailure.PAIRING_FAILED){
                services.log.error("manual_connect.pairing.failed",error,JSONObject(failureFields.toString()))
            }
            ManualConnectResult.Failure(reason,manualFailureMessage(reason,endpoint))
        } finally {
            runCatching{tcp?.close()}
        }
    }

    fun togglePause(){paused=!paused;TransferRuntime.update(TransferRuntime.ui.value.copy(status=if(paused)"일시정지됨" else "이어 전송 준비 중",state=if(paused)TransferState.PAUSED else TransferState.RETRYING))}
    fun cancel(){active?.cancel();active=null;TransferRuntime.update(TransferUiState(status="전송이 취소되었습니다",state=TransferState.CANCELLED))}
    fun completePairing(id:UUID,accepted:Boolean){pairing.remove(id)?.complete(accepted)}

    private suspend fun runOutgoing(deviceId:String,sources:List<SelectedSource>) {
        val identity=services.identity.getOrCreate();val transferId=UUID.randomUUID();val now=System.currentTimeMillis()
        val job=TransferJobRecord(transferId,identity.deviceId,deviceId,TransferDirection.SEND,TransferState.QUEUED,now,now)
        services.transfers.upsertJob(job)
        val records=sources.map{source->TransferFileRecord(transferId,UUID.randomUUID(),source.relativePath,source.uri.toString(),null,null,source.size,source.modifiedTicks,null,ProtocolConstants.DEFAULT_CHUNK_SIZE,0,0,byteArrayOf(),TransferState.QUEUED).also{services.transfers.upsertFile(it)}}
        runOutgoing(job,records,sources)
    }

    private suspend fun runOutgoing(job:TransferJobRecord,records:List<TransferFileRecord>,sources:List<SelectedSource>):Unit=sendMutex.withLock {
        var attempt=0
        val resumeLedger=ResumeProgressLedger()
        markRunning(job.transferId,true)
        try {
            while(currentCoroutineContext().isActive){
                while(paused)delay(500)
                if(peers.isManuallyDisconnected(job.destinationDeviceId)){parkForManualDisconnect(job);continue}
                val device=services.discovery.find(job.destinationDeviceId)
                if(device==null||!device.online){TransferRuntime.update(TransferUiState(status="기기를 기다리는 중...",state=TransferState.WAITING_DEVICE,hasTransfer=true,canCancel=true));waitForNetworkOrDelay(retry.delayMillis(attempt++));continue}
                var manualClose=false
                val liveSocket=java.util.concurrent.atomic.AtomicReference<SSLSocket?>(null)
                val link=peers.tryRegisterLink(device.deviceId,device.name){
                    manualClose=true
                    runCatching{liveSocket.get()?.close()}
                }
                if(link==null){parkForManualDisconnect(job);continue}
                try{
                    val skipped=runOutgoingSession(job,records,sources,device,resumeLedger){liveSocket.set(it)}
                    val now=System.currentTimeMillis()
                    services.transfers.upsertJob(job.copy(state=if(skipped)TransferState.USER_ACTION_REQUIRED else TransferState.COMPLETED,updatedUtc=now,completedUtc=if(skipped)null else now,errorCode=if(skipped)"SOURCE_CHANGED" else null))
                    TransferRuntime.update(TransferUiState(status=if(skipped)"일부 파일은 원본이 변경되어 확인이 필요합니다" else "전송 완료",state=if(skipped)TransferState.USER_ACTION_REQUIRED else TransferState.COMPLETED))
                    return
                }
                catch(e:SourceChangedException){services.transfers.upsertJob(job.copy(state=TransferState.RECOVERING,updatedUtc=System.currentTimeMillis(),errorCode="SOURCE_CHANGED"));TransferRuntime.update(TransferUiState(status="변경된 파일을 제외하고 나머지를 계속 전송합니다",state=TransferState.RECOVERING,hasTransfer=true,canCancel=true));continue}
                catch(e:Throwable){
                    if(e is CancellationException)throw e
                    if(e !is java.io.IOException&&e !is SocketException)throw e
                    if(manualClose||peers.isManuallyDisconnected(job.destinationDeviceId)){
                        // The session was torn down locally, not by the network. The durable
                        // checkpoint already on the receiver is what a reconnect resumes from.
                        services.log.info(
                            "android.transfer.session.closed.manual",
                            JSONObject().put("transferId",job.transferId.toString()).put("closeInitiator","local_user"),
                        )
                        parkForManualDisconnect(job)
                        continue
                    }
                    services.log.error(
                        "android.transfer.connection.recover",
                        e,
                        JSONObject()
                            .put("transferId",job.transferId.toString())
                            .put("attempt",attempt)
                            .put("closeInitiator","local_exception_scope"),
                    )
                    services.transfers.upsertJob(job.copy(state=TransferState.RECOVERING,updatedUtc=System.currentTimeMillis()))
                    TransferRuntime.update(TransferUiState(status="연결이 끊어졌습니다. 자동으로 다시 연결하는 중...",state=TransferState.RECOVERING,hasTransfer=true,canCancel=true))
                    services.log.info(
                        "android.transfer.reconnect.scheduled",
                        JSONObject().put("transferId",job.transferId.toString()).put("attempt",attempt),
                    )
                    waitForNetworkOrDelay(retry.delayMillis(attempt++))
                }
                finally { liveSocket.set(null);link.close() }
            }
        } catch(_:CancellationException){services.transfers.upsertJob(job.copy(state=TransferState.CANCELLED,updatedUtc=System.currentTimeMillis()))}
        finally { markRunning(job.transferId,false) }
    }

    /**
     * Holds a job in a resumable paused state after the user disconnected the peer.
     * Nothing is deleted; the next explicit reconnect resumes from the committed offset.
     */
    private suspend fun parkForManualDisconnect(job:TransferJobRecord) {
        services.transfers.upsertJob(
            job.copy(
                state=TransferState.PAUSED,
                updatedUtc=System.currentTimeMillis(),
                errorCode=TransferHistoryPolicy.MANUAL_DISCONNECT_ERROR_CODE,
            ),
        )
        TransferRuntime.update(
            TransferUiState(
                status="연결을 끊었습니다. 다시 연결하면 이어서 전송합니다",
                state=TransferState.PAUSED,
                hasTransfer=true,
                canCancel=true,
            ),
        )
        services.log.info(
            "android.transfer.parked.manual_disconnect",
            JSONObject().put("transferId",job.transferId.toString()).put("deviceId",job.destinationDeviceId),
        )
        // Poll rather than block forever so an explicit reconnect resumes promptly.
        while(currentCoroutineContext().isActive&&peers.isManuallyDisconnected(job.destinationDeviceId))delay(1_000)
    }

    private suspend fun runOutgoingSession(
        job:TransferJobRecord,
        records:List<TransferFileRecord>,
        sources:List<SelectedSource>,
        device:DiscoveredDevice,
        resumeLedger:ResumeProgressLedger,
        onSocketReady:(SSLSocket)->Unit={},
    ):Boolean{
        val connectionId=UUID.randomUUID()
        var skipped=false
        val sendable=buildList {
            records.zip(sources).forEach{(record,source)->
                val current=SlidingWindowSender.queryMetadata(services.context.contentResolver,source.uri)
                val unchanged=current.size==record.size&&(record.modifiedTicks<=0||current.modifiedTicks<=0||current.modifiedTicks==record.modifiedTicks)
                if(unchanged)add(record to source) else {
                    skipped=true
                    services.transfers.upsertFile(record.copy(state=TransferState.USER_ACTION_REQUIRED,errorCode="SOURCE_CHANGED"))
                }
            }
        }
        val trusted=services.trust.isTrusted(device.fingerprint)
        services.log.info(
            "android.outgoing.connection.begin",
            JSONObject().put("connectionId",connectionId.toString()).put("transferId",job.transferId.toString())
                .put("address",device.address.hostAddress).put("port",device.port),
        )
        val socket=(services.tls.create(pairingOnly=!trusted).socketFactory.createSocket(device.address,device.port) as SSLSocket).apply{useClientMode=true;soTimeout=45_000;enabledProtocols=enabledProtocols.filter{it=="TLSv1.3"||it=="TLSv1.2"}.toTypedArray()}
        onSocketReady(socket)
        socket.startHandshake()
        services.log.info(
            "android.outgoing.tls.success",
            JSONObject().put("connectionId",connectionId.toString()).put("transferId",job.transferId.toString())
                .put("protocol",socket.session.protocol).put("cipher",socket.session.cipherSuite),
        )
        socket.use {
            val session=establishClientProtocol(it)
            val writer=session.writer
            val reader=session.reader
            writer.writeControl(MessageType.JOB_MANIFEST,ManifestMessage(job.transferId,job.sourceDeviceId,job.destinationDeviceId,sendable.map{(r,_)->ManifestFile(r.fileId,r.relativePath,r.size,r.modifiedTicks,r.chunkSize,r.stableSourceId)}).json())
            val total=sendable.sumOf{(r,_)->r.size};var prior=0L
            sendable.forEach{(record,source)->
                val start=FileStartMessage(record.transferId,record.fileId,record.relativePath,record.size,record.modifiedTicks,record.chunkSize,record.stableSourceId)
                writer.writeControl(MessageType.FILE_START,start.json())
                val resume=ResumeInfoMessage.parse(readControl(reader,writer,MessageType.RESUME_INFO).json())
                val committed=resumeLedger.observeResume(record.fileId,resume.committedOffset)
                services.log.info(
                    "android.outgoing.receiver.resume",
                    JSONObject().put("connectionId",connectionId.toString()).put("transferId",job.transferId.toString())
                        .put("fileId",record.fileId.toString()).put("committedOffset",committed)
                        .put("committedLeaves",resume.committedLeaves),
                )
                val clock=System.nanoTime();val sender=SlidingWindowSender(services.context.contentResolver,reader,writer,record.chunkSize)
                val resumeProgress=ResumeProgressLedger.overallCommitted(prior,committed)
                TransferRuntime.update(TransferUiState("전송 중",record.relativePath,resumeProgress,total,0.0,null,TransferState.TRANSFERRING,true,true,true,hasTransfer=true))
                sender.onChunkSent={offset,length->services.log.info("android.outgoing.chunk.sent",JSONObject().put("connectionId",connectionId.toString()).put("transferId",job.transferId.toString()).put("fileId",record.fileId.toString()).put("offset",offset).put("length",length))}
                sender.onChunkAcknowledged={offset,length,received->services.log.info("android.outgoing.chunk.acknowledged",JSONObject().put("connectionId",connectionId.toString()).put("transferId",job.transferId.toString()).put("fileId",record.fileId.toString()).put("offset",offset).put("length",length).put("receivedOffset",received))}
                sender.onCheckpoint={checkpoint->resumeLedger.observeCheckpoint(record.fileId,checkpoint.committedOffset);services.log.info("android.outgoing.checkpoint.received",JSONObject().put("connectionId",connectionId.toString()).put("transferId",job.transferId.toString()).put("fileId",record.fileId.toString()).put("committedOffset",checkpoint.committedOffset).put("committedLeaves",checkpoint.committedLeaves))}
                sender.onProgress={p->val durable=resumeLedger.observeCheckpoint(record.fileId,p.safeOffset);val safe=ResumeProgressLedger.overallCommitted(prior,durable);val elapsed=(System.nanoTime()-clock)/1e9;val speed=if(elapsed>0)(p.receivedOffset-resume.committedOffset)/elapsed else 0.0;TransferRuntime.update(TransferUiState("전송 중",record.relativePath,safe,total,speed,if(speed>0)((total-safe)/speed).toLong() else null,TransferState.TRANSFERRING,true,true,true,hasTransfer=true))}
                sender.send(source.uri,start,resume,SourceMetadata(source.size,source.modifiedTicks))
                val verify=FileVerifyMessage.parse(readControl(reader,writer,MessageType.FILE_VERIFY).json());if(!verify.verified)throw FileIntegrityException()
                // Persist the per-file outcome so the history row reflects the verified send
                // instead of staying at its queued state forever.
                services.transfers.upsertFile(record.copy(receivedOffset=record.size,committedOffset=record.size,state=TransferState.COMPLETED,errorCode=null))
                services.log.info("android.outgoing.file.verified",JSONObject().put("connectionId",connectionId.toString()).put("transferId",job.transferId.toString()).put("fileId",record.fileId.toString()))
                prior+=record.size
            }
            writer.writeControl(MessageType.JOB_COMPLETE,JSONObject().put("transferId",job.transferId.toString()),FrameFlags.FINAL)
            services.log.info("android.outgoing.session.completed",JSONObject().put("connectionId",connectionId.toString()).put("transferId",job.transferId.toString()))
        }
        return skipped
    }

    private suspend fun acceptLoop(){
        while(scope.isActive){try{val socket=server?.accept() as? SSLSocket?:return;scope.launch(Dispatchers.IO){handleIncoming(socket)}}catch(e:Throwable){if(scope.isActive)delay(1000)}}
    }

    private suspend fun handleIncoming(socket:SSLSocket) = receiveMutex.withLock {
        val connectionId = UUID.randomUUID()
        var transferId: UUID? = null
        var closeInitiator = "local_normal"
        var link: PeerLink? = null
        var manualClose = false
        services.log.info(
            "android.incoming.connection.established",
            JSONObject()
                .put("connectionId", connectionId.toString())
                .put("remoteAddress", socket.inetAddress?.hostAddress)
                .put("remotePort", socket.port),
        )
        try {
            socket.use {
                it.useClientMode = false
                it.needClientAuth = true
                it.soTimeout = 45_000
                it.startHandshake()
                services.log.info(
                    "android.incoming.tls.completed",
                    JSONObject()
                        .put("connectionId", connectionId.toString())
                        .put("protocol", it.session.protocol)
                        .put("cipherSuite", it.session.cipherSuite),
                )
                val writer = ProtocolWriter(it.outputStream)
                val reader = ProtocolReader(it.inputStream)
                val remote = HelloMessage.parse(readControl(reader, writer, MessageType.HELLO).json())
                validatePeer(it, remote)

                link = peers.tryRegisterLink(remote.deviceId, remote.deviceName) {
                    manualClose = true
                    runCatching { it.close() }
                }
                if (link == null) {
                    closeInitiator = "local_user"
                    services.log.info(
                        "android.incoming.session.refused.manual_disconnect",
                        JSONObject()
                            .put("connectionId", connectionId.toString())
                            .put("remoteDeviceId", remote.deviceId)
                            .put("remoteDeviceName", remote.deviceName),
                    )
                    writer.writeControl(
                        MessageType.ERROR,
                        JSONObject()
                            .put("code", "PEER_DISCONNECTED")
                            .put("userMessage", "휴대폰에서 연결을 끊었습니다. 다시 연결한 뒤 시도하세요.")
                            .put("recoveryClass", "UserActionRequired"),
                        FrameFlags.RESPONSE or FrameFlags.FINAL,
                    )
                    return@use
                }

                val local = services.identity.getOrCreate()
                writer.writeControl(MessageType.HELLO, hello(local).json(), FrameFlags.RESPONSE)
                ensurePairedServer(reader, writer, local, remote)
                services.log.info(
                    "android.incoming.session.ready",
                    JSONObject()
                        .put("connectionId", connectionId.toString())
                        .put("remoteDeviceId", remote.deviceId)
                        .put("remoteDeviceName", remote.deviceName),
                )

                val manifest = ManifestMessage.parse(
                    readControl(reader, writer, MessageType.JOB_MANIFEST).json(),
                )
                transferId = manifest.transferId
                services.log.info(
                    "android.incoming.transfer.started",
                    JSONObject()
                        .put("connectionId", connectionId.toString())
                        .put("transferId", manifest.transferId.toString())
                        .put("fileCount", manifest.files.size),
                )
                val now = System.currentTimeMillis()
                val job = TransferJobRecord(
                    manifest.transferId,
                    manifest.sourceDeviceId,
                    manifest.destinationDeviceId,
                    TransferDirection.RECEIVE,
                    TransferState.TRANSFERRING,
                    now,
                    now,
                )
                services.transfers.upsertJob(job)
                markRunning(manifest.transferId, true)
                TransferRuntime.update(
                    TransferUiState(
                        status = "${remote.deviceName}에서 받는 중",
                        currentFile = manifest.files.firstOrNull()?.relativePath ?: "수신 준비 중",
                        totalBytes = manifest.files.sumOf { file -> file.size },
                        state = TransferState.TRANSFERRING,
                        hasTransfer = true,
                        canCancel = true,
                    ),
                )
                val manifestTotal = manifest.files.sumOf { file -> file.size }
                var receivedBefore = 0L
                for (expected in manifest.files) {
                    val start = FileStartMessage.parse(
                        readControl(reader, writer, MessageType.FILE_START).json(),
                    )
                    if (start.fileId != expected.fileId || start.size != expected.size) {
                        throw ProtocolException("Manifest mismatch")
                    }
                    receiveOne(reader, writer, start, receivedBefore, manifestTotal, remote.deviceName)
                    receivedBefore += expected.size
                }
                readControl(reader, writer, MessageType.JOB_COMPLETE)
                services.transfers.upsertJob(
                    job.copy(
                        state = TransferState.COMPLETED,
                        updatedUtc = System.currentTimeMillis(),
                        completedUtc = System.currentTimeMillis(),
                    ),
                )
                TransferRuntime.update(
                    TransferUiState(status = "전송 완료", state = TransferState.COMPLETED),
                )
                services.log.info(
                    "android.incoming.transfer.completed",
                    JSONObject()
                        .put("connectionId", connectionId.toString())
                        .put("transferId", manifest.transferId.toString()),
                )
            }
        } catch (error: CancellationException) {
            closeInitiator = "local_cancellation"
            services.log.warn(
                "android.incoming.session.cancelled",
                error,
                JSONObject()
                    .put("connectionId", connectionId.toString())
                    .put("transferId", transferId?.toString()),
            )
            throw error
        } catch (error: Throwable) {
            closeInitiator = when {
                manualClose -> "local_user"
                error is EOFException -> "remote"
                error is SocketException -> "transport"
                else -> "local_fault"
            }
            if (manualClose) {
                services.log.info(
                    "android.incoming.session.closed.manual",
                    JSONObject()
                        .put("connectionId", connectionId.toString())
                        .put("transferId", transferId?.toString())
                        .put("closeInitiator", closeInitiator),
                )
            } else {
                services.log.warn(
                    "android.incoming.session.interrupted",
                    error,
                    JSONObject()
                        .put("connectionId", connectionId.toString())
                        .put("transferId", transferId?.toString())
                        .put("closeInitiator", closeInitiator)
                        .put("exceptionType", error.javaClass.name),
                )
            }
        } finally {
            transferId?.let { markRunning(it, false) }
            link?.close()
            // No receive-side resume loop is running, so the notification returns to the
            // static idle look instead of spinning as if work were in progress.
            if (TransferRuntime.ui.value.hasTransfer) {
                TransferRuntime.update(TransferUiState(status = "전송이 중단되었습니다"))
            }
            services.log.info(
                "android.incoming.connection.closed",
                JSONObject()
                    .put("connectionId", connectionId.toString())
                    .put("transferId", transferId?.toString())
                    .put("closeInitiator", closeInitiator),
            )
        }
    }

    private suspend fun receiveOne(reader:ProtocolReader,writer:ProtocolWriter,start:FileStartMessage,priorBytes:Long=0,totalBytes:Long=start.size,senderName:String=""){
        val root=services.settings.get("receive.tree.uri") ?: run{
            writer.writeControl(MessageType.ERROR,JSONObject().put("code","RECEIVE_FOLDER_REQUIRED").put("userMessage","받은 파일 위치를 먼저 선택해 주세요.").put("recoveryClass","UserActionRequired"),FrameFlags.RESPONSE)
            throw java.io.IOException("Receive folder required")
        }
        var record=services.transfers.file(start.fileId)
        if(record!=null&&CompletedFileReplay.canReplay(record,record.finalUri?.let(services.documents::exists)==true)){
            val resume=CompletedFileReplay.createResume(record)
            services.log.info(
                "android.incoming.receiver.resume.completed",
                JSONObject().put("transferId",record.transferId.toString()).put("fileId",record.fileId.toString())
                    .put("committedOffset",resume.committedOffset).put("committedLeaves",resume.committedLeaves),
            )
            writer.writeControl(MessageType.RESUME_INFO,resume.json(),FrameFlags.RESPONSE)
            val complete=FileCompleteMessage.parse(readControl(reader,writer,MessageType.FILE_COMPLETE).json())
            CompletedFileReplay.verify(record,complete)
            writer.writeControl(MessageType.FILE_VERIFY,FileVerifyMessage(record.fileId,true).json(),FrameFlags.RESPONSE or FrameFlags.FINAL)
            services.log.info(
                "android.incoming.file.verify.replayed",
                JSONObject().put("transferId",record.transferId.toString()).put("fileId",record.fileId.toString()),
            )
            return
        }
        if(record?.state==TransferState.COMPLETED){
            record=record.copy(
                partialUri=null,
                finalUri=null,
                receivedOffset=0,
                committedOffset=0,
                merkleLeaves=byteArrayOf(),
                state=TransferState.TRANSFERRING,
                errorCode=null,
            )
            services.transfers.upsertFile(record)
        }
        val available=services.documents.availableBytes(root)
        val remaining=maxOf(0L,start.size-(record?.committedOffset?:0L))
        if(available>=0&&available<remaining){
            writer.writeControl(MessageType.ERROR,JSONObject().put("code","NO_SPACE").put("userMessage","저장 공간이 부족합니다.").put("recoveryClass","WaitForCondition"),FrameFlags.RESPONSE)
            throw java.io.IOException("Insufficient space")
        }
        if(record==null){
            record=TransferFileRecord(start.transferId,start.fileId,start.relativePath,"",null,null,start.size,start.modifiedUtcTicks,start.stableSourceId,start.chunkSize,0,0,byteArrayOf(),TransferState.TRANSFERRING)
            services.transfers.upsertFile(record)
        }
        if(record.size!=start.size||record.modifiedTicks!=start.modifiedUtcTicks)throw ProtocolException("Existing partial metadata mismatch")

        AndroidReceiverSession(record,services.transfers,services.documents,root).use{receiver->
            receiver.initialize()
            writer.writeControl(MessageType.RESUME_INFO,receiver.resumeInfo().json(),FrameFlags.RESPONSE)
            val retries=mutableMapOf<Long,Int>()
            val reorder=java.util.TreeMap<Long,ByteArray>()
            var retryOffset:Long?=null
            val clock=System.nanoTime()
            val startOffset=receiver.receivedOffset

            suspend fun acceptPayload(payload:ByteArray){
                val result=receiver.receive(payload)
                result.checkpoint?.let{
                    writer.writeControl(MessageType.CHECKPOINT,it.json(),FrameFlags.RESPONSE)
                    services.log.info(
                        "android.incoming.checkpoint.sent",
                        JSONObject().put("transferId",start.transferId.toString()).put("fileId",start.fileId.toString())
                            .put("committedOffset",it.committedOffset).put("committedLeaves",it.committedLeaves),
                    )
                }
                writer.writeControl(MessageType.CHUNK_ACK,result.ack.json(),FrameFlags.RESPONSE)
                // Publishing here keeps the notification's determinate bar tied to real bytes.
                val elapsed=(System.nanoTime()-clock)/1e9
                val speed=if(elapsed>0)(receiver.receivedOffset-startOffset)/elapsed else 0.0
                val safe=priorBytes+receiver.receivedOffset
                TransferRuntime.update(
                    TransferUiState(
                        status=if(senderName.isBlank())"받는 중" else "${senderName}에서 받는 중",
                        currentFile=start.relativePath,
                        safeBytes=safe,
                        totalBytes=totalBytes,
                        speedBytesPerSecond=speed,
                        etaSeconds=if(speed>0)((totalBytes-safe)/speed).toLong() else null,
                        state=TransferState.TRANSFERRING,
                        hasTransfer=true,
                        canCancel=true,
                    ),
                )
                services.log.info(
                    "android.incoming.chunk.acknowledged",
                    JSONObject().put("transferId",start.transferId.toString()).put("fileId",start.fileId.toString())
                        .put("offset",result.ack.offset).put("length",result.ack.length)
                        .put("receivedOffset",result.ack.receivedOffset),
                )
            }

            while(true){
                val frame=reader.read()
                when(frame.header.type){
                    MessageType.CHUNK_DATA->{
                        val metadata=ChunkPayload(frame.payload)
                        val missing=retryOffset
                        if(missing!=null&&metadata.offset!=missing){
                            if(reorder.size>=ProtocolConstants.DEFAULT_WINDOW_CHUNKS||reorder.putIfAbsent(metadata.offset,frame.payload)!=null)throw ProtocolException("Retry reorder window exceeded or duplicated")
                            continue
                        }
                        try{
                            acceptPayload(frame.payload)
                            retryOffset=null
                            while(true){
                                val buffered=reorder.remove(receiver.receivedOffset)?:break
                                acceptPayload(buffered)
                            }
                        }catch(e:ChunkIntegrityException){
                            retryOffset=e.offset
                            val count=(retries[e.offset]?:0)+1
                            retries[e.offset]=count
                            writer.writeControl(MessageType.ERROR,JSONObject().put("code",if(count<3)"CHUNK_RETRY" else "CONNECTION_REBUILD").put("userMessage","청크 확인에 실패해 자동으로 다시 시도합니다.").put("recoveryClass","Recoverable").put("fileId",start.fileId.toString()).put("offset",e.offset),FrameFlags.RESPONSE or FrameFlags.RETRY)
                            if(count>=3)throw e
                        }
                    }
                    MessageType.FILE_COMPLETE->{
                        val complete=FileCompleteMessage.parse(frame.json())
                        receiver.complete(complete)
                        writer.writeControl(MessageType.FILE_VERIFY,FileVerifyMessage(start.fileId,true).json(),FrameFlags.RESPONSE or FrameFlags.FINAL)
                        return
                    }
                    MessageType.PING->writer.writeControl(MessageType.PONG,frame.json(),FrameFlags.RESPONSE)
                    MessageType.PONG->Unit
                    MessageType.CANCEL->throw CancellationException("Remote cancelled")
                    else->throw ProtocolException("Unexpected ${frame.header.type}")
                }
            }
        }
    }

    private suspend fun establishClientProtocol(socket:SSLSocket,manual:Boolean=false):ClientProtocolSession {
        val local=services.identity.getOrCreate()
        val writer=ProtocolWriter(socket.outputStream)
        val reader=ProtocolReader(socket.inputStream)
        writer.writeControl(MessageType.HELLO,hello(local).json())
        val remote=HelloMessage.parse(readControl(reader,writer,MessageType.HELLO).json())
        validatePeer(socket,remote)
        val trusted=services.trust.isTrusted(remote.identityFingerprint)
        if(manual){
            services.log.info(
                "manual_connect.pairing.begin",
                JSONObject().put("deviceId",remote.deviceId).put("deviceName",remote.deviceName).put("alreadyTrusted",trusted),
            )
        }
        ensurePairedClient(reader,writer,local,remote,trusted)
        if(manual){
            services.log.info(
                "manual_connect.pairing.success",
                JSONObject().put("deviceId",remote.deviceId).put("alreadyTrusted",trusted),
            )
        }
        return ClientProtocolSession(reader,writer,remote)
    }

    private suspend fun ensurePairedClient(reader:ProtocolReader,writer:ProtocolWriter,local:DeviceIdentity,remote:HelloMessage,trusted:Boolean){if(trusted)return;val nonce=java.security.SecureRandom().generateSeed(16).joinToString(""){"%02X".format(it)};writer.writeControl(MessageType.PAIR_REQUEST,JSONObject().put("deviceId",local.deviceId).put("deviceName",services.deviceNames.current).put("nonce",nonce).put("identityFingerprint",local.fingerprint));if(!askPairing(remote,services.identity.pairingCode(local.fingerprint,remote.identityFingerprint,nonce)))throw PairingRejectedException("Pairing rejected locally");val accept=readControl(reader,writer,MessageType.PAIR_ACCEPT).json();if(!accept.getBoolean("accepted")||accept.getString("nonce")!=nonce)throw PairingRejectedException("Pairing rejected remotely");services.trust.trust(remote.deviceId,remote.deviceName,remote.identityFingerprint)}
    private suspend fun ensurePairedServer(reader:ProtocolReader,writer:ProtocolWriter,local:DeviceIdentity,remote:HelloMessage){if(services.trust.isTrusted(remote.identityFingerprint))return;val request=readControl(reader,writer,MessageType.PAIR_REQUEST).json();if(request.getString("deviceId")!=remote.deviceId||request.getString("identityFingerprint")!=remote.identityFingerprint)throw ProtocolException("Pair identity mismatch");val accepted=askPairing(remote,services.identity.pairingCode(local.fingerprint,remote.identityFingerprint,request.getString("nonce")));if(accepted)services.trust.trust(remote.deviceId,remote.deviceName,remote.identityFingerprint);writer.writeControl(MessageType.PAIR_ACCEPT,JSONObject().put("deviceId",local.deviceId).put("nonce",request.getString("nonce")).put("accepted",accepted),FrameFlags.RESPONSE);if(!accepted)throw java.io.IOException("Pairing rejected")}
    private suspend fun askPairing(remote:HelloMessage,code:String):Boolean{val id=UUID.randomUUID();val deferred=CompletableDeferred<Boolean>();pairing[id]=deferred;TransferRuntime.prompt(PairingPrompt(id,remote.deviceId,remote.deviceName,remote.identityFingerprint,code));return try{deferred.await()}finally{pairing.remove(id)}}

    private fun validatePeer(socket:SSLSocket,hello:HelloMessage){if(hello.protocolVersion!=ProtocolConstants.VERSION)throw ProtocolException("Incompatible protocol version ${hello.protocolVersion}");if(hello.deviceId.isBlank()||hello.identityFingerprint.length!=64)throw ProtocolException("Invalid HELLO identity");val cert=socket.session.peerCertificates.first() as X509Certificate;if(services.identity.fingerprint(cert)!=hello.identityFingerprint)throw ProtocolException("TLS identity does not match HELLO")}
    private fun hello(identity:DeviceIdentity)=HelloMessage(ProtocolConstants.VERSION,BuildConfig.VERSION_NAME,identity.deviceId,services.deviceNames.current,"android",identity.fingerprint,listOf("chunk-sha256","merkle-sha256","resume-v1","sliding-window","folders","saf"))
    private suspend fun readControl(reader:ProtocolReader,writer:ProtocolWriter,expected:MessageType):InboundFrame{while(true){val frame=reader.read();if(frame.header.type==MessageType.PING){writer.writeControl(MessageType.PONG,frame.json(),FrameFlags.RESPONSE);continue};if(frame.header.type==MessageType.PONG&&expected!=MessageType.PONG)continue;if(frame.header.type==MessageType.ERROR)throw java.io.IOException(frame.json().optString("userMessage","Remote error"));if(frame.header.type!=expected)throw ProtocolException("Expected $expected, got ${frame.header.type}");return frame}}
    private suspend fun waitForNetworkOrDelay(millis:Long){withTimeoutOrNull(millis){val current=services.discovery.networkEvents.value;services.discovery.networkEvents.first{it!=current}}}

    private fun manualFailure(stage:ManualConnectStage,error:Throwable):ManualConnectFailure=when {
        error is PairingRejectedException->ManualConnectFailure.PAIRING_FAILED
        error is ConnectException&&error.message.orEmpty().contains("refused",ignoreCase=true)->ManualConnectFailure.CONNECTION_REFUSED
        error is SocketTimeoutException&&stage==ManualConnectStage.TCP->ManualConnectFailure.CONNECTION_TIMEOUT
        error is SSLException->ManualConnectFailure.TLS_AUTH_FAILED
        error is ProtocolException&&error.message.orEmpty().contains("protocol",ignoreCase=true)->ManualConnectFailure.VERSION_MISMATCH
        error is ProtocolException&&error.message.orEmpty().contains("TLS identity",ignoreCase=true)->ManualConnectFailure.TLS_AUTH_FAILED
        error is EOFException||error is SocketTimeoutException->ManualConnectFailure.PROTOCOL_NO_RESPONSE
        stage==ManualConnectStage.TLS->ManualConnectFailure.TLS_AUTH_FAILED
        stage==ManualConnectStage.PROTOCOL->ManualConnectFailure.PROTOCOL_NO_RESPONSE
        else->ManualConnectFailure.CONNECTION_FAILED
    }

    private fun manualFailureMessage(reason:ManualConnectFailure,endpoint:ManualEndpoint):String=when(reason){
        ManualConnectFailure.CONNECTION_TIMEOUT->"${endpoint.display} 연결 시간이 초과되었습니다. PC와 휴대폰이 같은 네트워크인지 확인하세요."
        ManualConnectFailure.CONNECTION_REFUSED->"${endpoint.display}에서 연결을 거부했습니다. PC에서 QuickSend가 실행 중인지 확인하세요."
        ManualConnectFailure.TLS_AUTH_FAILED->"상대 기기의 보안 인증을 확인하지 못했습니다. 양쪽 QuickSend를 다시 실행해 보세요."
        ManualConnectFailure.PROTOCOL_NO_RESPONSE->"${endpoint.display}에서 QuickSend 프로토콜 응답을 받지 못했습니다. IP와 포트를 확인하세요."
        ManualConnectFailure.VERSION_MISMATCH->"상대 QuickSend 버전과 호환되지 않습니다. 양쪽 앱을 최신 빌드로 맞춰 주세요."
        ManualConnectFailure.PAIRING_FAILED->"기기 페어링이 완료되지 않았습니다. 양쪽에서 같은 인증번호를 확인하고 다시 시도하세요."
        ManualConnectFailure.SERVICE_NOT_READY->"연결 서비스가 수동 연결을 수행할 수 없는 상태입니다. 진단 로그에서 서비스 상태를 확인하세요."
        ManualConnectFailure.INVALID_IP->"IP 주소를 확인하세요. 예: 192.168.123.102"
        ManualConnectFailure.INVALID_PORT->"포트는 1부터 65535 사이의 숫자로 입력하세요."
        ManualConnectFailure.CONNECTION_FAILED->"${endpoint.display}에 연결할 수 없습니다. PC에서 QuickSend가 실행 중인지 확인하세요."
    }

    private enum class ManualConnectStage { TCP,TLS,PROTOCOL }
    private data class ClientProtocolSession(val reader:ProtocolReader,val writer:ProtocolWriter,val remote:HelloMessage)
    private class PairingRejectedException(message:String):IOException(message)

    companion object {
        fun querySource(resolver:ContentResolver,uri:Uri,relativePath:String?=null):SelectedSource {
            var name=relativePath;var size=-1L;var modified=0L
            resolver.query(uri,arrayOf(OpenableColumns.DISPLAY_NAME,OpenableColumns.SIZE,DocumentsContract.Document.COLUMN_LAST_MODIFIED),null,null,null)?.use{c->if(c.moveToFirst()){if(name==null)name=c.getString(0);if(!c.isNull(1))size=c.getLong(1);val index=c.getColumnIndex(DocumentsContract.Document.COLUMN_LAST_MODIFIED);if(index>=0&&!c.isNull(index))modified=c.getLong(index)}}
            if(size<0)resolver.openAssetFileDescriptor(uri,"r")?.use{size=it.length}
            require(size>=0){"파일 크기를 확인할 수 없습니다"}
            return SelectedSource(uri,name?:"file",size,modified)
        }
    }
}
