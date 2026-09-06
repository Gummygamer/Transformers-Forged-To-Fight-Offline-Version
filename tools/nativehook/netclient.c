/* Client half of the Arena realtime netcode relay.
 *
 * This is the piece the retail client is missing. Arena ships asynchronous: each device
 * fights a local AI copy of the opponent's stored team, so two players never actually
 * meet. tools/netrelay/netrelay.c supplies the transport; this file is the in-game end
 * of it, loaded into the game process through libil2cpp's DT_NEEDED on libdothook.so.
 *
 * WHY UDP AND WHY A SEPARATE THREAD
 *   UDP because a dropped frame must not delay the next one -- over TCP a lost segment
 *   stalls the fight for a retransmit timeout, which reads as a freeze. A separate
 *   thread because the game thread must never block on the network: Simulation.FixedUpdate
 *   runs the combat tick, and stalling it stalls the whole fight.
 *
 * WHY A QUEUE RATHER THAN DIRECT CALLS
 *   il2cpp objects may only be touched from the Unity main thread. So the boundary here
 *   is strictly text: the game thread enqueues packets and drains the inbox, and the
 *   network thread does all socket I/O and never sees an il2cpp pointer. See netclient.h.
 *
 * RING OVERFLOW POLICY
 *   Outbound overflow drops the NEW packet, inbound overflow drops the OLDEST one. They
 *   differ on purpose: a stale inbound state frame is worse than useless because applying
 *   it would move the remote fighter backwards, whereas a dropped outbound input frame is
 *   only a missed tap that the peer's next state frame corrects anyway.
 */
#include "netclient.h"

#include <arpa/inet.h>
#include <errno.h>
#include <netdb.h>
#include <netinet/in.h>
#include <poll.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <time.h>
#include <unistd.h>

typedef struct {
    char cmd[8];
    char peer[TFTF_NET_MAX_NAME];
    unsigned int seq;
    char payload[TFTF_NET_MAX_PAYLOAD];
} NetPacket;

static tftf_net_log_fn g_log;
static pthread_mutex_t g_lock = PTHREAD_MUTEX_INITIALIZER;
static pthread_t g_thread;
static int g_started;           /* guarded by g_lock */
static volatile int g_run;      /* set to 0 to ask the thread to exit */
static int g_sock = -1;
static char g_room[TFTF_NET_MAX_NAME];
static char g_peer[TFTF_NET_MAX_NAME];
static struct sockaddr_in g_server;
static int g_peer_count;

static NetPacket g_inbox[TFTF_NET_INBOX];
static int g_in_head, g_in_count;
static NetPacket g_outbox[TFTF_NET_OUTBOX];
static int g_out_head, g_out_count;

static void logmsg(const char *fmt, ...) {
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (g_log) g_log("%s", buf);
}

void tftf_net_set_logger(tftf_net_log_fn fn) { g_log = fn; }

static long long now_ms(void) {
    struct timeval tv;
    gettimeofday(&tv, NULL);
    return (long long)tv.tv_sec * 1000LL + tv.tv_usec / 1000LL;
}

static int send_line(const char *line) {
    ssize_t sent;
    if (g_sock < 0) return 0;
    sent = sendto(g_sock, line, strlen(line), MSG_NOSIGNAL,
                  (struct sockaddr *)&g_server, sizeof g_server);
    if (sent < 0) {
        if (errno != EAGAIN && errno != EWOULDBLOCK)
            logmsg("net: sendto failed: %s", strerror(errno));
        return 0;
    }
    return 1;
}

/* Split one '|'-delimited field out of line. Returns 1 when found, 0 when the line ran
 * out first. Mirrors the relay's own field() so both ends disagree about nothing. */
