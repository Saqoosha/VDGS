# Control Web UI Implementation Plan

> **For the implementing agent:** Follow the spec at `docs/superpowers/specs/2026-08-20-control-web-ui-design.md`. Do not add catalog fetch, CORS, or a new API endpoint. Each task is TDD where it has logic: failing test, then code, then passing test, then commit.

**Goal:** Replace the HTML string in `WebUi.cs` with a Vite + React app served from `<game>/vdgs/ui/`, keeping the existing POST API and security rules, and adding a local Library page.

**Architecture:** `web/` builds static files. `tools/deploy.sh` copies them to `<game>/vdgs/ui/`. `WebControl` serves that directory on `:8777` next to `/api/*`. Discover skips a reserved directory named `ui`. Status grows metadata fields so Library can render without a second endpoint.

**Stack:** bun, Vite, React, TypeScript, Tailwind, shadcn/ui, React Router. Plugin remains netstandard2.0. Tests: xunit net8 for Unity-free C#, Vitest for the frontend.

---

## Task 1: Test project and gitignore

**Files:**
- Create: `src/VDGS.Tests/VDGS.Tests.csproj`
- Create: `src/VDGS.Tests/SanityTests.cs`
- Modify: `.gitignore`

**Step 1: gitignore**

Add:

```
web/dist/
web/node_modules/
```

**Step 2: test project**

`src/VDGS.Tests/VDGS.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>disable</Nullable>
    <IsPackable>false</IsPackable>
    <RootNamespace>VDGS.Tests</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
  </ItemGroup>
  <ItemGroup>
    <Compile Include="..\VDGS\VdgsPaths.cs" Link="VdgsPaths.cs" Condition="Exists('..\VDGS\VdgsPaths.cs')" />
    <Compile Include="..\VDGS\SplatMetaFile.cs" Link="SplatMetaFile.cs" Condition="Exists('..\VDGS\SplatMetaFile.cs')" />
  </ItemGroup>
</Project>
```

The `Condition` lets this project compile before those files exist. Remove the conditions in Task 2/4 once the files are there.

`src/VDGS.Tests/SanityTests.cs`:

```csharp
using Xunit;

namespace VDGS.Tests
{
    public class SanityTests
    {
        [Fact]
        public void Xunit_runs()
        {
            Assert.True(true);
        }
    }
}
```

**Step 3: run**

```bash
dotnet test src/VDGS.Tests/VDGS.Tests.csproj
```

Expected: passed 1.

**Step 4: commit**

```bash
git add .gitignore src/VDGS.Tests
git commit -m "Add a plugin test project that does not reference Unity."
```

---

## Task 2: Reserved scene name

**Files:**
- Create: `src/VDGS/VdgsPaths.cs`
- Create: `src/VDGS.Tests/VdgsPathsTests.cs`
- Modify: `src/VDGS.Tests/VDGS.Tests.csproj` (drop the Condition on VdgsPaths.cs)

**Step 1: failing tests**

`src/VDGS.Tests/VdgsPathsTests.cs`:

```csharp
using Xunit;

namespace VDGS.Tests
{
    public class VdgsPathsTests
    {
        [Theory]
        [InlineData("ui")]
        [InlineData("UI")]
        [InlineData("Ui")]
        public void Ui_is_reserved(string name)
        {
            Assert.True(VdgsPaths.IsReservedSceneName(name));
        }

        [Theory]
        [InlineData("playroom")]
        [InlineData("ui-extra")]
        [InlineData("")]
        public void Other_names_are_scenes(string name)
        {
            Assert.False(VdgsPaths.IsReservedSceneName(name));
        }
    }
}
```

`dotnet test src/VDGS.Tests` — fail (type missing).

**Step 2: implementation**

`src/VDGS/VdgsPaths.cs` (Unity-free):

