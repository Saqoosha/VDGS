# Control Web UI 刷新

ゲーム内コントロール（`http://<host>:8777/`）を、埋め込み HTML から React SPA に載せ替える。

操作そのものは今と同じ（トラックへの紐付け、表示、backdrop、コリジョン、スケールと高さ）。
これから足すのはローカルのシーンライブラリで、将来の共有アプリから取って来れる形にしておく。
共有アプリ自体は別プロダクトで、この spec の範囲外。

今の `WebUi.cs` は C# 文字列に HTML/CSS/JS を埋め込んでいる。ビルドもコンポーネントも無いので、
一覧・検索・メタ表示を足すとファイルが溶ける。ゲーム機に CDN は期待しない。

## 決定事項

| 項目 | 決定 | 根拠 |
|---|---|---|
| 対象 | `:8777` のコントロール UI だけ | 共有アプリは別。ただしカタログを後から差せる形 |
| スタック | bun + Vite + React + TypeScript + Tailwind + shadcn/ui | 共有アプリとコンポーネントを分けやすい |
| 見た目 | 測量野帳。紙色、セリフ、朱のトンボ。カードは使わない | 現行の zinc カードが退屈だった。ダーク／HUD にはしない |
| 言語 | 英語 | 現行 UI と同じ |
| 置き場所 | `<game>/vdgs/ui/` | DLL 再ビルドなしで差し替えられる |
| 配信 | プラグインの `HttpListener` が静的ファイルを出す | 同一オリジンのまま。CORS を開けない |
| ルーティング | ブラウザ履歴（`/` と `/library`） | プラグインが未知パスを `index.html` に倒す |
| ポーリング | `GET /api/status` を 1.5 秒 | 現行と同じ。ライブラリ用にエンドポイントを増やさない |
| 共有カタログ | v1 では呼ばない。型だけ `source` を持つ | ブラウザから共有オリジンに直接行かない |
| 再 Discover | しない | 起動後ドロップは今も再起動待ち。この刷新では触らない |

## 構成

```
web/                     Vite アプリ（ゲームに入らない）
  src/
    api.ts               fetch ラッパ。POST は必ず application/json、空でも {}
    types.ts             Status / Scene
    App.tsx              Control | Library
  dist/                  gitignore。deploy がコピーする

<game>/vdgs/ui/          dist の中身
src/VDGS/WebControl.cs   /api/* と ui/ 配下の静的ファイル
src/VDGS/WebUi.cs        ui/ が無いときだけの短い HTML。アプリ本体は置かない
```

`bun run build` が `web/dist/` を出す。`tools/deploy.sh` がそれを `<game>/vdgs/ui/` に置く。

| `deploy.sh` | 動かすもの |
|---|---|
| 引数なし | プラグイン + UI + splat |
| `--plugin` | プラグイン + UI（splat 2.2GB を送らない） |
| `--ui` | UI だけ。`dotnet build` しない |

`--plugin` が UI も送るのは、UI が小さいから。`--ui` は DLL を触らずにフロントだけ差し替えるため。

シーン発見は `meta.json` のあるディレクトリだけを見るので、`vdgs/ui/` は今でもシーンにならない。
ディレクトリ名 `ui` は予約し、中に `meta.json` があっても無視する。

## 画面

1 枚の紙。四隅にトンボ、左に朱のマージン、番号付きセクション。カードは使わない。
フォントはバンドル（Fraunces + IBM Plex Sans/Mono）。CDN は使わない。
ヘッダは「VDGS」と **01 control / 02 library**。接続状態は `● link` / `○ off`。

### Control（`/`）

飛行中の操作盤。セクションは 3 つ。

1. **01 current track** — トラック名、紐付いている splat、`Bind shown` / `Unbind` / `Hide all`
2. **02 on screen** — 表示中のときだけ。名前、splat 数、backdrop / solid / mesh view、スケールと高さ
3. **03 bindings** — トラック名 → splat の表と remove。トラックの話なので Library には置かない

スライダーは現行と同じ写像。スケールは対数、高さは符号付き対数。ドラッグ中はポーリングで上書きしない。

