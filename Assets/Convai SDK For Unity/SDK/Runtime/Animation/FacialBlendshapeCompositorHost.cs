using System.Collections.Generic;
using Convai.Runtime.Embodiment;
using Convai.Runtime.Components;
using UnityEngine;
using Convai.Domain.Logging;
using Convai.Runtime.Logging;
using Unity.Profiling;

namespace Convai.Runtime.Animation
{
    /// <summary>
    ///     Single authoritative writer for runtime facial blendshape output on a character.
    ///     Sources submit logical layer weights during the frame and the host writes the final
    ///     composed result once in <see cref="LateUpdate" />.
    /// </summary>
    /// <remarks>
    ///     Supports both built-in layers (see <see cref="FacialBlendshapeLayers" />) and
    ///     user-registered custom layers via <see cref="RegisterCustomLayer" />.
    /// </remarks>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(EmbodimentExecutionOrders.FacialCompositor)]
    [AddComponentMenu("")]
    internal sealed class FacialBlendshapeCompositorHost : MonoBehaviour
    {
        private static readonly ProfilerMarker LateUpdateMarker = new("Convai.Animation.FacialCompositor.LateUpdate");

        /// <summary>
        ///     Minimum weight delta before writing to <see cref="SkinnedMeshRenderer" /> blendshapes.
        ///     Shared with direct lip-sync output so behavior matches compositor writes.
        /// </summary>
        internal const float BlendshapeWriteEpsilon = 0.0001f;

        private readonly Dictionary<int, Dictionary<BlendshapeTargetKey, float>> _layerFrames = new();
        private readonly HashSet<int> _activeLayersThisFrame = new();
        private readonly HashSet<int> _registeredCustomLayers = new();
        private readonly Dictionary<int, string> _customLayerDebugNames = new();

        private readonly Dictionary<BlendshapeTargetKey, float> _lastApplied = new();
        private readonly List<BlendshapeTargetKey> _composeKeys = new();
        private readonly HashSet<BlendshapeTargetKey> _composeKeySet = new();
        private readonly HashSet<BlendshapeTargetKey> _headOwnedTargets = new();
        private readonly Dictionary<BlendshapeTargetKey, FacialBlendshapeRegion> _regionCache = new();

        private ConvaiFacialCompositionProfile _compositionProfile;
        private int _lastProfileConfigHash;
        private float _speechBlendFactor;
        private EmotionReturnRamp _emotionReturnRamp;
        private bool _lipSyncSpeechReportedThisFrame;
        private bool _lipSyncSpeechActiveThisFrame;
        private int _nextCustomLayerId = FacialBlendshapeLayers.CustomLayerStart;
        private bool _ownsCompositionProfile;
        private bool _loggedComposePassThroughFallback;

        /// <summary>Current smoothed speech factor. 0 = idle, 1 = fully talking.</summary>
        public float SpeechBlendFactor => _speechBlendFactor;

        /// <summary>
        ///     Current Emotion/Custom layer blend factor. Rises with the same onset timing as
        ///     <see cref="SpeechBlendFactor" />, but falls back to idle on its own, slower, eased
        ///     timeline (<see cref="ConvaiFacialCompositionProfile.EmotionReturnDuration" />) so the
        ///     emotion expression settles instead of snapping back the instant speech ends.
        /// </summary>
        public float EmotionSpeechFactor => _emotionReturnRamp.Value;

        /// <summary>Whether multi-region composition is active.</summary>
        public bool IsRegionCompositionActive => _compositionProfile != null;

        public ConvaiFacialCompositionProfile CompositionProfile => _compositionProfile;

        /// <summary>
        ///     Finds an existing compositor on <paramref name="context" /> or under the preferred
        ///     character owner. Never adds a component.
        /// </summary>
        public static FacialBlendshapeCompositorHost TryResolve(Component context)
        {
            if (context == null) return null;

            FacialBlendshapeCompositorHost host = context.GetComponent<FacialBlendshapeCompositorHost>();
            if (host != null)
                return host;

            GameObject owner = ResolvePreferredOwner(context) ?? context.gameObject;

            host = owner.GetComponent<FacialBlendshapeCompositorHost>();
            if (host != null)
                return host;

            FacialBlendshapeCompositorHost[] existingHosts =
                owner.GetComponentsInChildren<FacialBlendshapeCompositorHost>(true);
            return existingHosts.Length > 0 ? existingHosts[0] : null;
        }

