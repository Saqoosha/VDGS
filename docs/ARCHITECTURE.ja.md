# VDGS 内部構造

*[English](ARCHITECTURE.md)*

このドキュメントは**なぜそうなっているか**を書く。手順は [USAGE.ja.md](USAGE.ja.md)、
環境固有の実測値と踏んだ罠は [AGENTS.md](../AGENTS.md)。

---

## 全体像

```
  Mac（開発）                          Windows（実行）
┌──────────────────────┐          ┌────────────────────────────────┐
│ .ply / .spz          │          │  VelociDrone (Unity 2021.3.45f2)│
│   │                  │          │  ┌──────────────────────────┐  │
│   │ verify_orient.py │          │  │ BepInEx 5.4 (Doorstop)   │  │
│   ▼                  │          │  │   └─ VDGS.dll            │  │
│ PlyExporter          │          │  │        ├ PlyLoader       │  │
│ (Unity 2022.3)       │          │  │        ├ SplatRenderer   │  │
│   │  ※任意          │          │  │        ├ TrackName       │  │
│   ▼                  │ deploy.sh│  │        ├ TrackBindings   │  │
│ meta.json + 5 .bin ──┼─────────▶│  │        └ WebControl :8777│  │
│ または .ply そのまま │   (scp)  │  └──────────────────────────┘  │
│                      │          │            ▲                    │
│ VDGSBundler          │          │            │ HTTP               │
│ (Unity 2021.3.45f2)  │          └────────────┼────────────────────┘
│   │ ※Windows で焼く │                       │
│   ▼                  │                  ブラウザ（LAN 上の任意のマシン）
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

`meta.json` は upstream の `GaussianSplatAsset` の要点だけを持つ：

```json
{ "formatVersion": 20231020, "splatCount": 1234567, "chunkCount": 0,
  "boundsMin": [0,0,0], "boundsMax": [1,1,1],
  "posFormat": "Norm11", "scaleFormat": "Norm11",
  "colorFormat": "Norm8x4", "shFormat": "Norm6" }
```

`formatVersion` は `GaussianSplatAsset.kCurrentVersion`（2023_10_20）と一致させる。
`color.bin` だけは Texture2D で、寸法は `CalcTextureSize(splatCount)`（幅 2048 固定）と
`ColorFormatToGraphics(colorFormat)` から決まる。残る 3 つは `GraphicsBuffer.Target.Raw`
の 4 バイト単位。

`bindings.json` の読み書きに **`JsonUtility` は使えない** — 辞書を扱えず、入れ子型を
**例外も警告もなく `{}` にする**。ファイルは正常に書けたように見えて中身だけ空になる。
ゲーム同梱の Newtonsoft.Json 13（`Managed/Newtonsoft.Json.dll`）を使う。

### `.ply` を直接置いてもいい

`<game>/vdgs/<name>.ply` でも動く。`PlyLoader` がロード時にパースし、
`SplatData.FromBuffers` を通して上と同じバッファを作る。

オフラインのパイプラインは配布しにくい側の半分だった — Unity をもう一本、Python、
SSH の往復が要る。`.ply` を直接読めばそれが全部消える。代償はフレームタイム 7% と
少し大きいフットプリント（132 バイト/splat）。パーサが踏む 3 つの罠込みで
[ply-loading.ja.md](ply-loading.ja.md) にある。

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

### chunk.bin は両方向に危ない

シェーダーは chunk 相対値をデコードするかどうかを、**バッファが bind されているか
だけ**で決める。つまり：

- **残骸の** `chunk.bin` が絶対座標に適用されると、シーンは宇宙まで外挿され、
  スケールは 8 乗される。一日溶かした「地面に破片が飛び散る」がこれ
- **欠けている**と、全 splat が 0..1 の重みのまま原点付近の塊に潰れる

**`posFormat` では判定できない。** `Float32` は格納幅の意味であって座標空間の話では
なく、chunk 付きのシーンは平気で 0..1 を Float32 に入れる。最初のガードはここを
取り違えて chunk 付きのシーンを全部壊した。知っているのは変換だけなので、
`PlyExporter` が `chunkCount` を `meta.json` に書き、`SplatData.AcceptChunks` が
それと突き合わせる。`deploy.sh` もソースに無いファイルを送り先から消すようにした
（そもそも残骸が生き残った原因がこれ）。

**サイズ検証だけでは足りない。** drjohnson の残骸 `chunk.bin` は 794,432 バイトで、
`ceil(3177554/256) × 64` と完全に一致していた（同じ ply を前の品質で変換したものだから
当然）。弾けるのはフォーマット側の規則だけ。

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
       ├ CalcDistances         カメラ距離を計算し、同時に視錐台カリング
       ├ SortPoints            GPU radix sort
       ├ PrepareDrawArgs       可視数を indirect args バッファへ
       ├ CalcViewData          各 splat のスクリーン空間データを compute で計算
       ├ DrawProceduralIndirect  可視 splat の数だけ quad を instancing 描画
       └ Composite             RT をカメラターゲットに合成
  └ CameraEvent.BeforeForwardAlpha に挿す
```

`BeforeForwardAlpha` に入れることで、**不透明ジオメトリの後・透明の前**に描かれる。
ゲートや機体との前後関係が正しく出るのはこのため。

### カリングはソートに相乗りする

視錐台カリングは専用の compaction パスではなく距離計算パスの中にある。そこが
既に描画順を決めているから。カリングされた splat には最大のソートキーを与えて可視の
後ろに追いやり、可視数は **wave ごとに 1 回の atomic** で数えて
`DrawProceduralIndirect` に渡す。画質を落とさずに 10.7%。導出と、そこで踏んだ
2 つの誤りは [performance.ja.md](performance.ja.md)。

