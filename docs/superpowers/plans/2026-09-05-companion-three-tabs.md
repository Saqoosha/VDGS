# companion 3 タブ統合 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** companion とゲーム内操作 UI を 1 つの 3 タブ構成に統合し、自分の `.ply` から自分のコースを作る経路を companion の中に通す。

**Architecture:** `web/` の React は既に 3 エントリ（`index.html` / `companion.html` / `site.html`）を 1 プロジェクトから吐いている。統合は同じコンポーネント木を両エントリが読む形にし、`bridge.ts` の `hosted` フラグで出すタブを切り替える。companion（Rust）は `.ply` の取り込み・トラック作成・`bindings.json` の書き込みを担い、プラグイン（C#）は `placement.json` を単一の真実として姿勢と位置を適用する。

**Tech Stack:** Rust (Tauri 2, rusqlite, serde_json) / C# (BepInEx 5, Unity 2021.3, Newtonsoft.Json) / TypeScript (React 19, Vite, Vitest, Tailwind)

**Spec:** `docs/superpowers/specs/2026-09-05-companion-three-tabs-design.md`

## Global Constraints

- **ダークモードを実装しない。** `prefers-color-scheme` も `data-theme` もテーマ切り替えも入れない。単一パレット
- **`Access-Control-Allow-Origin` をプラグインに足さない。** companion からの HTTP は Rust 経由で webview を通らないので不要。POST の `Content-Type: application/json` 必須も外さない
- **`JsonUtility` を使わない。** 辞書を例外も警告もなく `{}` にする。C# 側の JSON はゲーム同梱の Newtonsoft.Json 13
- **`bindings.json` の鍵は表示名。** `user11.db` の保存名は form 符号化されている。照合前に必ず表示形へ揃える
- **`ChunkInfo` は 64 バイト。** 触る場合の再確認用
- Rust テスト: `cd companion-tauri/src-tauri && cargo test`
- C# テスト: `dotnet test src/VDGS.Tests/VDGS.Tests.csproj`。テストプロジェクトは UnityEngine に依存しないファイルだけを `Compile Include` で取り込む
- web テスト: `cd web && bun run test`
- コミットメッセージは英語。本文の箇条書きも英語

---

### Task 1: Rust — 保存名の符号化

`tracks::display_name` の逆関数。companion がトラックを新規作成するとき、`user11.db` には
VelociDrone と同じ綴り（空白は `+`）で入れる必要がある。

**Files:**
- Modify: `companion-tauri/src-tauri/src/tracks.rs`

**Interfaces:**
- Consumes: `tracks::display_name(stored: &str) -> String`（既存、`tracks.rs:79`）
- Produces: `pub fn stored_name(display: &str) -> String`

- [ ] **Step 1: 失敗するテストを書く**

`companion-tauri/src-tauri/src/tracks.rs` の `mod tests` に足す:

```rust
    #[test]
    fn stored_name_is_the_inverse_of_display_name() {
        // Space becomes '+', and a literal '+' becomes '%2b' - the same two stages
        // VelociDrone applies, in the order that survives a round trip.
        assert_eq!(stored_name("VDGS my house"), "VDGS+my+house");
        assert_eq!(stored_name("Sols+Street+League+1"), "Sols%2bStreet%2bLeague%2b1");
        assert_eq!(stored_name("100% done"), "100%25+done");

        for display in ["VDGS my house", "Sols+Street+League+1", "100% done", "plain"] {
            assert_eq!(display_name(&stored_name(display)), display);
        }
    }
```

- [ ] **Step 2: 落ちることを確認**

Run: `cd companion-tauri/src-tauri && cargo test stored_name_is_the_inverse_of_display_name`
Expected: FAIL — `cannot find function stored_name in this scope`

- [ ] **Step 3: 実装**

`companion-tauri/src-tauri/src/tracks.rs` の `display_name` の直後に足す:

```rust
/// The inverse of [`display_name`].
///
/// The order is load-bearing and is the reverse of the decode. Percent-escape first, so
/// the '+' characters this function *creates* from spaces are not themselves escaped;
/// '%' goes first inside that, so an escape introduced later is not double-escaped.
pub fn stored_name(display: &str) -> String {
    display
        .replace('%', "%25")
        .replace('+', "%2b")
        .replace(' ', "+")
}
```

- [ ] **Step 4: 通ることを確認**

Run: `cd companion-tauri/src-tauri && cargo test stored_name`
Expected: PASS

- [ ] **Step 5: コミット**

```bash
git add companion-tauri/src-tauri/src/tracks.rs
git commit -m "Encode a typed track name the way VelociDrone stores it

- Add tracks::stored_name, the inverse of display_name
- Percent-escape before turning spaces into '+', so a created '+' is not escaped
- Round-trip test over names carrying spaces, literal '+', and '%'"
```

---

### Task 2: Rust — 種テンプレートの同梱と `createTrack`

**Files:**
- Create: `companion-tauri/src-tauri/resources/seed.track.json`
- Modify: `companion-tauri/src-tauri/tauri.conf.json`
- Modify: `companion-tauri/src-tauri/src/tracks.rs`
- Modify: `companion-tauri/src-tauri/src/lib.rs`

**Interfaces:**
- Consumes: `tracks::stored_name`, `tracks::import`, `tracks::display_name`, `game::bind`
- Produces: `pub fn seed_value(seed_json: &str) -> Result<(i64, i64, String), Error>` — 種の
  `.track.json` から `(scene_id, kind, value)` を取り出す。`Host::create_track(&self, name: &str, capture: &str)`

**前提:** `seed.track.json` はこの機械の `VDGS Template` から書き出したもの。まだ無い場合は
`"/Applications/VDGS Companion.app/Contents/MacOS/VDGS Companion" --export-track "VDGS Template" companion-tauri/src-tauri/resources/seed.track.json`
で作る。**この 1 ファイルが無いとタスク全体が着手できない。**

- [ ] **Step 1: 失敗するテストを書く**

`companion-tauri/src-tauri/src/tracks.rs` の `mod tests` に足す:

```rust
    #[test]
    fn seed_value_reads_scene_kind_and_value() {
        let seed = r#"{"name":"VDGS Template","scene_id":16,"type":0,"value":"{\"gates\":[]}"}"#;
        let (scene, kind, value) = seed_value(seed).unwrap();
        assert_eq!(scene, 16);
        assert_eq!(kind, 0);
        assert_eq!(value, "{\"gates\":[]}");
    }

    #[test]
    fn seed_value_rejects_a_seed_without_a_value() {
        let seed = r#"{"name":"VDGS Template","scene_id":16,"type":0}"#;
        assert!(seed_value(seed).is_err());
    }
```

- [ ] **Step 2: 落ちることを確認**

Run: `cd companion-tauri/src-tauri && cargo test seed_value`
Expected: FAIL — `cannot find function seed_value in this scope`

- [ ] **Step 3: 実装**

`companion-tauri/src-tauri/src/tracks.rs` に足す:

```rust
/// Pulls the three columns a track row needs out of a `.track.json`.
///
/// `value` is the game's own string and is copied byte for byte - a reformatted value is
/// a different track as far as VelociDrone is concerned.
pub fn seed_value(seed_json: &str) -> Result<(i64, i64, String), Error> {
    let v: serde_json::Value = serde_json::from_str(seed_json)
        .map_err(|e| Error::Msg(format!("seed template is not JSON: {e}")))?;
    let scene = v
        .get("scene_id")
        .and_then(|n| n.as_i64())
        .ok_or_else(|| Error::Msg("seed template has no scene_id".into()))?;
    let kind = v.get("type").and_then(|n| n.as_i64()).unwrap_or(0);
    let value = v
        .get("value")
        .and_then(|s| s.as_str())
        .ok_or_else(|| Error::Msg("seed template has no value".into()))?
        .to_string();
    Ok((scene, kind, value))
}
```

`Error` に `Msg(String)` バリアントが無ければ足す（`catalog::Error` と同じ形）。

- [ ] **Step 4: 通ることを確認**

Run: `cd companion-tauri/src-tauri && cargo test seed_value`
Expected: PASS

- [ ] **Step 5: 種を同梱する**

`companion-tauri/src-tauri/tauri.conf.json` の `bundle.resources` に足す:

```json
    "resources": [
      "resources/mod/**/*",
      "resources/seed.track.json"
    ],
```

- [ ] **Step 6: `create_track` を書く**

`companion-tauri/src-tauri/src/lib.rs` の `add_track` の隣に足す:

```rust
    /// Creates a track from the bundled seed and binds a capture to it in one step.
    ///
    /// The two happen together on purpose. Binding is keyed by track name, so a track
    /// created now and bound later is a rename waiting to break the link - and a broken
    /// link shows nothing at all, with no error anywhere.
    fn create_track(self: &Arc<Self>, name: &str, capture: &str) {
        let Some(app) = self.inner.lock().unwrap().game.clone() else {
            return;
        };
        let display = name.trim().to_string();
        if display.is_empty() {
            self.error_dialog("a track needs a name");
            return;
        }
        let capture = capture.to_string();
        let resource_dir = self.resource_dir.clone();
        let what = format!("creating {display}");
        self.run_busy(&what, move |_host, log| {
            if launch::is_running() {
                return Err(
                    "VelociDrone is running. Close it first - the track database is in use."
                        .into(),
                );
            }
            let seed_path = resource_dir.join("seed.track.json");
            let seed = std::fs::read_to_string(&seed_path)
                .map_err(|e| format!("seed template missing at {}: {e}", seed_path.display()))?;
            let (scene, kind, value) = tracks::seed_value(&seed).map_err(|e| e.to_string())?;

            let db = tracks::db_path();
            let stored = tracks::stored_name(&display);
            match tracks::import(&db, &stored, scene, kind, &value).map_err(|e| e.to_string())? {
                (tracks::ImportResult::Added, _) => log(format!("added track \"{display}\"")),
                (tracks::ImportResult::AlreadyPresent, _) => {
                    return Err(format!("a track called \"{display}\" is already there"))
                }
                (tracks::ImportResult::WouldOverwrite, _) => {
                    return Err(format!("a different track called \"{display}\" is already there"))
                }
            }

            let root = game::root(&app);
            game::bind(&root, &display, &capture).map_err(|e| e.to_string())?;
            log(format!("bound \"{display}\" to {capture}"));
            Ok(())
        });
    }
```

