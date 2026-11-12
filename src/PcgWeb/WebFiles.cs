using System.IO.Compression;
using KronosPcg.Tools;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using static KronosPcg.Lang;

namespace PcgWeb;

/// <summary>Dateien im Browser: PCG mit Fortschritt einlesen und Ergebnisse zum Sichern anbieten (gemeinsam für alle Reiter).</summary>
public static class WebFiles
{
    private const long MaxFileSize = 200L * 1024 * 1024;
    private static int _counter;

    /// <summary>
    /// Höchstlänge aller Namen, die die Werkstatt vergibt (PCG, KSC, Sample-Ordner; ohne Endung). Der Kronos verträgt
    /// höchstens 76 Zeichen für den ganzen Pfad einer Datei; mit 16 Zeichen bleibt auch für Samples in Unterordnern Luft.
    /// </summary>
    public const int NameLimit = 16;

    /// <summary>Name aus Grundname und Zusatz, zusammen höchstens <see cref="NameLimit"/> Zeichen (gekürzt wird der Grundname).</summary>
    public static string ShortName(string baseName, string suffix = "")
    {
        baseName = (baseName ?? "").Trim();
        var room = Math.Max(NameLimit - suffix.Length, 1);
        return (baseName.Length > room ? baseName[..room].TrimEnd() : baseName) + suffix;
    }

    /// <summary>Neuer, noch nicht vorhandener Pfad im Speicher der Seite.</summary>
    public static string NewPath(string prefix) => $"/tmp/{prefix}-{++_counter}.PCG";

