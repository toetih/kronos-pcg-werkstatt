using System;
using System.IO;
using System.Linq;

namespace KronosPcg.Tools;

/// <summary>
/// Stellt sicher, dass nie eine vorhandene Datei überschrieben wird.
/// </summary>
public static class SafeOutput
{
    public static void Check(string outFile, params string[] inputs)
    {
        var outPath = Path.GetFullPath(outFile);
        if (inputs.Any(input => string.Equals(Path.GetFullPath(input), outPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(Lang.T("Die Ausgabedatei darf keine der Eingabedateien sein.",
                "The output file must not be one of the input files."));
        }

        if (File.Exists(outPath))
        {
            throw new ArgumentException(Lang.T($"{outFile} existiert bereits. Bitte einen neuen Dateinamen wählen.",
                $"{outFile} already exists. Please choose a new file name."));
        }
    }
}
