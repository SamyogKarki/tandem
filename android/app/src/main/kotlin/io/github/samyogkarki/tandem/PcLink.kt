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
 *
 * Each accepted connection is independent (own reader, own writer). A dead PC connection can
 * leave a write blocked forever, so an old connection is torn down on its own thread and can
 * never hold up accepting or answering the next one. The PC pings every 20 s; a connection
 * that stays silent for [IDLE_TIMEOUT_MS] is dropped.
 */
class PcLink(
    private val onMessage: (Connection, JSONObject) -> Unit,
    private val onClosed: (Connection) -> Unit,
) {
    companion object {
        const val SOCKET_NAME = "tandem_companion"
        private const val TAG = "TandemLink"
        private const val MAX_FRAME = 8 * 1024 * 1024
        private const val IDLE_TIMEOUT_MS = 60_000
        private const val SHELL_UID = 2000 // adbd runs as shell on user builds
        private const val ROOT_UID = 0

        /** For the status screen. */
        @Volatile
        var connected = false
            private set
    }

    inner class Connection(private val socket: LocalSocket) {
        private val writer = Executors.newSingleThreadExecutor { Thread(it, "tandem-write") }
        private val output = DataOutputStream(socket.outputStream.buffered())
        @Volatile
        var closed = false
            private set

        fun start() {
            socket.soTimeout = IDLE_TIMEOUT_MS
            Thread(::readLoop, "tandem-read").start()
        }

        fun send(message: JSONObject) {
            if (closed) return
            try {
                writer.execute {
                    if (closed) return@execute
                    try {
                        val bytes = message.toString().toByteArray(Charsets.UTF_8)
                        output.writeInt(bytes.size)
                        output.write(bytes)
                        output.flush()
                    } catch (e: IOException) {
                        Log.i(TAG, "PC went away while writing: ${e.message}")
                        close()
                    }
                }
            } catch (_: java.util.concurrent.RejectedExecutionException) {
                // Closed concurrently; nothing to do.
            }
        }

        private fun readLoop() {
            try {
                val input = DataInputStream(socket.inputStream.buffered())
                while (!closed) {
                    val length = input.readInt()
                    if (length !in 0..MAX_FRAME) throw IOException("Bad frame length $length")
                    val bytes = ByteArray(length)
                    input.readFully(bytes)
                    onMessage(this, JSONObject(String(bytes, Charsets.UTF_8)))
                }
            } catch (e: Exception) {
                if (!closed) Log.i(TAG, "PC connection ended: ${e.message}")
            }
            close()
        }

        /** Idempotent. shutdown() first: unlike close(), it wakes threads blocked in read/write. */
        fun close() {
            synchronized(this) {
                if (closed) return
                closed = true
            }
            try { socket.shutdownInput() } catch (_: IOException) {}
            try { socket.shutdownOutput() } catch (_: IOException) {}
            try { socket.close() } catch (_: IOException) {}
            writer.shutdownNow()
            if (current === this) {
                current = null
                connected = false
            }
            onClosed(this)
        }
    }

    @Volatile
    private var running = false
    private var server: LocalServerSocket? = null

    /** The PC connection in use; newer connections replace older ones. */
    @Volatile
    var current: Connection? = null
        private set

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
        current?.let { old -> Thread({ old.close() }, "tandem-close").start() }
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

    /** Sends to the current PC connection, if any. */
    fun send(message: JSONObject) {
        current?.send(message)
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
                try { socket.close() } catch (_: IOException) {}
                break
            }
            try {
                val uid = try { socket.peerCredentials.uid } catch (_: IOException) { -1 }
                if (uid != SHELL_UID && uid != ROOT_UID) {
                    Log.w(TAG, "Rejected connection from uid $uid")
                    socket.close()
                    continue
                }
                val connection = Connection(socket)
                val old = current
                current = connection
                connected = true
                connection.start()
                Log.i(TAG, "PC connected" + if (old != null) " (replacing an older connection)" else "")
                // Never tear the old one down on this thread: a close can block on a dead peer.
                if (old != null) Thread({ old.close() }, "tandem-close").start()
            } catch (e: Exception) {
                // Whatever went wrong with this connection, keep accepting new ones.
                Log.w(TAG, "Couldn't set up a PC connection", e)
                try { socket.close() } catch (_: IOException) {}
            }
        }
    }
}
