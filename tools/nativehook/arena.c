/* Arena realtime fight bridge. See arena.h for the model and the threading rule. */
#include "arena.h"
#include "netclient.h"

#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/time.h>

static arena_log_fn g_log;
static ArenaOps g_ops;
static int g_ops_set;
static int g_started;
static void *g_local;      /* this device's player controller (Id == 0) */
static void *g_remote;     /* the controller drawn as the opponent (Id == 1) */
static unsigned int g_in_seq;
static unsigned int g_st_seq;
static long long g_last_state_ms;
static int g_state_interval_ms;
static int g_local_action;      /* pending local action, -1 when none */
static int g_local_special;     /* pending local special, -1 when none */
static int g_applied_inbound;
static int g_ended;             /* the peer reported the fight over */
static unsigned int g_tick_count;
static unsigned int g_state_sent;

static void logmsg(const char *fmt, ...) {
    char buf[512];
    va_list ap;
    if (!g_log) return;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    g_log("%s", buf);
}

static long long now_ms(void) {
    struct timeval tv;
    gettimeofday(&tv, NULL);
    return (long long)tv.tv_sec * 1000LL + tv.tv_usec / 1000LL;
}

void arena_set_logger(arena_log_fn fn) { g_log = fn; }

void arena_set_ops(const ArenaOps *ops) {
    if (!ops) { g_ops_set = 0; return; }
    g_ops = *ops;
    g_ops_set = 1;
}

int arena_kv(const char *payload, const char *key, char *out, size_t cap) {
    size_t keylen;
    const char *p;
    if (!payload || !key || !out || cap == 0) return 0;
    out[0] = 0;
    keylen = strlen(key);
    p = payload;
    while (*p) {
        const char *comma = strchr(p, ',');
        size_t len = comma ? (size_t)(comma - p) : strlen(p);
        if (len > keylen + 1 && !strncmp(p, key, keylen) && p[keylen] == '=') {
            size_t vlen = len - keylen - 1;
            const char *v = p + keylen + 1;
            if (vlen >= cap) vlen = cap - 1;
            memcpy(out, v, vlen);
            out[vlen] = 0;
            return 1;
        }
        if (!comma) break;
        p = comma + 1;
    }
    return 0;
}

int arena_encode_input(int action, int special, char *out, size_t cap) {
    if (!out || cap == 0) return 0;
    /* Both keys are always present so the peer never has to guess which the sender meant. */
    return snprintf(out, cap, "action=%d,special=%d", action, special) < (int)cap;
}

int arena_encode_state(float health, char *out, size_t cap) {
    if (!out || cap == 0) return 0;
    return snprintf(out, cap, "health=%.4f", (double)health) < (int)cap;
}

/* Copy a config value into a fixed field. Returns 0 when it does not fit: a silently
 * truncated peer name is worse than a rejected line, because two devices whose names share
 * a prefix would then look like a single peer to the relay. */
static int copy_field(char *dst, size_t cap, const char *src, const char *key, const char *path) {
    if (strlen(src) >= cap) {
        logmsg("arena: config key '%s' at %s is too long (%zu bytes, limit %zu)",
               key, path ? path : "(null)", strlen(src), cap - 1);
        return 0;
    }
    snprintf(dst, cap, "%s", src);
    return 1;
}

static void trim(char *s) {
    size_t n;
    while (*s == ' ' || *s == '\t' || *s == '\r' || *s == '\n') memmove(s, s + 1, strlen(s));
    n = strlen(s);
    while (n && (s[n - 1] == ' ' || s[n - 1] == '\t' || s[n - 1] == '\r' || s[n - 1] == '\n')) s[--n] = 0;
}

int arena_config_load(const char *path, ArenaConfig *out) {
    enum { LINE_CAP = 256 };
    char line[LINE_CAP];
    FILE *f;
    if (!path || !out) return 0;
    int ok = 1;
    memset(out, 0, sizeof *out);
    out->port = 8777;
    out->state_interval_ms = 100;
    f = fopen(path, "r");
    if (!f) return 0;
    while (fgets(line, sizeof line, f)) {
        char *eq;
        char key[LINE_CAP], value[LINE_CAP];
        trim(line);
        if (!line[0] || line[0] == '#') continue;
        eq = strchr(line, '=');
        if (!eq) continue;
        *eq = 0;
        snprintf(key, sizeof key, "%s", line);
        snprintf(value, sizeof value, "%s", eq + 1);
        trim(key);
        trim(value);
        if (!strcmp(key, "host")) ok &= copy_field(out->host, sizeof out->host, value, key, path);
        else if (!strcmp(key, "port")) out->port = atoi(value);
        else if (!strcmp(key, "room")) ok &= copy_field(out->room, sizeof out->room, value, key, path);
        else if (!strcmp(key, "peer")) ok &= copy_field(out->peer, sizeof out->peer, value, key, path);
        else if (!strcmp(key, "state_interval_ms")) out->state_interval_ms = atoi(value);
    }
    fclose(f);
    return ok && out->host[0] && out->room[0] && out->peer[0];
}

