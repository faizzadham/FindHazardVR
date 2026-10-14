using Convai.Modules.BodyAnimation.Data;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyAnimation
{
    public sealed class BodyAnimationSerializationMigrationTests
    {
        /// <summary>
        ///     <see cref="ClipMotionMetadata" /> schema v2→v3. A v2 (or unanalyzed) asset's
        ///     serialized YAML has no <c>_authoredMotionScale</c> key at all, so it deserializes
        ///     to the field's default (0, "unknown") — the runtime then treats it exactly as
        ///     before the authored-scale field existed (assumes the reference rig, scale
        ///     resolves to 1). Simulated with
        ///     <see cref="JsonUtility" /> since <see cref="ClipMotionMetadata" /> is a plain
        ///     <c>[Serializable]</c> class, not a ScriptableObject asset.
        /// </summary>
        [Test]
        public void ClipMotionMetadata_PreV3Data_DeserializesWithUnknownAuthoredMotionScale()
        {
            const string legacyV2Json =
                "{\"_schemaVersion\":2,\"_authoredSpeed\":1.2,\"_authoredDistance\":3.0,\"_analyzed\":true}";

            var metadata = JsonUtility.FromJson<ClipMotionMetadata>(legacyV2Json);

            Assert.NotNull(metadata);
            Assert.That(metadata.AuthoredMotionScale, Is.EqualTo(0f),
                "A earlier asset carries no _authoredMotionScale key and must read as unknown, not 0-is-invalid.");
            Assert.That(metadata.HasAuthoredMotionScale, Is.False);
            Assert.That(metadata.AuthoredSpeed, Is.EqualTo(1.2f).Within(1e-5f),
                "Unrelated pre-existing fields must survive the schema bump untouched.");
            Assert.That(metadata.IsAnalyzed, Is.True);
        }
    }
}
