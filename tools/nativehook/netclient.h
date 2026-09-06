#ifndef TFTF_NETCLIENT_H
#define TFTF_NETCLIENT_H

#include <stddef.h>

/* Client half of the Arena realtime netcode relay (see tools/netrelay/netrelay.c).
 *
 * THREAD RULE -- the reason this is a queue and not a direct socket call
 *   il2cpp objects may only be touched from the Unity main thread; calling into managed
 *   objects from an arbitrary thread corrupts the GC. So this module is strictly
 *   byte-oriented: the game thread enqueues text and drains received text, and the one
 *   background thread here does all socket I/O and never sees an il2cpp pointer.
 *
 * OUTBOUND (game thread -> net thread), all non-blocking, all bounded:
 *   tftf_net_start(host, port, room, peer)   open the socket, say HELLO, spawn the thread
 *   tftf_net_send(kind, seq, payload)        kind is TFTF_NET_IN / _ST / _EV
 *   tftf_net_stop()                          say BYE and join the thread
 *
 * INBOUND (net thread -> game thread), drained from Simulation.FixedUpdate:
 *   tftf_net_recv(out_cmd, out_peer, out_seq, out_payload)  pop one packet, 0 when empty
 *   tftf_net_peer_count()      live peers in the room, from the last OK or PR
 *   tftf_net_is_live()         a peer other than us is in the room right now
 *
 * A packet larger than TFTF_NET_MAX_PAYLOAD is refused rather than truncated, because a
 * half-delivered state frame would desynchronise the two fighters.
 */

#define TFTF_NET_MAX_PAYLOAD 384
#define TFTF_NET_MAX_LINE 512       /* must stay <= the relay's NETRELAY_MAX_PACKET */
#define TFTF_NET_MAX_NAME 48
#define TFTF_NET_INBOX 64           /* inbound ring capacity, in packets */
#define TFTF_NET_OUTBOX 64          /* outbound ring capacity, in packets */

#define TFTF_NET_IN "IN"
#define TFTF_NET_ST "ST"
#define TFTF_NET_EV "EV"

typedef void (*tftf_net_log_fn)(const char *fmt, ...);

void tftf_net_set_logger(tftf_net_log_fn fn);

/* Start the client. Returns 0 on success, negative on failure. Safe to call once; a
 * second start without a stop is refused so two threads never share one socket. */
int tftf_net_start(const char *host, int port, const char *room, const char *peer);

/* Enqueue one outbound packet. Returns 1 when queued, 0 when refused (not started,
 * payload too large, or the outbound ring is full). Never blocks. */
int tftf_net_send(const char *kind, unsigned int seq, const char *payload);

/* Pop one inbound packet. Returns 1 and fills the outputs when a packet was waiting,
 * 0 when the inbox is empty. Buffers must be at least the sizes given. */
int tftf_net_recv(char *out_cmd, size_t cmd_cap,
                  char *out_peer, size_t peer_cap,
                  unsigned int *out_seq,
                  char *out_payload, size_t payload_cap);

int tftf_net_peer_count(void);
int tftf_net_is_live(void);
int tftf_net_is_started(void);

/* Send BYE and join the background thread. Safe to call when not started. */
void tftf_net_stop(void);

#endif /* TFTF_NETCLIENT_H */
