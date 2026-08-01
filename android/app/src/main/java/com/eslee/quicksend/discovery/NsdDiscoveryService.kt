package com.eslee.quicksend.discovery

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.os.Build
import com.eslee.quicksend.diagnostics.DiagnosticLog
import com.eslee.quicksend.engine.DeviceNameWire
import com.eslee.quicksend.protocol.ProtocolConstants
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import org.json.JSONObject
import java.net.Inet4Address
import java.net.InetAddress
import java.util.concurrent.ConcurrentHashMap

data class DiscoveredDevice(
    val deviceId:String,
    val name:String,
    val address:InetAddress,
    val port:Int,
    val fingerprint:String,
    val online:Boolean,
    val lastSeen:Long,
)

enum class NsdHealth { NOT_STARTED,STARTING,READY,FAILED }

class NsdDiscoveryService(context:Context,private val log:DiagnosticLog) {
    private val nsd=context.getSystemService(NsdManager::class.java)
    private val connectivity=context.getSystemService(ConnectivityManager::class.java)
    private val entries=ConcurrentHashMap<String,DiscoveredDevice>()
    private val manualDeviceIds=ConcurrentHashMap.newKeySet<String>()
    private val resolving=ConcurrentHashMap.newKeySet<String>()
    private val _devices=MutableStateFlow<List<DiscoveredDevice>>(emptyList())
    val devices:StateFlow<List<DiscoveredDevice>> = _devices
    private val _health=MutableStateFlow(NsdHealth.NOT_STARTED)
    val health:StateFlow<NsdHealth> = _health
    val networkEvents=MutableStateFlow(0L)

    @Volatile private var localId:String?=null
    @Volatile private var localName:String?=null
    @Volatile private var localFingerprint:String?=null
    @Volatile private var localPort:Int=ProtocolConstants.DEFAULT_PORT
    @Volatile private var started=false
    @Volatile private var networkCallbackRegistered=false
    @Volatile private var registrationReady=false
    @Volatile private var discoveryReady=false
    private var registration:NsdManager.RegistrationListener?=null
    private var discovery:NsdManager.DiscoveryListener?=null

    private val networkCallback=object:ConnectivityManager.NetworkCallback(){
        override fun onAvailable(network:Network){
            networkEvents.value=System.nanoTime()
            log.info("android.network.available",networkSnapshot())
            refresh()
        }
        override fun onLost(network:Network){
            networkEvents.value=System.nanoTime()
            log.info("android.network.lost",JSONObject().put("network",network.toString()))
        }
        override fun onCapabilitiesChanged(network:Network,capabilities:NetworkCapabilities){
            networkEvents.value=System.nanoTime()
        }
    }

    @Synchronized
    fun start(deviceId:String,name:String,fingerprint:String,port:Int) {
        if(started){
            log.info("android.nsd.start.success",JSONObject().put("alreadyStarted",true))
            return
        }
        started=true
        registrationReady=false
        discoveryReady=false
        _health.value=NsdHealth.STARTING
        localId=deviceId
        localName=name
        localFingerprint=fingerprint
        localPort=port
        log.info(
            "android.nsd.start.begin",
            networkSnapshot()
                .put("serviceType",ProtocolConstants.SERVICE_TYPE)
                .put("serviceName",deviceId)
                .put("port",port)
                .put("protocol","PROTOCOL_DNS_SD"),
        )

        val info=buildServiceInfo(deviceId,name,fingerprint,port)
        try {
            registration=object:NsdManager.RegistrationListener{
                override fun onRegistrationFailed(serviceInfo:NsdServiceInfo,errorCode:Int){
                    registration=null
                    registrationReady=false
                    _health.value=NsdHealth.FAILED
                    logCallbackFailure("android.nsd.registration.failed",errorCode,serviceInfo)
                }
                override fun onUnregistrationFailed(serviceInfo:NsdServiceInfo,errorCode:Int){
                    logCallbackFailure("android.nsd.unregistration.failed",errorCode,serviceInfo)
                }
                override fun onServiceRegistered(serviceInfo:NsdServiceInfo){
                    registrationReady=true
                    updateHealthIfReady()
                    log.info(
                        "android.nsd.registration.success",
                        JSONObject()
                            .put("serviceName",serviceInfo.serviceName)
                            .put("serviceType",serviceInfo.serviceType)
                            .put("port",serviceInfo.port),
                    )
                }
                override fun onServiceUnregistered(serviceInfo:NsdServiceInfo){
                    log.info("android.nsd.unregistration.success",JSONObject().put("serviceName",serviceInfo.serviceName))
                }
            }.also{nsd.registerService(info,NsdManager.PROTOCOL_DNS_SD,it)}
            startDiscovery()
            connectivity.registerDefaultNetworkCallback(networkCallback)
            networkCallbackRegistered=true
        } catch(error:Throwable) {
            log.error("android.nsd.start.failed",error,networkSnapshot().put("startupStage","nsd.start"))
            stop()
            _health.value=NsdHealth.FAILED
            throw error
        }
    }

