#ifndef TFTF_ARENA_H
#define TFTF_ARENA_H

#include <stddef.h>

/* Arena realtime fight bridge -- the il2cpp side of the netcode.
 *
 * WHAT THIS ADDS TO THE GAME
 *   Retail Arena is asynchronous: you fight a local AI copy of the opponent's stored team,
 *   so two players never actually meet. This module turns one of the two fighters in a
 *   local fight into a proxy for a human on the other device. The model is
 *   authoritative-input with authoritative health:
 *
 *     - Every Action / SpecialAttack the LOCAL player issues is captured and sent to the
 *       peer (IN packet).
 *     - The peer's IN packets are replayed onto the controller this device draws as the
 *       opponent, and that opponent's AI is paused so it stops fighting on its own.
 *     - Each device also sends its own player's health (ST packet); the peer writes it
 *       onto the proxy. Health is mirrored rather than simulated so a difference in local
 *       damage rolls cannot make the two screens disagree about who is alive.
 *
 *   That is deliberately NOT rollback or lockstep. The shipped simulation stays in charge
 *   of animation, hit detection and combo state, which keeps both devices playable on a
 *   LAN with one-frame-scale latency. The cost is that a hit landing on one screen can lag
 *   the other by a round trip; the health mirror bounds how far the two can drift.
 *
 * WHY EVERY il2cpp CALL GOES THROUGH ArenaOps
 *   So this file can be compiled and tested on a desktop with fake controllers and fake
 *   il2cpp entry points (see tools/netrelay/test_arena.c). It also keeps arena.c free of
 *   any dependency on hook.c's statics.
 *
 * THREADING
 *   All functions here must be called from the Unity main thread. Socket I/O happens in
 *   netclient's own thread, which only ever moves text across the boundary.
 */

#if !defined(__aarch64__) && !defined(__x86_64__)
#error "arena.c offsets are arm64-only; armeabi-v7a needs patches/abi_map.lbl fields first"
#endif

/* PlayerController field offsets, from re_notes/dump.cs (public class PlayerController).
 * arm64 only -- see the #error above for why this file refuses a 32-bit ARM build. */
#define ARENA_PC_ATTRIBUTES 0x80   /* PlayerAttributes Attributes */
#define ARENA_PC_ID         0xF4   /* int <Id> : the local player is 0, the opponent is 1 */
#define ARENA_PC_OPPONENT   0xF8   /* PlayerController <Opponent> */
#define ARENA_PC_INPUT_ON   0x139  /* bool <InputEnabled> */

/* AIController field offsets, from re_notes/dump.cs (public class AIController). */
#define ARENA_AI_PLAYER     0x90   /* PlayerController PlayerController */

/* il2cpp entry points, as absolute addresses once the image base is known. */
typedef struct {
    void *pc_action;          /* PlayerController.Action(int)                    0x1179AF4 */
    void *pc_special_attack;  /* PlayerController.SpecialAttack(int)             0x1174300 */
    void *ai_set_paused;      /* AIController.SetPaused(bool)                    0xDB1D18  */
    void *attr_get_health;    /* PlayerAttributes.get_Health()                   0xDAC660  */
    void *attr_set_health;    /* PlayerAttributes.set_Health(float)              0xDAC67C  */
} ArenaOps;

/* Session configuration.
 *
 * WHERE IT COMES FROM, and why compile-time defaults exist at all:
 *   A rooted device gets a config file dropped into its app-private directory, which is what
 *   the emulator hot-deploy path does. A device that cannot be rooted -- the usual case for a
 *   real phone -- cannot have a file written there, and /data/local/tmp is not readable by an
 *   untrusted_app under SELinux, so an adb push does not help either. For those builds the
 *   session is baked in at compile time with -D, which the phone APK build already does for
 *   the server host, so the value is known then anyway.
 *
 *   The peer name is a readable label. The relay disambiguates duplicate labels by UDP
 *   endpoint, although distinct names are still useful for readable logs.
 */
#ifndef TFTF_ARENA_DEFAULT_HOST
#define TFTF_ARENA_DEFAULT_HOST ""
#endif
#ifndef TFTF_ARENA_DEFAULT_PORT
#define TFTF_ARENA_DEFAULT_PORT 8777
#endif
#ifndef TFTF_ARENA_DEFAULT_ROOM
#define TFTF_ARENA_DEFAULT_ROOM ""
#endif
#ifndef TFTF_ARENA_DEFAULT_PEER
#define TFTF_ARENA_DEFAULT_PEER ""
#endif
#ifndef TFTF_ARENA_DEFAULT_STATE_INTERVAL_MS
#define TFTF_ARENA_DEFAULT_STATE_INTERVAL_MS 100
#endif

typedef struct {
    char host[96];
    int port;
    char room[48];
    char peer[48];
    int state_interval_ms;   /* how often to mirror health; 0 means every tick */
} ArenaConfig;

typedef void (*arena_log_fn)(const char *fmt, ...);

void arena_set_logger(arena_log_fn fn);
void arena_set_ops(const ArenaOps *ops);

/* Parse "key=value,key=value" out of a relay payload. Returns 1 when found. The payload
 * alphabet deliberately excludes '|' and ',' so a value can never swallow a delimiter. */
int arena_kv(const char *payload, const char *key, char *out, size_t cap);

/* Encode the two packet payloads. Returns 1 on success, 0 when the buffer is too small. */
int arena_encode_input(int action, int special, char *out, size_t cap);
int arena_encode_state(float health, char *out, size_t cap);

/* Parse a config file of "key=value" lines. Missing keys keep their defaults, so a file
 * with only host and room still works. Returns 1 when at least host and room are present. */
int arena_config_load(const char *path, ArenaConfig *out);

/* Fill in the session baked into this build with -DTFTF_ARENA_DEFAULT_*. Returns 1 when that
 * build carries a usable session, 0 when it was compiled without one. */
int arena_config_defaults(ArenaConfig *out);

/* Bring up the session. Returns 0 on success, negative on failure. A second start without
 * a stop is refused so two pumps never drive one fight. */
int arena_start(const ArenaConfig *cfg);
int arena_start_from_file(const char *path);
void arena_stop(void);
int arena_is_started(void);

/* The two controllers in the current fight, captured from PlayerAttributes.Init. */
void arena_set_controllers(void *local_controller, void *remote_controller);
void *arena_remote_controller(void);
int arena_has_remote(void);

/* True when the peer is in the room, which is the gate for pausing the opponent's AI. */
int arena_is_live(void);

/* Capture one local input. Called from the PlayerController.Action / SpecialAttack hooks
 * only when the controller is the local one. Returns 1 when it was sent. */
int arena_on_local_action(void *controller, int action);
int arena_on_local_special(void *controller, int special_index);

/* Pause or resume the opponent's AI. Called from AIController.Simulate; returns 1 when the
 * AI for this controller should be skipped entirely this tick. */
int arena_should_pause_ai(void *ai_controller);

/* One simulation tick: mirror local health, then drain and apply everything the peer sent.
 * Returns the number of inbound packets applied. */
int arena_tick(void);

/* Tell the peer this fight is over and stop relaying. `result` is the local outcome, so the
 * two devices may still disagree and let the server's existing /pvp/report-result
 * reconciliation decide -- this packet only ends the realtime session. Returns 1 when sent. */
int arena_send_result(const char *result);

/* True once a result has been seen from either side, which stops input capture and the
 * health mirror so a post-fight screen is not driven by stray packets. */
int arena_has_ended(void);

#endif /* TFTF_ARENA_H */
