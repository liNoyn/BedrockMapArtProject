using System.Buffers.Binary;
using BMAG.Bedrock.LevelDb;
using BMAG.Imaging;
using BMAG.Nbt;

namespace BMAG.Tests;

internal static class TestHelpers
{
    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmag-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static RgbaImage Gradient(int w, int h, bool alpha = false)
    {
        var img = new RgbaImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                img[x, y] = new Rgba((byte)(x * 255 / Math.Max(1, w - 1)), (byte)(y * 255 / Math.Max(1, h - 1)),
                    (byte)((x + y) % 256), alpha ? (byte)(x < w / 2 ? 0 : 255) : (byte)255);
        return img;
    }

    /// <summary>Creates an empty LevelDB (CURRENT + MANIFEST + empty log) the way leveldb::DB::Open would.</summary>
    public static string CreateEmptyLevelDb(string dir, ulong lastSequence = 0)
    {
        Directory.CreateDirectory(dir);
        using var edit = new MemoryStream();
        void Var(ulong v) { while (v >= 0x80) { edit.WriteByte((byte)(v | 0x80)); v >>= 7; } edit.WriteByte((byte)v); }
        var cmp = "leveldb.BytewiseComparator"u8.ToArray();
        Var(1); Var((ulong)cmp.Length); edit.Write(cmp);
        Var(2); Var(3);              // log number 3
        Var(3); Var(4);              // next file number
        Var(4); Var(lastSequence);   // last sequence
        using (var fs = new FileStream(Path.Combine(dir, "MANIFEST-000002"), FileMode.Create))
            LogFile.AppendRecord(fs, edit.ToArray(), 0);
        File.WriteAllText(Path.Combine(dir, "CURRENT"), "MANIFEST-000002\n");
        File.WriteAllBytes(Path.Combine(dir, "000003.log"), []);
        return dir;
    }

    public static void CreateWorld(string worldDir, int[] version, string name = "Test World")
    {
        Directory.CreateDirectory(worldDir);
        CreateEmptyLevelDb(Path.Combine(worldDir, "db"));
        var root = new NbtCompound
        {
            { "LevelName", new NbtString(name) },
            { "StorageVersion", new NbtInt(10) },
            { "LastPlayed", new NbtLong(1_700_000_000) },
            { "lastOpenedWithVersion", new NbtList(NbtType.Int, version.Select(v => (NbtTag)new NbtInt(v))) },
            { "GameType", new NbtInt(1) },
        };
        var nbt = NbtIO.Write(root);
        var dat = new byte[8 + nbt.Length];
        BinaryPrimitives.WriteInt32LittleEndian(dat, 10);
        BinaryPrimitives.WriteInt32LittleEndian(dat.AsSpan(4), nbt.Length);
        nbt.CopyTo(dat, 8);
        File.WriteAllBytes(Path.Combine(worldDir, "level.dat"), dat);
        File.WriteAllText(Path.Combine(worldDir, "levelname.txt"), name);
    }
}
