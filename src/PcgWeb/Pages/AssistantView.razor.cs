using KronosPcg.Tools;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using PcgTools.Model.Common.Synth.MemoryAndFactory;
using PcgTools.Model.Common.Synth.Meta;
using PcgTools.Model.Common.Synth.PatchCombis;
using PcgTools.Model.Common.Synth.PatchPrograms;
using static KronosPcg.Lang;

namespace PcgWeb.Pages;

/// <summary>
/// Einfach-Modus für Kunden: ein Sound-Paket in die eigene PCG einfügen, in vier Schritten. Technik wie im Profi-Reiter
/// (CopyService mit Vorschau, Prüfung vor und nach dem Speichern), aber alle Entscheidungen sind vorbelegt.
/// </summary>
public partial class AssistantView
{
    private const string CombiKind = "Combi";
    private static readonly string[] ProgramKinds = { "HD-1", "EXi" };
    /// <summary>Namen der vier Schritte in der gewählten Sprache.</summary>
    private static string[] Steps => new[] { T("Sound-Paket", "Sound pack"), T("Deine PCG", "Your PCG"), T("Platz", "Slots"), T("Fertig", "Done") };

    private sealed record Check(bool Ok, string Text, string Detail = null);

    [Inject] private IJSRuntime JS { get; set; }

    private int _step = 1;
    private PcgSlot _pack = new();
    private PcgSlot _mine = new();
    private PackInfo _info;
    private string _packError;
    private string _mineError;

    // Samples: KSC der Sample-Bank im Paket (nur bei einem ZIP-Paket) und, wenn der Kunde eine hat, seine eigene Bank.
    private const long MaxOwnSampleBytes = 200L * 1024 * 1024;
    private string _packKsc;
    private SampleAttach.PackSamples _packSamples;
    private OwnBank _ownBank;
    private bool _wantOwnBank;
    private string _ownStatus;
    private string _ownError;
    private SampleAttachResult _attached;
    private List<(string Entry, string Path)> _zipFiles;

    /// <summary>Die Sample-Bank des Kunden im Speicher der Seite: KSC, _UserBank.KSC und, soweit gewählt, der Sample-Ordner.</summary>
    /// <param name="Complete">Alle Samples der Bank sind dabei: Das Ergebnis ist ein vollständiges Paket.</param>
    private sealed record OwnBank(string Dir, string KscPath, string Name, int Multisamples, bool Complete, int Files, string Note);

    // Platzwahl je Art (Combi, HD-1, EXi): gewählte Bank und optional ein angetippter Startplatz.
    private readonly Dictionary<string, string> _bank = new();
    private readonly Dictionary<string, string> _start = new();
    private string _choose;
    private int _page;
    private bool _rename = true;

    private CopyResult _preview;
    private CopyException _previewError;
    private bool _previewBusy;
    private CancellationTokenSource _previewDelay;

    private string _busy;
    private CopyException _error;
    private CopyResult _result;
    private string _resultPath;
    private string _outName;
    private string _saveStatus;

    // ---------- Schritt 1: Paket ----------

    private async Task LoadPack(IBrowserFile file)
    {
        _info = null;
        _packError = null;
        _packKsc = null;
        _packSamples = null;
        if (file.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            _packKsc = await WebFiles.LoadZipPackAsync(_pack, file, StateHasChanged, JS);
        }
        else
        {
            await WebFiles.LoadAsync(_pack, file, StateHasChanged, JS);
        }

        if (_pack.Pcg == null)
        {
            return;
        }

        _busy = T("Paket wird geprüft …", "Checking the pack …");
        StateHasChanged();
        await JS.InvokeVoidAsync("pcgPaint");
        try
        {
            _info = PackCheck.Analyze(_pack.Pcg);
            _allCombis = _info.Combis.Select(combi => (combi.Id, combi.Name.Trim())).ToList();
            _chosen = new HashSet<string>(_allCombis.Select(combi => combi.Id));
            if (_info.Combis.Count == 0 && _info.Programs.Count == 0)
            {
                _packError = T("In dieser Datei sind keine Sounds.", "There are no sounds in this file.");
                _info = null;
            }
            else if (_info.UserSamples && _packKsc != null)
            {
                _packSamples = SampleAttach.Inspect(_info.Programs, _packKsc);
            }
        }
        catch (Exception exception)
        {
            _packError = T($"Die Datei konnte nicht geprüft werden: {exception.Message}", $"The file could not be checked: {exception.Message}");
        }
        finally
        {
            _busy = null;
        }

        CheckFiles();
    }

    // Auswahl bei Paketen mit mehreren Combis: Alles ist angehakt, einzelne Combis lassen sich abwählen.
    private List<(string Id, string Name)> _allCombis = new();
    private HashSet<string> _chosen = new();

    private void ToggleCombi(string id)
    {
        if (!_chosen.Remove(id))
        {
            _chosen.Add(id);
        }

        ChosenChanged();
    }

    private void ChooseAll(bool all)
    {
        _chosen = all ? new HashSet<string>(_allCombis.Select(combi => combi.Id)) : new HashSet<string>();
        ChosenChanged();
    }

    /// <summary>Paket neu auswerten: Sounds, Samples und Hinweise gelten nur für die angehakten Combis.</summary>
    private void ChosenChanged()
    {
        try
        {
            _info = PackCheck.Analyze(_pack.Pcg, _chosen);
            _packSamples = _info.UserSamples && _packKsc != null ? SampleAttach.Inspect(_info.Programs, _packKsc) : null;
            CheckFiles();
        }
        catch (Exception exception)
        {
            _packError = T($"Die Datei konnte nicht geprüft werden: {exception.Message}", $"The file could not be checked: {exception.Message}");
        }
    }

    private bool PackReady => _info != null && (_allCombis.Count == 0 || _chosen.Count > 0);

