# spirula-studio でキャプチャを作る

[spirula-studio](https://github.com/harry7557558/spirula-studio) は、映像や写真から 3DGS を作る単一の実行ファイル。
フレームの抽出、AI マスク（SAM 3）、SfM、学習まで入っていて、Python も COLMAP も要らない。
Vulkan で動く。R6（Avata 2）と R5 の 360（Insta360 X3）を通した。**R6 は採用、R5 の 360 は不採用。**

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

## RTK の位置を SfM の中で効かせる（fork）

**素の spirula は、位置ファイルを最後の 7 パラメータの当てはめ（`fixGauge()`）にしか使わない。** 登録も統合も
バンドル調整も位置を読まないので、`--metric-positions` に cm 級の RTK を渡しても、SfM の貼り間違いと曲がりは
そのまま残る。iPhone の歩き撮り IMG_5423（rtk-clapper で全フレームに RTK、FIX 76%）で両方起きた：

- **285 秒以降の約 60 m が、似た場所へ 100 m ずれて貼られた。** 統合ではなく逐次の登録で起きている（ログは
  モデル 1 個・統合 0）。PnP が似た場所の点に登録し、あとの画像がそれに続いた。当てはめはこの区間を外れ値として
  黙って外すので、報告の RMS 0.89 m には出てこない
- **残りも 0.1〜2 m 曲がる。** 再投影誤差は 1.1 px で健全に見える。拘束を足しても再投影誤差は変わらなかったので、
  画像からよく決まらない方向に誤差がたまっていただけ

fork のブランチ `rtk-position-prior`（`~/repos/OSS/spirula-studio`、上流 `6b70e20` から）で 2 つ足した：

| フラグ | 効き目 |
|---|---|
| `--position-gate M` | PnP で置いたカメラが RTK の位置から M m 以上離れたら登録を拒む（`Mapper::farFromPrior`）。統合でカメラの 5% 超が外れる統合も拒む（`priorGuarded`） |
| `--position-sigma S` | 全体のバンドル調整ごとにモデルを RTK に当てはめ、FIX のカメラ中心を σ S m で引き寄せる。**項は CPU のソルバーにしか無い**。拘束があれば `runGlobalBA` が自動で CPU に回す |

どちらも `--metric-positions` のファイルを読む。**渡すのは FIX だけ**（FLOAT は hAcc 3 cm と申告して 1.3 m
ずれていた区間がある。rtk-clapper の AGENTS.md）。

```bash
spirula sfm auto images -o ws --data-type video --camera-mode single --focal 2773 \
  --metric-positions positions.txt --position-gate 2 --position-sigma 0.01 \
  --prefilter-sequential --audit
```

`--focal 2773` は iPhone Air の 26 mm 換算を 3840 px 幅に直した初期値。

| IMG_5423（FIX の SfM − RTK） | 素 | 関門だけ | 関門＋σ 5 cm | 関門＋σ 1 cm |
|---|---|---|---|---|
| 0〜284 秒（35 秒ごとの中央値） | 0.1〜2 m | 0.2〜2.3 m | 0.10〜0.43 m | **0.05〜0.10 m** |
| 285〜348 秒 | 約 100 m | 0.04〜0.06 m | 0.03〜0.05 m | 0.03〜0.05 m |
| 所要時間（1,044 枚） | 4 分 | 3 分 54 秒 | 5 分 52 秒 | 5 分 |

- **関門だけでは曲がりは直らず、逆に曲がりで外れた正しい画像まで弾く**（2 m で 35 枚）。σ を足すと 5〜24 枚に減る
- **σ 5 cm では押し切れず、1 cm で押し切れた。** 1 cm でも再投影誤差は 1.15 px のまま
- 関門で弾かれた区間は別のモデルになる。`fixGauge` がモデルごとに RTK に合わせるので重ねれば 1 つに見えるが、
  **1 つのモデルにはなっていない**（IMG_5423 は 0〜284 秒と 285〜348 秒の 2 つ）
- **CG の経路には項が無く、そこに入るとエラーで止まる。** CPU ソルバーは n_dim が 8,192 を超えると CG を選ぶので、
  カメラが約 1,300 台を超える入力では使えない
- 関門の 2 m と σ 1 cm はこの 1 本で決めた値

### win4090 でビルドする

作業場は `D:\spirula-src`（上流の clone。手元の fork から変えたファイルを scp で上書きする）。成果物は
`D:\spirula-src\build_vulkan\spirula.exe`。素のビルドは 1,157 ステップで十数分、差分なら数十ステップ。

- **Vulkan SDK が要る**（`winget install KhronosGroup.VulkanSDK`、1.4.357.0 を入れた）。VS 2022 は入っていた
- **`build_develop.bat` を SSH から直接呼ばない。** PATH が 8,191 文字を超えていて、`cmd` が `findstr` すら
  見つけられない（`'findstr' is not recognized`）。短い PATH と `VULKAN_SDK` をバッチの先頭で組み直した
  `D:\spirula-src\vdgs-build.bat` を、スケジュールタスク `vdgs-spirula-build` で走らせる。ログは `build.log`、
  最終行に `exit N`
- `-DSS_BUILD_GUI=OFF` で GUI を外す。コメントの長さの lint（`tools/check_comment_length.py`、1 か所 3 行まで）は
  ビルドの前に手元で通しておく
- SfM も長いものはスケジュールタスク `vdgs-spirula-sfm`（`D:\IMG_5423-spirula\run-prior*.bat`）で回した

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
