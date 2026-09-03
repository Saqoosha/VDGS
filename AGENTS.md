# VDGS — 3D Gaussian Splatting inside VelociDrone

VelociDrone に 3D Gaussian Splatting シーンを読み込む mod。BepInEx プラグインとして
コードを注入し、実行時に splat データをレンダリングする。

## 状態

**実データで動作確認済み。Windows と macOS の両方。** 3 シーン同時（計 117 万 splats）を
RTX 3060 で描画してクラッシュなし。ドローン機体との前後関係も半透明ブレンドも破綻しない。
配布まで通っていて、まっさらな機械から 4 クリックで飛べる（「配布は companion アプリ」）。
公開先は https://vdgs.saqoo.sh で、**両 OS の companion が並んでいる**。

```
shader 'Gaussian Splatting/Render Splats'  supported=True
compute 'SplatUtilities'                   supported=True
=> shaders READY
```

### 性能の要点（数字と導出は docs/performance.ja.md）

**フレームの 87% は splat ごとの固定コスト。** 射影・2D 共分散・SH 評価が splat 1 つに
つき 1 スレッド走る。帯域は 7%、ソートは 6%、画素の仕事は 6%。**「バイト/splat を減らせば
速くなる」は一度そう結論して外した** — 3 シーンの実機値に当てはめたのが誤りで、同一
ジオメトリで SH だけ 5.1 倍減らす統制比較を取ったら 6.5% しか動かなかった。

効いた手は 2 つ。どちらも品質を落とさない：

- **視錐台カリング**（`m_FrustumCulling`、既定 on）— 内部視点で 10.7% 減、ピクセル完全一致
- **Float32 を避ける**。実機で `26.83 ms / 37.3 fps` → `17.30 ms / 57.8 fps`

**品質は `High` を使う。** `Medium` 以下は drjohnson が 2.6 倍暗くなる（原因は未解明。
`PlyExporter` の既定は `High`、Medium 以下は警告を出す）。`Cluster16k` は 44% 小さいが
**速度も見た目も差が無く**、k-means に約 10 分かかり `.ply` 直読みでは作れないので、
`reprocess.sh` の既定は摩擦の少ない `High`。

**測定は必ず実機で。** 同じ比較が M1 Max で 6.5%、RTX 3060 で 48%。ユニファイドメモリが
帯域を隠す。ベンチは切り分け用で、判断は実機の値で下す。`<game>/vdgs-perf.log` に 5 秒ごと
追記されるので**飛んで、あとで読むだけ**（読むときの罠は docs/performance.ja.md）。

スポーン直後の 1 フレームだけ止まる（`GraphicsBuffer.SetData` で数十 MB をアップロード
するため）。飛行中に切り替えると必ずスタッターになるので、**飛ぶ前に表示させておく**。

## ターゲット環境

### Windows 機（開発・実行のメイン）

ゲームを走らせる Windows ボックスへ SSH する。リモートのデフォルトシェルは **PowerShell**。
ホスト名は `tools/local.env` の `VDGS_HOST`（gitignore 済み）。リポジトリには書かない。