```
toSlider(v)     = log10(max(0.01, v))          // スライダー範囲 -2..2
fromSlider(t)   = 10^t
kYReach         = 200
toYSlider(v)    = sign(v) * log1p(|v|) / log1p(kYReach)
fromYSlider(t)  = sign(t) * expm1(|t| * log1p(kYReach))
```

数値入力も残す。高さの数値は ±1000、スライダーの端は ±200 m。

### Library（`/library`）

ローカル一覧。検索（名前の部分一致、大小無視）と、罫線の目録。

各行: 通し番号、名前、splat 数、`kind`、フォーマット 4 つ、おおよそのサイズ、コリジョン有無、表示中なら `shown`。
Show でそのシーンだけ表示する。スライダーは置かない。

Catalog タブは出さない。

空のとき: `nothing in <game>/vdgs/`。ゲームに届かないとき: ドットが灰色。最後に成功した描画は残す。

## API とデータ

既存 POST のパスもボディも変えない。

| メソッド | パス | ボディ |
|---|---|---|
| POST | `/api/load` | `{"splat":"name"}` |
| POST | `/api/unload` | `{}` |
| POST | `/api/bind` | `{"splats":["name"]}` |
| POST | `/api/unbind` | `{}` または `{"track":"name"}` |
| POST | `/api/backdrop` | `{"splat":"name","on":true}` |
| POST | `/api/collision` | `{"splat":"name","on":true}` |
| POST | `/api/collisionview` | `{"splat":"name","mode":"off"}`（`solid` / `wire` も可） |
| POST | `/api/transform` | `{"splat":"name","scale":1.0,"y":0}`（欠けたフィールドは触らない） |

POST は `Content-Type: application/json` が無いと 415。フロントは空でも `{}` を付ける。

`GET /api/status` が Control と Library の唯一のソース。React Query などは入れない。`useEffect` と `setInterval(1500)` で足る。`available` の各要素に、Discover 時点で一度だけ読むメタを足す。`SplatData.Load` は呼ばない（バッファまで読む）。軽量な meta 読みは `JsonUtility` ではなく Newtonsoft で、バッファを開かない専用のパースにする（ゲームに同梱の Newtonsoft 13）。`SplatData.Load` の JsonUtility 経路は触らない。

```ts
type Scene = {
  name: string
  source: 'local'                 // v1 は常に local。catalog は型の予約
  kind: 'converted' | 'ply'
  splats: number
  posFormat?: string              // meta.json の文字列そのもの。品質ラベルは作らない
  scaleFormat?: string
  colorFormat?: string
  shFormat?: string
  bytes?: number                  // 下の規則
  hasCollision: boolean
  shown: boolean
  scale: number
  y: number
  backdrop: boolean
  collision: boolean
  collisionView: 'off' | 'solid' | 'wire'
}

type Status = {
  track: string | null
  loaded: string[]
  available: Scene[]
  bindings: Record<string, string[]>
}
```

`bytes` の規則:

- converted: そのディレクトリ直下のファイルの合計。`placement.json` は除く（操作で変わるローカル状態）
- ply: `.ply` 本体のサイズだけ。隣の `collision.bin` は含めない

`source` は v1 でも JSON に出す。コメントだけの予約にしない。

`GET /api/catalog` は作らない。あとで足すならプラグインが共有アプリをプロキシする。ブラウザは `:8777` 以外に行かない。理由はオリジンが LAN IP で、共有側 CORS も今の CSRF 対策（応答に CORS ヘッダを付けないこと）も壊れるから。

## 静的ファイル

`ui/` がドキュメントルート。

- `/` と `/index.html` → `ui/index.html`
- `/assets/...` → `ui/assets/...`（Vite のハッシュ付き）
- `/api/...` → 既存ハンドラ
- それ以外でファイルが無い → `ui/index.html`（SPA）。ファイルがある拡張子（`.js` `.css` `.map` `.svg` `.ico` `.png` `.woff2`）は 404 のまま。HTML に倒さない
- 解決後のパスが `ui/` の外なら 404。`..` と絶対パスを拒否

`index.html` は `Cache-Control: no-store`。ハッシュ付きアセットは `Cache-Control: public, max-age=31536000, immutable`。

