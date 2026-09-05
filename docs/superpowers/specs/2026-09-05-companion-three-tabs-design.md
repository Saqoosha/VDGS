# companion と操作 UI を 3 タブに統合する

2026-09-05

## 目的

companion（Tauri）とゲーム内操作 UI（プラグインが `:8777` で配る）を、**1 つの
3 タブ構成**に組み直す。同時に、いま存在しない「自分のキャプチャで自分のコースを作る」
経路を companion の中に通す。

## いまの問題

### 同じ名詞が 3 箇所に出る

| 面 | 画面 | 中身 |
|---|---|---|
| companion | 01 setup | ゲームパス・mod 導入/削除・トラック一覧・Add track・Install capture・True Lens 警告・FLY |
| companion | 02 get | カタログ一覧・Refresh catalog・FLY |
| `:8777` | Control | 現在のトラック・Bind/Unbind/Unload・on screen・bindings 一覧 |
| `:8777` | Library | キャプチャ一覧・検索・Show |

キャプチャは 3 箇所（setup の未紐付け・get のカタログ・Library）。トラックも 3 箇所
（setup の一覧・Control の現在・Control の bindings）。**紐付けを直せるのは Control だけ**
なのに、紐付いていないことを教えるのは setup。

### 操作 UI の存在を誰も知らない

`:8777` を人に伝えている場所は 3 つあり、3 つとも届かない。

- `web/src/App.tsx:18` の `local · lan :8777` — **その UI の中**。たどり着いた人にしか見えない
- `companion-tauri/src-tauri/src/lib.rs:659` のログ 1 行 — Setup 下部の履歴に流れて消える
- `docs/USAGE.ja.md` — 配っていない

### 自分の .ply を入れる口が無い

`Install capture` は `.zip` しか受けない。自分の `.ply` を入れるには zip に固めるか、
`<game>/vdgs/` に手で置く。**紐付けは companion からできない** — カタログエントリの
`install_as` 経由だけで、任意のキャプチャとトラックを結ぶ口が無い。

### カスタムトラックの手順が 7 段でアプリを 3 つ跨ぐ

`docs/TRACKS.ja.md` の手順のうち、**5 段が「知らないと辿り着けない場所」にある**。

| 段 | どこ | 問題 |
|---|---|---|
| 名前を決める | 頭の中 | 順番を間違えると絵が消える。ドキュメントにしか書いていない |
| シーナリーを選ぶ | `--export-track --list` | GUI アプリの隠しコマンドライン |
| 紐付ける | ブラウザ `:8777` | 存在を知られていない |
| 位置を合わせる | ブラウザ `:8777` | 同上 |
| 組む | ゲームのエディタ | 正しい。ゲームの機能 |
| コリジョンを焼く | リポジトリの python | companion に無い |
| 書き出す | `--export-track` | また隠しコマンドライン |

### 他人の .ply は向きが揃っていない

いまカタログに載っているキャプチャは、リポジトリの道具（`--mirror y` など）を通してから
変換してある。**利用者はその工程を通らない**ので、天地が逆・手系が逆のまま入る。
mod 側に直す手段が無い。

## 設計

### タブ 3 枚

**01 setup** — ゲームパス・mod 導入/削除・True Lens 警告・FLY

いまの `Setup.tsx` §01 のまま。§02 のトラック一覧は 02 へ出す。

**02 tracks** — 配布中と導入済みを 1 本の表に

`state.tracks`（`bindings.json` の鍵から構築）と `state.catalog.entries` を同じ表に畳む。
行の状態で出るボタンが変わる。

| 行の状態 | ボタン |
|---|---|
| 配布中・未導入 | `Get` |
| 導入済み | `Remove` |
| 導入済み・キャプチャ欠落 | `Get`（再取得） |
| 公式サーバー由来 | `Unbind` のみ。トラックは削除しない |

`Add track`（`.track.json` の取り込み）もここ。**独立した bindings 一覧は消える** — この表が
すでに `capture` 列を持っているため。行数が増えるので検索を持つ。

**03 create your own** — 自分の `.ply` から飛べる状態まで

