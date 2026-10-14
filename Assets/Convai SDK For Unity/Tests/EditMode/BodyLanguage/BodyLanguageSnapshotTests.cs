using System.Collections.Generic;
using Convai.Modules.BodyLanguage.Components;
using Convai.Modules.BodyLanguage.Core.Diagnostics;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     Snapshot capture tests: the allocation-free overload fills the caller's instance
    ///     (reusing its trace list) and the convenience overload allocates a fresh one.
    /// </summary>
    public sealed class BodyLanguageSnapshotTests
    {
        /// <summary>
        ///     Hosts the controller on an inactive GameObject. Snapshot capture is a pure read and
        ///     needs no lifecycle, and staying inactive keeps the component's <c>OnEnable</c> out of
        ///     it — that path correctly reports a component placed outside a Convai character, and
        ///     an intentional error is still an error NUnit fails the test on.
        /// </summary>
        private static ConvaiBodyLanguageController HostOn(GameObject root)
        {
            root.SetActive(false);
            return root.AddComponent<ConvaiBodyLanguageController>();
        }

        [Test]
        public void CaptureSnapshot_FillsExistingInstance_WithoutReplacingIt()
        {
            GameObject root = new("BodyLanguageSnapshotTestCharacter");
            try
            {
                ConvaiBodyLanguageController controller = HostOn(root);

                var snapshot = new BodyLanguageSnapshot
                {
                    ProfileName = "garbage",
                    IsInert = true,
                    HasSpine = true
                };
                List<BodyLanguageTraceEntry> traceList = snapshot.RecentTrace;

                controller.CaptureSnapshot(snapshot);

                Assert.That(snapshot.RecentTrace, Is.SameAs(traceList),
                    "The reusable overload must not swap the caller's trace list.");
                Assert.That(snapshot.ProfileName, Is.Not.EqualTo("garbage"),
                    "Clear + refill must overwrite stale values.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void CaptureSnapshot_NullInstance_IsANoOp()
        {
            GameObject root = new("BodyLanguageSnapshotNullTestCharacter");
            try
            {
                ConvaiBodyLanguageController controller = HostOn(root);
                Assert.DoesNotThrow(() => controller.CaptureSnapshot(null));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void CaptureSnapshot_ConvenienceOverload_AllocatesAndFills()
        {
            GameObject root = new("BodyLanguageSnapshotAllocTestCharacter");
            try
            {
                ConvaiBodyLanguageController controller = HostOn(root);
                BodyLanguageSnapshot snapshot = controller.CaptureSnapshot();

                Assert.NotNull(snapshot);
                Assert.NotNull(snapshot.ProfileName);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Snapshot_Clear_ResetsAllFields()
        {
            var snapshot = new BodyLanguageSnapshot
            {
                ProfileName = "x",
                IsInert = true,
                HasSpine = true,
                HasChest = true,
                HasUpperChest = true,
                HasShoulders = true
            };
            snapshot.RecentTrace.Add(default);

            snapshot.Clear();

            Assert.That(snapshot.ProfileName, Is.EqualTo("-"));
            Assert.IsFalse(snapshot.IsInert);
            Assert.IsFalse(snapshot.HasSpine);
            Assert.IsFalse(snapshot.HasChest);
            Assert.IsFalse(snapshot.HasUpperChest);
            Assert.IsFalse(snapshot.HasShoulders);
            Assert.That(snapshot.RecentTrace, Is.Empty);
        }
    }
}
