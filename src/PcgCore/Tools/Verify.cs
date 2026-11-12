using System;
using System.Collections.Generic;
using System.Linq;
using PcgTools.ClipBoard;
using PcgTools.Model.Common.Synth.Meta;
using PcgTools.Model.Common.Synth.PatchCombis;
using PcgTools.Model.Common.Synth.PatchPrograms;

namespace KronosPcg.Tools;

/// <summary>
/// Prüft, ob kopierte Combis auf Programs mit identischem Namen und identischen Daten zeigen.
/// </summary>
public static class Verify
{
    public static List<string> CombiCopies(IList<ICombi> originals, IList<IClipBoardPatch> copies)
    {
        var pairs = copies.Select(copy => (ICombi)copy.PasteDestination).ToList();
        var ordered = copies.Select(copy => originals.First(original => original.Id == copy.OriginalLocation.Id)).ToList();
        return CombiPairs(ordered, pairs);
    }

    /// <param name="renamed">Kopien, die absichtlich umbenannt wurden: Name wird nicht verglichen, nur die Daten.</param>
    public static List<string> CombiPairs(IList<ICombi> originals, IList<ICombi> copies, Func<IPatch, bool> renamed = null)
    {
        renamed ??= _ => false;
        var problems = new List<string>();
        for (var index = 0; index < originals.Count; index++)
        {
            var original = originals[index];
            var copy = copies[index];
            if (original.Name != copy.Name && !renamed(copy))
            {
                problems.Add(Lang.T($"{copy.Id}: Name '{copy.Name}' statt '{original.Name}'",
                    $"{copy.Id}: name '{copy.Name}' instead of '{original.Name}'"));
            }

            var originalTimbres = original.Timbres.TimbresCollection;
            var copyTimbres = copy.Timbres.TimbresCollection;
            for (var timbre = 0; timbre < originalTimbres.Count; timbre++)
            {
                var expected = UsedProgram(originalTimbres[timbre]);
                var actual = UsedProgram(copyTimbres[timbre]);
                if (expected == null && actual == null)
                {
                    continue;
                }

                var label = $"{copy.Id} Timbre {timbre + 1}";
                if (expected == null || actual == null)
                {
                    problems.Add(Lang.T($"{label}: Program fehlt", $"{label}: Program is missing"));
                }
                else if (expected.Name != actual.Name && !renamed(actual))
                {
                    problems.Add(Lang.T($"{label}: '{actual.Id} {actual.Name}' statt '{expected.Name}'",
                        $"{label}: '{actual.Id} {actual.Name}' instead of '{expected.Name}'"));
                }
                else if (IsGm(expected) ? !SameData(expected, actual)
                         : !SameWaveSequences(expected, actual) || !SameDataExceptWaveSequences(expected, actual, renamed(actual)))
                {
                    problems.Add(Lang.T($"{label}: {actual.Id} hat andere Daten als {expected.Id}",
                        $"{label}: {actual.Id} has different data than {expected.Id}"));
                }
            }
        }

        return problems;
    }

    public static List<string> ProgramCopies(IList<IClipBoardPatch> copies)
    {
        return ProgramPairs(copies.Select(copy => (IProgram)copy.OriginalLocation).ToList(),
            copies.Select(copy => (IProgram)copy.PasteDestination).ToList());
    }

    public static List<string> ProgramPairs(IList<IProgram> originals, IList<IProgram> copies, Func<IPatch, bool> renamed = null)
    {
        renamed ??= _ => false;
        var problems = new List<string>();
        for (var index = 0; index < originals.Count; index++)
        {
            if (originals[index].Name != copies[index].Name && !renamed(copies[index]))
            {
                problems.Add(Lang.T($"Program {copies[index].Id}: Name weicht von {originals[index].Id} {originals[index].Name} ab",
                    $"Program {copies[index].Id}: name differs from {originals[index].Id} {originals[index].Name}"));
            }
            else if (!SameWaveSequences(originals[index], copies[index]))
            {
                problems.Add(Lang.T($"Program {copies[index].Id}: nutzt andere Wave Sequences als {originals[index].Id} {originals[index].Name}",
                    $"Program {copies[index].Id}: uses different Wave Sequences than {originals[index].Id} {originals[index].Name}"));
            }
            else if (!SameDataExceptWaveSequences(originals[index], copies[index], renamed(copies[index])))
            {
                problems.Add(Lang.T($"Program {copies[index].Id}: Daten weichen von {originals[index].Id} {originals[index].Name} ab",
                    $"Program {copies[index].Id}: data differs from {originals[index].Id} {originals[index].Name}"));
            }
        }

        return problems;
    }

