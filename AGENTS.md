# VDGS — 3D Gaussian Splatting inside VelociDrone

VelociDrone に 3D Gaussian Splatting シーンを読み込む mod。BepInEx プラグインとして
コードを注入し、実行時に splat データをレンダリングする。

## 状態

**実データで動作確認済み。** 3 シーン同時（計 117 万 splats）を RTX 3060 で描画してクラッシュ
なし。ドローン機体との前後関係も半透明ブレンドも破綻しない。配布まで通っていて、まっさらな
Windows から 4 クリックで飛べる（「配布は companion アプリ」）。

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

### Mac（解析用）

PatchKit 経由の macOS 版（1.17、arm64 thin、adhoc 署名、同じ Unity 2021.3.45f2）でも
`settings.db` / AssetBundle の構造は Windows と同じなので解析には使える。BepInEx の
macOS universal ビルドは arm64 では**未検証**。

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

# 3. シェーダーバンドルを焼く（Windows 上で実行。macOS では不可能）
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
  `%USERPROFILE%` に展開するため）。中身だけ固めると `unpack failed: VDGSBundler missing`
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

## 配布は companion アプリ（`companion/`）

**mod を配る道具で、人がやることは 4 クリックだけ。** BepInEx の取得、mod の導入・削除、
キャプチャのダウンロードと導入、トラックの DB 登録と紐付け、`-force-d3d12` 付きの起動 —
全部これで済む。.NET Framework 4.8 + WinForms、中身は **WebView2 で `web/` の React を
描いている**（`companion.html`）。操作 UI と同じテーマ・同じコンポーネント・同じフォントで、
**別製品に見せない**ため。

```
companion/
  Program.cs      GUI 起動と CLI（--export-track / --check-catalog）
  MainForm.cs     WebView2 ホスト、postMessage のブリッジ、重い処理の別スレッド化
  GameInstall.cs  ゲーム発見・走査、mod の導入/削除、zip 展開、bindings 書き込み
  BepInEx.cs      ローダーの取得（版と digest を固定）
  TrackStore.cs   user11.db の読み書き
  Catalog.cs      公開カタログの取得・検証・ダウンロード
  Settings.cs     ゲームパスとカタログ URL を %LOCALAPPDATA% に記憶
  tests/          実データ寄りのテスト（Mac で走る）
```

**payload は毎回空にしてから詰める。** csproj の `CopyMod` は `Copy` タスクで、消す働きが
無い。web の資産はビルドごとに名前が変わるので、**入れ直すたびに前回のぶんが隣に残る** —
2026-09-01 に数えたら payload が 23 本、元は 5 本だった。全部 zip に入り、利用者の
`vdgs/ui` にも配られていた。`index.html` は現行の 2 本しか名指ししないので**何も壊れず**、
5 リリース気づかれなかった。ハーネスが数を見張っている。

**mod はアプリの中に同梱する**（`mod/` フォルダ、`make-release.sh` が組んだ木をビルド時に
コピー）。ボタンは自分の仕事を名乗る — `INSTALL MOD` / `REINSTALL MOD` /
`UPDATE TO <版>` / `NO MOD PAYLOAD`。**押してみないと分からない、をやらせない**ため。

**BepInEx も自分で取ってくる**（無いときだけ）。同梱ではなく本家リリースから、URL・サイズ・
sha256 を固定して。**「先に BepInEx を入れて」は全インストールの第 1 手で、いちばん間違えやすい手順だった。**
1 階層深く展開すると、**ゲームは起動して、何も起きない**。

**トラックは行ごと消せる**（各行にホバーで出る `REMOVE`）。消えるのは `user11.db` の行と
`bindings.json` の項目だけで、**キャプチャは残る** — GB 単位で、消す理由が無い。
**公式サーバー由来のトラックは削除しない**（ボタンは `UNBIND` になる）。作者のものであって、
こちらが人の機械から消していいものではない。DB は削除前にコピーする（**ラップタイムは
再取得不能**）。

**UNINSTALL もキャプチャを消さない。** 消すのは `VDGS.dll` / `vdgs-shaders` / `vdgs/ui` だけ。
キャプチャは GB 単位で再取得に数時間かかるし、`bindings.json` と `placement.json` を残せば
**入れ直したとき元の場所に戻る**。BepInEx も残す（こちらのものではない）。