        public static FacialBlendshapeCompositorHost GetOrCreate(Component context)
        {
            FacialBlendshapeCompositorHost host = TryResolve(context);
            if (host != null)
                return host;

            if (context == null || !UnityEngine.Application.isPlaying)
                return null;

            GameObject owner = ResolvePreferredOwner(context) ?? context.gameObject;
            FacialBlendshapeCompositorHost created = owner.AddComponent<FacialBlendshapeCompositorHost>();
            created.hideFlags = EmbodimentContext.RuntimeInfrastructureHideFlags();
            return created;
        }

        private void Awake()
        {
            hideFlags = EmbodimentContext.RuntimeInfrastructureHideFlags();
            EnsureDefaultProfileLoaded();
        }

        public void SetCompositionProfile(ConvaiFacialCompositionProfile profile)
        {
            if (ReferenceEquals(_compositionProfile, profile))
                return;

            // Releasing first matters: adopting a caller's asset over a host-created default would
            // otherwise leak that default for the lifetime of the play session.
            ReleaseOwnedProfile();

            _compositionProfile = profile;
            _lastProfileConfigHash = 0;
            InvalidateRegionCache();
        }

        /// <summary>
        ///     Installs the SDK default composition profile when none has been explicitly assigned.
        ///     Safe to call repeatedly.
        /// </summary>
        /// <remarks>
        ///     Was a <c>Resources.Load</c> of a shipped <c>.asset</c>, and could therefore fail — a
        ///     stripped, renamed or shadowed load left the compositor with no profile and dropped
        ///     the character onto <see cref="ComposePassThrough" />. The default is built in code
        ///     now, so the only way to have no profile is to assign <c>null</c> deliberately.
        /// </remarks>
        /// <returns><c>true</c> once a profile is assigned, which is always.</returns>
        public bool EnsureDefaultProfileLoaded()
        {
            if (_compositionProfile != null)
                return true;

            _compositionProfile = ConvaiFacialCompositionProfile.CreateDefault();
            _ownsCompositionProfile = _compositionProfile != null;
            _lastProfileConfigHash = 0;
            InvalidateRegionCache();
            return _compositionProfile != null;
        }

        /// <summary>
        ///     Destroys the profile this host created for itself. An assigned profile is an asset the
        ///     caller owns and is never touched.
        /// </summary>
        private void ReleaseOwnedProfile()
        {
            if (!_ownsCompositionProfile || _compositionProfile == null)
            {
                _ownsCompositionProfile = false;
                return;
            }

            // Fully qualified: the SDK has its own Convai.Application namespace, which shadows
            // UnityEngine.Application inside Convai.* — the same reason line 87 spells it out.
            if (UnityEngine.Application.isPlaying) Destroy(_compositionProfile);
            else DestroyImmediate(_compositionProfile);

            _compositionProfile = null;
            _ownsCompositionProfile = false;
        }

        private void OnDestroy()
        {
            ReleaseOwnedProfile();
        }

        // ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        //  Custom Layer Registration
        // ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        ///     Registers a custom layer and returns its unique layer ID.
        ///     Custom layer values are composed using the Custom weight in <see cref="RegionBlendConfig" />.
        /// </summary>
        public int RegisterCustomLayer(string debugName = null)
        {
            int layerId = _nextCustomLayerId++;
            _registeredCustomLayers.Add(layerId);
            if (!string.IsNullOrEmpty(debugName))
                _customLayerDebugNames[layerId] = debugName;
            return layerId;
        }

        /// <summary>
        ///     Unregisters a previously registered custom layer.
        /// </summary>
        public void UnregisterCustomLayer(int layerId)
        {
            if (!FacialBlendshapeLayers.IsCustom(layerId)) return;
            _registeredCustomLayers.Remove(layerId);
            _customLayerDebugNames.Remove(layerId);
            _layerFrames.Remove(layerId);
        }

        // ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        //  Layer Submission
        // ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        ///     Submits a layer contribution for the current frame.
        /// </summary>
        public void SubmitLayer(
            IFacialBlendshapeSource source,
            int layerId,
            IReadOnlyList<BlendshapeTargetKey> targets,
            IReadOnlyList<float> weights,
            int count)
        {
            if (targets == null || weights == null || count <= 0) return;

            Dictionary<BlendshapeTargetKey, float> destination = GetOrCreateFrameLayer(layerId);
            _activeLayersThisFrame.Add(layerId);

            int appliedCount = Mathf.Min(count, Mathf.Min(targets.Count, weights.Count));
            for (int i = 0; i < appliedCount; i++)
            {
                BlendshapeTargetKey key = targets[i];
                if (!key.IsValid) continue;

                float weight = Mathf.Clamp(weights[i], 0f, 100f);
                if (weight <= BlendshapeWriteEpsilon) continue;

                if (destination.TryGetValue(key, out float current) && current >= weight)
                    continue;

                destination[key] = weight;
            }
        }

        /// <summary>
        ///     Reports whether LipSync is actively speaking this frame.
        /// </summary>
        public void SetLipSyncSpeechActive(IFacialBlendshapeSource source, bool isActive)
        {
            _lipSyncSpeechReportedThisFrame = true;
            if (isActive) _lipSyncSpeechActiveThisFrame = true;
        }

        /// <summary>
        ///     Registers blendshape targets owned exclusively by the head-look system.
        /// </summary>
        public void RegisterHeadLookOwnedTargets(
            IFacialBlendshapeSource source,
            IReadOnlyList<BlendshapeTargetKey> targets,
            int count)
        {
            _headOwnedTargets.Clear();
            if (targets == null || count <= 0) return;

            int appliedCount = Mathf.Min(count, targets.Count);
            for (int i = 0; i < appliedCount; i++)
            {
                BlendshapeTargetKey key = targets[i];
                if (!key.IsValid) continue;
                _headOwnedTargets.Add(key);
            }
        }

        public void ClearHeadLookOwnedTargets(IFacialBlendshapeSource source)
        {
            _headOwnedTargets.Clear();
        }

        /// <summary>
        ///     Clears any previously submitted contribution for <paramref name="layerId" />, so a
        ///     source that is unbinding/tearing down mid-frame (after <see cref="SubmitLayer" /> but
        ///     before this frame's <see cref="LateUpdate" />) leaves no residual influence. Safe to
        ///     call when the layer has no active frame (no-op).
        /// </summary>
        public void ClearLayer(IFacialBlendshapeSource source, int layerId)
        {
            if (_layerFrames.TryGetValue(layerId, out Dictionary<BlendshapeTargetKey, float> frame))
                frame.Clear();
        }

        // ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        //  Composition Pipeline
        // ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

        private static GameObject ResolvePreferredOwner(Component context)
        {
            ConvaiCharacter characterRoot = context.GetComponentInParent<ConvaiCharacter>(true);
            return characterRoot != null ? characterRoot.gameObject : context.gameObject;
        }

        private void LateUpdate()
        {
            using ProfilerMarker.AutoScope _ = LateUpdateMarker.Auto();

            float targetSpeechFactor = _lipSyncSpeechReportedThisFrame && _lipSyncSpeechActiveThisFrame ? 1f : 0f;
            float deltaTime = UnityEngine.Application.isPlaying ? Time.deltaTime : (1f / 60f);
            if (deltaTime <= 0f) deltaTime = 1f / 60f;

            if (_compositionProfile == null)
                EnsureDefaultProfileLoaded();

            RefreshProfileState();
            UpdateSpeechBlendFactor(targetSpeechFactor, deltaTime);

            bool hasAnyActivity = _activeLayersThisFrame.Count > 0 || _lastApplied.Count > 0;
            if (!hasAnyActivity)
            {
                ClearFrameState();
                return;
            }

            BuildComposeKeySet();

            if (_compositionProfile != null)
                ComposeAndWriteRegion();
            else
                ComposePassThrough();

            ClearFrameState();
        }

