using System.Diagnostics;

namespace BMAP.Bedrock.World;

public sealed class WorldInfo
{
    public required string Folder { get; init; }
    public required string Name { get; init; }
    public string? Version { get; init; }
    public int[] VersionParts { get; init; } = [];
    public DateTime? LastPlayed { get; init; }
    public string Source { get; init; } = "";
    public string? Error { get; init; }
    public string FolderName => Path.GetFileName(Folder);

    public string Display => $"{Name}  [{Version ?? "?"}]  {LastPlayed:yyyy-MM-dd HH:mm}";

    public static WorldInfo Load(string folder, string source = "")
    {
        string name = Path.GetFileName(folder);
        try
        {
            var lvlName = Path.Combine(folder, "levelname.txt");
            if (File.Exists(lvlName)) name = File.ReadAllText(lvlName).Trim();
            var dat = LevelDat.Read(Path.Combine(folder, "level.dat"));
            if (!string.IsNullOrWhiteSpace(dat.LevelName)) name = dat.LevelName!;
            return new WorldInfo
            {
                Folder = folder, Name = name, Version = dat.LastOpenedWithVersion, VersionParts = dat.LastOpenedVersionParts,
                LastPlayed = dat.LastPlayed is { } lp and > 0 ? DateTimeOffset.FromUnixTimeSeconds(lp).LocalDateTime : null,
                Source = source,
            };
        }
        catch (Exception ex)
        {
            return new WorldInfo { Folder = folder, Name = name, Source = source, Error = ex.Message };
        }
    }

    public static bool LooksLikeWorld(string folder) =>
        File.Exists(Path.Combine(folder, "level.dat")) && Directory.Exists(Path.Combine(folder, "db"));
}

/// <summary>
/// Finds Bedrock worlds. The install location differs between the Microsoft Store (UWP) and newer GDK builds,
/// so several candidates are checked and manual folder selection is always possible (design 9.8).
/// </summary>
public static class WorldLocator
{
    public static IEnumerable<(string Label, string Path)> CandidateRoots()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var edition in new[] { "Minecraft Bedrock", "Minecraft Bedrock Preview" })
        {
            var users = Path.Combine(appData, edition, "Users");
            if (!Directory.Exists(users)) continue;
            foreach (var user in Directory.EnumerateDirectories(users))
            {
                var worlds = Path.Combine(user, "games", "com.mojang", "minecraftWorlds");
                if (Directory.Exists(worlds)) yield return ($"{edition} ({Path.GetFileName(user)})", worlds);
            }
        }
        foreach (var (label, pkg) in new[] { ("Microsoft Store 版", "Microsoft.MinecraftUWP_8wekyb3d8bbwe"), ("Preview (Store)", "Microsoft.MinecraftWindowsBeta_8wekyb3d8bbwe") })
        {
            var worlds = Path.Combine(local, "Packages", pkg, "LocalState", "games", "com.mojang", "minecraftWorlds");
            if (Directory.Exists(worlds)) yield return (label, worlds);
        }
    }

    public static List<WorldInfo> FindWorlds()
    {
        var list = new List<WorldInfo>();
        foreach (var (label, root) in CandidateRoots())
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                    if (WorldInfo.LooksLikeWorld(dir)) list.Add(WorldInfo.Load(dir, label));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return list.OrderByDescending(w => w.LastPlayed ?? DateTime.MinValue).ToList();
    }

    /// <summary>True when a Minecraft Bedrock process seems to be running.</summary>
    public static bool IsMinecraftRunning()
    {
        foreach (var name in new[] { "Minecraft.Windows", "Minecraft.WindowsBeta", "Minecraft.WindowsPreview" })
        {
            var procs = Process.GetProcessesByName(name);
            bool any = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            if (any) return true;
        }
        return false;
    }
}
