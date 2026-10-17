using System.Text.Json;

namespace BMAP.App.ViewModels;

/// <summary>Per-user app settings (%APPDATA%\BMAP\settings.json).</summary>
public sealed class AppSettings
{
    public bool English { get; set; }
    public string? ResourcePack { get; set; }
    public bool TexturesAccepted { get; set; }
    public bool TexturesAsked { get; set; }

    private static string FilePath =>
        Path.Combine(AppPaths.Roaming, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* settings are a convenience */ }
    }
}