```csharp
using System;
using System.Collections.Generic;
using System.IO;

namespace VDGS
{
    /// <summary>
    /// Layout under &lt;game&gt;/vdgs/ that is not a splat scene, and the mapping from
    /// a request path to a file under ui/.
    /// </summary>
    internal static class VdgsPaths
    {
        internal const string UiDirName = "ui";

        internal enum UiResult
        {
            MissingUi,
            Forbidden,
            NotFound,
            File,
            Spa,
        }

        private static readonly HashSet<string> AssetExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".js", ".css", ".map", ".svg", ".ico", ".png", ".woff2",
        };

        internal static bool IsReservedSceneName(string name)
        {
            return string.Equals(name, UiDirName, StringComparison.OrdinalIgnoreCase);
        }

        internal static UiResult ResolveUi(string uiRoot, string urlPath, out string filePath)
        {
            filePath = null;
            if (string.IsNullOrEmpty(uiRoot) || !Directory.Exists(uiRoot))
                return UiResult.MissingUi;

            var index = Path.Combine(uiRoot, "index.html");
            if (!File.Exists(index))
                return UiResult.MissingUi;

            var rel = string.IsNullOrEmpty(urlPath) ? "/" : urlPath;
            if (rel.IndexOf('\\') >= 0)
                return UiResult.Forbidden;

            try { rel = Uri.UnescapeDataString(rel); }
            catch (UriFormatException) { return UiResult.Forbidden; }

            if (rel.IndexOf('\0') >= 0)
                return UiResult.Forbidden;

            if (!rel.StartsWith("/"))
                rel = "/" + rel;

            if (rel == "/" || rel == "/index.html")
            {
                filePath = index;
                return UiResult.File;
            }

            var trimmed = rel.TrimStart('/');
            foreach (var seg in trimmed.Split('/'))
            {
                if (seg == ".." || seg == "." || seg.IndexOf(':') >= 0)
                    return UiResult.Forbidden;
            }

            var rootFull = Path.GetFullPath(uiRoot);
            var candidate = Path.GetFullPath(Path.Combine(uiRoot, trimmed.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(candidate, rootFull, StringComparison.OrdinalIgnoreCase))
                return UiResult.Forbidden;

            if (File.Exists(candidate))
            {
                filePath = candidate;
                return UiResult.File;
            }

            if (AssetExtensions.Contains(Path.GetExtension(candidate)))
                return UiResult.NotFound;

            filePath = index;
            return UiResult.Spa;
        }

        internal static string MimeType(string filePath)
        {
            switch (Path.GetExtension(filePath).ToLowerInvariant())
            {
                case ".html": return "text/html; charset=utf-8";
                case ".js": return "text/javascript; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".svg": return "image/svg+xml";
                case ".png": return "image/png";
                case ".ico": return "image/x-icon";
                case ".woff2": return "font/woff2";
                case ".map": return "application/json";
                default: return "application/octet-stream";
            }
        }

        internal static string CacheControl(string urlPath)
        {
            if (string.IsNullOrEmpty(urlPath) || urlPath == "/" || urlPath == "/index.html")
                return "no-store";
            if (urlPath.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
                return "public, max-age=31536000, immutable";
            return "no-store";
        }
    }
}
```

**Step 3: `dotnet test`** — reserved-name tests pass. Resolve tests come in Task 3.

**Step 4: commit**

```bash
git add src/VDGS/VdgsPaths.cs src/VDGS.Tests
git commit -m "Reserve the vdgs/ui directory name so it cannot become a scene."
```

---

## Task 3: UI path resolution tests

**Files:**
- Modify: `src/VDGS.Tests/VdgsPathsTests.cs`

**Step 1: add failing tests** that create a temp `ui/` with `index.html` and `assets/app.js`.

