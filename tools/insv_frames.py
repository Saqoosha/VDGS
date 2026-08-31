"""Insta360 の生フィッシュアイから透視投影フレームを切り出す。ステッチしない。

360 動画から 3DGS を作るとき、equirect に張り合わせてから切り出すのが通例だが、この
パイプラインではそれをしない。理由は 2 つ。

  - ステッチは中間で 1 回リサンプルを増やし、継ぎ目に位置誤差を残す。SfM は継ぎ目の
    誤差を「そういう形の世界」として素直に信じるので、後から取り返せない
  - `.insv` の trailer にレンズの厳密キャリブレーションが入っている。近似する必要がない

trailer には 3 つのモデルが並んで入っている。使うのは MEI（unified sphere）:

    2_<xi>_<fx>_<fy>_<cx>_<cy>_<a1>_<a2>_<a3>_<tx>_<ty>_<tz>_<k1>_<k2>_<k3>_<p1>_<p2>_<w>_<h>_<tag>
      ... 同じ 19 個がレンズ 2 個ぶん並び、末尾に 1 個

並びは憶測ではなく検算で確かめた。X3 5.7K の実測値（xi=1.948170, fx=4625.98,
参照解像度 11904x5952）で θ=100° の光線を投影すると中心から 1397 px。同じ trailer の
別モデルが報告する魚眼円の半径は 1405 px。0.6% 一致するので、この解釈で正しい。

投影は前向きだけで足りる。出力画素 -> 光線 -> MEI で魚眼画素、を LUT にして cv2.remap
に渡す。逆写像を解く必要はない。

    python3 tools/insv_frames.py calib VID_20260821_173728_00_007.insv
    python3 tools/insv_frames.py extract VID_..._00_007.insv VID_..._10_007.insv -o out/
"""
import argparse, json, os, re, subprocess, sys
import numpy as np
import cv2

TRAILER_MAGIC = b'8db42d694ccc418790edff439fe026bf'
MEI_FIELDS = 19  # xi fx fy cx cy a1 a2 a3 tx ty tz k1 k2 k3 p1 p2 w h tag


def read_trailer(path, window=32 << 20):
    """`.insv` の末尾から trailer を取り出す。前レンズのファイルだけが持っている。"""
    size = os.path.getsize(path)
    with open(path, 'rb') as f:
        f.seek(max(0, size - window))
        blob = f.read()
    if not blob.rstrip().endswith(TRAILER_MAGIC):
        return None
    return blob


def parse_calibration(path):
    """trailer から MEI モデルを引く。見つからなければ None。"""
    blob = read_trailer(path)
    if blob is None:
        return None
    best = None
    for m in re.finditer(rb'[0-9A-Za-z_.\-]{60,}', blob):
        tok = m.group().decode().split('_')
        # 型 '2' + 19*2 + 末尾 1 = 40。長いほうを採る（同じ文字列が 2 度出ることがある）
        if len(tok) != 2 * MEI_FIELDS + 2 or tok[0] != '2':
            continue
        try:
            v = [float(x) for x in tok[1:]]
        except ValueError:
            continue
        # xi が 2 つとも同じで 1 付近なら MEI。多項式モデルは先頭が半径（数千）になる
        if not (1.0 < v[0] < 3.0 and abs(v[0] - v[MEI_FIELDS]) < 1e-6):
            continue
        best = v
    if best is None:
        return None

    def lens(off):
        xi, fx, fy, cx, cy, a1, a2, a3, tx, ty, tz, k1, k2, k3, p1, p2, w, h, tag = \
            best[off:off + MEI_FIELDS]
        return dict(xi=xi, fx=fx, fy=fy, cx=cx, cy=cy, angles=[a1, a2, a3],
                    t=[tx, ty, tz], k=[k1, k2, k3], p=[p1, p2],
                    ref_w=int(w), ref_h=int(h))

    return [lens(0), lens(MEI_FIELDS)]


def scale_to_frame(cal, frame_w, frame_h, lens_index):
    """参照解像度（11904x5952 など）の値を、実フレーム 1 枚ぶんの画素に直す。

    参照は左右にレンズ 2 個を並べた 1 枚。1 レンズぶんの幅は ref_w/2 で、これが実際の
    フレーム（2880x2880）に対応する。レンズ 2 の cx は右半分にあるので原点を戻す。
    """
    half = cal['ref_w'] / 2.0
    s = frame_w / half
    cx = cal['cx'] - (half if lens_index == 1 else 0.0)
    out = dict(cal)
    out.update(fx=cal['fx'] * s, fy=cal['fy'] * s, cx=cx * s, cy=cal['cy'] * s, scale=s)
    return out


