#!/usr/bin/env sh

set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
RUN_AT="09:00"
TASK_NAME="PSTitlePriceNotification"
TASK_WRAPPER="$SCRIPT_DIR/.ps-price-notification-task.sh"
LOG_DIR="$SCRIPT_DIR/data"
LOG_FILE="$LOG_DIR/ps_price_scheduled.log"
TMP_CRON=""
CURRENT_CRON=""

cleanup() {
    rm -f "$TMP_CRON" "$CURRENT_CRON"
}
trap cleanup EXIT INT TERM

usage() {
    cat <<'EOF'
Usage: ./setup_task.sh [--run-at HH:MM] [--task-name NAME]

Installs a per-user scheduled run for PSPriceNotification:
  macOS           -> launchd LaunchAgent
  Linux/RPi OS    -> crontab entry

Resolution order:
  1. ./publish/PSPriceNotification   (native non-Windows published binary)
    2. dotnet run --project <dir>      (using the net10.0 target)

The scheduler prefers a native published binary when present and otherwise falls
back to dotnet run against the cross-platform framework target.

Options:
  --run-at HH:MM   Time of day to run the check (default: 09:00)
  --task-name NAME Scheduler label/name (default: PSTitlePriceNotification)
  --help           Show this help
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --run-at)
            [ $# -ge 2 ] || { echo "Missing value for --run-at" >&2; exit 1; }
            RUN_AT="$2"
            shift 2
            ;;
        --task-name)
            [ $# -ge 2 ] || { echo "Missing value for --task-name" >&2; exit 1; }
            TASK_NAME="$2"
            shift 2
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            echo "Unknown argument: $1" >&2
            usage >&2
            exit 1
            ;;
    esac
done

validate_time() {
    printf '%s' "$RUN_AT" | awk -F: '
        NF != 2 { exit 1 }
        $1 !~ /^[0-9][0-9]?$/ { exit 1 }
        $2 !~ /^[0-9][0-9]$/ { exit 1 }
        ($1 + 0) < 0 || ($1 + 0) > 23 { exit 1 }
        ($2 + 0) < 0 || ($2 + 0) > 59 { exit 1 }
    '
}

validate_time || {
    echo "Invalid --run-at value: $RUN_AT" >&2
    exit 1
}

mkdir -p "$LOG_DIR"

TARGET_FRAMEWORKS=$(awk -F'[<>]' '/<TargetFrameworks>/{ print $3; exit } /<TargetFramework>/{ print $3; exit }' "$SCRIPT_DIR/PSPriceNotification.csproj")
PUBLISHED_BINARY="$SCRIPT_DIR/publish/PSPriceNotification"

create_wrapper_for_binary() {
    cat > "$TASK_WRAPPER" <<EOF
#!/usr/bin/env sh
set -eu
cd "$SCRIPT_DIR"
exec "$PUBLISHED_BINARY"
EOF
}

create_wrapper_for_dotnet() {
    DOTNET_PATH=$(command -v dotnet)
    cat > "$TASK_WRAPPER" <<EOF
#!/usr/bin/env sh
set -eu
cd "$SCRIPT_DIR"
exec "$DOTNET_PATH" run --project "$SCRIPT_DIR" --configuration Release --framework net10.0
EOF
}

if [ -x "$PUBLISHED_BINARY" ]; then
    RUNTIME_LABEL="native published binary"
    create_wrapper_for_binary
elif command -v dotnet >/dev/null 2>&1; then
    case ";$TARGET_FRAMEWORKS;" in
        *";net10.0;"*)
            RUNTIME_LABEL="dotnet run"
            create_wrapper_for_dotnet
            ;;
        *)
            cat >&2 <<EOF
This project does not expose a cross-platform target framework.

Add a non-Windows target framework or publish a native non-Windows binary to:
  $PUBLISHED_BINARY

Then run ./setup_task.sh again.
EOF
            exit 1
            ;;
    esac
else
    cat >&2 <<EOF
