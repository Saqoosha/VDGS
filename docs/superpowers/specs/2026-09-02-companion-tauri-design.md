# companion の Tauri 版（macOS 先行）

2026-09-02。Windows の companion（`companion/`、.NET Framework 4.8 + WinForms + WebView2）と
同じ仕事を macOS でする app。Tauri 2 + Rust + 既存の React（`web/`）。Windows 版はあとから
この土台に移し、そのとき `companion/` を消す。

## 前提（実測済み）

- Mac 版 VelociDrone は `~/Library/Application Support/PatchKit/Apps/<hash>/Data/velocidrone.app`。
  arm64 thin、Mono、adhoc 署名でハードンドランタイム無し。`DYLD_INSERT_LIBRARIES` が通る
- BepInEx 公式 5.4.23.5 は Apple Silicon で preloader が落ちる。fork のリリース
  `https://github.com/Saqoosha/BepInEx/releases/tag/v5.4.23.5-vdgs.1` の zip を使う
  （SHA256 `950d55271c176c732fc896bcdae2750978ef92b940c951aa7fad0eb4251f1d61`、660,321 bytes）
- BepInEx の GameRootPath は `.app` の**親**（`Data/`）。`vdgs/`・`bindings.json`・ログは全部そこ
- ユーザー DB は `~/Library/Application Support/com.velocidrone.velocidrone/user11.db`。
  テーブルは Windows と同じ。scene 16 = `BlankCanvas`
- シェーダーは Metal 用バンドル（`BuildBundles.BuildMac`）。`-force-d3d12` は要らない
- 起動は PatchKit のランチャーからでは注入されない。companion が自分で起動する

## 変えないもの

- `web/` の companion 画面と、`bridge.ts` の契約（コマンド 10 個、push 5 種）
- `catalog.json` と scene / track の zip・json の形
- 「同名トラックは上書きしない」「DB は触る前にコピー」「zip はゲームフォルダの外に出さない」

## 構成

```
companion-tauri/
  src-tauri/
    Cargo.toml
    tauri.conf.json          bundle id sh.saqoo.vdgs.companion、frontendDist は web の companion ビルド
    src/
      main.rs / lib.rs       コマンド登録、状態の push（state / log / progress / busy / running）
      catalog.rs             catalog.json 取得、ダウンロード＋進捗、SHA256、zip 展開（脱出拒否）
      game.rs                velocidrone.app の探索、mod の版、キャプチャ一覧、bindings.json、vdgs/ui/
      bepinex.rs             fork の zip を SHA 固定で取得、.app の隣に展開、BepInEx.cfg、quarantine 解除
      tracks.rs              user11.db（rusqlite）。バックアップ → INSERT、表示名の 2 段復号
      launch.rs              DYLD_INSERT_LIBRARIES + DOORSTOP_* を付けて arch -arm64 で起動、pid 監視
    resources/mod/           同梱する mod（VDGS.dll、Metal の vdgs-shaders、vdgs/ui/）。make-release.sh が置く
```

`bridge.ts` は host を 3 択にする：`chrome.webview`（Windows）、`__TAURI__`（Tauri）、無ければ dev の
スタンドイン。コマンドは `invoke("cmd", { cmd, id })` 1 本、push は `listen("push")` 1 本。
React 側は一切知らない。

## 各モジュールの責務

- **catalog.rs**：`Catalog::fetch(url)`、`download(file, dir, progress) -> path`（SHA が合わなければ
  消して失敗）、`extract(zip, root)`（`root` の外に出るエントリは拒否）
- **game.rs**：`find() -> Option<Game>`（`PatchKit/Apps/*/Data/velocidrone.app` を走査、複数なら
  更新日時が新しい方）、`root()` は `.app` の親。`installed_mod_version()` は
  `BepInEx/plugins/VDGS.dll` のファイルバージョン。`scenes()` は `vdgs/*/meta.json`。
  `read_bindings()` / `write_bindings()`。`install_mod()` は同梱 `mod/` をコピー
- **bepinex.rs**：`install(root)` は zip を `root` に展開（`.cfg` は既存を残す）、`libdoorstop.dylib`
  の quarantine 属性を外す（`xattr -d com.apple.quarantine`）。`BepInEx.cfg` は
  `[Logging.Disk] Enabled = true` だけ（`UnityLogListening = false` は D3D12 の事情で Mac には
  要らない）。`uninstall(root)` は `BepInEx/`・`libdoorstop.dylib`・`run_bepinex.sh` を消す
- **tracks.rs**：`db_path()`、`list()`、`display_name(stored)`（`+` → 空白、次に `%XX`）、
  `import(name, scene_id, type, value) -> Added | AlreadyPresent | WouldOverwrite`、`remove(name)`
  は `online_id = 0 and protected_track = 0` のものだけ
- **launch.rs**：`spawn(game)`。環境変数は `run_bepinex.sh` と同じ集合
  （`DOORSTOP_ENABLED=1`、`DOORSTOP_TARGET_ASSEMBLY=<root>/BepInEx/core/BepInEx.Preloader.dll`、
  `DYLD_LIBRARY_PATH=<root>`、`DYLD_INSERT_LIBRARIES=libdoorstop.dylib`）。
  `arch -arm64 -e DYLD_INSERT_LIBRARIES=... <exe>`。`running()` は spawn した pid の生死

## 状態と流れ

push する `state` は Windows 版と同じ形（`game`、`mod`、`bundledMod`、`missing`、`ready`、
`running`、`busy`、`busyPercent`、`launchArgs`、`tracks`、`unbound`、`scenes`）。`launchArgs` は
空文字（Mac では付けない）。

コマンドはすべて「やる → `state` を push」。長いもの（ダウンロード、導入）は `busy` → `progress`
→ `state`。失敗は `log` に 1 行、`state` は変えない。

## 署名と配布

`tauri build` → `.app` を Developer ID（Tomohiko Koyama）で署名、`notarytool` で公証、`.dmg`。
公開先は `publish.sh` に Mac の成果物を足す（Worker は静的配信なので変更なし）。

## テスト

- Rust 単体：`display_name` の 2 段復号（`Sols%2bStreet` の順序）、zip 脱出の拒否、SHA 不一致で
  ファイルが残らないこと、`import` の 3 通り
- 通し：この Mac で `BepInEx/` を消した状態から、pick → install mod → get FDF → fly。
  `vdgs-probe.log` に `shaders READY`、`/api/status` に紐付き

## やらないこと

自動更新。Windows 版の置き換え（別の作業）。カタログ形式の変更。`.ply` 直置きの UI。
