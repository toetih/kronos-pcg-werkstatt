// Einzelsound-Paket mit Samples bauen: Die Sounds des Pakets bekommen eine eigene kleine Sample-Bank mit eigener
// Kennung, in der nur die Samples liegen, die sie wirklich nutzen (Nummern ab 0). So stößt das Paket mit keinem anderen
// Paket und keiner Sample-Bank des Kunden zusammen. Technisch ist das dasselbe wie das Anhängen an eine leere Bank.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KronosPcg.Tools
{
    public static class SamplePack
    {
        /// <param name="pcgFile">PCG mit den Sounds des Pakets (Ergebnis des Übernehmens in eine leere PCG).</param>
        /// <param name="programs">Plätze der Programs des Pakets.</param>
        /// <param name="sourceKsc">KSC der Sample-Bank, aus der die Sounds ihre Samples holen; der Sample-Ordner liegt daneben.</param>
        /// <param name="name">Name der neuen Sample-Bank (KSC, _UserBank.KSC und Ordner).</param>
        public static SampleAttachResult Build(string pcgFile, IReadOnlyList<string> programs, string sourceKsc, string name, string outDir, string outPcg)
        {
            var template = Path.Combine(Path.GetTempPath(), "bank-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(template);
            var id = Guid.NewGuid().ToString();
            var latin1 = Encoding.Latin1;
            File.WriteAllBytes(Path.Combine(template, name + ".KSC"), latin1.GetBytes($"#KORG Script Version 1.0\r\n#v2\r\n#uuid:{id}\r\n"));
            File.WriteAllBytes(Path.Combine(template, name + "_UserBank.KSC"), latin1.GetBytes($"#KORG Script Version 1.0\r\n#v2\r\n#>uuid:{id}.0.0.{name}\r\n"));
            return SampleAttach.Run(new SampleAttachRequest
            {
                PcgFile = pcgFile,
                OutPcg = outPcg,
                Programs = programs,
                PackKsc = sourceKsc,
                BankKsc = Path.Combine(template, name + ".KSC"),
                OutDir = outDir,
            });
        }
    }
}
