using System.Collections.Generic;

namespace Convai.Runtime.Room
{
    /// <summary>
    ///     Decides whether an ownership refresh found anything the room manager needs to be told
    ///     about.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The subtle half is the connectable roster. Ownership answers <i>who belongs to this
    ///         manager</i>, and enabling or disabling an owned character does not change that answer
    ///         at all — yet it changes who can be in the room. Comparing ownership alone therefore
    ///         reports "nothing changed" for exactly the case the Multi-Character sample promises
    ///         works, and the character never joins.
    ///     </para>
    ///     <para>
    ///         Kept as a plain decision over reference lists so the call site's rule is pinned by a
    ///         test rather than only its helpers, the same way <see cref="LiveRosterPlanner" /> and
    ///         <see cref="LateCharacterPlanner" /> are. The lists are compared by reference and in
    ///         order; the caller passes what it already holds, so nothing is allocated here.
    ///     </para>
    /// </remarks>
    internal static class OwnershipChangeDetector
    {
        internal static bool HasChanged(
            bool playerUnchanged,
            bool conversationTargetUnchanged,
            bool selectionModeUnchanged,
            IReadOnlyList<object> previousOwned,
            IReadOnlyList<object> currentOwned,
            IReadOnlyList<object> previousIncluded,
            IReadOnlyList<object> currentIncluded,
            IReadOnlyList<object> previousConnectable,
            IReadOnlyList<object> currentConnectable) =>
            !playerUnchanged ||
            !conversationTargetUnchanged ||
            !selectionModeUnchanged ||
            !HaveSameReferences(previousOwned, currentOwned) ||
            !HaveSameReferences(previousIncluded, currentIncluded) ||
            !HaveSameReferences(previousConnectable, currentConnectable);

        internal static bool HaveSameReferences(
            IReadOnlyList<object> previous,
            IReadOnlyList<object> current)
        {
            int previousCount = previous?.Count ?? 0;
            int currentCount = current?.Count ?? 0;
            if (previousCount != currentCount)
                return false;

            for (int i = 0; i < previousCount; i++)
            {
                if (!ReferenceEquals(previous[i], current[i]))
                    return false;
            }

            return true;
        }
    }
}
