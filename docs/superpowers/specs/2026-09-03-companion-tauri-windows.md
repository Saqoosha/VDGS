# companion-tauri を Windows でも動かす

`companion/`（C# + WinForms + WebView2）がやっている仕事を `companion-tauri/` に引き取る。
機能は既に 1 対 1 で揃っている — `dispatch` の 10 コマンドと `MainForm.cs` の `case` が
完全に一致する。**だから新規実装ではなく、macOS 決め打ちになっている箇所を `cfg` で割る作業。**

C# は消さない。実機で通し確認が済んだ次のコミットで消す。

## 割る場所

以下、`companion-tauri/src-tauri/src/` からの相対。参照実装は `companion/*.cs` で、
**挙動が食い違ったら C# が正解**。

### `game.rs`

| 関数 | macOS（現状） | Windows |
|---|---|---|
| `find()` | PatchKit の `Apps/<hash>/Data/velocidrone.app` を mtime で最新 | 下記 |
| `is_game(p)` | `p/Contents/MacOS/velocidrone` が実在 | `p/velocidrone.exe` が実在 |
| `root(p)` | `p.parent()`（`.app` の親） | **`p` 自身** |
| `exe(p)` | `p/Contents/MacOS/velocidrone` | `p/velocidrone.exe` |
| `has_bepinex(root)` | `libdoorstop.dylib` + `BepInEx/core/BepInEx.Preloader.dll` | `winhttp.dll` + `BepInEx/` ディレクトリ（`GameInstall.HasBepInEx`） |

Windows の `find()` は `GameInstall.FindGame` の写し。候補リストを順に見て、各候補は
**そのままと `app` サブフォルダの 2 回**試す。候補 = 名前つき + ドライブごと：

```
C:\VelociDrone
%USERPROFILE%\Desktop\VelociDrone
%USERPROFILE%\Documents\VelociDrone
%USERPROFILE%\Downloads\VelociDrone
%USERPROFILE%\Downloads\Velocidrone Windows Launcher
<各 ready ドライブ>\VelociDrone
```

候補が全部外れたら `GameInstall.ScanForGame` 相当の走査へ落とす — 根は
`%USERPROFILE%` と各 ready ドライブ、深さ 5 の BFS、`velocidrone.exe` を探す。
飛ばす名前は `Windows` `$Recycle.Bin` `System Volume Information` `ProgramData`
`AppData` `node_modules` `.git` `WindowsApps` と `.` 始まり。
**リパースポイントは辿らない**（ジャンクションが上に戻って無限に歩く）。

`installed_mod_version` / `bundled_mod_*` / `scenes` / bindings 系は**両 OS 共通、変更なし**。

### `bepinex.rs`

Windows は**公式リリースをそのまま使う**（arm64 の MonoMod バグは Windows に無い）。

```rust
VERSION = "5.4.23.5"
url    = "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip"
bytes  = 639118
sha256 = "82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4"
```

- **`is_ours` は Windows では `has_bepinex` だけ。** スタンプファイル
  (`BepInEx/vdgs-bepinex-version.txt`) は書かない。あれは「公式版と patched 版が
  見分けられない」という macOS 固有の問題への答えで、Windows は公式版が正解なので
  区別する必要が無い。
- **`strip_quarantine` は macOS のみ。** `xattr` を Windows で呼ばない。
- `uninstall` が消す名前は Windows では `BepInEx` `winhttp.dll` `doorstop_config.ini`
  `changelog.txt`（`libdoorstop.dylib` / `run_bepinex.sh` は macOS のみ）。
- **`write_logging_config` は Windows 版が長い。** `BepInEx.cs::WriteLoggingConfig` の
  内容をそのまま出す（`UnityLogListening = false` + `[Logging.Disk] Enabled = true` +
  `LogLevel`）。macOS 版は現状のまま短いのを維持 — 既にコメントで理由が書いてある。

### `launch.rs`

- `doorstop_env` は **macOS のみ**。Windows は `winhttp.dll` が自前で注入するので環境変数は不要。
- `spawn(game)` — Windows は `Command::new(game.join("velocidrone.exe"))`、
  引数 `-force-d3d12`、`current_dir(game)`。stdin/stdout/stderr は null のまま。
  **`-force-d3d12` は削らない** — これが無いと splat シェーダーが unsupported になり、
  ログにも画面にも理由が出ない。