**このリポジトリが向こうに置くものは全部 `%USERPROFILE%\VDGS\` の下**（`tools/_remote.sh` の
`REMOTE_ROOT`）。中継所も 3GB のバックアップも一度きりのログも、かつてホーム直下に 59 個
散らばっていて見分けが付かなかった。scp のパスは `"$HOST:$REMOTE_ROOT/..."`、送り込む先の
PowerShell は `$REMOTE_ROOT_PS`、`.ps1` 側は `Join-Path $env:USERPROFILE 'VDGS'` で同じ場所を
出す。**scp は書き込み先を作らない**ので、直下へ送る前に `remote_root_mkdir` を呼ぶ。
八月の足場は `VDGS\old\` にまとめてある。

| 項目 | 値 |
|---|---|
| ゲームパス | この機械では `%USERPROFILE%\Downloads\Velocidrone Windows Launcher\app`。上書きは `VDGS_GAME`。**これは既定ではない**（下） |
| ユーザーデータ | `%USERPROFILE%\AppData\LocalLow\velocidrone\velocidrone` |
| Velocidrone | 1.16.0 で確認 |
| Unity | 2021.3.45f2 (88f88f591b2e) |
| スクリプティング | **Mono**（IL2CPP ではない） |
| レンダーパイプライン | **Built-in RP**（URP/HDRP の DLL 無し。PostProcessing v2 + AmplifyColor + Bakery） |
| GPU | RTX 3060 12GB で測定 |
| 描画 API | **Direct3D 11** ← 3DGS には不足。D3D12/Vulkan が必要 |
| exe | x64 |

**VelociDrone に既定のインストール先は無い。** インストーラが存在せず、zip を解凍した場所に
`Launcher.exe` がゲームを落とすので、置き場所を決めるのは利用者。PatchKit はそれをどこにも
記録しない（`%LOCALAPPDATA%\PatchKit` は 32 バイトの `sender_id` 1 個だけ）。

公式ガイドが名指しする推奨先は `C:\VelociDrone`、Desktop、Documents、別ドライブ。
**`Program Files` は公式が避けろと言っている** — ランチャーが自分のフォルダに書き込むので
UAC に止められ、更新が失敗したりログインで固まる。**Steam 配信ではない。**

この表の `Downloads\Velocidrone Windows Launcher\app` は、この機械で zip を解凍した場所で
あって、それ以上の意味は無い。`GameInstall.FindGame` はかつて Steam 3 種と `Program Files` を
候補にしていた — **4 つのうち 3 つは存在しえず、1 つは公式が避けろと言う場所**。何も壊れず、
ただ一度も当たらず、走査が黙って全部を拾っていた。2026-09-01 に一次情報を当てて直した。

### Mac（mod も動く。解析だけの機械ではない）

PatchKit 経由の macOS 版（1.17、arm64 thin、adhoc 署名、同じ Unity 2021.3.45f2）で
**mod が動く。** 2.5M splats が M1 Max で 60fps 張り付き、4.5M で 57〜60fps。
**Windows と同等**。しかも `-force-d3d12` の副作用（ログ埋まり、メニュー放置クラッシュ）が
丸ごと無い。Metal はゲームの正規経路だから。

| 項目 | 値 |
|---|---|
| ゲーム | `~/Library/Application Support/PatchKit/Apps/<hash>/Data/velocidrone.app` |
| GameRootPath | **`.app` の親**（`Data/`）。`vdgs/`・`bindings.json`・ログは全部そこ |
| ユーザー DB | `~/Library/Application Support/com.velocidrone.velocidrone/user11.db`（scene 16 = `BlankCanvas`） |
| 描画 API | Metal。フラグ不要 |

**注入が通る理由は署名にある。** adhoc 署名でハードンドランタイム無し（`codesign -d -vvv`
の flags が `0x2(adhoc)` だけ、entitlements も空）なので `DYLD_INSERT_LIBRARIES` が素通り
する。再署名は要らない。

**BepInEx 公式 5.4.23.5 は Apple Silicon で死ぬ。** arm64 Mono では MonoMod の
`DetourHelper.GetIdentifiable()` が null を返し、preloader が chainloader に届く前に落ちる
— **プラグインは 1 つも読まれず、`preloader_<日時>.log` が 1 枚残るだけ**（BepInEx#1303）。
公式 PR #1288 が 3 箇所のうち 2 つを `Utility.TryDo` で包んだが、`HarmonyInteropFix.Apply()`
が残っていた。パッチ済みの universal ビルドを fork のリリースに置いてある：

```
https://github.com/Saqoosha/BepInEx/releases/tag/v5.4.23.5-vdgs.1
sha256 950d55271c176c732fc896bcdae2750978ef92b940c951aa7fad0eb4251f1d61  (660,321 bytes)
```

**公式版が既に入っている機械では「BepInEx があるか」を見てはいけない。** ファイル構成が
同じなので見分けが付かず、arm64 で死ぬ loader に mod を載せて READY と表示してしまう。
導入時に `BepInEx/vdgs-bepinex-version.txt` を書き、それと**ファイルの実在の両方**を見る
（`bepinex::is_ours`）。印だけ見ると、印が残ってファイルが消えた機械を UI から直せなくなる。

## MOD の仕組み

BepInEx 5.4.23.5 (win_x64) をゲームフォルダに展開。Doorstop 4 が `winhttp.dll` 経由で注入。

**注入は動作確認済み**（`BepInEx/cache/` と `BepInEx/config/` が起動のたびに更新される）。

BepInEx 5.4.23 は **ディスクログがデフォルト無効**で、`BepInEx.cfg` はゲームを一度動かすまで
生成されない。**セクションの不在は「まだ Chainloader に到達していない」という診断情報になる。**
companion が導入時に `[Logging.Disk] Enabled = true` と `UnityLogListening = false` を書くので
（既存の `.cfg` には触らない）、手で足す必要はもう無い。後者の理由は「副作用 1」。

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

# 3. シェーダーバンドルを焼く（D3D12 版。Windows 上で実行）
bash tools/bake-shaders.sh

# 4. ゲームを起動（セッション1、D3D12 強制）
bash tools/launch-win.sh
```

**手順 1 は省ける。** `<game>/vdgs/foo.ply` を置けばプラグインが実行時に読む。
変換済みディレクトリと同名なら**ディレクトリが勝つ**。

**直置き経路は Y を必ず鏡映する。** `PlyLoader` は既定 `mirrorY = true`、`SplatCollision` は
`.ply` なら必ず鏡映するので、**splat とコリジョンは常に一致する**（食い違いは起きない）。
ただし裏を返すと、**すでに床が下向きに整えてあるキャプチャは直置きでは必ず上下逆になる**
（`playroom-nocrop.ply` がそれ）。そういうものは `reprocess.sh` で変換して置く。

**`PlyBench` の数字は `GraphicsBuffer.SetData` の前で止まる。** ゲーム内で体感する停止は
217 万で 2.95 秒、400 万＋SH で 13〜14 秒。**引くのはコールドの値**（同一ブート 2 回目は
約 4 倍速いので、ベンチのループは放っておくと自分に都合のいい数字を出す）。
内訳と焼いた版との 7% 差は docs/ply-loading.ja.md。

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
  `%USERPROFILE%\VDGS` に展開するため）。中身だけ固めると `unpack failed: VDGSBundler missing`
  になり、転送の失敗に見える
- **バンドルが 1MB を切ったら失敗を疑う。** splat シェーダーは `#pragma require
  wavebasic/waveballot` を宣言していて、プロジェクトのグラフィックス API が D3D12 で
  ないと**エラーを出さずに** unsupported として焼かれる。バンドルは正常にロードでき、
  `shader.isSupported` が false になるだけ。正常な値は約 150 万バイト

#### 起動スクリプトは既定でゲームを残す。`-Diagnose` を付けると殺す

`bash tools/launch-win.sh` が `tools/launch-win.ps1` を**毎回送ってから**走らせる — 以前は
向こうに手で置いた版とリポジトリの版が別々に育ち、片方にしかない処理があった。

`-Diagnose` のときだけログを整形して出し、ゲームを止める。**この末尾は元々無条件に走って
いて、「起動して 40 秒ほどで静かに落ちるゲーム」にしか見えなかった** — `Stop-Process -Force`
はダンプもイベントログも残さず `Player.log` を行の途中で切り、固定 `Start-Sleep` なので
タイミングまで毎回同じで、本物のバグに見える。これを「起動直後に Web API を叩くと
クラッシュする」と誤診した（実際は無関係。45 秒連続ポーリングで無傷）。
**ゲームが理由もなく死んだら、まず自分が起動に使ったスクリプトの最後まで読む。**

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

