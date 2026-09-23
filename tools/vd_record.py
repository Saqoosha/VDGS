#!/usr/bin/env python3
"""Record a flight from VelociDrone's WebSocket, one JSON line per message.

VelociDrone streams the drone's state over ws://<host>:60003/velocidrone once
"WebSocket Communication" and "WebSocket IMU Data" are on in its settings
(sim_states `use-web-socket` / `web-socket-imu` in user11.db): an `imu` object with
PositionX/Y/Z in metres in the game's world frame (Y up), SpeedX/Y/Z, the attitude
quaternion AttitudeX/Y/Z/W and the game clock, plus `racestatus` and `racedata`
messages when a race starts, passes a gate or finishes. The format is the one the
VDAI project on the Windows box reads (Vdai.Hover/Telemetry).

Every message is written as it arrives, with the local receive time, so the file is a
faithful log rather than a summary: turning it into a camera path is a separate step
(tools/vd_path_to_supersplat.py). The socket closes when the game quits or leaves the
flight, and this reconnects until stopped with Ctrl-C.

    python3 tools/vd_record.py flight.jsonl [--url ws://127.0.0.1:60003/velocidrone]
"""
import argparse
import asyncio
import json
import time

import websockets


async def ping(ws):
    while True:
        await asyncio.sleep(5)
        await ws.send(json.dumps({"command": "ping"}))


async def run(url, out_path):
    n_imu = n_other = 0
    with open(out_path, "a", buffering=1) as out:
        while True:
            try:
                # ping_interval=None: the game answers only its own JSON ping (sent below),
                # not the protocol-level ping websockets sends by default. With the default
                # the library drops the connection 40 s in and spends 10 s closing it - an
                # 11 s hole in the flight every 51 s, which is what the first recording had.
                async with websockets.connect(url, max_size=None, ping_interval=None) as ws:
                    print(f"connected: {url} at {time.strftime('%H:%M:%S')}", flush=True)
                    pinger = asyncio.create_task(ping(ws))
                    try:
                        async for msg in ws:
                            if isinstance(msg, bytes):
                                msg = msg.decode("utf-8", "replace")
                            out.write(json.dumps({"t": time.time(), "msg": msg}) + "\n")
                            if '"imu"' in msg:
                                n_imu += 1
                            else:
                                n_other += 1
                                print(f"  {msg[:160]}", flush=True)
                            if n_imu and n_imu % 600 == 0:
                                print(f"  {n_imu} imu samples", flush=True)
                    finally:
                        pinger.cancel()
            except (OSError, websockets.exceptions.WebSocketException) as e:
                print(f"waiting for the game ({type(e).__name__}) at {time.strftime('%H:%M:%S')}", flush=True)
                await asyncio.sleep(1)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out")
    ap.add_argument("--url", default="ws://127.0.0.1:60003/velocidrone")
    a = ap.parse_args()
    try:
        asyncio.run(run(a.url, a.out))
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
