/* Tests for the in-game relay client (tools/nativehook/netclient.c) against the real
 * relay (tools/netrelay/netrelay.c).
 *
 * These are end-to-end wire tests: two netclient instances in two threads, both talking
 * to an actual netrelay process over UDP on localhost. That is the same path a fight
 * takes, minus the il2cpp calls, so what is asserted here is what a device will do.
 *
 * Build and run:
 *   bash tools/netrelay/run_tests.sh
 */
#include "../nativehook/netclient.h"

#include <arpa/inet.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <pthread.h>
#include <signal.h>
#include <stdarg.h>
#include <stdio.h>
#include <sys/socket.h>
#include <sys/types.h>
#include <stdlib.h>
#include <string.h>
#include <sys/wait.h>
#include <unistd.h>

static int g_failures;
static int g_tests;
static pid_t g_relay_pid;
static int g_port;

static void report(const char *name, int passed, const char *detail) {
    g_tests++;
    if (passed) {
        printf("[ok] %s\n", name);
    } else {
        printf("[!] %s%s%s\n", name, detail && detail[0] ? "  " : "", detail ? detail : "");
        g_failures++;
    }
}

static void test_log(const char *fmt, ...) {
    va_list ap;
    char buf[512];
    if (getenv("NETRELAY_TEST_VERBOSE")) {
        va_start(ap, fmt);
        vsnprintf(buf, sizeof buf, fmt, ap);
        va_end(ap);
        fprintf(stderr, "    %s\n", buf);
    }
}

/* Pick a free UDP port the same way the Python harness does: bind, read the number back,
 * release it. The relay's SO_REUSEADDR covers the window. */
static int free_port(void) {
    struct sockaddr_in addr;
    socklen_t len = sizeof addr;
    int probe = socket(AF_INET, SOCK_DGRAM, 0);
    int port;
    memset(&addr, 0, sizeof addr);
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (bind(probe, (struct sockaddr *)&addr, sizeof addr) < 0) { close(probe); return 0; }
    if (getsockname(probe, (struct sockaddr *)&addr, &len) < 0) { close(probe); return 0; }
    port = ntohs(addr.sin_port);
    close(probe);
    return port;
}

static int start_relay(int port, int ttl_ms) {
    char portbuf[12], ttlbuf[12];
    pid_t pid;
    snprintf(portbuf, sizeof portbuf, "%d", port);
    snprintf(ttlbuf, sizeof ttlbuf, "%d", ttl_ms);
    pid = fork();
    if (pid == 0) {
        int nullfd = open("/dev/null", O_WRONLY);
        if (nullfd >= 0) { dup2(nullfd, 1); dup2(nullfd, 2); close(nullfd); }
        execl("./netrelay", "netrelay", "--port", portbuf, "--ttl", ttlbuf, (char *)NULL);
        execl("tools/netrelay/netrelay", "netrelay", "--port", portbuf, "--ttl", ttlbuf, (char *)NULL);
        _exit(127);
    }
    if (pid < 0) return -1;
    g_relay_pid = pid;
    usleep(300000);
    return 0;
}

static void stop_relay(void) {
    int status;
    if (g_relay_pid > 0) {
        kill(g_relay_pid, SIGTERM);
        waitpid(g_relay_pid, &status, 0);
        g_relay_pid = 0;
    }
}

static int wait_kind(const char *want, char *peer, unsigned int *seq, char *payload, int timeout_ms) {
    char cmd[8];
    int waited = 0;
    while (waited < timeout_ms) {
        if (tftf_net_recv(cmd, sizeof cmd, peer, TFTF_NET_MAX_NAME, seq, payload, TFTF_NET_MAX_PAYLOAD)) {
            if (!strcmp(cmd, want)) return 1;
        }
        usleep(2000);
        waited += 2;
    }
    return 0;
}

/* Fork BEFORE either side starts, then run one side of the pairing in the child. See the
 * note above test_join_sees_the_peer for why the fork cannot come after a parent start. */
static pid_t fork_peer(const char *room, const char *peer, int (*role)(void)) {
    pid_t kid = fork();
    if (kid == 0) {
        int rc;
        tftf_net_set_logger(NULL);
        if (tftf_net_start("127.0.0.1", g_port, room, peer) != 0) _exit(1);
        rc = role();
        tftf_net_stop();
        _exit(rc);
    }
    return kid;
}

static int role_idle(void) { usleep(1500000); return 0; }

static int role_send_input(void) {
    usleep(600000);
    if (!tftf_net_send(TFTF_NET_IN, 41, "action=256,special=0")) return 2;
    usleep(800000);
    return 0;
}

static int role_send_state_and_event(void) {
    usleep(600000);
    if (!tftf_net_send(TFTF_NET_ST, 5, "hp=4820.5,pos=3.25,facing=1")) return 2;
    if (!tftf_net_send(TFTF_NET_EV, 6, "result=WON")) return 3;
    usleep(1000000);
    return 0;
}

static int role_send_input_other_room(void) {
    usleep(500000);
    tftf_net_send(TFTF_NET_IN, 9, "action=1");
    usleep(1000000);
    return 0;
}

