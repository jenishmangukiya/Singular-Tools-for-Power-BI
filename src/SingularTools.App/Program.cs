using System;
using System.IO;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace SingularTools_App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        App.Log($">>> Program.Main ENTERED (PID: {Environment.ProcessId}, Args: {string.Join(" ", args)}) <<<");

        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            App.Log("ComWrappersSupport initialized.");

            Application.Start((p) =>
            {
                try
                {
                    App.Log("Application.Start callback invoked.");
                    var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(context);

                    new App();
                    App.Log("App instance constructed.");
                }
                catch (Exception ex)
                {
                    App.Log($"FATAL error inside Application.Start: {ex}");
                }
            });
            App.Log("Application.Start finished.");
        }
        catch (Exception ex)
        {
            App.Log($"FATAL error in Main: {ex}");
        }
    }
}