        private void UpdateSpeechBlendFactor(float targetSpeechFactor, float deltaTime)
        {
            if (_compositionProfile == null)
            {
                _speechBlendFactor = targetSpeechFactor;
                _emotionReturnRamp.Reset(targetSpeechFactor);
                return;
            }

            _speechBlendFactor = SpeechBlendRamps.StepLinear(
                _speechBlendFactor, targetSpeechFactor, deltaTime,
                targetSpeechFactor > _speechBlendFactor
                    ? _compositionProfile.SpeechRampUpDuration
                    : _compositionProfile.SpeechRampDownDuration);

            _emotionReturnRamp.Step(
                targetSpeechFactor, deltaTime,
                _compositionProfile.SpeechRampUpDuration,
                _compositionProfile.EmotionReturnDuration);
        }

        private void RefreshProfileState()
        {
            if (_compositionProfile == null)
            {
                if (_lastProfileConfigHash != 0)
                {
                    _lastProfileConfigHash = 0;
                    InvalidateRegionCache();
                }
                return;
            }

            int configHash = _compositionProfile.ComputeConfigurationHash();
            if (configHash != _lastProfileConfigHash)
            {
                _lastProfileConfigHash = configHash;
                InvalidateRegionCache();
            }
        }

        private FacialBlendshapeRegion ClassifyRegion(BlendshapeTargetKey key)
        {
            if (_regionCache.TryGetValue(key, out FacialBlendshapeRegion cached))
                return cached;

            string blendshapeName = key.Mesh.sharedMesh.GetBlendShapeName(key.BlendshapeIndex);
            FacialBlendshapeRegion region = _compositionProfile.ClassifyBlendshape(blendshapeName);
            _regionCache[key] = region;
            return region;
        }

        private void InvalidateRegionCache()
        {
            _regionCache.Clear();
        }

        private void ComposeAndWriteRegion()
        {
            float speechFactor = _speechBlendFactor;
            float emotionFactor = _emotionReturnRamp.Value;
            bool globalNorm = _compositionProfile.EnableGlobalNormalization;

            for (int i = 0; i < _composeKeys.Count; i++)
            {
                BlendshapeTargetKey key = _composeKeys[i];
                if (!key.IsValid) continue;

                if (_headOwnedTargets.Contains(key))
                {
                    float headLookVal = GetLayerValue(FacialBlendshapeLayers.HeadLook, key);
                    WriteIfChanged(key, headLookVal);
                    continue;
                }

                FacialBlendshapeRegion region = ClassifyRegion(key);
                RegionBlendConfig config = _compositionProfile.GetRegionConfig(region);

                config.GetInterpolatedWeights(speechFactor, emotionFactor,
                    out float emotionW, out float lipSyncW, out float customW);

                float emotionGeneral = GetLayerValue(FacialBlendshapeLayers.EmotionGeneral, key);
                float emotionMouth = GetLayerValue(FacialBlendshapeLayers.EmotionMouth, key);
                float lipSync = GetLayerValue(FacialBlendshapeLayers.LipSync, key);
                float eyes = GetLayerValue(FacialBlendshapeLayers.Eyes, key);
                float emotionMicro = GetLayerValue(FacialBlendshapeLayers.EmotionMicro, key);
                float customVal = GetMaxCustomLayerValue(key);

                // EmotionMicro rides ADDITIVELY on top of the base emotion (not max-blended) so
                // idle drift/speech accents can only ever add life, never suppress the primary
                // expression. Downstream Compose()/global-normalization/Clamp(0,100) bound the
                // result. When nobody submits to EmotionMicro, GetLayerValue returns 0 and this
                // matches what compositing with no micro layer looks like.
                float emotionVal = Mathf.Max(emotionGeneral, emotionMouth) + emotionMicro;

                float composed = config.Compose(
                    emotionVal, emotionW,
                    lipSync, lipSyncW,
                    customVal, customW);

                float finalWeight = Mathf.Max(eyes, composed);

                if (globalNorm && finalWeight > 100f)
                    finalWeight = 100f;

                finalWeight = Mathf.Clamp(finalWeight, 0f, 100f);
                WriteIfChanged(key, finalWeight);
            }
        }

