using BMAP.Nbt;

namespace BMAP.Export;

/// <summary>A block state in a structure palette.</summary>
public sealed record PaletteBlock(string Name, NbtCompound States, int Version)
{
    public string Signature => Name + "|" + Version + "|" + string.Join(",", States.Select(kv => kv.Key + "=" + kv.Value));
}

/// <summary>
/// In-memory Bedrock .mcstructure (design 8.1). Layer 0 only; layer 1 (waterlogging) is written as all -1.
/// Index order: ((x * SizeY) + y) * SizeZ + z. -1 = structure void (leaves the world untouched).
/// </summary>
public sealed class McStructure
{
    public McStructure(int sizeX, int sizeY, int sizeZ)
    {
        if (sizeX <= 0 || sizeY <= 0 || sizeZ <= 0) throw new ArgumentOutOfRangeException(nameof(sizeX));
        SizeX = sizeX; SizeY = sizeY; SizeZ = sizeZ;
        Indices = new int[sizeX * sizeY * sizeZ];
        Array.Fill(Indices, -1);
    }

    public int SizeX { get; }
    public int SizeY { get; }
    public int SizeZ { get; }
    public int[] Indices { get; }
    public List<PaletteBlock> Palette { get; } = [];
    public Dictionary<int, NbtCompound> BlockEntities { get; } = [];
    public List<NbtCompound> Entities { get; } = [];
    public (int X, int Y, int Z) WorldOrigin { get; set; }

    private readonly Dictionary<string, int> _paletteLookup = [];

    public int IndexOf(int x, int y, int z) => (x * SizeY + y) * SizeZ + z;

    public int AddPalette(PaletteBlock block)
    {
        if (_paletteLookup.TryGetValue(block.Signature, out int i)) return i;
        Palette.Add(block);
        _paletteLookup[block.Signature] = Palette.Count - 1;
        return Palette.Count - 1;
    }

    public void SetBlock(int x, int y, int z, PaletteBlock block, NbtCompound? blockEntity = null)
    {
        int idx = IndexOf(x, y, z);
        Indices[idx] = AddPalette(block);
        if (blockEntity != null)
        {
            blockEntity["x"] = new NbtInt(WorldOrigin.X + x);
            blockEntity["y"] = new NbtInt(WorldOrigin.Y + y);
            blockEntity["z"] = new NbtInt(WorldOrigin.Z + z);
            BlockEntities[idx] = blockEntity;
        }
    }

    public int CountBlocks() => Indices.Count(i => i >= 0);

    public NbtCompound ToNbt()
    {
        var primary = new NbtList(NbtType.Int, Indices.Select(i => (NbtTag)new NbtInt(i)));
        var secondary = new NbtList(NbtType.Int, Enumerable.Repeat(-1, Indices.Length).Select(i => (NbtTag)new NbtInt(i)));
        var palette = new NbtList(NbtType.Compound, Palette.Select(p => (NbtTag)new NbtCompound
        {
            { "name", new NbtString(p.Name) },
            { "states", p.States },
            { "version", new NbtInt(p.Version) },
        }));
        var positionData = new NbtCompound();
        foreach (var (idx, be) in BlockEntities.OrderBy(kv => kv.Key))
            positionData.Add(idx.ToString(), new NbtCompound { { "block_entity_data", be } });

        return new NbtCompound
        {
            { "format_version", new NbtInt(1) },
            { "size", IntList(SizeX, SizeY, SizeZ) },
            { "structure", new NbtCompound
                {
                    { "block_indices", new NbtList(NbtType.List, [primary, secondary]) },
                    { "entities", new NbtList(NbtType.Compound, Entities) },
                    { "palette", new NbtCompound
                        {
                            { "default", new NbtCompound
                                {
                                    { "block_palette", palette },
                                    { "block_position_data", positionData },
                                }
                            },
                        }
                    },
                }
            },
            { "structure_world_origin", IntList(WorldOrigin.X, WorldOrigin.Y, WorldOrigin.Z) },
        };
    }

    public byte[] ToBytes() => NbtIO.Write(ToNbt());

    private static NbtList IntList(params int[] values) => new(NbtType.Int, values.Select(v => (NbtTag)new NbtInt(v)));

    public static McStructure FromBytes(byte[] data) => FromNbt(NbtIO.Read(data));

    public static McStructure FromNbt(NbtCompound root)
    {
        var size = root.Get<NbtList>("size") ?? throw new InvalidDataException("size がありません。");
        if (size.Count != 3) throw new InvalidDataException("size は 3 要素である必要があります。");
        var s = new McStructure(((NbtInt)size[0]).Value, ((NbtInt)size[1]).Value, ((NbtInt)size[2]).Value);
        var structure = root.Get<NbtCompound>("structure") ?? throw new InvalidDataException("structure がありません。");
        var layers = structure.Get<NbtList>("block_indices") ?? throw new InvalidDataException("block_indices がありません。");
        if (layers.Count < 1) throw new InvalidDataException("block_indices が空です。");
        var primary = (NbtList)layers[0];
        if (primary.Count != s.Indices.Length)
            throw new InvalidDataException($"block_indices の数 {primary.Count} が size の積 {s.Indices.Length} と一致しません。");
        for (int i = 0; i < primary.Count; i++) s.Indices[i] = ((NbtInt)primary[i]).Value;

        var def = structure.Get<NbtCompound>("palette")?.Get<NbtCompound>("default");
        if (def?.Get<NbtList>("block_palette") is { } bp)
            foreach (var t in bp.Items.Cast<NbtCompound>())
                s.Palette.Add(new PaletteBlock(t.GetString("name") ?? "", t.Get<NbtCompound>("states") ?? new NbtCompound(), t.GetInt("version") ?? 0));
        if (def?.Get<NbtCompound>("block_position_data") is { } pd)
            foreach (var (k, v) in pd)
                if (int.TryParse(k, out int idx) && v is NbtCompound c && c.Get<NbtCompound>("block_entity_data") is { } be)
                    s.BlockEntities[idx] = be;
        if (structure.Get<NbtList>("entities") is { } ents)
            s.Entities.AddRange(ents.Items.OfType<NbtCompound>());
        return s;
    }

    /// <summary>Pre-export checks (design 8.5). Returns a list of problems; empty = OK.</summary>
    public List<string> Validate()
    {
        var problems = new List<string>();
        if (Indices.Length != SizeX * SizeY * SizeZ) problems.Add("ブロック数と size が一致しません。");
        foreach (int i in Indices)
            if (i < -1 || i >= Palette.Count) { problems.Add($"無効なパレット番号 {i}"); break; }
        foreach (var idx in BlockEntities.Keys)
            if (idx < 0 || idx >= Indices.Length || Indices[idx] < 0) problems.Add($"ブロックエンティティの位置 {idx} にブロックがありません。");
        foreach (var p in Palette)
            if (string.IsNullOrWhiteSpace(p.Name) || !p.Name.Contains(':')) problems.Add($"不正なブロック識別子 '{p.Name}'");
        return problems;
    }
}
