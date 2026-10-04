/* TFTF Arena realtime netcode relay (host side).
 *
 * WHY THIS EXISTS
 *   The retail client has no realtime fight netcode: Arena is asynchronous, so each
 *   device fights a local AI copy of the opponent's stored team. The Legible fake
 *   server cannot carry a realtime fight either -- its request loop is single
 *   threaded and every relay write re-reads and re-writes a whole state file, so two
 *   devices polling it at 30 Hz would serialize behind disk I/O.
 *
 *   This relay is the missing transport. It is a small UDP forwarder: each device
 *   runs the shipped fight simulation locally, sends its own input and its own
 *   fighter's authoritative state, and applies what the peer sends to the fighter it
 *   draws as the opponent. UDP rather than TCP because a dropped frame must not
 *   delay the next one, and because `adb reverse` forwards TCP only -- a USB phone
 *   reaches this relay over the LAN address directly.
 *
 *   The relay never interprets fight rules. It forwards, tracks who is present, and
 *   logs. Result reconciliation stays with the Legible server's existing
 *   /pvp/report-result path, which already resolves two-sided claims deterministically.
 *
 * PROTOCOL   one datagram == one packet, '|' separated, <= NETRELAY_MAX_PACKET bytes
 *   client -> relay
 *     HELLO|<room>|<peer>                announce presence, request the roster
 *     IN|<room>|<peer>|<seq>|<payload>   buffered input (action, special index)
 *     ST|<room>|<peer>|<seq>|<payload>   authoritative fighter state
 *     EV|<room>|<peer>|<seq>|<payload>   fight lifecycle event (start, end, result)
 *     BYE|<room>|<peer>                  leave the room
 *   relay -> client
 *     OK|<yourpeer>|<count>|<peerlist>   HELLO acknowledgement
 *     PR|<count>|<peerlist>              roster changed or refreshed
 *     IN|<frompeer>|<seq>|<payload>      forwarded input
 *     ST|<frompeer>|<seq>|<payload>      forwarded state
 *     EV|<frompeer>|<seq>|<payload>      forwarded event
 *     ERR|<reason>                       malformed or rejected packet
 *   A peer that has not been heard from for peer_ttl_ms is dropped from the roster. The
 *   configured name is only a label: if two clients use the same name, their UDP endpoints
 *   are kept as separate roster entries and the later one receives a suffixed wire name.
 *
 * BUILD   gcc -O2 -Wall -Wextra -o netrelay netrelay.c
 * RUN     ./netrelay [--port 8777] [--ttl 10000] [--verbose]
 */
#include <arpa/inet.h>
#include <errno.h>
#include <netinet/in.h>
#include <poll.h>
#include <signal.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <time.h>
#include <unistd.h>

#define NETRELAY_MAX_PACKET 512
#define NETRELAY_MAX_PEERS 16
#define NETRELAY_MAX_ROOM 32
#define NETRELAY_MAX_PEER 48
#define NETRELAY_DEFAULT_PORT 8777
#define NETRELAY_DEFAULT_TTL_MS 10000

/* A roster lists every live peer name, so it can outgrow a single packet budget.
 * Sized from the peer cap rather than the packet cap so no format can truncate. */
#define NETRELAY_ROSTER_CAP (NETRELAY_MAX_PEERS * (NETRELAY_MAX_PEER + 1) + 2)
#define NETRELAY_LINE_CAP (NETRELAY_MAX_PACKET + NETRELAY_ROSTER_CAP + 32)
/* Receive buffer for inbound datagrams. Must exceed NETRELAY_MAX_PACKET so an oversized
 * send is seen at its true length and rejected instead of truncated into acceptance. */
#define NETRELAY_RECV_BUF (NETRELAY_MAX_PACKET * 8)

typedef struct {
    int used;
    char room[NETRELAY_MAX_ROOM];
    char peer[NETRELAY_MAX_PEER];
    struct sockaddr_in addr;
    long long last_ms;
    unsigned long long packets;
} RelayPeer;

