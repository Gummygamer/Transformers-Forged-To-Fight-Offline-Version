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

LEGIBLE_REPO="https://github.com/Gummygamer/legible-lang.git"
LEGIBLE_BRANCH="development"
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

# The Legible repository is public. Keep these operations explicitly
# non-interactive so an invalid URL, proxy, or Git configuration cannot make
# the launcher ask the user for a GitHub password (password authentication is
# not supported by GitHub).
git_public() {
  local askpass=/bin/false
  [ -x "$askpass" ] || askpass=false
  GIT_TERMINAL_PROMPT=0 GIT_ASKPASS="$askpass" \
    git -c credential.helper= -c core.askPass="$askpass" "$@"
}

verify_legible_source() {
  local result
  if ! result="$(git_public ls-remote --exit-code "$LEGIBLE_REPO" \
      "refs/heads/$LEGIBLE_BRANCH" 2>&1)"; then
    echo "Could not access the public Legible source repository." >&2
    echo "  Repository: $LEGIBLE_REPO" >&2
    echo "  Branch:    $LEGIBLE_BRANCH" >&2
    echo "$result" >&2
    echo "Check the URL, network/proxy settings, and Git configuration, then re-run." >&2
    return 1
  fi
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
  verify_legible_source
  ensure_c_compiler
  ensure_rust
  ensure_cargo_on_path

  mkdir -p "$(dirname "$LEGIBLE_SRC")"
  if [ -d "$LEGIBLE_SRC/.git" ]; then
    local remote branch status
    remote="$(git -C "$LEGIBLE_SRC" config --get remote.origin.url || true)"
    branch="$(git -C "$LEGIBLE_SRC" symbolic-ref --quiet --short HEAD || true)"
    status="$(git -C "$LEGIBLE_SRC" status --porcelain 2>/dev/null || true)"
    if [ "$remote" = "$LEGIBLE_REPO" ] && [ "$branch" = "$LEGIBLE_BRANCH" ]; then
      if [ -n "$status" ]; then
        echo "The Legible source cache has local changes and cannot be updated safely:" >&2
        echo "  $LEGIBLE_SRC" >&2
        echo "Commit or move those changes, then re-run the launcher." >&2
        exit 1
      fi
      log "Updating Legible interpreter source..."
      git_public -C "$LEGIBLE_SRC" pull --ff-only origin "$LEGIBLE_BRANCH"
    else
      local backup
      backup="${LEGIBLE_SRC}.invalid-$(date +%Y%m%d%H%M%S)-$$"
      log "Preserving the old Legible source cache at $backup"
      mv -- "$LEGIBLE_SRC" "$backup"
      clone_legible_source
    fi
  elif [ -e "$LEGIBLE_SRC" ]; then
    local backup
    backup="${LEGIBLE_SRC}.invalid-$(date +%Y%m%d%H%M%S)-$$"
    log "Preserving the incomplete Legible source cache at $backup"
    mv -- "$LEGIBLE_SRC" "$backup"
    clone_legible_source
  else
    clone_legible_source
  fi

  # --no-default-features skips the optional SDL2 build (window/graphics
  # builtins), which nothing in this project uses, so no system SDL2 dev
  # packages are required. HTTP, JSON, file, and SQLite builtins (used by
  # the APK patcher GUI) are not behind a feature flag and are always built.
  log "Building and installing the legible interpreter (first build can take several minutes)..."
  (cd "$LEGIBLE_SRC" && cargo install --path . --no-default-features --locked)
  ensure_cargo_on_path
}

clone_legible_source() {
  local temporary_src="${LEGIBLE_SRC}.tmp-$$"
  if [ -e "$temporary_src" ]; then
    local stale_tmp="${temporary_src}.stale-$(date +%Y%m%d%H%M%S)"
    log "Preserving an interrupted clone at $stale_tmp"
    mv -- "$temporary_src" "$stale_tmp"
  fi
  log "Cloning Legible interpreter source (anonymous HTTPS)..."
  if ! git_public clone --depth 1 --branch "$LEGIBLE_BRANCH" \
      "$LEGIBLE_REPO" "$temporary_src"; then
    local failed_tmp="${temporary_src}.failed-$(date +%Y%m%d%H%M%S)"
    if [ -e "$temporary_src" ]; then
      mv -- "$temporary_src" "$failed_tmp"
      echo "The incomplete clone was preserved at $failed_tmp." >&2
    fi
    echo "Legible source cloning failed; no GitHub credentials are required for this public repository." >&2
    exit 1
  fi
  mv -- "$temporary_src" "$LEGIBLE_SRC"
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