No runnable target found.

Expected one of:
  1. $PUBLISHED_BINARY
    2. dotnet on PATH with the net10.0 target available
EOF
    exit 1
fi

chmod +x "$TASK_WRAPPER"

HOUR=${RUN_AT%%:*}
MINUTE=${RUN_AT##*:}
HOUR_NUM=$(printf '%s' "$HOUR" | awk '{ print $1 + 0 }')
MINUTE_NUM=$(printf '%s' "$MINUTE" | awk '{ print $1 + 0 }')

install_macos() {
    PLIST_DIR="$HOME/Library/LaunchAgents"
    PLIST_PATH="$PLIST_DIR/$TASK_NAME.plist"
    LABEL="$TASK_NAME"

    mkdir -p "$PLIST_DIR"

    cat > "$PLIST_PATH" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
  <dict>
    <key>Label</key>
    <string>$LABEL</string>
    <key>ProgramArguments</key>
    <array>
      <string>$TASK_WRAPPER</string>
    </array>
    <key>StartCalendarInterval</key>
    <dict>
      <key>Hour</key>
        <integer>$HOUR_NUM</integer>
      <key>Minute</key>
        <integer>$MINUTE_NUM</integer>
    </dict>
    <key>WorkingDirectory</key>
    <string>$SCRIPT_DIR</string>
    <key>StandardOutPath</key>
    <string>$LOG_FILE</string>
    <key>StandardErrorPath</key>
    <string>$LOG_FILE</string>
    <key>RunAtLoad</key>
    <false/>
  </dict>
</plist>
EOF

    DOMAIN="gui/$(id -u)"
    launchctl bootout "$DOMAIN" "$PLIST_PATH" >/dev/null 2>&1 || true
    launchctl bootstrap "$DOMAIN" "$PLIST_PATH"
    launchctl enable "$DOMAIN/$LABEL"

    echo
    echo "LaunchAgent '$LABEL' registered successfully."
    echo "  Runs daily at : $RUN_AT"
    echo "  Runtime       : $RUNTIME_LABEL"
    echo "  Wrapper       : $TASK_WRAPPER"
    echo "  Plist         : $PLIST_PATH"
    echo "  Log file      : $LOG_FILE"
    echo
    echo "Useful commands:"
    echo "  launchctl kickstart -k $DOMAIN/$LABEL"
    echo "  launchctl print $DOMAIN/$LABEL"
    echo "  launchctl bootout $DOMAIN $PLIST_PATH"
}

install_linux() {
    command -v crontab >/dev/null 2>&1 || {
        echo "crontab is not installed on this system." >&2
        exit 1
    }

    CRON_LINE="$MINUTE $HOUR * * * \"$TASK_WRAPPER\" >> \"$LOG_FILE\" 2>&1"
    TMP_CRON=$(mktemp)
    CURRENT_CRON=$(mktemp)

    crontab -l > "$CURRENT_CRON" 2>/dev/null || true
    awk -v wrapper="$TASK_WRAPPER" 'index($0, wrapper) == 0 { print }' "$CURRENT_CRON" > "$TMP_CRON"
    printf '%s\n' "$CRON_LINE" >> "$TMP_CRON"
    crontab "$TMP_CRON"

    echo
    echo "Cron entry '$TASK_NAME' registered successfully."
    echo "  Runs daily at : $RUN_AT"
    echo "  Runtime       : $RUNTIME_LABEL"
    echo "  Wrapper       : $TASK_WRAPPER"
    echo "  Log file      : $LOG_FILE"
    echo
    echo "Useful commands:"
    echo "  $TASK_WRAPPER"
    echo "  crontab -l"
    echo "  crontab -l | grep -F '$TASK_WRAPPER'"
}

case "$(uname -s)" in
    Darwin)
        install_macos
        ;;
    Linux)
        install_linux
        ;;
    *)
        echo "Unsupported platform: $(uname -s)" >&2
        exit 1
        ;;
esac