```
① 入れる     .ply を選ぶ  → <game>/vdgs/<name>.ply
             ← ゲームが止まっているときだけ

② トラックを作る
             名前:  [ VDGS my-house        ]   ← .ply 名から既定を入れる
             種テンプレートを複製して user11.db に insert
             同じ瞬間に bindings.json も書く
             ← ゲームが止まっているときだけ

③ 合わせる   Up / Mirror / Turn / Scale / X / Z / Height
             Show / backdrop / collision / collision view
             ← ゲームが動いているときだけ

             別の画面から合わせる:  http://<LAN IP>:8777/   [QR]
```

紐付けの無いキャプチャ（いまの `state.unbound`）は ② に出す。

**今回入れないもの**：変換済みフォルダの取り込み、既存トラックへの紐付け。どちらも
`.ply` 1 本から新しいトラックを作る筋が通ってから足す。

### タブ 03 はゲームの状態で半分ずつ入れ替わる

**① と ② はゲームが止まっていないとできない。** ファイルを置き換え、`user11.db` に書くため。
`add_track` は既に `launch::is_running()` で弾いている（`lib.rs:603`）ので、同じ規則が
そのまま乗る。

**③ はゲームが動いていないとできない。** 相手はプラグインの HTTP サーバーで、ゲームが
止まっていれば存在しない。

| ゲーム | ① 入れる | ② 作る | ③ 合わせる |
|---|---|---|---|
| 止まっている | 有効 | 有効 | 無効。`FLY して合わせる` と出す |
| 動いている | 無効。`VelociDrone を閉じて` | 無効。同上 | 有効 |

**タブが順番そのものになる。** 準備する → 飛ぶ → 合わせる。どちらの状態でも「いま押せる
ものが 1 つある」画面になり、押せない側は理由を名乗る。

### トラック名

**入力欄を 1 つ持つ。** 既定は `.ply` のファイル名から `VDGS <name>`（`my-house.ply` なら
`VDGS my-house`）。

**保存名は `+` 区切りで入れる。** VelociDrone は保存名を form 符号化していて、空白が `+` に
なる（`AGENTS.md` の「トラック名は 2 通りに綴られる」）。companion が空白のまま insert すると、
自作トラックだけ他と綴りが違う行になる。insert 前に空白を `+` へ、`bindings.json` には
`tracks::display_name` を通した表示形を書く。**照合点を 1 つに保つ。**

### `:8777` が配るもの

**同じアプリ。** `web/index.html` は `companion.html` と同じ 3 タブのコンポーネント木を読む。
`bridge.ts` の `hosted = !!window.__TAURI__` が false のとき、タブ 01 と 02 は表示しない
（どちらも Tauri のダイアログとダウンロードを要する）。**タブ 03 の ③ だけが有効になる。**

結果としてブラウザで開いた画面はいまの `Control` とほぼ同じになるが、同じコンポーネントから
出るので**片方だけ直る事故が起きない**。

### LAN URL の置き場所

companion は `state.lan_url`（`http://<LAN IP>:8777/`）と QR を **タブ 03 の ③ の真横**に出す。
設定の奥ではない。全画面のゲームから alt-tab するのが最悪の操作なので、**合わせようとした
瞬間にだけ**別画面という選択肢を出す。

### `bindings.json` の書き込み経路を 1 本にする

**`TrackBindings.Load()` はコンストラクタでしか呼ばれない**（`src/VDGS/TrackBindings.cs:33`）。
プラグインは起動時に 1 回読んでメモリの写しで動くため、ゲーム稼働中に companion が
`bindings.json` を書いても気づかず、次にプラグインが保存したときに**上書きする**。
エラーは出ない。

**プラグイン側を直す。** `PollTrack` は既に 1 秒ごとに回っているので、そこで `bindings.json`
の mtime を見て、変わっていたら `Load()` し直す。自分の `Save()` 直後は自分が書いた mtime を
控えて無視する。

これで companion は**常にファイルを書くだけ**でよくなり、ゲームの稼働状態で経路を分けない。
手で `bindings.json` を編集した場合も 1 秒で効く。

**却下した代案**：稼働中は `/api/bind` を通す。あれは現在開いているトラックにしか効かないので、
タブ 02 の任意の行から `Unbind` ができなくなる。

### カスタムトラックの作成

