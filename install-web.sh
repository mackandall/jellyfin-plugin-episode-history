#!/usr/bin/env bash
# Patches jellyfin-web's index.html to load this plugin's guide script, which adds a
# "Mark as watched" button to the Live TV guide's program dialog.
#
# index.html is owned by root (installed by the jellyfin-web package) and the jellyfin
# service account can't write it, so this is a script you run with sudo rather than
# something the plugin does at startup. Safe to re-run: the snippet is only inserted if
# its marker comment is missing.
#
# Re-run after every jellyfin-web package upgrade: that package overwrites index.html,
# which drops the patch (the button just stops appearing).
#
# Usage: sudo ./install-web.sh [path-to-index.html]
#   Default path: /usr/share/jellyfin/web/index.html

set -euo pipefail

WEB_INDEX="${1:-/usr/share/jellyfin/web/index.html}"
MARKER="<!-- EpisodeHistory:guidemenu:begin -->"

if [[ ! -f "$WEB_INDEX" ]]; then
    echo "error: $WEB_INDEX not found" >&2
    exit 1
fi

if grep -qF "$MARKER" "$WEB_INDEX"; then
    echo "Already patched: $WEB_INDEX"
    exit 0
fi

if [[ ! -w "$WEB_INDEX" ]]; then
    echo "error: need write access to $WEB_INDEX — run with sudo" >&2
    exit 1
fi

cp "$WEB_INDEX" "$WEB_INDEX.episodehistory-backup"

WEB_INDEX="$WEB_INDEX" MARKER="$MARKER" python3 <<'PYEOF'
import os

path = os.environ["WEB_INDEX"]
snippet = (
    os.environ["MARKER"] + "\n"
    '<script defer src="/EpisodeHistory/web/guidemenu.js"></script>\n'
    "<!-- EpisodeHistory:guidemenu:end -->\n"
)

with open(path, "r", encoding="utf-8") as f:
    html = f.read()

if "</body>" not in html:
    raise SystemExit("error: no </body> tag found in " + path)

with open(path, "w", encoding="utf-8") as f:
    f.write(html.replace("</body>", snippet + "</body>", 1))
PYEOF

echo "Patched $WEB_INDEX (backup: $WEB_INDEX.episodehistory-backup)"
echo "Reload the Jellyfin web page (hard refresh) to pick up the change."