static int field(const char *line, int index, char *out, size_t cap) {
    int seen = 0;
    const char *p = line;
    if (cap) out[0] = 0;
    while (seen < index) {
        const char *bar = strchr(p, '|');
        if (!bar) return 0;
        p = bar + 1;
        seen++;
    }
    {
        const char *bar = strchr(p, '|');
        size_t len = bar ? (size_t)(bar - p) : strlen(p);
        if (cap) {
            if (len >= cap) len = cap - 1;
            memcpy(out, p, len);
            out[len] = 0;
        }
    }
    return 1;
}

static void inbox_push(const NetPacket *pkt) {
    /* Caller holds g_lock. Drop the oldest so a burst cannot wedge the queue. */
    if (g_in_count >= TFTF_NET_INBOX) {
        g_in_head = (g_in_head + 1) % TFTF_NET_INBOX;
        g_in_count--;
    }
    g_inbox[(g_in_head + g_in_count) % TFTF_NET_INBOX] = *pkt;
    g_in_count++;
}

static void handle_line(const char *line) {
    char cmd[8];
    if (!field(line, 0, cmd, sizeof cmd)) return;

    if (!strcmp(cmd, "OK")) {
        char count[16];
        pthread_mutex_lock(&g_lock);
        if (field(line, 2, count, sizeof count)) g_peer_count = atoi(count);
        pthread_mutex_unlock(&g_lock);
        logmsg("net: joined room=%s as peer=%s, %s live peer(s)", g_room, g_peer, count);
        return;
    }
    if (!strcmp(cmd, "PR")) {
        char count[16];
        pthread_mutex_lock(&g_lock);
        if (field(line, 1, count, sizeof count)) g_peer_count = atoi(count);
        pthread_mutex_unlock(&g_lock);
        return;
    }
    if (!strcmp(cmd, "ERR")) {
        char reason[64];
        field(line, 1, reason, sizeof reason);
        logmsg("net: relay refused a packet: %s", reason);
        return;
    }
    if (!strcmp(cmd, TFTF_NET_IN) || !strcmp(cmd, TFTF_NET_ST) || !strcmp(cmd, TFTF_NET_EV)) {
        NetPacket pkt;
        char seq[24];
        memset(&pkt, 0, sizeof pkt);
        snprintf(pkt.cmd, sizeof pkt.cmd, "%s", cmd);
        if (!field(line, 1, pkt.peer, sizeof pkt.peer)) return;
        if (field(line, 2, seq, sizeof seq)) pkt.seq = (unsigned int)strtoul(seq, NULL, 10);
        field(line, 3, pkt.payload, sizeof pkt.payload);
        pthread_mutex_lock(&g_lock);
        inbox_push(&pkt);
        pthread_mutex_unlock(&g_lock);
        return;
    }
    logmsg("net: ignoring unknown relay command '%s'", cmd);
}

static void *net_thread(void *unused) {
    char buf[TFTF_NET_MAX_LINE * 2];
    long long next_hello = 0;
    (void)unused;

    while (g_run) {
        struct pollfd pfd;
        int ready;
        long long now = now_ms();

        /* Drain the outbound ring first so a queued frame goes out even if no inbound
         * traffic arrives to wake the poll. */
        for (;;) {
            NetPacket pkt;
            char line[TFTF_NET_MAX_LINE];
            pthread_mutex_lock(&g_lock);
            if (g_out_count <= 0) { pthread_mutex_unlock(&g_lock); break; }
            pkt = g_outbox[g_out_head];
            g_out_head = (g_out_head + 1) % TFTF_NET_OUTBOX;
            g_out_count--;
            pthread_mutex_unlock(&g_lock);
            snprintf(line, sizeof line, "%s|%s|%s|%u|%s",
                     pkt.cmd, g_room, g_peer, pkt.seq, pkt.payload);
            send_line(line);
        }

        /* Re-announce once a second. The relay expires a peer it has not heard from, so
         * a HELLO refresh is what keeps this device in the opponent's roster while the
         * fight is quiet. */
        if (now >= next_hello) {
            char line[TFTF_NET_MAX_LINE];
            snprintf(line, sizeof line, "HELLO|%s|%s", g_room, g_peer);
            send_line(line);
            next_hello = now + 1000;
        }

        pfd.fd = g_sock;
        pfd.events = POLLIN;
        pfd.revents = 0;
        ready = poll(&pfd, 1, 10);
        if (ready < 0) { if (errno == EINTR) continue; logmsg("net: poll failed: %s", strerror(errno)); break; }
        if (ready > 0 && (pfd.revents & POLLIN)) {
            struct sockaddr_in from;
            socklen_t flen = sizeof from;
            ssize_t got = recvfrom(g_sock, buf, sizeof buf - 1, 0, (struct sockaddr *)&from, &flen);
            if (got >= 0) {
                buf[got] = 0;
                while (got && (buf[got - 1] == '\n' || buf[got - 1] == '\r')) buf[--got] = 0;
                handle_line(buf);
            } else if (errno != EINTR && errno != EAGAIN && errno != EWOULDBLOCK) {
                logmsg("net: recvfrom failed: %s", strerror(errno));
            }
        }
    }
    return NULL;
}

