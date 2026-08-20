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

### 実測パフォーマンス（詳細は docs/performance.ja.md）

**フレームの 87% は splat ごとの固定コスト。** 射影・2D 共分散・SH 評価が splat 1 つに
つき 1 スレッド走る。帯域は 7%、ソートは 6%、画素の仕事は 6%。**「バイト/splat を減らせば
速くなる」は一度そう結論して外した** — 3 シーンの実機値に当てはめたのが誤りで、同一
ジオメトリで SH だけ 5.1 倍減らす統制比較を取ったら 6.5% しか動かなかった。

RTX 3060 / D3D12、`RenderBench` でシーン内部・画角 120°（`bash tools/bench-win.sh`）：

```
                         splats   B/splat   フレーム
empty                         0        -     4.2 ms
playroom                  1.92M       84     8.2 ms
drjohnson  (Float32 SH)   3.18M      236    13.9 ms
drjohnson-shc + カリング   3.18M       47     9.2 ms
```

効いた手は 2 つ。どちらも品質を落とさない：

- **視錐台カリング**（`m_FrustumCulling`、既定 on）— 内部視点で 10.7% 減、ピクセル完全一致。
  余白は splat ごとに 256 単位の半径表から取る（散り読みを避けるため）
- **Float32 を避ける**。実機で `26.83 ms / 37.3 fps` → `17.30 ms / 57.8 fps`

**`Medium` 以下は使わない。** drjohnson が 2.6 倍暗くなる（平均差 58.83/255、形は
IoU 0.9958 で正しい）。原因は未解明。`PlyExporter` の既定は `High`、Medium 以下は警告を出す。

#### High と Cluster16k は区別がつかない。サイズで選んでいい

**速度も見た目も差が無い。** ベンチの 0.20 ms 差は実機のノイズ床の下：

```
実機（1 セッション、名前付きログ）
  drjohnson-high   n=9  中央値 13.99 ms  平均 12.74  レンジ 8.52-14.91
  drjohnson-shc    n=8  中央値 13.62 ms  平均 13.37  レンジ 11.78-14.86
  → 中央値と平均で符号が逆。t = -0.75
```

**1 シーン内のばらつきが 6.4 ms**（カメラの向き次第）で、ティア間の差の 30 倍。標準偏差
2 ms から 0.2 ms を検出するには**片側 1600 サンプル ≒ 2.2 時間の飛行が 2 回**要る。
**飛んでも決着しない。**

見た目も同じ。同一カメラ 3 視点で描いて引き算：

```
視点       平均|差|   p99   最大   >8/255
fwdZ        1.333    2.33     5     0.0%
fwdX        1.034    2.00     3     0.2% → 0.0%
fwdNegZ     0.786    2.33     4     0.0%
```

**1024×1024 の最悪の 1 画素で 5/255。** 8/255 を超える画素はゼロ。Float32 との差
（High 0.09、shc 1.44）は**人間には同じもの**で、0.09 が意味を持つのは Float32 と
比べるときだけ。

だから残る判断軸はサイズ：

| | High | shc |
|---|---|---|
| フォーマット | Norm16 / Norm16 / Float16x4 / Norm11 | **Float32 / Float32 / Float32x4** / Cluster16k |
| B/splat | 84 | 47 |
| drjohnson の実サイズ | 260 MB | 146 MB |
| VRAM (3.18M) | 267 MB | 149 MB |
| 変換 | 1 パス | **k-means 約 10 分** |
| `.ply` 直読みで作れる | ○ | **×** |

**shc は幾何を圧縮していない。** 位置・スケール・色は Float32 のまま、SH だけパレット化
している。つまり 1.44/255 はまるごと SH 由来。

`reprocess.sh` の既定は `High`（k-means 不要で摩擦が少ない）。**配布や VRAM が効く場面では
`-vdgsShFormat Cluster16k` を意識して選ぶ。**

#### ログの読み方

`<game>/vdgs-perf.log` に 5 秒ごとに
`time / fps / avg_ms / worst_ms / splats / scenes` が**追記**される。`worst_ms` は直近
5 秒の最悪フレーム。**飛んで、あとで読むだけ。**