```csharp
public class VdgsPathsResolveTests : IDisposable
{
    private readonly string _root;

    public VdgsPathsResolveTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vdgs-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        File.WriteAllText(Path.Combine(_root, "index.html"), "<!doctype html>");
        File.WriteAllText(Path.Combine(_root, "assets", "app.js"), "1");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void Root_serves_index()
    {
        var r = VdgsPaths.ResolveUi(_root, "/", out var p);
        Assert.Equal(VdgsPaths.UiResult.File, r);
        Assert.Equal("index.html", Path.GetFileName(p));
    }

    [Fact]
    public void Existing_asset_is_a_file()
    {
        var r = VdgsPaths.ResolveUi(_root, "/assets/app.js", out var p);
        Assert.Equal(VdgsPaths.UiResult.File, r);
        Assert.Equal("app.js", Path.GetFileName(p));
    }

    [Fact]
    public void Library_falls_back_to_spa()
    {
        var r = VdgsPaths.ResolveUi(_root, "/library", out var p);
        Assert.Equal(VdgsPaths.UiResult.Spa, r);
        Assert.Equal("index.html", Path.GetFileName(p));
    }

    [Fact]
    public void Missing_js_is_404_not_spa()
    {
        var r = VdgsPaths.ResolveUi(_root, "/assets/nope.js", out _);
        Assert.Equal(VdgsPaths.UiResult.NotFound, r);
    }

    [Theory]
    [InlineData("/../secret")]
    [InlineData("/assets/../../etc/passwd")]
    [InlineData("/assets\\..\\..\\x")]
    public void Escape_is_forbidden(string url)
    {
        var r = VdgsPaths.ResolveUi(_root, url, out _);
        Assert.Equal(VdgsPaths.UiResult.Forbidden, r);
    }

    [Fact]
    public void Missing_root_is_MissingUi()
    {
        var r = VdgsPaths.ResolveUi(Path.Combine(_root, "nope"), "/", out _);
        Assert.Equal(VdgsPaths.UiResult.MissingUi, r);
    }

    [Fact]
    public void Assets_are_immutable_cache()
    {
        Assert.Contains("immutable", VdgsPaths.CacheControl("/assets/app.js"));
        Assert.Equal("no-store", VdgsPaths.CacheControl("/"));
    }
}
```

**Step 2: `dotnet test`** — should already pass if Task 2's `ResolveUi` is complete. If a case fails, fix `VdgsPaths` until all pass. Do not weaken a test.

**Step 3: commit**

```bash
git add src/VDGS/VdgsPaths.cs src/VDGS.Tests/VdgsPathsTests.cs
git commit -m "Test that ui/ path resolution cannot walk out of the root."
```

---

## Task 4: Lightweight splat meta

**Files:**
- Create: `src/VDGS/SplatMetaFile.cs`
- Create: `src/VDGS.Tests/SplatMetaFileTests.cs`
- Modify: `src/VDGS.Tests/VDGS.Tests.csproj` (drop Condition on SplatMetaFile.cs)

**Step 1: failing tests**

Write a temp converted dir with `meta.json`, `pos.bin` (3 bytes), `placement.json` (ignored), and a `.ply` with a header `element vertex 12`.

```csharp
using System.IO;
using System.Text;
using Xunit;

namespace VDGS.Tests
{
    public class SplatMetaFileTests : IDisposable
    {
        private readonly string _dir;

        public SplatMetaFileTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vdgs-meta-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void Converted_reads_formats_and_skips_placement()
        {
            var scene = Path.Combine(_dir, "drjohnson");
            Directory.CreateDirectory(scene);
            File.WriteAllText(Path.Combine(scene, "meta.json"),
                "{\"formatVersion\":20231020,\"splatCount\":3177554,"
                + "\"posFormat\":\"Norm16\",\"scaleFormat\":\"Norm16\","
                + "\"colorFormat\":\"Float16x4\",\"shFormat\":\"Norm11\"}");
            File.WriteAllBytes(Path.Combine(scene, "pos.bin"), new byte[10]);
            File.WriteAllText(Path.Combine(scene, "placement.json"), "{}");

            var info = SplatMetaFile.Read(scene);
            Assert.Equal("converted", info.Kind);
            Assert.Equal(3177554, info.Splats);
            Assert.Equal("Norm16", info.PosFormat);
            Assert.Equal("Norm16", info.ScaleFormat);
            Assert.Equal("Float16x4", info.ColorFormat);
            Assert.Equal("Norm11", info.ShFormat);
            Assert.Equal(10 + Encoding.UTF8.GetByteCount(
                File.ReadAllText(Path.Combine(scene, "meta.json"))), info.Bytes);
        }

        [Fact]
        public void Ply_is_kind_ply_with_empty_formats()
        {
            var ply = Path.Combine(_dir, "luigi.ply");
            File.WriteAllText(ply, "ply\nformat binary_little_endian 1.0\nelement vertex 14526\nend_header\nxxxx");
            var info = SplatMetaFile.Read(ply);
            Assert.Equal("ply", info.Kind);
            Assert.Equal(14526, info.Splats);
            Assert.Null(info.PosFormat);
            Assert.Equal(new FileInfo(ply).Length, info.Bytes);
        }
    }
}
```

