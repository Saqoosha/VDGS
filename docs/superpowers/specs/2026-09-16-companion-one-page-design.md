# companion 1 画面化 設計

2026-09-16。3 タブ版（`2026-09-05-companion-three-tabs-design.md`）を実機で通した結果、
タブ 02 と 03 が「トラック」という 1 つの概念を 2 画面に割っていた。プレイヤーが数える
単位はトラックだけなので、そこに全部寄せる。タブは無くす。

## 実機で迷った場所（動機）

- `.ply` を持った人がまず押すのはタブ 02 の `Add track` で、それは `.track.json` しか
  受けない。ダイアログで `.ply` が灰色になり、理由はどこにも出ない
- タブ 03 で Create track が成功すると、直後の画面が「nothing installed yet」になる。
  紐付いてないキャプチャの一覧から選ばせる設計なので、成功した瞬間に必ず空になる
- ログはタブ 01 にしか無いが、失敗するボタンは 02 と 03 にある
- ダウンロードの進捗バー（`components/Progress.tsx`、#12）は `fab7a3e` で表から落ちて
  いて、`◐ 43%` の文字だけになっている。退行

## 画面

```
VDGS                          ● ready · installed himeji-lod2      ← 最新ログ 1 行
<game path>  [Change…] [Update mod] [Uninstall]                    ← 導入の帯
────────────────────────────────────────────────────────
tracks                                              find [      ]
01  VDGS Himeji          himeji-lod2 / 2,236,829 splats / ply     [Tweak] [Remove]
02  Nelson               3 tracks / CC BY / @tosolini              [Get]
  ◐ fetching nelson-lod2 ████████░░ 43%                           ← 取得中の行の下
                                                        [Add track]
────────────────────────────────────────────────────────
[                          FLY                          ]
```

### 導入の帯

`Setup.tsx` の中身をそのまま帯にする。`ready` なら 1 行（パスとボタン 3 つ）。未導入・
ゲーム未検出・True Lens on のときは判定と警告がその下に開く。埋め草は足さない — 導入時に
1 回来て、あとは壊れたときにしか読まない領域なので、薄いままが正しい。

### ログ

`Masthead` の status に最新の 1 行を出す。押すと全文（末尾 200 行）が帯の下に開く。
別画面・別窓は作らない。

### Add track — ファイル → 名前 → 作成を 1 操作に

1. `Add track` → host が `.ply` だけのダイアログを開く（`pickPly`）
2. 選ばれたら host は**コピーせず** `{type:'picked', path, stem}` を push する
3. 表の先頭に名前入力の行が生える。既定は `VDGS <stem>`。`Create` / `Cancel`
4. `Create` → `addTrack {path, name}`。host は `run_busy` の中で `install_ply` →
   `create_track_job` を続けて実行する。`Cancel` は UI が行を消すだけ

これで「入っているがトラックに乗っていない」キャプチャが通常の操作からは生まれない。
`unbound` は host の状態としては残す（手で `vdgs/` に置いたファイルは今後もそうなる）が、
表の下の 1 行「installed, on no track」に Remove を付けて始末できるようにする。

`.track.json` の取り込みは落とす。配布はカタログ経由で足りる。

### Tweak — 同じ窓の別画面

トラック行の `Tweak` を押すと、表の代わりに `Control`（Scale・Height・X・Z・Up・Turn・
Mirror・collision・backdrop、LAN の QR）が出る。`← tracks` で戻る。別窓は Tauri の
multi-window と bridge の配線が増えるだけで得るものが無く、第 2 画面が要る人には QR がある。

`Tweak` が生きる条件は「ゲームが動いていて、プラグインがそのトラックを読んでいる」
（`useStatus().live && plugin.track === row.track`）。調整はプラグインの HTTP API 経由で、
読み込み中のキャプチャにしか効かないため。それ以外は灰色で `fly this track first`。

### 進捗バー

`state.busy` の間、表の先頭に `<Progress what percent>` を戻す。押した人が見ている場所。

### ブラウザから開いたとき（プラグインが配る `vdgs/ui/`）

`hosted` でなければ `Control` だけを出す。今のタブ 03 の③と同じ。

## 消えるもの

- タブ、`pages/Own.tsx`、`pages/Setup.tsx`（帯に吸収）
- `installPly` / `createTrack` コマンド（`addTrack` に統合）。`removeCapture` は残す
- タブ 02 の `.track.json` 取り込み（`add_track` の JSON ダイアログ）

## 変えないもの

- Rust の `install_ply` / `create_track_job` / `remove_track` / `get` / `unbind` の中身
- `Control.tsx`、`bridge.ts` のゲート、`Tracks.tsx` の行の中身と `Get` の id 解決
- プラグイン側（C#）。何も変わらない

## 受け入れ

- まっさらな機械で、ゲームフォルダ選択 → Install mod → Add track（`.ply`）→ 名前 →
  Fly → Tweak で Scale を動かす、がタブ切り替え無しで通る
- Create の途中で Cancel したら `vdgs/` に何も増えない
- ダウンロード中にバーが表の中に出る
- Add track が失敗したとき、その行がどの画面でも見える
