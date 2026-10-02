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

    /// <summary>Non-fatal note to surface to the author even when publishing worked.</summary>
    public string? Warning { get; init; }

    public static PublishResult Ok() => new() { Success = true };

    public static PublishResult Ok(string warning) => new() { Success = true, Warning = warning };

    public static PublishResult Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>Stages of a multi-workspace publish, reported so a UI can show progress.</summary>
public enum PublishStage
{
    /// <summary>Writing the group's page visibility to the report.</summary>
    PreparingPages,

    /// <summary>Telling Power BI Desktop to apply the external change (step 0).</summary>
    WaitingForPowerBi,

    /// <summary>Running the publish dialog for a workspace.</summary>
    Publishing,

    /// <summary>Waiting for the publish itself to finish.</summary>
    WaitingForPublish,

    /// <summary>Putting the author's own page visibility back.</summary>
    RestoringPages,

    /// <summary>Final step: clearing the external-changes banner in Desktop.</summary>
    ApplyingExternalChanges
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

    /// <summary>How long the mid-publish flush waits for a banner that should already be up.</summary>
    private const int ExternalChangeFlushMs = 15000;

    /// <summary>
    /// How long the final flush waits for a banner Desktop has not raised yet.
    /// Desktop debounces the external-change notice and only raises it once the
    /// publish dialog has finished closing, so this is deliberately generous.
    /// </summary>
    private const int ExternalChangeFinalWaitMs = 30000;

    private static readonly string[] WorkspaceButtonLabels =
    {
        "Close Dialog", "Select", "Cancel", "Got it", "Done", "Replace", "Save", "Don't Save", "Don't save", "No", "Yes"
    };

    /// <summary>Exact label of the button on the "files changed externally" banner.</summary>
    private static readonly string[] ApplyExternalChangeLabels = { "Apply external changes" };

    // ---- Public API -------------------------------------------------------

    /// <summary>
    /// Whether Power BI Desktop is running. The Sort by Column tool writes TMDL
    /// directly, so it only makes sense to ask Desktop to reload when it is open.
    /// </summary>
    public static bool IsPowerBiRunning() => FindPowerBiProcess() != null;

    /// <summary>
    /// Makes Power BI Desktop pick up files that were changed on disk (TMDL, pages,
    /// anything else): clicks the "Apply external changes" banner and confirms the
    /// "Overwrite your unsaved edits" prompt if Desktop raises one. Blocking, so
    /// call it from a background thread.
    ///
    /// <paramref name="waitForBanner"/> is true when the banner is expected to
    /// arrive a few seconds after the files changed rather than already being on
    /// screen. Never throws: a banner that cannot be cleared is logged, not fatal.
    /// </summary>
    public static bool TryApplyExternalChangesInPowerBi(bool waitForBanner = true)
    {
        var process = FindPowerBiProcess();
        if (process == null)
        {
            App.Log("Power BI Desktop is not running — no external change to apply.");
            return false;
        }

        var mainWindow = FindMainWindow(process);
        if (mainWindow == null)
        {
            App.Log("Could not access the Power BI Desktop window — external change not applied.");
            return false;
        }

        FlushExternalChanges(process, mainWindow, CancellationToken.None, waitForBanner);
        return true;
    }