- **起動をまたいで残る**（`=== session <日付>`）。以前は起動のたびに `File.WriteAllText`
  で全消ししていて、**比較対象そのものを毎回壊していた** — A/B は「元を飛ぶ → 終了 →
  変更版を飛ぶ」なのに、その終了が基準値を消す
- **表示シーンが変わると `--- shown: <名前>`。splat 数はシーンを特定しない** —
  `drjohnson-high` と `drjohnson-shc` は**どちらも 3,177,554**。まさにその 2 本を比べた
  走行で、あとから区別できないという穴を踏んだ。6 列の書式は変えていない
- **fps の頭打ちを見たら、まず測定経路を疑う。** 「16.67 ms ちょうど・60.0 fps ちょうど」
  を長らく VSync 上限として記録していたが、**Parsec 越しに見ていたため**だった。
  実測は **119 fps / 8.40 ms**。ディスプレイは 120Hz。**上限に張り付いた値は「速い」では
  なく「測れていない」**

上限に隠れていない最初のデータ（同一セッション）：

```
utlida-full-s5   4,001,829 splats   12.04 ms   p90 13.99
utlida-lod1-s5   2,000,640 splats    9.13 ms   p90 13.39
```

**splat 2 倍で 2.91 ms。** 両方が上限に張り付いていた間は、この差が見えなかった。

**測定は必ず実機で。** 同じ比較が M1 Max で 6.5%、RTX 3060 で 48%。ユニファイドメモリが
帯域を隠す。ベンチは切り分け用で、判断は実機の値で下す。

スポーン直後の 1 フレームだけ止まる（`GraphicsBuffer.SetData` で数十 MB をアップロード
するため）。飛行中に切り替えると必ずスタッターになるので、**飛ぶ前に表示させておく**。

## ターゲット環境

### Windows 機（開発・実行のメイン）

ゲームを走らせる Windows ボックスへ SSH する。リモートのデフォルトシェルは **PowerShell**。
ホスト名は `tools/local.env` の `VDGS_HOST`（gitignore 済み）。リポジトリには書かない。

| 項目 | 値 |
|---|---|
| ゲームパス | ランチャー既定は `%USERPROFILE%\Downloads\Velocidrone Windows Launcher\app`。上書きは `VDGS_GAME` |
| ユーザーデータ | `%USERPROFILE%\AppData\LocalLow\velocidrone\velocidrone` |
| Velocidrone | 1.16.0 で確認 |
| Unity | 2021.3.45f2 (88f88f591b2e) |
| スクリプティング | **Mono**（IL2CPP ではない） |
| レンダーパイプライン | **Built-in RP**（URP/HDRP の DLL 無し。PostProcessing v2 + AmplifyColor + Bakery） |
| GPU | RTX 3060 12GB で測定 |
| 描画 API | **Direct3D 11** ← 3DGS には不足。D3D12/Vulkan が必要 |
| exe | x64 |

### Mac（解析用）

PatchKit 経由の macOS 版（1.17、arm64 thin、adhoc 署名、同じ Unity 2021.3.45f2）でも
`settings.db` / AssetBundle の構造は Windows と同じなので解析には使える。BepInEx の
macOS universal ビルドは arm64 では**未検証**。

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

### 難読化されているのは Assembly-CSharp だけ。globalgamemanagers は素で読める

**「このゲームの静的解析は信用するな」は Assembly-CSharp の話であって、シリアライズ
データには当てはまらない。** `globalgamemanagers` はプレーンな Unity のシリアライズ
データで、UnityPy がそのまま読む。**レイヤーと衝突マトリクスは難読化されていない。**

| 項目 | 値 |
|---|---|
| Fixed Timestep | **0.0025（400 Hz）** |
| Gravity | **-10.78**（9.81 ではない） |
| ドローンのレイヤー | `QuadColliders`(13) |
| 衝突相手 | `Default`(0) |
| Office のコライダー数 | 598、すべて `Default` |

400 Hz でも 150 km/h では **1 ステップ 0.104 m** 進む。**厚さ 10 cm 未満の壁は
すり抜ける。**

ゲームが何と衝突するかを知りたいときは、リフレクションで探るより 2 分で読める。

（コリジョン設計セッションの実測。こちらでは未検証だが、手順は再現可能）

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
  -vdgsQuality High -logFile -

