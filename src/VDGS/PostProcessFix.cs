using System;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace VDGS
{
    /// <summary>
    /// Silences the Auto Exposure failure caused by running the game under -force-d3d12.
    ///
    /// VelociDrone is built for D3D11, so its PostProcessing v2 compute shaders were only
    /// compiled for that API. Forcing D3D12 (which the splat shaders require) leaves
    /// LogHistogram without its kernels, and every frame throws:
    ///
    ///     Kernel 'KEyeHistogramClear' not found
    ///     UnityEngine.Rendering.PostProcessing.LogHistogram.Generate
    ///
    /// The effect cannot work either way, so switching it off costs nothing and stops the
    /// per-frame exception from drowning the log (and the GC churn that comes with it).
    /// </summary>
    internal static class PostProcessFix
    {
        internal static void DisableAutoExposure(StringBuilder report)
        {
            int scanned = 0, disabled = 0;
            try
            {
                // FindObjectsOfTypeAll also reaches inactive volumes and asset-backed
                // profiles, which is what we want: the failing volume may not be active yet.
                foreach (var v in Resources.FindObjectsOfTypeAll<PostProcessVolume>())
                {
                    if (v == null) continue;
                    scanned++;

                    var profile = v.sharedProfile != null ? v.sharedProfile : v.profile;
                    if (profile == null) continue;

                    AutoExposure ae;
                    if (profile.TryGetSettings(out ae) && ae != null && ae.active)
                    {
                        ae.active = false;
                        disabled++;
                    }
                }
            }
            catch (Exception e)
            {
                report.AppendLine("auto-exposure fix failed: " + e.Message);
                return;
            }

            report.AppendLine("auto-exposure: scanned " + scanned + " volume(s), disabled " + disabled);
        }
    }
}
