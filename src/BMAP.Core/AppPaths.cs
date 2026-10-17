namespace BMAP;

/// <summary>
/// Per-user folders. The app was called BMAG (Bedrock Map Art Generator) before v0.4.0: data in the old folders is
/// copied to the new ones the first time they are used, so presets, custom blocks and settings are kept.
/// </summary>
public static class AppPaths
{
    /// <summary>%APPDATA%\BMAP (settings, presets, custom blocks, colour profiles).</summary>
    public static string Roaming => Get(Environment.SpecialFolder.ApplicationData);
    /// <summary>%LOCALAPPDATA%\BMAP (texture cache, logs).</summary>
    public static string Local => Get(Environment.SpecialFolder.LocalApplicationData);

    private static string Get(Environment.SpecialFolder folder)
    {
        string baseDir = Environment.GetFolderPath(folder);
        string dir = Path.Combine(baseDir, "BMAP"), old = Path.Combine(baseDir, "BMAG");
        if (!Directory.Exists(dir) && Directory.Exists(old))
        {
            try { CopyDirectory(old, dir); } catch { /* migration is best effort; the old folder stays untouched */ }
        }
        return dir;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: false);
        foreach (var d in Directory.EnumerateDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }

    /// <summary>Project files: *.bmap.json, and *.bmag.json from older versions.</summary>
    public static bool IsProjectFile(string path) =>
        path.EndsWith(".bmap.json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bmag.json", StringComparison.OrdinalIgnoreCase);
}