static RelayPeer g_peers[NETRELAY_MAX_PEERS];
static int g_verbose;
static long long g_ttl_ms = NETRELAY_DEFAULT_TTL_MS;
static unsigned long long g_forwarded;
static unsigned long long g_dropped;
static unsigned long long g_rejected;
static int g_sock = -1;
static volatile int g_running = 1;

static long long now_ms(void) {
    struct timeval tv;
    gettimeofday(&tv, NULL);
    return (long long)tv.tv_sec * 1000LL + tv.tv_usec / 1000LL;
}

static void note(const char *fmt, ...) {
    va_list ap;
    char stamp[32];
    time_t t = time(NULL);
    struct tm tm;
    localtime_r(&t, &tm);
    strftime(stamp, sizeof stamp, "%H:%M:%S", &tm);
    fprintf(stdout, "[%s] ", stamp);
    va_start(ap, fmt);
    vfprintf(stdout, fmt, ap);
    va_end(ap);
    fputc('\n', stdout);
    fflush(stdout);
}

static void on_signal(int sig) {
    (void)sig;
    g_running = 0;
}

/* Copy the index-th '|'-delimited field out of packet. Returns 1 when the field was
 * found, 0 when the packet ended before it. Out is always NUL terminated. */
static int field(const char *packet, int index, char *out, int cap) {
    int seen = 0;
    const char *p = packet;
    if (cap > 0) out[0] = 0;
    while (seen < index) {
        const char *bar = strchr(p, '|');
        if (!bar) return 0;
        p = bar + 1;
        seen++;
    }
    {
        const char *bar = strchr(p, '|');
        size_t len = bar ? (size_t)(bar - p) : strlen(p);
        if (cap > 0) {
            if ((int)len >= cap) len = (size_t)(cap - 1);
            memcpy(out, p, len);
            out[len] = 0;
        }
    }
    return 1;
}

static RelayPeer *find_peer(const char *room, const char *peer) {
    int i;
    for (i = 0; i < NETRELAY_MAX_PEERS; i++) {
        if (g_peers[i].used && !strcmp(g_peers[i].room, room) && !strcmp(g_peers[i].peer, peer))
            return &g_peers[i];
    }
    return NULL;
}

static int same_endpoint(const struct sockaddr_in *a, const struct sockaddr_in *b) {
    return a->sin_family == b->sin_family &&
           a->sin_addr.s_addr == b->sin_addr.s_addr &&
           a->sin_port == b->sin_port;
}

static RelayPeer *find_peer_at(const char *room, const struct sockaddr_in *addr) {
    int i;
    for (i = 0; i < NETRELAY_MAX_PEERS; i++) {
        if (g_peers[i].used && !strcmp(g_peers[i].room, room) &&
            same_endpoint(&g_peers[i].addr, addr))
            return &g_peers[i];
    }
    return NULL;
}

static int peer_name_in_use(const char *room, const char *peer) {
    return find_peer(room, peer) != NULL;
}

/* Keep the first client's requested label, but make a duplicate label unique on the wire.
 * The endpoint is the authoritative identity for subsequent packets, so clients do not
 * need an extra device-ID API or a hand-edited config just to test two devices. */
static void unique_peer_name(const char *room, const char *requested, char *out, size_t cap) {
    int suffix;
    if (cap == 0) return;
    snprintf(out, cap, "%s", requested);
    if (!peer_name_in_use(room, out)) return;
    for (suffix = 2; suffix < 100000; suffix++) {
        char suffix_text[16];
        size_t suffix_len;
        size_t base_len;
        snprintf(suffix_text, sizeof suffix_text, "-%d", suffix);
        suffix_len = strlen(suffix_text);
        if (suffix_len >= cap) continue;
        base_len = strlen(requested);
        if (base_len > cap - suffix_len - 1) base_len = cap - suffix_len - 1;
        memcpy(out, requested, base_len);
        memcpy(out + base_len, suffix_text, suffix_len + 1);
        if (!peer_name_in_use(room, out)) return;
    }
    /* The room can only hold NETRELAY_MAX_PEERS entries, so this is unreachable in normal
     * operation. Leave a valid requested name in place if the suffix search ever exhausts. */
    snprintf(out, cap, "%s", requested);
}

