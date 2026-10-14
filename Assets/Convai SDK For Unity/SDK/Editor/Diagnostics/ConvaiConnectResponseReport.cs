using System.Globalization;
using Convai.Application;
using Convai.Infrastructure.Networking;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Convai.Editor.Diagnostics
{
    /// <summary>
    ///     Turns the last room connect response into the JSON a backend engineer asks for: the
    ///     response exactly as the server sent it, and the handful of client facts they would
    ///     otherwise have to ask about.
    /// </summary>
    /// <remarks>
    ///     The room token is the only field changed. It is a live credential for the room until it
    ///     expires, and this text is written to be pasted into chat and tickets; its length is kept
    ///     so a missing or truncated token is still visible.
    /// </remarks>
    internal static class ConvaiConnectResponseReport
    {
        internal const string TokenField = "token";

        /// <returns>Indented JSON, or null when <paramref name="snapshot" /> is null.</returns>
        internal static string Build(
            ConvaiConnectResponseSnapshot snapshot,
            string characterId = null, string characterName = null, string characterReference = null)
        {
            if (snapshot?.Details == null)
                return null;

            JObject response = JObject.FromObject(snapshot.Details);
            if (response[TokenField] is JValue { Value: string token } && token.Length > 0)
                response[TokenField] = $"<redacted, {token.Length} chars>";

            var client = new JObject
            {
                ["sdk_version"] = ConvaiSDK.Version.ToString(),
                ["unity_version"] = UnityEngine.Application.unityVersion,
                ["platform"] = UnityEngine.Application.platform.ToString(),
                ["build_target"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
                ["received_at_utc"] = snapshot.ReceivedAtUtc.ToString("o", CultureInfo.InvariantCulture)
            };

            // Copied from a character's inspector, the report says which character asked; from the
            // Room Manager there is no one character to name, and the response lists them all.
            if (!string.IsNullOrEmpty(characterId))
                client["character"] = new JObject
                {
                    ["id"] = characterId,
                    ["name"] = characterName ?? string.Empty,
                    ["reference"] = characterReference ?? string.Empty
                };

            var report = new JObject { ["client"] = client, ["response"] = response };

            return report.ToString(Formatting.Indented);
        }
    }
}
