using KronosPcg.Tools;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PcgTools.Model.Common.Synth.Meta;
using PcgTools.Model.Common.Synth.OldParameters;
using PcgTools.Model.Common.Synth.PatchCombis;
using PcgTools.Model.Common.Synth.PatchPrograms;
using static KronosPcg.Lang;

namespace PcgWeb.Pages;

public partial class Home
{
    private enum View { Combis, Programs }

    private enum Tab { Insert, Copy, Sort }

    private Tab _tab = Tab.Insert;

    /// <param name="Copied">Das Program wird beim Übernehmen mitkopiert (nicht GM, belegt, in der Datei).</param>
    private sealed record TimbreRow(int Number, string Status, bool Off, string ProgramId, string ProgramName, bool Copied);

    private sealed record CopyPlan(int Combis, int SinglePrograms, int Hd1, int Exi, List<string> Warnings, bool Ready);


    [Inject] private IJSRuntime JS { get; set; }

    /// <summary>Sprache wechseln: Die Seite merkt sich die Wahl und lädt neu (geladene Dateien sind danach weg).</summary>
    private async Task SwitchLanguage() => await JS.InvokeVoidAsync("pcgSetLanguage", English ? "de" : "en");

    private readonly PcgSlot _source = new();
    private PcgSlot _target = new();

    private View _sourceView = View.Combis;
    private IBank _sourceBank;
    private readonly HashSet<string> _selectedCombis = new();
    private readonly HashSet<string> _selectedPrograms = new();
    private readonly HashSet<string> _expanded = new();

    private View _targetView = View.Combis;
    private IBank _targetBank;
    // Zielbänke: Startbank je Typ (angetippter Platz oder erste freie User-Bank), danach die folgenden
    // User-Bänke desselben Typs, falls die Startbank voll ist. Belegte Plätze werden nie überschrieben.
    private readonly Dictionary<string, string> _defaultBanks = new();
    private HashSet<string> _combiBanks => Chain(WritableBanks(_target.Pcg?.CombiBanks), _combiStart?[..^3] ?? _defaultBanks.GetValueOrDefault("Combi"));
    private HashSet<string> _programBanks => new(new[] { "HD-1", "EXi" }.SelectMany(type =>
        Chain(ProgramBanksOfType(_target, type), _programStarts.GetValueOrDefault(type)?[..^3] ?? _defaultBanks.GetValueOrDefault(type))));
    private HashSet<string> _waveBanks => Chain(WritableBanks(_target.Pcg?.WaveSequenceBanks), _defaultBanks.GetValueOrDefault("Wave"));
    private bool _alwaysCopyPrograms;
    private string _combiStart;
    private readonly Dictionary<string, string> _programStarts = new();
    private bool _sameFile;

    // Umbenennen: vorhandene Sounds über ihren Platz im Ziel, neue Sounds über ihren Platz in der Quelle
    // (ihr Zielplatz steht erst nach dem Kopieren fest). Schlüssel wie CopyService.Key, mit "T|" bzw. "S|" beim Bearbeiten.
    private readonly Dictionary<string, string> _renamesTarget = new();
    private readonly Dictionary<string, string> _renamesSource = new();
    private string _editKey;
    private string _editText;
    private string _editError;
    private ElementReference _editInput;
    private bool _focusEdit;

    // Löschen im Ziel: doppelte Warnung. Erst beim Antippen des Papierkorbs (wer nutzt den Sound?), dann vor dem
    // Erzeugen der neuen Datei als Bestätigung in der Liste aller Löschungen.
    private readonly Dictionary<string, (string Name, List<string> Users)> _deletes = new();
    private string _askDelete;
    private List<string> _askDeleteUsers;
    private bool _deleteConfirmed;

    // Aufräumen im Ziel: Programs, die nichts nutzt, und doppelte Sounds (Platz -> erster gleicher Platz).
    private HashSet<string> _unused = new();
    private Dictionary<string, string> _duplicatePrograms = new();
    private Dictionary<string, string> _duplicateCombis = new();
    private bool _onlyUnused;

    private string DuplicateOf(IPatch patch) =>
        (patch is IProgram ? _duplicatePrograms : _duplicateCombis).GetValueOrDefault(patch.Id);

    private int UnusedIn(IBank bank) => bank is IProgramBank ? bank.Patches.Count(patch => _unused.Contains(patch.Id) && !_deletes.ContainsKey(Key(patch))) : 0;

    /// <summary>Alle ungenutzten Programs der gezeigten Bank zum Löschen vormerken (bestätigt wird wie immer vor dem Erzeugen).</summary>
    private void MarkUnused()
    {
        foreach (var patch in _targetBank.Patches.Where(patch => _unused.Contains(patch.Id)))
        {
            _deletes[Key(patch)] = (patch.Name, new List<string>());
        }

        _deleteConfirmed = false;
        _askDelete = null;
        Changed();
    }

