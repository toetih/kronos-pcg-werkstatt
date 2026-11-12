using PcgTools.Model.Common.Synth.MemoryAndFactory;

namespace PcgWeb;

/// <summary>Eine geladene PCG (Quelle oder Ziel) samt Ladezustand. Die Datei liegt im Speicher des Browsers.</summary>
public sealed class PcgSlot
{
    public string Name { get; set; }
    public string Path { get; set; }
    public PcgMemory Pcg { get; set; }

    /// <summary>Text während des Ladens; null, wenn nichts lädt.</summary>
    public string Status { get; set; }

    /// <summary>0–100 beim Lesen, -1 = läuft ohne Prozentangabe.</summary>
    public int Progress { get; set; }

    public string Error { get; set; }
}