static RelayPeer *alloc_peer(const char *room, const char *peer) {
    RelayPeer *found = find_peer(room, peer);
    int i, chosen = -1;
    long long oldest_ms = 0;
    if (found) return found;
    for (i = 0; i < NETRELAY_MAX_PEERS; i++) {
        if (!g_peers[i].used) { chosen = i; break; }
        if (chosen < 0 || g_peers[i].last_ms < oldest_ms) { oldest_ms = g_peers[i].last_ms; chosen = i; }
    }
    if (chosen < 0) return NULL;
    memset(&g_peers[chosen], 0, sizeof g_peers[chosen]);
    g_peers[chosen].used = 1;
    snprintf(g_peers[chosen].room, sizeof g_peers[chosen].room, "%s", room);
    snprintf(g_peers[chosen].peer, sizeof g_peers[chosen].peer, "%s", peer);
    return &g_peers[chosen];
}

static RelayPeer *alloc_hello_peer(const char *room, const char *requested,
                                   const struct sockaddr_in *from) {
    RelayPeer *found = find_peer_at(room, from);
    char effective[NETRELAY_MAX_PEER];
    if (found) return found;
    unique_peer_name(room, requested, effective, sizeof effective);
    return alloc_peer(room, effective);
}

/* Build the live roster for a room as "peerA,peerB". Returns the peer count. */
static int room_roster(const char *room, long long cutoff, char *out, int cap) {
    int i, count = 0;
    if (cap > 0) out[0] = 0;
    for (i = 0; i < NETRELAY_MAX_PEERS; i++) {
        if (!g_peers[i].used || strcmp(g_peers[i].room, room)) continue;
        if (g_peers[i].last_ms < cutoff) continue;
        if (count && cap > 0) strncat(out, ",", (size_t)(cap - (int)strlen(out) - 1));
        if (cap > 0) strncat(out, g_peers[i].peer, (size_t)(cap - (int)strlen(out) - 1));
        count++;
    }
    return count;
}

static int send_to(const struct sockaddr_in *addr, const char *packet) {
    ssize_t sent = sendto(g_sock, packet, strlen(packet), MSG_NOSIGNAL,
                          (const struct sockaddr *)addr, sizeof *addr);
    if (sent < 0) {
        if (g_verbose) note("sendto failed: %s", strerror(errno));
        return 0;
    }
    return 1;
}

/* Push the live roster to every peer in the room except `skip`. The newcomer is passed as
 * `skip` on HELLO because its OK reply already carries the same roster; sending it again
 * would be a duplicate frame the client has to ignore. */
static void send_roster(const char *room, RelayPeer *skip) {
    char roster[NETRELAY_ROSTER_CAP];
    char line[NETRELAY_LINE_CAP];
    long long cutoff = now_ms() - g_ttl_ms;
    int count = room_roster(room, cutoff, roster, sizeof roster);
    int i;
    snprintf(line, sizeof line, "PR|%d|%s", count, roster);
    for (i = 0; i < NETRELAY_MAX_PEERS; i++) {
        if (!g_peers[i].used || strcmp(g_peers[i].room, room)) continue;
        if (g_peers[i].last_ms < cutoff) continue;
        if (skip == &g_peers[i]) continue;
        send_to(&g_peers[i].addr, line);
    }
}

static void expire_stale(void) {
    long long cutoff = now_ms() - g_ttl_ms;
    int i;
    for (i = 0; i < NETRELAY_MAX_PEERS; i++) {
        char room[NETRELAY_MAX_ROOM];
        char peer[NETRELAY_MAX_PEER];
        unsigned long long packets;
        if (!g_peers[i].used || g_peers[i].last_ms >= cutoff) continue;
        snprintf(room, sizeof room, "%s", g_peers[i].room);
        snprintf(peer, sizeof peer, "%s", g_peers[i].peer);
        packets = g_peers[i].packets;
        g_peers[i].used = 0;
        note("peer %s in room %s expired after %llu packets", peer, room, packets);
        send_roster(room, NULL);
    }
}

static void reject(const struct sockaddr_in *addr, const char *reason) {
    char line[128];
    g_rejected++;
    snprintf(line, sizeof line, "ERR|%s", reason);
    send_to(addr, line);
}

