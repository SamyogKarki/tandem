package io.github.samyogkarki.tandem

import android.net.LocalServerSocket
import android.net.LocalSocket
import android.net.LocalSocketAddress
import android.util.Log
import org.json.JSONObject
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import java.util.concurrent.Executors

/**
 * The PC side reaches this socket through `adb forward tcp:N localabstract:tandem_companion`,
 * so every legitimate connection comes from adbd. Anything else (another app on the phone)
 * is rejected by uid before a single byte is read.
 *
 * Wire format, both directions: [u32 big-endian length][UTF-8 JSON object].
 */
class PcLink(
    private val onMessage: (JSONObject) -> Unit,
    private val onDisconnected: () -> Unit,
) {
    companion object {
        const val SOCKET_NAME = "tandem_companion"
        private const val TAG = "TandemLink"
        private const val MAX_FRAME = 8 * 1024 * 1024
        private const val SHELL_UID = 2000 // adbd runs as shell on user builds
        private const val ROOT_UID = 0

        /** For the status screen. */
        @Volatile
        var connected = false
            private set
    }

    @Volatile
    private var running = false
    private var server: LocalServerSocket? = null
    @Volatile
    private var client: LocalSocket? = null
    private var output: DataOutputStream? = null
    private val writer = Executors.newSingleThreadExecutor { Thread(it, "tandem-write") }

    fun start() {
        if (running) return
        server = try {
            LocalServerSocket(SOCKET_NAME)
        } catch (e: IOException) {
            Log.e(TAG, "Could not listen on $SOCKET_NAME", e)
            return
        }
        running = true
        Thread(::acceptLoop, "tandem-accept").start()
    }

    fun stop() {
        if (!running) return
        running = false
        closeClient()
        // LocalServerSocket.accept() doesn't return on close(); poke it with a connection.
        try {
            LocalSocket().use { it.connect(LocalSocketAddress(SOCKET_NAME)) }
        } catch (_: IOException) {
        }
        try {
            server?.close()
        } catch (_: IOException) {
        }
        server = null
    }

    /** Queues a message for the PC; silently dropped when nobody is connected. */
    fun send(message: JSONObject) {
        writer.execute {
            val out = output ?: return@execute
            try {
                val bytes = message.toString().toByteArray(Charsets.UTF_8)
                out.writeInt(bytes.size)
                out.write(bytes)
                out.flush()
            } catch (e: IOException) {
                Log.i(TAG, "PC went away while writing: ${e.message}")
                closeClient()
            }
        }
    }

    private fun acceptLoop() {
        while (running) {
            val socket = try {
                server?.accept() ?: break
            } catch (e: IOException) {
                if (running) Log.w(TAG, "accept failed", e)
                break
            }
            if (!running) {
                socket.close()
                break
            }
            val uid = try {
                socket.peerCredentials.uid
            } catch (_: IOException) {
                -1
            }
            if (uid != SHELL_UID && uid != ROOT_UID) {
                Log.w(TAG, "Rejected connection from uid $uid")
                socket.close()
                continue
            }
            // One PC at a time: a new connection (e.g. after the PC app restarted) replaces the old one.
            closeClient()
            client = socket
            output = DataOutputStream(socket.outputStream.buffered())
            connected = true
            Thread({ readLoop(socket) }, "tandem-read").start()
        }
    }

    private fun readLoop(socket: LocalSocket) {
        try {
            val input = DataInputStream(socket.inputStream.buffered())
            while (running && client === socket) {
                val length = input.readInt()
                if (length !in 0..MAX_FRAME) throw IOException("Bad frame length $length")
                val bytes = ByteArray(length)
                input.readFully(bytes)
                onMessage(JSONObject(String(bytes, Charsets.UTF_8)))
            }
        } catch (e: Exception) {
            Log.i(TAG, "PC connection ended: ${e.message}")
        }
        if (client === socket) {
            closeClient()
            onDisconnected()
        }
    }

    private fun closeClient() {
        val socket = client ?: return
        client = null
        output = null
        connected = false
        try {
            socket.close()
        } catch (_: IOException) {
        }
    }
}