int tftf_net_start(const char *host, int port, const char *room, const char *peer) {
    struct addrinfo hints, *res = NULL;
    char portbuf[12];
    char hello[TFTF_NET_MAX_LINE];
    int sock, status, spawned;

    if (!host || !room || !peer || !host[0] || !room[0] || !peer[0]) {
        logmsg("net: start needs a host, a room and a peer name");
        return -1;
    }
    if (port <= 0 || port > 65535) {
        logmsg("net: port %d out of range", port);
        return -2;
    }
    if (strlen(room) >= sizeof g_room || strlen(peer) >= sizeof g_peer) {
        logmsg("net: room or peer name too long");
        return -3;
    }

    pthread_mutex_lock(&g_lock);
    if (g_started) { pthread_mutex_unlock(&g_lock); return -4; }
    g_started = 1;
    pthread_mutex_unlock(&g_lock);

    memset(&hints, 0, sizeof hints);
    hints.ai_family = AF_INET;
    hints.ai_socktype = SOCK_DGRAM;
    snprintf(portbuf, sizeof portbuf, "%d", port);
    status = getaddrinfo(host, portbuf, &hints, &res);
    if (status != 0 || !res) {
        logmsg("net: cannot resolve %s:%d (%s)", host, port, gai_strerror(status));
        pthread_mutex_lock(&g_lock);
        g_started = 0;
        pthread_mutex_unlock(&g_lock);
        return -5;
    }
    memcpy(&g_server, res->ai_addr, sizeof g_server);
    freeaddrinfo(res);

    sock = socket(AF_INET, SOCK_DGRAM, 0);
    if (sock < 0) {
        logmsg("net: socket failed: %s", strerror(errno));
        pthread_mutex_lock(&g_lock);
        g_started = 0;
        pthread_mutex_unlock(&g_lock);
        return -6;
    }
    g_sock = sock;
    snprintf(g_room, sizeof g_room, "%s", room);
    snprintf(g_peer, sizeof g_peer, "%s", peer);

    pthread_mutex_lock(&g_lock);
    g_in_head = g_in_count = g_out_head = g_out_count = 0;
    g_peer_count = 0;
    pthread_mutex_unlock(&g_lock);

    /* Say HELLO from the caller's thread so a same-process loopback test sees the join
     * immediately, then let the thread's periodic refresh keep it alive. */
    snprintf(hello, sizeof hello, "HELLO|%s|%s", g_room, g_peer);
    if (!send_line(hello)) logmsg("net: first HELLO did not send; the thread will retry");

    g_run = 1;
    spawned = pthread_create(&g_thread, NULL, net_thread, NULL);
    if (spawned != 0) {
        logmsg("net: pthread_create failed: %s", strerror(spawned));
        g_run = 0;
        close(g_sock);
        g_sock = -1;
        pthread_mutex_lock(&g_lock);
        g_started = 0;
        pthread_mutex_unlock(&g_lock);
        return -7;
    }
    pthread_detach(g_thread);
    logmsg("net: started, relaying room=%s peer=%s to %s:%d", g_room, g_peer, host, port);
    return 0;
}

