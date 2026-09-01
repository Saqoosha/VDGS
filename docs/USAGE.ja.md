# VDGS の使い方

*[English](USAGE.md)*

VelociDrone に 3D Gaussian Splatting シーンを表示する mod の導入・運用手順。

内部構造・設計判断は [ARCHITECTURE.ja.md](ARCHITECTURE.ja.md)、環境固有の実測値と
踏んだ罠は [AGENTS.md](../AGENTS.md)。キャプチャの追加（`.ply` とコリジョン）は
[SCENES.ja.md](SCENES.ja.md)。ここは導入と操作。

---

## 1. 必要なもの

**動かすだけなら：**

| | |
|---|---|
| VelociDrone | Unity 2021.3.45f2 ビルド（1.16 以降で確認） |
| GPU | **D3D12 対応**（DX11 では動かない。理由は §3） |
| BepInEx | 5.4.23.5 win_x64 |

**mod をビルドするなら：**

| | |
|---|---|
| .NET SDK | `src/VDGS` のコンパイル用 |
| Unity 2021.3.45f2 | シェーダー AssetBundle を焼くため。**Windows 必須** |
| Unity 2022.3.x | 任意。オフラインの `.ply` コンバータ用 |

mod は `.ply` を直接読むので、**変換は必須ではない**。ディスク上のサイズを小さくしたい
とき、ロードを速くしたいときだけ変換する。

---

## 2. インストール

**いちばん簡単なのは companion アプリ。** `VDGS.exe` を起動して `INSTALL MOD` を押すと、
DLL・焼き済みシェーダーバンドル・操作 UI が入る（**アプリが中に持っている**ので、zip を
探す必要はない）。キャプチャは `02 GET` から落とせて、トラックの登録と紐付けまで一度に済む。
`FLY` は `-force-d3d12` を必ず付けて起動する。**BepInEx だけは先に自分で入れる**（2-1）。

以下は zip から手で入れる場合。リリースの `vdgs-mod-<version>.zip` は
DLL・焼き済みシェーダーバンドル・操作 UI の 3 つを入れてあるので、自分でビルドする
必要はない：

1. BepInEx を入れる（2-1）
2. `vdgs-mod-*.zip` を展開して、中の `BepInEx/` と `vdgs/` をゲームの `app/` に重ねる
3. キャプチャを入れる（`vdgs-scene-*.zip` を同じく `app/` に重ねる。§4-7）

以降の 2-2 と 2-3 は**自分でビルドする場合**の手順。

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

Mac から SSH が通るなら `bash tools/bake-shaders.sh` が往復とサイズ検査までやる。

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

### SSH 越しの起動

SSH シェルはセッション 0 で動き、ウィンドウステーションを持たない。そこから起動すると
DirectX がスワップチェーンを作れず、Unity は Mono のロードすら終わらずに死ぬ。
タスクスケジューラで対話セッションに投げる必要がある。

`tools/launch-win.ps1` がそれをやる：

```bash
bash tools/launch-win.sh          # スクリプトを送って実行する
```

ゲームは起動したまま残る。ログを回収して自動終了させたいときは `-Diagnose` を付ける。

同梱の Windows 用スクリプト：

| ファイル | 用途 |
|---|---|
| `bash tools/launch-win.sh` | 対話セッションで起動して残す（`-Diagnose` でログを出して停止） |
| `tools/capture-win.ps1` | 起動 → スクリーンショット → 終了（動作確認用） |
| `tools/build-shaders-win.ps1` | シェーダー AssetBundle を焼いてゲームに設置 |
| `bash tools/bench-win.sh` | 実機で描画時間を測る |

---

## 4. splat データの投入

### 4-1. 手っ取り早く：`.ply` を置く

```
<VelociDrone>\app\vdgs\myscene.ply
```

これだけが絵の手順。壁と床にはコリジョンが要る。焼き方は
[SCENES.ja.md](SCENES.ja.md)。mod はヘッダだけ読んで splat 数を UI に出し、表示したときに本体を読んで
GPU に上げる。

実測ロード時間（RTX 3060）：41.5 万 splats で 0.32 秒、217 万で 1.6 秒、318 万で 2.3 秒。
描画はオフライン最良フォーマットの 7% 遅れ。詳細は [ply-loading.ja.md](ply-loading.ja.md)。

配置を書くなら、同じ場所に `myscene.placement.json` を置く。