実データの入手先と取り込み手順は [docs/SCENES.ja.md](docs/SCENES.ja.md)、その上にコースを
組んで配る通しは [docs/TRACKS.ja.md](docs/TRACKS.ja.md)、手元の在庫は docs/testdata.ja.md。**再配布できるかは出どころで決まる**（次節）。

`dylanebert/3dgs` から引くときは `point_cloud/iteration_*/point_cloud.ply` を探すこと —
多くは `.splat` 形式で、UnityGaussianSplatting は **`.ply` と `.spz` しか読まない**。

### splat データは配布できない。同梱もしない

**mod と一緒に配れるキャプチャは `make_test_ply.py` が吐く合成データだけ。** 他は全部
他人の著作物で、こちらに再配布の権利が無い。2026-08-20 に一次情報を当たった結果：

| 出どころ | ライセンス | 再配布 |
|---|---|---|
| `dylanebert/3dgs`（HF） | **データセットカードが無い。表記ゼロ** | 不可 |
| Deep Blending（drjohnson, playroom） | **明示的なライセンス無し** | 不可（既定は全権利留保） |
| Mip-NeRF 360（bonsai） | プロジェクトページに**表記なし** | 不可 |
| INRIA 3DGS | 研究目的のみ・商用禁止・**制限が派生物に引き継がれる** | 不可 |
| SuperSplat 公開シーン | **作者が CC を明示していることがある**（下） | ライセンス次第 |

**「ライセンス表記が無い」は「自由」ではなく「許可が無い」。** 既定は全権利留保であって、
表記の不在は許諾ではない。

**SuperSplat だけは例外で、機械可読のライセンスが付く。** PlayCanvas がシーンごとに CC 4.0 の
6 種から選ばせていて、explore API が認証なしでその値を返す（叩き方は
[docs/SCENES.ja.md](docs/SCENES.ja.md)）。実測 14,086 本中 1,764 本がダウンロード可、うち
**1,544 本が CC BY 4.0** — 表示さえすれば再配布できる。

**すでに飛んでいる 4 本の判定は、これで変わる：**

| シーン | 作者 | ライセンス | 再配布 |
|---|---|---|---|
| nelson（3 本） | @tosolini | **CC BY** | 可（表示要） |
| calico（2 本） | @tosolini | **CC BY** | 可（表示要） |
| Utlida 1:80 | @overblickstudio | **CC BY** | 可（表示要） |
| utlida_test_4 | @overblickstudio | by-nc-nd | 不可 |
| textilni | @zenta | by-nc | 非商用のみ |

**utlida は同名で 2 本あり、ライセンスが違う。** どちらを落としたか確かめずに配らない。

GitHub Releases / R2 / gh-pages のどれに置くかは**この判断の下流**にあり、置き場所を変えても
再配布であることは変わらない。**配るならデータではなくレシピ** — 名前・元 URL・作者・
ライセンス・変換コマンド・期待される SHA と splat 数を書いた索引を置き、利用者が自分で
取得して自分で変換する。ホスティングの容量問題もそれで消える。

（`.ply` を落として自分で飛ぶのは別の話。制約がかかるのは**再公開**のほう。）

### 飛ぶなら「被写体周回」ではなく「室内を歩き回った」キャプチャを選ぶ

**bonsai は床が溶ける。欠損ではない** — y=0.0〜0.5 に 261,170 splats あり XZ の 88% を
覆っているのに、ドローン目線の浅い角度では滲んで使えない。原因は撮り方で、盆栽の周りを
回っただけで床にレンズを向けていない。**浅い角度からしか見られていない面は、その方向に
引き伸ばされたガウシアンとして復元される** — 真上からは埋まって見え、接地目線で溶ける。
Y オフセットで持ち上げても直らない（位置の問題ではない。1m 上げて確認済み）。

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
- **`splat-transform` は読み込み時に Z 軸 180 度回転を掛ける** — `(x, y, z)` が
  `(-x, -y, z)`。`--mirror y` の `(x, -y, z)` とは **X の符号だけ違う**。そこから出た
  メッシュをそのまま Unity に置くとキャプチャの鏡像を包むので、X を反転し、巻き順も
  3 個ずつ逆順にする（行列式 -1 で全面が内向きになるため）。**向きの判定に IoU は使わない**
  — 同じデータで 0.203 対 0.193 にしかならず決着しない。決着したのは AABB 残差
  （Z rot 180 が 0.12、Y flip が 1.06、8.9 倍差）

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

**掃除の判定は必ず 2 視点でやる。** 地面近くの巨大 splat は、内側から見れば霞で、真上から
見れば地面 —— 同じ splat が両方の役をしている。低い視点だけで「霞が消えた」と通した変更が、
真上では「地面が消えた」だった。**被覆の重みも「最長軸の二乗」ではなく「2 大軸の積」**
（Cauchy）。前者だと針が実面積の 60 倍に重みづけされ、犯人を取り違える。両方 docs/verification.ja.md。

### AirVis Studio で作る（全文は docs/airvis.ja.md）

**中身は COLMAP 4.2（private fork）＋ MCMC で、アルゴリズムに新規性は無い。** 差がつくのは
設定と、彼らが足した Vulkan の抽出・照合・Caspar。設定はアプリが `airvisstudio-*.json` に、
COLMAP の出どころは `third_party\colmap-runtime\airvis-colmap-runtime.json` に全部書き残す
ので、**推測せず読む。**

- **360 動画は解像度の役ではなく、地面の被覆の役。** 仮想ビューは 90° を 1600px で
  17.8 px/度、DJI は 63.3 px/度で **3.6 倍粗い**。360 カメラが同じ画素数を球に配る以上、
  設定では埋まらない
