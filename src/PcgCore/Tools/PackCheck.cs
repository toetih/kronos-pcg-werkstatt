// Was braucht ein Sound-Paket am Kronos? Eigene Samples (User-Sample-Bank) und EXs-Libraries stecken nicht in der
// PCG; fehlen sie am Kronos, klingen einzelne Sounds anders.
using System.Collections.Generic;
using System.Linq;
using PcgTools.Model.Common.Synth.MemoryAndFactory;
using PcgTools.Model.Common.Synth.Meta;
using PcgTools.Model.Common.Synth.PatchCombis;
using PcgTools.Model.Common.Synth.PatchPrograms;

namespace KronosPcg.Tools
{
    public sealed record PackInfo(List<ICombi> Combis, List<IProgram> Programs, bool UserSamples, List<string> Exs);

    public static class PackCheck
    {
        /// <summary>
        /// Inhalt eines Pakets: alle belegten Combis; die Programs sind die von ihnen genutzten (ohne GM), bei einem
        /// Paket ohne Combis alle belegten Programs.
        /// </summary>
        /// <param name="combiIds">Nur diese Combis des Pakets (null: alle).</param>
        public static PackInfo Analyze(IPcgMemory pcg, ICollection<string> combiIds = null)
        {
            var allCombis = Loaded(pcg.CombiBanks).Cast<ICombi>().Where(combi => !combi.IsEmptyOrInit).ToList();
            var combis = combiIds == null ? allCombis : allCombis.Where(combi => combiIds.Contains(combi.Id)).ToList();
            var programs = allCombis.Count > 0
                ? combis.SelectMany(combi => combi.Timbres.TimbresCollection)
                    .Where(timbre => timbre.UsedProgramBank != null && timbre.UsedProgram != null && timbre.UsedProgramBank.IsLoaded &&
                                     timbre.UsedProgramBank.Type != BankType.EType.Gm && !timbre.UsedProgram.IsEmptyOrInit)
                    .Select(timbre => (IProgram)timbre.UsedProgram).Distinct().ToList()
                : Loaded(pcg.ProgramBanks).Where(patch => !patch.IsEmptyOrInit && ((IProgramBank)patch.Parent).Type != BankType.EType.Gm)
                    .Cast<IProgram>().ToList();

            var userSamples = false;
            var exs = new SortedSet<int>();
            foreach (var program in programs)
            {
                Inspect(program, ref userSamples, exs);
            }

            return new PackInfo(combis, programs, userSamples, exs.Select(number => $"EXs{number}").ToList());
        }

        /// <summary>
        /// Zonen eines Programs ansehen (je Oszillator 8 Zonen zu 22 Bytes ab 2774, zweiter Oszillator 466 Bytes weiter;
        /// Drum-Programs haben keine solchen Zonen). Eine Zone nutzt ein Multisample oder (Typ 2) eine Wave Sequence,
        /// deren 64 Schritte (je 34 Bytes ab Byte 40) genauso aufgebaut sind.
        /// </summary>
        private static void Inspect(IProgram program, ref bool userSamples, SortedSet<int> exs)
        {
            var content = program.PcgRoot.Content;
            for (var osc = 0; osc < WaveSequenceCopier.OscillatorCount(program); osc++)
            {
                for (var zone = 0; zone < 8; zone++)
                {
                    var offset = program.ByteOffset + 2774 + osc * 466 + zone * 22;
                    if (content[offset] == 0)
                    {
                        continue;
                    }

                    if (content[offset] != 2)
                    {
                        InspectSample(content, offset, ref userSamples, exs);
                        continue;
                    }

                    var sequence = program.GetUsedWaveSequence(osc, zone);
                    for (var step = 0; sequence != null && step < 64; step++)
                    {
                        var stepOffset = sequence.ByteOffset + 40 + step * 34;
                        if (sequence.PcgRoot.Content[stepOffset] == 1)
                        {
                            InspectSample(sequence.PcgRoot.Content, stepOffset, ref userSamples, exs);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Woher kommt das Multisample? Bei +1..+16 steht die Kennung der Sample-Bank: beim Kronos-ROM und den EXs beginnt
        /// sie mit "KORG" und endet mit der EXs-Nummer (0 = ROM), alles andere ist eine eigene Sample-Bank.
        /// </summary>
        private static void InspectSample(byte[] content, int offset, ref bool userSamples, SortedSet<int> exs)
        {
            var korg = content[offset + 1] == 'K' && content[offset + 2] == 'O' && content[offset + 3] == 'R' && content[offset + 4] == 'G';
            if (!korg)
            {
                userSamples = true;
                return;
            }

            var number = (content[offset + 15] << 8) | content[offset + 16];
            if (number != 0)
            {
                exs.Add(number);
            }
        }

        private static IEnumerable<IPatch> Loaded(IBanks banks) =>
            banks?.BankCollection.Where(bank => bank.IsLoaded).SelectMany(bank => bank.Patches) ?? Enumerable.Empty<IPatch>();
    }
}