    // Einzelsound-Paket aus dem Ergebnis.
    private string _packageStatus;
    private PackageResult _package;
    private int _packageExtra;

    // Samples fürs Paket: Welche Sample-Bänke nutzen die übernommenen Sounds, und die dazu gewählte Bank (nur die nötigen Dateien).
    private Dictionary<string, int> _resultBanks = new();
    private (string KscPath, string Name, string Id, int Multisamples, int Files, long Bytes)? _packBank;
    private string _packBankStatus;
    private string _packBankError;

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

    // ---------- Laden ----------

    private async Task LoadFile(PcgSlot slot, IBrowserFile file)
    {
        ClearResult();
        await WebFiles.LoadAsync(slot, file, StateHasChanged, JS);

        _sameFile = SameContent(_source, _target);
        if (slot == _source)
        {
            _renamesSource.Clear();
            _selectedCombis.Clear();
            _selectedPrograms.Clear();
            _expanded.Clear();
            SetSourceView(_sourceView);
        }
        else
        {
            SetTargetDefaults();
        }

        SchedulePreview();
    }

    /// <summary>Gleiche Datei als Quelle und Ziel? Dann würde nichts kopiert (alles schon vorhanden).</summary>
    private static bool SameContent(PcgSlot a, PcgSlot b)
    {
        if (a.Pcg == null || b.Pcg == null)
        {
            return false;
        }

        var first = new FileInfo(a.Path);
        var second = new FileInfo(b.Path);
        return first.Length == second.Length && File.ReadAllBytes(a.Path).AsSpan().SequenceEqual(File.ReadAllBytes(b.Path));
    }

    private void SetTargetDefaults()
    {
        _renamesTarget.Clear();
        _deletes.Clear();
        _askDelete = null;
        _deleteConfirmed = false;
        _editKey = null;
        _combiStart = null;
        _programStarts.Clear();
        _defaultBanks.Clear();
        _unused = new();
        _duplicatePrograms = new();
        _duplicateCombis = new();
        _onlyUnused = false;
        if (_target.Pcg == null)
        {
            return;
        }

        _unused = new HashSet<string>(Housekeeping.UnusedPrograms(_target.Pcg).Select(program => program.Id));
        _duplicatePrograms = Housekeeping.Duplicates(_target.Pcg.ProgramBanks);
        _duplicateCombis = Housekeeping.Duplicates(_target.Pcg.CombiBanks);

        // Vorschlag: jeweils die erste User-Bank mit freien Plätzen; Wave Sequences wie bei den Einzelsound-Paketen U-AA.
        AddFirstFree("Combi", WritableBanks(_target.Pcg.CombiBanks));
        AddFirstFree("HD-1", ProgramBanksOfType(_target, "HD-1"));
        AddFirstFree("EXi", ProgramBanksOfType(_target, "EXi"));
        var waveBanks = WritableBanks(_target.Pcg.WaveSequenceBanks).ToList();
        AddFirstFree("Wave", waveBanks.Where(bank => bank.Id == "U-AA" && Free(bank) > 0).Concat(waveBanks));
        SetTargetView(_targetView);
    }

    private void AddFirstFree(string kind, IEnumerable<IBank> banks)
    {
        var list = banks.ToList();
        var bank = list.FirstOrDefault(b => b.Id.StartsWith("U-") && Free(b) > 0) ?? list.FirstOrDefault(b => Free(b) > 0);
        if (bank != null)
        {
            _defaultBanks[kind] = bank.Id;
        }
    }

    /// <summary>Startbank und alle danach folgenden User-Bänke (Dateireihenfolge) aus der Liste eines Typs.</summary>
    private static HashSet<string> Chain(IEnumerable<IBank> banks, string start)
    {
        var list = banks.ToList();
        var index = list.FindIndex(bank => bank.Id == start);
        return index < 0
            ? new HashSet<string>()
            : new HashSet<string>(list.Skip(index).Where((bank, i) => i == 0 || bank.Id.StartsWith("U-")).Select(bank => bank.Id));
    }

    // ---------- Anzeige ----------

    private void SetSourceView(View view)
    {
        _sourceView = view;
        _sourceBank = SourceBanks().FirstOrDefault();
    }

    private void SetTargetView(View view)
    {
        _targetView = view;
        var banks = TargetViewBanks().ToList();
        _targetBank = banks.FirstOrDefault(b => b.Id.StartsWith("U-")) ?? banks.FirstOrDefault();
    }

