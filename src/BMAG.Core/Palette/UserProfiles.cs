namespace BMAG.Palette;

/// <summary>Colour profiles saved by the user (%APPDATA%\BMAG\profiles\*.json), e.g. measured map colours.</summary>
public static class UserProfiles
{
    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BMAG", "profiles");

    public static List<ColorProfile> Load()
    {
        var list = new List<ColorProfile>();
        if (!System.IO.Directory.Exists(Directory)) return list;
        foreach (var f in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            try { list.Add(ColorProfile.LoadFile(f)); } catch { /* ignore broken files */ }
        }
        return list;
    }
}