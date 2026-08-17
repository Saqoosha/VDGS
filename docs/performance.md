# 大規模 GS シーンの描画コスト

drjohnson（317 万 splats）で RTX 3060 のファンが唸る問題の調査。**結論から言うと、
コストの 81% は球面調和（SH）データで、それは既に実装済みの機能でほぼ消せる。**

## 実測（2026-08-18、RTX 3060 / 120Hz / VSync ON）

`<game>/vdgs-perf.log` から：

| シーン | splats | 形式 | bytes/splat | GPU バッファ | フレーム時間 | fps |
|---|---:|---|---:|---:|---:|---:|
| playroom | 1,916,379 | Norm16 | 84 | 161 MB | 12.84 ms | 78 |
| bonsai | 1,157,141 | Float32 | 236 | 273 MB | 17.24 ms | 58 |
| drjohnson | 3,177,554 | Float32 | 236 | **750 MB** | 28.30 ms | 35 |
| （splat なし） | 0 | — | — | — | 8.33 ms | 120 |

drjohnson は最悪 43.9 ms（22.8 fps）まで落ちる。

## コストは splat 数ではなく帯域で決まる

**playroom は bonsai より splat が 66% 多いのに 26% 速い。** 違いは形式だけ。

ゲーム単体の 8.33 ms を引いて splat 分だけ取り出すと：

```
playroom    2.35 ms / 100万splat    84 B/splat
bonsai      7.70 ms / 100万splat   236 B/splat
drjohnson   6.28 ms / 100万splat   236 B/splat
```

バイト数の比 **236 / 84 = 2.8 倍**に対し、コスト比は **2.7〜3.3 倍**。ほぼ一致する。

3 点のフィットなので厳密な法則ではない（ソート段は splat 数に比例し、形式には依存しない）。
それでも「splat を減らす」より先に「1 splat あたりのバイトを減らす」ほうが効く、という
方向は明確。

## 内訳：SH が 81%

```
drjohnson  750 MB
  sh.bin     610 MB   ← 81%
  other.bin   51 MB
  color.bin   51 MB
  pos.bin     38 MB
```

SH は degree 3 で 15 係数 × 3 チャンネル × 4 バイト = 180 → 16 バイト境界で **192 B/splat**。
位置（12 B）の 16 倍を、見る角度による色の変化だけに使っている。

品質レベル別の 1 splat あたり：

| レベル | pos | other | color | SH | 合計 |
|---|---:|---:|---:|---:|---:|
| VeryHigh (Float32) | 12 | 16 | 16 | 192 | **236** |
| High (Norm16) | 6 | 10 | 8 | 96 | 120 |
| Medium (Norm11) | 4 | 8 | 4 | 60 | 76 |
| Low (Norm6) | 2 | 6 | 4 | 32 | 44 |

## 一番効く手：SH のクラスタリング（実装済み・未使用）

ランタイムは既にパレット方式の SH を読める：

```csharp
public enum SHFormat {
    Float32 = 0, Float16 = 1, Norm11 = 2, Norm6 = 3,
    Cluster64k = 4, Cluster32k = 5, Cluster16k = 6, Cluster8k = 7, Cluster4k = 8
}
```

シェーダー側も対応済みで、`shFormat > VECTOR_FMT_6` のとき各 splat は **2 バイトのパレット
索引**だけを持ち、SH 本体は全 splat で共有される。

drjohnson を `Cluster16k` にした場合：

```
索引    3,177,554 × 2 B  =  6.4 MB
パレット   16,384 × 192 B =  3.1 MB
                    合計    9.5 MB   ← 610 MB から 64 倍減
```

### 実際に焼いた結果（実測）

`-vdgsShFormat` を `PlyExporter` に足して drjohnson を焼き直した：

```
                 splats      形式         合計      B/splat
drjohnson     3,177,554   sh=Float32     750 MB      236
drjohnson-shc 3,177,554   sh=Cluster16k  148 MB       46    ← 5.1 倍減
   sh.bin      610 MB → 1.5 MB
   other.bin    51 MB →  57 MB   （2 バイトのパレット索引ぶん増える）
```

**見た目は Mac のビューアで比較して IoU 0.9934、平均差 1.58/255（0.6%）。ほぼ同一。**

