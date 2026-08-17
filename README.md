# VDGS

VelociDrone の中に 3D Gaussian Splatting シーンを表示する mod。

実際にスキャンした場所を FPV ドローンシムの中に持ち込んで飛ぶための道具。

![bonsai を VelociDrone 内に表示](docs/bonsai-real-data.png)

## 動作実績

| | |
|---|---|
| 最大 splat 数 | **1,157,141**（3 シーン同時） |
| フレームレート | **60 FPS 張り付き**（RTX 3060、worst frame 16.67ms） |
| 深度 | ゲートや機体との前後関係、半透明ブレンドとも破綻なし |

VSync の上限に当たっているので、実際の余力はこれより上。

## 仕組み

```
.ply → 生バイナリ5個 + meta.json → BepInEx プラグインが GPU に流して描画
                                    ↑ ブラウザから操作（http://<host>:8777/）
```

トラック名を1秒ごとに監視し、`bindings.json` の対応表に従って GS を自動で出し入れする。

描画部分は [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting)（MIT）
の移植。編集機能・URP/HDRP パス・Burst 依存を落として、注入環境で動く形にしてある。

## ドキュメント

| | |
|---|---|
| [docs/USAGE.md](docs/USAGE.md) | 導入・起動・データ投入・操作 |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | 内部構造と設計判断の理由 |
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
