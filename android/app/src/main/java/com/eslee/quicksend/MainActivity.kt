package com.eslee.quicksend

import android.Manifest
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.ServiceConnection
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.os.IBinder
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.InsertDriveFile
import androidx.compose.material.icons.outlined.Autorenew
import androidx.compose.material.icons.outlined.CheckCircle
import androidx.compose.material.icons.outlined.Close
import androidx.compose.material.icons.outlined.Computer
import androidx.compose.material.icons.outlined.Delete
import androidx.compose.material.icons.outlined.Folder
import androidx.compose.material.icons.outlined.FolderOpen
import androidx.compose.material.icons.outlined.Link
import androidx.compose.material.icons.outlined.LinkOff
import androidx.compose.material.icons.outlined.Pause
import androidx.compose.material.icons.outlined.Settings
import androidx.compose.material.icons.outlined.Smartphone
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.AssistChip
import androidx.compose.material3.Button
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ElevatedCard
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.ListItem
import androidx.compose.material3.ListItemDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedCard
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.documentfile.provider.DocumentFile
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.eslee.quicksend.discovery.DiscoveredDevice
import com.eslee.quicksend.engine.AndroidTransferCoordinator
import com.eslee.quicksend.engine.DeviceNameRules
import com.eslee.quicksend.engine.ManualCoordinatorAvailability
import com.eslee.quicksend.engine.ManualConnectResult
import com.eslee.quicksend.engine.ManualEndpoint
import com.eslee.quicksend.engine.PairingPrompt
import com.eslee.quicksend.engine.PeerLinkState
import com.eslee.quicksend.engine.SelectedSource
import com.eslee.quicksend.engine.TransferHistoryPolicy
import com.eslee.quicksend.engine.TransferRuntime
import com.eslee.quicksend.engine.TransferServicePhase
import com.eslee.quicksend.engine.TransferState
import com.eslee.quicksend.engine.TransferUiState
import com.eslee.quicksend.protocol.ProtocolConstants
import com.eslee.quicksend.persistence.DeviceNameUpdate
import com.eslee.quicksend.persistence.TransferDirection
import com.eslee.quicksend.persistence.TransferHistoryItem
import com.eslee.quicksend.service.TransferForegroundService
import com.eslee.quicksend.storage.SafDisplayPath
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.yield
import org.json.JSONObject

