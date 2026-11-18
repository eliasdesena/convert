using System.Diagnostics;
using System.Text;

namespace Convert.Core;

/// <summary>Finds the external programs Convert uses.</summary>
public static class Tools
{
    private static readonly string AppDir = AppContext.BaseDirectory;

    public static string? Find(string exe, params string[] extraDirs)
    {
        IEnumerable<string> Dirs()
        {
            yield return Path.Combine(AppDir, "tools");
            yield return AppDir;
            foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                yield return d.Trim('"');
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(local, "Microsoft", "WinGet", "Links");
            foreach (var d in extraDirs) yield return d;
        }
        foreach (var dir in Dirs())
        {
            try
            {
                var p = Path.Combine(Environment.ExpandEnvironmentVariables(dir), exe);
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        return FindInWingetPackages(exe);
    }

    /// <summary>winget installs portable packages under Packages\&lt;id&gt;\…\bin without always linking them.</summary>
    private static string? FindInWingetPackages(string exe)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(root)) return null;
        try
        {
            return Directory.EnumerateDirectories(root)
                .SelectMany(d => Directory.EnumerateFiles(d, exe, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4 }))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static readonly Lazy<string?> ffmpeg = new(() => Find("ffmpeg.exe"));
    private static readonly Lazy<string?> ffprobe = new(() =>
    {
        var ff = ffmpeg.Value;
        var sibling = ff == null ? null : Path.Combine(Path.GetDirectoryName(ff)!, "ffprobe.exe");
        return sibling != null && File.Exists(sibling) ? sibling : Find("ffprobe.exe");
    });
    private static readonly Lazy<string?> soffice = new(() => Find("soffice.exe",
        @"%ProgramFiles%\LibreOffice\program", @"%ProgramFiles(x86)%\LibreOffice\program"));

    public static string? FFmpeg => ffmpeg.Value;
    public static string? FFprobe => ffprobe.Value;
    public static string? LibreOffice => soffice.Value;

    private static readonly Lazy<HashSet<string>> encoders = new(() =>
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (FFmpeg is not { } ff) return set;
        try
        {
            var text = Proc.RunSync(ff, ["-hide_banner", "-encoders"]);
            foreach (var line in text.Split('\n'))
            {
                var parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].Length == 6) set.Add(parts[1]);
            }
        }
        catch (Exception) { }
        return set;
    });

    public static bool HasEncoder(string name) => encoders.Value.Contains(name);
}

public static class Proc
{
    /// <summary>Runs a tool to completion and returns stdout. Throws with the last stderr line on failure.</summary>
    public static string RunSync(string exe, IEnumerable<string> args, int timeoutMs = 120_000)
    {
        using var p = Start(exe, args);
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            throw new ConvertException($"{Path.GetFileNameWithoutExtension(exe)} took too long");
        }
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new ConvertException(LastLine(stderr.Result) ?? $"{Path.GetFileNameWithoutExtension(exe)} failed (exit code {p.ExitCode})");
        return stdout.Result;
    }

    public static Process Start(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new ConvertException($"Couldn't start {Path.GetFileName(exe)}");
        p.StandardInput.Close();
        return p;
    }

    public static string? LastLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
}

public static class Paths
{
    /// <summary>Next free file name next to the input: "name.ext", then "name 2.ext", …</summary>
    public static string UniqueOutput(string input, string ext, string suffix = "")
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(input))!;
        var name = Path.GetFileNameWithoutExtension(input) + suffix;
        var path = Path.Combine(dir, $"{name}.{ext}");
        for (var n = 2; File.Exists(path) || Directory.Exists(path); n++)
            path = Path.Combine(dir, $"{name} {n}.{ext}");
        return path;
    }

    public static string UniqueFolder(string input, string suffix)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(input))!;
        var name = Path.GetFileNameWithoutExtension(input) + suffix;
        var path = Path.Combine(dir, name);
        for (var n = 2; File.Exists(path) || Directory.Exists(path); n++)
            path = Path.Combine(dir, $"{name} {n}");
        return path;
    }

    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "Convert", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Runs ffmpeg-style processes with live progress and cancellation.</summary>
public sealed class Runner
{
    private Process? current;
    private volatile bool cancelled;
    private readonly object gate = new();

    public void Cancel()
    {
        cancelled = true;
        lock (gate)
        {
            try { current?.Kill(true); } catch (InvalidOperationException) { }
        }
    }

    public void Reset() => cancelled = false;
    public bool IsCancelled => cancelled;

    public async Task RunAsync(string exe, IEnumerable<string> args, double duration, Action<double> progress)
    {
        if (cancelled) throw new OperationCanceledException();
        using var p = Proc.Start(exe, args);
        lock (gate) current = p;
        try
        {
            var err = p.StandardError.ReadToEndAsync();
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync()) != null)
            {
                if (duration > 0 && line.StartsWith("out_time_us=", StringComparison.Ordinal)
                    && double.TryParse(line.AsSpan(12), System.Globalization.CultureInfo.InvariantCulture, out var us) && us >= 0)
                    progress(Math.Min(1, us / 1_000_000 / duration));
            }
            await p.WaitForExitAsync();
            if (cancelled) throw new OperationCanceledException();
            if (p.ExitCode != 0)
                throw new ConvertException(Proc.LastLine(await err) ?? $"ffmpeg exited with code {p.ExitCode}");
        }
        finally
        {
            lock (gate) current = null;
        }
    }
}
