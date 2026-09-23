# 空をキャプチャに持たせる

`blackout` はゲームの地面と一緒に空も黒くする。屋外のキャプチャでは、黒い空は不自然に見える。
この文書は、撮った映像そのものから空を作って、ゲーム・DVR viewer・SuperSplat に載せる方法をまとめる。
R6（JDL-2026-R6）で最初から最後まで通した。

## 原理

**空は無限遠にあるので視差が無い。** 同じ雲を見ているフレームは、どこから撮っても同じ方向に
同じ雲がある。効くのはカメラの位置ではなく向きだけ。SfM が全フレームの姿勢を持っているので、
空の画素を世界の方向に直して正距円筒（equirect）のビンに落とせば、全フレームが 1 枚の
パノラマに重なる。

spirula-studio が学習する skybox（`--background-mode sh`、checkpoint の `eng.bg_sky.sh_coeffs.npy`、
4 次 SH）は**使えない**。地平線の段差を表現できずにリンギングする。天頂は青いが、55° で黒くなり、
−20° にまた明るい帯が出る。雲もゼロ。

## 作る

```bash
# 空マスク（白 = 空）。sam track は frame_00000.png の連番で書くので、読んだ順の stem に付け替える
spirula sam track --model sam3-q4_0.ggml --frames images/v16 --text "sky; cloud" --keep-prompted --out masks_sky/v16

# 積む。--frame には、その ply を作ったのと同じ行列 json を渡す（パノラマが ply のフレームで出る）
python3 tools/sky_pano.py ws/sparse/0 images masks_sky sky.npy --frame enu_to_unity.json --width 4096 --step 2

# 見ていない天頂を埋めて JPEG にする
python3 tools/sky_fill.py sky.npy sky.jpg
```

R6 の実測：949 枚のうち、マスクが崩れたフレーム 161 枚を捨てて 788 枚を使った。実写で埋まるのは
上半球の 47%（高度 0〜50°）。ドローンは前か下を向いて飛ぶので、天頂は 1 枚も写っていない。

- **天頂は 3 次 SH の当てはめで埋める。** 雲の無い滑らかな勾配なので、外挿しても嘘にならない。
  継ぎ目は 8° かけて馴染ませる
- **地平線より下は外挿しない。** SH を下に伸ばしたら −1201 まで走り、朝 7 時の現場に夕焼けの帯が出た。
  撮っていない場所は、縁の色から暗く落とす
- **解像度は元映像に合わせる。** Avata 2 は fx 1048 で 18.3 px/度。2048 幅の equirect（5.7 px/度）は
  情報を 3 分の 1 に捨てていて、ゲームで粗さが見えた。4096 幅（11.4 px/度）＋ `--step 2` にした
- **粒はぼかす。** ビンごとの中央値は露出の揺れを画素単位の粒として残し、ゲーム画面では約 3 倍に
  拡大されて見える。左右をつなげて（経度 ±180° を跨いで）3 px のガウスでぼかした。隣接画素の差は 3.31 → 0.16
- Photoshop で手直しするときは、左右の端が繋がっていることに気をつける。R6 の出荷版には、
  地平線ぎわ（高度 3.2〜3.8°）の暗い点が 163 画素残っている。飛行中は木の稜線の裏に隠れるので許容した

## ハマりどころ（パノラマ）

どれも絵を見ただけでは原因が分からず、1 つのビンやフレームを名指しで追って決着した。

- **斑点の正体はゼロ。** 1 フレームの隣接画素が同じビンに落ちると、fancy indexing の書き込みは 1 個なのに
  カウンタは全部数える。ビンは「16 サンプルある」と言いながら中身は 3 個で、残りの空き枠のゼロで
  中央値を取っていた。1 フレーム 1 ビン 1 サンプル（`np.unique`）にして、暗い画素が 22% → 0%。
  侵食・リザーバサンプリング・フレーム棄却を順に足しても効かなかったのは、このせい
- **マスクの縁に木の葉が混ざる。** SAM は木を滑らかな輪郭で囲み、はみ出た葉が空の側に入る。
  侵食では届かないので、フレームごとの空の中央値に対する明るさでも切る
- **マスクがフレームまるごと外れることがある。** 空の中央値が 73（飛行全体では 171〜190）のフレームは、
  そのフレーム自身の中央値と比べても何も落ちない。最初に全フレームを測って、飛行全体と比べて捨てる

## ゲームで出す（mod）

`src/VDGS/WorldSky.cs` と `unity/VDGSBundler/Assets/VDGS/Shaders/PanoSkybox.shader`。

- **置き場所**：変換済みフォルダなら `<dir>/sky.jpg`、`.ply` なら隣の `<name>.sky.jpg`。
  フォルダの中に置くのは、配布・更新・削除がフォルダ単位で動くから
- **空は blackout の一部として出る。** placement の `blackout` が無ければ、空ファイルがあるときだけ自動で on
  （`SplatScene.BlackoutFor`）。明示の `false` は off。初版の placement にこのキーが無く、companion は
  更新時に利用者の placement を残すので、キーが無いときに on にしないと後から足した空が届かない
