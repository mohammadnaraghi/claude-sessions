#!/bin/sh
# Installs `cs`, the Claude Code session picker, into ~/.local/bin.
#   curl -fsSL https://raw.githubusercontent.com/mohammadnaraghi/claude-sessions/main/install.sh | sh
# Options (environment variables):
#   CS_BIN_DIR   install directory (default: ~/.local/bin)
set -e

RAW="https://raw.githubusercontent.com/mohammadnaraghi/claude-sessions/main/cs"
BIN_DIR="${CS_BIN_DIR:-$HOME/.local/bin}"
TARGET="$BIN_DIR/cs"

if ! command -v python3 >/dev/null 2>&1; then
  echo "cs needs python3 (3.8 or newer)." >&2
  echo "  macOS:  xcode-select --install   or   brew install python" >&2
  echo "  Debian/Ubuntu:  sudo apt install python3" >&2
  exit 1
fi

mkdir -p "$BIN_DIR"
TMP="$(mktemp)"
trap 'rm -f "$TMP"' EXIT

# Prefer a local copy when run from a checkout, otherwise download.
HERE="$(cd "$(dirname "$0")" 2>/dev/null && pwd || true)"
if [ -n "$HERE" ] && [ -f "$HERE/cs" ] && [ "$(basename "$0")" = "install.sh" ]; then
  cp "$HERE/cs" "$TMP"
elif command -v curl >/dev/null 2>&1; then
  curl -fsSL "$RAW" -o "$TMP"
elif command -v wget >/dev/null 2>&1; then
  wget -qO "$TMP" "$RAW"
else
  echo "need curl or wget to download cs" >&2
  exit 1
fi

python3 -m py_compile "$TMP" >/dev/null
rm -rf "${TMP}__pycache__" "$(dirname "$TMP")/__pycache__" 2>/dev/null || true
install -m 755 "$TMP" "$TARGET"
echo "installed $("$TARGET" --version) to $TARGET"

case ":$PATH:" in
  *":$BIN_DIR:"*) ;;
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
      printf '\n# added by claude-sessions installer\n%s\n' "$LINE" >> "$RC"
      echo "added $BIN_DIR to PATH in $RC"
    fi
    echo "open a new shell (or run:  $LINE ) and then type: cs"
    ;;
esac