**ゲームパスは `%LOCALAPPDATA%` に覚える。** 見つからなければ固定ディスクを走査する
（深さ制限つき、`Windows` 等は飛ばす、ジャンクションは辿らない）。**PatchKit は
インストール先をレジストリにも自前フォルダにも残さない**（`%LOCALAPPDATA%\PatchKit` は
32 バイトの `sender_id` 1 個だけ）ので、走査か「自分で探して」しかない。

### 踏むと高い罠

- **`scp host:relative` が exit 0 のまま何も転送しないことがある。** `-v` を見ると
  `Executing: cp --` ＝ **ローカルコピーだと判定されている**（ホスト名が 1 文字だと
  ドライブレターに見える）。`scp host:/C:/Users/a/name` と**絶対パスにする**
- **GUI サブシステムの exe は PowerShell が待たない。** `& $exe args` は即座に戻るので、
  出力もファイルも「無かった」ことになる。`Start-Process -Wait -PassThru`、
  かつ **`-ArgumentList` は空白を含む引数を勝手に引用しない**ので自分で `'"..."'` にする
- **`Get-Process VDGS` は配列を返しうる。** 2 つ動いていると `AppActivate($p.Id)` が
  `DISP_E_TYPEMISMATCH` で落ちる。`Select-Object -First 1` を挟む
- **WebView2 ホストを殺しても `msedgewebview2.exe` がファイルを掴んだままの瞬間がある。**
  フォルダごと消してから展開する配備は、そこで 1 ファイルだけ失敗して**半分だけ新しい木**
  になる。リトライを入れる
- **`AppDomain.CurrentDomain.BaseDirectory` に置いた `ui/` を `file://` では読めない。**
  ES モジュールが読み込まれない。`SetVirtualHostNameToFolderMapping` で
  `http://vdgs.invalid/` を生やして読む（ポートは開かない）
- **`.gitignore` に `web/` があると `web/` 配下の新規ファイルが `git add` で消える。**
  React 化前の名残。マージで一度復活し、コミットが自分のソースを半分置き去りにしかけた
- **macOS では `site.tsx` と `Site.tsx` が同じファイル。** 両方書くと後から書いた方だけが
  残る。エントリを消したのに **tsc も build もページのテストも全部通った**（コンポーネントを
  直接描くテストしか無かったため）。**各エントリが実際にマウントすることを固定してある**
  （`web/src/entries.test.tsx`）
- **state を作るのは重い。** 全キャプチャを歩いて `.ply` ヘッダを読んで SQLite を開く。
  「作業中」の表示をこれで送っていたら、**歩き終わるまで無言**になった（進捗表示が
  直そうとしていたもの、そのもの）。進捗と busy は**専用の軽いメッセージ**で送る。
  各 state は生成にかかった `stateMs` を持って帰るので、次に遅くなったら実測で分かる
- **テスト project に `RuntimeIdentifier` を付けない。** mono が Windows 版の
  `e_sqlite3.dll` を掴んで落ちる。RID が要るのはアプリ本体だけ

### 配布の通し

**キャプチャは mod に同梱しない**（数百 MB）。アプリの `02 GET` からカタログ経由で落とす。

```bash
# 1. コースを DB から出す（Windows 側。公式サーバー由来のトラックは拒否される）
VDGS.exe --export-track "VDGS FDF" VDGS-FDF.track.json   # → catalog/tracks/
VDGS.exe --check-catalog https://vdgs.saqoo.sh/catalog.json   # 読めるか確かめる

# 2〜4. 固めて、カタログを組んで、上げる
bash tools/make-release.sh --scene <name> --scene-dir <path>
bash tools/make-catalog.sh --base-url https://vdgs.saqoo.sh
bash tools/publish.sh
```

**サイズと sha256 は測って書く、手で書かない。** ダウンロードが途中で切れたか差し替えられたか
を見るのは digest だけで、手写しの digest はいつか必ず狂う。アプリは **https 以外を拒否**
（loopback だけ例外、テスト用）、**digest が合わなければ展開しない**、**知らない
`formatVersion` は読まない**。欠けたフィールドを「トラック無し」と誤読して、
**公開物の半分だけ入れる**のが最悪なので。