- **カメラの clear には触らず `RenderSettings.skybox` を差し替える。** ゲームの本来の描画経路に乗るので、
  不透明で埋まった画素には何も描かれない。空が出ている間は、`WorldBlackout` がカメラを Skybox に戻す
- **パノラマは ply のファイルの座標で作る。** mod は capture の `worldToLocalMatrix` を渡すだけなので、
  Tweak の `turn` に空が追従する。`mirrorY` で読んだ `.ply` はさらに Y の鏡映を掛けて引く
- 実行時に DXT1 ＋ mip へ圧縮する。4096×2048 で約 5.6 MB。**DLL とシェーダーバンドルは一緒に出す**
  （古いバンドルだと `panorama shader is missing from the bundle` をログに出して黒に落ちる）
- **equirect の継ぎ目に暗い破線が出る。** `atan2` が経度 ±180° で 0 と 1 に飛び、GPU が隣の画素との差分から
  最小ミップを選ぶ。パノラマ自体には暗い列は無い。半回転ずらした u の微分を `tex2Dgrad` に渡して直した
- ログは `vdgs-track.log` に `sky: loaded sky.jpg 4096x2048 DXT1` → `sky: on`。
  バンドル側は `vdgs-probe.log` の `panoSky=True`

## 座標の規約

ここを間違えると、太陽が地面と食い違う。

- 画像の行 0 が天頂。`u = atan2(x, −z) / 2π + 0.5`
- **v の式は環境で違う。** Unity はテクスチャを上下反転して読むので、シェーダーは `acos(−y)/π`。
  上から数える普通のサンプラ（WebGL の既定）は `acos(y)/π`
- Unity フレーム（x 東 / y 上 / z 北、det −1）では u=0 が北。web フレーム（x 東 / y 上 / z 南、det +1）では
  u=0 が南、0.25 が西、0.5 が北、0.75 が東
- **web 版 = Unity 版を u で鏡映してから W/2 ずらしたもの。** 実測の平均誤差は 0.98（JPEG のノイズ程度）。
  手系の違いは equirect では u の鏡映として出る。だから手直しは Unity 版に 1 回やればよく、web 版は機械的に導ける
- **PlayCanvas の cubemap は x を反転して引く**（`skyboxPS` の `SKY_CUBEMAP` 分岐に `dir.x *= -1.0`）。
  equirect を自分で 6 面に焼くなら、焼くときに x を反転する。忘れると、画像は正しいのに描画だけ鏡像になる。
  **鏡像は回転では直らない**（行列式が違う）ので、`skyboxRotation` で方位を合わせても雲の並びは左右逆のまま

## 検算

**空の画像そのものの太陽**を、その空を撮った時刻の太陽と比べる。R6 は白飛び領域の球面重心が
方位 97.7°・高度 24.4°、2026-09-19 07:10:16 JST の実際の太陽が方位 101.3°・高度 18.2°。
方位のずれ 3.6° は GPS フィットの方位の不確かさ程度で、規約を間違えれば 90° か 180° 動く。
高度が高めに出るのは、地平線の下を暗く落とした分だけ重心が上がるため。

描画が鏡になっていないかは、画像ではなく**描画**を測る。真北を向いて `readPixels` し、白飛びの重心の
x の符号を見る。

**別の時刻に撮った映像の空を基準にしてはいけない。** DVR の映像（別の飛行）の空の模様と突き合わせたら、
**鏡像（誤り）のほうがよく合う**という、はっきりした数字が出た。測り方も道具も正常で、壊れていたのは基準だけ。
決着をつけたのは、Saqoosha が Unity 版と見比べた判断だった。

## DVR viewer に出す

空は `build/dvr/hdz_0067/sky.jpg`（→ R2 の `dvr/jdl-2026-r6/data/sky.jpg`）。viewer はこれを CPU で
512² × 6 面の cubemap に焼くので、継ぎ目のミップの問題は起きない（x の反転だけ要る）。

- `tools/publish-dvr-viewer.sh` は **Worker ごと deploy して、`build/release/site` をまるごと出し直す**。
  空の差し替えだけなら、R2 のそのファイルだけを `rclone copyto` で置き換えれば足りる
- **配信は `Cache-Control: max-age=31536000, immutable`。** 同じ URL で中身を変えても、一度読んだブラウザは
  1 年間古い空を出し続ける。確実に更新したいならファイル名に版を入れる

## SuperSplat に出す

SuperSplat の Publish が運ぶのは splat と背景色だけで、skybox は運べない。ビューア自体は
`background.skyboxUrl` を読めるが、エディタに UI が無い。後から設定 API で書き足す手はあるが、
画像を CORS 付きで配信する必要があり（vdgs.saqoo.sh の画像は `Access-Control-Allow-Origin` を返さない）、
Worker の deploy は r2 の準備中のサイトとぶつかる。だから**空を splat にして ply に焼き込んだ**。

