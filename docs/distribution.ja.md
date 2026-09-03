# 配布は companion アプリ（`companion-tauri/`）

*[English](distribution.md)*

**mod をどうやって人に届けるか。** companion アプリの中身、リリースを組んで上げる通し、
置き場所の Cloudflare 構成、そして通し確認に使える実測値。**日常の開発では要らない** —
配るとき、あるいは配ったものが壊れたときに読む。

キャプチャの入れ方は [SCENES.ja.md](SCENES.ja.md)、コースの作り方は
[TRACKS.ja.md](TRACKS.ja.md)、利用者側の手順は [USAGE.ja.md](USAGE.ja.md)。
**何を再配布していいか**は [AGENTS.md](../AGENTS.md) の
「splat データは配布できない。同梱もしない」で決まる — 置き場所を変えても
再配布であることは変わらない。

---

**mod を配る道具で、人がやることは 4 クリックだけ。** BepInEx の取得、mod の導入・削除、
キャプチャのダウンロードと導入、トラックの DB 登録と紐付け、`-force-d3d12` 付きの起動 —
全部これで済む。**Tauri 2 + Rust の 1 本**で、中身は `web/` の React を描いている
（`companion.html`）。操作 UI と同じテーマ・同じコンポーネント・同じフォントで、
**別製品に見せない**ため。

```
companion-tauri/src-tauri/src/
  lib.rs       ウィンドウ、dispatch、重い処理の別スレッド化
  cli.rs       窓を開かない 2 つ（--export-track / --check-catalog）
  game.rs      ゲーム発見・走査、mod の導入/削除、zip 展開、bindings 書き込み
  bepinex.rs   ローダーの取得（版と digest を固定）
  tracks.rs    user11.db の読み書き
  catalog.rs   公開カタログの取得・検証・ダウンロード
  settings.rs  ゲームパスとカタログ URL を記憶（Windows は %LOCALAPPDATA%）
  launch.rs    ゲームの起動と生存確認
  state.rs     ページに送る状態の組み立て
```

テストは各ファイルの末尾の `#[cfg(test)] mod tests` にある（`cargo test`）。ページ側は
`web/` の `bun run test`。**C# 時代の `companion/tests/` は削除済み**で、そこにしか無かった
検査が何本か残っている（下の「残っている宿題」）。

**かつて Windows は別実装だった**（`companion/`、.NET Framework 4.8 + WinForms + WebView2）。
同じ 10 コマンドを 2 回書いて 1 対 1 で保つ期間があり、いまは終わって削除済み。
`bridge.ts` の `chrome.webview` 経路もそれと一緒に消えた。

**payload は毎回空にしてから詰める。** これは C# 時代からの規則で、理由も同じ —— web の資産は
ビルドごとに名前が変わるので、**入れ直すたびに前回のぶんが隣に残る**。2026-09-01 に数えたら
payload が 23 本、元は 5 本だった。全部 zip に入り、利用者の `vdgs/ui` にも配られていた。
`index.html` は現行の 2 本しか名指ししないので**何も壊れず**、5 リリース気づかれなかった。
いまは `make-win-app.sh` / `make-mac-app.sh` が `resources/mod` を `rm -rf` してから組む。

**mod はアプリの中に同梱する**（`resources/mod`、リリーススクリプトが組んだ木をビルド時に
バンドル）。ボタンは自分の仕事を名乗る — `INSTALL MOD` / `REINSTALL MOD` /
`UPDATE TO <版>` / `NO MOD PAYLOAD`。**押してみないと分からない、をやらせない**ため。

**BepInEx も自分で取ってくる**（無いときだけ）。同梱ではなく本家リリースから、URL・サイズ・
sha256 を固定して。**「先に BepInEx を入れて」は全インストールの第 1 手で、いちばん間違えやすい手順だった。**
1 階層深く展開すると、**ゲームは起動して、何も起きない**。