# 2. プラグイン + splat データを Windows 機へ（Mac）
bash tools/deploy.sh
bash tools/deploy.sh --plugin    # DLL だけ（splat 2.2GB を送り直さない）

# 3. シェーダーバンドルを焼く（Windows 上で実行。macOS では不可能）
bash tools/bake-shaders.sh

# 4. ゲームを起動（セッション1、D3D12 強制）
bash tools/launch-win.sh
```

**手順 1 は省ける。** `<game>/vdgs/foo.ply` を置けばプラグインが実行時に読む
（217 万 splats のパースが **0.97 秒**）。変換済みディレクトリと同名なら**ディレクトリが勝つ**。

**直置き経路は Y を必ず鏡映する。** `PlyLoader` は既定 `mirrorY = true`、`SplatCollision` は
`.ply` なら必ず鏡映するので、**splat とコリジョンは常に一致する**（食い違いは起きない）。
ただし裏を返すと、**すでに床が下向きに整えてあるキャプチャは直置きでは必ず上下逆になる**
（`playroom-nocrop.ply` がそれ）。そういうものは `reprocess.sh` で変換して置く。

**ただし 0.97 秒は体感する数字ではない。** `PlyBench` は `GraphicsBuffer.SetData` の前で
止まる。ゲーム内の実測は 217 万で **2.95 秒**、400 万＋SH で **13〜14 秒**。レートは
バイトではなく splat ごとで、**SH 無し 1.34 µs/splat、3 次 3.39 µs/splat**（詳細は
docs/ply-loading.ja.md）。

**これはコールドの値。同一ブート内の 2 回目は約 4 倍速い**（400 万＋SH が 12.8 → 3.5 秒）。
どちらを引くかは問いによる。「初めて触る人の体感」ならコールド。
速度は焼いた最速版の 7% 差、画質は測定に出ない差（詳細は docs/ply-loading.ja.md）。

**ゲームは必ず `-force-d3d12` で起動する。** 素の D3D11 では splat シェーダーが動かない。

```bash
# 変換（品質はそのまま、SH だけ圧縮）— reprocess.sh の既定でもある
bash tools/reprocess.sh [scene]

