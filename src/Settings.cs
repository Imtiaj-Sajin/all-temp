using System.Text.Json;

namespace AllTemp;

enum ViewMode { Temps, Usage, Both }

sealed class Settings
{
    public int X { get; set; } = int.MinValue;
    public int Y { get; set; } = int.MinValue;
    public int Bottom { get; set; } = int.MinValue;
    public bool Pinned { get; set; }
    public int PinnedOpacity { get; set; } = 85;
    public bool ClickThrough { get; set; }
    public bool Fahrenheit { get; set; }
    public ViewMode View { get; set; } = ViewMode.Temps;
    public bool ShowGraphs { get; set; } = true;
    public int IntervalMs { get; set; } = 1000;
    public bool Hidden { get; set; }
    public bool DriverPromptShown { get; set; }
    public HashSet<string> Expanded { get; set; } = [];

    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AllTemp");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* settings are best-effort */ }
    }
}
