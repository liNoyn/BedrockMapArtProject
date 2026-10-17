using System.Buffers.Binary;
using BMAG.Nbt;

namespace BMAG.Bedrock.World;

/// <summary>level.dat = storage version (int32 LE) + payload length (int32 LE) + little-endian NBT.</summary>
public sealed class LevelDat
{
    public required int StorageHeader { get; init; }
    public required NbtCompound Root { get; init; }
    public string RootName { get; init; } = "";

    public string? LevelName => Root.GetString("LevelName");
    public int? StorageVersion => Root.GetInt("StorageVersion");
    public long? LastPlayed => Root.GetLong("LastPlayed");
    public int? GameType => Root.GetInt("GameType");

    /// <summary>lastOpenedWithVersion as "1.21.50.7".</summary>
    public string? LastOpenedWithVersion =>
        Root.Get<NbtList>("lastOpenedWithVersion") is { } l
            ? string.Join(".", l.Items.OfType<NbtInt>().Select(i => i.Value))
            : null;

    public int[] LastOpenedVersionParts =>
        Root.Get<NbtList>("lastOpenedWithVersion")?.Items.OfType<NbtInt>().Select(i => i.Value).ToArray() ?? [];

    public static LevelDat Read(string path) => Parse(File.ReadAllBytes(path));

    public static LevelDat Parse(byte[] data)
    {
        if (data.Length < 8) throw new InvalidDataException("level.dat が短すぎます。");
        int header = BinaryPrimitives.ReadInt32LittleEndian(data);
        int length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        if (length < 0 || 8 + length > data.Length) throw new InvalidDataException("level.dat のヘッダーが不正です。");
        var root = NbtIO.Read(data.AsSpan(8, length), out string name, out _);
        return new LevelDat { StorageHeader = header, Root = root, RootName = name };
    }

    public byte[] Serialize()
    {
        var nbt = NbtIO.Write(Root, RootName);
        var result = new byte[8 + nbt.Length];
        BinaryPrimitives.WriteInt32LittleEndian(result, StorageHeader);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), nbt.Length);
        nbt.CopyTo(result, 8);
        return result;
    }

    /// <summary>True if parsing and re-serialising reproduces the file byte for byte (safety check before editing).</summary>
    public static bool RoundTrips(byte[] original)
    {
        try { return Parse(original).Serialize().AsSpan().SequenceEqual(original); }
        catch { return false; }
    }
}
