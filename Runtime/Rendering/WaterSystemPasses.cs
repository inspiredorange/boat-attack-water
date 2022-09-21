using System.Drawing.Drawing2D;
using UnityEditor.Graphs;
using UnityEngine;
using UnityEngine.Experimental.Rendering.RenderGraphModule;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RendererUtils;
using UnityEngine.Rendering.Universal;
using Unity.Mathematics;
using UnityEngine.Experimental.Rendering;

namespace WaterSystem.Rendering
{
    #region Water Effects Pass

    public class WaterFxPass : ScriptableRenderPass
    {
        internal class PassData
        {
            public RendererList RendererList;
            public TextureHandle BufferHandleA;
            public TextureHandle BufferHandleB;
            public RendererListDesc RenderListDesc;
            public RendererListParams RenderListParams;
        }
        
        private static int m_BufferATexture = Shader.PropertyToID("_WaterBufferA");
        private static int m_BufferBTexture = Shader.PropertyToID("_WaterBufferB");

        private static int
            m_MockDepthTexture = Shader.PropertyToID("_DepthBufferMock"); // TODO remove once bug is fixed

        private RenderTargetIdentifier m_BufferTargetA = new RenderTargetIdentifier(m_BufferATexture);
        private RenderTargetIdentifier m_BufferTargetB = new RenderTargetIdentifier(m_BufferBTexture);

        private RenderTargetIdentifier
            m_BufferDepth = new RenderTargetIdentifier(m_MockDepthTexture); // TODO also remove


        private const string k_RenderWaterFXTag = "Render Water FX";
        private ProfilingSampler m_WaterFX_Profile = new ProfilingSampler(k_RenderWaterFXTag);
        private readonly ShaderTagId m_WaterFXShaderTag = new ShaderTagId("WaterFX");

        private static readonly Color ClearColor = 
            new Color(0.0f, 0.5f, 0.5f, 0.5f); //r = foam mask, g = normal.x, b = normal.z, a = displacement

        private FilteringSettings m_FilteringSettings;
        private RenderTargetHandle m_WaterFX = RenderTargetHandle.CameraTarget;

        public WaterFxPass()
        {
            m_WaterFX.Init("_WaterFXMap");
            // only wanting to render transparent objects
            m_FilteringSettings = new FilteringSettings(RenderQueueRange.transparent);
            renderPassEvent = RenderPassEvent.BeforeRenderingPrePasses;
        }

        // Calling Configure since we are wanting to render into a RenderTexture and control cleat
        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            var rtd = BufferDescriptor(cameraTextureDescriptor.width, cameraTextureDescriptor.height);

            // get a temp RT for rendering into
            cmd.GetTemporaryRT(m_BufferATexture, rtd, FilterMode.Bilinear);
            cmd.GetTemporaryRT(m_BufferBTexture, rtd, FilterMode.Bilinear);
            cmd.GetTemporaryRT(m_MockDepthTexture, rtd, FilterMode.Point);

            RenderTargetIdentifier[] multiTargets = { m_BufferTargetA, m_BufferTargetB };
            ConfigureTarget(multiTargets, m_MockDepthTexture);
            // clear the screen with a specific color for the packed data
            ConfigureClear(ClearFlag.Color, ClearColor);

#if UNITY_2021_1_OR_NEWER
            ConfigureDepthStoreAction(RenderBufferStoreAction.DontCare);
#endif
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var cam = renderingData.cameraData.camera;
            if (!ExecuteCheck(cam)) return;

