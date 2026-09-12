using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace SingularTools_App;

public partial class App : Application
{
    public static MainWindow? CurrentMainWindow { get; private set; }
    public static string[] StartupArgs { get; private set; } = Array.Empty<string>();

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SingularPowerTools",
        "app.log");

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Log($"[FATAL AppDomain] {e.ExceptionObject}");
        };

        this.UnhandledException += (s, e) =>
        {
            Log($"[FATAL Xaml] {e.Message}\n{e.Exception}");
        };

        InitializeComponent();
    }

    public static void Log(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\r\n");
        }
        catch { }
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            Log("App launched.");
            StartupArgs = Environment.GetCommandLineArgs();
            Log($"Startup args: {string.Join(" ", StartupArgs)}");

            CurrentMainWindow = new MainWindow();
            CurrentMainWindow.Activate();
            Log("MainWindow activated.");
        }
        catch (Exception ex)
        {
            Log($"Error in OnLaunched: {ex}");
            throw;
        }
    }
}

