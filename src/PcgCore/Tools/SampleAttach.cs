// Samples eines Sound-Pakets an die eigene Sample-Bank (KSC) des Kunden anhängen, am Rechner, ohne Kronos.
//
// Aufbau einer User-Sample-Bank (aus Kronos-Dateien abgeleitet, Stand 12.11.2026):
//   <name>.KSC            Text: "#uuid:<Kennung>", dann je Zeile eine .KMP (Multisample) oder einzelne .KSF,
//                         danach "#>User.0.2.<Datei>" je Datei (weitere "#>…"-Zeilen bleiben unverändert)
//   <name>_UserBank.KSC   Text: "#>>uuid:<Kennung>.MS<nr>.1.0.<Name 24>", "#>>uuid:<Kennung>.DS<nr>.1.0.<Name 24>",
//                         "#>uuid:<Kennung>.<höchste MS-Nr + 1>.<höchste Sample-Nr + 1>.<name>"
//   <name>/XXXXXnnn.KMP   Multisample; Block MNO1 = Multisample-Nummer nnn, Block RLP1 = je Sample 6 Bytes + Dateiname (12)
//   <name>/XXXXXnnn/MSnnnkkk.KSF   Sample; Block SNO1 = Sample-Nummer in der Bank
// Ein Program verweist je Zone auf Kennung (Zone +1..+16) und Multisample-Nummer (Zone +18/+19).
//
// Anhängen heißt: neue, freie Nummern in der Bank des Kunden vergeben, Dateien mit diesen Nummern schreiben, beide
// KSC-Texte ergänzen und in der PCG die neu eingefügten Programs (und ihre Wave Sequences) auf Kennung und Nummern
// des Kunden umstellen. Vorhandene Dateien und Sounds des Kunden werden nicht angefasst.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PcgTools.Model.Common.Synth.Meta;
using PcgTools.Model.Common.Synth.OldParameters;
using PcgTools.Model.Common.Synth.PatchPrograms;
using PcgTools.Model.Common.Synth.PatchWaveSequences;

namespace KronosPcg.Tools
{
    public sealed class SampleAttachRequest
    {
        /// <summary>PCG, in die das Paket schon eingefügt wurde (Ergebnis des Übernehmens).</summary>
        public string PcgFile { get; init; }
        public string OutPcg { get; init; }

        /// <summary>Plätze der neu eingefügten Programs (nur diese werden umgestellt), z. B. "U-FF010".</summary>
        public IReadOnlyList<string> Programs { get; init; } = Array.Empty<string>();

        /// <summary>KSC des Pakets; KMP und KSF liegen im Ordner gleichen Namens daneben.</summary>
        public string PackKsc { get; init; }

        /// <summary>KSC des Kunden; daneben, wenn vorhanden, die _UserBank.KSC.</summary>
        public string BankKsc { get; init; }

        /// <summary>Ordner für die neuen Dateien: beide KSC-Texte und der Sample-Ordner nur mit den neuen Dateien.</summary>
        public string OutDir { get; init; }
    }

    public sealed class SampleAttachResult
    {
        public string BankId { get; set; }

        /// <summary>Multisample des Pakets → neue Nummer in der Bank des Kunden.</summary>
        public List<(int From, int To, string Name, string File)> Multisamples { get; } = new();
        public int Samples { get; set; }
        public long SampleBytes { get; set; }

        /// <summary>Umgestellte Zonen und Wave-Sequence-Schritte, zum Nachlesen.</summary>
        public List<string> Changed { get; } = new();
        public List<string> Warnings { get; } = new();
        public List<string> Files { get; } = new();
    }

    public static class SampleAttach
    {
        private static readonly Encoding Latin1 = Encoding.Latin1;