            CommandBuffer cmd = CommandBufferPool.Get();
            //context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            using (new ProfilingScope(cmd, m_WaterFX_Profile)) // makes sure we have profiling ability
            {
                // here we choose renderers based off the "WaterFX" shader pass and also sort back to front
                var drawSettings = CreateDrawingSettings(m_WaterFXShaderTag, ref renderingData,
                    SortingCriteria.CommonTransparent);

                // draw all the renderers matching the rules we setup
                context.DrawRenderers(renderingData.cullResults, ref drawSettings, ref m_FilteringSettings);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ref RenderingData renderingData)
        {
            var cam = renderingData.cameraData.camera;

            var camDesc = renderingData.cameraData.cameraTargetDescriptor;
            var rtd = BufferDescriptor(camDesc.width, camDesc.height);

            var depthRT = rtd;
            depthRT.depthBufferBits = 16;

            var fakeDepth = PassUtilities.CreateRenderGraphTexture(renderGraph, depthRT, "FakeDepth");
            var bufferA = PassUtilities.CreateRenderGraphTexture(renderGraph, rtd, "_WaterBufferA");
            var bufferB = PassUtilities.CreateRenderGraphTexture(renderGraph, rtd, "_WaterBufferB");
            
            using (var builder = renderGraph.AddRenderPass<PassData>(k_RenderWaterFXTag, out var pass))
            {
                builder.AllowPassCulling(false);

                pass.BufferHandleA = builder.UseColorBuffer(bufferA, 0);
                pass.BufferHandleB = builder.UseColorBuffer(bufferB, 1);
                builder.UseDepthBuffer(fakeDepth, DepthAccess.Write);
                
                RendererListDesc desc = new RendererListDesc(m_WaterFXShaderTag, renderingData.cullResults, cam);
                desc.renderQueueRange = RenderQueueRange.all;
                desc.sortingCriteria = SortingCriteria.CommonTransparent;
                pass.RenderListDesc = desc;

                var drawSettings = CreateDrawingSettings(m_WaterFXShaderTag, ref renderingData,
                    SortingCriteria.CommonTransparent);

                pass.RenderListParams =
                    new RendererListParams(renderingData.cullResults, drawSettings, m_FilteringSettings);
                
                builder.SetRenderFunc((PassData data, RenderGraphContext context) =>
                {
                    context.cmd.ClearRenderTarget(RTClearFlags.ColorDepth, ClearColor, 1, 0);
                    context.cmd.DrawRendererList(context.renderContext.CreateRendererList(ref data.RenderListParams));
                    context.cmd.SetGlobalTexture(m_BufferATexture, data.BufferHandleA);
                    context.cmd.SetGlobalTexture(m_BufferBTexture, data.BufferHandleB);
                });
            }
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            // since the texture is used within the single cameras use we need to cleanup the RT afterwards
            cmd.ReleaseTemporaryRT(m_BufferATexture);
            cmd.ReleaseTemporaryRT(m_BufferBTexture);
            cmd.ReleaseTemporaryRT(m_MockDepthTexture);
        }

        private static bool ExecuteCheck(Camera cam)
        {
            return cam.cameraType is CameraType.Game or CameraType.SceneView;
        }

        private static RenderTextureDescriptor BufferDescriptor(int width, int height)
        {
            return new RenderTextureDescriptor
            {
                // no need for a depth buffer
                depthBufferBits = 0,
                // dimension
                dimension = TextureDimension.Tex2D,
                // Half resolution
                width = width, // / 2;
                height = height, // / 2;
                // default format TODO research usefulness of HDR format
                colorFormat = RenderTextureFormat.Default,
                graphicsFormat = GraphicsFormatUtility.GetGraphicsFormat(RenderTextureFormat.Default, false),
                msaaSamples = 1,
                useMipMap = false,
                volumeDepth = 1,
                shadowSamplingMode = ShadowSamplingMode.None,
            };
        }
    }

    #endregion

    #region InfiniteWater Pass

    public class InfiniteWaterPass : ScriptableRenderPass
    {
        private const string k_RenderInfiniteWaterTag = "Infinite Water";
        private ProfilingSampler m_InfiniteWater_Profile = new ProfilingSampler(k_RenderInfiniteWaterTag);

        public class PassData
        {
            public Mesh infiniteMesh;
            public Material infiniteMaterial;
            public MaterialPropertyBlock mpb;
            public Matrix4x4 matrix;
        }

        public PassData passData;
        