# 本番機で描画時間を測る（Mac の数字は移らない）
bash tools/bench-win.sh                          # 全シーン、全体を画面に収めた視点
VDGS_BENCH_INSIDE=1 bash tools/bench-win.sh       # ドローン目線（カリングを測るならこちら）
VDGS_BENCH_INSIDE=1 VDGS_BENCH_CULL=0 bash tools/bench-win.sh   # カリング無しと比較
```

**シェーダーを変えたら `bash tools/bake-shaders.sh` で焼き直す。** 焼かないとゲーム側は
古いシェーダーのまま動き、C# だけ新しいという食い違いになる。

- tgz は**プロジェクトディレクトリごと**固める必要がある（`build-shaders-win.ps1` は
  `%USERPROFILE%` に展開するため）。中身だけ固めると `unpack failed: VDGSBundler missing`
  になり、転送の失敗に見える
- **バンドルが 1MB を切ったら失敗を疑う。** splat シェーダーは `#pragma require
  wavebasic/waveballot` を宣言していて、プロジェクトのグラフィックス API が D3D12 で
  ないと**エラーを出さずに** unsupported として焼かれる。バンドルは正常にロードでき、
  `shader.isSupported` が false になるだけ。正常な値は約 150 万バイト

#### 起動スクリプトは既定でゲームを残す。`-Diagnose` を付けると殺す

`bash tools/launch-win.sh` が `tools/launch-win.ps1` を Windows 機に送って走らせる。**毎回送る
のが要点** — 以前は向こうに手で置いた版とリポジトリの版が別々に育ち、片方にしかない処理が
あった。

`-Diagnose` を付けたときだけ、ログを整形して出したうえでゲームを止める。**この末尾は元々
無条件に走っていて、「起動して 40 秒ほどで静かに落ちるゲーム」にしか見えなかった**：

- `Stop-Process -Force` はプロセスを即座に終わらせるので、**クラッシュダンプも Windows の
  イベントログも残らず、Player.log は行の途中で切れる**
- タイミングは固定の `Start-Sleep` なので毎回ほぼ同じ ≒ 本物のバグに見える
- 出力を `grep pid` で絞っていると `=== stopping ===` が視界に入らない

これを「起動直後に Web API を叩くとクラッシュする」と誤診し、長い回り道をした。実際は
API と無関係（45 秒連続ポーリングで無傷）。ゲームが理由もなく死んだら、まず
**自分が起動に使ったスクリプトの最後まで読む**こと。

### Windows 側の Unity

`unity` CLI（1.0.0-beta.5）は PowerShell 版のインストーラで入れる（bash 版は Windows を
検出して拒否する）：

```powershell
$env:UNITY_CLI_CHANNEL = 'beta'
irm https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.ps1 | iex
```

Editor はユーザー領域（`%USERPROFILE%\UnityEditors\2021.3.45f2`）に置いている。2つ罠がある：

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

→ リモートホームの `vdgs-stage/`（スペース無し）に scp し、`Copy-Item` で設置する。
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

`lib/`（gitignore）にゲーム機から回収する：
- `lib/bepinex/` — BepInEx.dll, 0Harmony.dll ほか
- `lib/unity/` — UnityEngine*.dll 71個 + Assembly-CSharp.dll

`scp` の**ダウンロード**方向はスペース付きパスでも通る（アップロード方向だけが壊れる）。

## バックアップ

ゲームの `Managed/`、`globalgamemanagers` など Data 直下の小さいファイル、`settings.db`、
AssetBundle のマニフェスト、および `%LOCALAPPDATA%Low\velocidrone\`（**ラップタイム記録。
再取得不能。最優先**）は手元に取っておく。

ゲーム本体（`level*` / `sharedassets*`、数十 GB）は PatchKit ランチャーで再取得できるため
意図的にバックアップしていない。

## テストデータ

`tools/make_test_ply.py` が合成の 3DGS シーンを吐く。実データを待たずにパイプラインを
検証するためのもので、軸のねじれ・色の誤り・スケール違いが一目で分かるように作ってある
（+X 赤 / +Y 緑 / +Z 青 / 灰の床グリッド / 黄の原点マーカー）。

実データの入手先は学術データセットや SuperSplat の公開シーンだが、**再配布はできない**
（次節）。手元で飛ぶなら `.ply` を自分で取って `<game>/vdgs/` に置く。手順は
[docs/SCENES.ja.md](docs/SCENES.ja.md)。

`dylanebert/3dgs` には bicycle / garden / kitchen / room / stump / counter / playroom もある。
ただし多くは `.splat` 形式で、UnityGaussianSplatting は **`.ply` と `.spz` しか読まない**。
`point_cloud/iteration_*/point_cloud.ply` を探すこと。

`luigi.ply` は SH degree 0（`f_rest_*` を持たない）。それでも変換は通る。

### splat データは配布できない。同梱もしない

**mod と一緒に配れるキャプチャは `make_test_ply.py` が吐く合成データだけ。** 他は全部
他人の著作物で、こちらに再配布の権利が無い。2026-08-20 に一次情報を当たった結果：

| 出どころ | ライセンス | 再配布 |
|---|---|---|
| `dylanebert/3dgs`（HF） | **データセットカードが無い。表記ゼロ** | 不可 |
| Deep Blending（drjohnson, playroom） | **明示的なライセンス無し** | 不可（既定は全権利留保） |
| Mip-NeRF 360（bonsai） | プロジェクトページに**表記なし** | 不可 |
| INRIA 3DGS | 研究目的のみ・商用禁止・**制限が派生物に引き継がれる** | 不可 |
| SuperSplat 公開シーン（utlida / nelson / textilni / calico） | **第三者の公開作品**（`textilni` は「Textilní továrna, Krásná lípa」として公開されている） | 不可 |

**「ライセンス表記が無い」は「自由」ではなく「許可が無い」。** 既定は全権利留保であって、
表記の不在は許諾ではない。

GitHub Releases / R2 / gh-pages のどれに置くかは**この判断の下流**にあり、置き場所を変えても
再配布であることは変わらない。**配るならデータではなくレシピ** — 名前・元 URL・作者・
ライセンス・変換コマンド・期待される SHA と splat 数を書いた索引を置き、利用者が自分で
取得して自分で変換する。ホスティングの容量問題もそれで消える。

（`.ply` を落として自分で飛ぶのは別の話。制約がかかるのは**再公開**のほう。）

### 飛ぶなら「被写体周回」ではなく「室内を歩き回った」キャプチャを選ぶ

**bonsai は床が溶ける。** 欠損ではない — y=0.0〜0.5 に 261,170 splats あり、XZ の 88% を
覆っている。上から見れば床はある。だがドローン目線の浅い角度では滲んで使えない。

原因は撮り方。bonsai（Mip-NeRF 360）は**盆栽とテーブルの周りを回っただけで、床に
カメラを向けていない**。浅い角度からしか見られていない面は、その方向に引き伸ばされた
ガウシアンとして復元される。**真上からは埋まって見え、接地目線では溶ける。**

Y オフセットで持ち上げても直らない（位置の問題ではないため）。実際に 1m 上げて確認済み。

**飛行用には室内を移動しながら撮ったキャプチャ（playroom / drjohnson 系）を使う。**
新規に撮るなら、被写体だけでなく**床にレンズを向けたパスを必ず入れる**こと。

### 投入前の .ply を整える（全文は docs/alignment.ja.md）

**COLMAP 由来のデータは向きもスケールも任意。** `PlyExporter` は向きを変えないので、
直すのは変換ではなく投入前の `.ply`。向き合わせは
[superspl.at/editor](https://superspl.at/editor) の正射影ビューで目視。

踏むと高くつく 4 つだけここに置く。理由と検算は alignment ドキュメント：

- **3DGS は Unity で必ず鏡像になる。** 右手系 Y-down と左手系 Y-up の差で、
  UnityGaussianSplatting は軸変換をしない。`--mirror y`（鏡映、行列式 -1）が上下反転と
  鏡像を同時に直す。**`--rotate 180,0,0` では原理的に直らない**（行列式 +1）。
  被写体だけ見ていると気づかないので、**文字や左右非対称なもので判定する**
- **床の自動検出（`--up`）は動かない。** RANSAC を 3 通り試して 3 回とも壁を床と誤検出。
  しかも `tilt 1.4°` のようなもっともらしい数字を返す
- **crop はしない。** 内側から撮った部屋では壁が外周そのもの。playroom で 28%
  （54 万 splats）が消えた。`tools/crop_ply.py` はツールごと削除済み。破片が邪魔なら
  `--bounds` で箱を明示する
- **巨大 splat はサイズで切る**（`--max-sigma 5`）。位置でも連結性でもない —
  extent 1.8 個分の幅がある splat は定義上すべてに接続している。utlida では 178 個
  （0.004%）が描画面積の 60% を占めていた
- **`splat-transform` は読み込み時に Z 軸 180 度回転を掛ける** — ドキュメント通りで、
  `(x, y, z)` が `(-x, -y, z)`。`--mirror y` の `(x, -y, z)` とは **X の符号だけ違う**。
  そこから出たメッシュをそのまま Unity に置くとキャプチャの鏡像を包む。X を反転し、
  巻き順も 3 個ずつ逆順にすること（行列式 -1 で全面が内向きになるため）。
  **ここは一度「実際は Y 反転だけ」と逆に書かれていた** — `.voxel.json` のヘッダを
  `.collision.glb` の頂点と同じ座標系だと思って測っていた。決着は AABB 残差の比較
  （Z rot 180 が 0.12、Y flip が 1.06、8.9 倍差）。**同じデータの IoU は 0.203 対 0.193 で
  決着しない**ので、向きの判定に IoU は使わない

### 検証は目視でなく数値で（全文は docs/verification.ja.md）

**このプロジェクトで目視レビューは一度も欠陥を捕まえていない。** 鏡像も、残骸 chunk.bin も、
正射影カメラも、全部「それらしい絵」を出した。道具は 3 つある：

| 道具 | 何を測るか |
|---|---|
| `tools/verify_orientation.py` | 楕円体フレームを ply と `other.bin` から再構成して角度差。全シーン約 0.10°（10bit 量子化の下限） |
| `tools/compare_with_webref.sh` | 独立実装（antimatter15/splat）に同じカメラで描かせて引き算。IoU 0.94 |
| `tools/compare_renders.py` | 2 枚の差分。8 通りの向きを試して一致するものを報告 |

**差分画像は面が黒く輪郭だけ光るのが正常。** 面が光ったら系統的な誤り。

**ただし「違うデータ」を比べるときは IoU の読み方が変わる。** 同一実装・同一データなら
差＝バグだが、除去前後の比較では IoU が下がるのは意図した結果。**方向を分けて測る** —
消えた画素・増えた画素・それぞれの元の明るさとコントラスト。霞なら暗く平ら、シーン本体なら
残った画素と同じ統計になる。

**正射影カメラで 3DGS を描いてはいけない。** シェーダーは透視投影のヤコビアンで共分散を
射影するので、正射影では全 splat が誤ったサイズと剪断になる。エラーは出ず、ただぼやける。
`RenderViews` / `RenderCompare` は画角 4° の透視投影を遠くから当てている。

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
  "chunkCount": 0,
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
- `chunk.bin` は `ChunkInfo` の配列。**使うかどうかは `chunkCount` が決める**
- **`posFormat` は座標空間を語らない。** `Float32` は格納幅の意味で、chunk 付きのシーンは
  そこに 0..1 のチャンク相対値を入れる。`Float32` から「絶対座標」を推論すると、chunk を
  捨ててシーン全体を原点の塊に潰す
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
  SplatCollision.cs      collision.bin -> MeshCollider（.ply は読み込み時に Y 鏡映）
  SplatCollisionView.cs  コリジョン殻の描画（半透明＋背面カリング / ワイヤー）
  SplatBackdrop.cs       キャプチャを黒い箱で囲う
  TrackName.cs     ロード中のトラック名をランタイムに問い合わせる
  TrackBindings.cs トラック名 -> GS の対応表（bindings.json）
  TrackProbe.cs    難読化されたゲームから文字列の在処を探す調査用
  WebControl.cs    HTTP サーバー（操作 API と vdgs/ui/ の静的ファイル）
  WebUi.cs         vdgs/ui/ が無いときのフォールバック HTML
  VdgsPaths.cs     予約名 ui とパストラバーサル拒否
  SplatMetaFile.cs GPU バッファを開かない meta 読み
  PerfLog.cs       フレームタイム記録
  PostProcessFix.cs  D3D12 強制の副作用対応（未解決、記録のみ）
```

