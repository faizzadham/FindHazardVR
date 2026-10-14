using System.Collections.Generic;
using System.Reflection;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Core.Behaviors;
using Convai.Modules.Gaze.Providers;
using Convai.Shared.Compatibility;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     Guards the object-identity contract between <see cref="GazeJointAttention" /> and
    ///     <see cref="JointAttentionDirector" /> (finding F-2).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The provider hands the director a candidate id and later resolves the director's
    ///         chosen id back to a <see cref="Transform" /> through its own map. It used to key
    ///         that map by <c>ConvaiObjectId.Of(target).GetHashCode()</c> — a 64-bit object id
    ///         folded into 32 bits — so two candidates in the same attention cone whose ids
    ///         happened to collide resolved to each other's transform and the character glanced
    ///         at the wrong object. Silent, and non-deterministic between sessions.
    ///     </para>
    ///     <para>
    ///         The types alone no longer make the mistake expressible in a compile-safe way, but
    ///         they do not prevent it: an <c>int</c> hash still widens silently to <c>long</c>.
    ///         So this asserts the value itself, on the one method that derives it.
    ///     </para>
    /// </remarks>
    [TestFixture]
    public sealed class GazeJointAttentionIdentityTests
    {
        private GameObject _root;
        private GameObject _first;
        private GameObject _second;
        private GazeJointAttention _jointAttention;

        [SetUp]
        public void SetUp()
        {
            // Inactive: GazeJointAttention.OnEnable resolves an EmbodimentContext and logs itself
            // inert without one. AddCandidate needs no context, so keep the lifecycle out of it.
            _root = new GameObject("JointAttentionIdentityCharacter");
            _root.SetActive(false);
            _jointAttention = _root.AddComponent<GazeJointAttention>();

            _first = new GameObject("Vase");
            _second = new GameObject("Painting");
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_second);
            Object.DestroyImmediate(_first);
            Object.DestroyImmediate(_root);
        }

        [Test]
        public void CandidateId_IsTheUnnarrowedObjectId()
        {
            AddCandidate(_first.transform, "Vase");

            IReadOnlyList<JointAttentionCandidate> candidates = CandidateBuffer();
            Assert.AreEqual(1, candidates.Count, "Precondition: the candidate must have been accepted.");
            Assert.AreEqual(ConvaiObjectId.Of(_first.transform), candidates[0].Id,
                "The candidate id must be the object's identity as-is. Narrowing it (a hash, a cast " +
                "to int) is what let two objects share one id.");
        }

        [Test]
        public void CandidateMap_ResolvesEachIdToItsOwnTransform()
        {
            AddCandidate(_first.transform, "Vase");
            AddCandidate(_second.transform, "Painting");

            IReadOnlyDictionary<long, Transform> map = IdToTransform();
            Assert.AreEqual(2, map.Count, "Two distinct objects must occupy two distinct map entries.");
            Assert.AreSame(_first.transform, map[ConvaiObjectId.Of(_first.transform)]);
            Assert.AreSame(_second.transform, map[ConvaiObjectId.Of(_second.transform)]);
        }

        /// <summary>
        ///     A candidate the wiring layer cannot name is a candidate the glance cannot resolve;
        ///     it must be dropped rather than entered under the director's "no target" id.
        /// </summary>
        [Test]
        public void SelfAndDescendantTargets_AreNeverCandidates()
        {
            var child = new GameObject("Eyes");
            child.transform.SetParent(_root.transform);

            AddCandidate(_root.transform, "Self");
            AddCandidate(child.transform, "OwnEyes");

            Assert.AreEqual(0, CandidateBuffer().Count,
                "Joint attention must never fire at the character's own hierarchy — that is eye contact.");
            Assert.AreEqual(0, IdToTransform().Count);

            Object.DestroyImmediate(child);
        }

        private void AddCandidate(Transform target, string debugName)
        {
            GazeTargetCandidate candidate = new(
                GazeTargetKind.WorldObject,
                priority: 0,
                relevance: 1f,
                target,
                target.position,
                debugName);

            MethodInfo addCandidate = typeof(GazeJointAttention).GetMethod(
                "AddCandidate", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(addCandidate, "GazeJointAttention.AddCandidate has been renamed; update this guard.");

            addCandidate.Invoke(_jointAttention, new object[] { _root.transform, candidate });
        }

        private IReadOnlyList<JointAttentionCandidate> CandidateBuffer() =>
            (IReadOnlyList<JointAttentionCandidate>)PrivateField("_candidateBuffer");

        private IReadOnlyDictionary<long, Transform> IdToTransform() =>
            (IReadOnlyDictionary<long, Transform>)PrivateField("_idToTransform");

        private object PrivateField(string name)
        {
            FieldInfo field = typeof(GazeJointAttention).GetField(
                name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"GazeJointAttention.{name} has been renamed; update this guard.");
            return field.GetValue(_jointAttention);
        }
    }
}
