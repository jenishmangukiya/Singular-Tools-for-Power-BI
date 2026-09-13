using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace SingularTools_App;

public sealed class PublishResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }

    public static PublishResult Ok() => new() { Success = true };
    public static PublishResult Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// Drives Power BI Desktop's "Publish to Power BI" flow through UI Automation so
/// a report can be published to several workspaces without re-selecting each one
/// by hand. Reuses the same window-discovery approach as <see cref="PowerBiNavigator"/>.
///
/// Modern Power BI Desktop is a WinForms host around WebView2 (Chromium), so its
/// dialogs are HTML rendered inside a child window with AutomationId
/// 'MinervaDialog'. The workspace list lives under a List named 'Workspaces' and
/// the dialog buttons are plain Buttons with stable names ('Select', 'Cancel',
/// 'Close Dialog', 'Got it', 'Replace', 'Save').
/// </summary>
internal static class PowerBiPublisher
{
    private const int DialogWaitMs = 20000;
    private const int PublishTimeoutMs = 180000;

    private static readonly string[] WorkspaceButtonLabels =
    {
        "Close Dialog", "Select", "Cancel", "Got it", "Done", "Replace", "Save", "Don't Save", "Don't save", "No", "Yes"
    };

    // ---- Public API -------------------------------------------------------