    /// <summary>
    /// Saves the report open in Power BI Desktop so the author's unsaved edits are
    /// flushed to the .pbip project folder. Singular Tools reads that folder, so
    /// this is called when the app regains focus to avoid showing stale data.
    ///
    /// It invokes the ribbon Save button through UI Automation, so Desktop does not
    /// need to be foreground and its focus is not stolen. Skips the click when
    /// Desktop reports no unsaved changes. Blocking, so call it from a background
    /// thread. Never throws: failures are logged, not fatal.
    /// </summary>
    public static bool TrySaveOpenReportInPowerBi()
    {
        var process = FindPowerBiProcess();
        if (process == null)
        {
            App.Log("Power BI Desktop is not running — nothing to save.");
            return false;
        }

        var mainWindow = FindMainWindow(process);
        if (mainWindow == null)
        {
            App.Log("Could not access the Power BI Desktop window — report not saved.");
            return false;
        }

        var saveButton = FindReportSaveButton(mainWindow);
        if (saveButton == null)
        {
            App.Log("Could not find the Save button in Power BI Desktop — report not saved.");
            return false;
        }

        try
        {
            if (!saveButton.Current.IsEnabled)
            {
                App.Log("Power BI Desktop has no unsaved changes.");
                return false;
            }
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch
        {
            return false;
        }

        if (!Invoke(saveButton, out var error))
        {
            App.Log($"Could not click Save in Power BI Desktop: {error}");
            return false;
        }

        App.Log("Clicked Save in Power BI Desktop to flush unsaved edits.");
        return true;
    }

    /// <summary>
    /// The report's Save button on the ribbon. Matches the name exactly so
    /// "Save as" / "Save a copy" and dialog buttons named "Save" are ignored, and
    /// only returns a button the author can actually see.
    /// </summary>
    private static AutomationElement? FindReportSaveButton(AutomationElement root)
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

        AutomationElement? disabled = null;
        foreach (AutomationElement button in buttons)
        {
            try
            {
                if (!string.Equals(GetName(button), "Save", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (button.Current.IsOffscreen || button.Current.BoundingRectangle.IsEmpty)
                {
                    continue;
                }

                // Prefer the enabled button (Desktop reports unsaved changes that
                // way); remember a disabled one so the caller can log "nothing to save".
                if (button.Current.IsEnabled) return button;
                disabled ??= button;
            }
            catch (ElementNotAvailableException)
            {
                continue;
            }
            catch
            {
                continue;
            }
        }

        return disabled;
    }

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
    /// <param name="progress">Optional callback reporting each stage. Invoked from a background thread.</param>
    public static PublishResult PublishToWorkspace(
        string workspaceName,
        CancellationToken token = default,
        IProgress<PublishStage>? progress = null)
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

            progress?.Report(PublishStage.Publishing);
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

            progress?.Report(PublishStage.WaitingForPublish);
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

    /// <summary>
    /// Runs a full multi-workspace publish for a temporary page-visibility setup.
    ///
    /// It swaps visibility, tells Power BI Desktop to apply the external change
    /// (otherwise Desktop still shows the previous page set and would publish
    /// the wrong one), publishes to every workspace, then always restores the
    /// original visibility and clears the banner that the restore itself raises —
    /// which is the final step, so Desktop is left showing the report exactly as
    /// the author had it.
    ///
    /// The swap is applied through <paramref name="applyVisibility"/> and the
    /// snapshot is taken with <paramref name="captureVisibility"/>, so the caller
    /// stays responsible for how the report is written.
    /// </summary>
    /// <param name="progress">
    /// Optional callback reporting each stage, so a caller can show progress.
    /// Invoked from a background thread.
    /// </param>
    public static PublishResult PublishToWorkspaces(
        IReadOnlyList<string> workspaceNames,
        IReadOnlyDictionary<string, bool> hiddenByPageId,
        Func<Dictionary<string, bool>> captureVisibility,
        Action<IReadOnlyDictionary<string, bool>> applyVisibility,
        IProgress<PublishStage>? progress = null,
        CancellationToken token = default)
    {
        if (workspaceNames == null || workspaceNames.Count == 0)
        {
            return PublishResult.Fail("No workspaces were selected.");
        }

        var process = FindPowerBiProcess();
        var mainWindow = process == null ? null : FindMainWindow(process);

        // Remember the report's own visibility before touching it.
        var originalVisibility = captureVisibility();

        int succeeded = 0;
        var failures = new List<string>();
        string? restoreWarning = null;

        try
        {
            progress?.Report(PublishStage.PreparingPages);
            applyVisibility(hiddenByPageId);

            // Step 0: make Desktop pick the swapped visibility up before publishing.
            if (process != null && mainWindow != null)
            {
                progress?.Report(PublishStage.WaitingForPowerBi);
                FlushExternalChanges(process, mainWindow, token);
            }

            for (int i = 0; i < workspaceNames.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                progress?.Report(PublishStage.Publishing);
                var result = PublishToWorkspace(workspaceNames[i], token, progress);
                if (result.Success)
                {
                    succeeded++;
                }
                else
                {
                    failures.Add($"{workspaceNames[i]}: {result.Error ?? "Publish failed."}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            failures.Add("Cancelled.");
        }
        catch (Exception ex)
        {
            App.Log($"Multi-workspace publish failed: {ex}");
            failures.Add(ex.Message);
        }
        finally
        {
            // Always restore the report's own visibility, even after a failure.
            try
            {
                progress?.Report(PublishStage.RestoringPages);
                applyVisibility(originalVisibility);

                // The restore is itself an external change; this is the final step,
                // clearing that banner too so Desktop is left with the report the
                // author started from rather than a stale notice. Desktop raises the
                // banner a few seconds after the files change, so wait for it.
                if (process != null && mainWindow != null)
                {
                    progress?.Report(PublishStage.ApplyingExternalChanges);
                    FlushExternalChanges(process, mainWindow, CancellationToken.None, waitForBanner: true);
                    App.Log("Final external-changes flush completed.");
                }
            }
            catch (Exception ex)
            {
                App.Log($"Restoring page visibility failed: {ex}");
                restoreWarning = "Could not restore page visibility — check the report in Power BI Desktop.";
            }
        }

        if (failures.Count > 0)
        {
            return PublishResult.Fail(string.Join(" | ", failures));
        }

        return restoreWarning == null
            ? PublishResult.Ok()
            : PublishResult.Ok(restoreWarning);
    }

    /// <summary>
    /// Number of workspaces reported as failed in a combined result. Failures are
    /// joined as "workspace: reason", so a colon marks one failed workspace.
    /// </summary>
    internal static int CountFailedWorkspaces(PublishResult result)
    {
        if (result.Success || string.IsNullOrEmpty(result.Error)) return 0;

        return result.Error
            .Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Count(part => part.Contains(':'));
    }

    /// <summary>
    /// Clears Power BI Desktop's "this project's files were changed externally"
    /// banner so Desktop reloads what we wrote. Clicks the banner's button and
    /// then confirms the "Overwrite your unsaved edits" prompt, retrying while the
    /// banner is still present. Never throws: a banner that cannot be cleared is
    /// logged, not fatal, so publishing can still be attempted.
    ///
    /// Detection is anchored on the banner BUTTON, not its text. Desktop keeps
    /// several permanently-present copies of the banner copy inside
    /// 'cdk-visually-hidden' live-announcer nodes for screen readers, so text
    /// matching reports a banner that is not actually on screen.
    /// </summary>
    /// <param name="waitForBanner">
    /// True when the banner is expected to arrive shortly rather than already be
    /// on screen. Desktop raises it a few seconds after the files change — and
    /// only once the publish dialog has finished closing — so the final flush has
    /// to wait for it. The mid-publish flush must not, or it would block every
    /// publish on a report whose visibility did not actually change.
    /// </param>
    public static void FlushExternalChanges(
        Process process,
        AutomationElement mainWindow,
        CancellationToken token = default,
        bool waitForBanner = false)
    {
        var sw = Stopwatch.StartNew();
        bool clicked = false;
        int clicks = 0;
        int budgetMs = waitForBanner ? ExternalChangeFinalWaitMs : ExternalChangeFlushMs;

        while (sw.ElapsedMilliseconds < budgetMs)
        {
            if (token.IsCancellationRequested) return;

            // Check the real banner first: if it is gone, we are done, and this
            // must not be confused by the always-present screen-reader copies.
            if (FindVisibleApplyButton(mainWindow) == null)
            {
                bool stillReturning = ConfirmOverwritePrompt(process, mainWindow);
                if (stillReturning)
                {
                    clicked = true;
                    clicks++;
                    Thread.Sleep(400);
                    continue;
                }

                if (clicked)
                {
                    // A click landed and the banner has not come back: we are done.
                    App.Log($"External-changes banner cleared after {clicks} click(s).");
                    return;
                }

                if (!waitForBanner)
                {
                    App.Log("No external-changes banner present.");
                    return;
                }

                // Waiting mode: Desktop has not raised the banner yet. Keep
                // polling until it appears or the budget runs out.
                Thread.Sleep(250);
                continue;
            }

            // The "Overwrite your unsaved edits" prompt is the reliable signal
            // that the banner will not clear by itself. Confirm it first.
            if (ConfirmOverwritePrompt(process, mainWindow))
            {
                App.Log("Confirmed 'Overwrite your unsaved edits' for external changes.");
                clicked = true;
                clicks++;
                Thread.Sleep(400);
                continue;
            }

            if (TryClickApplyExternalChanges(mainWindow))
            {
                App.Log("Clicked 'Apply external changes' on the external-changes banner.");
                clicked = true;
                clicks++;
                // Let the confirmation prompt appear before looking for it.
                Thread.Sleep(700);
                continue;
            }

            Thread.Sleep(250);
        }

        if (!clicked)
        {
            App.Log(waitForBanner
                ? "External-changes banner never appeared within the wait budget — nothing to apply."
                : "No external-changes banner present.");
        }
        else
        {
            WriteDiagnosticDump(mainWindow, "external-changes");
            App.Log("External-changes banner did not clear within the timeout.");
        }
    }

    /// <summary>
    /// The banner's "Apply external changes" button. Returns null when the banner
    /// is not on screen, including when only the hidden screen-reader copies exist.
    /// </summary>
    private static AutomationElement? FindVisibleApplyButton(AutomationElement root)
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
            try
            {
                if (!string.Equals(GetName(button), ApplyExternalChangeLabels[0], StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Ignore anything hidden, offscreen or disabled: only a banner the
                // author can actually see and press counts.
                if (button.Current.IsOffscreen || !button.Current.IsEnabled) continue;
                if (button.Current.BoundingRectangle.IsEmpty) continue;

                return button;
            }
            catch (ElementNotAvailableException)
            {
                continue;
            }
            catch
            {
                continue;
            }
        }

        return null;
    }

    private static bool TryClickApplyExternalChanges(AutomationElement mainWindow)
    {
        var button = FindVisibleApplyButton(mainWindow);
        if (button == null) return false;

        if (Invoke(button, out _)) return true;

        // Some WebView2 buttons ignore Invoke but honor a real click, so fall back
        // to clicking the centre of the button's own bounds.
        return TryClickAtCenter(button);
    }

    /// <summary>
    /// Clicks the centre of an element using synthetic input — the fallback for
    /// WebView2 controls that expose no usable automation pattern.
    /// </summary>
    private static bool TryClickAtCenter(AutomationElement element)
    {
        try
        {
            var rect = element.Current.BoundingRectangle;
            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return false;

            int x = (int)(rect.X + rect.Width / 2);
            int y = (int)(rect.Y + rect.Height / 2);

            NativeInput.Click(x, y);
            App.Log($"Clicked external-changes button at {x},{y}.");
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"Click at centre failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Clicks "Apply external changes" in the "Overwrite your unsaved edits" prompt.
    /// </summary>
    private static bool ConfirmOverwritePrompt(Process process, AutomationElement mainWindow)
    {
        foreach (var root in EnumerateDialogRoots(process, mainWindow))
        {
            if (FindByTypeAndName(root, ControlType.Text, "Overwrite your unsaved edits") == null
                && FindByTypeAndName(root, ControlType.Text, "external changes") == null)
            {
                continue;
            }

            if (ClickDialogButton(root, out _, ApplyExternalChangeLabels[0]))
            {
                return true;
            }

            // The prompt's button can be a plain WebView2 action button too.
            var button = FindVisibleApplyButton(root);
            if (button != null && TryClickAtCenter(button))
            {
                return true;
            }
        }

        return false;
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
