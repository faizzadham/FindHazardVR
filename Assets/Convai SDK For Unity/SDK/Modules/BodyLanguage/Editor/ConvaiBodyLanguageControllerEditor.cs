using System.Collections.Generic;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Editor.Inspectors;
using Convai.Editor.Inspectors.Framework;
using Convai.Editor.UI;
using Convai.Modules.BodyLanguage.Components;
using Convai.Modules.BodyLanguage.Core.Diagnostics;
using Convai.Modules.BodyLanguage.Data;
using UnityEditor;
using UnityEngine;
using Glyphs = Convai.Editor.UI.ConvaiEditorGlyphs;
using Theme = Convai.Editor.UI.ConvaiEditorTheme;

namespace Convai.Modules.BodyLanguage.Editor
{
    /// <summary>
    ///     Convai inspector for <see cref="ConvaiBodyLanguageController" />. Outside Play mode it
    ///     runs <see cref="BodyLanguageSetupService" /> and reports what this character's rig can and
    ///     cannot do — so a rig that will never move says so before the user presses anything. In
    ///     Play mode it adds the live readout (inert/active, dialogue state, smoothed policy values,
    ///     recent trace tail) sourced from the controller's snapshot.
    /// </summary>
    [CustomEditor(typeof(ConvaiBodyLanguageController))]
    internal sealed class ConvaiBodyLanguageControllerEditor : ConvaiInspectorEditor
    {
        private const int TraceTailLength = 6;

        /// <summary>
        ///     Seconds between rig preflights. The probe walks the avatar's bone map, which is far
        ///     too much work for every repaint of a hovered inspector, and a rig does not change
        ///     between frames.
        /// </summary>
        private const double PreflightIntervalSeconds = 0.5d;

        private const string TitleText = "Body Language";
        private const string SubtitleText = "Convai Body Language Controller";

        private const string PurposeText =
            "Body Animation moves the body; Body Language makes it speak — gesticulation, posture, " +
            "breathing, and embodied listening, per dialogue state.";

        /// <summary>Stable id for the profile section's persisted expansion. Never localise it.</summary>
        private const string SectionProfile = "Profile";

        private static readonly GUIContent ProfileSection = new("Profile");
        private static readonly GUIContent SetupSection = new("This Character");
        private static readonly GUIContent SharingSection = new("Sharing This Body");
        private static readonly GUIContent StatusSection = new("Runtime Status");
        private static readonly GUIContent PostureSection = new("Posture (target → current)");
        private static readonly GUIContent BreathSection = new("Breath");
        private static readonly GUIContent TraceSection = new("Recent Trace");

        private static readonly GUIContent ProfileLabel = new(
            "Body Language Profile",
            "The asset holding how this character carries itself. Can be shared with other " +
            "characters.");

        private static readonly GUIContent ModuleLabel = new("Module");
        private static readonly GUIContent DialogueStateLabel = new("Dialogue State");
        private static readonly GUIContent ProfileNameLabel = new("Profile");
        private static readonly GUIContent RigLabel = new("Rig");
        private static readonly GUIContent ProceduralLimbsLabel = new("Procedural Limbs");
        private static readonly GUIContent GesticulationLabel = new("Gesticulation");
        private static readonly GUIContent ListeningPostureLabel = new("Listening Posture");
        private static readonly GUIContent PostureLabel = new("Posture");
        private static readonly GUIContent BreathingLabel = new("Breathing");
        private static readonly GUIContent FidgetsLabel = new("Fidgets");
        private static readonly GUIContent BodySharedWithLabel = new("Body Shared With");
        private static readonly GUIContent HeadMovedByLabel = new("Head Moved By");
        private static readonly GUIContent OpennessLabel = new("Openness");
        private static readonly GUIContent LeanLabel = new("Lean");
        private static readonly GUIContent TensionLabel = new("Tension");
        private static readonly GUIContent MasterWeightLabel = new("Master Weight");
        private static readonly GUIContent ProceduralGestureLabel = new("Procedural Gesture");
        private static readonly GUIContent PhaseWaveformLabel = new("Phase / Waveform");
        private static readonly GUIContent RateDepthLabel = new("Rate / Depth");

        /// <summary>Reused fix-button content; the label comes from the check, so it cannot be static.</summary>
        private static readonly GUIContent FixButtonScratch = new();