`dotnet test` — fail (type missing).

**Step 2: implementation**

`src/VDGS/SplatMetaFile.cs` — Newtonsoft only, no Unity:

- DTO with `splatCount`, `posFormat`, `scaleFormat`, `colorFormat`, `shFormat`
- `Read(path)`: if path ends with `.ply` (ignore case), kind `ply`, splat count from ASCII header `element vertex N`, bytes = file length, formats null
- else: deserialize `meta.json`, kind `converted`, bytes = sum of files in the directory except `placement.json` (ignore case)
- Header scan for ply: same as the current `SplatScene.PlySplatCount` (first 8KB, `element vertex `)

**Step 3: `dotnet test`** — both facts pass. If `Bytes` on converted fails because of UTF-8 vs the write encoding, assert `info.Bytes == new FileInfo(meta).Length + 10` after writing.

**Step 4: commit**

```bash
git add src/VDGS/SplatMetaFile.cs src/VDGS.Tests
git commit -m "Read splat metadata without opening the GPU buffers."
```

---

## Task 5: Wire meta and reserved name into the plugin

**Files:**
- Modify: `src/VDGS/SplatScene.cs`
- Modify: `src/VDGS/Plugin.cs`

**Discover:** after `GetDirectories`, skip when `VdgsPaths.IsReservedSceneName(new DirectoryInfo(dir).Name)` and log `skipping reserved dir: ui`.

**Constructor:** `m_Meta = SplatMetaFile.Read(path);` `m_MetaSplatCount` becomes `m_Meta.Splats`. Delete `MetaSplatCount()` and `PlySplatCount()`.

**New properties** on `SplatScene` (source is always the string `"local"`):

```csharp
internal string Source => "local";
internal string Kind => m_Meta.Kind;
internal string PosFormat => m_Meta.PosFormat;
internal string ScaleFormat => m_Meta.ScaleFormat;
internal string ColorFormat => m_Meta.ColorFormat;
internal string ShFormat => m_Meta.ShFormat;
internal long Bytes => m_Meta.Bytes;
```

**BuildStatus:** add to each available dict, in this order after `name`:

```
source, kind, posFormat, scaleFormat, colorFormat, shFormat, bytes
```

Keep existing keys. Null format fields may be omitted or serialized as null; Newtonsoft will emit `null`. The frontend treats them as optional.

`HasCollision` stays on `SplatScene` via `SplatCollision.Exists` — do not move it into `SplatMetaFile` (that class must stay Unity-free; `SplatCollision` is a MonoBehaviour).

**Check:** `dotnet build src/VDGS/VDGS.csproj -c Release` must still work (needs `lib/`). If `lib/` is missing in this environment, skip the plugin build and rely on the test project.

**Commit:**

```bash
git add src/VDGS/SplatScene.cs src/VDGS/Plugin.cs
git commit -m "Expose local-scene metadata on /api/status for the library page."
```

---

## Task 6: Serve `vdgs/ui/` and shrink WebUi

**Files:**
- Modify: `src/VDGS/WebControl.cs`
- Modify: `src/VDGS/WebUi.cs`
- Modify: `src/VDGS/Plugin.cs` (`StartWebControl`)

**WebControl:** add `internal string UiRoot { get; set; }`.

Replace the `/` and `/index.html` cases with `ServeUi(ctx, path)`. In `default`, if the path starts with `/api/` keep 404 JSON; otherwise `ServeUi(ctx, path)`.

`ServeUi`:

```csharp
private void ServeUi(HttpListenerContext ctx, string urlPath)
{
    var result = VdgsPaths.ResolveUi(UiRoot, urlPath, out var filePath);
    if (result == VdgsPaths.UiResult.MissingUi)
    {
        Respond(ctx, 200, WebUi.Html, "text/html; charset=utf-8");
        return;
    }
    if (result == VdgsPaths.UiResult.Forbidden || result == VdgsPaths.UiResult.NotFound)
    {
        Respond(ctx, 404, "{\"error\":\"not found\"}");
        return;
    }
    var bytes = File.ReadAllBytes(filePath);
    var mime = VdgsPaths.MimeType(filePath);
    var cache = VdgsPaths.CacheControl(urlPath);
    // write status 200, ContentType mime, ContentLength, Cache-Control cache
    // still no Access-Control-Allow-Origin
}
```

