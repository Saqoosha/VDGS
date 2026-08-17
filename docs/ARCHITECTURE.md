# VDGS 内部構造

このドキュメントは**なぜそうなっているか**を書く。手順は [USAGE.md](USAGE.md)、
環境固有の実測値と踏んだ罠は [AGENTS.md](../AGENTS.md)。

---

## 全体像

```
  Mac（開発）                          Windows（実行）
┌──────────────────────┐          ┌────────────────────────────────┐
│ .ply / .spz          │          │  VelociDrone (Unity 2021.3.45f2)│
│   │                  │          │  ┌──────────────────────────┐  │
│   │ crop_ply.py      │          │  │ BepInEx 5.4 (Doorstop)   │  │
│   ▼                  │          │  │   └─ VDGS.dll            │  │
│ PlyExporter          │          │  │        ├ SplatRenderer   │  │
│ (Unity 2022.3)       │          │  │        ├ TrackName       │  │
│   │                  │          │  │        ├ TrackBindings   │  │
│   ▼                  │ deploy.sh│  │        └ WebControl :8777│  │
│ meta.json + 5 .bin ──┼─────────▶│  └──────────────────────────┘  │
│                      │   (scp)  │            ▲                    │
│ VDGSBundler          │          │            │ HTTP               │
│ (Unity 2021.3.45f2)  │          └────────────┼────────────────────┘
│   │ ※Windows で焼く │                       │
│   ▼                  │                  ブラウザ（Mac から Tailscale 経由）
│ vdgs-shaders ────────┼──────────────────────┘
└──────────────────────┘
```

Unity プロジェクトが2つあるのは意図的。**バージョンを分けざるを得ない**：

| | Unity | 理由 |
|---|---|---|
| `unity/VDGSBundler` | **2021.3.45f2** | シェーダーはゲームと同一バージョンでしか読めない |
| `unity/VDGSConverter` | **2022.3.x** | UnityGaussianSplatting が `com.unity.collections` 2.x に依存し、2021.3 に入らない |

変換の出力はプレーンなバイナリなので、Converter 側のバージョンは何でもいい。
Bundler 側だけが厳密。

---

## AssetBundle だけで足りない理由

VelociDrone のトラックエディタで置けるオブジェクトは、素の AssetBundle から
読まれている（`trees`, `gates`, `barriers` …）。**そこに splat を足す形にはできない。**

理由は 3 つ：

1. **MonoBehaviour の型がゲーム側に存在しない。** AssetBundle 内のコンポーネントは
   ゲームのアセンブリに同名クラスがあって初めて復元される。`SplatRenderer` は
   ゲームに無いので、プレハブに載せても参照が壊れる
2. **compute shader を dispatch する主体がいない。** splat のソートは毎フレーム
   compute を回す必要があり、それを駆動する C# がどこかで動いていなければならない
3. **CommandBuffer をカメラに挿す必要がある。** 描画はマテリアル1枚では完結せず、
   カメラのレンダリングパイプラインに割り込む

だからコード注入（BepInEx）が要る。AssetBundle は**シェーダーの入れ物としてのみ**使う。

---

## データの形

### ScriptableObject を捨てた理由

upstream（aras-p/UnityGaussianSplatting）は `GaussianSplatAsset` という
ScriptableObject にデータを持つ。中身は**メタ情報 + 5 つの生バイナリ TextAsset**
でしかない。

注入されたアセンブリの中では、これが使えない：

- `AssetDatabase` が存在しない（Editor 専用 API）
- AssetBundle から ScriptableObject を復元するには、その型がゲーム側から解決できる
  必要がある。`GaussianSplatAsset` はゲームに無い

**同じ中身をただのファイルとして置けば、この問題は消える。**

```
<game>/vdgs/<name>/
  meta.json     splat 数、フォーマット、バウンディングボックス
  chunk.bin     ChunkInfo[]（64 バイト/要素）。任意
  pos.bin       位置。GraphicsBuffer.Target.Raw
  other.bin     回転・スケール。同上
  color.bin     色。Texture2D にアップロード
  sh.bin        球面調和。同上
```

`SplatData.Load()` がこれを読み、`SplatRenderer` が GPU バッファに流す。

### `ChunkInfo` は 64 バイト

```
uint   colR, colG, colB, colA     4 x 4 = 16
float2 posX, posY, posZ           3 x 8 = 24
uint   sclX, sclY, sclZ           3 x 4 = 12
uint   shR,  shG,  shB            3 x 4 = 12
                                        = 64
```