        private static readonly GUIContent ActiveChip = new("Active");
        private static readonly GUIContent InertChip = new("Inert");
        private static readonly GUIContent ReadyChip = new("Ready");
        private static readonly GUIContent ActionNeededChip = new("Action Needed");

        private readonly BodyLanguageSnapshot _snapshot = new();

        private SerializedProperty _profileProp;
        private bool _snapshotValid;
        private BodyLanguagePreflight _preflight;
        private BodyLanguageCoordination _coordination;
        private ConvaiEditorRefreshTimer _preflightTimer;
        private bool _preflightResolved;

        protected override string Title => TitleText;
        protected override string Subtitle => SubtitleText;
        protected override string Purpose => PurposeText;

        /// <summary>
        ///     While playing the chip reports what the module is actually doing; outside Play mode
        ///     it reports whether the rig will let it do anything at all.
        /// </summary>
        protected override GUIContent StatusChip
        {
            get
            {
                if (_snapshotValid) return _snapshot.IsInert ? InertChip : ActiveChip;
                return _preflight.IsFunctional ? ReadyChip : ActionNeededChip;
            }
        }

        protected override Color StatusChipTint
        {
            get
            {
                if (_snapshotValid) return _snapshot.IsInert ? Theme.StatusWarn : Theme.StatusReady;
                return _preflight.IsFunctional ? Theme.StatusReady : Theme.StatusError;
            }
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            _profileProp = serializedObject.FindProperty("profile");
            _preflightResolved = false;
        }

        /// <summary>
        ///     Refreshes both status sources once per pass, before the header reads
        ///     <see cref="StatusChip" /> from them.
        /// </summary>
        protected override void OnBeforeInspectorGUI()
        {
            var controller = (ConvaiBodyLanguageController)target;

            _snapshotValid = false;
            if (EditorApplication.isPlaying)
            {
                controller.CaptureSnapshot(_snapshot);
                _snapshotValid = true;
            }

            // A click may have just fixed the very row the checklist reports, so a changed GUI
            // re-probes at the top of the next pass instead of waiting out the interval.
            if (GUI.changed)
                _preflightTimer.Invalidate(true);

            if (!_preflightTimer.ShouldRefresh(_preflightResolved, PreflightIntervalSeconds))
                return;

            _preflightResolved = true;
            _preflight = BodyLanguageSetupService.Inspect(controller);
            // Resolved on the same interval as the preflight, and from the same service: the card
            // below and the assistant's diagnosis must never be able to describe this character
            // differently.
            _coordination = BodyLanguageSetupService.InspectCoordination(controller);
        }

        protected override void DrawBody()
        {
            // Body Language was the one module whose inspector said nothing when the component sat
            // outside a Convai character — the other four report it through their setup services, so
            // the single most common placement mistake was silent here and only here.
            ConvaiCharacterScopeNotice.DrawIfMisplaced((Component)target, TitleText);

            DrawSetupCard();
            DrawProfileSection();
            DrawSharingCard();
        }

        /// <summary>
        ///     Which personality this character runs on, and the field that changes it.
        /// </summary>
        /// <remarks>
        ///     Was a plain card at the very bottom of the inspector, below two others, with no
        ///     header state — so the single most identifying fact about the component was the last
        ///     thing a user reached and said nothing when collapsed. It is now a section like the
        ///     other four modules', directly under the setup checklist, with the asset's name in the
        ///     header summary.
        ///     <para>
        ///         No ownership notice here, deliberately. Nothing on this inspector writes into the
        ///         profile — it draws the field and reads the switches — and a shared-asset warning
        ///         over controls that cannot write is an alarm about something that cannot happen.
        ///         The notice belongs on <c>ConvaiBodyLanguageProfileEditor</c>, which does write,
        ///         and it is there.
        ///     </para>
        /// </remarks>
        private void DrawProfileSection()
        {
            // Read from the bound property rather than BodyLanguageSetupService: its resolvers
            // build a SerializedObject per call and this runs on every repaint, and
            // ResolveEffectiveProfile never returns null anyway — it hands back a hidden in-memory
            // default, while the header has to be able to say nothing is assigned.
            DrawSection(
                SectionProfile, ProfileSection, Glyphs.Profile,
                () =>
                {
                    ConvaiEditorProfileField.Draw(_profileProp, ProfileLabel);

                    if (_profileProp.objectReferenceValue == null)
                        InfoBox(
                            "Using SDK Defaults",
                            "This character has no personality asset, so it uses the built-in " +
                            "defaults — which work. Assign one above to give it a body of its own.");
                },
                summary: ConvaiEditorProfileField.Summarize(_profileProp.objectReferenceValue));
        }