`ui/` が無い（ディレクトリ無し、または `index.html` 無し）ときは `WebUi.cs` の短い HTML を返す。文面は「UI is not installed. Run tools/deploy.sh --ui.」API は通常どおり動く。

MIME は拡張子から付ける。`.js` → `text/javascript`、`.css` → `text/css`、`.html` → `text/html; charset=utf-8`、その他は `application/octet-stream`。

## セキュリティ

現行の 3 点を維持する。

1. 動的な値はテキストノード。`dangerouslySetInnerHTML` と DOM `innerHTML` は禁止。`href` にトラック名やシーン名を入れない
2. 応答に `Access-Control-Allow-Origin` を付けない
3. POST は `Content-Type: application/json` 必須

トラック名はコミュニティトラックから来るので攻撃者が書ける。シーン名はディレクトリ名。どちらも信頼しない。

## エラー処理

| 状況 | 動き |
|---|---|
| `GET /api/status` 失敗 | ドット灰色。最後に成功した画面を残す。ボタンは死なせない |
| POST 4xx/5xx | その操作だけ巻き戻す（チェックを戻す）。短いメッセージを `textContent` で出す |
| 操作中の連打 | 現行の `busy`。二発目は捨てる |
| transform ドラッグ中の失敗 | スライダーはユーザー値のまま。ポーリングはドラッグ中上書きしない |
| メインスレッド 5 秒タイムアウト | 現行どおり `{"error":"main thread did not respond"}`。ドット灰色 |
| 静的ファイルのパストラバーサル | 404 |
| ハンドラ例外 | 500 + `{"error":...}`。フロントは `error` をテキストで出す |

## テスト

フロントは `web/` で Vitest + Testing Library。

- POST に必ず `Content-Type: application/json`。空ボディは `{}`
- スライダー変換: スケール `1 → 0 → 1`、高さ `0` / `5.11` / `-206` が上の式と一致
- Library 検索: 名前の部分一致。大小無視
- XSS: `status.track` が `<img src=x onerror=alert(1)>` でもテキストノードになり、属性に入らない。シーン名も同じ

プラグイン本体は Unity 参照があるのでテストプロジェクトから参照しない。Unity を使わないファイルだけ切り出して `src/VDGS.Tests`（xunit, net8.0）でコンパイルする。

- 静的ファイルのパス解決: 根が `ui/` の外に出ない。`..`、絶対パス、区切りの混ぜは 404
- 予約名: `ui` はシーンにならない（大小無視）
- meta の軽量パース: フィクスチャ `meta.json` から splat 数とフォーマット 4 つが出る。`.ply` 相当の入力は `kind: 'ply'` でフォーマット欄が空

実機: `deploy.sh --ui` のあと、Control の操作一式と Library の検索がゲームを落とさずに動く。`ui/` を外すとフォールバック HTML が出て API は生きている。見た目は差分画像では測らない。

## 範囲外

- 共有ウェブアプリ、`GET /api/catalog`、シーンのダウンロード／インストール
- 起動後の splat 再 Discover
- 完全な High パッキング、Medium 以下の明るさ
- ゲーム内キー操作の復活
- CORS を開けてブラウザから共有オリジンを叩くこと
- `web/dist/` のコミット

## 既存ファイルへの影響

| ファイル | 変化 |
|---|---|
| `src/VDGS/WebUi.cs` | 埋め込みアプリを削除。フォールバック HTML だけ |
| `src/VDGS/WebControl.cs` | 静的ファイル配信。`/api` は維持 |
| `src/VDGS/Plugin.cs` `BuildStatus` | `available` にメタを足す |
| `src/VDGS/SplatScene.cs` | 予約名 `ui`。Discover 時に meta のフォーマットと bytes を読む |
| `src/VDGS.Tests/` | 新規。パス解決・予約名・軽量 meta パース |
| `tools/deploy.sh` | UI のビルドとコピー。`--ui` |
| `docs/ARCHITECTURE.md` とその `.ja.md` | 埋め込み HTML を静的ファイル配信に更新 |
| `docs/USAGE.md` とその `.ja.md` | Library の操作を追記 |
| `AGENTS.md` | UI の置き場所と「`innerHTML` 禁止」を React 側の言い方に更新 |

`web/` は新規。gitignore に `web/dist/` と `web/node_modules/` を足す。