    /**
     * Re-advertises with a new TXT `name` value. The service name, type, port, `id` and
     * `fp` are unchanged, so peers keep the same device entry and no re-pairing happens.
     */
    @Synchronized
    fun updateDeviceName(name:String) {
        val deviceId=localId ?: return
        val fingerprint=localFingerprint ?: return
        val listener=registration ?: return
        if(localName==name)return
        localName=name
        runCatching{nsd.unregisterService(listener)}.onFailure{
            log.error("android.nsd.unregistration.failed",it,JSONObject().put("startupStage","nsd.rename"))
        }
        registration=null
        registrationReady=false
        val info=buildServiceInfo(deviceId,name,fingerprint,localPort)
        runCatching{
            registration=object:NsdManager.RegistrationListener{
                override fun onRegistrationFailed(serviceInfo:NsdServiceInfo,errorCode:Int){
                    registration=null
                    registrationReady=false
                    _health.value=NsdHealth.FAILED
                    logCallbackFailure("android.nsd.registration.failed",errorCode,serviceInfo)
                }
                override fun onUnregistrationFailed(serviceInfo:NsdServiceInfo,errorCode:Int){
                    logCallbackFailure("android.nsd.unregistration.failed",errorCode,serviceInfo)
                }
                override fun onServiceRegistered(serviceInfo:NsdServiceInfo){
                    registrationReady=true
                    updateHealthIfReady()
                    log.info(
                        "android.nsd.txt.name.updated",
                        JSONObject()
                            .put("serviceName",serviceInfo.serviceName)
                            .put("advertisedName",DeviceNameWire.encode(name))
                            .put("port",serviceInfo.port),
                    )
                }
                override fun onServiceUnregistered(serviceInfo:NsdServiceInfo){
                    log.info("android.nsd.unregistration.success",JSONObject().put("serviceName",serviceInfo.serviceName))
                }
            }.also{nsd.registerService(info,NsdManager.PROTOCOL_DNS_SD,it)}
        }.onFailure{
            registration=null
            _health.value=NsdHealth.FAILED
            log.error("android.nsd.start.failed",it,JSONObject().put("startupStage","nsd.rename"))
        }
    }

    private fun buildServiceInfo(deviceId:String,name:String,fingerprint:String,port:Int):NsdServiceInfo =
        NsdServiceInfo().apply{
            serviceName=deviceId
            serviceType=ProtocolConstants.SERVICE_TYPE
            setPort(port)
            setAttribute("id",deviceId)
            // Percent-encoded UTF-8 keeps Hangul names transportable in an ASCII TXT value.
            setAttribute("name",DeviceNameWire.encode(name))
            setAttribute("fp",fingerprint)
            setAttribute("v",ProtocolConstants.VERSION.toString())
        }

    @Synchronized
    fun refresh(){
        if(!started || discovery!=null)return
        discoveryReady=false
        _health.value=NsdHealth.STARTING
        runCatching{startDiscovery()}.onFailure{
            _health.value=NsdHealth.FAILED
            log.error("android.nsd.start.failed",it,networkSnapshot().put("startupStage","nsd.refresh"))
        }
    }

