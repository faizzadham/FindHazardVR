using System;
using System.Security.Cryptography;
using System.Text;
using Convai.Infrastructure.Networking.Models;

namespace Convai.Runtime.Adapters.Networking
{
    /// <summary>Creates non-identifying keys that isolate resumable sessions by user and topology.</summary>
    internal static class SessionResumeScope
    {
        internal static string CreateIdentityScope(string endUserId) =>
            Hash("identity|" + Normalize(endUserId));

        internal static string CreatePersistenceKey(
            string characterId,
            string endUserId,
            RoomJoinOptions joinOptions)
        {
            var material = new StringBuilder()
                .Append("v2|character=").Append(Normalize(characterId))
                .Append("|identity=").Append(CreateIdentityScope(endUserId))
                .Append("|mode=").Append(joinOptions?.IsJoinRequest == true ? "join" : "create")
                .Append("|room=").Append(Normalize(joinOptions?.RoomName))
                .Append("|room_session=").Append(Normalize(joinOptions?.ResolvedRoomSessionId))
                .Append("|shared=").Append(Normalize(joinOptions?.ResolvedSharedSessionKey));

            // Keep existing custom-agent keys unchanged; version-aware sessions never share them.
            if (!string.IsNullOrWhiteSpace(joinOptions?.ResolvedCharacterReference))
                material.Append("|reference=").Append(NormalizeCharacterReference(joinOptions.ResolvedCharacterReference));

            if (joinOptions?.ResolvedCharacterRoster != null)
            {
                material.Append("|roster=");
                foreach (var character in joinOptions.ResolvedCharacterRoster)
                    material.Append(Normalize(character?.CharacterId)).Append(',');
            }

            return "convai-session-v2:" + Hash(material.ToString());
        }

        internal static bool CharacterReferencesMatch(string first, string second) =>
            string.Equals(first, second, StringComparison.Ordinal) ||
            (TryGetBareCharacterIdentity(first, out Guid firstIdentity) &&
             TryGetBareCharacterIdentity(second, out Guid secondIdentity) && firstIdentity == secondIdentity);

        private static string NormalizeCharacterReference(string reference) =>
            TryGetBareCharacterIdentity(reference, out Guid identity) ? identity.ToString("D") : Normalize(reference);

        private static bool TryGetBareCharacterIdentity(string reference, out Guid identity)
        {
            identity = default;
            // Only Live UUIDs are case-insensitive. Release references remain exact API-owned identifiers.
            return reference != null && reference.Length == 36 && Guid.TryParseExact(reference, "D", out identity);
        }

        private static string Normalize(string value) =>
            string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

        private static string Hash(string value)
        {
            using SHA256 sha = SHA256.Create();
            byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            var output = new StringBuilder(digest.Length * 2);
            foreach (byte item in digest)
                output.Append(item.ToString("x2"));
            return output.ToString();
        }
    }
}
