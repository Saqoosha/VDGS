# VDGS — 3D Gaussian Splatting inside VelociDrone

VelociDrone に 3D Gaussian Splatting シーンを読み込む mod。BepInEx プラグインとして
コードを注入し、実行時に splat データをレンダリングする。

## 状態：実データで動作確認済み（2026-08-17）

| シーン | splats | 確認内容 |
|---|---|---|
| testcube（合成） | 640 | 軸・色・スケール・深度、すべて設計通り |
| luigi（実データ） | 14,526 | 変換・描画 OK |
| bonsai（実データ） | **1,157,141** | 屋内シーンが実写の質感で描画 |

3つ同時（計 117 万 splats）を RTX 3060 で描画してクラッシュなし。ドローン機体との
前後関係（深度）も半透明ブレンドも破綻していない。

スクリーンショット: `build/shots/first.png`（テストキューブ）、`build/shots/real.png`（実データ）。

```
shader 'Gaussian Splatting/Render Splats'  supported=True
compute 'SplatUtilities'                   supported=True
=> shaders READY
```

### 実測パフォーマンス（RTX 3060 / 120Hz ディスプレイ / VSync ON）

splat の有無で切り分けた実測：

```
splats=0          120.0 fps    8.33 ms   ← splat なし
splats=1,916,379   77.9 fps   12.84 ms   ← playroom 全量
```

**191 万 splats の描画コストは約 4.5 ms/frame。** tinywhoop で屋内を飛ぶには
十分すぎる余裕がある。

**fps の頭打ちを見たら、まず測定経路を疑う。** 一度 `60.0 fps / 16.67ms` を
「VSync 上限」と記録したが誤り。ディスプレイは 120Hz で、あの 60 は **Parsec 越しに
見ていたため**（Parsec が 60fps に制限する）。物理画面で測るか、Parsec 経由と
明記すること。

スポーン直後の 1 フレームだけ数百 ms 〜 数秒かかる（`GraphicsBuffer.SetData` で
数十 MB をアップロードするため）。飛行中に GS を切り替えると必ずスタッターになるので、
本番では飛ぶ前に表示させておくこと。

フレームタイムは `<game>/vdgs-perf.log` に 5 秒ごとに追記される。

## ターゲット環境

### Windows 機（開発・実行のメイン）

Tailscale 経由の SSH。`ssh user@windows-box`（ユーザー名 `a`、ホスト `w`、デフォルトシェルは **PowerShell**）。

| 項目 | 値 |
|---|---|
| ゲームパス | `%USERPROFILE%\Downloads\Velocidrone Windows Launcher\app` |
| ユーザーデータ | `%USERPROFILE%\AppData\LocalLow\velocidrone\velocidrone` |
| Velocidrone | 1.16.0 |
| Unity | 2021.3.45f2 (88f88f591b2e) |
| スクリプティング | **Mono**（IL2CPP ではない） |
| レンダーパイプライン | **Built-in RP**（URP/HDRP の DLL 無し。PostProcessing v2 + AmplifyColor + Bakery） |
| GPU | RTX 3060 12GB |
| 描画 API | **Direct3D 11** ← 3DGS には不足。D3D12/Vulkan が必要 |
| exe | x64 |



### Mac（M1 Max, ローカル）

Velocidrone 1.17 がインストール済み：
`~/Library/Application Support/PatchKit/Apps/<app-id>/Data/velocidrone.app`

- arm64 thin、adhoc 署名、同じ Unity 2021.3.45f2
- BepInEx は macOS universal ビルドがあるが arm64 での動作は**未検証**
- Mac 版でも `settings.db` / AssetBundle 構造は Windows と同じ → 解析には使える

## ゲーム内部構造（実測）

### シーナリーは内蔵シーン、AssetBundle ではない

`StreamingAssets/settings.db`（SQLite, 357MB）に目録がある。

- `sceneries` テーブル（58行）: `name` が Unity のシーン名（`level0`〜`level50` に対応）、
  `title` が UI 表示名。例: `BlankCanvas` → "Empty Scene Day"
  → **新しいシーナリーの追加は不可能**。既存シーンに乗せるしかない

シーン名（`name`）は `SceneManager.sceneLoaded` が渡す値と一致する（実測済み）。
表示は**トラック名**で決まるのでシーン名を設定に書くことはないが、ログを読むときに要る：

| name | title |
|---|---|
| `BlankCanvas` | Empty Scene Day |
| `BlankCanvasNight` | Empty Scene Night |
| `EmptyPoly` | Empty PolyWorld |
| `Bando` | Bando |
| `House` | House |
| `Office` | Office |
| `Library` | Library |
| `Gym` | Sports Hall |
| `Scene5` / `Scene6` | Industrial Wasteland / Football Stadium |
| `MainMenu` / `auth` / `bootstrap` / `NetworkLobby` | （システム。splat は出さない） |

全 43 件は `sqlite3 settings.db "SELECT name,title FROM sceneries WHERE type='track'"` で取れる。
- `trackprefabs` テーブル（3394行）: トラックエディタで配置できるオブジェクト。
  `type` が AssetBundle 名（`trees`, `gates`, `barriers`, `bando`…）、`name` がバンドル内の
  プレハブ名、`image` が `track_editior_thumbs` バンドル内のサムネ名

### AssetBundle

