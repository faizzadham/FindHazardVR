using System.Collections.Generic;
using Convai.Runtime.Actions;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Actions
{
    /// <summary>
    ///     EditMode coverage for the one ladder that answers "what is this command's target".
    /// </summary>
    /// <remarks>
    ///     The reason this type exists is that the answer used to be computed twice — once to decide
    ///     whether to admit the command, once to decide what to hand the executor — and the two
    ///     copies disagreed in three ways. These tests pin the agreement, not just the answer:
    ///     a gate that reaches a different conclusion from the thing it gates re-introduces itself
    ///     the moment anyone edits one copy.
    /// </remarks>
    [TestFixture]
    public sealed class ConvaiActionTargetResolutionTests
    {
        [OneTimeSetUp]
        public void OneTimeSetUp() => Convai.Runtime.Logging.ConvaiLogger.Initialize();

        private readonly List<GameObject> _spawned = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _spawned.Count; i++)
                if (_spawned[i] != null)
                    Object.DestroyImmediate(_spawned[i]);

            _spawned.Clear();
        }

        private GameObject Spawn(string name, Vector3 position)
        {
            var go = new GameObject(name);
            go.transform.position = position;
            _spawned.Add(go);
            return go;
        }

        private static ConvaiActionDefinition WalkTo() => new()
        {
            ActionName = "Walk To",
            Description = "Walk over to a place.",
            TargetRequirement = ConvaiActionTargetRequirement.Either
        };

        // ── The divergence this type was created to make impossible ──────────────────────

        /// <summary>
        ///     Two targets share a name and stand in different places. The filter used to judge the
        ///     command with no origin — keeping the first entry it met — while the dispatcher passed
        ///     the character's position and walked to the nearest. So a command could be admitted on
        ///     the strength of one target and performed on another, silently, and only in the scenes
        ///     where duplicate names exist.
        /// </summary>
        [Test]
        public void SameNamedTargets_ResolveIdenticallyForAdmissionAndForExecution()
        {
            GameObject far = Spawn("resolution-tests-far", new Vector3(50f, 0f, 0f));
            GameObject near = Spawn("resolution-tests-near", new Vector3(1f, 0f, 0f));

            var config = new ConvaiActionConfig
            {
                Objects = new List<ConvaiActionObjectDefinition>
                {
                    // Authored first on purpose: first-wins and nearest-wins pick differently here.
                    new() { Name = "Solar Panel", Description = "The far one.", GameObjectReference = far },
                    new() { Name = "Solar Panel", Description = "The near one.", GameObjectReference = near }
                }
            };
            var command = new ConvaiActionCommand("Walk To", "Solar Panel");
            Vector3 origin = Vector3.zero;

            bool admitted = ConvaiActionTargetResolution.TryResolve(
                command, WalkTo(), config, origin, out ConvaiResolvedActionTarget admissionTarget);
            bool executed = ConvaiActionTargetResolution.TryResolve(
                command, WalkTo(), config, origin, out ConvaiResolvedActionTarget executionTarget);

            Assert.That(admitted, Is.True);
            Assert.That(executed, Is.True);
            Assert.That(admissionTarget.GameObjectReference, Is.SameAs(executionTarget.GameObjectReference),
                "Admission and execution must land on the same object, or a command is judged on " +
                "one target and performed on another.");
            Assert.That(admissionTarget.GameObjectReference, Is.SameAs(near),
                "With an origin the nearest candidate wins.");
        }

        /// <summary>
        ///     A name that matches an entry with nothing behind it in the scene resolves — that is
        ///     what the command meant — but must not be admitted. An executor handed one has no
        ///     transform to walk to and can only decline after the fact, for a reason that reads
        ///     like a fault in the behavior rather than in the authoring.
        /// </summary>
        [Test]
        public void EntryWithNothingBehindItInTheScene_ResolvesButIsNotAdmitted()
        {
            var config = new ConvaiActionConfig
            {
                Objects = new List<ConvaiActionObjectDefinition>
                {
                    new() { Name = "The Gallery", Description = "Talked about, never built." }
                }
            };

            Assert.That(
                ConvaiActionTargetResolution.TryResolve(
                    new ConvaiActionCommand("Walk To", "The Gallery"), WalkTo(), config, Vector3.zero,
                    out ConvaiResolvedActionTarget meaning),
                Is.True,
                "The command did name something, and callers that only need the name must still get it.");
            Assert.That(meaning, Is.Not.Null);
            Assert.That(ConvaiActionTargetResolution.IsActionable(meaning), Is.False);

            Assert.That(
                ConvaiActionTargetResolution.TryResolveActionable(
                    new ConvaiActionCommand("Walk To", "The Gallery"), WalkTo(), config, Vector3.zero,
                    out ConvaiResolvedActionTarget admitted),
                Is.False,
                "Nothing can be performed on it, so the command must not be admitted.");
            Assert.That(admitted, Is.Null);
        }

        /// <summary>
        ///     …and it must not shadow the real one either. Authored first, unbound, same name: a
        ///     ladder that stopped at the first match would resolve to nothing while the object the
        ///     player asked about stood right there.
        /// </summary>
        [Test]
        public void UnboundEntry_DoesNotShadowABoundOneWithTheSameName()
        {
            GameObject real = Spawn("resolution-tests-real", new Vector3(3f, 0f, 0f));
            var config = new ConvaiActionConfig
            {
                Objects = new List<ConvaiActionObjectDefinition>
                {
                    new() { Name = "The Gallery", Description = "Text only, authored first." },
                    new() { Name = "The Gallery", Description = "The actual room.", GameObjectReference = real }
                }
            };

            bool resolved = ConvaiActionTargetResolution.TryResolve(
                new ConvaiActionCommand("Walk To", "The Gallery"),
                WalkTo(),
                config,
                Vector3.zero,
                out ConvaiResolvedActionTarget target);

            Assert.That(resolved, Is.True);
            Assert.That(target.GameObjectReference, Is.SameAs(real));
        }

        // ── Candidate order ──────────────────────────────────────────────────────────────

        /// <summary>
        ///     The ordinary shape of "walk to somewhere": no target field, no declared parameters,
        ///     the name arriving inside the action text. Every such command was once dropped as
        ///     target-less while holding a perfectly good target.
        /// </summary>
        [Test]
        public void TargetSentInsideTheActionText_Resolves()
        {
            GameObject gallery = Spawn("resolution-tests-gallery", new Vector3(2f, 0f, 0f));
            var config = new ConvaiActionConfig
            {
                Objects = new List<ConvaiActionObjectDefinition>
                {
                    new() { Name = "The Gallery", Description = "The east room.", GameObjectReference = gallery }
                }
            };
            var definitions = new List<ConvaiActionDefinition> { WalkTo() };

            ConvaiActionCommand enriched = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Walk To The Gallery"), config, definitions);

            bool resolved = ConvaiActionTargetResolution.TryResolve(
                enriched, definitions[0], config, Vector3.zero, out ConvaiResolvedActionTarget target);

            Assert.That(resolved, Is.True);
            Assert.That(target.GameObjectReference, Is.SameAs(gallery));
        }

        /// <summary>
        ///     An action that requires nothing never fails for want of a target, but still takes one
        ///     when the backend named it — a wave at somebody is better aimed than a wave at nobody.
        /// </summary>
        [Test]
        public void TargetlessAction_SucceedsWithNothingAndStillTakesAnExplicitTarget()
        {
            GameObject visitor = Spawn("resolution-tests-visitor", new Vector3(4f, 0f, 0f));
            var config = new ConvaiActionConfig
            {
                Characters = new List<ConvaiActionCharacterDefinition>
                {
                    new() { Name = "The Visitor", Bio = "A guest.", GameObjectReference = visitor }
                }
            };
            var wave = new ConvaiActionDefinition
            {
                ActionName = "Wave",
                Description = "Wave.",
                TargetRequirement = ConvaiActionTargetRequirement.None
            };

            Assert.That(
                ConvaiActionTargetResolution.TryResolve(
                    new ConvaiActionCommand("Wave"), wave, config, Vector3.zero, out ConvaiResolvedActionTarget none),
                Is.True,
                "An action that requires nothing must never be blocked for want of a target.");
            Assert.That(none, Is.Null);

            ConvaiActionTargetResolution.TryResolve(
                new ConvaiActionCommand("Wave", "The Visitor"), wave, config, Vector3.zero,
                out ConvaiResolvedActionTarget aimed);
            Assert.That(aimed, Is.Not.Null);
            Assert.That(aimed.GameObjectReference, Is.SameAs(visitor));
        }

        /// <summary>
        ///     The requirement is honoured: an action that needs a person does not settle for a prop
        ///     of the same name.
        /// </summary>
        [Test]
        public void RequirementIsHonoured_AnObjectDoesNotSatisfyACharacterRequirement()
        {
            GameObject statue = Spawn("resolution-tests-statue", new Vector3(2f, 0f, 0f));
            var config = new ConvaiActionConfig
            {
                Objects = new List<ConvaiActionObjectDefinition>
                {
                    new() { Name = "Sofia", Description = "A statue of her.", GameObjectReference = statue }
                }
            };
            var greet = new ConvaiActionDefinition
            {
                ActionName = "Greet",
                Description = "Greet someone.",
                TargetRequirement = ConvaiActionTargetRequirement.Character
            };

            bool resolved = ConvaiActionTargetResolution.TryResolve(
                new ConvaiActionCommand("Greet", "Sofia"), greet, config, Vector3.zero,
                out ConvaiResolvedActionTarget target);

            Assert.That(resolved, Is.False, "A prop does not satisfy an action that needs a person.");
            Assert.That(target, Is.Not.Null,
                "The near miss is kept so a caller can say 'you asked for a person and Sofia is a " +
                "statue' rather than the far less useful 'nothing resolved'.");
            Assert.That(target.Kind, Is.EqualTo(ConvaiActionTargetKind.Object));
        }

        // ── Wire cleanup stays on the wire ───────────────────────────────────────────────

        /// <summary>
        ///     The repairs that make a model's output usable must not touch what an author typed. A
        ///     target legitimately named with a leading dash or wrapped in quotes has to stay
        ///     callable by its own name — when this cleanup lived in the shared normalizer it ran
        ///     over authored names too, and over every Clone and ToString on the way past.
        /// </summary>
        [Test]
        public void AuthoredNamesAreNotRewrittenByTheCleanupMeantForTheWire()
        {
            GameObject dash = Spawn("resolution-tests-dash", new Vector3(1f, 0f, 0f));
            GameObject quoted = Spawn("resolution-tests-quoted", new Vector3(2f, 0f, 0f));

            var config = new ConvaiActionConfig
            {
                Objects = new List<ConvaiActionObjectDefinition>
                {
                    new() { Name = "- Special", Description = "Named with a dash.", GameObjectReference = dash },
                    new() { Name = "\"Q\"", Description = "Named with quotes.", GameObjectReference = quoted }
                }
            };

            Assert.That(
                ConvaiResolvedActionTarget.Resolve("- Special", config, ConvaiActionTargetRequirement.Object)
                    ?.GameObjectReference,
                Is.SameAs(dash),
                "An authored name keeps its leading separator and must stay callable by it.");
            Assert.That(
                ConvaiResolvedActionTarget.Resolve("\"Q\"", config, ConvaiActionTargetRequirement.Object)
                    ?.GameObjectReference,
                Is.SameAs(quoted),
                "An authored name keeps its quotes.");
        }

        /// <summary>
        ///     An alias typed with a trailing space is invisible in the inspector and used to match
        ///     nothing at all — a whole alias silently doing no work, with no way to see why.
        /// </summary>
        [Test]
        public void AliasWithStraySpacing_StillMatches()
        {
            GameObject lantern = Spawn("resolution-tests-lantern", new Vector3(1f, 0f, 0f));
            var config = new ConvaiActionConfig
            {
                Objects = new List<ConvaiActionObjectDefinition>
                {
                    new()
                    {
                        Name = "Brass Lantern",
                        Description = "Hanging by the door.",
                        GameObjectReference = lantern,
                        Aliases = new List<string> { " lamp " }
                    }
                }
            };

            Assert.That(
                ConvaiResolvedActionTarget.Resolve("lamp", config, ConvaiActionTargetRequirement.Object)
                    ?.GameObjectReference,
                Is.SameAs(lantern));
        }
    }
}