    /// <summary>GM-Programs liegen fest im Kronos und werden nie kopiert.</summary>
    private static bool IsGm(IProgram program)
    {
        return ((PcgTools.Model.Common.Synth.Meta.IBank)program.Parent).Type == PcgTools.Model.Common.Synth.Meta.BankType.EType.Gm;
    }

    /// <summary>
    /// Gleiche Wave Sequences (Name und Daten) in allen Zonen? Liegt die Wave Sequence der Kopie in einer Bank, die es
    /// in der Datei nicht gibt (Teil-PCG ohne Werks-Bank), zählt derselbe Platz in der Werks-Bank als gleich.
    /// </summary>
    public static bool SameWaveSequences(IProgram a, IProgram b)
    {
        static string Print(PcgTools.Model.Common.Synth.PatchWaveSequences.IWaveSequence sequence) =>
            sequence.Name + "#" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                sequence.PcgRoot.Content.AsSpan(sequence.ByteOffset, sequence.ByteLength)))[..10];
        var first = a.UsedWaveSequences.ToList();
        var second = b.UsedWaveSequences.ToList();
        if (first.Count != second.Count)
        {
            return false;
        }

        for (var index = 0; index < first.Count; index++)
        {
            var loadedA = ((PcgTools.Model.Common.Synth.Meta.IBank)first[index].Parent).IsLoaded;
            var loadedB = ((PcgTools.Model.Common.Synth.Meta.IBank)second[index].Parent).IsLoaded;
            if (loadedA && loadedB ? Print(first[index]) != Print(second[index])
                    : first[index].Id != second[index].Id || !WaveSequenceCopier.IsFactory(first[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static IProgram UsedProgram(ITimbre timbre)
    {
        return timbre.UsedProgramBank == null ? null : timbre.UsedProgram;
    }

    /// <summary>
    /// Wie SameData, aber die Wave-Sequence-Nummern der Zonen (Zone +18/+19) dürfen abweichen,
    /// weil Wave Sequences beim Kopieren auf andere Plätze wandern können. Dass sie inhaltlich
    /// gleich sind, prüft SameWaveSequences.
    /// </summary>
    /// <param name="ignoreName">Umbenannt: die Namens-Bytes am Anfang nicht vergleichen.</param>
    private static bool SameDataExceptWaveSequences(IProgram expected, IProgram actual, bool ignoreName = false)
    {
        var a = expected.PcgRoot.Content.AsSpan(expected.ByteOffset, expected.ByteLength).ToArray();
        var b = actual.PcgRoot.Content.AsSpan(actual.ByteOffset, actual.ByteLength).ToArray();
        if (ignoreName)
        {
            Array.Clear(a, 0, expected.MaxNameLength);
            Array.Clear(b, 0, actual.MaxNameLength);
        }

        for (var osc = 0; osc < KronosPcg.Tools.WaveSequenceCopier.OscillatorCount(expected); osc++)
        {
            for (var zone = 0; zone < 8; zone++)
            {
                var offset = 2774 + osc * (3240 - 2774) + zone * (2796 - 2774);
                if (a[offset] == 2)   // Zone nutzt eine Wave Sequence
                {
                    a[offset + 18] = b[offset + 18] = 0;
                    a[offset + 19] = b[offset + 19] = 0;
                }
            }
        }

        return a.AsSpan().SequenceEqual(b);
    }

    private static bool SameData(IProgram expected, IProgram actual)
    {
        var a = expected.PcgRoot.Content.AsSpan(expected.ByteOffset, expected.ByteLength);
        var b = actual.PcgRoot.Content.AsSpan(actual.ByteOffset, actual.ByteLength);
        return a.SequenceEqual(b);
    }
}
