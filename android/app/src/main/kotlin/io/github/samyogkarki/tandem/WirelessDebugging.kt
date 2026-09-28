package io.github.samyogkarki.tandem

import android.Manifest
import android.app.PendingIntent
import android.app.job.JobInfo
import android.app.job.JobParameters
import android.app.job.JobScheduler
import android.app.job.JobService
import android.content.BroadcastReceiver
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.util.Log
import java.net.Inet4Address
import java.security.MessageDigest

/**
 * Turns Wireless debugging back on by itself, so the PC reconnects after the phone restarts
 * or drops off Wi-Fi. Android switches Wireless debugging off at every boot and whenever
 * Wi-Fi disconnects; without this, the user would have to dig through Developer options again.
 *
 * Only on Wi-Fi networks where the PC has actually connected before (we can't read the
 * network's name without location access, so we recognise it by its router, DHCP server,
 * subnet and DNS). Android adds its own check on top: on a network the user never allowed
 * Wireless debugging on, it asks instead of switching on.
 *
 * How we notice, without keeping a process running:
 *  - after a restart: [BootReceiver];
 *  - Wireless debugging switched off: [AdbWifiJob], a job Android runs when the setting changes.
 *    If Wi-Fi is down, Wi-Fi dropped: wait for it. If Wi-Fi is still up, the user switched it
 *    off: leave it off on this network until the PC connects again;
 *  - Wi-Fi back: a one-shot network request that wakes [WifiReceiver]. (Android fires these
 *    once and then drops them, which is why they're only armed while Wi-Fi is down.)
 *
 * Needs WRITE_SECURE_SETTINGS, which the Tandem PC app grants over adb at setup.
 */
object WirelessDebugging {
    const val TAG = "TandemWifiAdb"
    private const val PREFS = "tandem"
    private const val KEY_NETWORKS = "trusted_networks"
    private const val KEY_ENABLED = "auto_reconnect"
    private const val KEY_USER_OFF = "user_off_network"
    private const val MAX_NETWORKS = 10
    private const val JOB_ID = 1
    private const val ADB_WIFI_ENABLED = "adb_wifi_enabled" // Settings.Global.ADB_WIFI_ENABLED (hidden)

    /** Whether the PC granted the permission this needs. */
    fun canWrite(context: Context): Boolean =
        context.checkSelfPermission(Manifest.permission.WRITE_SECURE_SETTINGS) == PackageManager.PERMISSION_GRANTED

    /** The user's switch on the status screen (on by default). */
    fun isEnabled(context: Context): Boolean = prefs(context).getBoolean(KEY_ENABLED, true)

    fun setEnabled(context: Context, enabled: Boolean) {
        prefs(context).edit().putBoolean(KEY_ENABLED, enabled).apply()
        if (enabled) arm(context)
    }

    /** True when this phone will turn Wireless debugging back on by itself. */
    fun isActive(context: Context): Boolean = canWrite(context) && isEnabled(context)

    fun isOn(context: Context): Boolean =
        Settings.Global.getInt(context.contentResolver, ADB_WIFI_ENABLED, 0) == 1

    fun trustedNetworkCount(context: Context): Int = trusted(context).size

    /**
     * The PC just connected: remember the Wi-Fi we're on as one where Wireless debugging is
     * wanted, and forget an earlier "switched off by the user" (they've switched it back on).
     */
    fun onPcConnected(context: Context) {
        if (!isOn(context)) return // connected over USB only; nothing to learn
        prefs(context).edit().remove(KEY_USER_OFF).apply()
        val id = currentWifiId(context) ?: return
        val list = trusted(context)
        if (list.firstOrNull() != id) {
            list.remove(id)
            list.add(0, id)
            prefs(context).edit().putString(KEY_NETWORKS, list.take(MAX_NETWORKS).joinToString(",")).apply()
            Log.i(TAG, "remembered this Wi-Fi ($id; ${list.size.coerceAtMost(MAX_NETWORKS)} known)")
        }
        arm(context)
    }

