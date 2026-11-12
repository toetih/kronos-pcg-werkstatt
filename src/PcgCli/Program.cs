// Kommandozeilen-Werkzeug für Kronos-PCG-Dateien auf Basis des PCG-Tools-Kerns.
// Grundsatz: Eingabedateien werden nur gelesen, das Ergebnis ist immer eine neue Datei.
using Common.Mvvm;
using PcgTools.ClipBoard;
using PcgTools.Model.Common.File;
using PcgTools.Model.Common.Synth.MemoryAndFactory;
using PcgTools.Model.Common.Synth.Meta;
using PcgTools.Model.Common.Synth.PatchCombis;
using PcgTools.Model.Common.Synth.PatchPrograms;
using PcgTools.Model.Common.Synth.PatchSetLists;
using PcgTools.Model.Common.Synth.PatchSorting;
using PcgTools.Properties;
using KronosPcg.Tools;
using PcgTools.Model.Common.Synth.OldParameters;
using PcgTools.ViewModels;
using PcgTools.ViewModels.Commands.PcgCommands;

namespace PcgCli;

public static partial class Program
{
    // Zusätzliche Befehle aus Dateien, die es nur in der privaten Fassung gibt (z. B. der Nautilus-Umsteller).
    private static readonly Dictionary<string, Func<Options, int>> Extra = new();
    private static readonly List<string> ExtraUsage = new();

    static partial void RegisterExtra();

