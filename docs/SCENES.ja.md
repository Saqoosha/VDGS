# キャプチャを足す

*[English](SCENES.md)*

3D Gaussian Splatting の `.ply` を VelociDrone で飛ぶまでの手順。壁と床で止まるための
コリジョンメッシュの焼き方も含む。

**mod にキャプチャは付いてこない。** 権利のある `.ply` を自分で用意する。学術データセットも
SuperSplat の公開シーンも他人の作品で、ライセンス表記が無いことは再配布の許可ではない。
落として自分で飛ぶのと、再公開するのは別の話。

導入と起動は [USAGE.ja.md](USAGE.ja.md)。このファイルはキャプチャ側のパイプライン。

---

## 1. `.ply` を用意する

自分で撮って再構成する（Postshot、Brush、元の 3DGS トレイナなど）、または
[SuperSplat](https://superspl.at/editor) から書き出す。プラグインが読むのは **`.ply` だけ**。
`.splat` は読めない。

**床を撮る。** 被写体の周りを回っただけのキャプチャは、地面がカメラ方向に引き伸ばされた
ガウシアンになる。上から見れば床は埋まって見え、ドローン目線では溶ける。部屋を歩いて、
レンズを床に向ける。

室内を移動しながら撮ったキャプチャは飛ぶ。物体周回（テーブルの上の盆栽）は飛ばない。
Y を上げても直らない。

---

## 2. 向きを直す

COLMAP 由来のデータは向きもスケールも任意で、**Unity では必ず鏡像になる**（右手系 Y-down
対左手系 Y-up）。`PlyExporter` はこれを直さない。立つ `.ply` を渡す。

合わせるのは SuperSplat の**正射影**ビュー（ビューキューブの円をクリック）：

1. 床を水平に、天井を実寸（部屋なら 2.4〜2.7 m 程度）
2. `.ply` で書き出す
3. SuperSplat の書き出しは Y が反転する。`python3 tools/updir.py in.ply` で確認してから：

```bash
python3 tools/align_ply.py in.ply out.ply --mirror y
```

`--rotate 180,0,0` ではこの鏡映の代わりにならない。被写体ではなく、文字か左右非対称なもので
判定する。理由は [alignment.ja.md](alignment.ja.md)。

パーセンタイル crop はしない。内側から撮った部屋の壁は外周そのもの。拘束の無い巨大
ガウシアン（空、カメラパスの先）は**サイズ**で切る：

```bash
python3 tools/align_ply.py in.ply   # 報告だけ
python3 tools/align_ply.py in.ply out.ply --max-sigma 5
```

---

## 3. 置いて絵を飛ばす

```
<VelociDrone>\app\vdgs\myscene.ply
```

離陸前に `http://<host>:8777/` から表示する。初回は数十 MB のアップロードで止まる。
絵が正しければトラックに紐付ける。拡縮と高さはこのページ、回転は SuperSplat。

直置きの `.ply` はローダが Y を鏡映する。**すでに床が下向き**（`--mirror y` して変換した、
または手で直した）なら `.ply` 直置きは天井立ちになる。その場合は変換して置く。詳細は
[ply-loading.ja.md](ply-loading.ja.md)。

変換は任意。ファイルが小さくなり、ロードが速くなる。`High` を使う：

```bash
Unity -batchmode -quit -nographics -projectPath unity/VDGSConverter \
      -executeMethod PlyExporter.Run \
      -vdgsInput /abs/path/myscene.ply \
      -vdgsOutput /abs/path/build/splats/myscene \
      -vdgsQuality High -logFile -
```

出力ディレクトリを `<game>\vdgs\myscene\` にコピーする。

---

## 4. コリジョンメッシュを焼く

これがないとドローンは床を抜けてゲームの地面まで落ちる。絵はガウシアン、コライダーは
位置から焼いた三角形。

### 必要なもの

OpenVDB の `vdb_tool`。**Linux または WSL。** Homebrew の `openvdb` はこれをビルドしない。

```bash
# Ubuntu / WSL
sudo apt-get install -y python3-openvdb libopenvdb-tools python3-venv
python3 -m venv ~/vdgsvenv
~/vdgsvenv/bin/pip install numpy fast-simplification
```

前後の処理（`align_ply.py`、`ply_points.py`、`glb_to_collision.py`）は macOS でも走る。
レベルセットの焼きと最初の間引きだけ `vdb_tool` のある場所で。

他に `npx`（`@playcanvas/splat-transform`）と `tools/` の Python。

### voxel サイズ

つまみはひとつ。他はこれに追従する。

| | |
|---|---|
| 絵の面からの隙間 | 約 **2 × voxel** |
| 壁の厚み | 約 **4 × voxel** |
| 物理 | 400 Hz。150 km/h では 1 ステップ 10 cm 進むので、**厚さ 10 cm 未満の壁はすり抜ける** |

細かいほど絵に近く、壁は薄く、シートに穴が増える。粗いほど柱が太く、穴は減る。室内なら
**0.02〜0.06 m** から始めて、飛んで決める。屋外の敷地はもっと粗く。

工場の床は 0.06 で穴があったが飛べた。0.14 は柱が太すぎた。落下テストは診断であって合否では
ない。スクリプトの球が抜けてもドローンは止まることがある。

### コマンド

`myscene.ply` と `VOXEL` を置き換える。OpenVDB のブロックは Linux/WSL で走らせる
（100〜400 MB の中間メッシュをネットワークに乗せないため）。

```bash
# 前処理（Python と npx があるマシン）
python3 tools/align_ply.py myscene.ply clean0.ply --max-sigma 5
npx -y @playcanvas/splat-transform@3.3.0 -w clean0.ply \
    --filter-floaters --filter-cluster clean.ply
python3 tools/ply_points.py clean.ply points.ply

# 焼き + 間引き（Linux / WSL）— voxel はメートル
VOXEL=0.04
vdb_tool -read points.ply \
  -points2ls voxel=$VOXEL radius=2.0 width=4 \
  -median iter=1 -open radius=1 \
  -ls2mesh adapt=0.9 -write fine.ply
python3 tools/decimate_mesh.py fine.ply reduced.ply 500000

# 島の除去、glb、ランタイム形式（どこでも）
python3 tools/clean_mesh.py reduced.ply mesh.ply \
    --voxel $VOXEL --min-voxels 100 --min-extent 0.25
python3 tools/mesh_to_glb.py mesh.ply collision.glb
python3 tools/glb_to_collision.py collision.glb myscene.collision.bin
```

`bash tools/preview.sh myscene 0.04` が同じパイプラインにブラウザの重ね表示を足した版。
**`vdb_tool` が無く、`VDGS_HOST` も未設定なら失敗する。** splat-transform の voxel
メッシュには落ちない（測ったらギャップが約 8 倍）。

`--reverse` は巻き順を反転する。部屋によっては要る（付けないと殻の外側に乗る）。付けると
壊れるシーンもある。符号付き体積から推測しない。Web UI の **show solid**：正しく巻いて
あれば室内から壁が見え、裏返しだと消える。あとは飛ぶ。止めてくれるほうのフラグを残す。

### ファイルの場所

| キャプチャ | コリジョン |
|---|---|
| `<game>\vdgs\myscene.ply` | `<game>\vdgs\myscene.collision.bin` |
| `<game>\vdgs\myscene\`（変換済み） | `<game>\vdgs\myscene\collision.bin` |

ファイルを差し替えたらキャプチャを出し直す。`solid` の付け外しでは再読み込みしない。

### Web UI

| | |
|---|---|
| `solid` | メッシュがドローンを止める。Off はすり抜け。最初の cook のあと、飛行中のトグルは無料 |
| `hide mesh` / `show solid` / `show wire` | 殻の描画。solid は背面カリング（巻き順のテスト）。wire は形 |
| scale / Y | その場で効く。`placement.json` に書かれる。コライダーは子なので追従する |

`collision.bin` が無いキャプチャにチェックボックスは出ない。切ってあるのではなく、
メッシュが無い。

`solid` が on なら、飛ぶ前に表示しておく。数十万三角形の cook は splat のアップロードと
同じスポーン停滞に乗る。

---

## 5. シーンの zip ではなくレシピを配る理由

開発中に飛んだキャプチャを配る権利はこちらに無い。配れるのはこのパイプラインと、
`tools/make_test_ply.py`（軸確認用の合成部屋）だけ。

数字・失敗・捨てた手法は
[コリジョン設計ノート](superpowers/specs/2026-08-18-splat-collision-design.md)。
このページは手を動かす側。
