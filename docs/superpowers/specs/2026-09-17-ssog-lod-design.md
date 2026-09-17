# Streamed SOG と LOD 描画 設計

2026-09-17。`<game>/vdgs/<name>/` に SuperSplat の Streamed SOG（`lod-meta.json` + チャンク）
か `<name>.sog` を置けば、LOD 付きで飛べるようにする。取得は `tools/fetch_ssog.py`。
companion は触らない。

ベンチは https://superspl.at/scene/7a7bfaea
（`https://d28zzqy0iyovbz.cloudfront.net/7a7bfaea/v1/lod-meta.json`）：

| 項目 | 値 |
|---|---|
| 総 splat | 17,281,128（level 0: 8,893,019 / 1: 4,473,657 / 2: 2,236,829 / 3: 1,118,415 / 4: 559,208） |
| 段 | 5、1 段ごとに約半分 |
| 葉 / run / チャンクファイル | 2,324 / 11,612 / 36 |
| 木の深さ | 17（二分木） |
| 範囲 | 310 × 290 × 350 m、葉の最長辺は中央値 14 m（0.08〜159 m） |
| チャンク 1 つ | 558,784 splats、748×748 の WebP 7 枚 = 9.6 MB、SH 3 帯、パレット 65,536 本 |

全段常駐で約 730 MB（約 42 B/splat）+ ソート 140 MB。RTX 3060 の 12 GB に収まる。

## 決めたこと（brainstorm で確定）

- **WebP は純 C# で解く。** ネイティブ libwebp は Windows DLL と macOS dylib の 2 本と
  quarantine の罠が付く。companion（Rust）での変換は drop-in を殺し、VDGS 形式の書き手が
  3 つ目になる
- **全段をスポーン時に常駐させ、毎フレームは段の選択だけ。** 飛行中の `SetData` は必ず
  スタッターになる（CLAUDE.md）。ストリーミングは範囲外
- **companion は触らない。** 取得はスクリプト
- 実装は Grok に切り出す。ぼくは設計・レビュー・実機

## 仕様の要点（一次情報から）

SOG v2（https://developer.playcanvas.com/user-manual/gaussian-splatting/formats/sog/）：

- `meta.json` + 画像。**画像は全部ロスレス WebP**（VP8L）。`.sog` は同じファイルを直下に
  置いた zip
- `means_l` / `means_u`（RGB）：`q = (u << 8) | l`、`n = lerp(mins[k], maxs[k], q / 65535)`、
  `x = sign(n) * (exp(|n|) - 1)`
- `scales`（RGB）：`exp(codebook[byte])`
- `quats`（RGBA）：smallest-three。RGB が 3 成分を `[-√2/2, √2/2]` で量子化、A が
  `252 + 落とした成分の添字`（順序は `w, x, y, z`）。残りは `sqrt(max(0, 1 - a² - b² - c²))`
- `sh0`（RGBA）：`c = 0.5 + codebook[byte] * 0.2820948`、`opacity = A / 255`（線形、logit ではない）
- 参照実装 `splat-transform/src/lib/readers/read-sog.ts` で確認済み：quat の 3 成分は
  `(byte / 255 * 2 - 1) / √2`、落とした成分は正で復元、配置表は
  `[1,2,3, 0,2,3, 0,1,3, 0,1,2]`（`tag-252` ごとに a,b,c が入る `[w,x,y,z]` の添字）。
  tag が 252〜255 以外なら単位 quat。`label >= shN.count` の SH はゼロ
- `shN_centroids`（RGB、1 行 64 本）+ `shN_labels`（RG、`label = r | (g << 8)`）：
  係数 `c` の座標は `u = (label % 64) * coeffs + c`、`v = label / 64`。`bands` 1/2/3 →
  coeffs 3/8/15。値は `codebook[byte]`
