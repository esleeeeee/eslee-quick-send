package com.eslee.quicksend.service

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

class BootReceiver:BroadcastReceiver(){override fun onReceive(context:Context,intent:Intent){if(intent.action==Intent.ACTION_BOOT_COMPLETED)context.getSharedPreferences("recovery",Context.MODE_PRIVATE).edit().putBoolean("boot_pending",true).apply()}}

