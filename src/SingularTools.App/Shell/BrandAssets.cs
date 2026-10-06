using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace SingularTools_App.Shell;

/// <summary>
/// Loads brand images (logo, etc.) from the executable's Assets folder.
/// Uses a file stream rather than an ms-appx URI, because unpackaged apps do not
/// reliably resolve ms-appx resources after a Release publish.
/// </summary>
internal static class BrandAssets
{
    public const string LogoFile = "Square44x44Logo.scale-200.png";

    public static async Task ApplyAsync(Image target, string fileName = LogoFile)
    {
        if (target == null) return;

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
            if (!File.Exists(path))
            {
                target.Visibility = Visibility.Collapsed;
                App.Log($"Brand asset missing: {path}");
                return;
            }

            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            target.Source = bitmap;
            App.Log($"Brand asset loaded: {path}");
        }
        catch (Exception ex)
        {
            App.Log($"Brand asset load failed: {ex}");
            target.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Loads an SVG brand asset (Power BI, Fabric) from the executable's Assets
    /// folder via <see cref="SvgImageSource"/>, using a file stream for the same
    /// unpackaged-app reason as <see cref="ApplyAsync"/>.
    /// </summary>
    public static async Task ApplySvgAsync(Image target, string fileName)
    {
        if (target == null) return;

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
            if (!File.Exists(path))
            {
                target.Visibility = Visibility.Collapsed;
                App.Log($"Brand asset missing: {path}");
                return;
            }

            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();

            var svg = new SvgImageSource();
            await svg.SetSourceAsync(stream);
            target.Source = svg;
        }
        catch (Exception ex)
        {
            App.Log($"Brand SVG load failed ({fileName}): {ex}");
            target.Visibility = Visibility.Collapsed;
        }
    }
}