- 座標は右手系 x=右 y=上 z=後ろ。**Unity へは `.ply` と同じ鏡映で載る**（位置 y 反転、
  quat の x と z 反転、SH の奇 Y 帯 k=0,3,4,8,9,10 を反転 — `PlyLoader` と同じ規則）。
  鏡映は centroid 側で 1 回だけ掛ける
- v1（`version` 無し、`mins/maxs` の線形 lerp）は**読まない**。`version != 2` はエラー

Streamed SOG（https://developer.playcanvas.com/user-manual/gaussian-splatting/formats/streamed-sog/）：

- `lod-meta.json`：`count`、`counts[]`、`lodLevels`、`filenames[]`（各チャンクの `meta.json`
  への相対パス）、`tree`
- `tree` は二分木。内部ノードは `children: [Node, Node]`、葉は `lods: { "<level>":
  { file, offset, count } }`。**チャンクファイルの中身は、それを参照する葉 run の連結そのもの**
  で、run 内は Morton 順、run をまたぐ順序は無い。画素は `(i % W, i / W)`
- 段は同じ領域を粗くした別集合。**葉ごとに 1 段だけ選ぶ**。段を欠く葉がある（PlayCanvas
  #9326）ので、無い段は「描かない」
- `errors[]` は任意。今回は使わない（PlayCanvas も 2.22 で既定を距離モードに戻した。
  空だらけの葉が level 0 に張り付く）
- `environment` は任意の別 SOG。あれば run 1 本・常時 active として同じ経路で載せる

## 1. 発見と読み込み経路

| 置き方 | 判定 | 経路 |
|---|---|---|
| `<name>/lod-meta.json` | ssog | 全チャンク → 1 つの `SplatData` + LOD 表 |
| `<name>/meta.json` に `"version": 2` と `"means"` | SOG 展開版 | run 1 本の退化ケース |
| `<name>.sog` | zip（`System.IO.Compression`、ゲームの `Managed/` に同梱を確認済み） | 同上 |
| `<name>/meta.json` に `formatVersion` | いまの VDGS | 変更なし |

`SplatScene.Discover` は `lod-meta.json` を先に見る。`meta.json` の判定は `formatVersion`
の有無と `means` の有無の**両方**で決め、どちらでもなければ「不明な meta.json」として
報告して飛ばす。`.sog` と同名のディレクトリがあればディレクトリが勝つ（`.ply` と同じ規則）。

`SplatScene.IsPly` は `DecodesAtLoad`（ply または sog）に広げる。`MirrorFor` の既定 true、
`SetOrientation` の mirror 変更（despawn/respawn）、`SplatCollision` への mirror 受け渡しは
SOG にも同じに効く。`SplatMetaFile.Read` は `Kind = "ssog"` / `"sog"` を返し、splat 数は
`lod-meta.json` の `count`（または `meta.json` の `count`）、バイト数は配下の合計。

## 2. デコード層（UnityEngine 非依存）

`src/VDGS.Tests` は UnityEngine を参照できず、必要な `.cs` を 1 本ずつリンクして xunit で
回す。だからここに置くものは **System と Newtonsoft だけに依存する**。

- `Vp8l/Vp8lDecoder.cs` — ロスレス WebP。RIFF/`VP8L` ヘッダ、5 種の Huffman 群（緑+length
  prefix、赤、青、alpha、distance）、変換 4 種（predictor 14 モード、color transform、
  subtract green、color-indexing とピクセル束ね）、color cache、LZ77 の距離コード（近傍 120
  個の表）。入力 `byte[]` → `width, height, byte[] rgba`。VP8（lossy）と拡張ヘッダ
  `VP8X` は非対応でエラー
- `Sog/SogChunk.cs` — `meta.json` + 画像 7 枚 → `SogSplats`（`float[] pos`（3n）、
  `float[] scale`（3n）、`float[] quat`（4n、`x y z w`）、`float[] color`（3n）、
  `float[] opacity`、`ushort[] shLabel`、`float[] shPalette`（本数 × 45、無ければ空）、
  `int shBands`）。鏡映はここでは掛けない（純粋な復号）
