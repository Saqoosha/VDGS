# build/testdata の中身

30 本の ply、9.6 GB。名前に `-aligned` `-final` `-nocrop` `-full` `-crop` `-mirrorY`
`-up-manual` が付いているが、**どれがどの変換の結果かの記録が無い**。名前は証拠にならない。

同じキャプチャの版は splat 数が一致するので、それで束ねられる。変換の内容は bounds が語る。

```bash
python3 tools/inventory_ply.py build/testdata/*.ply
```

## 実測（2026-08-19）

splat 数でグループ化。同じ数 = 同じ点群を別の変換にかけたもの。

```
                          splats      SH   サイズ   min                      max
nelson-full            8,759,558     sh0   490 MB  [-253.26 -118.78 -456.65] [ 464.55    8.27  294.40]

utlida-full            4,003,388    sh45   945 MB  [-321.70 -206.27 -291.12] [ 194.78   54.14  259.04]
utlida-full-s5         4,001,829    sh45   944 MB  [-321.70 -105.55 -291.12] [ 190.44   54.14  259.04]

drjohnson              3,177,554    sh45   788 MB  [ -18.84  -23.29   -8.78] [  16.70   15.13    8.27]
drjohnson-aligned      3,177,554    sh45   750 MB  [ -23.19  -15.59  -14.13] [  15.10   11.84   13.33]
drjohnson-final        3,177,554    sh45   750 MB  [ -23.19  -11.83  -13.33] [  15.10   15.60   14.13]

calico-lod3            2,401,279     sh0   135 MB  [ -69.66 -150.69 -142.71] [ 396.16    1.62  263.11]
textilni-lod3          2,320,155    sh45   548 MB  [ -54.38  -13.09  -85.38] [  40.83    9.20   13.81]
drjohnson-crop         2,307,523    sh45   572 MB  [  -4.29   -2.37   -5.77] [   3.27    2.35    4.89]
nelson-lod2            2,171,895     sh0   122 MB  [-253.27 -118.18 -456.65] [ 464.55    8.28  294.44]

utlida-lod1            2,001,694    sh45   472 MB  [-286.90 -204.36 -238.75] [ 171.82   50.29  259.04]
utlida-lod1-s5         2,000,640    sh45   472 MB  [-262.79  -76.41 -124.00] [ 124.69   31.67  259.04]

playroom               1,916,379    sh45   475 MB  [ -13.89  -17.92  -23.83] [  11.51    8.73   10.36]
playroom-aligned       1,916,379    sh45   452 MB  [ -13.73  -22.66  -19.44] [  11.99    4.49   12.25]
playroom-full          1,916,379    sh45   452 MB  [ -13.73   -4.49  -12.25] [  11.99   22.66   19.44]
playroom-nocrop        1,916,379    sh45   452 MB  [  -4.39   -1.44   -3.92] [   3.83    7.25    6.22]

bonsai-mirror          1,244,819    sh45   309 MB  [ -17.88   -6.84  -28.13] [  26.95   36.73   20.35]
bonsai-mirrorY         1,244,819    sh45   309 MB  [ -26.95  -13.01  -28.13] [  17.88   30.56   20.35]
bonsai30k              1,244,819    sh45   309 MB  [ -26.95  -17.07  -28.13] [  17.88   26.50   20.35]

bonsai                 1,157,141    sh45   287 MB  [ -26.65  -16.87  -28.15] [  17.88   26.56   20.35]
bonsai2                1,157,141    sh45   273 MB  [ -25.91  -12.12  -30.97] [  32.71   12.82   21.49]
bonsai2-aligned        1,157,141    sh45   273 MB  [ -23.22  -12.34  -35.42] [  25.29   12.89   20.19]

bonsai30k-crop           934,442    sh45   232 MB  [ -11.82   -6.18   -5.48] [   9.06   10.55   12.65]

playroom-final           150,000    sh45    35 MB  [  -1.71   -0.21   -2.35] [   1.37    3.12    1.62]
playroom-up-manual       150,000    sh45    35 MB  [  -1.71   -3.09   -1.62] [   1.37    0.23    2.35]

luigi                     14,526     sh0   1.0 MB  [  -0.55   -0.66   -0.27] [   0.54    0.65    0.27]
luigi-fixed               14,526     sh0   1.0 MB  [  -0.55   -0.03   -0.27] [   0.54    1.28    0.27]

testcube                     640    sh45   0.2 MB  [   0.00    0.00    0.00] [  10.00   10.00   10.00]
orient                        78    sh45   0.0 MB  [  -7.20    0.00    0.00] [   6.00    1.75    8.00]
orient-mirrored               78    sh45   0.0 MB  [  -7.20   -0.27    0.00] [   6.00    1.48    8.00]
```

