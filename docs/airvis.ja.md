# AirVis Studio で作る

JDL-2026-R5 と FDF はどちらも AirVis Studio（Rockland Technologies、MSIX）で再構成した。
**中身は COLMAP（大域 mapper）＋ MCMC で、新規性は無い。** 差がつくのは設定で、
その設定は**アプリ自身が JSON に全部書き残す** —— 推測しないで読む：

```
SFM/airvisstudio-sfm.json            登録数、大域 mapper の可否、警告
SFM/airvisstudio-sfm-features.json   入力パス、枚数、球面モード、マスク数、Vulkan
splats/<n>/airvisstudio-splat.json   iterations, maxSplats, useMcmc, finalLoss, splatCount
splats/splat-trainer.log             反復ごとの loss / visible / splats
```

良い走行 2 本に共通するのは `backend colmap` / `exhaustiveMatchingEnabled true` /
`useMcmc true` / `trainingPreset conservative` / Vulkan 特徴抽出（456 枚が 32 秒）。
**`globalMappingEnabled` は共通しない** —— JDL の DJI 走行は true、FDF は false。
どちらも良い結果なので、大域 mapper は必須ではない（下記）。

**`Super User` を入れると advanced 設定が開き、image と mixed フォルダの静止画を全部使う。**
動画と写真を 1 つの再構成に入れる道はここにある（未検証）。ただし**並び順が逐次照合の
ペアを決める**ので、混ぜるなら接頭辞で系統を分ける。

## 360 動画は「使えるが、鋭くならない」

AirVis は `.mp4` のエクイレクタングラーを受ける。`Prepare Images` で
**1 フレームを 90°×90° のピンホール面に切り、リグとして登録する**
（`sphericalSfmMode: perspectiveRig`）。**これは正しい方式** —— 同じフレームの各面は
カメラ中心が同一で視差ゼロなので、画像フォルダとして渡すと COLMAP が
`PLANAR_OR_PANORAMIC` と判定してマッチグラフを壊す。リグなら壊れない。

`Views` の意味（ツールチップより）：

| | 中身 | 使いどころ |
|---|---|---|
| 8 | 水平リングだけ | 天井と床に情報が無い屋内 |
| 12 | 水平リング＋前方くさび（上下は yaw 0° と 45° だけ） | 既定の中間 |
| 16 | 水平リング＋上 4・下 4 | 屋内、細かい天井、狭い場所 |

**地面の被覆が狙いなら 8 を選んではいけない。** 上向きと一緒に**下向きも消える**。
実測で下向き（`pitch-060`）は空 2.8%＝地面だけを見ていて、いちばん価値が高い。
**そして 8 にしても空は減らない** —— 水平リング 8 本の平均が空 45.8% で、12 本全部の
46.1% とほぼ同じ。地上の水平ビューは半分が空。

**解像度は上がらない。** 仮想ビューは 1600×1600 / f=800（画角 90°）で **17.8 px/度**。
DJI は 3832×2155 / f=3285 で **63.3 px/度**。**3.6 倍粗い。** 360 カメラは同じ画素数を
球全体に配るので、これはカメラの物理であって設定の問題ではない。5760×2880 の
エクイレクタングラーは 16 px/度しかなく、そこから切った面は元より細かくならない。

**だから 360 は解像度の役ではなく、地面の被覆の役。** DJI（鋭い・上空）と組み合わせる
前提で使う。

## 空を消さないと opacity が崩壊する

**これが「姿勢は良いのに結果が悪い」の正体。** 実測：

```
              splats      生存     不透明度 p50   最長軸 p50   finalLoss
360 単独    4,999,456     7.6%      0.0522       0.06842      0.119186
DJI 単独    4,999,846    89.4%      0.9101       0.00098      0.022643
FDF         4,999,971    97.3%      0.9994       0.00264      0.144743
```

**93% が死に、生き残りは 70 倍大きくて透明** ＝ 霧。`splat-trainer.log` の `visible` が
反復ごとに 70 倍振れるのも同じ理由（空だけのビューと地面のビューが混ざる）。