- **空を消さないと opacity が崩壊する。** 地上の全天球は画面の **46% が空**（上向きは
  86〜96%）で、AirVis の自動マスクは自撮り棒しか隠さない。実測で **93% の splat が死に、
  生き残りは 70 倍大きくて不透明度 0.05** ＝ 霧。`tools/sky_person_mask.py` で空・雲・人を
  足し、AirVis のマスクと論理積する。**順番は Prepare → マスク → Train**（`Extracted/` は
  Prepare のたびに作り直される）
- **cap は学習器で上限が違い、trainer をまたいで比べない。** gsplat は 1440² で疎点群の
  4 倍（1M）は無事、12 倍（3M）で崩壊、5M は CUDA エラー。AirVis の trainer は 6M でも
  健全だが、**`--max-splats` を大きく取りすぎると preset が黙って `conservative` に落ちて
  `scale_reg=0` になる**（11.1M で踏んだ。針だらけ・座標正規化・減衰の 3 症状）
- **`Views` は 8 を選ばない。** 上向きと一緒に下向きも消えるのに、空は減らない
  （水平リングだけでも 45.8% が空）
- **AirVis の SFM は飛ばせる。** trainer 単体（`AirVis-SplatTrainer.exe`）が標準 COLMAP
  レイアウトを直接読むので、COLMAP 4.2 の `pycolmap.panorama` リグ（360 の登録 50% → 98.8%）
  を食わせる。CUDA + Caspar 付き pycolmap のビルドと罠は docs/findings-2026-09-03.md

### 屋外キャプチャの掃除とコリジョン（全文は docs/cleanup.ja.md）

屋内シーンと前提が違うところだけ：

- **大きい splat は削除せず、ワールド Y で寝かせる**（`σ_y = sqrt(Σ_yy)` を上限で潰し、
  共分散を固有分解し直す）。削除すると霞と一緒に地面が消える —— 真上のフィールド内側で
  暗い画素が 0.33% → 12.61%。寝かせれば 0.98% で、霞も消える
- **コリジョンは AirVis の glb を変換せず、掃除済みの `.ply` から焼く。** すでに最終
  フレームにいるので座標系を当てる工程が消える
- **密度場は薄いシートを取りこぼす。焼く前に必ず太らせる。** 地面は σ_y 2cm、格子は 0.12m
  なので約 17% の柱が丸ごと外れ、地面が滑らかなぶん**まとまって大穴になる**。最小軸を
  格子 1 セル分に底上げすると iso 超えセルが 5.5 倍、メッシュの成分数が 2,736 → 274
- **コリジョン用の ply は描画されないので合成 splat を使ってよい**（見た目用は実在 splat
  の複製のみ。合成は FDF で霞が戻った）。ただし**地面高さの推定は木で壊れる** —— 樹冠で
  埋まったセルは樹冠の底を地面と誤認するので、地面格子に最小値フィルタをかける
- **`gs_field_mesh.py` は 4 億セルで打ち切る。** ステンシルは立方体なので、寝かせた 10m の
  円盤も 10m の立方体として評価される（1 個で 1.9 億セル）

## splat データのオンディスク形式（VDGS 独自）

`GaussianSplatAsset` は ScriptableObject だが、中身はメタ情報 + 5 つの生バイナリでしかない。
AssetBundle 経由だと型解決で詰まるので、同じ内容をプレーンなファイルとして置いて実行時に直接読む
（`<game>/vdgs/<name>/` に `meta.json` `chunk.bin` `pos.bin` `other.bin` `color.bin` `sh.bin`）。

**`posFormat` は座標空間を語らない。** そして**古い `chunk.bin` が残るとシーンが黙って砕ける** —
シェーダーはバッファの有無だけで chunk 適用を決めるので、Float32 の絶対座標を lerp の重みに
入れて盛大に外挿する。**エラーは 1 行も出ない。** 一日溶かした罠。`meta.json` の中身、
`ChunkInfo` の 64 バイト、両方向の壊れ方は
[docs/ARCHITECTURE.ja.md](docs/ARCHITECTURE.ja.md) の「データの形」。

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
見た目は暗い場にガウシアンが漂う。カードは使わない。静的ファイルは
`<game>/vdgs/ui/`。無いときは `WebUi.cs` が短い案内だけ出す。

**ゲーム内キーでの操作は全部やめた。** ゲームが F7 と矢印キーをトラックエディタで使って
いて奪えず、Numpad は MacBook に無く、そもそも**押した結果を出す HUD がゲームに無い**。
外に出すとこれが全部消える上に、**別マシンのブラウザから操作できる**（Parsec でゲーム画面を
見ながら、手元の Mac で操作する運用）。

### トラック名は 2 通りに綴られる

VelociDrone は自分で保存したトラック名をフォーム符号化する。空白が `+` になり、リテラルの
`+` が `%2b` になる。だから 1 本のコースに綴りが 2 つある。

| どこ | 例 |
|---|---|
| `user11.db` の保存名 | `VDGS+FDF+2026-08-22` |
| 画面の表示名 | `VDGS FDF 2026-08-22` |
| `%2b` を含む保存名 | `Sols%2bStreet%2bLeague%2b1` |
| その表示名 | `Sols+Street+League+1` |

復号は 2 段とも要り、順番も決まっている。`+` を空白に戻してから `%XX` を戻す。逆にすると
`%2b` が `+` になり、次の段がそれを空白と読む。実機の 2,143 本のうち 31 本が `%2b` を
含んでいて、`+` の段だけ戻すとその全部が外れる。

mod は動いているゲームから名前を読むので、`bindings.json` の鍵は**必ず表示名**。companion は
DB を読むので保存名を持つ。**照合する前に `TrackStore.DisplayName` で表示形に揃える。**

間違えても何も起きない。トラックは入り、キャプチャは入り、紐付けも書かれ、splat だけが
出ない。2026-09-01 に踏んだ。エラーは 1 行も出ない。