Extend `Respond` or add `RespondBytes` that sets `Cache-Control`. Do not add CORS headers.

**Plugin:** `m_Web.UiRoot = Path.Combine(Paths.GameRootPath, "vdgs", VdgsPaths.UiDirName);`

**WebUi.cs:** delete the entire current HTML app. Replace `Html` with a short page:

```html
<!doctype html>
<meta charset="utf-8">
<title>VDGS Control</title>
<p>UI is not installed. Run tools/deploy.sh --ui.</p>
```

Keep the class and the comment that this is the missing-`ui/` fallback only.

**Commit:**

```bash
git add src/VDGS/WebControl.cs src/VDGS/WebUi.cs src/VDGS/Plugin.cs
git commit -m "Serve the control UI from disk instead of an HTML string."
```

---

## Task 7: Scaffold `web/`

**Files:** create `web/` via bun. Do not commit `node_modules` or `dist`.

From the repo root:

```bash
bun create vite web --template react-ts
cd web
bun install
bun add react-router-dom
bun add -d vitest @testing-library/react @testing-library/jest-dom @testing-library/user-event jsdom @vitejs/plugin-react
bun add tailwindcss @tailwindcss/vite
```

Tailwind v4: `web/vite.config.ts` plugins `[react(), tailwindcss()]`. `web/src/index.css`:

```css
@import "tailwindcss";
```

shadcn (non-interactive). If the CLI asks, choose: New York, Zinc, CSS variables, `src/`, React, no RTL.

```bash
cd web
bunx shadcn@latest init -y -b
bunx shadcn@latest add button card input slider checkbox select table badge label
```

If `init -y` fails, write `components.json` matching current shadcn defaults (zinc, CSS variables, aliases `@/components`, `@/lib/utils`) and add the components.

**Vitest:** in `vite.config.ts` add

```ts
test: { environment: 'jsdom', setupFiles: './src/test/setup.ts', globals: true }
```

`web/src/test/setup.ts`: `import '@testing-library/jest-dom/vitest'`

`web/package.json` scripts: `"test": "vitest run"`, `"test:watch": "vitest"`

**Router:** `BrowserRouter` in `main.tsx`. Routes `/` and `/library` in `App.tsx` (placeholder pages until Tasks 11–12).

**Fonts:** system stack only. No Google Fonts, no CDN.

**Smoke:**

```bash
cd web && bun test
cd web && bun run build
```

Build must emit `web/dist/index.html` and hashed assets under `web/dist/assets/`.

**Commit** the scaffold (`web/package.json`, lockfile, source, `components.json`, shadcn files). Not `node_modules` or `dist`.

```bash
git add web .gitignore
git commit -m "Scaffold the control UI as a Vite React app."
```

---

## Task 8: API client (TDD)

**Files:**
- Create: `web/src/types.ts`
- Create: `web/src/api.ts`
- Create: `web/src/api.test.ts`

**types.ts** — copy the `Scene` and `Status` types from the spec. Add `source: 'local' | 'catalog'`.

**api.ts:**

```ts
async function post(url: string, body: object = {}): Promise<void> {
  const r = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  if (!r.ok) {
    let msg = r.statusText
    try {
      const j = await r.json()
      if (j && j.error) msg = String(j.error)
    } catch { /* keep statusText */ }
    throw new Error(msg)
  }
}

export async function getStatus(): Promise<Status> {
  const r = await fetch('/api/status', { cache: 'no-store' })
  if (!r.ok) throw new Error('status ' + r.status)
  return r.json()
}

export const load = (splat: string) => post('/api/load', { splat })
export const unload = () => post('/api/unload', {})
export const bind = (splats: string[]) => post('/api/bind', { splats })
export const unbind = (track?: string) =>
  post('/api/unbind', track ? { track } : {})
export const setBackdrop = (splat: string, on: boolean) =>
  post('/api/backdrop', { splat, on })
export const setCollision = (splat: string, on: boolean) =>
  post('/api/collision', { splat, on })
export const setCollisionView = (splat: string, mode: Scene['collisionView']) =>
  post('/api/collisionview', { splat, mode })
export const setTransform = (splat: string, scale?: number, y?: number) => {
  const body: Record<string, unknown> = { splat }
  if (scale != null) body.scale = scale
  if (y != null) body.y = y
  return post('/api/transform', body)
}
```

