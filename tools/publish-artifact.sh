#!/bin/sh
# Baut die Browser-Version für ein claude.ai-Artifact: Release-Build ohne vorkomprimierte Dateien,
# Seite = Teil von index.html zwischen den ARTIFACT-Markierungen (der Rahmen kommt vom Artifact).
# Aufruf: tools/publish-artifact.sh <Zielordner>
set -e
out="$1"
[ -n "$out" ] || { echo "Zielordner fehlt"; exit 1; }
cd "$(dirname "$0")/../src/PcgWeb"
dotnet publish -c Release -o "$out/publish" -p:CompressionEnabled=false -p:BlazorEnableCompression=false -v q
www="$out/publish/wwwroot"
sed -n '/<!-- ARTIFACT-START -->/,/<!-- ARTIFACT-END -->/p' "$www/index.html" | sed '1d;$d' > "$www/page.html"
find "$www/_framework" -name '*.br' -o -name '*.gz' -o -name '*.pdb' | xargs -r rm -f
mv "$www/_framework" "$www/framework"
echo "Seite: $www/page.html"
(cd "$www" && find framework fonts -type f | sort) > "$www/files.txt"
echo "Dateien: $(wc -l < "$www/files.txt"), Größe: $(du -sh "$www/framework" | cut -f1)"
