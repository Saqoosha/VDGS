using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace VDGS
{
    /// <summary>
    /// Loads the Gaussian Splatting shaders from an AssetBundle built with the game's
    /// own Unity version. Shaders cannot be compiled at runtime, and the game's own
    /// assemblies contain none of them, so this bundle is the only way to get the
    /// splat rasterizer and the sorting compute kernels into the process.
    /// </summary>
    internal static class ShaderBundle
    {
        internal const string BundleFileName = "vdgs-shaders";

        private static AssetBundle s_Bundle;

        internal static Shader SplatShader { get; private set; }
        internal static Shader CompositeShader { get; private set; }
        internal static Shader DebugPointsShader { get; private set; }
        internal static Shader DebugBoxesShader { get; private set; }
        /// <summary>Flat unlit colour, used for the backdrop box (see SplatBackdrop).</summary>
        internal static Shader BlackShader { get; private set; }
        internal static ComputeShader SplatUtilities { get; private set; }

        internal static bool Loaded => SplatShader != null && CompositeShader != null && SplatUtilities != null;

        /// <summary>Loads the bundle from &lt;game&gt;/vdgs/. Safe to call twice.</summary>
        internal static bool Load(string vdgsDir, StringBuilder report)
        {
            if (s_Bundle != null)
            {
                report.AppendLine("bundle already loaded");
                return Loaded;
            }

            var path = Path.Combine(vdgsDir, BundleFileName);
            report.AppendLine("bundle path = " + path);

            if (!File.Exists(path))
            {
                report.AppendLine("MISSING - build it with unity/VDGSBundler and deploy");
                return false;
            }
            report.AppendLine("bundle size = " + new FileInfo(path).Length + " bytes");

            try
            {
                s_Bundle = AssetBundle.LoadFromFile(path);
            }
            catch (Exception e)
            {
                report.AppendLine("LoadFromFile threw: " + e.Message);
                return false;
            }

            if (s_Bundle == null)
            {
                report.AppendLine("LoadFromFile returned null (built with a different Unity version?)");
                return false;
            }

            var names = s_Bundle.GetAllAssetNames();
            report.AppendLine("assets in bundle = " + names.Length);
            foreach (var n in names)
                report.AppendLine("  " + n);

            // Shader assets keep their source names, so match on the file stem.
            var shaders = s_Bundle.LoadAllAssets<Shader>();
            var computes = s_Bundle.LoadAllAssets<ComputeShader>();
            report.AppendLine("loaded Shader objects  = " + shaders.Length);
            report.AppendLine("loaded Compute objects = " + computes.Length);

            var byName = new Dictionary<string, Shader>();
            foreach (var s in shaders)
            {
                report.AppendLine("  shader '" + s.name + "' supported=" + s.isSupported);
                byName[s.name] = s;
            }
            foreach (var c in computes)
                report.AppendLine("  compute '" + c.name + "' supported=" + c.HasKernel("CSCalcViewData"));

            SplatShader = Find(byName, "Gaussian Splatting/Render Splats", "RenderGaussianSplats");
            CompositeShader = Find(byName, "Gaussian Splatting/Composite", "GaussianComposite");
            DebugPointsShader = Find(byName, "Gaussian Splatting/Debug/Render Points", "GaussianDebugRenderPoints");
            DebugBoxesShader = Find(byName, "Gaussian Splatting/Debug/Render Boxes", "GaussianDebugRenderBoxes");
            BlackShader = Find(byName, "Unlit/BlackSkybox", "BlackSkybox");
            SplatUtilities = computes.Length > 0 ? computes[0] : null;

            report.AppendLine("resolved splats="     + (SplatShader != null)
                            + " composite="          + (CompositeShader != null)
                            + " debugPoints="        + (DebugPointsShader != null)
                            + " debugBoxes="         + (DebugBoxesShader != null)
                            + " black="              + (BlackShader != null)
                            + " splatUtilities="     + (SplatUtilities != null));

            return Loaded;
        }

        private static Shader Find(Dictionary<string, Shader> byName, params string[] candidates)
        {
            foreach (var c in candidates)
                if (byName.TryGetValue(c, out var s))
                    return s;

            // Fall back to a substring match: shader display names are set inside the
            // .shader file and may not match the filename we built from.
            foreach (var kv in byName)
                foreach (var c in candidates)
                    if (kv.Key.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0)
                        return kv.Value;

            return null;
        }
    }
}