    /// <summary>
    /// Opens the Publish dialog, reads every workspace shown in it, then closes
    /// the dialog. Throws with a user-facing message when Power BI is not running
    /// or the dialog cannot be read.
    /// </summary>
    public static IReadOnlyList<string> DetectWorkspaces()
    {
        var process = FindPowerBiProcess()
            ?? throw new InvalidOperationException("Power BI Desktop is not running.");

        var mainWindow = FindMainWindow(process)
            ?? throw new InvalidOperationException("Could not access the Power BI Desktop window.");

        AutomationElement? dialog = null;
        try
        {
            dialog = OpenPublishDialog(process, mainWindow);

            // If Power BI shows a "Save changes?" prompt (unsaved report) before
            // the workspace list finishes rendering, accept it so detection can
            // proceed.
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 3000)
            {
                if (!ClickDialogButton(process, mainWindow, out _, "Save", "Save changes"))
                {
                    break;
                }

                App.Log("Accepted 'Save changes' prompt before detecting workspaces.");
                Thread.Sleep(400);
            }

            return EnumerateWorkspaces(dialog);
        }
        finally
        {
            DismissDialog(process, mainWindow);
        }
    }

    /// <summary>
    /// Publishes the open report to a single workspace, handling the optional
    /// "Save changes?" and "Replace this report?" prompts automatically.
    /// </summary>
    public static PublishResult PublishToWorkspace(string workspaceName, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceName))
        {
            return PublishResult.Fail("Workspace name is empty.");
        }

        var process = FindPowerBiProcess()
            ?? throw new InvalidOperationException("Power BI Desktop is not running.");

        var mainWindow = FindMainWindow(process)
            ?? throw new InvalidOperationException("Could not access the Power BI Desktop window.");

        AutomationElement? dialog = null;
        try
        {
            token.ThrowIfCancellationRequested();

            dialog = OpenPublishDialog(process, mainWindow);
            token.ThrowIfCancellationRequested();

            if (!SelectWorkspace(dialog, workspaceName, out var selectError))
            {
                DismissDialog(process, mainWindow);
                return PublishResult.Fail($"Workspace '{workspaceName}' was not found in the publish list. {selectError}");
            }

            if (!ClickDialogButton(dialog, out var buttonError, "Select"))
            {
                DismissDialog(process, mainWindow);
                return PublishResult.Fail($"Could not click Select: {buttonError}");
            }

            App.Log($"Publish submitted for workspace '{workspaceName}'.");
            return WaitForPublishOutcome(process, mainWindow, token);
        }
        catch (OperationCanceledException)
        {
            DismissDialog(process, mainWindow);
            return PublishResult.Fail("Cancelled.");
        }
        catch (Exception ex)
        {
            DismissDialog(process, mainWindow);
            return PublishResult.Fail(ex.Message);
        }
    }

    // ---- Dialog lifecycle -------------------------------------------------

    private static AutomationElement OpenPublishDialog(Process process, AutomationElement mainWindow)
    {
        var publishButton = FindPublishButton(mainWindow)
            ?? throw new InvalidOperationException("Could not find the Publish button on the ribbon.");

        if (!Invoke(publishButton, out var invokeError))
        {
            throw new InvalidOperationException($"Could not click Publish: {invokeError}");
        }

        // Power BI may first ask whether to save unsaved changes; accept it so
        // publishing can continue unattended.
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < DialogWaitMs)
        {
            if (ClickDialogButton(process, mainWindow, out _, "Save", "Save changes"))
            {
                App.Log("Accepted 'Save changes' prompt before publishing.");
            }

            var dialog = FindPublishDialog(process, mainWindow);
            if (dialog != null)
            {
                Thread.Sleep(400);
                WriteDiagnosticDump(dialog, "publish-dialog");
                return dialog;
            }

            Thread.Sleep(250);
        }

        WriteDiagnosticDump(mainWindow, "main-window");
        throw new InvalidOperationException("The 'Publish to Power BI' dialog did not appear. Make sure you are signed in.");
    }

    /// <summary>Returns the Publish destination dialog (the one showing the Workspaces list).</summary>
    private static AutomationElement? FindPublishDialog(Process process, AutomationElement mainWindow)
    {
        foreach (var root in EnumerateDialogRoots(process, mainWindow))
        {
            if (FindByTypeAndName(root, ControlType.List, "Workspaces") != null)
            {
                return root;
            }
        }

        // Fallback: any dialog whose group/title reads "Publish to Power BI".
        foreach (var root in EnumerateDialogRoots(process, mainWindow))
        {
            if (FindByTypeAndName(root, ControlType.Text, "Publish to Power BI") != null)
            {
                return root;
            }
        }

        return null;
    }

    /// <summary>
    /// Every candidate dialog root: all nested MinervaDialog windows plus any
    /// non-main top-level windows belonging to the process.
    /// </summary>
    private static List<AutomationElement> EnumerateDialogRoots(Process process, AutomationElement mainWindow)
    {
        var roots = new List<AutomationElement>();
        var mainHandle = GetNativeWindowHandle(mainWindow);

        try
        {
            // Every child window of the main window (the WebView2 dialogs appear
            // as child windows, e.g. AutomationId 'MinervaDialog', but native
            // prompts such as "Save changes?" may use a different id too).
            foreach (AutomationElement element in mainWindow.FindAll(TreeScope.Descendants,
                         new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)))
            {
                roots.Add(element);
            }
        }
        catch
        {
            // Main window tree changed mid-enumeration.
        }

        try
        {
            foreach (var window in EnumerateTopLevelWindows(process))
            {
                if (mainHandle != IntPtr.Zero && GetNativeWindowHandle(window) == mainHandle)
                {
                    continue;
                }

                roots.Add(window);
            }
        }
        catch
        {
            // Process windows changed mid-enumeration.
        }

        return roots;
    }

    private static IntPtr GetNativeWindowHandle(AutomationElement element)
    {
        try
        {
            return (IntPtr)element.Current.NativeWindowHandle;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static IReadOnlyList<string> EnumerateWorkspaces(AutomationElement dialog)
    {
        var names = new List<string>();

        var list = FindByTypeAndName(dialog, ControlType.List, "Workspaces");
        var scope = list ?? dialog;

        foreach (AutomationElement item in scope.FindAll(TreeScope.Descendants,
                     new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)))
        {
            var name = GetName(item);
            if (IsRealWorkspace(name) && !names.Contains(name))
            {
                names.Add(name);
            }
        }

        if (names.Count == 0)
        {
            WriteDiagnosticDump(dialog, "publish-dialog");
        }

        return names;
    }

    private static bool IsRealWorkspace(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        foreach (var label in WorkspaceButtonLabels)
        {
            if (string.Equals(name, label, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static bool SelectWorkspace(AutomationElement dialog, string workspaceName, out string? error)
    {
        error = null;

        var list = FindByTypeAndName(dialog, ControlType.List, "Workspaces") ?? dialog;
        var item = FindByTypeAndName(list, ControlType.ListItem, workspaceName);
        if (item == null)
        {
            error = "No matching workspace item.";
            return false;
        }

        // Prefer the semantic selection; Angular Material lists usually honor it,
        // but fall back to a click (Invoke) if the Select button stays disabled.
        if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sel))
        {
            ((SelectionItemPattern)sel).Select();
            Thread.Sleep(250);
        }

        if (!IsSelectEnabled(dialog) && item.TryGetCurrentPattern(InvokePattern.Pattern, out var inv))
        {
            ((InvokePattern)inv).Invoke();
            Thread.Sleep(250);
        }

        return true;
    }

    private static bool IsSelectEnabled(AutomationElement dialog)
    {
        var select = FindByTypeAndName(dialog, ControlType.Button, "Select");
        if (select == null) return false;
        try
        {
            return select.Current.IsEnabled;
        }
        catch
        {
            return false;
        }
    }

    private static PublishResult WaitForPublishOutcome(Process process, AutomationElement mainWindow, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < PublishTimeoutMs)
        {
            token.ThrowIfCancellationRequested();

            foreach (var dialog in EnumerateDialogRoots(process, mainWindow))
            {
                // Replace prompt.
                if (ClickDialogButton(dialog, out _, "Replace", "Replace it", "Yes"))
                {
                    App.Log("Confirmed replace prompt.");
                    Thread.Sleep(400);
                    break;
                }

                // Save prompt (in case it appears after Select).
                if (ClickDialogButton(dialog, out _, "Save", "Save changes"))
                {
                    App.Log("Accepted 'Save changes' prompt during publishing.");
                    Thread.Sleep(400);
                    break;
                }

                // Success.
                if (ClickDialogButton(dialog, out _, "Got it", "Done"))
                {
                    App.Log("Publish succeeded.");
                    return PublishResult.Ok();
                }

                // Explicit error.
                var error = FindErrorText(dialog);
                if (error != null)
                {
                    DismissDialog(process, mainWindow);
                    return PublishResult.Fail(error);
                }
            }

            Thread.Sleep(300);
        }

        WriteDiagnosticDump(mainWindow, "publish-timeout");
        return PublishResult.Fail("Timed out waiting for the publish to finish.");
    }

    private static string? FindErrorText(AutomationElement dialog)
    {
        var phrases = new[]
        {
            "couldn't publish", "could not publish", "wasn't published", "was not published",
            "something went wrong", "unable to publish", "publish failed"
        };

        foreach (AutomationElement text in dialog.FindAll(TreeScope.Descendants,
                     new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)))
        {
            var value = GetName(text);
            foreach (var phrase in phrases)
            {
                if (value.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }
        }

        return null;
    }

    // ---- Button helpers ---------------------------------------------------

    /// <summary>Clicks the first button whose name exactly matches one of the candidates.</summary>
    private static bool ClickDialogButton(AutomationElement dialog, out string? error, params string[] buttonNames)
    {
        foreach (var name in buttonNames)
        {
            var button = FindButton(dialog, name);
            if (button == null) continue;

            try
            {
                if (!button.Current.IsEnabled) continue;
            }
            catch
            {
                continue;
            }

            if (Invoke(button, out error))
            {
                return true;
            }
        }

        error = $"none of [{string.Join(", ", buttonNames)}] found";
        return false;
    }

    /// <summary>Clicks a button by exact name in whichever dialog is currently open.</summary>
    private static bool ClickDialogButton(Process process, AutomationElement mainWindow, out string? error, params string[] buttonNames)
    {
        foreach (var root in EnumerateDialogRoots(process, mainWindow))
        {
            if (ClickDialogButton(root, out error, buttonNames))
            {
                return true;
            }
        }

        error = $"none of [{string.Join(", ", buttonNames)}] found in any dialog";
        return false;
    }

    private static void DismissDialog(Process process, AutomationElement mainWindow)
    {
        try
        {
            // Close whichever dialog is open (destination, replace, or success).
            ClickDialogButton(process, mainWindow, out _, "Cancel", "Close Dialog", "Got it", "Close");
        }
        catch
        {
            // Best-effort dismissal.
        }
    }

    private static AutomationElement? FindButton(AutomationElement root, string exactName)
    {
        AutomationElementCollection buttons;
        try
        {
            buttons = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        }
        catch
        {
            return null;
        }

        foreach (AutomationElement button in buttons)
        {
            if (string.Equals(GetName(button), exactName, StringComparison.OrdinalIgnoreCase))
            {
                return button;
            }
        }

        return null;
    }

    private static bool Invoke(AutomationElement element, out string? error)
    {
        error = null;
        try
        {
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
            {
                ((InvokePattern)pattern).Invoke();
                return true;
            }

            error = "element does not support Invoke";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static AutomationElement? FindPublishButton(AutomationElement mainWindow)
    {
        var button = FindByTypeAndName(mainWindow, ControlType.Button, "Publish");
        if (button != null)
        {
            return button;
        }

        // Fallback: any invokable element named "Publish".
        foreach (AutomationElement e in mainWindow.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            if (string.Equals(GetName(e), "Publish", StringComparison.OrdinalIgnoreCase)
                && e.TryGetCurrentPattern(InvokePattern.Pattern, out _))
            {
                return e;
            }
        }

        return null;
    }

    private static AutomationElement? FindByTypeAndName(AutomationElement root, ControlType type, string text)
    {
        AutomationElementCollection all;
        try
        {
            all = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, type));
        }
        catch
        {
            return null;
        }

        AutomationElement? partial = null;
        foreach (AutomationElement e in all)
        {
            var name = GetName(e);
            if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
            {
                return e;
            }

            if (partial == null && name.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                partial = e;
            }
        }

        return partial;
    }

    private static AutomationElement? FindMainWindow(Process process)
    {
        if (process.MainWindowHandle != IntPtr.Zero)
        {
            try
            {
                return AutomationElement.FromHandle(process.MainWindowHandle);
            }
            catch
            {
            }
        }

        foreach (var window in EnumerateTopLevelWindows(process))
        {
            if (!string.IsNullOrEmpty(GetName(window)))
            {
                return window;
            }
        }

        return null;
    }

    /// <summary>Every top-level window belonging to the process.</summary>
    private static IEnumerable<AutomationElement> EnumerateTopLevelWindows(Process process)
    {
        var all = AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id));
        foreach (AutomationElement window in all)
        {
            yield return window;
        }
    }

    private static Process? FindPowerBiProcess()
    {
        Process? fallback = null;
        foreach (var process in Process.GetProcessesByName("PBIDesktop"))
        {
            try
            {
                if (process.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(process.MainWindowTitle))
                {
                    return process;
                }
            }
            catch
            {
            }

            fallback ??= process;
        }

        return fallback;
    }

    private static string GetName(AutomationElement element)
    {
        try
        {
            return element.Current.Name ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // ---- Diagnostics ------------------------------------------------------

    private static string DiagnosticDumpPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SingularPowerTools",
        "publish-dialog-dump.txt");

    private static void WriteDiagnosticDump(AutomationElement root, string label)
    {
        try
        {
            var sb = new StringBuilder();
            var budget = new NodeBudget(20000);
            DumpElementDetailed(root, sb, 0, 16, budget);

            var dir = Path.GetDirectoryName(DiagnosticDumpPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(DiagnosticDumpPath, $"{DateTime.Now:O} [{label}]\n{sb}");

            App.Log($"Wrote diagnostic dump ({label}) to {DiagnosticDumpPath}");
        }
        catch (Exception ex)
        {
            App.Log($"Diagnostic dump failed: {ex.Message}");
        }
    }

    private sealed class NodeBudget
    {
        private int _remaining;
        public NodeBudget(int max) => _remaining = max;
        public bool TrySpend() => _remaining-- > 0;
    }

    private static void DumpElementDetailed(AutomationElement element, StringBuilder sb, int depth, int maxDepth, NodeBudget budget)
    {
        if (depth > maxDepth || !budget.TrySpend())
        {
            return;
        }

        try
        {
            sb.Append(' ', depth * 2)
              .Append(element.Current.ControlType.ProgrammaticName)
              .Append(" name='").Append(element.Current.Name)
              .Append("' id='").Append(element.Current.AutomationId)
              .Append("' class='").Append(element.Current.ClassName)
              .Append("' enabled=").Append(element.Current.IsEnabled)
              .AppendLine();
        }
        catch
        {
            return;
        }

        foreach (AutomationElement child in element.FindAll(TreeScope.Children, Condition.TrueCondition))
        {
            DumpElementDetailed(child, sb, depth + 1, maxDepth, budget);
        }
    }
}
