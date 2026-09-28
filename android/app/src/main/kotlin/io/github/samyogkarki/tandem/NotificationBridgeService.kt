package io.github.samyogkarki.tandem

import android.app.RemoteInput
import android.content.Intent
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import android.util.Log
import org.json.JSONArray
import org.json.JSONObject

/**
 * Receives every notification on the phone and relays the ones worth showing to the PC.
 * Also carries out what the PC asks for: dismiss, press an action button, send a reply.
 */
class NotificationBridgeService : NotificationListenerService() {
    companion object {
        private const val TAG = "TandemBridge"
        const val PROTOCOL_VERSION = 1

        /** Non-null while Android has the listener connected (for the status screen). */
        @Volatile
        var instance: NotificationBridgeService? = null
            private set
    }

    private val main = Handler(Looper.getMainLooper())
    private var link: PcLink? = null
    /** The PC connection that completed the hello handshake; nothing is sent before that. */
    @Volatile
    private var ready: PcLink.Connection? = null
    private val iconsSent = HashSet<String>()

    override fun onListenerConnected() {
        instance = this
        // Watch for Wireless debugging going off, and switch it back on if a restart just did that
        // (a fallback for phones that hold back BOOT_COMPLETED).
        WirelessDebugging.arm(this)
        WirelessDebugging.enableSoon(this, "listener started")
        link?.stop()
        link = PcLink(
            onMessage = { connection, msg ->
                val queued = android.os.SystemClock.elapsedRealtime()
                main.post {
                    val waited = android.os.SystemClock.elapsedRealtime() - queued
                    if (waited > 500) Log.w(TAG, "main thread was busy for $waited ms before ${msg.optString("t")}")
                    handle(connection, msg)
                }
            },
            onClosed = { connection ->
                main.post {
                    if (ready === connection) ready = null
                    if (link?.current == null) LinkService.stopSoon()
                }
            },
        ).also { it.start() }
    }

    override fun onListenerDisconnected() {
        instance = null
        ready = null
        link?.stop()
        link = null
    }

    override fun onDestroy() {
        onListenerDisconnected()
        super.onDestroy()
    }

    /** Keys the PC currently shows, so a notification that turns into something we skip gets withdrawn. */
    private val sentKeys = HashSet<String>()

    override fun onNotificationPosted(sbn: StatusBarNotification, rankingMap: RankingMap?) {
        val connection = ready?.takeUnless { it.closed } ?: return
        val json = NotificationMapper.toJson(this, sbn, rankingMap)
        if (json == null) {
            // e.g. a ringing call that was just answered is now an ongoing call: stop ringing on the PC.
            if (sentKeys.remove(sbn.key)) connection.send(JSONObject().put("t", "removed").put("key", sbn.key))
            return
        }
        sentKeys.add(sbn.key)
        sendIconIfNeeded(connection, sbn.packageName)
        connection.send(JSONObject().put("t", "posted").put("n", json))
    }

    override fun onNotificationRemoved(sbn: StatusBarNotification, rankingMap: RankingMap?, reason: Int) {
        sentKeys.remove(sbn.key)
        ready?.takeUnless { it.closed }?.send(JSONObject().put("t", "removed").put("key", sbn.key))
    }

    private fun handle(connection: PcLink.Connection, msg: JSONObject) {
        try {
            when (msg.optString("t")) {
                "hello" -> onHello(connection, msg)
                "dismiss" -> cancelNotification(msg.getString("key"))
                "action" -> find(msg.getString("key"))?.let { runAction(it, msg.getInt("i"), null) }
                "reply" -> find(msg.getString("key"))?.let { runAction(it, msg.getInt("i"), msg.getString("text")) }
                "test" -> TestNotifications.post(this)
                "testCall" -> TestNotifications.postCall(this)
                "dismissAll" -> cancelAllNotifications()
                // The PC just granted the permission reconnecting needs: start using it.
                "rearm" -> {
                    WirelessDebugging.onPcConnected(this)
                    connection.send(JSONObject().put("t", "reconnect").put("state", reconnectState()))
                }
                "ping" -> connection.send(JSONObject().put("t", "pong"))
                else -> Log.w(TAG, "Unknown message ${msg.optString("t")}")
            }
        } catch (e: Exception) {
            Log.w(TAG, "Couldn't handle ${msg.optString("t")}", e)
            connection.send(JSONObject().put("t", "error").put("message", e.message ?: e.javaClass.simpleName))
        }
    }

    /** Proves to the PC that this is the companion it paired with, then sends what's on screen now. */
    private fun onHello(connection: PcLink.Connection, msg: JSONObject) {
        val started = android.os.SystemClock.elapsedRealtime()
        Log.i(TAG, "hello from PC")
        // Normally already running (the PC wakes us first); make sure, so HyperOS won't freeze us mid-session.
        LinkService.start(this)
        val nonce = msg.optString("nonce")
        iconsSent.clear()
        // The PC reached us, so this Wi-Fi is one where Wireless debugging is wanted: remember it,
        // so we can switch Wireless debugging back on here after a restart. This also arms the
        // watch, in case the listener started before the PC granted the permission.
        WirelessDebugging.onPcConnected(this)
        connection.send(
            JSONObject()
                .put("t", "hello")
                .put("v", PROTOCOL_VERSION)
                .put("app", BuildConfigInfo.versionName(this))
                .put("proof", Pairing.proof(this, nonce))
                .put("reconnect", reconnectState())
        )
        ready = connection
        sentKeys.clear()
        val items = JSONArray()
        val rankings = currentRanking
        activeNotifications?.forEach { sbn ->
            NotificationMapper.toJson(this, sbn, rankings)?.let {
                sendIconIfNeeded(connection, sbn.packageName)
                sentKeys.add(sbn.key)
                items.put(it)
            }
        }
        connection.send(JSONObject().put("t", "snapshot").put("items", items))
        Log.i(TAG, "snapshot of ${items.length()} queued in ${android.os.SystemClock.elapsedRealtime() - started} ms")
    }

    private fun reconnectState(): String = when {
        !WirelessDebugging.canWrite(this) -> "noPermission"
        !WirelessDebugging.isEnabled(this) -> "off"
        else -> "on"
    }

    private fun sendIconIfNeeded(connection: PcLink.Connection, pkg: String) {
        if (!iconsSent.add(pkg)) return
        val png = NotificationMapper.appIconBase64(this, pkg) ?: return
        connection.send(JSONObject().put("t", "icon").put("pkg", pkg).put("png", png))
    }

    private fun find(key: String): StatusBarNotification? =
        getActiveNotifications(arrayOf(key))?.firstOrNull()

    /** Presses a notification button; with [replyText], fills in its reply box first (like the shade does). */
    private fun runAction(sbn: StatusBarNotification, index: Int, replyText: String?) {
        val action = sbn.notification.actions?.getOrNull(index) ?: return
        if (replyText == null) {
            action.actionIntent.send()
            return
        }
        val inputs = action.remoteInputs?.filter { it.allowFreeFormInput }.orEmpty()
        if (inputs.isEmpty()) return
        val results = Bundle().apply { inputs.forEach { putCharSequence(it.resultKey, replyText) } }
        val fillIn = Intent()
        RemoteInput.addResultsToIntent(action.remoteInputs, fillIn, results)
        RemoteInput.setResultsSource(fillIn, RemoteInput.SOURCE_FREE_FORM_INPUT)
        action.actionIntent.send(this, 0, fillIn)
    }
}