    /// <summary>
    /// Liest die Datei mit Fortschrittsanzeige ein und wertet sie aus. slot.Pcg bleibt bis zum Ende stehen: Würde die
    /// Ansicht vorher umschalten, verschwände der Dateiknopf, über den der Browser die Datei gerade liest.
    /// </summary>
    public static async Task LoadAsync(PcgSlot slot, IBrowserFile file, Action refresh, IJSRuntime js)
    {
        slot.Error = null;
        slot.Progress = 0;
        slot.Status = T($"{file.Name} wird gelesen …", $"Reading {file.Name} …");
        refresh();

        var path = NewPath("pcg");
        try
        {
            if (file.Size > MaxFileSize)
            {
                throw new InvalidDataException(T("Die Datei ist zu groß für eine PCG.", "The file is too large for a PCG."));
            }

            await using (var input = file.OpenReadStream(MaxFileSize))
            await using (var output = File.Create(path))
            {
                var buffer = new byte[1024 * 1024];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    var percent = (int)(done * 100 / Math.Max(file.Size, 1));
                    if (percent != slot.Progress)
                    {
                        slot.Progress = percent;
                        refresh();
                    }
                }
            }

            slot.Status = T("PCG wird ausgewertet …", "Analyzing the PCG …");
            slot.Progress = -1;
            refresh();
            await js.InvokeVoidAsync("pcgPaint");

            slot.Pcg = PcgFiles.Load(path);
            slot.Path = path;
            slot.Name = file.Name;
        }
        catch (Exception exception)
        {
            slot.Error = T($"{file.Name} konnte nicht gelesen werden: {exception.Message}", $"{file.Name} could not be read: {exception.Message}");
        }
        finally
        {
            slot.Status = null;
        }
    }

    /// <summary>Kopiert eine gewählte Datei in den Speicher der Seite (ohne sie auszuwerten).</summary>
    public static async Task CopyAsync(IBrowserFile file, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var input = file.OpenReadStream(MaxFileSize);
        await using var output = File.Create(path);
        await input.CopyToAsync(output);
    }

    /// <summary>Eine Sample-Bank aus einer Auswahl: ihre KSC liegt (mit der _UserBank.KSC, wenn vorhanden) in einem eigenen Ordner im Speicher der Seite.</summary>
    /// <param name="Folder">Ordner der KSC innerhalb der Auswahl; der Sample-Ordner der Bank liegt darin unter dem Namen der Bank.</param>
    public sealed record PickedBank(string Name, string KscPath, string Folder, string Id, int Multisamples, bool HasUserBank);

    private static bool HasExtension(string name, string extension) =>
        name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(name).StartsWith("._");

    /// <summary>Liest alle KSC-Dateien einer Auswahl (kleine Textdateien) und liefert die Sample-Bänke darin, die größte zuerst.</summary>
    public static async Task<List<PickedBank>> ReadBanksAsync(PickedFiles picked, string dir)
    {
        var folders = new Dictionary<string, string>();
        foreach (var (file, path) in picked.All.Where(item => HasExtension(item.Path, ".KSC")))
        {
            var folder = PickedFiles.Folder(path);
            if (!folders.TryGetValue(folder, out var target))
            {
                folders[folder] = target = Path.Combine(dir, "b" + folders.Count);
            }

            await CopyAsync(file, Path.Combine(target, file.Name));
        }

        var banks = new List<PickedBank>();
        foreach (var (folder, target) in folders)
        {
            foreach (var ksc in Directory.GetFiles(target).Where(path => !path.EndsWith("_UserBank.KSC", StringComparison.OrdinalIgnoreCase)))
            {
                if (SampleAttach.BankInfo(ksc) is { } info)
                {
                    var name = Path.GetFileNameWithoutExtension(ksc);
                    banks.Add(new PickedBank(name, ksc, folder, info.Id, info.Multisamples, File.Exists(Path.Combine(target, name + "_UserBank.KSC"))));
                }
            }
        }

        return banks.OrderByDescending(bank => bank.Multisamples).ToList();
    }

    /// <summary>
    /// Multisample- und Sample-Dateien einer Bank aus der Auswahl, mit ihrem Pfad innerhalb des Sample-Ordners der Bank.
    /// Leer, wenn die Auswahl keine Ordner kennt (einzeln gewählte Dateien): Dann ist nicht sicher, was wohin gehört.
    /// </summary>
    public static List<(IBrowserFile File, string Rel)> BankSamples(PickedFiles picked, PickedBank bank)
    {
        if (!picked.Paths.Any(path => path.Contains('/')))
        {
            return new List<(IBrowserFile, string)>();
        }

        var prefix = (bank.Folder.Length > 0 ? bank.Folder + "/" : "") + bank.Name + "/";
        return picked.All.Where(item => (HasExtension(item.Path, ".KMP") || HasExtension(item.Path, ".KSF")) &&
                                        item.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(item => (item.File, item.Path[prefix.Length..])).ToList();
    }

    /// <summary>Name nur aus Zeichen, die der Kronos in Dateinamen sicher verträgt, und höchstens <see cref="NameLimit"/> lang.</summary>
    public static string SafeName(string name)
    {
        var kept = new string((name ?? "").Select(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '+' ? c : ' ').ToArray());
        var clean = string.Join(" ", kept.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return ShortName(clean.Length == 0 ? T("Paket", "Pack") : clean);
    }

    /// <summary>
    /// Sound-Paket als ZIP: entpackt es in einen eigenen Ordner und lädt die eine PCG darin. Liefert den Pfad der KSC
    /// der Sample-Bank des Pakets (oder null, wenn keine dabei ist). Ordner im ZIP werden beibehalten, weil die KSC
    /// ihre Samples im Ordner gleichen Namens erwartet.
    /// </summary>
    public static async Task<string> LoadZipPackAsync(PcgSlot slot, IBrowserFile file, Action refresh, IJSRuntime js)
    {
        slot.Error = null;
        slot.Progress = -1;
        slot.Status = T($"{file.Name} wird gelesen …", $"Reading {file.Name} …");
        refresh();
        string ksc = null;
        try
        {
            var dir = $"/tmp/paket-{++_counter}";
            var zipPath = dir + ".zip";
            await CopyAsync(file, zipPath);
            slot.Status = T("Paket wird entpackt …", "Unpacking the pack …");
            refresh();
            await js.InvokeVoidAsync("pcgPaint");

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var entries = zip.Entries.Where(entry => entry.Name.Length > 0 && !entry.FullName.StartsWith("__MACOSX") && !entry.Name.StartsWith("._")).ToList();
                if (entries.Count > 5000 || entries.Sum(entry => entry.Length) > MaxFileSize * 2)
                {
                    throw new InvalidDataException(T("Das ZIP ist zu groß für ein Sound-Paket.", "The ZIP is too large for a sound pack."));
                }

                foreach (var entry in entries)
                {
                    var target = Path.GetFullPath(Path.Combine(dir, entry.FullName));
                    if (!target.StartsWith(dir + "/"))
                    {
                        throw new InvalidDataException(T("Das ZIP enthält ungültige Pfade.", "The ZIP contains invalid paths."));
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, true);
                }
            }

            File.Delete(zipPath);
            var pcgs = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Where(path => path.EndsWith(".PCG", StringComparison.OrdinalIgnoreCase)).ToList();
            if (pcgs.Count != 1)
            {
                throw new InvalidDataException(pcgs.Count == 0
                    ? T("Im ZIP ist keine PCG.", "There is no PCG in the ZIP.")
                    : T("Im ZIP sind mehrere PCGs. Ein Sound-Paket hat genau eine.", "There are several PCGs in the ZIP. A sound pack has exactly one."));
            }

            slot.Status = T("PCG wird ausgewertet …", "Analyzing the PCG …");
            refresh();
            await js.InvokeVoidAsync("pcgPaint");
            slot.Pcg = PcgFiles.Load(pcgs[0]);
            slot.Path = pcgs[0];
            slot.Name = file.Name;
            ksc = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".KSC", StringComparison.OrdinalIgnoreCase) && !path.EndsWith("_UserBank.KSC", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(path => SampleAttach.BankInfo(path) != null);
        }
        catch (Exception exception)
        {
            slot.Error = T($"{file.Name} konnte nicht gelesen werden: {exception.Message}", $"{file.Name} could not be read: {exception.Message}");
        }
        finally
        {
            slot.Status = null;
        }

        return ksc;
    }

    /// <summary>Fertiges Paket (PCG, KSC-Dateien, Sample-Ordner) als ein ZIP zum Sichern anbieten.</summary>
    /// <param name="files">Pfad im ZIP → Datei im Speicher der Seite</param>
    public static async Task<string> SaveZipAsync(IJSRuntime js, IReadOnlyList<(string Entry, string Path)> files, string wantedName)
    {
        var name = string.Concat((wantedName ?? "paket").Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '/' ? '_' : c));
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var (entryName, path) in files)
            {
                var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
                await using var entryStream = entry.Open();
                await using var input = File.OpenRead(path);
                await input.CopyToAsync(entryStream);
            }
        }

        var status = await js.InvokeAsync<string>("pcgSave", name + ".zip", stream.ToArray());
        return status switch
        {
            "saved" => T($"Gesichert als {name}.zip (Download-Ordner). Entpacken und den Inhalt auf den Stick kopieren.", $"Saved as {name}.zip (Downloads folder). Unzip it and copy the contents to the USB stick."),
            "declined" => T("Nicht gesichert.", "Not saved."),
            _ => T($"Sichern hat nicht geklappt ({status}).", $"Saving did not work ({status})."),
        };
    }

    /// <summary>
    /// Bietet die neue PCG zum Sichern an. Im claude.ai-Viewer als ZIP (der Viewer erlaubt kein .PCG), sonst
    /// (lokale Werkstatt, eigene Adresse) direkt als .PCG. Liefert einen Satz für die Anzeige.
    /// </summary>
    public static async Task<string> SaveAsync(IJSRuntime js, string pcgPath, string wantedName)
    {
        var name = string.Concat((wantedName ?? "ergebnis").Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '/' ? '_' : c));
        if (!name.EndsWith(".PCG", StringComparison.OrdinalIgnoreCase))
        {
            name += ".PCG";
        }

        if (name.Length - 4 > NameLimit)
        {
            return T($"Der Name ist zu lang: höchstens {NameLimit} Zeichen, sonst findet der Kronos die Datei nicht.", $"The name is too long: {NameLimit} characters at most, otherwise the Kronos cannot find the file.");
        }

        var asZip = await js.InvokeAsync<bool>("pcgNeedsZip");
        var bytes = await File.ReadAllBytesAsync(pcgPath);
        if (asZip)
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
                await using var entryStream = entry.Open();
                await entryStream.WriteAsync(bytes);
            }

            bytes = stream.ToArray();
        }

        var status = await js.InvokeAsync<string>("pcgSave", asZip ? Path.GetFileNameWithoutExtension(name) + ".zip" : name, bytes);
        return status switch
        {
            "saved" when asZip => T("Gesichert. In der ZIP liegt die PCG.", "Saved. The PCG is inside the ZIP."),
            "saved" => T($"Gesichert als {name} (Download-Ordner).", $"Saved as {name} (Downloads folder)."),
            "declined" => T("Nicht gesichert.", "Not saved."),
            _ => T($"Sichern hat nicht geklappt ({status}).", $"Saving did not work ({status})."),
        };
    }
}