**地上の全天球は画面の 46% が空**（12 ビューの実測。上向きは 86〜96%）。3DGS は空を
説明しようとして大きく薄いガウシアンを作る。**AirVis が自動生成するマスクは自撮り棒と
マウントだけで、空は隠さない**（白 96.8〜100%）。

**`maxSplats` も効くが、境目は学習器ごとに違い、trainer をまたいで比べてはいけない。**
疎点群に対する比で見ると：

```
AirVis   FDF   859,591 点 -> 5M      5.8 倍   生存 97.3%
AirVis   360   785,480 点 -> 5M      6.4 倍   生存  7.6%   （空マスク無し。cap だけの責任ではない）
AirVis   ゲート  44,695 点 -> 528k   11.8 倍   良品
gsplat   360   249,727 点 -> 1M      4.0 倍   生存 68.1%
gsplat   360   249,727 点 -> 3M     12.0 倍   生存  0.2%   崩壊
```

**gsplat は 2〜4 倍に置く。** 崩壊の機構は正則化が固定なこと —— splat が増えるほど 1 個
あたりの光学的圧力は薄まるのに `opacity_reg 0.01` は据え置きで、薄い側へ雪崩れる。
**AirVis のアプリはそれでも 11.8 倍で通る**（`scale_reg` / `opacity_reg` 0.01 固定 +
Vulkan trainer）ので、大きい cap が要るなら trainer を単体で叩く（下）。AirVis の自動 cap
（`cap_source=auto-image-count`）は画像数に比例せず、6,732 枚で 3.68M。

## 大域 mapper は枚数で落ちる。落ちても損はしない

128 GB / 空き 100 GB の機械で、**9,088 枚（16 ビュー・2fps）でメモリ溢れ。6,816 枚
（12 ビュー）は通る。** 切り分けは画像数で、境目はその間。

**切っても質は落ちない見込み。** いちばん良い結果（FDF）は `globalMappingEnabled: false`
＝ incremental mapper で出ている。**大域を使ったのは JDL の DJI 走行のほうだけ。**

**ただし副作用がある。** 大域を切ると `exhaustiveMatchingEnabled` も false になる。良い走行
2 本は両方 exhaustive を使っていたので、**そこは意識して戻す価値がある**（Caspar があれば払える）。

減らす順番は、目的を壊さないものから：

```
FPS 2 -> 1      枚数が半分。下向きビューを保てる
Views 16 -> 12  下向きが 4->2 本に減る。地面の被覆が狙いなら避ける
大域を切る       枚数はそのまま。いちばん良い結果と同じ設定になる
```

## Caspar Vulkan は速さであって、質ではない

ログに `COLMAP mapping used Caspar Vulkan bundle adjustment on <GPU>` と出る。AirVis 自前の
GPU バンドル調整で、MSIX の LocalCache に `caspar-vulkan` という名前で痕跡が残っている。

**効果は速さ。** 774 枚を Caspar 無しで 7,772 秒、9,088 枚を Caspar ありで 567 秒 ——
画像あたり約 180 倍。**バンドル調整は最小二乗なので、CPU でも GPU でも同じ最適解に収束する。**

**そして、いちばん良い結果 2 本は `casparBackend: "none"` で出ている。** 質の源ではない。
効くとすれば間接的で、総当たり照合や大量の画像が「払える」ようになること。

**中身の出どころは `third_party\colmap-runtime\airvis-colmap-runtime.json` が自己申告する。**
AirVis 1.0.9.0 の COLMAP は private fork `RocklandTechnologies/AVSColmap` の `4.2.0.dev0`
で、upstream 4.2.0 との差はリリース直前の 3 コミットだけ ＝ 実質 4.2.0。**ただし「設定だけ」
ではない** —— upstream の Caspar は CUDA のみで pinhole / simple-radial のカーネルしか持たず、
Vulkan 版は 1 ファイルも無い。AirVis は Vulkan と Metal の Caspar、`EQUIRECTANGULAR` の
カメラモデル、Vulkan の特徴抽出・照合・語彙探索を自前で足している。速さの源はそこ。
Vulkan を選ぶ理由は速度ではなく移植性（AMD / Intel / Apple）で、4090 一台なら CUDA でよい。