class MainActivity : ComponentActivity() {
    private var transferServiceBound=false
    private val transferServiceConnection=object:ServiceConnection {
        override fun onServiceConnected(name:ComponentName?,binder:IBinder?){
            val app=application as QuickSendApplication
            app.log.info(
                "android.transfer_service.onServiceConnected",
                JSONObject().put("component",name?.flattenToShortString()),
            )
            val localBinder=binder as? TransferForegroundService.LocalBinder
            val coordinator=localBinder?.coordinator()
            if(coordinator==null){
                val error=IllegalStateException("Transfer service returned no coordinator")
                app.log.error("android.transfer_service.binding.failed",error,JSONObject().put("callback","onServiceConnected"))
                changeServiceState(TransferServicePhase.FAILED_FATAL,"서비스에 연결됐지만 coordinator를 가져오지 못했습니다")
                return
            }
            TransferRuntime.attachCoordinator(coordinator)
        }

        override fun onServiceDisconnected(name:ComponentName?){
            val app=application as QuickSendApplication
            app.log.info(
                "android.transfer_service.onServiceDisconnected",
                JSONObject().put("component",name?.flattenToShortString()),
            )
            TransferRuntime.detachCoordinator()
            changeServiceState(TransferServicePhase.FAILED_FATAL,"연결 서비스가 예기치 않게 종료되었습니다")
        }

        override fun onBindingDied(name:ComponentName?){
            val app=application as QuickSendApplication
            app.log.info(
                "android.transfer_service.onBindingDied",
                JSONObject().put("component",name?.flattenToShortString()),
            )
            TransferRuntime.detachCoordinator()
            changeServiceState(TransferServicePhase.FAILED_FATAL,"연결 서비스 binder가 종료되었습니다")
        }

        override fun onNullBinding(name:ComponentName?){
            val app=application as QuickSendApplication
            app.log.info(
                "android.transfer_service.onNullBinding",
                JSONObject().put("component",name?.flattenToShortString()),
            )
            TransferRuntime.detachCoordinator()
            changeServiceState(TransferServicePhase.FAILED_FATAL,"연결 서비스가 유효한 binder를 제공하지 않았습니다")
        }
    }
    private val permissionLauncher=registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()){results->
        val app=application as QuickSendApplication
        val fields=JSONObject()
        if(Build.VERSION.SDK_INT>=33){
            fields.put("notifications",results[Manifest.permission.POST_NOTIFICATIONS])
            fields.put(
                "nearbyWifiAlreadyGranted",
                checkSelfPermission(Manifest.permission.NEARBY_WIFI_DEVICES)==PackageManager.PERMISSION_GRANTED,
            )
        }
        app.log.info(
            "android.permissions.result",
            fields,
        )
        startRuntimeAfterUi()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        val app=application as QuickSendApplication
        app.log.info("android.activity.onCreate.begin")
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            QuickSendTheme {
                val services=app.services
                if(services==null)StartupFailureScreen(app.startupFailure)
                else QuickSendScreen(services)
            }
        }
        app.log.info("android.activity.onCreate.complete",JSONObject().put("uiComposed",true))
        requestRuntimePermissionsOrStart()
    }

    private fun requestRuntimePermissionsOrStart() {
        val needed = buildList {
            if (Build.VERSION.SDK_INT >= 33) {
                if (checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                    add(Manifest.permission.POST_NOTIFICATIONS)
                }
            }
        }
        if(needed.isEmpty())startRuntimeAfterUi()
        else {
            (application as QuickSendApplication).log.info(
                "android.permissions.request",
                JSONObject().put("permissions",needed.joinToString(",")),
            )
            permissionLauncher.launch(needed.toTypedArray())
        }
    }

    private fun startRuntimeAfterUi(){
        lifecycleScope.launch{
            yield()
            val app=application as QuickSendApplication
            if(app.services==null){
                val failure=app.startupFailure ?: IllegalStateException("App services are unavailable")
                app.log.error("android.service.init.failed",failure,JSONObject().put("startupStage","service.request"))
                changeServiceState(TransferServicePhase.FAILED_FATAL,"필수 앱 서비스 초기화에 실패했습니다: ${failure.message ?: failure.javaClass.simpleName}")
                return@launch
            }
            try {
                if(TransferRuntime.serviceState.value.phase in setOf(TransferServicePhase.NOT_STARTED,TransferServicePhase.STOPPING)){
                    changeServiceState(TransferServicePhase.STARTING,"Activity requested transfer service startup")
                }
                val serviceIntent=Intent(this@MainActivity,TransferForegroundService::class.java)
                app.log.info("android.transfer_service.start.requested")
                ContextCompat.startForegroundService(this@MainActivity,serviceIntent)
                bindTransferService(serviceIntent)
            } catch(error:Throwable) {
                app.log.error("android.service.init.failed",error,JSONObject().put("startupStage","service.request"))
                changeServiceState(
                    TransferServicePhase.FAILED_FATAL,
                    "연결 서비스를 시작하지 못했습니다: ${error.message ?: error.javaClass.simpleName}",
                )
            }
        }
    }

    private fun bindTransferService(intent:Intent){
        val app=application as QuickSendApplication
        if(transferServiceBound)return
        app.log.info("android.transfer_service.bind.requested")
        val result=try {
            bindService(intent,transferServiceConnection,Context.BIND_AUTO_CREATE)
        } catch(error:Throwable) {
            app.log.error("android.transfer_service.bind.failed",error,JSONObject().put("startupStage","service.bind"))
            false
        }
        transferServiceBound=result
        app.log.info("android.transfer_service.bind.result",JSONObject().put("bound",result))
        if(!result){
            changeServiceState(TransferServicePhase.FAILED_FATAL,"연결 서비스에 bind하지 못했습니다")
        }
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
        if(phase==TransferServicePhase.FAILED_FATAL)TransferRuntime.startupIssue(reason)
    }

    override fun onDestroy(){
        if(transferServiceBound){
            runCatching{unbindService(transferServiceConnection)}
            transferServiceBound=false
        }
        TransferRuntime.detachCoordinator()
        super.onDestroy()
    }
}

@Composable
private fun QuickSendTheme(content: @Composable () -> Unit) {
    val colors = if (isSystemInDarkTheme()) {
        darkColorScheme(primary = Color(0xFF83D5C2))
    } else {
        lightColorScheme(primary = Color(0xFF176B5B))
    }
    MaterialTheme(colorScheme = colors, content = content)
}

