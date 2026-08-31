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

**速度も見た目も差が無い。** 実機の中央値差 0.37 ms は 1 シーン内のばらつき（6.4 ms、
カメラの向き次第）の 17 分の 1 で、**決着には片側 1600 サンプル ＝ 2.2 時間の飛行が 2 回**
要る。見た目も 1024×1024 の最悪 1 画素で 5/255、8/255 超はゼロ。統計と差分表は
docs/performance.ja.md の同名節。

だから判断軸はサイズだけ。**High は 84 B/splat・k-means 不要・`.ply` 直読みで作れる。
Cluster16k は 47 B/splat（44% 小さい）だが k-means に約 10 分かかり、`.ply` からは作れない。**
`reprocess.sh` の既定は `High`。**配布や VRAM が効く場面では `-vdgsShFormat Cluster16k` を
意識して選ぶ。**

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

実データの入手先は学術データセットや SuperSplat の公開シーンだが、**再配布できるかは
出どころで決まる**（次節）。手元で飛ぶなら `.ply` を自分で取って `<game>/vdgs/` に置く。手順は
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

**掃除の判定は必ず 2 視点でやる。** 地面近くの巨大 splat は、内側から見れば霞で、真上から
見れば地面 —— 同じ splat が両方の役をしている。低い視点だけで「霞が消えた」と通した変更が、
真上では「地面が消えた」だった。**被覆の重みも「最長軸の二乗」ではなく「2 大軸の積」**
（Cauchy）。前者だと針が実面積の 60 倍に重みづけされ、犯人を取り違える。両方 docs/verification.ja.md。

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

`http://<host>:8777/` でプラグインが HTTP サーバーを立てる。**ゲーム内キーでの操作は全部やめた**
（F7 はトラックエディタの保存に取られている、Numpad は MacBook に無い、HUD を描く場所が無い）。
表示は**トラック名 → GS の対応表**（`<game>/vdgs/bindings.json`）だけで決まり、1 秒ごとに
ポーリングする。紐付けの無いトラックでは何も出さない。

**トラック名の取得は多段フォールバックで、使ってはいけない候補が 3 つある。** `JsonUtility`
は入れ子型を無言で `{}` にする（Newtonsoft.Json 13 を使う）。UI は `innerHTML` を使わない
（トラック名は攻撃者が書ける）。設計の理由は
[docs/ARCHITECTURE.ja.md](docs/ARCHITECTURE.ja.md) の「トラックと GS の対応」「操作面」、
操作手順とキー割り当ては [docs/USAGE.ja.md](docs/USAGE.ja.md)。

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
| D3D11 | 無し | 13 分以上 | 無傷 |
| **D3D12** | **有り**（トラック内。メニュー滞在 5 秒） | **15.8 分** | **無傷** |

**プラグインを外しても直前の一行まで一致する。mod は無関係。** そして D3D11 では起きない。

**そして D3D12 のままでも、トラックに入っていれば落ちない。** メニューを 5 秒で抜けて
nelson-lod2（217 万 splats）を出したまま 15.8 分飛んで無傷、正常終了。落ちるまでの 5 分の
3 倍。**犯人は「D3D12」単独ではなく「D3D12 かつメニュー」。** 実務上の対処は一行で済む —
**メニューに長居しない。**

これは重い。**`-force-d3d12` は mod に必須**（ソートの compute が SM6 の wave intrinsics を
41 箇所使う）なので、**mod を使う限りこの制約が付いてくる**。`-force-vulkan` はゲーム自身が
描けないので逃げ道にならない。実務上は**メニューに長居しない**。

未解決のまま残っている問い、3 つ：

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

- **`-force-d3d12` でメニュー放置がクラッシュする原因は未解明。** mod 無しでも同一の直前行
  （`_LightTexture0` の寸法不一致）で落ち、D3D11 では起きない。飛行中に出るかも未確認。
  詳細は「制約と、いまも踏める罠」の副作用 2
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
- **JDL-2026-R5 は変換済みで実機投入待ち。** `build/splats/JDL-2026-R5-airvis/`
  （2,521,003 splats / 212 MB ＋ `collision.bin` 999,654 三角形 / 18 MB）。プレビューと
  落下テストは通っている。飛んで見るのはベール、地面の穴、コリジョンの当たり。
  **ゲーム機 `w` が起きたら送るだけ**
- **屋外用の掃除スクリプトはセッション用ディレクトリにしか無い。** 手順と数式は
  docs/cleanup.ja.md に書いたが、`groundsquash.py` / `deelong.py` / `groundfill.py` /
  `inflate.py` / `topcover.py` / `checkframe.py` / `glb2ply.py` 本体は `tools/` に
  入れていない。**次に屋外シーンを作るなら移す価値がある**
- **FDF の AirVis 版が未着手。** 写真 774 枚が win4090 の `C:\Users\saqoosha\fdf-photos\`
  に置いてある

## 参考

- [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting) — Mac M1 Max で 46FPS の実測あり
- [antimatter15/splat](https://github.com/antimatter15/splat) — 比較用リファレンス（MIT、単一ファイル WebGL）
- [BepInEx releases](https://github.com/BepInEx/BepInEx/releases)
