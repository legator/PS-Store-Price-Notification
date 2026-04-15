#!/usr/bin/env sh

set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
CONFIG_PATH="$SCRIPT_DIR/config.yaml"
DRY_RUN=0

usage() {
    cat <<'EOF'
Usage: ./Update-Locales.sh [--config PATH] [--dry-run]

Fetches the current PlayStation country selector locale map and updates the
'locales:' block in config.yaml.

Options:
  --config PATH  Path to config.yaml (default: ./config.yaml)
  --dry-run      Print the discovered diff without writing changes
  --help         Show this help
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --config)
            [ $# -ge 2 ] || { echo "Missing value for --config" >&2; exit 1; }
            CONFIG_PATH="$2"
            shift 2
            ;;
        --dry-run)
            DRY_RUN=1
            shift
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

if [ ! -f "$CONFIG_PATH" ]; then
    echo "Config file not found: $CONFIG_PATH" >&2
    exit 1
fi

TMP_HTML=$(mktemp)
TMP_LOCALES=$(mktemp)
TMP_EXISTING=$(mktemp)
TMP_DIFF=$(mktemp)
TMP_BLOCK=$(mktemp)
TMP_UPDATED=$(mktemp)

cleanup() {
    rm -f "$TMP_HTML" "$TMP_LOCALES" "$TMP_EXISTING" "$TMP_DIFF" "$TMP_BLOCK" "$TMP_UPDATED"
}
trap cleanup EXIT INT TERM

fetch_html() {
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL --max-time 30 "https://www.playstation.com/country-selector/index.html"
        return
    fi

    if command -v wget >/dev/null 2>&1; then
        wget -qO- --timeout=30 "https://www.playstation.com/country-selector/index.html"
        return
    fi

    echo "Neither curl nor wget is installed." >&2
    exit 1
}

echo "Fetching PlayStation country selector..."
fetch_html > "$TMP_HTML"

grep -Eo 'https://www\.playstation\.com/[a-z]{2,}(-[a-z0-9]+)+/' "$TMP_HTML" \
    | sed -E 's#https://www\.playstation\.com/([^/]+)/#\1#' \
    | awk -F- '
        {
            locale = tolower($0)
            country = tolower($NF)
            if (length(country) == 2 && !(country in seen)) {
                seen[country] = 1
                print country " " locale
            }
        }
    ' \
    | sort -k1,1 > "$TMP_LOCALES"

FOUND_COUNT=$(wc -l < "$TMP_LOCALES" | tr -d ' ')
if [ "$FOUND_COUNT" -eq 0 ]; then
    echo "No locales found; the page structure may have changed." >&2
    exit 1
fi

echo "Found $FOUND_COUNT country codes."

grep -E '^  [a-z]{2}: [a-z][a-z0-9-]+$' "$CONFIG_PATH" \
    | sed -E 's/^  ([a-z]{2}): ([a-z][a-z0-9-]+)$/\1 \2/' \
    | sort -k1,1 > "$TMP_EXISTING" || true

awk '
    NR == FNR {
        old[$1] = $2
        oldSeen[$1] = 1
        next
    }

    {
        new[$1] = $2
        newSeen[$1] = 1
    }

    END {
        for (key in newSeen) {
            if (!(key in oldSeen)) {
                print "ADD " key " " new[key]
            } else if (old[key] != new[key]) {
                print "CHG " key " " old[key] " " new[key]
            }
        }

        for (key in oldSeen) {
            if (!(key in newSeen)) {
                print "DEL " key " " old[key]
            }
        }
    }
' "$TMP_EXISTING" "$TMP_LOCALES" | sort -k2,2 > "$TMP_DIFF"

if [ ! -s "$TMP_DIFF" ]; then
    echo "No changes detected - config.yaml is already up to date."
    exit 0
fi

while IFS=' ' read -r kind country value1 value2; do
    case "$kind" in
        ADD)
            printf '  + %s : %s\n' "$country" "$value1"
            ;;
        CHG)
            printf '  ~ %s : %s -> %s\n' "$country" "$value1" "$value2"
            ;;
        DEL)
            printf '  - %s : %s\n' "$country" "$value1"
            ;;
    esac
done < "$TMP_DIFF"

if [ "$DRY_RUN" -eq 1 ]; then
    echo
    echo "Dry run - no changes written."
    exit 0
fi

{
    echo "locales:"
    while IFS=' ' read -r country locale; do
        printf '  %s: %s\n' "$country" "$locale"
    done < "$TMP_LOCALES"
} > "$TMP_BLOCK"

awk -v block_file="$TMP_BLOCK" '
    BEGIN {
        while ((getline line < block_file) > 0) {
            block = block line ORS
        }
        close(block_file)
        inBlock = 0
        replaced = 0
    }

    {
        if (!replaced && $0 == "locales:") {
            printf "%s", block
            replaced = 1
            inBlock = 1
            next
        }

        if (inBlock) {
            if ($0 ~ /^[[:space:]]+/) {
                next
            }

            if ($0 == "") {
                print
                inBlock = 0
                next
            }

            inBlock = 0
        }

        print
    }

    END {
        if (!replaced) {
            if (NR > 0) {
                print ""
            }
            printf "%s", block
        }
    }
' "$CONFIG_PATH" > "$TMP_UPDATED"

mv "$TMP_UPDATED" "$CONFIG_PATH"

echo
echo "config.yaml updated ($FOUND_COUNT countries)."
echo "Review any changed or removed entries before committing,"
echo "especially countries with multiple locale variants."