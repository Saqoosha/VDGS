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

            m_CommandBuffer.GetTemporaryRT(Props.GaussianSplatRT, -1, -1, 0, FilterMode.Point,
                GraphicsFormat.R16G16B16A16_SFloat);
            m_CommandBuffer.SetRenderTarget(Props.GaussianSplatRT, BuiltinRenderTextureType.CurrentActive);
            m_CommandBuffer.ClearRenderTarget(RTClearFlags.Color, new Color(0, 0, 0, 0), 0, 0);

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

        // Assigned in the inspector when running inside the editor viewer; the plugin
        // build fills these from the AssetBundle instead.
        public Shader m_ShaderSplats;
        public Shader m_ShaderComposite;
        public ComputeShader m_CSSplatUtilities;

        private bool ResourcesReady =>
            m_ShaderSplats != null && m_ShaderComposite != null &&
            m_CSSplatUtilities != null && SystemInfo.supportsComputeShaders;

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
            m_MatSplats = new Material(m_ShaderSplats) { name = "VDGS Splats" };
            m_MatComposite = new Material(m_ShaderComposite) { name = "VDGS Composite" };
        }

        internal void EnsureSorterAndRegister()
        {
            if (m_Sorter == null && ResourcesReady)
                m_Sorter = new GpuSorting(m_CSSplatUtilities);
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
            var cs = m_CSSplatUtilities;
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

            var cs = m_CSSplatUtilities;
            cs.SetBuffer((int)KernelIndices.SetIndices, Props.SplatSortKeys, m_GpuSortKeys);
            cs.SetInt(Props.SplatCount, m_GpuSortDistances.count);
            cs.GetKernelThreadGroupSizes((int)KernelIndices.SetIndices, out uint gsX, out _, out _);
            cs.Dispatch((int)KernelIndices.SetIndices, (m_GpuSortDistances.count + (int)gsX - 1) / (int)gsX, 1, 1);

            m_SorterArgs.inputKeys = m_GpuSortDistances;
            m_SorterArgs.inputValues = m_GpuSortKeys;
            m_SorterArgs.count = (uint)count;
            if (m_Sorter != null && m_Sorter.Valid)
                m_SorterArgs.resources = GpuSorting.SupportResources.Load((uint)count);
        }

        private void SetDataOnCS(CommandBuffer cmb, KernelIndices kernel)
        {
            var cs = m_CSSplatUtilities;
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

            SetDataOnCS(cmb, KernelIndices.CalcViewData);

            var cs = m_CSSplatUtilities;
            cmb.SetComputeMatrixParam(cs, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(cs, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(cs, Props.MatrixWorldToObject, matW2O);
            cmb.SetComputeVectorParam(cs, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(cs, Props.VecWorldSpaceCameraPos, camPos);
            cmb.SetComputeFloatParam(cs, Props.SplatScale, m_SplatScale);
            cmb.SetComputeFloatParam(cs, Props.SplatOpacityScale, m_OpacityScale);
            int shOrder = Mathf.Min(m_SHOrder, m_Data.ShOrder);
            cmb.SetComputeIntParam(cs, Props.SHOrder, shOrder);
            cmb.SetComputeIntParam(cs, Props.SplatSHOrder, shOrder);
            cmb.SetComputeIntParam(cs, Props.SHOnly, m_SHOnly ? 1 : 0);

            cs.GetKernelThreadGroupSizes((int)KernelIndices.CalcViewData, out uint gsX, out _, out _);
            cmb.DispatchCompute(cs, (int)KernelIndices.CalcViewData,
                (m_GpuView.count + (int)gsX - 1) / (int)gsX, 1, 1);
        }

        internal void SortPoints(CommandBuffer cmd, Camera cam, Matrix4x4 matrix)
        {
            if (cam.cameraType == CameraType.Preview || m_Sorter == null || !m_Sorter.Valid)
                return;

            var worldToCamMatrix = cam.worldToCameraMatrix;
            worldToCamMatrix.m20 *= -1;
            worldToCamMatrix.m21 *= -1;
            worldToCamMatrix.m22 *= -1;

            var cs = m_CSSplatUtilities;

            // Kernel indices for these are looked up by name: the enum's 0/1/2 mirror the
            // first three #pragma kernel lines, and hard-coding two more would break the
            // moment upstream adds a kernel above them.
            int kReset = cs.FindKernel("CSResetVisibleCount");
            int kArgs = cs.FindKernel("CSPrepareDrawArgs");

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
            cmd.SetComputeBufferParam(cs, k, Props.SplatChunkRadius, m_GpuChunkRadius);

            cs.GetKernelThreadGroupSizes(k, out uint gsX, out _, out _);
            cmd.DispatchCompute(cs, k, (m_GpuSortDistances.count + (int)gsX - 1) / (int)gsX, 1, 1);

            m_Sorter.Dispatch(cmd, m_SorterArgs);

            cmd.SetComputeBufferParam(cs, kArgs, Props.SplatVisibleCount, m_GpuVisibleCount);
            cmd.SetComputeBufferParam(cs, kArgs, Props.SplatDrawArgs, m_GpuDrawArgs);
            cmd.DispatchCompute(cs, kArgs, 1, 1, 1);
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
