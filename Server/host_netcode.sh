#!/usr/bin/env bash
# Host the full server side of a netcode multiplayer match: the two Legible fake-server
# listeners plus the UDP Arena relay, all advertising one reachable address.
#
# WHY A SCRIPT AND NOT THREE TERMINALS
#   One Legible process holds one listener, so HTTP and HTTPS are separate processes, and
#   the realtime relay is a third. All three must agree on a single advertised address.
#   Getting that wrong is silent: the server answers every request, but the content it
#   serves tells clients to fetch from an address they cannot reach, so the match hangs
#   after the first screen instead of erroring.
#
# USAGE
#   Server/host_netcode.sh                 # advertise the ZeroTier address, then start
#   Server/host_netcode.sh --tunnel-ip X   # advertise an explicit address (Hamachi, Tailscale)
#   Server/host_netcode.sh --lan           # advertise the first physical LAN address
#   Server/host_netcode.sh --check         # report readiness and exit; start nothing
#   Server/host_netcode.sh --stop          # stop what this script started
#
# The relay is UDP and needs DIRECT reachability from both devices. adb reverse forwards TCP
# only and cannot carry it, so --relay-host is always a real tunnel/LAN address here.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"

HTTP_PORT="${HTTP_PORT:-8080}"
HTTPS_PORT="${HTTPS_PORT:-8443}"
RELAY_PORT="${RELAY_PORT:-8777}"
SCHEME="${TFTF_SERVER_SCHEME:-https}"
# Over the Internet a 15s presence TTL is tight: round-trip latency plus a phone dozing can
# expire a peer mid-matchmaking. 30s still forgets a player who genuinely left.
PRESENCE_TTL_MS="${TFTF_PRESENCE_TTL_MS:-30000}"
RUNDIR="${RUNDIR:-$ROOT/build/host-netcode}"
ADDRESS_MODE="tunnel"
ACTION="start"
EXPLICIT_IP=""

usage() { sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; }

while (($#)); do
  case "$1" in
    --tunnel-ip) EXPLICIT_IP="${2:-}"; ADDRESS_MODE="explicit"; shift 2 ;;
    --lan)       ADDRESS_MODE="lan"; shift ;;
    --check)     ACTION="check"; shift ;;
    --stop)      ACTION="stop"; shift ;;
    -h|--help)   usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
done

note() { printf '[%s] %s\n' "$(date +%H:%M:%S)" "$*"; }
die()  { printf '[!] %s\n' "$*" >&2; exit 1; }

# ZeroTier assigns from the network's managed range, most commonly 10.147.0.0/16; Tailscale
# uses 100.64.0.0/10; Hamachi uses 25.0.0.0/8. Match all three so the host never has to
# remember which tool is installed, and so a physical LAN address is never picked by
# accident -- advertising 192.168.x.x to a remote peer produces a match that silently hangs.
is_tunnel_address() {
  case "$1" in
    10.147.*|10.148.*|100.6[4-9].*|100.[7-9][0-9].*|100.1[01][0-9].*|100.12[0-7].*|25.*) return 0 ;;
    *) return 1 ;;
  esac
}

# identity.public is world-readable, so the node id can be shown without sudo even though
# zerotier-cli itself refuses to run as a normal user.
node_id() { cut -d: -f1 /var/lib/zerotier-one/identity.public 2>/dev/null || echo "<unavailable>"; }

joined_network_ids() { ls /var/lib/zerotier-one/networks.d/ 2>/dev/null | sed 's/\.conf$//' | sed '/^$/d'; }

ipv4_candidates() { hostname -I 2>/dev/null | tr ' ' '\n' | sed '/^$/d'; }

pick_tunnel_address() {
  local candidate
  # A joined ZeroTier member also gets an interface; prefer the address actually bound to
  # it over a same-range address on some other adapter.
  candidate="$(ip -4 -o addr show 2>/dev/null | awk '$2 !~ /^zt/ {next} {split($4,a,"/"); print a[1]}' | head -1)"
  if [[ -n "$candidate" ]]; then echo "$candidate"; return 0; fi
  while read -r candidate; do
    [[ "$candidate" == 127.* ]] && continue
    if is_tunnel_address "$candidate"; then echo "$candidate"; return 0; fi
  done < <(ipv4_candidates)
  return 1
}

# Only this project's own listeners are adopted. A stale run_local.lbl from an earlier manual
# session is the normal case, and silently killing an unrelated service would be unforgivable,
# so anything else is a hard error instead.
owners_of() {  # owners_of <port/tcp> <port/udp> ...
  local port pids="" pid cmd
  for port in "$@"; do
    for pid in $(pids_on "${port%/*}" "${port#*/}"); do
      cmd="$(tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null || true)"
      case "$cmd" in
        *run_local.lbl*|*netrelay*) pids="$pids $pid" ;;
      esac
    done
  done
  echo "$pids" | tr ' ' '\n' | sed '/^$/d' | sort -u | tr '\n' ' '
}

