using System.Collections.Generic;
using System.Linq;
using LiveKit.Editor;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public sealed class FfiInstallPlannerTests
    {
        private const string Tag = "livekit-ffi/v0.12.69";

        private static readonly Dictionary<string, string> Manifest = new Dictionary<string, string>
        {
            { "ffi-macos-arm64", "aaa" },
            { "ffi-ios-arm64", "bbb" }
        };

        private static FfiLockFile Lock(string tag, params (string folder, string hash)[] entries)
        {
            var lockFile = new FfiLockFile { ffiTag = tag };
            foreach ((string folder, string hash) in entries) lockFile.Record(folder, hash);
            return lockFile;
        }

        [Test]
        public void Plan_WhenNoLock_PurgesEverythingPresentAndDownloadsRequired()
        {
            FfiInstallPlan plan = FfiInstallPlanner.Plan(Tag, Manifest, null,
                new[] { "ffi-macos-arm64" }, new[] { "ffi-macos-arm64", "ffi-windows-x86_64" });

            Assert.That(plan.PurgeAll, Is.True);
            Assert.That(plan.Purges, Is.EquivalentTo(new[] { "ffi-macos-arm64", "ffi-windows-x86_64" }));
            Assert.That(plan.Downloads, Is.EquivalentTo(new[] { "ffi-macos-arm64" }));
        }

        [Test]
        public void Plan_WhenLockTagDiffers_PurgesAll()
        {
            FfiInstallPlan plan = FfiInstallPlanner.Plan(Tag, Manifest, Lock("livekit-ffi/v0.12.63", ("ffi-macos-arm64", "aaa")),
                new[] { "ffi-macos-arm64" }, new[] { "ffi-macos-arm64" });

            Assert.That(plan.PurgeAll, Is.True);
            Assert.That(plan.Downloads, Is.EquivalentTo(new[] { "ffi-macos-arm64" }));
        }

        [Test]
        public void Plan_WhenVerified_KeepsAndIsNoOp()
        {
            FfiInstallPlan plan = FfiInstallPlanner.Plan(Tag, Manifest, Lock(Tag, ("ffi-macos-arm64", "AAA")),
                new[] { "ffi-macos-arm64" }, new[] { "ffi-macos-arm64" });

            Assert.That(plan.PurgeAll, Is.False);
            Assert.That(plan.IsNoOp, Is.True);
        }

        [Test]
        public void Plan_WhenFolderMissing_Downloads()
        {
            FfiInstallPlan plan = FfiInstallPlanner.Plan(Tag, Manifest, Lock(Tag, ("ffi-macos-arm64", "aaa")),
                new[] { "ffi-macos-arm64", "ffi-ios-arm64" }, new[] { "ffi-macos-arm64" });

            Assert.That(plan.Downloads, Is.EquivalentTo(new[] { "ffi-ios-arm64" }));
            Assert.That(plan.Steps.Single(s => s.Folder == "ffi-ios-arm64").Reason, Is.EqualTo("missing"));
        }

        [Test]
        public void Plan_WhenRecordedHashDiffers_Downloads()
        {
            FfiInstallPlan plan = FfiInstallPlanner.Plan(Tag, Manifest, Lock(Tag, ("ffi-macos-arm64", "stale")),
                new[] { "ffi-macos-arm64" }, new[] { "ffi-macos-arm64" });

            Assert.That(plan.Downloads, Is.EquivalentTo(new[] { "ffi-macos-arm64" }));
            Assert.That(plan.PurgeAll, Is.False);
        }

        [Test]
        public void Plan_WhenPresentButUnrecorded_Downloads()
        {
            FfiInstallPlan plan = FfiInstallPlanner.Plan(Tag, Manifest, Lock(Tag),
                new[] { "ffi-macos-arm64" }, new[] { "ffi-macos-arm64" });

            Assert.That(plan.Downloads, Is.EquivalentTo(new[] { "ffi-macos-arm64" }));
        }

        [Test]
        public void Plan_WhenRequiredFolderNotInManifest_KeepsWithoutDownloading()
        {
            FfiInstallPlan plan = FfiInstallPlanner.Plan(Tag, Manifest, Lock(Tag),
                new[] { "ffi-linux-arm64" }, new string[0]);

            Assert.That(plan.Downloads, Is.Empty);
            Assert.That(plan.Steps.Single().Reason, Is.EqualTo("not in manifest"));
        }

        private static readonly Dictionary<string, string> AndroidManifest = new Dictionary<string, string>
        {
            { "ffi-android-arm64", "a64" },
            { "ffi-android-armv7", "a7" },
            { "ffi-android-x86_64", "a86" },
            { "ffi-macos-arm64", "aaa" }
        };

        private static FfiInstallStep Step(FfiInstallPlan plan, string folder) => plan.Steps.Single(s => s.Folder == folder);

        [Test]
        public void Plan_WhenAnInstalledFolderAlreadyCarriesTheSharedJar_NewAndroidDownloadsOmitIt()
        {
            // Incremental install: arm64 is verified on disk and owns libwebrtc.jar; armv7 is added later.
            FfiInstallPlan plan = FfiInstallPlanner.Plan(Tag, AndroidManifest, Lock(Tag, ("ffi-android-arm64", "a64")),
                new[] { "ffi-android-arm64", "ffi-android-armv7" }, new[] { "ffi-android-arm64" },
                sharedAndroidJarOwners: new[] { "ffi-android-arm64" });

            Assert.That(Step(plan, "ffi-android-arm64").Action, Is.EqualTo(FfiInstallAction.Keep));
            Assert.That(Step(plan, "ffi-android-armv7").Action, Is.EqualTo(FfiInstallAction.Download));
            Assert.That(Step(plan, "ffi-android-armv7").CarriesSharedAndroidJar, Is.False,
                "a second copy of libwebrtc.jar breaks the Android build with duplicate classes");
        }

        [Test]
        public void Plan_WhenNothingCarriesTheSharedJar_OnlyTheArm64DownloadCarriesIt()
        {
            FfiInstallPlan plan = FfiInstallPlanner.Plan(Tag, AndroidManifest, null,
                new[] { "ffi-android-armv7", "ffi-android-arm64", "ffi-android-x86_64", "ffi-macos-arm64" }, new string[0]);

            Assert.That(Step(plan, "ffi-android-arm64").CarriesSharedAndroidJar, Is.True);
            Assert.That(Step(plan, "ffi-android-armv7").CarriesSharedAndroidJar, Is.False);
            Assert.That(Step(plan, "ffi-android-x86_64").CarriesSharedAndroidJar, Is.False);
            Assert.That(Step(plan, "ffi-macos-arm64").CarriesSharedAndroidJar, Is.False);
        }

        [Test]
        public void Plan_WhenArm64IsNotRequired_TheFirstAndroidDownloadCarriesTheSharedJar()
        {
            FfiInstallPlan plan = FfiInstallPlanner.Plan(Tag, AndroidManifest, null,
                new[] { "ffi-android-x86_64", "ffi-android-armv7" }, new string[0]);

            Assert.That(Step(plan, "ffi-android-x86_64").CarriesSharedAndroidJar, Is.True);
            Assert.That(Step(plan, "ffi-android-armv7").CarriesSharedAndroidJar, Is.False);
        }

        [Test]
        public void Plan_WhenTheJarOwnerIsReplaced_ItsDownloadKeepsTheSharedJar()
        {
            // arm64 owns the jar but its hash changed, so it is re-downloaded and must keep owning the jar.
            FfiInstallPlan plan = FfiInstallPlanner.Plan(Tag, AndroidManifest,
                Lock(Tag, ("ffi-android-arm64", "stale"), ("ffi-android-armv7", "a7")),
                new[] { "ffi-android-arm64", "ffi-android-armv7" }, new[] { "ffi-android-arm64", "ffi-android-armv7" },
                sharedAndroidJarOwners: new[] { "ffi-android-arm64" });

            Assert.That(Step(plan, "ffi-android-arm64").Action, Is.EqualTo(FfiInstallAction.Download));
            Assert.That(Step(plan, "ffi-android-arm64").CarriesSharedAndroidJar, Is.True);
            Assert.That(Step(plan, "ffi-android-armv7").Action, Is.EqualTo(FfiInstallAction.Keep));
        }

        [Test]
        public void Plan_WhenAnInstalledFolderThatIsNoLongerRequiredCarriesTheSharedJar_DownloadsOmitIt()
        {
            // armv7 was installed with the jar earlier; the build target now needs only arm64. armv7 stays on disk.
            FfiInstallPlan plan = FfiInstallPlanner.Plan(Tag, AndroidManifest, Lock(Tag, ("ffi-android-armv7", "a7")),
                new[] { "ffi-android-arm64" }, new[] { "ffi-android-armv7" },
                sharedAndroidJarOwners: new[] { "ffi-android-armv7" });

            Assert.That(Step(plan, "ffi-android-arm64").Action, Is.EqualTo(FfiInstallAction.Download));
            Assert.That(Step(plan, "ffi-android-arm64").CarriesSharedAndroidJar, Is.False);
        }

        [TestCase("ffi-android-arm64", "a64")]
        [TestCase("ffi-android-armv7", "a7")]
        public void Plan_WhenVerifiedAndroidFolderHasNoSharedJar_DownloadsItToRepairTheJar(string folder, string hash)
        {
            FfiInstallPlan plan = FfiInstallPlanner.Plan(Tag, AndroidManifest, Lock(Tag, (folder, hash)),
                new[] { folder }, new[] { folder }, sharedAndroidJarOwners: new string[0]);

            Assert.That(plan.IsNoOp, Is.False);
            Assert.That(plan.Downloads, Is.EqualTo(new[] { folder }));
            Assert.That(Step(plan, folder).CarriesSharedAndroidJar, Is.True);
        }

        [Test]
        public void Plan_WhenAllAndroidFoldersAreVerifiedButJarIsMissing_RepairsOnlyArm64()
        {
            string[] folders = { "ffi-android-armv7", "ffi-android-arm64" };
            FfiInstallPlan plan = FfiInstallPlanner.Plan(Tag, AndroidManifest,
                Lock(Tag, ("ffi-android-armv7", "a7"), ("ffi-android-arm64", "a64")),
                folders, folders, sharedAndroidJarOwners: new string[0]);

            Assert.That(plan.Downloads, Is.EqualTo(new[] { "ffi-android-arm64" }));
            Assert.That(Step(plan, "ffi-android-arm64").CarriesSharedAndroidJar, Is.True);
            Assert.That(Step(plan, "ffi-android-armv7").Action, Is.EqualTo(FfiInstallAction.Keep));
        }

        [Test]
        public void Plan_WhenVerifiedAndroidFolderStillOwnsJar_KeepsIt()
        {
            string[] folders = { "ffi-android-arm64" };
            FfiInstallPlan plan = FfiInstallPlanner.Plan(Tag, AndroidManifest,
                Lock(Tag, ("ffi-android-arm64", "a64")), folders, folders, sharedAndroidJarOwners: folders);

            Assert.That(plan.IsNoOp, Is.True);
            Assert.That(plan.Downloads, Is.Empty);
        }

        [Test]
        public void Plan_WhenAndroidFolderIsNotInManifest_DoesNotDownloadItToRepairJar()
        {
            string[] folders = { "ffi-android-unsupported" };
            FfiInstallPlan plan = FfiInstallPlanner.Plan(Tag, AndroidManifest, Lock(Tag),
                folders, folders, sharedAndroidJarOwners: new string[0]);

            Assert.That(plan.IsNoOp, Is.True);
            Assert.That(plan.Downloads, Is.Empty);
        }
    }
}
