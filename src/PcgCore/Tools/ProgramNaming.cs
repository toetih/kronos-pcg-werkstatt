// Programs nach der Combi benennen, in der sie spielen: Combi-Name + Nummern der aktiven Timbres
// ("Cosa della vita 01 03 04"). So sieht man beim Laden von Hand am Kronos, wohin ein Sound gehört.
using System;
using System.Collections.Generic;
using System.Linq;
using PcgTools.Model.Common.Synth.Meta;
using PcgTools.Model.Common.Synth.OldParameters;
using PcgTools.Model.Common.Synth.PatchCombis;

namespace KronosPcg.Tools
{
    public static class ProgramNaming
    {
        /// <summary>
        /// Neue Namen der Programs einer Combi (Program-Id -> Name). Nur Programs aktiver Timbres, die mitkopiert
        /// werden (nicht GM, belegt); stumme Timbres zählen nicht mit, ihre Programs behalten den Namen.
        /// </summary>
        public static Dictionary<string, string> ForCombi(ICombi combi, string combiName = null)
        {
            var name = (combiName ?? combi.Name).Trim();
            return combi.Timbres.TimbresCollection
                .Select((timbre, index) => (timbre, number: index + 1))
                .Where(item => item.timbre.UsedProgramBank != null && item.timbre.UsedProgram != null &&
                               item.timbre.UsedProgramBank.IsLoaded && item.timbre.UsedProgramBank.Type != BankType.EType.Gm &&
                               !item.timbre.UsedProgram.IsEmptyOrInit &&
                               (string)item.timbre.GetParam(ParameterNames.TimbreParameterName.Status).Value != "Off")
                .GroupBy(item => item.timbre.UsedProgram.Id)
                .ToDictionary(group => group.Key, group => Name(name, group.Select(item => item.number).ToList()));
        }

        /// <summary>
        /// Combi-Name + Timbre-Nummern in 24 Zeichen. Gekürzt wird an Wortgrenzen (so viele ganze Wörter wie passen,
        /// mindestens das erste Wort, notfalls angeschnitten). Blieben so weniger als zwei Wörter übrig, werden
        /// aufeinanderfolgende Nummern zu Bereichen zusammengefasst ("01-04 07"), wenn dadurch mehr vom Namen passt.
        /// </summary>
        public static string Name(string combiName, List<int> timbres, int maxLength = 24)
        {
            var words = combiName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var single = string.Join(" ", timbres.Select(number => number.ToString("00")));
            var (prefix, count) = Fit(words, maxLength - single.Length - 1);
            if (count < Math.Min(2, words.Length) || single.Length > maxLength - 6)
            {
                var ranges = Ranges(timbres);
                var (rangePrefix, rangeCount) = Fit(words, maxLength - ranges.Length - 1);
                if (rangeCount > count || single.Length > maxLength - 6)
                {
                    return $"{rangePrefix} {ranges}";
                }
            }

            return $"{prefix} {single}";
        }

        /// <summary>So viele ganze Wörter wie in <paramref name="budget"/> Zeichen passen (mindestens das erste, notfalls gekürzt).</summary>
        private static (string Prefix, int Words) Fit(string[] words, int budget)
        {
            if (words.Length == 0)
            {
                return ("", 0);
            }

            var prefix = words[0].Length > budget ? words[0][..Math.Max(budget, 1)] : words[0];
            var count = 1;
            foreach (var word in words.Skip(1))
            {
                if (prefix.Length + 1 + word.Length > budget)
                {
                    break;
                }

                prefix += " " + word;
                count++;
            }

            return (prefix, count);
        }

        /// <summary>Aufeinanderfolgende Nummern zusammenfassen: 1,2,3,4,7 -> "01-04 07".</summary>
        private static string Ranges(List<int> numbers)
        {
            var parts = new List<string>();
            for (var index = 0; index < numbers.Count; index++)
            {
                var start = numbers[index];
                while (index + 1 < numbers.Count && numbers[index + 1] == numbers[index] + 1)
                {
                    index++;
                }

                parts.Add(start == numbers[index] ? $"{start:00}" : $"{start:00}-{numbers[index]:00}");
            }

            return string.Join(" ", parts);
        }
    }
}
