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
    private const val ID = 1
    private const val KEY_REPLY = "reply"

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
}

class TestReplyReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) = TestNotifications.onReply(context, intent)
}