フロントは `web/`（Vite + React）。成果物は `<game>/vdgs/ui/`。ディレクトリ名 `ui` は
シーンにならない。

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

`http://<host>:8777/` でプラグインが HTTP サーバーを立てる（`WebControl` + `web/`）。
見た目は測量野帳（紙色、セリフ、朱のトンボ）と、ガウシアンが漂う particles の 2 案。ヘッダで切替。カードは使わない。静的ファイルは
`<game>/vdgs/ui/`。無いときは `WebUi.cs` が短い案内だけ出す。

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
  **誤り**だった。真相は起動スクリプトの末尾（`開発フロー` 参照）

### UI のセキュリティ（軽く扱わないこと）

**トラック名は攻撃者が書ける文字列。** VelociDrone はコミュニティのトラックを
ダウンロードでき、その名前がそのまま UI に表示される。サーバーは `http://*:8777/`
で LAN 全体に開いている。

- **`innerHTML` / `dangerouslySetInnerHTML` に動的な値を入れない。** React のテキストで
  組む。一度 `innerHTML` で書いてしまい、`<img src=x onerror=...>` という
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

## 制約と、いまも踏める罠

構成の理由（Unity を 2 本使う、ScriptableObject を捨てた、AssetBundle だけでは足りない）
は docs/ARCHITECTURE.ja.md。ここは踏むと高くつくものだけ。

