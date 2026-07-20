#!/usr/bin/env bash
# Provision a non-rooted Android phone over USB for the phone-specific APK.
set -euo pipefail

ADB="${ADB:-/home/darabat/Android/Sdk/platform-tools/adb}"
D="${D:-}"
PKG="${PKG:-com.kabam.bigrobot}"
ACTIVITY="${ACTIVITY:-com.explodingbarrel.Activity}"
HERE="$(cd "$(dirname "$0")" && pwd)"
APK="${APK:-$HERE/../build/Transformers-9.2-offline-phone.apk}"
ALLOW_UNVERIFIED_ADB="${ALLOW_UNVERIFIED_ADB:-0}"
UPDATE_APK="${UPDATE_APK:-0}"

if [[ -n "$D" ]]; then
  ADB_TARGET=("$ADB" -s "$D")
else
  mapfile -t PHONES < <("$ADB" devices | awk 'NR > 1 && $2 == "device" && $1 !~ /^emulator-/ {print $1}')
  if [[ ${#PHONES[@]} -ne 1 ]]; then
    echo "Expected exactly one authorized USB phone; found ${#PHONES[@]}." >&2
    echo "Set D=<serial> if more than one physical device is connected." >&2
    "$ADB" devices -l >&2
    exit 1
  fi
  D="${PHONES[0]}"
  ADB_TARGET=("$ADB" -s "$D")
fi

[[ -f "$APK" ]] || { echo "Missing APK: $APK" >&2; exit 1; }

echo "[*] device: $D ($("${ADB_TARGET[@]}" shell getprop ro.product.model | tr -d '\r'))"
if "${ADB_TARGET[@]}" shell pm path "$PKG" | tr -d '\r' | grep -q '^package:' && [[ "$UPDATE_APK" != 1 ]]; then
  echo "[*] $PKG is already installed; keeping its data (set UPDATE_APK=1 to install a rebuilt APK)"
elif [[ "$ALLOW_UNVERIFIED_ADB" == 1 ]]; then
  # Some Play Protect versions reject this known local modification. The opt-in
  # changes verification only for this install and restores both values even if
  # Package Manager fails or the script is interrupted.
  DEVICE_APK="/data/local/tmp/tftf-phone.apk"
  OLD_VERIFY_ADB="$("${ADB_TARGET[@]}" shell settings get global verifier_verify_adb_installs | tr -d '\r')"
  OLD_VERIFY_ALL="$("${ADB_TARGET[@]}" shell settings get global package_verifier_enable | tr -d '\r')"
  restore_verifier() {
    if [[ "$OLD_VERIFY_ADB" == null ]]; then
      "${ADB_TARGET[@]}" shell settings delete global verifier_verify_adb_installs >/dev/null
    else
      "${ADB_TARGET[@]}" shell settings put global verifier_verify_adb_installs "$OLD_VERIFY_ADB"
    fi
    if [[ "$OLD_VERIFY_ALL" == null ]]; then
      "${ADB_TARGET[@]}" shell settings delete global package_verifier_enable >/dev/null
    else
      "${ADB_TARGET[@]}" shell settings put global package_verifier_enable "$OLD_VERIFY_ALL"
    fi
    "${ADB_TARGET[@]}" shell rm -f "$DEVICE_APK"
  }
  trap restore_verifier EXIT
  echo "[*] copy APK for non-incremental Package Manager install"
  "${ADB_TARGET[@]}" push "$APK" "$DEVICE_APK"
  "${ADB_TARGET[@]}" shell settings put global verifier_verify_adb_installs 0
  "${ADB_TARGET[@]}" shell settings put global package_verifier_enable 0
  "${ADB_TARGET[@]}" shell pm install -r -d "$DEVICE_APK"
  restore_verifier
  trap - EXIT
else
  echo "[*] install phone APK"
  # Large incremental installs can report success and then disappear when the
  # backing mount is torn down, so always request a conventional install.
  if ! "${ADB_TARGET[@]}" install --no-incremental -r -d "$APK"; then
    cat >&2 <<EOF
Install failed. If INSTALL_FAILED_VERIFICATION_FAILURE appeared, review this APK
locally and rerun once with ALLOW_UNVERIFIED_ADB=1. The script restores Android's
verifier settings immediately. If INSTALL_FAILED_UPDATE_INCOMPATIBLE appeared, a
differently signed build is installed; this script will not erase its data.
EOF
    exit 1
  fi
fi

echo "[*] reverse phone ports :8443/:8080 to matching laptop ports"
"${ADB_TARGET[@]}" reverse tcp:8443 tcp:8443
"${ADB_TARGET[@]}" reverse tcp:8080 tcp:8080
"${ADB_TARGET[@]}" reverse --list

echo "[*] launch game"
"${ADB_TARGET[@]}" shell am force-stop "$PKG"
"${ADB_TARGET[@]}" shell am start -n "$PKG/$ACTIVITY" >/dev/null

cat <<'EOF'
[+] Phone provisioned and game launched.
Keep Server/run_local.py running and keep USB debugging connected while playing.
EOF