    private string PackTitle => _info == null ? "" :
        _info.Combis.Count == 1 ? _info.Combis[0].Name.Trim() :
        _info.Combis.Count > 1 ? $"{_info.Combis.Count} Combis" :
        _info.Programs.Count == 1 ? _info.Programs[0].Name.Trim() : T($"{_info.Programs.Count} Sounds", $"{_info.Programs.Count} sounds");

    private string PackMeta()
    {
        var combis = _info.Combis.Count switch { 0 => "", 1 => "1 Combi · ", var n => $"{n} Combis · " };
        var sounds = _info.Programs.Count == 1 ? T("1 Sound", "1 sound") : T($"{_info.Programs.Count} Sounds", $"{_info.Programs.Count} sounds");
        return combis + sounds;
    }

    private IEnumerable<Check> PackChecks()
    {
        if (!_info.UserSamples)
        {
            yield return new Check(true, T("Keine eigenen Samples nötig", "No extra samples needed"));
        }
        else if (_packSamples == null)
        {
            yield return new Check(false, T("Braucht eigene Samples", "Needs its own samples"),
                T("Die Samples stecken nicht in der PCG. Wähle statt der PCG das ganze Paket als ZIP, dann nimmt die Werkstatt die Samples gleich mit. " +
                  "Sonst lade sie vorher am Kronos (Paket-Ordner mit der .KSC-Datei).",
                  "The samples are not inside the PCG. Choose the whole pack as a ZIP instead of the PCG, then the toolbox takes the samples along. " +
                  "Otherwise load them on the Kronos beforehand (pack folder with the .KSC file)."));
        }
        else
        {
            if (_packSamples.Files > 0)
            {
                yield return new Check(true,
                    T($"{_packSamples.Files} {(_packSamples.Files == 1 ? "Sample liegt" : "Samples liegen")} dem Paket bei ({Megabytes(_packSamples.Bytes)})",
                      $"{_packSamples.Files} {(_packSamples.Files == 1 ? "sample comes" : "samples come")} with the pack ({Megabytes(_packSamples.Bytes)})"),
                    string.Join(", ", _packSamples.Multisamples));
            }

            if (_packSamples.Missing.Count + _packSamples.OtherBanks.Count > 0)
            {
                yield return new Check(false, T("Im Paket fehlen Samples", "Samples are missing from the pack"),
                    string.Join("; ", _packSamples.Missing.Concat(_packSamples.OtherBanks).Take(4)) +
                    T(". Diese Sounds klingen anders oder bleiben stumm.", ". These sounds will sound different or stay silent."));
            }
        }

        yield return _info.Exs.Count > 0
            ? new Check(false,
                T($"Braucht {(_info.Exs.Count == 1 ? "die Library" : "die Libraries")} {string.Join(", ", _info.Exs)}",
                  $"Needs the {(_info.Exs.Count == 1 ? "library" : "libraries")} {string.Join(", ", _info.Exs)}"),
                T("Ist sie auf deinem Kronos nicht installiert, klingen einzelne Sounds anders. Welche EXs installiert sind, siehst du am Kronos in der Liste der installierten Libraries.",
                  "If it is not installed on your Kronos, some sounds will sound different. You can see which EXs are installed in the list of installed libraries on the Kronos."))
            : new Check(true, T("Keine Zusatz-Library (EXs) nötig", "No add-on library (EXs) needed"));
    }

    /// <summary>Das Paket bringt Samples mit, die beim Einfügen mitgenommen werden.</summary>
    private bool HasSamples => _packSamples is { Files: > 0 };

    /// <summary>Größe in KB bzw. MB; im Deutschen mit Dezimalkomma, im Englischen mit Punkt.</summary>
    private static string Megabytes(long bytes) =>
        bytes < 1024 * 1024 ? $"{Math.Max(bytes / 1024, 1)} KB"
        : English ? $"{bytes / 1024.0 / 1024.0:0.0} MB"
        : $"{bytes / 1024.0 / 1024.0:0.0} MB".Replace('.', ',');

    private void ResetPack()
    {
        _pack = new PcgSlot();
        _packKsc = null;
        _packSamples = null;
        _allCombis = new();
        _chosen = new();
        _info = null;
        _packError = null;
        _mineError = null;
    }

    // ---------- Schritt 2: eigene PCG ----------

    private async Task LoadMine(IBrowserFile file)
    {
        _mineError = null;
        await WebFiles.LoadAsync(_mine, file, StateHasChanged, JS);
        CheckFiles();
    }

    private void ResetMine()
    {
        _mine = new PcgSlot();
        _mineError = null;
        ResetOwnBank();
    }

    // ---------- Schritt 2b: eigene Sample-Bank ----------

    private void ResetOwnBank()
    {
        _sampleBankDir = null;
        _sampleBankName = null;
        _ownBank = null;
        _ownError = null;
        _ownStatus = null;
    }

    private void SetWantOwnBank(bool value)
    {
        _wantOwnBank = value;
        if (!value)
        {
            ResetOwnBank();
        }
    }

