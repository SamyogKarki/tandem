package io.github.samyogkarki.tandem

import android.app.Activity
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/**
 * A secret shared with the PC at setup. Any app could squat on our socket name, so the PC
 * sends a random nonce and only trusts an answer that proves knowledge of the secret.
 */
object Pairing {
    private const val PREFS = "tandem"
    private const val KEY_SECRET = "pc_secret"

    fun store(context: Context, secretHex: String) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putString(KEY_SECRET, secretHex).apply()
    }

    fun isPaired(context: Context): Boolean = secret(context) != null

    /** hex(HMAC-SHA256(secret, "tandem-v1:" + nonce)), or null before setup. */
    fun proof(context: Context, nonce: String): String? {
        val secret = secret(context) ?: return null
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(fromHex(secret), "HmacSHA256"))
        return toHex(mac.doFinal("tandem-v1:$nonce".toByteArray(Charsets.UTF_8)))
    }

    private fun secret(context: Context): String? =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString(KEY_SECRET, null)

    private fun fromHex(hex: String): ByteArray =
        ByteArray(hex.length / 2) { i -> hex.substring(2 * i, 2 * i + 2).toInt(16).toByte() }

    private fun toHex(bytes: ByteArray): String =
        bytes.joinToString("") { "%02x".format(it.toInt() and 0xff) }
}

/** `adb shell am broadcast -n …/.PairReceiver --es secret <64 hex>`; guarded by the DUMP permission. */
class PairReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val secret = intent.getStringExtra("secret")
        if (secret == null || !Regex("^[0-9a-f]{64}$").matches(secret)) {
            resultCode = Activity.RESULT_CANCELED
            resultData = "bad-secret"
            return
        }
        Pairing.store(context, secret)
        resultCode = Activity.RESULT_OK
        resultData = "paired"
    }
}

object BuildConfigInfo {
    fun versionName(context: Context): String =
        context.packageManager.getPackageInfo(context.packageName, 0).versionName ?: "?"
}
