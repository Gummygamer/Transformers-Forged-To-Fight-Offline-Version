/* Tests for the Arena realtime fight bridge (tools/nativehook/arena.c).
 *
 * arena.c reaches into il2cpp only through the ArenaOps struct, so a desktop build can
 * substitute fake controllers and fake entry points and exercise the real logic: which
 * controller's input gets sent, which one remote input gets applied to, and when the AI
 * gets paused. The paired cases fork a child that runs the same code under a different
 * peer name against a real netrelay process, which is the wire path a device takes.
 *
 * Build and run: bash tools/netrelay/run_tests.sh
 */
#include "../nativehook/arena.h"

#include <arpa/inet.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <signal.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/types.h>
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

static void arena_log(const char *fmt, ...) {
    va_list ap;
    char buf[512];
    if (!getenv("NETRELAY_TEST_VERBOSE")) return;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    fprintf(stderr, "    arena: %s\n", buf);
}

/* ---------------------------------------------------------------- fakes */

/* Fighter state blobs laid out at the real PlayerController offsets. arena.c never reads
 * anything but the fixed offsets in arena.h, so a byte blob is an honest stand-in -- and it
 * is the same shape the game hands the module at runtime. 0x200 covers every offset used. */
#define FC_SIZE 0x200
static uint64_t g_fc_pool[64][FC_SIZE / 8];   /* uint64_t storage forces 8-byte alignment */
static int g_fc_used;

static void *fc_new(int id) {
    uint8_t *p = (uint8_t *)g_fc_pool[g_fc_used++];
    memset(p, 0, FC_SIZE);
    *(int32_t *)(p + ARENA_PC_ID) = id;
    return p;
}

static void fc_set_attributes(void *fc, void *attrs) {
    *(void **)((uintptr_t)fc + ARENA_PC_ATTRIBUTES) = attrs;
}

static void fc_set_opponent(void *fc, void *other) {
    *(void **)((uintptr_t)fc + ARENA_PC_OPPONENT) = other;
}

/* Aligned to 8 bytes on purpose: arena.c rejects a pointer that is not 8-aligned and above
 * 0x100000, exactly as hook.c's obj_ok does, because that is what an il2cpp managed object
 * reference always looks like. A bare 4-byte struct would be rejected at odd pool indices,
 * which would make the fake disagree with the real allocator. */
typedef struct {
    float health;
} __attribute__((aligned(8))) FakeAttrs;

static FakeAttrs g_attrs[8];
static int g_attrs_used;

static void *attrs_new(float health) {
    FakeAttrs *a = &g_attrs[g_attrs_used++];
    a->health = health;
    if ((uintptr_t)a & 7) { fprintf(stderr, "fake attrs misaligned at %p\n", (void *)a); abort(); }
    return a;
}

/* An AIController drives whichever PlayerController sits at ARENA_AI_PLAYER. */
static void *ai_new(void *driven) {
    void *ai = fc_new(-1);
    *(void **)((uintptr_t)ai + ARENA_AI_PLAYER) = driven;
    return ai;
}

/* Recorded il2cpp calls, so an assertion can say what the bridge actually did. */
static int g_action_count;
static void *g_action_who[64];
static int g_action_val[64];
static int g_special_count;
static void *g_special_who[64];
static int g_special_val[64];
static int g_health_writes;
static void *g_health_who[64];
static float g_health_val[64];

static void fake_pc_action(void *self, int action, void *method) {
    (void)method;
    if (g_action_count < 64) {
        g_action_who[g_action_count] = self;
        g_action_val[g_action_count] = action;
    }
    g_action_count++;
}

static void fake_pc_special(void *self, int index, void *method) {
    (void)method;
    if (g_special_count < 64) {
        g_special_who[g_special_count] = self;
        g_special_val[g_special_count] = index;
    }
    g_special_count++;
}

static float fake_get_health(void *self, void *method) {
    (void)method;
    return self ? ((FakeAttrs *)self)->health : -1.0f;
}