pids_on() {  # pids_on <port> <tcp|udp>
  local net; [[ "$2" == udp ]] && net="-u" || net="-t"
  ss "$net" -lHnp 2>/dev/null | grep -E "[:.]$1[[:space:]]" \
    | grep -oE 'pid=[0-9]+' | cut -d= -f2 | sort -u
}

port_busy() {  # port_busy <port> <tcp|udp>
  # -H drops the header so columns are stable: with -l the LOCAL address is $4 and the peer
  # is $5. Reading $5 here always matched the wildcard peer column, so no port ever looked
  # busy -- verified against a live listener on 8080 before fixing.
  local net
  [[ "$2" == udp ]] && net="-u" || net="-t"
  ss "$net" -lHn 2>/dev/null | awk '{print $4}' | grep -qE "(^|[^0-9])$1\$"
}

pick_lan_address() {
  local candidate
  while read -r candidate; do
    case "$candidate" in
      127.*|172.1[6-9].*|172.2[0-9].*|172.3[01].*) continue ;;  # loopback and docker bridges
    esac
    if is_tunnel_address "$candidate"; then continue; fi
    echo "$candidate"; return 0
  done < <(ipv4_candidates)
  return 1
}

resolve_host_address() {
  case "$ADDRESS_MODE" in
    explicit) [[ -n "$EXPLICIT_IP" ]] || die "--tunnel-ip needs an address"; echo "$EXPLICIT_IP" ;;
    lan)      pick_lan_address || die "no physical LAN IPv4 address found" ;;
    *)        pick_tunnel_address || die "no tunnel IPv4 address found. ZeroTier must be installed AND joined to a network:
  sudo zerotier-cli join <16-hex-network-id>
then tick Auth for this node in ZeroTier Central. This node id is $(node_id)." ;;
  esac
}

check_readiness() {
  local address="$1" fail=0
  command -v legible >/dev/null || { note "legible not on PATH"; fail=1; }
  [[ -x "$ROOT/tools/netrelay/netrelay" ]] || { note "relay not built: run tools/netrelay/run_tests.sh"; fail=1; }
  [[ -f "$ROOT/Server/certs/server.pem" ]] || { note "missing Server/certs/server.pem: run Server/gen_certs.sh"; fail=1; }

  note "advertised host address: $address"

  local port busy=""
  for port in "$HTTP_PORT/tcp" "$HTTPS_PORT/tcp" "$RELAY_PORT/udp"; do
    if port_busy "${port%/*}" "${port#*/}"; then busy="$busy $port"; else note "  port $port: free"; fi
  done
  # Failing loudly here beats the confusing alternative: the listener would die on
  # EADDRINUSE a moment later and report only "exited immediately".
  if [[ -n "$busy" ]]; then
    note "  ports already in use:$busy"
    ADOPT_PIDS="$(owners_of $busy)"
    mkdir -p "$RUNDIR" 2>/dev/null || true
    echo "$ADOPT_PIDS" > "$RUNDIR/adopted.pids" 2>/dev/null || true
    if [[ -n "$ADOPT_PIDS" ]]; then
      note "  held by this project's own server/relay; they will be restarted advertising $1"
    else
      for port in $busy; do
        ss -tulnp 2>/dev/null | grep -E ":${port%/*} " | head -2 | sed 's/^/    /'
      done
      die "a foreign process holds a required port; stop it or choose other ports"
    fi
  fi

  # ufw is commonly active while /etc/ufw/ufw.conf still says ENABLED=no, so the config file
  # is not a reliable answer. Only a root-readable ruleset is, so report what must be true.
  if systemctl is-active --quiet ufw 2>/dev/null; then
    note "  ufw is ACTIVE: verify these are allowed (needs root)"
    note "    sudo ufw allow $HTTP_PORT/tcp && sudo ufw allow $HTTPS_PORT/tcp && sudo ufw allow $RELAY_PORT/udp"
  fi
  return "$fail"
}

start_one() {  # start_one <name> <logfile> <cmd...>
  local name="$1" log="$2"; shift 2
  nohup "$@" >>"$log" 2>&1 &
  local pid=$!
  sleep 0.4
  [[ -d "/proc/$pid" ]] || die "$name exited immediately; see $log"
  echo "$pid" > "$RUNDIR/$name.pid"
  note "$name started (pid $pid) -> $log"
}