**リモートの Windows 機に置くとき、`deploy.sh` は使えない。** あれが同期するのは
`build/splats/<name>/` → `<game>\vdgs\<name>\` の**変換済みディレクトリだけ**で、
直置きの `.ply` と `.collision.bin` は対象外。そして**ゲームパスにはスペースが入り、
リモートの既定シェルは PowerShell でバックスラッシュをエスケープと見ない**ので、
`scp` に宛先を直接渡すと**エラーも出さずにファイルが消える**。`deploy.sh` と同じ作法で
逃げる — スペースの無い `vdgs-stage\` に scp してから `Copy-Item` で置く。

### 4-2. 変換する：ディスクが小さく、ロードが速い

```bash
bash tools/reprocess.sh [scene]
```

手で叩くなら：

```bash
Unity -batchmode -quit -nographics -projectPath unity/VDGSConverter \
      -executeMethod PlyExporter.Run \
      -vdgsInput /abs/path/scene.ply \
      -vdgsOutput /abs/path/build/splats/<name> \
      -vdgsQuality High -logFile -
```

**`High` を使うこと。** コンバータの既定値。84 バイト/splat で、RTX 3060 で最も速く、最も忠実。
`VeryHigh` は 236 バイト/splat 払って見た目が変わらず、`Medium` 以下はシーンによって
極端に暗くなる。根拠は [performance.ja.md](performance.ja.md)。

出力ディレクトリ（`meta.json` + 5 バイナリ）を `<VelociDrone>\app\vdgs\<name>\` に置く。

### 4-3. 向きとスケール

**これが実データで一番手間のかかる工程。** COLMAP 由来のキャプチャは必ず鏡像になり、
上下が逆だったり床が傾いていたりし、1 unit が何メートルかも決まっていない。

鏡像は無条件に起きる。直し方は 1 つ：

```bash
python3 tools/align_ply.py in.ply out.ply --mirror y
```

Y の反転（鏡映）が上下の反転と鏡像の解消を同時にやる。`--rotate 180,0,0` は回転なので
原理的に鏡像を直せない。経緯は [alignment.ja.md](alignment.ja.md)。

残りは [SuperSplat](https://superspl.at/editor) でやる：

1. `.ply` をブラウザにドラッグ
2. **View Cube の円をクリック**して正射影に切り替える（正面・側面）。
   正射影なら床が水平かどうかが遠近感に邪魔されずに分かる
3. Scene Manager で選択し、**TRANSFORM パネル**で回転を数値入力
4. 部屋の高さが実寸（2.4〜2.7m 程度）になるようスケールを合わせる
5. `.ply` で書き出す

大きいファイルが重いなら、間引いたプレビューで角度を決めてからフル解像度に同じ回転を
適用する：

```bash
python3 tools/align_ply.py big.ply preview.ply --sample 150000
python3 tools/align_ply.py in.ply out.ply --rotate -12,0,3 --ceiling 2.6
```

`--ceiling` を渡すと、その高さになるようスケールを決めて床を y=0 に落とす。
回転は**各ガウシアンの向きにも適用される**（位置だけ回すと splat が傾いたままになる）。

注意点：

- **SuperSplat の書き出しは Y が反転している。** エディタ上で正しく立っていても
  ply は上下が逆。`align_ply.py` が密度分布を見て警告する
- **`PlyExporter` は向きを変えない。** 逆さまに見えるならデータか書き出しが原因
- **SuperSplat の書き出しは点の順序を変える。** 変換前後を突き合わせて回転を
  逆算することはできないので、TRANSFORM パネルの数値を控えておくこと
- **`--up` による床の自動検出は動かない**（壁を床と誤検出する）。
  詳細は [alignment.ja.md](alignment.ja.md)
- **crop はしない。** パーセンタイルで外周を切ると、内側から撮った部屋では壁が消える。
  破片が目障りなら `--bounds` で箱を明示する
- **巨大な splat が個別に見えるなら**、3DGS が制約の無い領域を膨らませた跡。数個で描画面積の
  大半を占めることがある。位置ではなくサイズで切る：`--max-sigma 5`。出力パス無しで実行すれば
  報告だけ出るので、**先に測る**。詳細は [alignment.ja.md](alignment.ja.md)

### 4-4. 配置

`<VelociDrone>\app\vdgs\<name>\placement.json`（`.ply` の場合は `<name>.placement.json`）：

```json
{
    "position": [0.0, 0.0, 0.0],
    "rotation": [0.0, 0.0, 0.0],
    "scale": 1.0
}
```

**スケールと高さは Web UI** にあって、このファイルに書く。回転はファイルが来る前に
SuperSplat で合わせる。`placement.json` はスライダーが届かないものの最終手段。

### 4-5. コリジョン

メッシュの無いキャプチャはすり抜けになる。`myscene.ply` の隣に
`myscene.collision.bin`（変換済みディレクトリなら中の `collision.bin`）を置く。
焼き方は [SCENES.ja.md](SCENES.ja.md)。

UI では `solid` がドローンを止める。`show wire` / `show solid` が殻を描く。
ファイルを差し替えたらチェックボックスではなく、unload → load。

### 4-6. トラックとの紐付け

**どの GS をどのトラックで出すかは、トラック名で決まる。**

`<VelociDrone>\app\vdgs\bindings.json`：

```json
{
  "Empty Scene Day": ["myscene"],
  "Split-S": ["myscene", "other"]
}
```

手で書いてもいいが、ゲーム内から作るほうが早い（§5）。

**自分でコースを組んで配るなら** [TRACKS.ja.md](TRACKS.ja.md) — 名前・シーナリー・書き出し・公開の通し。

- **紐付けの無いトラックでは何も表示されない。** 間違った GS を出すより無害だから
- 1 つのトラックに複数の GS を紐付けられる
- シーナリー（Empty Scene Day など）単位ではなく**トラック単位**。同じシーナリー上に
  何本もトラックが載るため

キャプチャ抜きで飛びたいときは `-force-d3d12` を付けずに起動する。VelociDrone 自身の
ランチャーは付けない。D3D12 でないと splat シェーダーが unsupported になるので、キャプチャは
読み込まれもしない。

---

### 4-7. 配布されたキャプチャを受け取る

`vdgs-scene-*.zip` は展開して中の `vdgs/` をゲームの `app/` に重ねるだけ。中身は変換済み
ディレクトリ一式と、あれば `collision.bin` と `placement.json`。

**つまずくのはここだけ — 紐付けはトラック「名」で決まる。** 同梱の
`bindings.sample.json` はトラックが配布時の名前のままであることを前提にしている。
Track Manager で落としたあとに名前を変えたなら、**自分の名前で** 紐付け直す（§5 のブラウザ UI が早い）。

`placement.json` は**そのトラックに合わせた位置**なので、自分でコースを組むなら
ブラウザ UI で調整する（変更は自動保存される）。

## 5. 操作（ブラウザ）

ゲームが起動すると、mod が **`http://<ホスト>:8777/`** で操作用の Web UI を出す。
LAN 上の任意のマシンから開ける。Parsec でゲーム画面を見ながら、手元のブラウザで
操作するのが想定運用。UI だけ変えたあとは `bash tools/deploy.sh --ui` で
`web/dist/` を `<game>/vdgs/ui/` に置く。プラグインの再ビルドは不要。