`Host.resource_dir`（`lib.rs:24`）と `tracks::db_path()`（`tracks.rs:56`）は既にある。
`add_track` は `run_busy` ではなく `add_track_inner` + `error_dialog` の形なので、**どちらの
形に寄せるかは `create_track` が長時間かかるかで決める** — 種の複製は一瞬なので
`add_track` と同じ `_inner` 形が近い。上の骨格はどちらでも通る。

- [ ] **Step 7: ビルドが通ることを確認**

Run: `cd companion-tauri/src-tauri && cargo test`
Expected: PASS（既存テストも含めて全部）

- [ ] **Step 8: コミット**

```bash
git add companion-tauri/src-tauri/resources/seed.track.json \
        companion-tauri/src-tauri/tauri.conf.json \
        companion-tauri/src-tauri/src/tracks.rs \
        companion-tauri/src-tauri/src/lib.rs
git commit -m "Create a track from a bundled seed and bind it in one step

- Bundle seed.track.json, a minimal valid track (VelociDrone needs a start and two gates)
- Add tracks::seed_value to pull scene_id, type and value out of a .track.json
- Add Host::create_track: insert the row under the stored spelling, bind under the shown one
- Refuse while the game is running; the track database is in use"
```

---

### Task 3: Rust — `installPly`

**Files:**
- Modify: `companion-tauri/src-tauri/src/lib.rs`
- Modify: `companion-tauri/src-tauri/src/game.rs`

**Interfaces:**
- Produces: `Host::install_ply(&self)`, `game::install_ply(root: &Path, ply: &Path) -> io::Result<String>`（置いた名前を返す）

- [ ] **Step 1: 失敗するテストを書く**

`companion-tauri/src-tauri/src/game.rs` の `mod tests` に足す:

```rust
    #[test]
    fn install_ply_copies_into_vdgs_and_returns_the_name() {
        let root = tmp();
        let src = tmp().join("My House.ply");
        std::fs::write(&src, b"ply\nelement vertex 3\nend_header\n").unwrap();

        let name = install_ply(&root, &src).unwrap();
        assert_eq!(name, "My House");
        assert!(root.join("vdgs/My House.ply").is_file());
    }

    #[test]
    fn install_ply_refuses_a_name_that_escapes_vdgs() {
        let root = tmp();
        let src = tmp().join("..ply");
        std::fs::write(&src, b"ply\n").unwrap();
        assert!(install_ply(&root, &src).is_err());
    }
```

- [ ] **Step 2: 落ちることを確認**

Run: `cd companion-tauri/src-tauri && cargo test install_ply`
Expected: FAIL — `cannot find function install_ply`

- [ ] **Step 3: 実装**

`companion-tauri/src-tauri/src/game.rs` に足す:

```rust
/// Copies a .ply into `<game>/vdgs/`, keeping its name. Returns the capture's name.
///
/// The name is the file stem, and it becomes both a directory-shaped key in
/// bindings.json and part of a path, so it goes through the same reservation and
/// traversal checks any other capture name does.
pub fn install_ply(root: &Path, ply: &Path) -> io::Result<String> {
    let stem = ply
        .file_stem()
        .and_then(|s| s.to_str())
        .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidInput, "the file has no name"))?;
    if stem.is_empty()
        || stem.eq_ignore_ascii_case("ui")
        || stem.contains('/')
        || stem.contains('\\')
        || stem.starts_with('.')
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            format!("\"{stem}\" cannot be used as a capture name"),
        ));
    }
    let dir = root.join("vdgs");
    fs::create_dir_all(&dir)?;
    fs::copy(ply, dir.join(format!("{stem}.ply")))?;
    Ok(stem.to_string())
}
```

- [ ] **Step 4: 通ることを確認**

Run: `cd companion-tauri/src-tauri && cargo test install_ply`
Expected: PASS

- [ ] **Step 5: ピッカーを足す**

`companion-tauri/src-tauri/src/lib.rs` の `install_zip` があった場所に足す:

```rust
    /// Picks a .ply and drops it into `<game>/vdgs/`.
    ///
    /// A .ply is parsed on every spawn rather than read from packed buffers, so this is
    /// the slow-to-show path - but it is the one shape a capture arrives in from anywhere
    /// that is not this project's own tooling.
    #[allow(clippy::needless_return)]
    fn install_ply(self: &Arc<Self>) {
        let Some(app) = self.inner.lock().unwrap().game.clone() else {
            return;
        };
        let Some(picked) = self
            .app
            .dialog()
            .file()
            .add_filter("Gaussian splat capture", &["ply"])
            .blocking_pick_file()
        else {
            return;
        };
        let Ok(ply) = picked.into_path() else {
            return;
        };
        let label = ply
            .file_name()
            .and_then(|s| s.to_str())
            .unwrap_or("capture")
            .to_string();
        let what = format!("installing {label}");
        self.run_busy(&what, move |_host, log| {
            if launch::is_running() {
                return Err(
                    "VelociDrone is running. Close it first - files in use cannot be replaced."
                        .into(),
                );
            }
            let root = game::root(&app);
            let name = game::install_ply(&root, &ply).map_err(|e| e.to_string())?;
            log(format!("installed {name}"));
            Ok(())
        });
    }
```

- [ ] **Step 6: 全テストが通ることを確認**

Run: `cd companion-tauri/src-tauri && cargo test`
Expected: PASS

- [ ] **Step 7: コミット**

```bash
git add companion-tauri/src-tauri/src/game.rs companion-tauri/src-tauri/src/lib.rs
git commit -m "Take a .ply straight from a file picker

- Add game::install_ply: copy into <game>/vdgs/, return the capture name
- Reject the reserved name 'ui', separators, and dotfiles - the stem becomes a path
- Add Host::install_ply, refusing while the game holds the files open"
```

---

### Task 4: Rust — `lan_url`

操作 UI の在処を companion が名乗るために、この機械の LAN アドレスを state に載せる。

**Files:**
- Modify: `companion-tauri/src-tauri/src/state.rs`

**Interfaces:**
- Produces: `pub fn lan_url() -> Option<String>`、`SetupState.lan_url: Option<String>`

- [ ] **Step 1: 失敗するテストを書く**

`companion-tauri/src-tauri/src/state.rs` の `mod tests` に足す:

```rust
    #[test]
    fn lan_url_is_a_reachable_http_url_or_nothing() {
        // A machine with no route has no LAN address to advertise, and saying nothing is
        // correct there. When there is one it must be a plain http URL on the plugin's
        // port, never a loopback address - the whole point is reaching it from elsewhere.
        if let Some(url) = lan_url() {
            assert!(url.starts_with("http://"), "{url}");
            assert!(url.ends_with(":8777/"), "{url}");
            assert!(!url.contains("127.0.0.1"), "{url}");
        }
    }
```

- [ ] **Step 2: 落ちることを確認**

Run: `cd companion-tauri/src-tauri && cargo test lan_url`
Expected: FAIL — `cannot find function lan_url in this scope`

- [ ] **Step 3: 実装**

`companion-tauri/src-tauri/src/state.rs` に足す:

```rust
/// This machine's address on the local network, as the URL the plugin serves on.
///
/// Found by asking the OS which local address it would use to reach the outside, which
/// is the one interface a phone on the same Wi-Fi can also reach. The socket is UDP and
/// unconnected in any real sense - no packet is sent and the address is never contacted.
/// Enumerating interfaces instead means picking between several, and the wrong pick is a
/// URL that silently does not answer.
pub fn lan_url() -> Option<String> {
    let sock = std::net::UdpSocket::bind("0.0.0.0:0").ok()?;
    sock.connect("8.8.8.8:80").ok()?;
    let ip = sock.local_addr().ok()?.ip();
    if ip.is_loopback() || ip.is_unspecified() {
        return None;
    }
    Some(format!("http://{ip}:8777/"))
}
```

`SetupState` に `pub lan_url: Option<String>,` を足し、`build`（state を組み立てる関数）で
`lan_url: lan_url(),` を埋める。

- [ ] **Step 4: 通ることを確認**

Run: `cd companion-tauri/src-tauri && cargo test`
Expected: PASS

- [ ] **Step 5: コミット**

```bash
git add companion-tauri/src-tauri/src/state.rs
git commit -m "Tell the app where the control UI lives

- Add state::lan_url, this machine's address as the URL the plugin serves
- Ask the OS which address reaches the outside rather than enumerating interfaces
- Report nothing when the only address is loopback"
```

---

### Task 5: Rust — コマンドの入口を広げ、zip の経路を落とす