        public static SampleAttachResult Run(SampleAttachRequest request)
        {
            SafeOutput.Check(request.OutPcg, request.PcgFile);
            var result = new SampleAttachResult();
            var pack = Ksc.Read(request.PackKsc);
            var bank = Ksc.Read(request.BankKsc);
            if (pack.Id == null || bank.Id == null)
            {
                throw new CopyException(Lang.T("In einer der KSC-Dateien fehlt die Kennung der Sample-Bank (#uuid). Bitte die KSC der User-Sample-Bank wählen.",
                    "One of the KSC files has no sample bank ID (#uuid). Please choose the KSC of the user sample bank."));
            }

            result.BankId = bank.Id;
            var fromId = Guid.Parse(pack.Id);
            var toId = Guid.Parse(bank.Id);

            // 1. Welche Multisamples des Pakets nutzen die neuen Programs (direkt oder über eine Wave Sequence)?
            var pcg = PcgFiles.Load(request.PcgFile);
            var original = (byte[])pcg.Content.Clone();
            var programs = request.Programs.Select(id => (IProgram)PcgFiles.FindPatch(pcg.ProgramBanks, id)).ToList();
            var places = UserSamplePlaces(programs).Where(place => ReadId(pcg.Content, place.Offset) == fromId).ToList();
            var used = places.Select(place => ReadNumber(pcg.Content, place.Offset)).Distinct().OrderBy(number => number).ToList();
            if (used.Count == 0)
            {
                throw new CopyException(Lang.T("Keines der neuen Programs nutzt Samples aus der Sample-Bank dieses Pakets.",
                    "None of the new Programs uses samples from this pack's sample bank."));
            }

            // 2. Multisamples des Pakets lesen und neue Nummern vergeben.
            var packDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(request.PackKsc))!, Path.GetFileNameWithoutExtension(request.PackKsc));
            var packSamples = pack.Files.Where(file => file.EndsWith(".KMP", StringComparison.OrdinalIgnoreCase))
                .Select(file => Kmp.Read(Path.Combine(packDir, file))).ToDictionary(kmp => kmp.Number);
            var missing = used.Where(number => !packSamples.ContainsKey(number)).ToList();
            if (missing.Count > 0)
            {
                throw new CopyException(Lang.T("Die Sounds nutzen Multisamples, die nicht im Paket liegen.",
                        "The sounds use Multisamples that are not in the pack."),
                    missing.Select(number => Lang.T($"Multisample {number} fehlt in {Path.GetFileName(request.PackKsc)}",
                        $"Multisample {number} is missing in {Path.GetFileName(request.PackKsc)}")).ToList());
            }

            var userBank = UserBankFile.Read(UserBankPath(request.BankKsc), bank);
            var nextMultisample = userBank.MultisampleCount;
            var nextSample = userBank.SampleCount;
            var bankName = Path.GetFileNameWithoutExtension(request.BankKsc);
            var sampleDir = Path.Combine(request.OutDir, bankName);
            Directory.CreateDirectory(sampleDir);
            var numberMap = new Dictionary<int, int>();
            var newFiles = new List<string>();
            var multisampleLines = new List<string>();
            var sampleLines = new List<string>();
            foreach (var number in used)
            {
                var kmp = packSamples[number];
                var newNumber = nextMultisample++;
                if (newNumber > 999)
                {
                    throw new CopyException(Lang.T("Die Sample-Bank hat keine freie Multisample-Nummer mehr (höchstens 999).",
                        "The sample bank has no free Multisample number left (at most 999)."));
                }

                numberMap[number] = newNumber;
                var stem = Path.GetFileNameWithoutExtension(kmp.FileName)[..5] + newNumber.ToString("000");
                Directory.CreateDirectory(Path.Combine(sampleDir, stem));
                var fileNames = new List<string>();
                var written = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var oldName in kmp.SampleFiles)
                {
                    // Platzhalter des Kronos für ein nicht gespeichertes Sample: Eintrag bleibt, es gibt keine Datei dazu.
                    if (oldName.StartsWith("SKIPPEDSAMPL", StringComparison.OrdinalIgnoreCase))
                    {
                        fileNames.Add(oldName);
                        result.Warnings.Add(Lang.T(
                            $"{kmp.Name.TrimEnd()}: Ein Tastenbereich hat im Paket kein Sample (Platzhalter des Kronos), er bleibt stumm wie im Paket.",
                            $"{kmp.Name.TrimEnd()}: One key range has no sample in the pack (Kronos placeholder). It stays silent, as in the pack."));
                        continue;
                    }

                    // Dasselbe Sample kann in einem Multisample mehrfach liegen (mehrere Tastenbereiche): nur einmal schreiben.
                    if (!written.TryGetValue(oldName, out var newName))
                    {
                        var source = Path.Combine(packDir, Path.GetFileNameWithoutExtension(kmp.FileName), oldName);
                        var data = File.ReadAllBytes(source);
                        var sampleNumber = nextSample++;
                        newName = "MS" + newNumber.ToString("000") + Path.GetFileNameWithoutExtension(oldName)[^3..] + ".KSF";
                        var sampleNumberBytes = new byte[4];
                        BinaryPrimitives.WriteInt32BigEndian(sampleNumberBytes, sampleNumber);
                        Chunks.Set(data, "SNO1", sampleNumberBytes);
                        File.WriteAllBytes(Path.Combine(sampleDir, stem, newName), data);
                        sampleLines.Add($"#>>uuid:{bank.Id}.DS{sampleNumber}.1.0.{Latin1.GetString(Chunks.Get(data, "NAME"))}");
                        written[oldName] = newName;
                        result.Samples++;
                        result.SampleBytes += data.Length;
                        result.Files.Add($"{bankName}/{stem}/{newName}");
                    }

                    fileNames.Add(newName);
                }

