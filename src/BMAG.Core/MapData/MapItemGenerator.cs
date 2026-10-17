using BMAG.Export;
using BMAG.Nbt;

namespace BMAG.MapData;

/// <summary>
/// Creates map items and structures that deliver them in game (MapItemGenerator, design 9.6 / 9.7).
/// Blocks use 1.18.10 identifiers with their palette version so the game's block upgrader converts them
/// to the current format (e.g. facing_direction -> minecraft:cardinal_direction for chests).
/// </summary>
public static class MapItemGenerator
{
    public const int LegacyBlockVersion = 17959425; // 1.18.10

    public static NbtCompound CreateMapItem(long mapId, VersionProfile profile) => new()
    {
        { "Count", new NbtByte(1) },
        { "Damage", new NbtShort(0) },
        { "Name", new NbtString(profile.MapItemName) },
        { "WasPickedUp", new NbtByte(0) },
        { "tag", new NbtCompound { { "map_uuid", new NbtLong(mapId) } } },
    };

    /// <summary>A row of chests (27 maps each) holding the maps in reading order (option B).</summary>
    public static McStructure CreateChestStructure(IReadOnlyList<long> mapIds, VersionProfile profile)
    {
        int chests = Math.Max(1, (mapIds.Count + 26) / 27);
        // Leave one block between chests so they never merge into double chests.
        var s = new McStructure(chests * 2 - 1, 1, 1);
        var chest = new PaletteBlock("minecraft:chest", new NbtCompound { { "facing_direction", new NbtInt(2) } }, LegacyBlockVersion);
        for (int c = 0; c < chests; c++)
        {
            var items = new NbtList(NbtType.Compound);
            for (int slot = 0; slot < 27; slot++)
            {
                int i = c * 27 + slot;
                if (i >= mapIds.Count) break;
                var item = CreateMapItem(mapIds[i], profile);
                item.Add("Slot", new NbtByte((byte)slot));
                items.Add(item);
            }
            var be = new NbtCompound
            {
                { "id", new NbtString("Chest") },
                { "Items", items },
                { "Findable", new NbtByte(0) },
                { "isMovable", new NbtByte(1) },
            };
            s.SetBlock(c * 2, 0, 0, chest, be);
        }
        return s;
    }

    /// <summary>
    /// A wall of item frames showing the whole picture (option C). Frames face west (-X), so the viewer stands
    /// west of the wall looking east: image columns run north to south (+Z), image rows top to bottom (-Y).
    /// With <paramref name="backingBlock"/> a wall is placed behind the frames (+X) so they have something to hang on.
    /// </summary>
    public static McStructure CreateFrameWall(long[,] mapIds, VersionProfile profile, bool glow, PaletteBlock? backingBlock)
    {
        int cols = mapIds.GetLength(0), rows = mapIds.GetLength(1);
        var s = new McStructure(backingBlock is null ? 1 : 2, rows, cols);
        var frame = new PaletteBlock(glow ? "minecraft:glow_frame" : "minecraft:frame", new NbtCompound
        {
            { "facing_direction", new NbtInt(4) },
            { "item_frame_map_bit", new NbtByte(1) },
            { "item_frame_photo_bit", new NbtByte(0) },
        }, LegacyBlockVersion);
        for (int gx = 0; gx < cols; gx++)
            for (int gy = 0; gy < rows; gy++)
            {
                int y = rows - 1 - gy;
                var be = new NbtCompound
                {
                    { "id", new NbtString(glow ? "GlowItemFrame" : "ItemFrame") },
                    { "Item", CreateMapItem(mapIds[gx, gy], profile) },
                    { "ItemDropChance", new NbtFloat(1f) },
                    { "ItemRotation", new NbtFloat(0f) },
                    { "isMovable", new NbtByte(1) },
                };
                s.SetBlock(0, y, gx, frame, be);
                if (backingBlock is not null) s.SetBlock(1, y, gx, backingBlock);
            }
        return s;
    }
}