### D3D12 が必須な理由

ソートに使う `DeviceRadixSort.hlsl` が Shader Model 6 の wave intrinsics
（`WavePrefixSum`, `WaveReadLaneAt` など 41 箇所）を使う。DX11 にはこの命令が無い。

`-force-vulkan` では**ゲーム自身**が描画できない（VelociDrone は Vulkan 向けに
ビルドされていない）。よって **D3D12 の一択**。

### 背景ボックス

`SplatBackdrop` が GS の周りに黒い内向きの箱を置く。隙間からゲームの空が透けるのを
防ぐため。2 つ覚えておくこと：

- **全面が内側を向く**。`AssertFacesInward` が巻き順を信用せず、各法線を中心方向と
  突き合わせて測る。最初の版は 12 枚全部が裏返っていた
- **床はワールド y = 0.01**（箱自身の底ではない）。ゲームの地面が 0 にあって隠れる
  ため。`parent.InverseTransformPoint` 経由で固定してあるので、どんな placement でも
  そこに留まる

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
| `Current Track/Table Entry` 配下の `Track Name` ラベル | シーン中で「現在のトラック」を名乗る唯一の UI 要素 |
| `RaceInfo2/View - Gameplay/TrackName`（TMP） | 飛行 HUD。飛行中は正しいが、**エディタに戻っても最後に飛んだ名前を保持する**ので最後 |

**使ってはいけないもの、3 つ：**

- **`EditorManager.nnpnlmbjocf`** — 「最後に**エディタで**開いたトラック」。最初に
  見つかる上に一見完璧で、別のトラックを飛んでも更新されない。難読化された環境では、
  **1 つの値が一度正しく見えただけでは根拠にならない**
- **`Tracks Admin Entry(Clone)/TrackEntry/Track` ラベル** — ユーザーの全トラックが
  1 行ずつ並ぶ。現在のトラックではない
- **`Track Name` ラベルを名前だけで拾うこと** — 同じ modal に、テキストが文字列
  `"Track Name"` の**列見出し**が併存する。必ずパスで絞る

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
（Parsec でゲーム画面を見ながら、LAN 上のブラウザで操作する）。

```
HttpListener (:8777)
  ├ GET  /            埋め込み HTML（WebUi.cs）
  ├ GET  /api/status  現在のトラック / 表示中 / 利用可能 / 全紐付け
  ├ POST /api/load    指定の GS だけ表示
  ├ POST /api/unload  全部隠す
  ├ POST /api/bind    現在のトラックに紐付け
  ├ POST /api/unbind  紐付けを解除
  ├ POST /api/backdrop     キャプチャ周りの黒い箱
  ├ POST /api/collision    MeshCollider の on/off
  ├ POST /api/collisionview  hide / solid / wire
  └ POST /api/transform    スケールと Y。placement.json に書く
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

## 配置とコリジョン

回転はファイルが来る前に SuperSplat で合わせる。**スケールと高さは Web UI** にあって
`placement.json` に書く。ゲーム内キーでの位置合わせ（移動・回転・拡縮）は削除した。
数値を出す場所がなく、キャプチャを差し替えるたびにやり直しになるのが筋違いだった。

メッシュの無いキャプチャはすり抜けになる。`SplatCollision` が `collision.bin`
（`.ply` なら隣の `<name>.collision.bin`）を `MeshCollider` に載せる。
`SplatCollisionView` が殻を描く（solid / wire）。焼き方は OpenVDB。
[SCENES.ja.md](SCENES.ja.md)。

---

## ファイル対応表

| ファイル | 責務 |
|---|---|
| `Plugin.cs` | エントリポイント、各機能の配線、トラック監視 |
| `SplatData.cs` | `meta.json` + 5 バイナリ → メモリ、chunk のガード |
| `PlyLoader.cs` | `.ply` → 同じバッファ、ロード時に変換 |
| `SplatRenderer.cs` | GPU バッファ確保、CommandBuffer 構築、描画、カリング |
| `GpuSorting.cs` | 8bit radix sort（upstream ほぼ無改変） |
| `ShaderBundle.cs` | AssetBundle からシェーダーを取得 |
| `SplatScene.cs` | splat 1 つ分の生成・破棄・配置読み込み |
| `SplatCollision.cs` | `collision.bin` → MeshCollider（`.ply` は Y 鏡映） |
| `SplatCollisionView.cs` | コリジョン殻の描画（solid / wire） |
| `SplatBackdrop.cs` | 内向きの黒い箱 |
| `TrackName.cs` | ロード中のトラック名を多段フォールバックで取得 |
| `TrackBindings.cs` | `bindings.json` の読み書き |
| `TrackProbe.cs` | 難読化されたゲームから文字列の在処を探す（F12） |
| `WebControl.cs` | HTTP サーバー、スレッド境界の管理 |
| `WebUi.cs` | ブラウザ UI（埋め込み HTML） |
| `Probe.cs` | ランタイム環境の実測ダンプ（F9） |
| `PerfLog.cs` | フレームタイム記録 |
| `PostProcessFix.cs` | D3D12 強制の副作用対応（**効かない**。経緯の記録として残置） |

---

## 拡張するときの注意

- **API を足す**: `WebControl.Handle()` に case を追加し、`Plugin` 側にハンドラを書いて
  デリゲートに繋ぐ。Unity に触る処理は必ず `QueueOnMain` を通す
- **UI を足す**: `WebUi.Html` に追記。**動的な値は必ず `textContent` で入れる**
- **ゲームの内部状態を読みたい**: `TrackProbe` の needle を変えて F12。
  デコンパイル結果の定数は信用しない
- **シェーダーを変える**: `unity/VDGSBundler` で焼き直し。**Windows で**。
  焼けたバンドルが 1MB 未満なら失敗している