    /**
     * Asks Android to run [AdbWifiJob] the next time Wireless debugging is switched on or off.
     * Content-triggered jobs run once, so the job re-arms itself; they don't survive a restart,
     * so [BootReceiver] and the listener arm it too. Scheduling again just replaces it.
     */
    fun arm(context: Context) {
        if (!isActive(context)) return
        val job = JobInfo.Builder(JOB_ID, ComponentName(context, AdbWifiJob::class.java))
            .addTriggerContentUri(JobInfo.TriggerContentUri(Settings.Global.getUriFor(ADB_WIFI_ENABLED), 0))
            .setTriggerContentUpdateDelay(500)
            .setTriggerContentMaxDelay(2_000)
            .build()
        val result = context.getSystemService(JobScheduler::class.java).schedule(job)
        if (result != JobScheduler.RESULT_SUCCESS) Log.w(TAG, "Couldn't watch Wireless debugging")
    }

    /** Wireless debugging was switched on or off (from [AdbWifiJob]). */
    fun onSettingChanged(context: Context) {
        if (!isActive(context)) return
        if (isOn(context)) {
            prefs(context).edit().remove(KEY_USER_OFF).apply()
            return
        }
        val here = currentWifiId(context)
        if (here == null) {
            Log.i(TAG, "Wireless debugging went off with Wi-Fi; waiting for Wi-Fi")
            waitForWifi(context)
        } else {
            // Still on the same Wi-Fi, so this wasn't a drop: someone switched it off on purpose.
            Log.i(TAG, "Wireless debugging switched off on this Wi-Fi; leaving it off here")
            prefs(context).edit().putString(KEY_USER_OFF, here).apply()
        }
    }

    /**
     * Turns Wireless debugging on now if we're on a remembered Wi-Fi, or once Wi-Fi connects.
     * Android switches it straight back off if Wi-Fi isn't fully up yet, so wait a moment
     * first and try once more. Calls [done] when finished.
     */
    fun enableSoon(context: Context, why: String, done: () -> Unit = {}) {
        if (!isActive(context) || isOn(context)) return done()
        if (currentWifiId(context) == null) {
            waitForWifi(context)
            return done()
        }
        val main = Handler(Looper.getMainLooper())
        main.postDelayed({
            enableIfTrusted(context, why)
            main.postDelayed({
                if (!isOn(context)) enableIfTrusted(context, "$why, retry")
                done()
            }, 5_000)
        }, 2_000)
    }

    private fun enableIfTrusted(context: Context, why: String): Boolean {
        if (!isActive(context)) return false
        if (isOn(context)) return true
        val cm = context.getSystemService(ConnectivityManager::class.java)
        val lp = wifiNetwork(cm)?.let(cm::getLinkProperties) ?: return false
        val id = fingerprint(lp) ?: return false
        if (id !in trusted(context)) {
            Log.i(TAG, "$why: this Wi-Fi ($id: ${describe(lp)}) isn't one the PC has used; leaving Wireless debugging off")
            return false
        }
        if (prefs(context).getString(KEY_USER_OFF, null) == id) {
            Log.i(TAG, "$why: Wireless debugging was switched off on this Wi-Fi; leaving it off")
            return false
        }
        return try {
            Settings.Global.putInt(context.contentResolver, ADB_WIFI_ENABLED, 1)
            Log.i(TAG, "$why: turned Wireless debugging on")
            true
        } catch (e: SecurityException) {
            Log.w(TAG, "$why: not allowed to change Wireless debugging: ${e.message}")
            false
        }
    }