```bash
# web フレームの ply に、web フレームのパノラマを焼き込む。sky-web.jpg は、sky_pano.py を web の行列
# （{"scale": 1, "R": [[1,0,0],[0,0,1],[0,-1,0]], "t": [0,0,0]}）で回すか、Unity 版を u で鏡映して W/2 ずらして作る
python3 tools/sky_to_splats.py JDL-2026-R6-spirula-web-edit.ply sky-web.jpg out.ply   # --radius 2000 --spacing 0.3
```

- 半径 2 km の球面に、不透明で中心を向いた円盤を黄金螺旋で並べる（等面積なので天頂にも地平線にも偏らない）。
  間隔 0.3°、幅は間隔の 0.8 倍（隣の 3 枚が重なって透けない）。R6 で 301,468 個
- 球はシーンの外接球の一部になるので、ビューアの遠クリップ（外接球に合わせて自動で決まる）の内側に必ず入る。
  代わりに、初期視点を外接球に合わせると外から球を眺める絵になる。**初期視点は Publish した瞬間の
  エディタの視点**なので、コースの中に置いてから Publish する
- Publish は非公開（`listed: false`）で出る。**一覧には載らない**。掲載はサイト側で後から切り替える
- **ローカルの ply をエディタに読ませる**：エディタは File System Access API で開くので、DevTools から
  ファイルを差し込めない。`https://superspl.at/editor?load=<URL>&filename=<名前>` で取りに行かせる。
  ローカルの HTTP サーバで出すなら、`Access-Control-Allow-Origin: *` と
  `Access-Control-Allow-Private-Network: true` を付けて、OPTIONS にも答える。Chrome が「ローカルネットワークへの
  アクセス」の許可を聞くので、人が許可を押す
- エディタは iframe（`/editor-standalone`）の中で動き、`window.scene` がある。視点は
  `scene.camera.setPose(position, target, 0)`（Vec3 は `scene.camera.focalPoint.constructor`）

## VelociDrone の飛行経路をカメラアニメーションにする

VelociDrone は WebSocket で機体の状態を流す。仕様は `w` の `C:\Users\a\Documents\VDAI`
（`Vdai.Hover/Telemetry/VelocidroneTelemetry.cs`）から読んだ。

```bash
# 記録（ゲーム機の LAN の IP を指定する）
python3 tools/vd_record.py flight.jsonl --url ws://192.168.11.22:60003/velocidrone

# 最速の周を、placement の逆変換でキャプチャ座標 → web フレームに戻してキーにする
python3 tools/vd_path_to_supersplat.py flight.jsonl <capture>.placement.json lap.json --lap 3 --to-web
```

- **流れてくるもの**：`{"imu": {PositionX/Y/Z, SpeedX/Y/Z, AttitudeX/Y/Z/W, roll, pitch, yaw, timestamp}}` が約 32〜55 Hz。
  位置はゲームの世界座標（メートル、Y 上）で、placement と同じ系。ほかに `racestatus`、`racetype`、
  `countdown`、`racedata`（lap・gate・time・finished）
- **有効化**：ゲームの設定で WebSocket Communication と WebSocket IMU Data を on にする。中身は user11.db の
  `sim_states` の `use-web-socket` / `web-socket-imu`（文字列の `'true'`）
- **localhost では待っていない。** LAN の IP で待つ（Mac は `192.168.11.5`、`w` は `192.168.11.22`）
- **Python の `websockets` は `ping_interval=None` で繋ぐ。** ゲームはプロトコルレベルの ping に答えない。
  既定のままだと、20 秒後の ping の応答待ち 20 秒で切れて、閉じる待ち 10 秒と再接続 1 秒で
  **51 秒ごとに 11 秒途切れる**。周期が一定で、レース後に機体が止まってからも続くことで、記録側のバグと分かった。
  ゲームへの keepalive は JSON の `{"command":"ping"}` を 5 秒ごと
- 周はスタートゲート（最初のゲート通過の瞬間の位置）への最接近で切る。配信が途切れた区間を含む周は使わない
- カメラは機体の位置から 0.5 秒先の自分の経路を見る。SuperSplat のカメラにはロールが無いので FPV の視点は
  再現できないが、揺れの無い追いかけ視点になる
- エディタへは `scene.events` に `timeline.setFrameRate` → `timeline.setFrames` → `camera.loadPoses`
  （`{name, frame, position: Vec3, target: Vec3, fov}` の配列）。**タイムラインはメモリにしか無い**ので、
  ページを再読み込みしたら入れ直す。Publish のダイアログでアニメーションを on にする
- 検算：R6 の経路は、DVR の解析で求めたアーチ 4 つ（ENU で A(3.6, −15.2)・B(45, 4)・C(43.6, 43)・D(3.3, 33.9)）の
  1〜3 m 以内を通る。変換を間違えれば数十 m 外れる。`w` での 3 周は 28.32 / 27.30 / 26.18 秒
