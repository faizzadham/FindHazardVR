using Convai.Shared.Compatibility;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Convai.Editor.SceneTagging
{
    [InitializeOnLoad]
    internal sealed class SceneTaggerUrpPipeline : ISceneTaggerRenderPipeline
    {
        private readonly Dictionary<long, ColorAdjustments> _exposure = new();
        private readonly Dictionary<long, VolumeStack> _stacks = new();
        private readonly Dictionary<long, List<Volume>> _volumes = new();
        private readonly Dictionary<long, int> _volumeLayers = new();
        static SceneTaggerUrpPipeline() => SceneTaggerRenderPipelines.Adapters.Add(new SceneTaggerUrpPipeline());
        public bool SupportsCurrentPipeline => GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset;
        public void Configure(Camera camera, Scene sourceScene, List<Object> resources)
        {
            UniversalAdditionalCameraData data = camera.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            var sources = SceneTaggerRenderPipelines.LoadedComponents<Volume>(sourceScene).Where(v => v.enabled).ToArray();
            // Avoid an authored volume on the preferred capture layer without changing project layers.
            int layer = SceneTaggerRenderPipelines.CaptureVolumeLayer;
            while (layer >= 8 && sources.Any(v => v.gameObject.layer == layer)) layer--;
            if (layer < 8) throw new System.InvalidOperationException("No unused volume layer is available for isolated capture.");
            long id = ConvaiObjectId.Of(camera);
            _volumeLayers[id] = 1 << layer;
            var volumes = new List<Volume>();
            _volumes[id] = volumes;
            data.volumeLayerMask = 1 << layer;
            data.volumeTrigger = camera.transform;
            data.antialiasing = AntialiasingMode.None;
            foreach (Volume source in sources)
            {
                var go = new GameObject("Scene volume") { hideFlags = HideFlags.HideAndDontSave, layer = layer };
                SceneManager.MoveGameObjectToScene(go, camera.scene);
                go.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
                go.transform.localScale = source.transform.lossyScale;
                Volume volume = go.AddComponent<Volume>();
                EditorUtility.CopySerialized(source, volume);
                volume.enabled = false;
                volumes.Add(volume);
                foreach (Collider collider in source.GetComponents<Collider>()) EditorUtility.CopySerialized(collider, go.AddComponent(collider.GetType()));
            }
            var recovery = new GameObject("Capture exposure") { hideFlags = HideFlags.HideAndDontSave, layer = layer };
            SceneManager.MoveGameObjectToScene(recovery, camera.scene);
            Volume correction = recovery.AddComponent<Volume>();
            correction.enabled = false;
            volumes.Add(correction);
            correction.isGlobal = true;
            correction.priority = float.MaxValue;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.hideFlags = HideFlags.HideAndDontSave;
            resources.Add(profile);
            correction.sharedProfile = profile;
            ColorAdjustments exposure = profile.Add<ColorAdjustments>();
            resources.Add(exposure);
            exposure.active = false;
            _exposure[ConvaiObjectId.Of(camera)] = exposure;
        }
        public void SetExposure(Camera camera, float stops)
        {
            if (!_exposure.TryGetValue(ConvaiObjectId.Of(camera), out ColorAdjustments exposure) || !exposure) return;
            exposure.active = false;
            if (Mathf.Abs(stops) <= .001f) return;
            if (!VolumeManager.instance.isInitialized) return;
            if (!_stacks.TryGetValue(ConvaiObjectId.Of(camera), out VolumeStack stack) || stack == null || !stack.isValid)
                _stacks[ConvaiObjectId.Of(camera)] = stack = VolumeManager.instance.CreateStack();
            SetVolumesEnabled(ConvaiObjectId.Of(camera), true);
            try { VolumeManager.instance.Update(stack, camera.transform, _volumeLayers[ConvaiObjectId.Of(camera)]); }
            finally { SetVolumesEnabled(ConvaiObjectId.Of(camera), false); }
            float baseline = stack.GetComponent<ColorAdjustments>()?.postExposure.value ?? 0;
            exposure.postExposure.Override(baseline + stops);
            exposure.active = true;
        }
        private void SetVolumesEnabled(long id, bool enabled)
        {
            if (_volumes.TryGetValue(id, out var volumes))
                foreach (Volume volume in volumes) if (volume) volume.enabled = enabled;
        }
        public void Cleanup(long id, Scene scene)
        {
            SetVolumesEnabled(id, false);
            _volumes.Remove(id);
            _volumeLayers.Remove(id);
            _exposure.Remove(id);
            if (_stacks.TryGetValue(id, out VolumeStack stack)) VolumeManager.instance.DestroyStack(stack);
            _stacks.Remove(id);
        }
        public void CopyLight(Light source, Light destination)
        {
            var data = source.GetComponent<UniversalAdditionalLightData>();
            if (data) EditorUtility.CopySerialized(data, destination.gameObject.AddComponent<UniversalAdditionalLightData>());
        }
        public void SetSpecular(Camera camera, bool suppress) { }
        public void Render(Camera camera, RenderTexture destination)
        {
            var request = new UniversalRenderPipeline.SingleCameraRequest { destination = destination };
            if (!RenderPipeline.SupportsRenderRequest(camera, request)) throw new System.NotSupportedException("URP does not support this camera render request.");
            // SingleCameraRequest skips URP's normal camera-stack volume update. Without this,
            // captures inherit the previous camera's post effects and exposure recovery has no effect.
            var volumes = VolumeManager.instance;
            VolumeStack previous = volumes.stack;
            long id = ConvaiObjectId.Of(camera);
            // Pipeline reinitialization invalidates every stack owned by VolumeManager.
            if (!_stacks.TryGetValue(id, out VolumeStack stack) || stack == null || !stack.isValid)
                _stacks[id] = stack = volumes.CreateStack();
            SetVolumesEnabled(id, true);
            try
            {
                camera.TryGetComponent(out UniversalAdditionalCameraData data);
                volumes.Update(stack, data && data.volumeTrigger ? data.volumeTrigger : camera.transform, data ? data.volumeLayerMask : (LayerMask)~0);
                volumes.stack = stack;
                RenderPipeline.SubmitRenderRequest(camera, request);
            }
            finally
            {
                if (previous != null && previous.isValid) volumes.stack = previous;
                else volumes.ResetMainStack();
                SetVolumesEnabled(id, false);
            }
        }
    }
}
