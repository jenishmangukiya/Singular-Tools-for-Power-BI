using System;
using System.Diagnostics;
using System.Windows.Automation;

namespace SingularTools_App;

/// <summary>
/// Drives an already-open Power BI Desktop report by selecting its page tabs
/// through UI Automation.
/// </summary>
internal static class PowerBiNavigator
{
    public static bool TryGoToPage(string pageDisplayName, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(pageDisplayName))
        {
            error = "Page name is empty.";
            return false;
        }

        try
        {
            var pbi = FindPowerBiProcess();
            if (pbi == null)
            {
                error = "Power BI Desktop is not running.";
                return false;
            }

            var window = AutomationElement.RootElement.FindFirst(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, pbi.Id));

            if (window == null)
            {
                error = "Could not access the Power BI Desktop window.";
                return false;
            }

            var tab = FindPageTab(window, pageDisplayName);
            if (tab == null)
            {
                error = $"Page '{pageDisplayName}' was not found in the open report.";
                return false;
            }

            if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
            {
                ((SelectionItemPattern)selection).Select();
                return true;
            }

            if (tab.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
            {
                ((InvokePattern)invoke).Invoke();
                return true;
            }

            error = "The Power BI page tab does not support selection.";
            return false;
        }
        catch (ElementNotAvailableException)
        {
            error = "The Power BI window changed while navigating.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            App.Log($"PowerBiNavigator error: {ex}");
            return false;
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
                // Ignore processes that exit while enumerating.
            }

            fallback ??= process;
        }

        return fallback;
    }

    private static AutomationElement? FindPageTab(AutomationElement window, string pageDisplayName)
    {
        var tabCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
        var tabs = window.FindAll(TreeScope.Descendants, tabCondition);

        AutomationElement? partialMatch = null;
        foreach (AutomationElement tab in tabs)
        {
            string className;
            string name;
            try
            {
                className = tab.Current.ClassName ?? string.Empty;
                name = tab.Current.Name ?? string.Empty;
            }
            catch (ElementNotAvailableException)
            {
                continue;
            }

            // Page tabs live on the draggable thumbnail strip, which distinguishes
            // them from ribbon and view tabs that share the TabItem control type.
            if (!className.Contains("thumbnail-container") && !className.Contains("section dynamic"))
            {
                continue;
            }

            if (string.Equals(name, pageDisplayName, StringComparison.OrdinalIgnoreCase))
            {
                return tab;
            }

            if (partialMatch == null && name.IndexOf(pageDisplayName, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                partialMatch = tab;
            }
        }

        return partialMatch;
    }
}
