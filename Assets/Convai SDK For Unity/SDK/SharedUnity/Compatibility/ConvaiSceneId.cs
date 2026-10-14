using UnityEngine.SceneManagement;

namespace Convai.Shared.Compatibility
{
    /// <summary>
    ///     Version-stable scene identity. Every place in the package that needs a comparable handle
    ///     for a <see cref="Scene" /> — de-duplication sets, diagnostic keys — goes through here
    ///     instead of reading <c>Scene.handle</c> directly.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Unity is migrating scene identity from a 32-bit handle to a 64-bit <c>EntityId</c>,
    ///         and the migration lands in three bands (measured by reflecting over
    ///         <c>Scene.handle</c> on each supported editor):
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <b>6000.0 – 6000.2</b> — <c>Scene.handle</c> is an <c>int</c>. There is nothing
    ///             else to read.
    ///         </item>
    ///         <item>
    ///             <b>6000.3</b> — it becomes a <c>SceneHandle</c> struct wrapping an
    ///             <c>EntityId</c>, with implicit conversions to <c>int</c> and <c>uint</c> that are
    ///             not yet deprecated. <c>GetRawData()</c> does not exist yet, so the <c>int</c>
    ///             conversion is still the only way to read the value.
    ///         </item>
    ///         <item>
    ///             <b>6000.4</b> — <c>GetRawData()</c> arrives, returning the identity as a
    ///             <c>ulong</c>, and every implicit conversion is deprecated in favour of it. On
    ///             <b>6000.5</b> those conversions become obsolete-<i>as-error</i>, and the values
    ///             stop fitting: a scene measured on 6000.5.6f1 had a raw handle of
    ///             <c>568105589213756026</c>, which an <c>int</c> cannot hold.
    ///         </item>
    ///     </list>
    ///     <para>
    ///         So this seam reads <c>GetRawData()</c> from 6000.4 on — the first editor that offers
    ///         it, not the first that requires it, because taking it late would mean compiling
    ///         against a deprecated conversion on 6000.4 and narrowing a 64-bit identity to 32 bits
    ///         on the editor that first widened it. Below that it returns the editor's own
    ///         <c>int</c>, so ids on 6000.0 – 6000.3 are exactly the ones the package produced
    ///         before this seam existed. On 6000.4 the two agree in practice — the raw value
    ///         measured there was a sign-extended <c>-1402</c> — so that band is unchanged too.
    ///     </para>
    ///     <para>
    ///         <b><c>SceneHandle.GetHashCode()</c> is not an identity and must not be used as one.</b>
    ///         It is a lossy digest that already disagrees with the handle it comes from: the same
    ///         6000.3 scene reported handle <c>-1412</c> and hash <c>64711643</c>, and on 6000.5 raw
    ///         handles <c>568105589213756026</c> and <c>568105589213755988</c> hash to <c>-1414</c>
    ///         and <c>-1452</c>. Ids from this seam are compared for equality and held as set
    ///         *members*, never used as buckets, so two scenes sharing a digest would silently read
    ///         as one — and in the persistent-object sweep in <c>ConvaiRuntimeHost</c> that hides a
    ///         <c>ConvaiPlayer</c> or <c>ConvaiCharacter</c> from ownership resolution, which makes
    ///         the room refuse to start over an agent the scene plainly contains.
    ///     </para>
    ///     <para>
    ///         <b>An id is valid only inside the session that produced it</b>, exactly like
    ///         <see cref="ConvaiObjectId" />. Scene handles never survived a domain reload either.
    ///         Ids must not be serialized, written to disk, or compared across editor versions.
    ///     </para>
    /// </remarks>
    internal static class ConvaiSceneId
    {
        /// <summary>
        ///     Returns a session-scoped id for <paramref name="scene" />, comparable against other
        ///     ids from this seam. An invalid scene yields <c>0</c>.
        /// </summary>
        internal static long Of(Scene scene)
        {
            if (!scene.IsValid()) return 0L;

#if UNITY_6000_4_OR_NEWER
            return unchecked((long)scene.handle.GetRawData());
#else
            // Explicit, because 6000.3 offers implicit conversions to both int and uint and a
            // widening to long is ambiguous between them.
            return (int)scene.handle;
#endif
        }
    }
}
