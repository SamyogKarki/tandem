package io.github.samyogkarki.tandem

import android.app.Notification
import android.app.NotificationManager
import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.drawable.Drawable
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import android.util.Base64
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayOutputStream

/** Turns a phone notification into the JSON the PC understands, or null if it shouldn't be shown. */
object NotificationMapper {
    private const val ICON_PX = 96

    fun toJson(
        context: Context,
        sbn: StatusBarNotification,
        rankings: NotificationListenerService.RankingMap?,
    ): JSONObject? {
        val n = sbn.notification
        val extras = n.extras
        val isTest = extras.getBoolean(TestNotifications.EXTRA_TEST)
        if (sbn.packageName == context.packageName && !isTest) return null

        // Skip what would be noise on a PC: group summaries (the children carry the content),
        // ongoing ones (music, downloads, navigation), notifications the app marked as
        // phone-only, and silent ones. Incoming calls are ongoing too, but they're the point.
        val incomingCall = isIncomingCall(n)
        if (n.flags and Notification.FLAG_GROUP_SUMMARY != 0) return null
        if (n.flags and Notification.FLAG_LOCAL_ONLY != 0 && !incomingCall) return null
        if (sbn.isOngoing && !incomingCall) return null
        if (rankings != null && !incomingCall) {
            val ranking = NotificationListenerService.Ranking()
            if (rankings.getRanking(sbn.key, ranking) &&
                ranking.importance < NotificationManager.IMPORTANCE_DEFAULT
            ) return null
        }

        var title = (extras.getCharSequence(Notification.EXTRA_CONVERSATION_TITLE)
            ?: extras.getCharSequence(Notification.EXTRA_TITLE))?.toString()?.trim().orEmpty()
        var text = (extras.getCharSequence(Notification.EXTRA_BIG_TEXT)
            ?: extras.getCharSequence(Notification.EXTRA_TEXT))?.toString()?.trim().orEmpty()

        // Calls: the caller's name (CallStyle keeps it in EXTRA_CALL_PERSON).
        if (incomingCall) {
            @Suppress("DEPRECATION")
            val caller = (extras.getParcelable(Notification.EXTRA_CALL_PERSON) as? android.app.Person)?.name?.toString()
            if (!caller.isNullOrBlank()) title = caller
            if (text.isEmpty()) text = "Incoming call"
        }

        // Chat apps: show the newest message, with the sender's name in group chats.
        lastMessage(extras)?.let { (sender, message) ->
            val isGroup = extras.getBoolean(Notification.EXTRA_IS_GROUP_CONVERSATION)
            text = if (isGroup && sender != null) "$sender: $message" else message
            if (title.isEmpty() && sender != null) title = sender
        }
        if (title.isEmpty() && text.isEmpty()) return null

        val actions = JSONArray()
        n.actions?.forEachIndexed { index, action ->
            val isReply = action.remoteInputs?.any { it.allowFreeFormInput } == true
            actions.put(
                JSONObject()
                    .put("i", index)
                    .put("title", action.title?.toString().orEmpty())
                    .put("reply", isReply)
            )
        }

        return JSONObject()
            .put("key", sbn.key)
            .put("pkg", sbn.packageName)
            .put("app", appName(context, sbn.packageName))
            .put("title", title)
            .put("text", text)
            .put("sub", extras.getCharSequence(Notification.EXTRA_SUB_TEXT)?.toString().orEmpty())
            .put("when", if (n.`when` > 0) n.`when` else sbn.postTime)
            .put("cat", n.category.orEmpty())
            .put("img", n.getLargeIcon()?.loadDrawable(context)?.let(::toPngBase64))
            .put("actions", actions)
            .put("call", if (incomingCall) "incoming" else null)
    }

    /**
     * A ringing call: category CALL and either CallStyle's "incoming" type (Android 12+) or,
     * for apps that don't use CallStyle, an Answer/Accept button. Ongoing calls don't count.
     */
    private fun isIncomingCall(n: Notification): Boolean {
        if (n.category != Notification.CATEGORY_CALL) return false
        return when (n.extras.getInt(Notification.EXTRA_CALL_TYPE, 0)) {
            1 -> true // Notification.CallStyle.CALL_TYPE_INCOMING
            0 -> n.actions?.any { action ->
                val title = action.title?.toString()?.lowercase().orEmpty()
                "answer" in title || "accept" in title
            } == true
            else -> false // ongoing or screening
        }
    }

    private fun lastMessage(extras: android.os.Bundle): Pair<String?, String>? {
        @Suppress("DEPRECATION")
        val bundles = extras.getParcelableArray(Notification.EXTRA_MESSAGES) ?: return null
        val messages = Notification.MessagingStyle.Message.getMessagesFromBundleArray(bundles)
        val last = messages.lastOrNull() ?: return null
        val body = last.text?.toString()?.trim().orEmpty()
        if (body.isEmpty()) return null
        return last.senderPerson?.name?.toString() to body
    }

    fun appName(context: Context, pkg: String): String = try {
        val pm = context.packageManager
        pm.getApplicationLabel(pm.getApplicationInfo(pkg, 0)).toString()
    } catch (_: Exception) {
        pkg
    }

    fun appIconBase64(context: Context, pkg: String): String? = try {
        toPngBase64(context.packageManager.getApplicationIcon(pkg))
    } catch (_: Exception) {
        null
    }

    private fun toPngBase64(drawable: Drawable): String {
        val bitmap = Bitmap.createBitmap(ICON_PX, ICON_PX, Bitmap.Config.ARGB_8888)
        val canvas = Canvas(bitmap)
        drawable.setBounds(0, 0, ICON_PX, ICON_PX)
        drawable.draw(canvas)
        val out = ByteArrayOutputStream()
        bitmap.compress(Bitmap.CompressFormat.PNG, 100, out)
        bitmap.recycle()
        return Base64.encodeToString(out.toByteArray(), Base64.NO_WRAP)
    }
}
