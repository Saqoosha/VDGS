# VDGS

VelociDrone の中に 3D Gaussian Splatting シーンを表示する mod。

実際にスキャンした場所を FPV ドローンシムの中に持ち込んで飛ぶための道具。

![bonsai を VelociDrone 内に表示](docs/bonsai-real-data.png)

## 動作実績

| | |
|---|---|
| 最大 splat 数 | **3,177,554**（drjohnson 単体）／ 117 万を 3 シーン同時 |
| 描画時間 | **9.0 ms**（317 万 splats、RTX 3060、ドローン目線・画角 120°） |
| 深度 | ゲートや機体との前後関係、半透明ブレンドとも破綻なし |

SH のパレット圧縮と視錐台カリングで、どちらも**画質を落とさずに** 13.3 → 9.0 ms。
内訳は [docs/performance.md](docs/performance.md)。

## 仕組み

```
<game>/vdgs/foo.ply を置く → プラグインが実行時に読んで描画
                             ↑ ブラウザから操作（http://<host>:8777/）
```

外部ツールは要らない。事前に変換したい場合は 5 バイナリ + meta.json の形も読む。

トラック名を1秒ごとに監視し、`bindings.json` の対応表に従って GS を自動で出し入れする。

描画部分は [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting)（MIT）
の移植。編集機能・URP/HDRP パス・Burst 依存を落として、注入環境で動く形にしてある。

## ドキュメント

| | |
|---|---|
| [docs/USAGE.md](docs/USAGE.md) | 導入・起動・データ投入・操作 |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | 内部構造と設計判断の理由 |
| [docs/performance.md](docs/performance.md) | 描画コストの内訳と、削るための手 |
| [docs/verification.md](docs/verification.md) | 描画結果を数値で検証する道具 |
| [docs/alignment.md](docs/alignment.md) | キャプチャの向き合わせと鏡像の扱い |
| [docs/ply-loading.md](docs/ply-loading.md) | .ply を実行時に読む仕組みと罠 |
| [AGENTS.md](AGENTS.md) | 環境の実測値、踏んだ罠の全記録 |

## 必要なもの

- VelociDrone（Unity 2021.3.45f2 ビルド）
- **D3D12 対応 GPU** — splat のソートが Shader Model 6 の wave intrinsics を要求するため、
  DX11 では動かない。ゲームは `-force-d3d12` で起動する
- BepInEx 5.4.23.5 (win_x64)
- Unity 2021.3.45f2（シェーダー用。**Windows でのビルドが必須**）
- Unity 2022.3.x（`.ply` 変換用。Mac 可）

## 注意

**リーダーボードとマルチプレイでは使わないこと。** VelociDrone には
`ACTk.Runtime.dll`（Anti-Cheat Toolkit）が同梱されている。改造クライアントで
タイムを投稿するのは規約違反にあたる。ローカル飛行専用。

## ライセンス

`src/VDGS/GpuSorting.cs` と `unity/VDGSBundler/Assets/VDGS/Shaders/` は
[aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting)（MIT）に由来する。
GPU ソートはさらに [b0nes164/GPUSorting](https://github.com/b0nes164/GPUSorting)（MIT, Thomas Smith）由来。
