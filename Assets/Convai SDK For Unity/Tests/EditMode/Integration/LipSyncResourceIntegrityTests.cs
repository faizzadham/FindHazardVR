using System;
using System.Collections.Generic;
using Convai.Domain.Models.LipSync;
using Convai.Modules.LipSync;
using Convai.Modules.LipSync.Profiles;
using NUnit.Framework;
using UnityEditor;

namespace Convai.Tests.EditMode.Integration
{
    [TestFixture]
    public sealed class LipSyncResourceIntegrityTests
    {
        /// <summary>
        ///     Where the default blendshape maps live: inside the package, so they ship whether or
        ///     not a sample was imported. Their <c>Resources</c> path is unchanged by that move —
        ///     the folder is <c>…/Resources/LipSync/DefaultMaps</c> either way.
        /// </summary>
        private const string PackageMapRoot =
            "Packages/com.convai.convai-sdk-for-unity/SDK/Modules/LipSync/Resources/LipSync";

        [Test]
        public void ShippedMetaHumanAssets_UseSemanticProfileIdAndMhaWireFormat()
        {
            LipSyncProfileCatalog.ClearCachesForTests();

            // The MetaHuman profile is built in code, not loaded from an asset, so it is asked of
            // the catalog rather than the AssetDatabase. Its map is still an asset.
            Assert.IsTrue(
                LipSyncProfileCatalog.TryGetProfile(LipSyncProfileId.MetaHuman, out ConvaiLipSyncProfile profile),
                "The MetaHuman profile must be registered without any asset behind it.");

            ConvaiLipSyncMapAsset map = AssetDatabase.LoadAssetAtPath<ConvaiLipSyncMapAsset>(
                $"{PackageMapRoot}/DefaultMaps/ConvaiLipSyncDefaultMap_MetaHuman.asset");
            ConvaiLipSyncDefaultMapRegistry registry =
                AssetDatabase.LoadAssetAtPath<ConvaiLipSyncDefaultMapRegistry>(
                    $"{PackageMapRoot}/DefaultMaps/LipSyncDefaultMapRegistry.asset");

            Assert.NotNull(map);
            Assert.NotNull(registry);
            Assert.AreEqual(LipSyncProfileId.MetaHuman, profile.ProfileId);
            Assert.AreEqual("mha", profile.TransportFormat);
            Assert.AreEqual(LipSyncProfileId.MetaHuman, map.TargetProfileId);
            Assert.AreSame(map, registry.GetForProfile(LipSyncProfileId.MetaHuman));

            Assert.Throws<NotSupportedException>(() =>
                ((IList<ConvaiLipSyncMapAsset.BlendshapeMappingEntry>)map.Mappings).Clear());
            Assert.Throws<NotSupportedException>(() =>
                ((IList<ConvaiLipSyncProfile>)LipSyncProfileCatalog.GetProfiles()).Clear());
        }

        [Test]
        public void LegacyMhaProfileId_FallsBackToMetaHumanOnlyWhenExactProfileIsAbsent()
        {
            LipSyncProfileCatalog.ClearCachesForTests();
            Assert.IsTrue(LipSyncProfileCatalog.TryGetProfile("mha", out ConvaiLipSyncProfile profile));
            Assert.AreEqual(LipSyncProfileId.MetaHuman, profile.ProfileId);
        }
    }
}