## 空・雲・人のマスクを足す

`tools/sky_person_mask.py`（学習機の `~/dgs-field/.venv` に torch がある）。
**AirVis のマスクと論理積を取る** —— マウントの分を消さないため。

```bash
~/dgs-field/.venv/bin/python sky_person_mask.py \
  --images '<proj>/Extracted/sfm-images-1600' \
  --out    '<proj>/Extracted/sfm-masks-1600' \
  --existing '<proj>/Extracted/sfm-masks-1600.bak'
```

**順番を間違えると消える。** `Extracted/` は `Prepare Images` のたびに作り直されるので、
**Prepare → マスク → Train**。先にマスクを作っても上書きされる。

確定した設定と、その根拠：

| 設定 | 値 | なぜ |
|---|---|---|
| 空の閾値 | 0.25 | 地面の空確率は 0.0034。70 倍の余裕がある |
| 空を縮める | **0** | 縮めるとシルエットに空の縁が帯で残る。そこは 3DGS がいちばん汚い splat を作る場所。縮める意味があるのは細い電線がある会場だけ |
| 雲 | 空確率>0.05 かつ 明度>185 かつ 彩度<100 | **SegFormer は明るい積雲を wall 68% / waterfall 20% に誤分類する。** 閾値を下げるだけでは本物の構造を食う |
| 人 | 閾値 0.35、8px 広げる | 空とは逆に広げる。輪郭と足元の影が残るため |

**雲ルールの「門」が要る理由。** 雲は明るく彩度が低い（明度 239〜254 / 彩度 18〜55）が、
**芝の上の白い旗も同じ**。旗の空確率はほぼ 0 なので、**空確率 0.05 の門で分けられる。**
門なしだと地面の誤検出 0.65%、門ありで 0.33%、雲の取り残しは 2.2%。
**門が無いと旗が全ビューで消えて、復元されなくなる。**

**人は撮影者本人。** 人の割合が高いフレームは全部 `pitch-060`（下向き）—— 自撮り棒を
持っているのは本人なので、下を向くと自分が写る。**動くので静的マスクでは獲れない。**

## Insta360 Studio からの書き出し

| 設定 | 値 | なぜ |
|---|---|---|
| Optimization | **off** | オプティカルフローで繋ぎ目をフレームごとに非剛体で歪ませる。同じ 3D 方向が違う画素に移り、SfM の「内部パラメータが一定」が壊れる |
| Stabilization | **off** | 球を回すので同じ害。切るとマウントが固定位置に来て静的マスクで取れる |
| Lens Guards / Dive Case | 実機に合わせる | **ファイルに記録が無い**（トレーラーを確認済み。入っているのはレンズ校正だけ）。間違えると繋ぎ目で近くの物体が二重になるか裂ける。地面は近いので足元の継ぎ目で判定する |

**安定化を切ると、ビューが傾いて見える。** 歩くと自撮り棒が振れ、視線方向がカメラ本体
基準なので `pitch+060` が空を向くフレームも水平を向くフレームもある。**エクイレクタングラー
自体は正しい向きで、上端の帯が空 100%、下端が 7%。** リグ基準では一貫しているので
SfM は問題にならず、マスクは 1 枚ずつ計算するので追随する。

## trainer を単体で叩く（AirVis の SFM を飛ばして自前の COLMAP を食わせる）

MSIX の `SplatTrainerWindows\AirVis-SplatTrainer.exe`（exe 2 本 + shaders、3.4 MB、自己完結）は
**標準 COLMAP レイアウト（`images/` + `sparse/0/`、`masks/` があれば自動で拾う）を直接読む**。
`WindowsApps` 直下は実行を拒まれるので `%USERPROFILE%\airvis-trainer` に複製して使う。
`--help` が全オプションを出す。Vulkan なので CUDA より速く軽い：6M splats を 30,000 反復
12 分 / VRAM 5.2 GB（gsplat は同じ GPU で 3M から 24 GB に張り付いて崩壊）。

