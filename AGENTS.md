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

### 実測パフォーマンス（RTX 3060、2026-08-17）

```
time      fps    avg_ms  worst_ms  splats   scenes
02:28:08  17.2   58.17   2885.15   1172307  3     ← スポーン直後（GPU バッファ確保）
02:28:13  60.0   16.67     16.67   1172307  3     ← 安定後
```

**117 万 splats で 60 FPS 張り付き、worst frame も 16.67ms。** 60.0 ちょうどなのは VSync の
上限に当たっているためで、実際の余力はこれより上。

スポーン直後の 1 フレームだけ 2.9 秒かかる（`GraphicsBuffer.SetData` で 50MB 超を
アップロードするため）。飛行中に GS を切り替えると必ずスタッターになるので、
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

### 向きとスケールは元データ依存（変換は何も変えない）

**COLMAP 由来のデータは向きもスケールも任意。** 上下が逆だったり、床が傾いていたり、
1 unit が何メートルか決まっていない。

**`PlyExporter` は向きを一切変えない**（luigi が上下逆に見えたので変換を疑ったが、
SuperSplat で開いても同じく逆さまだった → 元データがそういう向き）。
つまり直すべきは変換ではなく、投入前の `.ply` そのもの。

**向き合わせは SuperSplat でやる。** [superspl.at/editor](https://superspl.at/editor) は
1.8 から正射影ビュー（View Cube の円をクリック）を持ち、TRANSFORM パネルで回転・
スケールを数値入力できる。正射影で見れば床が水平かどうか一目で分かる。

**床の自動検出は諦めた。** `tools/align_ply.py` に RANSAC を実装したが、3 回試して
3 回とも壁を床と誤検出した：

- drjohnson は bounds が `7.6 x 4.7 x 10.7` で人間なら一目で Y 軸が高さと分かるのに、
  無制約の探索は `x+` を返した（壁の方が広く、インライアが多い）
- 上方向を教えた上でも、`tilt 1.4 度` という**もっともらしい数字**を出しながら
  実際は壁に対する 1.4 度だった。ゲームに入れて初めて「床が壁にある」と発覚
- インライアの反復リファインを足したら精度が上がるどころか `11.9 度 → 23.7 度` と
  悪化した。下部バンドには家具も壁の裾も入るので、反復するほど「点が多い大きな面」に
  引っ張られる

**「点が最も多い平面」と「人間が床と認識する平面」は別物**で、後者は幾何だけからは
決まらない。SuperSplat が正射影を持っている時点で、そちらが正しい道具だった。

`tools/align_ply.py` に残っている使いどころ：

| オプション | 用途 |
|---|---|
| `--rotate X,Y,Z` | ビューアで読んだ角度を正確に適用。**クォータニオンにも適用される** |
| `--sample N` | 間引いてプレビューを作る（SuperSplat に投げやすくする） |
| `--ceiling H` | 天井高からスケールを決め、床を y=0 に落とす |
| ~~`--up`~~ | 床の自動検出。**動かない**。経緯の記録として残置 |

回転は位置だけでなく**各ガウシアンの向きクォータニオンにも適用が必要**。位置だけ回すと
点群は正しく見えるのに全 splat が傾いたままになる。

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

**`InGameChangeTrack.glnoaiifnln` と飛行 HUD（`RaceInfo2/View - Gameplay/TrackName`）**
から読む。多段フォールバックで、片方が死んでも動く（`TrackName.cs`）。

**`EditorManager.nnpnlmbjocf` は使ってはいけない。** 最初に見つかる上に一見正しく
見えるが、これは「最後に *エディタで* 開いたトラック」で、別のトラックをロードして
飛んでも更新されない。実際に別トラックを開いて初めて発覚した。

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

1. **D3D11 では 3DGS が動かない。** aras-p の UnityGaussianSplatting は DX11 サポートを
   削除済み。Windows では D3D12 か Vulkan が必須。
   → `-force-d3d12` / `-force-vulkan` で起動できるか要検証。ダメなら
   `globalgamemanagers` のグラフィックス API リストにパッチ
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
3. **`GaussianSplatAsset` は ScriptableObject。** AssetBundle 経由でロードすると型解決で詰まる。
   splat データは生バイナリとして読み、実行時に GraphicsBuffer へ流す自前ローダを書く
4. **3DGS にコリジョンは無い。** 飛べる壁になる。同じ撮影データからメッシュを抽出して
   invisible collider として置く必要がある

5. **`-force-d3d12` はゲーム本体に副作用がある。** ゲームは D3D11 向けにビルドされて
   いるため、PostProcessing v2 の compute shader が D3D12 では見つからない：

   ```
   Kernel 'KEyeHistogramClear' not found
   UnityEngine.Rendering.PostProcessing.LogHistogram.Generate
   ```

   Auto Exposure（Eye Adaptation）が毎フレーム例外を投げる。

   **実害はない**：117 万 splats を積んだまま 60 FPS が維持され、描画も正常。
   汚れるのはログだけ。

   **`AutoExposure.active = false` にしても止まらない**（`src/VDGS/PostProcessFix.cs` で
   試して失敗）。PostProcessing v2 の `PostProcessLayer.RenderBuiltins` は
   AutoExposure の有効・無効に関わらず `LogHistogram.Generate` を呼ぶため。
   Volume の無効化は成功する（`scanned 1, disabled 1`）のに、例外は増え続ける
   （11,210 件を確認）。同じ手を再発明しないこと。

   実際の対処は **`BepInEx.cfg` の `UnityLogListening = false`**。これで Unity の
   例外が BepInEx ログに転送されなくなる。Unity 自身の `Player.log` には残るが、
   そちらは肥大化しても実害がない

## 参考

- [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting) — Mac M1 Max で 46FPS の実測あり
- [BepInEx releases](https://github.com/BepInEx/BepInEx/releases)