static void fake_set_health(void *self, float value, void *method) {
    (void)method;
    if (!self) return;
    ((FakeAttrs *)self)->health = value;
    if (g_health_writes < 64) {
        g_health_who[g_health_writes] = self;
        g_health_val[g_health_writes] = value;
    }
    g_health_writes++;
}

static void install_fakes(void) {
    ArenaOps ops;
    g_fc_used = g_attrs_used = 0;
    g_action_count = g_special_count = g_health_writes = 0;
    memset(&ops, 0, sizeof ops);
    ops.pc_action = (void *)fake_pc_action;
    ops.pc_special_attack = (void *)fake_pc_special;
    ops.ai_set_paused = NULL;
    ops.attr_get_health = (void *)fake_get_health;
    ops.attr_set_health = (void *)fake_set_health;
    arena_set_ops(&ops);
}

static void reset_recorders(void) {
    g_action_count = g_special_count = g_health_writes = 0;
}

/* ---------------------------------------------------------------- relay */

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

static void fill_config(ArenaConfig *cfg, const char *peer) {
    memset(cfg, 0, sizeof *cfg);
    snprintf(cfg->host, sizeof cfg->host, "127.0.0.1");
    cfg->port = g_port;
    snprintf(cfg->room, sizeof cfg->room, "arena_versus");
    snprintf(cfg->peer, sizeof cfg->peer, "%s", peer);
    cfg->state_interval_ms = 20;   /* small so a health mirror lands within a test */
}

/* Wait until the peer is in the room. Returns 1 on success. Without this the paired tests
 * race the relay join and fail intermittently for reasons that have nothing to do with the
 * code under test. */
static int wait_live(int timeout_ms) {
    int waited = 0;
    while (waited < timeout_ms) {
        if (arena_is_live()) return 1;
        arena_tick();
        usleep(10000);
        waited += 10;
    }
    return arena_is_live();
}

/* ---------------------------------------------------------------- pure logic */

static void test_kv_parsing(void) {
    char out[32];
    report("kv reads the first key", arena_kv("action=256,special=0", "action", out, sizeof out) && !strcmp(out, "256"), out);
    report("kv reads a later key", arena_kv("action=256,special=0", "special", out, sizeof out) && !strcmp(out, "0"), out);
    report("kv reads a negative value", arena_kv("action=-1,special=3", "action", out, sizeof out) && !strcmp(out, "-1"), out);
    report("kv reads a fractional value", arena_kv("health=4820.5", "health", out, sizeof out) && !strcmp(out, "4820.5"), out);
    report("kv misses an absent key", arena_kv("action=1", "health", out, sizeof out) == 0, "");
    report("kv misses on an empty payload", arena_kv("", "action", out, sizeof out) == 0, "");
    /* A prefix must not match: "actionx=9" is a different key from "action". */
    report("kv does not match a key prefix", arena_kv("actionx=9", "action", out, sizeof out) == 0, out);
    report("kv does not match a key suffix", arena_kv("xaction=9", "action", out, sizeof out) == 0, out);
    report("kv clears out when the key is absent",
           (snprintf(out, sizeof out, "stale"), arena_kv("action=1", "health", out, sizeof out) == 0 && out[0] == 0), out);
    report("kv truncates rather than overruns",
           arena_kv("action=1234567890123456789012345678901234567890", "action", out, 8) && strlen(out) == 7, out);
    report("kv rejects a null payload", arena_kv(NULL, "action", out, sizeof out) == 0, "");
    report("kv rejects a zero cap", arena_kv("action=1", "action", out, 0) == 0, "");
}

