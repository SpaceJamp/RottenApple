#!/usr/bin/env bash
# RottenApple installer: installs dependencies on Arch or Fedora and logs every step.
# Usage: ./install.sh [--dry-run] [--verbose]
set -Eeuo pipefail

DRY_RUN=0
VERBOSE=0
for arg in "$@"; do
  case "$arg" in
    --dry-run) DRY_RUN=1 ;;
    --verbose) VERBOSE=1 ;;
    -h|--help) sed -n '2,3s/^# //p' "$0"; exit 0 ;;
    *) echo "Unknown option: $arg (try --help)" >&2; exit 2 ;;
  esac
done

LOG_DIR="${XDG_STATE_HOME:-$HOME/.local/state}/rottenapple"
LOG="$LOG_DIR/install-$(date +%Y%m%d-%H%M%S).log"
mkdir -p "$LOG_DIR"
: >"$LOG"
OS_RELEASE="${OS_RELEASE:-/etc/os-release}"  # overridable so tests can fake a distro
TOOLS=(idevice_id ideviceinfo idevicepair idevicebackup2 idevicerestore)
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
  log DEBUG "Script: $(cd "$(dirname "$0")" && pwd)/$(basename "$0"), args: ${*:-none}"
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
      PACKAGES=(usbmuxd libimobiledevice python tk) ;;
    *" fedora "*)
      PM=dnf
      INSTALL=(dnf install -y)
      PACKAGES=(usbmuxd libimobiledevice-utils python3 python3-tkinter) ;;
    *)
      log ERROR "Unsupported distro '$id'. RottenApple supports Arch and Fedora (and their derivatives)."
      exit 1 ;;
  esac
  log INFO "Package manager: $PM"
}

setup_sudo() {
  SUDO=()
  if ((EUID == 0)); then
    log WARN "Running as root. Run as your normal user instead; the script calls sudo itself."
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
  log INFO "Installing required packages: ${PACKAGES[*]}"
  if ! run "${SUDO[@]}" "${INSTALL[@]}" "${PACKAGES[@]}"; then
    [[ $PM == pacman ]] && log ERROR "Package database may be stale. Run 'sudo pacman -Syu', then rerun this script."
    log ERROR "Required packages failed to install. Full log: $LOG"
    exit 1
  fi
  log OK "Required packages installed."

  # Not every repo ships idevicerestore, so a failure here is a warning, not fatal.
  log INFO "Installing idevicerestore (needed for Update iOS)"
  if run "${SUDO[@]}" "${INSTALL[@]}" idevicerestore; then
    log OK "idevicerestore installed."
  else
    log WARN "idevicerestore is not available from $PM. Update iOS will not work."
    log WARN "Build it from https://github.com/libimobiledevice/idevicerestore"
  fi
}

start_usbmuxd() {
  log INFO "Starting usbmuxd"
  if run "${SUDO[@]}" systemctl start usbmuxd; then
    log OK "usbmuxd running."
  else
    log WARN "Could not start usbmuxd now. udev normally starts it when a device is plugged in."
  fi
}

verify() {
  log INFO "Checking installed tools"
  local tool path
  for tool in "${TOOLS[@]}"; do
    if path=$(command -v "$tool"); then
      log OK "$tool: $path ($("$tool" --version 2>&1 | head -n1 || true))"
    else
      log WARN "$tool: not found on PATH"
    fi
  done
  local tk
  if tk=$(python3 -c 'import tkinter; print(tkinter.TkVersion)' 2>&1); then
    log OK "python3 tkinter: Tk $tk"
  else
    log WARN "python3 tkinter import failed: ${tk##*$'\n'}"
  fi
}

main() {
  log_system_info "$@"
  detect_distro
  setup_sudo
  install_packages
  start_usbmuxd
  verify
  if ((WARNINGS)); then
    log WARN "Finished in ${SECONDS}s with $WARNINGS warning(s). Read them above or in $LOG"
  else
    log OK "Finished in ${SECONDS}s with no warnings."
  fi
  log INFO "Start the app: python3 $(cd "$(dirname "$0")" && pwd)/rottenapple.py"
}

main "$@"