    private IEnumerable<IBank> SourceBanks()
    {
        if (_source.Pcg == null)
        {
            return Enumerable.Empty<IBank>();
        }

        IBanks banks = _sourceView == View.Combis ? _source.Pcg.CombiBanks : _source.Pcg.ProgramBanks;
        return banks.BankCollection.Where(bank => bank.IsLoaded && !IsGm(bank) && bank.Patches.Any(patch => !patch.IsEmptyOrInit));
    }

    private IEnumerable<IPatch> SourcePatches() =>
        _sourceBank?.Patches.Where(patch => !patch.IsEmptyOrInit) ?? Enumerable.Empty<IPatch>();

    private IEnumerable<IBank> TargetViewBanks()
    {
        if (_target.Pcg == null)
        {
            return Enumerable.Empty<IBank>();
        }

        IBanks banks = _targetView == View.Combis ? _target.Pcg.CombiBanks : _target.Pcg.ProgramBanks;
        return banks.BankCollection.Where(bank => bank.IsLoaded && !IsGm(bank));
    }

    private static IEnumerable<IBank> WritableBanks(IBanks banks) =>
        banks?.BankCollection.Where(bank => bank.IsLoaded && bank.IsWritable) ?? Enumerable.Empty<IBank>();

    private static IEnumerable<IBank> ProgramBanksOfType(PcgSlot slot, string type) =>
        WritableBanks(slot.Pcg.ProgramBanks).Where(bank => !IsGm(bank) && CopyService.SynthesisType((IProgramBank)bank) == type);

    private static bool IsGm(IBank bank) => bank is IProgramBank programBank && programBank.Type == BankType.EType.Gm;

    // Blazor gibt bool-Attribute ohne Wert aus; für aria-pressed/aria-expanded braucht es "true"/"false".
    private static string P(bool value) => value ? "true" : "false";

    /// <summary>Bei Program-Bänken der Typ vor der Zahl, z. B. "EXi · ".</summary>
    private static string TypePrefix(IBank bank) =>
        bank is IProgramBank programBank ? CopyService.SynthesisType(programBank) + " · " : "";

    private static int Filled(IBank bank) => bank.Patches.Count(patch => !patch.IsEmptyOrInit);

    private static int Free(IBank bank) => bank.Patches.Count(patch => patch.IsEmptyOrInit);

    private static string Status(ITimbre timbre) => (string)timbre.GetParam(ParameterNames.TimbreParameterName.Status).Value;

    private static int ActiveTimbres(ICombi combi) => combi.Timbres.TimbresCollection.Count(timbre => Status(timbre) != "Off");

    /// <summary>Neuer Platz eines Quell-Programs laut Vorschau oder Ergebnis, sonst null.</summary>
    private string TargetOf(string programId) => Shown?.Programs.FirstOrDefault(program => program.From == programId)?.To;

    private static IEnumerable<TimbreRow> TimbreRows(ICombi combi) =>
        combi.Timbres.TimbresCollection.Select((timbre, index) =>
        {
            var status = Status(timbre);
            var program = timbre.UsedProgram;
            var bank = timbre.UsedProgramBank;
            var copied = program != null && bank != null && bank.IsLoaded && !IsGm(bank) && !program.IsEmptyOrInit;
            return new TimbreRow(index + 1, status, status == "Off", program?.Id ?? "–", program?.Name ?? "", copied);
        });

    // ---------- Programs nach Combi benennen ----------

    /// <summary>
    /// Jedes Program eines aktiven Timbres heißt danach wie die Combi plus die Nummern aller aktiven Timbres, auf denen
    /// es liegt ("Cosa della vita 01 03 04"), damit man beim Laden von Hand sieht, wohin es gehört. Stumme Timbres
    /// zählen nicht mit und ihre Programs behalten den Namen.
    /// </summary>
    private void NameProgramsAfterCombi(ICombi combi)
    {
        foreach (var (programId, name) in ProgramNaming.ForCombi(combi, _renamesSource.GetValueOrDefault("C:" + combi.Id)))
        {
            _renamesSource["P:" + programId] = name;
        }

        Changed();
    }

    private bool HasProgramRenames(ICombi combi) =>
        TimbreRows(combi).Any(timbre => _renamesSource.ContainsKey("P:" + timbre.ProgramId));

    private void ResetProgramNames(ICombi combi)
    {
        foreach (var timbre in TimbreRows(combi))
        {
            _renamesSource.Remove("P:" + timbre.ProgramId);
        }

        Changed();
    }

    // ---------- Umbenennen ----------

    private int RenameCount => _renamesTarget.Count + _renamesSource.Count(pair => IsSelectedSource(pair.Key));

    private bool IsSelectedSource(string key) => key.StartsWith("C:") ? _selectedCombis.Contains(key[2..]) : true;