static void forward(const char *cmd, const char *room, const char *peer, const char *rest) {
    char line[NETRELAY_LINE_CAP];
    long long cutoff = now_ms() - g_ttl_ms;
    int i, recipients = 0;
    snprintf(line, sizeof line, "%s|%s|%s", cmd, peer, rest);
    for (i = 0; i < NETRELAY_MAX_PEERS; i++) {
        if (!g_peers[i].used || strcmp(g_peers[i].room, room)) continue;
        if (g_peers[i].last_ms < cutoff) continue;
        if (!strcmp(g_peers[i].peer, peer)) continue;
        if (send_to(&g_peers[i].addr, line)) recipients++;
    }
    if (recipients) g_forwarded++;
    else g_dropped++;
    if (g_verbose && recipients == 0)
        note("%s room=%s peer=%s had no live recipient", cmd, room, peer);
}

static void handle_packet(const char *buf, size_t len, const struct sockaddr_in *from) {
    char cmd[16], room[NETRELAY_MAX_ROOM], peer[NETRELAY_MAX_PEER];
    char seq[24], payload[NETRELAY_MAX_PACKET];
    char line[NETRELAY_LINE_CAP];
    char roster[NETRELAY_ROSTER_CAP];
    long long cutoff;
    RelayPeer *sender;
    int count;

    if (len == 0 || len >= sizeof payload) { reject(from, "bad length"); return; }
    memcpy(payload, buf, len);
    payload[len] = 0;
    /* Strip a trailing CR/LF so a telnet-style probe still parses. */
    while (len && (payload[len - 1] == '\n' || payload[len - 1] == '\r')) payload[--len] = 0;

    if (!field(payload, 0, cmd, sizeof cmd)) { reject(from, "no command"); return; }

    if (!strcmp(cmd, "HELLO")) {
        if (!field(payload, 1, room, sizeof room) || !field(payload, 2, peer, sizeof peer)) {
            reject(from, "HELLO needs room and peer");
            return;
        }
        if (!room[0] || !peer[0]) { reject(from, "empty room or peer"); return; }
        sender = alloc_hello_peer(room, peer, from);
        if (!sender) { reject(from, "room full"); return; }
        sender->addr = *from;
        sender->last_ms = now_ms();
        sender->packets++;
        cutoff = now_ms() - g_ttl_ms;
        count = room_roster(room, cutoff, roster, sizeof roster);
        snprintf(line, sizeof line, "OK|%s|%d|%s", sender->peer, count, roster);
        send_to(from, line);
        note("HELLO room=%s peer=%s as=%s from %s -> %d live peer(s): %s", room, peer,
             sender->peer,
             inet_ntoa(from->sin_addr), count, roster);
        send_roster(room, sender);
        return;
    }

    if (!strcmp(cmd, "BYE")) {
        if (!field(payload, 1, room, sizeof room) || !field(payload, 2, peer, sizeof peer)) {
            reject(from, "BYE needs room and peer");
            return;
        }
        sender = find_peer_at(room, from);
        if (!sender) sender = find_peer(room, peer);
        if (sender) {
            note("BYE room=%s peer=%s after %llu packets", room, sender->peer, sender->packets);
            sender->used = 0;
            send_roster(room, NULL);
        }
        return;
    }

    if (strcmp(cmd, "IN") && strcmp(cmd, "ST") && strcmp(cmd, "EV")) {
        reject(from, "unknown command");
        return;
    }
    if (!field(payload, 1, room, sizeof room) || !field(payload, 2, peer, sizeof peer)) {
        reject(from, "forward needs room and peer");
        return;
    }
    sender = find_peer_at(room, from);
    if (!sender) {
        /* A client may send input before its HELLO lands; adopt it rather than drop
         * the frame, because dropping input is a visible stall in the fight. */
        char effective[NETRELAY_MAX_PEER];
        unique_peer_name(room, peer, effective, sizeof effective);
        sender = alloc_peer(room, effective);
        if (!sender) { reject(from, "room full"); return; }
        sender->addr = *from;
        send_roster(room, sender);
    }
    sender->addr = *from;
    sender->last_ms = now_ms();
    sender->packets++;

    /* Re-emit as CMD|<frompeer>|<seq>|<payload>, dropping the room the peers know. */
    if (!field(payload, 3, seq, sizeof seq)) seq[0] = 0;
    if (!field(payload, 4, line, sizeof line)) line[0] = 0;
    snprintf(payload, sizeof payload, "%s|%s", seq, line);
    forward(cmd, room, sender->peer, payload);
}