    /// <summary>
    /// Liest die Sample-Bank des Kunden: mindestens die KSC und die _UserBank.KSC (zwei kleine Textdateien). Sind auch
    /// die Multisamples und Samples dabei (ganzer Ordner gewählt), wird das Ergebnis ein vollständiges Paket.
    /// </summary>
    private async Task LoadOwnBank(PickedFiles picked)
    {
        ResetOwnBank();
        try
        {
            var banks = await WebFiles.ReadBanksAsync(picked, WebFiles.NewPath("bank")[..^4]);
            if (banks.Count == 0)
            {
                _ownError = T("Darin ist keine KSC einer eigenen Sample-Bank. Wähle den Ordner (oder die Dateien), in dem deine .KSC und die „…_UserBank.KSC“ liegen.",
                    "There is no KSC of a sample bank of your own in there. Choose the folder (or the files) that holds your .KSC and the “…_UserBank.KSC”.");
                return;
            }

            var bank = banks[0];
            if (!bank.HasUserBank)
            {
                _ownError = T($"Zu „{bank.Name}.KSC“ fehlt die Datei „{bank.Name}_UserBank.KSC“. Der Kronos legt sie beim Speichern daneben. Bitte beide wählen.",
                    $"The file “{bank.Name}_UserBank.KSC” that belongs to “{bank.Name}.KSC” is missing. The Kronos puts it next to the KSC when saving. Please choose both.");
                return;
            }

            var note = banks.Count > 1
                ? T($"Mehrere Sample-Bänke gefunden, genommen wird „{bank.Name}“ (die größte). ", $"Found several sample banks; using “{bank.Name}” (the largest). ")
                : "";
            var samples = WebFiles.BankSamples(picked, bank);
            var complete = false;
            if (samples.Count == 0)
            {
                note += T("Ohne Sample-Ordner gewählt: Du bekommst die neuen Dateien zum Dazukopieren in deinen Sample-Ordner.",
                          "Chosen without the sample folder: you get the new files to copy into your sample folder.");
            }
            else if (samples.Sum(sample => sample.File.Size) > MaxOwnSampleBytes)
            {
                note += T($"Dein Sample-Ordner ist groß ({Megabytes(samples.Sum(sample => sample.File.Size))}). Du bekommst deshalb nur die neuen Dateien zum Dazukopieren.",
                          $"Your sample folder is large ({Megabytes(samples.Sum(sample => sample.File.Size))}). So you only get the new files to copy into it.");
            }
            else
            {
                var bankDir = Path.Combine(Path.GetDirectoryName(bank.KscPath)!, bank.Name);
                for (var index = 0; index < samples.Count; index++)
                {
                    if (index % 5 == 0)
                    {
                        _ownStatus = T($"Deine Samples werden gelesen … {index + 1}/{samples.Count}", $"Reading your samples … {index + 1}/{samples.Count}");
                        StateHasChanged();
                    }

                    await WebFiles.CopyAsync(samples[index].File, Path.Combine(bankDir, samples[index].Rel));
                }

                complete = true;
                note += T($"{samples.Count} Sample-Dateien gelesen: Du bekommst am Ende ein vollständiges Paket.",
                          $"Read {samples.Count} sample files: you get a complete package at the end.");
            }

            _ownBank = new OwnBank(Path.GetDirectoryName(bank.KscPath), bank.KscPath, bank.Name, bank.Multisamples, complete, complete ? samples.Count : 0, note);
        }
        catch (Exception exception)
        {
            _ownError = T($"Die Dateien konnten nicht gelesen werden: {exception.Message}", $"The files could not be read: {exception.Message}");
        }
        finally
        {
            _ownStatus = null;
        }
    }

    /// <summary>Gleiche Datei zweimal oder unterschiedliche Kronos-Versionen abfangen.</summary>
    private void CheckFiles()
    {
        _mineError = null;
        if (_pack.Pcg == null || _mine.Pcg == null)
        {
            return;
        }

        if (new FileInfo(_pack.Path).Length == new FileInfo(_mine.Path).Length &&
            File.ReadAllBytes(_pack.Path).AsSpan().SequenceEqual(File.ReadAllBytes(_mine.Path)))
        {
            _mineError = T("Das ist dieselbe Datei wie das Sound-Paket. Wähle hier deine eigene PCG mit deinen Sounds.",
                           "This is the same file as the sound pack. Choose your own PCG with your sounds here.");
            return;
        }

        try
        {
            PcgFiles.CheckSameModel(_pack.Pcg, _mine.Pcg);
        }
        catch (Exception exception)
        {
            _mineError = exception.Message;
            return;
        }

        // Teil-PCG ohne Wave-Sequence-Bank, das Paket bringt aber eigene Wave Sequences mit: gleich hier sagen.
        var ownSequences = _info?.Programs.SelectMany(program => program.UsedWaveSequences)
            .Where(sequence => sequence != null && !WaveSequenceCopier.IsFactory(sequence)).Distinct().Count() ?? 0;
        if (ownSequences > 0 && !Writable(_mine.Pcg.WaveSequenceBanks).Any())
        {
            _mineError = T($"Diese PCG enthält keine Wave-Sequence-Bank, das Paket braucht aber {ownSequences} eigene Wave Sequence(s). " +
                           "Sie ist vermutlich eine Teil-PCG mit nur einzelnen Bänken. Nimm deine vollständige PCG: am Kronos Disk → Save → „Save PCG“ mit allen Bänken.",
                           $"This PCG has no Wave Sequence bank, but the pack needs {ownSequences} Wave Sequence(s) of its own. " +
                           "It is probably a partial PCG with only some banks. Use your complete PCG: on the Kronos Disk → Save → “Save PCG” with all banks.");
        }
    }

    private bool MineReady => _mine.Pcg != null && _mineError == null && _ownStatus == null &&
                              (!HasSamples || !_wantOwnBank || _ownBank != null);

    private string MineMeta()
    {
        var combis = Loaded(_mine.Pcg.CombiBanks).Count(patch => !patch.IsEmptyOrInit);
        var programs = Loaded(_mine.Pcg.ProgramBanks).Count(patch => !patch.IsEmptyOrInit && !IsGm((IBank)patch.Parent));
        return $"{Number(combis)} Combis · {Number(programs)} Programs";
    }

    // ---------- Schritt 3: Platz ----------

    /// <summary>Wie viele Sounds je Art ungefähr kommen (vor der Vorschau; schon vorhandene werden später mitgenutzt).</summary>
    private int Needed(string kind) => kind == CombiKind
        ? _info.Combis.Count
        : _info.Programs.Count(program => CopyService.SynthesisType((IProgramBank)program.Parent) == kind);

    private IEnumerable<string> Kinds() =>
        new[] { CombiKind }.Concat(ProgramKinds).Where(kind => Needed(kind) > 0);