    /// <summary>Für neue Sounds der Platz in der Quelle, für vorhandene der Platz im Ziel; leere Plätze: null.</summary>
    private string RenameKey(IPatch patch, bool arrives)
    {
        if (arrives)
        {
            var from = patch is IProgram
                ? Shown?.Programs.FirstOrDefault(program => program.To == patch.Id && !program.Reused)?.From
                : Shown?.Combis.FirstOrDefault(combi => combi.To == patch.Id)?.From;
            return from == null ? null : "S|" + (patch is IProgram ? "P:" : "C:") + from;
        }

        return patch.IsEmptyOrInit ? null : "T|" + Key(patch);
    }

    private bool IsRenamed(string renameKey) => renameKey != null &&
        (renameKey.StartsWith("S|") ? _renamesSource.ContainsKey(renameKey[2..]) : _renamesTarget.ContainsKey(renameKey[2..]));

    private void StartRename(string renameKey, string currentName)
    {
        _editKey = renameKey;
        _editText = currentName;
        _editError = null;
        _focusEdit = true;
    }

    private void CancelRename()
    {
        _editKey = null;
        _editError = null;
    }

    private void CommitRename()
    {
        var name = (_editText ?? "").Trim();
        if (CopyService.NameProblem(name) is { } problem)
        {
            _editError = problem;
            return;
        }

        var bySource = _editKey.StartsWith("S|");
        var key = _editKey[2..];
        var renames = bySource ? _renamesSource : _renamesTarget;
        var original = bySource ? OriginalSourceName(key) : PcgFiles.FindPatch(key.StartsWith("P:") ? _target.Pcg.ProgramBanks : _target.Pcg.CombiBanks, key[2..]).Name;
        if (name == original)
        {
            renames.Remove(key);
        }
        else
        {
            renames[key] = name;
        }

        _editKey = null;
        _editError = null;
        Changed();
    }

    private string OriginalSourceName(string key) =>
        PcgFiles.FindPatch(key.StartsWith("P:") ? _source.Pcg.ProgramBanks : _source.Pcg.CombiBanks, key[2..]).Name;

    private void EditKey(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
        {
            CommitRename();
        }
        else if (e.Key == "Escape")
        {
            CancelRename();
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusEdit && _editKey != null)
        {
            _focusEdit = false;
            try
            {
                await _editInput.FocusAsync();
            }
            catch (InvalidOperationException)
            {
                // Feld nicht mehr vorhanden (Ansicht gewechselt).
            }
        }
    }

    // ---------- Startplätze ----------

    private static string Key(IPatch patch) => (patch is IProgram ? "P:" : "C:") + patch.Id;

    private bool IsStart(IPatch patch) => patch is IProgram program
        ? _programStarts.TryGetValue(CopyService.SynthesisType((IProgramBank)program.Parent), out var id) && id == patch.Id
        : _combiStart == patch.Id;

    private void SetStart(IPatch patch)
    {
        var bank = (IBank)patch.Parent;
        if (!bank.IsWritable)
        {
            return;
        }

        if (patch is IProgram)
        {
            var type = CopyService.SynthesisType((IProgramBank)bank);
            if (type == "GM")
            {
                return;
            }

            if (IsStart(patch))
            {
                _programStarts.Remove(type);
            }
            else
            {
                _programStarts[type] = patch.Id;
            }
        }
        else
        {
            _combiStart = IsStart(patch) ? null : patch.Id;
        }

        Changed();
    }

    /// <summary>Wohin es geht, je Typ: angetippter Startplatz (zurücksetzbar) oder erste freie Bank.</summary>
    private IEnumerable<(string Label, string Text, Action Clear)> StartPills()
    {
        yield return ("Combis", _combiStart ?? FirstFree("Combi"), _combiStart == null ? null : () => { _combiStart = null; Changed(); });
        foreach (var type in new[] { "HD-1", "EXi" })
        {
            var start = _programStarts.GetValueOrDefault(type);
            yield return (type, start ?? FirstFree(type), start == null ? null : () => { _programStarts.Remove(type); Changed(); });
        }

        yield return ("Wave Seq.", FirstFree("Wave"), null);
    }

    private string FirstFree(string kind) =>
        _defaultBanks.TryGetValue(kind, out var bank) ? T($"{bank}, erster freier Platz", $"{bank}, first free slot") : T("keine freie Bank", "no free bank");

    /// <summary>Nach dem Kopieren: welche Plätze im Ziel neu belegt wurden (für die Anzeige in der Ziel-Liste).</summary>
    private Dictionary<string, string> IncomingNames()
    {
        var names = new Dictionary<string, string>();
        var shown = Shown;
        if (shown == null)
        {
            return names;
        }

        foreach (var combi in shown.Combis)
        {
            names["C:" + combi.To] = combi.Name;
        }

        foreach (var program in shown.Programs.Where(program => !program.Reused))
        {
            names["P:" + program.To] = program.Name;
        }

        return names;
    }