int arena_is_started(void) { return g_started; }

int arena_start(const ArenaConfig *cfg) {
    int rc;
    if (!cfg) return -1;
    if (g_started) return -2;
    if (!cfg->host[0] || !cfg->room[0] || !cfg->peer[0]) {
        logmsg("arena: config needs host, room and peer");
        return -3;
    }
    rc = tftf_net_start(cfg->host, cfg->port, cfg->room, cfg->peer);
    if (rc != 0) {
        logmsg("arena: relay start failed (%d) for %s:%d room=%s peer=%s",
               rc, cfg->host, cfg->port, cfg->room, cfg->peer);
        return rc;
    }
    g_started = 1;
    g_in_seq = 0;
    g_st_seq = 0;
    g_last_state_ms = 0;
    g_state_interval_ms = cfg->state_interval_ms > 0 ? cfg->state_interval_ms : 100;
    g_local = NULL;
    g_remote = NULL;
    g_local_action = -1;
    g_local_special = -1;
    g_applied_inbound = 0;
    g_ended = 0;
    g_tick_count = 0;
    g_state_sent = 0;
    logmsg("arena: started room=%s peer=%s relay=%s:%d state every %dms",
           cfg->room, cfg->peer, cfg->host, cfg->port, g_state_interval_ms);
    return 0;
}

int arena_config_defaults(ArenaConfig *out) {
    int ok;
    if (!out) return 0;
    memset(out, 0, sizeof *out);
    snprintf(out->host, sizeof out->host, "%s", TFTF_ARENA_DEFAULT_HOST);
    out->port = TFTF_ARENA_DEFAULT_PORT;
    snprintf(out->room, sizeof out->room, "%s", TFTF_ARENA_DEFAULT_ROOM);
    snprintf(out->peer, sizeof out->peer, "%s", TFTF_ARENA_DEFAULT_PEER);
    out->state_interval_ms = TFTF_ARENA_DEFAULT_STATE_INTERVAL_MS;
    ok = out->host[0] && out->room[0] && out->peer[0];
    if (!ok) logmsg("arena: this build has no compile-time arena session baked in");
    return ok;
}

int arena_start_from_file(const char *path) {
    ArenaConfig cfg;
    if (arena_config_load(path, &cfg)) {
        logmsg("arena: session from %s", path ? path : "(null)");
        return arena_start(&cfg);
    }
    /* No file is the normal case on a build that carries its session as -D defaults, so this
     * is a fallback rather than an error. An unrooted device cannot have a file written into
     * its app-private directory at all. */
    if (arena_config_defaults(&cfg)) {
        logmsg("arena: no config at %s; using the compile-time session", path ? path : "(null)");
        return arena_start(&cfg);
    }
    logmsg("arena: no session, neither a config at %s nor compile-time defaults",
           path ? path : "(null)");
    return -4;
}

void arena_stop(void) {
    if (!g_started) return;
    g_started = 0;
    tftf_net_stop();
    g_local = NULL;
    g_remote = NULL;
    logmsg("arena: stopped after %u input and %u state packets sent, %d inbound applied",
           g_in_seq, g_st_seq, g_applied_inbound);
}

void arena_set_controllers(void *local_controller, void *remote_controller) {
    g_local = local_controller;
    g_remote = remote_controller;
    if (g_started)
        logmsg("arena: controllers local=%p remote=%p", local_controller, remote_controller);
}

void *arena_remote_controller(void) { return g_remote; }
int arena_has_remote(void) { return g_remote != NULL; }
int arena_is_live(void) { return g_started && !g_ended && tftf_net_is_live(); }

int arena_on_local_action(void *controller, int action) {
    char payload[TFTF_NET_MAX_PAYLOAD];
    if (!g_started || g_ended || !controller || controller != g_local) return 0;
    /* Action(None) is how the game clears a held input; sending it would tell the peer to
     * cancel a move that only this device started. Skip it. */
    if (action <= 0) return 0;
    g_local_action = action;
    if (!arena_encode_input(action, -1, payload, sizeof payload)) return 0;
    if (!tftf_net_send(TFTF_NET_IN, ++g_in_seq, payload)) return 0;
    return 1;
}

int arena_on_local_special(void *controller, int special_index) {
    char payload[TFTF_NET_MAX_PAYLOAD];
    if (!g_started || g_ended || !controller || controller != g_local) return 0;
    if (special_index < 0) return 0;
    g_local_special = special_index;
    if (!arena_encode_input(-1, special_index, payload, sizeof payload)) return 0;
    if (!tftf_net_send(TFTF_NET_IN, ++g_in_seq, payload)) return 0;
    return 1;
}

