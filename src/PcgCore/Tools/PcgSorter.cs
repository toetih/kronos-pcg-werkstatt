// Sortieren von Programs und Combis über eine oder mehrere Bänke hinweg.
// Sortierreihenfolge (PatchSorter) und Datentausch (SwapPatch) aus PCG Tools (Michel Keijzers).
// Statt nach jedem Tausch alle Verweise anzupassen (PcgViewModel.MovePatchesForSorting), wird die
// Permutation einmal berechnet und die Verweise einmal am Ende angepasst.
using System.Collections.Generic;
using System.Linq;
using PcgTools.Model.Common.Synth.MemoryAndFactory;
using PcgTools.Model.Common.Synth.Meta;
using PcgTools.Model.Common.Synth.PatchCombis;
using PcgTools.Model.Common.Synth.PatchPrograms;
using PcgTools.Model.Common.Synth.PatchSetLists;
using PcgTools.Model.Common.Synth.PatchSorting;

namespace KronosPcg.Tools
{
    public static class PcgSorter
    {
        public enum Kind
        {
            Programs,
            Combis
        }

        /// <summary>
        /// Sortiert die Patches der angegebenen Bänke. Programs werden je Synthese-Typ getrennt sortiert,
        /// weil sie nur in Bänken ihres Typs liegen dürfen. Leere Plätze landen am Ende.
        /// Gibt die Anzahl der Patches zurück, die einen neuen Platz bekommen haben.
        /// </summary>
        public static int Sort(IPcgMemory pcg, Kind kind, IList<IBank> banks, PatchSorter.SortOrder order) =>
            Sort(pcg, kind, banks, order, out _);

        /// <param name="changes">Für jeden verschobenen Inhalt: alter Platz -> neuer Platz.</param>
        public static int Sort(IPcgMemory pcg, Kind kind, IList<IBank> banks, PatchSorter.SortOrder order,
            out Dictionary<IPatch, IPatch> changes)
        {
            changes = new Dictionary<IPatch, IPatch>();
            foreach (var list in BuildLists(kind, banks))
            {
                var destination = list.ToList();
                PatchSorter.SortBy(destination, order);
                Permute(pcg, list, destination, changes);
            }

            // Verweise einmal am Ende anpassen, in allen geladenen Bänken. PCG Tools passt sie nach jedem
            // Tausch an und lässt dabei Combis in Bänken ohne belegte Combi sowie leere Set Lists aus.
            if (kind == Kind.Programs)
            {
                ChangeProgramReferences(pcg, changes);
            }
            else
            {
                ChangeCombiReferences(pcg, changes);
            }

            return changes.Count;
        }

        /// <summary>
        /// Vorschau ohne Änderung der Datei: Für jeden Platz der Bänke der Inhalt, der nach dem Sortieren dort liegt
        /// (gleiche Reihenfolge wie <see cref="Sort(IPcgMemory, Kind, IList{IBank}, PatchSorter.SortOrder)"/>).
        /// </summary>
        public static List<(IPatch Place, IPatch Content)> Plan(Kind kind, IList<IBank> banks, PatchSorter.SortOrder order)
        {
            var plan = new List<(IPatch, IPatch)>();
            foreach (var list in BuildLists(kind, banks))
            {
                var destination = list.ToList();
                PatchSorter.SortBy(destination, order);
                plan.AddRange(list.Select((place, index) => (place, destination[index])));
            }

            return plan;
        }

        private static IEnumerable<List<IPatch>> BuildLists(Kind kind, IList<IBank> banks)
        {
            if (kind == Kind.Combis)
            {
                return new[] { banks.SelectMany(bank => bank.Patches).ToList() };
            }

            return banks.Cast<IProgramBank>()
                .GroupBy(bank => bank.BankSynthesisType)
                .Select(group => group.SelectMany(bank => bank.Patches).ToList());
        }

        /// <summary>
        /// Bringt die Inhalte der Plätze in <paramref name="list"/> in die Reihenfolge von
        /// <paramref name="destination"/> (Platz i bekommt den Inhalt von destination[i]).
        /// Getauscht wird mit SwapPatch des Modells; <paramref name="changes"/> erhält
        /// "alter Platz -> neuer Platz" für jeden verschobenen Inhalt.
        /// </summary>
        private static void Permute(IPcgMemory pcg, List<IPatch> list, List<IPatch> destination, Dictionary<IPatch, IPatch> changes)
        {
            var contentAt = list.ToArray();                      // Platz-Index -> ursprünglicher Platz des Inhalts
            var indexOfContent = new Dictionary<IPatch, int>();   // ursprünglicher Platz -> aktueller Platz-Index
            for (var index = 0; index < list.Count; index++)
            {
                indexOfContent[list[index]] = index;
            }

            for (var index = 0; index < list.Count; index++)
            {
                var wanted = destination[index];
                var from = indexOfContent[wanted];
                if (from == index)
                {
                    continue;
                }

                pcg.SwapPatch(list[index], list[from]);
                var displaced = contentAt[index];
                contentAt[index] = wanted;
                contentAt[from] = displaced;
                indexOfContent[wanted] = index;
                indexOfContent[displaced] = from;
            }

            for (var index = 0; index < list.Count; index++)
            {
                if (contentAt[index] != list[index])
                {
                    changes[contentAt[index]] = list[index];
                }
            }
        }

        private static void ChangeProgramReferences(IPcgMemory pcg, Dictionary<IPatch, IPatch> changes)
        {
            if (pcg.CombiBanks != null)
            {
                foreach (var timbre in LoadedPatches(pcg.CombiBanks).Cast<ICombi>().SelectMany(combi => combi.Timbres.TimbresCollection))
                {
                    var program = timbre.UsedProgramBank == null ? null : timbre.UsedProgram;
                    if (program != null && changes.TryGetValue(program, out var moved))
                    {
                        timbre.UsedProgram = (IProgram)moved;
                    }
                }
            }

            ChangeSetListReferences(pcg, changes, SetListSlot.PatchType.Program);
        }

        private static void ChangeCombiReferences(IPcgMemory pcg, Dictionary<IPatch, IPatch> changes)
        {
            ChangeSetListReferences(pcg, changes, SetListSlot.PatchType.Combi);
        }

        private static void ChangeSetListReferences(IPcgMemory pcg, Dictionary<IPatch, IPatch> changes, SetListSlot.PatchType type)
        {
            if (pcg.SetLists == null)
            {
                return;
            }

            foreach (var slot in LoadedPatches(pcg.SetLists).Cast<ISetListSlot>().Where(slot => slot.SelectedPatchType == type))
            {
                if (slot.UsedPatch != null && changes.TryGetValue(slot.UsedPatch, out var moved))
                {
                    slot.UsedPatch = moved;
                }
            }
        }

        private static IEnumerable<IPatch> LoadedPatches(IBanks banks)
        {
            return banks.BankCollection.Where(bank => bank.IsLoaded).SelectMany(bank => bank.Patches);
        }
    }
}