int tftf_net_send(const char *kind, unsigned int seq, const char *payload) {
    NetPacket pkt;
    int slot;
    if (!kind || !payload) return 0;
    if (strcmp(kind, TFTF_NET_IN) && strcmp(kind, TFTF_NET_ST) && strcmp(kind, TFTF_NET_EV)) return 0;
    if (strlen(payload) >= TFTF_NET_MAX_PAYLOAD) {
        logmsg("net: refusing a %zu-byte payload; the limit is %d", strlen(payload), TFTF_NET_MAX_PAYLOAD - 1);
        return 0;
    }
    pthread_mutex_lock(&g_lock);
    if (!g_started || g_out_count >= TFTF_NET_OUTBOX) {
        pthread_mutex_unlock(&g_lock);
        return 0;
    }
    memset(&pkt, 0, sizeof pkt);
    snprintf(pkt.cmd, sizeof pkt.cmd, "%s", kind);
    pkt.seq = seq;
    snprintf(pkt.payload, sizeof pkt.payload, "%s", payload);
    slot = (g_out_head + g_out_count) % TFTF_NET_OUTBOX;
    g_outbox[slot] = pkt;
    g_out_count++;
    pthread_mutex_unlock(&g_lock);
    return 1;
}

int tftf_net_recv(char *out_cmd, size_t cmd_cap, char *out_peer, size_t peer_cap,
                  unsigned int *out_seq, char *out_payload, size_t payload_cap) {
    NetPacket pkt;
    if (!out_cmd || !out_peer || !out_payload) return 0;
    pthread_mutex_lock(&g_lock);
    if (g_in_count <= 0) { pthread_mutex_unlock(&g_lock); return 0; }
    pkt = g_inbox[g_in_head];
    g_in_head = (g_in_head + 1) % TFTF_NET_INBOX;
    g_in_count--;
    pthread_mutex_unlock(&g_lock);
    snprintf(out_cmd, cmd_cap, "%s", pkt.cmd);
    snprintf(out_peer, peer_cap, "%s", pkt.peer);
    if (out_seq) *out_seq = pkt.seq;
    snprintf(out_payload, payload_cap, "%s", pkt.payload);
    return 1;
}

int tftf_net_peer_count(void) {
    int count;
    pthread_mutex_lock(&g_lock);
    count = g_peer_count;
    pthread_mutex_unlock(&g_lock);
    return count;
}

int tftf_net_is_live(void) {
    /* The relay counts us in the roster, so two means one opponent. */
    return tftf_net_peer_count() >= 2;
}

int tftf_net_is_started(void) {
    int started;
    pthread_mutex_lock(&g_lock);
    started = g_started;
    pthread_mutex_unlock(&g_lock);
    return started;
}

void tftf_net_stop(void) {
    char bye[TFTF_NET_MAX_LINE];
    pthread_mutex_lock(&g_lock);
    if (!g_started) { pthread_mutex_unlock(&g_lock); return; }
    g_started = 0;
    pthread_mutex_unlock(&g_lock);

    g_run = 0;
    /* Tell the relay we are gone so the peer sees the roster shrink at once instead of
     * waiting out the TTL. Best effort: the socket may already be closed. */
    snprintf(bye, sizeof bye, "BYE|%s|%s", g_room, g_peer);
    send_line(bye);
    usleep(20000);
    if (g_sock >= 0) { close(g_sock); g_sock = -1; }
    pthread_mutex_lock(&g_lock);
    g_peer_count = 0;
    g_in_count = g_out_count = 0;
    pthread_mutex_unlock(&g_lock);
    logmsg("net: stopped");
}