        /// <summary>
        ///     Which other Convai modules move this character's body, and what each one changes.
        /// </summary>
        /// <remarks>
        ///     The setup card's "Works with" row summarises this and ends "see below for what each one
        ///     changes" — this is that below. Without it the row promised a detail the Inspector never
        ///     showed, and the only place to read it was an assistant's diagnosis, which a user
        ///     without the AI Assistant package cannot reach at all. It is the same
        ///     <see cref="BodyLanguageCoordination" /> the diagnosis renders, so the two cannot
        ///     describe one character two ways.
        /// </remarks>
        private void DrawSharingCard()
        {
            if (!DrawSection(SharingSection.text, SharingSection, Glyphs.Contract)) return;

            DrawSectionBody(() =>
            {
                DrawSharingLine("Head and neck", _coordination.HeadGestures);
                DrawSharingLine("Gesture cues", _coordination.GestureCues);
                DrawSharingLine("What ducks posture", _coordination.GestureSuppression);
                DrawSharingLine("Speech rhythm", _coordination.SpeechRhythm);
                DrawSharingLine("Emotion", _coordination.Emotion);
                DrawSharingLine("Effort and breathing", _coordination.Exertion);

                GUILayout.Space(4f);
                GUILayout.Label(BodyLanguageCoordination.RuntimeCaveat, Theme.MutedWrapped);
            });
        }

        private static void DrawSharingLine(string label, string detail)
        {
            GUILayout.Label(label, Theme.RowLabel);
            GUILayout.Label(detail, Theme.MutedWrapped);
            GUILayout.Space(2f);
        }

        /// <summary>
        ///     What this character's rig offers, stated before Play. A blocked rig gets one named
        ///     blocker and what to do about it; an optional row states plainly which behavior is
        ///     unavailable, because a rig without shoulders is a legitimate rig and colouring that
        ///     as a fault would be a false alarm.
        /// </summary>
        private void DrawSetupCard()
        {
            if (_preflight.Checks == null || _preflight.Checks.Count == 0) return;

            Theme.BeginCard();
            Theme.SectionHeader(Glyphs.Validation, SetupSection);

            for (int i = 0; i < _preflight.Checks.Count; i++)
                DrawCheckRow(_preflight.Checks[i]);

            if (_preflight.TryGetBlocker(out BodyLanguageCheck blocker))
            {
                GUILayout.Space(4f);
                ErrorBox(
                    "This character will not move yet",
                    $"{blocker.Detail}.\n\nBody Language layers small rotations onto an animated " +
                    "skeleton, so it needs a Humanoid Avatar with the spine chain mapped. Set the " +
                    "model's Rig → Animation Type to Humanoid, or add a Character Rig and " +
                    "map its Spine to the character's spine bone.");
            }
            else if (!EditorApplication.isPlaying)
            {
                GUILayout.Space(2f);
                GUILayout.Label(
                    "This character will breathe, shift its weight and gesture as it talks when you " +
                    "press Play.",
                    Theme.MutedWrapped);
            }

            DrawSwitchedOff();

            Theme.EndCard();
        }

        /// <summary>
        ///     Behaviors this character's personality has switched off, and what each one costs.
        /// </summary>
        /// <remarks>
        ///     A rig that can move and a character that does not move look identical here, and the
        ///     usual reason is a toggle on the personality rather than anything wrong. The setup
        ///     service owns this list, so the assistant's diagnosis names the same switches in the
        ///     same words — and names them by the label this Inspector draws, which is the only name
        ///     a user can actually go and find.
        /// </remarks>
        private void DrawSwitchedOff()
        {
            ConvaiBodyLanguageProfile profile =
                BodyLanguageSetupService.ResolveEffectiveProfile((ConvaiBodyLanguageController)target);
            if (profile == null) return;

            IReadOnlyList<BodyLanguageSwitch> switches = BodyLanguageSetupService.SwitchesOf(profile);
            var off = new List<BodyLanguageSwitch>(switches.Count);
            for (int i = 0; i < switches.Count; i++)
                if (!switches[i].IsOn)
                    off.Add(switches[i]);

            if (off.Count == 0) return;

            GUILayout.Space(4f);
            GUILayout.Label("Switched off on this personality", Theme.RowLabel);
            for (int i = 0; i < off.Count; i++)
                GUILayout.Label($"{off[i].Label} — {off[i].ConsequenceWhenOff}.", Theme.MutedWrapped);
        }