`StreamingAssets/assetbundles/` に素の AssetBundle が 30個（6.4GB）。`AssetBundle.LoadFromFile`
で読まれる。`aa/` は Addressables（ドローンモデル用）。

**AssetBundle だけでは 3DGS は描けない** — MonoBehaviour のクラスがゲーム側 DLL に無いため
参照が壊れる。compute shader を dispatch する主体が存在しない。だからコード注入が要る。

### Assembly-CSharp は難読化されている

クラス名・メソッド名の一部（`ScenerySwapper+cngfgoinnio` のような形）に加えて、
**文字列定数と数値定数がシャッフルされている**。デコンパイル結果に出てくる
`Screen.width / 0` や、無関係な場所の `"/assetbundles/"` は嘘。

→ **静的解析（ILSpy）の定数は信用しない。実行時リフレクションで調べること。**

デコンパイル済みソース（267万行、gitignore 済み）: `research/decompiled/Assembly-CSharp.decompiled.cs`
クラス名とメソッドの構造は読める。定数だけが嘘。

### アンチチート

`ACTk.Runtime.dll`（Anti-Cheat Toolkit）が同梱されている。Assembly-CSharp 側での
使用有無は難読化のため未確認。**リーダーボードとマルチプレイでは使用しない。**
ローカル飛行専用。

## MOD の仕組み

BepInEx 5.4.23.5 (win_x64) をゲームフォルダに展開。Doorstop 4 が `winhttp.dll` 経由で注入。

**注入は動作確認済み**（`BepInEx/cache/` と `BepInEx/config/` が起動のたびに更新される）。

BepInEx 5.4.23 は **ディスクログがデフォルト無効**。`BepInEx/config/BepInEx.cfg` に
`[Logging.Disk]` セクションを手で足すと `BepInEx/LogOutput.log` が出るようになる
（`Enabled = true`, `WriteUnityLog = true`）。Chainloader が一度も走っていない状態では
そのセクション自体が生成されないので、**セクションの不在は「まだ Chainloader に到達していない」
という診断情報になる**。

プラグインは自前で `<game>/vdgs-probe.log` にも書く。

### 実測済みランタイム値（2026-08-17、RTX 3060 / セッション1起動）

```
graphicsDeviceType = Direct3D11        ← 3DGS には不足
shaderLevel        = 50                ← wave intrinsics には SM6 が必要
supportsComputeShaders = True
supportsAsyncCompute   = False
colorSpace         = Linear
usesReversedZBuffer = True
graphicsUVStartsAtTop = True
maxComputeWorkGroupSize = 1024
ARGBFloat / ARGBHalf / RFloat / RInt   すべて supported
```

シーン遷移は `auth` → `bootstrap` → …。`auth` シーンのカメラは 1個
（`Camera`, depth=0, cullingMask=0xFFFFF8FF, clear=SolidColor）。

## 開発フロー

```bash
# 1. PLY -> VDGS フォーマット（Mac、Unity 2022.3.42f1）
python3 tools/make_test_ply.py build/testdata/testcube.ply   # 合成テストデータ
/Applications/Unity/Hub/Editor/2022.3.42f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -quit -nographics -projectPath unity/VDGSConverter \
  -executeMethod PlyExporter.Run \
  -vdgsInput <abs path>.ply -vdgsOutput <abs path>/build/splats/<name> \
  -vdgsQuality Medium -logFile -

# 2. プラグイン + splat データを w へ（Mac）
bash tools/deploy.sh

# 3. シェーダーバンドルを焼く（w 上で実行。macOS では不可能）
ssh user@windows-box "powershell -ExecutionPolicy Bypass -File %USERPROFILE%\build-shaders-win.ps1"

# 4. ゲームを起動（セッション1、D3D12 強制）
ssh user@windows-box "powershell -ExecutionPolicy Bypass -File %USERPROFILE%\vdgs-run-interactive.ps1 -GameArgs '-force-d3d12'"
```

**ゲームは必ず `-force-d3d12` で起動する。** 素の D3D11 では splat シェーダーが動かない。

#### `vdgs-run-interactive.ps1` は既定でゲームを残す。`-Diagnose` を付けると殺す

このスクリプトは元々、末尾で無条件に `Stop-Process -Force` していた。ログを回収する
診断用として書かれたためだが、**「起動して 40 秒ほどで静かに落ちるゲーム」にしか見えない**：

- `Stop-Process -Force` はプロセスを即座に終わらせるので、**クラッシュダンプも Windows の
  イベントログも残らず、Player.log は行の途中で切れる**
- タイミングは「ログ待ち + `Start-Sleep 25`」なので毎回ほぼ同じ ≒ 本物のバグに見える
- スクリプトの出力を `grep pid` で絞っていると `=== stopping ===` が視界に入らない

これを「起動直後に Web API を叩くとクラッシュする」と誤診し、長い回り道をした。実際は
API と無関係（45 秒連続ポーリングで無傷）。**`Start-ScheduledTask` を直接叩いた回だけ
生き残った**という観測が唯一のヒントだった。ゲームが理由もなく死んだら、まず
**自分が起動に使ったスクリプトの最後まで読む**こと。

### Windows 側の Unity

`unity` CLI（1.0.0-beta.5）を `%USERPROFILE%\AppData\Local\Unity\bin\unity.exe` に導入済み。
インストーラは PowerShell 版を使う（bash 版は Windows を検出して拒否する）：

