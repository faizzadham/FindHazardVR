using System;

namespace Convai.Runtime
{
    /// <summary>Validates API-provided references without constructing release suffixes or changing character identity.</summary>
    public static class ConvaiCharacterVersionReference
    {
        /// <summary>
        ///     Returns the exact selected API reference. Live works with a bare UUID before discovery.
        ///     The version label is display metadata only; a release reference is never inferred from it.
        /// </summary>
        /// <exception cref="ArgumentException">The character ID or version selection is invalid.</exception>
        public static string Create(string characterId, ConvaiCharacterVersionMode mode, string version,
            string selectedReference = null)
        {
            if (TryCreate(characterId, mode, version, out string reference, out string error, selectedReference))
                return reference;

            throw new ArgumentException(error);
        }

        /// <summary>Validates a selection and returns an error suitable for inspector and runtime feedback.</summary>
        public static bool TryCreate(string characterId, ConvaiCharacterVersionMode mode, string version,
            out string reference, out string error, string selectedReference = null)
        {
            reference = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(characterId))
            {
                error = "Character ID is required. Copy the UUID from the Convai dashboard.";
                return false;
            }

            if (characterId.Length != 36 || !Guid.TryParseExact(characterId, "D", out _))
            {
                error = "Character ID must be a bare UUID without spaces or a version suffix.";
                return false;
            }

            switch (mode)
            {
                case ConvaiCharacterVersionMode.Live:
                    // Ignore stale release metadata when switching to Live, or after editing the base ID.
                    // Preserve the API's exact current.reference (including casing) when it matches.
                    reference = IsSameBareIdentity(characterId, selectedReference) ? selectedReference : characterId;
                    return true;
                case ConvaiCharacterVersionMode.Specific:
                    if (string.IsNullOrEmpty(selectedReference))
                    {
                        error = "Select a released version from the Character Version dropdown to save its API reference. Refresh the list and reselect older saved versions.";
                        return false;
                    }
                    if (selectedReference.Length <= 37 || selectedReference[36] != '-' ||
                        !IsSameBareIdentity(characterId, selectedReference.Substring(0, 36)))
                    {
                        error = "The saved release reference does not belong to this Character ID. Refresh the list and select a released version.";
                        return false;
                    }
                    string selector = selectedReference.Substring(37);
                    if (string.Equals(selector, "draft", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(selector, "latest", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(selector, "live", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(selector, "default", StringComparison.OrdinalIgnoreCase))
                    {
                        error = "Select a runtime-safe released version from the dropdown; draft and alias references are not supported.";
                        return false;
                    }
                    foreach (char character in selectedReference)
                        if (char.IsWhiteSpace(character) || char.IsControl(character) ||
                            character == '/' || character == '\\' || character == '?' || character == '#')
                        {
                            error = "The saved release reference is invalid. Refresh the list and select a released version.";
                            return false;
                        }
                    reference = selectedReference;
                    return true;
                default:
                    error = "Character version mode must be Live or Specific.";
                    return false;
            }
        }

        private static bool IsSameBareIdentity(string characterId, string reference) =>
            reference != null && reference.Length == 36 && Guid.TryParseExact(reference, "D", out Guid identity) &&
            Guid.TryParseExact(characterId, "D", out Guid characterIdentity) && identity == characterIdentity;
    }
}