**種テンプレートを複製する。** VelociDrone は**ゲート 2 個とスタート 1 個**を要求するため、
空のトラックは作れない。あらかじめ有効な最小トラックを 1 本作っておき、`value` をそのまま
複製して新しい名前で `user11.db` に insert する。

- 種は `scene 16`（BlankCanvas。平らで空。`docs/TRACKS.ja.md` の推奨）
- スタートゲートは**エディタの初期カメラが見ている場所**に置く（この機械では
  `(-100, 3, 85)`）。カメラの位置ではない。**エディタを開いた瞬間に視界の中にある**ため、
  探さずに掴める。`y = 3` は地面へのめり込み防止
- `y` を 3 にした理由と整合する事実：背景の箱の床は world `y = 0.01` に持ち上げてある
  （`src/VDGS/SplatScene.cs:210`。ゲームの地面との z-fighting 回避）

作成の流れ：

```
名前を打つ  →  種の value を複製
            →  user11.db に insert（tracks::import が既にある）
            →  同じ瞬間に bindings.json も書く
```

**これで順番の罠が消える。** 名前を決めた瞬間に紐付けまで終わるので、「組んでから名前を
変えて絵が消える」が起きない。

種の実体は `companion-tauri/src-tauri/resources/seed.track.json` として同梱し、
`tauri.conf.json` の `resources` に足す。

**シーナリーは選ばせない。** 種のゲート位置がそのシーナリー前提なので、別のシーナリーを
選ぶとゲートが地形の中に出る。

### 姿勢と位置のつまみ

`placement.json` は `position[3]` と `rotation[3]` を既に持ち、spawn で適用している
（`src/VDGS/SplatScene.cs:134-135`）。書き込み側の `SetTransform` が `scale` と
`position[1]` しか書かないだけ（`src/VDGS/SplatScene.cs:380`）。

| つまみ | 形 | 直すもの | 無いとどうなる |
|---|---|---|---|
| **Up** | ±X ±Y ±Z の 6 ボタン | 天地 | 天井を飛ぶ |
| **Mirror** | on / off | 手系 | 文字が裏返る。**回転では直らない** |
| **Turn** | 0〜360° の連続スライダー | 焼き込んだ光の向き | ドローンだけ違う方向から照らされる |
| **Scale** | 対数スライダー | 大きさ | 既存 |
| **X / Z** | スライダー | 水平位置 | キャプチャが視界に無い |
| **Height** | 対数スライダー ±200m | 高さ | 既存 |

**Turn が連続である理由**：3DGS は光を SH に焼き込んでおり、ゲーム側の太陽と向きが食い違うと
ドローンの機体だけ別方向から照らされる。**太陽は 90° の倍数にいない**ので刻みでは合わない。
キャプチャ自身は無照明で描かれるため影響を受けず、ずれるのは常時画面にいる機体のみ。

**却下した代案**：ゲームの太陽を回す。ゲームは Bakery を積んでおり**焼いた影は動かない**ので、
リアルタイム光だけ回すと影と光が食い違う。キャプチャを回すほうが自己完結する（背景の箱も
コリジョンも子オブジェクトなので親の回転についてくる）。

**水平位置を開ける理由**：`SplatScene.cs:199-202` は回転と水平位置を意図的に mod から
外しているが、その理由は「キャプチャは正しい向きで届くべき」であり**向きの話**。自分で
スキャンした人は原点位置を選べない（COLMAP が決める）ので、位置は動かせるべき。
**回転を開けるのは向きの話を覆すが**、これも同じ理由で覆る — 利用者は前処理を通らない。

### 鏡映は読み込み時、回転は変換

```
.ply / bins を読む → mirrorY を適用（データ）
                   → placement.rotation を適用（transform）
                   → placement.position / scale を適用（transform）
```

鏡映を後に回すと回転と交換できなくなるので、**データ側に固定する**。

**`mirrorY` は 1 個の値を 2 箇所が読む。** いま `SplatCollision` は拡張子で鏡映を決めている
（`src/VDGS/SplatCollision.cs:100`、`mirror = dir.EndsWith(".ply")`）ため、splat とコリジョンは
構造上ずれない。旗を UI に出すなら**同じ値を `PlyLoader` と `SplatCollision` の両方が読む**
必要がある。壁だけ鏡像になった世界はエラーを 1 行も出さない。

