# Kronos PCG Werkstatt (Kronos PCG Toolbox)

Free browser tool for Korg Kronos PCG files: add sound packs to your own PCG without overwriting anything, transfer Combis and Programs between files, rename, clean up and sort. Runs entirely in the browser (Mac, PC, iPad); your files never leave your device. German and English.

Kostenloses Browser-Werkzeug für Korg-Kronos-PCG-Dateien: Sound-Pakete in die eigene PCG einfügen, ohne etwas zu überschreiben, Combis und Programs übernehmen, umbenennen, aufräumen und sortieren. Läuft vollständig im Browser, die Dateien bleiben auf dem eigenen Gerät.

By [Thorsten Hillmann Keyboard-Sounds](https://keyboard-sounds.com).

## Based on PCG Tools

The core that reads and writes PCG files comes from [PCG Tools](https://github.com/DaBlick/PCG-Tools) by Michel Keijzers and is licensed under the LGPL-3.0 (see `LICENSE` and `vendor/pcgtools/LICENSE`). Changes to that core are in `vendor/pcgtools/`; everything under `src/` builds on it.

## Principles

- Input files are only read. The result is always a new file.
- Every result is checked before saving and again after reading the new file back.

## Layout

| Folder | Content |
|---|---|
| `vendor/pcgtools/` | Core of PCG Tools (Michel Keijzers, LGPL-3.0), with small adaptations to run without WPF |
| `src/PcgCore/` | Project for the core on .NET 8, replacements for Windows dialogs (`Shims/`), the toolbox's functions (`Tools/`) |
| `src/PcgCli/` | Command line tool `pcg` |
| `src/PcgWeb/` | The toolbox (Blazor WebAssembly) |
| `tools/` | Build scripts |

## Build

Requires the .NET 8 SDK.

```sh
tools/publish-site.sh site        # static website in ./site
python3 tools/build-offline.py <wwwroot> werkstatt.html   # single HTML file for offline use
dotnet run --project src/PcgCli -- info my.PCG             # command line
```

KORG, KRONOS and NAUTILUS are trademarks of KORG Inc. This is an independent project, not made or endorsed by KORG.
