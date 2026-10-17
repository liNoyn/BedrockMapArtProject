using System.Security.Cryptography;
using System.Text;
using BMAG.Bedrock.LevelDb;
using BMAG.Bedrock.World;
using BMAG.Calibration;
using BMAG.Imaging;
using BMAG.MapData;
using BMAG.Nbt;
using BMAG.Palette;

namespace BMAG.Tests;

public class BedrockTests
{
    [Fact] // T19
    public void Nbt_IsLittleEndian_AndRoundTrips()
    {
        var c = new NbtCompound
        {
            { "i", new NbtInt(0x01020304) },
            { "l", new NbtLong(-5) },
            { "s", new NbtString("日本語") },
            { "list", new NbtList(NbtType.Compound) },
            { "f", new NbtFloat(1.5f) },
            { "ia", new NbtIntArray([1, 2, 3]) },
        };
        var bytes = NbtIO.Write(c);
        // tag(1) + name len(2) + "" + [tag(1) + len(2) + "i" + payload]
        Assert.Equal(new byte[] { 3, 1, 0, (byte)'i', 4, 3, 2, 1 }, bytes[3..11]);
        Assert.Equal(bytes, NbtIO.Write(NbtIO.Read(bytes)));
    }

    [Fact] // T18, T21
    public void MapNbt_HasCorrectSizeAndKey()
    {
        var tile = TestHelpers.Gradient(128, 128);
        var nbt = MapDataEncoder.CreateMapNbt(42, tile, VersionProfile.Bedrock_1_16_210);
        var rec = MapRecord.Parse(42, MapDataEncoder.Serialize(nbt));
        Assert.Equal(65536, rec.Colors!.Length);
        Assert.Equal("map_42", rec.Key);
        Assert.True(rec.Locked);
        Assert.Equal(tile.Pixels, rec.ToImage()!.Pixels);
        Assert.Equal("map_-7", MapDataEncoder.KeyFor(-7));
        Assert.True(MapDataEncoder.TryParseKey("map_-42949657805", out long id) && id == -42949657805);
    }

    [Fact] // T20
    public void IdAllocator_SkipsUsedIds_AndKeepsRunsConsecutive()
    {
        var alloc = new MapIdAllocator([100, 101, 105]);
        var ids = alloc.Allocate(3, 100);
        Assert.Equal(new long[] { 102, 103, 104 }, ids);
        var more = alloc.Allocate(2, 100);
        Assert.Equal(new long[] { 106, 107 }, more);
    }

    [Fact] // T22
    public void LevelDb_WriteThenRead_RoundTrips_AcrossReopen()
    {
        var dir = TestHelpers.CreateEmptyLevelDb(Path.Combine(TestHelpers.TempDir(), "db"), lastSequence: 1000);
        var big = new byte[100_000];
        new Random(1).NextBytes(big); // spans several 32 KiB log blocks
        using (var db = LevelDbDatabase.Open(dir, readOnly: false))
        {
            var b = new WriteBatch();
            b.Put("map_1", [1, 2, 3]);
            b.Put("map_2", big);
            b.Put("other", [9]);
            db.Write(b);
            var b2 = new WriteBatch();
            b2.Put("map_1", [4]);
            b2.Delete("other");
            db.Write(b2);
            Assert.Equal(1005UL, db.LastSequence);
        }
        using (var db = LevelDbDatabase.Open(dir))
        {
            Assert.Null(db.LogCorruption);
            Assert.Equal(new byte[] { 4 }, db.Get("map_1"));
            Assert.Equal(big, db.Get("map_2"));
            Assert.Null(db.Get("other"));
            Assert.Equal(2, db.Scan("map_").Count);
            Assert.Equal(1005UL, db.LastSequence);
        }
    }

