// Fingerabdruck aller Verweise einer PCG: welche Sounds nutzt jede Combi und jeder Set-List-Slot?
// Dient zum Vergleich vor und nach einer Änderung.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using PcgTools.Model.Common.Synth.MemoryAndFactory;
using PcgTools.Model.Common.Synth.Meta;
using PcgTools.Model.Common.Synth.PatchCombis;
using PcgTools.Model.Common.Synth.PatchPrograms;
using PcgTools.Model.Common.Synth.PatchSetLists;

namespace KronosPcg.Tools
{
    public class ReferenceSnapshot
    {
        /// <summary>Combi-ID → Fingerabdruck der von ihren Timbres genutzten Programs (alle Combis, auch Init).</summary>
        public Dictionary<string, string> Combis { get; } = new();

        /// <summary>Set-List-Slot-ID → Fingerabdruck des genutzten Programs bzw. der genutzten Combi.</summary>
        public Dictionary<string, string> SetListSlots { get; } = new();

        public static ReferenceSnapshot Take(IPcgMemory pcg)
        {
            var snapshot = new ReferenceSnapshot();
            if (pcg.CombiBanks != null)
            {
                foreach (var combi in LoadedPatches(pcg.CombiBanks).Cast<ICombi>())
                {
                    snapshot.Combis[combi.Id] = CombiPrint(combi);
                }
            }

            if (pcg.SetLists != null)
            {
                foreach (var slot in LoadedPatches(pcg.SetLists).Cast<ISetListSlot>())
                {
                    snapshot.SetListSlots[slot.Id] = SlotPrint(slot);
                }
            }

            return snapshot;
        }

        /// <summary>
        /// Vergleicht Verweise bei gleichbleibenden Plätzen (z. B. nach dem Sortieren von Programs).
        /// </summary>
        public List<string> CompareByPlace(ReferenceSnapshot after)
        {
            var problems = new List<string>();
            Compare("Combi", Combis, after.Combis, problems);
            Compare(Lang.T("Set-List-Slot", "Set list slot"), SetListSlots, after.SetListSlots, problems);
            return problems;
        }

        /// <summary>
        /// Vergleicht nach dem Verschieben von Combis: Die Combis selbst dürfen den Platz wechseln,
        /// müssen aber als Menge identisch bleiben; Set-List-Slots behalten ihren Platz.
        /// </summary>
        public List<string> CompareAfterCombiMove(ReferenceSnapshot after)
        {
            var problems = new List<string>();
            var before = Combis.Values.OrderBy(value => value, StringComparer.Ordinal).ToList();
            var now = after.Combis.Values.OrderBy(value => value, StringComparer.Ordinal).ToList();
            if (!before.SequenceEqual(now))
            {
                problems.Add(Lang.T("Die Combis haben sich beim Verschieben inhaltlich verändert.", "The Combis changed in content while being moved."));
            }

            Compare(Lang.T("Set-List-Slot", "Set list slot"), SetListSlots, after.SetListSlots, problems);
            return problems;
        }

        private static void Compare(string label, Dictionary<string, string> before, Dictionary<string, string> after, List<string> problems)
        {
            foreach (var (id, print) in before)
            {
                if (!after.TryGetValue(id, out var now))
                {
                    problems.Add(Lang.T($"{label} {id} fehlt", $"{label} {id} is missing"));
                }
                else if (now != print)
                {
                    problems.Add(Lang.T($"{label} {id}: Verweis geändert ({Short(print)} -> {Short(now)})", $"{label} {id}: reference changed ({Short(print)} -> {Short(now)})"));
                }
            }
        }

        private static string Short(string print) => print.Length > 80 ? print[..80] + "…" : print;

        private static IEnumerable<IPatch> LoadedPatches(IBanks banks)
        {
            return banks.BankCollection.Where(bank => bank.IsLoaded).SelectMany(bank => bank.Patches);
        }

        private static string CombiPrint(ICombi combi)
        {
            var timbres = combi.Timbres.TimbresCollection.Select(timbre =>
                timbre.UsedProgramBank == null || timbre.UsedProgram == null ? "-" : ProgramPrint(timbre.UsedProgram));
            return combi.Name + "[" + string.Join(";", timbres) + "]";
        }

        private static string SlotPrint(ISetListSlot slot)
        {
            return slot.UsedPatch switch
            {
                IProgram program => "P:" + ProgramPrint(program),
                ICombi combi => "C:" + CombiPrint(combi),
                null => "-",
                var other => other.GetType().Name + ":" + other.Name
            };
        }

        private static string ProgramPrint(IProgram program)
        {
            var bytes = program.PcgRoot.Content.AsSpan(program.ByteOffset, program.ByteLength);
            return program.Name + "#" + Convert.ToHexString(SHA1.HashData(bytes))[..12];
        }
    }
}
