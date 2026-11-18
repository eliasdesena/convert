using Convert.Core;
using ImageMagick;
using Xunit;

namespace Convert.Core.Tests;

/// <summary>Runs real conversions against media generated on the fly.</summary>
public sealed class ConversionTests : IDisposable
{
    private readonly string dir = Paths.TempDir();

    public void Dispose() => Paths.TryDelete(dir);

    private string Make(string name, params string[] lavfi)
    {
        var path = Path.Combine(dir, name);
        Proc.RunSync(Tools.FFmpeg!, ["-hide_banner", "-loglevel", "error", "-y", .. lavfi, path]);
        return path;
    }

    private string Audio() => Make("tone.wav", "-f", "lavfi", "-i", "sine=frequency=440:duration=2");
    private string Video() => Make("clip.mp4", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25:duration=2",
        "-f", "lavfi", "-i", "sine=duration=2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest");

    private static async Task<List<string>> Run(string input, string targetId, ConvSettings? s = null)
    {
        var t = Targets.ById(targetId) ?? throw new Exception($"target {targetId} unavailable");
        var outs = await Converter.ConvertAsync(input, t, s ?? new ConvSettings(), new Runner(), _ => { });
        foreach (var o in outs) Assert.True(File.Exists(o) || Directory.Exists(o), $"{o} missing");
        return outs;
    }

    [Fact]
    public void FFmpegIsFound() => Assert.NotNull(Tools.FFmpeg);

    [Theory]
    [InlineData("mp3")] [InlineData("m4a")] [InlineData("flac")] [InlineData("opus")] [InlineData("ogg")]
    [InlineData("wma")] [InlineData("ac3")] [InlineData("wv")] [InlineData("mp2")] [InlineData("alac")] [InlineData("aiff")]
    public async Task AudioTargets(string id)
    {
        var out_ = (await Run(Audio(), id)).Single();
        var p = FF.ProbeFile(out_);
        Assert.True(p.HasAudio);
        Assert.InRange(p.Duration, 1.8, 2.3);
    }

    [Theory]
    [InlineData("mp4-h264")] [InlineData("mkv")] [InlineData("webm")] [InlineData("mov-prores")] [InlineData("gif")] [InlineData("mp4-av1")]
    public async Task VideoTargets(string id)
    {
        var out_ = (await Run(Video(), id)).Single();
        Assert.True(FF.ProbeFile(out_).HasVideo);
    }

    [Fact]
    public async Task SoftwareHevcWithResize()
    {
        var out_ = (await Run(Video(), "mp4-hevc", new ConvSettings { Hardware = false, VideoHeight = 120 })).Single();
        Assert.True(FF.ProbeFile(out_).HasVideo);
    }

    [Fact]
    public async Task VideoToAudioAndNoOverwrite()
    {
        var v = Video();
        var a = (await Run(v, "mp3")).Single();
        var b = (await Run(v, "mp3")).Single();
        Assert.Equal("clip.mp3", Path.GetFileName(a));
        Assert.Equal("clip 2.mp3", Path.GetFileName(b));
    }

    [Fact]
    public async Task AudioFileHasNoVideoTrack()
    {
        var e = await Assert.ThrowsAsync<ConvertException>(() => Run(Audio(), "mp4-h264"));
        Assert.Contains("no video", e.Message);
    }

    private string Image(string ext)
    {
        var path = Path.Combine(dir, "pic." + ext);
        using var img = new MagickImage(MagickColors.Transparent, 300, 200);
        img.Draw(new ImageMagick.Drawing.Drawables().FillColor(MagickColors.OrangeRed).Rectangle(20, 20, 200, 150));
        img.Write(path);
        return path;
    }

    [Theory]
    [InlineData("jpg")] [InlineData("png")] [InlineData("webp")] [InlineData("avif")] [InlineData("heic")]
    [InlineData("tiff")] [InlineData("gif-img")] [InlineData("bmp")] [InlineData("pdf-img")]
    public async Task ImageTargets(string id)
    {
        if (Targets.ById(id) == null) return;   // not writable with this Magick build
        var out_ = (await Run(Image("png"), id, new ConvSettings { MaxSize = 150 })).Single();
        if (id == "pdf-img") return;
        using var img = new MagickImage(out_);
        Assert.Equal(150u, Math.Max(img.Width, img.Height));
    }

    [Fact]
    public async Task JpegFlattensTransparencyOnWhite()
    {
        var out_ = (await Run(Image("png"), "jpg")).Single();
        using var img = new MagickImage(out_);
        var corner = img.GetPixels().GetPixel(0, 0).ToColor()!;
        Assert.True(corner.R > 250 && corner.G > 250 && corner.B > 250);
    }

    private string Markdown()
    {
        var path = Path.Combine(dir, "notes.md");
        File.WriteAllText(path, "# Hello Convert\n\nSome **bold** text.\n\n| a | b |\n|---|---|\n| 1 | 2 |\n");
        return path;
    }