HLSL 側と一致していなければならず、**間違えても例外は出ず、描画が静かに壊れる**。
実データの変換結果（640 splats → 3 チャンク → 192 バイト）で裏を取ってある。

---

## 描画

`SplatRenderSystem` + `SplatRenderer` は upstream の移植だが、以下を落としてある：

- 編集機能（selection / cutouts / export）
- URP・HDRP のパス（VelociDrone は Built-in RP）
- `Unity.Mathematics` / `Unity.Collections` / `Burst` への依存
- `Unity.Profiling`

結果、依存は UnityEngine だけになった。`GpuSorting.cs` は元から UnityEngine のみに
依存していたので、namespace 以外は無改変。

### フレームの流れ

```
Camera.onPreCull
  └ GatherSplatsForCamera      表示中の splat を集めて奥から手前に並べる
  └ CommandBuffer を構築
       ├ GetTemporaryRT        splat 専用の RT を確保（R16G16B16A16_SFloat）
       ├ SortPoints            カメラ距離を compute で計算 → GPU radix sort
       ├ CalcViewData          各 splat のスクリーン空間データを compute で計算
       ├ DrawProcedural        1 splat = 1 quad、instancing で一括描画
       └ Composite             RT をカメラターゲットに合成
  └ CameraEvent.BeforeForwardAlpha に挿す
```

`BeforeForwardAlpha` に入れることで、**不透明ジオメトリの後・透明の前**に描かれる。
ゲートや機体との前後関係が正しく出るのはこのため。

### D3D12 が必須な理由

ソートに使う `DeviceRadixSort.hlsl` が Shader Model 6 の wave intrinsics
（`WavePrefixSum`, `WaveReadLaneAt` など 41 箇所）を使う。DX11 にはこの命令が無い。

`-force-vulkan` では**ゲーム自身**が描画できない（VelociDrone は Vulkan 向けに
ビルドされていない）。よって **D3D12 の一択**。

---

## トラックと GS の対応

### 表示を決めるのはトラック名

シーナリー（Empty Scene Day など）単位ではない。**1 つのシーナリーに何本もトラックが
載る**ので、シーン単位で判定すると、そのマップを使う全トラックに splat が出てしまう。

```
bindings.json:  { "<トラック名>": ["<GS名>", ...] }
```

紐付けの無いトラックでは**何も表示しない**。間違った GS を出すより無害という判断。

### ポーリングにした理由

`SceneManager.sceneLoaded` では足りない。ゲーム内の change track ダイアログは
**Unity シーンを変えずにトラックだけ差し替える**。だから 1 秒ごとにトラック名を
読み、変化したら splat を入れ替える（`PollTrack`）。

### トラック名の取得は総当たりで見つけた

Assembly-CSharp は難読化されていて、フィールド名は `glnoaiifnln` のような文字列。
さらに**文字列定数がシャッフルされている**ため、デコンパイルしても嘘しか読めない。

`TrackProbe`（F12）が、生きているオブジェクト・UI テキスト・プロパティ・コレクションを
全部走査して、指定した文字列を含むフィールドを報告する。トラック名を
`VDGSPROBE7777` のような固有な値にしておくと一発で出る。

見つかった carrier：

| 場所 | 挙動 |
|---|---|
| `InGameChangeTrack.glnoaiifnln` | ロード中のトラックを追う。**第一候補** |
| `RaceInfo2/View - Gameplay/TrackName`（TMP） | 飛行 HUD。同じ値。フォールバック |
| ~~`EditorManager.nnpnlmbjocf`~~ | **使ってはいけない**（下記） |

`EditorManager.nnpnlmbjocf` は最初に見つかり、一見完璧に見えた。しかし実際は
「最後に**エディタで**開いたトラック」で、別のトラックをロードして飛んでも更新されない。
別トラックを開いて初めて発覚した。難読化された環境では、**1 つの値が一度正しく見えた
だけでは根拠にならない**という教訓。

`TrackName.cs` は複数の carrier を順に試し、どれも解決できなければクラス内の
全 string フィールドを走査するフォールバックまで持つ。アップデートで難読化名が
変わっても、UI 経由で動き続ける。

---

## 操作面

### Web UI にした理由

ゲーム内キーでの操作を試し、**全部潰れた**：

| キー | 衝突 |
|---|---|
| F7 | トラックエディタのシーン保存 |
| 矢印キー | トラックエディタのオブジェクト移動 |
| Numpad | ノート PC に無い |

さらに致命的なのは、**ゲームに HUD を描く場所がない**こと。キーを押しても結果が
見えないので、ログを読むまで成功したか分からない。

