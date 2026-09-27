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
    /** True once the PC has said hello; nothing is sent before that. */
    @Volatile
    private var ready = false
    private val iconsSent = HashSet<String>()

    override fun onListenerConnected() {
        instance = this
        link?.stop()
        link = PcLink(
            onMessage = { msg -> main.post { handle(msg) } },
            onDisconnected = { main.post { ready = false } },
        ).also { it.start() }
    }

    override fun onListenerDisconnected() {
        instance = null
        ready = false
        link?.stop()
        link = null
    }

    override fun onDestroy() {
        onListenerDisconnected()
        super.onDestroy()
    }

    override fun onNotificationPosted(sbn: StatusBarNotification, rankingMap: RankingMap?) {
        if (!ready) return
        val json = NotificationMapper.toJson(this, sbn, rankingMap) ?: return
        sendIconIfNeeded(sbn.packageName)
        link?.send(JSONObject().put("t", "posted").put("n", json))
    }

    override fun onNotificationRemoved(sbn: StatusBarNotification, rankingMap: RankingMap?, reason: Int) {
        if (!ready) return
        link?.send(JSONObject().put("t", "removed").put("key", sbn.key))
    }

    private fun handle(msg: JSONObject) {
        try {
            when (msg.optString("t")) {
                "hello" -> onHello(msg)
                "dismiss" -> cancelNotification(msg.getString("key"))
                "action" -> find(msg.getString("key"))?.let { runAction(it, msg.getInt("i"), null) }
                "reply" -> find(msg.getString("key"))?.let { runAction(it, msg.getInt("i"), msg.getString("text")) }
                "test" -> TestNotifications.post(this)
                "ping" -> link?.send(JSONObject().put("t", "pong"))
                else -> Log.w(TAG, "Unknown message ${msg.optString("t")}")
            }
        } catch (e: Exception) {
            Log.w(TAG, "Couldn't handle ${msg.optString("t")}", e)
            link?.send(JSONObject().put("t", "error").put("message", e.message ?: e.javaClass.simpleName))
        }
    }

    /** Proves to the PC that this is the companion it paired with, then sends what's on screen now. */
    private fun onHello(msg: JSONObject) {
        val nonce = msg.optString("nonce")
        iconsSent.clear()
        link?.send(
            JSONObject()
                .put("t", "hello")
                .put("v", PROTOCOL_VERSION)
                .put("app", BuildConfigInfo.versionName(this))
                .put("proof", Pairing.proof(this, nonce))
        )
        ready = true
        val items = JSONArray()
        val rankings = currentRanking
        activeNotifications?.forEach { sbn ->
            NotificationMapper.toJson(this, sbn, rankings)?.let {
                sendIconIfNeeded(sbn.packageName)
                items.put(it)
            }
        }
        link?.send(JSONObject().put("t", "snapshot").put("items", items))
    }

    private fun sendIconIfNeeded(pkg: String) {
        if (!iconsSent.add(pkg)) return
        val png = NotificationMapper.appIconBase64(this, pkg) ?: return
        link?.send(JSONObject().put("t", "icon").put("pkg", pkg).put("png", png))
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