**mod の版はリリースの日付で、`make-release.sh` だけが刻む**（`-p:Version=$VERSION`）。
`src/VDGS/VDGS.csproj` の `<Version>0.1.0</Version>` は置き場所で、そのまま出るのは
**dev ビルド**。companion の mod ボタンは導入済みの版と同梱の版を比べるので、
**全ビルドが同じ 0.1.0.0 を名乗っている間は「Reinstall mod」しか出せなかった** —
更新を更新だと知る手段が無かった。

**カタログ側にバージョン要件は無い。** `minModVersion` を一度足して、同じ日に外した。
まだ 1 本もリリースしていない時点では、**存在しない問題への防具**だった
（[#4](https://github.com/Saqoosha/VDGS/issues/4)）。要るのは、古い mod では描けない
キャプチャを実際に公開したときで、そのときに設計する。

**`publish.sh` は R2 に上げてから deploy する。** 逆にすると、**まだ無いファイルを指す
リストを必ず一度公開する**。

**上げるのは `rclone`。`wrangler` では 300 MiB を超えると必ず落ちる**
（`Wrangler only supports uploading files up to 300 MiB in size`、multipart のオプションが
無い）。FDF 2026-08-22 は 375 MB でここに当たった。鍵は 1Password の `VDGS` 環境を
`~/.claude/1p-mounts/vdgs.env` にマウントして読む（`VDGS_R2_ENV` で上書き可）。

**`--s3-no-check-bucket` は必須。** トークンは `vdgs` バケットだけの Object Read & Write に
絞ってあり、`rclone` は既定でバケットの存在を `CreateBucket` で確かめようとして 403 になる。
**直すのは呼ぶ側で、トークンを広げるほうではない。**

**同じ名前で中身を差し替えない。** 公開ファイルは `immutable, max-age=31536000` で配るので、
上書きするとエッジは古い中身を 1 年出し続け、カタログは新しい digest を名乗る。
**ダウンロードは完走して、そのあと展開が拒否される。** 版を上げて別名にする。
2026-09-01 に `vdgs-companion-2026.09.01.zip` でこれをやりかけた。

**見張りは digest で、サイズではない。** `publish.sh` は各オブジェクトに sha256 を user
metadata として刻み、次回はそれと突き合わせる。同じ日の companion 3 版が
6,607,301 / 6,607,540 / 6,607,546 バイトだったように、**別の中身が同じ長さになるのは普通**。

**`rclone` は `--metadata-set` だけではメタデータを書かない。`-M` が要る。** フラグは受理
されて何も起きず、次回の実行は比較材料が無いまま長さに落ちる。実測で判明した。

**`make-catalog.sh` は companion を mtime で選ぶ。** 名前順だと `2026.09.01.1` が
`2026.09.01` より前に並ぶ（`1` < `z`）ので、同じ日の 2 回目のビルドが 1 回目に負ける。

**公開していいのはライセンスが再配布を許すものだけ。** 表記の不在は許諾ではない。
判断は「splat データは配布できない。同梱もしない」節。

### ホスティング（`worker/`）

**https://vdgs.saqoo.sh/ — Cloudflare Worker 1 本。**

| パス | 出どころ |
|---|---|
| `/`, `/assets/*`, `/catalog.json` | 静的アセット（`build/release/site`） |
| `/scene/*`, `/track/*`, `/app/*` | R2 バケット `vdgs`（`build/release/files`） |

**分けている理由はサイズだけ** — デプロイは 1 ファイル 25 MiB 上限、キャプチャは数百 MB。
**オリジンは 1 つ**にしてあるので、カタログの URL とページのリンクが食い違いようがない。
R2 は Range 対応で streaming（回線が切れても再開できる）、`immutable` で長期キャッシュ
（公開ファイルは同じ名前で中身が変わらない）。

- **`wrangler` は `a@saqoo.sh` で認証済み**（`~/.wrangler/config/default.toml`）。
  `npx wrangler` でそのまま使える。**ダッシュボードは要らない** — カスタムドメインは
  `wrangler.jsonc` の `routes` に `custom_domain: true` で付く
- **saqoo.sh のゾーンは `User-Agent: Python-urllib/*` を 403 で弾く。** curl も
  companion（`VDGSCompanion`）も 200。スクリプトから叩くときは UA を変える

### 検証で使える基準値

| 測るもの | 値 |
|---|---|
| まっさらなゲームフォルダに `INSTALL MOD` | 133 ファイル / 5.2 MB、約 1 秒 |
| ＋ FDF を `02 GET` | 142 ファイル / 134.0 MB |
| `vdgs-shaders` | 1,538,627 バイト（1MB 未満は失敗版） |
| FDF のキャプチャ zip | 123,654,552 バイト / 1,497,617 splats |
| companion の zip | 約 6.0 MB |
| FDF のダウンロード | コールド 54 秒、エッジキャッシュ後 5 秒 |

**GUI は実機でしか確かめられない。** セッション 0 には窓が無いので、起動も撮影も
クリックもスケジュールタスク（`New-ScheduledTaskPrincipal -LogonType Interactive`）経由。
`tools/appstart-win.ps1` が起動して残し、撮影スクリプトが前面に上げて撮る。
**合成クリック**（`SetCursorPos` + `mouse_event`）で本物のボタンを押せる — 偽のゲーム
フォルダ（`velocidrone.exe` という名前の空ファイル 1 個）に向ければ**本番を触らずに
通し確認できる**。

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

**RT の深度アタッチメントは付けない。** upstream の
`SetRenderTarget(rt, BuiltinRenderTextureType.CurrentActive)` は、ゲームの HDR +
PostProcessing カメラ（D3D12）で**バインドごと無言で失敗する** — splat がカメラターゲット
に直接描かれ、composite は空の RT を素通しし、暗い splat が Linear パイプラインの sRGB
持ち上げを食う。色のみバインドし、前後関係は splat シェーダーで `_CameraDepthTexture` を
サンプルして解く（`m_DepthClip`）。**エラーは 1 行も出ない。**

**`-force-d3d12` 必須。** ソートの compute が SM6 の wave intrinsics を 41 箇所使う。
`-force-vulkan` は**ゲーム自身**が描けない（VelociDrone が Vulkan 向けにビルドされていない）。
companion の `FLY` は常にこのフラグを付ける。

**シェーダーは Windows の Unity 2021.3.45f2 でしか焼けない。** 罠が 2 つ：

- **プロジェクトのグラフィックス API を先に D3D12 にする**（`PlayerSettings.SetGraphicsAPIs`
  をビルド前に呼ぶ）。既定のまま焼くと無言で unsupported になる — 症状とサイズの基準は
  「開発フロー」
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
| D3D12 | 有り（メニュー放置、2026-09-01） | **3 分 44 秒** | Crash + `_LightTexture0` |
| D3D11 | 無し | 13 分以上 | 無傷 |
| **D3D12** | **有り**（トラック内。メニュー滞在 5 秒） | **15.8 分** | **無傷** |

**3 本目で下限が 5 分から 3 分 44 秒に下がった**（12:56:26 起動、13:00:10 が `Player.log`
最終書き込み）。「約 5 分」は安全側の見積もりではない。**メニューに戻したら、放置しない。**

**クラッシュハンドラは走るが、ダンプは残らない。** `Player.log` は
`C:/Users/a/AppData/Local/Temp/velocidrone/velocidrone/Crashes` を名指しするが、
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
- **空を学習から除くマスクは未実施。** FDF の巨大 splat は色まで測ると空と雲そのもの
  （高度 15m 超・2m 超の 1,694 個が平均 RGB 0.79/0.82/0.85、不透明度 0.77）で、
  `--max-sigma` はそれを後から削っているだけ。**マスクは空側を削る方向に膨張させる** —
  細い前景（電線・旗のポール・枝）を食うので。詳細は docs/alignment.ja.md
- **FDF の芝の平坦化は不要だった可能性が高い。** 平坦化前でも地面は破綻せず、むしろ芝目が
  残る（「over flattened で眠い」の原因）。一方**ゴミ除去は本当に必要**で、生データは空が
  白く埋まる
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