**Files:**
- Modify: `companion-tauri/src-tauri/src/lib.rs`
- Modify: `companion-tauri/src-tauri/src/game.rs`

**Interfaces:**
- Consumes: `Host::create_track`（Task 2）、`Host::install_ply`（Task 3）
- Produces: `dispatch(host, cmd, id, arg)` — `arg: Option<serde_json::Value>`。
  コマンド名 `installPly` / `createTrack` / `unbindTrack` / `removeCapture`。
  `game::remove_capture(root: &Path, name: &str) -> io::Result<bool>`

- [ ] **Step 1: 失敗するテストを書く**

`companion-tauri/src-tauri/src/game.rs` の `mod tests` に足す:

```rust
    #[test]
    fn remove_capture_takes_the_ply_and_its_siblings() {
        let root = tmp();
        let vdgs = root.join("vdgs");
        std::fs::create_dir_all(&vdgs).unwrap();
        for f in ["a.ply", "a.collision.bin", "a.placement.json", "b.ply"] {
            std::fs::write(vdgs.join(f), b"x").unwrap();
        }
        assert!(remove_capture(&root, "a").unwrap());
        assert!(!vdgs.join("a.ply").exists());
        assert!(!vdgs.join("a.collision.bin").exists());
        assert!(!vdgs.join("a.placement.json").exists());
        assert!(vdgs.join("b.ply").exists());
    }

    #[test]
    fn remove_capture_takes_a_converted_directory() {
        let root = tmp();
        let dir = root.join("vdgs/scene");
        std::fs::create_dir_all(&dir).unwrap();
        std::fs::write(dir.join("meta.json"), b"{}").unwrap();
        assert!(remove_capture(&root, "scene").unwrap());
        assert!(!dir.exists());
    }

    #[test]
    fn remove_capture_reports_nothing_removed() {
        let root = tmp();
        assert!(!remove_capture(&root, "absent").unwrap());
    }
```

- [ ] **Step 2: 落ちることを確認**

Run: `cd companion-tauri/src-tauri && cargo test remove_capture`
Expected: FAIL — `cannot find function remove_capture`

- [ ] **Step 3: 実装**

`companion-tauri/src-tauri/src/game.rs` に足す:

```rust
/// Removes a capture, whichever of the two shapes it is on disk.
///
/// A converted capture is a directory; a .ply is a file with up to two siblings that
/// carry its collision shell and its placement. Leaving a stale .placement.json behind
/// means the next capture that happens to take the name inherits someone else's scale.
pub fn remove_capture(root: &Path, name: &str) -> io::Result<bool> {
    let vdgs = root.join("vdgs");
    let mut removed = false;

    let dir = vdgs.join(name);
    if dir.is_dir() {
        fs::remove_dir_all(&dir)?;
        removed = true;
    }
    for ext in [".ply", ".collision.bin", ".placement.json"] {
        let p = vdgs.join(format!("{name}{ext}"));
        if p.is_file() {
            fs::remove_file(&p)?;
            removed = true;
        }
    }
    Ok(removed)
}
```

- [ ] **Step 4: 通ることを確認**

Run: `cd companion-tauri/src-tauri && cargo test remove_capture`
Expected: PASS

- [ ] **Step 5: `dispatch` を広げ、zip を落とす**

`companion-tauri/src-tauri/src/lib.rs`:

```rust
#[tauri::command(async)]
fn dispatch(
    host: tauri::State<'_, Arc<Host>>,
    cmd: String,
    id: Option<String>,
    arg: Option<serde_json::Value>,
) {
    let h = Arc::clone(&host);
    let field = |k: &str| -> Option<String> {
        arg.as_ref()
            .and_then(|v| v.get(k))
            .and_then(|v| v.as_str())
            .map(|s| s.to_string())
    };
    match cmd.as_str() {
        "refresh" => h.push(),
        "pick" => h.pick_game(),
        "installMod" => h.install_mod(),
        "uninstallMod" => h.uninstall_mod(),
        "installPly" => h.install_ply(),
        "refreshCatalog" => h.refresh_catalog(),
        "get" => {
            if let Some(id) = id {
                h.get_from_catalog(&id);
            }
        }
        "removeTrack" => {
            if let Some(id) = id {
                h.remove_track(&id);
            }
        }
        "createTrack" => {
            if let (Some(name), Some(capture)) = (field("name"), field("capture")) {
                h.create_track(&name, &capture);
            }
        }
        "unbindTrack" => {
            if let Some(id) = id {
                h.unbind_track(&id);
            }
        }
        "removeCapture" => {
            if let Some(id) = id {
                h.remove_capture(&id);
            }
        }
        "addTrack" => h.add_track(),
        "fly" => h.launch(),
        _ => {}
    }
}
```

`Host::install_zip` を削除する。`game::install_archive` は**残す** — カタログのダウンロードが
同じ関数を通っている（`lib.rs:447`）。

`Host` に 2 つ足す:

```rust
    fn unbind_track(self: &Arc<Self>, track: &str) {
        let Some(app) = self.inner.lock().unwrap().game.clone() else {
            return;
        };
        let track = track.to_string();
        self.run_busy(&format!("unbinding {track}"), move |_host, log| {
            // No is_running guard: bindings.json is ours, not the game's, and the plugin
            // picks the change up from its own poll within a second.
            let root = game::root(&app);
            if game::unbind(&root, &track).map_err(|e| e.to_string())? {
                log(format!("unbound \"{track}\""));
            } else {
                log(format!("\"{track}\" was not bound"));
            }
            Ok(())
        });
    }

    fn remove_capture(self: &Arc<Self>, name: &str) {
        let Some(app) = self.inner.lock().unwrap().game.clone() else {
            return;
        };
        let name = name.to_string();
        self.run_busy(&format!("removing {name}"), move |_host, log| {
            if launch::is_running() {
                return Err(
                    "VelociDrone is running. Close it first - files in use cannot be removed."
                        .into(),
                );
            }
            let root = game::root(&app);
            if game::remove_capture(&root, &name).map_err(|e| e.to_string())? {
                log(format!("removed {name}"));
            } else {
                log(format!("{name} was not there"));
            }
            Ok(())
        });
    }
```

**`unbind_track` だけ稼働中でも通す。** 書くのは `bindings.json` で、ゲームが開いている
ファイルではない。プラグインは自分の poll で 1 秒以内に拾う（Task 8）。

- [ ] **Step 6: 全テストとビルドを確認**

Run: `cd companion-tauri/src-tauri && cargo test && cargo build`
Expected: PASS。`install_zip` への参照が残っていればここで落ちる

- [ ] **Step 7: コミット**

```bash
git add companion-tauri/src-tauri/src/lib.rs companion-tauri/src-tauri/src/game.rs
git commit -m "Widen the command channel and drop the zip picker

- dispatch takes an arg object; creating a track needs a name and a capture, not one id
- Add unbindTrack, removeCapture, createTrack, installPly
- Add game::remove_capture, covering both the directory and the .ply-plus-siblings shape
- Remove Host::install_zip; game::install_archive stays, the catalog download uses it"
```

---

### Task 6: C# — 姿勢の合成（純粋関数）

Up と Turn から Unity のオイラー角を作る。UnityEngine に依存しないので単体テストできる。

**Files:**
- Create: `src/VDGS/SplatOrientation.cs`
- Create: `src/VDGS.Tests/SplatOrientationTests.cs`
- Modify: `src/VDGS.Tests/VDGS.Tests.csproj`

**Interfaces:**
- Produces: `VDGS.SplatOrientation.Compose(string up, float turn, out float x, out float y, out float z)`

- [ ] **Step 1: 失敗するテストを書く**

`src/VDGS.Tests/SplatOrientationTests.cs`:

```csharp
using VDGS;
using Xunit;

public class SplatOrientationTests
{
    // Unity applies euler angles Z, then X, then Y, so the Y slot is the last rotation
    // and is therefore a world-space yaw. That is exactly what Turn is, which is why it
    // can be dropped into the Y component whatever Up chose.
    [Theory]
    [InlineData("+y", 0f, 0f, 0f)]
    [InlineData("-y", 180f, 0f, 0f)]
    [InlineData("+z", -90f, 0f, 0f)]
    [InlineData("-z", 90f, 0f, 0f)]
    [InlineData("+x", 0f, 0f, 90f)]
    [InlineData("-x", 0f, 0f, -90f)]
    public void ComposesTheUpAxis(string up, float ex, float ey, float ez)
    {
        SplatOrientation.Compose(up, 0f, out var x, out var y, out var z);
        Assert.Equal(ex, x, 3);
        Assert.Equal(ey, y, 3);
        Assert.Equal(ez, z, 3);
    }

    [Fact]
    public void TurnGoesIntoTheYawSlotWithoutDisturbingUp()
    {
        SplatOrientation.Compose("+z", 37.5f, out var x, out var y, out var z);
        Assert.Equal(-90f, x, 3);
        Assert.Equal(37.5f, y, 3);
        Assert.Equal(0f, z, 3);
    }

    [Fact]
    public void TurnWrapsIntoZeroToThreeSixty()
    {
        SplatOrientation.Compose("+y", 400f, out _, out var y, out _);
        Assert.Equal(40f, y, 3);
        SplatOrientation.Compose("+y", -10f, out _, out var y2, out _);
        Assert.Equal(350f, y2, 3);
    }

    // An unreadable value must not silently become a different orientation - identity is
    // the one answer that leaves the capture as authored.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sideways")]
    public void AnUnknownUpFallsBackToIdentity(string up)
    {
        SplatOrientation.Compose(up, 0f, out var x, out var y, out var z);
        Assert.Equal(0f, x, 3);
        Assert.Equal(0f, y, 3);
        Assert.Equal(0f, z, 3);
    }
}
```