static int listen_on(int port) {
    struct sockaddr_in addr;
    int reuse = 1;
    g_sock = socket(AF_INET, SOCK_DGRAM, 0);
    if (g_sock < 0) { fprintf(stderr, "socket: %s\n", strerror(errno)); return -1; }
    setsockopt(g_sock, SOL_SOCKET, SO_REUSEADDR, &reuse, sizeof reuse);
    memset(&addr, 0, sizeof addr);
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_ANY);
    addr.sin_port = htons((unsigned short)port);
    if (bind(g_sock, (struct sockaddr *)&addr, sizeof addr) < 0) {
        fprintf(stderr, "bind %d: %s\n", port, strerror(errno));
        close(g_sock);
        g_sock = -1;
        return -1;
    }
    return 0;
}

int main(int argc, char **argv) {
    int port = NETRELAY_DEFAULT_PORT;
    /* Bigger than NETRELAY_MAX_PACKET on purpose: recvfrom truncates to the buffer, so a
     * buffer at the limit would silently shrink an oversized datagram back under it and
     * the packet would parse as legal instead of being rejected. */
    char buf[NETRELAY_RECV_BUF];
    int i;

    for (i = 1; i < argc; i++) {
        if (!strcmp(argv[i], "--port") && i + 1 < argc) port = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--ttl") && i + 1 < argc) g_ttl_ms = atoll(argv[++i]);
        else if (!strcmp(argv[i], "--verbose")) g_verbose = 1;
        else if (!strcmp(argv[i], "--help")) {
            printf("usage: %s [--port %d] [--ttl %lld] [--verbose]\n", argv[0],
                   NETRELAY_DEFAULT_PORT, g_ttl_ms);
            return 0;
        } else {
            fprintf(stderr, "unknown argument: %s\n", argv[i]);
            return 2;
        }
    }
    if (port <= 0 || port > 65535) { fprintf(stderr, "port out of range\n"); return 2; }
    if (g_ttl_ms <= 0) { fprintf(stderr, "ttl must be positive\n"); return 2; }
    if (listen_on(port)) return 1;

    signal(SIGINT, on_signal);
    signal(SIGTERM, on_signal);
    signal(SIGPIPE, SIG_IGN);
    note("netrelay listening on udp/0.0.0.0:%d ttl=%lldms peers<=%d packet<=%dB",
         port, g_ttl_ms, NETRELAY_MAX_PEERS, NETRELAY_MAX_PACKET);

    while (g_running) {
        struct pollfd pfd;
        int ready;
        pfd.fd = g_sock;
        pfd.events = POLLIN;
        pfd.revents = 0;
        ready = poll(&pfd, 1, 200);
        if (ready < 0) { if (errno == EINTR) continue; note("poll: %s", strerror(errno)); break; }
        if (ready > 0 && (pfd.revents & POLLIN)) {
            struct sockaddr_in from;
            socklen_t flen = sizeof from;
            ssize_t got = recvfrom(g_sock, buf, sizeof buf - 1, 0, (struct sockaddr *)&from, &flen);
            /* got == 0 is a real zero-length datagram, not end of stream; hand it to the
             * handler so it is rejected and counted rather than vanishing. */
            if (got >= 0) {
                handle_packet(buf, (size_t)got, &from);
            } else if (errno != EINTR && errno != EAGAIN) {
                note("recvfrom: %s", strerror(errno));
            }
        }
        expire_stale();
    }
    note("shutting down: forwarded=%llu dropped=%llu rejected=%llu", g_forwarded, g_dropped, g_rejected);
    if (g_sock >= 0) close(g_sock);
    return 0;
}
