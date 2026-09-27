# spirula-studio でキャプチャを作る

[spirula-studio](https://github.com/harry7557558/spirula-studio) は、映像や写真から 3DGS を作る単一の実行ファイル。
フレームの抽出、AI マスク（SAM 3）、SfM、学習まで入っていて、Python も COLMAP も要らない。
Vulkan で動く。R6（Avata 2）、R5 の 360（Insta360 X3）、FDF CUP 2026 R6（iPhone の RTK ＋ DJI）を通した。
**R6 と FDF は採用、R5 の 360 は不採用。**

## 置き場所

win4090 の `D:\spirula\spirula.exe`（2026.9.20、Windows Vulkan 版の zip を展開したもの）。C: は 98% 埋まって
いるので、作業場も D:（`D:\JDL-2026-R6-spirula\`、`D:\JDL-2026-R5-spirula\`）。

- **scp の宛先は `win4090:D:/...` と書く。** `/d/...` は scp が解釈せず、`remote mkdir ... No such file` で失敗する
- 学習は数十分かかるので、SSH が切れても死なないようにスケジュールタスクで回す（win4090 の罠は
  memory の `win4090-windows-box`）
- **`train` は既定で `--keep-viewer-alive 1`。** 学習が終わってもビューアのプロセスが残り、GPU を握り続ける。
  `--keep-viewer-alive 0` を付ける
- CLI の SAM 3 のモデルは自分で置く：`https://huggingface.co/PABannier/sam3.cpp/resolve/main/sam3-q4_0.ggml`
  （707 MB、Meta の SAM 3 ライセンス）。GUI は同意の画面を出して `%APPDATA%\spirula-studio\models` に落とす

## R6：DJI Avata 2 の映像（採用）

```bash
# 抽出。動きに合わせて間隔を変える（平均 3 fps 相当）。967 枚を 1.7 分（GPU デコード 680 fps）
spirula sam extract DJI_..._0016_D.MP4 -o images/v16 --skip 20 --adaptive

# SfM。GPS の位置で実スケールと向きを決める
spirula sfm auto images -o ws --data-type video --camera-mode single --focal 1048 \
  --metric-positions positions.txt --prefilter-sequential --audit

# 学習
spirula train --data . --colmap-recon-dir ws/sparse/0 --cap-max 3000000 --num-iterations 30000 \
  --sh-degree 3 --background-mode sh --floater-suppression mild --distraction-robustness mild \
  --keep-viewer-alive 0
```

- **Avata 2 のテレメトリは spirula が読めない**（`--telemetry` は `Telemetry: cannot read ... -- -`）。
  spirula の `djmd` の読み取りは Osmo 360 の protobuf 用。代わりに `--metric-positions` に
  `image_name X Y Z`（メートル、ENU）の 1 行 1 枚のファイルを渡す。exiftool で抜いた GPS から作った
  （win4090 の `D:\JDL-2026-R6-spirula\tools\metric_positions.py`。原点は R6 の GPS フィットと同じ
  lat0/lon0、高度は `RelativeAltitude`、時刻は `フレーム番号 / 59.94 + 0.033 s`）
- 結果：949/967 枚が登録、再投影 0.47 px、GPS との差は RMS 0.63 m、上向きの誤差 1.0°。
  SfM 2 分 35 秒（同じ素材で COLMAP は 40 分）、学習 23 分 40 秒、PSNR 34.2
- **同じ印刷のアーチへの貼り間違いを `fold-split` が切った。** COLMAP は、ゲート付近の 26 枚を別のアーチに
  貼っていた。spirula はそれを「2 つの場所が重ねて書かれている」と検出して、17 枚（97.7〜101.6 秒）を別モデルに
  切り離した。貼り間違いは起きない代わりに、その 4 秒はメインのモデルに入らない
- 出力の PLY は SfM の ENU メートル座標のまま（`scene_transform.json` が恒等）。Unity へは
  `fit_transform.py --apply` に `{"scale": 1, "R": [[1,0,0],[0,0,1],[0,1,0]], "t": [0,0,0]}`
  （y と z の入れ替え、det −1）、web へは R を `[[1,0,0],[0,0,1],[0,-1,0]]` にしたもの
- **不透明度が柔らかい。** 中央値 0.11（AirVis の MCMC は 0.98）、5 m を超える splat は 1,870 個（AirVis は 43,843 個）。
  Saqoosha が `w` で比べた判定は「浮遊物が少ない、色が悪い」。色は AirVis 版に Saqoosha が SuperSplat で掛けた
  編集を splat ごとに復元して掛けた（`tools/recolor_r6_edit.py`）
- 地面は AirVis 版より 0.2 m 低い（最も密な層が y 0.9 対 1.1）。placement の位置と turn は同じ値で合う

## FDF CUP 2026 R6：iPhone（RTK）＋ DJI

iPhone Air の歩き撮りに rtk-clapper で全フレームの RTK を付け、DJI の空撮と混ぜた。SfM は RTK を中で効かせる
fork（`D:\spirula-src\build_vulkan\spirula.exe`、`--position-gate` / `--position-sigma` / `--metric-min-perp`）。
作業場は `D:\FDF-2026-R6b-spirula\`（採用した撮り直し）と `D:\FDF-2026-R6-spirula\`（朝の 1 回目）。

```bash
# iPhone：PPK の位置を全フレームに（rtk-clapper。4K で約 20 分、ほぼ ffprobe）
tools/geotag.py IMG_5440.MOV local/sdlog/<log>.csv --pos local/ppk/<log>.pos --out <dir>
tools/sfm/metric_positions.py <dir>/IMG_5440.frames.csv names.txt pos_ppk.txt enu_ppk.csv  # names は ip40/00001.jpg の形

# 抽出。動画ごとにサブフォルダ（カメラがフォルダ単位で分かれる）
spirula sam extract video/IMG_5440.MOV -o x/ip40/images --skip 15 --adaptive \
  --model D:/spirula/models/sam3-q4_0.ggml --text "person; backpack" --mask-mode image   # マスクは x/ip40/masks
# → images/ip40 と masks/ip40 に移す。DJI の写真は 3840 幅に縮めて images/dji に置く（sips -Z 3840、EXIF は残る）

spirula sfm auto images -o ws1 --data-type video --camera-mode folder \
  --metric-positions pos_ppk.txt --metric-min-perp 0.02 --position-gate 2 --position-sigma 0.01 \
  --ba-solver dense --prefilter-sequential --audit
```

- **`--ba-solver dense` を付ける。** σ の拘束は dense の経路にしか無く、自動選択は `n_dim > 8192`（カメラ約 1,365 台）で
  CG に切り替わって `position priors are assembled on the dense path only` で止まる。朝の 1,862 枚は dense で 17 分
- **`--camera-mode folder` で iPhone と DJI を別カメラにする。** 焦点距離は EXIF から入り、動画の DJI はエピポーラの
  投票で決まった（4608 → 2,506 px）。学習はマスクを `masks/<サブフォルダ>/<stem>.png` で見つける（`find_aux_file`）
- **位置は PPK の FIX だけ渡す。** 基準局が途切れた後のライブ FIX と PPK の FIX は使わない（朝の IMG_5437 は動画の開始前に
  基準局のデータが切れていて、位置なしで入れた。それでも映像だけで 149/152 枚登録した）
- **DJI の写真にはマスクを掛けていない。** `sam extract` は動画しか受けない。人は少なく小さいので学習に任せた

| | 朝（IMG_5436/5437 ＋ DJI 動画 2 本） | 撮り直し（IMG_5440 ＋ DJI 写真 286 枚） |
|---|---|---|
| 登録 | 1,832/1,862、モデル 3 個（主 1,832） | 992/999、モデル 1 個 |
| FIX との差 | 202 台、RMS 6.5 cm | 629 台、RMS 6.6 cm |
| iPhone の高さ（地面から、中央値） | 1.41 m | 0.87 m |
| 学習（3M / 30k、random 背景） | 41 分 | 21 分 |

### 目の高さに水平の板が出る

**歩き撮りでカメラが全部同じ高さにあると、その高さに大きく平たい水平の splat が出る。** 水平の板はどのカメラからも
真横に見えて線になり、画像にほとんど写らないので、損失が罰しない。朝の版では地面から 1.2〜1.6 m（カメラの高さ 1.41 m）に
面積×不透明度がほかの高さの 3〜10 倍集まっていて、Saqoosha が SuperSplat で見つけた地点も 1.44 m だった。
DJI が 3〜5 m の低空から浅い角度で見ていても消えなかった。

**直したのは撮り方。** iPhone を膝の高さ（0.5 m 前後）で 20〜30° 下に向けてレースラインを歩き、DJI は低空の写真を足した。
1.0〜1.9 m の帯の板は 736 個 → 34 個（面積×不透明度 173 → 7）。後処理で抜く手（地面から 1.0〜1.9 m・法線が鉛直から 18° 以内・
平たいものを削除、朝の版で 4,234 個）もあるが、地面が近くから撮られていないぼやけ（ドローン目線の 0.5 m で見える）は直らない。

測り方：2 m 四方ごとに「小さく不透明な splat が最も密な 10 cm の層」を地面とし、そこからの高さで数える。平たい水平の条件は
「2 番目の軸 > 0.3 m、最短軸 < その 5%、最短軸（法線）が鉛直から 14° 以内、不透明度 > 0.1」。斜面では絶対高さで数えると
山が消えるので、必ず地面からの高さで見る。

## R5：Insta360 X3 の 360（不採用）

```bash
# X3 はレンズごとに別ファイル。同じフレーム番号で機械的に抜く（--keep 0）
spirula sam extract VID_..._00_007.insv -o images/cam0 --skip 30 --keep 0
spirula sam extract VID_..._10_007.insv -o images/cam1 --skip 30 --keep 0

# 2 本を 1 つのリグに束ねる。X3 のテレメトリ（IMU 1000 Hz、GPS 無し）は読める
spirula sfm auto images -o ws --data-type video --rig dual-fisheye=cam0,cam1 \
  --camera-model opencv-fisheye --masks masks_person --telemetry VID_..._00_007.insv --audit

spirula train 360-camera --data . --colmap-recon-dir ws/sparse/0 --mask-dir masks_person ...
```

- **`--keep` で鮮鋭なフレームを選ばせてはいけない。** 2 本が別々の瞬間を選び、リグの対にならない
- マスクは 3 層：`sam mask --shape "ellipse 0.5,0.5,0.49,0.49; -rect 0.33,0.9,0.67,1"`（縁とマウント。
  自動検出の縁は半径 0.54 と緩い）に、`sam track --text person` を重ねる
- **`sam track` は `frame_00000.png` の連番で書く**（stem ではない）。そのままだと `sam mask` の交差も
  SfM のマスクの照合も、黙って外れる。読んだ順の stem に付け替えてから重ねる
- 結果：548/568 登録、モデルは 1 つ、スケールは IMU から（不確かさ 1.75%）。リグの基線 3.7 cm は
  X3 のレンズ間隔と一致する。学習 27 分 38 秒、PSNR 27.8
- **不採用の理由**：面積 × 不透明度の 99.8% が、60 m より外の空のドームだった。外接球で視点を決める
  ビューアでは地面が見えない。ドームを切っても採用に至らなかった。trainer を替えても、360 単独では
  使い物にならないという前の結論（gsplat）と同じ
