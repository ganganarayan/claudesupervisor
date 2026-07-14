using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace ClaudeSupervisor.Services;

/// <summary>
/// Runs OCR over a captured bitmap using the OCR engine built into Windows 10/11.
/// No external data files or NuGet OCR packages required.
/// </summary>
public static class OcrService
{
    /// <summary>
    /// Recognizes all text in <paramref name="bitmap"/>. Returns the recognized text,
    /// or throws if no OCR language is available on the machine.
    /// </summary>
    public static async Task<string> RecognizeAsync(Bitmap bitmap)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
        {
            throw new InvalidOperationException(
                "No OCR language is installed. Add an English language pack via " +
                "Settings → Time & language → Language, then retry.");
        }

        using SoftwareBitmap software = await ToSoftwareBitmapAsync(bitmap);
        OcrResult result = await engine.RecognizeAsync(software);
        return result.Text ?? string.Empty;
    }

    private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);

        var ras = new InMemoryRandomAccessStream();
        var writer = new DataWriter(ras);
        writer.WriteBytes(ms.ToArray());
        await writer.StoreAsync();
        await writer.FlushAsync();
        writer.DetachStream();
        ras.Seek(0);

        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(ras);
        SoftwareBitmap software = await decoder.GetSoftwareBitmapAsync();

        // OcrEngine wants a straightforward pixel format.
        if (software.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
            software.BitmapAlphaMode == BitmapAlphaMode.Straight)
        {
            SoftwareBitmap converted =
                SoftwareBitmap.Convert(software, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            software.Dispose();
            software = converted;
        }

        return software;
    }
}