- [ ] **Step 2: 落ちることを確認**

`src/VDGS.Tests/VDGS.Tests.csproj` の `ItemGroup` に足してから:

```xml
    <Compile Include="..\VDGS\SplatOrientation.cs" Link="SplatOrientation.cs" />
```

Run: `dotnet test src/VDGS.Tests/VDGS.Tests.csproj`
Expected: FAIL — `SplatOrientation` が存在しない

- [ ] **Step 3: 実装**

`src/VDGS/SplatOrientation.cs`:

```csharp
namespace VDGS
{
    /// <summary>
    /// Turns the two authoring controls - which axis points at the sky, and how far the
    /// capture is turned about it - into the euler angles the transform takes.
    ///
    /// No UnityEngine types here on purpose: this is the part with arithmetic in it, and
    /// the test project can only compile files that do not reach into the engine.
    /// </summary>
    internal static class SplatOrientation
    {
        internal const string DefaultUp = "+y";

        /// <summary>
        /// Unity applies euler angles in the order Z, X, Y, so the Y component is the
        /// last rotation and acts in world space. Up therefore only ever needs X and Z,
        /// and Turn is free to occupy Y whatever Up chose.
        /// </summary>
        internal static void Compose(string up, float turn, out float x, out float y, out float z)
        {
            x = 0f;
            z = 0f;
            switch (up)
            {
                case "+y": break;
                case "-y": x = 180f; break;
                case "+z": x = -90f; break;
                case "-z": x = 90f; break;
                case "+x": z = 90f; break;
                case "-x": z = -90f; break;
                // Anything else is a file written by hand or by a newer build. Identity
                // leaves the capture exactly as it was authored, which is the only answer
                // that cannot make things worse.
                default: break;
            }
            y = turn % 360f;
            if (y < 0f) y += 360f;
        }
    }
}
```

`internal` だとテストから見えないので、`src/VDGS/VDGS.csproj` に
`<InternalsVisibleTo Include="VDGS.Tests" />` を足すか、`SplatOrientation` と `Compose` を
`public` にする。**既存の `VdgsPaths` に合わせる** — そちらと同じアクセス修飾子を使う。

- [ ] **Step 4: 通ることを確認**

Run: `dotnet test src/VDGS.Tests/VDGS.Tests.csproj`
Expected: PASS（16 ケース）

- [ ] **Step 5: コミット**

```bash
git add src/VDGS/SplatOrientation.cs src/VDGS.Tests/SplatOrientationTests.cs src/VDGS.Tests/VDGS.Tests.csproj
git commit -m "Compose an orientation from an up axis and a turn

- Add SplatOrientation.Compose, mapping six up axes plus a yaw onto euler angles
- Keep it free of UnityEngine so the test project can compile it
- Fall back to identity on an unreadable up rather than guessing a different pose"
```

---

### Task 7: C# — `placement.json` に `mirrorY` / `up` / `turn`

**Files:**
- Modify: `src/VDGS/SplatScene.cs`
- Modify: `src/VDGS/SplatCollision.cs`

**Interfaces:**
- Consumes: `SplatOrientation.Compose`（Task 6）、`PlyLoader.Load(path, out error, bool mirrorY)`（既存）
- Produces: `Placement` に `mirrorY` / `up` / `turn` の 3 フィールド。
  `SplatScene.SetOrientation(string up, float? turn, bool? mirror, StringBuilder log)`

**このタスクは UnityEngine に依存するので単体テストが無い。** 検証は実機で行い、手順を
Step 6 に書いてある。

- [ ] **Step 1: `Placement` にフィールドを足す**

`src/VDGS/SplatScene.cs` の `Placement` クラス（`position` / `rotation` / `scale` がある所）:

```csharp
            /// <summary>
            /// Which of the capture's axes points at the sky. One of +x -x +y -y +z -z.
            /// </summary>
            public string up = SplatOrientation.DefaultUp;

            /// <summary>Rotation about the up axis, in degrees. Matches the baked light.</summary>
            public float turn = 0f;

            /// <summary>
            /// Whether the capture is flipped in Y as it is read.
            ///
            /// 3DGS is right-handed Y-down and Unity is left-handed Y-up, so a capture
            /// that arrives untouched is a mirror image. This is a handedness change and
            /// no rotation can stand in for it. Null means "as this shape has always
            /// behaved": true for a .ply, false for a converted directory, which was
            /// already mirrored before export.
            /// </summary>
            public bool? mirrorY = null;
```

`EnsurePlacementDefaults`（`rotation` の長さを直している所、`SplatScene.cs:457` 付近）に足す:

```csharp
                if (string.IsNullOrEmpty(p.up)) p.up = SplatOrientation.DefaultUp;
```

- [ ] **Step 2: `Spawn` で placement を先に読む**

`src/VDGS/SplatScene.cs:113` の `Spawn`。いまは data を読んでから placement を読んでいるが、
`mirrorY` が data の読み方を決めるので**順番を逆にする**:

```csharp
            var placement = LoadPlacement();
            var mirror = placement.mirrorY ?? IsPly;

            var data = IsPly ? PlyLoader.Load(m_Dir, out var error, mirror)
                             : SplatData.Load(m_Dir, out error);
```

**変換済みキャプチャでは `mirrorY` は効かない。** `SplatData.Load` は packed buffer を読む
だけで、鏡映するには全フォーマットを復号し直す必要がある。UI 側でも変換済みには
このつまみを出さない（Task 13）。

`placement` を後で読み直している行があれば消す（二度読みは無駄で、しかも片方だけ古くなる）。

- [ ] **Step 3: 合成した回転を適用する**

同じ `Spawn` の中、`eulerAngles` を入れている `SplatScene.cs:135` を置き換える:

```csharp
            // up と turn があればそちらが真実。無い placement.json（手書き・古い版）だけが
            // 生の rotation にフォールバックする。
            if (!string.IsNullOrEmpty(placement.up))
            {
                SplatOrientation.Compose(placement.up, placement.turn,
                                         out var rx, out var ry, out var rz);
                m_Go.transform.eulerAngles = new Vector3(rx, ry, rz);
            }
            else
            {
                m_Go.transform.eulerAngles = new Vector3(
                    placement.rotation[0], placement.rotation[1], placement.rotation[2]);
            }
```

- [ ] **Step 4: コリジョンに同じ旗を渡す**

`src/VDGS/SplatCollision.cs:100` はいま拡張子だけで決めている:

```csharp
            var mirror = dir.EndsWith(".ply", StringComparison.OrdinalIgnoreCase);
```

呼び出し側から旗を受け取る形に変える。`Attach`（またはこのメソッド）に `bool mirrorY`
引数を足し、`SplatScene` が `placement.mirrorY ?? IsPly` を渡す。**同じ 1 つの値を splat と
コリジョンの両方が読む**のがこのタスクの要点で、拡張子から独立に決まる旗が 2 つあると
壁だけ鏡像になった世界ができる。エラーは 1 行も出ない。

- [ ] **Step 5: `SetOrientation` を書く**

`SetTransform` の隣に足す:

```csharp
        /// <summary>
        /// Sets the up axis, the turn, and the mirror flag, then persists them.
        ///
        /// Changing the mirror re-reads the capture, because the flip happens as the data
        /// is parsed rather than on the transform - a negative scale would flip the
        /// covariances with it. Up and turn are transform-only and take effect at once.
        /// </summary>
        internal void SetOrientation(string up, float? turn, bool? mirror, StringBuilder log)
        {
            var p = LoadPlacement();
            var reload = false;

            if (!string.IsNullOrEmpty(up)) p.up = up;
            if (turn.HasValue) p.turn = turn.Value;
            if (mirror.HasValue && mirror.Value != (p.mirrorY ?? IsPly))
            {
                p.mirrorY = mirror.Value;
                reload = true;
            }
            SavePlacementData(p, log);

            if (reload)
            {
                var wasSpawned = Spawned;
                Despawn();
                if (wasSpawned) Spawn(log);
                return;
            }
            if (m_Go != null)
            {
                SplatOrientation.Compose(p.up, p.turn, out var rx, out var ry, out var rz);
                m_Go.transform.eulerAngles = new Vector3(rx, ry, rz);
            }
            log?.AppendLine(Name + ": up=" + p.up + " turn=" + p.turn.ToString("0.#")
                            + " mirror=" + (p.mirrorY ?? IsPly));
        }
```

- [ ] **Step 6: 実機で確認**

```bash
bash tools/deploy.sh --plugin
bash tools/launch-win.sh          # macOS ならそのまま companion の FLY
```

確かめること:

1. `<game>/vdgs/<name>.placement.json` に `up` / `turn` / `mirrorY` が書かれる
2. `up` を `+z` にして飛び、**キャプチャが 90° 転ぶ**（描画が壊れない・共分散が破綻しない）
3. `turn` を `90` にして飛び、**上を軸に回る**
4. コリジョンを on にして `show solid` にし、**殻が splat と同じ向きで動く**
5. `mirrorY` を反転して飛び、**splat と殻が両方いっしょに反転する**（片方だけ反転したら失敗）
6. `vdgs-probe.log` にエラーが増えていない

