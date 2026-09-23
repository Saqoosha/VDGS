using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace VDGS
{
    /// <summary>
    /// Draws all registered splat renderers for a camera.
    ///
    /// Ported from aras-p/UnityGaussianSplatting (MIT). Differences from upstream:
    ///   - built-in render pipeline only (VelociDrone uses BiRP, so the URP/HDRP hooks are gone)
    ///   - no ProfilerMarker (Unity.Profiling is not referenced by the plugin)
    ///   - no editing / selection / cutout support, only rendering
    /// </summary>
    internal class SplatRenderSystem
    {
        internal static SplatRenderSystem instance => ms_Instance ?? (ms_Instance = new SplatRenderSystem());
        private static SplatRenderSystem ms_Instance;

        private readonly Dictionary<SplatRenderer, MaterialPropertyBlock> m_Splats =
            new Dictionary<SplatRenderer, MaterialPropertyBlock>();
        private readonly HashSet<Camera> m_CameraCommandBuffersDone = new HashSet<Camera>();
        private readonly List<KeyValuePair<SplatRenderer, MaterialPropertyBlock>> m_ActiveSplats =
            new List<KeyValuePair<SplatRenderer, MaterialPropertyBlock>>();

        private CommandBuffer m_CommandBuffer;

        /// <summary>Set to restrict rendering to one camera; null renders to every camera.</summary>
        internal static Func<Camera, bool> CameraFilter;

        internal void RegisterSplat(SplatRenderer r)
        {
            if (m_Splats.Count == 0)
                Camera.onPreCull += OnPreCullCamera;
            m_Splats[r] = new MaterialPropertyBlock();
        }

        internal void UnregisterSplat(SplatRenderer r)
        {
            if (!m_Splats.ContainsKey(r))
                return;
            m_Splats.Remove(r);
            if (m_Splats.Count != 0)
                return;

            if (m_CommandBuffer != null)
            {
                foreach (var cam in m_CameraCommandBuffersDone)
                    if (cam != null)
                        cam.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, m_CommandBuffer);
            }
            m_CameraCommandBuffersDone.Clear();
            m_ActiveSplats.Clear();
            if (m_CommandBuffer != null)
            {
                m_CommandBuffer.Dispose();
                m_CommandBuffer = null;
            }
            Camera.onPreCull -= OnPreCullCamera;
        }

        private bool GatherSplatsForCamera(Camera cam)
        {
            if (cam.cameraType == CameraType.Preview)
                return false;
            if (CameraFilter != null && !CameraFilter(cam))
                return false;

            m_ActiveSplats.Clear();
            foreach (var kvp in m_Splats)
            {
                var gs = kvp.Key;
                if (gs == null || !gs.isActiveAndEnabled || !gs.HasValidData || !gs.HasValidRenderSetup)
                    continue;
                m_ActiveSplats.Add(kvp);
            }
            if (m_ActiveSplats.Count == 0)
                return false;

            var camTr = cam.transform;
            m_ActiveSplats.Sort((a, b) =>
            {
                if (a.Key.m_RenderOrder != b.Key.m_RenderOrder)
                    return b.Key.m_RenderOrder.CompareTo(a.Key.m_RenderOrder);
                var posA = camTr.InverseTransformPoint(a.Key.transform.position);
                var posB = camTr.InverseTransformPoint(b.Key.transform.position);
                return posA.z.CompareTo(posB.z);
            });
            return true;
        }

        private Material SortAndRenderSplats(Camera cam, CommandBuffer cmb)
        {
            Material matComposite = null;
            foreach (var kvp in m_ActiveSplats)
            {
                var gs = kvp.Key;
                gs.EnsureMaterials();
                matComposite = gs.m_MatComposite;
                var mpb = kvp.Value;

                var matrix = gs.transform.localToWorldMatrix;
                if (gs.m_FrameCounter % Mathf.Max(1, gs.m_SortNthFrame) == 0)
                    gs.SortPoints(cmb, cam, matrix);
                ++gs.m_FrameCounter;

                mpb.Clear();
                var displayMat = gs.m_MatSplats;
                if (displayMat == null)
                    continue;

                gs.SetDataOnMaterial(mpb);
                mpb.SetBuffer(Props.SplatChunks, gs.m_GpuChunks);
                mpb.SetBuffer(Props.SplatViewData, gs.m_GpuView);
                mpb.SetBuffer(Props.OrderBuffer, gs.m_GpuSortKeys);
                mpb.SetFloat(Props.SplatScale, gs.m_SplatScale);
                mpb.SetFloat(Props.SplatOpacityScale, gs.m_OpacityScale);
                mpb.SetFloat(Props.SplatSize, gs.m_PointDisplaySize);
                mpb.SetFloat(Props.SplatGaussCut, gs.m_GaussCut);
                mpb.SetFloat(Props.SplatDepthClip, gs.m_DepthClip ? 1f : 0f);
                int shOrder = Mathf.Min(gs.m_SHOrder, gs.Data.ShOrder);
                mpb.SetInt(Props.SHOrder, shOrder);
                mpb.SetInt(Props.SplatSHOrder, shOrder);
                mpb.SetInt(Props.SHOnly, gs.m_SHOnly ? 1 : 0);
                mpb.SetInt(Props.DisplayIndex, 0);
                mpb.SetInt(Props.DisplayChunks, 0);

                gs.CalcViewData(cmb, cam);

                // Indirect, so the instance count comes from the GPU-side cull rather
                // than from the CPU guessing how much of the scene is on screen.
                cmb.DrawProceduralIndirect(gs.m_GpuIndexBuffer, matrix, displayMat, 0,
                    MeshTopology.Triangles, gs.m_GpuDrawArgs, 0, mpb);
            }
            return matComposite;
        }

        private void OnPreCullCamera(Camera cam)
        {
            if (!GatherSplatsForCamera(cam))
                return;

            if (m_CommandBuffer == null)
                m_CommandBuffer = new CommandBuffer { name = "VDGS RenderGaussianSplats" };
            if (cam != null && !m_CameraCommandBuffersDone.Contains(cam))
            {
                cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, m_CommandBuffer);
                m_CameraCommandBuffersDone.Add(cam);
            }
            m_CommandBuffer.Clear();

            // Explicit size rather than upstream's (-1, -1): the camera-relative size is what
            // the offline RenderCompare harness needed to get the RT bound at all. In the
            // game the size was fine and the depth attachment was the problem - see below.
            m_CommandBuffer.GetTemporaryRT(Props.GaussianSplatRT, cam.pixelWidth, cam.pixelHeight,
                0, FilterMode.Point, GraphicsFormat.R16G16B16A16_SFloat);
            // Colour-only bind, deliberately. Upstream binds the camera depth as well
            // (SetRenderTarget(rt, BuiltinRenderTextureType.CurrentActive)); on the game's
            // HDR + PostProcessing cameras under D3D12 that pair silently invalidates the
            // whole bind - the splats draw straight into the camera target, the composite
            // reads an empty RT and passes through, and every dim splat gets the linear
            // pipeline's sRGB lift (the grey-green veil over dark sky that SuperSplat never
            // shows). Proven in-game with a three-way probe composite: red (RT empty) with
            // the depth bind, green (RT live) without it. Scene-depth occlusion is done in
            // the splat fragment shader against _CameraDepthTexture instead, so the
            // camera is asked for a depth texture below.
            m_CommandBuffer.SetRenderTarget(Props.GaussianSplatRT);
            m_CommandBuffer.ClearRenderTarget(RTClearFlags.Color, new Color(0, 0, 0, 0), 0, 0);
            cam.depthTextureMode |= DepthTextureMode.Depth;

            // Only used to detect whether we render into the backbuffer; BiRP-only trick.
            m_CommandBuffer.SetGlobalTexture(Props.CameraTargetTexture, BuiltinRenderTextureType.CameraTarget);

            var matComposite = SortAndRenderSplats(cam, m_CommandBuffer);

            if (matComposite != null)
            {
                m_CommandBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                m_CommandBuffer.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 3, 1);
            }
            m_CommandBuffer.ReleaseTemporaryRT(Props.GaussianSplatRT);
        }
    }

    /// <summary>Shader property ids, matching the names in the splat HLSL.</summary>
    internal static class Props
    {
        internal static readonly int SplatPos = Shader.PropertyToID("_SplatPos");
        internal static readonly int SplatOther = Shader.PropertyToID("_SplatOther");
        internal static readonly int SplatSH = Shader.PropertyToID("_SplatSH");
        internal static readonly int SplatColor = Shader.PropertyToID("_SplatColor");
        internal static readonly int SplatSelectedBits = Shader.PropertyToID("_SplatSelectedBits");
        internal static readonly int SplatDeletedBits = Shader.PropertyToID("_SplatDeletedBits");
        internal static readonly int SplatBitsValid = Shader.PropertyToID("_SplatBitsValid");
        internal static readonly int SplatFormat = Shader.PropertyToID("_SplatFormat");
        internal static readonly int SplatGaussCut = Shader.PropertyToID("_SplatGaussCut");
        internal static readonly int SplatDepthClip = Shader.PropertyToID("_SplatDepthClip");
        internal static readonly int CullCenterSlack = Shader.PropertyToID("_CullCenterSlack");
        internal static readonly int SplatDropDegenerate = Shader.PropertyToID("_SplatDropDegenerate");
        internal static readonly int SplatChunks = Shader.PropertyToID("_SplatChunks");
        internal static readonly int SplatChunkCount = Shader.PropertyToID("_SplatChunkCount");
        internal static readonly int SplatViewData = Shader.PropertyToID("_SplatViewData");
        internal static readonly int OrderBuffer = Shader.PropertyToID("_OrderBuffer");
        internal static readonly int SplatScale = Shader.PropertyToID("_SplatScale");
        internal static readonly int SplatOpacityScale = Shader.PropertyToID("_SplatOpacityScale");
        internal static readonly int SplatSize = Shader.PropertyToID("_SplatSize");
        internal static readonly int SplatCount = Shader.PropertyToID("_SplatCount");
        internal static readonly int SHOrder = Shader.PropertyToID("_SHOrder");
        internal static readonly int SplatSHOrder = Shader.PropertyToID("_SplatSHOrder");
        internal static readonly int SHOnly = Shader.PropertyToID("_SHOnly");
        internal static readonly int DisplayIndex = Shader.PropertyToID("_DisplayIndex");
        internal static readonly int DisplayChunks = Shader.PropertyToID("_DisplayChunks");
        internal static readonly int GaussianSplatRT = Shader.PropertyToID("_GaussianSplatRT");
        internal static readonly int SplatSortKeys = Shader.PropertyToID("_SplatSortKeys");
        internal static readonly int MatrixObjectToClip = Shader.PropertyToID("_MatrixObjectToClip");
        internal static readonly int CullEnabled = Shader.PropertyToID("_CullEnabled");
        internal static readonly int CullMargin = Shader.PropertyToID("_CullMargin");
        internal static readonly int CullProjScale = Shader.PropertyToID("_CullProjScale");
        internal static readonly int CullRadiusScale = Shader.PropertyToID("_CullRadiusScale");
        internal static readonly int SplatVisibleCount = Shader.PropertyToID("_SplatVisibleCount");
        internal static readonly int SplatDrawArgs = Shader.PropertyToID("_SplatDrawArgs");
        internal static readonly int SplatChunkRadius = Shader.PropertyToID("_SplatChunkRadius");
        internal static readonly int SplatSortDistances = Shader.PropertyToID("_SplatSortDistances");
        internal static readonly int SplatRun = Shader.PropertyToID("_SplatRun");
        internal static readonly int RunInfo = Shader.PropertyToID("_RunInfo");
        internal static readonly int SortCount = Shader.PropertyToID("_SortCount");
        internal static readonly int SplatActiveCount = Shader.PropertyToID("_SplatActiveCount");
        internal static readonly int MatrixMV = Shader.PropertyToID("_MatrixMV");
        internal static readonly int MatrixObjectToWorld = Shader.PropertyToID("_MatrixObjectToWorld");
        internal static readonly int MatrixWorldToObject = Shader.PropertyToID("_MatrixWorldToObject");
        internal static readonly int VecScreenParams = Shader.PropertyToID("_VecScreenParams");
        internal static readonly int VecWorldSpaceCameraPos = Shader.PropertyToID("_VecWorldSpaceCameraPos");
        internal static readonly int CameraTargetTexture = Shader.PropertyToID("_CameraTargetTexture");
        internal static readonly int SplatCutoutsCount = Shader.PropertyToID("_SplatCutoutsCount");
        internal static readonly int SplatCutouts = Shader.PropertyToID("_SplatCutouts");
    }

    /// <summary>
    /// Renders one Gaussian Splat scene. Added at runtime by the plugin; there is no
    /// inspector, so every knob is a plain public field set from code.
    /// </summary>
    public class SplatRenderer : MonoBehaviour
    {
        public int m_RenderOrder;
        public float m_SplatScale = 1.0f;
        public float m_OpacityScale = 1.0f;
        public int m_SHOrder = 3;
        /// <summary>
        /// Squared-radius cut for each gaussian, matching the web viewers' `if (A &lt; -4.0)
        /// discard;` - two sigma. Without it every splat keeps a 1-5/255 ring out to where
        /// alpha crosses 1/255, and a million of those rings sum into visible haze. 0 = off.
        /// </summary>
        public float m_GaussCut = 4f;

        /// <summary>Manual scene-depth occlusion in the splat shader; off makes splats draw over everything.</summary>
        public bool m_DepthClip = true;

        /// <summary>
        /// Cull a splat when its centre leaves this multiple of the frustum, the way the web
        /// viewers do (`float clip = 1.2 * pos2d.w; if (pos2d.x &lt; -clip ...) return;`).
        ///
        /// This is what makes flying inside a capture look right. The radius-margin cull
        /// above deliberately keeps a splat whose centre is off screen but whose body
        /// reaches in - correct when looking at a capture from outside, catastrophic when
        /// the camera sits among splats a few centimetres away: each projects to thousands
        /// of pixels and dozens of them wash the whole frame, which is the "fog" and the
        /// "large splats" this scene showed for days. Measured at the same camera as a web
        /// viewer: sky 6.20 -&gt; 0.04 out of 255 (web reference: 0.00), lawn unchanged.
        ///
        /// 0 disables it and restores upstream behaviour.
        /// </summary>
        public float m_CullCenterSlack = 1.2f;

        /// <summary>
        /// Drop splats whose minor eigenvalue went negative rather than clamping them up to
        /// a sliver, matching the web viewers' `if (lambda2 &lt; 0.0) return;`. A clamped
        /// degenerate gaussian draws as a long thin streak.
        /// </summary>
        public bool m_DropDegenerate = true;
        public bool m_SHOnly;
        public int m_SortNthFrame = 1;

        /// <summary>
        /// Skip splats outside the view. Costs nothing in quality - they are not on
        /// screen - and per-splat work is 87% of the frame, so this is the only large
        /// lossless win available. How much it saves depends entirely on which way the
        /// camera looks: measured on drjohnson, between 31% and 97% of the capture falls
        /// inside a 120 degree frustum.
        /// </summary>
        public bool m_FrustumCulling = true;

        /// <summary>
        /// Sigma multiplier on each splat's own size, used as its frustum margin.
        ///
        /// The test is on centres, so a splat whose centre has just left the view but
        /// whose skirt has not must still be kept. Bounding each splat by its own radius
        /// rather than by one number for the whole scene is what makes the margin small:
        /// captures hold a few enormous diffuse gaussians, and a global margin has to
        /// cover those, so every small splat pays for them.
        ///
        /// 4 is measured, not chosen - the value was raised until the culled image
        /// matched the unculled one exactly. drjohnson from inside, at 120 degrees:
        ///
        ///     sigma 1   mean pixel difference 2.78/255    5.7% of pixels identical
        ///     sigma 2    1.07                            34.8%
        ///     sigma 3    0.0007                          99.8%
        ///     sigma 4    0.00                           100%
        ///
        /// The drawn quad is +/-2 sigma in the covariance axes, so 4 is two quads' worth
        /// of slack over the projection.
        /// </summary>
        public float m_CullMargin = 4f;
        public float m_PointDisplaySize = 3.0f;

        // Matches GaussianCutout.ShaderData: Matrix4x4 + uint.
        private const int kCutoutDataSize = 68;
        private const int kGpuViewDataSize = 40;

        // Kernel order must match the #pragma kernel order in SplatUtilities.compute.
        private enum KernelIndices
        {
            SetIndices = 0,
            CalcDistances = 1,
            CalcViewData = 2,
        }

        private SplatData m_Data;
        private int m_SplatCount;

        private GraphicsBuffer m_GpuSortDistances;
        internal GraphicsBuffer m_GpuSortKeys;
        private GraphicsBuffer m_GpuPosData;
        private GraphicsBuffer m_GpuOtherData;
        private GraphicsBuffer m_GpuSHData;
        private Texture m_GpuColorData;
        internal GraphicsBuffer m_GpuChunks;
        private GraphicsBuffer m_GpuVisibleCount;
        private GraphicsBuffer m_GpuChunkRadius;
        internal GraphicsBuffer m_GpuDrawArgs;   // read by the render system's indirect draw
        internal bool m_GpuChunksValid;
        internal GraphicsBuffer m_GpuView;
        internal GraphicsBuffer m_GpuIndexBuffer;
        // The compute shader always binds a cutouts buffer even when the count is zero.
        private GraphicsBuffer m_GpuCutoutsDummy;

        // Level of detail. Only bound, and only declared by the shader, under VDGS_LOD;
        // a capture without levels runs a variant that has never heard of them.
        private GraphicsBuffer m_GpuSplatRun;
        private GraphicsBuffer m_GpuRunInfo;
        private GraphicsBuffer m_GpuActiveCount;
        // Splats in the key buffer: the selected set, or all of them without levels.
        private int m_SortCount;
        private bool m_NeedCompact;
        private VDGS.Lod.LodSelector m_LodSelector;
        private byte[] m_RunActive;
        private uint[] m_RunInfoWords;
        private int m_LodFrame = -1;

        /// <summary>Apparent size (leaf extent / distance) at which a leaf draws its finest level.</summary>
        public float LodDetail = 1f;
        /// <summary>Splats the selection tries to stay under, coarsening the farthest leaves first.</summary>
        public long LodBudget = 3000000;
        /// <summary>Per-leaf spread of the level bands, so neighbours do not switch together.</summary>
        public float LodBandJitter = 0f;

        /// <summary>Active splats per level, or null for a capture without levels.</summary>
        public long[] LodActivePerLevel => m_LodSelector?.ActivePerLevel;
        public int LodLeaves => m_Data?.Lod?.Leaves.Length ?? 0;

        private GpuSorting m_Sorter;
        private GpuSorting.Args m_SorterArgs;

        internal Material m_MatSplats;
        internal Material m_MatComposite;

        internal int m_FrameCounter;
        private bool m_Registered;

        public int SplatCount => m_SplatCount;
        public SplatData Data => m_Data;

        internal bool HasValidData => m_Data != null && m_Data.SplatCount > 0;
        internal bool HasValidRenderSetup => m_GpuPosData != null && m_GpuOtherData != null && m_GpuChunks != null;

        private static bool ResourcesReady =>
            ShaderBundle.Loaded && SystemInfo.supportsComputeShaders;

        /// <summary>Assigns the scene to draw. Recreates all GPU resources.</summary>
        public void SetData(SplatData data)
        {
            if (m_Data == data)
                return;
            m_Data = data;
            if (!ResourcesReady)
                return;
            DisposeResources();
            EnsureMaterials();
            EnsureSorterAndRegister();
            CreateResources();
        }

        private void OnEnable()
        {
            m_FrameCounter = 0;
            if (!ResourcesReady || !HasValidData)
                return;
            EnsureMaterials();
            EnsureSorterAndRegister();
            CreateResources();
        }

        private void OnDisable()
        {
            DisposeResources();
            SplatRenderSystem.instance.UnregisterSplat(this);
            m_Registered = false;

            DestroyImmediate(m_MatSplats);
            DestroyImmediate(m_MatComposite);
            m_MatSplats = null;
            m_MatComposite = null;
        }

        internal void EnsureMaterials()
        {
            if (m_MatSplats != null || !ResourcesReady)
                return;
            m_MatSplats = new Material(ShaderBundle.SplatShader) { name = "VDGS Splats" };
            m_MatComposite = new Material(ShaderBundle.CompositeShader) { name = "VDGS Composite" };
        }

        internal void EnsureSorterAndRegister()
        {
            if (m_Sorter == null && ResourcesReady)
                m_Sorter = new GpuSorting(ShaderBundle.SplatUtilities);
            if (!m_Registered && ResourcesReady)
            {
                SplatRenderSystem.instance.RegisterSplat(this);
                m_Registered = true;
            }
        }

        private void CreateResources()
        {
            if (!HasValidData)
                return;

            m_SplatCount = m_Data.SplatCount;

            m_GpuPosData = RawBuffer(m_Data.PosData, "VDGS PosData");
            m_GpuOtherData = RawBuffer(m_Data.OtherData, "VDGS OtherData");
            m_GpuSHData = RawBuffer(m_Data.ShData, "VDGS SHData");

            SplatData.CalcTextureSize(m_SplatCount, out int texWidth, out int texHeight);
            // Unity 2021.3 has no DontInitializePixels/DontUploadUponCreate creation flags
            // and no GraphicsFormat Texture2D overload, so go through TextureFormat.
            var tex = new Texture2D(texWidth, texHeight, ColorFormatToTexture(m_Data.ColorFmt), false)
            { name = "VDGS ColorData" };
            tex.SetPixelData(m_Data.ColorData, 0);
            tex.Apply(false, true);
            m_GpuColorData = tex;

            if (m_Data.HasChunks)
            {
                int chunkCount = m_Data.ChunkData.Length / ChunkInfo.kSize;
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, chunkCount, ChunkInfo.kSize)
                { name = "VDGS ChunkData" };
                m_GpuChunks.SetData(m_Data.ChunkData);
                m_GpuChunksValid = true;
            }
            else
            {
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, ChunkInfo.kSize)
                { name = "VDGS ChunkData (dummy)" };
                m_GpuChunksValid = false;
            }

            m_GpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, kGpuViewDataSize)
            { name = "VDGS ViewData" };

            m_GpuCutoutsDummy = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, kCutoutDataSize)
            { name = "VDGS Cutouts (dummy)" };

            m_GpuIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 36, 2) { name = "VDGS IndexBuffer" };
            m_GpuIndexBuffer.SetData(new ushort[]
            {
                0, 1, 2, 1, 3, 2,
                4, 6, 5, 5, 6, 7,
                0, 2, 4, 4, 2, 6,
                1, 5, 3, 5, 7, 3,
                0, 4, 1, 4, 5, 1,
                2, 3, 6, 3, 7, 6
            });

            InitSortBuffers(m_SplatCount);
            CreateLodResources();
        }

        private void CreateLodResources()
        {
            var lod = m_Data.Lod;
            m_LodFrame = -1;
            m_SortCount = m_SplatCount;
            m_NeedCompact = false;
            if (m_GpuActiveCount == null)
                m_GpuActiveCount = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4)
                { name = "VDGS ActiveCount" };
            if (lod == null)
            {
                // Nothing to bind and nothing to select: without VDGS_LOD the shader never
                // names these buffers, so a capture with one level allocates neither.
                m_LodSelector = null;
                return;
            }

            int runs = lod.RunOffset.Length;
            m_GpuSplatRun = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, 4) { name = "VDGS SplatRun" };
            m_GpuSplatRun.SetData(lod.RunOfSplat);
            m_RunActive = new byte[runs];
            m_RunInfoWords = new uint[runs * 2];
            for (int r = 0; r < runs; r++)
                m_RunInfoWords[r * 2 + 1] = (uint)lod.RunShBase[r];
            m_GpuRunInfo = new GraphicsBuffer(GraphicsBuffer.Target.Structured, runs * 2, 4) { name = "VDGS RunInfo" };
            // Nothing is active until the first selection runs in SortPoints, which is the
            // same frame the first sort happens - no frame draws with an empty table.
            m_GpuRunInfo.SetData(m_RunInfoWords);
            m_LodSelector = new VDGS.Lod.LodSelector(lod.Leaves, lod.LevelCount, lod.RunLeaf, lod.RunLevel, lod.RunCount);
        }

        private static ComputeShader s_KeywordShader;
        private static UnityEngine.Rendering.LocalKeyword s_LodKeyword;

        /// <summary>The VDGS_LOD variant selector, looked up once per compute shader.</summary>
        private static UnityEngine.Rendering.LocalKeyword LodKeyword(ComputeShader cs)
        {
            if (s_KeywordShader != cs)
            {
                s_KeywordShader = cs;
                s_LodKeyword = new UnityEngine.Rendering.LocalKeyword(cs, "VDGS_LOD");
            }
            return s_LodKeyword;
        }

        private void BindLod(CommandBuffer cmb, ComputeShader cs, int kernel)
        {
            cmb.SetComputeIntParam(cs, Props.SortCount, m_SortCount);
            // Only a capture with levels touches the keyword, and it puts it back when its
            // dispatches are recorded (see SortPoints and CalcViewData). A renderer without
            // levels leaves the variant selector alone: switching it costs pipeline state,
            // and it was measured at about 0.3 ms a frame on a 2.24M .ply.
            if (m_LodSelector == null)
                return;
            // The keyword goes on the command buffer, not the shader object: keyword state
            // is read when the buffer EXECUTES, so setting it here would hand every capture
            // on screen whatever the last one asked for.
            cmb.SetKeyword(cs, LodKeyword(cs), true);
            cmb.SetComputeBufferParam(cs, kernel, Props.SplatRun, m_GpuSplatRun);
            cmb.SetComputeBufferParam(cs, kernel, Props.RunInfo, m_GpuRunInfo);
        }

        /// <summary>
        /// Re-pick levels right now, for the offline sweep harness. Time.frameCount does not
        /// advance between manual Camera.Render() calls in batch mode, so the interval guard
        /// below would freeze the selection at whatever the first frame chose.
        /// </summary>
        public void RefreshLod(Camera cam)
        {
            m_LodFrame = -1;
            UpdateLod(cam);
        }

        private void UpdateLod(Camera cam)
        {
            if (m_LodSelector == null)
                return;
            if (Time.frameCount == m_LodFrame)
                return;
            if (m_LodFrame >= 0 && Time.frameCount - m_LodFrame < 10)
                return;
            m_LodFrame = Time.frameCount;

            m_LodSelector.LodDetail = LodDetail;
            m_LodSelector.Budget = LodBudget;
            m_LodSelector.BandJitter = LodBandJitter;
            var local = transform.InverseTransformPoint(cam.transform.position);
            if (!m_LodSelector.Update(local.x, local.y, local.z, Mathf.Abs(transform.lossyScale.x), m_RunActive))
                return;
            for (int r = 0; r < m_RunActive.Length; r++)
                m_RunInfoWords[r * 2] = m_RunActive[r];
            m_GpuRunInfo.SetData(m_RunInfoWords);
            m_SortCount = (int)m_LodSelector.ActiveSplats;
            m_NeedCompact = true;
        }

        /// <summary>
        /// Raw buffers are addressed as uint by the shaders. GraphicsBuffer.SetData cannot
        /// take a byte[] for a 4-byte-stride buffer, so reinterpret the bytes as uint first.
        /// </summary>
        private static GraphicsBuffer RawBuffer(byte[] bytes, string name)
        {
            int uintCount = bytes.Length / 4;
            var buf = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource,
                uintCount, 4) { name = name };
            var words = new uint[uintCount];
            Buffer.BlockCopy(bytes, 0, words, 0, uintCount * 4);
            buf.SetData(words);
            return buf;
        }

        /// <summary>
        /// Largest gaussian radius per block of kChunkSize splats, computed once.
        ///
        /// The frustum cull needs a per-splat margin, and reading each splat's scale in
        /// the distance pass gave one - but the index there comes from the sorted key
        /// buffer, so those reads scatter over the whole of other.bin every frame and
        /// cost more than the tighter margin saved. This runs over the splats in order,
        /// where the reads are sequential, and leaves a table small enough to stay in
        /// cache: four bytes per 256 splats.
        /// </summary>
        private void BuildChunkRadii(int count)
        {
            var cs = ShaderBundle.SplatUtilities;
            if (cs == null) return;

            int blocks = (count + SplatData.kChunkSize - 1) / SplatData.kChunkSize;
            m_GpuChunkRadius = new GraphicsBuffer(GraphicsBuffer.Target.Structured, blocks, 4)
            { name = "VDGS ChunkRadius" };
            m_GpuChunkRadius.SetData(new uint[blocks]);

            int k = cs.FindKernel("CSCalcChunkRadius");
            cs.SetBuffer(k, Props.SplatChunkRadius, m_GpuChunkRadius);
            cs.SetBuffer(k, Props.SplatOther, m_GpuOtherData);
            cs.SetBuffer(k, Props.SplatChunks, m_GpuChunks);
            cs.SetInt(Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
            cs.SetInt(Props.SplatCount, count);
            cs.SetInt(Props.SplatFormat, (int)((uint)m_Data.PosFormat
                                             | ((uint)m_Data.ScaleFormat << 8)
                                             | ((uint)m_Data.ShFormat << 16)));
            cs.GetKernelThreadGroupSizes(k, out uint gsX, out _, out _);
            cs.Dispatch(k, (count + (int)gsX - 1) / (int)gsX, 1, 1);
        }

        private void InitSortBuffers(int count)
        {
            DisposeBuffer(ref m_GpuSortDistances);
            DisposeBuffer(ref m_GpuSortKeys);
            m_SorterArgs.resources.Dispose();

            EnsureSorterAndRegister();

            m_GpuSortDistances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4)
            { name = "VDGS SortDistances" };
            m_GpuSortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4)
            { name = "VDGS SortIndices" };

            DisposeBuffer(ref m_GpuVisibleCount);
            DisposeBuffer(ref m_GpuDrawArgs);
            DisposeBuffer(ref m_GpuChunkRadius);
            m_GpuVisibleCount = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4)
            { name = "VDGS VisibleCount" };
            m_GpuDrawArgs = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 5, 4)
            { name = "VDGS DrawArgs" };
            // Until the first sort runs the args buffer is empty, and an indirect draw
            // reading zeros would simply draw nothing on frame one. Seed it with the full
            // count so a scene is visible immediately and stays visible if sorting is
            // skipped by m_SortNthFrame.
            m_GpuDrawArgs.SetData(new uint[] { 6, (uint)count, 0, 0, 0 });

            BuildChunkRadii(count);

            var cs = ShaderBundle.SplatUtilities;
            cs.SetBuffer((int)KernelIndices.SetIndices, Props.SplatSortKeys, m_GpuSortKeys);
            cs.SetInt(Props.SplatCount, m_GpuSortDistances.count);
            cs.GetKernelThreadGroupSizes((int)KernelIndices.SetIndices, out uint gsX, out _, out _);
            cs.Dispatch((int)KernelIndices.SetIndices, (m_GpuSortDistances.count + (int)gsX - 1) / (int)gsX, 1, 1);

            m_SortCount = count;
            m_SorterArgs.inputKeys = m_GpuSortDistances;
            m_SorterArgs.inputValues = m_GpuSortKeys;
            m_SorterArgs.count = (uint)count;
            if (m_Sorter != null && m_Sorter.Valid)
                m_SorterArgs.resources = GpuSorting.SupportResources.Load((uint)count);
        }

        private void SetDataOnCS(CommandBuffer cmb, KernelIndices kernel)
        {
            SetDataOnCS(cmb, (int)kernel);
        }

        private void SetDataOnCS(CommandBuffer cmb, int kernel)
        {
            var cs = ShaderBundle.SplatUtilities;
            int k = (int)kernel;
            cmb.SetComputeBufferParam(cs, k, Props.SplatPos, m_GpuPosData);
            cmb.SetComputeBufferParam(cs, k, Props.SplatChunks, m_GpuChunks);
            cmb.SetComputeBufferParam(cs, k, Props.SplatOther, m_GpuOtherData);
            cmb.SetComputeBufferParam(cs, k, Props.SplatSH, m_GpuSHData);
            cmb.SetComputeTextureParam(cs, k, Props.SplatColor, m_GpuColorData);
            // No editing support: point the selection/deletion bit buffers at position data
            // and tell the shader they are invalid, exactly as upstream does when unset.
            cmb.SetComputeBufferParam(cs, k, Props.SplatSelectedBits, m_GpuPosData);
            cmb.SetComputeBufferParam(cs, k, Props.SplatDeletedBits, m_GpuPosData);
            cmb.SetComputeBufferParam(cs, k, Props.SplatViewData, m_GpuView);
            cmb.SetComputeBufferParam(cs, k, Props.OrderBuffer, m_GpuSortKeys);

            cmb.SetComputeIntParam(cs, Props.SplatBitsValid, 0);
            uint format = (uint)m_Data.PosFormat | ((uint)m_Data.ScaleFormat << 8) | ((uint)m_Data.ShFormat << 16);
            cmb.SetComputeIntParam(cs, Props.SplatFormat, (int)format);
            cmb.SetComputeIntParam(cs, Props.SplatCount, m_SplatCount);
            cmb.SetComputeIntParam(cs, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);

            cmb.SetComputeIntParam(cs, Props.SplatCutoutsCount, 0);
            cmb.SetComputeBufferParam(cs, k, Props.SplatCutouts, m_GpuCutoutsDummy);
            BindLod(cmb, cs, k);
        }

        internal void SetDataOnMaterial(MaterialPropertyBlock mat)
        {
            mat.SetBuffer(Props.SplatPos, m_GpuPosData);
            mat.SetBuffer(Props.SplatOther, m_GpuOtherData);
            mat.SetBuffer(Props.SplatSH, m_GpuSHData);
            mat.SetTexture(Props.SplatColor, m_GpuColorData);
            mat.SetBuffer(Props.SplatSelectedBits, m_GpuPosData);
            mat.SetBuffer(Props.SplatDeletedBits, m_GpuPosData);
            mat.SetInt(Props.SplatBitsValid, 0);
            uint format = (uint)m_Data.PosFormat | ((uint)m_Data.ScaleFormat << 8) | ((uint)m_Data.ShFormat << 16);
            mat.SetInt(Props.SplatFormat, (int)format);
            mat.SetInt(Props.SplatCount, m_SplatCount);
            mat.SetInt(Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
        }

        internal void CalcViewData(CommandBuffer cmb, Camera cam)
        {
            if (cam.cameraType == CameraType.Preview)
                return;

            var tr = transform;
            var matView = cam.worldToCameraMatrix;
            var matO2W = tr.localToWorldMatrix;
            var matW2O = tr.worldToLocalMatrix;
            var screenPar = new Vector4(cam.pixelWidth, cam.pixelHeight, 0, 0);
            Vector4 camPos = cam.transform.position;

            var cs = ShaderBundle.SplatUtilities;
            // A capture with levels runs the variant that reads the key buffer; everything
            // else runs the one that never binds it - binding alone cost a 2.24M .ply about
            // 1 ms a frame on the RTX 3060, with the branch never taken.
            int kView = m_LodSelector != null
                ? cs.FindKernel("CSCalcViewDataLod")
                : (int)KernelIndices.CalcViewData;
            SetDataOnCS(cmb, kView);
            if (m_LodSelector != null)
                cmb.SetComputeBufferParam(cs, kView, Props.SplatSortKeys, m_GpuSortKeys);

            cmb.SetComputeMatrixParam(cs, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(cs, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(cs, Props.MatrixWorldToObject, matW2O);
            cmb.SetComputeVectorParam(cs, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(cs, Props.VecWorldSpaceCameraPos, camPos);
            cmb.SetComputeFloatParam(cs, Props.SplatScale, m_SplatScale);
            cmb.SetComputeFloatParam(cs, Props.SplatOpacityScale, m_OpacityScale);
            cmb.SetComputeIntParam(cs, Props.SplatDropDegenerate, m_DropDegenerate ? 1 : 0);
            int shOrder = Mathf.Min(m_SHOrder, m_Data.ShOrder);
            cmb.SetComputeIntParam(cs, Props.SHOrder, shOrder);
            cmb.SetComputeIntParam(cs, Props.SplatSHOrder, shOrder);
            cmb.SetComputeIntParam(cs, Props.SHOnly, m_SHOnly ? 1 : 0);

            cs.GetKernelThreadGroupSizes(kView, out uint gsX, out _, out _);
            cmb.DispatchCompute(cs, kView, (m_SortCount + (int)gsX - 1) / (int)gsX, 1, 1);

            if (m_LodSelector != null)
                cmb.SetKeyword(cs, LodKeyword(cs), false);
        }

        internal void SortPoints(CommandBuffer cmd, Camera cam, Matrix4x4 matrix)
        {
            if (cam.cameraType == CameraType.Preview || m_Sorter == null || !m_Sorter.Valid)
                return;

            var worldToCamMatrix = cam.worldToCameraMatrix;
            worldToCamMatrix.m20 *= -1;
            worldToCamMatrix.m21 *= -1;
            worldToCamMatrix.m22 *= -1;

            UpdateLod(cam);

            var cs = ShaderBundle.SplatUtilities;

            // Kernel indices for these are looked up by name: the enum's 0/1/2 mirror the
            // first three #pragma kernel lines, and hard-coding two more would break the
            // moment upstream adds a kernel above them.
            int kReset = cs.FindKernel("CSResetVisibleCount");
            int kArgs = cs.FindKernel("CSPrepareDrawArgs");

            // Gather the selected splats to the front of the key buffer. Only when the
            // selection changed: the sort permutes that set every frame but never changes
            // which splats are in it.
            if (m_NeedCompact)
            {
                int kClear = cs.FindKernel("CSResetActiveCount");
                int kPack = cs.FindKernel("CSCompactActive");
                cmd.SetComputeBufferParam(cs, kClear, Props.SplatActiveCount, m_GpuActiveCount);
                cmd.DispatchCompute(cs, kClear, 1, 1, 1);
                cmd.SetComputeBufferParam(cs, kPack, Props.SplatActiveCount, m_GpuActiveCount);
                cmd.SetComputeBufferParam(cs, kPack, Props.SplatSortKeys, m_GpuSortKeys);
                cmd.SetComputeBufferParam(cs, kPack, Props.SplatRun, m_GpuSplatRun);
                cmd.SetComputeBufferParam(cs, kPack, Props.RunInfo, m_GpuRunInfo);
                cmd.SetKeyword(cs, LodKeyword(cs), true);
                cmd.SetComputeIntParam(cs, Props.SplatCount, m_SplatCount);
                cs.GetKernelThreadGroupSizes(kPack, out uint gsPack, out _, out _);
                cmd.DispatchCompute(cs, kPack, (m_SplatCount + (int)gsPack - 1) / (int)gsPack, 1, 1);
                m_NeedCompact = false;
            }

            cmd.SetComputeBufferParam(cs, kReset, Props.SplatVisibleCount, m_GpuVisibleCount);
            cmd.DispatchCompute(cs, kReset, 1, 1, 1);

            int k = (int)KernelIndices.CalcDistances;
            cmd.SetComputeBufferParam(cs, k, Props.SplatSortDistances, m_GpuSortDistances);
            cmd.SetComputeBufferParam(cs, k, Props.SplatSortKeys, m_GpuSortKeys);
            cmd.SetComputeBufferParam(cs, k, Props.SplatChunks, m_GpuChunks);
            cmd.SetComputeBufferParam(cs, k, Props.SplatPos, m_GpuPosData);
            // The whole format word, not just the position part. LoadSplatPos only reads
            // the low byte, which is why this used to get away with it - but the cull now
            // calls LoadSplatScale, and that derives the other.bin stride from the scale
            // and SH formats in the upper bytes. Leaving them zero computed a 16-byte
            // stride for data that is actually 18, so every scale read landed on the
            // wrong splat and the radius was noise. The symptom was a cull that ignored
            // its own margin: raising the multiplier twentyfold changed nothing.
            uint distFormat = (uint)m_Data.PosFormat
                            | ((uint)m_Data.ScaleFormat << 8)
                            | ((uint)m_Data.ShFormat << 16);
            cmd.SetComputeIntParam(cs, Props.SplatFormat, (int)distFormat);
            cmd.SetComputeMatrixParam(cs, Props.MatrixMV, worldToCamMatrix * matrix);
            cmd.SetComputeIntParam(cs, Props.SplatCount, m_SplatCount);
            cmd.SetComputeIntParam(cs, Props.SortCount, m_SortCount);
            cmd.SetComputeIntParam(cs, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
            cmd.SetComputeBufferParam(cs, k, Props.SplatVisibleCount, m_GpuVisibleCount);
            cmd.SetComputeIntParam(cs, Props.CullEnabled, m_FrustumCulling ? 1 : 0);
            cmd.SetComputeFloatParam(cs, Props.CullMargin, Mathf.Max(0f, m_CullMargin));
            // Object -> clip, with the platform's projection conventions applied. The
            // distance pass only has the modelview matrix, and a hand-rolled projection
            // here would be wrong on exactly one graphics API and right on the others.
            var proj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
            cmd.SetComputeMatrixParam(cs, Props.MatrixObjectToClip,
                proj * cam.worldToCameraMatrix * matrix);
            // A clip-space frustum plane is not unit length. For the side planes the
            // gradient with respect to view position is (P00, 0, -1), so a sphere of
            // radius r reaches r*sqrt(P00^2+1) across the plane, not r*P00. At 120
            // degrees that is a factor of two.
            cmd.SetComputeVectorParam(cs, Props.CullProjScale, new Vector4(
                Mathf.Sqrt(proj.m00 * proj.m00 + 1f),
                Mathf.Sqrt(proj.m11 * proj.m11 + 1f), 0, 0));
            // A gaussian's drawn footprint is the quad the vertex shader emits, +/-2 in
            // the covariance axes; m_CullMargin is now the sigma multiplier on top of
            // that rather than a fraction of the screen. The object's own scale has to be
            // folded in because the capture is placed with a scale in the world.
            cmd.SetComputeFloatParam(cs, Props.CullRadiusScale,
                Mathf.Max(0f, m_CullMargin) * m_SplatScale * Mathf.Abs(transform.lossyScale.x));
            cmd.SetComputeFloatParam(cs, Props.CullCenterSlack, m_CullCenterSlack);
            cmd.SetComputeBufferParam(cs, k, Props.SplatChunkRadius, m_GpuChunkRadius);
            BindLod(cmd, cs, k);

            cs.GetKernelThreadGroupSizes(k, out uint gsX, out _, out _);
            cmd.DispatchCompute(cs, k, (m_SortCount + (int)gsX - 1) / (int)gsX, 1, 1);

            // Only the compacted range is sorted; the buffers stay sized for every
            // resident splat so a selection that grows needs no reallocation.
            m_SorterArgs.count = (uint)m_SortCount;
            m_Sorter.Dispatch(cmd, m_SorterArgs);

            cmd.SetComputeBufferParam(cs, kArgs, Props.SplatVisibleCount, m_GpuVisibleCount);
            cmd.SetComputeBufferParam(cs, kArgs, Props.SplatDrawArgs, m_GpuDrawArgs);
            cmd.DispatchCompute(cs, kArgs, 1, 1, 1);

            if (m_LodSelector != null)
                cmd.SetKeyword(cs, LodKeyword(cs), false);
        }

        private static void DisposeBuffer(ref GraphicsBuffer buf)
        {
            if (buf != null) buf.Dispose();
            buf = null;
        }

        private void DisposeResources()
        {
            if (m_GpuColorData != null)
            {
                DestroyImmediate(m_GpuColorData);
                m_GpuColorData = null;
            }
            DisposeBuffer(ref m_GpuPosData);
            DisposeBuffer(ref m_GpuOtherData);
            DisposeBuffer(ref m_GpuSHData);
            DisposeBuffer(ref m_GpuChunks);
            DisposeBuffer(ref m_GpuVisibleCount);
            DisposeBuffer(ref m_GpuDrawArgs);
            DisposeBuffer(ref m_GpuView);
            DisposeBuffer(ref m_GpuIndexBuffer);
            DisposeBuffer(ref m_GpuSortDistances);
            DisposeBuffer(ref m_GpuSortKeys);
            DisposeBuffer(ref m_GpuCutoutsDummy);
            DisposeBuffer(ref m_GpuSplatRun);
            DisposeBuffer(ref m_GpuRunInfo);
            DisposeBuffer(ref m_GpuActiveCount);
            m_LodSelector = null;
            m_SorterArgs.resources.Dispose();

            m_SplatCount = 0;
            m_GpuChunksValid = false;
        }

        private static TextureFormat ColorFormatToTexture(SplatData.ColorFormat format)
        {
            switch (format)
            {
                case SplatData.ColorFormat.Float32x4: return TextureFormat.RGBAFloat;
                case SplatData.ColorFormat.Float16x4: return TextureFormat.RGBAHalf;
                case SplatData.ColorFormat.Norm8x4: return TextureFormat.RGBA32;
                case SplatData.ColorFormat.BC7: return TextureFormat.BC7;
                default: throw new ArgumentOutOfRangeException("format", format, null);
            }
        }

        /// <summary>
        /// Stride of GaussianSplatAsset.ChunkInfo, which the HLSL side mirrors exactly:
        ///   uint  colR, colG, colB, colA      4 x 4 = 16
        ///   float2 posX, posY, posZ           3 x 8 = 24
        ///   uint  sclX, sclY, sclZ            3 x 4 = 12
        ///   uint  shR, shG, shB               3 x 4 = 12
        ///                                            = 64
        /// Getting this wrong silently misreads every chunk, so it is spelled out.
        /// </summary>
        internal static class ChunkInfo
        {
            internal const int kSize = 64;
        }
    }
}
