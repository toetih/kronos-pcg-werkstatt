# Baut die Werkstatt als EINE HTML-Datei, die man per Doppelklick öffnen kann (file://, ohne Server).
# Alle Dateien aus framework/ werden als Base64 eingebettet; Blazor bekommt sie über loadBootResource
# (JS-Module als blob:-URL, alles andere als Response). Aufruf: build-offline.py <wwwroot> <ausgabe.html>
import base64, json, os, re, sys

www, out = sys.argv[1], sys.argv[2]
html = open(os.path.join(www, "index.html"), encoding="utf-8").read()

files = {}
for root, _, names in os.walk(os.path.join(www, "framework")):
    for name in names:
        path = os.path.join(root, name)
        rel = os.path.relpath(path, os.path.join(www, "framework")).replace(os.sep, "/")
        files[rel] = base64.b64encode(open(path, "rb").read()).decode("ascii")

loader = open(os.path.join(www, "framework", "blazor.webassembly.js"), encoding="utf-8").read()
loader = loader.replace("</script", "<\\/script")

start = """<script>
  // Offline-Werkstatt: alle Dateien stecken in dieser HTML-Datei.
  window.__pcgFiles = %s;
  (function () {
    var cache = {};
    function bytes(key) {
      var text = atob(window.__pcgFiles[key]);
      var data = new Uint8Array(text.length);
      for (var i = 0; i < text.length; i++) data[i] = text.charCodeAt(i);
      return data;
    }
    function mime(key) {
      return /\\.wasm$/.test(key) ? "application/wasm" : /\\.json$/.test(key) ? "application/json" : "text/javascript";
    }
    window.__pcgResource = function (type, name, defaultUri) {
      var m = /_framework\\/([^?#]*)/.exec(defaultUri || "");
      var key = m ? decodeURIComponent(m[1]) : name;
      if (!(key in window.__pcgFiles)) return null;
      if (type === "dotnetjs") {
        // Die .NET-Lader bestimmen ihren Ort über import.meta.url; aus einer blob:-Adresse heraus geht das
        // nicht. Deshalb bekommen sie die Adresse, unter der sie normalerweise lägen.
        if (!cache[key]) {
          var source = new TextDecoder().decode(bytes(key))
            .replace(/import\\.meta\\.url/g, JSON.stringify(new URL("_framework/" + key, document.baseURI).href));
          cache[key] = URL.createObjectURL(new Blob([source], { type: "text/javascript" }));
        }
        return cache[key];
      }
      return Promise.resolve(new Response(bytes(key), { headers: { "content-type": mime(key) } }));
    };
  })();
</script>
<script autostart="false">%s</script>
<script>
  Blazor.start({ loadBootResource: window.__pcgResource });
</script>""" % (json.dumps(files), loader)

pattern = re.compile(r'<script src="framework/blazor\.webassembly\.js" autostart="false"></script>\s*<script>\s*Blazor\.start\(\{.*?\}\);\s*</script>', re.S)
html, count = pattern.subn(lambda _: start, html)
if count != 1:
    sys.exit("Blazor-Start in index.html nicht gefunden")
# Schriften einbetten: Die eine HTML-Datei hat keinen Ordner fonts/ neben sich.
def font(match):
    data = base64.b64encode(open(os.path.join(www, "fonts", match.group(1)), "rb").read()).decode("ascii")
    return f'url("data:font/woff2;base64,{data}")'

html = re.sub(r'url\("fonts/([^"]+\.woff2)"\)', font, html)
# Logo einbetten (hell und dunkel)
def logo(match):
    data = base64.b64encode(open(os.path.join(www, match.group(1)), "rb").read()).decode("ascii")
    return f'url("data:image/png;base64,{data}")'

html = re.sub(r'url\("(logo[^"]*\.png)"\)', logo, html)
html = html.replace('    <link rel="icon" href="icon-192.png" />\n', "")
open(out, "w", encoding="utf-8").write(html)
print(f"{out}: {os.path.getsize(out) / 1e6:.1f} MB, {len(files)} Dateien eingebettet")