def project_mei(dirs, cal):
    """単位球上の方向 -> 魚眼画素。MEI（unified sphere）の前向き投影。"""
    n = dirs / np.linalg.norm(dirs, axis=-1, keepdims=True)
    z = n[..., 2] + cal['xi']
    # 球の裏側（z<=0）は写らない。あとで無効画素として落とす
    valid = z > 1e-6
    z = np.where(valid, z, 1.0)
    mx, my = n[..., 0] / z, n[..., 1] / z
    r2 = mx * mx + my * my
    k1, k2, k3 = cal['k']
    p1, p2 = cal['p']
    rad = 1.0 + k1 * r2 + k2 * r2 * r2 + k3 * r2 * r2 * r2
    xd = mx * rad + 2.0 * p1 * mx * my + p2 * (r2 + 2.0 * mx * mx)
    yd = my * rad + p1 * (r2 + 2.0 * my * my) + 2.0 * p2 * mx * my
    return cal['fx'] * xd + cal['cx'], cal['fy'] * yd + cal['cy'], valid


def rot(axis_angles_deg, order='zyx'):
    a = np.radians(axis_angles_deg)
    cx, cy, cz = np.cos(a)
    sx, sy, sz = np.sin(a)
    Rx = np.array([[1, 0, 0], [0, cx, -sx], [0, sx, cx]])
    Ry = np.array([[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]])
    Rz = np.array([[cz, -sz, 0], [sz, cz, 0], [0, 0, 1]])
    M = {'x': Rx, 'y': Ry, 'z': Rz}
    out = np.eye(3)
    for ch in order:
        out = out @ M[ch]
    return out


def view_rotation(yaw_deg, pitch_deg):
    """視線を lens 軸から yaw/pitch だけ振る。"""
    return rot([pitch_deg, yaw_deg, 0.0], order='yx')


def build_map(cal, frame_w, frame_h, lens_index, yaw, pitch, fov, size, roll):
    """出力画素ごとの魚眼サンプル位置（cv2.remap 用）と有効画素マスクを作る。"""
    c = scale_to_frame(cal, frame_w, frame_h, lens_index)
    f = (size / 2.0) / np.tan(np.radians(fov) / 2.0)
    j, i = np.meshgrid(np.arange(size, dtype=np.float64),
                       np.arange(size, dtype=np.float64), indexing='xy')
    dirs = np.stack([(j - size / 2.0 + 0.5) / f,
                     (i - size / 2.0 + 0.5) / f,
                     np.ones_like(j)], axis=-1)
    R = view_rotation(yaw, pitch)
    if roll:
        R = rot([0.0, 0.0, roll], order='z') @ R
    dirs = dirs @ R.T
    u, v, valid = project_mei(dirs, c)
    # 魚眼円の外は無効。円の半径は cx/cy と fx から決めず、参照値が無いので保守的に取る
    return u.astype(np.float32), v.astype(np.float32), valid


def circle_radius_px(blob_path, frame_w, ref_w):
    """trailer の別モデル（`p2_`）が持つ魚眼円の半径を、実フレームの画素に直す。"""
    blob = read_trailer(blob_path)
    if blob is None:
        return None
    for m in re.finditer(rb'p2_[0-9_.\-]{40,}', blob):
        tok = m.group().decode().split('_')
        try:
            r1 = float(tok[1])
        except (IndexError, ValueError):
            continue
        return r1 * (frame_w / (ref_w / 2.0))
    return None


def frame_reader(path, interval, width, height, hwaccel='auto'):
    """ffmpeg から間引いたフレームを raw で受け取る。1 パスで流す。"""
    cmd = ['ffmpeg', '-v', 'error']
    if hwaccel and hwaccel != 'none':
        cmd += ['-hwaccel', hwaccel]
    cmd += ['-i', path, '-vf', f'fps=1/{interval}', '-f', 'rawvideo',
            '-pix_fmt', 'bgr24', '-']
    n = width * height * 3
    proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, bufsize=n)
    try:
        while True:
            buf = proc.stdout.read(n)
            if len(buf) < n:
                break
            yield np.frombuffer(buf, np.uint8).reshape(height, width, 3)
    finally:
        proc.stdout.close()
        proc.wait()


def probe(path):
    out = subprocess.check_output(
        ['ffprobe', '-v', 'error', '-select_streams', 'v:0', '-show_entries',
         'stream=width,height,nb_frames,r_frame_rate', '-of', 'json', path])
    s = json.loads(out)['streams'][0]
    num, den = s['r_frame_rate'].split('/')
    return int(s['width']), int(s['height']), float(num) / float(den)


# pitch は +が上。試写で確かめた（-40 が地面、+40 が空）。傾き 40 + 対角 54.7 = 94.7 度で
# レンズの 100 度に収まるので、隅が魚眼円をはみ出さない。
DEFAULT_VIEWS = [('c', 0, 0), ('u', 0, 40), ('d', 0, -40), ('l', -40, 0), ('r', 40, 0)]