static void test_payload_encoding_round_trips(void) {
    char buf[64], out[32];
    report("input encoding fits the buffer", arena_encode_input(256, 0, buf, sizeof buf) == 1, buf);
    report("input encoding has no pipe", strchr(buf, '|') == NULL, buf);
    report("input encoding round-trips the action", arena_kv(buf, "action", out, sizeof out) && !strcmp(out, "256"), buf);
    report("input encoding round-trips the special", arena_kv(buf, "special", out, sizeof out) && !strcmp(out, "0"), buf);
    report("an absent action encodes as -1",
           arena_encode_input(-1, 2, buf, sizeof buf) && arena_kv(buf, "action", out, sizeof out) && !strcmp(out, "-1"), buf);
    report("state encoding round-trips health",
           arena_encode_state(4820.5f, buf, sizeof buf) && arena_kv(buf, "health", out, sizeof out) && atof(out) > 4820.4 && atof(out) < 4820.6, buf);
    report("state encoding has no pipe", strchr(buf, '|') == NULL, buf);
    report("encoding refuses a buffer too small", arena_encode_input(256, 0, buf, 5) == 0, "");
    report("state encoding refuses a null buffer", arena_encode_state(1.0f, NULL, 0) == 0, "");
}

static void test_config_loading(void) {
    ArenaConfig cfg;
    const char *good = "/tmp/arena_cfg_good.txt";
    const char *sparse = "/tmp/arena_cfg_sparse.txt";
    const char *long_peer = "/tmp/arena_cfg_longpeer.txt";
    FILE *f;

    f = fopen(good, "w");
    fprintf(f, "# a comment line\n\nhost = 192.168.1.20\nport=9001\nroom=arena_versus\npeer=deviceA\nstate_interval_ms=50\n");
    fclose(f);
    report("a complete config loads", arena_config_load(good, &cfg) == 1, "");
    report("host is read", !strcmp(cfg.host, "192.168.1.20"), cfg.host);
    report("whitespace around host is trimmed", strchr(cfg.host, ' ') == NULL, cfg.host);
    report("port is read", cfg.port == 9001, "");
    report("room is read", !strcmp(cfg.room, "arena_versus"), cfg.room);
    report("peer is read", !strcmp(cfg.peer, "deviceA"), cfg.peer);
    report("state interval is read", cfg.state_interval_ms == 50, "");
    report("comments and blank lines are skipped", cfg.host[0] == '1', cfg.host);

    f = fopen(sparse, "w");
    fprintf(f, "host=10.0.0.5\nroom=r\n");
    fclose(f);
    report("a config without a peer is rejected", arena_config_load(sparse, &cfg) == 0, "");
    report("port defaults when absent", cfg.port == 8777, "");
    report("state interval defaults when absent", cfg.state_interval_ms == 100, "");

    f = fopen(long_peer, "w");
    fprintf(f, "host=10.0.0.5\nroom=r\npeer=");
    { int i; for (i = 0; i < 200; i++) fputc('p', f); }
    fputc('\n', f);
    fclose(f);
    /* A truncated peer name would make two devices collide in the relay's roster, so an
     * over-long value must fail the load rather than be shortened. */
    report("an over-long peer is rejected, not truncated", arena_config_load(long_peer, &cfg) == 0, "");

    report("a missing file is rejected", arena_config_load("/tmp/arena_cfg_absent.txt", &cfg) == 0, "");
    report("a null path is rejected", arena_config_load(NULL, &cfg) == 0, "");
    report("a null out is rejected", arena_config_load(good, NULL) == 0, "");

    f = fopen(good, "w");
    fprintf(f, "host=10.0.0.5\nthis line has no equals sign\nroom=r\npeer=deviceA\n");
    fclose(f);
    report("a line without '=' is skipped, not fatal", arena_config_load(good, &cfg) == 1 && !strcmp(cfg.peer, "deviceA"), cfg.peer);
    unlink(good);
    unlink(sparse);
    unlink(long_peer);
}

/* The suite is normally built WITHOUT -DTFTF_ARENA_DEFAULT_*, so this asserts the "no
 * session baked in" side. run_tests.sh builds a second binary with them set to assert the
 * other side; see test_arena_defaults.c expectations there. */
static void test_compile_time_defaults(void) {
    ArenaConfig cfg;
    int have = arena_config_defaults(&cfg);
    if (have) {
        report("a built-in session is complete", cfg.host[0] && cfg.room[0] && cfg.peer[0], cfg.host);
        report("a built-in session carries a port", cfg.port > 0, "");
    } else {
        report("a build without defaults reports no session", have == 0, "");
        report("a build without defaults leaves the config empty", !cfg.host[0] && !cfg.room[0], "");
        report("a build without defaults still sets a port", cfg.port == TFTF_ARENA_DEFAULT_PORT, "");
    }
    report("defaults reject a null out", arena_config_defaults(NULL) == 0, "");
}