        public InfiniteWaterPass(Mesh mesh)
        {
            passData = new PassData();
            if (mesh)
                passData.infiniteMesh = mesh;
            passData.mpb = new MaterialPropertyBlock();
            renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            Camera cam = renderingData.cameraData.camera;

            if(!ExecuteCheck(cam, ref passData)) return;

            CommandBuffer cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, m_InfiniteWater_Profile))
            {
                var probe = RenderSettings.ambientProbe;

                passData.infiniteMaterial.SetFloat("_BumpScale", 0.5f);

                // Create the matrix to position the caustics mesh.
                var position = cam.transform.position;
                var matrix = Matrix4x4.TRS(position, Quaternion.identity, Vector3.one);
                // Setup the CommandBuffer and draw the mesh with the caustic material and matrix
                MaterialPropertyBlock matBloc = new MaterialPropertyBlock();
                matBloc.CopySHCoefficientArraysFrom(new[] { probe });
                cmd.DrawMesh(passData.infiniteMesh, matrix, passData.infiniteMaterial, 0, 0, matBloc);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
        
        public override void RecordRenderGraph(RenderGraph renderGraph, ref RenderingData renderingData)
        {
            Camera cam = renderingData.cameraData.camera;

            if(!ExecuteCheck(cam, ref passData)) return;

            using (var builder = renderGraph.AddRenderPass<PassData>(k_RenderInfiniteWaterTag, out var pass))
            {
                builder.AllowPassCulling(false);
                builder.UseColorBuffer(UniversalRenderer.m_ActiveRenderGraphColor, 0);
                builder.UseDepthBuffer(UniversalRenderer.m_ActiveRenderGraphDepth, DepthAccess.Read);
                
                var probe = RenderSettings.ambientProbe;

                pass.infiniteMesh = passData.infiniteMesh;
                pass.infiniteMaterial = passData.infiniteMaterial;

                pass.infiniteMaterial.SetFloat("_BumpScale", 0.5f);
            
                // Create the matrix to position the caustics mesh.
                var position = cam.transform.position;
                pass.matrix = Matrix4x4.TRS(position, Quaternion.identity, Vector3.one);
                // Setup the CommandBuffer and draw the mesh with the caustic material and matrix
                pass.mpb = passData.mpb;
                pass.mpb.CopySHCoefficientArraysFrom(new[] { probe });
                
                builder.SetRenderFunc((PassData data, RenderGraphContext context) =>
                {
                    context.cmd.DrawMesh(data.infiniteMesh, data.matrix, data.infiniteMaterial, 0, 0, data.mpb);
                });
            }
        }

        private static bool ExecuteCheck(Camera cam, ref PassData data)
        {
            if (cam.cameraType != CameraType.Game &&
                cam.cameraType != CameraType.SceneView ||
                cam.name.Contains("Reflections")) return false;

            if (data.infiniteMesh == null)
            {
                Debug.LogError("Infinite Water Pass Mesh is missing.");
                return false;
            }
            
            if (data.infiniteMaterial == null)
                data.infiniteMaterial = CoreUtils.CreateEngineMaterial("Boat Attack/Water/InfiniteWater");

            return data.infiniteMaterial && data.infiniteMesh;
        }
    }

    #endregion

    #region Caustics Pass

    public class WaterCausticsPass : ScriptableRenderPass
    {
        internal class PassData
        {
            public Material WaterCausticMaterial;
            public Mesh CausticMesh;
            public Matrix4x4 Matrix;
        }
        
        private const string k_RenderWaterCausticsTag = "Water Caustics";
        private ProfilingSampler m_WaterCaustics_Profile = new ProfilingSampler(k_RenderWaterCausticsTag);
        public Material WaterCausticMaterial;
        private static Mesh m_mesh;
        private static readonly int MainLightDir = Shader.PropertyToID("_MainLightDir");
        private static readonly int WaterLevel = Shader.PropertyToID("_WaterLevel");

        public WaterCausticsPass(Material material)
        {
            WaterCausticMaterial = material;
        }

        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var cam = renderingData.cameraData.camera;
            // Stop the pass rendering in the preview or material missing
            if (!ExecuteCheck(cam, WaterCausticMaterial)) return;

