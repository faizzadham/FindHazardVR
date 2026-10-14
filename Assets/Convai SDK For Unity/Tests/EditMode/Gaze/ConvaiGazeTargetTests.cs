using System.Collections.Generic;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Providers;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     The declarative, metadata-free <see cref="ConvaiGazeTarget" />: registry lifecycle,
    ///     distance-relevance falloff, aim-offset gaze point, and candidate identity.
    /// </summary>
    /// <remarks>
    ///     Unity does not invoke <c>OnEnable</c> for plain MonoBehaviours in edit mode, so these
    ///     tests drive the component's internal <c>HandleEnable</c>/<c>HandleDisable</c> seams —
    ///     the exact bodies the play-mode lifecycle runs.
    /// </remarks>
    public sealed class ConvaiGazeTargetTests
    {
        private readonly List<GameObject> _spawned = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = _spawned.Count - 1; i >= 0; i--)
            {
                if (_spawned[i] == null) continue;
                ConvaiGazeTarget target = _spawned[i].GetComponent<ConvaiGazeTarget>();
                if (target != null) target.HandleDisable();
                Object.DestroyImmediate(_spawned[i]);
            }
            _spawned.Clear();
        }

        // ── Registry lifecycle ───────────────────────────────────────────────

        [Test]
        public void HandleEnable_Registers_DoubleEnableDoesNotDuplicate()
        {
            ConvaiGazeTarget target = NewTarget("Target", Vector3.zero);

            target.HandleEnable();
            Assert.That(ConvaiGazeTarget.ActiveTargets, Has.Member(target));

            target.HandleEnable();
            int count = 0;
            foreach (ConvaiGazeTarget t in ConvaiGazeTarget.ActiveTargets)
                if (t == target) count++;
            Assert.That(count, Is.EqualTo(1), "A second HandleEnable must not duplicate the registry entry.");
        }

        [Test]
        public void HandleDisable_Unregisters_DoubleDisableIsSafe()
        {
            ConvaiGazeTarget target = NewTarget("Target", Vector3.zero);
            target.HandleEnable();

            target.HandleDisable();
            Assert.That(ConvaiGazeTarget.ActiveTargets, Has.No.Member(target));

            Assert.DoesNotThrow(() => target.HandleDisable());
        }

        // ── Distance relevance ───────────────────────────────────────────────

        [Test]
        public void InsideFullRelevanceDistance_RelevanceEqualsBaseRelevance()
        {
            ConvaiGazeTarget target = NewTarget("Near", Vector3.zero);
            target.MaxDistance = 10f;
            target.FullRelevanceDistance = 3f;
            target.BaseRelevance = 0.75f;

            GameObject observer = NewObserver(new Vector3(0f, 0f, 2f));

            Assert.IsTrue(target.TryGetCandidate(observer.transform, out GazeTargetCandidate candidate));
            Assert.That(candidate.Relevance, Is.EqualTo(target.BaseRelevance).Within(0.0001f));
        }

        [Test]
        public void BetweenFullAndMaxDistance_RelevanceIsStrictlyLower()
        {
            ConvaiGazeTarget target = NewTarget("Mid", Vector3.zero);
            target.MaxDistance = 10f;
            target.FullRelevanceDistance = 3f;
            target.BaseRelevance = 0.75f;

            GameObject observer = NewObserver(new Vector3(0f, 0f, 6f));

            Assert.IsTrue(target.TryGetCandidate(observer.transform, out GazeTargetCandidate candidate));
            Assert.That(candidate.Relevance, Is.LessThan(target.BaseRelevance),
                "Between the full-relevance and max distance, relevance must falloff below the base.");
        }

        [Test]
        public void BeyondMaxDistance_EmitsNoCandidate()
        {
            ConvaiGazeTarget target = NewTarget("Far", Vector3.zero);
            target.MaxDistance = 10f;
            target.FullRelevanceDistance = 3f;

            GameObject observer = NewObserver(new Vector3(0f, 0f, 20f));

            Assert.IsFalse(target.TryGetCandidate(observer.transform, out _),
                "A target beyond maxDistance is not a gaze candidate.");
        }

        // ── Aim offset ───────────────────────────────────────────────────────

        [Test]
        public void AimOffset_ShiftsWorldPointFromTransform()
        {
            ConvaiGazeTarget target = NewTarget("Painting", new Vector3(1f, 0f, 5f));
            target.AimOffset = new Vector3(0f, 1.5f, 0f);

            GameObject observer = NewObserver(Vector3.zero);

            Assert.IsTrue(target.TryGetCandidate(observer.transform, out GazeTargetCandidate candidate));
            Assert.That(candidate.WorldPoint, Is.EqualTo(target.transform.position + new Vector3(0f, 1.5f, 0f)));
        }

        // ── Candidate identity ───────────────────────────────────────────────

        [Test]
        public void Candidate_CarriesKindPriorityAndName()
        {
            ConvaiGazeTarget target = NewTarget("StatueOfInterest", Vector3.zero);
            target.Priority = 12;

            GameObject observer = NewObserver(new Vector3(0f, 0f, 1f));

            Assert.IsTrue(target.TryGetCandidate(observer.transform, out GazeTargetCandidate candidate));
            Assert.That(candidate.Kind, Is.EqualTo(GazeTargetKind.WorldObject));
            Assert.That(candidate.Priority, Is.EqualTo(12));
            Assert.That(candidate.DebugName, Is.EqualTo("StatueOfInterest"));
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private ConvaiGazeTarget NewTarget(string name, Vector3 position)
        {
            var go = new GameObject(name);
            go.transform.position = position;
            _spawned.Add(go);
            return go.AddComponent<ConvaiGazeTarget>();
        }

        private GameObject NewObserver(Vector3 position)
        {
            var go = new GameObject("Observer");
            go.transform.position = position;
            _spawned.Add(go);
            return go;
        }
    }
}