**companion が取り込んだ名前は届いた綴りのまま入る**ので、DB には両方の規約が同居する。
片側を再エンコードするのではなく表示形で比較しているのはそのため。

### トラック名の取得

**多段フォールバック**（`TrackName.cs`）。難読化フィールド → `Current Track/Table Entry`
パス配下の `Track Name` ラベル → 飛行 HUD の順。carrier の一覧と、それぞれが外れる条件は
docs/ARCHITECTURE.ja.md の「トラック名の取得は総当たりで見つけた」。

**使ってはいけない候補が 3 つある**（最後にエディタで開いた名前、トラック一覧の各行、
名前だけで拾った `Track Name` 列見出し）。どれも最初に見つかる上に一見正しく見えるので、
**必ずパスで絞る。**

難読化フィールドはゲームのアップデートで変わる。F12（`vdgs-track.txt`）で検索語
（`<game>/vdgs/needle.txt`、再ビルド不要）を当てて新しい carrier を探す。
**調査用のトラック名は `VDGSPROBE7777` のような固有文字列にすると一発で見つかる。**

### 罠

- **`JsonUtility` は使えない。** 辞書をシリアライズできず、入れ子型を
  **例外も警告もなく `{}` にする**。ファイルは正常に書けたように見えて中身だけ空になる。
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

**自動表示に切り替えは無い。** かつて `<game>/vdgs/autospawn` という空ファイルの有無で
判定していたが、2026-09-01 に仕組みごと削除した。作る側のコードがどこにも無く、開発機に
手で置いてあっただけだったので、**companion で新規導入した機械は全部これで無言のまま何も
映らなかった** — キャプチャもトラックも紐付けも正しいのに。しかもコメントが指す代替手段
（F8）は既に廃止済みで、消したら二度と出せない状態だった。

出したくないときは **`-force-d3d12` を付けずに起動する**（VelociDrone 自身のランチャーは
付けない）。D3D12 でないと splat シェーダーが unsupported になる。**そのときプラグインは
キャプチャを読み込みもしない** — `ShaderBundle.CanDraw` を spawn の前に見ている。見なければ
124 MB を読んで常駐させ、トラック切り替えごとに数秒止まって、画面には何も出ない。

（遠隔診断用の `<game>/vdgs/menuspawn` は別物で、そのまま残っている。）

設計の理由は [docs/ARCHITECTURE.ja.md](docs/ARCHITECTURE.ja.md) の「トラックと GS の対応」「操作面」、操作手順は [docs/USAGE.ja.md](docs/USAGE.ja.md)。

## 配布は companion アプリ（2 本ある）

**mod を配る道具で、人がやることは 4 クリックだけ。** BepInEx の取得、mod の導入・削除、
キャプチャのダウンロードと導入、トラックの DB 登録と紐付け、ゲームの起動 — 全部これで済む。
**`companion-tauri/` の 1 本だけ**（Tauri 2 + Rust）で、`web/` の React を描く。

| OS | 起動のしかた |
|---|---|
| Windows | `velocidrone.exe -force-d3d12` |
| macOS | `DYLD_INSERT_LIBRARIES` + `DOORSTOP_*` を付けて `arch -arm64` で exec |

**C# 版（`companion/`、.NET Framework 4.8 + WinForms + WebView2）は削除済み。** 同じ仕事を
2 回書いて 1 対 1 で保つ期間は終わった。当時の設計は
docs/superpowers/specs/2026-09-02-companion-tauri-design.md、Windows 移植の判断は
docs/superpowers/specs/2026-09-03-companion-tauri-windows.md に残してある。
**そこにある `companion/*.cs` への参照は歴史的な記録** — 現物はもう無い。

**窓を開かない仕事が 2 つある**（`cli.rs`。C# の `Program.cs` から移植し、**両 OS で動く** —
C# 版は Windows 専用だった）：

```
VDGS --export-track --list                     # user11.db のトラック一覧
VDGS --export-track "<名前>" [out.track.json]   # 公開用に書き出す（公式サーバー由来は拒否）
VDGS --check-catalog [url]                     # アプリがそのカタログを読めるか
```

**単一インスタンスの錠は戻した**（`tauri-plugin-single-instance`、Tauri 公式）。C# は
`Local\VDGSCompanion.window` の mutex で 2 枚目を開かせず、既存の窓を復元して前面に返して
いた。理由は「両方がゲームを探し、両方が互いに上書きインストールを提案し、片方が
ダウンロード中にもう片方から入れると同じフォルダに二重に書く」。

**プラグインは builder の先頭に置くが、CLI はその手前で抜ける。** `main.rs` が
`--export-track` / `--check-catalog` を捌いて `process::exit` するのは builder より前なので、
**2 個目の `VDGS --export-track` はプラグインに触れない**。順番を逆にすると、書き出しが
「既存の窓を前面に出して終わり」になり、**ファイルが作られないのに成功したように見える**。
実機で両方確認した — 2 回目の起動は自分で終了してプロセスは 1 つのまま、GUI が動いている
最中の `--check-catalog` はちゃんと自分の仕事をする。

**C# と一緒に消えて、まだ戻していないものが 1 つある：**

- **`companion/tests/` にしか無かった検査。** とくに 2 本 —— **ペイロードの資産を数えて
  各エントリ 1 本ずつであることを確かめるもの**（23 本入って 5 リリース気づかれなかった
  事故の見張り。いまは `rm -rf` してから組むので**溜まらない**が、**数えてはいない**）と、
  **BepInEx の pin を実際に取りに行くもの**（URL・バイト数・sha256 は**コードを誰も触らずに
  腐る**。いまはローカルの fixture しか見ていない）

