namespace Convert.Core;

// Mirrors Sources/Convert/Formats.swift: same target IDs, defaults and option ranges.

public enum OutGroup { Video, Audio, Image, Document, PdfPages }

public static class OutGroups
{
    public static string Label(this OutGroup g) => g switch
    {
        OutGroup.PdfPages => "From PDF",
        _ => g.ToString(),
    };

    public static IReadOnlySet<OutGroup> For(MediaKind k) => k switch
    {
        MediaKind.Audio => new HashSet<OutGroup> { OutGroup.Audio },
        MediaKind.Video => new HashSet<OutGroup> { OutGroup.Video, OutGroup.Audio },
        MediaKind.Image => new HashSet<OutGroup> { OutGroup.Image },
        MediaKind.Pdf => new HashSet<OutGroup> { OutGroup.PdfPages },
        _ => new HashSet<OutGroup> { OutGroup.Document },
    };
}

public static class MediaKindText
{
    public static string Noun(this MediaKind k) => k switch
    {
        MediaKind.Pdf => "PDF",
        _ => k.ToString(),
    };

    /// <summary>Segoe Fluent Icons glyph for the kind.</summary>
    public static string Glyph(this MediaKind k) => k switch
    {
        MediaKind.Audio => "",
        MediaKind.Video => "",
        MediaKind.Image => "",
        MediaKind.Pdf => "",
        _ => "",
    };
}

public sealed record Target(string Id, string Label, string Ext, OutGroup Group)
{
    public bool IsLossyAudio => Id is "mp3" or "m4a" or "he-aac" or "opus" or "ogg" or "wma" or "ac3" or "mp2";
    public bool IsLosslessAudio => Id is "wav" or "aiff" or "flac" or "alac" or "wv";
    public bool IsLossyImage => Id is "jpg" or "heic" or "avif" or "webp";
    /// <summary>ProRes has no hardware encoder on Windows, so unlike macOS it's not listed here.</summary>
    public bool HasHardwareOption => Id is "mp4-h264" or "mp4-hevc" or "mkv";

    public int[] BitrateOptions => Id switch
    {
        "mp3" => [0, 320, 256, 192, 160, 128, 96, 64],
        "m4a" => [320, 256, 192, 160, 128, 96],
        "he-aac" => [96, 80, 64, 48, 32],
        "opus" => [256, 192, 160, 128, 96, 64, 48, 32],
        "ogg" => [320, 256, 192, 160, 128, 96],
        "wma" => [256, 192, 160, 128, 96],
        "ac3" => [640, 448, 384, 256, 192],
        "mp2" => [384, 320, 256, 192, 128],
        _ => [],
    };

    public int DefaultBitrate => Id switch
    {
        "m4a" => 256,
        "he-aac" => 64,
        "opus" => 160,
        "ogg" or "wma" => 192,
        "ac3" => 448,
        "mp2" => 256,
        _ => 0,
    };

    public int[] SampleRates => Id == "opus" ? [] : IsLossyAudio ? [0, 44100, 48000] : [0, 44100, 48000, 88200, 96000];

    public bool HasChannelOption => Id != "ogg";

    /// <summary>Whether the window has anything to show under "Advanced" for this format.</summary>
    public bool HasSettings => !(Group == OutGroup.Document && Id != "pdf-doc") && Id != "pdf-txt";

    public override string ToString() => Label;
}

public static class Targets
{
    private static List<Target>? all;

    /// <summary>Every target this machine can produce (some depend on the installed ffmpeg / Magick build).</summary>
    public static IReadOnlyList<Target> All => all ??= Build();

