using System.Runtime.InteropServices;

namespace Convert.Core;

public static class Converter
{
    /// <summary>Converts one file and returns what was written (files, or a folder for multi-page PDFs).</summary>
    public static async Task<List<string>> ConvertAsync(string input, Target t, ConvSettings s, Runner runner, Action<double> progress)
    {
        if (!File.Exists(input)) throw new ConvertException("The file no longer exists");
        switch (t.Group)
        {
            case OutGroup.Audio:
            case OutGroup.Video:
            {
                var ff = Tools.FFmpeg ?? throw new ConvertException("ffmpeg isn't installed. Install it with: winget install Gyan.FFmpeg");
                var p = await Task.Run(() => FF.ProbeFile(input));
                if (p.Ok && t.Group == OutGroup.Audio && !p.HasAudio) throw new ConvertException("This file has no audio track");
                if (p.Ok && t.Group == OutGroup.Video && !p.HasVideo) throw new ConvertException("This file has no video track");
                var codec = t.Group == OutGroup.Audio ? FF.AudioArgs(t, s, p) : await Task.Run(() => FF.VideoArgs(t, s, p));
                var out_ = Paths.UniqueOutput(input, t.Ext);
                try
                {
                    await runner.RunAsync(ff, [.. FF.Common, "-i", input, .. codec, out_], p.Duration, progress);
                }
                catch
                {
                    Paths.TryDelete(out_);
                    throw;
                }
                return [out_];
            }
            case OutGroup.Image:
                return [await Task.Run(() => ImageConv.Convert(input, t, s))];
            case OutGroup.Document:
                return [await DocConv.ConvertAsync(input, t, s)];
            case OutGroup.PdfPages:
                return await PdfConv.ConvertAsync(input, t, s, progress);
            default:
                throw new ConvertException("Unsupported target");
        }
    }
}

public static class FileOps
{
    /// <summary>Moves a file to the Recycle Bin (recoverable). Returns false if Windows refused.</summary>
    public static bool Recycle(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = Path.GetFullPath(path) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted && !File.Exists(path);
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
