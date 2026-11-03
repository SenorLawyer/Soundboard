using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace Soundboard;

public partial class App : Application
{
    private Mutex? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new Mutex(true, @"Local\SenorLawyer.Soundboard", out bool first);
        if (!first)
        {
            Shell.SignalExisting();
            Shutdown();
            return;
        }
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soundboard");
            var library = new Library(root, Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds"));
            MainWindow = new MainWindow(library);
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Soundboard", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