- `is_live_game_process` の名前判定は Windows では `velocidrone.exe`。
  ゾンビ判定は Unix のみ意味があるので、Windows では名前一致だけでよい。

### `tracks.rs`

`db_path()` を分ける。Windows:
`%USERPROFILE%\AppData\LocalLow\velocidrone\velocidrone\user11.db`
（`TrackStore.cs:39`）。`display_name` / `list` / import / `true_lens_on` は共通。

### `settings.rs`

`path()` を分ける。Windows は `dirs::data_local_dir()`（= `%LOCALAPPDATA%`）配下の
`VDGSCompanion/settings.json`。**`data_dir()` を使わない** — Windows では Roaming を返し、
C# 版（`SpecialFolder.LocalApplicationData`）と場所が食い違って既存ユーザーの設定が消える。
macOS は現状（`data_dir()` = Application Support）のまま。

### `lib.rs`

- `resolve_picked_game` — Windows は `is_game(path)` のみ（`.app` のネストは無い）。
- `pick_game` の警告文 — Windows は `"No velocidrone.exe in that folder."`。
  フォルダ選択の説明も `MainForm.cs:686` に合わせる。
- **`local_hms` の `#[cfg(not(unix))]` が 1970 固定になっている。**
  ログ行のタイムスタンプが全部 `00:00:00` になるので、Windows 実装を書く。
  `kernel32` の `GetLocalTime`（`SYSTEMTIME`）を `extern "system"` で呼ぶ。新しい依存は足さない。
- `resolve_resource_dir` は現状のロジックのままで Windows でも通る（`resource_dir/mod` を先に見る）。

### `catalog.rs`

**変更なし。** top-level を落とす規則は「`.txt`/`.md` だけ飛ばす」なので、
`winhttp.dll` も `doorstop_config.ini` も残る。

### `tauri.conf.json`

```json
"bundle": { "targets": ["app", "dmg", "nsis"], "icon": ["icons/icon.icns", "icons/icon.ico"],
            "windows": { "webviewInstallMode": { "type": "downloadBootstrapper" } } }
```

配布は**素の exe を zip**（`vdgs-companion-<ver>.zip`、今と同じ名前規約）。
`make-catalog.sh` / `publish.sh` / サイトは触らない。nsis は焼けるようにだけしておく。

## 守ること

- **`#[cfg(target_os = "macos")]` / `#[cfg(windows)]` で割る。** ファイルを
  `game/mac.rs` + `game/win.rs` に分けない — 共通部分の方が圧倒的に多く、分けると
  `scenes` や bindings が片方にしか無い状態を作りやすい。
- **既存のコメントを消さない。** どれも一度踏んだ罠の記録。
  片 OS 専用になったコメントには、どちらの話か 1 行足す。
- **既存のテストを壊さない。** `cargo test` が macOS で今まで通り通ること。
  Windows 分岐に足すテストは、パスを組み立てる純関数（`exe` / `root` / `db_path` /
  候補リスト / 走査）に対して書く。`DriveInfo` 相当のドライブ列挙はテストしない。
- **`unsafe` は `GetLocalTime` の 1 箇所だけ。**

## 通し確認

1. `cargo test`（Mac）— 既存が全部通る
2. `win4090` でビルド（cargo/rustc/VS/bun あり。`cargo install tauri-cli` が要る）
3. できた exe を `w`（VelociDrone 実機）へ送って 4 クリック：
   BepInEx 取得 → mod 導入 → キャプチャ取得 → FLY
4. `%LOCALAPPDATA%\VDGSCompanion\settings.json` が C# 版と同じ場所に出る
5. トラック紐付けが表示名で一致する（`+` と `%2b` の 2 段復号）

## この作業の外

- **SmartScreen。** 未署名 exe は zip でもインストーラでも Mark-of-the-Web が伝播して
  同じ警告が出る。**今の C# 版も既に出ている**ので移植による悪化ではない。
  消す手は署名だけで、Azure Artifact Signing の個人枠は米加限定。別途判断。
- **`companion/` の削除**と `make-release.sh` の分岐整理、`web/src/bridge.ts` の
  WebView2 経路の撤去。実機確認が通った次のコミット。
- **Windows 用ペイロードの用意** — `resources/mod/vdgs/vdgs-shaders` は
  **D3D12 で焼いたバンドル**でなければならない（約 1.5 MB。Metal 版の約 437 KB を
  入れると `shader.isSupported` が false になり、エラーは 1 行も出ない）。
