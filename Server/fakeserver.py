#!/usr/bin/env python3
"""
TFTF local Sparx server (probe + build phase).
Listens HTTPS:443 (+HTTP:80), logs every request fully, and returns either a
canned response from server/responses/ or a default 200 {} so the client keeps
walking its request sequence. This is both the probe harness and the seed of the
real fake server.
"""
import http.server, socketserver, ssl, os, sys, json, threading, datetime

HERE = os.path.dirname(os.path.abspath(__file__))
PEM = os.path.join(HERE, "certs", "server.pem")
RESP_DIR = os.path.join(HERE, "responses")
LOG_DIR = os.path.join(HERE, "logs")
os.makedirs(RESP_DIR, exist_ok=True)
os.makedirs(LOG_DIR, exist_ok=True)
LOGFILE = os.path.join(LOG_DIR, "requests.log")
_lock = threading.Lock()

# Tutorials whose interactive "prompt" branch infinite-loops the main thread offline.
# We return an error envelope for these so the flow aborts gracefully instead of freezing.
BLOCK_TUTORIALS = {"ShieldTutorial"}

def ts():
    return datetime.datetime.now().strftime("%H:%M:%S.%f")[:-3]

def log(line):
    with _lock:
        print(line, flush=True)
        with open(LOGFILE, "a", encoding="utf-8", errors="replace") as f:
            f.write(line + "\n")

def resp_path_for(method, path):
    # map "/auth/login" -> responses/GET__auth_login.json
    key = method + "__" + path.strip("/").replace("/", "_").split("?")[0]
    if not key.endswith(".json"):
        key += ".json"
    return os.path.join(RESP_DIR, key)

