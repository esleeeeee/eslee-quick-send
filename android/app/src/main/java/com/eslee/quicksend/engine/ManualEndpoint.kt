package com.eslee.quicksend.engine

import com.eslee.quicksend.discovery.DiscoveredDevice
import java.net.InetAddress

enum class ManualConnectFailure {
    INVALID_IP,
    INVALID_PORT,
    SERVICE_NOT_READY,
    CONNECTION_TIMEOUT,
    CONNECTION_REFUSED,
    TLS_AUTH_FAILED,
    PROTOCOL_NO_RESPONSE,
    VERSION_MISMATCH,
    PAIRING_FAILED,
    CONNECTION_FAILED,
}

data class ManualEndpoint(val address:InetAddress,val port:Int) {
    val display:String get()="${address.hostAddress}:$port"

    companion object {
        const val LAST_HOST_SETTING="manual.last.host"
        const val LAST_PORT_SETTING="manual.last.port"

        @JvmStatic
        fun parse(hostText:String,portText:String):ManualEndpoint {
            val host=hostText.trim()
            val segments=host.split('.')
            if(segments.size!=4 || segments.any{segment->
                    val value=segment.toIntOrNull()
                    segment.isEmpty() || segment.any{!it.isDigit()} || value==null || value !in 0..255
                }) {
                throw ManualEndpointValidationException(
                    ManualConnectFailure.INVALID_IP,
                    "IP 주소를 확인하세요. 예: 192.168.123.102",
                )
            }
            val bytes=segments.map{it.toInt().toByte()}.toByteArray()
            val port=portText.trim().toIntOrNull()
            if(port==null || port !in 1..65535) {
                throw ManualEndpointValidationException(
                    ManualConnectFailure.INVALID_PORT,
                    "포트는 1부터 65535 사이의 숫자로 입력하세요.",
                )
            }
            return ManualEndpoint(InetAddress.getByAddress(bytes),port)
        }
    }
}

class ManualEndpointValidationException(
    val reason:ManualConnectFailure,
    val userMessage:String,
):IllegalArgumentException(userMessage)

sealed interface ManualConnectResult {
    data class Success(val device:DiscoveredDevice):ManualConnectResult
    data class Failure(val reason:ManualConnectFailure,val userMessage:String):ManualConnectResult
}