```powershell
$env:UNITY_CLI_CHANNEL = 'beta'
irm https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.ps1 | iex
```

Editor は `%USERPROFILE%\UnityEditors\2021.3.45f2`。2つ罠がある：

- **`Start-Process` で起動したインストーラは SSH 切断で死ぬ**（7GB のダウンロードが 41% で消えた）。
  タスクスケジューラ経由で起動すること
- **デフォルトの `C:\Program Files\Unity\Hub\Editor` は UAC 昇格が要る。**
  `unity install-path --set %USERPROFILE%\UnityEditors` でユーザー領域に変えてもなお昇格を求めるので、
  タスクは `-RunLevel Highest` で登録する（`-RunLevel Limited` だと誰も答えられない UAC
  プロンプトが出て `ELEVATION_CANCELLED` になる）

### scp の罠

ゲームパスにスペースが含まれ、リモートのデフォルトシェルが PowerShell。
**PowerShell はバックスラッシュをエスケープとして扱わない**ので、`scp` に
`Velocidrone\ Windows\ Launcher` を渡すとファイルが黙って消える（エラーも出ない）。

→ スペースを含まない `%USERPROFILE%/vdgs-stage/` に scp し、`Copy-Item` で設置する。
`tools/deploy.sh` がこれをやっている。

### SSH からゲームを起動できない（セッション 0 の壁）

SSH シェルは **セッション 0** で動く。ユーザーのデスクトップ（explorer）は **セッション 1**。
セッション 0 にはウィンドウステーションが無いため DirectX がスワップチェーンを作れず、
Unity は起動途中で死ぬ。症状：

```
Screen: DX11 could not switch resolution (1280x720 fs=0 hz=0)
- Completed reload, in 0.111 seconds      ← 正常時は 8.186 秒
```

Mono のアセンブリロードすら完了しないので、**BepInEx の Chainloader も走らない**。
プラグインが読まれないように見えるが、原因は BepInEx ではない。

`-screen-fullscreen 0` でも回避できない。解決策はセッション 1 で起動すること：

```powershell
$principal = New-ScheduledTaskPrincipal -UserId (whoami).Trim() -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName 'VDGS-Launch' -Action $action -Principal $principal
Start-ScheduledTask -TaskName 'VDGS-Launch'
```

`-UserId` に `"$env:USERDOMAIN\$env:USERNAME"` を使うと `No mapping between account names
and security IDs was done` で失敗する（SSH セッションでは `USERDOMAIN` が空）。
`(whoami).Trim()` は `DOMAIN\user` を返すので確実。

**副作用：ゲームがユーザーの物理画面に立ち上がる。** 作業中の相手に断りなくやらないこと。

### 参照アセンブリ

`lib/`（gitignore）に Windows 機から回収済み：
- `lib/bepinex/` — BepInEx.dll, 0Harmony.dll ほか
- `lib/unity/` — UnityEngine*.dll 71個 + Assembly-CSharp.dll

再取得するなら `scp` のダウンロード方向は `user@windows-box:%USERPROFILE%/Downloads/Velocidrone\ Windows\ Launcher/app/...`
で通る（アップロード方向だけが壊れる）。

## バックアップ