**GUI サブシステムの exe なので、そのままでは標準出力がどこにも出ない。** `AttachConsole` で
親のコンソールを借りるが、**既に有効なハンドルがあるなら触らない** — パイプやファイルへの
リダイレクトをコンソールで上書きすると、呼んだ側に何も届かず、誰も見ていない端末に出る。
そして `std::process::exit` は**フラッシュしない**ので、明示的に流す。**どちらも「終了コード
だけ正しくて出力が空」という形で出る**（端末は行バッファなので気づけない）。

**macOS 版で踏んだ罠 3 つ。全部エラーが 1 行も出ない：**

- **Tauri の `listen()` は非同期。** ページは `subscribe()` の直後に同期で `refresh` を送る
  ので、最初の state がリスナー登録前に emit されて**永久に届かない** — 窓は空のまま、
  理由は出ない。WebView2 は同期登録なので Windows では起きなかった。`bridge.ts` に
  「listen が解決するまでコマンドを保留する」ゲートがある
- **zip の直下エントリを一律で飛ばすと注入本体が消える。** C# の `InstallArchive` は
  「スラッシュを含まない＝README」として捨てるが、**BepInEx は `libdoorstop.dylib` を直下に
  置く**。ログは `installed BepInEx` と出て、`has_bepinex` だけが false のまま。直下でも
  `.txt`/`.md` だけを飛ばす
- **`reqwest` の `timeout` はボディ読み込みも含む。** 30 秒を付けると 376 MB のキャプチャは
  必ず失敗する。`timeout(None)` + `connect_timeout`。**代わりに無反応タイムアウトが無い**
  ので、本文が止まると復旧は app 再起動だけ（issue #23）

**ダイアログを開くコマンドは `#[tauri::command(async)]` にする。** 素の
`#[tauri::command]` はメインスレッドで走り、dialog plugin はそこにダイアログを投げてから
答えを待つので、**自分が描画を止めている窓を待って固まる**。フォルダ選択が Escape も
Cancel も受け付けなくなる。実機で再現・修正済み。

### 「入れたのに何も出ない」と言われたら

**推測せず、ログ一式を送ってもらう。** この症状は原因が 4 つあり、どれも外から見た姿が同じ：
注入されていない、シェーダーが使えない、トラック名が読めない、紐付けが違う。そして **True Lens
なら 4 つとも正常のまま何も出ない**（前節）。

```
curl -sSL https://raw.githubusercontent.com/Saqoosha/VDGS/master/tools/collect-mac-diagnostics.sh | bash
```

デスクトップに 15 KB の zip が出る（macOS のみ）。ログ 4 本と `Player.log`、キャプチャごとの
`meta.json` / `placement.json`、機械の情報。**抜粋ではなく全文を入れてある** — 先に書いた grep は
「誰かが既に想像した失敗」しか拾わず、ここで高くついた失敗は全部その外側だった。

**`bash <(curl ...)` は使わない。** process substitution は bash 固有で、
**fish は構文エラーで落ちる**。相手のシェルは選べない。パイプなら通る。

### macOS の署名と公証

**Developer ID は個人（Tomohiko Koyama / VCFY2GFR89）。** 公証は Canopy と同じ keychain
profile `notarytool-profile` をそのまま使う（同じチームなので作り直し不要）。

**app を先に公証して staple し、そのあと DMG を作る**（Canopy の順番）。DMG だけ staple
すると、Applications にドラッグした app が**オフラインで検証できない**。

**notarytool は DMG を内部でマウントする。** 前のマウントが残っていると
`xar_open_digest_verify` で固まり、**Apple には何も届かない**まま何分でも待つ。復旧の順番は
決まっている：`notarytool history` で未達を確認 → notarytool を kill → `lsof <dmg>` が挙げる
**孤児の `diskimages-helper`（PPID 1）を kill** → `hdiutil detach`。`tools/make-mac-app.sh`
は /tmp で作業し、EXIT で自分のマウントを外す。全文は Canopy の AGENTS.md。

**中身と、リリースを組んで上げる通しは [docs/distribution.ja.md](docs/distribution.ja.md)。**
配るときにしか要らないので本文から出した。そこに置いてあるもの — companion の各ファイルの
役割、踏むと高くつく罠 10 個（`scp host:relative` がローカルコピーになる、GUI exe を
PowerShell が待たない、payload に前回の資産が残る、など）、`make-release.sh` →
`make-catalog.sh` → `publish.sh` の順番と守るべき規則、Cloudflare Worker + R2 の構成、
通し確認に使える実測値の表。

**再配布していいかは置き場所と無関係**に決まる（「splat データは配布できない。同梱もしない」）。

## 制約と、いまも踏める罠

構成の理由（Unity を 2 本使う、ScriptableObject を捨てた、AssetBundle だけでは足りない）
は docs/ARCHITECTURE.ja.md。ここは踏むと高くつくものだけ。

**upstream のレンダラーは「外から眺める」前提で、中を飛ぶと破綻する。** `SplatRenderer`
の既定 3 つは upstream から意図的に変えてある。**戻すと霧が戻る**：

| 既定 | upstream | なぜ変えたか |
|---|---|---|
| `m_CullCenterSlack = 1.2` | 半径マージン込みで視錐台判定 | 中心が画面外でも体が入る splat を**残す**規則。キャプチャ内を飛ぶとカメラから 0.2〜1.1m の splat が数千個あり、各々が数千ピクセルに広がって画面を洗う。web は中心だけで切る |
| `m_GaussCut = 4` | α<1/255 まで描く | 2σ の外に 1〜5/255 のリングが残り、100 万個ぶん積算される |
| `m_DropDegenerate = true` | `max(lambda2, 0.1)` にクランプ | 潰れたガウシアンを細い針として描く ＝「筋状の大きな splat」 |

実測（同一カメラ、web リファレンスと比較）：空の平均 6.20 → **0.04**（web 0.00）、
木立 43.7 → 9.6（web 11.8）、芝は 133 のまま変化なし。

