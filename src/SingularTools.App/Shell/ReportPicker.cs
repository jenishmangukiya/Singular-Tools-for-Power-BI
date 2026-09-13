using System.Threading.Tasks;
using Windows.Storage.Pickers;

namespace SingularTools_App.Shell;

/// <summary>Folder picker for choosing a PBIP report folder, shared by tool pages.</summary>
internal static class ReportPicker
{
    public static async Task<string?> PickReportFolderAsync()
    {
        var window = App.CurrentMainWindow;
        if (window == null)
        {
            return null;
        }

        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