@Composable
private fun StartupFailureScreen(error:Throwable?) {
    Column(
        modifier=Modifier.fillMaxSize().padding(24.dp),
        verticalArrangement=Arrangement.Center,
    ) {
        Text("QuickSend를 초기화하지 못했습니다",style=MaterialTheme.typography.headlineSmall,fontWeight=FontWeight.SemiBold)
        Spacer(Modifier.height(12.dp))
        Text(error?.message ?: "알 수 없는 초기화 오류입니다. 진단 로그를 확인해 주세요.")
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun QuickSendScreen(services:AppServices) {
    val context = androidx.compose.ui.platform.LocalContext.current
    val scope = rememberCoroutineScope()
    val ui by TransferRuntime.ui.collectAsStateWithLifecycle()
    val serviceState by TransferRuntime.serviceState.collectAsStateWithLifecycle()
    val devices by services.discovery.devices.collectAsStateWithLifecycle()
    var selectedId by rememberSaveable { mutableStateOf<String?>(null) }
    var pairing by remember { mutableStateOf<PairingPrompt?>(null) }
    var receiveRoot by remember { mutableStateOf<String?>(null) }
    var showManualConnect by rememberSaveable { mutableStateOf(false) }
    var manualHost by rememberSaveable { mutableStateOf("") }
    var manualPort by rememberSaveable { mutableStateOf(ProtocolConstants.DEFAULT_PORT.toString()) }
    var manualError by rememberSaveable { mutableStateOf<String?>(null) }
    var manualConnecting by remember { mutableStateOf(false) }
    var history by remember { mutableStateOf<List<TransferHistoryItem>>(emptyList()) }
    var historyPendingDelete by remember { mutableStateOf<TransferHistoryItem?>(null) }
    var showClearHistory by remember { mutableStateOf(false) }
    var showRename by remember { mutableStateOf(false) }
    var renameInput by rememberSaveable { mutableStateOf("") }
    var renameError by remember { mutableStateOf<String?>(null) }
    var historyNotice by remember { mutableStateOf<String?>(null) }
    val coordinator by TransferRuntime.coordinatorFlow.collectAsStateWithLifecycle()
    val deviceName by services.deviceNames.name.collectAsStateWithLifecycle()
    val peerConnections by (coordinator?.peers?.snapshot ?: TransferRuntime.emptyPeerConnections)
        .collectAsStateWithLifecycle()
    val runningTransfers by (coordinator?.runningTransferIds ?: TransferRuntime.emptyRunningTransfers)
        .collectAsStateWithLifecycle()
    val historyRevision by services.transfers.revision.collectAsStateWithLifecycle()
    var receiveRootDisplay by remember { mutableStateOf(SafDisplayPath.UNKNOWN_FOLDER) }
    var historyLocations by remember { mutableStateOf<Map<java.util.UUID, String>>(emptyMap()) }

    LaunchedEffect(Unit) {
        try {
            receiveRoot=services.settings.get("receive.tree.uri")
            manualHost=services.settings.get(ManualEndpoint.LAST_HOST_SETTING).orEmpty()
            manualPort=services.settings.get(ManualEndpoint.LAST_PORT_SETTING)
                ?: ProtocolConstants.DEFAULT_PORT.toString()
        } catch(error:Throwable) {
            if(error is CancellationException)throw error
            services.log.error("android.compose.settings.load.failed",error,JSONObject().put("startupStage","compose.settings"))
            TransferRuntime.startupIssue("설정을 불러오지 못했지만 앱은 계속 실행됩니다.")
        }
    }

    // Resolving a SAF location can query a content provider, so it stays off the UI thread.
    LaunchedEffect(receiveRoot) {
        receiveRootDisplay = withContext(Dispatchers.IO) { services.documents.displayPathForTree(receiveRoot) }
    }

    // Reloading on every repository write is what makes a finished transfer appear in the
    // list immediately instead of only after the app is restarted.
    LaunchedEffect(historyRevision, runningTransfers) {
        try {
            val loaded=services.transfers.recentHistory(runningTransfers)
            history=loaded
            historyLocations=withContext(Dispatchers.IO) {
                loaded.filter{it.direction==TransferDirection.RECEIVE}
                    .mapNotNull{item->
                        // Legacy rows only stored a content URI, so a readable name is
                        // derived instead of ever showing the URI itself.
                        val location=item.file.displayPath
                            ?: SafDisplayPath.fromDocumentUri(context,item.file.finalUri)
                        location?.let{item.file.fileId to it}
                    }
                    .toMap()
            }
        } catch(error:Throwable) {
            if(error is CancellationException)throw error
            services.log.error("android.compose.history.load.failed",error,JSONObject().put("startupStage","compose.history"))
            TransferRuntime.startupIssue("전송 기록을 불러오지 못했지만 앱은 계속 실행됩니다.")
        }
    }

    LaunchedEffect(Unit) {
        try {
            TransferRuntime.pairing.collect{pairing=it}
        } catch(error:Throwable) {
            if(error is CancellationException)throw error
            services.log.error("android.compose.pairing.collect.failed",error,JSONObject().put("startupStage","compose.pairing"))
        }
    }

    val chooseReceive = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        if (uri != null) {
            context.contentResolver.takePersistableUriPermission(
                uri,
                Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION,
            )
            scope.launch {
                services.settings.set("receive.tree.uri", uri.toString())
                receiveRoot = uri.toString()
            }
        }
    }
    val chooseFiles = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        val target = selectedId
        if (uris.isNotEmpty() && target != null) {
            val sources = uris.map { uri ->
                runCatching {
                    context.contentResolver.takePersistableUriPermission(
                        uri,
                        Intent.FLAG_GRANT_READ_URI_PERMISSION,
                    )
                }
                AndroidTransferCoordinator.querySource(context.contentResolver, uri)
            }
            scope.launch {
                when(val availability=TransferRuntime.awaitManualCoordinator(10_000)){
                    is ManualCoordinatorAvailability.Ready->availability.coordinator.queue(target,sources)
                    is ManualCoordinatorAvailability.Failed->TransferRuntime.startupIssue("연결 서비스를 사용할 수 없습니다: ${availability.service.reason}")
                    is ManualCoordinatorAvailability.TimedOut->TransferRuntime.startupIssue("연결 서비스 초기화 시간이 초과되었습니다.")
                }
            }
        }
    }
    val chooseFolder = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        val target = selectedId
        if (uri != null && target != null) {
            runCatching {
                context.contentResolver.takePersistableUriPermission(
                    uri,
                    Intent.FLAG_GRANT_READ_URI_PERMISSION,
                )
            }
            val root = DocumentFile.fromTreeUri(context, uri)
            val sources = root?.let { expandTree(context, it, it.name ?: "folder") }.orEmpty()
            scope.launch {
                when(val availability=TransferRuntime.awaitManualCoordinator(10_000)){
                    is ManualCoordinatorAvailability.Ready->availability.coordinator.queue(target,sources)
                    is ManualCoordinatorAvailability.Failed->TransferRuntime.startupIssue("연결 서비스를 사용할 수 없습니다: ${availability.service.reason}")
                    is ManualCoordinatorAvailability.TimedOut->TransferRuntime.startupIssue("연결 서비스 초기화 시간이 초과되었습니다.")
                }
            }
        }
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = {
                    Column {
                        Text("eslee QuickSend", fontWeight = FontWeight.SemiBold)
                        Text("연결이 끊겨도 자동으로 이어서 전송합니다", style = MaterialTheme.typography.labelSmall)
                    }
                },
                actions = {
                    IconButton(onClick = { chooseReceive.launch(null) }) {
                        Icon(Icons.Outlined.Settings, "설정")
                    }
                },
            )
        },
    ) { padding ->
        LazyColumn(
            modifier = Modifier.fillMaxSize().padding(padding).padding(horizontal = 18.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
            contentPadding = PaddingValues(vertical = 16.dp),
        ) {
            item {
                TransferCard(
                    ui = ui,
                    onFiles = { if (selectedId != null) chooseFiles.launch(arrayOf("*/*")) },
                    onFolder = { if (selectedId != null) chooseFolder.launch(null) },
                    onPause = { TransferRuntime.coordinator?.togglePause() },
                    onCancel = { TransferRuntime.coordinator?.cancel() },
                    enabled = selectedId != null,
                )
            }
            item { Text("주변 기기", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold) }
            if (devices.isEmpty()) {
                item {
                    Text(
                        "같은 네트워크의 QuickSend 기기를 찾는 중...",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
            items(devices, key = { it.deviceId }) { device ->
                DeviceCard(
                    device = device,
                    selected = selectedId == device.deviceId,
                    linkState = peerConnections.state(device.deviceId),
                    onClick = { selectedId = device.deviceId },
                    onDisconnect = {
                        coordinator?.disconnectPeer(device.deviceId)
                        historyNotice = "${device.name}와의 연결을 끊었습니다. 신뢰 기기 등록은 유지됩니다."
                    },
                    onReconnect = {
                        coordinator?.reconnectPeer(device.deviceId)
                        selectedId = device.deviceId
                        historyNotice = null
                    },
                )
            }
            item {
                Column(verticalArrangement=Arrangement.spacedBy(6.dp)) {
                    Text(
                        "기기가 보이지 않나요?",
                        style=MaterialTheme.typography.bodyMedium,
                        color=MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    OutlinedButton(onClick={
                        manualError=null
                        showManualConnect=true
                    }) {
                        Text("IP 주소로 직접 연결")
                    }
                }
            }
            item { ReceiveLocationCard(receiveRootDisplay, receiveRoot != null) { chooseReceive.launch(null) } }
            item { DeviceNameCard(deviceName) { renameInput = deviceName; renameError = null; showRename = true } }
            item {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text("전송 기록", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                    TextButton(onClick = { showClearHistory = true }) {
                        Text("기록 정리")
                    }
                }
            }
            historyNotice?.let { notice ->
                item {
                    Text(
                        notice,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.primary,
                    )
                }
            }
            if (history.isEmpty()) {
                item {
                    Text(
                        "아직 전송 기록이 없습니다",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
            items(history, key = { it.file.fileId }) { item ->
                val record = item.file
                val location = historyLocations[record.fileId]
                ListItem(
                    headlineContent = {
                        Text(record.relativePath.substringAfterLast('/'), maxLines = 1, overflow = TextOverflow.Ellipsis)
                    },
                    supportingContent = {
                        Column {
                            Text(
                                "${if (item.direction == TransferDirection.SEND) "보냄" else "받음"} · ${formatBytes(record.size)}",
                            )
                            location?.let {
                                Text(it, style = MaterialTheme.typography.labelSmall, maxLines = 2, overflow = TextOverflow.Ellipsis)
                            }
                        }
                    },
                    trailingContent = {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text(historyStateText(item))
                            IconButton(
                                enabled = item.canDelete,
                                onClick = { historyPendingDelete = item },
                            ) {
                                Icon(Icons.Outlined.Delete, "이 전송 기록 삭제")
                            }
                        }
                    },
                )
            }
        }
    }

    historyPendingDelete?.let { item ->
        AlertDialog(
            onDismissRequest = { historyPendingDelete = null },
            title = { Text("전송 기록을 삭제하시겠습니까?") },
            text = { Text("목록에서만 삭제합니다. 보낸 원본 파일과 받은 파일은 그대로 유지됩니다.") },
            confirmButton = {
                Button(onClick = {
                    historyPendingDelete = null
                    scope.launch {
                        try {
                            val deleted = services.transfers.deleteHistoryFile(item.file.fileId, runningTransfers)
                            services.log.info(
                                "android.history.file.delete",
                                JSONObject().put("fileId", item.file.fileId.toString()).put("deleted", deleted),
                            )
                            historyNotice = if (deleted) null else "진행 중인 전송 기록은 전송이 끝난 뒤 삭제할 수 있습니다."
                            history = services.transfers.recentHistory(runningTransfers)
                        } catch (error: Throwable) {
                            if (error is CancellationException) throw error
                            services.log.error("android.history.file.delete.failed", error)
                            TransferRuntime.startupIssue("전송 기록을 삭제하지 못했습니다.")
                        }
                    }
                }) { Text("기록 삭제") }
            },
            dismissButton = {
                TextButton(onClick = { historyPendingDelete = null }) { Text("취소") }
            },
        )
    }

    if (showClearHistory) {
        AlertDialog(
            onDismissRequest = { showClearHistory = false },
            title = { Text("완료된 전송 기록을 정리하시겠습니까?") },
            text = { Text("완료·취소·실패 기록과, 더 이상 진행되지 않는 오래된 기록을 목록에서 삭제합니다. 진행 중인 전송과 실제 파일은 그대로 유지됩니다.") },
            confirmButton = {
                Button(onClick = {
                    showClearHistory = false
                    scope.launch {
                        try {
                            val result = services.transfers.clearHistory(runningTransfers)
                            services.log.info(
                                "android.history.clear",
                                JSONObject()
                                    .put("deletedFiles", result.deletedFiles)
                                    .put("deletedJobs", result.deletedJobs)
                                    .put("keptRunningJobs", result.keptRunningJobs),
                            )
                            historyNotice = if (result.keptRunningJobs > 0) {
                                "진행 중인 전송 ${result.keptRunningJobs}건의 기록은 삭제하지 않았습니다."
                            } else {
                                null
                            }
                            history = services.transfers.recentHistory(runningTransfers)
                        } catch (error: Throwable) {
                            if (error is CancellationException) throw error
                            services.log.error("android.history.clear.failed", error)
                            TransferRuntime.startupIssue("전송 기록을 정리하지 못했습니다.")
                        }
                    }
                }) { Text("기록 정리") }
            },
            dismissButton = {
                TextButton(onClick = { showClearHistory = false }) { Text("취소") }
            },
        )
    }

    if (showRename) {
        AlertDialog(
            onDismissRequest = { showRename = false },
            title = { Text("기기 이름 변경") },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    Text("주변 기기 목록에 표시할 이름입니다. 한글을 사용할 수 있고 최대 ${DeviceNameRules.MAX_LENGTH}자입니다.")
                    Text(
                        "이름을 바꿔도 기기 인증과 신뢰 관계는 그대로 유지됩니다.",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    OutlinedTextField(
                        value = renameInput,
                        onValueChange = { renameInput = it; renameError = null },
                        label = { Text("기기 이름") },
                        singleLine = true,
                    )
                    renameError?.let {
                        Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
                    }
                }
            },
            confirmButton = {
                Button(onClick = {
                    scope.launch {
                        try {
                            when (val result = services.deviceNames.set(renameInput)) {
                                is DeviceNameUpdate.Rejected -> renameError = result.message
                                is DeviceNameUpdate.Accepted -> {
                                    services.log.info(
                                        "device.name.changed",
                                        JSONObject().put("name", result.name).put("identityPreserved", true),
                                    )
                                    showRename = false
                                }
                            }
                        } catch (error: Throwable) {
                            if (error is CancellationException) throw error
                            services.log.error("device.name.change.failed", error)
                            renameError = "기기 이름을 저장하지 못했습니다."
                        }
                    }
                }) { Text("저장") }
            },
            dismissButton = { TextButton(onClick = { showRename = false }) { Text("취소") } },
        )
    }

    pairing?.let { prompt ->
        AlertDialog(
            onDismissRequest = {},
            title = { Text("${prompt.deviceName}와 연결하시겠습니까?") },
            text = {
                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    Text("인증번호")
                    Text(prompt.code, style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.Bold)
                    Spacer(Modifier.height(8.dp))
                    Text("두 기기에 같은 번호가 표시되는지 확인하세요.")
                }
            },
            confirmButton = {
                Button(onClick = {
                    TransferRuntime.coordinator?.completePairing(prompt.id, true)
                    pairing = null
                }) { Text("신뢰") }
            },
            dismissButton = {
                TextButton(onClick = {
                    TransferRuntime.coordinator?.completePairing(prompt.id, false)
                    pairing = null
                }) { Text("거부") }
            },
        )
    }

    if(showManualConnect&&pairing==null){
        AlertDialog(
            onDismissRequest={if(!manualConnecting)showManualConnect=false},
            title={Text("IP 주소로 직접 연결")},
            text={
                Column(verticalArrangement=Arrangement.spacedBy(12.dp)) {
                    Text("Windows QuickSend에 표시된 IP 주소와 포트를 입력하세요.")
                    when(serviceState.phase){
                        TransferServicePhase.STARTING,TransferServicePhase.NOT_STARTED,TransferServicePhase.STOPPING->
                            Text(
                                "연결 준비 중입니다. 연결을 누르면 준비 완료 후 자동으로 계속합니다.",
                                style=MaterialTheme.typography.bodySmall,
                                color=MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        TransferServicePhase.DEGRADED->
                            Text(
                                "자동 발견은 제한되었지만 IP 직접 연결은 사용할 수 있습니다.",
                                style=MaterialTheme.typography.bodySmall,
                                color=MaterialTheme.colorScheme.primary,
                            )
                        TransferServicePhase.FAILED_FATAL->
                            Text(
                                "연결 서비스를 사용할 수 없습니다: ${serviceState.reason}",
                                style=MaterialTheme.typography.bodySmall,
                                color=MaterialTheme.colorScheme.error,
                            )
                        TransferServicePhase.READY->Unit
                    }
                    OutlinedTextField(
                        value=manualHost,
                        onValueChange={manualHost=it;manualError=null},
                        label={Text("IP 주소")},
                        placeholder={Text("예: 192.168.123.102")},
                        singleLine=true,
                        enabled=!manualConnecting,
                        keyboardOptions=KeyboardOptions(keyboardType=KeyboardType.Decimal),
                    )
                    OutlinedTextField(
                        value=manualPort,
                        onValueChange={manualPort=it;manualError=null},
                        label={Text("Port")},
                        placeholder={Text(ProtocolConstants.DEFAULT_PORT.toString())},
                        singleLine=true,
                        enabled=!manualConnecting,
                        keyboardOptions=KeyboardOptions(keyboardType=KeyboardType.Number),
                    )
                    manualError?.let{message->
                        Text(message,color=MaterialTheme.colorScheme.error,style=MaterialTheme.typography.bodySmall)
                    }
                }
            },
            confirmButton={
                Button(
                    enabled=!manualConnecting&&serviceState.phase!=TransferServicePhase.FAILED_FATAL,
                    onClick={
                        manualConnecting=true
                        manualError=null
                        scope.launch{
                            services.log.info(
                                "manual_connect.button.clicked",
                                JSONObject().put("host",manualHost.trim()).put("port",manualPort.trim()),
                            )
                            services.log.info(
                                "manual_connect.service_state",
                                JSONObject().put("state",serviceState.phase.name).put("reason",serviceState.reason),
                            )
                            if(!serviceState.manualConnectAllowed||TransferRuntime.coordinator==null){
                                services.log.info(
                                    "manual_connect.waiting_for_service",
                                    JSONObject().put("state",serviceState.phase.name).put("timeoutMillis",10_000),
                                )
                            }
                            when(val availability=TransferRuntime.awaitManualCoordinator(10_000)){
                                is ManualCoordinatorAvailability.Ready->{
                                    services.log.info(
                                        "manual_connect.service_ready",
                                        JSONObject().put("state",availability.service.phase.name),
                                    )
                                    try {
                                        when(val result=availability.coordinator.manualConnect(manualHost,manualPort)){
                                            is ManualConnectResult.Success->{
                                                selectedId=result.device.deviceId
                                                showManualConnect=false
                                            }
                                            is ManualConnectResult.Failure->manualError=result.userMessage
                                        }
                                    } catch(error:Throwable) {
                                        if(error is CancellationException)throw error
                                        services.log.error(
                                            "manual_connect.failed",
                                            error,
                                            JSONObject().put("stage","ui.dispatch"),
                                        )
                                        manualError="수동 연결을 시작하지 못했습니다: ${error.message ?: error.javaClass.simpleName}"
                                    }
                                }
                                is ManualCoordinatorAvailability.Failed->{
                                    manualError="연결 서비스를 사용할 수 없습니다: ${availability.service.reason}"
                                }
                                is ManualCoordinatorAvailability.TimedOut->{
                                    manualError="연결 서비스 초기화가 10초 안에 끝나지 않았습니다. 앱을 다시 열고 진단 로그를 확인하세요."
                                    services.log.error(
                                        "manual_connect.service_wait.timeout",
                                        IllegalStateException("Transfer service readiness timed out"),
                                        JSONObject()
                                            .put("state",availability.service.phase.name)
                                            .put("reason",availability.service.reason),
                                    )
                                }
                            }
                            if(!showManualConnect){
                                manualConnecting=false
                                return@launch
                            }
                            manualConnecting=false
                        }
                    },
                ) {
                    Text(
                        when {
                            manualConnecting&&!serviceState.manualConnectAllowed->"연결 준비 중..."
                            manualConnecting->"연결 중..."
                            serviceState.phase==TransferServicePhase.STARTING->"준비 후 연결"
                            else->"연결"
                        },
                    )
                }
            },
            dismissButton={
                TextButton(
                    enabled=!manualConnecting,
                    onClick={showManualConnect=false},
                ) { Text("취소") }
            },
        )
    }
}

@Composable
private fun TransferCard(
    ui: TransferUiState,
    onFiles: () -> Unit,
    onFolder: () -> Unit,
    onPause: () -> Unit,
    onCancel: () -> Unit,
    enabled: Boolean,
) {
    ElevatedCard {
        Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                Column {
                    Text("현재 전송", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
                    Text(ui.status, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                AssistChip(
                    onClick = {},
                    label = { Text("자동 이어받기 켜짐") },
                    leadingIcon = { Icon(Icons.Outlined.Autorenew, null) },
                )
            }
            Text(ui.currentFile, maxLines = 1, overflow = TextOverflow.Ellipsis, fontWeight = FontWeight.SemiBold)
            LinearProgressIndicator(progress = { ui.progress }, modifier = Modifier.fillMaxWidth())
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                Text("${formatBytes(ui.safeBytes)} / ${formatBytes(ui.totalBytes)}")
                Text("${formatBytes(ui.speedBytesPerSecond.toLong())}/s", fontWeight = FontWeight.SemiBold)
            }
            Text(
                ui.etaSeconds?.let { "약 ${formatEta(it)} 남음" } ?: "남은 시간 계산 중",
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(onClick = onFiles, enabled = enabled) {
                    Icon(Icons.AutoMirrored.Outlined.InsertDriveFile, null)
                    Spacer(Modifier.width(6.dp))
                    Text("파일")
                }
                OutlinedButton(onClick = onFolder, enabled = enabled) {
                    Icon(Icons.Outlined.Folder, null)
                    Spacer(Modifier.width(6.dp))
                    Text("폴더")
                }
                if (ui.canPause) IconButton(onClick = onPause) { Icon(Icons.Outlined.Pause, "일시정지") }
                if (ui.canCancel) IconButton(onClick = onCancel) { Icon(Icons.Outlined.Close, "취소") }
            }
        }
    }
}

@Composable
private fun DeviceCard(
    device: DiscoveredDevice,
    selected: Boolean,
    linkState: PeerLinkState,
    onClick: () -> Unit,
    onDisconnect: () -> Unit,
    onReconnect: () -> Unit,
) {
    val blocked = linkState == PeerLinkState.MANUALLY_DISCONNECTED
    // The card paints one container colour for its whole height. Selection is shown with
    // the border and the check icon instead of a filled container, because a filled
    // container is only covered by the ListItem's own opaque background and would leak
    // through the action row underneath as a coloured strip.
    OutlinedCard(
        onClick = onClick,
        colors = CardDefaults.outlinedCardColors(containerColor = MaterialTheme.colorScheme.surface),
        border = BorderStroke(
            width = if (selected) 2.dp else 1.dp,
            color = when {
                blocked -> MaterialTheme.colorScheme.outline
                selected -> MaterialTheme.colorScheme.primary
                else -> MaterialTheme.colorScheme.outlineVariant
            },
        ),
    ) {
        Column {
            ListItem(
                // Transparent so the card's single container colour is what shows through
                // everywhere, top and bottom alike.
                colors = ListItemDefaults.colors(containerColor = Color.Transparent),
                headlineContent = { Text(device.name, fontWeight = FontWeight.SemiBold) },
                supportingContent = {
                    Text(
                        when {
                            blocked -> "연결 끊김 - 다시 연결하면 이어집니다"
                            linkState == PeerLinkState.CONNECTED -> "연결됨"
                            device.online -> "온라인"
                            else -> "오프라인 - 자동 대기 중"
                        },
                    )
                },
                leadingContent = { Icon(Icons.Outlined.Computer, null) },
                trailingContent = {
                    if (selected) Icon(Icons.Outlined.CheckCircle, null, tint = MaterialTheme.colorScheme.primary)
                },
            )
            Row(
                modifier = Modifier.fillMaxWidth().padding(start = 16.dp, end = 16.dp, bottom = 12.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                if (blocked) {
                    Button(onClick = onReconnect) {
                        Icon(Icons.Outlined.Link, null)
                        Spacer(Modifier.width(6.dp))
                        Text("다시 연결")
                    }
                } else {
                    OutlinedButton(onClick = onDisconnect) {
                        Icon(Icons.Outlined.LinkOff, null)
                        Spacer(Modifier.width(6.dp))
                        Text("연결 끊기")
                    }
                }
                Text(
                    if (blocked) "신뢰 기기 등록은 유지됩니다" else "이 기기와의 연결만 끊습니다",
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

@Composable
private fun ReceiveLocationCard(displayPath: String, chosen: Boolean, onChoose: () -> Unit) {
    OutlinedCard {
        ListItem(
            headlineContent = { Text("받은 파일 위치") },
            supportingContent = {
                // The raw SAF content URI is never shown; only the readable location is.
                Text(
                    if (chosen) displayPath else "먼저 수신 폴더를 선택해 주세요",
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
            },
            leadingContent = { Icon(Icons.Outlined.FolderOpen, null) },
            trailingContent = { TextButton(onClick = onChoose) { Text("변경") } },
        )
    }
}

@Composable
private fun DeviceNameCard(name: String, onRename: () -> Unit) {
    OutlinedCard {
        ListItem(
            headlineContent = { Text("이 기기 이름") },
            supportingContent = { Text(name, maxLines = 1, overflow = TextOverflow.Ellipsis) },
            leadingContent = { Icon(Icons.Outlined.Smartphone, null) },
            trailingContent = { TextButton(onClick = onRename) { Text("변경") } },
        )
    }
}

private fun expandTree(context: Context, node: DocumentFile, path: String): List<SelectedSource> = buildList {
    for (child in node.listFiles()) {
        val childPath = "$path/${child.name ?: "file"}"
        if (child.isDirectory) {
            addAll(expandTree(context, child, childPath))
        } else {
            add(AndroidTransferCoordinator.querySource(context.contentResolver, child.uri, childPath))
        }
    }
}

private fun formatBytes(value: Long): String {
    var amount = value.coerceAtLeast(0).toDouble()
    val units = arrayOf("B", "KB", "MB", "GB", "TB")
    var index = 0
    while (amount >= 1024 && index < units.lastIndex) {
        amount /= 1024
        index++
    }
    return "%.1f %s".format(amount, units[index])
}

private fun formatEta(seconds: Long) = "%d분 %02d초".format(seconds / 60, seconds % 60)

private fun historyStateText(item: TransferHistoryItem): String = when {
    item.jobState == TransferState.COMPLETED -> "완료"
    item.jobState == TransferState.CANCELLED -> "취소됨"
    item.jobState == TransferState.FAILED_FATAL -> "실패"
    item.jobErrorCode == TransferHistoryPolicy.RESUME_EXPIRED_ERROR_CODE -> "이어받기 만료"
    item.jobErrorCode == TransferHistoryPolicy.MANUAL_DISCONNECT_ERROR_CODE -> "연결 끊김(수동)"
    item.jobState == TransferState.USER_ACTION_REQUIRED -> "확인 필요"
    item.jobState == TransferState.PAUSED -> "일시정지"
    item.jobState == TransferState.WAITING_DEVICE -> "이어받기 대기"
    !item.isRunning -> "중단됨"
    else -> "진행 중"
}