- `Sog/SsogIndex.cs` — `lod-meta.json` → `Leaf[] {min, max}`、`Run[] {file, offset, count,
  leaf, level}`、`LevelCount`、`Files[]`
- `Sog/SogSource.cs` — 3 つの置き方（ssog ディレクトリ / SOG ディレクトリ / `.sog` zip）を
  「`meta.json` と画像バイト列を名前で引く」1 つの口に揃える

UnityEngine 側は `SogLoader.cs` 1 本：`SogSource` を開き、チャンクを `Parallel.For` で
復号し、`SplatWriter` で詰める。

### 常駐レイアウトと `SplatWriter`

`PlyLoader` の内側にある「1 splat を pos.bin / other.bin / color テクスチャに詰める」部分
（Morton テクセル、10.10.10.2 の rot、half 化、SH の転置と符号）を `SplatWriter.cs` に
切り出して両方から使う。PlyLoader の出力は 1 バイトも変わらない（既存の ply で
`verify_orientation.py` が同じ値を返すことで確認する）。

フォーマットは PlyLoader と同じ `Float32 / Float32 / Float16x4`。**SH だけ `Cluster64k`**：
SOG のパレット + ラベルは upstream の cluster 形式そのもの（`other.bin` の末尾 2 バイトが
ushort 索引、`sh.bin` は 1 行 96 B = 45 half を 16 B 境界に丸めたもの）。splat あたり
96 B → 2 B。SH 無しのチャンク（`shN` 無し、または `bands` 0）は索引 0 の全ゼロ行を 1 本置く。
`bands` 1/2 は残りの係数をゼロで埋める。

常駐順は**チャンクファイルの連結順のまま**（`filenames` の順）。run 表の `offset` は
その上のグローバル範囲に読み替える。コピーはしない。

パレットも同じ順に連結する。**チャンクごとにパレットが 65,536 本あり、renderer の索引は
16 bit** なので、run ごとに `shBase`（そのチャンクのパレット開始行）を持ち、シェーダーで
`shIndex = ushort + shBase` にする（§3）。36 × 65,536 × 96 B = 226 MB。

### 復号のコスト（見積り、実測で置き換える）

チャンク 1 つが 748² × 7 枚 = 3.9M 画素、36 ファイルで 560 MB の RGBA。純 C# の VP8L が
単スレッド 20〜40 MB/s なら 14〜28 秒、16 コアで並列にして 1〜3 秒。加えて WebP の読み
350 MB と `SetData` 730 MB。**初回スポーンの数字を `[VDGS] ssog` のログ行に残す**（PlyLoader と
同じ形式：read / decode / pack / total）。遅ければキャッシュは findings。

## 3. LOD のデータと renderer

`SplatData` に任意の `LodInfo` が付く：

```
Runs[]    { int offset, count, leaf, level; int shBase }
Leaves[]  { float3 min, max }        // オブジェクト空間
LevelCount
uint[] RunOfSplat                    // splat ごと。長さ SplatCount
```

GPU 側は新バッファ 2 つ：

| バッファ | 内容 | 更新 |
|---|---|---|
| `_SplatRun` | splat ごと uint（run id）。17.3M で 69 MB | スポーン時 1 回 |
| `_RunInfo` | run ごと `uint2 { active, shBase }`。11,612 本 = 93 KB | 選択が変わったフレームだけ `SetData` |

シェーダーの変更は 3 箇所：

- `CSCalcDistances` の先頭：`_LodEnabled != 0 && _RunInfo[_SplatRun[origIdx]].x == 0` なら
  `culled = true`。既存の「最大ソートキーに駐車して indirect draw が届かない」がそのまま効く
