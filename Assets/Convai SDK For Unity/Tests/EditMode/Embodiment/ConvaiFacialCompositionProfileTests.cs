using Convai.Runtime.Animation;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Embodiment
{
    /// <summary>
    ///     Covers the built-in facial composition default, which the compositor now builds in code
    ///     instead of loading a shipped asset out of <c>Resources</c>.
    /// </summary>
    /// <remarks>
    ///     The asset it replaced was a serialization of this type's own field initializers, so it
    ///     could only ever agree with them — until it did not. These tests assert the values the
    ///     compositor actually depends on, so a change to an initializer is a decision rather than a
    ///     surprise.
    /// </remarks>
    [TestFixture]
    public sealed class ConvaiFacialCompositionProfileTests
    {
        [Test]
        public void CreateDefault_ReturnsAnInstanceThatCanNeverBeSavedIntoAScene()
        {
            ConvaiFacialCompositionProfile profile = ConvaiFacialCompositionProfile.CreateDefault();
            try
            {
                Assert.NotNull(profile);
                Assert.That(profile.hideFlags, Is.EqualTo(HideFlags.HideAndDontSave));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void CreateDefault_GivesSpeechTheMouthAndLeavesTheBrowToEmotion()
        {
            // The one thing this profile exists to decide. If speech ever stops owning the mouth, or
            // the brow stops belonging to Emotion, a talking character reads wrong — so these two
            // are pinned rather than left to whatever the initializers drift to.
            ConvaiFacialCompositionProfile profile = ConvaiFacialCompositionProfile.CreateDefault();
            try
            {
                RegionBlendConfig mouth = profile.GetRegionConfig(FacialBlendshapeRegion.Mouth);
                Assert.That(mouth.SpeakingLipSyncWeight, Is.EqualTo(1f).Within(1e-4f));
                Assert.That(mouth.SpeakingEmotionWeight, Is.LessThan(mouth.SpeakingLipSyncWeight));
                Assert.That(mouth.IdleLipSyncWeight, Is.EqualTo(0f).Within(1e-4f));
                Assert.That(mouth.IdleEmotionWeight, Is.EqualTo(1f).Within(1e-4f));

                RegionBlendConfig brow = profile.GetRegionConfig(FacialBlendshapeRegion.Brow);
                Assert.That(brow.SpeakingEmotionWeight, Is.GreaterThan(brow.SpeakingLipSyncWeight));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void CreateDefault_ClassifiesTheStandardBlendshapeNames()
        {
            ConvaiFacialCompositionProfile profile = ConvaiFacialCompositionProfile.CreateDefault();
            try
            {
                Assert.That(profile.ClassifyBlendshape("MouthSmileLeft"), Is.EqualTo(FacialBlendshapeRegion.Mouth));
                Assert.That(profile.ClassifyBlendshape("JawOpen"), Is.EqualTo(FacialBlendshapeRegion.Mouth),
                    "Jaw_Open is a mouth pattern; only directional jaw movement is its own region.");
                Assert.That(profile.ClassifyBlendshape("JawForward"), Is.EqualTo(FacialBlendshapeRegion.Jaw));
                Assert.That(profile.ClassifyBlendshape("BrowInnerUp"), Is.EqualTo(FacialBlendshapeRegion.Brow));
                Assert.That(profile.ClassifyBlendshape("EyeBlinkLeft"), Is.EqualTo(FacialBlendshapeRegion.Eye));
                Assert.That(profile.ClassifyBlendshape("CheekPuff"), Is.EqualTo(FacialBlendshapeRegion.Cheek));
                Assert.That(profile.ClassifyBlendshape("SomethingUnrecognised"),
                    Is.EqualTo(FacialBlendshapeRegion.Other));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>
        ///     The eye and jaw patterns must classify the naming Character Creator and MetaHuman
        ///     rigs actually use, not only the ARKit spelling they were written against.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Separators are ignored when matching but order is not, and Character Creator puts
        ///         the side in the middle of the name: <c>Eye_L_Look_Up</c> never contained
        ///         <c>Eye_Look</c>. Measured against the shipped lip-sync maps, every one of the
        ///         eight eye-look targets on a CC rig fell through to <c>Other</c> — as did
        ///         <c>Jaw_L</c>, <c>Jaw_R</c>, <c>Jaw_Up</c>, <c>Jaw_Down</c> and
        ///         <c>Jaw_Backward</c>, which the jaw list simply never named.
        ///     </para>
        ///     <para>
        ///         Landing in <c>Other</c> is not cosmetic: that region composes lip sync at 0.3
        ///         where Jaw composes it at 1, so directional jaw motion drove at under a third of
        ///         the strength the profile says it should.
        ///     </para>
        /// </remarks>
        [Test]
        public void CreateDefault_ClassifiesTheNamingTheShippedRigsUse()
        {
            ConvaiFacialCompositionProfile profile = ConvaiFacialCompositionProfile.CreateDefault();
            try
            {
                // Character Creator: the side sits between the parts of the name.
                Assert.That(profile.ClassifyBlendshape("Eye_L_Look_Up"), Is.EqualTo(FacialBlendshapeRegion.Eye));
                Assert.That(profile.ClassifyBlendshape("Eye_R_Look_Down"), Is.EqualTo(FacialBlendshapeRegion.Eye));
                Assert.That(profile.ClassifyBlendshape("Eyelash_Upper_Up_L"), Is.EqualTo(FacialBlendshapeRegion.Eye),
                    "Eyelashes follow the lids, so they belong to the eye rather than to the leftovers.");

                // ARKit spelling must keep working — the abbreviated jaw patterns cover both.
                Assert.That(profile.ClassifyBlendshape("eyeLookUpLeft"), Is.EqualTo(FacialBlendshapeRegion.Eye));
                Assert.That(profile.ClassifyBlendshape("jawLeft"), Is.EqualTo(FacialBlendshapeRegion.Jaw));
                Assert.That(profile.ClassifyBlendshape("jawRight"), Is.EqualTo(FacialBlendshapeRegion.Jaw));

                Assert.That(profile.ClassifyBlendshape("Jaw_L"), Is.EqualTo(FacialBlendshapeRegion.Jaw));
                Assert.That(profile.ClassifyBlendshape("Jaw_R"), Is.EqualTo(FacialBlendshapeRegion.Jaw));
                Assert.That(profile.ClassifyBlendshape("Jaw_Up"), Is.EqualTo(FacialBlendshapeRegion.Jaw));
                Assert.That(profile.ClassifyBlendshape("Jaw_Down"), Is.EqualTo(FacialBlendshapeRegion.Jaw));
                Assert.That(profile.ClassifyBlendshape("Jaw_Backward"), Is.EqualTo(FacialBlendshapeRegion.Jaw));

                // Still leftovers, and correctly so — no facial region owns these.
                Assert.That(profile.ClassifyBlendshape("Ear_Up_L"), Is.EqualTo(FacialBlendshapeRegion.Other));
                Assert.That(profile.ClassifyBlendshape("Neck_Tighten_L"), Is.EqualTo(FacialBlendshapeRegion.Other));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>
        ///     The profile Convai ships must be the one a customer gets by creating their own.
        /// </summary>
        /// <remarks>
        ///     This type's field initializers are the built-in default the compositor uses when no
        ///     profile is assigned, and the shipped asset is a serialization of them — so the two
        ///     can only ever agree until someone tunes one of them. Body Animation shipped exactly
        ///     that divergence: four values hand-tuned on the asset and never brought back, which
        ///     made a hand-made config a different character from every Convai sample.
        /// </remarks>
        [Test]
        public void TheShippedProfile_MatchesTheBuiltInDefault()
        {
            const string Path =
                "Packages/com.convai.convai-sdk-for-unity/SamplesShared/Profiles/Embodiment/" +
                "FacialComposition/ConvaiSamplesShared_FacialCompositionProfile.asset";

            var shipped = UnityEditor.AssetDatabase.LoadAssetAtPath<ConvaiFacialCompositionProfile>(Path);
            Assert.That(shipped, Is.Not.Null, $"Shipped facial composition profile missing: {Path}");

            ConvaiFacialCompositionProfile builtIn = ConvaiFacialCompositionProfile.CreateDefault();
            try
            {
                var a = new UnityEditor.SerializedObject(shipped);
                var b = new UnityEditor.SerializedObject(builtIn);

                var drifted = new System.Collections.Generic.List<string>();
                UnityEditor.SerializedProperty walker = a.GetIterator();
                bool stepped = walker.Next(true);
                while (stepped)
                {
                    // Unity bookkeeping, not composition: a saved asset has a name and no hide
                    // flags, while CreateDefault deliberately returns HideAndDontSave so a built-in
                    // default can never be dragged into a scene.
                    if (walker.propertyPath != "m_Script" && !walker.hasVisibleChildren &&
                        !walker.propertyPath.StartsWith("m_Name") &&
                        !walker.propertyPath.StartsWith("m_ObjectHideFlags") &&
                        !walker.propertyPath.StartsWith("m_EditorClassIdentifier"))
                    {
                        UnityEditor.SerializedProperty theirs = b.FindProperty(walker.propertyPath);
                        if (theirs != null && !UnityEditor.SerializedProperty.DataEquals(walker, theirs))
                            drifted.Add(walker.propertyPath);
                    }

                    stepped = walker.Next(true);
                }

                Assert.That(drifted, Is.Empty,
                    "The shipped facial composition profile no longer matches the built-in default, so a " +
                    "character on the shipped asset and a character on the built-in default compose their " +
                    "faces differently with nothing saying why:\n" + string.Join("\n", drifted));
            }
            finally
            {
                Object.DestroyImmediate(builtIn);
            }
        }

        [Test]
        public void CreateDefault_PrefersHeadAndFaceMeshesForBlendshapeDiscovery()
        {
            ConvaiFacialCompositionProfile profile = ConvaiFacialCompositionProfile.CreateDefault();
            try
            {
                int head = profile.GetMeshDiscoveryPriority("CC_Base_Head");
                int teeth = profile.GetMeshDiscoveryPriority("CC_Base_Teeth");
                int tongue = profile.GetMeshDiscoveryPriority("CC_Base_Tongue");
                int body = profile.GetMeshDiscoveryPriority("CC_Base_Body");

                Assert.That(head, Is.LessThan(teeth));
                Assert.That(teeth, Is.LessThan(tongue));
                Assert.That(tongue, Is.LessThan(body));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void TwoDefaults_AreConfiguredIdentically()
        {
            // The compositor rebuilds its default per host. Two characters must not end up composing
            // their faces differently because they each made their own copy.
            ConvaiFacialCompositionProfile a = ConvaiFacialCompositionProfile.CreateDefault();
            ConvaiFacialCompositionProfile b = ConvaiFacialCompositionProfile.CreateDefault();
            try
            {
                Assert.That(a.ComputeConfigurationHash(), Is.EqualTo(b.ComputeConfigurationHash()));
            }
            finally
            {
                Object.DestroyImmediate(a);
                Object.DestroyImmediate(b);
            }
        }
    }
}