**api.test.ts:** mock `globalThis.fetch`. Assert every post helper is called with `method: 'POST'` and headers containing `Content-Type: application/json`. `unload()` body is `'{}'`. `unbind()` without a track is `'{}'`. `setTransform('a', 2)` JSON has `scale` and no `y`.

```bash
cd web && bun test
```

**Commit:**

```bash
git add web/src/types.ts web/src/api.ts web/src/api.test.ts
git commit -m "Wrap the control API so POSTs always send JSON."
```

---

## Task 9: Slider math (TDD)

**Files:**
- Create: `web/src/sliders.ts`
- Create: `web/src/sliders.test.ts`

Copy the formulas from the spec. Export `toSlider`, `fromSlider`, `toYSlider`, `fromYSlider`, `kYReach = 200`.

Tests:

- `fromSlider(toSlider(1))` ≈ 1
- `fromYSlider(toYSlider(0))` ≈ 0
- `fromYSlider(toYSlider(5.11))` ≈ 5.11 (tolerance 1e-6)
- `fromYSlider(toYSlider(-206))` ≈ -206 — wait: kYReach is 200, so -206 is outside the slider. The **number box** accepts ±1000. `toYSlider` should clamp to ±kYReach for the slider position; `fromYSlider` of the clamped position will not round-trip -206. Test this explicitly:
  - `fromYSlider(toYSlider(5.11))` ≈ 5.11
  - `Math.abs(toYSlider(-206)) === 1` (clamped to the end) **or** document clamp in `toYSlider`:
    `const c = Math.max(-kYReach, Math.min(kYReach, v))` then the signed log1p on `c`
  - Round-trip -206 is **not** required; the number input sends -206 straight to the API

Also: slider range for scale is -2..2 because `log10(0.01)=-2` and `log10(100)=2`.

```bash
cd web && bun test
```

**Commit:**

```bash
git add web/src/sliders.ts web/src/sliders.test.ts
git commit -m "Port the scale and height slider mapping from the old UI."
```

---

## Task 10: Library search helper (TDD)

**Files:**
- Create: `web/src/search.ts`
- Create: `web/src/search.test.ts`

```ts
export function filterScenes(scenes: Scene[], q: string): Scene[] {
  const n = q.trim().toLowerCase()
  if (!n) return scenes
  return scenes.filter(s => s.name.toLowerCase().includes(n))
}
```

Tests: empty query returns all; `"Play"` matches `"playroom"`; no match returns `[]`.

**Commit.**

---

## Task 11: App shell and Control page

**Files (under `web/src/`):**
- `App.tsx` — header (dot + VDGS + NavLink Control/Library) + `<Outlet />`
- `main.tsx` — `BrowserRouter` with routes `/` → Control, `/library` → Library
- `useStatus.ts` — `useEffect` + `setInterval(1500)` calling `getStatus`. On success set live=true and keep state. On failure set live=false and **do not** clear the last good state.
- `pages/Control.tsx`
- `pages/Control.test.tsx` (XSS)

**Control layout** (shadcn Card, light, zinc, more padding than the old 18px — use `p-6`, `max-w-3xl mx-auto`):

1. Current track: `state.track` as text. If null, muted `no track loaded`. Bound-to line. Buttons: Bind shown / Unbind this track / Hide all. Disable Bind/Unbind as the old UI did (`!track`, `!bindings[track]`).
2. Shown splat: only if `loaded[0]` exists in `available`. Name, splat count, backdrop checkbox, collision checkbox+select if `hasCollision`, scale slider+number, height slider+number. `dragging` ref so a poll does not call `setScaleUi` while pointer is down.
3. Bindings table with remove.

Reuse the old interaction: after a successful POST, `await new Promise(r => setTimeout(r, 250)); refresh()`. A module-level `busy` boolean drops overlapping posts.

