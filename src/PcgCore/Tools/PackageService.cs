// Teil-PCG mit ausgewählten Bänken schreiben (z. B. ein Einzelsound-Paket für Kunden). Beim Laden am Kronos werden
// nur diese Bänke ersetzt. Vor dem Schreiben wird gemeldet, was den Combis im Paket fehlen würde; danach wird die
// Teil-PCG neu eingelesen und Bank für Bank mit dem Original verglichen.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PcgTools.Model.Common.Synth.MemoryAndFactory;
using PcgTools.Model.Common.Synth.Meta;
using PcgTools.Model.Common.Synth.OldParameters;
using PcgTools.Model.Common.Synth.PatchCombis;
using PcgTools.Model.Common.Synth.PatchPrograms;

namespace KronosPcg.Tools
{
    public sealed class PackageRequest
    {
        public string File { get; init; }
        public string OutFile { get; init; }
        public IReadOnlyList<string> CombiBanks { get; init; } = Array.Empty<string>();

        /// <summary>null: alle User-Program-Bänke, die aktive Timbres der Combis nutzen.</summary>
        public IReadOnlyList<string> ProgramBanks { get; init; }

        /// <summary>null: alle User-Wave-Sequence-Bänke, die die Programs nutzen.</summary>
        public IReadOnlyList<string> WaveSequenceBanks { get; init; }
    }

    public sealed class PackageResult
    {
        public List<string> ProgramBanks { get; } = new();
        public List<string> CombiBanks { get; } = new();
        public List<string> WaveSequenceBanks { get; } = new();

        /// <summary>User-Programs aktiver Timbres, die nicht im Paket sind (Combi klingt beim Kunden anders).</summary>
        public SortedSet<string> MissingPrograms { get; } = new();

        /// <summary>Werks-Programs aktiver Timbres (hat jeder Kronos, solange der Kunde sie nicht überschrieben hat).</summary>
        public SortedSet<string> FactoryPrograms { get; } = new();

        public int MutedOutside { get; set; }
        public SortedSet<string> MissingWaveSequences { get; } = new();
        public long Size { get; set; }
        public long OriginalSize { get; set; }
    }