    /// <summary>
    /// Vorschlag je Art: dieselbe Bank wie im Paket (bei den Einzelsound-Paketen U-G, U-EE, U-FF), wenn sie in der eigenen
    /// PCG genug Platz hat; sonst die erste User-Bank mit genug freien Plätzen, notfalls irgendeine mit freien Plätzen.
    /// </summary>
    private void SuggestPlaces()
    {
        _shortage.Clear();
        _overwrite.Clear();
        _unusedMine = null;
        _bank.Clear();
        _start.Clear();
        _choose = null;
        foreach (var kind in Kinds())
        {
            var banks = BanksOf(_mine.Pcg, kind).ToList();
            var needed = Needed(kind);
            var packBank = (kind == CombiKind ? _info.Combis.Cast<IPatch>() : _info.Programs.Where(program =>
                    CopyService.SynthesisType((IProgramBank)program.Parent) == kind))
                .Select(patch => ((IBank)patch.Parent).Id).GroupBy(id => id).OrderByDescending(group => group.Count())
                .Select(group => group.Key).FirstOrDefault();
            var bank = banks.FirstOrDefault(b => b.Id == packBank && Free(b) >= needed)
                       ?? banks.FirstOrDefault(b => b.Id.StartsWith("U-") && Free(b) >= needed)
                       ?? banks.FirstOrDefault(b => Free(b) > 0);
            if (bank != null)
            {
                _bank[kind] = bank.Id;
            }
        }
    }

    /// <summary>Beschreibbare Bänke einer Art in Dateireihenfolge (für Programs nur der passende Typ, ohne GM).</summary>
    private static IEnumerable<IBank> BanksOf(IPcgMemory pcg, string kind) => kind == CombiKind
        ? Writable(pcg.CombiBanks)
        : Writable(pcg.ProgramBanks).Where(bank => !IsGm(bank) && CopyService.SynthesisType((IProgramBank)bank) == kind);

    /// <summary>Gewählte Bank und danach die folgenden User-Bänke derselben Art, falls sie voll wird.</summary>
    private List<string> Chain(string kind)
    {
        var list = BanksOf(_mine.Pcg, kind).ToList();
        var index = list.FindIndex(bank => bank.Id == _bank.GetValueOrDefault(kind));
        return index < 0
            ? new List<string>()
            : list.Skip(index).Where((bank, i) => i == 0 || bank.Id.StartsWith("U-")).Select(bank => bank.Id).ToList();
    }

    private CopyRequest BuildRequest(string outPath)
    {
        var renames = new Dictionary<string, string>();
        if (_rename)
        {
            foreach (var combi in _info.Combis)
            {
                foreach (var (programId, name) in ProgramNaming.ForCombi(combi))
                {
                    renames["P:" + programId] = name;
                }
            }
        }

        var waveBanks = Writable(_mine.Pcg.WaveSequenceBanks).ToList();
        var waveStart = waveBanks.FirstOrDefault(bank => bank.Id == "U-AA" && Free(bank) > 0) ??
                        waveBanks.FirstOrDefault(bank => bank.Id.StartsWith("U-") && Free(bank) > 0);
        return new CopyRequest
        {
            SourceFile = _pack.Path,
            TargetFile = _mine.Path,
            OutFile = outPath,
            Combis = _info.Combis.Select(combi => combi.Id).ToList(),
            Programs = _info.Combis.Count == 0 ? _info.Programs.Select(program => program.Id).ToList() : new List<string>(),
            CombiBanks = Chain(CombiKind),
            ProgramBanks = ProgramKinds.SelectMany(Chain).ToList(),
            WaveSequenceBanks = waveStart == null ? new List<string>() : waveBanks.SkipWhile(bank => bank != waveStart)
                .Where(bank => bank.Id.StartsWith("U-")).Select(bank => bank.Id).ToList(),
            CombiStart = _start.GetValueOrDefault(CombiKind),
            ProgramStarts = ProgramKinds.Select(kind => _start.GetValueOrDefault(kind)).Where(id => id != null).ToList(),
            RenamesBySource = renames,
            // Sounds mit Samples nie mit einem gleich aussehenden Sound im Ziel zusammenlegen: Gleiche Bytes können dort
            // auf ein anderes Sample zeigen (gleiche Nummer in einer anderen Sample-Bank).
            AlwaysCopyPrograms = HasSamples,
            // Bestätigt: ungenutzte Sounds überschreiben, weil sonst kein Platz ist.
            Deletes = _overwrite.SelectMany(OverwriteList).Select(program => "P:" + program.Id).ToList(),
        };
    }

