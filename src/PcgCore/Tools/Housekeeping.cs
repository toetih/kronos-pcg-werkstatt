// Aufräumen: Welche Programs nutzt nichts in der Datei (keine Combi, kein Set-List-Slot, kein Drum Track eines anderen
// Programs), und welche Sounds liegen doppelt in der Datei (byte-gleich, samt Name)?
// "Ungenutzt" heißt nicht "unwichtig": Ein Program, das man direkt im Program-Modus spielt, nutzt auch keine Combi.
// Deshalb wird nie von selbst gelöscht; die Oberfläche zeigt die Namen und fragt nach.
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
    public static class Housekeeping
    {
        /// <summary>Alle Programs, auf die irgendetwas in der Datei zeigt (auch stumme Timbres zählen: Die Combi verweist darauf).</summary>
        public static HashSet<IPatch> UsedPrograms(IPcgMemory pcg)
        {
            var used = new HashSet<IPatch>();
            foreach (var combi in Loaded(pcg.CombiBanks).Cast<ICombi>().Where(combi => !combi.IsEmptyOrInit))
            {
                foreach (var timbre in combi.Timbres.TimbresCollection.Where(timbre => timbre.UsedProgramBank != null && timbre.UsedProgram != null))
                {
                    used.Add(timbre.UsedProgram);
                }
            }

            foreach (var slot in Loaded(pcg.SetLists).Cast<ISetListSlot>().Where(slot => !slot.IsEmptyOrInit && slot.UsedPatch is IProgram))
            {
                used.Add(slot.UsedPatch);
            }

            foreach (var program in Loaded(pcg.ProgramBanks).Cast<IProgram>().Where(program => !program.IsEmptyOrInit))
            {
                if (DrumTrackProgram(program) is { } drums)
                {
                    used.Add(drums);
                }
            }

            return used;
        }

        /// <summary>Program, das der Drum Track dieses Programs spielt (null, wenn nicht lesbar).</summary>
        public static IProgram DrumTrackProgram(IProgram program)
        {
            try
            {
                return (program as Program)?.UsedDrumTrackProgram;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Belegte, beschreibbare Programs, auf die nichts zeigt, in Dateireihenfolge.</summary>
        public static List<IProgram> UnusedPrograms(IPcgMemory pcg)
        {
            var used = UsedPrograms(pcg);
            return pcg.ProgramBanks.BankCollection.Where(bank => bank.IsLoaded && bank.IsWritable && bank.Type != BankType.EType.Gm)
                .SelectMany(bank => bank.Patches).Cast<IProgram>().Where(program => !program.IsEmptyOrInit && !used.Contains(program)).ToList();
        }

        /// <summary>
        /// Doppelte Sounds: Platz → der erste Platz in der Datei mit byte-gleichen Daten (samt Name). Der erste Platz
        /// selbst steht nicht in der Liste.
        /// </summary>
        public static Dictionary<string, string> Duplicates(IBanks banks)
        {
            var first = new Dictionary<string, string>();
            var duplicates = new Dictionary<string, string>();
            foreach (var patch in banks.BankCollection.Where(bank => bank.IsLoaded && bank.Type != BankType.EType.Gm).SelectMany(bank => bank.Patches)
                         .Where(patch => !patch.IsEmptyOrInit))
            {
                var print = Convert.ToHexString(SHA1.HashData(patch.PcgRoot.Content.AsSpan(patch.ByteOffset, patch.ByteLength)));
                if (first.TryGetValue(print, out var original))
                {
                    duplicates[patch.Id] = original;
                }
                else
                {
                    first[print] = patch.Id;
                }
            }

            return duplicates;
        }

        private static IEnumerable<IPatch> Loaded(IBanks banks) =>
            banks?.BankCollection.Where(bank => bank.IsLoaded).SelectMany(bank => bank.Patches) ?? Enumerable.Empty<IPatch>();
    }
}