int arena_should_pause_ai(void *ai_controller) {
    void *driven;
    /* Pause only once the peer is actually in the room: before that the AI is what makes
     * the fight playable at all, and killing it would leave a standing target. */
    if (!arena_is_live()) return 0;
    if (!g_remote || !ai_controller) return 0;
    /* Per-controller, never blanket: a fight has an AIController for every fighter, and
     * pausing the ones still playing locally would freeze them mid-animation. */
    driven = *(void **)((uintptr_t)ai_controller + ARENA_AI_PLAYER);
    return driven != NULL && driven == g_remote;
}

static void apply_inbound(const char *cmd, const char *peer, unsigned int seq, const char *payload) {
    char value[32];
    (void)peer;
    (void)seq;
    if (!strcmp(cmd, TFTF_NET_IN)) {
        int action = -1, special = -1;
        if (!g_remote || !g_ops_set) return;
        if (arena_kv(payload, "action", value, sizeof value)) action = atoi(value);
        if (arena_kv(payload, "special", value, sizeof value)) special = atoi(value);
        if (action > 0 && g_ops.pc_action) {
            ((void (*)(void *, int, void *))g_ops.pc_action)(g_remote, action, NULL);
            g_applied_inbound++;
        }
        if (special >= 0 && g_ops.pc_special_attack) {
            ((void (*)(void *, int, void *))g_ops.pc_special_attack)(g_remote, special, NULL);
            g_applied_inbound++;
        }
        return;
    }
    if (!strcmp(cmd, TFTF_NET_ST)) {
        void *attrs;
        if (!g_remote || !g_ops_set || !g_ops.attr_set_health) return;
        if (!arena_kv(payload, "health", value, sizeof value)) return;
        attrs = *(void **)((uintptr_t)g_remote + ARENA_PC_ATTRIBUTES);
        if (!attrs || (uintptr_t)attrs < 0x100000 || ((uintptr_t)attrs & 7)) return;
        ((void (*)(void *, float, void *))g_ops.attr_set_health)(attrs, (float)atof(value), NULL);
        g_applied_inbound++;
        return;
    }
    if (!strcmp(cmd, TFTF_NET_EV)) {
        if (arena_kv(payload, "result", value, sizeof value)) {
            g_ended = 1;
            logmsg("arena: peer reported result=%s; stopping the relay", value);
        }
        return;
    }
}

int arena_has_ended(void) { return g_started && g_ended; }

int arena_send_result(const char *result) {
    char payload[TFTF_NET_MAX_PAYLOAD];
    if (!g_started || g_ended || !result || !result[0]) return 0;
    if (strlen(result) >= TFTF_NET_MAX_PAYLOAD - 16) return 0;
    snprintf(payload, sizeof payload, "result=%s", result);
    if (!tftf_net_send(TFTF_NET_EV, ++g_in_seq, payload)) return 0;
    /* Mark it ended here too: the fight is over on this device the moment it says so, and
     * waiting for the peer's echo would keep relaying into the results screen. */
    g_ended = 1;
    logmsg("arena: sent result=%s and stopped relaying", result);
    return 1;
}

int arena_tick(void) {
    char cmd[8], peer[TFTF_NET_MAX_NAME], payload[TFTF_NET_MAX_PAYLOAD];
    unsigned int seq = 0;
    int applied = 0;
    long long now;
    if (!g_started || g_ended) return 0;
    g_tick_count++;
    if (g_tick_count <= 3)
        logmsg("arena: tick=%u local=%p remote=%p ops=%d interval=%d",
               g_tick_count, g_local, g_remote, g_ops_set, g_state_interval_ms);

    /* Mirror this device's own player health so both screens agree on who is alive. The
     * interval exists because a health write every fixed tick would flood the relay with
     * frames that carry no new information. */
    now = now_ms();
    if (g_local && g_remote && g_ops_set && g_ops.attr_get_health &&
        now - g_last_state_ms >= g_state_interval_ms) {
        void *attrs = *(void **)((uintptr_t)g_local + ARENA_PC_ATTRIBUTES);
        /* Re-check the pointer the same way hook.c's obj_ok does: an 8-aligned address
         * above the mapped range. A controller captured last fight can be freed by now. */
        if (attrs && (uintptr_t)attrs >= 0x100000 && !((uintptr_t)attrs & 7)) {
            float hp = ((float (*)(void *, void *))g_ops.attr_get_health)(attrs, NULL);
            char st[TFTF_NET_MAX_PAYLOAD];
            if (arena_encode_state(hp, st, sizeof st) && tftf_net_send(TFTF_NET_ST, ++g_st_seq, st)) {
                g_last_state_ms = now;
                g_state_sent++;
                if (g_state_sent == 1)
                    logmsg("arena: first state health=%.4f attrs=%p", (double)hp, attrs);
            }
        } else if (g_tick_count <= 3) {
            logmsg("arena: local attributes unavailable local=%p attrs=%p", g_local, attrs);
        }
    }

    while (tftf_net_recv(cmd, sizeof cmd, peer, sizeof peer, &seq, payload, sizeof payload)) {
        apply_inbound(cmd, peer, seq, payload);
        applied++;
    }
    return applied;
}
