using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Stopwatch = System.Diagnostics.Stopwatch;
using UnityEngine;
using VDGS.Lod;
using VDGS.Sog;

namespace VDGS
{
    /// <summary>
    /// Loads a SOG (.sog / meta.json directory) or streamed SOG (lod-meta.json) into the
    /// same resident buffers PlyLoader produces. SH is always Cluster64k.
    /// </summary>
    public static class SogLoader
    {
        private const long kMaxSplats = 2048L * 16384L;

        /// <summary>
        /// path: a directory holding lod-meta.json or meta.json, or a .sog file.
        /// Returns null and fills <paramref name="error"/> on failure — never throws.
        /// </summary>
        public static SplatData Load(string path, out string error, bool mirrorY = true,
                                     Action<string> onStage = null)
        {
            error = null;
            try
            {
                return LoadInner(path, ref error, mirrorY, onStage);
            }
            catch (Exception e)
            {
                // Unwrapped: the Parallel.For passes throw AggregateException, whose message says nothing.
                error = "sog load failed: " + (e is AggregateException ? e.GetBaseException() : e).Message;
                return null;
            }
        }

        private static SplatData LoadInner(string path, ref string error, bool mirrorY, Action<string> onStage)
        {
            var sw = Stopwatch.StartNew();
            double tRead, tDecode, tPack;

            if (string.IsNullOrEmpty(path))
            {
                error = "empty sog path";
                return null;
            }

            ISogFiles files = null;
            string[] metaPaths;
            SsogIndex index = null;
            bool withLod;
            string name;

            try
            {
                if (File.Exists(path) && path.EndsWith(".sog", StringComparison.OrdinalIgnoreCase))
                {
                    files = SogSource.OpenZip(path);
                    metaPaths = new[] { "meta.json" };
                    withLod = false;
                    name = Path.GetFileNameWithoutExtension(path);
                }
                else if (Directory.Exists(path))
                {
                    name = new DirectoryInfo(path).Name;
                    files = SogSource.OpenDirectory(path);
                    string lodMeta = Path.Combine(path, "lod-meta.json");
                    string metaJson = Path.Combine(path, "meta.json");
                    if (File.Exists(lodMeta))
                    {
                        index = SsogIndex.Parse(File.ReadAllBytes(lodMeta));
                        var list = new List<string>(index.Files);
                        if (!string.IsNullOrEmpty(index.Environment))
                            list.Add(index.Environment);
                        metaPaths = list.ToArray();
                        withLod = true;
                    }
                    else if (File.Exists(metaJson))
                    {
                        metaPaths = new[] { "meta.json" };
                        withLod = false;
                    }
                    else
                    {
                        error = "no lod-meta.json or meta.json in " + path;
                        return null;
                    }
                }
                else
                {
                    error = "sog path not found: " + path;
                    return null;
                }

                tRead = sw.Elapsed.TotalMilliseconds;
                sw.Restart();

                int fileCount = metaPaths.Length;
                var chunks = new SogSplats[fileCount];
                // Environment, not SystemInfo: this runs off the main thread.
                int workers = Math.Max(1, Math.Min(Environment.ProcessorCount, 16));
                string decodeError = null;
                object decodeLock = new object();
                int decoded = 0;
                onStage?.Invoke("decoding 0/" + fileCount);

                Parallel.For(0, fileCount, new ParallelOptions { MaxDegreeOfParallelism = workers }, f =>
                {
                    try
                    {
                        chunks[f] = SogChunk.Decode(files, metaPaths[f]);
                        onStage?.Invoke("decoding " + Interlocked.Increment(ref decoded) + "/" + fileCount);
                    }
                    catch (Exception e)
                    {
                        lock (decodeLock)
                        {
                            if (decodeError == null)
                                decodeError = metaPaths[f] + ": " + e.Message;
                        }
                    }
                });

                if (decodeError != null)
                {
                    error = decodeError;
                    return null;
                }

                tDecode = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                onStage?.Invoke("packing");

                var fileBase = new int[fileCount];
                int total = 0;
                for (int f = 0; f < fileCount; f++)
                {
                    fileBase[f] = total;
                    total += chunks[f].Count;
                }

                if (total > kMaxSplats)
                {
                    error = "too many splats for the colour texture: " + total;
                    return null;
                }

                for (int f = 0; f < fileCount; f++)
                {
                    // The index a splat stores is 16 bits, relative to its chunk's palette.
                    // A full 65,536-entry palette is the normal case (splat-transform's
                    // default) and fits exactly: the zero row appended after it is only ever
                    // targeted by a label >= count, which 16 bits cannot express then.
                    if (chunks[f].PaletteCount > 65536)
                    {
                        error = "palette too large for 16-bit index: " + chunks[f].PaletteCount
                                + " in " + metaPaths[f];
                        return null;
                    }
                }

                // Always Cluster64k, even when no chunk carries harmonics: every splat then
                // points at a single zero row. One SH format for every SOG keeps the shader's
                // palette-base addition the only LOD-specific path through LoadSplatData.
                var writer = new SplatWriter(total, withShFloat16: false, clusterSh: true);

                var paletteBase = new int[fileCount];
                int paletteRows = 0;
                for (int f = 0; f < fileCount; f++)
                {
                    paletteBase[f] = paletteRows;
                    // Chunks with SH already include a trailing zero row; band-0 gets one.
                    int rows = chunks[f].ShBands > 0 ? chunks[f].PaletteCount + 1 : 1;
                    paletteRows += rows;
                }
                if (paletteRows < 1) paletteRows = 1;

                // Chunk by chunk in parallel: each owns its rows. On one thread this was half
                // the pack for the bench scene (3.0 s -> 1.4 s on the RTX 3060 box).
                var shData = new byte[paletteRows * 96];
                Parallel.For(0, fileCount, new ParallelOptions { MaxDegreeOfParallelism = workers }, f =>
                {
                    int baseRow = paletteBase[f];
                    var c = chunks[f];
                    if (c.ShBands > 0)
                    {
                        int rows = c.PaletteCount + 1;
                        for (int p = 0; p < rows; p++)
                            WritePaletteRow(shData, (baseRow + p) * 96, c.ShPalette, p * 45, mirrorY);
                    }
                    // else: one zero row already (bytes left at 0)
                });

                string packError = null;
                object packLock = new object();
                var mins = new Vector3[fileCount];
                var maxs = new Vector3[fileCount];

                Parallel.For(0, fileCount, new ParallelOptions { MaxDegreeOfParallelism = workers }, f =>
                {
                    try
                    {
                        var c = chunks[f];
                        int baseI = fileBase[f];
                        float sign = mirrorY ? -1f : 1f;
                        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

                        for (int i = 0; i < c.Count; i++)
                        {
                            int p = i * 3;
                            float px = c.Pos[p];
                            float py = sign * c.Pos[p + 1];
                            float pz = c.Pos[p + 2];
                            if (px < min.x) min.x = px; if (px > max.x) max.x = px;
                            if (py < min.y) min.y = py; if (py > max.y) max.y = py;
                            if (pz < min.z) min.z = pz; if (pz > max.z) max.z = pz;

                            int r = i * 4;
                            float qx = sign * c.Rot[r];
                            float qy = c.Rot[r + 1];
                            float qz = sign * c.Rot[r + 2];
                            float qw = c.Rot[r + 3];

                            writer.Put(baseI + i, px, py, pz, qx, qy, qz, qw,
                                c.Scale[p], c.Scale[p + 1], c.Scale[p + 2],
                                c.Color[p], c.Color[p + 1], c.Color[p + 2],
                                c.Opacity[i]);

                            ushort label = 0;
                            if (c.ShBands > 0 && c.ShLabel != null && i < c.ShLabel.Length)
                                label = c.ShLabel[i];
                            writer.PutShIndex(baseI + i, label);
                        }

                        mins[f] = min;
                        maxs[f] = max;
                    }
                    catch (Exception e)
                    {
                        lock (packLock)
                        {
                            if (packError == null)
                                packError = e.Message;
                        }
                    }
                });

                if (packError != null)
                {
                    error = packError;
                    return null;
                }

                var minAll = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var maxAll = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                for (int f = 0; f < fileCount; f++)
                {
                    if (chunks[f].Count <= 0) continue;
                    minAll = Vector3.Min(minAll, mins[f]);
                    maxAll = Vector3.Max(maxAll, maxs[f]);
                }
                if (total == 0)
                {
                    minAll = Vector3.zero;
                    maxAll = Vector3.zero;
                }

                int shOrder = 0;
                for (int f = 0; f < fileCount; f++)
                    if (chunks[f].ShBands > shOrder) shOrder = chunks[f].ShBands;

                LodInfo lod = null;
                if (withLod)
                    lod = BuildLod(index, chunks, fileBase, paletteBase, metaPaths.Length, total, mirrorY);

                tPack = sw.Elapsed.TotalMilliseconds;
                double totalMs = tRead + tDecode + tPack;

                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[VDGS] sog '{0}' {1:N0} splats {2} files  read {3:0} ms decode {4:0} ms pack {5:0} ms  total {6:0.00} s",
                    name, total, fileCount, tRead, tDecode, tPack, totalMs / 1000.0));

