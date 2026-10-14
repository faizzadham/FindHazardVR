using System;
using System.Collections.Generic;
using Convai.Runtime.Components;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Convai.Editor.Diagnostics
{
    /// <summary>
    ///     Finds characters that share a Character ID.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Duplicating a working character is the ordinary way to get a second one — select it in
    ///         the Hierarchy and press Ctrl+D — and the copy arrives holding the original's Character
    ///         ID. Two characters with one ID is not a cosmetic clash: the SDK's own registry keys
    ///         ownership, participants and audio sources by Character ID, so the second character
    ///         does not get its own routing, it collides with the first's.
    ///     </para>
    ///     <para>
    ///         One implementation, asked by both surfaces that report it — the Character inspector
    ///         and <c>Convai.DiagnoseConversation</c>. They used to be one surface and one blind
    ///         spot: the diagnosis knew, and the inspector the author was actually looking at said
    ///         nothing. A second copy of the rule would have let those two drift apart again.
    ///     </para>
    ///     <para>
    ///         Lives in <c>Convai.Editor</c> rather than beside the analyzer because the assembly
    ///         reference runs that way: the AI assembly can see this one, and not the reverse.
    ///     </para>
    /// </remarks>
    internal static class ConvaiCharacterIdConflicts
    {
        /// <summary>
        ///     Every Character ID held by more than one of <paramref name="characters" />, each with
        ///     the characters holding it.
        /// </summary>
        /// <remarks>
        ///     <paramref name="conflicts" /> is cleared first, so a caller cannot accumulate an
        ///     answer across two scans and report a conflict that has since been fixed.
        /// </remarks>
        internal static void Collect(
            IReadOnlyList<ConvaiCharacter> characters,
            List<ConvaiCharacterIdConflict> conflicts)
        {
            conflicts.Clear();
            if (characters == null || characters.Count < 2) return;

            var byId = new Dictionary<string, List<ConvaiCharacter>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < characters.Count; i++)
            {
                ConvaiCharacter character = characters[i];
                string id = NormalizeId(character);
                if (id == null) continue;

                if (!byId.TryGetValue(id, out List<ConvaiCharacter> sharing))
                    byId[id] = sharing = new List<ConvaiCharacter>(2);
                sharing.Add(character);
            }

            foreach (KeyValuePair<string, List<ConvaiCharacter>> entry in byId)
                if (entry.Value.Count > 1)
                    conflicts.Add(new ConvaiCharacterIdConflict(entry.Key, entry.Value));
        }

        /// <summary>
        ///     The other characters in the loaded scenes holding <paramref name="character" />'s
        ///     Character ID. Empty when the ID is unique, blank, or the character is gone.
        /// </summary>
        /// <remarks>
        ///     Inactive characters are included. A disabled duplicate is enabled sooner or later and
        ///     collides then; reporting it only once it is switched on would put the message as far
        ///     as possible from the edit that caused it.
        /// </remarks>
        internal static void FindOthersSharingId(
            ConvaiCharacter character,
            List<ConvaiCharacter> others)
        {
            others.Clear();

            string id = NormalizeId(character);
            if (id == null) return;

            for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                Scene scene = SceneManager.GetSceneAt(sceneIndex);
                if (!scene.IsValid() || !scene.isLoaded) continue;

                GameObject[] roots = scene.GetRootGameObjects();
                for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
                {
                    ConvaiCharacter[] candidates =
                        roots[rootIndex].GetComponentsInChildren<ConvaiCharacter>(true);
                    for (int i = 0; i < candidates.Length; i++)
                    {
                        ConvaiCharacter candidate = candidates[i];
                        if (candidate == null || ReferenceEquals(candidate, character)) continue;
                        if (string.Equals(NormalizeId(candidate), id, StringComparison.OrdinalIgnoreCase))
                            others.Add(candidate);
                    }
                }
            }
        }

        /// <summary>
        ///     The Character ID as the comparison sees it, or <c>null</c> when there is nothing to
        ///     compare.
        /// </summary>
        /// <remarks>
        ///     Trimmed, because a copy whose ID picked up a stray space is the same mistake wearing
        ///     a disguise — and the inspector's format check already tells the author about the
        ///     space itself, so nothing is lost by looking past it here.
        /// </remarks>
        private static string NormalizeId(ConvaiCharacter character)
        {
            if (character == null) return null;
            string id = character.CharacterId;
            return string.IsNullOrWhiteSpace(id) ? null : id.Trim();
        }
    }

    /// <summary>One Character ID and the characters that all claim it.</summary>
    internal readonly struct ConvaiCharacterIdConflict
    {
        internal ConvaiCharacterIdConflict(string characterId, IReadOnlyList<ConvaiCharacter> characters)
        {
            CharacterId = characterId;
            Characters = characters;
        }

        internal string CharacterId { get; }
        internal IReadOnlyList<ConvaiCharacter> Characters { get; }
    }
}
