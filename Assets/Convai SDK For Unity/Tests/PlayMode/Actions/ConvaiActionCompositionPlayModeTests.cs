using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Convai.Runtime.Actions;
using Convai.Runtime.Components;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Convai.Tests.PlayMode.Actions
{
    /// <summary>
    ///     How a character's action targets are assembled from four sources at once.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This cannot be an EditMode test, and pretending otherwise is how the gap persisted.</b>
    ///         A <c>ConvaiActionTarget</c> puts itself into <c>ActiveTargets</c> from <c>OnEnable</c>,
    ///         and EditMode never runs <c>OnEnable</c> on a component without <c>[ExecuteAlways]</c>.
    ///         Every cross-source question — which source owns a contested name, whether a component
    ///         completes a blank authored entry, whether two sources produce one merged entry or two —
    ///         is therefore invisible there. The declared merge table is guarded in EditMode by
    ///         reflection; whether the code obeys it is guarded here.
    ///     </para>
    ///     <para>
    ///         The two properties under test pull against each other, which is why both are here.
    ///         A later source must not take a name an earlier source owns — that is the duplicate-key
    ///         defect, where two entries under one name left the choice to scene geometry. But several
    ///         entries <em>within</em> one source sharing a name is a feature, not a collision:
    ///         registering three chairs as "chair" and walking to the nearest is the point of the
    ///         registry. A fix for the first that also collapsed the second shipped once already.
    ///     </para>
    /// </remarks>
    public sealed class ConvaiActionCompositionPlayModeTests
    {
        private readonly ConvaiActionPlayModeScene _scene = new();

        [TearDown]
        public void TearDown() => _scene.Dispose();

        private static int CountNamed(IReadOnlyList<ConvaiActionObjectDefinition> entries, string name) =>
            entries?.Count(e => e != null &&
                                string.Equals(e.Name?.Trim(), name, System.StringComparison.OrdinalIgnoreCase)) ?? 0;

        // ── Across sources: one name, one entry ─────────────────────────────────────────

        /// <summary>
        ///     A scene component does not add a second entry under a name the base config owns.
        /// </summary>
        /// <remarks>
        ///     The live defect §4.4 records. Writing a name in Scene Knowledge <em>and</em> putting a
        ///     Convai Action Target of that name on the object it describes is the obvious way to set
        ///     one up, and it used to produce two entries — after which which one a command meant was
        ///     decided by where things happened to be standing.
        /// </remarks>
        [UnityTest]
        public IEnumerator AComponentDoesNotDuplicateANameTheBaseConfigAlreadyOwns()
        {
            ConvaiCharacter character = _scene.Character(
                new ConvaiActionObjectDefinition { Name = "The Gallery" });

            GameObject gallery = _scene.At("gallery-object", new Vector3(5f, 0f, 0f));
            ConvaiActionTarget target = gallery.AddComponent<ConvaiActionTarget>();

            // Named explicitly, and the object deliberately called something else. `TargetName`
            // falls back to the GameObject's name when blank, so a component left unnamed on an
            // object called "gallery-object" contests nothing — and this test would then pass by
            // counting the one authored entry, having never set up the collision it describes.
            target.TargetName = "The Gallery";
            target.enabled = true;
            yield return null;

            ConvaiActionConfig merged = character.GetRuntimeActionConfig();

            Assert.That(
                CountNamed(merged.Objects, "The Gallery"),
                Is.EqualTo(1),
                "Two entries under one name is the duplicate-key defect: the resolution ladder then "
                + "picks between them by scene geometry rather than by anything the author decided.");
        }

        /// <summary>
        ///     A component completes the authored entry it shares a name with.
        /// </summary>
        /// <remarks>
        ///     The other half of the rule above. Collapsing to one entry is only correct if the
        ///     surviving entry gains what the component brought — otherwise the name resolves to an
        ///     entry with nothing behind it while the real object stands right there, which is the
        ///     failure the collapse was supposed to prevent.
        /// </remarks>
        [UnityTest]
        public IEnumerator AComponentCompletesTheAuthoredEntryRatherThanBeingDiscarded()
        {
            ConvaiCharacter character = _scene.Character(
                new ConvaiActionObjectDefinition { Name = "The Gallery" });

            GameObject gallery = _scene.At("gallery-object", new Vector3(5f, 0f, 0f));
            ConvaiActionTarget target = gallery.AddComponent<ConvaiActionTarget>();
            target.TargetName = "The Gallery";
            target.enabled = true;
            yield return null;

            ConvaiActionConfig merged = character.GetRuntimeActionConfig();
            ConvaiActionObjectDefinition entry = merged.Objects
                .First(o => string.Equals(o.Name?.Trim(), "The Gallery", System.StringComparison.OrdinalIgnoreCase));

            Assert.That(
                entry.GameObjectReference,
                Is.SameAs(gallery),
                "The authored entry had no object of its own; the component's is what makes the name "
                + "actionable, and dropping it would leave the action unable to run on a target it has.");
        }

        // ── Within one source: several entries may share a name ─────────────────────────

        /// <summary>
        ///     Registering the same name twice keeps both, so the ladder can pick the nearer.
        /// </summary>
        /// <remarks>
        ///     Three chairs called "chair" is the registry working as intended. A fix for the
        ///     cross-source duplicate shipped once with this collapsed to one entry, and the only
        ///     thing that caught it was a test asserting the count before an unrelated unregister.
        /// </remarks>
        [UnityTest]
        public IEnumerator RegisteringTwoTargetsUnderOneNameKeepsBoth()
        {
            ConvaiCharacter character = _scene.Character();
            yield return null;

            character.Actions.RegisterObject("chair", "the near one");
            character.Actions.RegisterObject("chair", "the far one");

            ConvaiActionConfig merged = character.GetRuntimeActionConfig();

            Assert.That(
                CountNamed(merged.Objects, "chair"),
                Is.EqualTo(2),
                "Several things can share a name within one source. Proximity is how the ladder "
                + "tells them apart, and collapsing them takes that choice away.");
        }

        /// <summary>
        ///     Two scene components of the same name are likewise both kept.
        /// </summary>
        [UnityTest]
        public IEnumerator TwoComponentsUnderOneNameAreBothKept()
        {
            ConvaiCharacter character = _scene.Character();

            GameObject near = _scene.At("door-near", new Vector3(2f, 0f, 0f));
            GameObject far = _scene.At("door-far", new Vector3(20f, 0f, 0f));

            // Both under one name, which is the claim. Left blank they would take their objects'
            // names and be two differently-named targets — two entries either way, and nothing
            // about sharing a name proved.
            ConvaiActionTarget nearTarget = near.AddComponent<ConvaiActionTarget>();
            ConvaiActionTarget farTarget = far.AddComponent<ConvaiActionTarget>();
            nearTarget.TargetName = "Door";
            farTarget.TargetName = "Door";
            nearTarget.enabled = true;
            farTarget.enabled = true;
            yield return null;

            ConvaiActionConfig merged = character.GetRuntimeActionConfig();
            int doors = merged.Objects.Count(o => o?.GameObjectReference == near || o?.GameObjectReference == far);

            Assert.That(
                CountNamed(merged.Objects, "Door"),
                Is.EqualTo(2),
                "Both are called 'Door' and both are real; collapsing them by name would take the "
                + "ladder's proximity choice away.");

            Assert.That(
                doors,
                Is.EqualTo(2),
                "Two doors are two doors, whatever they are called. Only a later source may not take "
                + "an earlier source's name.");
        }

        // ── Grounding the backend's prompt ──────────────────────────────────────────────

        /// <summary>
        ///     A scene component makes the character stage a sync, so the model is told it exists.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Resolving a target locally is only half of what a target is for. If the model was
        ///         never told the thing exists it will not ask for it, and the character answers
        ///         <em>"that is not in our environment"</em> about an object standing in front of it —
        ///         measured, in the Terminal scene, against the live backend.
        ///     </para>
        ///     <para>
        ///         The sync used to be staged only when the runtime <em>registry</em> was non-empty,
        ///         and a <c>ConvaiActionTarget</c> deliberately never joins that registry. So this is
        ///         the case that was silently missing, and it is the one this test pins.
        ///     </para>
        /// </remarks>
        [UnityTest]
        public IEnumerator AComponentOnlyTargetMakesTheCharacterStageABackendSync()
        {
            ConvaiCharacter character = _scene.Character();
            yield return null;

            Assert.That(
                character.HasTargetsTheConnectPayloadDidNotCarry(),
                Is.False,
                "Sanity: with nothing but the authored config, there is nothing to tell the backend.");

            GameObject prop = _scene.At("component-only-prop", new Vector3(3f, 0f, 0f));
            prop.AddComponent<ConvaiActionTarget>().enabled = true;
            yield return null;

            Assert.That(
                character.HasTargetsTheConnectPayloadDidNotCarry(),
                Is.True,
                "A Convai Action Target in the scene is something the character can act on and the "
                + "model has never heard of. Without staging a sync it stays invisible, and the "
                + "component's own documentation promises the opposite.");
        }

        /// <summary>A runtime registration stages one too — the case that always worked.</summary>
        /// <remarks>
        ///     Kept beside the component case deliberately. The fix replaced a registry-shaped
        ///     question with a broader one, and the risk in that kind of change is losing the
        ///     narrower case it used to cover.
        /// </remarks>
        [UnityTest]
        public IEnumerator ARuntimeRegistrationStillStagesABackendSync()
        {
            ConvaiCharacter character = _scene.Character();
            yield return null;

            character.Actions.RegisterObject("Spawned Crate", "A crate that appeared mid-session.");
            yield return null;

            Assert.That(character.HasTargetsTheConnectPayloadDidNotCarry(), Is.True);
        }

        // ── Order independence (G3) ─────────────────────────────────────────────────────

        /// <summary>
        ///     Which entry owns a contested name does not depend on the order entries were added.
        /// </summary>
        /// <remarks>
        ///     "First wins" has to mean the declared source precedence, not the order a list happened
        ///     to be traversed in. Registering in either order must give the base config the name
        ///     both times.
        /// </remarks>
        [UnityTest]
        public IEnumerator TheBaseConfigOwnsAContestedNameWhicheverOrderTheRegistryAddsIn()
        {
            foreach (bool reversed in new[] { false, true })
            {
                ConvaiCharacter character = _scene.Character(
                    new ConvaiActionObjectDefinition { Name = "Lantern", Description = "authored" });
                yield return null;

                if (reversed)
                {
                    character.Actions.RegisterObject("Other", "unrelated");
                    character.Actions.RegisterObject("Lantern", "registered");
                }
                else
                {
                    character.Actions.RegisterObject("Lantern", "registered");
                    character.Actions.RegisterObject("Other", "unrelated");
                }

                ConvaiActionConfig merged = character.GetRuntimeActionConfig();

                Assert.That(
                    CountNamed(merged.Objects, "Lantern"),
                    Is.EqualTo(1),
                    $"reversed={reversed}: the registry must not take a name the base config owns.");

                Assert.That(
                    merged.Objects.First(o => o.Name == "Lantern").Description,
                    Is.EqualTo("authored"),
                    $"reversed={reversed}: the earlier source's wording wins, both ways round.");

                _scene.Dispose();
            }
        }
    }
}