**トラックは行ごと消せる**（各行にホバーで出る `REMOVE`）。消えるのは `user11.db` の行と
`bindings.json` の項目だけで、**キャプチャは残る** — GB 単位で、消す理由が無い。
**公式サーバー由来のトラックは削除しない**（ボタンは `UNBIND` になる）。作者のものであって、
こちらが人の機械から消していいものではない。DB は削除前にコピーする（**ラップタイムは
再取得不能**）。

**UNINSTALL もキャプチャを消さない。** 消すのは `VDGS.dll` / `vdgs-shaders` / `vdgs/ui` だけ。
キャプチャは GB 単位で再取得に数時間かかるし、`bindings.json` と `placement.json` を残せば
**入れ直したとき元の場所に戻る**。BepInEx も残す（こちらのものではない）。

**ゲームパスは `%LOCALAPPDATA%` に覚える。** 見つからなければ固定ディスクを走査する
（深さ制限つき、`Windows` 等は飛ばす、ジャンクションは辿らない）。**PatchKit は
インストール先をレジストリにも自前フォルダにも残さない**（`%LOCALAPPDATA%\PatchKit` は
32 バイトの `sender_id` 1 個だけ）ので、走査か「自分で探して」しかない。

## 踏むと高い罠

（WebView2 ホストがファイルを掴んで配備が半分だけ通る話と、`BaseDirectory` の `ui/` を
`file://` で読めない話は、C# 版と一緒に消えた。どちらも WinForms ホスト固有だった。）

- **`scp host:relative` が exit 0 のまま何も転送しないことがある。** `-v` を見ると
  `Executing: cp --` ＝ **ローカルコピーだと判定されている**（ホスト名が 1 文字だと
  ドライブレターに見える）。`scp host:/C:/Users/<you>/name` と**絶対パスにする**
- **`ssh host '...'` の中身は PowerShell に 2 回読まれる。** 向こうの既定シェルが
  PowerShell なので、`powershell -Command "..."` に届く前に**外側の PowerShell が
  `$` を展開してしまう**。`$env:USERPROFILE` は外側にも値があるので通るが、
  **`$_` は外側で空**になり、`ForEach-Object { $_.Name }` が
  `ForEach-Object { .Name }` になって `.Name is not recognized` で落ちる。
  一行で済ませたいなら `Select-Object -ExpandProperty Name` のように `$_` を避ける。
  **確実なのは `.ps1` を `scp` して `-File` で走らせること** — 引用の段が 1 つ減る
- **GUI サブシステムの exe は PowerShell が待たない。** `& $exe args` は即座に戻るので、
  出力もファイルも「無かった」ことになる。`Start-Process -Wait -PassThru`、
  かつ **`-ArgumentList` は空白を含む引数を勝手に引用しない**ので自分で `'"..."'` にする
- **`Get-Process VDGS` は配列を返しうる。** 2 つ動いていると `AppActivate($p.Id)` が
  `DISP_E_TYPEMISMATCH` で落ちる。`Select-Object -First 1` を挟む
- **`.gitignore` に `web/` があると `web/` 配下の新規ファイルが `git add` で消える。**
  React 化前の名残。マージで一度復活し、コミットが自分のソースを半分置き去りにしかけた
- **macOS では `site.tsx` と `Site.tsx` が同じファイル。** 両方書くと後から書いた方だけが
  残る。エントリを消したのに **tsc も build もページのテストも全部通った**（コンポーネントを
  直接描くテストしか無かったため）。**各エントリが実際にマウントすることを固定してある**
  （`web/src/entries.test.tsx`）
- **state を作るのは重い。** 全キャプチャを歩いて `.ply` ヘッダを読んで SQLite を開く。
  「作業中」の表示をこれで送っていたら、**歩き終わるまで無言**になった（進捗表示が
  直そうとしていたもの、そのもの）。進捗と busy は**専用の軽いメッセージ**で送る。
  各 state は生成にかかった `stateMs` を持って帰るので、次に遅くなったら実測で分かる
- **テスト project に `RuntimeIdentifier` を付けない。** mono が Windows 版の
  `e_sqlite3.dll` を掴んで落ちる。RID が要るのはアプリ本体だけ

