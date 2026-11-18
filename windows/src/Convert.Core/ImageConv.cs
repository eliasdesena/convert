using ImageMagick;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Convert.Core;

public static class ImageConv
{
    public static MagickFormat FormatFor(string id) => id switch
    {
        "jpg" or "pdf-jpg" => MagickFormat.Jpeg,
        "png" or "pdf-png" => MagickFormat.Png,
        "heic" => MagickFormat.Heic,
        "avif" => MagickFormat.Avif,
        "webp" => MagickFormat.WebP,
        "tiff" => MagickFormat.Tiff,
        "gif-img" => MagickFormat.Gif,
        "bmp" => MagickFormat.Bmp,
        "pdf-img" => MagickFormat.Pdf,
        _ => throw new ConvertException($"Unknown image format {id}"),
    };

    public static bool CanWrite(string id)
    {
        // Magick.NET reads HEIC but can't write it; Windows' own HEIF encoder can.
        if (id == "heic") return WindowsHeif.Available;
        try { return MagickFormatInfo.Create(FormatFor(id))?.SupportsWriting == true; }
        catch (Exception) { return false; }
    }

    public static string Convert(string input, Target t, ConvSettings s)
    {
        var read = new MagickReadSettings { FrameIndex = 0, FrameCount = 1 };
        MagickImage img;
        try { img = new MagickImage(input, read); }
        catch (MagickException e) { throw new ConvertException($"Couldn't read this image ({FirstLine(e.Message)})"); }

        using (img)
        {
            img.AutoOrient();
            if (s.MaxSize > 0 && Math.Max(img.Width, img.Height) > s.MaxSize)
                img.Resize(new MagickGeometry((uint)s.MaxSize, (uint)s.MaxSize));   // fits inside, keeps aspect
            if (t.Id is "jpg" or "bmp" && img.HasAlpha)
            {
                img.BackgroundColor = MagickColors.White;
                img.Alpha(AlphaAction.Remove);
                img.Alpha(AlphaAction.Off);
            }
            if (t.IsLossyImage) img.Quality = (uint)Math.Round(Math.Clamp(s.ImageQuality, 0.05, 1) * 100);
            if (t.Id == "pdf-img") img.Strip();

            var out_ = Paths.UniqueOutput(input, t.Ext);
            try
            {
                if (t.Id == "heic") WindowsHeif.Write(img, out_, s.ImageQuality);
                else img.Write(out_, FormatFor(t.Id));
            }
            catch (MagickException e)
            {
                Paths.TryDelete(out_);
                throw new ConvertException($"Couldn't write {t.Label} ({FirstLine(e.Message)})");
            }
            catch (Exception e) when (e is not ConvertException)
            {
                Paths.TryDelete(out_);
                throw new ConvertException($"Couldn't write {t.Label} ({FirstLine(e.Message)})");
            }
            return out_;
        }
    }

    private static string FirstLine(string s) => s.Split('\n')[0].Trim();
}

/// <summary>
/// HEIC through Windows Imaging Component. Needs the "HEIF Image Extensions" and "HEVC Video Extensions"
/// from the Microsoft Store; without them the target is hidden.
/// </summary>
public static class WindowsHeif
{
    private static readonly Lazy<bool> available = new(() =>
    {
        try
        {
            using var probe = new MagickImage(MagickColors.Gray, 64, 64);
            var tmp = Path.Combine(Path.GetTempPath(), $"convert-heif-probe-{Guid.NewGuid():N}.heic");
            try
            {
                Write(probe, tmp, 0.5);
                return new FileInfo(tmp).Length > 0;
            }
            finally { Paths.TryDelete(tmp); }
        }
        catch (Exception) { return false; }
    });

    public static bool Available => available.Value;

    /// <summary>Encodes an already decoded, oriented and resized image as HEIC.</summary>
    public static void Write(MagickImage img, string path, double quality) =>
        // WinRT async calls block safely on the thread pool (never on a UI thread).
        Task.Run(() => WriteAsync(img, path, quality)).GetAwaiter().GetResult();

    private static async Task WriteAsync(MagickImage img, string path, double quality)
    {
        // Hand the pixels over as BGRA8, the format WIC's encoders take.
        var w = img.Width;
        var h = img.Height;
        byte[] pixels;
        using (var copy = (MagickImage)img.Clone())
        {
            copy.ColorSpace = ColorSpace.sRGB;
            copy.Alpha(AlphaAction.Set);
            pixels = copy.GetPixels().ToByteArray(PixelMapping.BGRA)
                     ?? throw new ConvertException("Couldn't read the image pixels");
        }

        using var stream = new InMemoryRandomAccessStream();
        var options = new BitmapPropertySet
        {
            ["ImageQuality"] = new BitmapTypedValue((float)Math.Clamp(quality, 0.05, 1), Windows.Foundation.PropertyType.Single),
        };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.HeifEncoderId, stream, options);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, w, h, 96, 96, pixels);
        await encoder.FlushAsync();

        stream.Seek(0);
        await using var src = stream.AsStreamForRead();
        await using var fs = File.Create(path);
        await src.CopyToAsync(fs);
    }
}
