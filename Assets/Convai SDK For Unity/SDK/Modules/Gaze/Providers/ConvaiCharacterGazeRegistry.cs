using System.Collections.Generic;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Semantics;
using Convai.Runtime.Components;
using Convai.Runtime.Embodiment;
using Convai.Shared.Compatibility;
using UnityEngine;

namespace Convai.Modules.Gaze.Providers
{
    /// <summary>
    ///     Scene-wide registry of characters that publish themselves as gaze targets for
    ///     other Convai characters (character-to-character mutual gaze). Entries register
    ///     from <see cref="CharacterGazeTargetProvider" /> on enable and unregister on
    ///     disable — no scene scans, no <c>Find*</c>, so it scales with participant count and
    ///     costs nothing when unused.
    /// </summary>
    internal static class ConvaiCharacterGazeRegistry
    {
        /// <summary>One participating character in the mutual-gaze registry.</summary>
        internal sealed class Entry
        {
            /// <summary>The character's embodiment context (dialogue-state source + identity).</summary>
            public EmbodimentContext Context;

            /// <summary>Character root — used as the candidate identity and diagnostics name.</summary>
            public Transform Root;

            /// <summary>
            ///     Eye-line gaze point (head bone). Falls back to <see cref="Root" /> until the
            ///     rig binds; consumers re-resolve lazily because the first rig bind does not
            ///     raise <c>RigBindingChanged</c>.
            /// </summary>
            public Transform HeadAnchor;

            /// <summary>Stable display name for diagnostics.</summary>
            public string DisplayName;

            /// <summary>
            ///     The character itself, when this entry belongs to a Convai character. Its
            ///     <see cref="ConvaiCharacter.IsSpeaking" /> is the SDK's own answer to "is this
            ///     character talking right now" — the same turn-level signal that gates lip sync
            ///     and speaking animation — so it is what other characters listen to as well.
            /// </summary>
            public ConvaiCharacter Character;

            /// <summary>Vertical lift applied while the gaze point falls back to the root.</summary>
            public float EyeLineOffset = 1.6f;

            /// <summary>Whether the gaze point is still the root fallback (no head bone yet).</summary>
            public bool HeadIsFallback => HeadAnchor == null || HeadAnchor == Root;

            /// <summary>
            ///     Stable identity of this participant, so a consumer can tell one speaker from
            ///     another across frames. Derived from the character root through the object-id
            ///     compatibility seam, with the display name as the fallback for an entry whose
            ///     root has gone.
            /// </summary>
            public int Key => Root != null
                ? ConvaiObjectId.Of(Root).GetHashCode()
                : DisplayName?.GetHashCode() ?? 0;

            /// <summary>
            ///     Re-resolves the head bone from <paramref name="rigBinding" /> (or the
            ///     context's current binding). Downgrades to the root when the binding has no
            ///     head, so a rebind to a headless rig never leaves a stale anchor behind.
            /// </summary>
            public void RefreshHeadAnchor(IStandardRigBinding rigBinding = null)
            {
                HeadAnchor = Root;

                IStandardRigBinding rig = rigBinding ?? Context?.RigBinding;
                if (rig != null && rig.TryGetBone(StandardBone.Head, out Transform head) && head != null)
                    HeadAnchor = head;
            }

            /// <summary>
            ///     The world-space point other characters gaze at: the head bone when resolved,
            ///     otherwise the root lifted to the eye line. Returns <c>false</c> when the
            ///     entry has no usable transform at all.
            /// </summary>
            public bool TryGetGazePoint(out Vector3 point)
            {
                if (HeadAnchor != null && HeadAnchor != Root)
                {
                    point = HeadAnchor.position;
                    return true;
                }

                if (Root != null)
                {
                    point = Root.position + Vector3.up * Mathf.Max(0f, EyeLineOffset);
                    return true;
                }

                point = default;
                return false;
            }
        }

        private static readonly List<Entry> Entries = new(8);

        /// <summary>Registered characters (observers poll this, skipping their own entry).</summary>
        internal static IReadOnlyList<Entry> All => Entries;

        internal static void Register(Entry entry)
        {
            if (entry == null || Entries.Contains(entry)) return;
            Entries.Add(entry);
        }

        internal static void Unregister(Entry entry)
        {
            if (entry != null) Entries.Remove(entry);
        }

        /// <summary>Test/reset seam — empties the registry.</summary>
        internal static void Clear() => Entries.Clear();

