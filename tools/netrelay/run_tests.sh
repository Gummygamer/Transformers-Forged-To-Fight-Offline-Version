#!/usr/bin/env bash
# Build and run every realtime-netcode test.
#
# Three suites cover the relay end to end:
#   test_netrelay.py  -- the relay alone, driven by raw UDP sockets
#   test_netclient    -- the transport client (tools/nativehook/netclient.c) against a real
#                        relay process, which is the path a device actually takes
#   test_arena        -- the fight bridge (tools/nativehook/arena.c) against the same relay,
#                        with fake il2cpp controllers so the logic runs on a desktop
#
# All three are black-box: they assert on wire behaviour and on recorded fake calls, never
# on the C source. test_arena is the closest thing to a real fight that can run off-device:
# arena.c reaches into il2cpp only through ArenaOps, so substituting that one struct lets the
# genuine capture, relay, apply and AI-pause decisions be exercised here.
set -u

cd "$(dirname "$0")" || exit 1

CC=${CC:-gcc}
CFLAGS=${CFLAGS:--O2 -Wall -Wextra}
FAIL=0

echo "=== build relay ==="
$CC $CFLAGS -o netrelay netrelay.c || { echo "[!] netrelay build failed"; exit 1; }

echo "=== build client tests ==="
$CC $CFLAGS -o test_netclient test_netclient.c ../nativehook/netclient.c -lpthread \
  || { echo "[!] test_netclient build failed"; exit 1; }

echo "=== build arena bridge tests ==="
$CC $CFLAGS -o test_arena test_arena.c ../nativehook/arena.c ../nativehook/netclient.c -lpthread \
  || { echo "[!] test_arena build failed"; exit 1; }

# Second arena binary with a session baked in via -D. An unrooted phone cannot have a config
# file written into its app-private directory, and /data/local/tmp is not readable by an
# untrusted_app under SELinux, so the phone APK build passes these flags instead. The suite
# asserts whichever side the binary was built for, so both branches are covered by two builds.
echo "=== build arena bridge tests with a compile-time session ==="
# The values are string literals, so each needs nested quotes: without them the preprocessor
# substitutes a bare 127.0.0.1 into snprintf's format argument and the build fails.
$CC $CFLAGS \
  -DTFTF_ARENA_DEFAULT_HOST='"127.0.0.1"' \
  -DTFTF_ARENA_DEFAULT_ROOM='"arena_versus"' \
  -DTFTF_ARENA_DEFAULT_PEER='"alice"' \
  -o test_arena_defaults test_arena.c ../nativehook/arena.c ../nativehook/netclient.c -lpthread \
  || { echo "[!] test_arena_defaults build failed"; exit 1; }

NDK_ROOT=${ANDROID_NDK_HOME:-$HOME/Android/Sdk/ndk}
NDK_BIN=$(ls -d "$NDK_ROOT"/*/toolchains/llvm/prebuilt/*/bin 2>/dev/null | head -1)
if [ -n "$NDK_BIN" ]; then
  echo "=== cross-compile the netcode for both ABIs ($NDK_BIN) ==="
  CC64="$NDK_BIN/aarch64-linux-android28-clang"
  CC32="$NDK_BIN/armv7a-linux-androideabi28-clang"

  # arm64: the whole netcode, transport and fight bridge.
  if [ -x "$CC64" ]; then
    "$CC64" $CFLAGS -fPIC -c ../nativehook/netclient.c -o /tmp/netclient_arm64.o \
      && "$CC64" $CFLAGS -fPIC -c ../nativehook/arena.c -o /tmp/arena_arm64.o \
      && echo "[ok] arm64 netcode compiles clean" \
      || { echo "[!] arm64 netcode failed to compile"; FAIL=1; }
    "$CC64" -shared -O2 -fPIC -Wl,-soname,libdothook.so \
      -DTFTF_ENABLE_ARENA=1 \
      -DTFTF_ARENA_DEFAULT_HOST='"192.0.2.10"' \
      -DTFTF_ARENA_DEFAULT_PORT=8777 \
      -DTFTF_ARENA_DEFAULT_ROOM='"arena_versus"' \
      -DTFTF_ARENA_DEFAULT_PEER='"emulator-5554"' \
      -o /tmp/libdothook_arena_check.so ../nativehook/hook.c ../nativehook/inapk_server.c \
      ../nativehook/arena.c ../nativehook/netclient.c -llog \
      && echo "[ok] arm64 Arena-enabled hook links clean" \
      || { echo "[!] arm64 Arena-enabled hook failed to link"; FAIL=1; }
  else
    echo "[!] $CC64 missing"; FAIL=1
  fi

  # armeabi-v7a: netclient.c is ABI-neutral transport and must still compile. arena.c must
  # NOT -- its PlayerController/AIController offsets are arm64 values, and on armv7 a managed
  # header is 8 bytes instead of 16 with 4-byte reference fields, so every offset shifts.
  # arena.h #errors rather than let a v7a build write to the wrong field of a live controller,
  # and this asserts the guard still bites (see hook_arm32.c PORT STATUS item 6).
  if [ -x "$CC32" ]; then
    "$CC32" $CFLAGS -fPIC -c ../nativehook/netclient.c -o /tmp/netclient_armv7.o \
      && echo "[ok] armeabi-v7a transport compiles clean" \
      || { echo "[!] armeabi-v7a transport failed to compile"; FAIL=1; }
    if "$CC32" $CFLAGS -fPIC -c ../nativehook/arena.c -o /tmp/arena_armv7.o 2>/dev/null; then
      echo "[!] arena.c compiled for armeabi-v7a: its arm64-offset guard is no longer firing"
      FAIL=1
    else
      echo "[ok] arena.c is refused on armeabi-v7a, as intended"
    fi
    "$CC32" -shared -O2 -fPIC -Wl,-soname,libdothook.so -o /tmp/libdothook_v7a_check.so \
      ../nativehook/hook_arm32.c ../nativehook/inapk_server.c -llog \
      && echo "[ok] armeabi-v7a hook library links without the netcode" \
      || { echo "[!] armeabi-v7a hook library failed to link"; FAIL=1; }
  else
    echo "[!] $CC32 missing"; FAIL=1
  fi
else
  echo "[!] no NDK found; skipped the per-ABI compile check"
fi

echo
echo "=== relay suite ==="
python3 test_netrelay.py || FAIL=1

echo
echo "=== client suite ==="
./test_netclient || FAIL=1

echo
echo "=== arena bridge suite (no compile-time session) ==="
./test_arena || FAIL=1

echo
echo "=== arena bridge suite (compile-time session) ==="
./test_arena_defaults || FAIL=1

echo
if [ "$FAIL" -ne 0 ]; then
  echo "netcode tests FAILED"
  exit 1
fi
echo "all netcode tests passed"
