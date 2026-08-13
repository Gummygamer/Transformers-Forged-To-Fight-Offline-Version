#!/usr/bin/env python3
"""
TFTF local Sparx server (probe + build phase).
Listens HTTPS:443 (+HTTP:80), logs every request fully, and returns either a
canned response from server/responses/ or a default 200 {} so the client keeps
walking its request sequence. This is both the probe harness and the seed of the
real fake server.
"""
import http.server, socketserver, ssl, os, sys, json, threading, datetime
import urllib.parse

try:
    import gamedata  # authored roster + combat curve (single source of truth)
except Exception as _e:  # keep the server usable even if the data module is absent
    gamedata = None
    print(f"[!] gamedata module unavailable ({_e}); using built-in fallbacks", flush=True)

HERE = os.path.dirname(os.path.abspath(__file__))
PEM = os.path.join(HERE, "certs", "server.pem")
RESP_DIR = os.path.join(HERE, "responses")
LOG_DIR = os.path.join(HERE, "logs")
os.makedirs(RESP_DIR, exist_ok=True)
os.makedirs(LOG_DIR, exist_ok=True)
LOGFILE = os.path.join(LOG_DIR, "requests.log")
_lock = threading.Lock()

# Minimal server-authoritative quest state. quest-movedir only sends a direction,
# not the player's absolute position, so remembering the last accepted tile is
# required for a second move to advance beyond the first node. A fresh quest-begin
# resets the corresponding mission to its authored start tile.
_quest_positions = {}
_quest_state_lock = threading.Lock()
_saved_team = None


def get_saved_team():
    """Return a copy of the session squad so tests cannot mutate server state in place."""
    with _quest_state_lock:
        return list(_saved_team) if _saved_team is not None else None


def reset_saved_team():
    """Clear the session squad, restoring gamedata's DEFAULT_TEAM fallback."""
    global _saved_team
    with _quest_state_lock:
        _saved_team = None

# Tutorials whose interactive "prompt" branch infinite-loops the main thread offline.
# We return an error envelope for these so the flow aborts gracefully instead of freezing.
BLOCK_TUTORIALS = {"ShieldTutorial"}

