namespace Convert.Core;

/// <summary>What kind of file something is, decided by its extension.</summary>
/// <remarks>
/// Linked into Convert.Shell as source, so it must not depend on anything else in Core.
/// It is also the single source for the extensions the context menu registers for
/// (<c>Convert.exe --list-extensions</c> → packaging manifest).
/// </remarks>
public enum MediaKind { Audio, Video, Image, Pdf, Document }

public static class MediaKinds
{
    public static readonly IReadOnlyDictionary<MediaKind, string[]> Extensions = new Dictionary<MediaKind, string[]>
    {
        [MediaKind.Audio] = ["mp3", "wav", "wave", "aif", "aiff", "aifc", "flac", "m4a", "m4b", "aac", "alac", "ogg", "oga",
                             "opus", "wma", "mka", "ape", "wv", "tta", "dsf", "dff", "ac3", "eac3", "dts", "amr", "mp2",
                             "caf", "weba"],
        [MediaKind.Video] = ["mp4", "m4v", "mov", "qt", "mkv", "webm", "avi", "flv", "wmv", "asf", "ts", "mts", "m2ts",
                             "3gp", "3g2", "ogv", "mxf", "vob", "mpg", "mpeg", "m2v", "divx", "f4v"],
        [MediaKind.Image] = ["jpg", "jpeg", "jpe", "jfif", "png", "gif", "bmp", "dib", "tif", "tiff", "webp", "heic",
                             "heif", "hif", "avif", "ico", "jxl", "psd", "tga", "dng", "cr2", "cr3", "nef", "nrw", "arw",
                             "orf", "rw2", "raf", "pef", "srw", "erf", "3fr", "svg"],
        [MediaKind.Pdf] = ["pdf"],
        [MediaKind.Document] = ["docx", "doc", "odt", "rtf", "txt", "text", "md", "markdown", "html", "htm"],
    };

    private static readonly Dictionary<string, MediaKind> ByExtension =
        Extensions.SelectMany(kv => kv.Value.Select(e => (e, kv.Key)))
                  .ToDictionary(p => p.e, p => p.Key, StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<string> AllExtensions => ByExtension.Keys.OrderBy(e => e, StringComparer.Ordinal);

    public static MediaKind? Of(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return null;
        return ByExtension.TryGetValue(ext[1..], out var k) ? k : null;
    }

    /// <summary>Kinds whose files may be converted together in one batch.</summary>
    public static bool Compatible(IReadOnlySet<MediaKind> kinds) =>
        kinds.Count == 1 || (kinds.Count == 2 && kinds.Contains(MediaKind.Audio) && kinds.Contains(MediaKind.Video));
}