**`-force-d3d12` 必須。** ソートの compute が SM6 の wave intrinsics を 41 箇所使う。
`-force-vulkan` は**ゲーム自身**が描けない（VelociDrone が Vulkan 向けにビルドされていない）。

**シェーダーは Windows の Unity 2021.3.45f2 でしか焼けない。** 罠が 2 つ：

- **プロジェクトのグラフィックス API を先に D3D12 にする。** splat シェーダーは
  `#pragma require wavebasic/waveballot` を宣言していて、既定（D3D11）で焼くと
  **エラーを出さずに** unsupported として焼かれる。バンドルは正常にロードでき、
  `shader.isSupported` が false になるだけ。`PlayerSettings.SetGraphicsAPIs` を
  ビルド前に呼ぶ。**焼けたバンドルが 1MB 未満なら失敗**（正常は約 150 万バイト）
- **macOS の Editor は D3D 向けに DXC を回せない** —
  `DXC: can only use DXC to target D3D from the Windows Editor.`

**`-force-d3d12` の副作用でログが埋まる。** ゲームは D3D11 向けビルドなので
PostProcessing v2 の compute が見つからない：

```
Kernel 'KEyeHistogramClear' not found
UnityEngine.Rendering.PostProcessing.LogHistogram.Generate
```

Auto Exposure が毎フレーム例外を投げる。**描画への実害は無い、汚れるのはログだけ。**
**`AutoExposure.active = false` では止まらない**（`src/VDGS/PostProcessFix.cs` で試して失敗。
`PostProcessLayer.RenderBuiltins` が有効・無効に関わらず `LogHistogram.Generate` を呼ぶ）。
対処は `BepInEx.cfg` の `UnityLogListening = false`。ただし副作用で**例外は `Player.log`
にしか出なくなり、そこがこのスパムで数十 MB に膨らむ**（1 セッションで 64MB 観測）。
読むときは必ず除外する：