        private float GetLayerValue(int layerId, BlendshapeTargetKey key)
        {
            if (!_layerFrames.TryGetValue(layerId, out Dictionary<BlendshapeTargetKey, float> frame))
                return 0f;
            frame.TryGetValue(key, out float value);
            return value;
        }

        private float GetMaxCustomLayerValue(BlendshapeTargetKey key)
        {
            float max = 0f;
            foreach (int customId in _registeredCustomLayers)
            {
                if (!_layerFrames.TryGetValue(customId, out Dictionary<BlendshapeTargetKey, float> frame))
                    continue;
                if (frame.TryGetValue(key, out float val) && val > max)
                    max = val;
            }
            return max;
        }

        /// <summary>
        ///     Fallback composition path used when no <see cref="ConvaiFacialCompositionProfile" />
        ///     is available (e.g. the default asset was stripped from the build). Combines all
        ///     active layer contributions per target using a max-blend so basic features like
        ///     LipSync and Emotion still produce visible output instead of being zeroed out.
        /// </summary>
        private void ComposePassThrough()
        {
            if (!_loggedComposePassThroughFallback)
            {
                _loggedComposePassThroughFallback = true;
                ConvaiLogger.Warning(
                    "[FacialBlendshapeCompositorHost] No facial composition profile is assigned, so " +
                    "the face is composed with a max-blend fallback that costs more per target and " +
                    "reads worse while speaking. This only happens when a profile was explicitly set " +
                    "to null: assign a ConvaiFacialCompositionProfile on the Convai Character, or " +
                    "clear the override to get the built-in default back.",
                    LogCategory.Character);
            }

            for (int i = 0; i < _composeKeys.Count; i++)
            {
                BlendshapeTargetKey key = _composeKeys[i];
                if (!key.IsValid) continue;

                if (_headOwnedTargets.Contains(key))
                {
                    float headLookVal = GetLayerValue(FacialBlendshapeLayers.HeadLook, key);
                    WriteIfChanged(key, headLookVal);
                    continue;
                }

                float maxWeight = 0f;
                foreach (KeyValuePair<int, Dictionary<BlendshapeTargetKey, float>> kvp in _layerFrames)
                {
                    if (kvp.Value.Count == 0) continue;
                    if (kvp.Value.TryGetValue(key, out float val) && val > maxWeight)
                        maxWeight = val;
                }

                WriteIfChanged(key, Mathf.Clamp(maxWeight, 0f, 100f));
            }
        }

        private void WriteIfChanged(BlendshapeTargetKey key, float finalWeight)
        {
            finalWeight = Mathf.Clamp(finalWeight, 0f, 100f);
            _lastApplied.TryGetValue(key, out float previousWeight);

            if (Mathf.Abs(finalWeight - previousWeight) < BlendshapeWriteEpsilon)
                return;

            key.Mesh.SetBlendShapeWeight(key.BlendshapeIndex, finalWeight);

            if (finalWeight <= BlendshapeWriteEpsilon)
                _lastApplied.Remove(key);
            else
                _lastApplied[key] = finalWeight;
        }

        // ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        //  Frame Layer Management
        // ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

        private Dictionary<BlendshapeTargetKey, float> GetOrCreateFrameLayer(int layerId)
        {
            if (_layerFrames.TryGetValue(layerId, out Dictionary<BlendshapeTargetKey, float> existing))
                return existing;

            var frame = new Dictionary<BlendshapeTargetKey, float>();
            _layerFrames[layerId] = frame;
            return frame;
        }

        private void BuildComposeKeySet()
        {
            _composeKeys.Clear();
            _composeKeySet.Clear();

            foreach (KeyValuePair<int, Dictionary<BlendshapeTargetKey, float>> kvp in _layerFrames)
            {
                if (kvp.Value.Count == 0) continue;
                AddComposeKeys(kvp.Value);
            }

            AddComposeKeys(_headOwnedTargets);
            AddComposeKeys(_lastApplied);
        }

        private void AddComposeKeys(Dictionary<BlendshapeTargetKey, float> source)
        {
            foreach (BlendshapeTargetKey key in source.Keys)
            {
                if (!_composeKeySet.Add(key)) continue;
                _composeKeys.Add(key);
            }
        }

