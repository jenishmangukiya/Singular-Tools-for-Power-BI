using System;
using System.IO;
using Microsoft.UI.Xaml;
using SingularTools_App.Shell;

namespace SingularTools_App;

public partial class App : Application
{
    public static MainWindow? CurrentMainWindow { get; private set; }
    public static string[] StartupArgs { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// True when Power BI Desktop launched us from its External Tools ribbon, rather
    /// than the user starting the app directly. The registered tool passes the model's
    /// %server% and %database% (see distribution/register-external-tool.ps1), so a
    /// ribbon launch always arrives with two positional arguments; a direct launch
    /// sees only the executable path. Used to stop the user switching away from the
    /// report Power BI handed us.
    /// </summary>
    public static bool LaunchedFromPowerBi => StartupArgs.Length > 1;

    /// <summary>The single report session shared by every tool page.</summary>
    public static ReportWorkspace Workspace { get; } = new();

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SingularTools",
        "app.log");

    public App()
    {
        TryMigrateLegacyDataFolder();

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

    /// <summary>
    /// One-time move of app data from the old <c>SingularPowerTools</c> folder to
    /// <c>SingularTools</c> after the product rename. Runs before anything reads or
    /// writes the data folder; a no-op once the old folder is gone.
    /// </summary>
    private static void TryMigrateLegacyDataFolder()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var oldFolder = Path.Combine(localAppData, "SingularPowerTools");
            var newFolder = Path.Combine(localAppData, "SingularTools");

            if (!Directory.Exists(oldFolder)) return;

            if (!Directory.Exists(newFolder))
            {
                // Fast path: nothing to merge, just rename the whole folder.
                Directory.Move(oldFolder, newFolder);
                return;
            }

            // Both exist: move each entry over, leaving any name that already
            // exists in the new folder alone rather than overwriting it.
            foreach (var directory in Directory.GetDirectories(oldFolder))
            {
                var target = Path.Combine(newFolder, Path.GetFileName(directory));
                if (!Directory.Exists(target)) Directory.Move(directory, target);
            }

            foreach (var file in Directory.GetFiles(oldFolder))
            {
                var target = Path.Combine(newFolder, Path.GetFileName(file));
                if (!File.Exists(target)) File.Move(file, target);
            }
        }
        catch
        {
            // Migration is best-effort: a failure must never stop the app starting.
        }
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