    public static class PackageService
    {
        public static PackageResult Run(PackageRequest request)
        {
            SafeOutput.Check(request.OutFile, request.File);
            var pcg = PcgFiles.Load(request.File);
            var result = new PackageResult { OriginalSize = pcg.Content.Length };
            var combiBanks = request.CombiBanks.ToList();
            var combis = pcg.CombiBanks.BankCollection.Where(bank => combiBanks.Contains(bank.Id)).SelectMany(bank => bank.Patches)
                .Cast<ICombi>().Where(combi => !combi.IsEmptyOrInit).ToList();

            var programBanks = request.ProgramBanks?.ToList() ?? combis
                .SelectMany(combi => combi.Timbres.TimbresCollection)
                .Where(timbre => timbre.UsedProgramBank != null && timbre.UsedProgramBank.IsLoaded &&
                                 timbre.UsedProgramBank.Id.StartsWith("U-") && Status(timbre) != "Off")
                .Select(timbre => timbre.UsedProgramBank.Id).Distinct()
                .OrderBy(id => Array.IndexOf(PartialPcgExporter.ProgramBankOrder, id)).ToList();

            // Was brauchen die Combis, das nicht im Paket landet?
            foreach (var combi in combis)
            {
                foreach (var timbre in combi.Timbres.TimbresCollection.Where(timbre => timbre.UsedProgramBank != null && timbre.UsedProgram != null))
                {
                    var bank = timbre.UsedProgramBank;
                    if (bank.Type == BankType.EType.Gm || programBanks.Contains(bank.Id))
                    {
                        continue;
                    }

                    if (Status(timbre) == "Off")
                    {
                        result.MutedOutside++;
                        continue;
                    }

                    var name = bank.IsLoaded ? timbre.UsedProgram.Name : Lang.T("(nicht in dieser Datei)", "(not in this file)");
                    var entry = $"{timbre.UsedProgram.Id} {name} (in {combi.Id} {combi.Name})";
                    (bank.Id.StartsWith("I-") ? result.FactoryPrograms : result.MissingPrograms).Add(entry);
                }
            }

            // Wave Sequences, die die Programs nutzen. Werks-Wave-Sequences (INT) hat jeder Kronos.
            var usedSequences = pcg.ProgramBanks.BankCollection.Where(bank => programBanks.Contains(bank.Id))
                .SelectMany(bank => bank.Patches).Cast<IProgram>().Where(program => !program.IsEmptyOrInit)
                .SelectMany(program => program.UsedWaveSequences.Distinct().Select(sequence => (program, sequence)))
                .Where(use => ((IBank)use.sequence.Parent).Id != "INT")
                .ToList();
            var waveBanks = request.WaveSequenceBanks?.ToList() ?? usedSequences.Select(use => ((IBank)use.sequence.Parent).Id).Distinct()
                .OrderBy(id => Array.IndexOf(PartialPcgExporter.WaveSequenceBankOrder, id)).ToList();
            foreach (var use in usedSequences.Where(use => !waveBanks.Contains(((IBank)use.sequence.Parent).Id)))
            {
                result.MissingWaveSequences.Add(Lang.T($"{use.program.Id} {use.program.Name} nutzt Wave Sequence {use.sequence.Id} {use.sequence.Name}",
                    $"{use.program.Id} {use.program.Name} uses Wave Sequence {use.sequence.Id} {use.sequence.Name}"));
            }

            var bytes = PartialPcgExporter.Export(pcg.Content, programBanks, combiBanks, waveBanks);
            File.WriteAllBytes(request.OutFile, bytes);
            result.Size = bytes.Length;
            result.ProgramBanks.AddRange(programBanks);
            result.CombiBanks.AddRange(combiBanks);
            result.WaveSequenceBanks.AddRange(waveBanks);

            // Gegenprobe: Teil-PCG einlesen und jede Bank Byte für Byte mit dem Original vergleichen.
            var written = PcgFiles.Load(request.OutFile);
            var problems = new List<string>();
            CompareBanks(pcg.ProgramBanks, written.ProgramBanks, programBanks, problems);
            CompareBanks(pcg.CombiBanks, written.CombiBanks, combiBanks, problems);
            if (waveBanks.Count > 0)
            {
                CompareBanks(pcg.WaveSequenceBanks, written.WaveSequenceBanks, waveBanks, problems);
            }

            // Jedes exportierte Program muss im Paket dieselben Wave Sequences nutzen (Namen nur für die im Paket).
            foreach (var bankId in programBanks)
            {
                var before = pcg.ProgramBanks.BankCollection.First(bank => bank.Id == bankId).Patches.Cast<IProgram>().ToList();
                var after = written.ProgramBanks.BankCollection.First(bank => bank.Id == bankId).Patches.Cast<IProgram>().ToList();
                for (var index = 0; index < before.Count; index++)
                {
                    string Print(IProgram program) => string.Join(",", program.UsedWaveSequences.Select(sequence =>
                        waveBanks.Contains(((IBank)sequence.Parent).Id) ? sequence.Id + " " + sequence.Name : sequence.Id));
                    if (Print(before[index]) != Print(after[index]))
                    {
                        problems.Add($"{before[index].Id}: Wave Sequences {Print(before[index])} -> {Print(after[index])}");
                    }
                }
            }

            if (problems.Count > 0)
            {
                File.Delete(request.OutFile);
                throw new CopyException(Lang.T("Gegenprobe des Pakets fehlgeschlagen. Die Datei wurde wieder gelöscht.",
                    "Verification of the pack failed. The file was deleted again."), problems);
            }

            return result;
        }

        private static string Status(ITimbre timbre) => (string)timbre.GetParam(ParameterNames.TimbreParameterName.Status).Value;

        private static void CompareBanks(IBanks original, IBanks written, List<string> bankIds, List<string> problems)
        {
            foreach (var bankId in bankIds)
            {
                var a = original.BankCollection.First(bank => bank.Id == bankId);
                var b = written.BankCollection.FirstOrDefault(bank => bank.Id == bankId);
                if (b == null || !b.IsLoaded)
                {
                    problems.Add(Lang.T($"Bank {bankId} fehlt in der Teil-PCG", $"Bank {bankId} is missing in the partial PCG"));
                    continue;
                }

                for (var index = 0; index < a.Patches.Count; index++)
                {
                    var pa = a.Patches[index];
                    var pb = b.Patches[index];
                    if (!pa.PcgRoot.Content.AsSpan(pa.ByteOffset, pa.ByteLength).SequenceEqual(pb.PcgRoot.Content.AsSpan(pb.ByteOffset, pb.ByteLength)))
                    {
                        problems.Add(Lang.T($"{pa.Id} {pa.Name}: Daten weichen ab", $"{pa.Id} {pa.Name}: data differs"));
                    }
                }
            }

            foreach (var bank in written.BankCollection.Where(bank => bank.IsLoaded && bank.Type != BankType.EType.Gm && !bankIds.Contains(bank.Id)))
            {
                problems.Add(Lang.T($"Bank {bank.Id} ist unerwartet enthalten", $"Bank {bank.Id} is included unexpectedly"));
            }
        }
    }
}
