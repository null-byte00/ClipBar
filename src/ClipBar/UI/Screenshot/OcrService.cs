using System.IO;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace ClipBar.UI.Screenshot;

public static class OcrText
{
    public static async Task<string> RecognizeAsync(BitmapSource image)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        enc.Save(ms);
        ms.Position = 0;

        var ras = ms.AsRandomAccessStream();
        var dec = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras);
        var sb = await dec.GetSoftwareBitmapAsync();
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? throw new InvalidOperationException("OCR-движок недоступен на этой системе");
        var res = await engine.RecognizeAsync(sb);
        return res.Text;
    }
}
