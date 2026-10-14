using System.Collections.Generic;
using Convai.Domain.Models.LipSync;
using Convai.Modules.LipSync;
using Convai.Modules.LipSync.Profiles;
using NUnit.Framework;
using UnityEditor;

namespace Convai.Tests.EditMode.Application
{
    /// <summary>
    ///     Pins the observable behaviour of the SDK's built-in lipsync content exactly as it shipped
    ///     before the content-ownership refactor that moved it out of <c>Resources/</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         That refactor moves the default maps into the package, replaces the built-in profile
    ///         assets with code, and teaches the registry to hold more than one map per transport.
    ///         Each of those steps could silently change what a customer's character actually does.
    ///         This fixture is the baseline they are checked against: it asserts through the catalog
    ///         and resolver APIs, and locates assets by name rather than by path, so it keeps
    ///         asserting the same behaviour after the assets move.
    ///     </para>
    ///     <para>
    ///         Deliberately not a guard on folder layout — <c>LipSyncResourceIntegrityTests</c> owns
    ///         the shipped-path assertions and is expected to change when the content moves.
    ///     </para>
    /// </remarks>
    [TestFixture]
    public sealed class LipSyncBuiltInContentBaselineTests
    {
        private const string ArkitToCc4MapName = "ConvaiLipSyncDefaultMap_ARKitToCC4Extended";

        [SetUp]
        public void SetUp()
        {
            LipSyncProfileCatalog.ClearCachesForTests();
            LipSyncDefaultMappingResolver.ClearCachesForTests();
        }

        [TearDown]
        public void TearDown()
        {
            LipSyncProfileCatalog.ClearCachesForTests();
            LipSyncDefaultMappingResolver.ClearCachesForTests();
        }

        [Test]
        public void BuiltInProfiles_AreTheThreeShippedOnes_InProfileIdOrder_WithNoValidationIssues()
        {
            IReadOnlyList<ConvaiLipSyncProfile> profiles = LipSyncProfileCatalog.GetProfiles();

            Assert.AreEqual(3, profiles.Count, "The SDK ships exactly three built-in lipsync profiles.");

            Assert.AreEqual("arkit", profiles[0].ProfileId.Value);
            Assert.AreEqual("ARKit", profiles[0].DisplayName);
            Assert.AreEqual("arkit", profiles[0].TransportFormat);

            Assert.AreEqual("cc4_extended", profiles[1].ProfileId.Value);
            Assert.AreEqual("CC4 Extended", profiles[1].DisplayName);
            Assert.AreEqual("cc4_extended", profiles[1].TransportFormat);

            Assert.AreEqual("metahuman", profiles[2].ProfileId.Value);
            Assert.AreEqual("MetaHuman", profiles[2].DisplayName);
            Assert.AreEqual("mha", profiles[2].TransportFormat,
                "MetaHuman's wire format is 'mha' and is not the same string as its profile id.");

            Assert.IsEmpty(LipSyncProfileCatalog.GetValidationIssues(),
                "The shipped content must register cleanly; any issue here is a defect, not noise.");
        }

        [Test]
        [TestCase("arkit", "ConvaiLipSyncDefaultMap_ARKit", 61)]
        [TestCase("cc4_extended", "ConvaiLipSyncDefaultMap_CC4Extended", 170)]
        [TestCase("metahuman", "ConvaiLipSyncDefaultMap_MetaHuman", 251)]
        public void DefaultMapRegistry_ResolvesTheExpectedMapForEachBuiltInProfile(
            string profileId, string expectedMapName, int expectedMappingCount)
        {
            var id = new LipSyncProfileId(profileId);

            ConvaiLipSyncMapAsset map = LipSyncDefaultMappingResolver.ResolveProfileDefault(id);

            Assert.NotNull(map, $"No default map resolved for '{profileId}'.");
            Assert.AreEqual(expectedMapName, map.name);
            Assert.AreEqual(id, map.TargetProfileId);
            Assert.AreEqual(expectedMappingCount, map.Mappings.Count,
                "Mapping count changed — the map's content was edited, which this refactor must not do.");
        }