static void test_start_from_file_falls_back_to_defaults(void) {
    ArenaConfig cfg;
    install_fakes();
    /* A missing file must not be fatal when the build carries a session: an unrooted phone
     * cannot have anything written into its app-private directory at all. */
    if (arena_config_defaults(&cfg)) {
        cfg.port = g_port;
        report("start_from_file falls back to the built-in session",
               arena_start_from_file("/tmp/arena_cfg_absent.txt") == 0, "");
        report("the bridge came up from defaults", arena_is_started() == 1, "");
        arena_stop();
    } else {
        report("without a built-in session, start_from_file still refuses a bad path",
               arena_start_from_file("/tmp/arena_cfg_absent.txt") < 0, "");
        report("a refused fallback start leaves it stopped", arena_is_started() == 0, "");
    }
}

static void test_start_validation(void) {
    ArenaConfig cfg;
    install_fakes();
    fill_config(&cfg, "alice");
    cfg.host[0] = 0;
    report("start rejects an empty host", arena_start(&cfg) < 0, "");
    fill_config(&cfg, "alice");
    cfg.room[0] = 0;
    report("start rejects an empty room", arena_start(&cfg) < 0, "");
    fill_config(&cfg, "alice");
    cfg.peer[0] = 0;
    report("start rejects an empty peer", arena_start(&cfg) < 0, "");
    report("start rejects a null config", arena_start(NULL) < 0, "");
    report("a rejected start leaves the bridge stopped", arena_is_started() == 0, "");
    report("is_live is false while stopped", arena_is_live() == 0, "");
    report("input capture is refused while stopped", arena_on_local_action((void *)0x100000, 1) == 0, "");
    report("the AI is never paused while stopped", arena_should_pause_ai((void *)0x100000) == 0, "");
    report("tick does nothing while stopped", arena_tick() == 0, "");
    arena_stop();
    report("stop while not started is harmless", arena_is_started() == 0, "");
}

static void test_singleton_start(void) {
    ArenaConfig cfg;
    install_fakes();
    fill_config(&cfg, "alice");
    report("start succeeds against a live relay", arena_start(&cfg) == 0, "");
    report("the bridge reports itself started", arena_is_started() == 1, "");
    fill_config(&cfg, "bob");
    report("a second start is refused", arena_start(&cfg) < 0, "");
    report("the refused second start did not stop the first", arena_is_started() == 1, "");
    arena_stop();
    report("stop clears the started flag", arena_is_started() == 0, "");
    arena_stop();
    report("stop is idempotent", arena_is_started() == 0, "");
}

static void test_start_from_file(void) {
    const char *path = "/tmp/arena_cfg_start.txt";
    ArenaConfig baked;
    int rc;
    FILE *f;
    install_fakes();
    f = fopen(path, "w");
    fprintf(f, "host=127.0.0.1\nport=%d\nroom=arena_versus\npeer=alice\n", g_port);
    fclose(f);
    report("start_from_file brings the bridge up", arena_start_from_file(path) == 0, "");
    report("the bridge is started", arena_is_started() == 1, "");
    arena_stop();

    /* A bad path is only an error on a build with no session baked in. With
     * -DTFTF_ARENA_DEFAULT_* set, start_from_file legitimately falls back to the compile-time
     * session -- that fallback is what lets an unrooted phone, which cannot have anything
     * written into its app-private directory, run a relay at all. Asserting rejection
     * unconditionally would not only fail here, it would also leave that session running, and
     * then every later arena_start in this binary would be refused by the singleton guard and
     * report a spurious "start failed". */
    rc = arena_start_from_file("/tmp/arena_cfg_absent.txt");
    if (arena_config_defaults(&baked)) {
        report("with a built-in session, start_from_file falls back on a bad path", rc == 0, "");
        report("the fallback start left the bridge running", arena_is_started() == 1, "");
    } else {
        report("start_from_file rejects a bad path", rc < 0, "");
        report("a rejected start_from_file leaves it stopped", arena_is_started() == 0, "");
    }
    /* Unconditional: stop while not started is a no-op, and the next test needs a clean slate
     * no matter which branch above ran. */
    arena_stop();
    report("start_from_file left no session behind", arena_is_started() == 0, "");
    unlink(path);
}