                File.WriteAllBytes(Path.Combine(sampleDir, stem + ".KMP"), kmp.WithNumber(newNumber, fileNames));
                result.Files.Add($"{bankName}/{stem}.KMP");
                newFiles.Add(stem + ".KMP");
                multisampleLines.Add($"#>>uuid:{bank.Id}.MS{newNumber}.1.0.{kmp.Name}");
                result.Multisamples.Add((number, newNumber, kmp.Name.TrimEnd(), stem + ".KMP"));
            }

            // 3. Beide KSC-Texte des Kunden ergänzen (als neue Dateien im Ausgabeordner).
            File.WriteAllBytes(Path.Combine(request.OutDir, Path.GetFileName(request.BankKsc)), bank.WithFiles(newFiles));
            result.Files.Add(Path.GetFileName(request.BankKsc));
            var userBankName = Path.GetFileName(UserBankPath(request.BankKsc));
            File.WriteAllBytes(Path.Combine(request.OutDir, userBankName), userBank.With(multisampleLines, sampleLines, nextMultisample, nextSample));
            result.Files.Add(userBankName);

            // 4. PCG umstellen: Kennung und Nummer in den Zonen der neuen Programs und in ihren Wave Sequences.
            var allowed = new HashSet<int>();
            foreach (var (offset, label) in places)
            {
                var oldNumber = ReadNumber(pcg.Content, offset);
                var newNumber = numberMap[oldNumber];
                toId.TryWriteBytes(pcg.Content.AsSpan(offset + 1, 16), bigEndian: true, out _);
                pcg.Content[offset + 18] = (byte)(newNumber & 0xFF);
                pcg.Content[offset + 19] = (byte)(newNumber >> 8);
                for (var index = 1; index <= 19; index++)
                {
                    allowed.Add(offset + index);
                }

                result.Changed.Add($"{label}: Multisample {oldNumber} → {newNumber}");
            }

            pcg.FileName = request.OutPcg;
            pcg.SaveFile(true, true);

            // Gegenprobe: Die neue PCG darf sich vom Eingang nur in den umgestellten Zonen (und den Prüfsummen) unterscheiden,
            // und jede umgestellte Zone muss auf die Bank des Kunden und die neue Nummer zeigen.
            var check = File.ReadAllBytes(request.OutPcg);
            var problems = new List<string>();
            if (check.Length != original.Length)
            {
                problems.Add(Lang.T($"Die neue PCG hat eine andere Größe ({check.Length} statt {original.Length} Bytes)",
                    $"The new PCG has a different size ({check.Length} instead of {original.Length} bytes)"));
            }
            else
            {
                var checksums = PartialPcgExporter.ChecksumOffsets(check);
                var stray = Enumerable.Range(0, check.Length).Count(index => check[index] != original[index] && !allowed.Contains(index) && !checksums.Contains(index));
                if (stray > 0)
                {
                    problems.Add(Lang.T($"{stray} Bytes außerhalb der umgestellten Zonen haben sich geändert",
                        $"{stray} bytes outside the remapped zones have changed"));
                }

                problems.AddRange(places.Where(place => ReadId(check, place.Offset) != toId ||
                                                        ReadNumber(check, place.Offset) != numberMap[ReadNumber(original, place.Offset)])
                    .Select(place => Lang.T($"{place.Label}: Verweis nicht umgestellt", $"{place.Label}: reference not remapped")));
            }

            PcgFiles.Load(request.OutPcg);   // muss sich lesen lassen
            if (problems.Count > 0)
            {
                File.Delete(request.OutPcg);
                throw new CopyException(Lang.T("Gegenprobe nach dem Anhängen der Samples fehlgeschlagen. Die neue PCG wurde wieder gelöscht.",
                    "Verification after appending the samples failed. The new PCG was deleted again."), problems);
            }

            return result;
        }

        /// <summary>
        /// Alle Stellen, an denen die Programs ein Multisample außerhalb des Kronos-ROM nutzen: Zonen der Programs und
        /// Schritte ihrer Wave Sequences. (EXs-Samples haben die ROM-Kennung "KORG…" und zählen nicht dazu.)
        /// </summary>
        private static List<(int Offset, string Label)> UserSamplePlaces(IEnumerable<IProgram> programs)
        {
            var places = new List<(int Offset, string Label)>();
            foreach (var program in programs)
            {
                foreach (var (offset, label) in ProgramZones(program))
                {
                    places.Add((offset, $"{program.Id} {program.Name} {label}"));
                }

                foreach (var sequence in program.UsedWaveSequences.Where(sequence => sequence != null).Distinct())
                {
                    for (var step = 0; step < 64; step++)
                    {
                        places.Add((sequence.ByteOffset + 40 + step * 34, Lang.T($"Wave Sequence {sequence.Id} {sequence.Name} Schritt {step + 1}",
                            $"Wave Sequence {sequence.Id} {sequence.Name} step {step + 1}")));
                    }
                }
            }

            return places.Where(place =>
                {
                    var content = programs.First().PcgRoot.Content;
                    return content[place.Offset] == 1 && !(content[place.Offset + 1] == 'K' && content[place.Offset + 2] == 'O' &&
                                                           content[place.Offset + 3] == 'R' && content[place.Offset + 4] == 'G');
                })
                .GroupBy(place => place.Offset).Select(group => group.First()).ToList();
        }

        /// <summary>Was ein Paket an Samples braucht und ob sie in seiner KSC liegen.</summary>
        /// <param name="Multisamples">Namen der genutzten Multisamples aus der KSC des Pakets.</param>
        /// <param name="Missing">Genutzte Multisamples, die in der KSC fehlen (Beschreibung).</param>
        /// <param name="OtherBanks">Verweise auf andere Sample-Bänke als die des Pakets (Beschreibung).</param>
        public sealed record PackSamples(string BankId, List<string> Multisamples, List<string> Missing, List<string> OtherBanks, int Files, long Bytes);

        public static PackSamples Inspect(IReadOnlyList<IProgram> programs, string packKsc)
        {
            var pack = Ksc.Read(packKsc);
            if (pack.Id == null)
            {
                throw new CopyException(Lang.T("Die KSC im Paket ist keine User-Sample-Bank (die Kennung #uuid fehlt).",
                    "The KSC in the pack is not a user sample bank (the #uuid ID is missing)."));
            }

            var packId = Guid.Parse(pack.Id);
            var packDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(packKsc))!, Path.GetFileNameWithoutExtension(packKsc));
            var samples = pack.Files.Where(file => file.EndsWith(".KMP", StringComparison.OrdinalIgnoreCase))
                .Select(file => Path.Combine(packDir, file)).Where(File.Exists).Select(Kmp.Read)
                .GroupBy(kmp => kmp.Number).ToDictionary(group => group.Key, group => group.First());
            var places = programs.Count == 0 ? new List<(int Offset, string Label)>() : UserSamplePlaces(programs);
            var content = programs.Count == 0 ? Array.Empty<byte>() : programs[0].PcgRoot.Content;
            var mine = places.Where(place => ReadId(content, place.Offset) == packId).ToList();
            var used = mine.Select(place => ReadNumber(content, place.Offset)).Distinct().OrderBy(number => number).ToList();
            var files = used.Where(samples.ContainsKey).SelectMany(number => samples[number].SampleFiles
                    .Where(name => !name.StartsWith("SKIPPEDSAMPL", StringComparison.OrdinalIgnoreCase))
                    .Select(name => Path.Combine(packDir, Path.GetFileNameWithoutExtension(samples[number].FileName), name)))
                .Distinct().ToList();
            var missing = used.Where(number => !samples.ContainsKey(number)).Select(number => $"Multisample {number}").ToList();
            missing.AddRange(files.Where(file => !File.Exists(file)).Select(file =>
                Lang.T($"Sample-Datei {Path.GetFileName(file)}", $"sample file {Path.GetFileName(file)}")));
            return new PackSamples(pack.Id,
                used.Where(samples.ContainsKey).Select(number => samples[number].Name.TrimEnd()).ToList(),
                missing,
                places.Except(mine).Select(place => place.Label).ToList(),
                files.Count(File.Exists), files.Where(File.Exists).Sum(file => new FileInfo(file).Length));
        }

        /// <summary>Kennungen der Sample-Bänke (außerhalb des Kronos-ROM), die diese Programs nutzen, mit der Zahl der Stellen.</summary>
        public static Dictionary<string, int> UsedBanks(IReadOnlyList<IProgram> programs)
        {
            if (programs.Count == 0)
            {
                return new Dictionary<string, int>();
            }

            var content = programs[0].PcgRoot.Content;
            return UserSamplePlaces(programs).GroupBy(place => ReadId(content, place.Offset).ToString())
                .ToDictionary(group => group.Key, group => group.Count());
        }

        /// <summary>Nummern der Multisamples, die diese Programs aus der Bank mit dieser Kennung nutzen.</summary>
        public static List<int> UsedNumbers(IReadOnlyList<IProgram> programs, string bankId)
        {
            if (programs.Count == 0)
            {
                return new List<int>();
            }

            var content = programs[0].PcgRoot.Content;
            var id = Guid.Parse(bankId);
            return UserSamplePlaces(programs).Where(place => ReadId(content, place.Offset) == id)
                .Select(place => ReadNumber(content, place.Offset)).Distinct().OrderBy(number => number).ToList();
        }

        /// <summary>Nummer eines Multisamples (aus seiner KMP-Datei).</summary>
        public static int MultisampleNumber(string kmpFile) => Kmp.Read(kmpFile).Number;

        /// <summary>Kennung und Zahl der Multisamples einer KSC; null, wenn es keine User-Sample-Bank ist.</summary>
        public static (string Id, int Multisamples)? BankInfo(string kscFile)
        {
            var ksc = Ksc.Read(kscFile);
            return ksc.Id == null ? null : (ksc.Id, ksc.Files.Count(file => file.EndsWith(".KMP", StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>Dateinamen (ohne Ordner) der Samples eines Multisamples, damit lose gewählte Dateien wieder in ihren Ordner finden.</summary>
        public static List<string> SampleFilesOf(string kmpFile) => Kmp.Read(kmpFile).SampleFiles;

        private static string UserBankPath(string kscFile) =>
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(kscFile))!, Path.GetFileNameWithoutExtension(kscFile) + "_UserBank.KSC");

        private static Guid ReadId(byte[] content, int offset) => new(content.AsSpan(offset + 1, 16), bigEndian: true);

        private static int ReadNumber(byte[] content, int offset) => content[offset + 18] | (content[offset + 19] << 8);

        /// <summary>Alle Zonen eines Programs: je Oszillator 8 Zonen zu 22 Bytes ab 2774, zweiter Oszillator 466 Bytes weiter.</summary>
        private static IEnumerable<(int Offset, string Label)> ProgramZones(IProgram program)
        {
            var oscillators = (string)program.GetParam(ParameterNames.ProgramParameterName.OscMode).Value switch
            {
                "Single" => 1, "Double" => 2, "Drums" => 1, "Double Drums" => 2, _ => 0
            };
            for (var osc = 0; osc < oscillators; osc++)
            {
                for (var zone = 0; zone < 8; zone++)
                {
                    yield return (program.ByteOffset + 2774 + osc * 466 + zone * 22, $"OSC{osc + 1} Zone {zone + 1}");
                }
            }
        }

        // ---------- Dateiformate ----------

        /// <summary>Blöcke in KMP und KSF: 4 Zeichen Name, Länge (big endian), Daten.</summary>
        private static class Chunks
        {
            public static (int Offset, int Length) Find(byte[] data, string tag)
            {
                for (var index = 0; index + 8 <= data.Length;)
                {
                    var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(index + 4));
                    if (Encoding.ASCII.GetString(data, index, 4) == tag)
                    {
                        return (index + 8, length);
                    }

                    index += 8 + length;
                }

                throw new CopyException(Lang.T($"Block {tag} fehlt in einer Sample-Datei.", $"Block {tag} is missing in a sample file."));
            }

            public static byte[] Get(byte[] data, string tag)
            {
                var (offset, length) = Find(data, tag);
                return data.AsSpan(offset, length).ToArray();
            }

            public static void Set(byte[] data, string tag, byte[] value)
            {
                var (offset, length) = Find(data, tag);
                if (length != value.Length)
                {
                    throw new CopyException(Lang.T($"Block {tag} hat eine unerwartete Länge.", $"Block {tag} has an unexpected length."));
                }

                value.CopyTo(data, offset);
            }
        }

        private sealed class Kmp
        {
            public string FileName { get; private init; }
            public int Number { get; private init; }
            public string Name { get; private init; }
            public List<string> SampleFiles { get; private init; }
            private byte[] _data;

            public static Kmp Read(string path)
            {
                var data = File.ReadAllBytes(path);
                var (offset, length) = Chunks.Find(data, "RLP1");
                return new Kmp
                {
                    FileName = Path.GetFileName(path),
                    Number = BinaryPrimitives.ReadInt32BigEndian(Chunks.Get(data, "MNO1")),
                    Name = Latin1.GetString(Chunks.Get(data, "NAME")),
                    SampleFiles = Enumerable.Range(0, length / 18).Select(index => Latin1.GetString(data, offset + index * 18 + 6, 12).TrimEnd('\0', ' ')).ToList(),
                    _data = data,
                };
            }

            public byte[] WithNumber(int number, List<string> sampleFiles)
            {
                var data = (byte[])_data.Clone();
                var value = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(value, number);
                Chunks.Set(data, "MNO1", value);
                var (offset, _) = Chunks.Find(data, "RLP1");
                for (var index = 0; index < sampleFiles.Count; index++)
                {
                    Latin1.GetBytes(sampleFiles[index].PadRight(12)[..12]).CopyTo(data, offset + index * 18 + 6);
                }

                return data;
            }
        }

        /// <summary>Die KSC einer User-Sample-Bank: Kennung, Dateiliste und alle Zeilen im Original.</summary>
        private sealed class Ksc
        {
            public string Id { get; private init; }
            public List<string> Files { get; private init; }
            private List<string> _lines;

            public static Ksc Read(string path)
            {
                var lines = Latin1.GetString(File.ReadAllBytes(path)).Split("\r\n").ToList();
                return new Ksc
                {
                    Id = lines.FirstOrDefault(line => line.StartsWith("#uuid:"))?[6..].Trim().ToLowerInvariant(),
                    Files = lines.Where(line => line.Length > 0 && !line.StartsWith("#")).Select(line => line.Trim()).ToList(),
                    _lines = lines,
                };
            }

            /// <summary>Neue Dateien hinter die letzte Datei, ihre "#>User"-Zeilen hinter die letzte "#>User"-Zeile.</summary>
            public byte[] WithFiles(List<string> files)
            {
                var lines = new List<string>(_lines);
                var lastFile = lines.FindLastIndex(line => line.Length > 0 && !line.StartsWith("#"));
                if (lastFile < 0)
                {
                    lastFile = lines.FindLastIndex(line => line.StartsWith("#uuid:"));
                }

                lines.InsertRange(lastFile + 1, files);
                var lastUser = lines.FindLastIndex(line => line.StartsWith("#>User."));
                lines.InsertRange((lastUser < 0 ? lastFile + files.Count : lastUser) + 1, files.Select(file => "#>User.0.2." + file));
                return Latin1.GetBytes(string.Join("\r\n", lines));
            }
        }

        /// <summary>Die _UserBank.KSC: Namensliste der Multisamples (MS) und Samples (DS) mit den Zählern.</summary>
        private sealed class UserBankFile
        {
            public int MultisampleCount { get; private init; }
            public int SampleCount { get; private init; }
            private List<string> _lines;
            private string _id;

            public static UserBankFile Read(string path, Ksc bank)
            {
                if (!File.Exists(path))
                {
                    throw new CopyException(Lang.T(
                        "Neben der KSC fehlt die Datei „…_UserBank.KSC“. Sie nennt die höchste vergebene Sample-Nummer; " +
                        "ohne sie kann die Werkstatt keine freien Nummern bestimmen.",
                        "The file “…_UserBank.KSC” is missing next to the KSC. It holds the highest sample number in use; " +
                        "without it the toolbox cannot work out free numbers."));
                }

                var lines = Latin1.GetString(File.ReadAllBytes(path)).Split("\r\n").ToList();
                var prefix = $"#>uuid:{bank.Id}.";
                var counter = lines.FirstOrDefault(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                              ?? throw new CopyException(Lang.T("Die _UserBank.KSC gehört nicht zu dieser Sample-Bank (andere Kennung).",
                                  "The _UserBank.KSC does not belong to this sample bank (different ID)."));
                var parts = counter[prefix.Length..].Split('.', 3);
                int Highest(string kind) => lines.Where(line => line.StartsWith($"#>>uuid:{bank.Id}.{kind}", StringComparison.OrdinalIgnoreCase))
                    .Select(line => int.Parse(line[$"#>>uuid:{bank.Id}.{kind}".Length..].Split('.')[0])).DefaultIfEmpty(-1).Max();
                return new UserBankFile
                {
                    MultisampleCount = Math.Max(int.Parse(parts[0]), Highest("MS") + 1),
                    SampleCount = Math.Max(int.Parse(parts[1]), Highest("DS") + 1),
                    _lines = lines,
                    _id = bank.Id,
                };
            }

            public byte[] With(List<string> multisamples, List<string> samples, int multisampleCount, int sampleCount)
            {
                var lines = new List<string>(_lines);
                var counterIndex = lines.FindIndex(line => line.StartsWith($"#>uuid:{_id}.", StringComparison.OrdinalIgnoreCase));
                var name = lines[counterIndex][$"#>uuid:{_id}.".Length..].Split('.', 3)[2];
                lines[counterIndex] = $"#>uuid:{_id}.{multisampleCount}.{sampleCount}.{name}";
                var lastSample = lines.FindLastIndex(line => line.StartsWith($"#>>uuid:{_id}.DS", StringComparison.OrdinalIgnoreCase));
                lines.InsertRange((lastSample < 0 ? counterIndex - 1 : lastSample) + 1, samples);
                var lastMultisample = lines.FindLastIndex(line => line.StartsWith($"#>>uuid:{_id}.MS", StringComparison.OrdinalIgnoreCase));
                lines.InsertRange((lastMultisample < 0 ? lines.FindIndex(line => line.StartsWith("#>>uuid:") || line.StartsWith("#>uuid:")) - 1 : lastMultisample) + 1, multisamples);
                return Latin1.GetBytes(string.Join("\r\n", lines));
            }
        }
    }
}
