# Streamed SOG を読んで LOD で描く

SuperSplat の Streamed SOG（`lod-meta.json` + チャンク）を実行時に読み、葉ごとに 1 段だけ
描く。ベンチは https://superspl.at/scene/7a7bfaea（姫路城、17,281,128 splats、5 段、葉 2,324、
run 11,612、チャンクファイル 36）。

設計時の検討は docs/superpowers/specs/2026-09-17-ssog-lod-design.md にあるが、**§4〜5 の
距離帯の記述は実装前の案で、採用しなかった**。現物はこのドキュメント。

## 置き方と発見の順

`<game>/vdgs/<name>/` に置く。`SplatMetaFile.ClassifyDirectory` と `SplatScene.Discover` が
この順で見る：

| 見るもの | 判定 | LOD |
|---|---|---|
| `lod-meta.json` | Streamed SOG | あり |
| `meta.json` に `formatVersion` | 変換済み（従来形式） | なし |
| `meta.json` に `version: 2` と `means` | SOG ディレクトリ | なし |
| `<name>.sog` | 束ねた SOG（zip） | なし |
| `<name>.ply` | 生の ply | なし |

`python3 tools/fetch_ssog.py <hash> <dir>` が SuperSplat から一式を落とす。

**鏡映の規則は `.ply` と同じ。** 既定 `mirrorY = true`、`SplatScene.MirrorFor(Placement)` が
splat とコリジョンの両方に同じ値を渡す。SH 係数にも掛かる（`SogLoader.WritePaletteRow`）。

## ディスク上の形

### SOG v2 のチャンク

1 チャンク = `meta.json` + ロスレス WebP が数枚。`meta.json` が読むフィールドは
`SogMeta.cs` の DTO がそのまま：

```
version  2 固定
count    このチャンクの splat 数
means    { mins[3], maxs[3], files[2] }      位置。下位・上位バイトで 2 枚
scales   { codebook[256], files[1] }         対数スケール。索引はバイト
quats    { files[1] }                        smallest-three。alpha に 252+m
sh0      { codebook[256], files[1] }         DC 項
shN      { count, bands, codebook[256], files[2] }     SH パレット
```

**`quats` の alpha は落とした成分の番号を `252 + m` で持つ。** 252〜255 以外は
単位回転として扱う。

**`shN` は 64 列のタイル並び。** centroid 画像の幅は `64 * ShCoeffs[bands]` でなければ
ならず、合わなければ `SogException` を投げる（高さは見ていない。「踏むと高くつくところ」）。

### `lod-meta.json`

```
count       全段の splat 合計（environment を含まない）
lodLevels   段数
counts[]    段ごとの合計。長さは lodLevels と一致すること
filenames[] チャンクの meta.json への相対パス
environment 常に描く 1 チャンク（省略可）
tree        二分木。内部ノードは children[2]、葉は bound{min[3],max[3]} と lods{}
```

`lods` は `{"0": {file, offset, count}, "1": {...}}` の形。`SsogIndex.Walk` が木を深さ優先で
畳んで、**葉の番号順・段の昇順**に並んだ `Runs[]` を作る。1 run = 「ある葉のある段が、
どのチャンクファイルのどの範囲か」。

**run はチャンクファイルを隙間なく敷き詰める。** `SsogIndexTests.RunsTileEachChunkFile` が
fixture でそれを見ている。**読み込み時も `BuildLod` が検査する** —— どの splat もちょうど 1 つの
run に属すること。隙間や重なりは `SogException` になる（「外から来るデータ」）。

## 復号の経路

`src/VDGS/Vp8l/` と `src/VDGS/Sog/` は **UnityEngine に依存しない**。同じファイルが
BepInEx プラグイン（netstandard2.0）と xunit（net8）の両方にリンクされるので、`unsafe` も
Newtonsoft 以外の外部パッケージも使えない。テストが書けるのはこの分割のおかげ。

### VP8L を純 C# で解く

`Vp8lDecoder.cs`。仕様は RFC 9649 §3。Huffman グループ、14 種の予測器、
カラーキャッシュ、LZ77、4 つの逆変換まで実装してある。

**`dwebp` とバイト一致を xunit で見ている** — 合成した `.webp` 27 枚。`dwebp` の出した画素
そのものは 14 MB あるので置かず、
**`<バイト数> <sha256>` の 1 行**を `.sha256` として隣に置いてある。ハッシュ一致はバイト一致なので、検査の強さは
変わらない。どちらも `tools/make_vp8l_fixtures.sh` が再生成する。

### チャンクから `SogSplats` へ

`SogChunk.Decode` が 1 チャンクを float 配列に開く（位置・スケール・回転・色・不透明度・
SH ラベル・SH パレット）。`SogLoader` が `Parallel.For` でチャンクを並列に回す。

## 常駐レイアウトへの詰め直し