**2 と 5 は今回はじめて通電する経路。** ここで壊れたら仕様書の「未検証」に戻して報告する。

- [ ] **Step 7: コミット**

```bash
git add src/VDGS/SplatScene.cs src/VDGS/SplatCollision.cs
git commit -m "Let a capture be turned, stood up, and unmirrored from its placement

- Add up, turn and mirrorY to placement.json; compose the euler from up and turn
- Read the placement before the data, because mirrorY decides how the data is read
- Pass one mirror flag to both the loader and the collision shell so they cannot disagree
- Fall back to the raw rotation only when up is absent, for hand-written files"
```

---

### Task 8: C# — `bindings.json` の再読み込み

**Files:**
- Modify: `src/VDGS/TrackBindings.cs`
- Modify: `src/VDGS/Plugin.cs`

**Interfaces:**
- Produces: `TrackBindings.ReloadIfChanged()` — ファイルが外から書き換わっていたら読み直す

**背景:** `Load()` はコンストラクタでしか呼ばれない（`TrackBindings.cs:33`）。ゲーム稼働中に
companion が `bindings.json` を書いてもプラグインは気づかず、次の `Save()` で**上書きする**。

- [ ] **Step 1: 実装**

`src/VDGS/TrackBindings.cs`:

```csharp
        // The stamp of the last write this object knows about - either what it read or
        // what it wrote. Anything else on disk came from outside and wins.
        private DateTime m_Stamp = DateTime.MinValue;
        private long m_Length = -1;

        /// <summary>
        /// Re-reads bindings.json when someone else has written it.
        ///
        /// The companion writes this file directly, with the game either running or not,
        /// so the copy held here goes stale without anything saying so - and the next
        /// Save() would put the stale copy back over the new one. Called once a second
        /// from the track poll, which is already running.
        ///
        /// Length is compared as well as time because two writes inside one filesystem
        /// timestamp tick are indistinguishable otherwise, and a binding edit is exactly
        /// the kind of small change that lands in the same tick as the one before it.
        /// </summary>
        internal void ReloadIfChanged()
        {
            try
            {
                if (!System.IO.File.Exists(m_Path))
                    return;
                var info = new System.IO.FileInfo(m_Path);
                if (info.LastWriteTimeUtc == m_Stamp && info.Length == m_Length)
                    return;
                Load();
                VdgsPlugin.Log.LogInfo("[VDGS] bindings.json changed on disk, reloaded ("
                                       + m_Map.Count + " track(s))");
            }
            catch (Exception ex)
            {
                VdgsPlugin.Log.LogError("bindings.json reload failed: " + ex.Message);
            }
        }

        private void Stamp()
        {
            try
            {
                var info = new System.IO.FileInfo(m_Path);
                m_Stamp = info.LastWriteTimeUtc;
                m_Length = info.Length;
            }
            catch
            {
                m_Stamp = DateTime.MinValue;
                m_Length = -1;
            }
        }
```

`Load()` の末尾と `Save()` の `File.WriteAllText` の直後で `Stamp()` を呼ぶ。**両方要る** —
自分が書いた直後に控えないと、次の poll で自分の書き込みを「外からの変更」と読んで
読み直しが毎秒走る。

- [ ] **Step 2: poll から呼ぶ**

`src/VDGS/Plugin.cs` の `PollTrack`（1 秒ごとに回っている）の先頭で:

```csharp
            m_Bindings.ReloadIfChanged();
```

- [ ] **Step 3: 実機で確認**

```bash
bash tools/deploy.sh --plugin
```

1. ゲームを起動して紐付けのあるトラックに入り、キャプチャが出ていることを確認
2. **ゲームを動かしたまま** `<game>/vdgs/bindings.json` からその行を消す
3. **1 秒ほどでキャプチャが消える**。`vdgs-probe.log` に `reloaded` が出る
4. 行を戻すと 1 秒ほどで戻る
5. ブラウザ UI から `Bind` を押し、**その直後の 5 秒間に `reloaded` が出ない**
   （自分の書き込みを外部変更と誤読していないこと）

- [ ] **Step 4: コミット**

```bash
git add src/VDGS/TrackBindings.cs src/VDGS/Plugin.cs
git commit -m "Notice when bindings.json is written from outside

- Load() only ever ran from the constructor, so a companion write went unseen and the
  next Save() put the stale map back over it, silently
- Add ReloadIfChanged, called from the track poll that already runs once a second
- Compare length as well as write time; two writes can share one timestamp tick
- Stamp after our own writes too, or every poll would re-read what we just saved"
```

---

### Task 9: C# — `/api/transform` を広げる

**Files:**
- Modify: `src/VDGS/WebControl.cs`
- Modify: `src/VDGS/SplatScene.cs`
- Modify: `src/VDGS/Plugin.cs`

**Interfaces:**
- Consumes: `SplatScene.SetOrientation`（Task 7）
- Produces: `/api/transform` が `x` / `z` / `up` / `turn` / `mirror` も受ける。
  `/api/status` の各 scene が `up` / `turn` / `mirror` / `x` / `z` を返す

- [ ] **Step 1: `SetTransform` を水平位置まで広げる**

`src/VDGS/SplatScene.cs:380` の `SetTransform` に `x` と `z` を足す:

```csharp
        internal void SetTransform(float? scale, float? yOffset, float? x, float? z,
                                   StringBuilder log)
        {
            var p = LoadPlacement();
            if (scale.HasValue)
                p.scale = Mathf.Clamp(scale.Value, 0.01f, 100f);
            if (yOffset.HasValue)
                p.position[1] = Mathf.Clamp(yOffset.Value, -1000f, 1000f);
            // Horizontal position was left out on the grounds that a capture should
            // arrive already oriented. That reasoning is about orientation: someone who
            // scanned their own room does not get to choose where COLMAP put the origin,
            // and the capture lands at the scenery origin whether that helps or not.
            if (x.HasValue)
                p.position[0] = Mathf.Clamp(x.Value, -1000f, 1000f);
            if (z.HasValue)
                p.position[2] = Mathf.Clamp(z.Value, -1000f, 1000f);

            if (m_Go != null)
            {
                m_Go.transform.localScale = Vector3.one * p.scale;
                m_Go.transform.position =
                    new Vector3(p.position[0], p.position[1], p.position[2]);
            }
            SavePlacementData(p, log);
            log?.AppendLine(Name + ": scale=" + p.scale.ToString("0.###")
                            + " pos=(" + p.position[0].ToString("0.##") + ", "
                            + p.position[1].ToString("0.##") + ", "
                            + p.position[2].ToString("0.##") + ")");
        }
```

呼び出し側（`Plugin.cs` の `SetTransform` ハンドラ）を新しい引数に合わせる。

- [ ] **Step 2: `/api/transform` を広げる**

`src/VDGS/WebControl.cs:203`:

```csharp
                case "/api/transform":
                {
                    var body = ReadBody(ctx);
                    // Nullable throughout so the UI can send only the field it changed; a
                    // missing value must leave the others alone rather than resetting them.
                    var req = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
                    string splat = null, up = null;
                    float? scale = null, y = null, x = null, z = null, turn = null;
                    bool? mirror = null;
                    if (req != null)
                    {
                        if (req.TryGetValue("splat", out var sv) && sv != null) splat = sv.ToString();
                        if (req.TryGetValue("scale", out var cv) && cv != null) scale = Convert.ToSingle(cv);
                        if (req.TryGetValue("y", out var yv) && yv != null) y = Convert.ToSingle(yv);
                        if (req.TryGetValue("x", out var xv) && xv != null) x = Convert.ToSingle(xv);
                        if (req.TryGetValue("z", out var zv) && zv != null) z = Convert.ToSingle(zv);
                        if (req.TryGetValue("turn", out var tv) && tv != null) turn = Convert.ToSingle(tv);
                        if (req.TryGetValue("up", out var uv) && uv != null) up = uv.ToString();
                        if (req.TryGetValue("mirror", out var mv) && mv != null) mirror = Convert.ToBoolean(mv);
                    }
                    var sName = splat; var sScale = scale; var sY = y; var sX = x; var sZ = z;
                    var sUp = up; var sTurn = turn; var sMirror = mirror;
                    QueueOnMain(() =>
                    {
                        SetTransform?.Invoke(sName, sScale, sY, sX, sZ);
                        if (sUp != null || sTurn.HasValue || sMirror.HasValue)
                            SetOrientation?.Invoke(sName, sUp, sTurn, sMirror);
                    });
                    Respond(ctx, 200, "{\"ok\":true}");
                    return;
                }
```

`SetTransform` デリゲートの型を 5 引数に、`SetOrientation` デリゲートを新設し、`Plugin.cs`
から `SplatScene` のメソッドに繋ぐ。

- [ ] **Step 3: `/api/status` に読み返しを足す**

各 scene を組んでいる箇所（`Plugin.cs` の status 構築）に足す:

```csharp
                ["up"] = scene.Up,
                ["turn"] = scene.Turn,
                ["mirror"] = scene.MirrorY,
                ["x"] = scene.XOffset,
                ["z"] = scene.ZOffset,
```

`SplatScene` に `Scale` / `YOffset` と同じ形の読み出しプロパティを足す:

```csharp
        internal string Up => LoadPlacement().up;
        internal float Turn => LoadPlacement().turn;
        internal bool MirrorY => LoadPlacement().mirrorY ?? IsPly;
        internal float XOffset => m_Go != null ? m_Go.transform.position.x : LoadPlacement().position[0];
        internal float ZOffset => m_Go != null ? m_Go.transform.position.z : LoadPlacement().position[2];
```

