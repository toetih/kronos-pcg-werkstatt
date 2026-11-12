#!/bin/sh
# Baut die Werkstatt als fertige Webseite (nur feste Dateien) für eine eigene Adresse, z. B. GitHub Pages.
# Aufruf: tools/publish-site.sh <Zielordner> [eigene-adresse.de]
# Ergebnis im Zielordner: index.html, framework/, fonts/, icon, 404.html, .nojekyll und (mit Adresse) CNAME.
set -e
out="$1"
domain="$2"
[ -n "$out" ] || { echo "Zielordner fehlt"; exit 1; }
here="$(cd "$(dirname "$0")" && pwd)"
work="$(mktemp -d)"
"$here/publish-artifact.sh" "$work" > /dev/null
www="$work/publish/wwwroot"
mkdir -p "$out"
cp -r "$www/framework" "$www/fonts" "$out/"
cp "$www/index.html" "$www/icon-192.png" "$out/"
cp "$www/index.html" "$out/404.html"
: > "$out/.nojekyll"
[ -z "$domain" ] || printf '%s\n' "$domain" > "$out/CNAME"
echo "Webseite: $out ($(du -sh "$out" | cut -f1), $(find "$out" -type f | wc -l) Dateien)"
