using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Data;
using Convai.Editor.UI;
using Convai.Modules.Gaze.Editor;
using NUnit.Framework;
using System.Collections.Generic;
using System.Reflection;
using System;
using UnityEditor;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     The state table editor draws states rather than array elements, which means a state the
    ///     editor does not know about becomes invisible in the UI while still applying at runtime.
    ///     These tests make adding a <see cref="DialogueState" /> without updating the editor a
    ///     build failure rather than a silent gap.
    /// </summary>
    internal sealed class GazeStatePolicyTableTests
    {
        private static DialogueState[] EditorOrder =>
            (DialogueState[])typeof(GazeStatePolicyTable)
                .GetField("Order", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetValue(null);


        [Test]
        public void EveryDialogueState_HasARowInTheEditor()
        {
            var listed = new HashSet<DialogueState>(EditorOrder);
            var missing = new List<string>();

            foreach (DialogueState state in Enum.GetValues(typeof(DialogueState)))
                if (!listed.Contains(state))
                    missing.Add(state.ToString());

            Assert.IsEmpty(missing,
                "These conversation states are not drawn by the state table, so a profile authoring " +
                "them would apply behaviour the user cannot see or edit:\n  " +
                string.Join("\n  ", missing));
        }

        [Test]
        public void EveryDrawnState_HasAnExplanationAndAName()
        {
            foreach (DialogueState state in EditorOrder)
            {
                Assert.IsNotEmpty(
                    ConvaiDialogueStateLabels.Explain(state),
                    $"{state} has no plain-English explanation, so its row is a bare name with nothing beside it.");
                Assert.IsNotEmpty(GazeStatePolicyTable.FriendlyName(state));
            }
        }

        /// <summary>
        ///     One state, one name — in the table, in every live panel, and in the API.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         This table used to relabel two states on its own: "Attending" was drawn as
        ///         "Addressed" and "Settling" as "Winding down", while every live panel in the SDK
        ///         showed the raw enum. A user watching the character sit in "Attending" could not
        ///         find the row that governed it. The meaning now travels in
        ///         <see cref="ConvaiDialogueStateLabels.Explain" /> beside the name instead of
        ///         replacing it.
        ///     </para>
        /// </remarks>
        [Test]
        public void StateNames_AreTheSameWordAsTheApi()
        {
            foreach (DialogueState state in Enum.GetValues(typeof(DialogueState)))
            {
                Assert.AreEqual(
                    state.ToString(),
                    ConvaiDialogueStateLabels.Name(state),
                    $"{state} is shown to the user under a different name than the API uses.");
                Assert.AreEqual(
                    ConvaiDialogueStateLabels.Name(state),
                    GazeStatePolicyTable.FriendlyName(state),
                    $"{state}: the gaze state table must not have a vocabulary of its own.");
            }
        }

        [Test]
        public void EditorOrder_HasNoDuplicates()
        {
            var seen = new HashSet<DialogueState>();
            foreach (DialogueState state in EditorOrder)
                Assert.IsTrue(seen.Add(state), $"{state} is listed twice, so it would draw two rows.");
        }

        [Test]
        public void IndexOf_FindsAuthoredStatesAndReportsMissingOnes()
        {
            ConvaiGazeProfile profile = ConvaiGazeProfile.CreateDefault();
            try
            {
                var serialized = new SerializedObject(profile);
                SerializedProperty policies = GazeProfileSerializedPaths.Find(serialized, "statePolicies");

                Assert.That(GazeStatePolicyTable.IndexOf(policies, DialogueState.Idle), Is.GreaterThanOrEqualTo(0),
                    "The shipped profile authors Idle, which is the fallback for everything else.");

                // Remove every row and confirm the lookup reports absence rather than throwing.
                policies.ClearArray();
                serialized.ApplyModifiedProperties();
                Assert.AreEqual(-1, GazeStatePolicyTable.IndexOf(policies, DialogueState.Idle));
                Assert.AreEqual(-1, GazeStatePolicyTable.IndexOf(null, DialogueState.Idle));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }
    }
}
