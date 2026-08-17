using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VDGS
{
    /// <summary>
    /// Writes runtime facts we cannot learn by reading files on disk: which graphics
    /// device the game actually got, whether compute shaders are usable, and how the
    /// camera stack is arranged in a live scene. All of it decides whether a Gaussian
    /// Splat renderer can be attached at all.
    /// </summary>
    internal static class Probe
    {
        internal static string LogPath;

        internal static void Write(string tag)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("======== " + tag + " @ " + DateTime.Now.ToString("HH:mm:ss.fff") + " ========");

                sb.AppendLine("-- engine --");
                sb.AppendLine("unityVersion       = " + Application.unityVersion);
                sb.AppendLine("platform           = " + Application.platform);
                sb.AppendLine("dataPath           = " + Application.dataPath);

                sb.AppendLine("-- graphics device --");
                sb.AppendLine("graphicsDeviceType = " + SystemInfo.graphicsDeviceType);
                sb.AppendLine("graphicsDeviceName = " + SystemInfo.graphicsDeviceName);
                sb.AppendLine("graphicsDeviceVer  = " + SystemInfo.graphicsDeviceVersion);
                sb.AppendLine("shaderLevel        = " + SystemInfo.graphicsShaderLevel);
                sb.AppendLine("graphicsMemorySize = " + SystemInfo.graphicsMemorySize + " MB");

                sb.AppendLine("-- compute capability (3DGS depends on all of these) --");
                sb.AppendLine("supportsComputeShaders   = " + SystemInfo.supportsComputeShaders);
                sb.AppendLine("supportsInstancing       = " + SystemInfo.supportsInstancing);
                sb.AppendLine("supportsAsyncCompute     = " + SystemInfo.supportsAsyncCompute);
                sb.AppendLine("supportsSetConstantBuffer= " + SystemInfo.supportsSetConstantBuffer);
                sb.AppendLine("maxComputeBufferInputsCompute = " + SystemInfo.maxComputeBufferInputsCompute);
                sb.AppendLine("maxComputeWorkGroupSize  = " + SystemInfo.maxComputeWorkGroupSize);
                sb.AppendLine("copyTextureSupport       = " + SystemInfo.copyTextureSupport);
                sb.AppendLine("usesReversedZBuffer      = " + SystemInfo.usesReversedZBuffer);
                sb.AppendLine("graphicsUVStartsAtTop    = " + SystemInfo.graphicsUVStartsAtTop);

                sb.AppendLine("-- render texture formats used by splat pipelines --");
                LogFormat(sb, RenderTextureFormat.ARGBFloat);
                LogFormat(sb, RenderTextureFormat.ARGBHalf);
                LogFormat(sb, RenderTextureFormat.RFloat);
                LogFormat(sb, RenderTextureFormat.RInt);

                var active = SceneManager.GetActiveScene();
                sb.AppendLine("-- scene --");
                sb.AppendLine("activeScene        = " + active.name + " (buildIndex " + active.buildIndex + ")");
                sb.AppendLine("sceneCount         = " + SceneManager.sceneCount);
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var s = SceneManager.GetSceneAt(i);
                    sb.AppendLine("  scene[" + i + "] = " + s.name + " loaded=" + s.isLoaded + " rootCount=" + s.rootCount);
                }

                sb.AppendLine("-- cameras (a splat pass must hook the right one) --");
                var cams = Camera.allCameras;
                sb.AppendLine("cameraCount        = " + cams.Length);
                foreach (var c in cams)
                {
                    if (c == null) continue;
                    sb.AppendLine(string.Format(
                        "  '{0}' depth={1} tag={2} clear={3} cullingMask=0x{4:X8} target={5} hdr={6} allowMSAA={7} fov={8} near={9} far={10} path={11}",
                        c.name, c.depth, c.tag, c.clearFlags, c.cullingMask,
                        c.targetTexture == null ? "screen" : c.targetTexture.name,
                        c.allowHDR, c.allowMSAA, c.fieldOfView, c.nearClipPlane, c.farClipPlane,
                        FullPath(c.transform)));
                }

                sb.AppendLine("-- quality --");
                sb.AppendLine("qualityLevel       = " + QualitySettings.GetQualityLevel() + " '" + QualitySettings.names[QualitySettings.GetQualityLevel()] + "'");
                sb.AppendLine("colorSpace         = " + QualitySettings.activeColorSpace);
                sb.AppendLine();

                File.AppendAllText(LogPath, sb.ToString());
            }
            catch (Exception e)
            {
                try { File.AppendAllText(LogPath, "PROBE FAILED: " + e + "\n"); } catch { }
            }
        }

        private static void LogFormat(StringBuilder sb, RenderTextureFormat f)
        {
            sb.AppendLine("  " + f + " supported=" + SystemInfo.SupportsRenderTextureFormat(f));
        }

        internal static string FullPath(Transform t)
        {
            var sb = new StringBuilder(t.name);
            while (t.parent != null)
            {
                t = t.parent;
                sb.Insert(0, t.name + "/");
            }
            return sb.ToString();
        }
    }
}
