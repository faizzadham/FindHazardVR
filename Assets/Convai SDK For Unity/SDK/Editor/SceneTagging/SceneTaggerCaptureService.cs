using System;
using Convai.Shared.Compatibility;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
using static Convai.Editor.SceneTagging.SceneTaggerNative;

namespace Convai.Editor.SceneTagging
{
    internal interface ISceneTaggerRenderPipeline
    {
        bool SupportsCurrentPipeline { get; }
        void Configure(Camera camera, Scene sourceScene, List<Object> resources);
        void Render(Camera camera, RenderTexture destination);
        void SetExposure(Camera camera, float stops);
        void CopyLight(Light source, Light destination);
        void SetSpecular(Camera camera, bool suppress);
        void Cleanup(long cameraId, Scene scene);
    }

    internal static class SceneTaggerRenderPipelines
    {
        internal static readonly List<ISceneTaggerRenderPipeline> Adapters = new();
        // Capture volumes only register while submitting a capture. The layer also prevents
        // the capture camera from blending original scene volumes a second time.
        internal const int CaptureVolumeLayer = 31;
        internal static IEnumerable<T> LoadedComponents<T>(Scene source = default) where T : Component
        {
            if (source.IsValid() && source.isLoaded && EditorSceneManager.IsPreviewScene(source))
                foreach (GameObject root in source.GetRootGameObjects())
                    foreach (T component in root.GetComponentsInChildren<T>()) yield return component;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || EditorSceneManager.IsPreviewScene(scene)) continue;
                foreach (GameObject root in scene.GetRootGameObjects())
                    foreach (T component in root.GetComponentsInChildren<T>()) yield return component;
            }
        }
        internal static ISceneTaggerRenderPipeline Resolve() => Adapters.FirstOrDefault(a => a.SupportsCurrentPipeline) ??
            (GraphicsSettings.currentRenderPipeline == null ? new BuiltIn() : throw new NotSupportedException("The active render pipeline does not have a Scene Tagger capture adapter."));
        private sealed class BuiltIn : ISceneTaggerRenderPipeline
        {
            public bool SupportsCurrentPipeline => GraphicsSettings.currentRenderPipeline == null;
            public void Configure(Camera camera, Scene scene, List<Object> resources) { }
            private float _exposure;
            private RenderTexture _hdr;
            private Material _resolve;
            public void SetExposure(Camera camera, float stops) => _exposure = stops;
            public void CopyLight(Light source, Light destination) { }
            public void SetSpecular(Camera camera, bool suppress) { }
            public void Cleanup(long cameraId, Scene scene)
            {
                if (_hdr) Object.DestroyImmediate(_hdr);
                if (_resolve) Object.DestroyImmediate(_resolve);
            }
            public void Render(Camera camera, RenderTexture destination)
            {
                if (!_hdr || _hdr.width != destination.width)
                {
                    if (_hdr) Object.DestroyImmediate(_hdr);
                    _hdr = new RenderTexture(destination.width, destination.height, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
                        { hideFlags = HideFlags.HideAndDontSave };
                    _hdr.Create();
                }
                if (!_resolve) _resolve = new Material(Shader.Find("Hidden/Convai/SceneTaggerExposure")) { hideFlags = HideFlags.HideAndDontSave };
                camera.targetTexture = _hdr;
                camera.Render();
                _resolve.SetFloat("_Exposure", Mathf.Pow(2, _exposure));
                bool previous = GL.sRGBWrite;
                try { GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear; Graphics.Blit(_hdr, destination, _resolve); }
                finally { GL.sRGBWrite = previous; }
            }
        }
    }

    internal sealed class SceneTaggerCaptureService : IDisposable
    {
        private readonly Scene _preview;
        private readonly SceneTaggerRow _row;
        private readonly List<Object> _resources = new();
        private readonly List<MeshRenderer> _renderers = new();
        private readonly ISceneTaggerRenderPipeline _pipeline;
        private readonly List<Light> _recovery = new();
        private readonly Dictionary<Transform, Transform> _transforms = new();
        private RenderTexture _target, _mask;
        private readonly Camera _camera;
        private readonly long _cameraId;
        private readonly List<AsyncGPUReadbackRequest> _readbacks = new();
        private readonly Dictionary<(MeshRenderer, int), Material> _maskMaterials = new();
        private readonly MaterialPropertyBlock _maskProperties = new();
        private Shader _maskShader;
        private bool _disposed, _rendered;
        private int _profile;
        private uint _lightingPolicy;
        private float _highlightExposure = -1;
        private float ExposureStops => _profile == 1 ? _highlightExposure : Exposure[_profile];
        private readonly Dictionary<Material, float> _specular = new();
        private static readonly HashSet<SceneTaggerCaptureService> Active = new();
        static SceneTaggerCaptureService()
        {
            AssemblyReloadEvents.beforeAssemblyReload += DisposeAll;
            EditorApplication.quitting += DisposeAll;
        }
        private static void DisposeAll() { foreach (var capture in Active.ToArray()) capture.Dispose(); }
        private readonly List<Texture2D> _streaming = new();
        private readonly Dictionary<Texture2D, int> _oldMips = new();
        private Vector3 _displayedDirection;
        private Vector2 _displayedPan;
        private float _displayedDistanceScale;
        internal Vector3 LocalDirection;
        internal Vector2 Pan;
        internal float DistanceScale = 1;
        internal Texture Preview => _target;
        internal bool HasRendered => _rendered;
        internal int Profile => _profile;
        private static readonly float[] Exposure = { 0, -1, 1, 0, 1, 0, 0, .5f, .5f, 1.5f, 2.5f };

        internal SceneTaggerCaptureService(SceneTaggerRow row, int resolution)
        {
            _row = row;
            _pipeline = SceneTaggerRenderPipelines.Resolve();
            _preview = EditorSceneManager.NewPreviewScene();
            Active.Add(this);
            try
            {
                foreach (Renderer source in row.Renderers) CopyRenderer(source);
                foreach (Light source in SceneTaggerRenderPipelines.LoadedComponents<Light>(row.Target.scene))
                {
                    if (!source || !source.enabled) continue;
                    GameObject go = Create("Scene light");
                    go.layer = source.gameObject.layer;
                    go.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
                    Light light = go.AddComponent<Light>();
                    EditorUtility.CopySerialized(source, light);
                    _pipeline.CopyLight(source, light);
                    light.hideFlags = HideFlags.HideAndDontSave;
                }
                foreach (ReflectionProbe source in SceneTaggerRenderPipelines.LoadedComponents<ReflectionProbe>(row.Target.scene))
                {
                    if (!source.enabled) continue;
                    GameObject go = Create("Scene reflection");
                    go.layer = source.gameObject.layer;
                    go.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
                    ReflectionProbe probe = go.AddComponent<ReflectionProbe>();
                    EditorUtility.CopySerialized(source, probe);
                    probe.mode = ReflectionProbeMode.Custom;
                    probe.customBakedTexture = source.texture;
                    probe.hideFlags = HideFlags.HideAndDontSave;
                }
                _camera = Create("Scene Tagger camera").AddComponent<Camera>();
                _cameraId = ConvaiObjectId.Of(_camera);
                _camera.enabled = false;
                _camera.scene = _preview;
                _camera.fieldOfView = 35;
                _camera.aspect = 1;
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.backgroundColor = new Color32(30, 30, 30, 255);
                _camera.allowHDR = true;
                _camera.allowMSAA = false;
                _pipeline.Configure(_camera, row.Target.scene, _resources);
                for (int i = 0; i < 3; i++)
                {
                    Light light = Create("Capture fill").AddComponent<Light>();
                    light.type = LightType.Spot;
                    light.spotAngle = 58;
                    light.innerSpotAngle = 32;
                    light.shadows = LightShadows.None;
                    light.bounceIntensity = 0;
                    light.color = Color.white;
                    light.enabled = false;
                    _recovery.Add(light);
                }
                Resize(resolution);
                LocalDirection = row.Evidence?.LocalDirection ?? new Vector3(0, .15f, 1).normalized;
                Pan = row.Evidence?.Pan ?? Vector2.zero;
                DistanceScale = row.Evidence?.DistanceScale ?? 1;
                _highlightExposure = row.Evidence?.HighlightExposure ?? -1;
                SetLightingPolicy(row.Evidence?.LightingPolicy ?? 0);
                SetProfile(row.Evidence?.LightingProfile ?? 0);
            }
            catch { Dispose(); throw; }
        }

        private GameObject Create(string name)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            SceneManager.MoveGameObjectToScene(go, _preview);
            return go;
        }
        private Transform CopyTransform(Transform original)
        {
            if (_transforms.TryGetValue(original, out Transform copy)) return copy;
            copy = Create(original.name).transform;
            if (original.parent) copy.SetParent(CopyTransform(original.parent), false);
            copy.localPosition = original.localPosition;
            copy.localRotation = original.localRotation;
            copy.localScale = original.localScale;
            _transforms.Add(original, copy);
            return copy;
        }
        private void CopyRenderer(Renderer source)
        {
            Transform transform = CopyTransform(source.transform);
            transform.gameObject.layer = source.gameObject.layer;
            Mesh mesh;
            if (source is SkinnedMeshRenderer skin)
            {
                mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                skin.BakeMesh(mesh);
                mesh.RecalculateBounds();
                _resources.Add(mesh);
            }
            else mesh = source.GetComponent<MeshFilter>().sharedMesh;
            transform.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer target = transform.gameObject.AddComponent<MeshRenderer>();
            target.sharedMaterials = source.sharedMaterials;
            target.shadowCastingMode = source.shadowCastingMode;
            target.receiveShadows = source.receiveShadows;
            target.renderingLayerMask = source.renderingLayerMask;
            target.motionVectorGenerationMode = source.motionVectorGenerationMode;
            target.allowOcclusionWhenDynamic = source.allowOcclusionWhenDynamic;
            target.lightmapIndex = source.lightmapIndex;
            target.lightmapScaleOffset = source.lightmapScaleOffset;
            target.reflectionProbeUsage = source.reflectionProbeUsage;
            if (source.probeAnchor) target.probeAnchor = CopyTransform(source.probeAnchor);
            CopyLightingProperties(source, target);
            for (int i = 0; i < source.sharedMaterials.Length; i++)
            {
                Material material = source.sharedMaterials[i];
                if (material && material.mainTexture is Texture2D texture && texture.streamingMipmaps && !_oldMips.ContainsKey(texture))
                {
                    _oldMips.Add(texture, texture.requestedMipmapLevel);
                    texture.requestedMipmapLevel = 0;
                    _streaming.Add(texture);
                }
            }
            _renderers.Add(target);
        }

        internal static void CopyLightingProperties(Renderer source, Renderer target)
        {
            // Freeze interpolated probes at the authored anchor. Keep custom shader
            // data intact, including the material-specific blocks that override it.
            bool interpolate = source.lightProbeUsage == LightProbeUsage.BlendProbes ||
                source.lightProbeUsage == LightProbeUsage.UseProxyVolume;
            SphericalHarmonicsL2[] probes = null;
            Vector4[] occlusion = null;
            if (interpolate)
            {
                probes = new SphericalHarmonicsL2[1];
                occlusion = new Vector4[1];
                Vector3 position = source.probeAnchor ? source.probeAnchor.position : source.bounds.center;
                LightProbes.CalculateInterpolatedLightAndOcclusionProbes(new[] { position }, probes, occlusion);
            }
            target.lightProbeUsage = interpolate ? LightProbeUsage.CustomProvided : source.lightProbeUsage;
            var block = new MaterialPropertyBlock();
            source.GetPropertyBlock(block);
            SetBlock(-1);
            for (int i = 0; i < source.sharedMaterials.Length; i++)
            {
                source.GetPropertyBlock(block, i);
                if (!block.isEmpty) SetBlock(i);
            }

            void SetBlock(int index)
            {
                if (interpolate)
                {
                    block.CopySHCoefficientArraysFrom(probes);
                    block.CopyProbeOcclusionArrayFrom(occlusion);
                }
                if (index < 0) target.SetPropertyBlock(block);
                else target.SetPropertyBlock(block, index);
            }
        }

        internal void Resize(int size)
        {
            if (_target && _target.width == size) return;
            if (_target) Object.DestroyImmediate(_target);
            if (_mask) Object.DestroyImmediate(_mask);
            _target = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { hideFlags = HideFlags.HideAndDontSave, name = "Scene Tagger evidence" };
            _mask = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { hideFlags = HideFlags.HideAndDontSave, name = "Scene Tagger geometry" };
            _target.Create();
            _mask.Create();
            _rendered = false;
        }

        internal void Render()
        {
            if (_disposed || !_row.IsCurrent) throw new OperationCanceledException("The captured scene object is no longer available.");
            Vector3 direction = _row.Target.transform.TransformDirection(LocalDirection).normalized;
            Vector3 up = Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > .98f ? Vector3.forward : Vector3.up;
            Quaternion rotation = Quaternion.LookRotation(-direction, up);
            Vector3 right = rotation * Vector3.right;
            up = rotation * Vector3.up;
            float distance = FitDistance(_row.Corners.SelectMany(c => c).Select(point => _row.Target.transform.position + _row.Target.transform.rotation * point), _row.Bounds.center, right, up, -direction);
            Vector3 center = _row.Bounds.center + (right * Pan.x + up * Pan.y) * _row.Bounds.size.magnitude;
            _camera.transform.SetPositionAndRotation(center + direction * distance * DistanceScale, rotation);
            _camera.nearClipPlane = Mathf.Max(.001f, distance * .001f);
            _camera.farClipPlane = Mathf.Max(10, distance * 5 + _row.Bounds.size.magnitude * 3);
            PositionLights(direction, right, up, center, distance);
            _pipeline.SetExposure(_camera, ExposureStops);
            _pipeline.SetSpecular(_camera, _lightingPolicy != 0 && !(_lightingPolicy == 2 && _profile >= 8));
            _pipeline.Render(_camera, _target);
            _rendered = true;
            _displayedDirection = LocalDirection;
            _displayedPan = Pan;
            _displayedDistanceScale = DistanceScale;
        }

        internal static float FitDistance(IEnumerable<Vector3> points, Vector3 center, Vector3 right, Vector3 up, Vector3 forward)
        {
            float distance = .01f;
            float tangent = Mathf.Tan(17.5f * Mathf.Deg2Rad) * .8f;
            foreach (Vector3 point in points)
            {
                Vector3 p = point - center;
                distance = Mathf.Max(distance, Mathf.Max(Mathf.Abs(Vector3.Dot(p, right)), Mathf.Abs(Vector3.Dot(p, up))) / tangent - Vector3.Dot(p, forward));
            }
            return distance;
        }

        private void SetProfile(int index)
        {
            _profile = Mathf.Clamp(index, 0, Exposure.Length - 1);
            _pipeline.SetExposure(_camera, ExposureStops);
            foreach (var pair in _specular)
            {
                bool suppress = _lightingPolicy != 0 && !(_lightingPolicy == 2 && _profile >= 8);
                pair.Key.SetFloat("_SpecularHighlights", suppress ? 0 : pair.Value);
                if (suppress) pair.Key.EnableKeyword("_SPECULARHIGHLIGHTS_OFF"); else pair.Key.DisableKeyword("_SPECULARHIGHLIGHTS_OFF");
            }
        }
        private void SetLightingPolicy(uint policy)
        {
            _lightingPolicy = policy;
            if (policy == 0 || _specular.Count > 0) return;
            var copies = new Dictionary<Material, Material>();
            foreach (MeshRenderer renderer in _renderers)
            {
                Material[] materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    Material source = materials[i];
                    if (!source || !source.HasProperty("_SpecularHighlights")) continue;
                    if (!copies.TryGetValue(source, out Material copy))
                    {
                        copy = new Material(source) { hideFlags = HideFlags.HideAndDontSave };
                        copies[source] = copy; _resources.Add(copy);
                        _specular[copy] = copy.GetFloat("_SpecularHighlights");
                    }
                    materials[i] = copy;
                }
                renderer.sharedMaterials = materials;
            }
        }

        private void PositionLights(Vector3 direction, Vector3 right, Vector3 up, Vector3 center, float distance)
        {
            float radius = Mathf.Max(.1f, _row.Bounds.extents.magnitude);
            for (int i = 0; i < _recovery.Count; i++)
            {
                Light light = _recovery[i];
                light.enabled = _profile >= 3 && (i == 0 || i == 1 && _profile >= 5 && _profile < 8 || i == 2 && _profile >= 6);
                Vector3 offset = i == 0 ? direction : i == 1 ? (-direction + right * .4f).normalized : (direction + right + up * .5f).normalized;
                light.transform.position = center + offset * Mathf.Max(radius * 2, distance * .7f);
                light.transform.rotation = Quaternion.LookRotation(center - light.transform.position);
                light.range = radius * 8 + distance * 2;
                light.intensity = (i == 0 ? 2.8f : i == 1 ? .84f : 1.82f) * (_profile >= 8 ? 2 : 1);
            }
        }

        private void RenderMask()
        {
            if (!_maskShader) _maskShader = Shader.Find("Hidden/Convai/SceneTaggerMask");
            if (!_maskShader) throw new InvalidOperationException("Scene Tagger mask shader is missing.");
            using var commands = new CommandBuffer { name = "Scene Tagger geometry mask" };
            commands.SetRenderTarget(_mask);
            commands.ClearRenderTarget(true, true, Color.black);
            commands.SetViewProjectionMatrices(_camera.worldToCameraMatrix, GL.GetGPUProjectionMatrix(_camera.projectionMatrix, true));
            foreach (MeshRenderer renderer in _renderers)
            {
                Mesh mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
                for (int i = 0; i < mesh.subMeshCount; i++)
                {
                    Material source = renderer.sharedMaterials.ElementAtOrDefault(i);
                    if (!_maskMaterials.TryGetValue((renderer, i), out Material material))
                    {
                        material = new Material(_maskShader) { hideFlags = HideFlags.HideAndDontSave };
                        _maskMaterials.Add((renderer, i), material);
                        _resources.Add(material);
                    }
                    // Shared materials may change during Edit View. Refresh properties without
                    // reallocating the cached mask material, including defaults for removed maps.
                    material.SetTexture("_MainTex", Texture2D.whiteTexture);
                    material.SetTextureScale("_MainTex", Vector2.one);
                    material.SetTextureOffset("_MainTex", Vector2.zero);
                    string textureName = source && source.HasProperty("_BaseMap") ? "_BaseMap" : source && source.HasProperty("_BaseColorMap") ? "_BaseColorMap" : "_MainTex";
                    if (source && source.HasProperty(textureName))
                    {
                        material.SetTexture("_MainTex", source.GetTexture(textureName));
                        material.SetTextureScale("_MainTex", source.GetTextureScale(textureName));
                        material.SetTextureOffset("_MainTex", source.GetTextureOffset(textureName));
                    }
                    MaterialPropertyBlock block = _maskProperties;
                    block.Clear();
                    renderer.GetPropertyBlock(block, i);
                    if (block.isEmpty) renderer.GetPropertyBlock(block);
                    int textureId = Shader.PropertyToID(textureName);
                    if (block.HasProperty(textureId)) material.SetTexture("_MainTex", block.GetTexture(textureId));
                    int stId = Shader.PropertyToID(textureName + "_ST");
                    if (block.HasProperty(stId)) material.SetVector("_MainTex_ST", block.GetVector(stId));
                    string colorName = source && source.HasProperty("_BaseColor") ? "_BaseColor" : "_Color";
                    int colorId = Shader.PropertyToID(colorName);
                    float alpha = block.HasProperty(colorId) ? block.GetColor(colorId).a : source && source.HasProperty(colorId) ? source.GetColor(colorId).a : 1;
                    material.SetFloat("_Alpha", alpha);
                    bool clipped = source && (source.IsKeywordEnabled("_ALPHATEST_ON") || source.HasProperty("_AlphaCutoffEnable") && source.GetFloat("_AlphaCutoffEnable") > 0);
                    string cutoffName = source && source.HasProperty("_AlphaCutoff") ? "_AlphaCutoff" : "_Cutoff";
                    int cutoffId = Shader.PropertyToID(cutoffName);
                    float cutoff = block.HasProperty(cutoffId) ? block.GetFloat(cutoffId) : source && source.HasProperty(cutoffId) ? source.GetFloat(cutoffId) : .5f;
                    bool transparent = source && source.renderQueue >= 3000;
                    material.SetFloat("_Cutoff", clipped ? cutoff : transparent ? .01f : 0);
                    commands.DrawMesh(mesh, renderer.localToWorldMatrix, material, i);
                }
            }
            Graphics.ExecuteCommandBuffer(commands);
        }

        internal async Task<SceneTaggerPixels> ReadAsync(CancellationToken cancellation, bool useDisplayedImage = false)
        {
            cancellation.ThrowIfCancellationRequested();
            if (_disposed) throw new OperationCanceledException();
            if (!useDisplayedImage || !_rendered) Render();
            RenderMask();
            int width = _target.width, height = _target.height;
            // Queue both transfers while the capture resources are still owned by this call.
            Task<byte[]> imageRead = ReadTextureAsync(_target, cancellation, _readbacks), maskRead = ReadTextureAsync(_mask, cancellation, _readbacks);
            await Task.WhenAll(imageRead, maskRead);
            byte[] rgba = imageRead.Result, maskRgba = maskRead.Result;
            cancellation.ThrowIfCancellationRequested();
            if (_disposed) throw new OperationCanceledException();
            byte[] mask = new byte[width * height];
            for (int i = 0; i < mask.Length; i++)
            {
                mask[i] = maskRgba[i * 4] > 127 ? (byte)255 : (byte)0;
                rgba[i * 4 + 3] = 255;
            }
            return new SceneTaggerPixels { Width = width, Height = height, Rgba = rgba, Mask = mask };
        }

        internal static async Task<byte[]> CaptureSceneViewAsync(Camera camera, CancellationToken cancellation)
        {
            if (!camera) throw new InvalidOperationException("Open a Scene view and frame the environment first.");
            float scale = Mathf.Min(1f, 1536f / Mathf.Max(camera.pixelWidth, camera.pixelHeight));
            int width = Mathf.Max(1, Mathf.RoundToInt(camera.pixelWidth * scale));
            int height = Mathf.Max(1, Mathf.RoundToInt(camera.pixelHeight * scale));
            ISceneTaggerRenderPipeline pipeline = SceneTaggerRenderPipelines.Resolve();
            long cameraId = ConvaiObjectId.Of(camera);
            Scene cameraScene = camera.scene;
            RenderTexture target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            try
            {
                // The Scene view's own target can be stale or cleared while another window covers it.
                // Submit a fresh render instead of reading that target outside the Scene view render loop.
                for (int frame = 0; frame < 3; frame++)
                {
                    await NextEditorFrame(cancellation);
                    if (!camera) throw new InvalidOperationException("The Scene view was closed during capture.");
                    RenderTexture previous = camera.targetTexture;
                    try { pipeline.Render(camera, target); }
                    finally { if (camera) camera.targetTexture = previous; }
                }
                byte[] rgba = await ReadTextureAsync(target, cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (!rgba.Where((value, i) => i % 4 != 3).Any(value => value > 8))
                    throw new InvalidOperationException("The Scene view capture is black. Frame visible objects and check scene lighting before trying again.");
                return Encode(new SceneTaggerPixels { Width = width, Height = height, Rgba = rgba });
            }
            finally { pipeline.Cleanup(cameraId, cameraScene); RenderTexture.ReleaseTemporary(target); }
        }

        private static async Task<byte[]> ReadTextureAsync(RenderTexture texture, CancellationToken cancellation,
            List<AsyncGPUReadbackRequest> pending = null)
        {
            cancellation.ThrowIfCancellationRequested();
            if (SystemInfo.supportsAsyncGPUReadback)
            {
                var completion = new TaskCompletionSource<byte[]>();
                AsyncGPUReadbackRequest readbackRequest = AsyncGPUReadback.Request(texture, 0, TextureFormat.RGBA32, request =>
                {
                    if (request.hasError || cancellation.IsCancellationRequested) completion.TrySetResult(null);
                    else completion.TrySetResult(request.GetData<byte>().ToArray());
                });
                pending?.Add(readbackRequest);
                try
                {
                    while (!completion.Task.IsCompleted) await NextEditorFrame(cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    byte[] data = await completion.Task;
                    if (data != null) return data;
                }
                finally
                {
                    // A cancelled caller may release the texture immediately. Drain only this
                    // transfer, never unrelated requests issued by the user's scene or other tools.
                    if (!readbackRequest.done) readbackRequest.WaitForCompletion();
                    pending?.Remove(readbackRequest);
                }
            }
            cancellation.ThrowIfCancellationRequested();
            RenderTexture previous = RenderTexture.active;
            var readback = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = texture;
                readback.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                readback.Apply();
                return readback.GetRawTextureData<byte>().ToArray();
            }
            finally { RenderTexture.active = previous; Object.DestroyImmediate(readback); }
        }

        internal async Task WarmAsync(int minimumFrames, double minimumSeconds, int maximumFrames, double maximumSeconds, CancellationToken cancellation)
        {
            double start = EditorApplication.timeSinceStartup;
            int frames = 0;
            do
            {
                cancellation.ThrowIfCancellationRequested();
                Render();
                await NextEditorFrame(cancellation);
                frames++;
            } while (frames < minimumFrames || EditorApplication.timeSinceStartup - start < minimumSeconds ||
                     frames < maximumFrames && EditorApplication.timeSinceStartup - start < maximumSeconds && _streaming.Any(t => t && !t.IsRequestedMipmapLevelLoaded()));
        }

        internal static Task NextEditorFrame(CancellationToken cancellation)
        {
            var completion = new TaskCompletionSource<bool>();
            void Tick()
            {
                EditorApplication.update -= Tick;
                if (cancellation.IsCancellationRequested) completion.TrySetCanceled(); else completion.TrySetResult(true);
            }
            EditorApplication.update += Tick;
            EditorApplication.QueuePlayerLoopUpdate();
            return completion.Task;
        }

        internal async Task<SceneTaggerPixels> RecoverAsync(CancellationToken cancellation)
        {
            SceneTaggerPixels best = await ReadAsync(cancellation);
            SceneTaggerImageQuality quality = SceneTaggerImageQuality.Measure(best);
            int bestProfile = _profile;
            bool suppress = _lightingPolicy != 0;
            if (!suppress && SceneTaggerLightingPolicy.ShouldAttemptHighlightRecovery(quality))
            {
                _highlightExposure = SceneTaggerLightingPolicy.ComputeHighlightRecoveryExposureBias(quality);
                await Probe(1, SceneTaggerLightingPolicy.PreferHighlightRecovery);
            }
            if (quality.NeedsRecovery) await Probe(2, (a, b) => b.PreferReadability(a));
            if (quality.NeedsRecovery) await Probe(3, (a, b) => b.PreferReadability(a));
            if (quality.NeedsRecovery || SceneTaggerLightingPolicy.ShouldAttemptPlanarDiffuseExposureRecovery(quality, suppress, _lightingPolicy == 2))
                await Probe(4, (a, b) => b.PreferReadability(a) || SceneTaggerLightingPolicy.PreferPlanarDiffuseExposureRecovery(a, b));
            if (SceneTaggerLightingPolicy.ShouldAttemptOpposingDiffuseRecovery(quality, bestProfile == 3 || bestProfile == 4, suppress))
                await Probe(5, SceneTaggerLightingPolicy.PreferOpposingDiffuseRecovery);
            if (SceneTaggerLightingPolicy.ShouldAttemptDimensionalShadowBalanceRecovery(quality, suppress))
                await Probe(6, SceneTaggerLightingPolicy.PreferDimensionalShadowBalanceRecovery);
            if (SceneTaggerLightingPolicy.ShouldAttemptDimensionalReadabilityFloorRecovery(quality, suppress))
                await Probe(7, SceneTaggerLightingPolicy.PreferDimensionalReadabilityFloorRecovery);
            if (quality.NeedsRecovery || _lightingPolicy == 2 && !quality.Readable)
                for (int profile = 8; profile < Exposure.Length; profile++)
                {
                    if (suppress && _lightingPolicy != 2) break;
                    await Probe(profile, (a, b) => !a.Readable ? b.PreferReadability(a) : b.PreferKey(a));
                    if (quality.Readable && !quality.NeedsRecovery) break;
                }
            SetProfile(bestProfile);
            await WarmAsync(2, 0, 2, .1, cancellation);
            return best;

            async Task Probe(int profile, Func<SceneTaggerImageQuality, SceneTaggerImageQuality, bool> prefer)
            {
                SetProfile(profile);
                await WarmAsync(2, 0, 2, .1, cancellation);
                SceneTaggerPixels trial = await ReadAsync(cancellation);
                SceneTaggerImageQuality measured = SceneTaggerImageQuality.Measure(trial);
                if (prefer(quality, measured)) { best = trial; quality = measured; bestProfile = profile; }
            }
        }

        internal async Task<SceneTaggerEvidence> CaptureAsync(SceneTaggerSettings settings, CancellationToken cancellation, uint requestFlags = 0)
        {
            SceneTaggerCoreAdapter core = SceneTaggerCoreAdapter.Instance;
            convai_sat_view_plan_v1 plan = core.Plan(_row.Corners, _row.Metallic, _row.Evidence?.PresentationDirection);
            SetLightingPolicy(plan.lighting_policy);
            SetProfile(0);
            Resize(384);
            for (int i = 0; i < plan.candidate_count; i++) { LocalDirection = SceneTaggerCoreAdapter.FromCore(plan.candidates[i].direction_actor_local); Render(); await NextEditorFrame(cancellation); }
            var probes = new List<SceneTaggerPixels>();
            for (int i = 0; i < plan.candidate_count; i++)
            {
                LocalDirection = SceneTaggerCoreAdapter.FromCore(plan.candidates[i].direction_actor_local);
                await WarmAsync(3, .1, 12, .75, cancellation);
                probes.Add(await ReadAsync(cancellation));
            }
            convai_sat_select_view_result_v4 selected = core.Select(plan, probes);
            // Core returns -1 when none of the probes contains usable evidence.
            // Keep its first planned direction for bounded recovery and failure review.
            int selectedIndex = selected.selected_index < 0 ? 0 : selected.selected_index;
            LocalDirection = SceneTaggerCoreAdapter.FromCore(plan.candidates[selectedIndex].direction_actor_local);
            Resize(settings.EvidenceSize);
            await WarmAsync(5, 0, 60, 3, cancellation);
            SceneTaggerPixels pixels = await RecoverAsync(cancellation);
            uint flags = requestFlags | (_row.Explicit ? 4u : 0u) | (_row.Target.GetComponent<Convai.Runtime.SceneMetadata.ConvaiObjectMetadata>() ? 16u : 0u);
            if ((plan.flags & 2) != 0) flags |= 8;
            var evaluated = core.Evaluate(pixels, selected.metrics, _row.Significance, _row.SignificanceReasons, _row.Renderers.Length, flags);
            SceneTaggerEvidence evidence = Evidence(pixels, evaluated, (plan.flags & 2) != 0);
            SceneTaggerImageQuality quality = SceneTaggerImageQuality.Measure(pixels);
            if (!quality.Readable) evidence.Failure = quality.Foreground >= 64 ? "The capture is too dark after lighting recovery. Adjust its view and try again." : "The capture contains no visible geometry. Adjust its view or check the materials.";
            else if (evaluated.decision != 0) { evidence.LowInformation = true; evidence.Failure = "No useful visual detail. Check this row to analyze the retained image anyway."; }
            return evidence;
        }

        internal async Task<SceneTaggerEvidence> CommitAsync(CancellationToken cancellation)
        {
            if (_rendered) { LocalDirection = _displayedDirection; Pan = _displayedPan; DistanceScale = _displayedDistanceScale; }
            var pixels = await ReadAsync(cancellation, true);
            if (!SceneTaggerImageQuality.Measure(pixels).Readable) throw new InvalidOperationException("The current view is empty or too dark. Adjust the angle and try again.");
            var evaluated = SceneTaggerCoreAdapter.Instance.Evaluate(pixels, null, _row.Significance, _row.SignificanceReasons, _row.Renderers.Length, 6);
            SceneTaggerEvidence evidence = Evidence(pixels, evaluated, _row.Evidence?.BroadFace ?? false);
            evidence.PresentationDirection = LocalDirection;
            return evidence;
        }
        private SceneTaggerEvidence Evidence(SceneTaggerPixels pixels, convai_sat_evaluate_capture_result_v4 evaluation, bool broadFace) => new()
        {
            Png = Encode(pixels), LocalDirection = LocalDirection, Pan = Pan, DistanceScale = DistanceScale,
            PresentationDirection = _row.Evidence?.PresentationDirection,
            LightingProfile = _profile, HighlightExposure = _highlightExposure, LightingPolicy = _lightingPolicy, Descriptor = evaluation.descriptor, DescriptorValid = evaluation.descriptor_valid != 0,
            BroadFace = broadFace, Policy = SceneTaggerCoreAdapter.Instance.Policy
        };
        internal static byte[] Encode(SceneTaggerPixels pixels)
        {
            var texture = new Texture2D(pixels.Width, pixels.Height, TextureFormat.RGBA32, false);
            try { texture.LoadRawTextureData(pixels.Rgba); texture.Apply(); return texture.EncodeToPNG(); }
            finally { Object.DestroyImmediate(texture); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Active.Remove(this);
            foreach (var request in _readbacks.ToArray()) if (!request.done) request.WaitForCompletion();
            _pipeline.Cleanup(_cameraId, _preview);
            foreach (var pair in _oldMips) if (pair.Key) { if (pair.Value < 0) pair.Key.ClearRequestedMipmapLevel(); else pair.Key.requestedMipmapLevel = pair.Value; }
            if (_target) Object.DestroyImmediate(_target);
            if (_mask) Object.DestroyImmediate(_mask);
            if (_preview.IsValid()) EditorSceneManager.ClosePreviewScene(_preview);
            foreach (Object resource in _resources) if (resource) Object.DestroyImmediate(resource);
        }
    }

    internal struct SceneTaggerImageQuality
    {
        internal int ForegroundSamples;
        internal float ForegroundFraction, MeanLuminance, P10Luminance, P25Luminance, MedianLuminance, P90Luminance,
            ToneSpan, InteriorDetail, ShoulderFraction, ClippedFraction, LowChromaFraction;
        internal int Foreground => ForegroundSamples;
        internal bool HasGeometry => ForegroundSamples >= 64 && ForegroundFraction >= .02f;
        internal bool Readable => HasGeometry && MeanLuminance >= 4 && P90Luminance >= 32;
        internal bool NeedsRecovery => !HasGeometry || SceneTaggerLightingPolicy.ShouldAttemptDarkRecovery(this);
        internal bool PreferReadability(SceneTaggerImageQuality original) => HasGeometry && ShoulderFraction <= .30f &&
            ClippedFraction <= .04f && P90Luminance >= 32 &&
            (!original.HasGeometry || SceneTaggerLightingPolicy.PreferDarkRecovery(original, this));
        internal bool PreferKey(SceneTaggerImageQuality original) => PreferReadability(original) &&
            ShoulderFraction <= .18f && ClippedFraction <= .015f && P90Luminance <= 242 &&
            (!original.HasGeometry ? P90Luminance >= 48 && ToneSpan >= 18 && InteriorDetail >= 3 :
                MedianLuminance >= original.MedianLuminance + 12 && P90Luminance >= original.P90Luminance + 16 &&
                ToneSpan >= Mathf.Max(18, original.ToneSpan * .90f) && InteriorDetail >= Mathf.Max(original.InteriorDetail + .5f, original.InteriorDetail * 1.05f));

        internal static SceneTaggerImageQuality Measure(SceneTaggerPixels pixels)
        {
            var result = new SceneTaggerImageQuality();
            var histogram = new int[256];
            int clipped = 0, shoulder = 0, lowChroma = 0, samples = 0, pairs = 0;
            int step = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(pixels.Width * pixels.Height / 65536f)));
            float detail = 0;
            for (int y = 0; y < pixels.Height; y += step)
            for (int x = 0; x < pixels.Width; x += step)
            {
                samples++;
                int i = y * pixels.Width + x;
                if (pixels.Mask != null && pixels.Mask[i] == 0) continue;
                int r = pixels.Rgba[i * 4], g = pixels.Rgba[i * 4 + 1], b = pixels.Rgba[i * 4 + 2];
                int luminance = Luminance(i);
                histogram[luminance]++;
                result.MeanLuminance += luminance;
                result.ForegroundSamples++;
                if (luminance >= 224) shoulder++;
                if (luminance >= 248 && Mathf.Max(r, g, b) >= 253) clipped++;
                if (Mathf.Max(r, g, b) - Mathf.Min(r, g, b) <= 18) lowChroma++;
                Accumulate(x + step, y, luminance);
                Accumulate(x, y + step, luminance);
            }
            if (result.ForegroundSamples == 0) return result;
            result.MeanLuminance /= result.ForegroundSamples;
            result.ForegroundFraction = (float)result.ForegroundSamples / samples;
            result.ShoulderFraction = (float)shoulder / result.ForegroundSamples;
            result.ClippedFraction = (float)clipped / result.ForegroundSamples;
            result.LowChromaFraction = (float)lowChroma / result.ForegroundSamples;
            result.InteriorDetail = pairs == 0 ? 0 : detail / pairs;
            int foreground = result.ForegroundSamples;
            result.P10Luminance = Percentile(.1f);
            result.P25Luminance = Percentile(.25f);
            result.MedianLuminance = Percentile(.5f);
            result.P90Luminance = Percentile(.9f);
            result.ToneSpan = result.P90Luminance - result.P10Luminance;
            return result;

            int Luminance(int index) => (54 * pixels.Rgba[index * 4] + 183 * pixels.Rgba[index * 4 + 1] + 19 * pixels.Rgba[index * 4 + 2]) >> 8;
            void Accumulate(int x, int y, int luminance)
            {
                if (x >= pixels.Width || y >= pixels.Height) return;
                int index = y * pixels.Width + x;
                if (pixels.Mask != null && pixels.Mask[index] == 0) return;
                detail += Mathf.Abs(luminance - Luminance(index)); pairs++;
            }
            float Percentile(float value)
            {
                int count = 0;
                for (int i = 0; i < histogram.Length; i++) { count += histogram[i]; if (count >= foreground * value) return i; }
                return 255;
            }
        }
    }
}