        private void DrawCheckRow(BodyLanguageCheck check)
        {
            (Color tint, string word) = CheckStatus(check.State);
            Theme.CheckRow(tint, word, check.Label, check.Detail);

            string fixLabel = BodyLanguageSetupService.DescribeFix(check.Fix);
            if (fixLabel == null) return;

            FixButtonScratch.text = fixLabel;
            if (Theme.GhostButtonRow(FixButtonScratch) == 0)
                BodyLanguageSetupService.ApplyFix((ConvaiBodyLanguageController)target, check.Fix);
            GUILayout.Space(4f);
        }

        /// <summary>
        ///     A check's dot colour and status word. A fixable row is an offer and an optional one a
        ///     legitimate rig, so neither takes the warning tint.
        /// </summary>
        private static (Color tint, string word) CheckStatus(BodyLanguageCheckState state) => state switch
        {
            BodyLanguageCheckState.Ok => (Theme.StatusReady, null),
            BodyLanguageCheckState.Fixable => (Theme.StatusInfo, "Suggested"),
            BodyLanguageCheckState.Blocked => (Theme.StatusError, "Blocked"),
            _ => (Theme.StatusIdle, "Optional")
        };

        protected override void DrawLiveSection()
        {
            if (!_snapshotValid)
                return;

            DrawStatusCard();
            DrawPostureCard();
            DrawBreathCard();
            DrawTraceCard();

            // Smoothed policy values move every frame; without this the readout freezes on the last
            // event that happened to repaint the inspector.
            Repaint();
        }

        private void DrawStatusCard()
        {
            Theme.BeginCard();
            Theme.SectionHeader(Glyphs.Live, StatusSection);

            Theme.KeyValueRow(ModuleLabel, _snapshot.IsInert ? "Inert (see Console)" : "Active");
            Theme.KeyValueRow(DialogueStateLabel, ConvaiDialogueStateLabels.Name(_snapshot.DialogueState));
            Theme.KeyValueRow(ProfileNameLabel, _snapshot.ProfileName);
            Theme.KeyValueRow(RigLabel,
                $"spine {(_snapshot.HasSpine ? "ok" : "missing")}  chest {(_snapshot.HasChest ? "ok" : "-")}  " +
                $"upper chest {(_snapshot.HasUpperChest ? "ok" : "-")}  shoulders {(_snapshot.HasShoulders ? "ok" : "-")}");
            Theme.KeyValueRow(ProceduralLimbsLabel,
                $"arms {(_snapshot.HasProceduralArmChain ? "ok" : "unavailable")}  " +
                $"fingers {(_snapshot.HasProceduralFingerChain ? "ok" : "wrist-only")}");

            if (!_snapshot.HasProceduralArmChain)
                WarningBox(
                    "Procedural gestures unavailable",
                    "The procedural semantic gesture fallback requires a Humanoid Animator with bilateral " +
                    "upper arms, lower arms, and hands. Authored gestures remain unaffected.");

            if (!_snapshot.HasChest)
                InfoBox(
                    "Chest unresolved",
                    "Breathing and posture safely redistribute to Spine, but torso realism is reduced.");

            Theme.KeyValueRow(GesticulationLabel,
                _snapshot.ActivePolicy.GesticulationEnabled
                    ? $"on, intensity {_snapshot.ActivePolicy.GesticulationIntensity:0.00}"
                    : "off");
            Theme.KeyValueRow(ListeningPostureLabel,
                _snapshot.ActivePolicy.ListeningPostureEnabled
                    ? $"on, lean-in {_snapshot.ActivePolicy.ListeningLeanIn:0.00}"
                    : "off");
            Theme.KeyValueRow(PostureLabel,
                $"openness {_snapshot.ActivePolicy.PostureOpennessBias:+0.00;-0.00}  " +
                $"lean {_snapshot.ActivePolicy.SagittalLeanBias:+0.00;-0.00}  " +
                $"drift {_snapshot.ActivePolicy.AmbientDrift:0.00}");
            Theme.KeyValueRow(BreathingLabel,
                $"{_snapshot.ActivePolicy.BreathRateCpm:0.0} cpm  depth {_snapshot.ActivePolicy.BreathDepth:0.00}  " +
                $"irregularity {_snapshot.ActivePolicy.BreathIrregularity:0.00}");
            Theme.KeyValueRow(FidgetsLabel,
                _snapshot.ActivePolicy.FidgetsEnabled
                    ? $"on, rate {_snapshot.ActivePolicy.FidgetRate:0.00}"
                    : "off");

            Theme.KeyValueRow(BodySharedWithLabel, DescribeLiveSharing());
            Theme.KeyValueRow(HeadMovedByLabel,
                _snapshot.HeadGestureConsumerCount > 0
                    ? "Gaze"
                    : "Body Language itself");

            Theme.EndCard();
        }

