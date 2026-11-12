// Zwei Sprachen ohne Textdateien: Jeder sichtbare Text steht als Paar im Code, T("Deutsch", "English").
// So bleiben beide Fassungen nebeneinander und keine geht beim Ändern verloren. Die Kommandozeile bleibt deutsch.
namespace KronosPcg
{
    public static class Lang
    {
        /// <summary>Oberfläche auf Englisch (wird beim Start der Werkstatt aus Browser-Sprache bzw. Auswahl gesetzt).</summary>
        public static bool English { get; set; }

        public static string T(string german, string english) => English ? english : german;
    }
}