    private fun startDiscovery(){
        val listener=object:NsdManager.DiscoveryListener{
            override fun onDiscoveryStarted(serviceType:String){
                discoveryReady=true
                updateHealthIfReady()
                log.info(
                    "android.nsd.start.success",
                    networkSnapshot().put("serviceType",serviceType).put("active",true),
                )
            }
            override fun onStartDiscoveryFailed(serviceType:String,errorCode:Int){
                discovery=null
                discoveryReady=false
                _health.value=NsdHealth.FAILED
                logCallbackFailure("android.nsd.start.failed",errorCode,null,serviceType)
            }
            override fun onStopDiscoveryFailed(serviceType:String,errorCode:Int){
                discovery=null
                discoveryReady=false
                if(started)_health.value=NsdHealth.FAILED
                logCallbackFailure("android.nsd.stop.failed",errorCode,null,serviceType)
            }
            override fun onDiscoveryStopped(serviceType:String){
                discovery=null
                discoveryReady=false
                if(started)_health.value=NsdHealth.FAILED
                log.info("android.nsd.stopped",JSONObject().put("serviceType",serviceType))
            }
            override fun onServiceFound(serviceInfo:NsdServiceInfo){
                log.info(
                    "android.nsd.service_found",
                    JSONObject()
                        .put("serviceName",serviceInfo.serviceName)
                        .put("serviceType",serviceInfo.serviceType),
                )
                if(serviceInfo.serviceName!=localId)resolve(serviceInfo)
            }
            override fun onServiceLost(serviceInfo:NsdServiceInfo){
                val retained=serviceInfo.serviceName in manualDeviceIds
                if(!retained){
                    entries.computeIfPresent(serviceInfo.serviceName){_,device->
                        device.copy(online=false,lastSeen=System.currentTimeMillis())
                    }
                }
                publish()
                log.info(
                    "android.nsd.service_lost",
                    JSONObject()
                        .put("serviceName",serviceInfo.serviceName)
                        .put("serviceType",serviceInfo.serviceType)
                        .put("manualEndpointRetained",retained),
                )
            }
        }
        discovery=listener
        try {
            nsd.discoverServices(ProtocolConstants.SERVICE_TYPE,NsdManager.PROTOCOL_DNS_SD,listener)
        } catch(error:Throwable) {
            discovery=null
            throw error
        }
    }

    @Suppress("DEPRECATION")
    private fun resolve(info:NsdServiceInfo){
        val key="${info.serviceName}|${info.serviceType}"
        if(!resolving.add(key))return
        log.info(
            "android.nsd.resolve.begin",
            JSONObject().put("serviceName",info.serviceName).put("serviceType",info.serviceType),
        )
        try {
            nsd.resolveService(info,object:NsdManager.ResolveListener{
                override fun onResolveFailed(serviceInfo:NsdServiceInfo,errorCode:Int){
                    resolving.remove(key)
                    logCallbackFailure("android.nsd.resolve.failed",errorCode,serviceInfo)
                }
                override fun onServiceResolved(serviceInfo:NsdServiceInfo){
                    resolving.remove(key)
                    val id=serviceInfo.attributes["id"]?.toString(Charsets.UTF_8)?:serviceInfo.serviceName
                    if(id==localId)return
                    val addresses=if(Build.VERSION.SDK_INT>=34)serviceInfo.hostAddresses else listOfNotNull(serviceInfo.host)
                    val address=addresses.firstOrNull{it is Inet4Address}?:addresses.firstOrNull()
                    if(address==null){
                        log.error(
                            "android.nsd.resolve.failed",
                            IllegalStateException("Resolved service has no address"),
                            JSONObject().put("serviceName",serviceInfo.serviceName),
                        )
                        return
                    }
                    val advertisedName=serviceInfo.attributes["name"]?.toString(Charsets.UTF_8)
                    val device=DiscoveredDevice(
                        id,
                        DeviceNameWire.decode(advertisedName).takeIf{it.isNotBlank()}?:serviceInfo.serviceName,
                        address,
                        serviceInfo.port,
                        serviceInfo.attributes["fp"]?.toString(Charsets.UTF_8).orEmpty(),
                        true,
                        System.currentTimeMillis(),
                    )
                    entries[id]=device
                    publish()
                    log.info(
                        "android.nsd.resolve.success",
                        JSONObject()
                            .put("serviceName",serviceInfo.serviceName)
                            .put("serviceType",serviceInfo.serviceType)
                            .put("deviceId",id)
                            .put("address",address.hostAddress)
                            .put("allAddresses",addresses.joinToString(","){it.hostAddress.orEmpty()})
                            .put("port",serviceInfo.port)
                            .put("txtKeys",serviceInfo.attributes.keys.sorted().joinToString(",")),
                    )
                }
            })
        } catch(error:Throwable) {
            resolving.remove(key)
            log.error(
                "android.nsd.resolve.failed",
                error,
                JSONObject().put("startupStage","nsd.resolve").put("serviceName",info.serviceName),
            )
        }
    }