/* Wait until the roster satisfies `at_least`. Returns the count seen, or -1 on timeout. */
static int wait_peer_count_at_least(int want, int timeout_ms) {
    int waited = 0;
    while (waited < timeout_ms) {
        int count = tftf_net_peer_count();
        if (count >= want) return count;
        usleep(10000);
        waited += 10;
    }
    return -1;
}

/* Stops its own client once it has actually been seen in the room, then stays alive but
 * silent so the parent can watch the relay shrink the roster in response to BYE rather
 * than to a TTL expiry. Waiting on the roster instead of on a fixed offset matters: with a
 * fixed offset the child could leave before the parent had even finished starting, and the
 * parent would then never observe a two-peer roster at all. */
static int role_stop_early_then_idle(void) {
    if (wait_peer_count_at_least(2, 4000) < 0) return 4;
    usleep(500000);
    tftf_net_stop();
    usleep(3000000);
    return 0;
}

static void test_start_rejects_bad_arguments(void) {
    report("start rejects a null host", tftf_net_start(NULL, g_port, "room", "peer") < 0, "");
    report("start rejects an empty room", tftf_net_start("127.0.0.1", g_port, "", "peer") < 0, "");
    report("start rejects an empty peer", tftf_net_start("127.0.0.1", g_port, "room", "") < 0, "");
    report("start rejects port zero", tftf_net_start("127.0.0.1", 0, "room", "peer") < 0, "");
    report("start rejects a port above 65535", tftf_net_start("127.0.0.1", 70000, "room", "peer") < 0, "");
    report("start rejects an unresolvable host", tftf_net_start("no.such.host.invalid", g_port, "room", "peer") < 0, "");
    report("a rejected start leaves the client stopped", !tftf_net_is_started(), "");
    report("send is refused while stopped", tftf_net_send(TFTF_NET_IN, 1, "action=1") == 0, "");
}

static void test_singleton_start_refuses_a_second_start(void) {
    report("start succeeds against a live relay", tftf_net_start("127.0.0.1", g_port, "arena_versus", "alice") == 0, "");
    report("the client reports itself started", tftf_net_is_started() == 1, "");
    report("a second start is refused", tftf_net_start("127.0.0.1", g_port, "arena_versus", "bob") < 0, "");
    report("the refused second start did not stop the first", tftf_net_is_started() == 1, "");
    tftf_net_stop();
    report("stop clears the started flag", tftf_net_is_started() == 0, "");
    tftf_net_stop();
    report("stop is idempotent", !tftf_net_is_started(), "");
}

/* The client is a process-wide singleton by design, so a pairing is modelled with a forked
 * child that runs the same code under a different peer name against the same relay. */
static void test_join_sees_the_peer(void) {
    int status;
    pid_t kid = fork_peer("arena_versus", "bob", role_idle);
    if (kid < 0) { report("fork for the second peer", 0, "fork failed"); return; }
    if (tftf_net_start("127.0.0.1", g_port, "arena_versus", "alice") != 0) {
        report("join sees the peer: start", 0, "start failed");
        waitpid(kid, &status, 0);
        return;
    }
    usleep(1500000);
    report("the roster reaches two once a peer joins", tftf_net_peer_count() >= 2, "");
    report("is_live is true with a peer present", tftf_net_is_live() == 1, "");
    tftf_net_stop();
    waitpid(kid, &status, 0);
    report("the peer child exited cleanly", WIFEXITED(status) && WEXITSTATUS(status) == 0, "");
}

static void test_input_crosses_between_two_clients(void) {
    char peer[TFTF_NET_MAX_NAME], payload[TFTF_NET_MAX_PAYLOAD];
    unsigned int seq = 0;
    int status, got;
    pid_t kid = fork_peer("arena_versus", "bob", role_send_input);
    if (kid < 0) { report("fork for input crossing", 0, "fork failed"); return; }
    if (tftf_net_start("127.0.0.1", g_port, "arena_versus", "alice") != 0) {
        report("input crosses: start", 0, "start failed");
        waitpid(kid, &status, 0);
        return;
    }
    got = wait_kind(TFTF_NET_IN, peer, &seq, payload, 4000);
    report("remote input reaches the local client", got == 1, "");
    if (got) {
        report("the packet names the remote peer", !strcmp(peer, "bob"), peer);
        report("the sequence number survives the relay", seq == 41, "");
        report("the payload survives the relay", !strcmp(payload, "action=256,special=0"), payload);
    }
    tftf_net_stop();
    waitpid(kid, &status, 0);
    report("the sending child exited cleanly", WIFEXITED(status) && WEXITSTATUS(status) == 0, "");
}

