# VDGS

*[English](README.md)*

VelociDrone の中に 3D Gaussian Splatting シーンを表示する mod。

実際にスキャンした場所を FPV ドローンシムの中に持ち込んで飛ぶための道具。

[![スキャンした場所を VelociDrone の中で飛行](docs/vdgs.jpg)](https://www.youtube.com/watch?v=MuDq_7X-4Mo)

[飛行映像](https://www.youtube.com/watch?v=MuDq_7X-4Mo)

## 動作実績

| | |
|---|---|
| 最大 splat 数 | **3,177,554**（drjohnson 単体）／ 117 万を 3 シーン同時 |
| 描画時間 | **9.0 ms**（317 万 splats、RTX 3060、ドローン目線・画角 120°） |
| 深度 | ゲートや機体との前後関係、半透明ブレンドとも破綻なし |

SH のパッキングと視錐台カリングで、どちらも**画質を落とさずに** 13.3 → 9.0 ms。
内訳は [docs/performance.ja.md](docs/performance.ja.md)。

## 仕組み

```
<game>/vdgs/foo.ply を置く → プラグインが実行時に読んで描画
                             ↑ ブラウザから操作（http://<host>:8777/）
```

外部ツールは要らない。217 万 splats のキャプチャはパースが 1 秒未満、画面に出るまで約 3 秒。
400 万 splats に SH が付くと 13 秒かかるので、**飛びながらではなく飛ぶ前に**表示させること。
事前に変換した形（5 バイナリ + `meta.json`）も引き続き使える。

トラック名を 1 秒ごとに監視し、`bindings.json` の対応表に従って GS を出し入れする。
紐付けたトラックでだけ出て、それ以外では出ない。

描画部分は [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting)（MIT）
の移植。編集機能・URP/HDRP パス・Burst 依存を落として、注入環境で動く形にしてある。

## ドキュメント

| | |
|---|---|
| [docs/USAGE.ja.md](docs/USAGE.ja.md) | 導入・起動・操作 |
| [docs/SCENES.ja.md](docs/SCENES.ja.md) | `.ply` の用意、向き合わせ、コリジョンの焼き方 |
| [docs/TRACKS.ja.md](docs/TRACKS.ja.md) | キャプチャの上にコースを組んで配れる形にする |
| [docs/distribution.ja.md](docs/distribution.ja.md) | companion アプリ、リリースの通し、ホスティング |
| [docs/ARCHITECTURE.ja.md](docs/ARCHITECTURE.ja.md) | 内部構造と設計判断の理由 |
| [docs/ply-loading.ja.md](docs/ply-loading.ja.md) | .ply を実行時に読む仕組みと罠 |
| [docs/performance.ja.md](docs/performance.ja.md) | 描画コストの内訳と、削るための手 |
| [docs/verification.ja.md](docs/verification.ja.md) | 描画結果を数値で検証する道具 |
| [docs/alignment.ja.md](docs/alignment.ja.md) | キャプチャの向き合わせと鏡像の扱い |
| [AGENTS.md](AGENTS.md) | 踏んだ罠の実測（日本語）。マシン固有のパスは書いていない |

## 必要なもの

- VelociDrone（Unity 2021.3.45f2 ビルド）
- **D3D12 対応 GPU** — splat のソートが Shader Model 6 の wave intrinsics を要求するため、
  DX11 では動かない。ゲームは `-force-d3d12` で起動する
- BepInEx 5.4.23.5 (win_x64)

mod をビルドするには、シェーダーバンドル用に **Windows 上の** Unity 2021.3.45f2 が要る
（macOS では D3D 向けシェーダーをコンパイルできない）。**キャプチャを足すだけなら何も要らない** —
`.ply` を置くだけ。

## 注意

**リーダーボードとマルチプレイでは使わないこと。** VelociDrone には
`ACTk.Runtime.dll`（Anti-Cheat Toolkit）が同梱されている。改造クライアントで
タイムを投稿するのは規約違反にあたる。ローカル飛行専用。

## splat データは同梱していない

**この mod にキャプチャは付いてこない。** リポジトリに入っている splat データは
`tools/make_test_ply.py` が生成する合成シーンだけ（軸・色・スケールの検証用）。開発中に
飛んだものは全部他人の著作物 — 学術データセットは**そもそもライセンスを表記していない**
（それは「自由」ではなく全権利留保）、INRIA 3DGS のライセンスは研究目的限定でその制限が
派生物にも及び、残りは SuperSplat に公開された第三者のキャプチャ。

`.ply` は自分で用意して `<game>/vdgs/` に置く。コリジョンは任意で、手元で焼く —
[docs/SCENES.ja.md](docs/SCENES.ja.md)。

## ライセンス

このプロジェクトは [MIT](LICENSE)。`src/VDGS/GpuSorting.cs` と
`unity/VDGSBundler/Assets/VDGS/Shaders/` はさらに
[aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting)（MIT）に由来する。
GPU ソートはさらに [b0nes164/GPUSorting](https://github.com/b0nes164/GPUSorting)（MIT, Thomas Smith）由来。