class H(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def _dynamic(self, path, btxt):
        """Synthesize responses that must reflect the request body.
        Tutorial mutation endpoints (start-tutorial / start-branch /
        early-start-branch / complete-tutorial) return the tutorial keyed by
        the request's `tid`; their client callback does UpdateTutorialData(result)
        then _userData.get_Item(tid), which KeyNotFounds unless result[tid] exists."""
        p = path.split("?")[0]
        tut_eps = ("/tutorial/start-tutorial", "/tutorial/start-branch",
                   "/tutorial/early-start-branch", "/tutorial/complete-tutorial")
        if any(p.endswith(e) for e in tut_eps):
            try:
                req = json.loads(btxt) if btxt else {}
            except Exception:
                req = {}
            tid = req.get("tid") or req.get("tutorialId") or req.get("id")
            if not tid:
                return None
            # Some tutorials drive an interactive "prompt" branch (e.g. ShieldTutorial ->
            # Shields/ShieldPromptState) that, when reported as the CURRENT branch,
            # activates a shield-prompt flow which INFINITE-LOOPS on the main thread
            # (re-parsing Response envelopes) and freezes the game offline. For these,
            # return success but with NO active branch (current_bid="" / empty branches)
            # so the looping prompt state never activates. (Erroring instead triggers a
            # "LOST CONNECTION" session-error modal, so we keep it a clean success.)
            if tid in BLOCK_TUTORIALS:
                # Erroring the request makes the client's b__0 take the error path and
                # NEVER activate the looping ShieldPromptState. (A success of any branch
                # state still activates it client-side and re-freezes.) Mark non-retry/
                # non-fatal to avoid the "LOST CONNECTION" retry-exhaustion escalation.
                return json.dumps({"err": "unavailable", "error": "unavailable",
                                   "retry": False, "fatal": False, "result": {}}).encode()
            bid = req.get("bid") or req.get("branchId") or ""
            # Report every tutorial step as completed so the game skips tutorial UI
            # (which needs live server-synced state) and advances to the home screen.
            branches = {bid: {"s": "completed"}} if bid else {}
            entry = {"current_bid": bid, "s": "completed", "branches": branches}
            return json.dumps({"error": None, "result": {tid: entry}}).encode()

        # bcg/getBaseHeroData: the client posts a `heroes` list (each {bid,rank,level,
        # sig_lvl,...}) and expects an ARRAY of computed BCGHeroDetails back. The
        # callback NREs if result isn't a non-null ArrayList. Echo each hero with
        # plausible base stats so the roster/hero screens can display them offline.
        if p.endswith("/bcg/getBaseHeroData"):
            try:
                req = json.loads(btxt) if btxt else {}
            except Exception:
                req = {}
            heroes = req.get("heroes") or []
            out = []
            for h in heroes:
                rank = int(h.get("rank", 1) or 1)
                level = int(h.get("level", 1) or 1)
                # crude monotonic stat curve so higher rank/level => higher stats
                hp = 3000 + rank * 4000 + level * 600
                atk = 300 + rank * 400 + level * 60
                out.append({
                    "bid": h.get("bid", ""), "rank": rank, "level": level,
                    "sig_lvl": int(h.get("sig_lvl", 0) or 0),
                    "rating_hp": hp, "max_hp": hp,
                    "rating_attack": atk, "attack": atk,
                    "health": hp, "armor": 0, "crit_rate": 0, "crit_dmg": 0,
                    "block_prof": 0, "perfect_block": 0, "sig_ability": 0,
                    "special_attacks": 0, "user_owned": True,
                    "synergyBonuses": [], "pvpb": {},
                })
            return json.dumps({"error": None, "result": out}).encode()
        return None

    def _handle(self):
        length = int(self.headers.get("Content-Length", 0) or 0)
        body = self.rfile.read(length) if length else b""
        host = self.headers.get("Host", "?")
        try:
            btxt = body.decode("utf-8")
        except Exception:
            btxt = body.hex()
        hdrs = "; ".join(f"{k}={v}" for k, v in self.headers.items())
        log(f"[{ts()}] {self.command} https://{host}{self.path}")
        log(f"    HDRS: {hdrs}")
        if body:
            log(f"    BODY({length}): {btxt[:4000]}")

        # 0) dynamic synthesis (tutorial endpoints echo the requested tid so
        #    UpdateTutorialData populates _userData[tid] and the b__0
        #    get_Item(tid) lookup succeeds for ANY tutorial, not just a canned one)
        data = self._dynamic(self.path, btxt)
        if data is not None:
            log(f"    -> dynamic ({len(data)}B)")
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            try: self.wfile.write(data)
            except Exception as e: log(f"    !! write failed: {e}")
            return

        # 1) exact canned response  2) prefix rule  3) default envelope
        rp = resp_path_for(self.command, self.path)
        data = None
        if os.path.exists(rp):
            with open(rp, "rb") as f:
                data = f.read()
            log(f"    -> canned {os.path.basename(rp)} ({len(data)}B)")
        else:
            # prefix rules: responses/_prefix_rules.json = [["/autorefresh/","_autorefresh.json"], ...]
            rules_f = os.path.join(RESP_DIR, "_prefix_rules.json")
            if os.path.exists(rules_f):
                try:
                    rules = json.load(open(rules_f))
                except Exception:
                    rules = []
                p = self.path.split("?")[0]
                for prefix, fname in rules:
                    if p.startswith(prefix):
                        fp = os.path.join(RESP_DIR, fname)
                        if os.path.exists(fp):
                            data = open(fp, "rb").read()
                            log(f"    -> rule {prefix} -> {fname} ({len(data)}B)")
                            break
            if data is None:
                data = b'{"error":null,"result":{}}'
                log(f"    -> default 200 envelope")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        try:
            self.wfile.write(data)
        except Exception as e:
            log(f"    !! write failed: {e}")

    do_GET = do_POST = do_PUT = do_DELETE = do_PATCH = _handle

    def do_HEAD(self):
        self.send_response(200); self.send_header("Content-Length","0"); self.end_headers()

    def log_message(self, *a):
        pass

class TS(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True

class TLSServer(TS):
    """HTTPS server that wraps per-connection and LOGS every TCP accept + TLS
    handshake outcome (incl. SNI), so we can see connections that fail the
    handshake (cert distrust / pinning) instead of silently dropping them."""
    def __init__(self, addr, handler, ctx):
        self.ctx = ctx
        super().__init__(addr, handler)
    def get_request(self):
        conn, addr = self.socket.accept()
        sni = {"name": None}
        def grab_sni(sock, server_name, sslctx):
            sni["name"] = server_name
        try:
            ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            ctx.load_cert_chain(PEM)
            ctx.sni_callback = grab_sni
            tls = ctx.wrap_socket(conn, server_side=True)
            log(f"[{ts()}] TCP {addr[0]}:{addr[1]} TLS-OK sni={sni['name']}")
            return tls, addr
        except Exception as e:
            log(f"[{ts()}] TCP {addr[0]}:{addr[1]} TLS-FAIL sni={sni['name']} :: {type(e).__name__}: {e}")
            try: conn.close()
            except Exception: pass
            raise OSError("tls handshake failed")

def serve_http(port):
    srv = TS(("0.0.0.0", port), H)
    log(f"[*] HTTP  listening on :{port}")
    srv.serve_forever()

def serve_https(port):
    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ctx.load_cert_chain(PEM)
    srv = TLSServer(("0.0.0.0", port), H, ctx)
    log(f"[*] HTTPS listening on :{port} (per-conn logging)")
    srv.serve_forever()

if __name__ == "__main__":
    open(LOGFILE, "w").close()
    threading.Thread(target=serve_http, args=(80,), daemon=True).start()
    serve_https(443)
