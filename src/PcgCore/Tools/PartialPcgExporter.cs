// Export einer Teil-PCG (nur ausgewählte Program- und Combi-Bänke), wie sie der Kronos
// mit "Save PCG" und abgewählten Bänken schreibt. Aufbau abgeleitet aus einer vom Kronos 2
// (OS 3.x) gespeicherten Teil-PCG und "PCG Structure Kronos.txt" aus PCG Tools.
// Wave-Sequence-Bänke können mit exportiert werden (Aufbau wie in der vollen PCG, Bitmaske laut
// "PCG Structure Kronos.txt"; noch nicht mit einer vom Kronos gespeicherten Teil-PCG verglichen).
// Drum Kits, Set Lists und Global werden nicht exportiert.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KronosPcg.Tools
{
    public static class PartialPcgExporter
    {
        // Reihenfolge der Bänke im Kronos (Bit-Nummer in DIV1 = Index in dieser Liste).
        public static readonly string[] ProgramBankOrder =
        {
            "I-A", "I-B", "I-C", "I-D", "I-E", "I-F",
            "U-A", "U-B", "U-C", "U-D", "U-E", "U-F", "U-G",
            "U-AA", "U-BB", "U-CC", "U-DD", "U-EE", "U-FF", "U-GG"
        };

        public static readonly string[] WaveSequenceBankOrder =
        {
            "INT", "U-A", "U-B", "U-C", "U-D", "U-E", "U-F", "U-G",
            "U-AA", "U-BB", "U-CC", "U-DD", "U-EE", "U-FF", "U-GG"
        };

        public static readonly string[] CombiBankOrder =
        {
            "I-A", "I-B", "I-C", "I-D", "I-E", "I-F", "I-G",
            "U-A", "U-B", "U-C", "U-D", "U-E", "U-F", "U-G"
        };

        private sealed record Chunk(string Name, int Offset, int Length, List<Chunk> Children)
        {
            public int TotalLength => 12 + Length;
            public int BodyOffset => Offset + 12;
        }

        private static readonly HashSet<string> Containers = new() { "PCG1", "SLS1", "PRG1", "CMB1", "DKT1", "WSQ1" };

        /// <summary>
        /// Erzeugt die Bytes einer Teil-PCG aus dem Inhalt einer vollständigen Kronos-PCG.
        /// </summary>
        public static byte[] Export(byte[] content, IReadOnlyCollection<string> programBanks, IReadOnlyCollection<string> combiBanks,
            IReadOnlyCollection<string> waveSequenceBanks = null)
        {
            waveSequenceBanks ??= Array.Empty<string>();
            if (content.Length < 16 || content[0] != 'K' || content[1] != 'O' || content[2] != 'R' || content[3] != 'G')
            {
                throw new InvalidDataException("Keine Korg-Datei.");
            }

            if (programBanks.Count == 0 || combiBanks.Count == 0)
            {
                throw new ArgumentException("Mindestens eine Program-Bank und eine Combi-Bank wählen (andere Varianten sind noch nicht am Kronos geprüft).");
            }

            var pcg1 = ReadChunks(content, 16, content.Length).Single(chunk => chunk.Name == "PCG1");
            var div1 = pcg1.Children.Single(chunk => chunk.Name == "DIV1");
            var prg1 = pcg1.Children.Single(chunk => chunk.Name == "PRG1");
            var cmb1 = pcg1.Children.Single(chunk => chunk.Name == "CMB1");

            var programChunks = SelectBanks(content, prg1, programBanks, ProgramBankOrder, "Program", BankKey);
            var combiChunks = SelectBanks(content, cmb1, combiBanks, CombiBankOrder, "Combi", BankKey);
            var wsq1 = pcg1.Children.SingleOrDefault(chunk => chunk.Name == "WSQ1");
            var waveSequenceChunks = waveSequenceBanks.Count == 0
                ? new List<Chunk>()
                : SelectBanks(content, wsq1 ?? throw new InvalidDataException("Keine Wave Sequences in der Datei."),
                    waveSequenceBanks, WaveSequenceBankOrder, "Wave-Sequence", WaveSequenceBankKey);

            using var output = new MemoryStream();
            output.Write(content, 0, 16);                                         // "KORG", Produkt, Version
            var pcg1Header = output.Position;
            output.Write(content, pcg1.Offset, 12);                               // PCG1, Länge folgt unten

            output.Write(BuildDiv1(content, div1, programBanks, combiBanks, waveSequenceBanks));
            WriteContainer(output, content, prg1, programChunks);
            WriteContainer(output, content, cmb1, combiChunks);
            if (waveSequenceChunks.Count > 0)
            {
                WriteContainer(output, content, wsq1, waveSequenceChunks);
            }

            var bytes = output.ToArray();
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan((int)pcg1Header + 4), bytes.Length - (int)pcg1Header - 12);
            return bytes;
        }

        /// <summary>Positionen der Prüfsummen-Bytes aller Bank-Blöcke (Kopf +11).</summary>
        public static HashSet<int> ChecksumOffsets(byte[] content)
        {
            var offsets = new HashSet<int>();
            void Collect(IEnumerable<Chunk> chunks)
            {
                foreach (var chunk in chunks)
                {
                    offsets.Add(chunk.Offset + 11);
                    Collect(chunk.Children);
                }
            }

            Collect(ReadChunks(content, 16, content.Length));
            return offsets;
        }

        private static List<Chunk> ReadChunks(byte[] content, int offset, int end)
        {
            var chunks = new List<Chunk>();
            while (offset + 12 <= end)
            {
                var name = System.Text.Encoding.ASCII.GetString(content, offset, 4);
                var length = BinaryPrimitives.ReadInt32BigEndian(content.AsSpan(offset + 4));
                if (!name.All(char.IsLetterOrDigit) || length < 0 || offset + 12 + length > end)
                {
                    throw new InvalidDataException($"Unerwarteter Datenblock bei {offset:X}.");
                }

                var children = Containers.Contains(name) ? ReadChunks(content, offset + 12, offset + 12 + length) : new List<Chunk>();
                chunks.Add(new Chunk(name, offset, length, children));
                offset += 12 + length;
            }

            return chunks;
        }

        /// <summary>
        /// Bank-Kennung im Kopf jedes Bank-Blocks: 0x0000nnnn = interne Bank nn, 0x0002nnnn = User-Bank nn
        /// (U-A..U-G = 0..6, U-AA..U-GG = 7..13).
        /// </summary>
        private static int BankKey(string bankId)
        {
            var user = bankId.StartsWith("U-");
            var letters = bankId[2..];
            var index = letters[0] - 'A' + (letters.Length == 2 ? 7 : 0);
            return (user ? 0x20000 : 0) | index;
        }

        /// <summary>Wave-Sequence-Bänke: INT = 0x00000000, U-A..U-G = 0x00020000..06, U-AA..U-GG = 0x00020007..0D.</summary>
        private static int WaveSequenceBankKey(string bankId)
        {
            return bankId == "INT" ? 0 : BankKey(bankId);
        }

        private static List<Chunk> SelectBanks(byte[] content, Chunk container, IReadOnlyCollection<string> bankIds, string[] order, string label,
            Func<string, int> keyOf)
        {
            var selected = new List<Chunk>();
            foreach (var bankId in order.Where(bankIds.Contains))
            {
                var key = keyOf(bankId);
                var chunk = container.Children.SingleOrDefault(child =>
                    BinaryPrimitives.ReadInt32BigEndian(content.AsSpan(child.BodyOffset + 8)) == key)
                    ?? throw new InvalidDataException($"{label}-Bank {bankId} ist nicht in der Datei.");
                selected.Add(chunk);
            }

            var unknown = bankIds.Except(order).ToList();
            if (unknown.Count > 0)
            {
                throw new ArgumentException($"Unbekannte {label}-Bank: {string.Join(", ", unknown)}");
            }

            return selected;
        }

        private static byte[] BuildDiv1(byte[] content, Chunk div1, IReadOnlyCollection<string> programBanks, IReadOnlyCollection<string> combiBanks,
            IReadOnlyCollection<string> waveSequenceBanks)
        {
            var bytes = content.AsSpan(div1.Offset, div1.TotalLength).ToArray();
            var body = 12;

            var programMask = Mask(programBanks, ProgramBankOrder);
            SetMask(bytes, body + 0x04, (ushort)(programMask & 0xFFFF));          // I-A .. U-CC
            SetMask(bytes, body + 0x08, (ushort)(programMask >> 16));             // U-DD .. U-GG
            SetMask(bytes, body + 0x0C, (ushort)Mask(combiBanks, CombiBankOrder));

            // Drum Kits nicht enthalten: Werte wie in der vom Kronos gespeicherten Teil-PCG.
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(body + 0x14), 0x0000000F);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(body + 0x18), 0x0000000F);

            // Wave Sequences: Bitmaske (Bit 0 = INT, 1..7 = U-A..U-G, 8..14 = U-AA..U-GG) und Anzahl 15;
            // das folgende Wort ist in vollen PCGs 0x10, in der Kronos-Teil-PCG ohne Wave Sequences 0x0F.
            var waveMask = (ushort)Mask(waveSequenceBanks, WaveSequenceBankOrder);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(body + 0x1C), waveMask);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(body + 0x1E), 0x000F);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(body + 0x20), waveMask != 0 ? 0x00000010 : 0x0000000F);

            bytes[body + 0x24] = 0x00;   // Drum Patterns (DPI)
            bytes[body + 0x25] = 0x00;   // Set Lists
            bytes[body + 0x27] = 0x00;   // Global
            return bytes;
        }

        private static long Mask(IReadOnlyCollection<string> banks, string[] order)
        {
            long mask = 0;
            for (var index = 0; index < order.Length; index++)
            {
                if (banks.Contains(order[index]))
                {
                    mask |= 1L << index;
                }
            }

            return mask;
        }

        private static void SetMask(byte[] bytes, int offset, ushort mask)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), mask);
        }

        private static void WriteContainer(Stream output, byte[] content, Chunk container, List<Chunk> children)
        {
            var header = content.AsSpan(container.Offset, 12).ToArray();
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), children.Sum(child => child.TotalLength));
            output.Write(header);
            foreach (var child in children)
            {
                output.Write(content, child.Offset, child.TotalLength);
            }
        }
    }
}
