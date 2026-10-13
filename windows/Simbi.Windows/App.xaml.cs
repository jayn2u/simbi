using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Simbi.Windows.Core;

namespace Simbi.Windows;

public partial class App : Application
{
    private Mutex? instance;
    private string instanceName = "";
    public bool TryAcquireRoot(string root)
    {
        var name = "Local\\Simbi-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())))[..24];
        if (name == instanceName) return true;
        var candidate = new Mutex(true, name, out var first);
        if (!first) { candidate.Dispose(); return false; }
        if (instance != null) { instance.ReleaseMutex(); instance.Dispose(); }
        instance = candidate; instanceName = name; return true;
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var settings = AppSettings.Load();
            var index = Array.IndexOf(e.Args, "--notes-root");
            var temporaryRoot = index >= 0 && index + 1 < e.Args.Length;
            if (temporaryRoot) settings.NotesRoot = Path.GetFullPath(e.Args[index + 1]);
            if (!TryAcquireRoot(settings.NotesRoot)) { MessageBox.Show("Simbi is already open for this notes folder.", "Simbi"); Shutdown(); return; }
            var window = new MainWindow(settings, temporaryRoot);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Simbi could not start"); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { if (instance != null) { instance.ReleaseMutex(); instance.Dispose(); } base.OnExit(e); }
}