## 読み取れた関係

**`-final` は `-aligned` の X 軸 180 度回転。** Y と Z の符号が両方反転している：

```
drjohnson-aligned   y [-15.59, 11.84]   z [-14.13, 13.33]
drjohnson-final     y [-11.83, 15.60]   z [-13.33, 14.13]
```

`playroom-final` と `playroom-up-manual` も同じ関係で、どちらも 150,000 splats
（playroom 系の 1,916,379 から間引かれている）。

**`-up-manual` の出自は不明。** 名前は「手で上向きに直した」と読めるが、Saqoosha は作った
覚えがないと言っている。**測定で言えるのは「`-final` の X 軸 180 度回転」だけ**で、それは
純粋な回転なので、元さえ分かれば再生成できる。**名前からの推測を根拠にしない。**

**`-aligned` は手作業**（Saqoosha が SuperSplat 上で床の向きを合わせたもの）なので、
この系列の出発点は再生成できない。

**`playroom-nocrop` だけ寸法が 1/3。** 他の playroom 版が 25 単位級なのに対し 8 単位級で、
部屋として実寸に近い。`reprocess.sh` が `-aligned` ではなくこれを使う理由がここにある。

**`luigi-fixed` は `luigi` の Y 反転**（`[-0.66, 0.65]` が `[-0.03, 1.28]`、床が y=0 に来ている）。

**`-s5` は `--max-sigma 5` で巨大ガウシアンを落とした版**（別セッションの作業、A/B 測定中）。
splat 数はほぼ同じで bounds だけ縮む。utlida-full は 4,003,388 から 1,559 本減っただけで
y の下端が -206 から -105 になる。

## いま実際に使われているのは 6 本

```
bonsai2-aligned.ply     260 MB   reprocess.sh
playroom-nocrop.ply     431 MB   reprocess.sh + preview.sh
drjohnson-aligned.ply   715 MB   reprocess.sh + preview.sh
luigi.ply               0.9 MB   reprocess.sh + preview.sh
calico-lod3.ply         128 MB   preview.sh
textilni-lod3.ply       522 MB   preview.sh
```

残り 24 本、約 7.5 GB はどのツールからも参照されていない。ただし**消す前に確認が要る**：

- **再取得できるもの** — `bonsai` `drjohnson` `playroom` の素の版は Hugging Face から
  curl で戻せる（README 参照）
- **戻らないもの（手作業）** — **`-aligned` は Saqoosha が SuperSplat 上で床の向きを手で
  合わせたもの**。再生成できない
- **出自不明** — `playroom-up-manual` `playroom-final` `bonsai-mirror` `bonsai-mirrorY`
  など。誰がどの引数で作ったか分からない。**名前から推測しない**
- **死んだ手法の残骸** — `-crop` 系（`drjohnson-crop` 572 MB、`bonsai30k-crop` 232 MB）。
  パーセンタイル切りは**手法ごと捨てられていて**、`tools/crop_ply.py` も削除済み
  （AGENTS.md「crop はしない」）。使っていないと Saqoosha が確認済み
- **再生成できるかもしれないもの** — `-mirror` 系は `align_ply.py` で作り直せるはずだが、
  **どの引数で作られたかの記録が無い**ので、いま消すと同じものが戻る保証はない
- **使用中** — `utlida-*-s5` は別セッションが測定に使っている

### `-aligned` が手作業だと分かって辻褄が合ったこと

