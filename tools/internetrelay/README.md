# TFTF Internet relay

`internetrelay` is the public rendezvous point for the Android PvP companion
app. Both phones make outbound TLS connections, so neither phone needs an
inbound firewall rule or a port-forwarded game listener.

The relay is deliberately not a general proxy:

- the first connection for a session must be the host;
- the invitation is an expiring, high-entropy bearer credential;
- only one host and one join participant may use a session;
- HTTP and combat connections are paired only with the same session and channel;
- combat frames are capped at 512 bytes and queued at 64 frames per direction;
- no client-supplied IP address, port, room, or peer name is interpreted by the relay.

Run it with a certificate chain whose DNS name matches the value configured in
the app:

```sh
python3 -m tools.internetrelay --host 0.0.0.0 --port 4433 \
  --cert /etc/tftf-relay/fullchain.pem --key /etc/tftf-relay/privkey.pem
```

TLS 1.2 or newer is required. The Android client uses the platform trust store
and hostname verification. A firewall needs to permit the chosen TCP port.
There are no committed keys or machine-specific relay values in this project.

The HTTP channel carries the existing host API byte-for-byte. The combat
channel uses `u32be length || datagram`, with one frame per UDP packet. The join
companion exposes loopback `127.0.0.1:8080` and `127.0.0.1:8777` to its game;
the host companion connects relay channels to the host's existing local
listeners. The app integration owns invite presentation, readiness, and
shutdown lifecycle.
