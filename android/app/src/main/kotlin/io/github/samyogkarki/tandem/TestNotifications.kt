package io.github.samyogkarki.tandem

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.RemoteInput
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.graphics.drawable.Icon
import android.os.Bundle

/** "Send a test notification" from the PC: shows up on the PC, and replying to it proves replies work. */
object TestNotifications {
    const val EXTRA_TEST = "io.github.samyogkarki.tandem.TEST"
    private const val CHANNEL = "tandem_test"
    private const val CALL_CHANNEL = "tandem_test_call"
    private const val ID = 1
    private const val CALL_ID = 3
    private const val KEY_REPLY = "reply"
    private const val CALL_RING_MS = 30_000L

    fun post(
        context: Context,
        text: String = context.getString(R.string.test_notification_text),
    ) {
        val manager = context.getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL, context.getString(R.string.test_channel_name), NotificationManager.IMPORTANCE_HIGH)
        )
        val replyIntent = PendingIntent.getBroadcast(
            context, 0, Intent(context, TestReplyReceiver::class.java),
            // Mutable so the system can attach the typed reply; explicit intent, so this is safe.
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE,
        )
        val reply = Notification.Action.Builder(
            Icon.createWithResource(context, R.drawable.ic_stat_tandem), context.getString(R.string.reply), replyIntent
        ).addRemoteInput(RemoteInput.Builder(KEY_REPLY).setLabel(context.getString(R.string.reply)).build()).build()

        val notification = Notification.Builder(context, CHANNEL)
            .setSmallIcon(R.drawable.ic_stat_tandem)
            .setContentTitle(context.getString(R.string.test_notification_title))
            .setContentText(text)
            .setStyle(Notification.BigTextStyle().bigText(text))
            .addAction(reply)
            .addExtras(Bundle().apply { putBoolean(EXTRA_TEST, true) })
            .setAutoCancel(true)
            .build()
        manager.notify(ID, notification)
    }

    fun onReply(context: Context, intent: Intent) {
        val text = RemoteInput.getResultsFromIntent(intent)?.getCharSequence(KEY_REPLY) ?: return
        post(context, context.getString(R.string.test_reply_received, text))
    }

    /**
     * "Try a pretend call" from the PC. Shaped like a real incoming call (category CALL,
     * call type "incoming", Answer and Decline buttons) but silent on the phone, so only the PC
     * rings. Not a CallStyle notification: Android only allows those for real calling apps.
     * Answering turns it into an ongoing call, which the PC must stop ringing for.
     */
    fun postCall(context: Context) {
        val manager = context.getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(CALL_CHANNEL, context.getString(R.string.test_call_channel_name), NotificationManager.IMPORTANCE_HIGH)
                .apply { setSound(null, null) }
        )
        val caller = context.getString(R.string.test_call_caller)
        val notification = Notification.Builder(context, CALL_CHANNEL)
            .setSmallIcon(R.drawable.ic_stat_tandem)
            .setContentTitle(caller)
            .setContentText(context.getString(R.string.test_call_ringing))
            .setCategory(Notification.CATEGORY_CALL)
            .addAction(callAction(context, R.string.decline, TestCallReceiver.DECLINE))
            .addAction(callAction(context, R.string.answer, TestCallReceiver.ANSWER))
            .addExtras(Bundle().apply {
                putBoolean(EXTRA_TEST, true)
                putInt(Notification.EXTRA_CALL_TYPE, 1) // CallStyle.CALL_TYPE_INCOMING
            })
            .setTimeoutAfter(CALL_RING_MS) // nobody answered: it stops ringing, like a missed call
            .build()
        manager.notify(CALL_ID, notification)
    }

    fun onCallAction(context: Context, action: String?) {
        val manager = context.getSystemService(NotificationManager::class.java)
        if (action != TestCallReceiver.ANSWER) {
            manager.cancel(CALL_ID)
            return
        }
        val inCall = Notification.Builder(context, CALL_CHANNEL)
            .setSmallIcon(R.drawable.ic_stat_tandem)
            .setContentTitle(context.getString(R.string.test_call_caller))
            .setContentText(context.getString(R.string.test_call_ongoing))
            .setCategory(Notification.CATEGORY_CALL)
            .setOngoing(true)
            .setUsesChronometer(true)
            .addAction(callAction(context, R.string.hang_up, TestCallReceiver.HANG_UP))
            .addExtras(Bundle().apply {
                putBoolean(EXTRA_TEST, true)
                putInt(Notification.EXTRA_CALL_TYPE, 2) // CallStyle.CALL_TYPE_ONGOING
            })
            .setTimeoutAfter(CALL_RING_MS)
            .build()
        manager.notify(CALL_ID, inCall)
    }

    private fun callAction(context: Context, label: Int, action: String): Notification.Action {
        val intent = PendingIntent.getBroadcast(
            context, action.hashCode(), Intent(context, TestCallReceiver::class.java).setAction(action),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        return Notification.Action.Builder(
            Icon.createWithResource(context, R.drawable.ic_stat_tandem), context.getString(label), intent
        ).build()
    }
}

class TestReplyReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) = TestNotifications.onReply(context, intent)
}

class TestCallReceiver : BroadcastReceiver() {
    companion object {
        const val ANSWER = "io.github.samyogkarki.tandem.test.ANSWER"
        const val DECLINE = "io.github.samyogkarki.tandem.test.DECLINE"
        const val HANG_UP = "io.github.samyogkarki.tandem.test.HANG_UP"
    }

    override fun onReceive(context: Context, intent: Intent) = TestNotifications.onCallAction(context, intent.action)
}
