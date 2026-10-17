# Bedrock Map Art Project (BMAP)

[English](README.md) | **日本語**

> **🚧 開発中のプロジェクトです。** 機能は一通り動きますが、ゲーム内での動作確認はまだ済んでいません
> （下の「検証状況」を参照）。リリース版はまだありません。ソースからビルドしてお使いください。

画像から Minecraft 統合版（Bedrock Edition）のマップアートを作る Windows 用デスクトップアプリです。
[MapartCraft](https://github.com/rebane2001/mapartcraft)（Java 版向け Web アプリ）の機能と画面構成を統合版向けに作り直したもので、
画像をブロックの地図色に減色し、**`.mcstructure` ファイル**（構造ブロックで読み込める 64×64 分割、またはマップ単位・全体 1 ファイル）と
建築ガイド（テクスチャ付き配置図・行ごとの手順・素材 CSV・配置座標）を出力します。

画面は MapartCraft と同じ 3 列構成です: 左に **ブロック選択**、中央に **マッププレビュー**（地図色・元画像・差分・陰影解析・
建築ビュー 2D / 3D・高さマップ）、右に **マップ設定**・出力ボタン・**陰影・建築チェック**・**材料**。日本語と英語に対応しています（右上で切り替え）。

技術: C# / .NET 8 / Avalonia 11（GUI）、SkiaSharp（画像処理）。Little-Endian NBT、`.mcstructure`、ディザリング、
3D ビューのソフトウェアレンダラーは外部ライブラリを使わずに自前で実装しています。

> v0.4.0 で名前を **Bedrock Map Art Generator (BMAG)** から **Bedrock Map Art Project (BMAP)** に変更しました。
> 以前の設定・プリセット・カスタムブロック（`%APPDATA%\BMAG`）は初回起動時に `%APPDATA%\BMAP` へコピーされ、
> `*.bmag.json` のプロジェクトもそのまま開けます。

## 主な機能

**ブロック選択（MapartCraft と同じ方式）**
- Minecraft の地図色ごとに 60 の色セット、合計 323 ブロック（MapartCraft の全ブロックに対応する統合版ブロック）。
  使いたいブロックをクリックで **いくつでも** 選べます。行の左には暗・通常・明の 3 段階の地図色と選択数が出ます。
- 同じ色のブロックを複数選ぶと、その色のピクセルを均等に分担します（地図上の見た目は同じで素材を分散できます）。
  分け方は「まとまりで」（マップごと・行ごとに連続した範囲。建築しやすい）か「ばらばら」を選べます。
- ブロックの識別子と状態は Mojang 公式のブロックメタデータ（Bedrock 1.26）で検証し、全状態を出力します。
- プリセット: 各色 1 ブロック / サバイバル向け / サバイバル向け・全部 / 支え不要 / すべてのブロック / カーペット /
  コンクリート / 羊毛 / グレースケール / なし、ユーザー定義プリセットの保存。
- カスタムブロック: 手入力、またはビヘイビアパック（フォルダ / `.mcpack` / `.mcaddon`）の `minecraft:map_color` から読み込み。
- **ブロックのアイコン**: Minecraft のテクスチャで表示します。初回のビルド時に Mojang 公式の
  [bedrock-samples](https://github.com/Mojang/bedrock-samples) から必要なテクスチャを `.textures/` に取得し、
  `BMAP.exe` と同じフォルダの `textures/` に入れるので、起動した時点からアイコンが出ます
  （Minecraft EULA が適用されます。テクスチャは本リポジトリには含めていません）。
  オフラインでビルドした場合はアプリ内の「Minecraft のテクスチャを取得」から取得できます。リソースパックも指定できます。

**変換**
- 画像: PNG / JPEG / WebP / BMP / GIF / TIFF。アニメーション GIF・WebP と複数ページ TIFF はフレームを選択可能。
  動画（mp4 など）は ffmpeg があれば指定した秒数のフレームを取り込みます。
- 切り抜き（なし / 中央 / 手動・ドラッグ）、Fit / Fill / Stretch、Lanczos / バイリニア / 最近傍、明るさ・コントラスト・彩度・ガンマ・シャープ。
- 透明部分: 背景色となめらかに合成 / ディザ合成 / 透明を保持（ブロックなし）。
- 色差: Oklab（既定）/ CIELAB ΔE76 / CIEDE2000 / 重み付き RGB / RGB。リサイズはリニア光で行います。
- **ディザリング**
  - 誤差拡散: Floyd–Steinberg、Jarvis–Judice–Ninke、Burkes、Sierra Lite、Stucki、Atkinson。
    **リニア光で誤差を拡散**（既定）し、遠くから見たときの明るさを正確に保ちます。蛇行走査・強さの調整つき。
  - **Knoll パターンディザ**（Photoshop で使われている Thomas Knoll の方式。Bayer 8×8 / ブルーノイズ）:
    各色を地図色の組み合わせで近似し、規則的なパターンで並べます。従来の Bayer より見た目の色差が 35〜41% 小さくなります。
  - 規則ディザ: ブルーノイズ 64×64（void-and-cluster 法で生成）、Bayer 2×2 / 4×4 / 8×8、Ordered 3×3。
  - 方式ごとの比較は [docs/benchmarks.md](docs/benchmarks.md) にあります。

**3D マップアート（階段）と陰影規則**
- 3D: なし（平面）/ Classic / Valley / 全面暗 / 全面明。
- **陰影規則の解析**（詳しくは [docs/shading-rules.md](docs/shading-rules.md)）
  - 地図シミュレーター: ブロックの配置だけから地図を描き直し、計画した色と 1 ピクセルずつ照合します（全方式で一致をテスト）。
  - 陰影・建築チェック: 予定と違うピクセル、北隣が空で地形しだいのピクセル、落下・破損するブロック、
    暗・通常・明の数、最も高い列、段差と落差の数。陰影解析ビューで場所も確認できます。
  - 陰影テストパッド: 段差の大きさ・階段・ジグザグ・東西の高さ違いを並べた地図 1 枚分の構造物と、予測した地図を出力します。
    ゲームで地図を作って見比べれば、統合版の陰影規則をワールドデータを読まずに確認できます。
- **高さの最適化**
  - 「最大の高さ」（Valley 階段）を決めると、その高さの中で一番きれいになる陰影を計算します（ディザなし・規則ディザ・
    Knoll では列ごとの動的計画法による最適解、誤差拡散では各ピクセルで高さを超えないように選択）。
  - 「この画像に最適な高さを分析」: 平面・各高さ・無制限で変換し、遠目の色差（リニア光でぼかして CIEDE2000 で比較）と
    建築の高さを一覧にして、効果がほぼ頭打ちになる高さをおすすめします。例: ある写真では無制限の 37 ブロックに対し、
    7 ブロックの高さで改善の 83% を得られました。
  - 「背景色を平面で出せる色にする」: 真っ白などは「明」の陰影でしか出せず背景全体が階段になるため、平面で出せる近い色に変えます。
  - 「高さを抑える」、平らな部分で暗い横線が出ないよう下がる位置を列ごとに分散、置く高さの診断（Y −64〜319、推奨 Y）、高さマップ。
- 支えブロック: なし / 必要なブロックの下だけ / すべて（サバイバル向け・最適化）/ すべて（二重）。支えブロックが地図に写らないことを検証済み。
- 手修正: 建築ビュー 2D でブロックを 1 ピクセルずつ塗れます（元に戻す・自動に戻す、プロジェクトに保存）。
- Bedrock 固有: 草ブロックと一部の葉はバイオームで地図色が変わるため、既定では使いません。

**プレビュー・ガイド**
- ブロック地図色（予測）/ 元画像 / 差分 / 陰影解析 / 建築ビュー 2D（テクスチャ・高さ・チャンク境界・座標表示）/ 高さマップ。
- **建築ビュー 3D**: 自由に回転・移動・ズーム、遠近法（透視補正テクスチャ）。テクスチャ / 地図に写る色 / 陰影 / 高さ の色分け、
  アンビエントオクルージョンと太陽の影、チャンク・地図の境界線、表示する高さの上限（1 段ずつ建てる用）、
  最も高い列への移動、マウスを重ねたブロックの強調と座標・陰影の表示。8×8 マップ（540 万面）でも約 1 秒で準備できます。
- 素材リスト: マップ別、スタック数、代替ブロック、**所持数と不足数**、CSV 保存。建築の進捗（マップ・チャンク単位）。
- 出力: `.mcstructure`（64×64 / マップ単位 / 全体）と zip、`配置方法.txt`（各ファイルの座標と `/structure load` コマンド）、
  `placement.csv`、配置図 PNG、行ごとの手順、`materials.csv`、地図プレビュー PNG。ビヘイビアパックは任意。

## ビルドと起動

```bash
dotnet build BMAP.sln
```

初回のビルドはテクスチャの取得のため 1 分ほどかかります（インターネット接続が必要。取得しない場合は `-p:BundleTextures=false`）。

```bash
dotnet run --project src/BMAP.App
```

```bash
dotnet test tests/BMAP.Tests
```

配布用 zip（.NET 不要の単一 exe。テクスチャは含めず、初回起動時に取得を案内します）:

```bash
powershell -ExecutionPolicy Bypass -File tools/publish.ps1
```

コマンドライン（`BMAP.Cli`）: `build`（建築データ出力）/ `analyze`（高さ分析）/ `bench`（ディザ・色差の比較）/
`render`（3D ビューを PNG に、各段階の処理時間も表示）/ `textures`。引数なしで使い方を表示します。

## .mcstructure の読み込み方

1. 出力フォルダの `mcstructure` にファイルがあります（`配置方法.txt` に各ファイルの座標とコマンド）。
2. ゲームで `/give @s structure_block` → 構造ブロックを置き「読み込み」モード → 「インポート」でファイルを選択。
3. オフセットを合わせて読み込むか、インポート後に `配置方法.txt` の `/structure load` コマンドを実行します。

## 構成

| パス | 内容 |
|---|---|
| `src/BMAP.Core` | UI に依存しない処理一式（下表） |
| `src/BMAP.App` | Avalonia GUI（日本語 / 英語） |
| `src/BMAP.Cli` | コマンドライン |
| `tests/BMAP.Tests` | xUnit テスト |
| `tools/PaletteGen` | `blocks.txt` から `palettes/bedrock-full-v2.json` を生成（Mojang のメタデータで検証） |
| `tools/publish.ps1` / `tools/make-icon.ps1` | 配布用 zip / アプリアイコンの作成 |
| `docs/` | [陰影規則](docs/shading-rules.md)、[ベンチマーク](docs/benchmarks.md) |

| 機能 | 実装（`src/BMAP.Core`） |
|---|---|
| 画像処理 | `Imaging/`（デコード、TIFF・フレーム・動画、切り抜き、リサイズ、補正、分割） |
| 色空間・色差 | `ColorEngine/ColorMath.cs` |
| ブロックと色セット | `Palette/`, `BlockEngine/PaletteSelection.cs` |
| 減色・ディザ | `BlockEngine/ColorQuantizer.cs`（`.Knoll.cs`）, `BlueNoise.cs` |
| 階段・支えブロック | `BlockEngine/BlockLayout.cs`, `ColumnIndex.cs` |
| 高さの分析・配置 | `BlockEngine/HeightAdvisor.cs` |
| 陰影規則・地図シミュレーター | `Shading/` |
| 2D / 3D プレビュー | `Preview/LayoutRenderer.cs`, `Preview/Viewer3D.cs` |
| 出力・ガイド・素材 | `Export/`, `BlockEngine/BuildGuideGenerator.cs`, `Materials.cs` |
| テクスチャ | `Textures/`（ダウンロード・リソースパック・TGA） |
| プロジェクト | `Project/BmapProject.cs`（`*.bmap.json`） |

## 検証状況（重要）

開発環境から実際のゲームは操作できないため、次の点はまだ **ゲーム内で確認していません**。

| 項目 | 状況 |
|---|---|
| ブロックの地図色 | Minecraft の地図の基本色（Java 版の値）を **推定値** として使用（全ブロック未検証） |
| 陰影の規則と倍率（180/220/255） | Java 版の規則を仮定。陰影テストパッドで実機と見比べて確認できます |
| ブロック識別子・状態 | Mojang 公式メタデータ（1.26.50）で全 323 ブロックを検証済み。構造物としての読み込みは未確認 |

問題があればログ（`%LOCALAPPDATA%\BMAP\logs`）をご確認ください。

## 既知の制限

- APNG は最初のフレームのみ。動画の読み込みには ffmpeg（PATH 上か BMAP と同じフォルダ）が必要です。
- 水（深さで陰影が変わる）は MapartCraft と同じく対象外です。
- 地図データの直接生成（ワールドへの書き込み）は、調整が難しいため v0.3.0 で削除しました。
- バイオームで色が変わるブロック（草・一部の葉）の色はバイオームを指定できません。
- 構造ブロックでインポートした構造物の名前（mystructure:…）はゲームのバージョンによって異なる可能性があります。

## 参考にしたプロジェクト・資料

- [MapartCraft](https://github.com/rebane2001/mapartcraft)（rebane2001、GPL-3.0）: 色セットの分け方、画面構成、多くの機能。コード・画像は含みません。
- [Cartographer](https://github.com/julienvincent/cartographer): 複数の階段アルゴリズムを持つマップアート変換ツール。
- Pekka Väänänen, [Revisiting Yliluoma's ordered dither algorithm](https://30fps.net/pages/revisiting-yliluoma-2/):
  Knoll のパターンディザと、リニア光でディザするべき理由。
- R. Ulichney, "The void-and-cluster method for dither array generation"（1993）: ブルーノイズの生成方法。
- B. Ottosson, [Oklab](https://bottosson.github.io/posts/oklab/)（2020）、G. Sharma ほか「The CIEDE2000 color-difference formula」（2005）。
- S. Abasi ほか「Distance metrics for very large color differences」（2020、HyAB）: 試しましたが、この用途では悪化したため不採用です。

## ライセンス

[MIT License](LICENSE)

Minecraft は Mojang Studios / Microsoft の商標です。本プロジェクトは Mojang Studios / Microsoft とは関係ありません。
ブロックのテクスチャは利用者の PC にダウンロードされるもので、本プロジェクトには含まれません。
