using System.Collections.Generic;
using Convai.Editor.Actions;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Editor
{
    /// <summary>
    ///     Covers <see cref="ConvaiActionsSceneKnowledgeModel" /> — the pure logic behind the
    ///     Actions Editor window's Scene Knowledge pane: scan-row classification (known by entry /
    ///     registers automatically / not known) and initial-attention validation, both of which must
    ///     mirror the runtime's trim + case-insensitive name matching so the pane never disagrees
    ///     with what actually happens at connect time.
    /// </summary>
    [TestFixture]
    public sealed class ConvaiActionsSceneKnowledgeModelTests
    {
        private static List<ConvaiActionObjectDefinition> Objects(params string[] names)
        {
            var list = new List<ConvaiActionObjectDefinition>();
            foreach (string name in names)
                list.Add(new ConvaiActionObjectDefinition { Name = name });
            return list;
        }

        private static List<ConvaiActionCharacterDefinition> Characters(params string[] names)
        {
            var list = new List<ConvaiActionCharacterDefinition>();
            foreach (string name in names)
                list.Add(new ConvaiActionCharacterDefinition { Name = name });
            return list;
        }

        #region Classify

        [Test]
        public void Classify_ObjectMatchingEntry_IsKnownByEntry()
        {
            ConvaiSceneKnowledgeScanStatus status = ConvaiActionsSceneKnowledgeModel.Classify(
                "Desk Lamp", ConvaiActionTargetKind.Object, autoRegistersForCharacter: false,
                Objects("Desk Lamp"), Characters());

            Assert.AreEqual(ConvaiSceneKnowledgeScanStatus.KnownByEntry, status);
        }

        [Test]
        public void Classify_MatchIsCaseInsensitiveAndTrimmed()
        {
            ConvaiSceneKnowledgeScanStatus status = ConvaiActionsSceneKnowledgeModel.Classify(
                "  desk lamp ", ConvaiActionTargetKind.Object, autoRegistersForCharacter: false,
                Objects("DESK LAMP"), Characters());

            Assert.AreEqual(ConvaiSceneKnowledgeScanStatus.KnownByEntry, status);
        }

        [Test]
        public void Classify_ObjectKind_DoesNotMatchCharacterEntries()
        {
            ConvaiSceneKnowledgeScanStatus status = ConvaiActionsSceneKnowledgeModel.Classify(
                "Guard", ConvaiActionTargetKind.Object, autoRegistersForCharacter: false,
                Objects(), Characters("Guard"));

            Assert.AreEqual(ConvaiSceneKnowledgeScanStatus.NotKnown, status);
        }

        [Test]
        public void Classify_CharacterKind_MatchesCharacterEntries()
        {
            ConvaiSceneKnowledgeScanStatus status = ConvaiActionsSceneKnowledgeModel.Classify(
                "Guard", ConvaiActionTargetKind.Character, autoRegistersForCharacter: false,
                Objects("Guard"), Characters("Guard"));

            Assert.AreEqual(ConvaiSceneKnowledgeScanStatus.KnownByEntry, status);
        }

        [Test]
        public void Classify_NoEntryButAutoRegisters_IsRegistersAutomatically()
        {
            ConvaiSceneKnowledgeScanStatus status = ConvaiActionsSceneKnowledgeModel.Classify(
                "Crate", ConvaiActionTargetKind.Object, autoRegistersForCharacter: true,
                Objects(), Characters());

            Assert.AreEqual(ConvaiSceneKnowledgeScanStatus.RegistersAutomatically, status);
        }

        [Test]
        public void Classify_EntryMatchWins_OverAutoRegistration()
        {
            ConvaiSceneKnowledgeScanStatus status = ConvaiActionsSceneKnowledgeModel.Classify(
                "Crate", ConvaiActionTargetKind.Object, autoRegistersForCharacter: true,
                Objects("Crate"), Characters());

            Assert.AreEqual(ConvaiSceneKnowledgeScanStatus.KnownByEntry, status);
        }

        [Test]
        public void Classify_NoEntryNoAutoRegistration_IsNotKnown()
        {
            ConvaiSceneKnowledgeScanStatus status = ConvaiActionsSceneKnowledgeModel.Classify(
                "Crate", ConvaiActionTargetKind.Object, autoRegistersForCharacter: false,
                Objects("Barrel"), Characters());

            Assert.AreEqual(ConvaiSceneKnowledgeScanStatus.NotKnown, status);
        }

        [Test]
        public void Classify_NullEntryListsAndNullEntries_AreTolerated()
        {
            var objectsWithNullEntry = new List<ConvaiActionObjectDefinition> { null };

            Assert.AreEqual(
                ConvaiSceneKnowledgeScanStatus.NotKnown,
                ConvaiActionsSceneKnowledgeModel.Classify(
                    "Crate", ConvaiActionTargetKind.Object, autoRegistersForCharacter: false,
                    objectsWithNullEntry, null));
        }

        #endregion

        #region ValidateInitialAttention

        [Test]
        public void ValidateInitialAttention_Empty_IsNotSet()
        {
            Assert.AreEqual(
                ConvaiInitialAttentionStatus.NotSet,
                ConvaiActionsSceneKnowledgeModel.ValidateInitialAttention(null, Objects("Lamp")));
            Assert.AreEqual(
                ConvaiInitialAttentionStatus.NotSet,
                ConvaiActionsSceneKnowledgeModel.ValidateInitialAttention("   ", Objects("Lamp")));
        }

        [Test]
        public void ValidateInitialAttention_MatchingObject_IsKnown()
        {
            Assert.AreEqual(
                ConvaiInitialAttentionStatus.Known,
                ConvaiActionsSceneKnowledgeModel.ValidateInitialAttention(" lamp ", Objects("Lamp")));
        }

        [Test]
        public void ValidateInitialAttention_NoMatchingObject_IsUnknown()
        {
            Assert.AreEqual(
                ConvaiInitialAttentionStatus.Unknown,
                ConvaiActionsSceneKnowledgeModel.ValidateInitialAttention("Chair", Objects("Lamp")));
        }

        [Test]
        public void ValidateInitialAttention_NeverMatchesCharacterNames()
        {
            // Mirrors the runtime: current_attention_object is resolved against objects only.
            Assert.AreEqual(
                ConvaiInitialAttentionStatus.Unknown,
                ConvaiActionsSceneKnowledgeModel.ValidateInitialAttention("Guard", Objects("Lamp")));
        }

        #endregion

        #region NamesMatch

        [Test]
        public void NamesMatch_BlankSides_NeverMatch()
        {
            Assert.IsFalse(ConvaiActionsSceneKnowledgeModel.NamesMatch(null, "Lamp"));
            Assert.IsFalse(ConvaiActionsSceneKnowledgeModel.NamesMatch("Lamp", " "));
            Assert.IsFalse(ConvaiActionsSceneKnowledgeModel.NamesMatch(string.Empty, string.Empty));
        }

        [Test]
        public void NamesMatch_TrimsAndIgnoresCase()
        {
            Assert.IsTrue(ConvaiActionsSceneKnowledgeModel.NamesMatch("  Desk Lamp ", "desk lamp"));
        }

        #endregion

        #region Section summaries

        // Every Scene Knowledge section is collapsible, so its header summary is the only thing a
        // folded section says about itself. These cover the states a user reads that summary in.

        [Test]
        public void KnownObjectsSummary_CountsAndSingular()
        {
            Assert.AreEqual("none yet", ConvaiActionsEditorStrings.BuildKnownObjectsSummary(0));
            Assert.AreEqual("1 object", ConvaiActionsEditorStrings.BuildKnownObjectsSummary(1));
            Assert.AreEqual("8 objects", ConvaiActionsEditorStrings.BuildKnownObjectsSummary(8));
        }

        [Test]
        public void KnownCharactersSummary_CountsAndSingular()
        {
            Assert.AreEqual("none yet", ConvaiActionsEditorStrings.BuildKnownCharactersSummary(0));
            Assert.AreEqual("1 character", ConvaiActionsEditorStrings.BuildKnownCharactersSummary(1));
            Assert.AreEqual("3 characters", ConvaiActionsEditorStrings.BuildKnownCharactersSummary(3));
        }

        [Test]
        public void InitialAttentionSummary_ReportsTheChoiceOrThatThereIsNone()
        {
            Assert.AreEqual("none", ConvaiActionsEditorStrings.BuildInitialAttentionSummary(null));
            Assert.AreEqual("none", ConvaiActionsEditorStrings.BuildInitialAttentionSummary("   "));
            Assert.AreEqual("The Stage", ConvaiActionsEditorStrings.BuildInitialAttentionSummary("  The Stage "));
        }

        [Test]
        public void ScanSummary_SeparatesNeverScannedFromFoundNothing()
        {
            Assert.AreEqual("not scanned yet", ConvaiActionsEditorStrings.BuildScanSummary(false, 0, 0));
            Assert.AreEqual("none found", ConvaiActionsEditorStrings.BuildScanSummary(true, 0, 0));
        }

        [Test]
        public void ScanSummary_LeadsWithWhatIsStillNotKnown()
        {
            Assert.AreEqual("1 found · all known", ConvaiActionsEditorStrings.BuildScanSummary(true, 1, 0));
            Assert.AreEqual("12 found · 3 not known", ConvaiActionsEditorStrings.BuildScanSummary(true, 12, 3));
        }

        [Test]
        public void SentToConvaiSummary_SeparatesNothingSentFromNothingListed()
        {
            // Two different situations with two different fixes: the character has no working action
            // to carry scene knowledge at all, versus it has one and there is nothing to say.
            Assert.AreEqual("nothing sent", ConvaiActionsEditorStrings.BuildSentToConvaiSummary(true, 4, 1));
            Assert.AreEqual("nothing listed", ConvaiActionsEditorStrings.BuildSentToConvaiSummary(false, 0, 0));
        }

        [Test]
        public void SentToConvaiSummary_CountsBothDeliveryChannelsTogether()
        {
            // The header answers "how much does it know?", so it leads with the total.
            Assert.AreEqual("1 entry", ConvaiActionsEditorStrings.BuildSentToConvaiSummary(false, 1, 0));
            Assert.AreEqual("9 entries", ConvaiActionsEditorStrings.BuildSentToConvaiSummary(false, 9, 0));
        }

        [Test]
        public void SentToConvaiSummary_QualifiesTheTotalWhenSceneTargetsArriveLater()
        {
            // Reporting 19 while the character ends up knowing 57 was accurate about the connect
            // message and wrong about the character.
            Assert.AreEqual(
                "57 entries · 19 at connect",
                ConvaiActionsEditorStrings.BuildSentToConvaiSummary(false, 19, 38));
        }

        [Test]
        public void ScanOutcome_AccountsForEveryRowItFound()
        {
            // The bare count left the reader to work out whether "39 found" was good news.
            Assert.AreEqual(
                "All 39 targets in your scene already reach this Convai Character — " +
                "17 through an entry above, 22 automatically.",
                ConvaiActionsEditorStrings.BuildScanOutcome(39, 17, 22, 0));
        }

        [Test]
        public void ScanOutcome_LeadsWithTheOnesThatReachNobody()
        {
            Assert.AreEqual(
                "9 of the 12 targets in your scene reach this Convai Character — " +
                "4 through an entry above, 5 automatically. 3 reach it through neither, and are listed below.",
                ConvaiActionsEditorStrings.BuildScanOutcome(12, 4, 5, 3));
        }

        [Test]
        public void ScanOutcome_SingularWhenOnlyOneIsUnreachable()
        {
            Assert.AreEqual(
                "2 of the 3 targets in your scene reach this Convai Character — " +
                "each through an entry above. One reaches it through neither, and is listed below.",
                ConvaiActionsEditorStrings.BuildScanOutcome(3, 2, 0, 1));
        }

        [Test]
        public void ScanOutcome_DoesNotClaimAMixWhenThereIsOnlyOneChannel()
        {
            Assert.AreEqual(
                "All 5 targets in your scene already reach this Convai Character — " +
                "automatically, with no entry needed.",
                ConvaiActionsEditorStrings.BuildScanOutcome(5, 0, 5, 0));
            Assert.AreEqual(
                "All 5 targets in your scene already reach this Convai Character — " +
                "each through an entry above.",
                ConvaiActionsEditorStrings.BuildScanOutcome(5, 5, 0, 0));
        }

        [Test]
        public void ScanOutcome_IsNothingAtAllWhenNothingWasFound()
        {
            // The card has its own sentence for an empty scan; this must not compete with it.
            Assert.IsNull(ConvaiActionsEditorStrings.BuildScanOutcome(0, 0, 0, 0));
        }

        [Test]
        public void ScanAddAllButton_NamesTheCountAndReadsAsEnglishAtOne()
        {
            Assert.AreEqual("Add the 1 not known", ConvaiActionsEditorStrings.BuildScanAddAllButton(1).text);
            Assert.AreEqual("Add all 4 not known", ConvaiActionsEditorStrings.BuildScanAddAllButton(4).text);
        }

        #endregion

        #region ComputeReach

        // What the character actually ends up knowing, split by when each name reaches the backend:
        // authored entries ride the connect payload, scene targets arrive in the follow-up sync.

        private static List<ConvaiScannedTargetName> Scanned(
            params (string Name, ConvaiSceneKnowledgeScanStatus Status)[] rows)
        {
            var list = new List<ConvaiScannedTargetName>();
            foreach ((string name, ConvaiSceneKnowledgeScanStatus status) in rows)
                list.Add(new ConvaiScannedTargetName(name, status));
            return list;
        }

        [Test]
        public void ComputeReach_BeforeAnyScan_ReportsTheConnectPayloadAlone()
        {
            ConvaiSceneKnowledgeReach reach = ConvaiActionsSceneKnowledgeModel.ComputeReach(18, 1, null);

            Assert.AreEqual(19, reach.AtConnectCount);
            Assert.IsEmpty(reach.AtConversationStart);
            Assert.AreEqual(0, reach.NotDeliveredCount);
            Assert.AreEqual(19, reach.TotalKnownCount);
        }

        [Test]
        public void ComputeReach_AutoRegisteringTargets_ArriveAtConversationStart()
        {
            ConvaiSceneKnowledgeReach reach = ConvaiActionsSceneKnowledgeModel.ComputeReach(
                2, 0,
                Scanned(
                    ("Crate", ConvaiSceneKnowledgeScanStatus.RegistersAutomatically),
                    ("Barrel", ConvaiSceneKnowledgeScanStatus.RegistersAutomatically)));

            Assert.AreEqual(2, reach.AtConnectCount);
            CollectionAssert.AreEqual(new[] { "Crate", "Barrel" }, reach.AtConversationStart);
            Assert.AreEqual(4, reach.TotalKnownCount);
        }

        [Test]
        public void ComputeReach_TargetKnownByAnEntry_IsNotCountedTwice()
        {
            // The authored entry already carries this name in the connect payload.
            ConvaiSceneKnowledgeReach reach = ConvaiActionsSceneKnowledgeModel.ComputeReach(
                1, 0, Scanned(("Crate", ConvaiSceneKnowledgeScanStatus.KnownByEntry)));

            Assert.AreEqual(1, reach.AtConnectCount);
            Assert.IsEmpty(reach.AtConversationStart);
            Assert.AreEqual(1, reach.TotalKnownCount);
        }

        [Test]
        public void ComputeReach_NotKnownTargets_ReachNeitherChannel()
        {
            ConvaiSceneKnowledgeReach reach = ConvaiActionsSceneKnowledgeModel.ComputeReach(
                0, 0,
                Scanned(
                    ("Crate", ConvaiSceneKnowledgeScanStatus.NotKnown),
                    ("Barrel", ConvaiSceneKnowledgeScanStatus.NotKnown)));

            Assert.AreEqual(2, reach.NotDeliveredCount);
            Assert.IsEmpty(reach.AtConversationStart);
            Assert.AreEqual(0, reach.TotalKnownCount);
        }

        [Test]
        public void ComputeReach_RepeatedNames_AreCountedOnce()
        {
            // Mirrors BuildActionConfigWirePatch: the backend rejects duplicate names, so counting
            // both would promise more than the wire delivers.
            ConvaiSceneKnowledgeReach reach = ConvaiActionsSceneKnowledgeModel.ComputeReach(
                0, 0,
                Scanned(
                    ("Crate", ConvaiSceneKnowledgeScanStatus.RegistersAutomatically),
                    (" crate ", ConvaiSceneKnowledgeScanStatus.RegistersAutomatically),
                    ("CRATE", ConvaiSceneKnowledgeScanStatus.RegistersAutomatically)));

            CollectionAssert.AreEqual(new[] { "Crate" }, reach.AtConversationStart);
        }

        [Test]
        public void ComputeReach_BlankNames_AreDropped()
        {
            ConvaiSceneKnowledgeReach reach = ConvaiActionsSceneKnowledgeModel.ComputeReach(
                0, 0,
                Scanned(
                    (null, ConvaiSceneKnowledgeScanStatus.RegistersAutomatically),
                    ("   ", ConvaiSceneKnowledgeScanStatus.RegistersAutomatically),
                    ("Crate", ConvaiSceneKnowledgeScanStatus.RegistersAutomatically)));

            CollectionAssert.AreEqual(new[] { "Crate" }, reach.AtConversationStart);
        }

        [Test]
        public void ComputeReach_NamesAreTrimmedTheWayTheWireTrimsThem()
        {
            ConvaiSceneKnowledgeReach reach = ConvaiActionsSceneKnowledgeModel.ComputeReach(
                0, 0, Scanned(("  Crate  ", ConvaiSceneKnowledgeScanStatus.RegistersAutomatically)));

            CollectionAssert.AreEqual(new[] { "Crate" }, reach.AtConversationStart);
        }

        [Test]
        public void ComputeReach_MixedScan_SplitsEveryStatusIntoTheRightBucket()
        {
            ConvaiSceneKnowledgeReach reach = ConvaiActionsSceneKnowledgeModel.ComputeReach(
                3, 1,
                Scanned(
                    ("Known Crate", ConvaiSceneKnowledgeScanStatus.KnownByEntry),
                    ("Auto Barrel", ConvaiSceneKnowledgeScanStatus.RegistersAutomatically),
                    ("Orphan Lamp", ConvaiSceneKnowledgeScanStatus.NotKnown)));

            Assert.AreEqual(4, reach.AtConnectCount);
            CollectionAssert.AreEqual(new[] { "Auto Barrel" }, reach.AtConversationStart);
            Assert.AreEqual(1, reach.NotDeliveredCount);
            Assert.AreEqual(5, reach.TotalKnownCount);
        }

        #endregion
    }
}