`%USERPROFILE%\vdgs-backup\<timestamp>\`（2.8GB）に取得済み：

- `Managed/` — DLL 注入対象
- `Data-loose/` — `globalgamemanagers` など Data 直下の 50MB 未満のファイル
- `settings.db` — trackprefabs / sceneries テーブル
- `assetbundle-manifests/`
- `LocalLow-velocidrone/` — **ラップタイム記録。再取得不能。最優先**

ゲーム本体 39GB（`level*` / `sharedassets*`）は PatchKit ランチャーで再取得できるため
意図的にバックアップしていない。

## テストデータ

`tools/make_test_ply.py` が合成の 3DGS シーンを吐く。実データを待たずにパイプラインを
検証するためのもので、軸のねじれ・色の誤り・スケール違いが一目で分かるように作ってある
（+X 赤 / +Y 緑 / +Z 青 / 灰の床グリッド / 黄の原点マーカー）。

実データの入手先（すべて `.ply`、Hugging Face から直接 curl できる）：

| シーン | splat 数 | サイズ | URL |
|---|---|---|---|
| luigi | 14,526 | 1.0 MB | `datasets/dylanebert/3dgs/resolve/main/luigi/luigi.ply` |
| bonsai | 1,157,141 | 287 MB | `datasets/dylanebert/3dgs/resolve/main/bonsai/point_cloud/iteration_7000/point_cloud.ply` |

`dylanebert/3dgs` には bicycle / garden / kitchen / room / stump / counter / playroom もある。
ただし多くは `.splat` 形式で、UnityGaussianSplatting は **`.ply` と `.spz` しか読まない**。
`point_cloud/iteration_*/point_cloud.ply` を探すこと。

`luigi.ply` は SH degree 0（`f_rest_*` を持たない）。それでも変換は通る。

### 飛ぶなら「被写体周回」ではなく「室内を歩き回った」キャプチャを選ぶ

**bonsai は床が溶ける。** 欠損ではない — y=0.0〜0.5 に 261,170 splats あり、XZ の 88% を
覆っている。上から見れば床はある。だがドローン目線の浅い角度では滲んで使えない。

原因は撮り方。bonsai（Mip-NeRF 360）は**盆栽とテーブルの周りを回っただけで、床に
カメラを向けていない**。浅い角度からしか見られていない面は、その方向に引き伸ばされた
ガウシアンとして復元される。**真上からは埋まって見え、接地目線では溶ける。**

Y オフセットで持ち上げても直らない（位置の問題ではないため）。実際に 1m 上げて確認済み。

**飛行用には室内を移動しながら撮ったキャプチャ（playroom / drjohnson 系）を使う。**
新規に撮るなら、被写体だけでなく**床にレンズを向けたパスを必ず入れる**こと。

### 向きとスケールは元データ依存（変換は何も変えない）

**COLMAP 由来のデータは向きもスケールも任意。** 上下が逆だったり、床が傾いていたり、
1 unit が何メートルか決まっていない。

**`PlyExporter` は向きを一切変えない**（luigi が上下逆に見えたので変換を疑ったが、
SuperSplat で開いても同じく逆さまだった → 元データがそういう向き）。
つまり直すべきは変換ではなく、投入前の `.ply` そのもの。

**向き合わせは SuperSplat でやる。** [superspl.at/editor](https://superspl.at/editor) は
1.8 から正射影ビュー（View Cube の円をクリック）を持ち、TRANSFORM パネルで回転・
スケールを数値入力できる。正射影で見れば床が水平かどうか一目で分かる。

**床の自動検出は諦めた。** RANSAC を 3 通り試して 3 回とも壁を床と誤検出し、しかも
`tilt 1.4 度` のような**もっともらしい数字**を返すので気づけない（実際は壁に対する
1.4 度で、ゲームに入れて初めて発覚）。インライアの反復リファインは精度を上げるどころか
`11.9 度 → 23.7 度` に悪化させた — 反復するほど「点が多い大きな面」に引かれる。

**「点が最も多い平面」と「人間が床と認識する平面」は別物**で、後者は幾何だけからは
決まらない。SuperSplat の正射影ビューが正しい道具。

`tools/align_ply.py` に残っている使いどころ：

| オプション | 用途 |
|---|---|
| `--rotate X,Y,Z` | ビューアで読んだ角度を正確に適用。**クォータニオンにも適用される** |
| `--sample N` | 間引いてプレビューを作る（SuperSplat に投げやすくする） |
| `--ceiling H` | 天井高からスケールを決め、床を y=0 に落とす |
| ~~`--up`~~ | 床の自動検出。**動かない**。経緯の記録として残置 |

回転は位置だけでなく**各ガウシアンの向きクォータニオンにも適用が必要**。位置だけ回すと
点群は正しく見えるのに全 splat が傾いたままになる。

#### crop はしない（ツールごと削除済み）

パーセンタイルで外周を切ると、**部屋を内側から撮ったキャプチャでは壁が外周そのもの**
なので部屋を削ることになる。playroom に `--percentile 5` をかけたら 28%（54 万 splats）が
消えて密度が目に見えて落ちた。

品質を落とす選択肢は持たないと決めたので `tools/crop_ply.py` と cropped な ply は
削除した。破片が目障りなら `align_ply.py --bounds` で箱を明示する。

#### 3DGS は Unity で必ず鏡像になる（upstream は座標変換をしない）

**すべてのキャプチャが鏡像で表示される。** データ個別の問題ではない。

3DGS（COLMAP 由来）は**右手系・Y-down**、Unity は**左手系・Y-up**。
UnityGaussianSplatting のパッケージ全体を検索しても軸を反転する箇所は 1 つも無く、
ply の座標をそのまま Unity 座標として読む。したがって必ず鏡像になる。

被写体だけ見ていると気づかない。**文字・左右非対称なもので判定する**こと。

**正しい変換は Y の 1 軸反転**：

```bash
python3 tools/align_ply.py in.ply out.ply --mirror y
```

Y の反転（鏡映、行列式 -1）は**上下の反転と鏡像の解消を同時に行う**。
`--rotate 180,0,0` は X 軸まわりの**回転**（行列式 +1）なので、上下は直っても
**鏡像は原理的に直らない**。ここを取り違えて長く回り道した。

#### 鏡映のときクォータニオンの w を反転してはいけない

`R' = M R M` をクォータニオンに落とすと、**反転軸の成分と w はそのまま、
残り 2 軸を反転**する：

```
mirror x -> (w,  x, -y, -z)
mirror y -> (w, -x,  y, -z)
mirror z -> (w, -x, -y,  z)
```

w も反転したくなる（q と -q は同じ回転だから）が、**その同一性は 4 成分すべてを
反転したときにしか成り立たない**。w だけ余分に反転すると、

- **位置は完全に正しいまま**
- **各ガウシアンの楕円体だけが別方向を向く**

という壊れ方をする。画面上は「シーンの形は合っているのに、全体が針状に
飛び散る」という見え方になり、原因が位置なのか向きなのか判別しづらい。

実装を変えたら、行列で検算すること（`M·R·M` と一致するか）。
ランダム回転 200 個で誤差 0 を確認済み。

#### 向きの検証は目視でなく `tools/verify_orientation.py` で

向きの誤りは**見た目に出ない**。少しぼやけた同じシーンに見えるだけで、症状（針状の
ハイライト）が出るころには変換もデプロイも終わっている。1 個ずつ数値で照合する：

```bash
python3 tools/verify_orientation.py build/testdata/bonsai2-aligned.ply \
        build/splats/bonsai --mirror y --sample 40000 --cell 0.02
```