static void test_controller_gating_needs_no_network(void) {
    void *local, *remote, *ai_local, *ai_remote;
    ArenaConfig cfg;
    install_fakes();
    fill_config(&cfg, "alice");
    if (arena_start(&cfg) != 0) { report("gating: start", 0, "start failed"); return; }

    local = fc_new(0);
    remote = fc_new(1);
    fc_set_attributes(local, attrs_new(100.0f));
    fc_set_attributes(remote, attrs_new(200.0f));
    fc_set_opponent(local, remote);
    fc_set_opponent(remote, local);
    ai_local = ai_new(local);
    ai_remote = ai_new(remote);
    arena_set_controllers(local, remote);

    report("the remote controller is exposed", arena_remote_controller() == remote, "");
    report("has_remote is true once set", arena_has_remote() == 1, "");
    report("is_live is false with no peer in the room", arena_is_live() == 0, "");
    report("the local fighter's AI is never paused", arena_should_pause_ai(ai_local) == 0, "");
    report("the remote fighter's AI is not paused while offline", arena_should_pause_ai(ai_remote) == 0, "");
    report("a null AI controller is not paused", arena_should_pause_ai(NULL) == 0, "");
    report("an unrelated AI controller is not paused", arena_should_pause_ai(fc_new(-1)) == 0, "");

    /* Capture is allowed while offline -- the frames queue for the peer -- but only ever
     * from the local controller. Echoing the opponent's action back is the failure mode
     * this guards: it would double every remote move. */
    reset_recorders();
    report("an action on the remote controller is not captured",
           arena_on_local_action(remote, 256) == 0, "");
    report("a special on the remote controller is not captured",
           arena_on_local_special(remote, 2) == 0, "");
    report("an action on a null controller is not captured",
           arena_on_local_action(NULL, 256) == 0, "");
    report("an action on the local controller is captured",
           arena_on_local_action(local, 256) == 1, "");
    report("a special on the local controller is captured",
           arena_on_local_special(local, 1) == 1, "");
    report("Action(None) is not captured", arena_on_local_action(local, 0) == 0, "");
    report("a negative action is not captured", arena_on_local_action(local, -1) == 0, "");
    report("a negative special is not captured", arena_on_local_special(local, -1) == 0, "");

    arena_stop();
    report("the remote controller is cleared on stop", arena_has_remote() == 0, "");
}

/* ---------------------------------------------------------------- paired */

/* The bridge is a process-wide singleton (one fight per process), so each side of a
 * pairing runs in its own process. The child forks BEFORE the parent starts, otherwise it
 * would inherit g_started and its own start would be refused. */
static int role_send_input(void) {
    void *local, *remote;
    int i;
    install_fakes();
    local = fc_new(0);
    remote = fc_new(1);
    fc_set_attributes(local, attrs_new(4820.5f));
    fc_set_attributes(remote, attrs_new(9999.0f));
    arena_set_controllers(local, remote);
    for (i = 0; i < 120 && !arena_is_live(); i++) { arena_tick(); usleep(25000); }
    if (!arena_is_live()) return 4;
    if (!arena_on_local_action(local, 256)) return 5;
    if (!arena_on_local_special(local, 2)) return 6;
    /* Keep ticking so the periodic health mirror goes out. */
    for (i = 0; i < 60; i++) { arena_tick(); usleep(20000); }
    return 0;
}

