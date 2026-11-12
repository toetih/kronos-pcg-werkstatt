using Microsoft.AspNetCore.Components.Forms;

namespace PcgWeb;

/// <summary>Gewählte Dateien mit ihrem Pfad im gewählten Ordner (bei einzeln gewählten Dateien nur der Name).</summary>
public sealed record PickedFiles(IReadOnlyList<IBrowserFile> Files, IReadOnlyList<string> Paths)
{
    public IEnumerable<(IBrowserFile File, string Path)> All => Files.Select((file, index) => (file, Paths[index]));

    /// <summary>Ordner einer Datei innerhalb der Auswahl ("" für die oberste Ebene).</summary>
    public static string Folder(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
}
