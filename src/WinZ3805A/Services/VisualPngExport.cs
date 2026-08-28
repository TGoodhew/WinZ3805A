using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

using WinRT.Interop;

namespace WinZ3805A.Services;

/// <summary>
/// Writes a piece of the live visual tree to a PNG the user chooses (#47, OQ-D6).
/// </summary>
/// <remarks>
/// <para>
/// A sibling of <c>DetailsWindow.ExportFrom</c> rather than a member of it, because the two
/// share only the picker dance and differ in everything after it — one serialises a document a
/// page built, the other rasterises what is already on the screen. The picker setup is repeated
/// deliberately, with its two non-obvious requirements carried across in full: an owner HWND
/// (<c>FileSavePicker</c> is a WinRT type expecting a CoreWindow that a desktop app does not
/// have, and without one it throws at <c>PickSaveFileAsync</c> rather than at construction), and
/// writing through the returned <c>StorageFile</c>'s stream rather than by path, because the
/// broker can hand back a location this process cannot reach by path at all.
/// </para>
/// <para>
/// <b>The capture is whatever the user is looking at.</b> There is no light-theme override for
/// export: a record that silently recoloured itself would be a picture of a screen nobody saw,
/// and under high contrast the colours are the user's own choices, which this application has no
/// standing to substitute. §9.4.3's severity encoding survives the trip intact for the same
/// reason it works on screen — colour <i>plus</i> shape plus text, so a greyscale print of this
/// file still says which satellites are weak.
/// </para>
/// </remarks>
internal static class VisualPngExport
{
    /// <summary>
    /// Renders an element and saves it, reporting any failure rather than closing silently.
    /// </summary>
    /// <param name="element">The subtree to capture. Must be in the tree and not collapsed.</param>
    /// <param name="xamlRoot">The root the picker and any error dialog are parented to.</param>
    /// <param name="suggestedFileName">The name offered in the dialog, without an extension.</param>
    /// <param name="scale">Multiple of on-screen size to render at; 1 is the screen copy.</param>
    /// <remarks>
    /// <para>
    /// The render happens <b>before</b> the picker opens. Doing it afterwards would capture the
    /// card as it stood when the user finished browsing for a folder, by which time a poll may
    /// have moved every satellite — an image whose caption says one time and whose contents show
    /// another. Rendering first costs a discarded bitmap when the dialog is cancelled, which is
    /// the cheaper mistake by a wide margin.
    /// </para>
    /// <para>
    /// <c>RenderTargetBitmap</c> silently produces a truncated bitmap rather than throwing when
    /// asked for more pixels than the hardware allows, so the caller is responsible for keeping
    /// the request inside a budget — see <c>SkyPlotExport.ScaleFor</c>.
    /// </para>
    /// </remarks>
    internal static async Task SaveAsync(
        FrameworkElement element,
        XamlRoot? xamlRoot,
        string suggestedFileName,
        int scale)
    {
        if (xamlRoot is null || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return;
        }

        RenderTargetBitmap bitmap = new();
        await bitmap.RenderAsync(
            element,
            (int)Math.Round(element.ActualWidth * scale),
            (int)Math.Round(element.ActualHeight * scale));

        IBuffer buffer = await bitmap.GetPixelsAsync();

        // Read out through a DataReader rather than an IBuffer extension: the ToArray helpers
        // that used to hang off IBuffer are not on the CsWinRT projection, and the one the
        // compiler does find is ImmutableArray's.
        byte[] pixels = new byte[buffer.Length];
        using (DataReader reader = DataReader.FromBuffer(buffer))
        {
            reader.ReadBytes(pixels);
        }

        FileSavePicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = suggestedFileName,
        };

        picker.FileTypeChoices.Add("PNG image", [".png"]);

        InitializeWithWindow.Initialize(
            picker,
            Win32Interop.GetWindowFromWindowId(xamlRoot.ContentIslandEnvironment.AppWindowId));

        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            CachedFileManager.DeferUpdates(file);

            using (IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                // SetLength(0) for the same reason the CSV export does it: saving over a bigger
                // previous export would otherwise leave its tail behind, and a PNG with trailing
                // bytes opens in some viewers and not others.
                stream.Size = 0;

                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);

                // The DPI is the element's own, not a fixed 96. A capture taken at 225 % display
                // scaling carries three times the pixels of one taken at 100 %, and a file that
                // claimed 96 dpi for both would place the same plot at three different physical
                // sizes in a document depending on which machine exported it.
                double dpi = 96d * element.XamlRoot.RasterizationScale * scale;

                encoder.SetPixelData(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    (uint)bitmap.PixelWidth,
                    (uint)bitmap.PixelHeight,
                    dpi,
                    dpi,
                    pixels);

                await encoder.FlushAsync();
            }

            await CachedFileManager.CompleteUpdatesAsync(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // §9.11's reservation of ContentDialog is stretched here exactly as far as the CSV
            // path stretches it, and for the same reason: the failure follows directly from
            // something the user just asked for, and silence after a save dialog closes reads as
            // success.
            await new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "Couldn't save the image",
                Content = $"{file.Name} could not be written. {exception.Message}",
                CloseButtonText = "Close",
            }.ShowAsync();
        }
    }
}
