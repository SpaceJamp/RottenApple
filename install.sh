#!/usr/bin/env bash
# RottenApple installer: installs dependencies on Arch or Fedora and logs every step.
# Usage: ./install.sh [--dry-run] [--verbose] [--build-idevicerestore]
set -Eeuo pipefail

DRY_RUN=0
VERBOSE=0
FORCE_BUILD=0
for arg in "$@"; do
  case "$arg" in
    --dry-run) DRY_RUN=1 ;;
    --verbose) VERBOSE=1 ;;
    --build-idevicerestore) FORCE_BUILD=1 ;;
    -h|--help) sed -n '2,3s/^# //p' "$0"; exit 0 ;;
    *) echo "Unknown option: $arg (try --help)" >&2; exit 2 ;;
  esac
done

SCRIPT_DIR=$(cd "$(dirname "$(readlink -f "$0")")" && pwd)  # real location, even when run via a symlink
STATE_HOME=${XDG_STATE_HOME:-}
[[ $STATE_HOME == /* ]] || STATE_HOME="$HOME/.local/state"  # XDG spec: ignore relative values
LOG_DIR="$STATE_HOME/rottenapple"
LOG="$LOG_DIR/install-$(date +%Y%m%d-%H%M%S)-$$.log"
mkdir -p -m 700 "$LOG_DIR"
# logs hold usernames, paths and device output: private to this user, and never follow a planted symlink
(umask 077 && set -o noclobber && : >"$LOG") || { echo "Cannot create log file $LOG" >&2; exit 1; }
OS_RELEASE="${OS_RELEASE:-/etc/os-release}"  # overridable so tests can fake a distro
TOOLS=(idevice_id ideviceinfo idevicepair idevicebackup2 idevicerestore)
# Upstream commits for the source build (--build-idevicerestore). Bump them deliberately after
# reviewing the changes: git ls-remote https://github.com/libimobiledevice/<repo>.git HEAD
declare -A PINNED=(
  [libplist]=fe3dc34dc3484006e12b403f7ceb06f0ad40b6f4
  [libimobiledevice-glue]=10ddafa16458a012bcbd3e83a6ab36a032dc84d8
  [libusbmuxd]=93eb168bf6b07472d17781328c21df0c60300524
  [libtatsu]=e7d6ad13ef928aa609d0ccdfc586f7d6e8e049bf
  [libimobiledevice]=fa0f79190142bc309307967c058f89c1b36eb6b8
  [libirecovery]=dc2e4c2628284b1f0cd1cebdd09b1b6c0738a1db
  [idevicerestore]=0313795a84ac15248bc109850b5e2ea660609a23
)
APP_DIR="$HOME/.local/share/rottenapple"
WARNINGS=0

if [[ -t 2 ]]; then
  declare -A COLOR=([DEBUG]=$'\e[2m' [INFO]=$'\e[36m' [OK]=$'\e[32m' [WARN]=$'\e[33m' [ERROR]=$'\e[31m' [CMD]=$'\e[2m')
  RESET=$'\e[0m'
else
  declare -A COLOR=()
  RESET=""
fi

# Every level goes to the log file; DEBUG reaches the screen only with --verbose.
log() {
  local level=$1 ts
  shift
  printf -v ts '%(%Y-%m-%d %H:%M:%S)T' -1
  printf '%s [%-5s] %s\n' "$ts" "$level" "$*" >>"$LOG"
  [[ $level == WARN ]] && WARNINGS=$((WARNINGS + 1))
  [[ $level == DEBUG && $VERBOSE == 0 ]] && return 0
  printf '%s%s [%-5s] %s%s\n' "${COLOR[$level]:-}" "$ts" "$level" "$*" "$RESET" >&2
}

# Success message for a step that run() executed; in a dry run nothing happened, so say so.
ok() {
  if ((DRY_RUN)); then log INFO "(dry run) would be done: $*"; else log OK "$*"; fi
}

# Run a command, log each output line, its exit code and how long it took.
run() {
  log INFO "\$ $*"
  if ((DRY_RUN)); then
    log INFO "(dry run, not executed)"
    return 0
  fi
  local start=$SECONDS rc=0
  "$@" 2>&1 | while IFS= read -r line || [[ -n $line ]]; do log CMD "$line"; done || rc=$?
  log DEBUG "exit code $rc after $((SECONDS - start))s: $*"
  return "$rc"
}

on_error() {
  log ERROR "Line $1 failed with exit code $2: $3"
  log ERROR "Install did not finish. Full log: $LOG"
}
trap 'rc=$?; on_error "$LINENO" "$rc" "$BASH_COMMAND"' ERR

log_system_info() {
  log INFO "RottenApple installer started$( ((DRY_RUN)) && echo ' (dry run)')"
  log INFO "Log file: $LOG"
  log DEBUG "Kernel: $(uname -srm)"
  log DEBUG "User: $(id -un) (uid $(id -u)), shell bash $BASH_VERSION"
  log DEBUG "Script: $SCRIPT_DIR/$(basename "$0"), args: ${*:-none}"
  if [[ -r $OS_RELEASE ]]; then
    while IFS= read -r line; do log DEBUG "os-release: $line"; done <"$OS_RELEASE"
  fi
}

detect_distro() {
  [[ -r $OS_RELEASE ]] || { log ERROR "Cannot read $OS_RELEASE, unable to detect the distro."; exit 1; }
  local id id_like name
  id=$(. "$OS_RELEASE"; echo "${ID:-}")
  id_like=$(. "$OS_RELEASE"; echo "${ID_LIKE:-}")
  name=$(. "$OS_RELEASE"; echo "${PRETTY_NAME:-$id}")
  log INFO "Detected: $name (ID=$id, ID_LIKE=${id_like:-none})"
  case " $id $id_like " in
    *" arch "*)
      PM=pacman
      INSTALL=(pacman -S --needed --noconfirm)
      PACKAGES=(usbmuxd libimobiledevice dotnet-sdk)
      BUILD_DEPS=(base-devel git autoconf automake libtool pkgconf libzip curl openssl libusb readline zlib) ;;
    *" fedora "*)
      PM=dnf
      INSTALL=(dnf install -y)
      PACKAGES=(usbmuxd libimobiledevice-utils dotnet-sdk-10.0)
      BUILD_DEPS=(gcc make git autoconf automake libtool pkgconf-pkg-config libzip-devel libcurl-devel
                  openssl-devel libusb1-devel readline-devel zlib-devel) ;;
    *)
      log ERROR "Unsupported distro '$id'. RottenApple supports Arch and Fedora (and their derivatives)."
      exit 1 ;;
  esac
  log INFO "Package manager: $PM"
}

setup_sudo() {
  SUDO=()
  if ((EUID == 0)); then
    # as root, fetched upstream code would build and install as root and leave root-owned files in $HOME
    log ERROR "Do not run this as root. Run it as your normal user; it calls sudo itself when needed."
    exit 1
  elif ((DRY_RUN)); then
    SUDO=(sudo)
  else
    command -v sudo >/dev/null || { log ERROR "sudo not found. Install it or run as root."; exit 1; }
    SUDO=(sudo)
    log INFO "Asking for sudo password once up front."
    sudo -v
  fi
}

install_packages() {
  if ! ((DRY_RUN)) && ! command -v "$PM" >/dev/null; then
    log ERROR "$PM not found on PATH."
    exit 1
  fi
  # pacman -S against an old sync database can fetch outdated or missing packages; never run -Sy alone
  if [[ $PM == pacman ]] && ! find /var/lib/pacman/sync -name '*.db' -mtime -7 2>/dev/null | grep -q .; then
    log WARN "The pacman package database is over a week old. Run 'sudo pacman -Syu' first if installs fail."
  fi
  log INFO "Installing required packages: ${PACKAGES[*]}"
  if ! run "${SUDO[@]}" "${INSTALL[@]}" "${PACKAGES[@]}"; then
    [[ $PM == pacman ]] && log ERROR "Package database may be stale. Run 'sudo pacman -Syu', then rerun this script."
    log ERROR "Required packages failed to install. Full log: $LOG"
    exit 1
  fi
  ok "Required packages installed."

  if ((FORCE_BUILD)); then
    log INFO "--build-idevicerestore given, skipping the $PM package."
    build_idevicerestore
    return 0
  fi
  log INFO "Installing idevicerestore (needed for Update iOS and Erase)"
  if run "${SUDO[@]}" "${INSTALL[@]}" idevicerestore; then
    ok "idevicerestore installed."
  else
    log WARN "idevicerestore is not available from $PM. Building it from source instead."
    build_idevicerestore
  fi
}

# Build idevicerestore and the libimobiledevice libraries it needs from git into ~/.local.
# Distro packages are often older than idevicerestore requires (configure fails on libzip,
# libtatsu, libimobiledevice >= 1.4.0), so the whole stack is built together. Libraries get
# an rpath to ~/.local/lib, so system packages are left untouched and no sudo is needed.
build_idevicerestore() {
  local prefix="$HOME/.local" src="${XDG_CACHE_HOME:-$HOME/.cache}/rottenapple/src" repo dir
  log INFO "Installing build tools: ${BUILD_DEPS[*]}"
  if ! run "${SUDO[@]}" "${INSTALL[@]}" "${BUILD_DEPS[@]}"; then
    log WARN "Build tools failed to install. Skipping the idevicerestore build; Update iOS will not work."
    return 0
  fi
  export PKG_CONFIG_PATH="$prefix/lib/pkgconfig${PKG_CONFIG_PATH:+:$PKG_CONFIG_PATH}"
  export LDFLAGS="-Wl,-rpath,$prefix/lib ${LDFLAGS:-}"
  log DEBUG "PKG_CONFIG_PATH=$PKG_CONFIG_PATH LDFLAGS=$LDFLAGS"
  ((DRY_RUN)) || mkdir -p "$src"
  # Order matters: each repo depends on the ones before it.
  for repo in libplist libimobiledevice-glue libusbmuxd libtatsu libimobiledevice libirecovery idevicerestore; do
    dir="$src/$repo"
    local sha=${PINNED[$repo]}
    log INFO "Building $repo at pinned commit $sha"
    # Fetch exactly the pinned commit, so a new or tampered upstream commit never runs here unreviewed.
    if [[ ! -d $dir/.git ]]; then
      run git init -q "$dir" && run git -C "$dir" remote add origin "https://github.com/libimobiledevice/$repo.git" \
        || { build_failed "$repo"; return 0; }
    fi
    run git -C "$dir" fetch -q --depth 1 origin "$sha" && run git -C "$dir" checkout -q --detach FETCH_HEAD \
      || { build_failed "$repo"; return 0; }
    if ! ((DRY_RUN)) && [[ $(git -C "$dir" rev-parse HEAD) != "$sha" ]]; then
      log ERROR "$repo is not at the pinned commit $sha. Refusing to build it."
      build_failed "$repo"
      return 0
    fi
    # --without-udev: libirecovery would otherwise install rules into /usr/lib/udev, which needs root.
    # Other repos ignore options they do not know, with a warning.
    run env -C "$dir" ./autogen.sh --prefix="$prefix" --libdir="$prefix/lib" --without-cython --without-udev \
      && run make -C "$dir" -j"$(nproc 2>/dev/null || echo 2)" \
      && run make -C "$dir" install \
      || { build_failed "$repo"; return 0; }
    ok "$repo built and installed to $prefix"
  done
  ok "idevicerestore built: $prefix/bin/idevicerestore"
  [[ :$PATH: == *":$prefix/bin:"* ]] || log WARN "$prefix/bin is not on your PATH. The app still finds idevicerestore there."
}

build_failed() {
  log WARN "Building $1 failed. Update iOS and Erase will not work. The log above shows the compiler output."
  log WARN "Report it with this log file at https://github.com/libimobiledevice/$1/issues"
}

start_usbmuxd() {
  log INFO "Starting usbmuxd"
  if run "${SUDO[@]}" systemctl start usbmuxd; then
    ok "usbmuxd running."
  else
    log WARN "Could not start usbmuxd now. udev normally starts it when a device is plugged in."
  fi
}

verify() {
  log INFO "Checking installed tools"
  local tool path
  for tool in "${TOOLS[@]}"; do
    if path=$(command -v "$tool" || command -v "$HOME/.local/bin/$tool"); then
      log OK "$tool: $path ($("$path" --version 2>&1 | head -n1 || true))"
    else
      log WARN "$tool: not found on PATH"
    fi
  done
  if command -v dotnet >/dev/null; then
    log OK "dotnet: $(dotnet --version 2>&1 | head -n1 || true)"
  else
    log WARN "dotnet: not found on PATH"
  fi
  if [[ ${XDG_SESSION_TYPE:-} == wayland || -n ${WAYLAND_DISPLAY:-} ]]; then
    log OK "Session: Wayland"
  else
    log WARN "Session: ${XDG_SESSION_TYPE:-unknown}. RottenApple only runs in a Wayland session, not X11."
  fi
  if [[ -x $APP_DIR/RottenApple ]]; then
    log OK "RottenApple: $APP_DIR/RottenApple"
  else
    log WARN "RottenApple: not built"
  fi
}

# Publish the Avalonia app to ~/.local/share/rottenapple and add a launcher and menu entry.
build_app() {
  local launcher="$HOME/.local/bin/rottenapple" desktop="$HOME/.local/share/applications/rottenapple.desktop"
  local icon="$HOME/.local/share/icons/hicolor/scalable/apps/rottenapple.svg"
  # A newline or backslash cannot be written safely into the launcher or the .desktop Exec line.
  if [[ $HOME == *[$'\n\\']* ]]; then
    log ERROR "Your home directory path contains a newline or backslash; cannot write a safe launcher."
    exit 1
  fi
  export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
  log INFO "Building RottenApple (Avalonia, .NET) into $APP_DIR"
  if ! run dotnet publish "$SCRIPT_DIR/src/RottenApple/RottenApple.csproj" -c Release -o "$APP_DIR"; then
    log ERROR "Building RottenApple failed. The dotnet output is in $LOG"
    exit 1
  fi
  ok "RottenApple built."
  ((DRY_RUN)) && { log INFO "(dry run, launcher and menu entry not written)"; return 0; }
  mkdir -p "${launcher%/*}" "${desktop%/*}" "${icon%/*}"
  # %q quotes the path for bash, so spaces, quotes or $ in it cannot break or inject into the launcher
  printf '#!/bin/bash\nexec %q "$@"\n' "$APP_DIR/RottenApple" >"$launcher"
  chmod +x "$launcher"
  cp "$SCRIPT_DIR/src/RottenApple/Assets/rottenapple.svg" "$icon"
  # Desktop Entry Exec quoting: wrap in "", escape " ` $ with a backslash, double every % (field codes)
  local exec=$launcher
  exec=${exec//\"/\\\"}
  exec=${exec//\`/\\\`}
  exec=${exec//\$/\\\$}
  exec=${exec//%/%%}
  # StartupWMClass lets the compositor match the window to this entry and its icon.
  printf '[Desktop Entry]\nType=Application\nName=RottenApple\nComment=Back up, restore and update iPhones and iPads\nExec="%s"\nIcon=rottenapple\nStartupWMClass=RottenApple\nTerminal=false\nCategories=Utility;\n' \
    "$exec" >"$desktop"
  log OK "Launcher: $launcher"
  log OK "Icon: $icon"
  log OK "Menu entry: $desktop"
}

main() {
  log_system_info "$@"
  detect_distro
  setup_sudo
  install_packages
  build_app
  start_usbmuxd
  verify
  if ((WARNINGS)); then
    log WARN "Finished in ${SECONDS}s with $WARNINGS warning(s). Read them above or in $LOG"
  else
    log OK "Finished in ${SECONDS}s with no warnings."
  fi
  log INFO "Start the app: rottenapple (or from your app menu)"
}

main "$@"
