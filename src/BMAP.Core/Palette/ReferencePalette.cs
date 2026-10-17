using System.Text.RegularExpressions;
using BMAP.Imaging;

namespace BMAP.Palette;

/// <summary>One colour category of another project's palette (third-party data, kept separate from BMAP's profile).</summary>
public sealed record ReferenceColour(string Key, string Name, string BlockId, string States, bool BiomeDependent, Rgba Normal);

/// <summary>
/// Reference palettes from other Bedrock map art projects. They are downloaded when needed and compared with BMAP's
/// profile and with colours read from maps the game drew; they are never shipped with BMAP and never marked as verified.
/// </summary>
public static partial class ReferencePalette
{
    /// <summary>gd-codes/mc-pixelart-maker (MIT): Bedrock colour categories determined experimentally by its author.</summary>
    public const string GdCodesUrl = "https://raw.githubusercontent.com/gd-codes/mc-pixelart-maker/main/scripts/data.js";

    /// <summary>Parses the <c>Colours</c> map of gd-codes' data.js (name, id with states, biome flag, normal RGB).</summary>
    public static List<ReferenceColour> ParseGdCodes(string js)
    {
        var list = new List<ReferenceColour>();
        foreach (Match m in GdEntry().Matches(js))
        {
            string id = m.Groups["id"].Value.Trim();
            int bracket = id.IndexOf(' ');
            string block = bracket < 0 ? id : id[..bracket];
            string states = bracket < 0 ? "" : id[(bracket + 1)..].Trim();
            list.Add(new ReferenceColour(m.Groups["key"].Value, m.Groups["name"].Value, "minecraft:" + block, states,
                m.Groups["biome"].Value == "true",
                new Rgba(byte.Parse(m.Groups["r"].Value), byte.Parse(m.Groups["g"].Value), byte.Parse(m.Groups["b"].Value))));
        }
        return list;
    }

    [GeneratedRegex("""\["(?<key>\w+)",\s*\{\s*name:\s*"(?<name>[^"]+)"[\s\S]*?id:\s*'(?<id>[^']+)'[\s\S]*?is_biomevar:\s*(?<biome>\w+)[\s\S]*?rgb:\s*\[(?<r>\d+),\s*(?<g>\d+),\s*(?<b>\d+)""")]
    private static partial Regex GdEntry();

    /// <summary>A reference colour compared with BMAP's profile.</summary>
    public sealed record Comparison(ReferenceColour Reference, ColourSet? ExactSet, ColourSet? BlockSet, Rgba? BlockSetNormal, int Distance);

    /// <summary>
    /// For every reference colour: the BMAP colour set whose normal colour is identical (if any), and the set of the same
    /// block identifier with its normal colour and the largest channel difference.
    /// </summary>
    public static List<Comparison> Compare(IEnumerable<ReferenceColour> reference, ColorProfile profile)
    {
        var byNormal = new Dictionary<Rgba, ColourSet>();
        foreach (var s in profile.ColourSets)
            byNormal.TryAdd(profile.Shaded(Rgba.FromHex(s.MapColor), Shade.Normal), s);
        var result = new List<Comparison>();
        foreach (var r in reference)
        {
            byNormal.TryGetValue(r.Normal, out var exact);
            var block = profile.Blocks.FirstOrDefault(b => b.Name == r.BlockId);
            var set = block is null ? null : profile.SetOf(block);
            Rgba? n = set is null ? null : profile.Shaded(Rgba.FromHex(set.MapColor), Shade.Normal);
            int dist = n is { } c ? Math.Max(Math.Abs(c.R - r.Normal.R), Math.Max(Math.Abs(c.G - r.Normal.G), Math.Abs(c.B - r.Normal.B))) : -1;
            result.Add(new Comparison(r, exact, set, n, dist));
        }
        return result;
    }
}