```powershell
Get-Content $log | Where-Object { $_ -notmatch "KEyeHistogramClear|PostProcessing|^\s*at " }
```

**コリジョンは付くようになった**（`SplatCollision.cs`、実装済み・実機で確認済み）。
焼き方は [docs/SCENES.ja.md](docs/SCENES.ja.md)。設計の数字と捨てた手法は
docs/superpowers/specs/2026-08-18-splat-collision-design.md。

**壁の厚みは速度で決まる。** 物理は 400 Hz、150 km/h で 1 ステップ 0.104 m 進むので
**厚さ 10 cm 未満の壁はすり抜ける**。level set の帯を voxel の 4 倍で焼くのはこのため。

## 残タスク

- **`Medium` 以下が 2.6 倍暗くなる原因は未解明。** 形は正しい（IoU 0.9958）ので色か
  不透明度。upstream のティアか移植側かも不明。**使わないと決めて封印してある**ので
  実害は無い。追うなら Norm8x4 の色デコードから
- **`reprocess.sh` の既定を `High` のままにするか未決。** 速度も見た目も Cluster16k と
  区別がつかず、Cluster16k は 44% 小さい。既定は摩擦の少ない High（k-means 不要）に
  してあり、**配布や VRAM が効く場面で意識して切り替える**という整理にしている
- **完全な High パッキングは未実装。** ランタイムローダーは 132 B/splat で、焼いた版
  （84 B）との差は 7%。Norm16 + chunk + Morton で埋まるが符号化が 5 種類増える。
  **差に見合うかは疑問**
- **異方性から板と針を見分けるには中間軸が要る。** `max/min` だけでは両方 100 になり、
  「針だらけ」という誤診を招く（実際に一度出した）。log 空間で
  `t = (log(mid)-log(min))/(log(max)-log(min))` を取ると `t≈0` が針、`t≈1` が板。
  **3DGS の壁と床は板で、正常**。上から見るとエッジオンで線に見えるだけ
- **キャプチャごとのコリジョン焼き。** 手順は docs/SCENES.ja.md。voxel はシーンで決める
  （細かいほど穴、粗いほど柱が太い）。textilni は 0.06 で穴あり許容、0.14 は柱が太く不採用
- **nelson は voxel 0.06、`--filter-cluster` は使わない。** 原点が空なのでクラスタが
  孤立ブロック 1 個（1 splat）だけ残す。floater だけ落として 8.73M → 45 万三角形。
  `nelson-full.collision.bin`（lod2 も同一座標なので同じファイルをコピー済み）。
  `--reverse` は未確認。Web UI の show solid で中から壁が見えるか見てから決める

## 参考

- [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting) — Mac M1 Max で 46FPS の実測あり
- [antimatter15/splat](https://github.com/antimatter15/splat) — 比較用リファレンス（MIT、単一ファイル WebGL）
- [BepInEx releases](https://github.com/BepInEx/BepInEx/releases)