- `CSCalcViewData` の先頭：同じ判定で `return`。**これが無いと 87% のコストを常駐 17.3M 全部に
  払う。** run は連続範囲なので wave ごと丸ごと抜ける
- `LoadSplatData` の SH 索引：cluster 形式のとき `shIndex += _RunInfo[_SplatRun[idx]].y`

`_LodEnabled == 0`（既存の ply / VDGS）は 4 バイトのダミーを束ねて読まない。**既存シーンの
ピクセルは変わらない**（`compare_renders.py` で焼き直し前後を比較する）。

シェーダーが変わるので **Windows で D3D12 版、Mac で Metal 版を両方焼く**。バンドルの
サイズ基準は CLAUDE.md（D3D12 約 1.5 MB、Metal 約 437 KB）。

## 4. 段の選択（`LodSelector.cs`、UnityEngine 非依存）

PlayCanvas の現在の既定「距離モード + 予算」を写す。入力は葉の AABB、run 表、カメラの
オブジェクト空間位置、`lodDistance`、`lodBudget`。出力は葉ごとの段。

1. 葉ごとに **AABB の最近点までの距離** `d`（中心ではない。大きな葉の中にいるとき中心
   距離は誤る — PlayCanvas #8138）。オブジェクト空間の距離に `lossyScale` を掛けて
   メートルにする
2. `level = d < D ? 0 : min(floor(log2(d / D)) + 1, LevelCount - 1)`（`D = lodDistance`）。既定
   `lodDistance = 10`（葉の extent 中央値 14 m から。level 0 が 10 m 以内、1 が 20 m、
   2 が 40 m、3 が 80 m、4 がそれ以遠）
3. 選んだ段の `count` の合計が `lodBudget`（既定 3,000,000、60fps の境目）を超えたら、
   **遠い葉から順に 1 段ずつ粗く**して収まるまで繰り返す。全部が最粗でも超えるなら
   そのまま（切らない。何が起きたかはログに出す）
4. 選んだ段が葉に無いとき：最粗の段より粗い側を求めたなら**描かない**（active 0。
   PlayCanvas #9326 と同じ — 数 splat のために最粗を出さない）。それ以外は**最も近い粗い側の
   既存段**に丸める（最細より細い側は最細に）。1 つの葉が同時に 2 段 active になることは無い
5. ヒステリシス：段 `p` の帯は `[lo, hi)`、`lo = p == 0 ? 0 : D·2^(p-1)`、`hi = D·2^p`
   （最粗は `hi = ∞`）。前回の段 `p` から変えるのは `d < lo·0.9` か `d ≥ hi·1.1` のときだけ
6. `SplatRenderer` が 10 フレームに 1 回、飛行カメラ（Preview 以外で最初に来たもの）で
   評価し、変わったときだけ `_RunInfo` を書く。True Lens の 6 カメラは同じ結果を使う

## 5. 操作面

- `placement.json` に `lodDistance`（m）と `lodBudget`（splats）。無ければ既定
- Tweak にダイアル 2 つ。`web/` は同じコンポーネントが companion にも出るので片方だけ
  直る事故は起きない
- `/api/status` に段ごとの active splat 数と葉の数
- `vdgs-perf.log` に段ごとの active 数を足す（ベンチで「何が見えていたか」が残る）

## 6. 検証とベンチ

「このプロジェクトで目視レビューは一度も欠陥を捕まえていない」（CLAUDE.md）。全部数値で。