    private fun logCallbackFailure(event:String,errorCode:Int,info:NsdServiceInfo?,serviceType:String?=info?.serviceType){
        log.error(
            event,
            IllegalStateException("NSD error code $errorCode"),
            JSONObject()
                .put("startupStage","nsd.callback")
                .put("errorCode",errorCode)
                .put("serviceName",info?.serviceName ?: JSONObject.NULL)
                .put("serviceType",serviceType ?: JSONObject.NULL),
        )
    }

    private fun networkSnapshot():JSONObject{
        val result=JSONObject()
        return try {
            val network=connectivity.activeNetwork
            val capabilities=network?.let(connectivity::getNetworkCapabilities)
            val links=network?.let(connectivity::getLinkProperties)
            val ipv4=links?.linkAddresses?.firstOrNull{it.address is Inet4Address&&!it.address.isLoopbackAddress}
            result
                .put("activeNetwork",network?.toString() ?: JSONObject.NULL)
                .put("transport",when{
                    capabilities?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)==true->"WiFi"
                    capabilities?.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)==true->"Ethernet"
                    capabilities?.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR)==true->"Cellular"
                    capabilities?.hasTransport(NetworkCapabilities.TRANSPORT_VPN)==true->"VPN"
                    else->"OtherOrUnavailable"
                })
                .put("localIpv4",ipv4?.address?.hostAddress ?: JSONObject.NULL)
                .put("prefixLength",ipv4?.prefixLength ?: JSONObject.NULL)
        } catch(error:Throwable) {
            result.put("networkSnapshotError",error.javaClass.name).put("networkSnapshotMessage",error.message)
        }
    }

    private fun publish(){_devices.value=entries.values.sortedBy{it.name.lowercase()}}
    private fun updateHealthIfReady(){
        if(started&&registrationReady&&discoveryReady)_health.value=NsdHealth.READY
    }
    fun find(id:String)=entries[id]

    fun upsertManualPeer(deviceId:String,name:String,address:InetAddress,port:Int,fingerprint:String):DiscoveredDevice {
        val device=DiscoveredDevice(deviceId,name,address,port,fingerprint,true,System.currentTimeMillis())
        manualDeviceIds.add(deviceId)
        entries[deviceId]=device
        publish()
        log.info(
            "android.nsd.manual_peer.added",
            JSONObject()
                .put("deviceId",deviceId)
                .put("deviceName",name)
                .put("address",address.hostAddress)
                .put("port",port),
        )
        return device
    }

    @Synchronized
    fun stop(){
        started=false
        registrationReady=false
        discoveryReady=false
        _health.value=NsdHealth.NOT_STARTED
        registration?.let{listener->
            runCatching{nsd.unregisterService(listener)}.onFailure{
                log.error("android.nsd.unregistration.failed",it,JSONObject().put("startupStage","nsd.stop"))
            }
        }
        registration=null
        discovery?.let{listener->
            runCatching{nsd.stopServiceDiscovery(listener)}.onFailure{
                log.error("android.nsd.stop.failed",it,JSONObject().put("startupStage","nsd.stop"))
            }
        }
        discovery=null
        if(networkCallbackRegistered){
            runCatching{connectivity.unregisterNetworkCallback(networkCallback)}.onFailure{
                log.error("android.network.callback.stop.failed",it,JSONObject().put("startupStage","nsd.stop"))
            }
        }
        networkCallbackRegistered=false
        resolving.clear()
        manualDeviceIds.clear()
        entries.clear()
        publish()
    }
}