## 配布の通し

**キャプチャは mod に同梱しない**（数百 MB）。アプリの `02 GET` からカタログ経由で落とす。
**4 手順（トラック書き出し → 固める → カタログ → 上げる）は catalog/README.md。**
サイズと digest を実物から測る理由は docs/TRACKS.ja.md。

アプリ側の防具は 3 つ。

- https 以外を拒否する（loopback だけ例外）
- digest が合わなければ展開しない
- 知らない `formatVersion` は読まない

**欠けたフィールドを「トラック無し」と誤読して、公開物の半分だけ入れるのが最悪**なので。

**カタログの `app` は OS ごとに 1 つ持つ** — `{"windows": {...}, "macos": {...}}`。あるものだけ
入り、無い OS はボタンが出ないだけで壊れない。**Windows の companion はこの欄を読まない**
（`scenes` しか見ない）ので、形を変えても既存のアプリは壊れなかった。読むのはサイトだけで、
サイトはカタログと一緒に deploy される。

**Windows の companion は Tauri になった。焼くのは `tools/make-win-app.sh`。**
`make-release.sh` がこれを呼ぶ（`VDGS_WIN_BUILD_HOST` が設定されているときだけ。無ければ
**黙って飛ばさず**、古い zip がカタログに載ると言って続行する）。macOS 側の
`make-mac-app.sh` と対になる形で、**1 ターゲットにつき 1 台**：

| 変数 | どの機械 | 何をする |
|---|---|---|
| `VDGS_HOST` | ゲーム機 | VelociDrone と Unity。D3D12 シェーダーを焼く。**Rust は無い** |
| `VDGS_WIN_BUILD_HOST` | ビルド機 | Rust（`x86_64-pc-windows-msvc`）+ VS のリンカ + `cargo-tauri` |

出すのは**素の exe を zip**（`--no-bundle`）で、`VDGS.exe` と `resources/` の 2 つ。名前も
形も C# 版と同じなので、`make-catalog.sh` から下は何も変わらない。**`resources/` を入れ忘れると
アプリは開いて「mod ペイロードを持っていない」と言う。** NSIS インストーラも焼けるが、
未署名では SmartScreen が同じように出るので今は得が無い。

**踏むと高くつく罠が 4 つあり、全部スクリプトに埋めてある：**

- **ペイロードは毎回 `rm -rf` してから組む。** `resources/mod` は macOS ビルドと**共有**して
  いて、`make-mac-app.sh` は Metal 版のバンドルをそこに置く。残っていると
  **エラーを 1 行も出さずに死んだシェーダーを配る**。最後の砦がサイズ検査（1MB 未満で停止。
  D3D12 は約 1.5MB、Metal は約 437KB）
- **`beforeBuildCommand` を空にして送る。** ビルド機に bun も node も要らなくなる。理由は
  綺麗さではない —— **`cmd.exe` の PATH 上限 8191 文字**で子プロセスの PATH が切り詰められ、
  `bun` が見つからなくなる。症状は「ツールが入っていない」に見える。同じ理由で、
  ビルド時の PATH も**短いものを自分で組んで渡す**
- **`COPYFILE_DISABLE=1` で tar する。** macOS の tar が `._*` を作り、Tauri が
  `capabilities\._default.json` を「不正な UTF-8」で拒否して止まる。**誰も書いていない
  ファイルでビルドが落ちる**
- **持ち帰った zip を開いて検算する。** 上の全部が成功しても zip が間違っていることはある
  （古い exe、macOS 向けのペイロード）。`VDGS.exe`・プラグイン・**staging したのと同じ
  バイト数の**シェーダーバンドルがあることを確かめてから終わる

`make-catalog.sh` は `build/release` から拾う：Windows は `vdgs-companion-*.zip`、macOS は
`VDGS-Companion-*-macos.dmg`。どちらも**名前順ではなく mtime で最新**を選ぶ（`2026.09.01.1`
は名前順だと `2026.09.01` より古く見える）。`publish.sh` はカタログが名指しした**全部**を
上げる — 1 つでも漏らすと 404 するボタンを公開することになる。