static void test_state_and_events_cross(void) {
    char peer[TFTF_NET_MAX_NAME];
    char st_payload[TFTF_NET_MAX_PAYLOAD], ev_payload[TFTF_NET_MAX_PAYLOAD];
    unsigned int st_seq = 0, ev_seq = 0;
    int status, got_st, got_ev;
    pid_t kid = fork_peer("arena_versus", "bob", role_send_state_and_event);
    if (kid < 0) { report("fork for state crossing", 0, "fork failed"); return; }
    if (tftf_net_start("127.0.0.1", g_port, "arena_versus", "alice") != 0) {
        report("state crosses: start", 0, "start failed");
        waitpid(kid, &status, 0);
        return;
    }
    got_st = wait_kind(TFTF_NET_ST, peer, &st_seq, st_payload, 4000);
    got_ev = wait_kind(TFTF_NET_EV, peer, &ev_seq, ev_payload, 4000);
    report("fighter state reaches the local client", got_st == 1, "");
    report("the state payload is intact", got_st && !strcmp(st_payload, "hp=4820.5,pos=3.25,facing=1"), st_payload);
    report("the state sequence survives", got_st && st_seq == 5, "");
    report("fight events reach the local client", got_ev == 1, "");
    report("the event payload is intact", got_ev && !strcmp(ev_payload, "result=WON"), ev_payload);
    tftf_net_stop();
    waitpid(kid, &status, 0);
}

static void test_rooms_are_isolated_for_clients(void) {
    char peer[TFTF_NET_MAX_NAME], payload[TFTF_NET_MAX_PAYLOAD];
    unsigned int seq = 0;
    int status;
    pid_t kid = fork_peer("other_arena", "bob", role_send_input_other_room);
    if (kid < 0) { report("fork for room isolation", 0, "fork failed"); return; }
    if (tftf_net_start("127.0.0.1", g_port, "arena_versus", "alice") != 0) {
        report("rooms isolated: start", 0, "start failed");
        waitpid(kid, &status, 0);
        return;
    }
    usleep(1800000);
    report("a different room is not seen as live", tftf_net_is_live() == 0, "");
    report("no input arrives from a different room",
           wait_kind(TFTF_NET_IN, peer, &seq, payload, 300) == 0, "");
    tftf_net_stop();
    waitpid(kid, &status, 0);
}

static void test_oversize_payload_is_refused_locally(void) {
    char big[TFTF_NET_MAX_PAYLOAD * 4];
    char peer[TFTF_NET_MAX_NAME], payload[TFTF_NET_MAX_PAYLOAD];
    unsigned int seq = 0;
    memset(big, 'x', sizeof big - 1);
    big[sizeof big - 1] = 0;

    if (tftf_net_start("127.0.0.1", g_port, "arena_versus", "alice") != 0) {
        report("oversize refused: start", 0, "start failed");
        return;
    }
    report("an oversized payload is refused before it is queued",
           tftf_net_send(TFTF_NET_IN, 1, big) == 0, "");
    report("an unknown packet kind is refused", tftf_net_send("XX", 1, "action=1") == 0, "");
    report("a legal payload is accepted", tftf_net_send(TFTF_NET_IN, 2, "action=1") == 1, "");
    /* Nothing drains this frame: no peer is present, so the relay has no recipient and
     * the packet never comes back. That is the assertion below. */
    report("an unreciprocated packet is not echoed back",
           wait_kind(TFTF_NET_IN, peer, &seq, payload, 300) == 0, "");
    tftf_net_stop();
}

static void test_stop_releases_the_peer_from_the_roster(void) {
    int status;
    pid_t kid = fork_peer("arena_versus", "bob", role_stop_early_then_idle);
    if (kid < 0) { report("fork for stop release", 0, "fork failed"); return; }
    if (tftf_net_start("127.0.0.1", g_port, "arena_versus", "alice") != 0) {
        report("stop releases: start", 0, "start failed");
        waitpid(kid, &status, 0);
        return;
    }
    report("the roster reaches two while both are live", wait_peer_count_at_least(2, 4000) >= 2, "");
    /* The child leaves half a second after it too sees two peers, so poll for the shrink
     * rather than sampling once at a guessed instant. */
    {
        int shrunk = 0, waited = 0;
        while (waited < 5000 && !shrunk) {
            if (tftf_net_peer_count() <= 1) shrunk = 1;
            else { usleep(10000); waited += 10; }
        }
        report("BYE on stop drops the peer from the roster", shrunk == 1, "");
    }
    tftf_net_stop();
    waitpid(kid, &status, 0);
    report("the leaving child exited cleanly", WIFEXITED(status) && WEXITSTATUS(status) == 0, "");
}

int main(void) {
    tftf_net_set_logger(test_log);
    g_port = free_port();
    if (!g_port) { printf("[!] could not allocate a free UDP port\n"); return 1; }
    printf("[*] relay port %d\n", g_port);
    if (start_relay(g_port, 3000) < 0) { printf("[!] could not start netrelay\n"); return 1; }

    test_start_rejects_bad_arguments();
    test_singleton_start_refuses_a_second_start();
    test_join_sees_the_peer();
    test_input_crosses_between_two_clients();
    test_state_and_events_cross();
    test_rooms_are_isolated_for_clients();
    test_oversize_payload_is_refused_locally();
    test_stop_releases_the_peer_from_the_roster();

    stop_relay();
    printf("\n");
    if (g_failures) {
        printf("%d of %d netclient assertions failed\n", g_failures, g_tests);
        return 1;
    }
    printf("all %d netclient assertions passed\n", g_tests);
    return 0;
}
