"""Wire format shared by the Internet relay and its clients.

The first line on every TLS connection is a bounded JSON hello.  HTTP channels
then carry raw HTTP/TCP bytes.  Combat channels carry a four-byte network-order
length followed by one UDP datagram.  The relay never interprets game payloads.
"""

from __future__ import annotations

from dataclasses import dataclass
import json
import re
import secrets

MAX_HELLO = 4096
MAX_FRAME = 512
MAX_COMBAT_PACKET = 512
SESSION_BYTES = 16
TOKEN_BYTES = 32
ROLES = frozenset(("host", "join"))
CHANNELS = frozenset(("http", "combat"))
_ID = re.compile(r"^[A-Za-z0-9_-]{8,128}$")


class ProtocolError(ValueError):
    """The peer sent an invalid or unsafe protocol value."""


@dataclass(frozen=True)
class Hello:
    session: str
    role: str
    token: str
    channel: str
    invite: str = ""


def new_session_id() -> str:
    return secrets.token_urlsafe(SESSION_BYTES)


def new_invitation_token() -> str:
    return secrets.token_urlsafe(TOKEN_BYTES)


def encode_hello(hello: Hello) -> bytes:
    validate_hello(hello)
    raw = json.dumps(
        {
            "version": 1,
            "session": hello.session,
            "role": hello.role,
            "token": hello.token,
            "channel": hello.channel,
            "invite": hello.invite,
        },
        separators=(",", ":"),
    ).encode("ascii") + b"\n"
    if len(raw) > MAX_HELLO:
        raise ProtocolError("hello too large")
    return raw


def decode_hello(raw: bytes) -> Hello:
    if len(raw) == 0 or len(raw) > MAX_HELLO or not raw.endswith(b"\n"):
        raise ProtocolError("invalid hello length")
    try:
        value = json.loads(raw[:-1].decode("ascii"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ProtocolError("invalid hello") from exc
    if not isinstance(value, dict) or value.get("version") != 1:
        raise ProtocolError("unsupported hello version")
    fields = (
        value.get("session"), value.get("role"), value.get("token"), value.get("channel"),
        value.get("invite", ""),
    )
    if not all(isinstance(item, str) for item in fields):
        raise ProtocolError("hello fields must be strings")
    hello = Hello(*fields)
    validate_hello(hello)
    return hello


def validate_hello(hello: Hello) -> None:
    if hello.role not in ROLES:
        raise ProtocolError("invalid role")
    if hello.channel not in CHANNELS:
        raise ProtocolError("invalid channel")
    if not _ID.fullmatch(hello.session) or not _ID.fullmatch(hello.token):
        raise ProtocolError("invalid session credentials")
    if hello.role == "host" and not _ID.fullmatch(hello.invite):
        raise ProtocolError("host invitation is missing")
    if hello.role == "join" and hello.invite:
        raise ProtocolError("join cannot provide host authority")


def encode_frame(payload: bytes) -> bytes:
    if not 1 <= len(payload) <= MAX_FRAME:
        raise ProtocolError("combat frame outside permitted size")
    return len(payload).to_bytes(4, "big") + payload


def decode_frame_header(raw: bytes) -> int:
    if len(raw) != 4:
        raise ProtocolError("truncated combat frame header")
    size = int.from_bytes(raw, "big")
    if not 1 <= size <= MAX_FRAME:
        raise ProtocolError("combat frame outside permitted size")
    return size
