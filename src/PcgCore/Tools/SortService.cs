// Programs oder Combis einer PCG nach Namen (oder Kategorie) sortieren, über mehrere Bänke hinweg.
// Grundsatz wie beim Kopieren: Die Datei wird nur gelesen, das Ergebnis ist eine neue Datei. Vor und nach
// dem Speichern wird geprüft, dass alle Combis und Set-List-Slots dieselben Sounds nutzen wie vorher.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PcgTools.Model.Common.Synth.Meta;
using PcgTools.Model.Common.Synth.PatchSorting;

namespace KronosPcg.Tools
{
    public sealed class SortRequest
    {
        public string File { get; init; }
        public string OutFile { get; init; }
        public PcgSorter.Kind Kind { get; init; }
        public IReadOnlyList<string> Banks { get; init; } = Array.Empty<string>();
        public PatchSorter.SortOrder Order { get; init; } = PatchSorter.SortOrder.ESortOrderNameCategory;
    }

    public sealed class SortResult
    {
        /// <summary>Verschobene Inhalte: (alter Platz, neuer Platz, Name).</summary>
        public List<(string From, string To, string Name)> Moved { get; } = new();
    }

    public static class SortService
    {
        public static SortResult Run(SortRequest request)
        {
            SafeOutput.Check(request.OutFile, request.File);
            if (request.Banks.Count == 0)
            {
                throw new CopyException(Lang.T("Keine Bank gewählt.", "No bank selected."));
            }

            var pcg = PcgFiles.Load(request.File);
            IBanks banks = request.Kind == PcgSorter.Kind.Programs ? pcg.ProgramBanks : pcg.CombiBanks;
            var selected = PcgFiles.SelectBanks(banks, request.Banks);

            var before = ReferenceSnapshot.Take(pcg);
            PcgSorter.Sort(pcg, request.Kind, selected, request.Order, out var changes);
            var result = new SortResult();
            foreach (var (from, to) in changes.Where(change => !change.Value.IsEmptyOrInit))
            {
                // Der Inhalt, der vorher auf "from" lag, liegt jetzt auf "to".
                result.Moved.Add((from.Id, to.Id, to.Name));
            }

            var problems = Check(before, ReferenceSnapshot.Take(pcg), request.Kind);
            if (problems.Count > 0)
            {
                throw new CopyException(Lang.T("Prüfung fehlgeschlagen: Nach dem Sortieren würden Combis andere Sounds nutzen.",
                    "Check failed: after sorting, Combis would use different sounds."), problems);
            }

            pcg.FileName = request.OutFile;
            pcg.SaveFile(true, true);

            // Gegenprobe: neue Datei frisch einlesen und mit dem Original vergleichen.
            var reloadProblems = Check(before, ReferenceSnapshot.Take(PcgFiles.Load(request.OutFile)), request.Kind);
            if (reloadProblems.Count > 0)
            {
                File.Delete(request.OutFile);
                throw new CopyException(Lang.T("Gegenprobe nach dem Speichern fehlgeschlagen. Die neue Datei wurde wieder gelöscht.",
                    "Verification after saving failed. The new file was deleted again."), reloadProblems);
            }

            return result;
        }

        private static List<string> Check(ReferenceSnapshot before, ReferenceSnapshot after, PcgSorter.Kind kind) =>
            kind == PcgSorter.Kind.Programs ? before.CompareByPlace(after) : before.CompareAfterCombiMove(after);
    }
}