    public static int Main(string[] args)
    {
        RegisterExtra();
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        try
        {
            var options = Options.Parse(args.Skip(1));
            switch (args[0])
            {
                case "info":
                    return Info(options.Positional(0));
                case "list":
                    return List(options.Positional(0), options.Positional(1), options.Get("bank"));
                case "copy":
                case "copy-combis":
                    return Copy(options);
                case "export":
                    return Export(options);
                case "unused":
                    return Unused(options.Positional(0));
                case "samples":
                    return Samples(options.Positional(0), options.Get("ksc"));
                case "zones":
                    return Zones(options.Positional(0), options.Positional(1));
                case "where":
                    return Where(options.Positional(0), options.Positional(1));
                case "sort":
                    return Sort(options);
                case "attach-samples":
                    return AttachSamples(options);
                case "pack-samples":
                    return PackSamples(options);
                case "packcheck":
                    return PackCheckCommand(options.Positional(0));
                default:
                    if (Extra.TryGetValue(args[0], out var extra))
                    {
                        return extra(options);
                    }

                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception exc)
        {
            Console.Error.WriteLine($"Fehler: {exc.Message}");
            return 2;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            pcg info <datei.PCG>
            pcg list <datei.PCG> programs|combis [--bank I-A]
            pcg copy --from <quelle.PCG> --to <ziel.PCG> --out <neu.PCG>
                     [--combis I-A000,I-A005 --combi-bank U-A]
                     [--programs I-B003,I-C010]
                     --program-banks U-B,U-D [--overwrite-filled] [--always-copy-programs]
                     [--combi-start U-A010] [--program-starts U-B005,U-D000]
                     [--renames "C:U-A010=Neuer Name|P:U-B002=Anderer Name"] [--deletes C:U-A005,P:U-B002]
            pcg attach-samples <eingefügt.PCG> --programs U-FF010 --pack-ksc <paket.KSC> --bank-ksc <eigene.KSC>
                     --out <neu.PCG> --out-dir <ordner für neue KSC und Samples>
            pcg pack-samples <paket-voll.PCG> --programs U-FF000 --bank-ksc <quelle.KSC> --name <Name> --out <neu.PCG> --out-dir <ordner>
            pcg export <datei.PCG> --out <teil.PCG> --combi-banks U-G --program-banks auto [--wave-banks auto]
            pcg sort <datei.PCG> --out <neu.PCG> --kind programs|combis --banks U-A,U-B
                     [--order name|category]
            """);
        ExtraUsage.ForEach(Console.WriteLine);
    }

    private static PcgMemory Load(string fileName) => PcgFiles.Load(fileName);

    private static int Info(string fileName)
    {
        var pcg = Load(fileName);
        Console.WriteLine($"Datei:   {fileName}");
        Console.WriteLine($"Modell:  {pcg.Model.ModelAsString}, OS {pcg.Model.OsVersionString}");
        PrintBanks("Program-Bänke", pcg.ProgramBanks);
        PrintBanks("Combi-Bänke", pcg.CombiBanks);
        PrintBanks("Set Lists", pcg.SetLists);
        return 0;
    }

    private static void PrintBanks(string title, IBanks banks)
    {
        if (banks == null)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine(title);
        foreach (var bank in banks.BankCollection.Where(bank => bank.IsLoaded && (bank is not ISetList || bank.IsFilled)))
        {
            var filled = bank.Patches.Count(patch => !patch.IsEmptyOrInit);
            var synthesis = bank is IProgramBank programBank ? $" {programBank.BankSynthesisType}" : string.Empty;
            var writable = bank.IsWritable ? string.Empty : " (schreibgeschützt)";
            Console.WriteLine($"  {bank.Id,-6}{synthesis,-10} {filled,4}/{bank.Patches.Count} belegt{writable}");
        }
    }

    private static int List(string fileName, string kind, string bankId)
    {
        var pcg = Load(fileName);
        IBanks banks = kind switch
        {
            "programs" => pcg.ProgramBanks,
            "combis" => pcg.CombiBanks,
            _ => throw new ArgumentException("Typ muss 'programs' oder 'combis' sein.")
        };

        foreach (var bank in banks.BankCollection.Where(bank => bank.IsLoaded && (bankId == null || bank.Id == bankId)))
        {
            foreach (var patch in bank.Patches.Where(patch => !patch.IsEmptyOrInit))
            {
                Console.WriteLine($"{patch.Id,-9} {patch.Name}");
                if (patch is ICombi combi)
                {
                    foreach (var line in DescribeTimbres(combi))
                    {
                        Console.WriteLine($"            {line}");
                    }
                }
            }
        }

        return 0;
    }

    private static IEnumerable<string> DescribeTimbres(ICombi combi)
    {
        var index = 1;
        foreach (var timbre in combi.Timbres.TimbresCollection)
        {
            var program = timbre.UsedProgramBank == null ? null : timbre.UsedProgram;
            if (program != null)
            {
                var status = (string)timbre.GetParam(ParameterNames.TimbreParameterName.Status).Value;
                var name = ((IBank)program.Parent).IsLoaded ? program.Name : "(nicht in dieser Datei)";
                yield return $"Timbre {index,2}: {status,-4} {program.Id,-9} {name}";
            }

            index++;
        }
    }

    private static int Copy(Options options)
    {
        CopyResult result;
        try
        {
            result = CopyService.Run(new CopyRequest
            {
                SourceFile = options.Require("from"),
                TargetFile = options.Require("to"),
                OutFile = options.Require("out"),
                Combis = Ids(options.Get("combis")).ToList(),
                Programs = Ids(options.Get("programs")).ToList(),
                CombiBanks = Ids(options.Get("combi-banks") ?? options.Get("combi-bank")).ToList(),
                ProgramBanks = Ids(options.Require("program-banks")).ToList(),
                WaveSequenceBanks = Ids(options.Get("wave-banks") ?? "U-AA").ToList(),
                OverwriteFilled = options.Has("overwrite-filled"),
                AlwaysCopyPrograms = options.Has("always-copy-programs"),
                CombiStart = options.Get("combi-start"),
                ProgramStarts = Ids(options.Get("program-starts")).ToList(),
                Deletes = Ids(options.Get("deletes")).ToList(),
                Renames = (options.Get("renames") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries)
                    .Select(pair => pair.Split('=', 2)).ToDictionary(pair => pair[0].Trim(), pair => pair[1]),
            });
        }
        catch (CopyException exception)
        {
            foreach (var problem in exception.Problems)
            {
                Console.Error.WriteLine($"  PROBLEM: {problem}");
            }

            throw;
        }

        Console.WriteLine($"Zwischenablage: {result.Combis.Count} Combi(s), {result.Programs.Count} Program(s)");
        result.WaveSequencesCopied.ForEach(line => Console.WriteLine($"  Wave Sequence kopiert: {line}"));
        result.WaveSequencesReused.ForEach(line => Console.WriteLine($"  Wave Sequence {line}"));
        foreach (var program in result.Programs)
        {
            Console.WriteLine($"  Program {program.From,-9} -> {program.To,-9} {program.Name}");
        }

        foreach (var combi in result.Combis)
        {
            Console.WriteLine($"  Combi   {combi.From,-9} -> {combi.To,-9} {combi.Name}");
        }

        result.Deleted.ForEach(deleted => Console.WriteLine($"  Gelöscht  {deleted.Key[2..],-9} {deleted.Name}"));
        result.Renamed.ForEach(rename => Console.WriteLine($"  Umbenannt {rename.Key[2..],-9} {rename.OldName} -> {rename.NewName}"));
        Console.WriteLine($"OK: {options.Require("out")} geschrieben und geprüft. Originaldateien unverändert.");
        return 0;
    }

    private static IEnumerable<string> Ids(string list) => PcgFiles.Ids(list);

    /// <summary>
    /// Teil-PCG mit ausgewählten Program- und Combi-Bänken exportieren (z. B. für Downloads).
    /// Beim Laden am Kronos werden nur diese Bänke überschrieben; Global bleibt unberührt.
    /// </summary>
    private static int Export(Options options)
    {
        var fileName = options.Positional(0) ?? throw new ArgumentException("Datei fehlt.");
        var outFile = options.Require("out");
        var waveOption = options.Get("wave-banks") ?? "auto";
        PackageResult result;
        try
        {
            result = PackageService.Run(new PackageRequest
            {
                File = fileName,
                OutFile = outFile,
                CombiBanks = Ids(options.Require("combi-banks")).ToList(),
                ProgramBanks = options.Require("program-banks") == "auto" ? null : Ids(options.Require("program-banks")).ToList(),
                WaveSequenceBanks = waveOption == "auto" ? null : waveOption == "none" ? new List<string>() : Ids(waveOption).ToList(),
            });
        }
        catch (CopyException exception)
        {
            exception.Problems.ToList().ForEach(problem => Console.Error.WriteLine($"  PROBLEM: {problem}"));
            throw;
        }

        Console.WriteLine($"Program-Bänke: {string.Join(", ", result.ProgramBanks)}");
        foreach (var entry in result.MissingPrograms)
        {
            Console.WriteLine($"  WARNUNG fehlt: {entry}");
        }

        foreach (var entry in result.FactoryPrograms)
        {
            Console.WriteLine($"  Hinweis Werks-Program: {entry}");
        }

        if (result.MutedOutside > 0)
        {
            Console.WriteLine($"  Hinweis: {result.MutedOutside} stumme Timbres (Status Off) zeigen auf Programs außerhalb der Teil-PCG.");
        }

        if (result.WaveSequenceBanks.Count > 0)
        {
            Console.WriteLine($"Wave-Sequence-Bänke: {string.Join(", ", result.WaveSequenceBanks)}");
        }

        foreach (var entry in result.MissingWaveSequences)
        {
            Console.WriteLine($"  WARNUNG Wave Sequence nicht im Paket: {entry}");
        }

        Console.WriteLine($"OK: {outFile} ({result.Size / 1024} KB statt {result.OriginalSize / 1024} KB), " +
                          $"Programs {string.Join(",", result.ProgramBanks)}, Combis {string.Join(",", result.CombiBanks)}" +
                          (result.WaveSequenceBanks.Count > 0 ? $", Wave Sequences {string.Join(",", result.WaveSequenceBanks)}. " : ". ") +
                          $"{result.MissingPrograms.Count} fehlende User-Programs, {result.FactoryPrograms.Count} Werks-Programs genutzt.");
        return result.MissingPrograms.Count > 0 || result.MissingWaveSequences.Count > 0 ? 3 : 0;
    }

    /// <summary>
    /// Bericht: belegte Programs, die von keiner Combi (auch nicht von stummen Timbres) und keinem
    /// Set-List-Slot genutzt werden. Ändert nichts.
    /// </summary>
    private static int Unused(string fileName)
    {
        var pcg = Load(fileName);
        var unused = Housekeeping.UnusedPrograms(pcg);
        var duplicates = Housekeeping.Duplicates(pcg.ProgramBanks);
        foreach (var bank in pcg.ProgramBanks.BankCollection.Where(bank => bank.IsLoaded && bank.IsWritable))
        {
            var filled = bank.Patches.Count(patch => !patch.IsEmptyOrInit);
            Console.WriteLine($"  {bank.Id,-6} {filled,4} belegt, davon {unused.Count(program => program.Parent == bank),4} ungenutzt (keine Combi, Set List, Drum Track), " +
                              $"{bank.Patches.Count(patch => duplicates.ContainsKey(patch.Id)),4} doppelt");
        }

        Console.WriteLine($"  Doppelte Combis: {Housekeeping.Duplicates(pcg.CombiBanks).Count}");
        return 0;
    }

    /// <summary>Samples eines Pakets an die eigene Sample-Bank hängen und die neuen Programs darauf umstellen.</summary>
    private static int AttachSamples(Options options)
    {
        SampleAttachResult result;
        try
        {
            result = SampleAttach.Run(new SampleAttachRequest
            {
                PcgFile = options.Positional(0) ?? throw new ArgumentException("PCG fehlt."),
                OutPcg = options.Require("out"),
                Programs = Ids(options.Require("programs")).ToList(),
                PackKsc = options.Require("pack-ksc"),
                BankKsc = options.Require("bank-ksc"),
                OutDir = options.Require("out-dir"),
            });
        }
        catch (CopyException exception)
        {
            foreach (var problem in exception.Problems)
            {
                Console.Error.WriteLine($"  PROBLEM: {problem}");
            }

            throw;
        }

        Console.WriteLine($"Sample-Bank des Kunden: {result.BankId}");
        result.Multisamples.ForEach(sample => Console.WriteLine($"  Multisample {sample.From} -> {sample.To}  {sample.Name}  ({sample.File})"));
        Console.WriteLine($"  {result.Samples} Sample-Datei(en), {result.SampleBytes / 1024} KB");
        result.Changed.ForEach(line => Console.WriteLine($"  Umgestellt: {line}"));
        result.Warnings.ForEach(line => Console.WriteLine($"  Hinweis: {line}"));
        Console.WriteLine($"OK: {options.Require("out")} und {result.Files.Count} Datei(en) in {options.Require("out-dir")} geschrieben und geprüft.");
        return 0;
    }

    /// <summary>Eigene kleine Sample-Bank für ein Paket bauen (eigene Kennung, nur die genutzten Samples, Nummern ab 0).</summary>
    private static int PackSamples(Options options)
    {
        var result = SamplePack.Build(options.Positional(0) ?? throw new ArgumentException("PCG fehlt."), Ids(options.Require("programs")).ToList(),
            options.Require("bank-ksc"), options.Require("name"), options.Require("out-dir"), options.Require("out"));
        Console.WriteLine($"Neue Sample-Bank: {result.BankId}");
        result.Multisamples.ForEach(sample => Console.WriteLine($"  Multisample {sample.From} -> {sample.To}  {sample.Name}  ({sample.File})"));
        result.Changed.ForEach(line => Console.WriteLine($"  Umgestellt: {line}"));
        Console.WriteLine($"OK: {result.Samples} Sample-Datei(en), {result.SampleBytes / 1024} KB, {result.Files.Count} Datei(en) in {options.Require("out-dir")}.");
        return 0;
    }

    /// <summary>Was braucht jede Combi der Datei (eigene Samples, EXs)? Eine Zeile je Combi, zum Vergleichen und Nachsehen.</summary>
    private static int PackCheckCommand(string fileName)
    {
        var pcg = Load(fileName ?? throw new ArgumentException("Datei fehlt."));
        var all = PackCheck.Analyze(pcg);
        Console.WriteLine($"Gesamt: {all.Combis.Count} Combis, {all.Programs.Count} Programs, eigene Samples: {(all.UserSamples ? "ja" : "nein")}, EXs: {string.Join(" ", all.Exs)}");
        foreach (var combi in all.Combis)
        {
            var info = PackCheck.Analyze(pcg, new[] { combi.Id });
            Console.WriteLine($"{combi.Id};{combi.Name};{info.Programs.Count};{(info.UserSamples ? "ja" : "")};{string.Join(" ", info.Exs)}");
        }

        return 0;
    }

    /// <summary>
    /// Welche Programs nutzen Multisamples außerhalb des Kronos-ROM (User-Samples, EXs)?
    /// Jede Zone enthält bei +1..+16 die Kennung (UUID) der Sample-Bank und bei +18/+19 die
    /// Multisample-Nummer; das ROM hat die Kennung "KORG"..."MS". Mit --ksc wird geprüft, ob die
    /// KSC-Datei diese Sample-Bank selbst mitbringt.
    /// </summary>
    private static int Samples(string fileName, string kscFile)
    {
        var pcg = Load(fileName);
        string kscUuid = null;
        if (kscFile != null)
        {
            kscUuid = File.ReadLines(kscFile).Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith("#uuid:"))?.Substring(6).ToLowerInvariant();
            Console.WriteLine($"KSC {Path.GetFileName(kscFile)}: eigene Sample-Bank {kscUuid ?? "(keine)"}");
        }

        var missing = 0;
        foreach (var program in pcg.ProgramBanks.BankCollection.Where(bank => bank.IsLoaded && bank.Type != BankType.EType.Gm)
                     .SelectMany(bank => bank.Patches).Cast<IProgram>().Where(program => !program.IsEmptyOrInit))
        {
            var oscillators = (string)program.GetParam(ParameterNames.ProgramParameterName.OscMode).Value switch
            {
                "Single" => 1, "Double" => 2, "Drums" => 1, "Double Drums" => 2, _ => 0
            };
            for (var osc = 0; osc < oscillators; osc++)
            {
                for (var zone = 0; zone < 8; zone++)
                {
                    var offset = program.ByteOffset + 2774 + osc * (3240 - 2774) + zone * (2796 - 2774);
                    var content = program.PcgRoot.Content;
                    if (content[offset] != 1)   // 1 = Multisample (2 = Wave Sequence)
                    {
                        continue;
                    }

                    if (content[offset + 1] == 'K' && content[offset + 2] == 'O' && content[offset + 3] == 'R' && content[offset + 4] == 'G')
                    {
                        continue;   // Kronos-ROM
                    }

                    var id = new Guid(content.AsSpan(offset + 1, 16), bigEndian: true).ToString();
                    var number = content[offset + 18] | (content[offset + 19] << 8);
                    var inKsc = kscUuid != null && id == kscUuid;
                    if (kscUuid != null && !inKsc)
                    {
                        missing++;
                    }

                    var state = kscUuid == null ? "" : inKsc ? "  -> in der KSC enthalten" : "  -> NICHT in der KSC";
                    Console.WriteLine($"  {program.Id,-8} {program.Name,-24} OSC{osc + 1} Zone {zone + 1}: Sample-Bank {id}, Multisample {number}{state}");
                }
            }
        }

        return missing > 0 ? 3 : 0;
    }

    /// <summary>Diagnose: OSC-Modus und MS-Typ-Bytes je Zone (Offsets aus KronosProgram.GetZoneMsType).</summary>
    private static int Zones(string fileName, string bankId)
    {
        var pcg = Load(fileName);
        var bank = pcg.ProgramBanks.BankCollection.First(candidate => candidate.Id == bankId);
        foreach (var program in bank.Patches.Cast<IProgram>().Where(program => !program.IsEmptyOrInit))
        {
            var mode = (string)program.GetParam(ParameterNames.ProgramParameterName.OscMode).Value;
            var types = new List<string>();
            for (var osc = 0; osc < 2; osc++)
            {
                var zones = new List<string>();
                for (var zone = 0; zone < 8; zone++)
                {
                    zones.Add(program.PcgRoot.Content[program.ByteOffset + 2774 + osc * (3240 - 2774) + zone * (2796 - 2774)].ToString());
                }

                types.Add(string.Join("", zones));
            }

            var sequences = string.Join(", ", program.UsedWaveSequences.Select(sequence => sequence.Id + " " + sequence.Name).Distinct());
            Console.WriteLine($"{program.Id,-8} {program.Name,-24} {mode,-12} OSC1 {types[0]} OSC2 {types[1]}  {sequences}");
        }

        return 0;
    }

    /// <summary>Diagnose: Lage eines Patches in der Datei (Byte-Offset und Länge).</summary>
    private static int Where(string fileName, string id)
    {
        var pcg = Load(fileName);
        var patch = new IBanks[] { pcg.ProgramBanks, pcg.CombiBanks }
            .SelectMany(banks => banks.BankCollection.SelectMany(bank => bank.Patches))
            .First(candidate => candidate.Id == id);
        Console.WriteLine($"{patch.Id} {patch.Name} offset={patch.ByteOffset} length={patch.ByteLength}");
        return 0;
    }

    private static int Sort(Options options)
    {
        var fileName = options.Positional(0) ?? throw new ArgumentException("Datei fehlt.");
        var outFile = options.Require("out");
        var kind = options.Require("kind") switch
        {
            "programs" => PcgSorter.Kind.Programs,
            "combis" => PcgSorter.Kind.Combis,
            _ => throw new ArgumentException("--kind muss 'programs' oder 'combis' sein.")
        };
        var order = (options.Get("order") ?? "name") switch
        {
            "name" => PatchSorter.SortOrder.ESortOrderNameCategory,
            "category" => PatchSorter.SortOrder.ESortOrderCategoryName,
            _ => throw new ArgumentException("--order muss 'name' oder 'category' sein.")
        };
        SortResult result;
        try
        {
            result = SortService.Run(new SortRequest { File = fileName, OutFile = outFile, Kind = kind, Banks = Ids(options.Require("banks")).ToList(), Order = order });
        }
        catch (CopyException exception)
        {
            exception.Problems.ToList().ForEach(problem => Console.Error.WriteLine($"  PROBLEM: {problem}"));
            throw;
        }

        Console.WriteLine($"{result.Moved.Count} Patch(es) haben einen neuen Platz.");
        Console.WriteLine($"OK: {outFile} geschrieben. Alle Combis und Set-List-Slots nutzen dieselben Sounds wie vorher. Original unverändert.");
        return 0;
    }


    private static ObservableCollectionEx<IBank> SelectBanks(IBanks banks, string ids) => PcgFiles.SelectBanks(banks, Ids(ids));

    private static IPatch FindPatch(IBanks banks, string id) => PcgFiles.FindPatch(banks, id);
}
