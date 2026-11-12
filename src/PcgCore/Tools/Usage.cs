// Wer nutzt einen Sound? Vor dem Löschen zeigen, welche Combis (Timbres) und Set-List-Slots danach anders klingen.
using System.Collections.Generic;
using System.Linq;
using PcgTools.Model.Common.Synth.MemoryAndFactory;
using PcgTools.Model.Common.Synth.Meta;
using PcgTools.Model.Common.Synth.PatchCombis;
using PcgTools.Model.Common.Synth.PatchPrograms;
using PcgTools.Model.Common.Synth.PatchSetLists;

namespace KronosPcg.Tools
{
    public static class Usage
    {
        /// <summary>Lesbare Liste der Nutzer, z. B. "U-A010 Final Countdown (Timbre 3, 4)" oder "Set List 000 Slot 012".</summary>
        public static List<string> UsersOf(IPcgMemory pcg, IPatch patch)
        {
            var users = new List<string>();
            if (patch is IProgram && pcg.CombiBanks != null)
            {
                foreach (var combi in pcg.CombiBanks.BankCollection.Where(bank => bank.IsLoaded).SelectMany(bank => bank.Patches)
                             .Cast<ICombi>().Where(combi => !combi.IsEmptyOrInit))
                {
                    var timbres = combi.Timbres.TimbresCollection
                        .Select((timbre, index) => (timbre, number: index + 1))
                        .Where(item => item.timbre.UsedProgramBank != null && item.timbre.UsedProgram == patch)
                        .Select(item => item.number).ToList();
                    if (timbres.Count > 0)
                    {
                        users.Add($"{combi.Id} {combi.Name} (Timbre {string.Join(", ", timbres)})");
                    }
                }
            }

            if (patch is IProgram && pcg.ProgramBanks != null)
            {
                users.AddRange(pcg.ProgramBanks.BankCollection.Where(bank => bank.IsLoaded).SelectMany(bank => bank.Patches).Cast<IProgram>()
                    .Where(program => !program.IsEmptyOrInit && program != patch && Housekeeping.DrumTrackProgram(program) == patch)
                    .Select(program => Lang.T($"Drum Track von {program.Id} {program.Name}", $"Drum Track of {program.Id} {program.Name}")));
            }

            if (pcg.SetLists != null)
            {
                foreach (var slot in pcg.SetLists.BankCollection.Where(bank => bank.IsLoaded).SelectMany(bank => bank.Patches)
                             .Cast<ISetListSlot>().Where(slot => !slot.IsEmptyOrInit && slot.UsedPatch == patch))
                {
                    users.Add($"Set List {((IBank)slot.Parent).Id} Slot {slot.Index:000}");
                }
            }

            return users;
        }
    }
}