    /// <summary>Wie viele neue Sounds laut Vorschau bzw. Ergebnis in dieser Ziel-Bank landen.</summary>
    private int IncomingCount(IBank bank)
    {
        var shown = Shown;
        if (shown == null)
        {
            return 0;
        }

        var mappings = bank is IProgramBank ? shown.Programs.Where(program => !program.Reused) : shown.Combis;
        return mappings.Count(mapping => mapping.To.Length == bank.Id.Length + 3 && mapping.To.StartsWith(bank.Id));
    }

    /// <summary>Freie Plätze einer gewählten Bank, bei einem Startplatz nur ab dort.</summary>
    private int FreeFrom(IBank bank)
    {
        var start = bank.Patches.FirstOrDefault(IsStart);
        return start == null ? Free(bank) : bank.Patches.SkipWhile(patch => patch != start).Count(patch => patch.IsEmptyOrInit);
    }

    private void ToggleSource(IPatch patch)
    {
        Toggle(patch is ICombi ? _selectedCombis : _selectedPrograms, patch.Id);
    }

    private void ToggleExpanded(string id)
    {
        if (!_expanded.Remove(id))
        {
            _expanded.Add(id);
        }
    }

    private void Toggle(HashSet<string> set, string id)
    {
        if (!set.Remove(id))
        {
            set.Add(id);
        }

        Changed();
    }

    // ---------- Planen und Kopieren ----------

    /// <summary>Was ungefähr passieren wird. Die genaue Zuordnung (inkl. schon vorhandener Programs) liefert erst das Kopieren.</summary>
    private CopyPlan Plan()
    {
        var warnings = new List<string>();
        if (_source.Pcg == null || _target.Pcg == null)
        {
            return new CopyPlan(0, 0, 0, 0, warnings, false);
        }

        var combis = _selectedCombis.Select(id => PcgFiles.FindPatch(_source.Pcg.CombiBanks, id)).Cast<ICombi>().ToList();
        var programs = combis.SelectMany(combi => combi.Timbres.TimbresCollection)
            .Where(timbre => timbre.UsedProgramBank != null && timbre.UsedProgram != null && !IsGm(timbre.UsedProgramBank) &&
                             timbre.UsedProgramBank.IsLoaded && !timbre.UsedProgram.IsEmptyOrInit)
            .Select(timbre => (IProgram)timbre.UsedProgram)
            .Concat(_selectedPrograms.Select(id => (IProgram)PcgFiles.FindPatch(_source.Pcg.ProgramBanks, id)))
            .Distinct().ToList();
        var hd1 = programs.Count(program => CopyService.SynthesisType((IProgramBank)program.Parent) == "HD-1");
        var exi = programs.Count - hd1;

        var ready = combis.Count + _selectedPrograms.Count > 0 || RenameCount > 0 || _deletes.Count > 0;
        if (combis.Count > 0 && _combiBanks.Count == 0)
        {
            warnings.Add(T("Ziel-Bank für Combis wählen", "Choose a target bank for Combis"));
            ready = false;
        }

        foreach (var (type, count) in new[] { ("HD-1", hd1), ("EXi", exi) })
        {
            var banks = ProgramBanksOfType(_target, type).Where(bank => _programBanks.Contains(bank.Id)).ToList();
            if (count > 0 && banks.Count == 0)
            {
                warnings.Add(T($"Ziel-Bank für {type}-Programs wählen", $"Choose a target bank for {type} Programs"));
                ready = false;
            }
            else if (count > banks.Sum(FreeFrom))
            {
                warnings.Add(T($"nur {banks.Sum(FreeFrom)} freie {type}-Plätze gewählt", $"only {banks.Sum(FreeFrom)} free {type} slots selected"));
            }
        }

        var freeCombis = WritableBanks(_target.Pcg.CombiBanks).Where(bank => _combiBanks.Contains(bank.Id)).Sum(FreeFrom);
        if (combis.Count > freeCombis && _combiBanks.Count > 0)
        {
            warnings.Add(T($"nur {freeCombis} freie Combi-Plätze gewählt", $"only {freeCombis} free Combi slots selected"));
        }

        return new CopyPlan(combis.Count, _selectedPrograms.Count, hd1, exi, warnings, ready);
    }