**版はリリースの日付で、`make-release.sh` と `make-mac-app.sh` が同じ既定を持つ**
（引数無しなら `date +%Y.%m.%d`）。`src/VDGS/VDGS.csproj` の `<Version>0.1.0</Version>` は
置き場所で、そのまま出るのは **dev ビルド**。companion の mod ボタンは導入済みの版と同梱の版を
比べるので、**全ビルドが同じ 0.1.0.0 を名乗っている間は「Reinstall mod」しか出せない**。

**既定が片方だけ csproj を見ていた時期があり、同じ日に両方焼くと `0.1.0` の DMG が
`2026.09.03` の zip の隣に並んだ。** 揃えてある。

**macOS の bundle に刻む版は SemVer に写す必要がある**（`tools/calver_to_semver.py`）。
Tauri は `tauri.conf.json` の `version` を SemVer として読み、**先頭ゼロを拒否する** —
`2026.09.03` は `must be a semver string` で**ビルドごと落ちる**。ゼロを落とし、4 つ目は
build metadata にする：

```
2026.09.03   -> 2026.9.3
2026.09.01.3 -> 2026.9.1+3
```

**4 つ目を捨ててはいけない。** `2026.09.01` と `2026.09.01.3` が両方 `2026.9.1` を名乗り、
**別物が同じ版を主張する**。Apple の `CFBundleShortVersionString` は仕様上「整数 3 つ」だが、
**`+3` 付きで公証・staple・Gatekeeper すべて通ることは実測済み**。

