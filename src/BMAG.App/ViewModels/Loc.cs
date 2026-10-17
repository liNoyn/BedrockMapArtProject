using System.ComponentModel;

namespace BMAG.App.ViewModels;

/// <summary>UI strings in Japanese and English. XAML binds to <c>L[Key]</c>; switching language refreshes every binding.</summary>
public sealed class Loc : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _english;
    public bool English
    {
        get => _english;
        set
        {
            if (_english == value) return;
            _english = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(English)));
        }
    }

    public string this[string key] => Strings.TryGetValue(key, out var s) ? (English ? s.En : s.Ja) : key;

    public string Format(string key, params object[] args) => string.Format(this[key], args);

    private static readonly Dictionary<string, (string Ja, string En)> Strings = new()
    {
        // menu
        ["File"] = ("ファイル(_F)", "_File"),
        ["NewProject"] = ("新規プロジェクト", "New project"),
        ["OpenProject"] = ("プロジェクトを開く…", "Open project…"),
        ["SaveProject"] = ("プロジェクトを保存", "Save project"),
        ["SaveProjectAs"] = ("名前を付けて保存…", "Save project as…"),
        ["OpenImage"] = ("画像・動画を開く…", "Open image or video…"),
        ["View"] = ("表示(_V)", "_View"),
        ["Language"] = ("言語 / Language", "Language / 言語"),
        ["Japanese"] = ("日本語", "日本語 (Japanese)"),
        ["EnglishLang"] = ("English", "English"),

        // left panel
        ["Project"] = ("プロジェクト", "Project"),
        ["Image"] = ("画像", "Image"),
        ["DropHint"] = ("ウィンドウに画像をドラッグ＆ドロップしても開けます。", "You can also drop an image onto the window."),
        ["Frame"] = ("フレーム / ページ", "Frame / page"),
        ["VideoTime"] = ("動画の位置（秒）", "Video position (s)"),
        ["ReloadFrame"] = ("このフレームを読み込む", "Load this frame"),
        ["MapCount"] = ("地図の枚数", "Number of maps"),
        ["Crop"] = ("切り抜き", "Crop"),
        ["CropOff"] = ("なし（画像全体）", "Off (whole image)"),
        ["CropCenter"] = ("中央を出力の縦横比で切り抜く", "Centre, output aspect ratio"),
        ["CropManual"] = ("手動（X, Y, 幅, 高さ）", "Manual (X, Y, width, height)"),
        ["Placement"] = ("配置", "Placement"),
        ["Zoom"] = ("拡大", "Zoom"),
        ["OffsetX"] = ("横位置", "Horizontal offset"),
        ["OffsetY"] = ("縦位置", "Vertical offset"),
        ["Adjust"] = ("色の補正", "Adjustments"),
        ["Brightness"] = ("明るさ", "Brightness"),
        ["Contrast"] = ("コントラスト", "Contrast"),
        ["Saturation"] = ("彩度", "Saturation"),
        ["Gamma"] = ("ガンマ", "Gamma"),
        ["Sharpen"] = ("シャープ", "Sharpen"),
        ["Background"] = ("背景色・透明", "Background & transparency"),
        ["TransSmooth"] = ("背景色となめらかに合成", "Blend with background (smooth)"),
        ["TransDithered"] = ("背景色とディザ合成", "Dither with background"),
        ["TransKeep"] = ("透明を保持（ブロックなし）", "Keep transparency (no block)"),
        ["FitFit"] = ("全体を収める (Fit)", "Fit (letterbox)"),
        ["FitFill"] = ("隙間なく埋める (Fill)", "Fill (crop)"),
        ["FitStretch"] = ("引き伸ばす (Stretch)", "Stretch"),
        ["InterpLanczos"] = ("Lanczos（高品質）", "Lanczos (high quality)"),
        ["InterpBilinear"] = ("バイリニア", "Bilinear"),
        ["InterpNearest"] = ("最近傍（ドット絵向け）", "Nearest (pixel art)"),

        // preview
        ["PvOriginal"] = ("元画像", "Original"),
        ["PvDirect"] = ("地図直接（RGBA）", "Direct map (RGBA)"),
        ["PvBlockMap"] = ("ブロック地図色（予測）", "Block map colours (predicted)"),
        ["PvDifference"] = ("差分", "Difference"),
        ["PvBuild2D"] = ("建築ビュー 2D（テクスチャ）", "Build view 2D (textures)"),
        ["PvBuild3D"] = ("建築ビュー 3D", "Build view 3D"),
        ["ShowGrid"] = ("地図・構造物の境界", "Map / structure borders"),
        ["DropHere"] = ("画像を開くか、ここにドロップしてください", "Open an image or drop it here"),
        ["ViewMap"] = ("表示範囲", "Area"),
        ["AllMaps"] = ("全体", "Everything"),
        ["MapN"] = ("マップ #{0}", "Map #{0}"),
        ["CellSize"] = ("拡大", "Zoom"),
        ["Rotate"] = ("回転", "Rotate"),
        ["ShowSupports"] = ("支えブロックを表示", "Show support blocks"),
        ["ShowHeights"] = ("高さを濃淡で表示", "Shade by height"),
        ["EditProgress"] = ("クリックでチャンクを完了にする", "Click to mark chunks done"),
        ["InfoOriginal"] = ("元画像 {0} x {1}", "Original {0} x {1}"),
        ["InfoDirect"] = ("地図データへそのまま書き込む RGBA 画像", "RGBA image written directly into map data"),
        ["InfoBlockMap"] = ("ブロックの地図色による予測（実際のブロックの見た目とは異なります）", "Prediction from block map colours (not the block textures)"),
        ["InfoDifference"] = ("元画像との差（明るいほど差が大きい）", "Difference to the source (brighter = larger)"),
        ["InfoBuild"] = ("マウスを重ねるとブロックと座標を表示します", "Hover to see the block and its coordinates"),

        // palette tab
        ["TabPalette"] = ("ブロック選択", "Blocks"),
        ["Preset"] = ("プリセット", "Preset"),
        ["Apply"] = ("適用", "Apply"),
        ["SavePreset"] = ("現在の選択を保存", "Save current selection"),
        ["DeletePreset"] = ("削除", "Delete"),
        ["PresetName"] = ("プリセット名", "Preset name"),
        ["Textures"] = ("ブロックのアイコン", "Block icons"),
        ["DownloadTextures"] = ("Minecraft のテクスチャを取得…", "Get Minecraft textures…"),
        ["UseResourcePack"] = ("リソースパックを選択…", "Use a resource pack…"),
        ["NoTextures"] = ("テクスチャ未取得のため色で表示しています。", "No textures yet: showing colours."),
        ["TexturesReady"] = ("Minecraft のテクスチャを表示しています。", "Showing Minecraft textures."),
        ["NoneBlock"] = ("使わない", "Don't use"),
        ["PaletteHint"] = ("各色につき 1 ブロックを選びます。左の 3 色は暗・通常・明の地図色です。", "Pick one block per colour. The three bars are the dark / normal / light map colours."),
        ["CustomBlocks"] = ("カスタムブロック", "Custom blocks"),
        ["CustomId"] = ("ブロック ID（例: myaddon:tile）", "Block ID (e.g. myaddon:tile)"),
        ["CustomStates"] = ("状態 JSON（省略可）", "States JSON (optional)"),
        ["CustomSet"] = ("色セットに追加", "Add to colour set"),
        ["CustomColor"] = ("または地図色 #RRGGBB", "or map colour #RRGGBB"),
        ["CustomSupport"] = ("下に支えが必要", "Needs a block below"),
        ["AddCustom"] = ("追加", "Add"),
        ["ImportBp"] = ("ビヘイビアパックから読み込む…", "Import from behavior pack…"),
        ["ImportBpFile"] = (".mcpack / .mcaddon から読み込む…", "Import from .mcpack / .mcaddon…"),
        ["Remove"] = ("削除", "Remove"),

        // settings tab
        ["TabBuild"] = ("建築設定", "Build"),
        ["ColorProfile"] = ("色プロファイル", "Colour profile"),
        ["Metric"] = ("色差の計算", "Colour distance"),
        ["Dither"] = ("ディザリング", "Dithering"),
        ["Staircase"] = ("3D（階段）", "3D (staircasing)"),
        ["SupportMode"] = ("支えブロック", "Support blocks"),
        ["SupportBlock"] = ("支えブロックの種類", "Support block type"),
        ["MaxTypes"] = ("ブロック種類の上限", "Max. block types"),
        ["MaxTypesHint"] = ("0 = 上限なし", "0 = unlimited"),
        ["StairWarn"] = ("階段系の方式は Java 版の陰影規則を仮定した実験機能です。「地図・校正」タブで実測してから使ってください。", "Staircase modes assume Java's shading rules and are experimental. Calibrate in the Maps tab first."),
        ["StFlat"] = ("なし（平面）", "Off (flat)"),
        ["StClassic"] = ("Classic 階段", "Classic staircase"),
        ["StValley"] = ("Valley 階段（低く抑える）", "Valley staircase (lower)"),
        ["StDark"] = ("全面 暗", "Full dark"),
        ["StBright"] = ("全面 明", "Full light"),
        ["SuNone"] = ("なし", "None"),
        ["SuImportant"] = ("必要なブロックの下だけ", "Under blocks that need it"),
        ["SuAll"] = ("すべて（サバイバル向け・最適化）", "All (survival, optimized)"),
        ["SuDouble"] = ("すべて（二重・最適化）", "All (double, optimized)"),
        ["WorldPos"] = ("建築位置（ワールド座標）", "Build position (world coordinates)"),
        ["UseOrigin"] = ("地図の範囲に合わせた座標を出力する", "Align with the map grid"),
        ["MapX"] = ("地図X", "Map X"),
        ["MapZ"] = ("地図Z", "Map Z"),
        ["Result"] = ("結果", "Result"),
        ["ExportBuild"] = (".mcstructure と建築ガイドを出力…", "Export .mcstructure files and build guide…"),
        ["Output"] = ("出力", "Output"),
        ["SplitTiles"] = ("64×64 に分割（構造ブロックで読み込み可）", "64×64 pieces (structure block import)"),
        ["SplitPerMap"] = ("マップ 1 枚ごと（128×128）", "One per map (128×128)"),
        ["SplitWhole"] = ("全体を 1 ファイル", "Everything in one file"),
        ["SplitHint"] = ("構造ブロックで読み込めるのは 64×64 までです。大きいファイルは /structure load やエディタ（Amulet など）向けです。", "Structure blocks load at most 64×64. Larger files are for /structure load or editors such as Amulet."),
        ["WriteBP"] = ("ビヘイビアパック（.mcpack）も出力する", "Also write a behavior pack (.mcpack)"),
        ["RegisterInWorld"] = ("ワールドのコピーに構造物を登録…", "Register structures in a world copy…"),
        ["RegisterHint"] = ("「地図直接生成」タブで選んだワールドのコピーを作り、全ピースを mystructure:ファイル名 として保存します（インポート不要）。", "Copies the world selected in the Direct maps tab and saves every piece as mystructure:<file name> (no import needed)."),
        ["RegisterConfirm"] = ("{0} 個の構造物を「{1}」のコピーに登録します。元のワールドは変更しません。実行しますか？", "Register {0} structures in a copy of \"{1}\"? The original world is not changed."),
        ["RegisterDone"] = ("構造物を登録したワールドコピーを作成しました:", "Created a world copy with the structures:"),
        ["ExportedFiles"] = ("  .mcstructure {0} 個: {1}", "  {0} .mcstructure files: {1}"),
        ["Accuracy"] = ("精度", "Accuracy"),
        ["DitherStrength"] = ("ディザの強さ", "Dither strength"),
        ["Serpentine"] = ("蛇行走査（誤差拡散のすじを減らす）", "Serpentine scan (fewer diffusion streaks)"),
        ["LinearLight"] = ("リニア光でリサイズ（明暗を正確に保つ）", "Resize in linear light (accurate brightness)"),
        ["Paint"] = ("ペイント（クリック・ドラッグで塗る）", "Paint (click / drag)"),
        ["Brush"] = ("ブラシ", "Brush"),
        ["BrushAuto"] = ("自動に戻す（消しゴム）", "Back to automatic (eraser)"),
        ["BrushNone"] = ("ブロックなし", "No block"),
        ["ShadeNormal"] = ("通常", "Normal"),
        ["ShadeBright"] = ("明", "Light"),
        ["ShadeDark"] = ("暗", "Dark"),
        ["Undo"] = ("元に戻す", "Undo"),
        ["ClearEdits"] = ("手修正をすべて消す", "Clear all edits"),
        ["ClearEditsConfirm"] = ("手作業で塗った部分をすべて消して自動変換の結果に戻します。よろしいですか？", "Remove every manual edit and go back to the automatic result?"),
        ["EditsCount"] = ("手修正 {0} ピクセル", "{0} edited pixels"),
        ["CropDragHint"] = ("元画像の表示でドラッグすると切り抜き範囲を指定できます。", "Drag on the Original view to set the crop."),
        ["Help"] = ("ヘルプ(_H)", "_Help"),
        ["About"] = ("BMAG について…", "About BMAG…"),
        ["OpenLogs"] = ("ログフォルダを開く", "Open log folder"),
        ["FirstRunTextures"] = ("ブロックのアイコン用に Minecraft のテクスチャを取得しますか？\n（Mojang 公式の bedrock-samples から PC にダウンロードします。Minecraft EULA が適用されます。）", "Download the Minecraft textures for the block icons?\n(From Mojang's official bedrock-samples to this PC. The Minecraft EULA applies.)"),
        ["OriginText"] = ("画像左上 = X {0}, Z {1}（支え行 Z {2}）, 高さ Y {3}", "Top-left of image = X {0}, Z {1} (support row Z {2}), Y {3}"),
        ["OriginNone"] = ("レイアウト座標のみで出力します", "Layout coordinates only"),

        // materials tab
        ["TabMaterials"] = ("素材・進捗", "Materials"),
        ["MaterialsFor"] = ("対象", "For"),
        ["Owned"] = ("所持", "Have"),
        ["Missing"] = ("不足", "Missing"),
        ["Need"] = ("必要", "Need"),
        ["Support"] = ("支え", "support"),
        ["Body"] = ("本体", "art"),
        ["Progress"] = ("建築の進捗", "Build progress"),
        ["ChunksDone"] = ("完了チャンク {0}/{1}", "Chunks done {0}/{1}"),
        ["PrNotStarted"] = ("未着手", "Not started"),
        ["PrInProgress"] = ("作業中", "In progress"),
        ["PrDone"] = ("完了", "Done"),
        ["PrSkipped"] = ("スキップ", "Skipped"),
        ["PrNeedsFix"] = ("要修正", "Needs fixing"),
        ["ExportMaterials"] = ("素材リストを CSV で保存…", "Save material list as CSV…"),

        // direct tab
        ["TabDirect"] = ("地図直接生成", "Direct maps"),
        ["DirectHint"] = ("画像をそのまま地図データ（RGBA）としてワールドのコピーへ追加します。元のワールドは変更しません。Minecraft は終了しておいてください。",
                          "Adds the image as map data (RGBA) to a copy of the world. The original world is never changed. Close Minecraft first."),
        ["World"] = ("ワールド", "World"),
        ["Rescan"] = ("再検出", "Rescan"),
        ["BrowseFolder"] = ("フォルダを選択…", "Choose folder…"),
        ["Settings"] = ("設定", "Settings"),
        ["StartId"] = ("開始 ID", "Start ID"),
        ["Delivery"] = ("受け取り方法", "Delivery"),
        ["DlBoth"] = ("チェスト＋額縁の壁", "Chest + item frame wall"),
        ["DlChest"] = ("チェストのみ", "Chest only"),
        ["DlWall"] = ("額縁の壁のみ", "Item frame wall only"),
        ["DlNone"] = ("地図データのみ", "Map data only"),
        ["GlowFrames"] = ("輝く額縁を使う", "Use glow item frames"),
        ["BackingWall"] = ("額縁の後ろに壁を付ける", "Add a wall behind the frames"),
        ["AddToInventory"] = ("プレイヤーのインベントリの空きにも入れる", "Also put maps into empty inventory slots"),
        ["DirectNote"] = ("使用中の ID は自動的に避けます。地図・チェスト・額縁は構造物としてコピー内に保存され、/structure load で取り出せます。", "Used IDs are skipped automatically. Chests and frames are saved as structures in the copy (/structure load)."),
        ["PlanExport"] = ("書き込み内容を確認", "Preview changes"),
        ["RunExport"] = ("コピーを作成して地図を追加…", "Create copy and add maps…"),
        ["ExportPng"] = ("地図画像を PNG で出力…", "Export map images (PNG)…"),

        // maps tab
        ["TabMaps"] = ("地図・校正", "Maps"),
        ["MapsHint"] = ("「地図直接生成」タブで選んだワールドの地図を読み取り専用で表示します。", "Shows the maps of the world selected in the Direct maps tab (read-only)."),
        ["LoadMaps"] = ("地図を読み込む", "Load maps"),
        ["SavePng"] = ("PNG で保存…", "Save PNG…"),
        ["UseAsSource"] = ("元画像として使う", "Use as source image"),
        ["Calibration"] = ("色の校正（実機測定）", "Colour calibration (in-game)"),
        ["CalibrationHint"] = ("全ブロックを並べたテストパッドをゲーム内に置いて地図を作り、その地図から実際の地図色を測定します。", "Place a test pad with every block in game, draw a map of it, then measure the real map colours."),
        ["ExportPad"] = ("テストパッドを出力…", "Export test pad…"),
        ["Measure"] = ("選択した地図で測定", "Measure selected map"),
        ["SaveMeasured"] = ("実測プロファイルとして保存", "Save as calibrated profile"),

        // status / misc
        ["Ready"] = ("準備完了", "Ready"),
        ["Converting"] = ("変換中…", "Converting…"),
        ["Exporting"] = ("出力中…", "Exporting…"),
        ["Error"] = ("エラー", "Error"),
        ["NoImage"] = ("画像が読み込まれていません", "No image loaded"),
        ["Run"] = ("実行する", "Run"),
        ["Cancel"] = ("キャンセル", "Cancel"),
        ["SelectWorld"] = ("ワールドを選択してください。", "Select a world."),
    };
}