**True Lens を on にすると、キャプチャは描かれるのに 1 ピクセルも見えない。** ゲーム側の設定で、
**OS は関係ない**（Windows でも同じ）。飛行シーンのカメラが 1 個から**6 個**になる —
`CameraLocation/Camera` の子として `Front/Left/Right/Bottom/Top` が 90° 画角で生え、それを
歪めた合成が画面に出る。`SplatRenderer` は Preview 以外の全カメラに付いて `CameraTarget` に
合成するので、**1 フレームに 6 回ソートして描き、どれも表示には届かない。**

**気づきにくいのは、症状が「mod が入っていない」と見分けが付かないから。** ログは全部成功と
言い、`vdgs-perf.log` には splat 数が出続ける。**見分ける印は速度** — M3 Max が 252 万で
30fps、M1 Max が同じキャプチャで 60fps。速い機械の方が遅ければこれを疑う。カメラ数は
`vdgs-probe.log` の `cameraCount` にそのまま出る。

**設定は `user11.db` の `sim_states` に平文で入っている** — `name='true_lens'`、値は文字列の
`'true'` / `'false'`。**companion が既に開いている同じファイル**なので、読むのは 1 行。
`true_lens_size` など前方一致する行が並んでいるので**完全一致で引く**。

**だから companion が FLY の真上で警告する**（両 OS）。サイトに書くだけでは読まれない —
**症状は「mod が入っていない」と見分けが付かず、本人は設定を疑いもしない。**
規則は 1 つ、**`true` だけが警告する**。DB が無い・読めない・行が無いは全部「分からない」で
黙る。**「読めなかった」を「危ない」と出す警告は、一度嘘をつけば二度と信じられない。**