**カタログ側にバージョン要件は置かない。** 要るのは古い mod では描けないキャプチャを
実際に公開したときで、そのときに設計する（[#4](https://github.com/Saqoosha/VDGS/issues/4)）。

**公開直後の `catalog.json` は数分間キャッシュが返る。** 素で叩くと前の版が出るので、
**上げ損ねたように見える**。確認するときは `?cb=$RANDOM` を付ける。アプリは起動ごとに
取り直すので実害は薄いが、**「反映されていない」という誤診の材料**にはなる。

**`publish.sh` は R2 に上げてから deploy する。**
逆にすると、**まだ無いファイルを指すリストを必ず一度公開する**ことになる。

**上げるのは `rclone`。`wrangler` では 300 MiB を超えると必ず落ちる**
（`Wrangler only supports uploading files up to 300 MiB in size`、multipart のオプションが
無い）。FDF 2026-08-22 は 375 MB でここに当たった。鍵は 1Password の `VDGS` 環境を
`~/.claude/1p-mounts/vdgs.env` にマウントして読む（`VDGS_R2_ENV` で上書き可）。

**`--s3-no-check-bucket` は必須。** トークンは `vdgs` バケットだけの Object Read & Write に
絞ってあり、`rclone` は既定でバケットの存在を `CreateBucket` で確かめようとして 403 になる。
**直すのは呼ぶ側で、トークンを広げるほうではない。**

**同じ名前で中身を差し替えない。** 公開ファイルは `immutable, max-age=31536000` で配るので、
上書きするとエッジは古い中身を 1 年出し続け、カタログは新しい digest を名乗る。
**ダウンロードは完走して、そのあと展開が拒否される。** 版を上げて別名にする。
**`publish.sh` が実際に止めてくれる** — 「published with a different sha256 / Those names are
spent」。二度やりかけて二度とも止まった（`vdgs-companion-2026.09.01.zip`、
`VDGS-Companion-2026.09.03-macos.dmg`）ので、**この防具は効いている**。

**見張りは digest で、サイズではない。** `publish.sh` は各オブジェクトに sha256 を user
metadata として刻み、次回はそれと突き合わせる。同じ日の companion 3 版が
6,607,301 / 6,607,540 / 6,607,546 バイトだったように、**別の中身が同じ長さになるのは普通**。

**`rclone` は `--metadata-set` だけではメタデータを書かない。`-M` が要る。** フラグは受理
されて何も起きず、次回の実行は比較材料が無いまま長さに落ちる。実測で判明した。

**`make-catalog.sh` は companion を mtime で選ぶ。** 名前順だと `2026.09.01.1` が
`2026.09.01` より前に並ぶ（`1` < `z`）ので、同じ日の 2 回目のビルドが 1 回目に負ける。

**公開していいのはライセンスが再配布を許すものだけ。** 表記の不在は許諾ではない。
判断は「splat データは配布できない。同梱もしない」節。

## ホスティング（`worker/`）

**https://vdgs.saqoo.sh/ — Cloudflare Worker 1 本。**

| パス | 出どころ |
|---|---|
| `/`, `/assets/*`, `/catalog.json` | 静的アセット（`build/release/site`） |
| `/scene/*`, `/track/*`, `/app/*` | R2 バケット `vdgs`（`build/release/files`） |

**分けている理由はサイズだけ** — デプロイは 1 ファイル 25 MiB 上限、キャプチャは数百 MB。
**オリジンは 1 つ**にしてあるので、カタログの URL とページのリンクが食い違いようがない。
R2 は Range 対応で streaming（回線が切れても再開できる）、`immutable` で長期キャッシュ
（公開ファイルは同じ名前で中身が変わらない）。

- **`wrangler` は `a@saqoo.sh` で認証済み**（`~/.wrangler/config/default.toml`）。
  `npx wrangler` でそのまま使える。**ダッシュボードは要らない** — カスタムドメインは
  `wrangler.jsonc` の `routes` に `custom_domain: true` で付く
- **saqoo.sh のゾーンは `User-Agent: Python-urllib/*` を 403 で弾く。** curl も
  companion（`VDGSCompanion`）も 200。スクリプトから叩くときは UA を変える

## 検証で使える基準値

| 測るもの | 値 |
|---|---|
| まっさらなゲームフォルダに `INSTALL MOD` | 133 ファイル / 5.2 MB、約 1 秒 |
| `vdgs-shaders` | 1,538,627 バイト（1MB 未満は失敗版） |
| companion の zip | 約 6.0 MB |
| キャプチャのダウンロード | コールド 54 秒、エッジキャッシュ後 5 秒（134 MB 時の実測） |

配備中の 2 シーン（どちらも実機で飛行確認済み。掃除とコリジョンの設定は docs/cleanup.ja.md）：

| | splats | サイズ | コリジョン |
|---|---|---|---|
| `JDL-2026-R5-airvis` | 2,521,003 | 212 MB | 1,597,643 三角形 / 27 MB / 辺 0.137 m |
| `FDF-2026-08-22` | 4,508,391 | 362 MB | 2,197,134 三角形 / 39 MB / 辺 0.213 m |

`FDF-2026-08-22` の配布 zip は 375,693,617 バイト
（sha256 `cecf661690560e42887422c794b4d81693201f188f3248b7b5d2ab18984eddb2`）。

**この値は手元の zip ではなく公開カタログから引く。** 同じ splat を固め直すと 11 バイト
違う別ファイルになり、**中身が同じでも digest は一致しない**。手元のビルドを基準値として
書くと、公開物と照合したときに必ず外れる（実際に一度そうなっていた）。
`curl -A VDGSCompanion https://vdgs.saqoo.sh/catalog.json` が正。

**GUI は実機でしか確かめられない。** セッション 0 には窓が無いので、起動も撮影も
クリックもスケジュールタスク（`New-ScheduledTaskPrincipal -LogonType Interactive`）経由。
`tools/appstart-win.ps1` が起動して残し、撮影スクリプトが前面に上げて撮る。
**合成クリック**（`SetCursorPos` + `mouse_event`）で本物のボタンを押せる — 偽のゲーム
フォルダ（`velocidrone.exe` という名前の空ファイル 1 個）に向ければ**本番を触らずに
通し確認できる**。