プロセスの外に出すと、これが全部消える。加えて別マシンから操作できる
（Parsec でゲーム画面を見ながら、手元の Mac のブラウザで操作する）。

```
HttpListener (:8777)
  ├ GET  /            埋め込み HTML（WebUi.cs）
  ├ GET  /api/status  現在のトラック / 表示中 / 利用可能 / 全紐付け
  ├ POST /api/load    指定の GS だけ表示
  ├ POST /api/unload  全部隠す
  ├ POST /api/bind    現在のトラックに紐付け
  └ POST /api/unbind  紐付けを解除
```

### スレッド境界

`HttpListener` は専用スレッドで受ける。**Unity のオブジェクトはメインスレッドからしか
触れない**ので、リクエストは `Queue<Action>` に積み、`Update()` から `Pump()` で流す。

`GET /api/status` だけは値を返す必要があるため、メインスレッドに投げて
`ManualResetEventSlim` で待つ（5 秒でタイムアウト。ゲームが停止していても
ハングしないため）。

### セキュリティ

**トラック名は攻撃者が書ける文字列。** VelociDrone はコミュニティのトラックを
ダウンロードでき、その名前がそのまま UI に出る。サーバーは LAN 全体に開いている。

3 つで守っている：

1. **`innerHTML` を使わない。** `createElement` + `textContent` で組む。
   一度これを怠り、`<img src=x onerror=...>` という名前のトラックを 1 本落とすだけで
   任意コードが動く状態を作ってしまった
2. **CORS ヘッダを出さない。** UI は同一オリジンから配信されるので不要。
   付けると利用者が開いた任意のサイトから API を叩ける
3. **POST に `Content-Type: application/json` を要求する。** クロスオリジンのページは
   preflight なしにこのヘッダを付けられず、CORS ポリシーも無いので通らない。
   これが CSRF の防波堤。2 だけでは `text/plain` の simple request で抜けられる

---

## 位置合わせを実装しない理由

一度は実装した（キーで移動・回転・拡縮、`placement.json` に保存）。**削除した。**

splat の座標をシムの中で合わせるのは、道具として筋が悪い：

- ゲーム内には数値を表示する場所がなく、目分量になる
- 撮影・学習側（Postshot など）は、そもそも正しい座標系で出力できる
- mod 側に持つと、GS を差し替えるたびに合わせ直しになる

`placement.json` は残してあるが、**手で書く最終手段**であって、ゲーム内から変更する
手段は無い。

同じ理由で**コリジョンも実装していない**。飛べるコースにしたければ、
VelociDrone 純正のゲートやバリアを置けばよく、それらは最初からコリジョンを持つ。

---

## ファイル対応表

| ファイル | 責務 |
|---|---|
| `Plugin.cs` | エントリポイント、各機能の配線、トラック監視 |
| `SplatData.cs` | `meta.json` + 5 バイナリ → メモリ |
| `SplatRenderer.cs` | GPU バッファ確保、CommandBuffer 構築、描画 |
| `GpuSorting.cs` | 8bit radix sort（upstream ほぼ無改変） |
| `ShaderBundle.cs` | AssetBundle からシェーダーを取得 |
| `SplatScene.cs` | splat 1 つ分の生成・破棄・配置読み込み |
| `TrackName.cs` | ロード中のトラック名を多段フォールバックで取得 |
| `TrackBindings.cs` | `bindings.json` の読み書き |
| `TrackProbe.cs` | 難読化されたゲームから文字列の在処を探す（F12） |
| `WebControl.cs` | HTTP サーバー、スレッド境界の管理 |
| `WebUi.cs` | ブラウザ UI（埋め込み HTML） |
| `Probe.cs` | ランタイム環境の実測ダンプ（F9） |
| `PerfLog.cs` | フレームタイム記録 |
| `PostProcessFix.cs` | D3D12 強制の副作用対応（**効かない**。経緯の記録として残置） |

---

## 拡張するときに

- **API を足す**: `WebControl.Handle()` に case を追加し、`Plugin` 側にハンドラを書いて
  デリゲートに繋ぐ。Unity に触る処理は必ず `QueueOnMain` を通す
- **UI を足す**: `WebUi.Html` に追記。**動的な値は必ず `textContent` で入れる**
- **ゲームの内部状態を読みたい**: `TrackProbe` の needle を変えて F12。
  デコンパイル結果の定数は信用しない
- **シェーダーを変える**: `unity/VDGSBundler` で焼き直し。**Windows で**。
  焼けたバンドルが 1MB 未満なら失敗している
