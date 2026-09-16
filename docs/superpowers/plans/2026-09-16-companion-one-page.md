# companion 1 画面化 実装計画

spec: `docs/superpowers/specs/2026-09-16-companion-one-page-design.md`。ブランチ
`worktree-companion-three-tabs` の上に積む。テストは `cd web && bun run test`、
`cd companion-tauri/src-tauri && cargo test`。

## Task 1: Rust — `pickPly` と原子的な `addTrack`

- `install_ply` を `pick_ply` に変える：ダイアログで `.ply` を選ばせ、**コピーせず**
  `{"type":"picked","path":…,"stem":…}` を post する。ゲーム実行中は先に断る
- `add_track(path, name)` を新設：`run_busy("adding <name>")` の中で
  `game::install_ply` → `create_track_job` を続けて呼ぶ。JSON 版の `add_track` と
  `add_track_inner`、`create_track` は削除
- `dispatch`：`"pickPly"`、`"addTrack"` は `field("path")` と `field("name")` の両方が
  要る。`"installPly"` / `"createTrack"` は消す
- テスト：`add_track_job`（install → create を 1 本にした free function）が、コピー先と
  DB 行と binding を全部作ること、名前が空なら何も書かないこと

## Task 2: web — bridge に `picked`

- `HostMessage` に `{ type: 'picked'; path: string; stem: string }` を足す
- dev の `devPush` 経路（`vite-mock-api.ts`）で `pickPly` に応答して `picked` を返す
- `bridge.test.ts` に 1 本

## Task 3: web — 1 画面の殻

- `CompanionApp.tsx`：タブと `TabId` を消す。`view: 'tracks' | { tweak: string }`
- Masthead の status：busy なら `◐ 43%`、そうでなければ `● ready · <最新ログ>`。
  押すと `showLog` が反転し、帯の下に `<ol>` が開く
- 導入の帯 `SetupStrip`（`Setup.tsx` の中身を移す。ready なら 1 行、そうでなければ
  判定と True Lens を開く）。`Setup.tsx` は削除
- 未 hosted なら `Control` だけ
- テスト：`CompanionApp.test.tsx` のタブ前提を全部書き直す

## Task 4: web — Tracks に Add track の流れと Tweak と Progress

- `Add track` → `send('pickPly')`。`picked` を受けたら表の先頭に `NameRow`（既定
  `VDGS <stem>`、Create → `send('addTrack', undefined, { path, name })`、Cancel）
- `Tweak` ボタン：`useStatus().live && plugin.track === row.track` で有効。押すと
  `onTweak(row.track)`
- `state.busy` の間、表の先頭に `<Progress what={busy} percent={busyPercent} />`
- 「installed, on no track」の行に各キャプチャの Remove（`removeCapture`）
- `Own.tsx` と `Own.test.tsx` を削除。`Tracks.test.tsx` に Add の 3 状態（picked 行が
  出る / Cancel で消えて何も送らない / Create が path と name を送る）と Tweak の
  有効条件、Progress の表示

## Task 5: docs

- `docs/USAGE.ja.md` / `USAGE.md` / `TRACKS.ja.md` / `TRACKS.md` / `AGENTS.md` の
  「タブ 01/02/03」を 1 画面の言い方に直す。`.track.json` 取り込みの記述を落とす

## 順番

1 → 2 → 3 → 4 → 5。3 と 4 は同じセッションで続けて（殻の props を 4 が使う）。
実機確認は 4 の後に `bash <scratchpad>/reset-mac.sh` からの通し。
