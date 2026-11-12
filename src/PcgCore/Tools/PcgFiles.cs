// Gemeinsame Hilfen für Kommandozeile und Browser: PCG laden, Patches und Bänke finden.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Common.Mvvm;
using PcgTools.Model.Common.File;
using PcgTools.Model.Common.Synth.MemoryAndFactory;
using PcgTools.Model.Common.Synth.Meta;

namespace KronosPcg.Tools
{
    public static class PcgFiles
    {
        public static PcgMemory Load(string fileName)
        {
            if (!File.Exists(fileName))
            {
                throw new FileNotFoundException(Lang.T($"Datei nicht gefunden: {fileName}", $"File not found: {fileName}"));
            }

            // Der Original-Leser schreibt Debug-Ausgaben auf die Konsole; die blenden wir aus.
            var console = Console.Out;
            PcgMemory memory;
            try
            {
                Console.SetOut(TextWriter.Null);
                memory = new KorgFileReader().Read(fileName) as PcgMemory;
            }
            finally
            {
                Console.SetOut(console);
            }

            return memory ?? throw new InvalidDataException(Lang.T($"Keine PCG-Datei: {fileName}", $"Not a PCG file: {fileName}"));
        }

        public static IEnumerable<string> Ids(string list)
        {
            return string.IsNullOrWhiteSpace(list)
                ? Enumerable.Empty<string>()
                : list.Split(',').Select(id => id.Trim()).Where(id => id.Length > 0);
        }

        public static IPatch FindPatch(IBanks banks, string id)
        {
            return banks.BankCollection.SelectMany(bank => bank.Patches).FirstOrDefault(patch => patch.Id == id)
                   ?? throw new ArgumentException(Lang.T($"{id} nicht gefunden.", $"{id} not found."));
        }

        public static ObservableCollectionEx<IBank> SelectBanks(IBanks banks, IEnumerable<string> ids)
        {
            var selected = new ObservableCollectionEx<IBank>();
            foreach (var id in ids)
            {
                var bank = banks.BankCollection.FirstOrDefault(candidate => candidate.Id == id)
                           ?? throw new ArgumentException(Lang.T($"Bank {id} nicht gefunden.", $"Bank {id} not found."));
                if (!bank.IsWritable)
                {
                    throw new ArgumentException(Lang.T($"Bank {id} ist schreibgeschützt.", $"Bank {id} is write-protected."));
                }

                bank.IsSelected = true;
                selected.Add(bank);
            }

            return selected;
        }

        public static void CheckSameModel(IPcgMemory source, IPcgMemory target)
        {
            if (source.Model.ModelType != target.Model.ModelType)
            {
                throw new InvalidOperationException(Lang.T(
                    $"Unterschiedliche Modelle: {source.Model.ModelAsString} und {target.Model.ModelAsString}.",
                    $"Different models: {source.Model.ModelAsString} and {target.Model.ModelAsString}."));
            }
        }
    }
}
