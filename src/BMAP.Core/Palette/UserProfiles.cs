namespace BMAP.Palette;

/// <summary>Colour profiles saved by the user (%APPDATA%\BMAP\profiles\*.json), e.g. measured map colours.</summary>
public static class UserProfiles
{
    public static string Directory =>
        Path.Combine(AppPaths.Roaming, "profiles");

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

    public static string Save(ColorProfile profile)
    {
        System.IO.Directory.CreateDirectory(Directory);
        string path = Path.Combine(Directory, profile.Id + ".json");
        File.WriteAllText(path, profile.ToJson(), new System.Text.UTF8Encoding(false));
        return path;
    }
}