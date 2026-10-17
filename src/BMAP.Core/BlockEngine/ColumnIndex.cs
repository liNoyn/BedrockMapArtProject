namespace BMAP.BlockEngine;

/// <summary>
/// Which heights are occupied in every (x, z) column of a layout, stored compactly (a few entries per column instead of
/// a dense 3D grid, which for 8x8 maps would be hundreds of megabytes).
/// </summary>
public sealed class ColumnIndex
{
    private readonly int _w, _d;
    private readonly int[] _start;   // per column: first index into _ys (columns in z-major order), length w*d+1
    private readonly int[] _ys;      // occupied heights, ascending per column
    private readonly int[] _top;     // highest occupied height per column, int.MinValue when empty

    public int Width => _w;
    public int Depth => _d;

    public ColumnIndex(int width, int depth, IEnumerable<BlockPlacement> blocks)
    {
        _w = width;
        _d = depth;
        var list = blocks as IReadOnlyList<BlockPlacement> ?? blocks.ToList();
        var count = new int[width * depth + 1];
        foreach (var b in list) if (InBounds(b.X, b.Z)) count[b.Z * width + b.X + 1]++;
        for (int i = 1; i < count.Length; i++) count[i] += count[i - 1];
        _start = count;
        _ys = new int[count[^1]];
        var fill = (int[])count.Clone();
        foreach (var b in list) if (InBounds(b.X, b.Z)) _ys[fill[b.Z * width + b.X]++] = b.Y;
        _top = new int[width * depth];
        for (int c = 0; c < width * depth; c++)
        {
            Array.Sort(_ys, _start[c], _start[c + 1] - _start[c]);
            _top[c] = _start[c + 1] > _start[c] ? _ys[_start[c + 1] - 1] : int.MinValue;
        }
    }

    public bool InBounds(int x, int z) => x >= 0 && z >= 0 && x < _w && z < _d;

    /// <summary>Highest occupied height of the column, or <see cref="int.MinValue"/> when it is empty or outside.</summary>
    public int Top(int x, int z) => InBounds(x, z) ? _top[z * _w + x] : int.MinValue;

    public bool Occupied(int x, int y, int z)
    {
        if (!InBounds(x, z)) return false;
        int c = z * _w + x;
        if (y > _top[c]) return false;
        for (int i = _start[c + 1] - 1; i >= _start[c]; i--)
        {
            if (_ys[i] == y) return true;
            if (_ys[i] < y) return false;
        }
        return false;
    }
}