def parse_views(spec):
    if not spec:
        return DEFAULT_VIEWS
    out = []
    for part in spec.split():
        name, yaw, pitch = part.split(',')
        out.append((name, float(yaw), float(pitch)))
    return out


def cmd_calib(args):
    cal = parse_calibration(args.insv)
    if cal is None:
        sys.exit('no MEI calibration in trailer (rear-lens files do not carry one)')
    w, h, fps = probe(args.insv)
    print(f'frame {w}x{h} @ {fps:.3f} fps')
    r = circle_radius_px(args.insv, w, cal[0]['ref_w'])
    if r:
        print(f'fisheye circle radius {r:.1f} px (of half-width {w / 2:.0f})')
    for i, c in enumerate(cal):
        s = scale_to_frame(c, w, h, i)
        print(f'--- lens {i} ---')
        print(f'  xi {c["xi"]:.6f}  ref {c["ref_w"]}x{c["ref_h"]}')
        print(f'  f  {s["fx"]:.2f}, {s["fy"]:.2f}   c {s["cx"]:.2f}, {s["cy"]:.2f}')
        print(f'  k  {c["k"]}  p {c["p"]}')
        print(f'  angles {c["angles"]}  t {c["t"]}')
        # 検算: FOV の縁がどこに落ちるか
        for deg in (80, 90, 100):
            d = np.array([[np.sin(np.radians(deg)), 0.0, np.cos(np.radians(deg))]])
            u, v, _ = project_mei(d, s)
            print(f'  theta {deg}deg -> r {abs(u[0] - s["cx"]):.1f} px')


def cmd_extract(args):
    cal = parse_calibration(args.front)
    if cal is None:
        sys.exit('no MEI calibration in the front-lens trailer')
    views = parse_views(args.views)
    os.makedirs(args.out, exist_ok=True)
    meta = {'views': [], 'source': {}}

    for lens_index, path in enumerate([args.front, args.rear]):
        if path is None:
            continue
        w, h, fps = probe(path)
        maps = []
        for name, yaw, pitch in views:
            u, v, valid = build_map(cal[lens_index], w, h, lens_index, yaw, pitch,
                                    args.fov, args.size, args.roll)
            maps.append((name, u, v, valid))
            f = (args.size / 2.0) / np.tan(np.radians(args.fov) / 2.0)
            meta['views'].append(dict(lens=lens_index, name=name, yaw=yaw, pitch=pitch,
                                      fov=args.fov, size=args.size, fx=f, fy=f,
                                      cx=args.size / 2.0, cy=args.size / 2.0))
        meta['source'][f'lens{lens_index}'] = dict(path=os.path.basename(path),
                                                   w=w, h=h, fps=fps)
        n = 0
        for k, frame in enumerate(frame_reader(path, args.interval, w, h, args.hwaccel)):
            for name, u, v, valid in maps:
                img = cv2.remap(frame, u, v, cv2.INTER_CUBIC,
                                borderMode=cv2.BORDER_CONSTANT, borderValue=(0, 0, 0))
                img[~valid] = 0
                out = os.path.join(args.out, f'insv{lens_index}_{name}_{k:05d}.jpg')
                cv2.imwrite(out, img, [cv2.IMWRITE_JPEG_QUALITY, args.quality])
            n += 1
            if n % 20 == 0:
                print(f'lens {lens_index}: {n} frames', flush=True)
            if args.limit and n >= args.limit:
                break
        print(f'lens {lens_index}: {n} frames x {len(maps)} views', flush=True)

    with open(os.path.join(args.out, 'views.json'), 'w') as f:
        json.dump(meta, f, indent=2)


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest='cmd', required=True)

    c = sub.add_parser('calib', help='trailer のキャリブレーションを出す')
    c.add_argument('insv')
    c.set_defaults(func=cmd_calib)

    e = sub.add_parser('extract', help='透視投影フレームを書き出す')
    e.add_argument('front')
    e.add_argument('rear', nargs='?')
    e.add_argument('-o', '--out', required=True)
    e.add_argument('--interval', type=float, default=2.0, help='秒。既定 2.0')
    e.add_argument('--fov', type=float, default=90.0)
    e.add_argument('--size', type=int, default=1400)
    e.add_argument('--roll', type=float, default=0.0,
                   help='出力を光軸まわりに回す（度）。センサの向きを直す用')
    e.add_argument('--views', default='',
                   help='"名前,yaw,pitch" を空白区切り。既定は中央+上下左右 40 度')
    e.add_argument('--quality', type=int, default=95)
    e.add_argument('--limit', type=int, default=0, help='先頭 N フレームで止める（試写用）')
    e.add_argument('--hwaccel', default='auto')
    e.set_defaults(func=cmd_extract)

    args = ap.parse_args()
    args.func(args)


if __name__ == '__main__':
    main()