        /// <summary>
        ///     Whether this participant is talking right now.
        /// </summary>
        /// <remarks>
        ///     <see cref="ConvaiCharacter.IsSpeaking" /> is the authority: it is set from the
        ///     transport's own <c>CharacterSpeechStateChanged</c>, which is what the rest of the
        ///     SDK gates lip sync and speaking animation on, so a listener and a mouth cannot
        ///     disagree about who is talking. The dialogue state is the fallback for a character
        ///     that has no <see cref="ConvaiCharacter" /> — a test fixture, or a participant
        ///     driven by something other than the service — and it is deliberately second,
        ///     because it also reports <c>Thinking</c> during the moment before speech begins.
        /// </remarks>
        internal static bool IsSpeaking(Entry entry)
        {
            if (entry == null) return false;
            if (entry.Character != null) return entry.Character.IsSpeaking;

            IConversationFlowSource flow = entry.Context?.ConversationFlowSource;
            return flow != null && flow.Current.Primary == DialogueState.Speaking;
        }

        /// <summary>
        ///     This participant's place in the registry, or -1 when it is not registered. Used
        ///     only to spread listeners' timings apart: two characters that take up a speaker in
        ///     the same frame must not then glance in the same frame, and independent random
        ///     streams do not guarantee that — distinct ordinals do.
        /// </summary>
        internal static int OrdinalOf(Entry entry) => entry == null ? -1 : Entries.IndexOf(entry);

        /// <summary>
        ///     Re-resolves a participant by the identity a consumer latched earlier, so a listener
        ///     can keep aiming at the person who still holds the floor while they are between
        ///     sentences. Returns <c>false</c> once that character has left the registry.
        /// </summary>
        /// <summary>
        ///     Finds the entry published for a <see cref="ConvaiCharacter" /> and its current
        ///     gaze point. False when that character publishes no gaze target.
        /// </summary>
        internal static bool TryGetByCharacter(ConvaiCharacter character, out Entry entry, out Vector3 point)
        {
            entry = null;
            point = default;
            if (character == null) return false;

            for (int i = 0; i < Entries.Count; i++)
            {
                Entry candidate = Entries[i];
                if (candidate == null || candidate.Character != character) continue;

                if (candidate.HeadIsFallback) candidate.RefreshHeadAnchor();
                if (!candidate.TryGetGazePoint(out point)) return false;

                entry = candidate;
                return true;
            }

            return false;
        }

        internal static bool TryGetByKey(int key, out Entry entry, out Vector3 point)
        {
            entry = null;
            point = default;
            if (key == 0) return false;

            for (int i = 0; i < Entries.Count; i++)
            {
                Entry candidate = Entries[i];
                if (candidate == null || candidate.Key != key) continue;

                if (candidate.HeadIsFallback) candidate.RefreshHeadAnchor();
                if (!candidate.TryGetGazePoint(out point)) return false;

                entry = candidate;
                return true;
            }

            return false;
        }

        /// <summary>
        ///     Finds the registered character that is currently speaking, nearest first, skipping
        ///     the observer's own entry. This is the room's answer to "who has the floor" for
        ///     everything that follows a conversation it is not itself part of.
        /// </summary>
        /// <remarks>
        ///     Nearest rather than first-registered so that in a room where two characters talk at
        ///     once a listener attends the one it is actually standing with, and so the answer does
        ///     not depend on component enable order. Allocation-free: a straight walk of the
        ///     registry, with the same lazy head-anchor upgrade the candidate path uses.
        /// </remarks>
        internal static bool TryGetSpeaking(
            EmbodimentContext observerContext,
            Transform observerRoot,
            Vector3 observerPosition,
            out Entry speaking,
            out Vector3 point)
        {
            speaking = null;
            point = default;
            float nearest = float.PositiveInfinity;

            for (int i = 0; i < Entries.Count; i++)
            {
                Entry entry = Entries[i];
                if (entry?.Context == null) continue;
                if (observerContext != null && entry.Context == observerContext) continue;
                if (observerRoot != null && entry.Root == observerRoot) continue;

                if (!IsSpeaking(entry)) continue;

                if (entry.HeadIsFallback) entry.RefreshHeadAnchor();
                if (!entry.TryGetGazePoint(out Vector3 candidatePoint)) continue;

                float distance = (candidatePoint - observerPosition).sqrMagnitude;
                if (distance >= nearest) continue;

                nearest = distance;
                speaking = entry;
                point = candidatePoint;
            }

            return speaking != null;
        }
    }
}
