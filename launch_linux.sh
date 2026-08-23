#!/usr/bin/env bash
# Linux launcher for the APK patcher GUI.
#
# Installs the `legible` interpreter (and the build tools it needs) if it is
# not already on PATH, then runs `legible run tools/apk_patcher_gui/server.lbl`
# from the repository root. Re-running this script after the first successful
# run is fast: it finds `legible` already installed and skips straight to
# launching the GUI.
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)"
cd "$SCRIPT_DIR"

LEGIBLE_REPO="https://github.com/darabat/legible"
LEGIBLE_SRC="${XDG_CACHE_HOME:-$HOME/.cache}/legible-lang/src"

log() { printf '==> %s\n' "$1"; }

ensure_cargo_on_path() {
  if ! command -v cargo >/dev/null 2>&1 && [ -f "$HOME/.cargo/env" ]; then
    # shellcheck source=/dev/null
    source "$HOME/.cargo/env"
  fi
}

install_apt_package() {
  local pkg="$1"
  if command -v apt-get >/dev/null 2>&1; then
    log "Installing $pkg via apt-get (needs sudo)..."
    sudo apt-get update -y
    sudo apt-get install -y "$pkg"
  else
    echo "Could not find apt-get to install '$pkg' automatically." >&2
    echo "Install '$pkg' with your distro's package manager and re-run this script." >&2
    exit 1
  fi
}

ensure_git() {
  command -v git >/dev/null 2>&1 || install_apt_package git
}

ensure_c_compiler() {
  command -v cc >/dev/null 2>&1 || command -v gcc >/dev/null 2>&1 || install_apt_package build-essential
}

ensure_rust() {
  ensure_cargo_on_path
  if command -v cargo >/dev/null 2>&1; then
    return
  fi
  log "Rust not found; installing via rustup..."
  if command -v curl >/dev/null 2>&1; then
    curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y --default-toolchain stable
  elif command -v wget >/dev/null 2>&1; then
    wget -qO- https://sh.rustup.rs | sh -s -- -y --default-toolchain stable
  else
    install_apt_package curl
    curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y --default-toolchain stable
  fi
  ensure_cargo_on_path
}

install_legible() {
  ensure_git
  ensure_c_compiler
  ensure_rust
  ensure_cargo_on_path

  mkdir -p "$(dirname "$LEGIBLE_SRC")"
  if [ -d "$LEGIBLE_SRC/.git" ]; then
    log "Updating Legible interpreter source..."
    git -C "$LEGIBLE_SRC" pull --ff-only
  else
    log "Cloning Legible interpreter source..."
    git clone --depth 1 "$LEGIBLE_REPO" "$LEGIBLE_SRC"
  fi

  # --no-default-features skips the optional SDL2 build (window/graphics
  # builtins), which nothing in this project uses, so no system SDL2 dev
  # packages are required. HTTP, JSON, file, and SQLite builtins (used by
  # the APK patcher GUI) are not behind a feature flag and are always built.
  log "Building and installing the legible interpreter (first build can take several minutes)..."
  (cd "$LEGIBLE_SRC" && cargo install --path . --no-default-features --locked)
  ensure_cargo_on_path
}

ensure_cargo_on_path
if ! command -v legible >/dev/null 2>&1; then
  install_legible
fi

if ! command -v legible >/dev/null 2>&1; then
  echo "legible was installed but is not on PATH in this shell." >&2
  echo "Open a new terminal (so \$HOME/.cargo/bin is picked up) and re-run this script." >&2
  exit 1
fi

log "Launching APK patcher GUI..."
exec legible run tools/apk_patcher_gui/server.lbl "$@"
