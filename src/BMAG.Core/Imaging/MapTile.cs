namespace BMAG.Imaging;

public enum TileStatus { Pending, Processing, Done, Error, Cancelled }

/// <summary>One 128x128 map of the grid (design 5.5 / 13.2). Ordered from top-left to bottom-right.</summary>
public sealed class MapTile
{
    public const int Size = 128;

    public required int Index { get; init; }
    public required int GridX { get; init; }
    public required int GridY { get; init; }
    public required RgbaImage Image { get; init; }

    /// <summary>Pixel rectangle inside the processed full image.</summary>
    public int PixelX => GridX * Size;
    public int PixelY => GridY * Size;

    public long? AssignedMapId { get; set; }
    public TileStatus Status { get; set; } = TileStatus.Pending;
    public string ImageHash => Image.ComputeHash();

    public string Label => $"#{Index + 1} (列{GridX + 1}, 行{GridY + 1})";
}

public static class TileSplitter
{
    public static List<MapTile> Split(RgbaImage full, int mapsX, int mapsY)
    {
        if (full.Width != mapsX * MapTile.Size || full.Height != mapsY * MapTile.Size)
            throw new ArgumentException($"Image is {full.Width}x{full.Height}, expected {mapsX * 128}x{mapsY * 128}.");
        var tiles = new List<MapTile>(mapsX * mapsY);
        for (int gy = 0; gy < mapsY; gy++)
            for (int gx = 0; gx < mapsX; gx++)
                tiles.Add(new MapTile
                {
                    Index = gy * mapsX + gx,
                    GridX = gx,
                    GridY = gy,
                    Image = full.Crop(gx * MapTile.Size, gy * MapTile.Size, MapTile.Size, MapTile.Size),
                });
        return tiles;
    }
}
