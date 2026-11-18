using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Convert.Core;

namespace Convert;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        // Command-line modes (no window).
        if (args.Length > 0 && args[0] == "--list-extensions")
        {
            Cli.Attach();
            foreach (var ext in MediaKinds.AllExtensions) Console.WriteLine(ext);
            Shutdown(0);
            return;
        }
        if (args.Length > 0 && args[0] == "--cli")
        {
            Cli.Attach();
            Cli.RunAsync(args[1..]).ContinueWith(t => Dispatcher.Invoke(() => Shutdown(t.Result)));
            return;
        }

        var files = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            // The context menu passes long selections as a list file (one path per line) to stay under
            // the command-line length limit.
            if (args[i] == "--files" && i + 1 < args.Length)
            {
                var list = args[++i];
                try
                {
                    files.AddRange(File.ReadAllLines(list).Where(l => l.Length > 0));
                    File.Delete(list);
                }
                catch (IOException) { }
            }
            else files.Add(args[i]);
        }

        if (files.Any(f => MediaKinds.Of(f) is MediaKind.Video)) HardwareEncoder.Warmup();
        new MainWindow(new MainViewModel(files)).Show();
    }
}

/// <summary>Test mode: <c>Convert.exe --cli &lt;target-id&gt; file…</c> converts with default settings.</summary>
internal static class Cli
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    /// <summary>Convert.exe is a GUI app; borrow the parent console so Console output shows up.</summary>
    public static void Attach() => AttachConsole(-1);

    public static async Task<int> RunAsync(string[] args)
    {
        Target? target = args.Length >= 2 ? Targets.ById(args[0]) : null;
        if (target == null)
        {
            Console.WriteLine("usage: Convert.exe --cli <target> file…");
            Console.WriteLine("targets: " + string.Join(" ", Targets.All.Select(t => t.Id)));
            return 2;
        }
        var settings = new ConvSettings();
        if (Environment.GetEnvironmentVariable("CONVERT_SETTINGS") is { Length: > 0 } json)
            settings = JsonSerializer.Deserialize<ConvSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? settings;

        var failed = false;
        foreach (var path in args[1..])
        {
            var started = DateTime.Now;
            try
            {
                var outs = await Converter.ConvertAsync(Path.GetFullPath(path), target, settings, new Runner(), _ => { });
                Console.WriteLine($"OK   {Path.GetFileName(path)} → {string.Join(", ", outs.Select(Path.GetFileName))} ({(DateTime.Now - started).TotalSeconds:0.00}s)");
            }
            catch (Exception ex)
            {
                failed = true;
                Console.WriteLine($"FAIL {Path.GetFileName(path)}: {ex.Message}");
            }
        }
        return failed ? 1 : 0;
    }
}