static void test_input_and_health_cross(void) {
    ArenaConfig cfg;
    void *local, *remote;
    int status, i, saw_action = 0, saw_special = 0, saw_health = 0;
    pid_t kid;

    install_fakes();
    fill_config(&cfg, "alice");
    kid = fork();
    if (kid == 0) {
        int rc;
        ArenaConfig child_cfg;
        install_fakes();
        fill_config(&child_cfg, "bob");
        rc = arena_start(&child_cfg) == 0 ? role_send_input() : 1;
        arena_stop();
        _exit(rc);
    }
    if (kid < 0) { report("fork for input crossing", 0, "fork failed"); return; }

    if (arena_start(&cfg) != 0) {
        report("input crosses: start", 0, "start failed");
        waitpid(kid, &status, 0);
        return;
    }
    local = fc_new(0);
    remote = fc_new(1);
    fc_set_attributes(local, attrs_new(100.0f));
    fc_set_attributes(remote, attrs_new(200.0f));
    arena_set_controllers(local, remote);

    if (!wait_live(4000)) report("the peer joins the room", 0, "never went live");
    else report("the peer joins the room", 1, "");

    for (i = 0; i < 150 && !(saw_action && saw_special && saw_health); i++) {
        arena_tick();
        if (g_action_count && !saw_action) saw_action = 1;
        if (g_special_count && !saw_special) saw_special = 1;
        if (g_health_writes && !saw_health) saw_health = 1;
        usleep(20000);
    }

    report("the remote action is applied", saw_action == 1, "");
    if (saw_action) {
        report("the remote action lands on the local remote controller", g_action_who[0] == remote, "");
        report("the remote action keeps its value", g_action_val[0] == 256, "");
        report("the remote action never lands on the local player", g_action_who[0] != local, "");
    }
    report("the remote special is applied", saw_special == 1, "");
    if (saw_special) {
        report("the remote special lands on the local remote controller", g_special_who[0] == remote, "");
        report("the remote special keeps its index", g_special_val[0] == 2, "");
    }
    report("the peer's health mirror is applied", saw_health == 1, "");
    if (saw_health) {
        report("the mirror writes the remote fighter's health",
               g_health_val[0] > 4820.4f && g_health_val[0] < 4820.6f, "");
        report("the mirror targets the remote fighter's attributes",
               g_health_who[0] == *(void **)((uintptr_t)remote + ARENA_PC_ATTRIBUTES), "");
    }

    arena_stop();
    waitpid(kid, &status, 0);
    report("the sending child exited cleanly", WIFEXITED(status) && WEXITSTATUS(status) == 0, "");
}

static int role_pause_check(void) {
    int i;
    for (i = 0; i < 120; i++) { arena_tick(); usleep(25000); }
    return 0;
}

static void test_ai_is_paused_only_for_the_remote_fighter(void) {
    ArenaConfig cfg;
    void *local, *remote, *ai_local, *ai_remote, *ai_other;
    int status, i;
    pid_t kid;

    install_fakes();
    fill_config(&cfg, "alice");
    kid = fork();
    if (kid == 0) {
        int rc;
        ArenaConfig child_cfg;
        fill_config(&child_cfg, "bob");
        install_fakes();
        if (arena_start(&child_cfg) != 0) _exit(1);
        {
            void *cl = fc_new(0), *cr = fc_new(1);
            fc_set_attributes(cl, attrs_new(50.0f));
            fc_set_attributes(cr, attrs_new(50.0f));
            arena_set_controllers(cl, cr);
        }
        rc = role_pause_check();
        arena_stop();
        _exit(rc);
    }
    if (kid < 0) { report("fork for the AI pause test", 0, "fork failed"); return; }
    if (arena_start(&cfg) != 0) {
        report("ai pause: start", 0, "start failed");
        waitpid(kid, &status, 0);
        return;
    }
    local = fc_new(0);
    remote = fc_new(1);
    fc_set_attributes(local, attrs_new(100.0f));
    fc_set_attributes(remote, attrs_new(200.0f));
    ai_local = ai_new(local);
    ai_remote = ai_new(remote);
    ai_other = ai_new(fc_new(2));
    arena_set_controllers(local, remote);

    report("before the peer joins, no AI is paused", arena_should_pause_ai(ai_remote) == 0, "");
    if (!wait_live(4000)) { report("ai pause: peer joined", 0, "never went live"); }
    else report("ai pause: peer joined", 1, "");

    report("once live, the remote fighter's AI is paused", arena_should_pause_ai(ai_remote) == 1, "");
    report("once live, the local fighter's AI is not paused", arena_should_pause_ai(ai_local) == 0, "");
    report("once live, an unrelated fighter's AI is not paused", arena_should_pause_ai(ai_other) == 0, "");

    for (i = 0; i < 20; i++) { arena_tick(); usleep(20000); }
    arena_stop();
    report("after stop the AI is no longer paused", arena_should_pause_ai(ai_remote) == 0, "");
    waitpid(kid, &status, 0);
}

