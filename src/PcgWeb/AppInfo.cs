namespace PcgWeb;

/// <summary>Feste Angaben für die Fußzeile: Herkunft des Kerns, Quelltext und rechtliche Seiten des Shops.</summary>
public static class AppInfo
{
    /// <summary>Öffentliches Projekt mit dem Quelltext (der Kern stammt aus PCG Tools und steht unter der LGPL).</summary>
    public const string SourceUrl = "https://github.com/toetih/kronos-pcg-werkstatt";

    /// <summary>Anleitung zum Ausdrucken (eine Seite), liegt bei der Werkstatt selbst; Quelle: anleitung/ im privaten Projekt.</summary>
    public static string GuideUrl => KronosPcg.Lang.English
        ? "https://werkstatt.keyboard-sounds.com/anleitung/toolbox-guide.pdf"
        : "https://werkstatt.keyboard-sounds.com/anleitung/werkstatt-anleitung.pdf";

    public const string PcgToolsUrl = "https://github.com/DaBlick/PCG-Tools";
    public const string ShopUrl = "https://keyboard-sounds.com";
    public const string ImprintUrl = "https://keyboard-sounds.com/policies/legal-notice";
    public const string PrivacyUrl = "https://keyboard-sounds.com/policies/privacy-policy";
}