                return SplatData.FromBuffers(
                    name, total, minAll, maxAll,
                    SplatData.VectorFormat.Float32, SplatData.VectorFormat.Float32,
                    SplatData.ColorFormat.Float16x4, SplatData.SHFormat.Cluster64k,
                    writer.Pos, writer.Other, writer.Color, shData, null,
                    shOrder, lod);
            }
            finally
            {
                files?.Dispose();
            }
        }

        private static LodInfo BuildLod(
            SsogIndex index, SogSplats[] chunks, int[] fileBase, int[] paletteBase,
            int fileCount, int total, bool mirrorY)
        {
            int envFile = -1;
            if (!string.IsNullOrEmpty(index.Environment))
                envFile = fileCount - 1; // appended as the last decoded file

            int leafCount = index.Leaves.Length + (envFile >= 0 ? 1 : 0);
            int runCount = index.Runs.Length + (envFile >= 0 ? 1 : 0);
            if (runCount == 0) throw new SogException("lod-meta.json has no runs");

            var leaves = new LodBox[leafCount];
            for (int i = 0; i < index.Leaves.Length; i++)
            {
                var L = index.Leaves[i];
                if (mirrorY)
                {
                    leaves[i] = new LodBox
                    {
                        MinX = L.MinX, MaxX = L.MaxX,
                        MinY = -L.MaxY, MaxY = -L.MinY,
                        MinZ = L.MinZ, MaxZ = L.MaxZ
                    };
                }
                else
                {
                    leaves[i] = new LodBox
                    {
                        MinX = L.MinX, MaxX = L.MaxX,
                        MinY = L.MinY, MaxY = L.MaxY,
                        MinZ = L.MinZ, MaxZ = L.MaxZ
                    };
                }
            }
            if (envFile >= 0)
            {
                // Huge box so nearest-point distance is always 0 → LodSelector stays on level 0.
                leaves[index.Leaves.Length] = new LodBox
                {
                    MinX = float.MinValue, MinY = float.MinValue, MinZ = float.MinValue,
                    MaxX = float.MaxValue, MaxY = float.MaxValue, MaxZ = float.MaxValue
                };
            }

            var runOffset = new int[runCount];
            var runCountArr = new int[runCount];
            var runLeaf = new int[runCount];
            var runLevel = new int[runCount];
            var runShBase = new int[runCount];

            for (int r = 0; r < index.Runs.Length; r++)
            {
                var run = index.Runs[r];
                if ((long)run.Offset + run.Count > chunks[run.File].Count)
                    throw new SogException("lod run " + r + " (leaf " + run.Leaf + ", level " + run.Level
                        + ") ends past file " + run.File + "'s " + chunks[run.File].Count + " splats");
                runOffset[r] = fileBase[run.File] + run.Offset;
                runCountArr[r] = run.Count;
                runLeaf[r] = run.Leaf;
                runLevel[r] = run.Level;
                runShBase[r] = paletteBase[run.File];
            }

            if (envFile >= 0)
            {
                int r = index.Runs.Length;
                runOffset[r] = fileBase[envFile];
                runCountArr[r] = chunks[envFile].Count;
                runLeaf[r] = index.Leaves.Length;
                runLevel[r] = 0;
                runShBase[r] = paletteBase[envFile];
            }

            // Every splat in exactly one run, or GPU compaction and the CPU sort count disagree.
            const uint Unowned = uint.MaxValue;
            var runOfSplat = new uint[total];
            for (int i = 0; i < total; i++) runOfSplat[i] = Unowned;
            for (int r = 0; r < runCount; r++)
            {
                int lo = runOffset[r];
                int hi = lo + runCountArr[r];
                for (int i = lo; i < hi; i++)
                {
                    if (runOfSplat[i] != Unowned)
                        throw new SogException("lod runs " + runOfSplat[i] + " and " + r + " overlap at splat " + i);
                    runOfSplat[i] = (uint)r;
                }
            }
            for (int i = 0; i < total; i++)
                if (runOfSplat[i] == Unowned)
                    throw new SogException("splat " + i + " belongs to no lod run");

            return new LodInfo
            {
                Leaves = leaves,
                LevelCount = index.LevelCount,
                RunOffset = runOffset,
                RunCount = runCountArr,
                RunLeaf = runLeaf,
                RunLevel = runLevel,
                RunShBase = runShBase,
                RunOfSplat = runOfSplat
            };
        }

        private static void WritePaletteRow(byte[] shData, int byteOff, float[] palette, int floatOff, bool mirrorY)
        {
            for (int k = 0; k < 15; k++)
            {
                float s = (mirrorY && (k == 0 || k == 3 || k == 4 || k == 8 || k == 9 || k == 10)) ? -1f : 1f;
                int src = floatOff + k * 3;
                float r = s * palette[src];
                float g = s * palette[src + 1];
                float b = s * palette[src + 2];
                PutHalf(shData, byteOff + k * 6, r);
                PutHalf(shData, byteOff + k * 6 + 2, g);
                PutHalf(shData, byteOff + k * 6 + 4, b);
            }
            // 6 bytes of padding to 96 already zero.
        }

        private static void PutHalf(byte[] dst, int off, float v)
        {
            ushort h = Mathf.FloatToHalf(v);
            dst[off] = (byte)h;
            dst[off + 1] = (byte)(h >> 8);
        }
    }
}
