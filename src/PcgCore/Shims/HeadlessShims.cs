// Ersatz für WPF-Teile, die der PCG-Kern von PCG Tools aufruft.
// Ohne Oberfläche gibt es keine Dialoge und keine Master-Dateien.
using System;
using System.IO;
using Common.Mvvm;
using PcgTools.Model.Common.Synth.MemoryAndFactory;
using PcgTools.Model.Common.Synth.Meta;

namespace System.Windows
{
    public enum MessageBoxButton { OK, OKCancel, YesNoCancel, YesNo }
    public enum MessageBoxImage { None, Error, Question, Warning, Information }
    public enum MessageBoxResult { None, OK, Cancel, Yes, No }

    /// <summary>
    /// Statt eines Dialogs wird eine Ausnahme ausgelöst, damit Fehler nicht verschluckt werden.
    /// </summary>
    public static class MessageBox
    {
        public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage icon)
        {
            throw new InvalidOperationException($"{caption}: {text}");
        }
    }
}

namespace PcgTools.ViewModels
{
    public static class MainViewModel
    {
        public static string PcgToolsApplicationDataDir => Path.GetTempPath();
    }
}

namespace PcgTools.MasterFiles
{
    public class MasterFile : ObservableObject
    {
        public string FileName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Master-Dateien (Namensauflösung über eine Referenz-PCG) werden im Kern nicht unterstützt.
    /// </summary>
    public class MasterFiles
    {
        public static MasterFiles Instances { get; } = new MasterFiles();

        public MasterFile FindMasterFile(IModel model) => null;

        public IPcgMemory FindMasterPcg(IModel model) => null;
    }
}

namespace Common.Utils
{
    /// <summary>Ersatz für die WPF-Hilfsklasse (nur im Release-Build von ListGenerator genutzt).</summary>
    public static class WindowUtils
    {
        public enum EMessageBoxButton { Ok, OkCancel, YesNoCancel, YesNo }
        public enum EMessageBoxImage { None, Error, Question, Warning, Information }
        public enum EMessageBoxResult { None, Ok, Cancel, Yes, No }

        public static EMessageBoxResult ShowMessageBox(object owner, string text, string title, EMessageBoxButton button,
            EMessageBoxImage image, EMessageBoxResult defaultResult)
        {
            throw new InvalidOperationException($"{title}: {text}");
        }
    }
}
