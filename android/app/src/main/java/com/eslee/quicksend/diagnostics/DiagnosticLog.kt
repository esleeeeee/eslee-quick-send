package com.eslee.quicksend.diagnostics

import android.content.Context
import android.os.Build
import android.os.Process
import android.util.Log
import com.eslee.quicksend.BuildConfig
import org.json.JSONObject
import java.io.File
import java.time.Instant

class DiagnosticLog(context:Context) {
    private val directory=File(context.filesDir,"logs").apply{mkdirs()}
    private val current=File(directory,"quicksend.ndjson")
    private val gate=Any()

    fun info(event:String,fields:JSONObject=JSONObject())=write("info",event,fields,null)
    fun warn(event:String,error:Throwable,fields:JSONObject=JSONObject())=write("warn",event,fields,error)
    fun error(event:String,error:Throwable,fields:JSONObject=JSONObject())=write("error",event,fields,error)

    private fun write(level:String,event:String,fields:JSONObject,error:Throwable?){
        try {
            synchronized(gate){
                if(current.length()>5L*1024*1024){
                    val previous=File(directory,"quicksend.1.ndjson")
                    if(previous.exists())previous.delete()
                    current.renameTo(previous)
                }
                val line=JSONObject()
                    .put("at",Instant.now().toString())
                    .put("level",level)
                    .put("event",event)
                    .put("fields",fields)
                    .put("sdkInt",Build.VERSION.SDK_INT)
                    .put("appVersion",BuildConfig.VERSION_NAME)
                    .put("appVersionCode",BuildConfig.VERSION_CODE)
                    .put("processId",Process.myPid())
                    .put("thread",Thread.currentThread().name)
                if(error!=null){
                    line.put("errorType",error.javaClass.name)
                    line.put("errorMessage",error.message ?: JSONObject.NULL)
                    line.put("stackTrace",error.stackTraceToString())
                }
                current.appendText(line.toString()+"\n",Charsets.UTF_8)
            }
        } catch(logFailure:Throwable) {
            Log.e("eslee-QuickSend","Diagnostic logging failed for $event",logFailure)
            if(error!=null)Log.e("eslee-QuickSend",event,error)
        }
    }
}