    [Fact]
    public async Task MarkdownToHtmlAndText()
    {
        var html = File.ReadAllText((await Run(Markdown(), "html")).Single());
        Assert.Contains("<h1", html);
        Assert.Contains("<table", html);
        var txt = File.ReadAllText((await Run(Markdown(), "txt")).Single());
        Assert.Contains("Hello Convert", txt);
        Assert.DoesNotContain("**", txt);
    }

    [Fact]
    public async Task MarkdownToPdfThenPdfToTextAndImages()
    {
        var pdf = (await Run(Markdown(), "pdf-doc")).Single();
        var text = File.ReadAllText((await Run(pdf, "pdf-txt")).Single());
        Assert.Contains("Hello Convert", text);

        var png = (await Run(pdf, "pdf-png", new ConvSettings { PdfDpi = 72 })).Single();
        using (var img = new MagickImage(png)) Assert.InRange((int)img.Width, 590, 600);   // A4 at 72 dpi = 595 px
        var jpg = (await Run(pdf, "pdf-jpg")).Single();
        Assert.EndsWith(".jpg", jpg);
    }

    [Fact]
    public async Task ImageOnlyPdfHasNoText()
    {
        var pdf = (await Run(Image("png"), "pdf-img")).Single();
        var e = await Assert.ThrowsAsync<ConvertException>(() => Run(pdf, "pdf-txt"));
        Assert.Contains("no extractable text", e.Message);
    }

    [Fact]
    public async Task MultiPagePdfGoesIntoAFolder()
    {
        var md = Path.Combine(dir, "long.md");
        File.WriteAllText(md, string.Join("\n\n", Enumerable.Range(1, 120).Select(i => $"Paragraph {i}: lorem ipsum dolor sit amet.")));
        var pdf = (await Run(md, "pdf-doc")).Single();
        var folder = (await Run(pdf, "pdf-png", new ConvSettings { PdfDpi = 72 })).Single();
        Assert.True(Directory.Exists(folder));
        Assert.EndsWith("long pages", folder);
        Assert.True(File.Exists(Path.Combine(folder, "page 1.png")));
        Assert.True(File.Exists(Path.Combine(folder, "page 2.png")));
    }

    [Fact]
    public void DocxNeedsLibreOffice()
    {
        var docx = Targets.ById("docx")!;
        Assert.True(DocConv.NeedsLibreOffice("a.md", docx));
        Assert.False(DocConv.NeedsLibreOffice("a.md", Targets.ById("pdf-doc")!));
        Assert.True(DocConv.NeedsLibreOffice("a.docx", Targets.ById("pdf-doc")!));
    }

}

public sealed class FormatTests
{
    [Fact]
    public void KindsByExtension()
    {
        Assert.Equal(MediaKind.Audio, MediaKinds.Of(@"C:\x\song.OPUS"));
        Assert.Equal(MediaKind.Video, MediaKinds.Of("clip.mkv"));
        Assert.Equal(MediaKind.Image, MediaKinds.Of("photo.heic"));
        Assert.Equal(MediaKind.Pdf, MediaKinds.Of("a.pdf"));
        Assert.Equal(MediaKind.Document, MediaKinds.Of("readme.md"));
        Assert.Null(MediaKinds.Of("archive.zip"));
        Assert.Null(MediaKinds.Of("noext"));
    }

    [Fact]
    public void MixedAudioVideoOffersOnlyAudio()
    {
        var groups = Targets.Groups(new HashSet<MediaKind> { MediaKind.Audio, MediaKind.Video });
        Assert.Equal([OutGroup.Audio], groups.Select(g => g.Group));
        Assert.Empty(Targets.Groups(new HashSet<MediaKind> { MediaKind.Audio, MediaKind.Image }));
    }

    [Fact]
    public void PreferredSkipsSourceFormat()
    {
        var kinds = new HashSet<MediaKind> { MediaKind.Audio };
        var available = Targets.Groups(kinds).SelectMany(g => g.Targets).ToList();
        Assert.Equal("wav", Targets.Preferred(kinds, new HashSet<string> { "mp3" }, available)!.Id);
        Assert.Equal("mp3", Targets.Preferred(kinds, new HashSet<string> { "opus" }, available)!.Id);
    }

    [Fact]
    public void UniqueOutputCountsUp()
    {
        var d = Paths.TempDir();
        try
        {
            var input = Path.Combine(d, "a.wav");
            Assert.Equal(Path.Combine(d, "a.mp3"), Paths.UniqueOutput(input, "mp3"));
            File.WriteAllText(Path.Combine(d, "a.mp3"), "");
            Assert.Equal(Path.Combine(d, "a 2.mp3"), Paths.UniqueOutput(input, "mp3"));
        }
        finally { Paths.TryDelete(d); }
    }
}