# Tutorials that must actually RUN (not be auto-completed): the FTE is the scripted
# intro fight (Optimus vs Starscream) — the client drives it locally and reports
# progress via start-tutorial/start-branch. Answering "completed" would make the
# client skip the fight, so for these we echo the state the client is asking for
# ("started", with the requested branch current). complete-tutorial still completes.
LIVE_TUTORIALS = {"FTE"}

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

        # getUserData is normally canned for the payload exporter and in-APK server, but the
        # live host server can fold this response into the current session after a re-login.
        # The saved-team response's activeTeams update was verified to drive the board marker
        # and prefight selector, so preserve the same squad when that refresh occurs.
        if p.endswith("/bcg/getUserData") and gamedata is not None:
            return json.dumps(
                {"error": None, "result": gamedata.build_user_data(team=get_saved_team())},
                separators=(",", ":"),
            ).encode()

        # TuningGameplay subscribes after the account-data config event, so repeat the
        # missions config through the periodic grouped autorefresh path. The response
        # shape is AutoRefreshingUpdate's exact Dot field contract; unrelated groups
        # are omitted and retain the fake server's existing fallback behavior.
        if p.endswith("/autorefresh/grouprefresh") and gamedata is not None:
            query = urllib.parse.parse_qs(
                urllib.parse.urlsplit(path).query, keep_blank_values=True
            )
            group_names = {
                value
                for key, values in query.items()
                if key.startswith("groups.") and key.endswith(".name")
                for value in values
            }
            updates = []
            if "missionsconfig" in group_names:
                updates.append(gamedata.build_missions_autorefresh_update())
            return json.dumps(
                {"error": None, "result": {"updates": updates}},
                separators=(",", ":"),
            ).encode()

        # base/active: the home screen's base board. BaseManager parses this into the
        # user's `Base` (an ActiveMission), which HomeFlow turns into the ActiveQuest
        # that builds the "Baseboard" scene. With the previous default {} reply the
        # base stayed null and the home screen rendered an empty black board.
        # See gamedata.build_base_active for the wire shape.
        if p.endswith("/base/active") and gamedata is not None:
            return json.dumps(
                {"error": None, "result": gamedata.build_base_active()},
                separators=(",", ":"),
            ).encode()

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
            # LIVE tutorials (the FTE intro fight): echo "started" so the client-side
            # flow actually runs instead of being skipped as already-done. Only
            # complete-tutorial marks them completed.
            if tid in LIVE_TUTORIALS and not p.endswith("/tutorial/complete-tutorial"):
                branches = {bid: {"s": "started"}} if bid else {}
                entry = {"current_bid": bid, "s": "started", "branches": branches}
                return json.dumps({"error": None, "result": {tid: entry}}).encode()
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
            if gamedata is not None:
                # Draw stats from the authored roster curve so hero details match the
                # roster/getUserData numbers instead of a disconnected ad-hoc curve.
                out = gamedata.build_base_hero_details(heroes)
            else:
                out = []
                for h in heroes:
                    rank = int(h.get("rank", 1) or 1)
                    level = int(h.get("level", 1) or 1)
                    # fallback crude monotonic curve (gamedata module missing)
                    hp = 3000 + rank * 4000 + level * 600
                    atk = 300 + rank * 400 + level * 60
                    out.append({
                        "bid": h.get("bid", ""), "rank": rank, "level": level,
                        "sig_lvl": int(h.get("sig_lvl", 0) or 0),
                        "rating_hp": hp, "max_hp": hp,
                        "rating_attack": atk, "attack": atk,
                        "health": hp, "armor": 0, "crit_rate": 0, "crit_dmg": 0,
                        "block_prof": 0, "perfect_block": 0, "sig_ability": 0,
                        # These original revival fallback values prevent zero/missing mana_gain from locking the SP meter.
                        "special_attacks": 3, "mana_gain": 1.0, "mana_start": 0,
                        "user_owned": True,
                        "synergyBonuses": [], "pvpb": {},
                    })
            return json.dumps({"error": None, "result": out}).encode()

        # quests/quest-detail/<missionId>: the pre-battle TeamSelect screen posts this
        # (body {"hash","setId"}) to fetch the detailed mission Summary + battle map. The
        # response builds the model's _activeQuest / QuestSummary (get_QuestSummary), without
        # which START stays disabled. QuestDB.AddQuestDetails reads result["data"] as the
        # summary. See gamedata.build_quest_detail.
        if "/quests/quest-detail/" in p:
            mission_id = p.rsplit("/", 1)[-1]
            try:
                req = json.loads(btxt) if btxt else {}
            except Exception:
                req = {}
            set_id = req.get("setId", "story_act1")
            if gamedata is not None:
                result = gamedata.build_quest_detail(mission_id, set_id)
            else:
                result = {"data": {"id": mission_id, "setId": set_id}, "progression": {}}
            return json.dumps({"error": None, "result": result}).encode()

        # quests/quest-begin/<qid>: START posts the team + setId; the client expects
        # result["activeQuests"] (the now-active quest, with its battle map). Without it the
        # begin flow throws "unknown error". See gamedata.build_quest_begin.
        if "/quests/quest-begin/" in p:
            global _saved_team
            qid = p.rsplit("/", 1)[-1]
            try:
                req = json.loads(btxt) if btxt else {}
            except Exception:
                req = {}
            set_id = req.get("setId", "story_act1")
            posted_team = [req[k] for k in ("tm0", "tm1", "tm2") if req.get(k)]
            with _quest_state_lock:
                if posted_team:
                    _saved_team = list(posted_team)
                team = list(_saved_team) if _saved_team else []
                _quest_positions[qid] = (0, 1)
            if gamedata is not None:
                result = gamedata.build_quest_begin(qid, set_id, team)
            else:
                result = {"activeQuests": []}
            return json.dumps({"error": None, "result": result}).encode()

        # quests/quest-movedir/<qid>-<teamId>/<offX>/<offY>: tapping a reachable board node POSTs
        # this. The response must carry results/progression/teamData so the client's
        # ProcessActionResultsAndProgression -> AddActionResultsAndUpdateProgression runs and the
        # player animates one tile. See gamedata.build_quest_movedir.
        if "/quests/quest-movedir/" in p:
            parts = p.rsplit("/", 3)   # [..., "<qid>-<teamId>", "<offX>", "<offY>"]
            team_seg, offx, offy = parts[-3], parts[-2], parts[-1]
            qid = team_seg.rsplit("-", 1)[0]   # "1.1.1-0" -> "1.1.1"
            try:
                offx_i, offy_i = int(offx), int(offy)
            except Exception:
                offx_i, offy_i = 1, 0
            with _quest_state_lock:
                start = _quest_positions.get(qid, (0, 1))
                team = list(_saved_team) if _saved_team else []
                candidate = (start[0] + offx_i, start[1] + offy_i)
                # The authored 1.1.1 map is a vertical path in map coordinates.
                # Keep this acceptance gate on the same dimensions as build_quest_map:
                # the wire walk test's second /1/0 move reached (2,1), so a separate
                # literal here would otherwise be able to clamp a valid authored node.
                quest_dim = gamedata.QUEST_DIM if gamedata is not None else 3
                path_col = gamedata.QUEST_PATH_COL if gamedata is not None else 1
                if 0 <= candidate[0] < quest_dim and candidate[1] == path_col:
                    _quest_positions[qid] = candidate
                else:
                    offx_i = offy_i = 0
            if gamedata is not None:
                result = gamedata.build_quest_movedir(
                    qid, offx_i, offy_i, start=start, team=team
                )
            else:
                result = {}
            return json.dumps({"error": None, "result": result}).encode()

        # bcg/setSavedTeam: echo a BCGUserData-shaped update so the saved team is reflected
        # into BCGManager.savedTeams (the pre-battle screen reads it back via GetSavedTeam).
        if p.endswith("/bcg/setSavedTeam"):
            try:
                req = json.loads(btxt) if btxt else {}
            except Exception:
                req = {}
            team_id = str(req.get("teamID", "0"))
            heroes = req.get("heroes") or []
            if not isinstance(heroes, list):
                heroes = []
            with _quest_state_lock:
                _saved_team = list(heroes)
            if gamedata is not None:
                team = gamedata.build_saved_team(team_id, heroes)
                # The story mission is the only qid this host handler currently starts; mirror
                # its existing literal so the active-team fold key is "<qid>-<teamID>".
                active_team = gamedata.build_active_team("1.1.1-%s" % team_id, heroes=heroes)
            else:
                team = {"TeamID": team_id, "teamID": team_id, "id": team_id,
                        "TeamHeroes": list(heroes), "heroes": list(heroes)}
                active_team = {"aid": "1.1.1-%s" % team_id, "type": "PvE",
                               "modes": ["PvE"],
                               "heroes": {bid: {"bid": bid} for bid in heroes[:2]},
                               "expire": 0}
            result = {"updates": {"savedTeams": [team], "activeTeams": [active_team]},
                      "deletes": {}}
            return json.dumps({"error": None, "result": result}).encode()
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