| 層 | 方法 | 置き場 |
|---|---|---|
| VP8L | `dwebp -pam` の出力とバイト一致。fixture は `tools/make_vp8l_fixtures.sh` が `cwebp -lossless -z 0..9` と `-exact` で作る合成画像（predictor 全モード、palette 2/4/16/256 色、color cache あり/なし、大きい画像で LZ77 の遠距離）+ 実チャンク `0_0/` の 7 枚 | `src/VDGS.Tests/fixtures/vp8l/` |
| SOG 復号 | `npx @playcanvas/splat-transform 0_0/meta.json chunk.ply` の ply を読み、位置（16bit log の量子化幅以内）、scale、quat（角度差）、色、opacity、SH（パレット経由の実値）を比較 | `src/VDGS.Tests/SogChunkTests.cs` |
| SsogIndex | 7a7bfaea の `lod-meta.json` で葉 2,324 / run 11,612 / 段ごとの合計が `counts[]` と一致 | 同上 |
| SplatWriter | 切り出し前後で既存 ply の `pos.bin` / `other.bin` / `color` がバイト一致 | xunit（ply の小さい fixture） |
| LodSelector | 近い葉が level 0、距離で単調、予算を守る、ヒステリシスで揺れない、無い段は active 0 | xunit |
| 既存シーン | 焼き直したバンドルで nelson の 1 視点を `compare_renders.py`。差ゼロ | 実機 |
| ベンチ | `tools/fetch_ssog.py` で 7a7bfaea を落とし、`VDGS_BENCH_INSIDE=1 bash tools/bench-win.sh`。比較対象は `splat-transform -L 2` の単段 ply（2.24M）。目標は RTX 3060 で予算 3M のまま 60fps。初回スポーンの復号時間、段ごとの active 数を記録 | 実機、docs/performance.ja.md に追記 |

## 7. 範囲外（findings）

実装後の実測で分かったもの（2026-09-17）：

- **常駐分のソート費用**：RTX 3060 で LOD 既定 15.4 ms に対し、level 2 だけの ply（2.24M）は
  8.1 ms。距離パスと radix ソートが常駐 17.3M 全部に走るため。可視分だけを詰めてソートする
- **スポーンで 16 秒止まる**：RTX 3060 機のゲーム内で 16.1 秒（decode 7.6・pack 8.3）。
  エディタ Debug で RTX 3060 機 16.6 秒、M1 Max 67 秒（Release 39 秒）。
  見積もりの 1〜3 秒から大きく外れた。Mac でスレッドプールの最小数を上げても変わらない。
  疑い：`PackRotation` の splat ごとの `new float[4]`、VP8L のビット読みが Mono で遅いこと。
  ゲーム内でもエディタと同じなので、Release コードの差ではない
- **復号済み float 配列を全チャンク分抱えてから詰める**のでピーク約 2.3 GB（見積もり、未計測）
- **予算 3M だとこのシーンは level 0 が 1 本も選ばれない**（原点付近 10 m に密集）。既定値の
  見直しは実機で飛んでから
- **Mac エディタの描画時間は判断に使えない**（同構成で平均 27〜72 ms、全葉 level 0 は 241 ms。RTX 3060 は 23 ms）

設計時に範囲外としたもの：

- 復号結果のディスクキャッシュ（初回スポーンが遅ければ）
- チャンクのストリーミング（VRAM を予算分だけに）
- companion のカタログから ssog を落とす
- SOG の量子化を GPU 側で保つ形式（VRAM 半減、新形式、両 OS 再焼き）
- `CSCalcViewData` を可視分だけの indirect dispatch にする（LOD 無しでも効く）
- `errors[]` を使う選択モード
- コリジョン：ssog から `collision.bin` を焼く手順（`splat-transform -L 4` で ply にして
  既存の焼き方を使えば動くはず。docs/SCENES.ja.md に 1 段落）

## 捨てた案

- SH の 36 パレット：索引を 32 bit に広げる新形式（両 OS 再焼き、`other.bin` の stride
  変更）より、LOD で足す `_SplatRun` に相乗りする run 基底加算
- LOD ゲート：run を 256 整列に詰めて `_SplatChunkRadius` と同じ表で引く（11,612 run で
  padding が最大 3M）より、splat ごと run id
- 形式：SOG の量子化を GPU 側で保つより PlyLoader 互換（実績あり、焼いた High との差 7%）