**UI がボタンの押下状態を復元するために要る** — 無いと画面を開き直すたびに Up が `+y` に
見え、押していない状態が正しいかのように表示される。

`web/src/types.ts` の `Scene` にも同じ 5 つを足す。

- [ ] **Step 4: curl で確認**

ゲームを起動してキャプチャを 1 つ出し、別の端末から:

```bash
curl -sS -X POST http://localhost:8777/api/transform \
     -H 'Content-Type: application/json' \
     -d '{"splat":"<name>","up":"+z","turn":45,"x":-100,"z":85}'
curl -sS http://localhost:8777/api/status | python3 -m json.tool | grep -E '"(up|turn|mirror|x|z|scale)"'
```

Expected: 送った値がそのまま返る。ゲーム内でキャプチャが転んで、水平に動いている

**`Content-Type: application/json` を外すと通らないこと**も 1 回確かめる（CSRF の防波堤が
生きていること）:

```bash
curl -sS -X POST http://localhost:8777/api/transform -d '{"splat":"x"}' -o /dev/null -w '%{http_code}\n'
```

Expected: 200 以外

- [ ] **Step 5: コミット**

```bash
git add src/VDGS/WebControl.cs src/VDGS/SplatScene.cs src/VDGS/Plugin.cs
git commit -m "Expose horizontal position and orientation over the control API

- /api/transform takes x, z, up, turn and mirror alongside scale and y
- /api/status reports them back, so the page can restore which buttons are pressed
- SetTransform clamps horizontal position the same way it clamps height"
```

---

### Task 10: web — `api.ts` のトランスポート切り替え

**Files:**
- Modify: `web/src/api.ts`
- Create: `web/src/api.test.ts` への追記（既存ファイルがある）
- Modify: `companion-tauri/src-tauri/Cargo.toml`
- Modify: `companion-tauri/src-tauri/src/lib.rs`
- Modify: `companion-tauri/src-tauri/capabilities/default.json`

**Interfaces:**
- Consumes: `bridge.ts` の `hosted`
- Produces: `api.ts` の関数群は呼び口を変えない。`hosted` のとき `http://127.0.0.1:8777` へ Rust 経由で出る

**なぜ Rust 経由か:** webview から `:8777` を直接叩くとクロスオリジンになり、プラグインに
`Access-Control-Allow-Origin` を足すことになる。それは LAN 全体に API を開くのと同じで、
いま意図的に付けていない（`AGENTS.md`「UI のセキュリティ」）。Rust 経由なら webview を
通らないので、その方針を 1 文字も変えずに済む。

- [ ] **Step 1: 失敗するテストを書く**

`web/src/api.test.ts` に足す:

```ts
import { describe, it, expect, vi, beforeEach } from 'vitest'

describe('api transport', () => {
  beforeEach(() => {
    vi.resetModules()
    delete (window as any).__TAURI__
  })

  it('uses a relative fetch when the plugin serves the page', async () => {
    const fetchSpy = vi.fn().mockResolvedValue({ ok: true, json: async () => ({}) })
    vi.stubGlobal('fetch', fetchSpy)
    const { load } = await import('./api')
    await load('my-house')
    expect(fetchSpy.mock.calls[0][0]).toBe('/api/load')
  })

  it('goes through the host to an absolute URL when hosted', async () => {
    const hostFetch = vi.fn().mockResolvedValue({ ok: true, json: async () => ({}) })
    ;(window as any).__TAURI__ = {
      core: { invoke: vi.fn() },
      event: { listen: vi.fn().mockResolvedValue(() => {}) },
      http: { fetch: hostFetch },
    }
    const { load } = await import('./api')
    await load('my-house')
    expect(hostFetch.mock.calls[0][0]).toBe('http://127.0.0.1:8777/api/load')
  })
})
```

- [ ] **Step 2: 落ちることを確認**

Run: `cd web && bun run test api.test.ts`
Expected: FAIL — 2 つめが `/api/load` を受け取る

- [ ] **Step 3: 実装**

`web/src/api.ts` の先頭を差し替える:

```ts
import type { CollisionView, Scene, Status } from './types'

/**
 * Two transports, the same reason bridge.ts has two.
 *
 * Served by the plugin, the page is same-origin and a relative fetch is right. Inside the
 * companion the page is on Tauri's own origin, so the same fetch would be cross-origin -
 * and answering it would mean putting Access-Control-Allow-Origin on a server that is
 * open to the whole LAN. Going out through the host instead never touches the webview,
 * so the plugin's headers stay exactly as they are.
 */
type HostHttp = { fetch: (url: string, init?: RequestInit) => Promise<Response> }
const host: HostHttp | undefined = (
  window as unknown as { __TAURI__?: { http?: HostHttp } }
).__TAURI__?.http

const base = host ? 'http://127.0.0.1:8777' : ''
const call = (url: string, init?: RequestInit): Promise<Response> =>
  host ? host.fetch(base + url, init) : fetch(base + url, init)
```

`post` と `getStatus` の中の `fetch(` を `call(` に置き換える。

`setTransform` を広げる:

```ts
export const setTransform = (
  splat: string,
  v: { scale?: number; y?: number; x?: number; z?: number },
) => {
  const body: Record<string, unknown> = { splat }
  if (v.scale != null) body.scale = v.scale
  if (v.y != null) body.y = v.y
  if (v.x != null) body.x = v.x
  if (v.z != null) body.z = v.z
  return post('/api/transform', body)
}

export const setOrientation = (
  splat: string,
  v: { up?: string; turn?: number; mirror?: boolean },
) => {
  const body: Record<string, unknown> = { splat }
  if (v.up != null) body.up = v.up
  if (v.turn != null) body.turn = v.turn
  if (v.mirror != null) body.mirror = v.mirror
  return post('/api/transform', body)
}
```

`setTransform` の既存の呼び出し（`Control.tsx`）を新しい形に直す。

- [ ] **Step 4: 通ることを確認**

Run: `cd web && bun run test`
Expected: PASS（既存テストも含めて）

- [ ] **Step 5: Rust 側に HTTP プラグインを足す**

`companion-tauri/src-tauri/Cargo.toml`:

```toml
tauri-plugin-http = "2"
```

`companion-tauri/src-tauri/src/lib.rs` の builder に（single-instance の**後**、他のプラグインと
並べて）:

```rust
        .plugin(tauri_plugin_http::init())
```

`companion-tauri/src-tauri/capabilities/default.json`:

```json
{
  "identifier": "default",
  "windows": ["main"],
  "permissions": [
    "core:default",
    "core:event:default",
    "dialog:default",
    {
      "identifier": "http:default",
      "allow": [{ "url": "http://127.0.0.1:8777/*" }]
    }
  ]
}
```

**スコープは loopback のポート 1 つだけ。** ワイルドカードを広げると、この webview が
任意のホストへ出られるようになる。

- [ ] **Step 6: ビルドを確認**

Run: `cd companion-tauri/src-tauri && cargo build`
Expected: 成功

- [ ] **Step 7: コミット**

```bash
git add web/src/api.ts web/src/api.test.ts companion-tauri/src-tauri/Cargo.toml \
        companion-tauri/src-tauri/src/lib.rs companion-tauri/src-tauri/capabilities/default.json
git commit -m "Let the companion talk to the plugin without opening the plugin up

- api.ts picks a transport the way bridge.ts does: relative when served, host when hosted
- Route the hosted case through tauri-plugin-http so the request never leaves the webview
  as a cross-origin one, keeping the plugin free of Access-Control-Allow-Origin
- Scope the capability to http://127.0.0.1:8777/* and nothing else
- Widen setTransform to x and z, add setOrientation"
```

---

### Task 11: web — 3 タブの殻とタブ 01

**Files:**
- Modify: `web/src/CompanionApp.tsx`
- Modify: `web/src/main.tsx`
- Modify: `web/src/pages/Setup.tsx`
- Modify: `web/src/types.ts`
- Modify: `web/src/CompanionApp.test.tsx`

**Interfaces:**
- Consumes: `bridge.ts` の `hosted` / `send` / `subscribe`
- Produces: `CompanionApp` が `'setup' | 'tracks' | 'own'` の 3 タブを持つ。`hosted === false` の
  とき `setup` と `tracks` を出さず `own` を初期タブにする

- [ ] **Step 1: 失敗するテストを書く**

`web/src/CompanionApp.test.tsx` に足す:

```tsx
it('shows three tabs when hosted', async () => {
  ;(window as any).__TAURI__ = {
    core: { invoke: vi.fn() },
    event: { listen: vi.fn().mockResolvedValue(() => {}) },
  }
  const { default: CompanionApp } = await import('./CompanionApp')
  render(<CompanionApp />)
  expect(screen.getByRole('tab', { name: /setup/i })).toBeInTheDocument()
  expect(screen.getByRole('tab', { name: /tracks/i })).toBeInTheDocument()
  expect(screen.getByRole('tab', { name: /create your own/i })).toBeInTheDocument()
  delete (window as any).__TAURI__
})

// Opened in a browser there is no host to pick a folder or download anything, so the two
// tabs that do only that would be a wall of dead buttons.
it('shows only create-your-own in a plain browser', async () => {
  delete (window as any).__TAURI__
  const { default: CompanionApp } = await import('./CompanionApp')
  render(<CompanionApp />)
  expect(screen.queryByRole('tab', { name: /setup/i })).toBeNull()
  expect(screen.queryByRole('tab', { name: /tracks/i })).toBeNull()
  expect(screen.getByRole('tab', { name: /create your own/i })).toBeInTheDocument()
})
```