既定は現在の挙動を保つ — `.ply` は `true`、変換済みは `false`。

## 実装

### Rust（companion）

| 追加 | 中身 |
|---|---|
| `SetupState.lan_url: Option<String>` | `http://<LAN IP>:8777/` |
| `installPly` | `.ply` を選び `<game>/vdgs/` へコピー |
| `createTrack` | 種の `value` を複製し `tracks::import` で insert、続けて `game::bind` |
| `unbindTrack` | `game::unbind` は既にある。口が無いだけ。タブ 02 の行から呼ぶ |
| `removeCapture` | キャプチャの削除 |

**`dispatch` の引数を広げる。** いまは `cmd` + `id: Option<String>` で、紐付けには 2 つ要る。
`arg: Option<serde_json::Value>` にする。

### 削除

- `Setup.tsx` の `Install capture` ボタン
- `dispatch` の `"installCapture"`（`lib.rs:759`）
- `Host::install_zip`（`lib.rs:307`）

**`game::install_archive` は残す。** カタログのダウンロードが同じ関数を通る（`lib.rs:447`）。
消えるのは人が zip を選ぶ経路だけで、zip は「カタログが裏で使う転送形式」に戻る。

### プラグイン（C#）

- `TrackBindings` に mtime 監視を足し、`PollTrack` から呼ぶ
- `placement.json` に `mirrorY` を足し、`PlyLoader` と `SplatCollision` の両方が読む
- `SetTransform` を `position[0]` / `position[2]` / `rotation` まで広げる

### web

- `api.ts` に `bridge.ts` と同じトランスポート切り替えを入れる。`hosted` なら
  `tauri-plugin-http` で `http://127.0.0.1:8777` を叩く
- `capabilities/default.json` にスコープ `http://127.0.0.1:8777/*` を足す
- **プラグインに CORS を足さない。** Rust 経由なら webview を通らないので、
  `Access-Control-Allow-Origin` を付けない方針と `Content-Type: application/json` 必須は
  そのまま残る
- `App.tsx` のルーター殻をタブに置き換え、`Get.tsx` をタブ 02 に、`Control.tsx` と
  `Library.tsx` の中身をタブ 03 の ③ に移す
- ダークモードは実装しない

## 未検証のまま残すもの

- **`placement.rotation` は一度も 0 以外になったことが無い。** 配線はあるが通電した記録が
  無い。UI を作る前に、90° 回したキャプチャが正しく描けるか（共分散が壊れないか）と
  コリジョンが追随するかを実機で 1 回確認する
- **`scene_id` が機械をまたいで同じかは未検証**（`AGENTS.md` に記載）。種に 16 を焼き込んで
  いるので、別の機械で 16 が違うシーナリーなら平地のつもりが地形の中になる。配布直前に確認する
- **種テンプレート `seed.track.json` は未書き出し。** この機械の `VDGS Template` から
  `--export-track` で出す

## 引き継がないもの

`bindings.sample.json` が使われないまま `<game>/` 直下に落ちる問題は、人が zip を開ける口が
無くなるため消える。

## 検証

| 対象 | やりかた |
|---|---|
| `bindings.json` の再読み込み | ゲーム稼働中に companion から `Unbind` → 1 秒以内に絵が消えること。逆に `/api/bind` の直後に companion が書いた値が消えないこと |
| `mirrorY` の共有 | 旗を反転して、splat とコリジョン殻（`show solid`）が**同じ向き**で動くこと |
| 姿勢のつまみ | Up 6 通り × Mirror 2 通りを 1 つのキャプチャで通し、文字が読める組み合わせが 1 つだけ存在すること |
| 種の複製 | 複製したトラックがゲームの一覧に出て、開けて、飛べること |
| ブラウザ側 | `:8777` をブラウザで開き、タブ 01/02 が出ず、③ が動くこと |
| ゲーム状態 | 稼働中にタブ 03 を開き、① と ② が理由付きで無効、③ が有効になること。停止中はその逆 |
| Rust | `tracks::import` / `game::bind` の既存テストを維持。トラック名の `+` 符号化と `display_name` の往復に単体テストを足す |