Failed POST: throw from `api.ts`; catch, show a flash message via `textContent` (a `<p>`), revert the checkbox to the previous `state` value.

**XSS test:** render Control with `track: '<img src=x onerror=alert(1)>'` and a binding key of the same string. `queryByRole('img')` is null. `getByText` finds the raw string. No `dangerouslySetInnerHTML` anywhere in `web/src` — grep the tree before commit and fail the task if it appears.

Do not put transform sliders on Library.

**Commit.**

---

## Task 12: Library page

**Files:**
- `web/src/pages/Library.tsx`
- `web/src/pages/Library.test.tsx`

Search input filters `available` through `filterScenes`. Each scene is a Card: name, splat count (`toLocaleString()`), `kind`, the four format strings (omit if missing), bytes as a short human string (e.g. `12.4 MB` from `bytes`, empty if missing), collision yes/no, Shown badge if `shown`. Button Show calls `load(name)` then refresh. Do not show a Catalog tab.

Empty list: `nothing in <game>/vdgs/` as text.

XSS test: scene name `'<img src=x onerror=alert(1)>'` is a text node, not an img.

```bash
cd web && bun test && bun run build
```

**Commit.**

---

## Task 13: `deploy.sh --ui`

**Files:**
- Modify: `tools/deploy.sh`

Parse args:

```
PLUGIN_ONLY=0
UI_ONLY=0
case "${1:-}" in
  --plugin) PLUGIN_ONLY=1 ;;
  --ui) UI_ONLY=1 ;;
  "") ;;
  *) echo "usage: tools/deploy.sh [--plugin|--ui]" >&2; exit 2 ;;
esac
```

Update the header comment to the spec's table.

When `UI_ONLY=0`, keep the existing `dotnet build` of the plugin.

Always (including `--plugin` and `--ui`): if `web/package.json` exists,

```bash
( cd "$ROOT/web" && bun run build )
```

Stage `web/dist/` to `vdgs-stage/ui/` via scp (same space-path workaround as the DLL). On the remote side, copy to `Join-Path $game 'vdgs\ui'`. Replace the destination directory contents so stale hashed assets disappear (delete `$dst\*` then copy, or robocopy /MIR). Do **not** touch sibling splat directories.

When `UI_ONLY=1`, skip `dotnet build`, skip DLL copy, skip splat copy.

When `PLUGIN_ONLY=1`, skip splat copy, still copy DLL and UI.

**Commit.**

```bash
git add tools/deploy.sh
git commit -m "Deploy the control UI to vdgs/ui without rebuilding splat data."
```

---

## Task 14: Docs

**Files:**
- `docs/ARCHITECTURE.md` and `docs/ARCHITECTURE.ja.md` — tree: `GET /` serves `vdgs/ui/`; `WebUi.cs` is the missing-ui fallback. File table: WebUi fallback, WebControl static files, `web/` exists. “Adding UI” becomes “edit `web/`, `bun run build`, deploy `--ui`”. Keep the three security rules; say React text nodes instead of `createElement`.
- `docs/USAGE.md` and `docs/USAGE.ja.md` §5 — ASCII mockup with Control / Library nav. Library: search, metadata, Show. Note `deploy.sh --ui`.
- `AGENTS.md` — plugin list: WebUi is fallback; add `web/` and `<game>/vdgs/ui/`. innerHTML rule: no `dangerouslySetInnerHTML`. `vdgs/ui` is a reserved scene name.

Do not mention a sharing catalog as if it shipped.

**Commit:**

```bash
git add docs AGENTS.md
git commit -m "Document the disk-served control UI and the library page."
```

---

## Done when

- `dotnet test src/VDGS.Tests/VDGS.Tests.csproj` passes
- `cd web && bun test && bun run build` passes
- `rg -n "dangerouslySetInnerHTML|innerHTML" web/src src/VDGS/WebUi.cs` is empty (WebControl must not grow an HTML builder for track names either)
- Real machine (not required to finish the PR, but required before calling it flown): `bash tools/deploy.sh --ui`, open `:8777`, Control actions work, Library search works, removing `vdgs/ui` shows the fallback page and `/api/status` still 200