    /// <summary>Probelauf des echten Einfügens ohne Speichern; danach stehen die genauen Plätze fest.</summary>
    private async void SchedulePreview()
    {
        _previewDelay?.Cancel();
        var delay = _previewDelay = new CancellationTokenSource();
        _previewError = null;
        try
        {
            await Task.Delay(250, delay.Token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        _previewBusy = true;
        StateHasChanged();
        await JS.InvokeVoidAsync("pcgPaint");
        if (delay.IsCancellationRequested)
        {
            return;
        }

        try
        {
            _preview = Needed(CombiKind) > 0 && !_bank.ContainsKey(CombiKind)
                ? throw new CopyException(T("In deiner PCG ist kein Platz mehr für Combis.", "There is no free slot left for Combis in your PCG."))
                : CopyService.Preview(BuildRequest(null), _pack.Pcg);
            foreach (var kind in ProgramKinds.Where(kind => !_overwrite.Contains(kind)))
            {
                _shortage.Remove(kind);
            }
        }
        catch (CopyException exception)
        {
            _preview = null;
            _previewError = exception;

            // Kein Platz für Sounds einer Art: merken, wie viele fehlen. Dann bietet Schritt 3 an, ungenutzte Sounds zu überschreiben.
            foreach (var kind in ProgramKinds.Where(kind => !_overwrite.Contains(kind)))
            {
                var count = exception.MissingByType.GetValueOrDefault(kind);
                if (count > 0)
                {
                    _shortage[kind] = count;
                }
                else
                {
                    _shortage.Remove(kind);
                }
            }
        }
        catch (Exception exception)
        {
            _preview = null;
            _previewError = new CopyException(exception.Message);
        }
        finally
        {
            if (!delay.IsCancellationRequested)
            {
                _previewBusy = false;
                StateHasChanged();
            }
        }
    }

    // ---------- Kein Platz: ungenutzte Sounds überschreiben ----------

    // Fehlende Plätze je Art laut Vorschau, und die Arten, für die der Kunde das Überschreiben bestätigt hat.
    private readonly Dictionary<string, int> _shortage = new();
    private readonly HashSet<string> _overwrite = new();
    private List<IProgram> _unusedMine;

    /// <summary>User-Bänke einer Art in Dateireihenfolge (dort wird überschrieben, nie in den Werks-Bänken).</summary>
    private List<IBank> UserBanksOf(string kind) => BanksOf(_mine.Pcg, kind).Where(bank => bank.Id.StartsWith("U-")).ToList();

    /// <summary>Die Sounds, die für diese Art überschrieben würden: so viele ungenutzte wie fehlen, aus den User-Bänken der Reihe nach.</summary>
    private List<IProgram> OverwriteList(string kind)
    {
        _unusedMine ??= Housekeeping.UnusedPrograms(_mine.Pcg);
        var banks = UserBanksOf(kind);
        return _unusedMine.Where(program => banks.Contains((IBank)program.Parent)).Take(_shortage.GetValueOrDefault(kind)).ToList();
    }

    private void SetOverwrite(string kind, bool value)
    {
        if (value)
        {
            _overwrite.Add(kind);
            // Ab der ersten User-Bank dieser Art ablegen: So sind alle frei werdenden Plätze erreichbar.
            var first = UserBanksOf(kind).FirstOrDefault();
            if (first != null)
            {
                _bank[kind] = first.Id;
                _start.Remove(kind);
            }
        }
        else
        {
            _overwrite.Remove(kind);
        }

        _choose = null;
        SchedulePreview();
    }

    /// <summary>Die Fehlermeldung "kein Platz" wird durch das Angebot ersetzt, solange es noch nicht angenommen ist.</summary>
    private bool OfferCoversError => _error == null && _previewError != null && _shortage.Count > 0 &&
                                     _shortage.Keys.Any(kind => !_overwrite.Contains(kind)) &&
                                     _shortage.All(pair => OverwriteList(pair.Key).Count >= pair.Value);

    /// <summary>Neue Plätze laut Vorschau je Art (nur neu eingefügte, keine mitgenutzten).</summary>
    private List<CopyMapping> Arriving(string kind) => _preview == null
        ? new List<CopyMapping>()
        : kind == CombiKind ? _preview.Combis : _preview.Programs.Where(program => !program.Reused && program.Type == kind).ToList();

    private int ReusedCount => _preview?.Programs.Count(program => program.Reused) ?? 0;

    /// <summary>"U-EE 000–005" bzw. "U-EE 120–127, U-GG 000–003", wenn eine Bank voll wird.</summary>
    private static string PlaceText(IEnumerable<string> ids)
    {
        var parts = ids.Select(id => (Bank: id[..^3], Slot: id[^3..])).GroupBy(place => place.Bank)
            .Select(group =>
            {
                var slots = group.Select(place => place.Slot).OrderBy(slot => slot, StringComparer.Ordinal).ToList();
                var contiguous = int.Parse(slots[^1]) - int.Parse(slots[0]) == slots.Count - 1;
                return slots.Count == 1 ? $"{group.Key} {slots[0]}"
                    : contiguous ? $"{group.Key} {slots[0]}–{slots[^1]}"
                    : slots.Count <= 4 ? $"{group.Key} {string.Join(", ", slots)}"
                    : $"{group.Key} {slots[0]} … {slots[^1]} {T("(freie Plätze)", "(free slots)")}";
            }).ToList();
        return parts.Count == 0 ? "–" : string.Join(", ", parts);
    }

    private void ToggleChooser(string kind)
    {
        _choose = _choose == kind ? null : kind;
        if (_choose != null)
        {
            var bank = _bank[kind];
            var first = _start.GetValueOrDefault(kind) ??
                        Arriving(kind).Select(mapping => mapping.To).FirstOrDefault(id => id.Length == bank.Length + 3 && id.StartsWith(bank));
            _page = first == null ? 0 : int.Parse(first[^3..]) / 16;
        }
    }

    private void ChooseBank(string kind, IBank bank)
    {
        _bank[kind] = bank.Id;
        _start.Remove(kind);
        var firstFree = bank.Patches.FirstOrDefault(patch => patch.IsEmptyOrInit);
        _page = firstFree == null ? 0 : firstFree.Index / 16;
        SchedulePreview();
    }

    private void ChooseSlot(string kind, IPatch patch)
    {
        _start[kind] = patch.Id;
        SchedulePreview();
    }

    /// <summary>Bank-Knöpfe wie am Kronos: Zeilen A–G, Spalten I-, U- (und bei Programs U-xx). Null = Lücke.</summary>
    private List<IBank> BankGrid(string kind)
    {
        IBanks banks = kind == CombiKind ? _mine.Pcg.CombiBanks : _mine.Pcg.ProgramBanks;
        var byId = banks.BankCollection.Where(bank => bank.IsLoaded && !IsGm(bank)).ToDictionary(bank => bank.Id);
        var grid = new List<IBank>();
        foreach (var letter in "ABCDEFG")
        {
            grid.Add(byId.GetValueOrDefault($"I-{letter}"));
            grid.Add(byId.GetValueOrDefault($"U-{letter}"));
            if (kind != CombiKind)
            {
                grid.Add(byId.GetValueOrDefault($"U-{letter}{letter}"));
            }
        }

        return grid;
    }

    private static bool Selectable(string kind, IBank bank) =>
        bank != null && bank.IsWritable && BanksOfKindMatch(kind, bank) && Free(bank) > 0;

    private static bool BanksOfKindMatch(string kind, IBank bank) =>
        kind == CombiKind ? bank is ICombiBank : bank is IProgramBank programBank && CopyService.SynthesisType(programBank) == kind;

    private static string BankTitle(string kind, IBank bank) =>
        !bank.IsWritable ? T("Werks-Bank", "Factory bank")
        : !BanksOfKindMatch(kind, bank) ? T("Passt nicht für diese Sounds", "Doesn’t fit these sounds")
        : Free(bank) == 0 ? T("Bank ist voll", "Bank is full")
        : null;

    private IBank ShownBank(string kind)
    {
        IBanks banks = kind == CombiKind ? _mine.Pcg.CombiBanks : _mine.Pcg.ProgramBanks;
        return banks.BankCollection.FirstOrDefault(bank => bank.Id == _bank.GetValueOrDefault(kind));
    }

    // ---------- Navigation ----------

    private void Next()
    {
        if (_step == 2)
        {
            SuggestPlaces();
            _preview = null;
            SchedulePreview();
        }

        _step++;
    }

    private void Back()
    {
        _choose = null;
        _error = null;
        _step--;
    }

    private void SetRename(bool value)
    {
        _rename = value;
        SchedulePreview();
    }

    /// <summary>Beispielname für den Hinweis beim Umbenennen.</summary>
    private string RenameExample()
    {
        var combi = _info?.Combis.FirstOrDefault();
        var name = combi == null ? null : ProgramNaming.ForCombi(combi).Values.FirstOrDefault();
        return name ?? T("Songname 01 02", "Song name 01 02");
    }

    // ---------- Schritt 4: Einfügen und sichern ----------

    private async Task Insert()
    {
        _error = null;
        _busy = T("Sounds werden eingefügt und geprüft …", "Adding and verifying the sounds …");
        StateHasChanged();
        await JS.InvokeVoidAsync("pcgPaint");
        var outPath = WebFiles.NewPath("eingefuegt");
        try
        {
            _result = CopyService.Run(BuildRequest(outPath));
            _resultPath = outPath;
            // "… neu" (englisch "… new") nur einmal anhängen, auch wenn mehrere Pakete nacheinander eingefügt werden; kurz
            // halten für den Kronos. Beide Zusätze werden erkannt, falls die Datei in der anderen Sprache entstanden ist.
            var baseName = Path.GetFileNameWithoutExtension(_mine.Name);
            var stem = baseName.EndsWith(" neu") || baseName.EndsWith(" new") ? baseName[..^4] : baseName;
            _outName = WebFiles.ShortName(stem, T(" neu", " new")) + ".PCG";
            _attached = null;
            _zipFiles = null;
            if (HasSamples)
            {
                _busy = T("Samples werden angehängt und geprüft …", "Appending and verifying the samples …");
                StateHasChanged();
                await JS.InvokeVoidAsync("pcgPaint");
                AttachSamples();
            }
            else if (_sampleBankDir != null)
            {
                // Paket ohne Samples nach einem mit Samples: Die Sample-Bank aus dem vorigen Durchgang bleibt im Ergebnis.
                _zipFiles = BankFiles(_sampleBankDir);
            }

            _saveStatus = null;
            _choose = null;
            _step = 4;
        }
        catch (CopyException exception)
        {
            _error = exception;
        }
        catch (Exception exception)
        {
            _error = new CopyException(exception.Message);
        }
        finally
        {
            _busy = null;
        }
    }

    /// <summary>
    /// Samples des Pakets mitnehmen. Mit eigener Sample-Bank: anhängen (neue Nummern, Sounds umstellen). Ohne: Die
    /// Sample-Bank des Pakets kommt unverändert dazu. In beiden Fällen entsteht die Dateiliste für das fertige ZIP.
    /// </summary>
    private void AttachSamples()
    {
        var outDir = WebFiles.NewPath("fertig")[..^4];
        Directory.CreateDirectory(outDir);
        string bankName;
        if (_ownBank != null)
        {
            var attachedPcg = WebFiles.NewPath("mit-samples");
            _attached = SampleAttach.Run(new SampleAttachRequest
            {
                PcgFile = _resultPath,
                OutPcg = attachedPcg,
                Programs = _result.Programs.Where(program => !program.Reused).Select(program => program.To).ToList(),
                PackKsc = _packKsc,
                BankKsc = _ownBank.KscPath,
                OutDir = outDir,
            });
            _resultPath = attachedPcg;
            bankName = _ownBank.Name;

            // Schon vorhandene Samples des Kunden (soweit gelesen) dazu, damit das Paket vollständig ist.
            var ownSamples = Path.Combine(_ownBank.Dir, bankName);
            if (Directory.Exists(ownSamples))
            {
                foreach (var file in Directory.GetFiles(ownSamples, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(outDir, bankName, Path.GetRelativePath(ownSamples, file));
                    if (!File.Exists(target))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(file, target);
                    }
                }
            }
        }
        else
        {
            var packDir = Path.GetDirectoryName(_packKsc)!;
            bankName = Path.GetFileNameWithoutExtension(_packKsc);
            foreach (var file in Directory.GetFiles(packDir, bankName + "*.KSC").Concat(
                         Directory.Exists(Path.Combine(packDir, bankName))
                             ? Directory.GetFiles(Path.Combine(packDir, bankName), "*", SearchOption.AllDirectories)
                             : Array.Empty<string>()))
            {
                var target = Path.Combine(outDir, Path.GetRelativePath(packDir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }
        }

        _sampleBankName = bankName;
        _sampleBankDir = outDir;
        _zipFiles = BankFiles(outDir);
    }

    private static List<(string Entry, string Path)> BankFiles(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => (Path.GetRelativePath(dir, path).Replace('\\', '/'), path)).ToList();

    private string _sampleBankName;
    private string _sampleBankDir;

    /// <summary>Alle Samples der Bank liegen im Ergebnis (sonst nur die neuen Dateien zum Dazukopieren).</summary>
    private bool ResultIsComplete => _ownBank == null || _ownBank.Complete;

    // Ein Name für alles: PCG, beide KSC-Dateien und der Sample-Ordner heißen gleich, damit man das Paket wiederfindet.
    private bool _renameBank = true;

    /// <summary>Die Sample-Bank lässt sich nur umbenennen, wenn sie vollständig im Ergebnis liegt (sonst muss sie zum vorhandenen Ordner passen).</summary>
    /// <summary>Name der Sample-Bank, wie er im gesicherten Paket steht.</summary>
    private string ShownBankName => CanRenameBank && _renameBank ? BaseName : _sampleBankName;

    private bool CanRenameBank => _zipFiles != null && ResultIsComplete;

    /// <summary>Name ohne Endung, wie er im Feld steht.</summary>
    private string BaseName
    {
        get => _outName != null && _outName.EndsWith(".PCG", StringComparison.OrdinalIgnoreCase) ? _outName[..^4] : _outName;
        set => _outName = (value ?? "") + ".PCG";   // nicht kürzen: Beim Tippen muss ein Leerzeichen am Ende stehen bleiben
    }

    /// <summary>Höchstlänge des Namens, für PCG, KSC-Dateien und Sample-Ordner gleich (siehe WebFiles.NameLimit).</summary>
    private const int NameLimit = WebFiles.NameLimit;

    /// <summary>Kronos-Dateinamen: kurz und nur einfache Zeichen, damit der Kronos KSC und Ordner sicher findet.</summary>
    private string NameProblem(string name) =>
        string.IsNullOrWhiteSpace(name) ? T("Bitte einen Namen eingeben.", "Please enter a name.")
        : name.Length > NameLimit
            ? T($"Der Name ist zu lang: höchstens {NameLimit} Zeichen, sonst findet der Kronos die Dateien nicht (Pfade dürfen dort nur 76 Zeichen lang sein).",
                $"The name is too long: at most {NameLimit} characters, otherwise the Kronos cannot find the files (paths there may only be 76 characters long).")
        : name.Any(c => !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or ' ' or '-' or '_' or '+'))
            ? T("Bitte nur Buchstaben ohne Umlaute, Ziffern, Leerzeichen, - _ und + verwenden.", "Please use only plain letters (A–Z), digits, spaces, - _ and +.")
            : null;

    private async Task Save()
    {
        if (_zipFiles == null)
        {
            _saveStatus = T("Speichern wird angeboten …", "Offering the file to save …");
            StateHasChanged();
            _saveStatus = NameProblem(BaseName.Trim()) ?? await WebFiles.SaveAsync(JS, _resultPath, BaseName.Trim() + ".PCG");
            return;
        }

        var name = BaseName.Trim();
        if (NameProblem(name) is { } problem)
        {
            _saveStatus = problem;
            return;
        }

        _saveStatus = T("Speichern wird angeboten …", "Offering the file to save …");
        StateHasChanged();
        await JS.InvokeVoidAsync("pcgPaint");
        var files = new List<(string Entry, string Path)> { (name + ".PCG", _resultPath) };
        files.AddRange(CanRenameBank && _renameBank && name != _sampleBankName ? RenamedBank(name) : _zipFiles);
        _saveStatus = await WebFiles.SaveZipAsync(JS, files, name);
    }

    /// <summary>
    /// Sample-Bank unter neuem Namen: KSC, _UserBank.KSC und Ordner heißen wie die PCG. In der _UserBank.KSC steht der
    /// Name der KSC in der Zählerzeile ("#>uuid:Kennung.Anzahl.Anzahl.Name"), er wird dort mit geändert.
    /// </summary>
    private List<(string Entry, string Path)> RenamedBank(string name)
    {
        var old = _sampleBankName;
        var renamed = new List<(string Entry, string Path)>();
        foreach (var (entry, path) in _zipFiles)
        {
            if (entry == old + ".KSC")
            {
                renamed.Add((name + ".KSC", path));
            }
            else if (entry == old + "_UserBank.KSC")
            {
                var latin1 = System.Text.Encoding.Latin1;
                var lines = latin1.GetString(File.ReadAllBytes(path)).Split("\r\n");
                for (var index = 0; index < lines.Length; index++)
                {
                    if (lines[index].StartsWith("#>uuid:") && lines[index].EndsWith("." + old))
                    {
                        lines[index] = lines[index][..^old.Length] + name;
                    }
                }

                var copy = WebFiles.NewPath("userbank")[..^4] + ".KSC";
                File.WriteAllBytes(copy, latin1.GetBytes(string.Join("\r\n", lines)));
                renamed.Add((name + "_UserBank.KSC", copy));
            }
            else if (entry.StartsWith(old + "/"))
            {
                renamed.Add((name + entry[old.Length..], path));
            }
            else
            {
                renamed.Add((entry, path));
            }
        }

        return renamed;
    }

    private IEnumerable<Check> ResultChecks()
    {
        var neu = _result.Programs.Count(program => !program.Reused);
        var where = ProgramKinds.Select(kind => _result.Programs.Where(program => !program.Reused && program.Type == kind).Select(program => program.To))
            .Where(ids => ids.Any()).Select(PlaceText).ToList();
        yield return new Check(true,
            T($"{neu} {(neu == 1 ? "Sound" : "Sounds")} neu eingefügt", $"{neu} new {(neu == 1 ? "sound" : "sounds")} added"),
            where.Count > 0 ? T("auf ", "at ") + string.Join(T(" und ", " and "), where) : null);
        var reused = _result.Programs.Count(program => program.Reused);
        if (reused > 0)
        {
            yield return new Check(true,
                T($"{reused} {(reused == 1 ? "Sound hattest" : "Sounds hattest")} du schon", $"You already had {reused} {(reused == 1 ? "sound" : "sounds")}"),
                T("Identische Sounds werden mitgenutzt statt doppelt abgelegt.", "Identical sounds are reused instead of stored twice."));
        }

        if (_result.Combis.Count > 0)
        {
            yield return new Check(true, _result.Combis.Count == 1
                ? T("Die Combi spielt genau die Sounds aus dem Paket", "The Combi plays exactly the sounds from the pack")
                : T("Die Combis spielen genau die Sounds aus dem Paket", "The Combis play exactly the sounds from the pack"));
        }

        if (_attached != null)
        {
            var numbers = _attached.Multisamples.Select(sample => sample.To).ToList();
            yield return new Check(true,
                T($"{_attached.Samples} {(_attached.Samples == 1 ? "Sample" : "Samples")} an deine Sample-Bank „{_sampleBankName}“ angehängt",
                  $"{_attached.Samples} {(_attached.Samples == 1 ? "sample" : "samples")} appended to your sample bank “{_sampleBankName}”"),
                T($"Neue Multisample-{(numbers.Count == 1 ? "Nummer" : "Nummern")} {string.Join(", ", numbers)}. Deine vorhandenen Samples behalten ihre Nummern.",
                  $"New Multisample {(numbers.Count == 1 ? "number" : "numbers")} {string.Join(", ", numbers)}. Your existing samples keep their numbers."));
            foreach (var warning in _attached.Warnings.Distinct())
            {
                yield return new Check(false, T("Hinweis zu den Samples", "Note on the samples"), warning);
            }
        }
        else if (_zipFiles != null)
        {
            yield return new Check(true, T($"Die Sample-Bank „{_sampleBankName}“ des Pakets liegt bei", $"The pack’s sample bank “{_sampleBankName}” is included"));
        }

        if (_result.Deleted.Count > 0)
        {
            yield return new Check(false,
                T($"{_result.Deleted.Count} {(_result.Deleted.Count == 1 ? "ungenutzter Sound" : "ungenutzte Sounds")} überschrieben",
                  $"{_result.Deleted.Count} unused {(_result.Deleted.Count == 1 ? "sound" : "sounds")} overwritten"),
                string.Join(", ", _result.Deleted.Take(8).Select(deleted => deleted.Name.Trim())) + (_result.Deleted.Count > 8 ? " …" : "") +
                T(". In deiner alten PCG sind sie weiterhin vorhanden.", ". They are still in your old PCG."));
        }

        yield return new Check(true, _result.Deleted.Count > 0
            ? T("Deine übrigen Combis und Programs sind unverändert", "Your other Combis and Programs are unchanged")
            : T("Deine bisherigen Combis und Programs sind unverändert", "Your existing Combis and Programs are unchanged"));
        yield return new Check(true, T("Neue Datei nach dem Speichern noch einmal geprüft", "New file verified once more after saving"));
        foreach (var check in PackChecks().Where(check => !check.Ok))
        {
            yield return check;
        }
    }

    /// <summary>Noch ein Paket: Die neue Datei wird zur eigenen PCG, damit alles in einer Datei landet.</summary>
    private async Task Again()
    {
        _busy = T("Neue Datei wird übernommen …", "Taking over the new file …");
        StateHasChanged();
        await JS.InvokeVoidAsync("pcgPaint");
        _mine = new PcgSlot { Path = _resultPath, Name = _outName, Pcg = PcgFiles.Load(_resultPath) };
        if (_zipFiles != null)
        {
            // Die Sample-Bank aus diesem Durchgang ist ab jetzt die eigene: Das nächste Paket wird daran angehängt.
            var ksc = Path.Combine(_sampleBankDir, _sampleBankName + ".KSC");
            var complete = ResultIsComplete;
            _ownBank = new OwnBank(_sampleBankDir, ksc, _sampleBankName, SampleAttach.BankInfo(ksc)?.Multisamples ?? 0, complete,
                _zipFiles.Count, T("Sample-Bank aus dem vorigen Durchgang, das nächste Paket wird daran angehängt.",
                    "Sample bank from the previous round; the next pack is appended to it."));
            _wantOwnBank = true;
        }

        _attached = null;
        _zipFiles = null;
        _result = null;
        _preview = null;
        _busy = null;
        ResetPack();
        _step = 1;
    }

    private void StartOver()
    {
        ResetPack();
        ResetMine();
        _wantOwnBank = false;
        _attached = null;
        _zipFiles = null;
        _result = null;
        _preview = null;
        _error = null;
        _step = 1;
    }

    // ---------- Hilfen ----------

    private static IEnumerable<IPatch> Loaded(IBanks banks) =>
        banks?.BankCollection.Where(bank => bank.IsLoaded).SelectMany(bank => bank.Patches) ?? Enumerable.Empty<IPatch>();

    private static IEnumerable<IBank> Writable(IBanks banks) =>
        banks?.BankCollection.Where(bank => bank.IsLoaded && bank.IsWritable) ?? Enumerable.Empty<IBank>();

    private static bool IsGm(IBank bank) => bank is IProgramBank programBank && programBank.Type == BankType.EType.Gm;

    private static int Free(IBank bank) => bank.Patches.Count(patch => patch.IsEmptyOrInit);

    private static string P(bool value) => value ? "true" : "false";

    /// <summary>Tausenderpunkt wie im Deutschen, im Englischen Komma (die Seite läuft ohne Sprachdaten).</summary>
    private static string Number(int value)
    {
        var text = value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        return English ? text : text.Replace(',', '.');
    }

    private static string KindLabel(string kind, int count) => kind == CombiKind
        ? count == 1 ? "Combi" : $"{count} Combis"
        : count == 1 ? T("1 Sound", "1 sound") : T($"{count} Sounds", $"{count} sounds");
}
