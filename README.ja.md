# VDGS

*[English](README.md)*

VelociDrone の中に 3D Gaussian Splatting シーンを表示する mod。

実際にスキャンした場所を FPV ドローンシムの中に持ち込んで飛ぶための道具。

![bonsai を VelociDrone 内に表示](docs/bonsai-real-data.png)

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

外部ツールは要らない。217 万 splats のキャプチャが 1 秒未満で読み込まれる。
事前に変換した形（5 バイナリ + `meta.json`）も引き続き使える。

トラック名を 1 秒ごとに監視し、`bindings.json` の対応表に従って GS を出し入れする。
紐付けたトラックでだけ出て、それ以外では出ない。

描画部分は [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting)（MIT）
の移植。編集機能・URP/HDRP パス・Burst 依存を落として、注入環境で動く形にしてある。

## ドキュメント

| | |
|---|---|
| [docs/USAGE.ja.md](docs/USAGE.ja.md) | 導入・起動・データ投入・操作 |
| [docs/ARCHITECTURE.ja.md](docs/ARCHITECTURE.ja.md) | 内部構造と設計判断の理由 |
| [docs/ply-loading.ja.md](docs/ply-loading.ja.md) | .ply を実行時に読む仕組みと罠 |
| [docs/performance.ja.md](docs/performance.ja.md) | 描画コストの内訳と、削るための手 |
| [docs/verification.ja.md](docs/verification.ja.md) | 描画結果を数値で検証する道具 |
| [docs/alignment.ja.md](docs/alignment.ja.md) | キャプチャの向き合わせと鏡像の扱い |
| [AGENTS.md](AGENTS.md) | 環境の実測値、踏んだ罠の全記録 |

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

## ライセンス

`src/VDGS/GpuSorting.cs` と `unity/VDGSBundler/Assets/VDGS/Shaders/` は
[aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting)（MIT）に由来する。
GPU ソートはさらに [b0nes164/GPUSorting](https://github.com/b0nes164/GPUSorting)（MIT, Thomas Smith）由来。