- [ ] **Step 2: 落ちることを確認**

Run: `cd web && bun run test CompanionApp`
Expected: FAIL — `tracks` タブが無い

- [ ] **Step 3: 殻を書く**

`web/src/CompanionApp.tsx` の `tab` 状態を 3 値に広げ、`hosted` で絞る:

```tsx
import { hosted, send, subscribe } from './bridge'

type TabId = 'setup' | 'tracks' | 'own'

const ALL: { id: TabId; label: string }[] = [
  { id: 'setup', label: '01 setup' },
  { id: 'tracks', label: '02 tracks' },
  { id: 'own', label: '03 create your own' },
]

// Without a host there is no folder picker and no downloader, so the first two tabs
// could only show buttons that do nothing. What is left is the half the plugin serves.
const TABS = hosted ? ALL : ALL.filter((t) => t.id === 'own')

export default function CompanionApp() {
  const [tab, setTab] = useState<TabId>(TABS[0].id)
  // ...既存の state / log / subscribe はそのまま
```

`Tab` コンポーネントに `role="tab"` を付ける（テストがロールで引くため）。

**同じ手で `bridge.ts` を広げる。** タブ 02 と 03 が新しいコマンドを送るので、ここで型を
通しておかないと次のタスクが型検査で落ちる:

```ts
export type Command =
  | 'refresh'
  | 'pick'
  | 'installMod'
  | 'uninstallMod'
  | 'installPly'
  | 'removeTrack'
  | 'removeCapture'
  | 'unbindTrack'
  | 'createTrack'
  | 'refreshCatalog'
  | 'get'
  | 'addTrack'
  | 'fly'

export function send(cmd: Command, id?: string, arg?: Record<string, unknown>): void {
  if (!tauri) return devSend(cmd, id)
  const invoke = () =>
    tauri.core.invoke('dispatch', { cmd, id: id ?? null, arg: arg ?? null })
  void (listening ? listening.then(invoke, invoke) : invoke())
}
```

`devState` に `lanUrl` と、`unbound` の項目を 1 つ足す（ブラウザで `bun run dev` したときに
タブ 03 が空にならないため）。

`FLY` ボタンは殻の下端に置き、タブに依らず常駐させる。`hosted === false` のときは出さない。

`web/src/main.tsx`（`index.html` の入口）を `CompanionApp` を描くだけに置き換える。
`BrowserRouter` と `App.tsx` のルーター殻は消える。

- [ ] **Step 4: 通ることを確認**

Run: `cd web && bun run test`
Expected: PASS

- [ ] **Step 5: タブ 01 から不要な節を外す**

`web/src/pages/Setup.tsx` から §02「tracks」ブロックと `Add track` / `Install capture` の
ボタン行を削除する。残すのは §01（ゲームパス・mod 導入/削除）と True Lens 警告。

`web/src/types.ts` の `SetupState` に `lanUrl: string | null` を足す。

- [ ] **Step 6: ビルドと型を確認**

Run: `cd web && bun run build && bun run test`
Expected: PASS。`Setup.tsx` から消した節を参照している箇所が残っていればここで落ちる

- [ ] **Step 7: コミット**

```bash
git add web/src/CompanionApp.tsx web/src/main.tsx web/src/pages/Setup.tsx \
        web/src/types.ts web/src/bridge.ts web/src/CompanionApp.test.tsx
git commit -m "Put the companion and the control UI in one three-tab shell

- index.html and companion.html now render the same component tree
- Hide setup and tracks without a host: both do nothing but pick folders and download
- Move the track list out of setup; FLY becomes a fixture of the shell
- Drop the router; the app is a window with tabs, not a site"
```

---

### Task 12: web — タブ 02（配布中と導入済みを 1 本の表に）

**Files:**
- Create: `web/src/pages/Tracks.tsx`
- Create: `web/src/pages/Tracks.test.tsx`
- Delete: `web/src/pages/Get.tsx`, `web/src/pages/Get.test.tsx`
- Modify: `web/src/CompanionApp.tsx`

**Interfaces:**
- Consumes: `SetupState.tracks`（`TrackEntry[]`）、`SetupState.catalog`、`send`
- Produces: `Tracks` — 1 本の表。行ごとに `Get` / `Remove` / `Unbind` を出し分ける

- [ ] **Step 1: 失敗するテストを書く**

`web/src/pages/Tracks.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react'
import { describe, it, expect, vi } from 'vitest'
import Tracks from './Tracks'
import type { SetupState } from '../types'

const state = (over: Partial<SetupState>): SetupState =>
  ({
    game: '/games/velocidrone', mod: '0.1.0.0', bundledMod: '0.1.0.0', missing: [],
    ready: true, running: false, busy: null, busyPercent: null, launchArgs: '',
    lanUrl: null, tracks: [], catalog: null, unbound: [], trueLens: null, ...over,
  }) as SetupState

const track = (over: object) => ({
  track: 'VDGS FDF', capture: 'fdf', splats: 10, bytes: 1, collision: true,
  captureInstalled: true, converted: true, inGame: true, fromServer: false, ...over,
})

describe('the merged track table', () => {
  it('offers Get for a catalog entry that is not installed', () => {
    render(<Tracks state={state({
      catalog: { url: 'u', error: null, entries: [
        { id: 'a', name: 'Nelson', description: null, author: null, licence: null,
          splats: 1, bytes: 1, installed: false },
      ] },
    })} busy={false} />)
    expect(screen.getByRole('button', { name: /get/i })).toBeInTheDocument()
  })

  it('offers Remove for a track this machine owns', () => {
    render(<Tracks state={state({ tracks: [track({})] })} busy={false} />)
    expect(screen.getByRole('button', { name: /remove/i })).toBeInTheDocument()
  })

  // A track downloaded from the official server belongs to its author. The binding is
  // ours to drop; the track is not ours to delete.
  it('offers only Unbind for a track from the official server', () => {
    render(<Tracks state={state({ tracks: [track({ fromServer: true })] })} busy={false} />)
    expect(screen.getByRole('button', { name: /unbind/i })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /remove/i })).toBeNull()
  })

  it('offers Get again when the track is there but its capture is missing', () => {
    render(<Tracks state={state({ tracks: [track({ captureInstalled: false })] })} busy={false} />)
    expect(screen.getByRole('button', { name: /get/i })).toBeInTheDocument()
  })
})
```

- [ ] **Step 2: 落ちることを確認**

Run: `cd web && bun run test Tracks`
Expected: FAIL — `./Tracks` が無い

- [ ] **Step 3: 実装**

`web/src/pages/Tracks.tsx` を書く。`Get.tsx` のカタログ行と `Setup.tsx` の §02 を 1 本の
リストに畳む。**動的な値は React のテキストとして描く** — `dangerouslySetInnerHTML` は使わない。
トラック名は攻撃者が書ける文字列（コミュニティのトラックをダウンロードできる）。

行の出し分け:

```tsx
function Actions({ row, busy }: { row: Row; busy: boolean }) {
  if (row.kind === 'catalog') {
    return <Button disabled={busy} onClick={() => send('get', row.id)}>Get</Button>
  }
  if (!row.captureInstalled && row.catalogId) {
    return <Button disabled={busy} onClick={() => send('get', row.catalogId)}>Get</Button>
  }
  if (row.fromServer) {
    return (
      <Button variant="outline" disabled={busy}
              onClick={() => send('unbindTrack', row.track)}>Unbind</Button>
    )
  }
  return (
    <Button variant="outline" disabled={busy}
            onClick={() => send('removeTrack', row.track)}>Remove</Button>
  )
}
```

検索欄は `Library.tsx` の実装をそのまま持ってくる（`web/src/search.ts` が既にある）。
`Add track` ボタンをこのページの下端に置く。

- [ ] **Step 4: 通ることを確認**

Run: `cd web && bun run test`
Expected: PASS

- [ ] **Step 5: `Get.tsx` を消す**

```bash
git rm web/src/pages/Get.tsx web/src/pages/Get.test.tsx
```

`CompanionApp.tsx` の import を差し替える。

- [ ] **Step 6: ビルドを確認**

Run: `cd web && bun run build && bun run test`
Expected: PASS

- [ ] **Step 7: コミット**

```bash
git add web/src/pages/Tracks.tsx web/src/pages/Tracks.test.tsx web/src/CompanionApp.tsx
git commit -m "Fold the catalog and the installed tracks into one table

- A player counts tracks, not captures; a catalog entry is a track plus its capture
- One row per track, with the action following the row's state
- A track from the official server can be unbound but never deleted
- Remove the separate bindings list: this table already carries the capture column"
```

---

### Task 13: web — タブ 03（入れる → 作る → 合わせる）

**Files:**
- Create: `web/src/pages/Own.tsx`
- Create: `web/src/pages/Own.test.tsx`
- Modify: `web/src/pages/Control.tsx`（③ の中身として作り直す）
- Delete: `web/src/pages/Library.tsx`, `web/src/pages/Library.test.tsx`, `web/src/App.tsx`
- Modify: `web/src/CompanionApp.tsx`, `web/package.json`

