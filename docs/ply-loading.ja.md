# .ply をそのまま読む

*[English](ply-loading.md)*

`<game>/vdgs/foo.ply` を置けば飛べる。Python も Unity も要らない。

変換済みディレクトリ（`<name>/meta.json` + 5 バイナリ）も従来どおり使える。**同名なら
ディレクトリが勝つ**（既にパック済みなので、ply を読み直すぶん遅くなるだけ）。

配置は ply の隣に `<name>.placement.json` として保存される。

## 実行時に読める理由

変換の 10 分は**まるごと k-means、つまり SH のパレット圧縮だった**。ply のボディは固定長行の
配列なので、読むのは実質 memcpy。

実測（RTX 3060 の本番機、`bash tools/bench-win.sh <name>` がロード時間も出す）：

```
nelson       2,171,895 splats  121.6 MB   header 4 ms  read  99 ms  decode  870 ms   0.97 s
nelson-full  8,759,558 splats  490.5 MB   header 2 ms  read 265 ms  decode 3078 ms   3.34 s   (M1 Max)
```

**読み込みは 100〜265 ms しかない。時間は全部 decode。** そこは splat ごとに独立なので
コアに分割してある（`Parallel.For`）。

### 確保をゼロにするまで、並列化は逆効果だった

最初は `Put` が `BitConverter.GetBytes` を呼んでいて、**1 splat あたり 10 回以上
`byte[4]` を確保**していた。217 万 splats で 2000 万回超。

単体でも重いが、**並列にすると 2 倍遅くなった**（3154 → 6334 ms）。スレッドが decode では
なく Mono のアロケータを奪い合うため。共用体（`StructLayout(Explicit)`）で直接書くように
したら **870 ms**。

```
確保あり・単スレッド   3154 ms
確保あり・並列         6334 ms   ← 並列化が裏目
確保なし・並列          870 ms
```

**「並列にしたら遅くなった」を並列化の限界と読まないこと。** アロケータが詰まっていた。

## 出力フォーマットと速度

位置とスケールは Float32、色と SH は half。**chunk を使わない**のは、Float16 より下の
形式が「chunk の min/max を通して初めて意味を持つ 0..1 の重み」だからで、採用すると
Morton 並べ替えと chunk 境界の計算を引き込む。

RTX 3060、drjohnson 317 万、シーン内部・画角 120°、カリング on：

```
Float32 全部        236 B/splat   14.42 ms
このローダー        132 B/splat    9.98 ms
High（焼いた最速）   84 B/splat    9.34 ms
```

**焼いた最速版との差は 0.64 ms（7%）。** 完全な High パッキング（Norm16 + chunk +
Morton）は符号化が 5 種類増えるので、この差に見合うかは疑問。

画質は luigi で **0.0162/255**（Float32 の変換器と比較）。half 化による劣化は測定に出ない。

## 3 つの罠（どれも静かに間違える）

### プロパティは名前で引く。オフセット決め打ちは即死する

**ply の属性構成は固定ではない。** 手元の実データ：

| 出所 | 構成 |
|---|---|
| INRIA 標準 | `x,y,z, nx,ny,nz, f_dc_0..2, f_rest_0..44, opacity, scale_0..2, rot_0..3`（62 float） |
| `drjohnson-aligned.ply` | 法線なし（59 float） |
| `splat-transform` 出力 | `x,y,z, rot_0..3, scale_0..2, opacity, f_dc_0..2 [, f_rest_0..44]`（14 or 59 float） |

**`splat-transform` は rot が scale より前、`f_dc` が最後**で、INRIA とまるで違う。
`PlyLoader` はヘッダのプロパティ一覧を読んで名前で引く。

### 色テクスチャは 16×16 タイル内で Morton 並び

`SplatIndexToPixelIndex` の並びに合わせる必要がある。線形に置くと **16×16 ブロック単位で
色とアルファが混ざる** — 形は完璧なまま色だけ崩れるので、幾何のバグに見えない。

### `f_rest` はチャンネルごと、シェーダーは係数ごと

ply は R を 15 個、G を 15 個、B を 15 個の順で並べる。シェーダーは `sh1.rgb, sh2.rgb, …`
と読む。**転置しないと全バンドの色がずれる。**

## SH を持たないキャプチャ

XGRIDS PortalCam のような LiDAR ハンドヘルドは **SH degree 0** を出す。`f_rest_*` が
1 つも無い ply になる（`luigi.ply` も同じ）。

`_SplatSHOrder == 0` のときシェーダーは SH の読み出しごと飛ばし、ローダーは 16 バイトしか
確保しない。**効果は誤差ではない**：

```
nelson-lod2 (217 万 splats)   sh.bin  417 MB → 16 バイト、シーン全体 92 MB（ply は 116 MB）
nelson-full (876 万 splats)   1.68 GB の単一配列を確保せずに済む
```

1.68 GB は `int.MaxValue` の 78%。**876 万 splats が通るのはこれのおかげ**（実測：ピーク
RSS 1.29 GB、出力 368 MB）。

## 検証のしかた

変換器と**同じ変換**で比べる。`PlyLoader` は既定で Y 鏡映するので、変換器の生出力と
比べるときは切ること：

```bash
# 生の ply を変換器で焼く（鏡映も床移動もしない）
Unity -batchmode -quit -nographics -projectPath unity/VDGSConverter \
  -executeMethod PlyExporter.Run -vdgsInput <raw>.ply -vdgsOutput build/splats/<x> -vdgsQuality VeryHigh

# 同じカメラで両方描いて引き算する（-vdgsPlyNoMirror で条件を揃える）
Unity ... -executeMethod RenderCompare.Run -vdgsScene <raw>.ply -vdgsPlyNoMirror 1 ...
```

**一度これを揃え損ねて平均差 27.9/255 を出し、ローダーのバグだと思い込んだ。**
実際は鏡映が効いていただけだった。

絵で比べても「違う」としか分からない。**バッファをバイト単位で突き合わせる**と、どれが
違うかが言える（`PlyDump.Run` が 5 ファイル + meta.json を書き出す）。luigi での結果：

```
sh.bin        完全一致
scale         完全一致（差 0）
色 + アルファ  完全一致（rgb 差 0.000000）
位置          集合として一致（変換器が空間順に並べ替えるので順列違い）
回転          ply に対する誤差 平均 0.0748°（変換器は 0.0993°）
```

`PlyDump.Run` は `meta.json` も書くので、**ローダーをオフラインの変換器として使える**
（変換済みディレクトリが欲しいとき、Unity 2022 も Python も要らない）。
