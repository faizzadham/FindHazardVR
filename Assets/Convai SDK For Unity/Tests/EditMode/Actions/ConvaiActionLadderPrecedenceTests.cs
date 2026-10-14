using System.Collections.Generic;
using Convai.Runtime.Actions;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Actions
{
    /// <summary>
    ///     Pins down which of several things a name means.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The ladder now walks rung by rung across both kinds instead of finishing one kind
    ///         before starting the other. That fixes one thing — a fuzzy match of one kind no longer
    ///         beats an exact match of the other — and the whole risk of the change is that it
    ///         quietly fixes more than one thing. Most of the tests below therefore assert that
    ///         something did <em>not</em> change.
    ///     </para>
    /// </remarks>
    [TestFixture]
    public sealed class ConvaiActionLadderPrecedenceTests
    {
        [OneTimeSetUp]
        public void OneTimeSetUp() => Convai.Runtime.Logging.ConvaiLogger.Initialize();

        private readonly List<GameObject> _spawned = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _spawned.Count; i++)
            {
                if (_spawned[i] != null)
                    Object.DestroyImmediate(_spawned[i]);
            }

            _spawned.Clear();
        }

        private GameObject At(string name, Vector3 position)
        {
            var go = new GameObject(name);
            go.transform.position = position;
            _spawned.Add(go);
            return go;
        }

        private ConvaiActionObjectDefinition Obj(
            string name, Vector3? at = null, params string[] aliases) =>
            new()
            {
                Name = name,
                GameObjectReference = at.HasValue ? At(name, at.Value) : null,
                Aliases = new List<string>(aliases ?? System.Array.Empty<string>())
            };

        private ConvaiActionCharacterDefinition Chr(
            string name, Vector3? at = null, params string[] aliases) =>
            new()
            {
                Name = name,
                GameObjectReference = at.HasValue ? At(name, at.Value) : null,
                Aliases = new List<string>(aliases ?? System.Array.Empty<string>())
            };

        private static ConvaiActionConfig Config(
            IEnumerable<ConvaiActionObjectDefinition> objects = null,
            IEnumerable<ConvaiActionCharacterDefinition> characters = null) =>
            new()
            {
                Objects = objects == null
                    ? new List<ConvaiActionObjectDefinition>()
                    : new List<ConvaiActionObjectDefinition>(objects),
                Characters = characters == null
                    ? new List<ConvaiActionCharacterDefinition>()
                    : new List<ConvaiActionCharacterDefinition>(characters)
            };

        private static ConvaiResolvedActionTarget Resolve(
            string query,
            ConvaiActionConfig config,
            ConvaiActionTargetRequirement requirement = ConvaiActionTargetRequirement.Either,
            Vector3? origin = null)
        {
            var command = new ConvaiActionCommand("Act", query);
            var definition = new ConvaiActionDefinition
            {
                ActionName = "Act",
                TargetRequirement = requirement
            };

            ConvaiActionTargetResolution.TryResolve(command, definition, config, origin, out ConvaiResolvedActionTarget target);
            return target;
        }

        // ── The defect (C-4) ─────────────────────────────────────────────────────────────

        /// <summary>
        ///     A statue called "Sofia's Statue" used to answer to the name of the person standing
        ///     next to it.
        /// </summary>
        /// <remarks>
        ///     The object ladder ran to completion first, so its fourth rung — a loose
        ///     contains match — was reached before the character ladder's first. And it was logged as
        ///     a successful match, so from outside it read as the system working.
        /// </remarks>
        [Test]
        public void ExactNameBeatsAContainsMatchOfTheOtherKind()
        {
            ConvaiActionConfig config = Config(
                objects: new[] { Obj("Sofia's Statue", Vector3.zero) },
                characters: new[] { Chr("Sofia", Vector3.one) });

            ConvaiResolvedActionTarget resolved = Resolve("Sofia", config);

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved.Kind, Is.EqualTo(ConvaiActionTargetKind.Character));
            Assert.That(resolved.Name, Is.EqualTo("Sofia"));
        }

        [Test]
        public void AliasBeatsAContainsMatchOfTheOtherKind()
        {
            ConvaiActionConfig config = Config(
                objects: new[] { Obj("Reception Desk Panel", Vector3.zero) },
                characters: new[] { Chr("Mira", Vector3.one, "Reception Desk") });

            ConvaiResolvedActionTarget resolved = Resolve("Reception Desk", config);

            Assert.That(resolved.Kind, Is.EqualTo(ConvaiActionTargetKind.Character),
                "An alias is an exact match on an alternate name; contains is a guess.");
        }

        // ── What must NOT change ─────────────────────────────────────────────────────────

        /// <summary>
        ///     Proximity must never decide between an object and a character.
        /// </summary>
        /// <remarks>
        ///     The tempting version of the fix is "at each rung, nearest wins". It would have flipped
        ///     this case — which works today and is not the defect — so that a character standing
        ///     closer than a same-named object would start winning, silently, depending on where the
        ///     Convai Character happens to be standing. Distance only ever separates two entries of
        ///     the same kind.
        /// </remarks>
        [Test]
        public void ANearerCharacterDoesNotTakeANameFromAFartherObjectOfTheSameName()
        {
            ConvaiActionConfig config = Config(
                objects: new[] { Obj("Sofia", new Vector3(10f, 0f, 0f)) },
                characters: new[] { Chr("Sofia", new Vector3(1f, 0f, 0f)) });

            ConvaiResolvedActionTarget resolved = Resolve(
                "Sofia", config, ConvaiActionTargetRequirement.Either, origin: Vector3.zero);

            Assert.That(resolved.Kind, Is.EqualTo(ConvaiActionTargetKind.Object),
                "Objects are considered before characters when the action accepts either — as before.");
        }

        [Test]
        public void ObjectsAreStillConsideredBeforeCharactersWithNoOriginAtAll()
        {
            ConvaiActionConfig config = Config(
                objects: new[] { Obj("Ada", Vector3.zero) },
                characters: new[] { Chr("Ada", Vector3.zero) });

            Assert.That(Resolve("Ada", config).Kind, Is.EqualTo(ConvaiActionTargetKind.Object));
        }

        /// <summary>Within one kind, the nearest still wins — that part is untouched.</summary>
        [Test]
        public void TheNearestOfTwoSameNamedObjectsStillWins()
        {
            var far = Obj("Door", new Vector3(20f, 0f, 0f));
            var near = Obj("Door", new Vector3(2f, 0f, 0f));
            ConvaiActionConfig config = Config(objects: new[] { far, near });

            ConvaiResolvedActionTarget resolved = Resolve(
                "Door", config, ConvaiActionTargetRequirement.Object, origin: Vector3.zero);

            Assert.That(resolved.GameObjectReference, Is.SameAs(near.GameObjectReference));
        }

        /// <summary>An entry with something behind it still beats one with nothing.</summary>
        [Test]
        public void ABoundEntryStillBeatsAnUnboundOneOfTheSameName()
        {
            var textOnly = Obj("Gallery");
            var real = Obj("Gallery", new Vector3(5f, 0f, 0f));
            ConvaiActionConfig config = Config(objects: new[] { textOnly, real });

            ConvaiResolvedActionTarget resolved = Resolve("Gallery", config);

            Assert.That(resolved.GameObjectReference, Is.SameAs(real.GameObjectReference),
                "Writing the name in Scene Knowledge and putting a target on the object is the obvious "
                + "setup; the entry with nothing behind it must not win it.");
        }

        // ── Requirement and the near miss ────────────────────────────────────────────────

        /// <summary>
        ///     A wrong-kind match must not end the search.
        /// </summary>
        /// <remarks>
        ///     An action that needs an object, a character matching exactly, and an object matching
        ///     only loosely: the object is what the action asked for, so the ladder has to keep going
        ///     past the character to find it.
        /// </remarks>
        [Test]
        public void AWrongKindExactMatchDoesNotStopTheSearchForTheRightKind()
        {
            ConvaiActionConfig config = Config(
                objects: new[] { Obj("Archive Terminal", Vector3.zero) },
                characters: new[] { Chr("Archive", Vector3.one) });

            ConvaiResolvedActionTarget resolved = Resolve(
                "Archive", config, ConvaiActionTargetRequirement.Object);

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved.Kind, Is.EqualTo(ConvaiActionTargetKind.Object));
            Assert.That(resolved.Name, Is.EqualTo("Archive Terminal"));
        }

        /// <summary>
        ///     When only the wrong kind matches, it still comes back — as the near miss.
        /// </summary>
        /// <remarks>
        ///     Throwing it away would cost the one message worth sending here: <em>"you asked for a
        ///     person and Sofia is a statue"</em>. <c>TryResolve</c> reports false; the candidate is
        ///     still handed over so the caller can say which.
        /// </remarks>
        [Test]
        public void TheWrongKindComesBackAsTheNearMissWhenNothingRightMatches()
        {
            ConvaiActionConfig config = Config(objects: new[] { Obj("Sofia", Vector3.zero) });

            var command = new ConvaiActionCommand("Greet", "Sofia");
            var definition = new ConvaiActionDefinition
            {
                ActionName = "Greet",
                TargetRequirement = ConvaiActionTargetRequirement.Character
            };

            bool satisfied = ConvaiActionTargetResolution.TryResolve(
                command, definition, config, null, out ConvaiResolvedActionTarget target);

            Assert.That(satisfied, Is.False, "A statue is not a person.");
            Assert.That(target, Is.Not.Null, "But the caller needs to be able to say what it found.");
            Assert.That(target.Kind, Is.EqualTo(ConvaiActionTargetKind.Object));
        }

        // ── A named kind is a constraint, not a preference ───────────────────────────────

        /// <summary>
        ///     A reference already known to be a character must not come back as an object.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Two overloads reach this ladder and they have never meant the same thing. An
        ///         action's <c>TargetRequirement</c> is a preference — when it matches nothing the
        ///         other kind comes back as the near miss, and the caller checks. A
        ///         <c>ConvaiActionTargetKind</c> is a constraint: the only caller passing one is
        ///         re-resolving a parameter whose kind an earlier read already determined, and it
        ///         hands the answer on to be checked against the <em>action's</em> requirement
        ///         instead of against the kind.
        ///     </para>
        ///     <para>
        ///         So on an <c>Either</c> action the near miss would have been accepted: asked again
        ///         for the character Sofia, in a scene where only the statue is left, the ladder
        ///         would have offered the statue and every check downstream would have passed it.
        ///         That is a silently wrong target — the exact class this ladder was rewritten to
        ///         close — and the rewrite introduced it by folding the two overloads onto one path.
        ///     </para>
        ///     <para>
        ///         Revert <c>KindIsAConstraint</c> to <c>KindIsAPreference</c> in
        ///         <c>ConvaiActionResolution</c> and this test goes red.
        ///     </para>
        /// </remarks>
        [Test]
        public void AKindedReferenceDoesNotFallBackToTheOtherKind()
        {
            ConvaiActionConfig config = Config(objects: new[] { Obj("Sofia", Vector3.zero) });

            ConvaiResolvedActionTarget resolved = ConvaiResolvedActionTarget.Resolve(
                "Sofia", config, ConvaiActionTargetKind.Character);

            Assert.That(resolved, Is.Null,
                "A caller that names a kind is stating a constraint. Handing it the object would be "
                + "a wrong target that nothing downstream re-checks.");
        }

        /// <summary>The same call still finds the kind it asked for when that kind is there.</summary>
        [Test]
        public void AKindedReferenceStillResolvesWithinItsOwnKind()
        {
            ConvaiActionConfig config = Config(
                objects: new[] { Obj("Sofia", Vector3.zero) },
                characters: new[] { Chr("Sofia", Vector3.one) });

            ConvaiResolvedActionTarget resolved = ConvaiResolvedActionTarget.Resolve(
                "Sofia", config, ConvaiActionTargetKind.Character);

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved.Kind, Is.EqualTo(ConvaiActionTargetKind.Character));
        }

        /// <summary>
        ///     The requirement overload keeps its near miss — the two must stay different.
        /// </summary>
        /// <remarks>
        ///     Written as the other half of the pair deliberately. Making the kinded overload strict
        ///     by making <em>both</em> strict would delete the <c>WrongKind</c> outcome and turn
        ///     "you asked for a person and Sofia is a statue" back into "nothing matched".
        /// </remarks>
        [Test]
        public void TheRequirementOverloadStillReturnsTheOtherKindAsANearMiss()
        {
            ConvaiActionConfig config = Config(objects: new[] { Obj("Sofia", Vector3.zero) });

            ConvaiResolvedActionTarget resolved = ConvaiResolvedActionTarget.Resolve(
                "Sofia", config, ConvaiActionTargetRequirement.Character);

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved.Kind, Is.EqualTo(ConvaiActionTargetKind.Object));
        }

        // ── The fuzzy rung still refuses to guess ────────────────────────────────────────

        [Test]
        public void TwoLooseFitsOfTheSameKindStillResolveToNothing()
        {
            ConvaiActionConfig config = Config(objects: new[]
            {
                Obj("North Storage Bay", Vector3.zero),
                Obj("South Storage Bay", Vector3.one)
            });

            Assert.That(Resolve("Storage Bay", config), Is.Null,
                "Two things it could mean, so it means neither — guessing here is how a command "
                + "acts on the wrong thing.");
        }

        /// <summary>
        ///     Ambiguity among objects must not take characters out of the running.
        /// </summary>
        /// <remarks>
        ///     Previously the object ladder returned null on ambiguity and the character ladder ran
        ///     anyway. Folding the two into one pass could easily have turned that into a refusal,
        ///     which would drop commands that work today.
        /// </remarks>
        [Test]
        public void AmbiguityAmongObjectsStillLeavesCharactersReachable()
        {
            ConvaiActionConfig config = Config(
                objects: new[]
                {
                    Obj("North Storage Bay", Vector3.zero),
                    Obj("South Storage Bay", Vector3.one)
                },
                characters: new[] { Chr("Storage Bay Warden", new Vector3(3f, 0f, 0f)) });

            ConvaiResolvedActionTarget resolved = Resolve("Storage Bay", config);
            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved.Kind, Is.EqualTo(ConvaiActionTargetKind.Character));
        }

        // ── Unavailable entries ──────────────────────────────────────────────────────────

        [Test]
        public void AWithdrawnEntryIsInvisibleAtEveryRung()
        {
            var withdrawn = Obj("Sofia", Vector3.zero);
            withdrawn.Available = false;
            ConvaiActionConfig config = Config(
                objects: new[] { withdrawn },
                characters: new[] { Chr("Sofia", Vector3.one) });

            ConvaiResolvedActionTarget resolved = Resolve("Sofia", config);

            Assert.That(resolved.Kind, Is.EqualTo(ConvaiActionTargetKind.Character),
                "A withdrawn object does not hold the name against a live character.");
        }

        // ── Normalized rung ──────────────────────────────────────────────────────────────

        [Test]
        public void ANormalizedMatchBeatsAContainsMatchOfTheOtherKind()
        {
            ConvaiActionConfig config = Config(
                objects: new[] { Obj("Gallery Door Frame", Vector3.zero) },
                characters: new[] { Chr("Gallery Door", Vector3.one) });

            ConvaiResolvedActionTarget resolved = Resolve("The Gallery Door", config);

            Assert.That(resolved.Kind, Is.EqualTo(ConvaiActionTargetKind.Character),
                "'The Gallery Door' normalizes onto the character exactly; the object is only a "
                + "substring fit.");
        }
    }
}
