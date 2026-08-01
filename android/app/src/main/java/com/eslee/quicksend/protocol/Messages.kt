package com.eslee.quicksend.protocol

import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID

data class HelloMessage(
    val protocolVersion: Int,
    val appVersion: String,
    val deviceId: String,
    val deviceName: String,
    val platform: String,
    val identityFingerprint: String,
    val capabilities: List<String>,
) {
    fun json() = JSONObject().put("protocolVersion", protocolVersion).put("appVersion", appVersion)
        .put("deviceId", deviceId).put("deviceName", deviceName).put("platform", platform)
        .put("identityFingerprint", identityFingerprint).put("capabilities", jsonArray(capabilities))

    companion object {
        fun parse(json: JSONObject) = HelloMessage(json.getInt("protocolVersion"), json.getString("appVersion"),
            json.getString("deviceId"), json.getString("deviceName"), json.getString("platform"),
            json.getString("identityFingerprint"), json.stringList("capabilities"))
    }
}

data class ManifestFile(val fileId: UUID, val relativePath: String, val size: Long, val modifiedUtcTicks: Long, val chunkSize: Int, val stableSourceId: String?) {
    fun json() = JSONObject().put("fileId", fileId.toString()).put("relativePath", relativePath).put("size", size)
        .put("modifiedUtcTicks", modifiedUtcTicks).put("chunkSize", chunkSize).putOpt("stableSourceId", stableSourceId)
    companion object { fun parse(j: JSONObject) = ManifestFile(UUID.fromString(j.getString("fileId")), j.getString("relativePath"), j.getLong("size"), j.getLong("modifiedUtcTicks"), j.getInt("chunkSize"), j.optString("stableSourceId").ifBlank { null }) }
}

data class ManifestMessage(val transferId: UUID, val sourceDeviceId: String, val destinationDeviceId: String, val files: List<ManifestFile>) {
    fun json() = JSONObject().put("transferId", transferId.toString()).put("sourceDeviceId", sourceDeviceId)
        .put("destinationDeviceId", destinationDeviceId).put("files", JSONArray().apply { files.forEach { put(it.json()) } })
    companion object { fun parse(j: JSONObject): ManifestMessage { val a=j.getJSONArray("files"); return ManifestMessage(UUID.fromString(j.getString("transferId")),j.getString("sourceDeviceId"),j.getString("destinationDeviceId"),List(a.length()){ManifestFile.parse(a.getJSONObject(it))}) } }
}

data class FileStartMessage(val transferId: UUID, val fileId: UUID, val relativePath: String, val size: Long, val modifiedUtcTicks: Long, val chunkSize: Int, val stableSourceId: String?) {
    fun json() = JSONObject().put("transferId", transferId.toString()).put("fileId", fileId.toString()).put("relativePath", relativePath)
        .put("size", size).put("modifiedUtcTicks", modifiedUtcTicks).put("chunkSize", chunkSize).putOpt("stableSourceId", stableSourceId)
    companion object { fun parse(j: JSONObject)=FileStartMessage(UUID.fromString(j.getString("transferId")),UUID.fromString(j.getString("fileId")),j.getString("relativePath"),j.getLong("size"),j.getLong("modifiedUtcTicks"),j.getInt("chunkSize"),j.optString("stableSourceId").ifBlank{null}) }
}

data class ResumeInfoMessage(val transferId: UUID,val fileId: UUID,val committedOffset: Long,val committedLeaves: Int,val merkleSnapshotBase64: String) {
    fun json()=JSONObject().put("transferId",transferId.toString()).put("fileId",fileId.toString()).put("committedOffset",committedOffset).put("committedLeaves",committedLeaves).put("merkleSnapshotBase64",merkleSnapshotBase64)
    companion object { fun parse(j:JSONObject)=ResumeInfoMessage(UUID.fromString(j.getString("transferId")),UUID.fromString(j.getString("fileId")),j.getLong("committedOffset"),j.getInt("committedLeaves"),j.optString("merkleSnapshotBase64")) }
}

data class ChunkAckMessage(val fileId: UUID,val offset: Long,val length: Int,val receivedOffset: Long) {
    fun json()=JSONObject().put("fileId",fileId.toString()).put("offset",offset).put("length",length).put("receivedOffset",receivedOffset)
    companion object { fun parse(j:JSONObject)=ChunkAckMessage(UUID.fromString(j.getString("fileId")),j.getLong("offset"),j.getInt("length"),j.getLong("receivedOffset")) }
}

data class CheckpointMessage(val fileId:UUID,val committedOffset:Long,val committedLeaves:Int,val merkleSnapshotBase64:String) {
    fun json()=JSONObject().put("fileId",fileId.toString()).put("committedOffset",committedOffset).put("committedLeaves",committedLeaves).put("merkleSnapshotBase64",merkleSnapshotBase64)
    companion object { fun parse(j:JSONObject)=CheckpointMessage(UUID.fromString(j.getString("fileId")),j.getLong("committedOffset"),j.getInt("committedLeaves"),j.getString("merkleSnapshotBase64")) }
}

data class FileCompleteMessage(val fileId:UUID,val size:Long,val leafCount:Int,val merkleRootBase64:String) {
    fun json()=JSONObject().put("fileId",fileId.toString()).put("size",size).put("leafCount",leafCount).put("merkleRootBase64",merkleRootBase64)
    companion object { fun parse(j:JSONObject)=FileCompleteMessage(UUID.fromString(j.getString("fileId")),j.getLong("size"),j.getInt("leafCount"),j.getString("merkleRootBase64")) }
}

data class FileVerifyMessage(val fileId:UUID,val verified:Boolean,val errorCode:String?=null) {
    fun json()=JSONObject().put("fileId",fileId.toString()).put("verified",verified).putOpt("errorCode",errorCode)
    companion object { fun parse(j:JSONObject)=FileVerifyMessage(UUID.fromString(j.getString("fileId")),j.getBoolean("verified"),j.optString("errorCode").ifBlank{null}) }
}

fun InboundFrame.json(): JSONObject = JSONObject(payload.toString(Charsets.UTF_8))