**全段を常駐させる。** 飛行中の `GraphicsBuffer.SetData` を避けるため。段を切り替えても
アップロードは起きない。

`SplatWriter`（`PlyLoader` から切り出し、両方が共用）が 1 splat を `pos` / `other` /
`color` に詰める。SOG 経路は必ず `clusterSh: true`（`other` の stride は 18 = 回転 4 +
スケール 12 + SH 索引 2）。

**SH は常に `Cluster64k`。** チャンクが SH を持たない場合でも同じ形にして、シェーダー側の
分岐を「パレット基点の加算」1 本に絞ってある。

**パレットはチャンクごとに連結する。** splat が持つ索引は 16 bit で自分のチャンク内相対
なので、run ごとの先頭行（`RunShBase`）をシェーダーで足す。

**ちょうど 65,536 本が普通**（splat-transform の既定）。範囲外ラベル用のゼロ行を上限に
数えると実データが全部弾かれるので、判定は `> 65536`。fixture は小さくてこれを踏めなかった。

## 段の選択

`src/VDGS/Lod/LodSelector.cs`。UnityEngine 非依存で、構築後は原則割り付けなし。

**見かけの大きさで選ぶ。距離ではない。**

```
size = 葉の最長辺 * worldScale / max(距離, 1e-4)
```

3D Tiles の SSE や Unity の LODGroup と同じ指標。`worldScale` は分母分子の両方に掛かるので
**打ち消し合う** — 44 倍で置いたキャプチャでも設定を変えなくていい（`ScaleCancelsOut`）。

**距離帯ではこれが壊れた。** 姫路の天守は 65 m で最粗段になり、カメラの足元の芝が最細段に
なった。段だけの `.ply` に負けた。葉の大きさが 10 倍なら 10 倍遠くまで detail を保つ、が
正しい規則。

1 tick の流れ：

1. 葉の AABB 上の最近点までの距離
2. `size` から欲しい段 `want` を求める。`size >= detail` なら 0、そうでなければ
   `floor(log2(detail/size)) + 1` を `[0, L-1]` にクランプ
3. 直前の段の帯 `(lo, hi]` に ±10% の余裕を付け、その中なら段を据え置く（ヒステリシス）
4. その葉が実際に持つ段へスナップ（`FirstLevelAtLeast`）。**持っている中で最も粗い段より
   さらに粗いものが欲しいときは、その葉は描かない**（`-1`）
5. 予算超過なら、遠い葉から 1 段ずつ粗くする

**選択は 10 フレームに 1 回**（`UpdateLod` の間隔ガード）。オフラインの sweep harness は
`Camera.Render()` の間で `Time.frameCount` が進まないので `RefreshLod` で強制する。

`BandJitter` は葉ごとに閾値を ±ずらして、同じ距離に並ぶ葉が同じ tick でいっせいに切り替わる
のを防ぐ。**既定 0 で、`RenderSweep` の `-vdgsLodJitter` からしか設定できない**。

## GPU 側の経路

選ばれなかった splat は**ソート鍵バッファから外す**。駐車ではない。

```
選択が変わった frame:  CSResetActiveCount -> CSCompactActive
毎 frame:              CSCalcDistances -> radix sort -> CSCalcViewDataLod
```

`CSCompactActive` が `InterlockedAdd` で選択された splat を鍵バッファの前方へ詰める。以降
`CSCalcDistances` も radix sort も `CSCalcViewDataLod` も **`_SortCount` 本だけ**走る。
`_SortCount` は CPU 側の `LodSelector.ActiveSplats`。

**詰める側は常駐全部を走査する**（dispatch は `m_SplatCount` 本）。ただし走るのは選択が
変わったフレームだけで、選択自体が 10 フレームに 1 回なので、毎フレームの費用にはならない。

run テーブルはバッファ 2 本：

- `_SplatRun[splat]` — その splat が属する run
- `_RunInfo[run*2]` — その run の段がいま選ばれていれば 1
- `_RunInfo[run*2+1]` — その run のチャンクのパレット先頭行

**LOD の有無はシェーダーキーワードで分ける**（`VDGS_LOD`、`SplatUtilities.compute` にのみ
`#pragma multi_compile`）。実行時フラグにしなかった理由は測ってある：**バッファを名前で
参照するだけでカーネルが「使っている」扱いになる**ので、段の無いキャプチャもダミーを
バインドしないと Unity が dispatch を落として何も描かれない。そしてダミーを bind するだけで
2.24M の `.ply` が約 0.7 ms 遅くなった。キーワードなら**バッファが存在しない**。

**キーワードはシェーダーではなく CommandBuffer に置く。** キーワードの状態は
buffer が **実行される時**に読まれるので、シェーダー側に置くと画面上の全キャプチャが
最後の 1 つの設定を食う。

## 操作面

