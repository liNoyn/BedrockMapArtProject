# Bedrock Map Art Generator (BMAG)

画像から Minecraft 統合版（Bedrock Edition）のマップアートを作る Windows 用デスクトップアプリです。
MapartCraft（Java 版向け）の統合版版にあたります。2 つの方式に対応しています。

- **ブロック建築型** — 画像をブロックの地図色に減色し、建築ガイド（配置図 PNG・行ごとの手順・素材 CSV）と、
  64×64 に分割した `.mcstructure` を含むビヘイビアパック（`.mcpack`）を出力します。
- **地図データ直接生成型** — 画像を RGBA の地図データ（`map_<ID>`）として **ワールドのコピー** に書き込み、
  地図入りのチェストや額縁の壁を構造物として一緒に保存します。元のワールドには書き込みません。

技術: C# / .NET 8 / Avalonia 11（GUI）、SkiaSharp（画像の読み書き）。LevelDB（Bedrock の zlib raw 圧縮）、
Little-Endian NBT、`.mcstructure` は外部ライブラリを使わずに自前で実装しています。

## ビルドと起動

```bash
dotnet build BMAG.sln
```

```bash
dotnet run --project src/BMAG.App
```

```bash
dotnet test tests/BMAG.Tests
```

## 構成

| プロジェクト | 内容 |
|---|---|
| `src/BMAG.Core` | UI に依存しない処理一式（下表） |
| `src/BMAG.App` | Avalonia GUI（日本語） |
| `src/BMAG.Cli` | コマンドライン（`build` / `direct` / `maps` / `info` ほか） |
| `tests/BMAG.Tests` | xUnit テスト（34 件） |
| `palettes/` | 色プロファイル（JSON、Core に埋め込み） |

設計書の各コンポーネントは、プロジェクトを細かく分けずに `BMAG.Core` 内の名前空間として実装しています。

| 設計書 | 実装 |
|---|---|
| ImageCore | `Imaging/`（デコード、クロップ、Fit/Fill/Stretch、Lanczos/バイリニア/最近傍、補正、分割） |
| ColorEngine | `ColorEngine/ColorMath.cs`（RGB、重み付き RGB、CIELAB、CIEDE2000） |
| BlockPaletteEngine | `Palette/BlockPalette.cs` + `palettes/*.json` |
| ColorQuantizer | `BlockEngine/ColorQuantizer.cs`（なし / Floyd–Steinberg / Atkinson / JJN / Bayer） |
| BlockLayoutGenerator | `BlockEngine/BlockLayout.cs`（Flat、Staircase は実験的） |
| BuildGuideGenerator | `BlockEngine/BuildGuideGenerator.cs`, `Materials.cs` |
| StructureExporter | `Export/McStructure.cs`, `StructureExporter.cs`（分割、ビヘイビアパック、再読み込み検証） |
| MapDataEncoder / MapIdAllocator / MapItemGenerator | `MapData/` |
| BedrockWorldAdapter | `Bedrock/LevelDb/`（読み取り + WAL 追記による書き込み）, `Bedrock/World/` |
| SafeWorldExporter | `MapData/SafeWorldExporter.cs` |
| 色プロファイル校正（F20） | `Calibration/Calibration.cs` |
| ProjectCore | `Project/BmagProject.cs`（`*.bmag.json`） |

## 使い方（GUI）

1. 画像を開く（ドラッグ＆ドロップ可）→ 地図の枚数（横 × 縦）と配置・色補正を調整します。
2. **ブロック建築** タブ: 色差の計算方法・ディザリング・使うブロック（パレット タブ）を選び、
   「建築データを出力」で次のファイルを出力します。
   - `guide/` — マップごとの配置図 PNG（チャンク・構造物境界と座標入り）、行ごとの手順、`materials.csv`、`placements.csv`
   - `BMAG_<名前>.mcpack` — 構造物と読み込み用ファンクション（`/function bmag/<名前>/all` など）
3. **地図直接生成** タブ: ワールドを選んで「書き込み内容を確認」→「コピーを作成して地図を追加」。
   ワールド一覧に「<元の名前> (BMAG …)」が追加されます。ゲーム内でチートを有効にし、
   `/structure load bmag:<名前>_maps ~1 ~ ~`（チェスト）や `bmag:<名前>_wall`（額縁の壁）で地図を取り出します。
4. **地図・校正** タブ: ワールド内の地図を一覧・PNG 保存できます。また、テストパッドを使って実際の地図色を測定し、
   実測プロファイル（`%APPDATA%\BMAG\profiles`）を作れます。

## 安全のための仕組み（設計書 10 章）

- 元のワールドへは書き込みません。コピーを作る前後で元ワールドの全ファイルの SHA-256 を比較し、
  コピーの内容も元と一致することを確認してから、コピー側にだけ書き込みます。
- Minecraft の起動中は実行しません。`db/LOCK` も排他で取得します。
- 書き込みは LevelDB の 1 バッチ（ログへの追記）で行い、終了後に読み直して全キーの内容を照合します。
- 失敗した場合は未完成のコピーを削除し、成功とは表示しません。ログは `%LOCALAPPDATA%\BMAG\logs` に残ります。
- 対応表にないゲームバージョンのワールドには書き込みません（閲覧と、ブロック建築・PNG 出力は使えます）。

## 検証状況（重要）

開発環境から実際のゲームは操作できないため、次の点はまだ **ゲーム内で確認していません**。

| 項目 | 状況 |
|---|---|
| ブロックの地図色 | Java 版の基本地図色を **推定値** として収録（全ブロック `verified: false`）。校正機能で実測値に置き換えられます |
| 地図色の陰影倍率（180/220/255） | Java 版の値を仮定。Staircase 方式はそのため「実験的」としています |
| LevelDB の読み取り | 実際のワールド（1.26.33〜1.26.51）のコピー 4 つで読み取りを確認済み。破損や未対応の形式は検出されませんでした |
| LevelDB への書き込み | 実ワールドのコピーへ書き込み、BMAG 自身で読み戻せることを確認済み。**ゲームが読み込めるかは未確認** |
| 地図 NBT | 実ワールドに保存されていた地図と同じフィールド構成であることを確認済み。表示は未確認 |
| 構造物（チェスト・額縁・ブロック） | 1.18.10 時点の識別子を古いバージョン番号付きで出力し、ゲームの変換機能で現行形式へ変換させる方式です。読み込みは未確認 |
| 額縁の向き・並び順 | Image Map と同じ配置（西向き、左上が 1 枚目）。ゲーム内では未確認 |

最初は **重要でないワールド** で試し、問題があればログ（`%LOCALAPPDATA%\BMAG\logs`）をご確認ください。

## 既知の未対応

- 3D プレビュー、建築進捗の管理 UI（データ構造のみあり）、所持数を入れて不足数を出す機能
- カスタムブロック（`minecraft:map_color`）の読み込み、TIFF / 動画フレーム
- プレイヤーのインベントリへ直接地図を入れる方式（設計どおり、チェストと額縁の構造物を優先しています）
- 英語 UI