    /** Wakes [WifiReceiver] once, when a Wi-Fi network connects (straight away if one already is). */
    private fun waitForWifi(context: Context) {
        val cm = context.getSystemService(ConnectivityManager::class.java)
        val request = NetworkRequest.Builder()
            .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
            .removeCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) // home Wi-Fi without internet still counts
            .build()
        try {
            cm.registerNetworkCallback(request, wifiIntent(context))
        } catch (e: Exception) {
            Log.w(TAG, "Couldn't wait for Wi-Fi: ${e.message}")
        }
    }

    private fun currentWifiId(context: Context): String? {
        val cm = context.getSystemService(ConnectivityManager::class.java)
        return wifiNetwork(cm)?.let(cm::getLinkProperties)?.let(::fingerprint)
    }

    private fun wifiNetwork(cm: ConnectivityManager): Network? {
        val active = cm.activeNetwork
        if (active != null && cm.getNetworkCapabilities(active)?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true)
            return active
        // e.g. a VPN is the active network; the Wi-Fi underneath is what matters.
        @Suppress("DEPRECATION")
        return cm.allNetworks.firstOrNull {
            val caps = cm.getNetworkCapabilities(it) ?: return@firstOrNull false
            caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) && !caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)
        }
    }

    /** A stable, private id for "this Wi-Fi": a hash of its router, DHCP server, subnet, DNS and domain. */
    private fun fingerprint(lp: LinkProperties): String? {
        val raw = describe(lp) ?: return null
        val digest = MessageDigest.getInstance("SHA-256").digest(raw.toByteArray(Charsets.UTF_8))
        return digest.take(12).joinToString("") { "%02x".format(it.toInt() and 0xff) }
    }

    /** What the fingerprint is made of (local addresses only; safe to log). */
    private fun describe(lp: LinkProperties): String? {
        val v4 = lp.linkAddresses.firstOrNull { it.address is Inet4Address } ?: return null
        val prefix = v4.prefixLength
        val bytes = v4.address.address.clone()
        for (i in bytes.indices) {
            val keep = (prefix - i * 8).coerceIn(0, 8)
            bytes[i] = (bytes[i].toInt() and (0xff shl (8 - keep))).toByte()
        }
        val subnet = bytes.joinToString(".") { (it.toInt() and 0xff).toString() } + "/$prefix"
        val gateway = lp.routes.firstOrNull { it.isDefaultRoute && it.gateway is Inet4Address }?.gateway?.hostAddress
        val dhcp = lp.dhcpServerAddress?.hostAddress
        val dns = lp.dnsServers.filterIsInstance<Inet4Address>().mapNotNull { it.hostAddress }.sorted().joinToString("+")
        return "net=$subnet;gw=$gateway;dhcp=$dhcp;dns=$dns;dom=${lp.domains.orEmpty()}"
    }

    private fun trusted(context: Context): MutableList<String> =
        prefs(context).getString(KEY_NETWORKS, null)?.split(',')?.filter { it.isNotBlank() }?.toMutableList()
            ?: mutableListOf()

    private fun prefs(context: Context) = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    private fun wifiIntent(context: Context): PendingIntent = PendingIntent.getBroadcast(
        context, 0, Intent(context, WifiReceiver::class.java),
        // Mutable: Android attaches the network to it. Explicit intent, so nobody else can use it.
        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE,
    )
}

/** Wireless debugging was switched on or off (see [WirelessDebugging.arm]). */
class AdbWifiJob : JobService() {
    override fun onStartJob(params: JobParameters): Boolean {
        WirelessDebugging.onSettingChanged(this)
        // Re-arm for the next change, then finish (the pattern Android's own samples use).
        Handler(Looper.getMainLooper()).post {
            WirelessDebugging.arm(this)
            jobFinished(params, false)
        }
        return true
    }

    override fun onStopJob(params: JobParameters): Boolean = false
}

/** Wi-Fi connected while we were waiting for it. */
class WifiReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (!WirelessDebugging.isActive(context) || WirelessDebugging.isOn(context)) return
        Log.i(WirelessDebugging.TAG, "Wi-Fi connected; Wireless debugging is off")
        val pending = goAsync()
        WirelessDebugging.enableSoon(context, "Wi-Fi connected") { pending.finish() }
    }
}

/** After a restart (Android always starts with Wireless debugging off) or an update. */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        when (intent.action) {
            Intent.ACTION_BOOT_COMPLETED -> {
                WirelessDebugging.arm(context)
                val pending = goAsync()
                WirelessDebugging.enableSoon(context, "after restart") { pending.finish() }
            }
            Intent.ACTION_MY_PACKAGE_REPLACED -> WirelessDebugging.arm(context)
        }
    }
}
