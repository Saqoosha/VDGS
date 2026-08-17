#!/usr/bin/env node
// Screenshot a WebGL splat viewer at an exact size, driven over the DevTools protocol.
//
// Chrome's own --screenshot flag cannot do this: the viewer renders in a
// requestAnimationFrame loop that never goes idle, so --virtual-time-budget never
// settles and the process hangs until it is killed. Driving CDP directly lets us wait
// for a real readiness signal - the viewer hiding its spinner once the splats are
// uploaded - and capture exactly then.
//
// Node 24 has a native WebSocket, so this needs no dependencies.
//
//   node tools/webref_shot.mjs <url> <out.png> [size]

const [, , url, outPath, sizeArg] = process.argv;
if (!url || !outPath) {
    console.error("usage: webref_shot.mjs <url> <out.png> [size]");
    process.exit(2);
}
const SIZE = parseInt(sizeArg || "1024", 10);
const PORT = parseInt(process.env.VDGS_CDP_PORT || "9223", 10);

const rpc = (() => {
    let id = 0;
    return (ws, method, params = {}, sessionId) =>
        new Promise((resolve, reject) => {
            const msgId = ++id;
            const onMessage = (ev) => {
                const m = JSON.parse(ev.data);
                if (m.id !== msgId) return;
                ws.removeEventListener("message", onMessage);
                m.error ? reject(new Error(method + ": " + m.error.message)) : resolve(m.result);
            };
            ws.addEventListener("message", onMessage);
            ws.send(JSON.stringify({ id: msgId, method, params, sessionId }));
        });
})();

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function main() {
    // A fresh tab per run, so nothing carries over from a previous camera.
    const created = await fetch(
        `http://127.0.0.1:${PORT}/json/new?about:blank`, { method: "PUT" },
    ).then((r) => r.json());

    const ws = new WebSocket(created.webSocketDebuggerUrl);
    await new Promise((res, rej) => {
        ws.addEventListener("open", res, { once: true });
        ws.addEventListener("error", rej, { once: true });
    });

    // Fix the viewport before navigating: the viewer reads innerWidth/innerHeight once
    // on load to build its projection, so resizing afterwards would need a resize event.
    await rpc(ws, "Emulation.setDeviceMetricsOverride", {
        width: SIZE, height: SIZE, deviceScaleFactor: 1, mobile: false,
    });
    await rpc(ws, "Page.enable");
    await rpc(ws, "Page.navigate", { url });

    // The spinner is hidden once the splat buffer is uploaded. Poll for that rather
    // than guessing a delay - a large scene takes much longer than a small one.
    // Generous: a million-splat .ply has to be fetched, parsed and sorted in a worker
    // before the first frame, and that is minutes rather than seconds.
    const deadline = Date.now() + 300000;
    let ready = false;
    while (Date.now() < deadline) {
        await sleep(250);
        const r = await rpc(ws, "Runtime.evaluate", {
            expression:
                "(() => { const s = document.getElementById('spinner');" +
                " return s ? getComputedStyle(s).display === 'none' : false; })()",
            returnByValue: true,
        }).catch(() => null);
        if (r?.result?.value) { ready = true; break; }
    }
    if (!ready) {
        console.error("viewer never signalled ready");
        process.exit(1);
    }

    // The sort runs asynchronously in a worker, so the first ready frame can still be
    // in draw order. A short settle beats a racy capture.
    await sleep(2500);

    // The viewer draws its own title and an fps counter over the canvas. Left in, they
    // become permanent differences in every pixel comparison, so strip everything that
    // is not the canvas itself.
    await rpc(ws, "Runtime.evaluate", {
        expression:
            "document.querySelectorAll('body > *').forEach(el => {" +
            "  if (el.id !== 'canvas' && el.tagName !== 'SCRIPT') el.style.display = 'none';" +
            "});" +
            "document.body.style.margin = '0';",
    });
    await sleep(300);

    const shot = await rpc(ws, "Page.captureScreenshot", {
        format: "png",
        clip: { x: 0, y: 0, width: SIZE, height: SIZE, scale: 1 },
        captureBeyondViewport: false,
    });

    const { writeFileSync } = await import("node:fs");
    writeFileSync(outPath, Buffer.from(shot.data, "base64"));
    console.log(`wrote ${outPath} (${SIZE}x${SIZE})`);

    await fetch(`http://127.0.0.1:${PORT}/json/close/${created.id}`).catch(() => {});
    ws.close();
}

main().catch((e) => { console.error(e.message); process.exit(1); });