        /// <summary>
        ///     The ARKit-to-CC4 map drives a Character Creator 4 rig from ARKit transport data: the
        ///     backend sends ARKit channel names, but the character's mesh carries CC4-named
        ///     blendshapes, so the map renames them on the way through. It is a real shipped
        ///     configuration and must survive the refactor byte-for-byte.
        /// </summary>
        [Test]
        public void ArkitToCc4ExtendedMap_IsIntact()
        {
            ConvaiLipSyncMapAsset map = LoadSingleMapByName(ArkitToCc4MapName);

            Assert.AreEqual("arkit", map.TargetProfileId.Value,
                "It applies to the ARKit transport — that is what TargetProfileId means here.");
            Assert.AreEqual(61, map.Mappings.Count);
            Assert.IsNotEmpty(map.Description);

            Assert.IsTrue(map.TryGetEntry("EyeBlinkLeft", out ConvaiLipSyncMapAsset.BlendshapeMappingSnapshot entry),
                "The ARKit source channel 'EyeBlinkLeft' must still be mapped.");
            CollectionAssert.Contains(entry.TargetNames, "Eye_Blink_L",
                "It must still retarget onto the CC4-named blendshape — that is the map's whole purpose.");
        }

        /// <summary>
        ///     Two maps legitimately serve the ARKit transport — one for ARKit-named rigs, one for
        ///     CC4-named rigs — and since phase B4a the registry can hold both, keyed by the rig
        ///     vocabulary as well as the transport.
        /// </summary>
        /// <remarks>
        ///     Replaces a characterization test that recorded the opposite: before B4a the registry
        ///     was keyed by transport alone, so the CC4 variant lost the single slot and was
        ///     reachable from no code path at all.
        /// </remarks>
        [Test]
        public void ArkitTransport_ResolvesPerRigVocabulary()
        {
            ConvaiLipSyncDefaultMapRegistry registry = LipSyncDefaultMappingResolver.GetRegistry();
            Assert.NotNull(registry);

            Assert.AreEqual("ConvaiLipSyncDefaultMap_ARKit",
                registry.GetForProfile(LipSyncProfileId.ARKit, LipSyncProfileId.ARKit).name,
                "An ARKit-named rig on ARKit transport keeps the plain ARKit map.");

            Assert.AreEqual(ArkitToCc4MapName,
                registry.GetForProfile(LipSyncProfileId.ARKit, LipSyncProfileId.Cc4Extended).name,
                "A CC4-named rig on ARKit transport must reach the ARKit-to-CC4 map.");

            Assert.AreEqual("ConvaiLipSyncDefaultMap_ARKit",
                registry.GetForProfile(LipSyncProfileId.ARKit, LipSyncProfileId.MetaHuman).name,
                "An unrecognised rig falls back to the transport's plain default.");
        }

        /// <summary>
        ///     The single-argument lookup predates rig keys and is public API. Adding the second
        ///     overload must not have changed what it returns for any shipped profile, or every
        ///     existing caller and customer registry would quietly start resolving differently.
        /// </summary>
        [Test]
        [TestCase("arkit", "ConvaiLipSyncDefaultMap_ARKit")]
        [TestCase("cc4_extended", "ConvaiLipSyncDefaultMap_CC4Extended")]
        [TestCase("metahuman", "ConvaiLipSyncDefaultMap_MetaHuman")]
        public void SingleArgumentLookup_IsUnchangedByRigKeys(string profileId, string expectedMapName)
        {
            ConvaiLipSyncDefaultMapRegistry registry = LipSyncDefaultMappingResolver.GetRegistry();

            Assert.AreEqual(expectedMapName, registry.GetForProfile(new LipSyncProfileId(profileId)).name);
        }

        /// <summary>The shipped registry must describe itself cleanly, rig keys included.</summary>
        [Test]
        public void ShippedRegistry_HasNoValidationIssues()
        {
            Assert.IsEmpty(LipSyncDefaultMappingResolver.GetRegistry().ValidationIssues);
        }

        private static ConvaiLipSyncMapAsset LoadSingleMapByName(string assetName)
        {
            string[] guids = AssetDatabase.FindAssets(assetName);
            var found = new List<ConvaiLipSyncMapAsset>();

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var map = AssetDatabase.LoadAssetAtPath<ConvaiLipSyncMapAsset>(path);
                if (map != null && map.name == assetName) found.Add(map);
            }

            Assert.AreEqual(1, found.Count,
                $"Expected exactly one '{assetName}' in the project, found {found.Count}.");
            return found[0];
        }
    }
}