    private async Task RunCopy()
    {
        ClearResult();
        _busy = T("Sounds werden übernommen und geprüft …", "Transferring and verifying the sounds …");
        StateHasChanged();
        await JS.InvokeVoidAsync("pcgPaint");

        var outPath = WebFiles.NewPath("ergebnis");
        try
        {
            _result = CopyService.Run(BuildRequest(outPath));
            _resultPath = outPath;
            _resultBanks = SampleAttach.UsedBanks(ResultPrograms(PcgFiles.Load(outPath)));
            _outName = WebFiles.ShortName(Path.GetFileNameWithoutExtension(_target.Name), T("-neu", "-new")) + ".PCG";
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

    private CopyRequest BuildRequest(string outPath) => new()
    {
        SourceFile = _source.Path,
        TargetFile = _target.Path,
        OutFile = outPath,
        Combis = _selectedCombis.OrderBy(id => id, StringComparer.Ordinal).ToList(),
        Programs = _selectedPrograms.OrderBy(id => id, StringComparer.Ordinal).ToList(),
        // Reihenfolge der Bänke wie in der Datei, damit "der Reihe nach" stimmt.
        CombiBanks = InFileOrder(_target.Pcg.CombiBanks, _combiBanks),
        ProgramBanks = InFileOrder(_target.Pcg.ProgramBanks, _programBanks),
        WaveSequenceBanks = InFileOrder(_target.Pcg.WaveSequenceBanks, _waveBanks),
        AlwaysCopyPrograms = _alwaysCopyPrograms,
        CombiStart = _combiStart != null && _combiBanks.Contains(_combiStart[..^3]) ? _combiStart : null,
        ProgramStarts = _programStarts.Values.Where(id => _programBanks.Contains(id[..^3])).ToList(),
        Renames = _renamesTarget.Where(pair => !_deletes.ContainsKey(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value),
        Deletes = _deletes.Keys.ToList(),
        RenamesBySource = new Dictionary<string, string>(_renamesSource),
    };

    /// <summary>Etwas an Auswahl oder Ziel hat sich geändert: altes Ergebnis verwerfen, Vorschau neu berechnen.</summary>
    private void Changed()
    {
        ClearResult();
        SchedulePreview();
    }

    /// <summary>
    /// Vorschau: Probelauf des echten Kopierens ohne Speichern, kurz nach der letzten Änderung. Dadurch zeigt die
    /// Ziel-Liste schon vorher genau die Plätze, die "Neue Datei erzeugen" danach belegt.
    /// </summary>
    private async void SchedulePreview()
    {
        _previewDelay?.Cancel();
        var delay = _previewDelay = new CancellationTokenSource();
        _preview = null;
        _previewError = null;
        try
        {
            await Task.Delay(400, delay.Token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (!Plan().Ready || _sameFile)
        {
            StateHasChanged();
            return;
        }

        _previewBusy = true;
        StateHasChanged();
        await JS.InvokeVoidAsync("pcgPaint");
        if (delay.IsCancellationRequested)
        {
            _previewBusy = false;
            return;
        }

        try
        {
            _preview = CopyService.Preview(BuildRequest(null), _source.Pcg);
        }
        catch (CopyException exception)
        {
            _previewError = exception;
        }
        catch (Exception exception)
        {
            _previewError = new CopyException(exception.Message);
        }
        finally
        {
            _previewBusy = false;
            StateHasChanged();
        }
    }

    private CopyResult Shown => _result ?? _preview;

    private static List<string> InFileOrder(IBanks banks, HashSet<string> ids) =>
        banks?.BankCollection.Where(bank => ids.Contains(bank.Id)).Select(bank => bank.Id).ToList() ?? new List<string>();

    private void ClearResult()
    {
        _error = null;
        _result = null;
        _saveStatus = null;
        _packageStatus = null;
        _package = null;
        _resultBanks = new();
        _packBank = null;
        _packBankStatus = null;
        _packBankError = null;
    }

    /// <summary>Die neu eingefügten Programs in der (frisch geladenen) Ergebnis-PCG.</summary>
    private List<IProgram> ResultPrograms(PcgTools.Model.Common.Synth.MemoryAndFactory.IPcgMemory pcg) =>
        _result.Programs.Where(program => !program.Reused).Select(program => (IProgram)PcgFiles.FindPatch(pcg.ProgramBanks, program.To)).ToList();

    /// <summary>
    /// Ordner der eigenen Sample-Bank gewählt: die Bank mit der passenden Kennung suchen und nur lesen, was das Paket
    /// braucht (alle Multisample-Dateien, weil sie winzig sind, aber nur die Samples der genutzten Multisamples).
    /// </summary>
    private async Task PickPackBank(PickedFiles picked)
    {
        _packBank = null;
        _packBankError = null;
        _packBankStatus = T("Sample-Bank wird gesucht …", "Looking for the sample bank …");
        StateHasChanged();
        try
        {
            var banks = await WebFiles.ReadBanksAsync(picked, WebFiles.NewPath("quelle")[..^4]);
            var bank = banks.FirstOrDefault(candidate => _resultBanks.ContainsKey(candidate.Id));
            if (bank == null)
            {
                _packBankError = banks.Count == 0
                    ? T("In diesem Ordner liegt keine KSC einer Sample-Bank.", "There is no KSC of a sample bank in this folder.")
                    : T($"Keine der Sample-Bänke in diesem Ordner ({string.Join(", ", banks.Select(candidate => candidate.Name))}) hat die Kennung, die die Sounds nutzen ({string.Join(", ", _resultBanks.Keys)}).",
                        $"None of the sample banks in this folder ({string.Join(", ", banks.Select(candidate => candidate.Name))}) has the ID the sounds use ({string.Join(", ", _resultBanks.Keys)}).");
                return;
            }

            var samples = WebFiles.BankSamples(picked, bank);
            var bankDir = Path.Combine(Path.GetDirectoryName(bank.KscPath)!, bank.Name);
            var needed = SampleAttach.UsedNumbers(ResultPrograms(PcgFiles.Load(_resultPath)), bank.Id);
            var files = 0;
            long bytes = 0;
            var found = 0;
            foreach (var (file, rel) in samples.Where(sample => !sample.Rel.Contains('/') && sample.Rel.EndsWith(".KMP", StringComparison.OrdinalIgnoreCase)))
            {
                var kmpPath = Path.Combine(bankDir, rel);
                await WebFiles.CopyAsync(file, kmpPath);
                if (!needed.Contains(SampleAttach.MultisampleNumber(kmpPath)))
                {
                    continue;
                }

                found++;
                var folder = Path.GetFileNameWithoutExtension(rel) + "/";
                var names = new HashSet<string>(SampleAttach.SampleFilesOf(kmpPath), StringComparer.OrdinalIgnoreCase);
                foreach (var (sampleFile, sampleRel) in samples.Where(sample => sample.Rel.StartsWith(folder, StringComparison.OrdinalIgnoreCase) &&
                                                                              names.Contains(sample.Rel[folder.Length..])))
                {
                    files++;   // vor dem Text zählen: T() wertet beide Sprachfassungen aus
                    _packBankStatus = T($"Samples werden gelesen … {files}", $"Reading samples … {files}");
                    StateHasChanged();
                    await WebFiles.CopyAsync(sampleFile, Path.Combine(bankDir, sampleRel));
                    bytes += sampleFile.Size;
                }
            }

            if (found < needed.Count)
            {
                _packBankError = T($"In „{bank.Name}“ fehlen {needed.Count - found} der {needed.Count} genutzten Multisamples. Ist der Sample-Ordner „{bank.Name}“ im gewählten Ordner?",
                    $"“{bank.Name}” is missing {needed.Count - found} of the {needed.Count} Multisamples in use. Is the sample folder “{bank.Name}” inside the folder you chose?");
                return;
            }

            _packBank = (bank.KscPath, bank.Name, bank.Id, needed.Count, files, bytes);
        }
        catch (Exception exception)
        {
            _packBankError = T($"Die Sample-Bank konnte nicht gelesen werden: {exception.Message}", $"The sample bank could not be read: {exception.Message}");
        }
        finally
        {
            _packBankStatus = null;
        }
    }

    // ---------- Löschen ----------

    /// <summary>Erste Warnung: Papierkorb angetippt, zeigen, wer den Sound nutzt.</summary>
    private void AskDelete(IPatch patch)
    {
        _editKey = null;
        _askDelete = Key(patch);
        _askDeleteUsers = Usage.UsersOf(_target.Pcg, patch);
    }

    private void ConfirmDelete(IPatch patch)
    {
        _deletes[Key(patch)] = (patch.Name, _askDeleteUsers ?? new List<string>());
        _askDelete = null;
        _deleteConfirmed = false;
        Changed();
    }

    private void UndoDelete(string key)
    {
        _deletes.Remove(key);
        _deleteConfirmed = false;
        Changed();
    }

    private int AffectedCount => _deletes.Values.Sum(entry => entry.Users.Count);

    private bool CanRun(CopyPlan plan) => plan.Ready && _busy == null && !_sameFile && (_deletes.Count == 0 || _deleteConfirmed);

    // ---------- Einzelsound-Paket ----------

    /// <summary>
    /// Teil-PCG aus dem Ergebnis: genau die Bänke, in denen die übernommenen Combis und Programs liegen (auch die von
    /// stummen Timbres). Beim Laden am Kronos ersetzt sie nur diese Bänke; der Einfach-Modus fügt sie sauber ein.
    /// </summary>
    private async Task SavePackage()
    {
        _package = null;
        _packageStatus = T("Paket wird erstellt und geprüft …", "Creating and verifying the pack …");
        StateHasChanged();
        await JS.InvokeVoidAsync("pcgPaint");
        try
        {
            // Kurz halten: Der Name wird beim Kunden auch zum Namen von PCG, KSC und Sample-Ordner.
            var name = _result.Combis.Count == 1
                ? WebFiles.SafeName(_result.Combis[0].Name)
                : WebFiles.ShortName(WebFiles.SafeName(Path.GetFileNameWithoutExtension(_outName))[..Math.Min(12, WebFiles.SafeName(Path.GetFileNameWithoutExtension(_outName)).Length)], " Pak");

            // Mit Samples: erst eine eigene kleine Sample-Bank fürs Paket bauen und die Sounds darauf umstellen.
            var source = _resultPath;
            List<(string Entry, string Path)> bankFiles = null;
            SampleAttachResult samples = null;
            if (_packBank is { } packBank)
            {
                var bankOut = WebFiles.NewPath("paketbank")[..^4];
                source = WebFiles.NewPath("paket-samples");
                samples = SamplePack.Build(_resultPath, _result.Programs.Where(program => !program.Reused).Select(program => program.To).ToList(),
                    packBank.KscPath, name, bankOut, source);
                bankFiles = Directory.GetFiles(bankOut, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal)
                    .Select(path => (Path.GetRelativePath(bankOut, path).Replace('\\', '/'), path)).ToList();
            }

            var outPath = WebFiles.NewPath("paket");
            _package = PackageService.Run(new PackageRequest
            {
                File = source,
                OutFile = outPath,
                CombiBanks = _result.Combis.Select(combi => combi.To[..^3]).Distinct().ToList(),
                ProgramBanks = _result.Programs.Select(program => program.To[..^3]).Where(bank => bank.StartsWith("U-")).Distinct()
                    .OrderBy(bank => Array.IndexOf(PartialPcgExporter.ProgramBankOrder, bank)).ToList(),
            });
            // Andere Sounds, die schon vorher in diesen Bänken lagen, fahren mit (ganze Bänke). Für ein reines
            // Einzelsound-Paket als Ziel eine leere PCG nehmen.
            var copied = new HashSet<string>(_result.Combis.Select(combi => combi.To).Concat(_result.Programs.Select(program => program.To)));
            _packageExtra = _target.Pcg.CombiBanks.BankCollection.Where(bank => _package.CombiBanks.Contains(bank.Id))
                .Concat(_target.Pcg.ProgramBanks.BankCollection.Where(bank => _package.ProgramBanks.Contains(bank.Id)))
                .SelectMany(bank => bank.Patches)
                .Count(patch => !patch.IsEmptyOrInit && !copied.Contains(patch.Id) && !_deletes.ContainsKey(Key(patch)));
            string saved;
            if (bankFiles == null)
            {
                saved = await WebFiles.SaveAsync(JS, outPath, name);
            }
            else
            {
                var files = new List<(string Entry, string Path)> { (name + ".PCG", outPath) };
                files.AddRange(bankFiles);
                saved = await WebFiles.SaveZipAsync(JS, files, name);
            }

            var parts = new[] { ("Combis", _package.CombiBanks), ("Programs", _package.ProgramBanks), ("Wave Sequences", _package.WaveSequenceBanks) }
                .Where(part => part.Item2.Count > 0).Select(part => $"{part.Item1} {string.Join(", ", part.Item2)}");
            _packageStatus = T($"{saved} Bänke im Paket: {string.Join(" · ", parts)}.", $"{saved} Banks in the pack: {string.Join(" · ", parts)}.") +
                             (samples == null ? "" : T($" Eigene Sample-Bank „{name}“ mit {samples.Multisamples.Count} Multisample(s), {samples.Samples} Sample-Datei(en).",
                                 $" Own sample bank “{name}” with {samples.Multisamples.Count} Multisample(s), {samples.Samples} sample file(s)."));
        }
        catch (CopyException exception)
        {
            _packageStatus = exception.Message + (exception.Problems.Count > 0 ? " " + string.Join("; ", exception.Problems.Take(5)) : "");
        }
        catch (Exception exception)
        {
            _packageStatus = T("Paket konnte nicht erstellt werden: ", "The pack could not be created: ") + exception.Message;
        }
    }

    private async Task SaveResult()
    {
        _saveStatus = T("Speichern wird angeboten …", "Offering the file for saving …");
        StateHasChanged();
        _saveStatus = await WebFiles.SaveAsync(JS, _resultPath, _outName);
    }

    private async Task UseResultAsTarget()
    {
        _busy = T("Ergebnis wird als neues Ziel geladen …", "Loading the result as the new target …");
        StateHasChanged();
        await JS.InvokeVoidAsync("pcgPaint");
        _target = new PcgSlot { Path = _resultPath, Name = _outName, Pcg = PcgFiles.Load(_resultPath) };
        _renamesSource.Clear();
        _selectedCombis.Clear();
        _selectedPrograms.Clear();
        ClearResult();
        _busy = null;
        SetTargetDefaults();
        SchedulePreview();
    }
}