ソース ply と `other.bin` の両方から各ガウシアンの楕円体フレームを再構成し、
対応する 3 軸の角度差を測る。実測（2026-08-18）：

| シーン | 平均 | p99 | 最大 |
|---|---|---|---|
| bonsai 1,157,141 | 0.102° | 0.200° | 0.311° |
| drjohnson 3,177,554 | 0.103° | 0.220° | 0.368° |
| luigi 14,526 | 0.099° | 0.182° | 0.285° |

**誤差 0 が正解ではない。** rotation は品質設定に関わらず常に 10bit の smallest-three に
量子化されるので、約 0.18° が理論下限。実測はそこに乗っている。1° を超えたら本物の欠陥。

作るときに踏んだ罠が 3 つ。どれも「もっともらしい誤答」を返す：

- **コンバータは splat を空間順に並べ替える。** インデックス同士の比較は無意味。
  位置でマッチングする
- **デコードした float4 は `(x,y,z,w)`。** ply と同じ `(w,x,y,z)` として読むと平均 38° ずれる
- **本物の 3DGS ply はクォータニオンを正規化して保存しない**（bonsai は 0.98 前後）。
  回転行列の式は単位長前提なので、そのまま入れると歪んだ行列になり **22° の幻のエラー**が出る。
  合成テストデータは単位長なので素通りする

向きの既知パターンを持つ合成データは `tools/make_orient_ply.py` が吐く
（赤=+X / 緑=+Y / 青=+Z / 黄=対角、XY 平面の扇は鏡映で回転方向が反転する）。

#### 絵の検証は独立実装との差分で。自分の目では無理

鏡像も、残骸 chunk.bin も、正射影カメラも、**全部「それらしい絵」を出した**。目視は
このプロジェクトで一度も欠陥を捕まえていない。別実装に同じ ply を同じカメラで描かせ、
引き算する：

```bash
bash tools/compare_with_webref.sh bonsai build/testdata/bonsai2-aligned.ply 1.035,-1.145,-54.8
```