static int role_end_the_fight(void) {
    void *local, *remote;
    int i;
    install_fakes();
    local = fc_new(0);
    remote = fc_new(1);
    fc_set_attributes(local, attrs_new(50.0f));
    fc_set_attributes(remote, attrs_new(50.0f));
    arena_set_controllers(local, remote);
    for (i = 0; i < 60 && !arena_is_live(); i++) { arena_tick(); usleep(25000); }
    if (!arena_is_live()) return 4;
    if (!arena_send_result("WON")) return 5;
    /* A move after the result must still leave this device, so the peer can see that the
     * session ended on its side rather than merely going quiet. */
    usleep(300000);
    if (arena_on_local_action(local, 512) != 0) return 6;
    for (i = 0; i < 40; i++) { arena_tick(); usleep(20000); }
    return 0;
}

static void test_result_event_ends_the_session(void) {
    ArenaConfig cfg;
    void *local, *remote;
    int status, i, ended = 0;
    pid_t kid;

    install_fakes();
    fill_config(&cfg, "alice");
    kid = fork();
    if (kid == 0) {
        ArenaConfig child_cfg;
        int rc;
        fill_config(&child_cfg, "bob");
        if (arena_start(&child_cfg) != 0) _exit(1);
        rc = role_end_the_fight();
        arena_stop();
        _exit(rc);
    }
    if (kid < 0) { report("fork for the result event test", 0, "fork failed"); return; }
    if (arena_start(&cfg) != 0) {
        report("result event: start", 0, "start failed");
        waitpid(kid, &status, 0);
        return;
    }
    local = fc_new(0);
    remote = fc_new(1);
    fc_set_attributes(local, attrs_new(100.0f));
    fc_set_attributes(remote, attrs_new(200.0f));
    arena_set_controllers(local, remote);

    if (!wait_live(4000)) { report("result event: peer joined", 0, "never went live"); }
    else report("result event: peer joined", 1, "");

    reset_recorders();
    for (i = 0; i < 200 && !ended; i++) {
        arena_tick();
        if (!arena_is_live()) ended = 1;
        usleep(20000);
    }
    report("a result event takes the session out of live", ended == 1, "");
    reset_recorders();
    for (i = 0; i < 40; i++) { arena_tick(); usleep(20000); }
    report("input after the result event is not applied", g_action_count == 0, "");
    arena_stop();
    waitpid(kid, &status, 0);
}

int main(void) {
    arena_set_logger(arena_log);
    g_port = free_port();
    if (!g_port) { printf("[!] could not allocate a free UDP port\n"); return 1; }
    printf("[*] relay port %d\n", g_port);
    if (start_relay(g_port, 5000) < 0) { printf("[!] could not start netrelay\n"); return 1; }

    test_kv_parsing();
    test_payload_encoding_round_trips();
    test_config_loading();
    test_compile_time_defaults();
    test_start_from_file_falls_back_to_defaults();
    test_start_validation();
    test_singleton_start();
    test_start_from_file();
    test_controller_gating_needs_no_network();
    test_input_and_health_cross();
    test_ai_is_paused_only_for_the_remote_fighter();
    test_result_event_ends_the_session();

    stop_relay();
    printf("\n");
    if (g_failures) {
        printf("%d of %d arena assertions failed\n", g_failures, g_tests);
        return 1;
    }
    printf("all %d arena assertions passed\n", g_tests);
    return 0;
}