    [Fact]
    public void LevelDb_RefusesWriteWhileLocked()
    {
        var dir = TestHelpers.CreateEmptyLevelDb(Path.Combine(TestHelpers.TempDir(), "db"));
        using var holder = new FileStream(Path.Combine(dir, "LOCK"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => LevelDbDatabase.Open(dir, readOnly: false));
    }

    [Fact]
    public void LogAppend_AfterPreallocatedZeros_IsStillReadable()
    {
        var dir = TestHelpers.CreateEmptyLevelDb(Path.Combine(TestHelpers.TempDir(), "db"));
        using (var db = LevelDbDatabase.Open(dir, readOnly: false))
        {
            var b = new WriteBatch(); b.Put("a", [1]); db.Write(b);
        }
        using (var fs = new FileStream(Path.Combine(dir, "000003.log"), FileMode.Append)) fs.Write(new byte[5000]);
        using (var db = LevelDbDatabase.Open(dir, readOnly: false))
        {
            var b = new WriteBatch(); b.Put("b", [2]); db.Write(b);
        }
        using var check = LevelDbDatabase.Open(dir);
        Assert.Null(check.LogCorruption);
        Assert.Equal(new byte[] { 1 }, check.Get("a"));
        Assert.Equal(new byte[] { 2 }, check.Get("b"));
    }

    [Fact]
    public void Crc32C_KnownVector()
    {
        Assert.Equal(0xE3069283u, Crc32C.Compute("123456789"u8));
    }

    [Fact] // T23, T24, T25 (fake world built from scratch)
    public void SafeExport_WritesOnlyToCopy()
    {
        var root = TestHelpers.TempDir();
        var world = Path.Combine(root, "abc=");
        TestHelpers.CreateWorld(world, [1, 26, 51, 1, 0]);
        var before = HashDir(world);

        var img = ImagePipeline.Process(TestHelpers.Gradient(300, 200), new ImageSettings { MapsX = 2, MapsY = 1 });
        var tiles = TileSplitter.Split(img, 2, 1);
        var plan = SafeWorldExporter.Plan(WorldInfo.Load(world), tiles, 2, 1, new WorldExportOptions { ProjectName = "test", StartId = 500 });
        Assert.Equal(new long[] { 500, 501 }, plan.MapIds);
        var result = SafeWorldExporter.Execute(plan);

        Assert.Equal(before, HashDir(world)); // original untouched
        using var db = LevelDbDatabase.Open(Path.Combine(result.Destination, "db"));
        var map = MapRecord.Parse(501, db.Get("map_501")!);
        Assert.Equal(tiles[1].Image.Pixels, map.ToImage()!.Pixels);
        var wall = BMAG.Export.McStructure.FromBytes(db.Get("structuretemplate_bmag:test_wall")!);
        Assert.Equal((2, 1, 2), (wall.SizeX, wall.SizeY, wall.SizeZ));
        // Left column (z = 0) holds the first map.
        var item = wall.BlockEntities[wall.IndexOf(0, 0, 0)].Get<NbtCompound>("Item")!;
        Assert.Equal(500, item.Get<NbtCompound>("tag")!.GetLong("map_uuid"));
        var chest = BMAG.Export.McStructure.FromBytes(db.Get("structuretemplate_bmag:test_maps")!);
        Assert.Equal(2, chest.BlockEntities.Values.Single().Get<NbtList>("Items")!.Count);
        Assert.Equal("Test World (BMAG test)", LevelDat.Read(Path.Combine(result.Destination, "level.dat")).LevelName);
        Assert.False(Directory.Exists(plan.Destination + "_incomplete"));
    }

    [Fact] // T30
    public void UnknownVersion_DisablesWriting()
    {
        var root = TestHelpers.TempDir();
        var world = Path.Combine(root, "old=");
        TestHelpers.CreateWorld(world, [1, 12, 0]);
        var tiles = TileSplitter.Split(new RgbaImage(128, 128), 1, 1);
        Assert.Throws<NotSupportedException>(() => SafeWorldExporter.Plan(WorldInfo.Load(world), tiles, 1, 1, new WorldExportOptions()));
    }

    [Fact]
    public void Calibration_MeasuresSwatchesFromAMap()
    {
        var profile = ColorProfile.LoadBuiltIn();
        var blocks = CalibrationPad.CalibratableBlocks(profile);
        // Simulate the game drawing the pad using the profile's own colours, with one block measured differently.
        var img = new RgbaImage(128, 128);
        for (int slot = 0; slot < blocks.Count; slot++)
            for (int r = 0; r < 8; r++)
                for (int c = 0; c < 8; c++)
                {
                    var shade = r == 3 ? Shade.Bright : r == 5 ? Shade.Dark : Shade.Normal;
                    var col = profile.ColorOf(blocks[slot], shade);
                    if (slot == 0 && shade == Shade.Normal) col = new Rgba(1, 2, 3);
                    img[slot % 16 * 8 + c, slot / 16 * 8 + r] = col;
                }
        var nbt = MapDataEncoder.CreateMapNbt(1, img, VersionProfile.Bedrock_1_16_210);
        var samples = Calibrator.Measure(MapRecord.Parse(1, NbtIO.Write(nbt)), profile);
        Assert.Equal(blocks.Count * 3, samples.Count);
        var measured = Calibrator.ToMeasurements(samples);
        var calibrated = profile.WithMeasurements(measured, "test-cal", "test");
        Assert.Equal(new Rgba(1, 2, 3), calibrated.ColorOf(calibrated.Find(blocks[0].Key)!, Shade.Normal));
        Assert.True(calibrated.Find(blocks[0].Key)!.Verified);
        var reloaded = ColorProfile.LoadJson(calibrated.ToJson());
        Assert.Equal(new Rgba(1, 2, 3), reloaded.ColorOf(reloaded.Find(blocks[0].Key)!, Shade.Normal));
    }

    /// <summary>Optional: set BMAG_TEST_WORLD to a COPY of a real Bedrock world to test reading and writing it.</summary>
    [Fact]
    public void RealWorldCopy_ReadWriteVerify()
    {
        var src = Environment.GetEnvironmentVariable("BMAG_TEST_WORLD");
        if (string.IsNullOrEmpty(src) || !Directory.Exists(src)) return;
        var info = WorldInfo.Load(src);
        var tiles = TileSplitter.Split(ImagePipeline.Process(TestHelpers.Gradient(128, 128), new ImageSettings()), 1, 1);
        var plan = SafeWorldExporter.Plan(info, tiles, 1, 1, new WorldExportOptions { ProjectName = "bmag_test" });
        var result = SafeWorldExporter.Execute(plan);
        using var db = LevelDbDatabase.Open(Path.Combine(result.Destination, "db"));
        Assert.NotNull(db.Get(MapDataEncoder.KeyFor(plan.MapIds[0])));
        Assert.True(db.Scan(Array.Empty<byte>()).Count > 10); // old data still readable
    }

    private static string HashDir(string dir)
    {
        var sb = new StringBuilder();
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order())
            sb.Append(Path.GetRelativePath(dir, f)).Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
        return sb.ToString();
    }
}