    private static List<Target> Build()
    {
        static IEnumerable<Target> T(OutGroup g, params (string id, string label, string ext)[] list) =>
            list.Select(x => new Target(x.id, x.label, x.ext, g));

        var audio = new List<(string, string, string)>
        {
            ("mp3", "MP3", "mp3"),
            ("m4a", "M4A (AAC)", "m4a"),
        };
        if (Tools.HasEncoder("libfdk_aac")) audio.Add(("he-aac", "M4A (HE-AAC, small)", "m4a"));
        audio.AddRange([
            ("wav", "WAV", "wav"),
            ("aiff", "AIFF", "aiff"),
            ("flac", "FLAC", "flac"),
            ("alac", "ALAC (Apple Lossless)", "m4a"),
            ("opus", "Opus", "opus"),
            ("ogg", "OGG (Vorbis)", "ogg"),
            ("wma", "WMA", "wma"),
            ("ac3", "AC3 (Dolby Digital)", "ac3"),
            ("wv", "WavPack", "wv"),
            ("mp2", "MP2", "mp2"),
        ]);

        var image = new List<(string, string, string)> { ("jpg", "JPEG", "jpg"), ("png", "PNG", "png") };
        if (ImageConv.CanWrite("heic")) image.Add(("heic", "HEIC", "heic"));
        if (ImageConv.CanWrite("avif")) image.Add(("avif", "AVIF", "avif"));
        if (ImageConv.CanWrite("webp")) image.Add(("webp", "WebP", "webp"));
        image.AddRange([("tiff", "TIFF", "tiff"), ("gif-img", "GIF", "gif"), ("bmp", "BMP", "bmp"), ("pdf-img", "PDF", "pdf")]);

        return
        [
            ..T(OutGroup.Video,
                ("mp4-h264", "MP4 (H.264)", "mp4"),
                ("mp4-hevc", "MP4 (HEVC / H.265)", "mp4"),
                ("mov-prores", "MOV (ProRes 422)", "mov"),
                ("webm", "WebM (VP9)", "webm"),
                ("mkv", "MKV (H.264)", "mkv"),
                ("mp4-av1", "MP4 (AV1)", "mp4"),
                ("gif", "GIF (animated)", "gif")),
            ..T(OutGroup.Audio, [.. audio]),
            ..T(OutGroup.Image, [.. image]),
            ..T(OutGroup.Document,
                ("pdf-doc", "PDF", "pdf"),
                ("docx", "Word (DOCX)", "docx"),
                ("doc", "Word 97–2003 (DOC)", "doc"),
                ("odt", "OpenDocument (ODT)", "odt"),
                ("rtf", "Rich Text (RTF)", "rtf"),
                ("txt", "Plain Text", "txt"),
                ("html", "HTML", "html")),
            ..T(OutGroup.PdfPages,
                ("pdf-png", "PNG (one per page)", "png"),
                ("pdf-jpg", "JPEG (one per page)", "jpg"),
                ("pdf-txt", "Plain Text", "txt")),
        ];
    }

    public static Target? ById(string id) => All.FirstOrDefault(t => t.Id == id);

    /// <summary>Output groups valid for every selected kind, in display order.</summary>
    public static List<(OutGroup Group, List<Target> Targets)> Groups(IReadOnlySet<MediaKind> kinds)
    {
        if (kinds.Count == 0) return [];
        var allowed = new HashSet<OutGroup>(Enum.GetValues<OutGroup>());
        foreach (var k in kinds) allowed.IntersectWith(OutGroups.For(k));
        return Enum.GetValues<OutGroup>().Where(allowed.Contains)
                   .Select(g => (g, All.Where(t => t.Group == g).ToList())).ToList();
    }

    /// <summary>Sensible first choice for a selection, skipping the format the files are already in.</summary>
    public static Target? Preferred(IReadOnlySet<MediaKind> kinds, IReadOnlySet<string> sourceExts, IReadOnlyList<Target> available)
    {
        MediaKind? k = kinds.Count == 1 ? kinds.First()
            : kinds.SetEquals([MediaKind.Audio, MediaKind.Video]) ? MediaKind.Audio : null;
        string[] order = k switch
        {
            MediaKind.Audio => ["mp3", "wav", "m4a"],
            MediaKind.Video => ["mp4-h264", "mov-prores"],
            MediaKind.Image => ["jpg", "png"],
            MediaKind.Pdf => ["pdf-png"],
            MediaKind.Document => ["pdf-doc", "docx"],
            _ => [],
        };
        foreach (var id in order)
        {
            var t = available.FirstOrDefault(x => x.Id == id);
            if (t != null && !sourceExts.Contains(t.Ext)) return t;
        }
        return available.FirstOrDefault(t => !sourceExts.Contains(t.Ext)) ?? available.FirstOrDefault();
    }
}

public sealed class ConvSettings
{
    // Audio
    public int AudioBitrate { get; set; }          // kbps; 0 = MP3 VBR best
    public int SampleRate { get; set; }            // 0 = same as source
    public int Channels { get; set; }              // 0 = same, 1 = mono, 2 = stereo
    public int BitDepth { get; set; }              // 0 = auto (match source), 16, 24
    // Video
    public double VideoQuality { get; set; } = 0.65; // 0 = smallest … 1 = best
    public int VideoHeight { get; set; }           // 0 = same as source
    public int Fps { get; set; }                   // 0 = same as source
    public bool KeepAudio { get; set; } = true;
    public bool Hardware { get; set; } = true;
    public int GifWidth { get; set; } = 480;
    public int GifFps { get; set; } = 15;
    // Image
    public double ImageQuality { get; set; } = 0.85;
    public int MaxSize { get; set; }               // longest edge in px; 0 = same as source
    // Documents / PDF
    public bool PaperA4 { get; set; } = true;
    public int PdfDpi { get; set; } = 150;

    public ConvSettings Clone() => (ConvSettings)MemberwiseClone();
}

public sealed class ConvertException(string message) : Exception(message);