            CommandBuffer cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, m_WaterCaustics_Profile))
            {
                WaterCausticMaterial.SetMatrix(MainLightDir, GetSunMatrix());
                WaterCausticMaterial.SetFloat(WaterLevel, Ocean.Instance.transform.position.y);

                // Create mesh if needed
                if (!m_mesh)
                    m_mesh = PassUtilities.GenerateCausticsMesh(1000f);

                // Setup the CommandBuffer and draw the mesh with the caustic material and matrix
                cmd.DrawMesh(m_mesh, GetMeshMatrix(cam), WaterCausticMaterial, 0, 0);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ref RenderingData renderingData)
        {
            var cam = renderingData.cameraData.camera;
            // Stop the pass rendering in the preview or material missing
            if (!ExecuteCheck(cam, WaterCausticMaterial)) return;
            
            WaterCausticMaterial.SetMatrix(MainLightDir, GetSunMatrix());
            WaterCausticMaterial.SetFloat(WaterLevel, Ocean.Instance.transform.position.y);
            
            using (var builder = renderGraph.AddRenderPass<PassData>(k_RenderWaterCausticsTag, out var pass))
            {
                builder.AllowPassCulling(false);
                
                builder.UseColorBuffer(UniversalRenderer.m_ActiveRenderGraphColor, 0);

                pass.CausticMesh = PassUtilities.GenerateCausticsMesh(1000);
                pass.WaterCausticMaterial = WaterCausticMaterial;
                pass.Matrix = GetMeshMatrix(cam);
                
                builder.SetRenderFunc((PassData data, RenderGraphContext context) =>
                {
                    context.cmd.DrawMesh(data.CausticMesh, data.Matrix, data.WaterCausticMaterial, 0, 0);
                });
            }
        }

        private static Matrix4x4 GetSunMatrix()
        {
            return RenderSettings.sun != null
                ? RenderSettings.sun.transform.localToWorldMatrix
                : Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(-45f, 45f, 0f), Vector3.one);
        }

        private static Matrix4x4 GetMeshMatrix(Component cam)
        {
            var position = cam.transform.position;
            position.y = Ocean.Instance.transform.position.y;
            return Matrix4x4.TRS(position, Quaternion.identity, Vector3.one);
        }

        private static bool ExecuteCheck(Camera cam, Material mat)
        {
            // Stop the pass rendering in the preview or material missing
            if (cam.cameraType == CameraType.Preview || !mat)
                return false;

            return cam.cameraType is CameraType.Game or CameraType.SceneView;
        }
    }

    #endregion

    public static class PassUtilities
    {
        public static Mesh GenerateCausticsMesh(float size, bool flat = true)
        {
            var m = new Mesh();
            size *= 0.5f;

            var verts = new[]
            {
                new Vector3(-size, flat ? 0f : -size, flat ? -size : 0f),
                new Vector3(size, flat ? 0f : -size, flat ? -size : 0f),
                new Vector3(-size, flat ? 0f : size, flat ? size : 0f),
                new Vector3(size, flat ? 0f : size, flat ? size : 0f)
            };
            m.vertices = verts;

            var tris = new[]
            {
                0, 2, 1,
                2, 3, 1
            };
            m.triangles = tris;

            return m;
        }

        [System.Serializable]
        public class WaterSystemSettings
        {
            [Header("Caustics Settings")] [Range(0.1f, 1f)]
            public float causticScale = 0.25f;

            public float causticBlendDistance = 3f;

            [Header("Infinite Water")] public Mesh mesh;

            [Header("Advanced Settings")] public DebugMode debug = DebugMode.Disabled;

            public enum DebugMode
            {
                Disabled,
                WaterEffects,
                Caustics
            }
        }
        
        public static TextureHandle CreateRenderGraphTexture(RenderGraph renderGraph, RenderTextureDescriptor desc, string name, bool clear = false,
            FilterMode filterMode = FilterMode.Point, TextureWrapMode wrapMode = TextureWrapMode.Clamp)
        {
            TextureDesc rgDesc = new TextureDesc(desc.width, desc.height);
            rgDesc.dimension = desc.dimension;
            rgDesc.clearBuffer = clear;
            rgDesc.bindTextureMS = desc.bindMS;
            rgDesc.colorFormat = desc.graphicsFormat;
            rgDesc.depthBufferBits = (DepthBits)desc.depthBufferBits;
            rgDesc.slices = desc.volumeDepth;
            rgDesc.msaaSamples = (MSAASamples)desc.msaaSamples;
            rgDesc.name = name;
            rgDesc.enableRandomWrite = false;
            rgDesc.filterMode = filterMode;
            rgDesc.wrapMode = wrapMode;
            rgDesc.isShadowMap = desc.shadowSamplingMode != ShadowSamplingMode.None;
            // TODO RENDERGRAPH: depthStencilFormat handling?

            return renderGraph.CreateTexture(rgDesc);
        }
    }
}