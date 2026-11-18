using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Convert.Core;

namespace Convert;

public enum Phase { Idle, Running, Done }

public sealed record TargetItem(Target Target)
{
    public string GroupLabel => Target.Group.Label();
    public override string ToString() => Target.Label;
}

/// <summary>State of one Convert window. Mirrors Sources/Convert/ConvertModel.swift.</summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void ChangedAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    private readonly SettingsStore store = SettingsStore.Load();
    private readonly Runner runner = new();

    public ObservableCollection<string> Files { get; } = [];
    public int Skipped { get; private set; }
    public Phase Phase { get; private set; } = Phase.Idle;
    public double Progress { get; private set; }
    public string CurrentName { get; private set; } = "";
    public List<string> Outputs { get; } = [];
    public ObservableCollection<string> Errors { get; } = [];
    public bool Stopped { get; private set; }
    public int Trashed { get; private set; }
    public int Converted { get; private set; }

    public MainViewModel(IEnumerable<string> files)
    {
        Add(files);
    }

    public IReadOnlySet<MediaKind> Kinds => Files.Select(MediaKinds.Of).OfType<MediaKind>().ToHashSet();
    public List<(OutGroup Group, List<Target> Targets)> Groups => Targets.Groups(Kinds);
    public List<TargetItem> TargetItems => Groups.SelectMany(g => g.Targets).Select(t => new TargetItem(t)).ToList();
    public bool HasSeveralGroups => Groups.Count > 1;
    public MediaKind? PrimaryKind => Kinds.Count == 1 ? Kinds.First() : null;
    private string GroupKey => string.Join("+", Kinds.Select(k => k.ToString().ToLowerInvariant()).Order());

    // Format and settings

    private Target? target;
    public Target? Target
    {
        get => target;
        set
        {
            if (value == null || value == target) return;
            target = value;
            store.LastTarget[GroupKey] = value.Id;
            LoadSettings();
            store.Save();
            ChangedAll();
        }
    }

    public ConvSettings Settings { get; private set; } = new();

    private void LoadSettings()
    {
        if (target is not { } t) return;
        var s = store.Settings.TryGetValue(t.Id, out var saved) ? saved.Clone() : new ConvSettings();
        if (t.BitrateOptions.Length > 0 && !t.BitrateOptions.Contains(s.AudioBitrate)) s.AudioBitrate = t.DefaultBitrate;
        if (!t.SampleRates.Contains(s.SampleRate)) s.SampleRate = 0;
        Settings = s;
    }

    /// <summary>Call after the window changed <see cref="Settings"/>; remembers them for this format.</summary>
    public void SettingsChanged()
    {
        if (target is not { } t) return;
        store.Settings[t.Id] = Settings.Clone();
        store.Save();
        Changed(nameof(Warning));
    }

    public bool TrashOriginals
    {
        get => store.TrashOriginals;
        set { store.TrashOriginals = value; store.Save(); Changed(); }
    }

    public bool ShowAdvanced
    {
        get => store.ShowAdvanced;
        set { store.ShowAdvanced = value; store.Save(); Changed(); }
    }

    // Files

    public void Add(IEnumerable<string> paths)
    {
        if (Phase == Phase.Running) return;
        if (Phase == Phase.Done) Reset();
        foreach (var raw in paths)
        {
            string p;
            try { p = Path.GetFullPath(raw.Trim('"')); } catch (Exception) { Skipped++; continue; }
            if (Files.Contains(p, StringComparer.OrdinalIgnoreCase)) continue;
            if (File.Exists(p) && MediaKinds.Of(p) != null) Files.Add(p); else Skipped++;
        }
        ChooseTarget();
        ChangedAll();
    }

    public void Remove(string path)
    {
        if (Phase != Phase.Idle) return;
        Files.Remove(path);
        ChooseTarget();
        ChangedAll();
    }

    private void ChooseTarget()
    {
        var available = Groups.SelectMany(g => g.Targets).ToList();
        if (available.Count == 0) { target = null; return; }
        if (target != null && available.Contains(target)) return;
        store.LastTarget.TryGetValue(GroupKey, out var last);
        var exts = Files.Select(f => Path.GetExtension(f).TrimStart('.').ToLowerInvariant()).ToHashSet();
        target = available.FirstOrDefault(t => t.Id == last)
                 ?? Targets.Preferred(Kinds, exts, available)
                 ?? available[0];
        LoadSettings();
    }

    // Header text

    public string Title => Files.Count switch
    {
        0 => "Convert",
        1 => Path.GetFileName(Files[0]),
        _ => $"{Files.Count} files",
    };

    public string Subtitle
    {
        get
        {
            if (Files.Count == 0) return "Drop files here, or right-click files in Explorer → Convert…";
            var parts = new List<string>();
            if (Files.Count == 1)
            {
                if (MediaKinds.Of(Files[0]) is { } k) parts.Add(k.Noun());
                try { parts.Add(FormatSize(new FileInfo(Files[0]).Length)); } catch (IOException) { }
            }
            else
            {
                var names = string.Join(", ", Files.Take(3).Select(Path.GetFileName));
                parts.Add(Files.Count > 3 ? $"{names} + {Files.Count - 3} more" : names);
            }
            if (Skipped > 0) parts.Add($"{Skipped} unsupported skipped");
            return string.Join(" · ", parts);
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var i = 0;
        while (v >= 1000 && i < units.Length - 1) { v /= 1000; i++; }
        return i == 0 ? $"{bytes} bytes" : $"{v:0.#} {units[i]}";
    }

    public string Glyph => PrimaryKind?.Glyph() ?? "";

    /// <summary>Shown instead of the Recycle Bin checkbox when the selected format can't work on this PC.</summary>
    public string? Warning
    {
        get
        {
            if (target is not { } t) return null;
            if (t.Group is OutGroup.Audio or OutGroup.Video && Tools.FFmpeg == null)
                return "Needs ffmpeg (winget install Gyan.FFmpeg)";
            if (t.Group == OutGroup.Document && Tools.LibreOffice == null && Files.Any(f => DocConv.NeedsLibreOffice(f, t)))
                return "Needs LibreOffice (winget install TheDocumentFoundation.LibreOffice)";
            return null;
        }
    }

    public bool CanConvert => Phase == Phase.Idle && target != null && Files.Count > 0;

    // Running

    private void Reset()
    {
        Phase = Phase.Idle; Outputs.Clear(); Errors.Clear(); Progress = 0; Stopped = false; Trashed = 0; Converted = 0;
    }

    public async Task StartAsync()
    {
        if (target is not { } t || Phase != Phase.Idle || Files.Count == 0) return;
        Reset();
        runner.Reset();
        Phase = Phase.Running;
        ChangedAll();
        var list = Files.ToList();
        var s = Settings.Clone();
        var trash = TrashOriginals;
        double n = list.Count;

        for (var i = 0; i < list.Count; i++)
        {
            if (Stopped) break;
            var file = list[i];
            CurrentName = Path.GetFileName(file);
            Progress = i / n;
            ChangedAll();
            var index = i;
            var progress = new Progress<double>(p =>
            {
                if (Phase != Phase.Running) return;
                Progress = (index + p) / n;
                Changed(nameof(Progress));
            });
            try
            {
                var made = await Converter.ConvertAsync(file, t, s, runner, ((IProgress<double>)progress).Report);
                Outputs.AddRange(made);
                Converted++;
                // Only after a finished, separate output exists on disk; the Recycle Bin keeps it recoverable.
                if (trash && !Stopped && made.Count > 0 && !made.Contains(file, StringComparer.OrdinalIgnoreCase)
                    && made.All(m => File.Exists(m) || Directory.Exists(m)))
                {
                    if (FileOps.Recycle(file)) Trashed++;
                    else Errors.Add($"{Path.GetFileName(file)}: converted, but couldn't move it to the Recycle Bin");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                if (!Stopped) Errors.Add($"{Path.GetFileName(file)}: {e.Message}");
            }
        }
        Progress = 1;
        Phase = Phase.Done;
        ChangedAll();
    }

    public void Cancel()
    {
        if (Phase != Phase.Running) return;
        Stopped = true;
        runner.Cancel();
        ChangedAll();
    }
}
