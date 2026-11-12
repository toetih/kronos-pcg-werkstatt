// Wave Sequences beim Kopieren von Programs mitnehmen. PCG Tools kopiert sie nicht; die eingefügten
// Programs zeigen sonst auf die Wave Sequence, die im Ziel zufällig unter derselben Nummer liegt.
using System;
using System.Collections.Generic;
using System.Linq;
using PcgTools.Model.Common.Synth.MemoryAndFactory;
using PcgTools.Model.Common.Synth.Meta;
using PcgTools.Model.Common.Synth.OldParameters;
using PcgTools.Model.Common.Synth.PatchPrograms;
using PcgTools.Model.Common.Synth.PatchWaveSequences;

namespace KronosPcg.Tools
{
    public class WaveSequenceCopier
    {
        public List<string> Copied { get; } = new();
        public List<string> Reused { get; } = new();
        public List<string> Problems { get; } = new();

        private readonly IPcgMemory _target;
        private readonly List<IBank> _freeBanks;
        private readonly Dictionary<IWaveSequence, IWaveSequence> _map = new();

        /// <param name="target">Ziel-PCG</param>
        /// <param name="freeBanks">Wave-Sequence-Bänke im Ziel, in die neue Wave Sequences kommen (freie Plätze)</param>
        public WaveSequenceCopier(IPcgMemory target, IEnumerable<IBank> freeBanks)
        {
            _target = target;
            _freeBanks = freeBanks.ToList();
        }

        /// <summary>
        /// Sorgt dafür, dass <paramref name="pasted"/> im Ziel dieselben Wave Sequences nutzt wie
        /// <paramref name="source"/>: gleiche Wave Sequence im Ziel wiederverwenden, sonst auf einen
        /// freien Platz kopieren, und die Verweise der Zonen umstellen.
        /// </summary>
        public void Apply(IProgram source, IProgram pasted)
        {
            for (var osc = 0; osc < OscillatorCount(source); osc++)
            {
                for (var zone = 0; zone < 8; zone++)
                {
                    var sourceSequence = source.GetUsedWaveSequence(osc, zone);
                    if (sourceSequence == null)
                    {
                        continue;
                    }

                    // Werks-Wave-Sequence (Bank INT), und das Ziel ist eine Teil-PCG ohne diese Bank: Der Verweis bleibt,
                    // wie er ist. Die Werks-Bank liegt in jedem Kronos; kopiert wird sie nie.
                    if (IsFactory(sourceSequence) && !FactoryBankInTarget)
                    {
                        var note = Lang.T($"{sourceSequence.Id} {sourceSequence.Name} (Werks-Wave-Sequence, Verweis bleibt)",
                            $"{sourceSequence.Id} {sourceSequence.Name} (factory wave sequence, reference kept)");
                        if (!Reused.Contains(note))
                        {
                            Reused.Add(note);
                        }

                        continue;
                    }

                    var targetSequence = Resolve(sourceSequence);
                    if (targetSequence == null)
                    {
                        var problem = Lang.T($"{sourceSequence.Id} {sourceSequence.Name} (für {source.Name.Trim()})",
                            $"{sourceSequence.Id} {sourceSequence.Name} (for {source.Name.Trim()})");
                        if (!Problems.Contains(problem))
                        {
                            Problems.Add(problem);
                        }

                        continue;
                    }

                    pasted.SetWaveSequence(osc, zone, targetSequence);
                }
            }
        }

        public static bool IsFactory(IWaveSequence sequence) => ((IBank)sequence.Parent).Id == "INT";

        private bool FactoryBankInTarget => _target.WaveSequenceBanks.BankCollection.Any(bank => bank.Id == "INT" && bank.IsLoaded);

        /// <summary>Anzahl der Oszillatoren, deren Zonen Wave Sequences nutzen können.</summary>
        public static int OscillatorCount(IProgram program)
        {
            return (string)program.GetParam(ParameterNames.ProgramParameterName.OscMode).Value switch
            {
                "Single" => 1,
                "Double" => 2,
                _ => 0
            };
        }

        private IWaveSequence Resolve(IWaveSequence source)
        {
            if (_map.TryGetValue(source, out var mapped))
            {
                return mapped;
            }

            var all = _target.WaveSequenceBanks.BankCollection.Where(bank => bank.IsLoaded)
                .SelectMany(bank => bank.Patches).Cast<IWaveSequence>().ToList();

            // 1. Gleiche Wave Sequence am selben Platz, 2. irgendwo im Ziel.
            var same = all.FirstOrDefault(candidate => candidate.Id == source.Id && Same(candidate, source))
                       ?? all.FirstOrDefault(candidate => Same(candidate, source));
            if (same != null)
            {
                Reused.Add(Lang.T($"{source.Id} {source.Name} -> {same.Id} (vorhanden)",
                    $"{source.Id} {source.Name} -> {same.Id} (already there)"));
                return _map[source] = same;
            }

            // 3. Auf den ersten freien Platz der gewählten Bänke kopieren.
            var free = _freeBanks.SelectMany(bank => bank.Patches).Cast<IWaveSequence>()
                .FirstOrDefault(candidate => candidate.IsEmptyOrInit && !_map.ContainsValue(candidate));
            if (free == null)
            {
                return null;
            }

            Array.Copy(source.PcgRoot.Content, source.ByteOffset, free.PcgRoot.Content, free.ByteOffset, source.ByteLength);
            Copied.Add($"{source.Id} {source.Name} -> {free.Id}");
            return _map[source] = free;
        }

        public static bool Same(IPatch a, IPatch b)
        {
            return a.ByteLength == b.ByteLength &&
                   a.PcgRoot.Content.AsSpan(a.ByteOffset, a.ByteLength).SequenceEqual(b.PcgRoot.Content.AsSpan(b.ByteOffset, b.ByteLength));
        }
    }
}