リファレンスは [antimatter15/splat](https://github.com/antimatter15/splat)（MIT、単一
ファイルの WebGL ビューア）。リポジトリには入れず `build/webref/` に取得する。

実測（2026-08-18、1024×1024 / focal 1920）：

| シーン | splat 数 | 一致する向き | coverage IoU | 平均差 /255 |
|---|---|---|---|---|
| luigi | 14,526 | flip-y | 0.9376 | 8.16 |
| bonsai | 1,157,141 | flip-y | 0.9389 | 9.79 |

**差分画像で面が黒く、輪郭だけ光るのが正常。** 面が光ったら系統的な誤り。

3つ知っておくこと：

- **リファレンスは Y-down（元祖 3DGS 規約）なので、絵は必ず上下反転で一致する。**
  `compare_renders.py` は 8 通りの向きを全部試して勝ったものを報告する。決め打ちしない
- **左右対称な被写体は coverage だけでは向きが決まらない**（Luigi は rot180 でも
  IoU 0.92 出る）。色差で決着する。実装済み
- **ビューアの同梱カメラは `fx=1159.59, fy=1164.66`。** 0.4% の差は 1024 幅で 4px あり、
  比較を潰す。`?f=<focal>` で両方同じ値に上書きするパッチを当てている

##### 正射影カメラで 3DGS を描いてはいけない

比較ビューを作るとき、SuperSplat の view cube に合わせて正射影にしたが、**これが
自分でバグを作っていた**。シェーダーの `CalcCovariance2D` は透視投影のヤコビアンを組む：

```hlsl
float focal = screenParams.x * matrixP._m00 / 2;
J = { focal/z, 0, -focal*x/z^2, ... };
```

正射影行列では `_m00` は tanFov ではなく、`1/z` の項は意味を持たない。結果、すべての
splat が誤ったサイズと剪断で描かれる。**エラーは出ず、ただ全体がぼやける。**
データを疑って時間を溶かした。

`RenderViews` / `RenderCompare` は**極端に細い画角（4°）の透視投影を遠くから**当てる。
見た目は正射影のまま、シェーダーの数学が正しくなる。

その他の実務メモ：

- **`Camera.Render()` は 2 回呼ぶ。** ソートは描画と同じコマンドバッファに積まれるが、
  1 フレーム目が確実である保証はない。粒状ノイズが出たら疑う
- **1024 では実データがザラつく。** サンプル不足。比較は 1024 で足りるが、目視用の
  絵は `-vdgsSize 2048` にする
- **Chrome の `--screenshot` は使えない。** ビューアが `requestAnimationFrame` を回し
  続けるので `--virtual-time-budget` が終わらず、プロセスが固まる。`webref_shot.mjs` が
  CDP を直接叩き、スピナーが消えるのを待って撮る（Node 24 のネイティブ WebSocket、依存なし）

#### SuperSplat の書き出しは Y が反転している

**エディタ上で完璧に立っていても、書き出した ply は Unity と上下が逆。**
必ず `--rotate 180,0,0` を通すこと。

これは実際に踏んだ。SuperSplat で正しく整列したファイルをそのまま変換してゲームに
入れ、bounds も「床 y=0、天井 2.7m」と正しく見えた。**数字も見た目も正常だった。**
実際には天井を地面に接地させていた。

luigi が「SuperSplat でも mod でも逆さま」だったので座標系は一致していると推論したが、
それは誤りだった（元データの向きと書き出しの反転が偶然重なっていた）。

**検証方法：Y 方向の密度ヒストグラム。** 部屋のキャプチャでは床が最も多くのガウシアンを
持つので、**最密スライスが上半分にあれば上下逆**。`align_ply.py` はこれを自動で
チェックして警告する：

```
  density check : densest slice at y=2.48 (98% up the range)
  *** WARNING: the densest surface is near the TOP.
```

推論ではなく測定で判定できる唯一の手段だったので、ツールに組み込んである。

#### その他の書き出し仕様

- **法線を落とす**（62 プロパティ → 59）。3DGS では未使用なので変換は問題なく通る
- **点の順序を保存しない。** 変換前後のファイルを突き合わせて回転を逆算することは
  できない（Kabsch が使えない）。フル解像度に同じ変換を移したいなら、TRANSFORM
  パネルの数値を控えるか、フル解像度の方を SuperSplat で開き直す

## splat データのオンディスク形式（VDGS 独自）

`GaussianSplatAsset` は ScriptableObject だが、中身は**メタ情報 + 5つの生バイナリ TextAsset**
でしかない。AssetBundle 経由だと MonoBehaviour/ScriptableObject の型解決で詰まるので、
同じ内容をプレーンなファイルとして置き、ランタイムで直接読む。

```
<game>/vdgs/
  vdgs-shaders            AssetBundle（シェーダーのみ）
  <name>/
    meta.json
    chunk.bin
    pos.bin
    other.bin
    color.bin
    sh.bin
```

`meta.json`:

```json
{
  "formatVersion": 20231020,
  "splatCount": 1234567,
  "boundsMin": [0, 0, 0],
  "boundsMax": [1, 1, 1],
  "posFormat":   "Norm11",
  "scaleFormat": "Norm11",
  "colorFormat": "Norm8x4",
  "shFormat":    "Norm6"
}
```

- `formatVersion` は `GaussianSplatAsset.kCurrentVersion`（2023_10_20）と一致させる
- `color.bin` は Texture2D にアップロードする。サイズは `CalcTextureSize(splatCount)`
  （幅 2048 固定）と `ColorFormatToGraphics(colorFormat)` から決まる
- `chunk.bin` は `ChunkInfo` の配列。空なら chunk 無し（ダミーバッファを作る）
- 他の3つは `GraphicsBuffer.Target.Raw`、4バイト単位

### 古い chunk.bin が残ると、シーンが黙って砕ける

**VeryHigh（Float32）で変換すると `chunk.bin` は出力されない。** ところが deploy は
ファイルをコピーするだけで、消えたファイルを消さなかった。結果、前回 Norm16 で変換した
ときの `chunk.bin` がゲーム側に生き残る。

シェーダーは **バッファの有無だけ** で chunk 適用を決める。フォーマットを見ない：

```hlsl
uint chunkIdx = idx / kChunkSize;
if (chunkIdx < _SplatChunkCount)
    pos = lerp(chunk.posMin, chunk.posMax, pos);   // pos は 0..1 の重みという前提
```

chunk 付きなら `pos` は箱の中の 0..1 なので正しい。**Float32 は絶対座標**なので、
`-23.2` を lerp の重みに入れて盛大に外挿する。スケールはさらに悪く、lerp のあと
8 乗される。位置も色も SH も同じ扱いを受ける。

見た目は「地面に破片が飛び散る」。**エラーは 1 行も出ない**（ファイル自体は正常だから）。
向きやクォータニオンを疑って一日溶かした。実際は転送の残骸だった。

対処は 2 段：

- `tools/deploy.sh` は、コピー後に **ソースに無いファイルを送り先から削除**する
  （`placement.json` だけは in-game で編集されるので除外）
- `SplatData.AcceptChunks` が、`posFormat` が Float32 のとき chunk.bin を警告つきで
  破棄し、chunk 付きのときは chunk 数が `ceil(splatCount/256)` と一致しなければ
  ロードを失敗させる

**サイズ検証だけでは足りない。** drjohnson の残骸 chunk.bin は 794,432 バイトで、
`ceil(3177554/256)×64` と完全に一致していた（同じ ply を前の品質で変換したもの
だから当然）。フォーマットで弾く規則のほうが本体。

## プラグインの構成

```
src/VDGS/
  Plugin.cs        BepInEx エントリ、シーン監視、キー操作
  Probe.cs         ランタイム環境の実測ダンプ
  ShaderBundle.cs  AssetBundle からシェーダーを取得
  SplatData.cs     meta.json + 5バイナリのローダ
  SplatRenderer.cs 描画本体（CommandBuffer + compute sort）
  GpuSorting.cs    8bit radix sort（upstream からほぼ無改変）
  SplatScene.cs    1つの splat シーンの生成・破棄と placement.json の読み込み
  TrackName.cs     ロード中のトラック名をランタイムに問い合わせる
  TrackBindings.cs トラック名 -> GS の対応表（bindings.json）
  TrackProbe.cs    難読化されたゲームから文字列の在処を探す調査用
  WebControl.cs    HTTP サーバー（操作 API）
  WebUi.cs         ブラウザ UI（埋め込み HTML）
  PerfLog.cs       フレームタイム記録
  PostProcessFix.cs  D3D12 強制の副作用対応（未解決、記録のみ）
```

upstream から**削った**もの：編集機能・selection・cutouts・URP/HDRP パス・Profiler。
依存も `Unity.Mathematics` / `Unity.Collections` / `Burst` を全部剥がし、UnityEngine のみにした。

### 移植時の落とし穴

- **`ChunkInfo` は 64 バイト**（`uint×4 + float2×3 + uint×3 + uint×3`）。96 ではない。
  間違えると全チャンクを誤読して、エラーを出さずに描画が壊れる
- **Unity 2021.3 には `TextureCreationFlags.DontInitializePixels` /
  `DontUploadUponCreate` が無い**（2022.2 で追加）。`GraphicsFormat` を取る `Texture2D`
  コンストラクタも無いので、`TextureFormat` を直接指定する
- compute shader は cutouts バッファを常にバインドする。編集機能を削っても
  **ダミーバッファ（stride 68 = `Matrix4x4` + `uint`）が必要**
- 同様に selection/deletion ビットバッファもバインドが要る。upstream に倣って
  位置バッファを指し、`_SplatBitsValid = 0` を渡す

## 操作は Web UI（ゲーム内キーではない）

`http://<host>:8777/` でプラグインが HTTP サーバーを立てる（`WebControl` + `WebUi`）。

**ゲーム内キーでの操作は全部やめた。** 理由：

- **F7 はトラックエディタのシーン保存**に割り当て済み
- **矢印キーはトラックエディタのオブジェクト移動**。奪うとエディタが使えなくなる
- **Numpad は MacBook に無い**
- **ゲームには HUD を描く場所がない**ので、キーを押しても結果が見えない

外に出すとこれが全部消える上に、別マシンのブラウザから操作できる
（Parsec でゲーム画面を見ながら、手元の Mac で操作する運用）。

### トラック名の取得

多段フォールバック（`TrackName.cs`）。順に：

1. `InGameChangeTrack.glnoaiifnln`（難読化フィールド。**トラックエディタのシーンには
   この型自体が存在しない**ので、そこでは空振りする）
2. **`Track Name` ラベル、パスに `Current Track/Table Entry` を含むもの**
   （`TrackManager2/Modal - Gameplay - Change Scenery/Content/Current Track/...`）。
   シーン中で「現在のトラック」を名乗る唯一の UI 要素
3. 飛行 HUD の `TrackName` ラベル。飛行中は正しいが、**エディタに戻っても最後に飛んだ
   トラック名を保持し続ける**ので最後に置く

**使ってはいけないもの、3 つ:**

- **`EditorManager.nnpnlmbjocf`** — 「最後に *エディタで* 開いたトラック」。飛んでも
  更新されない。最初に見つかる上に一見正しく見える
- **`Tracks Admin Entry(Clone)/TrackEntry/Track` ラベル** — トラック一覧の各行で、
  ユーザーの全トラックが並ぶ。現在のトラックではない
- **`Track Name` ラベルを名前だけで拾うこと** — 同じ modal に**列見出し**の
  `Track Name`（テキストも文字列 `"Track Name"`）が併存する。**必ずパスで絞る**

難読化されたフィールド名はゲームのアップデートで変わる。変わった場合は
F12（`vdgs-track.txt`）でトラック名を検索して、新しいフィールドを探すこと。
検索語は `<game>/vdgs/needle.txt` に書く（プラグインの再ビルド不要）。
**調査用にトラック名を `VDGSPROBE7777` のような固有な文字列にすると一発で見つかる。**

### 罠

- **`JsonUtility` は使えない。** 辞書をシリアライズできず、入れ子型を**例外も警告もなく
  `{}` にする**。ファイルは正常に書けたように見えて中身だけ空になる。
  bindings はゲーム同梱の **Newtonsoft.Json 13**（`Managed/Newtonsoft.Json.dll`）を使う
- **`HttpListener` はボディの無い POST を `411 Length Required` で弾く。**
  ハンドラまで届かないので、`curl -X POST .../api/unload` は失敗する。`-d '{}'` を付ける
- **API のポーリングはゲームを落とさない。** 45 秒連続で `GET /api/status` を叩いても
  全部 200、ゲームは無事。一度「起動直後のポーリングで 40 秒後に落ちる」と結論したが
  **誤り**だった。真相は `vdgs-run-interactive.ps1` の末尾（下記）

### UI のセキュリティ（軽く扱わないこと）

**トラック名は攻撃者が書ける文字列。** VelociDrone はコミュニティのトラックを
ダウンロードでき、その名前がそのまま UI に表示される。サーバーは `http://*:8777/`
で LAN 全体に開いている。

- **`innerHTML` に動的な値を入れない。** `document.createElement` + `textContent` で
  組む（`WebUi.cs`）。一度 `innerHTML` で書いてしまい、`<img src=x onerror=...>` という
  名前のトラックを1本落とすだけで任意コードが動く状態だった
- **`Access-Control-Allow-Origin` を付けない。** UI は同じサーバーから配信されるので
  不要。付けると利用者が開いた任意のサイトからこの API を叩けるようになる
- **POST は `Content-Type: application/json` を必須にする。** クロスオリジンのページは
  preflight なしにこのヘッダを付けられないため、これが CSRF の防波堤になっている。
  外すと `text/plain` の simple request で誰でも API を叩ける

### 開発者向けキー（残置）

| キー | 動作 |
|---|---|
| F9 | 環境プローブを追記 |
| F10 | シーンのヒエラルキーをダンプ |
| F12 | トラック名の探索ダンプ |

F5・F6・F7・F8 は**使っていない**。操作は Web UI から。

### 表示の決まり方

**トラック名 → GS** の対応表（`<game>/vdgs/bindings.json`）だけで決まる。
シーナリー（Empty Scene Day など）単位ではない。同じシーナリー上に何本も
トラックが載るため、シーン単位だと全部のトラックに出てしまう。

トラックはシーンを跨がずに切り替えられる（ゲーム内の change track ダイアログ）ので、
`sceneLoaded` では足りない。**1 秒ごとにトラック名をポーリング**して、
変わったら GS を入れ替える（`PollTrack`）。

紐付けの無いトラックでは**何も表示しない**。間違った GS を出すより無害。

`<game>/vdgs/autospawn`（空ファイル）が無い場合は自動表示そのものが無効。

## 既知の壁

1. **D3D11 では 3DGS が動かない**（解決済み）。aras-p の UnityGaussianSplatting は
   DX11 サポートを削除済みで、Windows では D3D12 か Vulkan が要る。**`-force-d3d12`
   で起動すれば通る**ので `globalgamemanagers` へのパッチは不要だった。副作用は 6. 参照
2. **シェーダーは Unity 2021.3.45f2 でビルドする必要がある**（導入済み）。C# はバージョン
   非依存に書けるが、シェーダーと compute shader はゲームと同じ Unity バージョンの
   AssetBundle で供給しないと動かない。

   さらに 2つの罠：

   **(a) プロジェクトのグラフィックス API を先に設定する。** splat シェーダーは
   `#pragma use_dxc` と `#pragma require wavebasic/waveballot` を宣言している。
   新規プロジェクトのデフォルト（D3D11）でビルドすると、**エラーを出さずに**
   unsupported なシェーダーが焼かれる。バンドルは正常にロードでき、
   `shader.isSupported` が false になるだけなので気づきにくい。
   `PlayerSettings.SetGraphicsAPIs` をビルド前に呼ぶこと。

   **(b) macOS の Editor では D3D 向けの DXC コンパイルができない。**
   ```
   DXC: can only use DXC to target D3D from the Windows Editor.
   ```
   → D3D12 向けのシェーダーバンドルは Mac では作れない。**Vulkan をターゲットにして
   ゲームを `-force-vulkan` で起動する**か、Windows 機に Unity を入れてビルドする。

3. **UnityGaussianSplatting の C# は Unity 2022.3 前提**（`com.unity.collections` 2.1.4 が
   2021.3 に入らない）。だからバージョンを分ける：
   - シェーダー AssetBundle → **2021.3.45f2**（ゲームと一致が必須）
   - PLY → バイナリ変換 → **2022.3.42f1**（出力はプレーンなバイナリなのでバージョン非依存）
   - ランタイム C# → BepInEx プラグインに自前実装（collections/burst に依存しない）
4. **`GaussianSplatAsset` は ScriptableObject。** AssetBundle 経由でロードすると型解決で詰まる。
   splat データは生バイナリとして読み、実行時に GraphicsBuffer へ流す自前ローダを書く
5. **3DGS にコリジョンは無い。** 飛べる壁になる。同じ撮影データからメッシュを抽出して
   invisible collider として置く必要がある

6. **`-force-d3d12` はゲーム本体に副作用がある。** ゲームは D3D11 向けにビルドされて
   いるため、PostProcessing v2 の compute shader が D3D12 では見つからない：

   ```
   Kernel 'KEyeHistogramClear' not found
   UnityEngine.Rendering.PostProcessing.LogHistogram.Generate
   ```

   Auto Exposure が毎フレーム例外を投げる。**描画への実害は無い**、汚れるのはログだけ。

   **`AutoExposure.active = false` では止まらない**（`src/VDGS/PostProcessFix.cs` で
   試して失敗）。`PostProcessLayer.RenderBuiltins` は有効・無効に関わらず
   `LogHistogram.Generate` を呼ぶため。同じ手を再発明しないこと。

   対処は `BepInEx.cfg` の `UnityLogListening = false`。ただし副作用として
   **Unity 側の例外は `Player.log` にしか出なくなり、そこはこのスパムで数十 MB に
   膨らむ**（1 セッションで 64MB を観測）。ログを読むときは必ず除外する：

   ```powershell
   Get-Content $log | Where-Object { $_ -notmatch "KEyeHistogramClear|PostProcessing|^\s*at " }
   ```

## 残タスク

- **`tools/reprocess.sh` の `|| true` が失敗を隠す。** Unity のプロジェクトロックで
  変換が黙って飛んでも成功に見える。今回の chunk.bin 事故の原因ではないが、次に噛む
- **`vdgs-run-interactive.ps1` がリポジトリ外**（`w` の `%USERPROFILE%\`）にしか無い。
  版管理されておらず、今回のような仕掛けが見えにくい。`tools/` に取り込むべき
- **luigi が逆さまに出る。** 元データ由来で、`--mirror y` を通すと上下が反転する。
  テスト用アセットなので飛行シーンには無関係

## 参考

- [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting) — Mac M1 Max で 46FPS の実測あり
- [antimatter15/splat](https://github.com/antimatter15/splat) — 比較用リファレンス（MIT、単一ファイル WebGL）
- [BepInEx releases](https://github.com/BepInEx/BepInEx/releases)
