# VDGS の使い方

VelociDrone に 3D Gaussian Splatting シーンを表示する mod の導入・運用手順。

内部構造・設計判断・踏んだ罠は [AGENTS.md](../AGENTS.md) を見ること。ここは操作手順だけ。

---

## 1. 必要なもの

| | |
|---|---|
| VelociDrone | Unity 2021.3.45f2 ビルド（1.16 以降で確認） |
| GPU | **D3D12 対応**（DX11 では動かない。理由は後述） |
| BepInEx | 5.4.23.5 win_x64 |
| Unity 2021.3.45f2 | シェーダー AssetBundle を焼くため。**Windows 必須** |
| Unity 2022.3.x | `.ply` を変換するため。Mac でも可 |

---

## 2. インストール

### 2-1. BepInEx

[BepInEx 5.4.23.5 win_x64](https://github.com/BepInEx/BepInEx/releases) をゲームフォルダに展開する。

```powershell
$app = '<VelociDrone>\app'
Invoke-WebRequest 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip' -OutFile "$env:TEMP\bepinex.zip"
Expand-Archive "$env:TEMP\bepinex.zip" -DestinationPath $app -Force
```

一度ゲームを起動して終了すると `BepInEx\config\BepInEx.cfg` が生成される。

**ログを見たいなら** `BepInEx.cfg` の末尾に足す（5.4.23 はディスクログが既定で無効）：

```ini
[Logging.Disk]
Enabled = true
LogLevel = Fatal, Error, Warning, Message, Info

[Logging]
UnityLogListening = false
```

`UnityLogListening = false` は必須に近い。`-force-d3d12` で起動するとゲーム側の
Auto Exposure が毎フレーム例外を投げ、これを切らないとログが埋まる（実害は無い）。

### 2-2. シェーダー AssetBundle

**Windows の Unity 2021.3.45f2 でしか焼けない。** macOS の Unity は D3D 向けの DXC
コンパイルを拒否し、エラーを出さずに空のシェーダーを吐く。

```powershell
# unity/VDGSBundler をプロジェクトとして開き、2段階で実行する
Unity.exe -batchmode -quit -nographics -projectPath <VDGSBundler> `
          -executeMethod BuildBundles.SetGraphicsApis -logFile -
Unity.exe -batchmode -quit -nographics -projectPath <VDGSBundler> `
          -executeMethod BuildBundles.BuildWindows -vdgsOut <出力先> -logFile -
```

できた `vdgs-shaders` を `<VelociDrone>\app\vdgs\vdgs-shaders` に置く。

**サイズが 1MB 以上あることを必ず確認する。** 数十 KB なら中身が空で、グラフィックス
API の設定漏れかホスト OS の問題。バンドルは正常にロードできてしまい、
`shader.isSupported` が false になるだけなので気づきにくい。

### 2-3. プラグイン

```bash
bash tools/deploy.sh          # ビルド → SSH で転送 → 設置
```

SSH を使わない場合は `dotnet build src/VDGS/VDGS.csproj -c Release` して、
`VDGS.dll` を `<VelociDrone>\app\BepInEx\plugins\` に置く。

---

## 3. 起動

**必ず `-force-d3d12` を付ける。**

```
velocidrone.exe -force-d3d12
```

splat のソートに使う compute shader が Shader Model 6 の wave intrinsics
（`WavePrefixSum` など 41 箇所）を要求する。DX11 には存在しない命令なので、
素で起動すると splat は一切描画されない。

`-force-vulkan` は**使えない**。VelociDrone 自身が Vulkan 向けにビルドされておらず、
ゲームのシェーダーが無くて画面が出ない。

### SSH 越しに起動する場合

SSH シェルはセッション 0 で動き、ウィンドウステーションを持たない。そこから起動すると
DirectX がスワップチェーンを作れず、Unity は Mono のロードすら終わらずに死ぬ。
タスクスケジューラで対話セッションに投げる必要がある。

`tools/launch-win.ps1` がそれをやる：

```bash
scp tools/launch-win.ps1 <host>:C:/Users/<user>/launch.ps1
ssh <host> "powershell -ExecutionPolicy Bypass -File C:\Users\<user>\launch.ps1 -GameArgs '-force-d3d12'"
```

ゲームは起動したまま残る。ログを読んで自動終了させたい場合は
`tools/capture-win.ps1`（起動 → 待機 → スクリーンショット → 終了）を使う。

同梱の Windows 用スクリプト：

| ファイル | 用途 |
|---|---|
| `tools/launch-win.ps1` | 対話セッションで起動して残す |
| `tools/capture-win.ps1` | 起動 → スクリーンショット → 終了（動作確認用） |
| `tools/build-shaders-win.ps1` | シェーダー AssetBundle を焼いてゲームに設置 |

---

## 4. splat データを入れる

### 4-1. 変換

```bash
Unity -batchmode -quit -nographics -projectPath unity/VDGSConverter \
      -executeMethod PlyExporter.Run \
      -vdgsInput /abs/path/scene.ply \
      -vdgsOutput /abs/path/build/splats/<name> \
      -vdgsQuality Medium -logFile -
```

`-vdgsQuality` は `VeryHigh` / `High` / `Medium` / `Low` / `VeryLow`。
Medium で 100 万 splats がおよそ 45MB になる。

### 4-2. 破片を落とす（推奨）

3DGS の再構成は被写体の周りに必ずゴミのガウシアンを撒く。飛行中は宙に浮いた
破片として見えるので、変換前に切る：

```bash
python3 tools/crop_ply.py in.ply out.ply --percentile 5   # 外周 5% を落とす
python3 tools/crop_ply.py in.ply --stats                  # 分布だけ見る
```

bonsai の実例では 25% が破片で、落とすとバウンディングボックスが
44x43x48 から 21x17x18 に締まった。

### 4-3. 向きとスケールを合わせる

**これが実データで一番手間のかかる工程。** COLMAP 由来のキャプチャは上下が逆だったり
床が傾いていたりし、1 unit が何メートルかも決まっていない。

**[SuperSplat](https://superspl.at/editor) でやる**のが確実：

1. `.ply` をブラウザにドラッグ
2. **View Cube の円をクリック**して正射影に切り替える（正面・側面）。
   正射影なら床が水平かどうかが遠近感に邪魔されずに分かる
3. Scene Manager で選択し、**TRANSFORM パネル**で回転を数値入力
4. 部屋の高さが実寸（2.4〜2.7m 程度）になるようスケールを合わせる
5. `.ply` で書き出す

大きいファイルが重い場合は間引いたプレビューで角度を決められる：

```bash
python3 tools/align_ply.py big.ply preview.ply --rotate 0,0,0 --sample 150000
```

角度が分かったら、フル解像度に同じ回転を適用する：

```bash
python3 tools/align_ply.py in.ply out.ply --rotate -12,0,3 --ceiling 2.6
```

`--ceiling` を渡すと、その高さになるようスケールを決めて床を y=0 に落とす。
回転は**各ガウシアンの向きにも適用される**（位置だけ回すと splat が傾いたままになる）。

**注意点：**

- **SuperSplat の書き出しは Y が反転している。** エディタ上で正しく立っていても、
  ply は Unity と上下が逆。**必ず `--rotate 180,0,0` を通すこと**：

  ```bash
  python3 tools/align_ply.py supersplat-export.ply out.ply --rotate 180,0,0
  ```

  `align_ply.py` は Y 方向の密度を見て、上下が逆なら警告する（部屋のキャプチャでは
  床が最も密なので、最密面が上半分にあれば反転している）
- **`PlyExporter` は向きを変えない。** 逆さまに見えるならデータか書き出しが原因
- **SuperSplat の書き出しは点の順序を変える。** 変換前後を突き合わせて回転を
  逆算することはできないので、TRANSFORM パネルの数値を控えておくこと
- `align_ply.py --up` による床の自動検出は**動かない**（壁を床と誤検出する）。
  詳細は [AGENTS.md](../AGENTS.md)

### 4-4. 配置

`<VelociDrone>\app\vdgs\<name>\placement.json`：

```json
{
    "position": [0.0, 0.0, 0.0],
    "rotation": [0.0, 0.0, 0.0],
    "scale": 1.0
}
```

**位置合わせ機能は mod にはない。** GS 側が正しい座標・スケールで作られている前提で、
`placement.json` はオフセットの微調整用に残してあるだけ（ゲーム内から変更する手段はない）。
座標を合わせるのは撮影・学習側の仕事。

`scale` は COLMAP 由来のデータだと任意単位になるので、必要なら手で書き換える。

### 4-5. トラックとの紐付け

**どの GS をどのトラックで出すかは、トラック名で決まる。**

`<VelociDrone>\app\vdgs\bindings.json`：

```json
{
  "2026 Fusion Flight Festival - Presented by Neos": ["shibuya"],
  "Split-S": ["luigi", "bonsai"]
}
```

手で書いてもいいが、ゲーム内から作るほうが早い（§5）。

- **紐付けの無いトラックでは何も表示されない。** 間違った GS を出すより無害だから
- 1つのトラックに複数の GS を紐付けられる
- シーナリー（Empty Scene Day など）単位ではなく**トラック単位**。同じシーナリー上に
  何本もトラックが載るため

`<VelociDrone>\app\vdgs\autospawn`（空ファイル）が無いと自動表示そのものが無効になる。

---

## 5. 操作（ブラウザ）

ゲームが起動すると、mod が **`http://<ホスト>:8777/`** で操作用の Web UI を出す。
別マシンからでも開ける（Tailscale 越しなど）。Parsec でゲーム画面を見ながら、
手元のブラウザで操作するのが想定運用。

```
┌─ VDGS Control ──────────────────────────────────┐
│  Current track                                  │
│  2026 Fusion Flight Festival - Presented by Neos │
│  bound to shibuya                               │
├─────────────────────────────────────────────────┤
│  Splat scenes on this machine                   │
│  [shown]  shibuya   934,442 splats              │
│  [show ]  luigi      14,526 splats              │
│                                                 │
│  [Bind shown splat to this track]               │
│  [Unbind this track]  [Hide all]                │
├─────────────────────────────────────────────────┤
│  Bindings                                       │
│  <track名>  →  shibuya          [remove]        │
└─────────────────────────────────────────────────┘
```

**ゲームのキーは一切奪わない。** トラックエディタの矢印キーも F7（シーン保存）も
そのまま使える。UI は 1.5 秒ごとに自動更新され、ゲーム内でトラックを変えると
「Current track」が追従する。

### 紐付けの手順

1. トラックをロードする（プレイでもエディタでもよい）
2. UI で出したい GS の **show** を押す
3. **Bind shown splat to this track**
4. 以降そのトラックをロードすると、自動でその GS が出る

### 開発者向けキー（残置）

| キー | 動作 |
|---|---|
| F9 | 環境情報を `vdgs-probe.log` に追記 |
| F10 | シーン構造を `vdgs-hierarchy.txt` にダンプ |
| F12 | トラック名の探索ダンプ（`vdgs-track.txt`、検索語は `vdgs/needle.txt`） |

F5・F6・F7・F8 は**使っていない**。F7 はトラックエディタのシーン保存に
割り当て済みで衝突する。

### HTTP API

UI が使っているものと同じ。スクリプトから叩ける。

| | |
|---|---|
| `GET /api/status` | 現在のトラック、表示中の GS、利用可能な GS、全紐付け |
| `POST /api/load` | `{"splat":"name"}` — その GS だけを表示 |
| `POST /api/unload` | `{}` — 全部隠す |
| `POST /api/bind` | `{"splats":["name"]}` — 現在のトラックに紐付け |
| `POST /api/unbind` | `{}` で現在のトラック、`{"track":"name"}` で任意のトラック |

**POST には必ずボディを付けること。** `HttpListener` は `Content-Length` の無い POST を
mod のハンドラに渡す前に `411 Length Required` で弾く。`curl -X POST .../api/unload` は
失敗し、`curl -X POST .../api/unload -d '{}'` は成功する。

---

## 6. 出力されるファイル

`<VelociDrone>\app\` 直下：

| ファイル | 内容 |
|---|---|
| `vdgs-probe.log` | 環境情報、シェーダーの状態、スポーン結果 |
| `vdgs-perf.log` | 5 秒ごとのフレームタイム（fps / avg / worst / splat 数） |
| `vdgs-track.log` | トラック名の検出、紐付け、GS の出し入れの履歴 |
| `vdgs-hierarchy.txt` | F10 で吐いたシーン構造 |
| `vdgs-track.txt` | F12 で吐いたトラック名の探索結果 |
| `BepInEx\LogOutput.log` | BepInEx とプラグインのログ |

---

## 7. うまくいかないとき

| 症状 | 原因と対処 |
|---|---|
| 何も表示されない | `-force-d3d12` を付け忘れ。`vdgs-probe.log` の `graphicsDeviceType` を確認 |
| `shaders NOT READY` | バンドルが空。サイズが 1MB 未満なら焼き直し（§2-2） |
| `shader.isSupported=false` | 同上。macOS で焼いた D3D12 バンドルは必ずこうなる |
| プラグインが読まれない | SSH から起動していないか確認（§3）。`BepInEx\config\` が生成されていなければ Chainloader に到達していない |
| 表示が破片だらけ | 元データの外れ値。`crop_ply.py` で切る（§4-2） |
| 小さすぎ / 大きすぎ | `placement.json` の `scale`。COLMAP のスケールは任意 |
| 表示した瞬間に固まる | 数十 MB を GPU に一括アップロードするため。**飛ぶ前に表示させておく**。実測 2.9 秒 |
| ログが例外で埋まる | `UnityLogListening = false`（§2-1）。実害は無い |

---

## 8. 注意

**リーダーボードとマルチプレイでは使わないこと。**

VelociDrone には `ACTk.Runtime.dll`（Anti-Cheat Toolkit）が同梱されている。
実際に検出に使われているかは未確認だが、改造クライアントでタイムを投稿するのは
規約違反にあたる。ローカル飛行専用と考えること。

PatchKit のアップデートが走るとプラグインとシェーダーは消える。`tools/deploy.sh` で
入れ直す。BepInEx 本体も消えた場合は §2-1 からやり直し。