```
AirVis-SplatTrainer.exe <colmap_dir> --max-splats 6000000 --alpha-mode masked ^
  --mcmc-init-preset safe --scale-reg 0.01 --bilateral-grid false ^
  --total-train-iters 30000 --export-every 10000 --export-path <out> ^
  --export-name export_{iter}.ply --image-cache-mb max --strategy mcmc
```

**`--max-splats` を大きく取りすぎると preset が黙って `conservative` に落ちる。** このデータ
（6,732 枚、疎点群 249,727）では 6M は `safe` のまま、11.1M で落ちた。落ちると
`scale_reg=0`（大きさを罰しない → 1% がシーン幅の 3 分の 1 の巨大 splat → 針だらけ）、
`opacity_reg` が 0 へ減衰、世界が正規化される（`frame=normalized`）。**3 つの症状が 1 つの
フラグから出るので、必ずログの `=== Resolved trainer configuration ===` で `mcmc_init_preset` /
`scale_reg` / `frame=` を確認してから放置する。** auto cap は `--total-train-iters 1` で
1 分で聞ける（画像数に比例しない：320 枚→528k、6,732 枚→3.68M）。

- `[LowQuality]` は手動フラグではなく画像幅 1900px 未満で自動。アプリの JSON の
  `lowQualityCapture: true` はそれ
- `--bilateral-grid` の CLI 既定は on。実測で負けている（色補正後 −0.41 dB）ので切る
- **書き出しの ply は COLMAP のカメラと大域の相似変換で対応しない**（`safe` の `frame=points`
  でも）。軸置換 48 通り × 倍率 3 通り（最高相関 0.43、正解は 0.89）、ICP（平面優勢で偽解）、
  `--align-to-first-camera true`（1 枚 0.56、2 枚平均 0.29）、局所最適化（0.41）を全部外して
  打ち切り。**AirVis 出力を自前カメラで描いて評価する筋は追わない** —— 品質は SuperSplat、
  VDGS への配置は `align_ply.py` の手動合わせ
- ply のプロパティ順は `x,y,z,scale,opacity,rot,f_dc,f_rest`、法線なし（アプリの export も
  同じ）。名前で読めば無事、位置で読むローダーは取り違える。`--export-opacity-floor`
  既定 0.05 で不透明度が下から押さえられるので、生存率の比較には使えない
- **Windows の OpenSSH はセッション終了でプロセス木を殺す**（`Start-Process` でも）。
  長い学習は `Register-ScheduledTask`（`-LogonType Interactive -RunLevel Highest`、
  `-ExecutionTimeLimit ([TimeSpan]::Zero)`）で起動する

## 実測値

```
                入力            cap/iter   splats      loss     生存    備考
JDL v4  DJI 456 枚              3M/50k    2,999,491  0.0269   ―      採用。配備中
JDL v5  DJI 456 枚              5M/100k   4,999,846  0.0226   89.4%  細部は良いが被覆は 0.40->0.35
JDL v6  360 6,816（マスク無）    5M/100k   4,999,456  0.1192    7.6%  崩壊
JDL v7  360 9,088（マスク有）    2M/100k   1,999,993  0.1101   52.9%  改善したが健全ではない
FDF     774 枚                  5M/100k   4,999,971  0.1447   97.3%  被覆 p50 1.68
```

**AirVis の枠内ではマスクと cap で 7.6% → 52.9% が上限だった。** 残っていた容疑者は
全面マスクの 6.4%（`loss 0`）と総当たり照合が切れていること —— どちらも外れで、
**効いたのは SFM の登録率と学習器の正則化**（下）。

**AirVis の外で同じ素材を回すと指標は健全な範囲に入る。ただし品質は使い物にならない**
（Saqoosha の判定、2026-09-03。経緯は docs/findings-2026-09-03.md）：

```
                          SfM 登録        splats     生存    不透明度p50  最長軸p50   masked PSNR
AirVis 360 単独（v6）      50%（自前 SFM）  4,999,456   7.6%    0.0522      0.0684      —
COLMAP 4.2 リグ + gsplat   98.8%           1,000,000  68.1%    0.0304      0.0060      24.6
COLMAP 4.2 リグ + AirVis   98.8%           6,000,000  100%*    0.050*      0.0000      —
```

