using System.Text.Json;
using Convert.Core;

namespace Convert;

/// <summary>Remembered choices, in %LOCALAPPDATA%\Convert\settings.json.</summary>
public sealed class SettingsStore
{
    public Dictionary<string, string> LastTarget { get; set; } = [];
    public Dictionary<string, ConvSettings> Settings { get; set; } = [];
    public bool TrashOriginals { get; set; }
    public bool ShowAdvanced { get; set; }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Convert", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static SettingsStore Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<SettingsStore>(File.ReadAllText(FilePath), Json) ?? new SettingsStore();
        }
        catch (Exception) { }
        return new SettingsStore();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception) { }
    }
}
