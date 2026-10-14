using System;
using System.Collections.Generic;
using Convai.Runtime.Behaviors;
using UnityEngine;

namespace Convai.Runtime.Room
{
    /// <summary>
    ///     The rule that every character in a room must be a different Convai character.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Not a style preference. The SDK's registry keys ownership, participant bindings and
    ///         audio sources by Character ID, so two characters holding one ID do not each get their
    ///         own routing — the second collides with the first, and the room answers both through
    ///         whichever was registered first. A voice arriving from the wrong mouth is a far harder
    ///         thing to trace back than a refusal that names the two GameObjects.
    ///     </para>
    ///     <para>
    ///         Checked on both paths into a roster, like <see cref="MultiCharacterRoomLimits" />,
    ///         because they are two ways to make the same mistake: the connect path is handed the
    ///         whole roster at once, and the live path adds one character to a roster that is
    ///         already up.
    ///     </para>
    ///     <para>
    ///         The mistake this exists for is ordinary rather than exotic. Duplicating a working
    ///         character is how most scenes get their second one, and the copy arrives holding the
    ///         original's Character ID.
    ///     </para>
    /// </remarks>
    internal static class MultiCharacterRosterIdentity
    {
        /// <summary>
        ///     Finds the first pair in <paramref name="characters" /> that share a Character ID.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Compared case-insensitively and with surrounding whitespace ignored: an ID pasted
        ///         with a stray space is the same character, and letting it through here would turn
        ///         a refusal the author can act on into a routing collision they cannot see.
        ///     </para>
        ///     <para>
        ///         Characters with no ID at all are skipped. They are refused separately, by the
        ///         check that says every character in a multi-character room needs one — and two
        ///         blank IDs reported as "duplicates" would send the reader looking for a clash
        ///         rather than at the empty field.
        ///     </para>
        /// </remarks>
        /// <param name="existing">The character already holding the ID.</param>
        /// <param name="duplicate">The character that claims it a second time.</param>
        internal static bool TryFindDuplicate(
            IReadOnlyList<IConvaiCharacterAgent> characters,
            out IConvaiCharacterAgent existing,
            out IConvaiCharacterAgent duplicate)
        {
            existing = null;
            duplicate = null;
            if (characters == null || characters.Count < 2) return false;

            var seen = new Dictionary<string, IConvaiCharacterAgent>(
                characters.Count, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < characters.Count; i++)
            {
                IConvaiCharacterAgent character = characters[i];
                string id = Normalize(character);
                if (id == null) continue;

                if (seen.TryGetValue(id, out IConvaiCharacterAgent owner))
                {
                    existing = owner;
                    duplicate = character;
                    return true;
                }

                seen[id] = character;
            }

            return false;
        }

        /// <summary>
        ///     Whether <paramref name="candidate" /> would collide with anything already in
        ///     <paramref name="characters" />.
        /// </summary>
        /// <remarks>
        ///     The live path's question. It differs from <see cref="TryFindDuplicate" /> only in that
        ///     the roster it is checking against is known to be clean already, so the one character
        ///     arriving is the only one that can be at fault — and saying which character it clashes
        ///     with is the whole of what the caller has to report.
        /// </remarks>
        internal static bool ConflictsWithRoster(
            IReadOnlyList<IConvaiCharacterAgent> characters,
            IConvaiCharacterAgent candidate,
            out IConvaiCharacterAgent existing)
        {
            existing = null;

            string id = Normalize(candidate);
            if (id == null || characters == null) return false;

            for (int i = 0; i < characters.Count; i++)
            {
                IConvaiCharacterAgent character = characters[i];
                if (ReferenceEquals(character, candidate)) continue;
                if (!string.Equals(Normalize(character), id, StringComparison.OrdinalIgnoreCase)) continue;

                existing = character;
                return true;
            }

            return false;
        }

        /// <summary>Says which two characters clash, over which ID, and what to change.</summary>
        internal static string DescribeDuplicate(
            IConvaiCharacterAgent existing,
            IConvaiCharacterAgent duplicate) =>
            $"'{Describe(existing)}' and '{Describe(duplicate)}' both use Character ID " +
            $"{Normalize(duplicate) ?? Normalize(existing)}. Two characters in one room cannot share " +
            "an ID — the SDK routes ownership, participants and audio by it, so they would collide " +
            "rather than each being answered separately. Give each character its own Character ID " +
            "from the Convai dashboard.";

        /// <summary>
        ///     The name to put in front of a reader who has to go and find this character.
        /// </summary>
        /// <remarks>
        ///     The display name first, because that is what the roster, the transcript and the
        ///     inspector all call it. A character that has not been given one falls back to its
        ///     GameObject name, which is what the Hierarchy calls it — and giving the reader
        ///     something they can search for is the entire point of naming it here.
        /// </remarks>
        private static string Describe(IConvaiCharacterAgent character)
        {
            if (character == null) return "unknown character";
            if (!string.IsNullOrWhiteSpace(character.CharacterName)) return character.CharacterName;
            return character is Component component ? component.gameObject.name : "unnamed character";
        }

        /// <summary>The Character ID as the comparison sees it, or <c>null</c> when there is none.</summary>
        private static string Normalize(IConvaiCharacterAgent character)
        {
            if (character == null) return null;
            string id = character.CharacterId;
            return string.IsNullOrWhiteSpace(id) ? null : id.Trim();
        }
    }
}