フレーム時間は上のコストモデルの予測で **12.2 ms ≒ 82 fps**（現状 28.3 ms / 35 fps）。
これは**まだ実機で測っていない**。`w` の `vdgs/drjohnson-shc` に配置済みなので、出して
`vdgs-perf.log` を見れば確定する。

変換には k-means が入るので Mac 側で 10 分ほどかかった。実行時のコストではない。

### 途中で踏んだ罠：`posFormat` は座標空間を語らない

クラスタ化 SH で焼くと `chunk.bin` が出力される。そして **`posFormat: Float32` のまま
位置が 0..1 のチャンク相対**になる：

```
drjohnson       pos range  -23.186 .. 15.099   ← 絶対座標
drjohnson-shc   pos range    0.000 ..  1.000   ← チャンク相対
```

`Float32` は**格納幅**であって、絶対座標という意味ではない。この session で最初に入れた
ガードは「Float32 なら chunk は不要」と決め打ちしていたため、chunk を捨てて**シーン全体を
原点付近の塊に潰した**。デブリを防ぐために書いたガードが、同じくらい静かに壊していた。

正しい判定材料は変換側しか持っていないので、`meta.json` に `chunkCount` を書き、
ロード側はそれと突き合わせる（不一致は即エラー、0 ならファイルが存在してはいけない）。

## 次に効く手

### splat を減らす（要ツール）

drjohnson の 317 万は一部屋にしては多い。文献では **90% 以上の枝刈りでも見た目がほぼ
変わらない**と報告されている：

- [Speedy-Splat](https://speedysplat.github.io/)（CVPR 2025）— 正確なタイル交差判定で
  ラスタライズを 2 倍、枝刈り込みで通算 6 倍以上。**枝刈りは訓練時**なので、既存の ply に
  そのまま適用はできない
- [REFINE](https://arxiv.org/html/2606.09074) — レンダリング不要の重要度評価による枝刈り。
  訓練済みモデルに後から掛けられる系統
- [3DGS.zip サーベイ](https://onlinelibrary.wiley.com/doi/10.1111/cgf.70078?af=R)（CGF 2025）
  — 圧縮手法の全体像。どれがどれだけ効くかの比較表がある

「不透明度が低い」「画面上で小さい」splat を落とすだけの単純な枝刈りでも、まず試す価値が
ある。`tools/` に後処理として書ける。

### ソート頻度を下げる（1 行）

`SplatRenderer.m_SortNthFrame`（既定 1）を 2 にすると GPU radix sort が 1 フレームおきに
なる。317 万キーのソートは安くない。速く動くとブレンド順の破綻が見えるはずなので、
**実際に飛んで確認が要る**。効果は未計測。

### LOD（重い）

距離に応じて splat 数を落とす階層表現。大規模屋外向けの本命だが、一部屋には過剰。

- [Hierarchical 3D Gaussians](https://repo-sam.inria.fr/fungraph/hierarchical-3d-gaussians/)（INRIA, SIGGRAPH 2024）
- [LODGE](https://arxiv.org/abs/2505.23158)（NeurIPS 2025）— 距離ベースの LOD 選択に加え、
  空間チャンク単位の動的ロードで GPU メモリも削る

### 視錐台カリング（未調査）

素の VeryHigh は chunk を出力しないので、チャンク単位で視野外を捨てる余地が構造的に
無かった。`Cluster16k` で焼くと chunk が付くので、その余地は生まれている。ただし
**upstream の実装が実際にチャンク単位で捨てているかは未確認**。捨てていないなら、
256 splat ごとの境界ボックスは既にあるので追加は難しくないはず。

## 推奨する順番

1. ~~`-vdgsShFormat` を足して `Cluster16k` で焼き直す~~ — **完了。** `w` の
   `vdgs/drjohnson-shc` に配置済み
2. **実機で `drjohnson-shc` を出して `vdgs-perf.log` を見る。** 予測 12.2 ms / 82 fps。
   ここが次の一手
3. 足りなければ単純な枝刈り（低不透明度・微小 splat）を `tools/` に足す
4. `m_SortNthFrame = 2` を飛びながら試す（安いが、破綻するかは目で見るしかない）

**2 の実測より先に 3 や 4 に手を出さないこと。** 帯域が支配的だという測定があるので、
まず帯域を削った結果を見るのが筋。
