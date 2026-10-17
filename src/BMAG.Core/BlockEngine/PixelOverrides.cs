using System.Globalization;
using BMAG.Palette;

namespace BMAG.BlockEngine;

/// <summary>One manual edit: the pixel gets this block (null = no block) with the given shade.</summary>
public readonly record struct PixelEdit(int X, int Y, string? BlockKey, Shade Shade = Shade.Normal);

/// <summary>
/// Manual edits painted on top of the automatic conversion (MapartCraft / Cartographer "edit mode").
/// Edits are kept separately so that changing settings re-runs the conversion and re-applies them.
/// </summary>
public sealed class PixelOverrides
{
    private readonly Dictionary<(int, int), PixelEdit> _edits = [];
    private readonly Stack<List<PixelEdit?[]>> _undo = new();
    private List<PixelEdit?[]>? _stroke;

    public int Count => _edits.Count;
    public bool CanUndo => _undo.Count > 0;
    public IEnumerable<PixelEdit> Edits => _edits.Values;

    public void BeginStroke() => _stroke = [];

    public void EndStroke()
    {
        if (_stroke is { Count: > 0 }) _undo.Push(_stroke);
        _stroke = null;
    }

    /// <summary>Sets (or with <paramref name="clear"/> removes) an edit. Returns true when something changed.</summary>
    public bool Set(PixelEdit edit, bool clear = false)
    {
        var key = (edit.X, edit.Y);
        _edits.TryGetValue(key, out var old);
        bool had = _edits.ContainsKey(key);
        if (clear)
        {
            if (!had) return false;
            _edits.Remove(key);
        }
        else
        {
            if (had && old == edit) return false;
            _edits[key] = edit;
        }
        // Remember the previous state for undo: [before, after].
        (_stroke ??= []).Add([had ? old : null, clear ? null : edit]);
        if (_undo.Count > 200) TrimUndo();
        return true;
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var stroke = _undo.Pop();
        for (int i = stroke.Count - 1; i >= 0; i--)
        {
            var (before, after) = (stroke[i][0], stroke[i][1]);
            var e = before ?? after!.Value;
            if (before is { } b) _edits[(b.X, b.Y)] = b;
            else _edits.Remove((e.X, e.Y));
        }
    }

    public void Clear()
    {
        _edits.Clear();
        _undo.Clear();
    }

    private void TrimUndo()
    {
        var keep = _undo.Take(150).Reverse().ToList();
        _undo.Clear();
        foreach (var s in keep) _undo.Push(s);
    }

    /// <summary>Applies the edits to a conversion result (in place). Blocks not in the palette are added as entries.</summary>
    public void ApplyTo(QuantizeResult q, ColorProfile profile)
    {
        foreach (var e in _edits.Values)
        {
            if (e.X < 0 || e.Y < 0 || e.X >= q.Width || e.Y >= q.Height) continue;
            int i = e.Y * q.Width + e.X;
            if (e.BlockKey is null)
            {
                q.EntryIndex[i] = -1;
                q.Preview.Pixels[i * 4 + 3] = 0;
                continue;
            }
            var block = profile.Find(e.BlockKey);
            if (block is null) continue;
            int entry = q.Entries.FindIndex(x => x.Block.Key == block.Key);
            if (entry < 0)
            {
                q.Entries.Add(new PaletteEntry(block, []));
                entry = q.Entries.Count - 1;
            }
            var shade = q.Mode == StaircaseMode.Flat ? Shade.Normal : e.Shade;
            q.EntryIndex[i] = entry;
            q.Shades[i] = shade;
            var c = profile.ColorOf(block, shade);
            q.Preview.Pixels[i * 4] = c.R;
            q.Preview.Pixels[i * 4 + 1] = c.G;
            q.Preview.Pixels[i * 4 + 2] = c.B;
            q.Preview.Pixels[i * 4 + 3] = 255;
        }
    }

    /// <summary>Compact text form for project files: "x,y,key,shade" per edit (key "-" = no block).</summary>
    public List<string> Serialize() =>
        _edits.Values.Select(e => string.Join(",", e.X, e.Y, e.BlockKey ?? "-", (int)e.Shade)).ToList();

    public static PixelOverrides Deserialize(IEnumerable<string>? lines)
    {
        var o = new PixelOverrides();
        foreach (var line in lines ?? [])
        {
            var p = line.Split(',');
            if (p.Length < 4 || !int.TryParse(p[0], CultureInfo.InvariantCulture, out int x) || !int.TryParse(p[1], CultureInfo.InvariantCulture, out int y)) continue;
            o._edits[(x, y)] = new PixelEdit(x, y, p[2] == "-" ? null : p[2], (Shade)Math.Clamp(int.Parse(p[3], CultureInfo.InvariantCulture), 0, 2));
        }
        return o;
    }
}