        private void AddComposeKeys(IEnumerable<BlendshapeTargetKey> keys)
        {
            foreach (BlendshapeTargetKey key in keys)
            {
                if (!_composeKeySet.Add(key)) continue;
                _composeKeys.Add(key);
            }
        }

        private void ClearFrameState()
        {
            foreach (KeyValuePair<int, Dictionary<BlendshapeTargetKey, float>> kvp in _layerFrames)
                kvp.Value.Clear();

            _activeLayersThisFrame.Clear();
            _lipSyncSpeechReportedThisFrame = false;
            _lipSyncSpeechActiveThisFrame = false;
        }
    }

    /// <summary>
    ///     Pure per-frame ramp math shared by <see cref="FacialBlendshapeCompositorHost" />'s two
    ///     speech-driven blend factors. Kept free of <see cref="MonoBehaviour" /> state so it can be
    ///     driven directly by tests with explicit delta times.
    /// </summary>
    internal static class SpeechBlendRamps
    {
        /// <summary>
        ///     Advances <paramref name="current" /> linearly toward <paramref name="target" /> over
        ///     <paramref name="durationSeconds" />. Matches the lip-sync ramp's original behavior
        ///     exactly (immediate onset feel, constant-rate return).
        /// </summary>
        public static float StepLinear(float current, float target, float deltaTime, float durationSeconds)
        {
            float step = deltaTime / Mathf.Max(0.01f, durationSeconds);
            return Mathf.MoveTowards(current, target, step);
        }

        /// <summary>
        ///     Eases a normalized progress value (0..1) so the mapped output has zero velocity at
        ///     both ends — a gentle settle rather than a linear (constant-velocity) motion.
        /// </summary>
        public static float SmoothStep01(float progress)
        {
            float p = Mathf.Clamp01(progress);
            return p * p * (3f - 2f * p);
        }
    }

    /// <summary>
    ///     Tracks the Emotion/Custom layer blend factor. Rises linearly (same feel as the lip-sync
    ///     ramp-up) but falls back to idle on its own, slower, eased timeline, so a finished sentence
    ///     does not pop the emotion expression back on the lip-sync's fast timeline. Re-entering
    ///     speech mid-return resumes the up-ramp from the current value with no snap.
    /// </summary>
    internal struct EmotionReturnRamp
    {
        private float _value;
        private float _returnStartValue;
        private float _returnProgress;
        private bool _isReturning;

        /// <summary>Current factor value. 0 = idle, 1 = fully speaking.</summary>
        public float Value => _value;

        /// <summary>
        ///     Advances the ramp by one frame and returns the new value.
        /// </summary>
        /// <param name="target">1 while speech is active this frame, otherwise 0.</param>
        /// <param name="deltaTime">Frame delta time in seconds.</param>
        /// <param name="rampUpDuration">Seconds to rise from idle to speaking (matches lip-sync onset).</param>
        /// <param name="returnDuration">Seconds to eased-return from speaking to idle.</param>
        public float Step(float target, float deltaTime, float rampUpDuration, float returnDuration)
        {
            if (target > _value)
            {
                // Rising (or resuming mid-return): plain linear MoveTowards from wherever the
                // value currently sits, so a re-entry to speech never snaps.
                _isReturning = false;
                _value = SpeechBlendRamps.StepLinear(_value, target, deltaTime, rampUpDuration);
            }
            else if (target < _value)
            {
                if (!_isReturning)
                {
                    _isReturning = true;
                    _returnStartValue = _value;
                    _returnProgress = 0f;
                }

                _returnProgress = Mathf.Clamp01(_returnProgress + deltaTime / Mathf.Max(0.01f, returnDuration));
                float eased = SpeechBlendRamps.SmoothStep01(_returnProgress);
                _value = Mathf.Lerp(_returnStartValue, target, eased);
            }
            else
            {
                _isReturning = false;
            }

            return _value;
        }

        /// <summary>Snaps the ramp to <paramref name="value" /> with no easing state carried over.</summary>
        public void Reset(float value)
        {
            _value = value;
            _returnStartValue = value;
            _returnProgress = 0f;
            _isReturning = false;
        }
    }
}
