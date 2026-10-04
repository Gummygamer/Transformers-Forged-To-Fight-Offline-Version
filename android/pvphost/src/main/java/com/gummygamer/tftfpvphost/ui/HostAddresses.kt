package com.gummygamer.tftfpvphost.ui

import java.net.Inet4Address
import java.net.InetAddress
import java.net.NetworkInterface

/** Finds the IPv4 addresses players can reach this device on. Loopback and link-local are never advertised. */
object HostAddresses {
    /** True for an address a LAN player could actually connect to. */
    fun isAdvertisable(address: InetAddress): Boolean =
        address is Inet4Address && !address.isLoopbackAddress && !address.isAnyLocalAddress &&
            !address.isLinkLocalAddress

    /** Non-loopback IPv4 addresses of interfaces that are up, sorted so the result is stable. */
    fun list(): List<String> = try {
        NetworkInterface.getNetworkInterfaces().toList()
            .filter { it.isUp && !it.isLoopback }
            .flatMap { it.inetAddresses.toList() }
            .filter(::isAdvertisable)
            .mapNotNull { it.hostAddress }
            .distinct()
            .sorted()
    } catch (e: java.net.SocketException) {
        emptyList()
    }
}
