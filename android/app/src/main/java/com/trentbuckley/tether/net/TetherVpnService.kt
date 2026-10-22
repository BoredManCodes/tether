package com.trentbuckley.tether.net

import android.content.Context
import android.content.Intent
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.VpnService
import android.os.ParcelFileDescriptor
import com.trentbuckley.tether.sync.AdBlock
import com.trentbuckley.tether.sync.DnsLog
import java.io.FileInputStream
import java.io.FileOutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.util.concurrent.Executors
import kotlin.concurrent.thread

/**
 * DNS-only local VPN. The tunnel owns one fake address (DNS_IP) and the
 * system is told to use it as the DNS server, so the only packets that
 * reach us are DNS queries. We read the queried name, log it, forward the
 * query to the real resolver over a protected socket, and write the answer
 * back. Names on the ad/tracker blocklist (AdBlock) get NXDOMAIN instead of
 * being forwarded. All other traffic never enters the tunnel.
 *
 * Limits: apps that hard-code a resolver or use DNS-over-HTTPS/TLS skip us
 * (Chrome's DoH is switched off by Hardening). Only the domain is visible,
 * never the page or the content.
 */
class TetherVpnService : VpnService() {

    @Volatile private var running = false
    private var tun: ParcelFileDescriptor? = null
    private var reader: Thread? = null
    private val pool = Executors.newCachedThreadPool()
    private val writeLock = Any()

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (!running) start()
        return START_STICKY
    }

    override fun onDestroy() {
        running = false
        try { tun?.close() } catch (_: Exception) {}
        pool.shutdownNow()
        super.onDestroy()
    }

    override fun onRevoke() {
        running = false
        try { tun?.close() } catch (_: Exception) {}
        super.onRevoke()
    }

    private fun start() {
        val fd = try {
            Builder()
                .setSession("Tether")
                .addAddress(CLIENT_IP, 24)
                .addRoute(DNS_IP, 32)
                .addDnsServer(DNS_IP)
                .addDisallowedApplication(packageName) // our own sync traffic stays outside the tunnel
                .setBlocking(true)
                .establish()
        } catch (_: Exception) { null } ?: return
        tun = fd
        running = true
        reader = thread(name = "tether-vpn") { readLoop(fd) }
    }

    private fun readLoop(fd: ParcelFileDescriptor) {
        val input = FileInputStream(fd.fileDescriptor)
        val output = FileOutputStream(fd.fileDescriptor)
        val buf = ByteArray(32767)
        while (running) {
            val n = try { input.read(buf) } catch (_: Exception) { break }
            if (n <= 0) continue
            val packet = buf.copyOf(n)
            pool.execute { try { handle(packet, output) } catch (_: Exception) {} }
        }
        running = false
    }

    private fun handle(p: ByteArray, out: FileOutputStream) {
        if (p.size < 28 || (p[0].toInt() shr 4) != 4) return // IPv4 only
        val ihl = (p[0].toInt() and 0x0F) * 4
        if (p[9].toInt() != 17 || p.size < ihl + 8) return // UDP only
        val srcPort = u16(p, ihl)
        val dstPort = u16(p, ihl + 2)
        if (dstPort != 53) return
        val dns = p.copyOfRange(ihl + 8, p.size)
        val name = queryName(dns)
        if (name != null && !name.endsWith(".arpa")) {
            DnsLog.add(name, ownerPackage(p, srcPort))
        }

        val blocked = name != null && AdBlock.isBlocked(this, name)
        val reply = (if (blocked) nxdomain(dns) else forward(dns)) ?: return
        val resp = buildResponse(p, srcPort, reply)
        synchronized(writeLock) { out.write(resp) }
    }

    /** NXDOMAIN answer built from the query: header and question only, no records. */
    private fun nxdomain(q: ByteArray): ByteArray? {
        var i = 12
        while (i < q.size && q[i].toInt() != 0) i += (q[i].toInt() and 0xFF) + 1
        val end = i + 5 // root label, QTYPE, QCLASS
        if (end > q.size) return null
        val r = q.copyOf(end)
        r[2] = (0x80 or (q[2].toInt() and 0x01)).toByte() // QR, keep the caller's RD
        r[3] = 0x83.toByte() // RA, RCODE 3
        r[4] = 0; r[5] = 1 // one question
        for (k in 6..11) r[k] = 0 // no answer, authority or additional records
        return r
    }

    private fun ownerPackage(p: ByteArray, srcPort: Int): String? {
        return try {
            val cm = getSystemService(ConnectivityManager::class.java)
            val src = InetSocketAddress(InetAddress.getByAddress(p.copyOfRange(12, 16)), srcPort)
            val dst = InetSocketAddress(InetAddress.getByAddress(p.copyOfRange(16, 20)), 53)
            val uid = cm.getConnectionOwnerUid(17, src, dst)
            if (uid < 0) null else packageManager.getPackagesForUid(uid)?.firstOrNull()
        } catch (_: Exception) {
            null
        }
    }

    private fun forward(query: ByteArray): ByteArray? {
        val upstream = upstreamDns() ?: return null
        DatagramSocket().use { s ->
            protect(s) // keep our forwarded query out of the tunnel
            s.soTimeout = 4000
            s.send(DatagramPacket(query, query.size, InetSocketAddress(upstream, 53)))
            val rb = ByteArray(4096)
            val dp = DatagramPacket(rb, rb.size)
            return try { s.receive(dp); rb.copyOf(dp.length) } catch (_: Exception) { null }
        }
    }

    /** First IPv4 resolver of a real (non-VPN) network. */
    private fun upstreamDns(): InetAddress? {
        val cm = getSystemService(ConnectivityManager::class.java)
        for (net in cm.allNetworks) {
            val caps = cm.getNetworkCapabilities(net) ?: continue
            if (caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) continue
            if (!caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)) continue
            val dns = cm.getLinkProperties(net)?.dnsServers?.firstOrNull { it is Inet4Address }
            if (dns != null) return dns
        }
        return null
    }

    private fun buildResponse(req: ByteArray, clientPort: Int, dns: ByteArray): ByteArray {
        val total = 20 + 8 + dns.size
        val r = ByteArray(total)
        r[0] = 0x45
        r[2] = (total shr 8).toByte(); r[3] = total.toByte()
        r[6] = 0x40 // don't fragment
        r[8] = 64
        r[9] = 17
        System.arraycopy(req, 16, r, 12, 4) // src = the DNS address the client asked
        System.arraycopy(req, 12, r, 16, 4) // dst = the client
        val sum = checksum(r, 0, 20)
        r[10] = (sum shr 8).toByte(); r[11] = sum.toByte()
        r[20] = 0; r[21] = 53
        r[22] = (clientPort shr 8).toByte(); r[23] = clientPort.toByte()
        val udpLen = 8 + dns.size
        r[24] = (udpLen shr 8).toByte(); r[25] = udpLen.toByte()
        // UDP checksum 0 = not computed, which IPv4 allows.
        System.arraycopy(dns, 0, r, 28, dns.size)
        return r
    }

    private fun checksum(b: ByteArray, off: Int, len: Int): Int {
        var sum = 0
        var i = off
        while (i < off + len) {
            sum += ((b[i].toInt() and 0xFF) shl 8) or (b[i + 1].toInt() and 0xFF)
            i += 2
        }
        while (sum shr 16 != 0) sum = (sum and 0xFFFF) + (sum shr 16)
        return sum.inv() and 0xFFFF
    }

    private fun u16(b: ByteArray, i: Int) = ((b[i].toInt() and 0xFF) shl 8) or (b[i + 1].toInt() and 0xFF)

    /** QNAME of the first question, or null for anything that isn't a plain query. */
    private fun queryName(dns: ByteArray): String? {
        if (dns.size < 13) return null
        if ((dns[2].toInt() and 0x80) != 0) return null // a response, not a query
        if (u16(dns, 4) < 1) return null
        val sb = StringBuilder()
        var i = 12
        while (i < dns.size) {
            val len = dns[i].toInt() and 0xFF
            if (len == 0) break
            if (len > 63 || i + 1 + len > dns.size) return null
            if (sb.isNotEmpty()) sb.append('.')
            for (j in 1..len) sb.append((dns[i + j].toInt() and 0xFF).toChar())
            i += len + 1
        }
        return sb.toString().lowercase().ifEmpty { null }
    }

    companion object {
        private const val DNS_IP = "10.111.222.1"
        private const val CLIENT_IP = "10.111.222.2"

        fun start(ctx: Context) {
            // prepare() is non-null until the user has approved a VPN for this app.
            // Under Device Owner, Hardening makes this app the always-on VPN, which
            // needs no prompt and starts the service itself.
            if (prepare(ctx) != null) return
            try { ctx.startService(Intent(ctx, TetherVpnService::class.java)) } catch (_: Exception) {}
        }
    }
}
