// Combis (mit allen genutzten Programs, Drum Kits und Wave Sequences) und einzelne Programs von einer
// PCG in eine andere übernehmen. Grundsatz: Quelle und Ziel werden nur gelesen, das Ergebnis ist eine
// neue Datei, die vor und nach dem Speichern geprüft wird. Genutzt von Kommandozeile und Browser.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Common.Mvvm;
using PcgTools.ClipBoard;
using PcgTools.Model.Common.Synth.MemoryAndFactory;
using PcgTools.Model.Common.Synth.Meta;
using PcgTools.Model.Common.Synth.PatchCombis;
using PcgTools.Model.Common.Synth.PatchPrograms;
using PcgTools.Properties;
using PcgTools.ViewModels;
using PcgTools.ViewModels.Commands.PcgCommands;

namespace KronosPcg.Tools
{
    public sealed class CopyRequest
    {
        public string SourceFile { get; init; }
        public string TargetFile { get; init; }
        public string OutFile { get; init; }
        public IReadOnlyList<string> Combis { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Programs { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> CombiBanks { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> ProgramBanks { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> WaveSequenceBanks { get; init; } = new[] { "U-AA" };
        public bool OverwriteFilled { get; init; }

        /// <summary>
        /// Optionaler Startplatz für Combis (z. B. "U-A010"): Davor liegende Plätze derselben Bank bleiben frei,
        /// die Bank des Startplatzes wird zuerst belegt.
        /// </summary>
        public string CombiStart { get; init; }

        /// <summary>Optionale Startplätze für Programs, höchstens einer je Synthese-Typ (HD-1, EXi).</summary>
        public IReadOnlyList<string> ProgramStarts { get; init; } = Array.Empty<string>();

        /// <summary>Programs auch dann kopieren, wenn das Ziel sie schon hat (z. B. Werks-Programs).</summary>
        public bool AlwaysCopyPrograms { get; init; }

        /// <summary>
        /// Umbenennen im Ergebnis, Schlüssel "C:U-A010" (Combi) bzw. "P:U-B002" (Program) = Platz im Ziel nach dem
        /// Kopieren. Gilt für neu übernommene und für schon vorhandene Sounds des Ziels.
        /// </summary>
        public IReadOnlyDictionary<string, string> Renames { get; init; } = new Dictionary<string, string>();

        /// <summary>
        /// Umbenennen über den Platz in der Quelle ("C:U-A026", "P:I-C049"): gilt für den Sound dort, wo er im Ziel
        /// landet. Für die Oberfläche, denn der Zielplatz neuer Sounds steht erst nach dem Kopieren fest.
        /// </summary>
        public IReadOnlyDictionary<string, string> RenamesBySource { get; init; } = new Dictionary<string, string>();

        /// <summary>
        /// Im Ziel löschen, vor dem Einfügen (die Plätze werden dadurch frei). Schlüssel wie bei Renames ("P:U-B002").
        /// Wer den Sound nutzt, zeigt danach auf einen leeren Platz; die Oberfläche warnt vorher doppelt.
        /// </summary>
        public IReadOnlyList<string> Deletes { get; init; } = Array.Empty<string>();
    }

    public sealed record Rename(string Key, string OldName, string NewName);

    /// <param name="Reused">Das Ziel hatte dieses Program schon (byte-gleich); es wird mitgenutzt statt kopiert.</param>
    public sealed record CopyMapping(string From, string To, string Name, string Type, bool Reused);

    public sealed class CopyResult
    {
        public List<CopyMapping> Combis { get; } = new();
        public List<CopyMapping> Programs { get; } = new();
        public List<string> WaveSequencesCopied { get; } = new();
        public List<string> WaveSequencesReused { get; } = new();
        public List<string> Notes { get; } = new();
        public List<Rename> Renamed { get; } = new();

        /// <summary>Gelöschte Plätze mit ihrem früheren Namen.</summary>
        public List<(string Key, string Name)> Deleted { get; } = new();
    }

    /// <summary>Kopieren nicht möglich oder Prüfung fehlgeschlagen; es gibt keine neue Datei.</summary>
    public sealed class CopyException : InvalidOperationException
    {
        public IReadOnlyList<string> Problems { get; }

        /// <summary>Wenn Sounds keinen Platz gefunden haben: wie viele je Art ("Combi", "HD-1", "EXi"). Sonst leer.</summary>
        public IReadOnlyDictionary<string, int> MissingByType { get; init; } = new Dictionary<string, int>();

        public CopyException(string message, IEnumerable<string> problems = null) : base(message)
        {
            Problems = (problems ?? Enumerable.Empty<string>()).ToList();
        }
    }

    public static class CopyService
    {
        public static CopyResult Run(CopyRequest request) => Run(request, null, dryRun: false);

        /// <summary>
        /// Wie <see cref="Run(CopyRequest)"/>, aber ohne Speichern: dieselbe Zuordnung und dieselbe Prüfung vor dem
        /// Speichern, nur im Speicher. Für die Vorschau in der Oberfläche; das Ziel wird dafür jedes Mal frisch gelesen.
        /// </summary>
        /// <param name="source">Schon geladene Quelle (wird nur gelesen), sonst wird sie aus SourceFile geladen.</param>
        public static CopyResult Preview(CopyRequest request, PcgMemory source) => Run(request, source, dryRun: true);

        private static CopyResult Run(CopyRequest request, PcgMemory loadedSource, bool dryRun)
        {
            if (!dryRun)
            {
                SafeOutput.Check(request.OutFile, request.SourceFile, request.TargetFile);
            }

            var source = loadedSource ?? PcgFiles.Load(request.SourceFile);
            var target = PcgFiles.Load(request.TargetFile);
            PcgFiles.CheckSameModel(source, target);
            Configure(request.OverwriteFilled, request.AlwaysCopyPrograms);

            var result = new CopyResult();
            var combis = request.Combis.Select(id => PcgFiles.FindPatch(source.CombiBanks, id)).Cast<ICombi>()
                .Where(combi => !combi.IsEmptyOrInit).ToList();
            var programs = request.Programs.Select(id => PcgFiles.FindPatch(source.ProgramBanks, id)).Cast<IProgram>().ToList();
            if (combis.Count == 0 && programs.Count == 0 && request.Renames.Count == 0 && request.RenamesBySource.Count == 0 &&
                request.Deletes.Count == 0)
            {
                throw new ArgumentException(Lang.T("Keine Combis oder Programs gewählt, nichts umbenannt und nichts gelöscht.",
                    "No Combis or Programs selected, nothing renamed and nothing deleted."));
            }

            // Löschen vor dem Einfügen: Die Plätze werden frei und können gleich neu belegt werden.
            foreach (var key in request.Deletes)
            {
                var patch = Slot(target, key);
                if (patch.IsEmptyOrInit)
                {
                    continue;
                }

                if (!((IBank)patch.Parent).IsWritable)
                {
                    throw new CopyException(Lang.T($"{key[2..]} liegt in einer schreibgeschützten Bank.",
                        $"{key[2..]} is in a write-protected bank."));
                }

                result.Deleted.Add((key, patch.Name));
                patch.Clear();
            }

            // Kopieren: PCG Tools legt die Combis, alle von ihnen genutzten Programs (und deren Drum Kits)
            // sowie die einzeln gewählten Programs in die Zwischenablage.
            var clipBoard = new PcgClipBoard();
            var selection = new ObservableCollectionEx<IPatch>();
            foreach (var patch in programs.Cast<IPatch>().Concat(combis))
            {
                patch.IsSelected = true;
                selection.Add(patch);
            }

            new CopyPasteCommands().CopyPasteCopy(clipBoard, source, PcgViewModel.ScopeSet.Patches,
                false, false, false, false, false, false, true,
                new ObservableCollectionEx<IBank>(), selection, false);
            foreach (var patch in selection)
            {
                // Auswahl zurücksetzen: Eine geladene Quelle wird für weitere Vorschauen wiederverwendet.
                patch.IsSelected = false;
            }

            var copiedCombis = clipBoard.Combis.CopiedPatches.ToList();
            var copiedPrograms = clipBoard.Programs.SelectMany(group => group.CopiedPatches).ToList();
            var filledBefore = new HashSet<IPatch>(target.ProgramBanks.BankCollection.SelectMany(bank => bank.Patches)
                .Where(patch => !patch.IsEmptyOrInit));

            // Einfügen: erst die Programs in die Ziel-Program-Bänke, dann die Combis.
            // Die Verweise der Combis auf die neuen Program-Plätze korrigiert PCG Tools beim Einfügen.
            // PCG Tools beendet das Einfügen, sobald die erste Bank-Gruppe fertig ist. Deshalb wird
            // je Synthese-Typ (HD-1, EXi) getrennt eingefügt.
            // Startplätze: Plätze vor dem Start sind für das Einfügen gesperrt (PCG Tools überspringt "geschützte" Patches).
            var programStartBanks = request.ProgramStarts.Select(id => Protect(clipBoard, PcgFiles.FindPatch(target.ProgramBanks, id))).ToList();
            var combiStartBank = string.IsNullOrEmpty(request.CombiStart)
                ? null
                : Protect(clipBoard, PcgFiles.FindPatch(target.CombiBanks, request.CombiStart));

            var programBanks = PcgFiles.SelectBanks(target.ProgramBanks, StartBanksFirst(request.ProgramBanks, programStartBanks, target.ProgramBanks));
            foreach (var group in programBanks.Cast<IProgramBank>().GroupBy(bank => bank.BankSynthesisType))
            {
                var banks = new ObservableCollectionEx<IBank>();
                foreach (var bank in programBanks.Where(bank => group.Contains(bank)))
                {
                    banks.Add(bank);
                }

                Paste(clipBoard, target, banks, programBanksSelected: true);
            }

            if (copiedCombis.Count > 0)
            {
                var combiStarts = combiStartBank == null ? new List<IBank>() : new List<IBank> { combiStartBank };
                Paste(clipBoard, target, PcgFiles.SelectBanks(target.CombiBanks, StartBanksFirst(request.CombiBanks, combiStarts, target.CombiBanks)),
                    programBanksSelected: false);
            }

            // Wave Sequences mitnehmen (PCG Tools kopiert sie nicht). Nur bei neu eingefügten Programs;
            // bereits vorhandene Programs im Ziel werden nicht verändert.
            var copier = new WaveSequenceCopier(target, PcgFiles.SelectBanks(target.WaveSequenceBanks, request.WaveSequenceBanks));
            foreach (var program in copiedPrograms.Where(patch => patch.PasteDestination != null && !filledBefore.Contains(patch.PasteDestination)))
            {
                copier.Apply((IProgram)program.OriginalLocation, (IProgram)program.PasteDestination);
            }

            var reusedMismatch = copiedPrograms
                .Where(patch => patch.PasteDestination != null && filledBefore.Contains(patch.PasteDestination))
                .Where(patch => !Verify.SameWaveSequences((IProgram)patch.OriginalLocation, (IProgram)patch.PasteDestination))
                .Select(patch => Lang.T(
                    $"{patch.PasteDestination.Id} {patch.PasteDestination.Name} (gleich wie {patch.OriginalLocation.Id}, " +
                    "aber im Ziel zeigt es auf andere Wave Sequences)",
                    $"{patch.PasteDestination.Id} {patch.PasteDestination.Name} (same as {patch.OriginalLocation.Id}, " +
                    "but in the target it points to different Wave Sequences)"))
                .ToList();
            if (reusedMismatch.Count > 0)
            {
                throw new CopyException(Lang.T(
                    "Im Ziel gibt es Programs, die gleich aussehen, aber andere Wave Sequences nutzen. Die Combis würden sie mitnutzen " +
                    "und anders klingen. Abhilfe: diese Programs im Ziel löschen oder korrigieren.",
                    "The target has Programs that look the same but use different Wave Sequences. The Combis would share them " +
                    "and sound different. Fix: delete or correct these Programs in the target."), reusedMismatch);
            }

            result.WaveSequencesCopied.AddRange(copier.Copied);
            result.WaveSequencesReused.AddRange(copier.Reused);
            if (copier.Problems.Count > 0)
            {
                var hasBank = target.WaveSequenceBanks.BankCollection.Any(bank => bank.IsLoaded && bank.IsWritable);
                var sequences = copier.Problems.Count == 1 ? "Wave Sequence" : "Wave Sequences";
                throw new CopyException(hasBank
                    ? Lang.T($"Für {copier.Problems.Count} Wave Sequence(s) ist in den Wave-Sequence-Bänken des Ziels kein Platz mehr frei.",
                        $"There is no free slot left in the target's Wave Sequence banks for {copier.Problems.Count} {sequences}.")
                    : Lang.T($"Die Ziel-PCG enthält keine Wave-Sequence-Bank, die Sounds brauchen aber {copier.Problems.Count} eigene Wave Sequence(s). " +
                             "Das Ziel ist vermutlich eine Teil-PCG (nur einzelne Bänke). Nimm eine vollständige PCG: am Kronos Disk → Save → „Save PCG“ mit allen Bänken.",
                        $"The target PCG has no Wave Sequence bank, but the sounds need {copier.Problems.Count} {sequences} of their own. " +
                        "The target is probably a partial PCG (only some banks). Use a full PCG: on the Kronos Disk → Save → “Save PCG” with all banks."),
                    copier.Problems);
            }

            var unplaced = copiedCombis.Concat(copiedPrograms).Where(patch => patch.PasteDestination == null)
                .Select(patch =>
                {
                    var bank = (IBank)patch.OriginalLocation.Parent;
                    var type = bank is IProgramBank programBank ? SynthesisType(programBank) : "Combi";
                    return (Type: type, Text: Lang.T($"nicht eingefügt: {patch.OriginalLocation.Id} {patch.OriginalLocation.Name} ({type})",
                        $"not added: {patch.OriginalLocation.Id} {patch.OriginalLocation.Name} ({type})"));
                }).ToList();
            if (unplaced.Count > 0)
            {
                throw new CopyException(Lang.T($"{unplaced.Count} Sound(s) haben keinen Platz gefunden. Bitte mehr Ziel-Bänke wählen " +
                                               "oder Plätze frei machen.",
                    $"{unplaced.Count} {(unplaced.Count == 1 ? "sound has" : "sounds have")} found no slot. Please choose more target banks " +
                    "or free up slots."), unplaced.Select(item => item.Text))
                {
                    MissingByType = unplaced.GroupBy(item => item.Type).ToDictionary(group => group.Key, group => group.Count()),
                };
            }

            // Prüfen vor dem Speichern: Jede Combi muss dieselben Programs (Name und Daten) nutzen wie im Original.
            var problems = Verify.CombiCopies(combis, copiedCombis);
            problems.AddRange(Verify.ProgramCopies(copiedPrograms));
            if (problems.Count > 0)
            {
                throw new CopyException(Lang.T("Prüfung vor dem Speichern fehlgeschlagen.", "Check before saving failed."), problems);
            }

            // Umbenennen erst nach der Prüfung der Kopien; die Gegenprobe vergleicht bei umbenannten Sounds nur die Daten.
            var renames = copiedCombis.Concat(copiedPrograms)
                // Nur neu eingefügte Sounds; ein im Ziel schon vorhandenes, mitgenutztes Program behält seinen Namen.
                .Where(patch => patch.PasteDestination != null && !filledBefore.Contains(patch.PasteDestination) &&
                                request.RenamesBySource.ContainsKey(Key(patch.OriginalLocation)))
                .GroupBy(patch => Key(patch.PasteDestination))   // mehrere gleiche Quell-Programs können auf einen Platz zeigen
                .ToDictionary(group => group.Key, group => request.RenamesBySource[Key(group.Last().OriginalLocation)]);
            foreach (var (key, name) in request.Renames)
            {
                renames[key] = name;
            }

            foreach (var (key, newName) in renames)
            {
                var patch = Slot(target, key);
                var name = newName.Trim();
                if (patch.IsEmptyOrInit)
                {
                    throw new CopyException(Lang.T($"{key[2..]} ist leer und kann nicht umbenannt werden.",
                        $"{key[2..]} is empty and cannot be renamed."));
                }

                if (NameProblem(name, patch.MaxNameLength) is { } problem)
                {
                    throw new CopyException(Lang.T($"Name für {key[2..]}: {problem}", $"Name for {key[2..]}: {problem}"));
                }

                result.Renamed.Add(new Rename(key, patch.Name, name));
                patch.Name = name;
            }

            foreach (var program in copiedPrograms)
            {
                result.Programs.Add(new CopyMapping(program.OriginalLocation.Id, program.PasteDestination.Id, program.PasteDestination.Name,
                    SynthesisType((IProgramBank)program.PasteDestination.Parent), filledBefore.Contains(program.PasteDestination)));
            }

            foreach (var combi in copiedCombis)
            {
                result.Combis.Add(new CopyMapping(combi.OriginalLocation.Id, combi.PasteDestination.Id, combi.PasteDestination.Name, "Combi", false));
            }

            if (dryRun)
            {
                return result;
            }

            target.FileName = request.OutFile;
            target.SaveFile(true, true);

            // Gegenprobe: neue Datei frisch einlesen und noch einmal prüfen.
            var written = PcgFiles.Load(request.OutFile);
            bool Renamed(IPatch patch) => renames.ContainsKey(Key(patch));
            var reloadedCombis = copiedCombis.Select(patch => (ICombi)PcgFiles.FindPatch(written.CombiBanks, patch.PasteDestination.Id)).ToList();
            var reloadProblems = Verify.CombiPairs(
                copiedCombis.Select(patch => combis.First(combi => combi.Id == patch.OriginalLocation.Id)).ToList(), reloadedCombis, Renamed);
            reloadProblems.AddRange(Verify.ProgramPairs(
                copiedPrograms.Select(patch => (IProgram)patch.OriginalLocation).ToList(),
                copiedPrograms.Select(patch => (IProgram)PcgFiles.FindPatch(written.ProgramBanks, patch.PasteDestination.Id)).ToList(), Renamed));
            var filledByCopy = new HashSet<string>(copiedCombis.Concat(copiedPrograms).Where(patch => patch.PasteDestination != null)
                .Select(patch => Key(patch.PasteDestination)));
            reloadProblems.AddRange(result.Deleted.Where(deleted => !filledByCopy.Contains(deleted.Key) && !Slot(written, deleted.Key).IsEmptyOrInit)
                .Select(deleted => Lang.T($"{deleted.Key[2..]}: sollte gelöscht sein, ist es aber nicht",
                    $"{deleted.Key[2..]}: should be deleted, but is not")));
            reloadProblems.AddRange(result.Renamed.Where(rename => Slot(written, rename.Key).Name != rename.NewName)
                .Select(rename => Lang.T($"{rename.Key[2..]}: Name '{Slot(written, rename.Key).Name}' statt '{rename.NewName}'",
                    $"{rename.Key[2..]}: name '{Slot(written, rename.Key).Name}' instead of '{rename.NewName}'")));
            if (reloadProblems.Count > 0)
            {
                File.Delete(request.OutFile);
                throw new CopyException(Lang.T("Gegenprobe nach dem Speichern fehlgeschlagen. Die neue Datei wurde wieder gelöscht.",
                    "Verification after saving failed. The new file was deleted again."), reloadProblems);
            }

            return result;
        }

        /// <summary>Schlüssel eines Platzes für <see cref="CopyRequest.Renames"/>: "C:U-A010" oder "P:U-B002".</summary>
        public static string Key(IPatch patch) => (patch is IProgram ? "P:" : "C:") + patch.Id;

        private static IPatch Slot(IPcgMemory pcg, string key) =>
            PcgFiles.FindPatch(key.StartsWith("P:") ? pcg.ProgramBanks : pcg.CombiBanks, key[2..]);

        /// <summary>Kronos-Namen: höchstens 24 Zeichen, nur einfache Zeichen (keine Umlaute), nicht leer.</summary>
        public static string NameProblem(string name, int maxLength = 24)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return Lang.T("Der Name darf nicht leer sein.", "The name must not be empty.");
            }

            if (name.Length > maxLength)
            {
                return Lang.T($"höchstens {maxLength} Zeichen.", $"at most {maxLength} characters.");
            }

            var invalid = name.Where(c => c < 32 || c > 126).Distinct().ToList();
            if (invalid.Count > 0)
            {
                return Lang.T($"Zeichen, die der Kronos nicht kennt: {string.Join(" ", invalid)} (z. B. ä → ae).",
                    $"Characters the Kronos does not know: {string.Join(" ", invalid)} (e.g. ä → ae).");
            }

            // PCG Tools (und damit die Werkstatt) hält solche Namen für leere Plätze.
            if (name.Contains("Init") && (name.Contains("Prog") || name.Contains("Combi")))
            {
                return Lang.T("Namen mit „Init“ und „Prog“/„Combi“ gelten als leerer Platz.",
                    "Names with “Init” and “Prog”/“Combi” count as an empty slot.");
            }

            return null;
        }

        /// <summary>Sperrt alle Plätze vor dem Startplatz in dessen Bank und liefert die Bank.</summary>
        private static IBank Protect(PcgClipBoard clipBoard, IPatch start)
        {
            var bank = (IBank)start.Parent;
            foreach (var patch in bank.Patches.TakeWhile(patch => patch != start))
            {
                clipBoard.ProtectedPatches.Add(patch);
            }

            return bank;
        }

        /// <summary>
        /// Reihenfolge der Zielbänke: Eine Bank mit Startplatz kommt vor die übrigen Bänke desselben Typs,
        /// sonst bleibt die gewählte Reihenfolge (Dateireihenfolge).
        /// </summary>
        private static List<string> StartBanksFirst(IReadOnlyList<string> ids, List<IBank> startBanks, IBanks banks)
        {
            var starts = startBanks.Where(bank => ids.Contains(bank.Id)).Select(bank => bank.Id).ToList();
            return starts.Concat(ids.Where(id => !starts.Contains(id))).ToList();
        }

        public static string SynthesisType(IProgramBank bank) =>
            bank.BankSynthesisType == ProgramBank.SynthesisType.Hd1 ? "HD-1" : bank.Type == BankType.EType.Gm ? "GM" : "EXi";

        private static void Paste(PcgClipBoard clipBoard, IPcgMemory target, ObservableCollectionEx<IBank> banks, bool programBanksSelected)
        {
            // Der Rückgabetext meldet nur, dass in einer Bank-Gruppe nichts eingefügt wurde. Fehlende Plätze
            // werden danach einzeln geprüft (PasteDestination == null).
            new CopyPasteCommands().CopyPastePaste(clipBoard, target, PcgViewModel.ScopeSet.Banks,
                programBanksSelected, !programBanksSelected, false, false, false, false, false,
                banks, new ObservableCollectionEx<IPatch>());
        }

        private static void Configure(bool overwriteFilled, bool alwaysCopyPrograms)
        {
            var settings = Settings.Default;
            settings.CopyPaste_AutoExtendedSinglePatchSelectionPaste = false;
            settings.CopyPaste_CopyIncompleteCombis = false;
            settings.CopyPaste_CopyIncompleteSetListSlots = false;
            settings.CopyPaste_CopyPatchesFromMasterFile = false;
            settings.CopyPaste_OverwriteFilledCombis = overwriteFilled;
            settings.CopyPaste_OverwriteFilledPrograms = overwriteFilled;
            settings.CopyPaste_OverwriteFilledSetListSlots = overwriteFilled;
            settings.CopyPaste_PasteDuplicateCombis = false;
            settings.CopyPaste_PasteDuplicatePrograms = alwaysCopyPrograms;
            settings.CopyPaste_PasteDuplicateSetListSlots = false;
            settings.CopyPaste_PatchDuplicationName = (int)CopyPaste.PatchDuplication.DoNotUsePatchNames;
        }
    }
}