stop_all() {
  mkdir -p "$RUNDIR"
  # Reap listeners adopted from a previous manual session before starting our own.
  if [[ -s "$RUNDIR/adopted.pids" ]]; then
    local apid
    for apid in $(cat "$RUNDIR/adopted.pids"); do
      if [[ -d "/proc/$apid" ]]; then
        kill "$apid" 2>/dev/null || true; sleep 0.3; kill -9 "$apid" 2>/dev/null || true
        note "stopped adopted listener (pid $apid)"
      fi
    done
    rm -f "$RUNDIR/adopted.pids"
  fi
  [[ -d "$RUNDIR" ]] || { note "nothing to stop: $RUNDIR absent"; return 0; }
  local f name pid
  for f in "$RUNDIR"/*.pid; do
    [[ -e "$f" ]] || continue
    name="$(basename "$f" .pid)"; pid="$(cat "$f")"
    if [[ -d "/proc/$pid" ]]; then
      kill "$pid" 2>/dev/null || true; sleep 0.3; kill -9 "$pid" 2>/dev/null || true
      note "stopped $name (pid $pid)"
    else
      note "$name (pid $pid) was not running"
    fi
    rm -f "$f"
  done
}

ADDRESS="$(resolve_host_address)"

if [[ "$ACTION" == check ]]; then
  check_readiness "$ADDRESS" || die "not ready to host"
  note "ready"
  exit 0
fi

if [[ "$ACTION" == stop ]]; then
  stop_all
  exit 0
fi

check_readiness "$ADDRESS" || die "not ready to host"
mkdir -p "$RUNDIR"
stop_all   # a stale listener would make the new one fail with EADDRINUSE

note "advertising $SCHEME://$ADDRESS:$HTTPS_PORT and relay udp/$ADDRESS:$RELAY_PORT"
note "presence TTL ${PRESENCE_TTL_MS}ms"

start_one http  "$RUNDIR/http.log"  env TFTF_SERVER_HOST="$ADDRESS" TFTF_SERVER_SCHEME="$SCHEME" \
                     TFTF_SERVER_PORT="$HTTPS_PORT" TFTF_PRESENCE_TTL_MS="$PRESENCE_TTL_MS" \
                     HTTP_PORT="$HTTP_PORT" legible run "$ROOT/Server/run_local.lbl"
start_one https "$RUNDIR/https.log" env TFTF_SERVER_HOST="$ADDRESS" TFTF_SERVER_SCHEME="$SCHEME" \
                     TFTF_SERVER_PORT="$HTTPS_PORT" TFTF_PRESENCE_TTL_MS="$PRESENCE_TTL_MS" \
                     HTTPS_PORT="$HTTPS_PORT" legible run "$ROOT/Server/run_local.lbl" --https
start_one relay "$RUNDIR/relay.log" "$ROOT/tools/netrelay/netrelay" --port "$RELAY_PORT" --verbose

# Prove the listeners really answer on the advertised address, not just on loopback. Serving
# on 0.0.0.0 is necessary but not sufficient: a firewall or a wrong advertised address both
# look identical to the player.
note "waiting for listeners to answer on $ADDRESS"
ok=0
for _ in $(seq 1 60); do
  code="$(curl -sS --noproxy '*' -k --max-time 5 -o /dev/null -w '%{http_code}' \
          "http://$ADDRESS:$HTTP_PORT/bcg/getUserData" 2>/dev/null || echo 000)"
  [[ "$code" == 200 ]] && { ok=1; break; }
  sleep 0.5
done
[[ "$ok" == 1 ]] || die "HTTP $HTTP_PORT did not answer on $ADDRESS (see $RUNDIR/http.log)"

code="$(curl -sS --noproxy '*' -k --max-time 5 -o /dev/null -w '%{http_code}' \
        "https://$ADDRESS:$HTTPS_PORT/bcg/getUserData" 2>/dev/null || echo 000)"
[[ "$code" == 200 ]] || die "HTTPS $HTTPS_PORT did not answer on $ADDRESS (see $RUNDIR/https.log)"
note "HTTP and HTTPS both answer 200 on $ADDRESS"

# A live UDP round trip through the relay: HELLO must come back OK with our peer name.
reply="$(python3 - "$ADDRESS" "$RELAY_PORT" <<'PY' 2>/dev/null || true
import socket, sys
host, port = sys.argv[1], int(sys.argv[2])
s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.settimeout(4)
s.sendto(b'HELLO|arena_versus|host-selfcheck', (host, port))
try:
    print(s.recvfrom(512)[0].decode())
except OSError as e:
    print('NO_REPLY: %s' % e)
PY
)"
case "$reply" in
  OK\|host-selfcheck\|*) note "relay answered on udp $ADDRESS:$RELAY_PORT: $reply" ;;
  *) die "relay did not answer on udp $ADDRESS:$RELAY_PORT: ${reply:-empty} (see $RUNDIR/relay.log)" ;;
esac

cat <<EOF

Hosting on $ADDRESS. Build one hook + APK per device, changing --peer each time:
  Server/build_arena_hook.sh --relay-host $ADDRESS --peer player-a
  legible run Server/build_phone_apk.lbl "Transformers 9.2 offline.apk" \\
    build/net-a.apk --server-host $ADDRESS --scheme $SCHEME --server-port $HTTPS_PORT
  # keep that APK, then repeat both with --peer player-b

Remote peers must be able to reach: tcp/$HTTP_PORT tcp/$HTTPS_PORT udp/$RELAY_PORT on $ADDRESS.
Stop with: Server/host_netcode.sh --stop
EOF