        /// <summary>
        ///     What another module is taking from this character's body at this instant — the live
        ///     counterpart to the Sharing This Body card, which describes what each peer *can* take.
        ///     Stated as behaviour rather than as the suppression level's name, because the name is
        ///     the one thing a user cannot act on.
        /// </summary>
        private string DescribeLiveSharing()
        {
            string state = _snapshot.GesticulationSuppression switch
            {
                GestureSuppression.FullBody =>
                    "another module is using the whole body — posture and breathing have faded out",
                GestureSuppression.UpperBody =>
                    "another module is using the upper body — posture and gesticulation are reduced, " +
                    "head-beats and breathing continue",
                _ => "nothing is reducing this character's motion"
            };

            return _snapshot.UsingMotionBudget
                ? $"{state} (upper body {_snapshot.UpperBodyOccupancy:P0} occupied)"
                : state;
        }

        private void DrawPostureCard()
        {
            Theme.BeginCard();
            Theme.SectionHeader(Glyphs.Motion, PostureSection);

            Theme.KeyValueRow(OpennessLabel,
                $"{_snapshot.PostureOpennessTarget:+0.00;-0.00} → {_snapshot.PostureOpennessCurrent:+0.00;-0.00}");
            Theme.KeyValueRow(LeanLabel,
                $"{_snapshot.PostureLeanTarget:+0.00;-0.00} → {_snapshot.PostureLeanCurrent:+0.00;-0.00}");
            Theme.KeyValueRow(TensionLabel,
                $"{_snapshot.PostureTensionTarget:+0.00;-0.00} → {_snapshot.PostureTensionCurrent:+0.00;-0.00}");
            Theme.KeyValueRow(MasterWeightLabel, $"{_snapshot.MasterWeight:0.00}");
            Theme.KeyValueRow(ProceduralGestureLabel,
                _snapshot.ProceduralGestureFallbackActive ? "active (performer fallback)" : "inactive");

            Theme.EndCard();
        }

        private void DrawBreathCard()
        {
            Theme.BeginCard();
            Theme.SectionHeader(Glyphs.Motion, BreathSection);

            Theme.KeyValueRow(PhaseWaveformLabel,
                $"{_snapshot.BreathPhase:0.00} rad / {_snapshot.BreathWaveform:+0.00;-0.00}");
            Theme.KeyValueRow(RateDepthLabel,
                $"{_snapshot.BreathRateCpm:0.0} cpm / {_snapshot.BreathDepth:0.00}");

            Theme.EndCard();
        }

        private void DrawTraceCard()
        {
            if (_snapshot.RecentTrace.Count == 0)
                return;

            Theme.BeginCard();
            Theme.SectionHeader(Glyphs.Validation, TraceSection);

            int first = Mathf.Max(0, _snapshot.RecentTrace.Count - TraceTailLength);
            for (int i = first; i < _snapshot.RecentTrace.Count; i++)
            {
                BodyLanguageTraceEntry entry = _snapshot.RecentTrace[i];
                GUILayout.Label($"[{entry.Time:0.0}s] {entry.Message}", Theme.MicroLabel);
            }

            Theme.EndCard();
        }
    }
}