`placement.json` の 2 つ。Tweak 画面のダイアルからも動く。

| 鍵 | 既定 | 範囲 | 意味 |
|---|---|---|---|
| `lodDetail` | 1 | 0.02〜20 | 最細段を保つ見かけの大きさ。**無単位** |
| `lodBudget` | 3,000,000 | 100k〜50M | 描く splat の上限 |

`/api/status` が段ごとの内訳（`activePerLevel`）を返す。`vdgs-perf.log` にも出る。

## 数字

RTX 3060、エディタ D3D12、1024²、カメラはシーン内：

| 構成 | 常駐 | 描画 | 平均 |
|---|---|---|---|
| 空フレーム | — | 0 | 2.6〜3.2 ms |
| level 2 だけを `.ply` で | 2.24M | 2.24M | 9.3 ms |
| ssog、LOD 既定 | 17.3M | 2.98M | **10.9 ms** |
| ssog、全葉 level 0 | 17.3M | 8.89M | 23.1 ms |

**LOD の判断に Mac エディタの数字を使わない。** 全葉 level 0 が M1 Max で 241 ms、
RTX 3060 で 23 ms。17.3M 常駐でユニファイドメモリが崩れる。

ポッピングは `RenderSweep` で測った（同一カメラ位置で段だけ変えた 2 枚の差分）。周回で
中央値 1.10%、p90 5.09%、最大 8.61%。`BandJitter = 0.4` は中央値を半減させるが最大は
変えない。**距離を伸ばすと悪化する**（予算の取り合いが増えるため）。

## 踏むと高くつくところ

- **シェーダーを変えたら DLL も一緒に配る。** 焼いただけで DLL が古いと `_SortCount` が 0 の
  まま渡り、**エラーを出さずに何も描かれない**。逆も同じ
- **`Resolve` の封じ込めは文字列の検査。** `..`・絶対パス・バックスラッシュ・NUL・ドライブ
  レターを弾き、結合後のパスをルートと前置き比較する。ただし `Path.GetFullPath` は
  **シンボリックリンクを辿らない**（netstandard2.0 に `ResolveLinkTarget` が無い）
- **`shN` centroid 画像は幅しか検査していない。** 高さが足りないぶんのパレット行は無言で
  `codebook[0]` になる。SH が一定値でずれた色になる
- **外から来るデータは読み込みの段で弾く。** キャプチャは第三者が作ったファイルなので：
  - `SsogIndex.Parse` —— `count` / `lodLevels`（1〜16）/ run の `file` / `offset` / `count` は
    整数が必須。段は範囲内、同じ葉に同じ段は 1 つ（`"1"` と `"01"` も重複扱い）、`bound` は min ≤ max
  - `BuildLod` —— run が自分のチャンクに収まり、全 splat がちょうど 1 つの run に属する
  - サイズ —— チャンクは 33.5M splats まで、パレットは 65,536 行まで。WebP はヘッダを読んだ時点で
    画素数を上限と比べる（上限はチャンクの `count` から出す）。`.sog` の中身は申告サイズではなく
    実際に展開した量で 512 MB で打ち切る

  どれも `SogException` になり、`SogLoader.Load` の `error` として出る。**`Spawn` まで届かない**
  ので、半分組んだシーンも残らない。以前はどれも無検査で、小さなファイル 1 つで 1〜2 GB を確保させられた
- **environment チャンクは `counts` に入っていない。** `BuildLod` が余分な file・葉・run として
  後ろに足すので、**`SplatCount != Σcounts` は environment 付きでは正常**。ここに
  「Σ が合うこと」を検証として足すと**ベンチの姫路城が弾かれる**

## 未解決

- **スポーンで 16 秒止まる。** RTX 3060 の実機で 17.3M が decode 7.6 秒・pack 8.3 秒。
  M1 Max のエディタは 39〜67 秒。**飛ぶ前に出しておく**。`SplatWriter.PackRotation` と
  `SogChunk.UnpackQuat` が splat ごとに 4 要素の配列を確保しているのが筆頭の容疑
- **段の無い `.ply` が master より 0.85 ms（約 9%）遅い。** `-vdgsSortNth` で切ると
  ソート側 0.5 ms・view パス 0.3 ms。仮説 2 つは否定済み（LOD バッファを bind しない →
  何も描かれない、`SetKeyword` の回数を減らす → 変化なし）。パスごとの計測が要る
- **名前の綴りが読み手ごとに違う。** `SogSource.Resolve` と `SogSource.ZipFiles.Read` は
  先頭の `./` について揃えたが、`Resolve` はバックスラッシュを拒み `ZipFiles.Read` は
  正規化する。`SplatMetaFile.ReadSogZip` は `./` を剥がさないので、エントリが `./meta.json`
  の `.sog` は**正しく描けるのに `splats: 0` と表示される**
