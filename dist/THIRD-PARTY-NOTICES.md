# Third-party notices

VDGS bundles or derives from the following components.

---

## UnityGaussianSplatting

The splat renderer, the sorting driver and the shaders in `vdgs-shaders` are
derived from **aras-p/UnityGaussianSplatting**, with the editing tools, URP and
HDRP passes and the Burst/Collections dependencies removed.

- https://github.com/aras-p/UnityGaussianSplatting
- Copyright (c) 2023 Aras Pranckevičius
- SPDX-License-Identifier: MIT

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## GPUSorting

The GPU radix sort (`GpuSorting.cs` and `DeviceRadixSort.hlsl`, reached through
UnityGaussianSplatting) comes from **b0nes164/GPUSorting**.

- https://github.com/b0nes164/GPUSorting
- Copyright (c) 2024 Thomas Smith
- SPDX-License-Identifier: MIT

---

## BepInEx

**Not included in this package.** `install.ps1` downloads it from the official
release page at install time.

- https://github.com/BepInEx/BepInEx
- License: LGPL-2.1

---

## Newtonsoft.Json

**Not included.** The plugin references the copy that ships with VelociDrone
itself (`velocidrone_Data/Managed/Newtonsoft.Json.dll`); nothing is
redistributed here.

- https://www.newtonsoft.com/json
- License: MIT