（* AirVis の export は不透明度を 0.05 で下から押さえる。生存率は人工物、p50 は床の値）

COLMAP 4.2 の `pycolmap.panorama`（perspective リグ、yaw 4 × pitch −35/0/+35 = 12 面、
下向きリングが必ず 4 本入る）で登録が 50% → 98.8%。学習は空・人マスクを損失から外し、
gsplat は `opacity_reg 0.001`、AirVis の trainer は `safe` preset（`scale_reg 0.01`）。
gsplat は 1440² で cap 3M から崩壊（生存 0.2%）、5M は CUDA エラー。

**それでも配備中の JDL（DJI 456 枚）より良くない**（Saqoosha の目視、SuperSplat）。
360 単独は「崩壊しない」まで来たが、16 px/度と歩き撮りの動きブレ（Laplacian 分散の
p95/p50 が 1.51 倍、5 枚に 1 枚を選抜しても 1.15 倍 ＝ 均一なので選抜が効かない）は
撮影の物理で、設定では越えられない。**残る使い道は「DJI と同じ COLMAP モデルに 13 台目の
カメラとして混ぜ、地面の被覆だけ 360 から足す」**。道具は揃った（リグ + 自前 SfM +
trainer 単体、下）が未実施。配備した JDL は DJI 456 枚だけで作られている。

**v4→v5 は学習を倍にして splat を 1.67 倍にした結果、細部は良くなったが被覆は上がらない。**
MCMC は splat を増やすとき 1 個を小さくするので、面積の総和が変わらない。**地面の霞は
撮影密度で決まる** —— JDL は 0.015 枚/m²、FDF は 0.070 枚/m² で 4.6 倍差。

**被覆の比較には `groundfill.py` の局所地面版を使う。`topcover.py` は使えない** ——
全体の Y の 1 パーセンタイルを床にして「床+3m」を帯にするので、**起伏のある地形では帯が
地面から外れる**（FDF で p50 0.00 という誤った値を出した）。

## 360 の結果を測るとき、道具が 2 つ使えなくなる

**`camup.py` は 360 リグに使えない。** あれは「全カメラがだいたい地面を向く」前提（ドローン
静止画）で書いてある。16 面のリグは全方位を向くので、平均の前方が打ち消し合って残差しか
出ない（前方のばらつきが中央値 90 度、p90 143 度なのが兆候）。

**下向きビューだけ使えば曖昧さが無い。** `pitch-060` の 4 本の平均前方が地面向き。JDL では
カメラ中心が張る平面の法線とも 2 度で一致した。**一方 ply の PCA 最薄軸はそこから 27 度
ずれていた** —— 生存 52.9% の壊れかけモデルでは、PCA が地面ではなく人工物に引かれる。
**カメラのほうを信じる。**

**半径比によるスケール導出は、別のキャプチャ相手には使えない。** 同じ SFM から出た 2 本
（JDL の 3M と 5M）なら分位間のばらつき 4.1% で信用できるが、360 と DJI では 32.4% に開く
——撮った範囲が違うので、同じ分位が同じ場所を指さない。**道具が自分でばらつきを報告するので、
10% を超えたら使わない。**

## トレーナーのログの読み方

| 行 | 意味 |
|---|---|
| `visible` が反復ごとに 70 倍振れる | 空だけのビューと地面のビューが混ざっている。マスクが要る |
| `loss 0` | **全面マスクの画像を引いた。** 勾配ゼロなのに正則化は効き続け、不透明度だけが押し下げられる |
| `Relocated N of M` | MCMC が毎ステップほぼ全部を再配置している。cap が初期点数から離れすぎ |

**`loss 0` は「何もしない」ではない。** JDL の 360 では 581/9,088（6.4%）が全面マスクで、
10 万反復のうち 6,400 歩がその状態だった。空マスクを入れると必ず出るので、
**上向きビューを増やすほど不利になる**（Views 16 で 4 本）。