**対処は「True Lens を切る」。** mod 側で支えられるかは未着手（[#27](https://github.com/Saqoosha/VDGS/issues/27)）。

**RT の深度アタッチメントは付けない。** upstream の
`SetRenderTarget(rt, BuiltinRenderTextureType.CurrentActive)` は、ゲームの HDR +
PostProcessing カメラ（D3D12）で**バインドごと無言で失敗する** — splat がカメラターゲット
に直接描かれ、composite は空の RT を素通しし、暗い splat が Linear パイプラインの sRGB
持ち上げを食う。色のみバインドし、前後関係は splat シェーダーで `_CameraDepthTexture` を
サンプルして解く（`m_DepthClip`）。**エラーは 1 行も出ない。**

**Windows では `-force-d3d12` 必須。** ソートの compute が SM6 の wave intrinsics を 41 箇所
使う。`-force-vulkan` は**ゲーム自身**が描けない（VelociDrone が Vulkan 向けにビルドされて
いない）。companion の `FLY` は常にこのフラグを付ける。**macOS は Metal が正規経路なので
フラグは要らず、以下の副作用も一切出ない。**

**シェーダーは焼く OS がターゲットを決める。** D3D12 版は Windows の Unity 2021.3.45f2 で
しか焼けず、Metal 版は Mac で焼ける（`BuildBundles.BuildMac`）。罠が 2 つ：

- **プロジェクトのグラフィックス API を先に合わせる**（`PlayerSettings.SetGraphicsAPIs`
  をビルド前に呼ぶ）。既定のまま焼くと無言で unsupported になる — 症状とサイズの基準は
  「開発フロー」
- **macOS の Editor は D3D 向けに DXC を回せない** —
  `DXC: can only use DXC to target D3D from the Windows Editor.`
  **Metal 版を焼くときもこの行はログに出るが無視してよい** — Metal のプログラムはその前に
  焼き終わっている（`metal (total internal programs: 2, unique: 2)` が先に出る）。
  Metal バンドルは約 437 KB、D3D12 版は約 1.5 MB。**サイズの基準が OS で違う**

**`-force-d3d12` の副作用はログだけではない。** 既知のものが 2 つある。軽いほうから。

### 副作用 1：ログが埋まる（描画には無害）

ゲームは D3D11 向けビルドなので PostProcessing v2 の compute が見つからない：

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

### 副作用 2：メインメニューに放置すると落ちる

**D3D12 で起動してメインメニューに置いておくと、5 分前後でクラッシュする。** 直前の一行は
毎回これ：

```
Error assigning 2D texture to (null) texture property '_LightTexture0': Dimensions must match
Crash!!!
```

`_LightTexture0` は Built-in RP のライトクッキー／減衰テクスチャで、**こちらのコードに接点は
無い**。切り分けの実測（同じ機械・同じ日）：

| 描画 API | プラグイン | 放置 | 結果 |
|---|---|---|---|
| D3D12 | 有り | 約 5 分 | Crash + `_LightTexture0` |
| D3D12 | **無し**（`plugins/` を空に） | 約 5 分 | **Crash + `_LightTexture0`（同一）** |
| D3D12 | 有り（メニュー放置、2026-09-01） | **3 分 44 秒** | Crash + `_LightTexture0` |
| D3D11 | 無し | 13 分以上 | 無傷 |
| **D3D12** | **有り**（トラック内。メニュー滞在 5 秒） | **15.8 分** | **無傷** |

**3 本目で下限が 5 分から 3 分 44 秒に下がった**（12:56:26 起動、13:00:10 が `Player.log`
最終書き込み）。「約 5 分」は安全側の見積もりではない。**メニューに戻したら、放置しない。**

**クラッシュハンドラは走るが、ダンプは残らない。** `Player.log` は
`%LOCALAPPDATA%\Temp\velocidrone\velocidrone\Crashes` を名指しするが、
**そのディレクトリは存在しない**（2026-09-01 に確認）。自然死を待てばダンプが手に入る、
という筋は使えない。

**プラグインを外しても直前の一行まで一致する。mod は無関係。** そして D3D11 では起きない。

**そして D3D12 のままでも、トラックに入っていれば落ちない。** メニューを 5 秒で抜けて
nelson-lod2（217 万 splats）を出したまま 15.8 分飛んで無傷、正常終了。落ちるまでの 5 分の
3 倍。**犯人は「D3D12」単独ではなく「D3D12 かつメニュー」。** 実務上の対処は一行で済む —
**メニューに長居しない。**

これは重い。`-force-d3d12` は mod に必須で `-force-vulkan` は逃げ道にならないので、
**mod を使う限りこの制約が付いてくる**。

未解決のまま残っている問い、2 つ：

- **どちらの側も n=1。** D3D11 放置 13 分、D3D12 トラック内 15.8 分。D3D12 側の
  クラッシュ 2 回が 5 分前後なので差は明確だが、生存側を積む価値はある
- **mod 側で止められるのか。** ライトクッキーの差し替えを掴めるなら `PostProcessFix` の隣に
  置ける。未着手

**「クラッシュしたらまず自分の変更を疑う」は正しい順番で、実際そこから始めた。** ただし
この件は**プラグインを外す 1 回**で決着した。落ちたときは早めにその 1 回を使うこと。

**コリジョンは付くようになった**（`SplatCollision.cs`、実装済み・実機で確認済み）。
焼き方は [docs/SCENES.ja.md](docs/SCENES.ja.md)。設計の数字と捨てた手法は
docs/superpowers/specs/2026-08-18-splat-collision-design.md。

**壁の厚みは速度で決まる。** 物理は 400 Hz、150 km/h で 1 ステップ 0.104 m 進むので
**厚さ 10 cm 未満の壁はすり抜ける**。level set の帯を voxel の 4 倍で焼くのはこのため。

## 残タスク

- **`-force-d3d12` でメニュー放置がクラッシュする原因は未解明**（「副作用 2」）。
  飛行中に出るかも未確認
- **`Medium` 以下が 2.6 倍暗くなる原因は未解明。** 使わないと決めて封印してあるので実害は
  無い。追うなら Norm8x4 の色デコードから
- **完全な High パッキングは未実装。** 焼いた版との差は 7% で、**差に見合うかは疑問**
  （数字は docs/ply-loading.ja.md）
- **空マスクは実施済みで、道具は `tools/sky_person_mask.py`。** 空・雲・人を SegFormer で
  切って AirVis の自動マスク（自撮り棒とマウント）と論理積する。閾値と根拠は
  docs/airvis.ja.md。360 でも効く（下）が、**gsplat 側は損失から外す `mask_dir` と
  アルファを押す `alpha_mask_dir` を分ける** —— 人はアルファを押すと歩いた道の下の地面が消える
- **True Lens の下でも描けるようにするか**（[#27](https://github.com/Saqoosha/VDGS/issues/27)）。
  いまの答えは「切ってもらう」で、companion が FLY の手前でそう言う。5 面それぞれに合成して
  歪みの前に届ける必要があり、**そもそも届く場所があるかを見ていない**
- **未着手の issue** — #23（本文が止まったダウンロードから復旧できない）、#24（`.track.json`
  の検証が C# より緩い）、#26（小物 4 つ：未使用の `bepinex::uninstall`、`csp: null`、
  `.ply` ヘッダ読みの上限、`install_as` のフォールバックが片側だけ）
- **FDF の芝の平坦化は不要だった可能性が高い。** 平坦化前でも地面は破綻せず、むしろ芝目が
  残る（「over flattened で眠い」の原因）。一方**ゴミ除去は本当に必要**で、生データは空が
  白く埋まる
- **異方性から板と針を見分けるには中間軸が要る。** `max/min` だけでは両方 100 になり、
  「針だらけ」という誤診を招く（実際に一度出した）。log 空間で
  `t = (log(mid)-log(min))/(log(max)-log(min))` を取ると `t≈0` が針、`t≈1` が板。
  **3DGS の壁と床は板で、正常**。上から見るとエッジオンで線に見えるだけ
- **コリジョンの voxel はシーンごとに決める**（細かいほど穴、粗いほど柱が太い）。手順と
  各シーンの採用値は docs/SCENES.ja.md。nelson は 0.06 で `--filter-cluster` を使わない
  （原点が空でクラスタが 1 splat だけ残る）
- **JDL-2026-R5 は地面近くの霞がまだ少し残る。** 実機で飛んで確認済み。掃除の側はやり切って
  いて、**残りは撮影密度の問題に見える** —— FDF と比べて写真が 4.6 分の 1（0.015 対
  0.070 枚/m²）、地面の被覆が 4 分の 1（p50 0.40 対 1.68）。**学習を厚くしても埋まらない**
  ことは確認済み（5M / 10 万反復にしても被覆は 0.40 → 0.35 で、細部だけが良くなった）
- **Insta360 の地上映像は単独では使い物にならない。** AirVis の外で回せば指標は健全に
  なる（生存 68%、masked PSNR 24.6）が、**品質は前より良いだけで、使えるレベルではなく、
  DJI 版 JDL にも届かない**（2026-09-03 に目視で判定）。効いたのは COLMAP 4.2 の
  panorama リグ（登録 50% → 98.8%）と空・人マスク、正則化。届かないのは 16 px/度と均一な
  動きブレで、撮影の物理。**残る手は DJI と同じ COLMAP モデルに混ぜて地面の被覆だけ足す
  合成で、道具は揃ったが未実施**（docs/airvis.ja.md「実測値」、docs/findings-2026-09-03.md）。
  配備中の `JDL-2026-R5-airvis` は DJI 456 枚だけで作られている
## 参考

- [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting) — Mac M1 Max で 46FPS の実測あり
- [antimatter15/splat](https://github.com/antimatter15/splat) — 比較用リファレンス（MIT、単一ファイル WebGL）
- [BepInEx releases](https://github.com/BepInEx/BepInEx/releases)