SuperSplat の書き出しは Y が反転する。つまり**エディタ上で床を合わせても、書き出した時点で
上下がひっくり返る**。だから `-aligned` は正しく作られていて、なお `--mirror y` が要る。

`updir.py` の判定がそのまま裏付けになっている：

```
drjohnson-aligned   as-is 97.5%  ->  MIRROR が必要
bonsai2-aligned     as-is 97.5%  ->  MIRROR が必要
playroom-nocrop     as-is  2.5%  ->  ミラー不要
```

`playroom-nocrop` だけ違うのは、SuperSplat 書き出しではないか、別の経路を通っているため。
**そこはまだ分かっていない。**

## 構成（2026-08-19 に実施）

```
build/testdata/            9.6 GB -> 7.7 GB
  scenes/     6 本  2.0 GB   ツールが参照する版。1 キャプチャ 1 本
  raw/        5 本  2.8 GB   素の配布物。再取得できる
  work/       8 本  2.9 GB   実験の中間生成物と手作業
  fixtures/   3 本  208 KB   testcube, orient
```

`bash tools/organize_testdata.sh` で再実行できる（冪等）。

**移動だけで、何も削除していない。** そして**旧パスにはシンボリックリンクを残した**ので、
別セッションが `build/testdata/utlida-full-s5.ply` を測定に使っている最中でも壊れない。
参照が無くなったことを確認してからリンクを掃除する。

`reprocess.sh` と `preview.sh` は `scenes/` を直接見るようにした。

### 削除した 8 本（2026-08-19、約 2.5 GB）

**死んだ手法の残骸** — パーセンタイル切りは手法ごと捨てられ、`tools/crop_ply.py` も削除済み：

```
work/drjohnson-crop.ply    546 MB
work/bonsai30k-crop.ply    221 MB
```

**別ファイルから完全に再生成できるもの** — 点を直接突き合わせて残差 0.00000 を確認した：

```
削除したもの              = 元ファイル            変換               復元コマンド
drjohnson-final    750 MB   drjohnson-aligned   sign (1,-1,-1)   --rotate 180,0,0
playroom-full      452 MB   playroom-aligned    sign (1,-1,-1)   --rotate 180,0,0
bonsai-mirror      309 MB   bonsai30k           sign (-1, 1, 1)  --mirror x
bonsai-mirrorY     309 MB   bonsai30k           sign ( 1,-1, 1)  --mirror y
playroom-up-manual  35 MB   playroom-final      sign (1,-1,-1)   --rotate 180,0,0
luigi-fixed          1 MB   luigi               sign (1,-1,-1)   --rotate 180,0,0
```

いずれも `python3 tools/align_ply.py <元> <出力> <変換>` で戻せる。平行移動が付くものが
あるが（`luigi-fixed` は +0.619、`bonsai-mirror` は +10.234）、これは `--rotate` が床を
y=0 に落とす副作用によるもの。

### 派生の判定は点で行う。箱では足りない

最初にバウンディングボックスで判定して、`drjohnson-final` を「`drjohnson-aligned` の平行
移動」と結論した。**実際は Y と Z の両方が反転していて、どちらも同じ箱を作る。**

**箱は反射と平行移動を区別できない。** このプロジェクトが座標系の判定で何度も踏んだ罠と
同じもので、判定対象がキャプチャからファイル系譜に変わっただけ。

正しい手順は、両方から同じ行番号のサンプルを取り、候補の符号と平行移動を当てて、
**点そのものが重なるか**を見ること。変換ツールは行の順序を保つので、行対行の比較が
そのまま成立する。

### まだ足りないもの

**どの変換で作ったかがファイルに書かれていない。** `-aligned` と `-final` と `-up-manual`
の区別は、いまも `tools/inventory_ply.py` で bounds を突き合わせて推測するしかない。
`scenes/<name>.json` に元ファイルと `align_ply.py` の引数を残す案は未実装。

削除の判断も保留のまま。**再取得できるもの / 引数さえ分かれば再生成できるもの / 手作業で
戻らないもの**の 3 分類は上に書いた通り。
