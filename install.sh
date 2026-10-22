#!/bin/sh
# Installs `s` (sessions), the Claude Code and Codex session picker, and its companion
# `ai-bridge` (shared memory and MCP servers between Claude Code and Codex) into ~/.local/bin.
#   curl -fsSL https://raw.githubusercontent.com/mohammadnaraghi/sessions/main/install.sh | sh
# ai-bridge changes nothing until you run `ai-bridge setup`.
# Options (environment variables):
#   SESSIONS_BIN_DIR   install directory (default: ~/.local/bin)
set -e

BASE="https://raw.githubusercontent.com/mohammadnaraghi/sessions/main"
BIN_DIR="${SESSIONS_BIN_DIR:-${CS_BIN_DIR:-$HOME/.local/bin}}"

if ! command -v python3 >/dev/null 2>&1; then
  echo "sessions needs python3 (3.8 or newer)." >&2
  echo "  macOS:  xcode-select --install   or   brew install python" >&2
  echo "  Debian/Ubuntu:  sudo apt install python3" >&2
  exit 1
fi

mkdir -p "$BIN_DIR"
TMP="$(mktemp)"
trap 'rm -f "$TMP"' EXIT
HERE="$(cd "$(dirname "$0")" 2>/dev/null && pwd || true)"

# install NAME: a local copy when run from a checkout, otherwise the latest release
install_one() {
  if [ -n "$HERE" ] && [ -f "$HERE/$1" ] && [ "$(basename "$0")" = "install.sh" ]; then
    cp "$HERE/$1" "$TMP"
  elif command -v curl >/dev/null 2>&1; then
    curl -fsSL "$BASE/$1" -o "$TMP"
  elif command -v wget >/dev/null 2>&1; then
    wget -qO "$TMP" "$BASE/$1"
  else
    echo "need curl or wget to download $1" >&2
    exit 1
  fi
  python3 -m py_compile "$TMP" >/dev/null
  rm -rf "${TMP}__pycache__" "$(dirname "$TMP")/__pycache__" 2>/dev/null || true
  install -m 755 "$TMP" "$BIN_DIR/$1"
  echo "installed $("$BIN_DIR/$1" --version) to $BIN_DIR/$1"
}

install_one s
install_one ai-bridge

# This tool used to be called cs; remove that copy so there is one command. Anything else
# named cs is left alone.
if grep -q "mohammadnaraghi/claude-sessions" "$BIN_DIR/cs" 2>/dev/null; then
  rm -f "$BIN_DIR/cs"
  echo "removed the old cs command (your hosts, sync state and cache move over on the first run)"
fi

if grep -q ai-bridge "$HOME/.codex/hooks.json" 2>/dev/null; then
  "$BIN_DIR/ai-bridge" setup >/dev/null && echo "ai-bridge: setup refreshed"
else
  echo "use Codex too? run  ai-bridge setup  to share memory and MCP servers with Claude Code"
fi

case ":$PATH:" in
  *":$BIN_DIR:"*) echo "type: s" ;;
  *)
    LINE="export PATH=\"$BIN_DIR:\$PATH\""
    case "$(basename "${SHELL:-sh}")" in
      zsh)  RC="$HOME/.zshrc" ;;
      bash) if [ -f "$HOME/.bashrc" ]; then RC="$HOME/.bashrc"; else RC="$HOME/.bash_profile"; fi ;;
      fish) RC="$HOME/.config/fish/config.fish"; LINE="fish_add_path $BIN_DIR" ;;
      *)    RC="$HOME/.profile" ;;
    esac
    if [ -f "$RC" ] && grep -qF "$BIN_DIR" "$RC" 2>/dev/null; then
      :
    else
      mkdir -p "$(dirname "$RC")"
      printf '\n# added by the sessions installer\n%s\n' "$LINE" >> "$RC"
      echo "added $BIN_DIR to PATH in $RC"
    fi
    echo "open a new shell (or run:  $LINE ) and then type: s"
    ;;
esac