```
┌ VDGS · local · 01 control / 02 library ─────────┐
│  01 current track                               │
│  Empty Scene Day                                │
│  bound → myscene                                │
│  [Bind shown]  [Unbind]  [Hide all]             │
│  02 on screen                                   │
│  myscene   1,916,379 splats                     │
│  [x] box  [x] solid  [hide mesh]                │
│  Scale  ────│────  1.00×                        │
│  Height ────│────  0.00m                        │
│  03 bindings                                    │
│  <track名>  →  myscene          [remove]        │
└─────────────────────────────────────────────────┘

Library はこのマシン上のキャプチャの番号付き目録（検索、splat 数、フォーマット、
サイズ、コリジョン）。Show で表示する。スライダーは Control 側。
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
| `POST /api/backdrop` | `{"splat":"name","on":true}` — 黒い箱 |
| `POST /api/collision` | `{"splat":"name","on":true}` — MeshCollider |
| `POST /api/collisionview` | `{"splat":"name","mode":"wire"}` — hide / solid / wire |
| `POST /api/transform` | `{"splat":"name","scale":1.0,"y":0}` — スケールと Y。`placement.json` に書く |

**POST には必ずボディを付けること。** `HttpListener` は `Content-Length` の無い POST を
mod のハンドラに渡す前に `411 Length Required` で弾く。`curl -X POST .../api/unload` は
失敗し、`curl -X POST .../api/unload -d '{}'` は成功する。

---

## 6. 出力されるファイル

`<VelociDrone>\app\` 直下：

| ファイル | 内容 |
|---|---|
| `vdgs-probe.log` | 環境情報、シェーダーの状態、スポーン結果 |
| `vdgs-perf.log` | 5 秒ごとのフレームタイム（fps / avg / worst / splat 数）。起動をまたいで追記、`=== session` と `--- shown:` の区切り付き |
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
| 破片が飛び散る | 前回の変換の `chunk.bin` が残っている。deploy が消すようになったので入れ直す。それでも出るなら元データの外れ値で、`align_ply.py --bounds` で箱を明示する |
| 原点に潰れた塊になる | 逆。chunk 付きのデータから `chunk.bin` が欠けている |
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