**Interfaces:**
- Consumes: `SetupState`（`running` / `unbound` / `lanUrl`）、`useStatus()`、`api.setTransform` /
  `api.setOrientation` / `api.load` / `api.setBackdrop` / `api.setCollision` / `api.setCollisionView`
- Produces: `Own` — ① 入れる ② トラックを作る ③ 合わせる

- [ ] **Step 1: QR のライブラリを足す**

```bash
cd web && bun add qrcode && bun add -d @types/qrcode
```

- [ ] **Step 2: 失敗するテストを書く**

`web/src/pages/Own.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react'
import { describe, it, expect } from 'vitest'
import Own from './Own'
import type { SetupState } from '../types'

const state = (over: Partial<SetupState>): SetupState =>
  ({
    game: '/games/velocidrone', mod: '0.1.0.0', bundledMod: '0.1.0.0', missing: [],
    ready: true, running: false, busy: null, busyPercent: null, launchArgs: '',
    lanUrl: 'http://192.168.1.42:8777/', tracks: [], catalog: null,
    unbound: [], trueLens: null, ...over,
  }) as SetupState

describe('create your own', () => {
  // The two halves need opposite conditions: putting files down and writing the track
  // database need the game closed, and tuning needs it open. Whichever way round it is,
  // exactly one half is live and the other says why not.
  it('lets you add a capture while the game is closed', () => {
    render(<Own state={state({ running: false })} busy={false} />)
    expect(screen.getByRole('button', { name: /add a \.ply/i })).toBeEnabled()
  })

  it('refuses to add a capture while the game is running, and says why', () => {
    render(<Own state={state({ running: true })} busy={false} />)
    expect(screen.getByRole('button', { name: /add a \.ply/i })).toBeDisabled()
    expect(screen.getByText(/close velocidrone/i)).toBeInTheDocument()
  })

  it('sends the track name and the capture together', async () => {
    const user = userEvent.setup()
    render(<Own state={state({
      running: false,
      unbound: [{ name: 'my-house', splats: 1, collision: false, bytes: 1 }],
    })} busy={false} />)
    const field = screen.getByLabelText(/track name/i) as HTMLInputElement
    // The default is derived from the capture, so the common path is one click.
    expect(field.value).toBe('VDGS my-house')
    await user.click(screen.getByRole('button', { name: /create track/i }))
    expect(sent).toEqual(['createTrack', { name: 'VDGS my-house', capture: 'my-house' }])
  })

  it('offers the LAN address only where the tuning controls are', () => {
    render(<Own state={state({ running: true })} busy={false} />)
    expect(screen.getByText('http://192.168.1.42:8777/')).toBeInTheDocument()
  })
})
```

`sent` は `vi.mock('../bridge', ...)` で `send` を捕まえる。`userEvent` と `vi` の import を
`Tracks.test.tsx` と同じ形で足す。

- [ ] **Step 3: 落ちることを確認**

Run: `cd web && bun run test Own`
Expected: FAIL — `./Own` が無い

- [ ] **Step 4: 実装**

`web/src/pages/Own.tsx`:

```tsx
/**
 * Three steps in the order they have to happen, and the game's state decides which of
 * them is live.
 *
 * Putting a file into the game folder and writing a row into user11.db both need the game
 * closed. Tuning talks to the plugin's HTTP server, which does not exist unless the game
 * is open. So this page is never all-enabled, and the half that is off says which way to
 * go rather than looking broken.
 */
export default function Own({ state, busy }: { state: SetupState | null; busy: boolean }) {
  const running = !!state?.running
  const prepare = !running && !busy && !!state?.game
  // ...
}
```

- ① `Add a .ply` ボタン → `send('installPly')`。`disabled={!prepare}`。稼働中は
  `close VelociDrone first` を添える
- ② キャプチャの選択（`state.unbound` から）＋ `Track name` 入力欄。既定は
  `VDGS ${capture}`。`Create track` → `send('createTrack', undefined, { name, capture })`。
  `disabled={!prepare}`
- ③ `running` のときだけ有効。中身は `Control.tsx` の §02「on screen」＋ `Library.tsx` の
  `Show`。つまみは **Up（6 ボタン）/ Mirror（トグル）/ Turn（0〜360 のスライダー）/ Scale /
  X / Z / Height**
- **Mirror は変換済みキャプチャでは出さない。** `SplatData.Load` は packed buffer を読むだけで
  鏡映できない（Task 7 Step 2）。`scene.kind === 'ply'` のときだけ描く
- ③ の真横に `state.lanUrl` と QR。`lanUrl` が `null` なら節ごと出さない

`send` の 3 引数化は Task 11 で済んでいる。

- [ ] **Step 5: 通ることを確認**

Run: `cd web && bun run test`
Expected: PASS

- [ ] **Step 6: 古い殻を消す**

```bash
git rm web/src/pages/Library.tsx web/src/pages/Library.test.tsx web/src/App.tsx
```

`Control.tsx` は §01「current track」と §03「bindings」を落とし、③ の中身として残す。
`Unload` は消す — トラックを切り替えれば入れ替わる。

- [ ] **Step 7: ビルドを確認**

Run: `cd web && bun run build && bun run test`
Expected: PASS

- [ ] **Step 8: 通しで確認**

```bash
cd companion-tauri && bun run tauri dev
```

1. ゲームを閉じた状態で ① と ② が有効、③ が `FLY して合わせる` と出る
2. `.ply` を 1 つ入れて、名前が `VDGS <名前>` で埋まる
3. `Create track` → ログに `added track` と `bound` が出る
4. `FLY` → ゲームでそのトラックを開き、キャプチャが出る
5. ③ の Up / Turn / Mirror / X / Z を動かし、**ゲーム内で即座に反映される**
6. LAN の URL を別の端末のブラウザで開き、**タブ 01 と 02 が出ず ③ が動く**

- [ ] **Step 9: コミット**

```bash
git add web/src/pages/Own.tsx web/src/pages/Own.test.tsx web/src/pages/Control.tsx \
        web/src/CompanionApp.tsx web/src/bridge.ts web/package.json
git commit -m "Add the create-your-own tab: put a capture in, make a track, tune it

- Three steps in the order they must happen, gated on whether the game is running
- Default the track name from the capture, so the common path is one click
- Bind at creation time: a track named now and bound later is a rename waiting to break
- Show the LAN address beside the tuning controls, the one moment a second screen helps
- Hide Mirror for converted captures; the flip happens while parsing a .ply"
```

---

### Task 14: ドキュメントの更新

**Files:**
- Modify: `AGENTS.md`
- Modify: `docs/TRACKS.ja.md`, `docs/TRACKS.md`
- Modify: `docs/USAGE.ja.md`, `docs/USAGE.md`

- [ ] **Step 1: `AGENTS.md` を直す**

- 「配布は companion アプリ」節に 3 タブの構成を書く
- 「操作は Web UI（ゲーム内キーではない）」を、**companion がその UI を内蔵していて、
  ブラウザは同じアプリの縮小版**という形に書き直す
- `bindings.json` が外から書かれても 1 秒で効くようになったことを書く
- `placement.json` に `up` / `turn` / `mirrorY` が増えたことを「データの形」に足す
- **踏んだ罠を残す**：`TrackBindings.Load()` がコンストラクタでしか呼ばれず、companion の
  書き込みが黙って上書きされていたこと

- [ ] **Step 2: `docs/TRACKS.ja.md` を書き直す**

7 段のうち 5 段が companion の中に移る。**`--export-track --list` でシーナリーを選ぶ節は
丸ごと消える**（種が `scene_id` を持っているため）。残るのは「組む」「コリジョンを焼く」
「書き出す」「配る」。

- [ ] **Step 3: `docs/USAGE.ja.md` を直す**

タブ 03 の ③ が置き位置の調整先になったことを書き、ブラウザ UI は「別の画面から操作したい
とき」の位置づけにする。

- [ ] **Step 4: 英語版を揃える**

`docs/TRACKS.md` と `docs/USAGE.md` を対応させる。

- [ ] **Step 5: 太字の検査を通す**

Run: `python3 ~/.claude/scripts/md_emphasis_check.py AGENTS.md docs/TRACKS.ja.md docs/USAGE.ja.md`
Expected: `TOTAL broken: 0`

- [ ] **Step 6: コミット**

```bash
git add AGENTS.md docs/TRACKS.ja.md docs/TRACKS.md docs/USAGE.ja.md docs/USAGE.md
git commit -m "Document the three-tab companion

- Describe the tabs and which of them a plain browser gets
- Drop the scenery-picking step from the track guide: the seed carries scene_id
- Record that TrackBindings only ever loaded from its constructor, so a companion write
  was silently overwritten by the next save"
```

---

## 通しの検証

全タスク完了後、実機で 1 回だけ通す。

| 確認 | 期待 |
|---|---|
| まっさらな機械で導入 → `.ply` を入れる → トラックを作る → FLY | キャプチャが出る |
| ゲーム稼働中にタブ 03 を開く | ① ② が理由付きで無効、③ が有効 |
| 別端末のブラウザで LAN の URL | タブ 03 の ③ だけが出て動く |
| ゲーム稼働中に companion からトラックを Unbind | 1 秒以内にキャプチャが消える |
| Mirror を反転 | splat とコリジョン殻が**いっしょに**反転する |
| `curl -X POST /api/transform` を `Content-Type` 無しで | 200 以外（CSRF の防波堤が生きている） |
| `cargo test` / `dotnet test` / `bun run test` | 全部 PASS |
