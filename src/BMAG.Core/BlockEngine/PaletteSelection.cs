using BMAG.Palette;

namespace BMAG.BlockEngine;

/// <summary>User's choice of usable blocks plus survival restrictions (design 6.2 / 6.8).</summary>
public sealed record PaletteSelection
{
    /// <summary>Explicitly enabled block keys. Null = use each block's DefaultEnabled flag.</summary>
    public HashSet<string>? EnabledKeys { get; init; }

    public bool ExcludeGravity { get; init; } = true;
    public bool ExcludeFlammable { get; init; }
    public bool ExcludeBiomeTinted { get; init; } = true;
    /// <summary>Blocks rarer than this are not used (0..3).</summary>
    public int MaxRarity { get; init; } = 3;
    /// <summary>Upper bound for the number of different blocks in the result (0 = unlimited).</summary>
    public int MaxBlockTypes { get; init; }

    public bool IsAllowed(BlockDefinition b)
    {
        bool enabled = EnabledKeys?.Contains(b.Key) ?? b.DefaultEnabled;
        if (!enabled) return false;
        if (ExcludeGravity && b.Gravity) return false;
        if (ExcludeFlammable && b.Flammable) return false;
        if (ExcludeBiomeTinted && b.BiomeTinted) return false;
        if (b.Rarity > MaxRarity) return false;
        return true;
    }

    /// <summary>
    /// Allowed blocks with duplicates of the same map colour collapsed: the cheapest block wins
    /// (lower rarity, then palette order). The others are kept as alternatives for the material list.
    /// </summary>
    public List<PaletteEntry> Resolve(ColorProfile profile)
    {
        var groups = new Dictionary<string, PaletteEntry>();
        var order = new List<string>();
        foreach (var b in profile.Blocks)
        {
            if (!IsAllowed(b)) continue;
            // Group by every shade colour so measured differences keep blocks apart.
            string sig = string.Join("|", Enum.GetValues<Shade>().Select(s => profile.ColorOf(b, s).ToHex()));
            if (groups.TryGetValue(sig, out var existing))
            {
                if (b.Rarity < existing.Block.Rarity)
                {
                    existing.Alternatives.Add(existing.Block);
                    groups[sig] = new PaletteEntry(b, existing.Alternatives);
                }
                else existing.Alternatives.Add(b);
            }
            else
            {
                groups[sig] = new PaletteEntry(b, []);
                order.Add(sig);
            }
        }
        return order.Select(s => groups[s]).ToList();
    }
}

public sealed record PaletteEntry(BlockDefinition Block, List<BlockDefinition> Alternatives);
