// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

namespace Convai.Sample.Camera
{
    /// <summary>
    ///     Factory for creating pipeline-specific <see cref="IOrbitVolumeAdapter" /> instances.
    ///     <para>
    ///         Auto-detects active render pipeline at compile time using conditional compilation
    ///         (#if URP_10_0_OR_NEWER, HDRP_10_0_OR_NEWER) and instantiates correct adapter.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     Caches adapter instance to avoid repeated allocations. Thread-safe for static access.
    /// </remarks>
    public static class OrbitVolumeAdapterFactory
    {
        private static IOrbitVolumeAdapter _cachedAdapter;
        private static readonly object _lock = new object();

        /// <summary>
        ///     Creates or returns cached Volume adapter for active render pipeline.
        ///     <para>
        ///         Priority order:
        ///         <list type="number">
        ///             <item><description>URP (if URP 10.0+ package installed)</description></item>
        ///             <item><description>HDRP (if HDRP 10.0+ package installed)</description></item>
        ///             <item><description>Built-in (fallback, limited support)</description></item>
        ///         </list>
        ///     </para>
        /// </summary>
        /// <returns>Non-null adapter instance. Never fails.</returns>
        /// <example>
        ///     <code>
        ///     IOrbitVolumeAdapter adapter = OrbitVolumeAdapterFactory.Create();
        ///     if (adapter.IsSupported)
        ///         adapter.TryApplyDepthOfField(camera, 2.8f, 1.5f);
        ///     </code>
        /// </example>
        public static IOrbitVolumeAdapter Create()
        {
            if (_cachedAdapter != null)
                return _cachedAdapter;

            lock (_lock)
            {
                if (_cachedAdapter != null)
                    return _cachedAdapter;

                // Conditional compilation determines adapter at build time
#if URP_10_0_OR_NEWER
                _cachedAdapter = new OrbitUrpVolumeAdapter();
#elif HDRP_10_0_OR_NEWER
                _cachedAdapter = new OrbitHdrpVolumeAdapter();
#else
                _cachedAdapter = new OrbitBuiltinVolumeAdapter();
#endif

                return _cachedAdapter;
            }
        }

        /// <summary>
        ///     Clears cached adapter. Use when switching pipelines at runtime (rare).
        /// </summary>
        internal static void ClearCache()
        {
            lock (_lock)
            {
                _cachedAdapter = null;
            }
        }
    }
}
