using System.Text;
using BMAP.BlockEngine;
using BMAP.Export;
using BMAP.Imaging;
using BMAP.Palette;

namespace BMAP.Shading;

/// <summary>Writes a calibration pad: the structure, pad.json for reading it back, the predicted map and instructions.</summary>
public static class PadExporter
{
    public static string Export(string folder, CalibrationPad pad, ColorProfile profile, WorldOrigin? origin, bool english = false)
    {
        string name = pad.Descriptor.Kind == PadKind.Shading ? "shading_pad" : "colour_pad";
        string dir = Path.Combine(folder, $"BMAP_{name}");
        Directory.CreateDirectory(dir);
        var d = pad.Descriptor with
        {
            OriginX = origin?.X, OriginY = origin?.Y, OriginZ = origin is { } o ? o.Z + 1 : null,
        };
        File.WriteAllText(Path.Combine(dir, "pad.json"), d.ToJson(), new UTF8Encoding(false));
        var map = MapSimulator.Simulate(pad.Layout, profile).Image;
        ImageCodec.SavePng(map, Path.Combine(dir, "predicted_map.png"));
        ImageCodec.SavePng(map.ScaleNearest(4), Path.Combine(dir, "predicted_map_x4.png"));
        StructureExporter.Export(dir, name, pad.Layout, 1,
            new StructureExportOptions { Split = StructureSplit.PerMap, English = english }, origin);
        File.WriteAllLines(Path.Combine(dir, english ? "HOW-TO.txt" : "手順.txt"), Steps(d, english).Concat([""]).Concat(pad.Legend),
            new UTF8Encoding(true));
        return dir;
    }

    private static IEnumerable<string> Steps(PadDescriptor d, bool english)
    {
        string pos = d.OriginX is int x ? $"X {x}, Y {d.OriginY}, Z {d.OriginZ - 1}" : "?";
        if (english)
        {
            yield return "1. Use a creative world (or a copy) with a flat, open area. Build in the air if the ground is uneven.";
            yield return $"2. Load the structure in the mcstructure folder so its north-west corner (support row) is at {pos}";
            yield return "   (placement text in this folder has the exact /structure load command). The map area must match exactly.";
            yield return "3. Stand on the pad with an empty map (scale 0: do not zoom it out) and walk over the whole pad until every pixel is drawn.";
            yield return "4. Leave the world and close Minecraft so the map is saved.";
            yield return "5. In BMAP: Map settings > Calibration > choose the world and this pad.json > Read maps and compare.";
            yield return "   BMAP only reads a temporary copy of the world's database; the world is never changed.";
        }
        else
        {
            yield return "1. 平らで開けたクリエイティブワールド（またはそのコピー）を使います。地面が平らでなければ空中に置きます。";
            yield return $"2. mcstructure フォルダの構造物を、北西の角（支え行）が {pos} になるように読み込みます";
            yield return "   （正確な /structure load コマンドはこのフォルダの配置方法の説明にあります）。地図の範囲とぴったり合わせてください。";
            yield return "3. 白紙の地図（縮尺 0。拡大しないでください）を持ってパッドの上を歩き、全体が描かれるまで待ちます。";
            yield return "4. ワールドを出て Minecraft を終了し、地図を保存させます。";
            yield return "5. BMAP のマップ設定 →「実機キャリブレーション」でワールドとこの pad.json を選び、「地図を読み取って比較」を押します。";
            yield return "   BMAP はワールドのデータベースの一時コピーを読むだけで、ワールドは変更しません。";
        }
    }
}
