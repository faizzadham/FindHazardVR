using Convai.Shared.Compatibility;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Convai.Editor.SceneTagging;
using Convai.Runtime.Actions;
using Convai.Runtime.SceneMetadata;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.SceneTagging
{
    public sealed class SceneTaggerTests
    {
        private Scene _scene;
        private readonly List<Object> _assets = new();
        [SetUp] public void Setup() => _scene = EditorSceneManager.NewPreviewScene();
        [TearDown] public void Cleanup()
        {
            if (_scene.IsValid()) EditorSceneManager.ClosePreviewScene(_scene);
            foreach (Object asset in _assets) if (asset) Object.DestroyImmediate(asset);
            _assets.Clear();
        }
        private static Shader FixtureShader()
        {
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            return Shader.Find(!pipeline ? "Standard" : pipeline.GetType().Name.Contains("HDRender") ? "HDRP/Lit" : "Universal Render Pipeline/Lit");
        }
        private GameObject Cube(string name = "Test object")
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            SceneManager.MoveGameObjectToScene(go, _scene);
            return go;
        }

        [Test] public void DiscoverySharesBoundariesAndIncludesRenderersOutsideLodLevels()
        {
            GameObject root = Cube("LOD object"), extra = Cube("Attached prop"), low = Cube("Low detail");
            extra.transform.SetParent(root.transform); low.transform.SetParent(root.transform);
            root.AddComponent<LODGroup>().SetLODs(new[] { new LOD(.5f, new[] { root.GetComponent<Renderer>() }), new LOD(.1f, new[] { low.GetComponent<Renderer>() }) });
            var boundaries = new HashSet<GameObject> { root };
            var row = SceneTaggerDiscovery.Create(root, true, boundaries);
            Assert.That(row.Boundaries, Is.SameAs(boundaries));
            Assert.That(row.Renderers, Has.Member(extra.GetComponent<Renderer>()));
            Assert.That(row.Renderers, Has.No.Member(low.GetComponent<Renderer>()));
        }

        [Test] public void ProceduralFingerprintDetectsSecondaryUvAndTopologyChanges()
        {
            GameObject go = Cube();
            Mesh mesh = Object.Instantiate(go.GetComponent<MeshFilter>().sharedMesh); _assets.Add(mesh);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            string original = SceneTaggerDiscovery.HashMeshBytes(mesh);
            mesh.uv2 = Enumerable.Repeat(new Vector2(.2f, .7f), mesh.vertexCount).ToArray();
            string changed = SceneTaggerDiscovery.HashMeshBytes(mesh);
            Assert.That(changed, Is.Not.EqualTo(original));
            Assert.That(SceneTaggerDiscovery.HashMeshBytes(mesh), Is.EqualTo(changed));
            var indices = mesh.GetIndices(0); Array.Reverse(indices); mesh.SetIndices(indices, MeshTopology.Triangles, 0);
            Assert.That(SceneTaggerDiscovery.HashMeshBytes(mesh), Is.Not.EqualTo(changed));
        }

        [TestCase("waterproof_barrel", "roof")]
        [TestCase("robust_crate", "bust")]
        public void RelevanceMatchesWordsRatherThanSubstrings(string name, string accidentalMatch)
        {
            Assert.That(SceneTaggerDiscovery.LabelTokens(name), Does.Not.Contain(accidentalMatch));
            Assert.That(SceneTaggerDiscovery.LabelTokens("WallClock_01"), Does.Contain("wall").And.Contain("clock"));
        }

        [Test] public void MissingRowsCanBeRemovedAndIdleCancelDoesNotInventARun()
        {
            var session = new SceneTaggerSession(); var row = Ready(Cube()); session.Rows.Add(row);
            string progress = session.Progress; session.Cancel();
            Assert.That(session.State, Is.EqualTo(SceneTaggerState.Idle)); Assert.That(session.Progress, Is.EqualTo(progress));
            Object.DestroyImmediate(row.Target);
            Assert.That(SceneTaggerReview.Removable(row), Is.True);
            session.RemoveRows(new[] { row }); Assert.That(session.Rows, Is.Empty);
        }

        [Test] public void MetadataBoundActionSettersAndMemberInteractionPointsUseCanonicalData()
        {
            GameObject go = Cube(); var metadata = go.AddComponent<ConvaiObjectMetadata>();
            var target = go.AddComponent<ConvaiActionTarget>(); target.MetadataSource = metadata;
            target.TargetName = "Updated name"; target.Description = "Updated description";
            Assert.That(metadata.ObjectName, Is.EqualTo(target.TargetName).And.EqualTo("Updated name"));
            Assert.That(metadata.ObjectDescription, Is.EqualTo(target.Description).And.EqualTo("Updated description"));
            GameObject point = Cube("Interaction point"); target.InteractionPoint = point.transform;
            Assert.That(ConvaiSceneObjectProjection.InteractionPoint(go), Is.SameAs(point.transform));
        }

        [Test] public async Task FailedDraftLeavesNoBusyProgressOrDuplicateGlobalError()
        {
            var session = ReviewSession(out _);
            try
            {
                await session.DraftContextAsync(_ => Task.FromException<byte[]>(new InvalidOperationException("Capture closed")), CancellationToken.None);
                Assert.Fail("Expected a capture failure.");
            }
            catch (InvalidOperationException error) { Assert.That(error.Message, Is.EqualTo("Capture closed")); }
            Assert.That(session.Busy, Is.False); Assert.That(session.Total, Is.Zero);
            Assert.That(session.State, Is.EqualTo(SceneTaggerState.NeedsAttention));
            Assert.That(session.Progress, Does.Contain("draft failed")); Assert.That(session.LastError, Is.Empty);
        }

        [Test] public async Task CacheHitClearsPreviousCaptureErrorAndPendingImage()
        {
            const string path = "Library/ConvaiSceneTagger/cache.json";
            byte[] previous = File.Exists(path) ? File.ReadAllBytes(path) : null;
            try
            {
                if (File.Exists(path)) File.Delete(path);
                var session = ReviewSession(out SceneTaggerRow row); session.Settings.CacheEnabled = true;
                int calls = 0;
                session.Analyze = (request, _) =>
                {
                    calls++;
                    var response = new SceneTaggerResponse { HttpStatus = 200 };
                    response.Objects.Add(new SceneTaggerObjectResult { Index = 1, Id = row.Id, Name = "Box", Description = "A box.", Confidence = .8f });
                    return Task.FromResult(response);
                };
                await session.RunAsync(new[] { row });
                row.Evidence = null; row.Error = "Previous capture failed"; row.Status = SceneTaggerStatus.Attention; row.PendingAnalysis = true;
                await session.RunAsync(new[] { row });
                Assert.That(calls, Is.EqualTo(1)); Assert.That(row.Error, Is.Empty);
                Assert.That(SceneTaggerReview.Ready(row), Is.True); Assert.That(row.PendingAnalysis, Is.False);
                Assert.That(session.State, Is.EqualTo(SceneTaggerState.ReviewReady));
            }
            finally { if (previous != null) File.WriteAllBytes(path, previous); else if (File.Exists(path)) File.Delete(path); }
        }

        [UnityTest] public IEnumerator ClosingDraftCameraStillCleansItsPipelineState()
        {
            var go = new GameObject("Closing Scene view"); SceneManager.MoveGameObjectToScene(go, _scene);
            var camera = go.AddComponent<Camera>(); camera.scene = _scene; camera.enabled = false;
            long cameraId = ConvaiObjectId.Of(camera);
            var pipeline = new CleanupTrackingPipeline(); SceneTaggerRenderPipelines.Adapters.Insert(0, pipeline);
            try
            {
                Task<byte[]> capture = SceneTaggerCaptureService.CaptureSceneViewAsync(camera, CancellationToken.None);
                Object.DestroyImmediate(go);
                while (!capture.IsCompleted) yield return null;
                Assert.That(capture.IsFaulted, Is.True);
                Assert.That(capture.Exception.GetBaseException().Message, Does.Contain("closed"));
                Assert.That(pipeline.CleanedCameraId, Is.EqualTo(cameraId));
            }
            finally { SceneTaggerRenderPipelines.Adapters.Remove(pipeline); }
        }

        private sealed class CleanupTrackingPipeline : ISceneTaggerRenderPipeline
        {
            internal long CleanedCameraId;
            public bool SupportsCurrentPipeline => true;
            public void Configure(Camera camera, Scene sourceScene, List<Object> resources) { }
            public void Render(Camera camera, RenderTexture destination) { }
            public void SetExposure(Camera camera, float stops) { }
            public void CopyLight(Light source, Light destination) { }
            public void SetSpecular(Camera camera, bool suppress) { }
            public void Cleanup(long cameraId, Scene scene) => CleanedCameraId = cameraId;
        }

        [TestCase(1f), TestCase(-1f)]
        public void PreviewCanOrbitAwayFromStraightTopAndBottomViews(float sign)
        {
            Vector3 start = Vector3.up * sign;
            Vector3 moved = SceneTaggerWindow.OrbitDirection(start, new Vector2(40, 120));
            Assert.That(moved.magnitude, Is.EqualTo(1).Within(.0001f));
            Assert.That(Vector3.Angle(start, moved), Is.EqualTo(60).Within(.001f));
            Assert.That(new Vector2(moved.x, moved.z).magnitude, Is.GreaterThan(.8f));
        }

        [Test] public void NativeLibraryValidatesLayoutsAndPlansRotatedMirroredGeometry()
        {
            SceneTaggerNative.ValidateLayouts();
            SceneTaggerCoreAdapter core = SceneTaggerCoreAdapter.Instance;
            Assert.That(core.Version, Is.EqualTo("0.10.0"));
            GameObject go = Cube();
            go.transform.SetPositionAndRotation(new Vector3(8, 3, -2), Quaternion.Euler(17, 62, -23));
            go.transform.localScale = new Vector3(-3, 2, .3f);
            SceneTaggerRow row = SceneTaggerDiscovery.Create(go, true);
            var plan = core.Plan(row.Corners, false);
            Assert.That(plan.candidate_count, Is.InRange(1, 4));
            Vector3 original = new(2, -3, 4);
            Assert.That(Vector3.Distance(SceneTaggerCoreAdapter.FromCore(SceneTaggerCoreAdapter.ToCore(original)), original), Is.LessThan(.0001f));
            Assert.That(SceneTaggerCoreAdapter.ToCore(original, true).x, Is.EqualTo(400));
            SceneTaggerRow cube = SceneTaggerDiscovery.Create(Cube(), true);
            Vector3 hint = new Vector3(-1, .2f, -1).normalized;
            var hinted = core.Plan(cube.Corners, false, hint);
            Assert.That(Enumerable.Range(0, (int)hinted.candidate_count).Any(i =>
                Vector3.Dot(SceneTaggerCoreAdapter.FromCore(hinted.candidates[i].direction_actor_local).normalized, hint) > .999f), Is.True,
                "An authored camera direction must reach Core without reflecting the wrong axis.");
        }

        [Test] public void DiscoveryPreservesNestedMetadataAndRejectsChangedEvidence()
        {
            GameObject parent = Cube("Cabinet"), child = Cube("Object inside cabinet");
            child.transform.SetParent(parent.transform);
            child.AddComponent<ConvaiObjectMetadata>();
            SceneTaggerRow row = SceneTaggerDiscovery.Create(parent, true);
            Assert.That(row.Renderers, Has.Length.EqualTo(1));
            Assert.That(SceneTaggerDiscovery.Validate(row), Is.True);
            parent.transform.localScale = new Vector3(2, 1, 1);
            Assert.That(SceneTaggerDiscovery.Validate(row), Is.False);
        }

        [Test] public void ProceduralAndPropertyBlockObjectsDoNotShareEvidence()
        {
            GameObject first = Cube(), second = Cube();
            var material = new Material(FixtureShader()); _assets.Add(material);
            first.GetComponent<Renderer>().sharedMaterial = material; second.GetComponent<Renderer>().sharedMaterial = material;
            string colorProperty = material.HasProperty("_BaseColor") ? "_BaseColor" : "_Color";
            var block = new MaterialPropertyBlock(); block.SetColor(colorProperty, Color.red);
            first.GetComponent<Renderer>().SetPropertyBlock(block);
            second.GetComponent<Renderer>().SetPropertyBlock(block);
            SceneTaggerRow row = SceneTaggerDiscovery.Create(first, true);
            Assert.That(SceneTaggerDiscovery.Validate(row), Is.True);
            Assert.That(row.Fingerprint, Is.Not.EqualTo(SceneTaggerDiscovery.Create(second, true).Fingerprint));
            block.SetColor(colorProperty, Color.blue);
            first.GetComponent<Renderer>().SetPropertyBlock(block);
            Assert.That(SceneTaggerDiscovery.Validate(row), Is.False);
            material.SetFloat("_Metallic", .6f);
            second.transform.SetParent(first.transform, false);
            Assert.That(SceneTaggerDiscovery.Create(first, true).Metallic, Is.True,
                "One explicit metallic material can span multiple renderers.");
            var other = new Material(material); _assets.Add(other);
            second.GetComponent<Renderer>().sharedMaterial = other;
            Assert.That(SceneTaggerDiscovery.Create(first, true).Metallic, Is.False);
        }

        [Test] public void ExplicitSelectionBypassesSoftFiltersAndTreatsLodsAsOneTarget()
        {
            GameObject root = Cube("Cube"), lowerLod = Cube("Lower LOD");
            lowerLod.transform.SetParent(root.transform, false);
            root.AddComponent<ConvaiObjectMetadata>();
            root.AddComponent<LODGroup>().SetLODs(new[] {
                new LOD(.5f, new[] { root.GetComponent<Renderer>() }),
                new LOD(.1f, new[] { lowerLod.GetComponent<Renderer>() }) });
            var rows = SceneTaggerDiscovery.Discover(new SceneTaggerSettings {
                SelectedOnly = true, ExcludeBasicShapes = true, IncludeTagged = false, Relevance = 1
            }, new[] { root, lowerLod });
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows[0].Renderers, Is.EqualTo(new[] { root.GetComponent<Renderer>() }));
            Assert.That(rows[0].Explicit, Is.True);
        }

        [TestCase("tagged", "Already tagged: 1", "Re-analyze tagged objects")]
        [TestCase("basic", "Plain engine shapes: 1", "Exclude plain engine shapes")]
        [TestCase("relevance", "Below minimum relevance: 1", "Minimum local relevance")]
        public void EmptyDiscoveryExplainsTheExcludingFilterAndRecoversWhenChanged(string filter, string count, string setting)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            try
            {
                var target = Cube("Plain object");
                SceneManager.MoveGameObjectToScene(target, scene);
                if (filter == "tagged") target.AddComponent<ConvaiObjectMetadata>();
                var session = new SceneTaggerSession();
                session.Settings.ExcludeBasicShapes = filter == "basic";
                session.Settings.Relevance = filter == "relevance" ? 1 : 0;
                session.Discover();
                Assert.That(session.Rows, Is.Empty);
                Assert.That(session.Progress, Does.Contain(count).And.Contain(setting));

                if (filter == "tagged") session.Settings.IncludeTagged = true;
                if (filter == "basic") session.Settings.ExcludeBasicShapes = false;
                if (filter == "relevance") session.Settings.Relevance = 0;
                session.Discover();
                Assert.That(session.Rows.Select(r => r.Target), Is.EqualTo(new[] { target }));
                Assert.That(session.Progress, Is.EqualTo("1 objects ready for capture."));
            }
            finally
            {
                if (scene.IsValid() && scene.isLoaded) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        [Test] public void SkinnedCaptureFitsCurrentPoseInsteadOfAnimationCullingBounds()
        {
            GameObject source = Cube(), root = new GameObject("Posed mesh");
            SceneManager.MoveGameObjectToScene(root, _scene);
            var mesh = Object.Instantiate(source.GetComponent<MeshFilter>().sharedMesh); _assets.Add(mesh);
            mesh.AddBlendShapeFrame("Lift", 100, Enumerable.Repeat(Vector3.up * 4, mesh.vertexCount).ToArray(),
                new Vector3[mesh.vertexCount], new Vector3[mesh.vertexCount]);
            var skin = root.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = mesh;
            skin.sharedMaterial = source.GetComponent<Renderer>().sharedMaterial;
            skin.localBounds = new Bounds(Vector3.zero, Vector3.one * 100);
            skin.SetBlendShapeWeight(0, 100);
            root.transform.position = new Vector3(3, 2, 1);
            root.transform.localScale = new Vector3(-2, 1, .5f);
            SceneTaggerRow row = SceneTaggerDiscovery.Create(root, true);
            Assert.That(Vector3.Distance(row.Bounds.center, new Vector3(3, 6, 1)), Is.LessThan(.001f));
            Assert.That(Vector3.Distance(row.Bounds.size, new Vector3(2, 1, .5f)), Is.LessThan(.001f));
            skin.SetBlendShapeWeight(0, 50);
            Assert.That(SceneTaggerDiscovery.Validate(row), Is.False, "A changed pose invalidates retained evidence.");
        }

        [Test] public void AuthoringLimitsDoNotSplitSurrogates()
        {
            Assert.That(SceneTaggerText.Normalize(new string('a', 63) + "😀", 64), Is.EqualTo(new string('a', 63)));
            Assert.That(SceneTaggerText.Normalize(new string('a', 62) + "😀", 64).Length, Is.EqualTo(64));
            var metadata = Cube().AddComponent<ConvaiObjectMetadata>();
            metadata.ObjectDescription = new string('x', 400);
            Assert.That(metadata.ObjectDescription.Length, Is.EqualTo(400), "Runtime manual strings must remain intact.");
        }

        [Test] public void ApplyUndoAndManualProtection()
        {
            GameObject go = Cube();
            SceneTaggerRow row = Ready(go);
            SceneTaggerApplyService.Apply(new() { new() { row } }, new SceneTaggerSettings { Gaze = false });
            var metadata = go.GetComponent<ConvaiObjectMetadata>();
            Assert.That(metadata.ObjectName, Is.EqualTo("Red box"));
            Assert.That(go.GetComponent<ConvaiActionTarget>().MetadataSource, Is.SameAs(metadata));
            Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
            Assert.That(go.GetComponent<ConvaiObjectMetadata>(), Is.Null);
            Undo.PerformRedo();
            metadata = go.GetComponent<ConvaiObjectMetadata>();
            Assert.That(metadata.ObjectName, Is.EqualTo("Red box"));
            metadata.ObjectName = "My authored name";
            row.Status = SceneTaggerStatus.Ready;
            SceneTaggerApplyService.Apply(new() { new() { row } }, new SceneTaggerSettings());
            Assert.That(metadata.ObjectName, Is.EqualTo("My authored name"));
            Assert.That(row.Error, Does.Contain("preserved"));
        }

        [Test] public void ExistingMetadataAndTrackedPropertiesAreNotOverwritten()
        {
            GameObject go = Cube(); var metadata = go.AddComponent<ConvaiObjectMetadata>();
            metadata.ObjectName = "Authored";
            SceneTaggerRow row = Ready(go);
            SceneTaggerApplyService.Apply(new() { new() { row } }, new SceneTaggerSettings());
            Assert.That(metadata.ObjectName, Is.EqualTo("Authored"));
            Assert.That(go.GetComponent<ConvaiAutoTagOwnership>(), Is.Null);
        }

        [Test] public void IncrementalGroupingIncludesAppliedMembersAndPreservesGroupEdits()
        {
            var session = new SceneTaggerSession();
            session.Settings.Gaze = false; session.Settings.Grouping = SceneTaggerGrouping.GroupAllVerified;
            SceneTaggerRow first = Ready(Cube("First chair")); first.ProofGroup = "verified";
            session.Rows.Add(first); session.Apply();
            SceneTaggerRow second = Ready(Cube("Second chair")); second.ProofGroup = "verified";
            session.Rows.Add(second); session.Apply();
            var group = _scene.GetRootGameObjects().SelectMany(g => g.GetComponents<ConvaiLogicalObjectGroup>()).Single();
            Assert.That(group.Members, Has.Count.EqualTo(2));
            Assert.That(first.Target.GetComponent<ConvaiActionTarget>(), Is.Null);
            Assert.That(group.GetComponent<ConvaiActionTarget>().MetadataSource, Is.SameAs(group.Metadata));
            Assert.That(SceneTaggerDiscovery.Create(first.Target, true).LogicalGroupId, Is.EqualTo(first.LogicalGroupId));
            second.Status = SceneTaggerStatus.Ready; second.Description = "A reviewed group correction.";
            session.Apply();
            Assert.That(group.Metadata.ObjectDescription, Is.EqualTo("A reviewed group correction."), "The newly approved row supplies the group proposal, not an earlier applied row.");
            group.Metadata.ObjectDescription = "My authored group description";
            second.Status = SceneTaggerStatus.Ready; second.Description = "A proposed replacement";
            session.Apply();
            Assert.That(group.Metadata.ObjectDescription, Is.EqualTo("My authored group description"));
            Assert.That(second.Target.GetComponent<ConvaiObjectMetadata>().ObjectDescription, Is.EqualTo("A reviewed group correction."));
            Assert.That(second.Error, Does.Contain("preserved"));
        }

        [Test] public void GroupedApplyRejectsConflictingApprovedDescriptionsBeforeMutation()
        {
            SceneTaggerRow first = Ready(Cube("First exhibit")), second = Ready(Cube("Second exhibit"));
            second.Description = "A different approved description.";
            first.ProofGroup = second.ProofGroup = "verified";
            var session = new SceneTaggerSession(); session.Settings.Gaze = false;
            session.Settings.Grouping = SceneTaggerGrouping.GroupAllVerified;
            session.Rows.Add(first); session.Rows.Add(second);
            session.Apply();
            Assert.That(first.Target.GetComponent<ConvaiObjectMetadata>(), Is.Null);
            Assert.That(second.Target.GetComponent<ConvaiObjectMetadata>(), Is.Null);
            Assert.That(first.Error, Does.Contain("different approved text"));
            session.Settings.Grouping = SceneTaggerGrouping.KeepSeparate; session.EnsureUniqueNames();
            session.Apply();
            Assert.That(first.Status, Is.EqualTo(SceneTaggerStatus.Applied));
            Assert.That(second.Status, Is.EqualTo(SceneTaggerStatus.Applied));
            Assert.That(first.LogicalGroupId, Is.Not.EqualTo(second.LogicalGroupId));
        }

        [Test] public void DisablingGeneratedActionsRemovesOnlyOwnedTargets()
        {
            GameObject go = Cube(); var row = Ready(go);
            var settings = new SceneTaggerSettings { Gaze = false };
            SceneTaggerApplyService.Apply(new() { new() { row } }, settings);
            row.Status = SceneTaggerStatus.Ready; settings.Actions = false;
            SceneTaggerApplyService.Apply(new() { new() { row } }, settings);
            Assert.That(go.GetComponent<ConvaiActionTarget>(), Is.Null);
            var manual = go.AddComponent<ConvaiActionTarget>(); manual.TargetName = "Manual target";
            row.Status = SceneTaggerStatus.Ready;
            SceneTaggerApplyService.Apply(new() { new() { row } }, settings);
            Assert.That(go.GetComponent<ConvaiActionTarget>(), Is.SameAs(manual));
            Assert.That(manual.TargetName, Is.EqualTo("Manual target"));
        }

        [Test] public void CleanupPreservesEditedAndAddedStandingPositions()
        {
            GameObject go = Cube(); var row = Ready(go);
            SceneTaggerApplyService.Apply(new() { new() { row } }, new SceneTaggerSettings { Gaze = false });
            var owner = go.GetComponent<ConvaiAutoTagOwnership>();
            var points = go.AddComponent<ConvaiObjectMovementPoints>();
            var generated = new ConvaiMovementPoint { Position = Vector3.forward };
            points.Points.Add(generated);
            owner.MovementPoints.Add(new ConvaiAutoTagOwnership.OwnedMovementPoint { Id = generated.Id, Json = JsonUtility.ToJson(generated) });
            SceneTaggerApplyService.Own(owner, points); SceneTaggerApplyService.RecordOwnedComponents(owner);
            generated.Position = Vector3.right;
            var manual = new ConvaiMovementPoint { Name = "Authored", Position = Vector3.back };
            points.Points.Add(manual);
            SceneTaggerApplyService.RecordOwnedComponents(owner);
            SceneTaggerApplyService.RemoveGenerated(new[] { go });
            Assert.That(points, Is.Not.Null);
            Assert.That(points.Points, Has.Count.EqualTo(2));
            Assert.That(points.Points[0].Position, Is.EqualTo(Vector3.right));
            Assert.That(points.Points[1].Name, Is.EqualTo("Authored"));
        }

        [Test] public void SavedOwnershipRemainsApplicableAfterSceneReopen()
        {
            string path = "Assets/SceneTaggerPersistence-" + Guid.NewGuid().ToString("N") + ".unity";
            Scene saved = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            try
            {
                GameObject go = Cube("Saved generated object"); SceneManager.MoveGameObjectToScene(go, saved);
                var row = Ready(go);
                SceneTaggerApplyService.Apply(new() { new() { row } }, new SceneTaggerSettings { Gaze = false });
                string id = go.GetComponent<ConvaiAutoTagOwnership>().ObjectId;
                Assert.That(EditorSceneManager.SaveScene(saved, path), Is.True);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                saved = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                go = saved.GetRootGameObjects().Single();
                var owner = go.GetComponent<ConvaiAutoTagOwnership>();
                Assert.That(owner.ObjectId, Is.EqualTo(id));
                Assert.That(SceneTaggerApplyService.ComponentsUnchanged(owner), Is.True);
                row = Ready(go); row.Description = "Approved after reopening.";
                SceneTaggerApplyService.Apply(new() { new() { row } }, new SceneTaggerSettings { Gaze = false });
                Assert.That(row.Status, Is.EqualTo(SceneTaggerStatus.Applied), row.Error);
                Assert.That(go.GetComponent<ConvaiObjectMetadata>().ObjectDescription, Is.EqualTo(row.Description));
                SceneTaggerApplyService.RemoveGenerated(new[] { go });
                Assert.That(go.GetComponent<ConvaiObjectMetadata>(), Is.Null);
            }
            finally
            {
                if (saved.IsValid() && saved.isLoaded) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                AssetDatabase.DeleteAsset(path);
            }
        }

        [Test] public void MissingOrMismatchedNativeFilesFailBeforeLoading()
        {
            string directory = Path.Combine(Path.GetTempPath(), "convai-native-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "convai_http_helper.dll");
                Assert.Throws<FileNotFoundException>(() => SceneTaggerCoreAdapter.ValidateLibrary(path, "expected"));
                File.WriteAllText(path, "wrong native dependency");
                Assert.Throws<InvalidOperationException>(() => SceneTaggerCoreAdapter.ValidateLibrary(path, "expected"));
            }
            finally { Directory.Delete(directory, true); }
        }

        [Test] public async Task NativeUtf8RoundTripAndCancellationDrainBeforeUnlock()
        {
            var reservation = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            reservation.Start(); int port = ((System.Net.IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            using var listener = new System.Net.HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
            var pixels = new Texture2D(8, 8);
            byte[] png;
            try { pixels.SetPixels(Enumerable.Repeat(Color.red, 64).ToArray()); pixels.Apply(); png = pixels.EncodeToPNG(); }
            finally { Object.DestroyImmediate(pixels); }
            var request = new SceneTaggerRequest { ApiKey = "fixture", CharacterId = "fixture", BaseUrl = $"http://127.0.0.1:{port}", Png = png,
                Cells = new[] { new SceneTaggerCell { Id = "chair-µ", Note = "café" } } };
            var core = SceneTaggerCoreAdapter.Instance;
            Task<SceneTaggerResponse> running = core.AnalyzeAsync(request, CancellationToken.None);
            var incoming = await listener.GetContextAsync();
            using (var reader = new StreamReader(incoming.Request.InputStream)) Assert.That(await reader.ReadToEndAsync(), Does.Contain("café"));
            byte[] response = System.Text.Encoding.UTF8.GetBytes("{\"images\":[{\"image_file_name\":\"objects.png\",\"objects\":[{\"index\":1,\"echoId\":\"chair-µ\",\"name\":\"椅子 😀\",\"description\":\"Une chaise café.\",\"confidence\":0.9}]}]}");
            await Reply(incoming);
            SceneTaggerResponse result = await running;
            Assert.That(result.Objects.Single().Name, Is.EqualTo("椅子 😀"));
            Assert.That(result.Objects.Single().Id, Is.EqualTo("chair-µ"));
            Assert.That(SceneTaggerCoreAdapter.HasActiveCalls, Is.False);

            using var cancellation = new CancellationTokenSource();
            running = core.AnalyzeAsync(request, cancellation.Token);
            incoming = await listener.GetContextAsync();
            using (var reader = new StreamReader(incoming.Request.InputStream)) await reader.ReadToEndAsync();
            cancellation.Cancel();
            Assert.That(SceneTaggerCoreAdapter.HasActiveCalls, Is.True);
            Assert.That(running.IsCompleted, Is.False, "Cancellation must not release a request still running in native code.");
            await Reply(incoming);
            try { await running; Assert.Fail("The cancelled response must not be delivered."); }
            catch (OperationCanceledException) { }
            Assert.That(SceneTaggerCoreAdapter.HasActiveCalls, Is.False);

            async Task Reply(System.Net.HttpListenerContext context)
            {
                context.Response.StatusCode = 200; context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = response.Length;
                await context.Response.OutputStream.WriteAsync(response, 0, response.Length);
                context.Response.Close();
            }
        }

        [Test] public async Task ReviewRetriesKeepFrozenCharacterGuidanceAndQuality()
        {
            var session = ReviewSession(out SceneTaggerRow row);
            session.Settings.SceneDescription = "Original gallery"; session.Settings.Focus = "Visible artwork";
            var requests = new List<SceneTaggerRequest>();
            session.Analyze = (request, _) =>
            {
                requests.Add(request);
                var response = new SceneTaggerResponse { HttpStatus = 200 };
                response.Objects.Add(new SceneTaggerObjectResult { Id = row.Id, Index = 1, Name = "Display box", Description = "A red display box.", Confidence = .8f });
                return Task.FromResult(response);
            };
            await session.RunAsync(new[] { row }, showSummary: true);
            Assert.That(session.ShowSummary, Is.True, "Explore opens the completion summary.");
            string character = requests[0].CharacterId;
            session.Settings.CharacterId = "different-character"; session.Settings.SceneDescription = "Changed";
            session.Settings.Focus = "Changed focus"; session.Settings.Quality = SceneTaggerQuality.Accurate;
            await session.RunAsync(new[] { row });
            Assert.That(requests, Has.Count.EqualTo(2));
            Assert.That(requests[1].CharacterId, Is.EqualTo(character));
            Assert.That(requests[1].SceneDescription, Is.EqualTo("Original gallery"));
            Assert.That(requests[1].Focus, Is.EqualTo("Visible artwork"));
            Assert.That(session.RunSettings.Quality, Is.EqualTo(SceneTaggerQuality.Fast));
            Assert.That(session.State, Is.EqualTo(SceneTaggerState.ReviewReady));
            Assert.That(session.Completed, Is.EqualTo(session.Total));
            Assert.That(session.ShowSummary, Is.False, "Review actions must not reopen the exploration summary.");
        }

        [TestCase(false), TestCase(true)]
        public async Task CancelledReviewSuppressesLateSuggestionsAndKeepsEvidence(bool showSummary)
        {
            var session = ReviewSession(out SceneTaggerRow row);
            var image = row.Evidence;
            var completion = new TaskCompletionSource<SceneTaggerResponse>();
            session.Analyze = (_, _) => completion.Task;
            Task run = session.RunAsync(new[] { row }, showSummary: showSummary);
            session.Cancel();
            completion.SetResult(new SceneTaggerResponse { HttpStatus = 200 });
            await run;
            Assert.That(session.State, Is.EqualTo(SceneTaggerState.Cancelled));
            Assert.That(session.Busy, Is.False);
            Assert.That(row.Evidence, Is.SameAs(image));
            Assert.That(row.Name, Is.EqualTo("Red box"));
            Assert.That(session.Progress, Does.Contain("Completed suggestions remain"));
            Assert.That(session.ShowSummary, Is.EqualTo(showSummary));
        }

        [Test] public async Task LowInformationCanBeExplicitlyAnalyzedButUnreadableCapturesCannot()
        {
            var session = ReviewSession(out SceneTaggerRow row);
            row.Evidence.LowInformation = true; row.Evidence.Failure = "No useful visual detail.";
            int calls = 0;
            session.Analyze = (_, _) => { calls++; return Task.FromResult(new SceneTaggerResponse { HttpStatus = 401 }); };
            await session.RunAsync(new[] { row });
            Assert.That(row.Status, Is.EqualTo(SceneTaggerStatus.Skipped));
            Assert.That(calls, Is.Zero);
            Assert.That(SceneTaggerReview.HasUsableImage(row), Is.True);
            row.AllowLowInformation = true;
            await session.RunAsync(new[] { row });
            Assert.That(calls, Is.EqualTo(1));
            row.Evidence.LowInformation = false;
            await session.RunAsync(new[] { row });
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(SceneTaggerReview.HasUsableImage(row), Is.False);
        }

        [Test] public void ReviewSearchSortingAndBatchEligibilityRespectPendingAndAppliedRows()
        {
            var first = Ready(Cube("First")); first.Confidence = .3f;
            var second = Ready(Cube("Second")); second.Confidence = .9f; second.Note = "Copper sculpture";
            Assert.That(SceneTaggerReview.Sort(new[] { second, first }).First(), Is.SameAs(second));
            Assert.That(SceneTaggerReview.Matches(second, "COPPER"), Is.True);
            Assert.That(SceneTaggerReview.Status(second), Is.EqualTo("Needs analysis"));
            second.Note = ""; second.PendingAnalysis = true;
            Assert.That(SceneTaggerReview.Complete(second), Is.False);
            second.Status = SceneTaggerStatus.Applied;
            Assert.That(SceneTaggerReview.Eligible(second), Is.False);
            Object.DestroyImmediate(first.Target);
            Assert.That(SceneTaggerReview.Status(first), Is.EqualTo("Object missing"));
            Assert.That(SceneTaggerReview.Attention(first), Is.True);
        }

        [TestCase("candidate", "Not analyzed", false, true)]
        [TestCase("ready", "Included", true, false)]
        [TestCase("excluded", "Not included", true, false)]
        [TestCase("applied", "Applied", true, false)]
        [TestCase("capturing", "Processing", false, false)]
        [TestCase("analyzing", "Processing", false, false)]
        [TestCase("pending", "Needs analysis", false, true)]
        [TestCase("note", "Needs analysis", false, true)]
        [TestCase("error", "Attention", false, false)]
        [TestCase("attention", "Attention", false, false)]
        [TestCase("skipped", "Skipped", false, false)]
        [TestCase("missing", "Object missing", false, false)]
        public void ReviewStatusAndResumeAnalysisRespectRowState(string scenario, string status, bool complete, bool resume)
        {
            var row = Ready(Cube());
            row.Confidence = .85f;
            switch (scenario)
            {
                case "candidate": row.Status = SceneTaggerStatus.Candidate; break;
                case "excluded": row.Included = false; break;
                case "applied": row.Status = SceneTaggerStatus.Applied; break;
                case "capturing": row.Status = SceneTaggerStatus.Capturing; break;
                case "analyzing": row.Status = SceneTaggerStatus.Analyzing; break;
                case "pending": row.PendingAnalysis = true; break;
                case "note": row.Note = "Describe the handle"; break;
                case "error": row.Error = "Request failed"; break;
                case "attention": row.Status = SceneTaggerStatus.Attention; break;
                case "skipped": row.Status = SceneTaggerStatus.Skipped; row.Error = "Low visual detail"; break;
                case "missing": Object.DestroyImmediate(row.Target); break;
            }
            Assert.That(SceneTaggerReview.Status(row), Is.EqualTo(status));
            Assert.That(SceneTaggerReview.Complete(row), Is.EqualTo(complete));
            Assert.That(SceneTaggerReview.NeedsIncludedAnalysis(row), Is.EqualTo(resume));
            Assert.That(SceneTaggerReview.ConfidenceLabel(row), Is.EqualTo(complete ? $"{row.Confidence:P0} (uncalibrated)" : "pending"));
            row.Included = false;
            Assert.That(SceneTaggerReview.NeedsIncludedAnalysis(row), Is.False);
        }

        [TestCase("suggested"), TestCase("hierarchy"), TestCase("description")]
        [TestCase("refinement"), TestCase("failure"), TestCase("cache")]
        [TestCase("")]
        public void ReviewSearchFindsEveryVisibleTextFieldIgnoringCase(string query)
        {
            var row = Ready(Cube("Hierarchy"));
            row.Name = "Suggested"; row.Description = "Description"; row.Note = "Refinement";
            row.Error = "Failure"; row.Source = SceneTaggerSource.MetadataCache;
            Assert.That(SceneTaggerReview.Matches(row, query.ToUpperInvariant()), Is.True);
            Assert.That(SceneTaggerReview.Matches(row, "unrelated"), Is.False);
        }

        [Test] public void ReviewOrderDoesNotChangeWhileEditingSuggestionsOrNotes()
        {
            var first = Ready(Cube("A")); var second = Ready(Cube("B"));
            first.Confidence = second.Confidence = .8f;
            var rows = new[] { second, first };
            Assert.That(SceneTaggerReview.Sort(rows), Is.EqualTo(new[] { first, second }));
            first.Note = "Refine this"; first.Name = "Z"; first.PendingAnalysis = true;
            Assert.That(SceneTaggerReview.Sort(rows), Is.EqualTo(new[] { first, second }));
            Assert.That(SceneTaggerReview.Ready(first), Is.False);
            second.Status = SceneTaggerStatus.Applied;
            Assert.That(SceneTaggerReview.Ready(second), Is.False);
        }

        [TestCase(false, "Recapture & analyze checked")]
        [TestCase(true, "Analyze all checked")]
        public void ReviewRecoveryExplainsWhetherToReuseOrRecaptureEvidence(bool retained, string action)
        {
            var row = Ready(Cube()); row.Error = "Request failed";
            if (retained) row.Evidence = new SceneTaggerEvidence { Png = new byte[] { 1 } };
            Assert.That(SceneTaggerReview.Recovery(row), Does.StartWith("Request failed").And.Contain(action));
            Assert.That(SceneTaggerReview.EvidenceCaption(row, false), Does.Contain(action));
            row.Error = ""; row.PendingAnalysis = true;
            Assert.That(SceneTaggerReview.Recovery(row), Does.Contain("saved view has not been analyzed").And.Contain(action));
            row.Note = "Visible handle";
            Assert.That(SceneTaggerReview.Recovery(row), Does.Contain("Analyze the note or clear it before Apply"));
            Object.DestroyImmediate(row.Target);
            Assert.That(SceneTaggerReview.Recovery(row), Does.Contain("no longer exists"));
        }

        [Test] public void RemovedReviewRowsRestoreIdentityAndEditsWithoutRetainingImageMemory()
        {
            var session = ReviewSession(out SceneTaggerRow row); row.PendingAnalysis = false;
            var previousSelection = Selection.objects;
            try
            {
                row.Name = "Edited exhibit"; row.Note = "Keep this correction";
                session.RemoveRows(new[] { row });
                Assert.That(row.Evidence, Is.Null);
                Selection.activeGameObject = row.Target;
                session.Discover(true);
                Assert.That(session.Rows.Single(), Is.SameAs(row));
                Assert.That(session.RestoredCount, Is.EqualTo(1));
                Assert.That(row.Name, Is.EqualTo("Edited exhibit"));
                Assert.That(row.Note, Is.EqualTo("Keep this correction"));
                session.Discover(true);
                Assert.That(session.Rows, Has.Count.EqualTo(1));
                Assert.That(session.SkippedCount, Is.EqualTo(1));
            }
            finally { Selection.objects = previousSelection; }
        }

        [Test] public void UndoAndRedoReconcileAppliedReviewWithoutChangingManualMetadata()
        {
            var session = new SceneTaggerSession(); session.Settings.Gaze = false; session.Settings.Actions = false;
            var row = Ready(Cube()); session.Rows.Add(row);
            session.Apply();
            Assert.That(row.Status, Is.EqualTo(SceneTaggerStatus.Applied));
            Undo.PerformUndo(); session.ReconcileUndo();
            Assert.That(row.Status, Is.EqualTo(SceneTaggerStatus.Ready));
            Undo.PerformRedo(); session.ReconcileUndo();
            Assert.That(row.Status, Is.EqualTo(SceneTaggerStatus.Applied));
            var metadata = row.Target.GetComponent<ConvaiObjectMetadata>();
            metadata.ObjectName = "Manual name"; session.ReconcileUndo();
            Assert.That(row.Status, Is.EqualTo(SceneTaggerStatus.Attention));
            Assert.That(metadata.ObjectName, Is.EqualTo("Manual name"));
            Assert.That(session.LastApplySummary, Does.Contain("Undo/Redo"));
        }

        [Test] public void BoundedGuidancePreservesTypingSpacesAndSurrogatePairs()
        {
            Assert.That(SceneTaggerText.Limit("A ", 20), Is.EqualTo("A "));
            Assert.That(SceneTaggerText.Limit("A\U0001F680B", 2), Is.EqualTo("A"));
            Assert.That(SceneTaggerText.Limit("A\U0001F680B", 3), Is.EqualTo("A\U0001F680"));
        }

        [Test] public async Task RetriesTransportButNotAuthentication()
        {
            var session = new SceneTaggerSession(); int calls = 0;
            session.Analyze = (_, _) => Task.FromResult(new SceneTaggerResponse { HttpStatus = ++calls == 1 ? 503 : 200 });
            Assert.That((await session.RequestWithRetriesAsync(new SceneTaggerRequest(), default)).HttpStatus, Is.EqualTo(200));
            Assert.That(calls, Is.EqualTo(2));
            calls = 0; session.Analyze = (_, _) => { calls++; return Task.FromResult(new SceneTaggerResponse { HttpStatus = 401 }); };
            Assert.That((await session.RequestWithRetriesAsync(new SceneTaggerRequest(), default)).HttpStatus, Is.EqualTo(401));
            Assert.That(calls, Is.EqualTo(1));
            calls = 0; session.Analyze = (_, _) => { calls++; return Task.FromResult(new SceneTaggerResponse { HttpStatus = 0, CanRetryTransport = false }); };
            await session.RequestWithRetriesAsync(new SceneTaggerRequest(), default);
            Assert.That(calls, Is.EqualTo(1), "Missing helpers and invalid native arguments cannot be repaired by retrying.");
        }

        [Test] public async Task ServerFailureRetriesSmallerGroupsOncePerObject()
        {
            var session = ReviewSession(out SceneTaggerRow first);
            first.EditRevision = 1;
            for (int i = 0; i < 3; i++)
            {
                var row = Ready(Cube("Object " + i)); row.Evidence = first.Evidence;
                row.PendingAnalysis = true; row.EditRevision = 1; session.Rows.Add(row);
            }
            var targetCounts = new List<int>();
            session.Analyze = (request, _) =>
            {
                targetCounts.Add(request.Cells.Count(c => !c.ContextOnly));
                if (targetCounts.Count == 1) return Task.FromResult(new SceneTaggerResponse { HttpStatus = 504, Error = "Gateway timeout" });
                var response = new SceneTaggerResponse { HttpStatus = 200 };
                for (int i = 0; i < request.Cells.Length; i++)
                    if (!request.Cells[i].ContextOnly) response.Objects.Add(new SceneTaggerObjectResult
                    { Index = i + 1, Id = request.Cells[i].Id, Name = "Recovered object", Description = "A visible object.", Confidence = .9f });
                return Task.FromResult(response);
            };
            await session.RunAsync(session.Rows.ToArray());
            Assert.That(targetCounts, Is.EqualTo(new[] { 4, 2, 2 }));
            Assert.That(session.Rows.All(r => r.CanApply && string.IsNullOrEmpty(r.Error)), Is.True);
        }

        [Test] public async Task DuplicateRowsShowSharedRequestFailure()
        {
            var session = ReviewSession(out SceneTaggerRow first);
            var duplicate = Ready(Cube("Duplicate")); duplicate.Evidence = first.Evidence; duplicate.PendingAnalysis = true;
            duplicate.Fingerprint = first.Fingerprint; session.Rows.Add(duplicate);
            int calls = 0;
            session.Analyze = (_, _) => { calls++; return Task.FromResult(new SceneTaggerResponse { HttpStatus = 504, Error = "Gateway timeout" }); };
            await session.RunAsync(session.Rows.ToArray());
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(duplicate.Status, Is.EqualTo(SceneTaggerStatus.Attention));
            Assert.That(duplicate.Error, Is.EqualTo(first.Error).And.Not.Empty);
        }

        [TestCase(false), TestCase(true)] public async Task FailedRetakePreservesThePreviousUsableImageAndText(bool cached)
        {
            var session = ReviewSession(out SceneTaggerRow row);
            row.PendingAnalysis = false;
            SceneTaggerEvidence retake = new SceneTaggerEvidence { Png = (byte[])row.Evidence.Png.Clone() };
            if (cached) { row.Evidence = null; row.Source = SceneTaggerSource.MetadataCache; }
            SceneTaggerEvidence previous = row.Evidence;
            session.ReplaceEvidence(row, retake);
            session.Analyze = (_, _) => Task.FromResult(new SceneTaggerResponse { HttpStatus = 401, Error = "Fixture authentication failure" });
            await session.RunAsync(new[] { row });
            Assert.That(row.Evidence, Is.SameAs(previous));
            Assert.That(row.RejectedEvidence, Is.SameAs(retake));
            Assert.That(row.Name, Is.EqualTo("Red box"));
            Assert.That(row.CanApply, Is.True);
            Assert.That(row.Error, Does.Contain("previous result"));
        }

        [Test] public void RetakeRecoveryCannotOverwriteANewerManualEdit()
        {
            var session = ReviewSession(out SceneTaggerRow row); row.PendingAnalysis = false;
            session.ReplaceEvidence(row, new SceneTaggerEvidence { Png = (byte[])row.Evidence.Png.Clone() });
            row.Name = "New manual edit"; row.EditRevision++;
            Assert.That(SceneTaggerSession.RestorePreviousUsable(row), Is.False);
            Assert.That(row.Name, Is.EqualTo("New manual edit"));
            Assert.That(row.PendingAnalysis, Is.True);
        }

        [Test] public async Task ClearingAnUnresolvedNoteRestoresApplyForThePreviousImage()
        {
            var session = ReviewSession(out SceneTaggerRow row); row.PendingAnalysis = false;
            row.Note = "Make the description more specific."; row.NoteRevision++;
            int requests = 0;
            session.Analyze = (request, _) =>
            {
                requests++;
                var response = new SceneTaggerResponse { HttpStatus = 200 };
                response.Objects.Add(new SceneTaggerObjectResult { Id = row.Id, Index = 1, Name = row.Name, Description = row.Description, Confidence = .8f });
                return Task.FromResult(response);
            };
            await session.RunAsync(new[] { row });
            Assert.That(requests, Is.EqualTo(1), "An unchanged correction must not incur a second request.");
            Assert.That(row.Error, Does.Contain("correction did not change"));
            Assert.That(row.CanApply, Is.False);
            row.Note = ""; row.NoteRevision++;
            Assert.That(row.CanApply, Is.True);
            Assert.That(row.Name, Is.EqualTo("Red box"));
        }

        [Test] public async Task PartialResultsKeepValidSiblingAndRetryOnlyMissingCell()
        {
            var session = ReviewSession(out SceneTaggerRow first);
            GameObject secondObject = Cube("Second fixture"); secondObject.transform.localScale = new Vector3(2, 1, 1);
            SceneTaggerRow second = Ready(secondObject); second.Evidence = first.Evidence; second.PendingAnalysis = true;
            session.Rows.Add(second);
            int calls = 0;
            session.Analyze = (request, _) =>
            {
                calls++;
                var response = new SceneTaggerResponse { HttpStatus = 200 };
                response.Objects.Add(new SceneTaggerObjectResult { Id = request.Cells[0].Id, Index = 1,
                    Name = calls == 1 ? "First box" : "Second box", Description = "A visible box.", Confidence = .8f });
                return Task.FromResult(response);
            };
            await session.RunAsync(session.Rows);
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(first.Status, Is.EqualTo(SceneTaggerStatus.Ready));
            Assert.That(second.Status, Is.EqualTo(SceneTaggerStatus.Ready));
            Assert.That(first.Name, Is.EqualTo("First box"));
            Assert.That(second.Name, Is.EqualTo("Second box"));
        }

        [Test] public async Task LateResponseCannotOverwriteANewerEditOrCorrection()
        {
            var session = ReviewSession(out SceneTaggerRow row);
            var completion = new TaskCompletionSource<SceneTaggerResponse>();
            session.Analyze = (_, _) => completion.Task;
            Task running = session.RunAsync(new[] { row });
            row.Name = "User correction"; row.EditRevision++; row.Note = "New note"; row.NoteRevision++;
            var response = new SceneTaggerResponse { HttpStatus = 200 };
            response.Objects.Add(new SceneTaggerObjectResult { Id = row.Id, Index = 1, Name = "Old answer", Description = "Old description", Confidence = .9f });
            completion.SetResult(response);
            await running;
            Assert.That(row.Name, Is.EqualTo("User correction"));
            Assert.That(row.Note, Is.EqualTo("New note"));
            Assert.That(row.CanApply, Is.False);
        }

        [Test] public async Task DuplicateReuseRejectsAChangedAndClearedNote()
        {
            var session = ReviewSession(out SceneTaggerRow first);
            SceneTaggerRow second = Ready(Cube("Duplicate"));
            second.Evidence = first.Evidence; second.PendingAnalysis = true;
            session.Rows.Add(second);
            Assert.That(second.Fingerprint, Is.EqualTo(first.Fingerprint));
            var completion = new TaskCompletionSource<SceneTaggerResponse>();
            int calls = 0;
            session.Analyze = (_, _) => { calls++; return completion.Task; };
            Task running = session.RunAsync(session.Rows);
            second.Note = "New guidance"; second.NoteRevision++;
            second.Note = ""; second.NoteRevision++;
            var response = new SceneTaggerResponse { HttpStatus = 200 };
            response.Objects.Add(new SceneTaggerObjectResult { Id = first.Id, Index = 1, Name = "Analyzed box", Description = "New proposal", Confidence = .9f });
            completion.SetResult(response);
            await running;
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(first.Name, Is.EqualTo("Analyzed box"));
            Assert.That(second.Name, Is.EqualTo("Red box"));
            Assert.That(second.PendingAnalysis, Is.True);
            Assert.That(second.CanApply, Is.False);
        }

        [Test] public async Task ContextCellsExcludeFailedAndExcludedImages()
        {
            var session = ReviewSession(out SceneTaggerRow row);
            SceneTaggerRow excluded = Ready(Cube("Excluded")), failed = Ready(Cube("Failed capture"));
            excluded.Included = false; excluded.Evidence = new SceneTaggerEvidence { Png = new byte[] { 1 } };
            failed.Evidence = new SceneTaggerEvidence { Png = new byte[] { 2 }, Failure = "Unreadable" };
            session.Rows.Add(excluded); session.Rows.Add(failed);
            session.Analyze = (request, _) =>
            {
                Assert.That(request.Cells, Has.Length.EqualTo(1));
                var response = new SceneTaggerResponse { HttpStatus = 200 };
                response.Objects.Add(new SceneTaggerObjectResult { Id = row.Id, Index = 1, Name = "Box", Description = "An approved image", Confidence = .8f });
                return Task.FromResult(response);
            };
            await session.RunAsync(new[] { row });
            Assert.That(row.Status, Is.EqualTo(SceneTaggerStatus.Ready), row.Error);
            Assert.That(row.PendingAnalysis, Is.False);
        }

        [Test] public async Task GeometryChangedDuringAnalysisRejectsTheResponse()
        {
            var session = ReviewSession(out SceneTaggerRow row);
            var completion = new TaskCompletionSource<SceneTaggerResponse>();
            session.Analyze = (_, _) => completion.Task;
            Task running = session.RunAsync(new[] { row });
            row.Target.transform.localScale = new Vector3(2, 1, 1);
            var response = new SceneTaggerResponse { HttpStatus = 200 };
            response.Objects.Add(new SceneTaggerObjectResult { Id = row.Id, Index = 1, Name = "Stale proposal", Description = "Old geometry", Confidence = .9f });
            completion.SetResult(response);
            await running;
            Assert.That(row.Name, Is.EqualTo("Red box"));
            Assert.That(row.Status, Is.EqualTo(SceneTaggerStatus.Attention));
            Assert.That(row.Error, Does.Contain("geometry changed"));
            Assert.That(row.CanApply, Is.False);
        }

        [Test] public void StandingPositionGenerationUsesNavMeshAndPreservesManualChanges()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            UnityEngine.AI.NavMeshDataInstance instance = default;
            try
            {
                var settings = UnityEngine.AI.NavMesh.GetSettingsByIndex(0);
                var sources = new List<UnityEngine.AI.NavMeshBuildSource> {
                    new() { shape = UnityEngine.AI.NavMeshBuildSourceShape.Box, size = new Vector3(20, .2f, 20),
                        transform = Matrix4x4.TRS(new Vector3(0, -.1f, 0), Quaternion.identity, Vector3.one), area = 0 }
                };
                var data = UnityEngine.AI.NavMeshBuilder.BuildNavMeshData(settings, sources, new Bounds(Vector3.zero, new Vector3(25, 5, 25)), Vector3.zero, Quaternion.identity);
                Assert.That(data, Is.Not.Null); _assets.Add(data);
                instance = UnityEngine.AI.NavMesh.AddNavMeshData(data);
                GameObject target = Cube("Standing position exhibit"); SceneManager.MoveGameObjectToScene(target, scene);
                target.transform.position = new Vector3(0, .5f, 0);
                var owner = target.AddComponent<ConvaiAutoTagOwnership>();
                int generated = SceneTaggerMovementGenerator.Generate(target, false);
                Assert.That(generated, Is.InRange(1, 2));
                var points = target.GetComponent<ConvaiObjectMovementPoints>();
                foreach (ConvaiMovementPoint point in points.Points)
                {
                    Vector3 position = points.WorldPosition(point);
                    Assert.That(UnityEngine.AI.NavMesh.SamplePosition(position, out var hit, .15f, UnityEngine.AI.NavMesh.AllAreas), Is.True);
                    Vector3 look = Vector3.ProjectOnPlane(target.transform.position - position, Vector3.up).normalized;
                    Assert.That(Vector3.Dot(target.transform.rotation * point.Rotation * Vector3.forward, look), Is.GreaterThan(.99f));
                }
                ConvaiMovementPoint edited = points.Points[0]; edited.Name = "Authored standing point";
                Assert.That(SceneTaggerMovementGenerator.Generate(target, true), Is.InRange(1, 2));
                Assert.That(points.Points, Does.Contain(edited));
                SceneTaggerMovementGenerator.Clear(owner);
                Assert.That(points.Points, Has.Count.EqualTo(1));
                Assert.That(points.Points[0].Name, Is.EqualTo("Authored standing point"));
                instance.Remove();
                Assert.That(SceneTaggerMovementGenerator.Generate(target, false), Is.Zero);
                Assert.That(points.Points, Has.Count.EqualTo(1));
            }
            finally
            {
                if (instance.valid) instance.Remove();
                if (scene.IsValid() && scene.isLoaded) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        [Test] public async Task InvalidDescriptionCannotBecomeApplicable()
        {
            var session = ReviewSession(out SceneTaggerRow row);
            session.Analyze = (_, _) =>
            {
                var response = new SceneTaggerResponse { HttpStatus = 200 };
                response.Objects.Add(new SceneTaggerObjectResult { Id = row.Id, Index = 1, Name = "Box", Description = "", Confidence = .9f });
                return Task.FromResult(response);
            };
            await session.RunAsync(new[] { row });
            Assert.That(row.Status, Is.EqualTo(SceneTaggerStatus.Attention));
            Assert.That(row.CanApply, Is.False);
        }

        private SceneTaggerSession ReviewSession(out SceneTaggerRow row)
        {
            row = Ready(Cube());
            var image = new Texture2D(16, 16);
            try { image.SetPixels(Enumerable.Repeat(Color.red, 256).ToArray()); image.Apply(); row.Evidence = new SceneTaggerEvidence { Png = image.EncodeToPNG() }; }
            finally { Object.DestroyImmediate(image); }
            row.PendingAnalysis = true;
            var session = new SceneTaggerSession { Credentials = () => ("fixture", "http://127.0.0.1") };
            session.Settings.CharacterId = "fixture";
            session.Settings.Grouping = SceneTaggerGrouping.KeepSeparate;
            session.Rows.Add(row);
            return session;
        }

        [Test] public void LogicalGroupsRetainPhysicalMembersAndUseCanonicalMetadata()
        {
            var first = Cube().AddComponent<ConvaiObjectMetadata>(); first.ObjectName = "Chair";
            var second = Cube().AddComponent<ConvaiObjectMetadata>(); second.transform.position = new Vector3(10, 0, 0);
            var root = new GameObject("Logical chairs"); SceneManager.MoveGameObjectToScene(root, _scene);
            var group = root.AddComponent<ConvaiLogicalObjectGroup>(); group.Metadata.ObjectName = "Chairs";
            group.Members.Add(first); group.Members.Add(second); group.NotifyMembershipChanged();
            Assert.That(group.ClosestMember(new Vector3(9, 0, 0)), Is.SameAs(second));
            var action = root.AddComponent<ConvaiActionTarget>(); action.MetadataSource = group.Metadata;
            Assert.That(action.TargetName, Is.EqualTo("Chairs"));
            group.Metadata.ObjectName = "Seats"; Assert.That(action.TargetName, Is.EqualTo("Seats"));
            second.gameObject.SetActive(false); Assert.That(group.ClosestMember(new Vector3(9, 0, 0)), Is.SameAs(first));
        }

        [UnityTest] public IEnumerator RenderedCaptureHasForegroundAndPreservesSource()
        {
            GameObject go = Cube();
            var material = new Material(FixtureShader());
            _assets.Add(material); material.color = new Color(.8f, .15f, .06f);
            go.GetComponent<Renderer>().sharedMaterial = material;
            var lightObject = new GameObject("Fixture light"); SceneManager.MoveGameObjectToScene(lightObject, _scene);
            Light light = lightObject.AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 2;
            light.transform.rotation = Quaternion.Euler(30, -30, 0);
            string before = EditorJsonUtility.ToJson(go.GetComponent<Renderer>());
            SceneTaggerRow row = SceneTaggerDiscovery.Create(go, true);
            using var capture = new SceneTaggerCaptureService(row, 384);
            Task<SceneTaggerEvidence> task = capture.CaptureAsync(new SceneTaggerSettings(), CancellationToken.None);
            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted) throw task.Exception.GetBaseException();
            Assert.That(task.Result.Png.Length, Is.GreaterThan(1000));
            Assert.That(EditorJsonUtility.ToJson(go.GetComponent<Renderer>()), Is.EqualTo(before));
            Assert.That(go.GetComponents<Component>(), Has.Length.EqualTo(4));
            Directory.CreateDirectory("Library/ConvaiSceneTagger/Qualification");
            File.WriteAllBytes("Library/ConvaiSceneTagger/Qualification/fixture.png", task.Result.Png);
        }

        [TestCase("Floor lamp")]
        [TestCase("Wall clock")]
        public void MountedPropsAreNotPenalizedAsArchitecture(string name)
        {
            GameObject root = Cube(name);
            GameObject part = Cube("Detail"); part.transform.SetParent(root.transform);
            var material = new Material(FixtureShader()) { name = "Authored surface" }; _assets.Add(material);
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>()) renderer.sharedMaterial = material;
            Assert.That(SceneTaggerDiscovery.Create(root, false).Significance, Is.GreaterThanOrEqualTo(new SceneTaggerSettings().Relevance));
            root.name = "Wall";
            Assert.That(SceneTaggerDiscovery.Create(root, false).Significance, Is.LessThan(new SceneTaggerSettings().Relevance));
        }

        [UnityTest] public IEnumerator SceneContextRendersFreshPixelsAndRestoresCameraTarget()
        {
            var go = new GameObject("Scene context camera"); SceneManager.MoveGameObjectToScene(go, _scene);
            var camera = go.AddComponent<Camera>(); camera.scene = _scene; camera.enabled = false;
            camera.transform.position = new Vector3(0, 0, -4);
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.blue;
            var target = new RenderTexture(192, 96, 24); _assets.Add(target); target.Create(); camera.targetTexture = target;
            RenderTexture old = RenderTexture.active;
            try { RenderTexture.active = target; GL.Clear(true, true, Color.black); }
            finally { RenderTexture.active = old; }
            var material = new Material(FixtureShader()) { color = Color.red }; _assets.Add(material);
            material.EnableKeyword("_EMISSION"); material.SetColor("_EmissionColor", Color.red);
            Cube("Visible object").GetComponent<Renderer>().sharedMaterial = material;
            Task<byte[]> task = SceneTaggerCaptureService.CaptureSceneViewAsync(camera, CancellationToken.None);
            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted) throw task.Exception.GetBaseException();
            var image = new Texture2D(2, 2); _assets.Add(image); image.LoadImage(task.Result);
            Assert.That(image.GetPixels32().Count(p => p.r > p.b + 30), Is.GreaterThan(100), "Render visible geometry instead of returning the cleared camera target.");
            Assert.That(camera.targetTexture, Is.SameAs(target));
            Assert.That(image.width, Is.EqualTo(192)); Assert.That(image.height, Is.EqualTo(96));
        }

        [UnityTest] public IEnumerator CaptureExposureChangesPixelsWithoutLeakingVolumeState()
        {
            var go = new GameObject("Exposure camera"); SceneManager.MoveGameObjectToScene(go, _scene);
            var camera = go.AddComponent<Camera>(); camera.scene = _scene; camera.enabled = false;
            camera.transform.position = new Vector3(0, 0, -4);
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            Shader shader = FixtureShader();
            // HDRP's physical sky can saturate even a black lit material through specular reflection.
            if (shader.name == "HDRP/Lit") shader = Shader.Find("HDRP/Unlit");
            var material = new Material(shader) { color = Color.black }; _assets.Add(material);
            material.EnableKeyword("_EMISSION");
            material.SetColor(material.HasProperty("_EmissiveColor") ? "_EmissiveColor" : "_EmissionColor", new Color(.06f, .06f, .06f));
            FindLoadedType("UnityEngine.Rendering.HighDefinition.HDMaterial")?.GetMethod("ValidateMaterial")?.Invoke(null, new object[] { material });
            Cube("Emissive panel").GetComponent<Renderer>().sharedMaterial = material;
            var target = new RenderTexture(128, 128, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB); _assets.Add(target); target.Create();
            var image = new Texture2D(128, 128, TextureFormat.RGBA32, false); _assets.Add(image);
            ISceneTaggerRenderPipeline pipeline = SceneTaggerRenderPipelines.Resolve();
            ConfigureFixedHdrpFixtureExposure();
            pipeline.Configure(camera, _scene, _assets);
            try
            {
                for (int frame = 0; frame < 3; frame++) { yield return null; pipeline.Render(camera, target); }
                pipeline.SetExposure(camera, 0); pipeline.Render(camera, target);
                float original = Center();
                Assert.That(original, Is.InRange(.05f, .7f), "The emissive fixture must start below saturation so exposure can be measured. Pixel: " + image.GetPixel(64, 64) + " Shader: " + material.shader.name);
                pipeline.SetExposure(camera, 2);
                AssertCaptureVolumesDisabled();
                pipeline.Render(camera, target);
                AssertCaptureVolumesDisabled();
                float brighter = Center();
                pipeline.SetExposure(camera, 0); pipeline.Render(camera, target);
                Assert.That(brighter, Is.GreaterThan(original + .12f), "Standalone render requests must apply their camera's exposure volume.");
                Assert.That(Center(), Is.EqualTo(original).Within(.02f), "Resetting capture exposure must restore the original pixels.");
            }
            finally { pipeline.Cleanup(ConvaiObjectId.Of(camera), camera.scene); }
            void AssertCaptureVolumesDisabled()
            {
                foreach (GameObject root in camera.scene.GetRootGameObjects())
                    if (root.name == "Capture exposure" || root.name == "Scene volume")
                    {
                        Assert.That(root.layer, Is.InRange(8, 31));
                        foreach (Behaviour component in root.GetComponents<Behaviour>())
                            if (component.GetType().Name == "Volume") Assert.That(component.enabled, Is.False, "Capture volumes must not register between requests.");
                    }
            }
            float Center()
            {
                RenderTexture old = RenderTexture.active;
                try { RenderTexture.active = target; image.ReadPixels(new Rect(0, 0, 128, 128), 0, 0); return image.GetPixel(64, 64).r; }
                finally { RenderTexture.active = old; }
            }
        }

        private static Type FindLoadedType(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);

        private void ConfigureFixedHdrpFixtureExposure()
        {
            Type exposureType = FindLoadedType("UnityEngine.Rendering.HighDefinition.Exposure");
            if (exposureType == null) return;
            // HDRP projects may default to automatic exposure, which saturates this small emissive
            // panel against a black background. Give the fixture a deterministic authored baseline.
            Type profileType = FindLoadedType("UnityEngine.Rendering.VolumeProfile");
            Type volumeType = FindLoadedType("UnityEngine.Rendering.Volume");
            var profile = ScriptableObject.CreateInstance(profileType); _assets.Add(profile);
            var exposure = (Object)profileType.GetMethod("Add", new[] { typeof(Type), typeof(bool) }).Invoke(profile, new object[] { exposureType, true });
            _assets.Add(exposure);
            object mode = exposureType.GetField("mode").GetValue(exposure);
            var modeValue = mode.GetType().GetProperty("value");
            modeValue.SetValue(mode, Enum.ToObject(modeValue.PropertyType, 0));
            var go = new GameObject("Fixture exposure"); SceneManager.MoveGameObjectToScene(go, _scene);
            Component volume = go.AddComponent(volumeType);
            volumeType.GetProperty("isGlobal").SetValue(volume, true);
            volumeType.GetField("sharedProfile").SetValue(volume, profile);
            volumeType.GetField("priority").SetValue(volume, 1000f);
        }

        [Test] public void CapturePreservesCustomAndPerMaterialProbeData()
        {
            Renderer source = Cube().GetComponent<Renderer>(), target = Cube().GetComponent<Renderer>();
            var block = new MaterialPropertyBlock();
            Vector4 authored = new Vector4(.1f, .2f, .3f, .4f);
            block.SetVector("unity_SHAr", authored);
            block.SetColor("_Color", Color.blue);
            source.SetPropertyBlock(block);
            block.SetVector("unity_SHAr", authored * 2);
            source.SetPropertyBlock(block, 0);
            source.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.CustomProvided;
            SceneTaggerCaptureService.CopyLightingProperties(source, target);
            target.GetPropertyBlock(block);
            Assert.That(block.GetVector("unity_SHAr"), Is.EqualTo(authored));
            target.GetPropertyBlock(block, 0);
            Assert.That(block.GetVector("unity_SHAr"), Is.EqualTo(authored * 2));
            Assert.That(block.GetColor("_Color"), Is.EqualTo(Color.blue));

            block.Clear(); block.SetColor("_Color", Color.red);
            source.SetPropertyBlock(block); source.SetPropertyBlock(block, 0);
            source.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.BlendProbes;
            SceneTaggerCaptureService.CopyLightingProperties(source, target);
            target.GetPropertyBlock(block, 0);
            Assert.That(block.GetVectorArray("unity_SHAr"), Has.Length.EqualTo(1));
            Assert.That(block.GetVectorArray("unity_ProbesOcclusion"), Has.Length.EqualTo(1));
            Assert.That(block.GetColor("_Color"), Is.EqualTo(Color.red));
            source.GetPropertyBlock(block, 0);
            Assert.That(block.HasProperty("unity_SHAr"), Is.False, "Capture must not modify the source block.");

            source.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            SceneTaggerCaptureService.CopyLightingProperties(source, target);
            Assert.That(target.lightProbeUsage, Is.EqualTo(UnityEngine.Rendering.LightProbeUsage.Off));
            target.GetPropertyBlock(block);
            Assert.That(block.HasProperty("unity_SHAr"), Is.False);
        }

        [UnityTest] public IEnumerator GeometryMaskHonorsAlphaClippedPropertyBlocks()
        {
            GameObject go = Cube();
            var material = new Material(FixtureShader()); _assets.Add(material);
            material.EnableKeyword("_ALPHATEST_ON");
            if (material.HasProperty("_AlphaClip")) material.SetFloat("_AlphaClip", 1);
            if (material.HasProperty("_AlphaCutoffEnable")) material.SetFloat("_AlphaCutoffEnable", 1);
            if (material.HasProperty("_Cutoff")) material.SetFloat("_Cutoff", .5f);
            go.GetComponent<Renderer>().sharedMaterial = material;
            var block = new MaterialPropertyBlock();
            block.SetColor(material.HasProperty("_BaseColor") ? "_BaseColor" : "_Color", Color.clear);
            go.GetComponent<Renderer>().SetPropertyBlock(block);
            SceneTaggerRow row = SceneTaggerDiscovery.Create(go, true);
            using var capture = new SceneTaggerCaptureService(row, 128);
            var pixels = capture.ReadAsync(CancellationToken.None);
            while (!pixels.IsCompleted) yield return null;
            if (pixels.IsFaulted) throw pixels.Exception.GetBaseException();
            Assert.That(SceneTaggerImageQuality.Measure(pixels.Result).HasGeometry, Is.False,
                "Fully clipped pixels must not be counted as dark foreground.");
            Assert.That(go.GetComponent<Renderer>().sharedMaterial, Is.SameAs(material));
            capture.Dispose();
            var session = new SceneTaggerSession { Credentials = () => ("fixture", "http://127.0.0.1") };
            session.Settings.CharacterId = "fixture"; session.Rows.Add(row);
            int requests = 0;
            session.Analyze = (_, _) => { requests++; return Task.FromResult(new SceneTaggerResponse { HttpStatus = 200 }); };
            Task analysis = session.RunAsync(new[] { row });
            while (!analysis.IsCompleted) yield return null;
            if (analysis.IsFaulted) throw analysis.Exception.GetBaseException();
            Assert.That(row.Status, Is.EqualTo(SceneTaggerStatus.Attention));
            Assert.That(row.Evidence, Is.Not.Null, row.Error);
            Assert.That(row.Evidence.Png, Is.Not.Empty, "Rejected pixels must remain available for review.");
            Assert.That(row.Evidence.Failure, Is.Not.Empty);
            Assert.That(row.CanApply, Is.False);
            Assert.That(requests, Is.Zero, "Do not upload rejected evidence.");
        }

        [UnityTest] public IEnumerator ManualRetakeKeepsDisplayedPixelsAndCamera()
        {
            GameObject go = Cube();
            var material = new Material(FixtureShader()); _assets.Add(material); material.color = Color.red;
            go.GetComponent<Renderer>().sharedMaterial = material;
            SceneTaggerRow row = SceneTaggerDiscovery.Create(go, true);
            using var capture = new SceneTaggerCaptureService(row, 384);
            Task<SceneTaggerEvidence> automatic = capture.CaptureAsync(new SceneTaggerSettings(), CancellationToken.None);
            while (!automatic.IsCompleted) yield return null;
            if (automatic.IsFaulted) throw automatic.Exception.GetBaseException();
            capture.Render();
            Vector3 displayed = capture.LocalDirection;
            var pixels = capture.ReadAsync(CancellationToken.None, true);
            while (!pixels.IsCompleted) yield return null;
            byte[] displayedPng = SceneTaggerCaptureService.Encode(pixels.Result);
            capture.LocalDirection = -displayed; // A pending mouse event has not rendered yet.
            var committed = capture.CommitAsync(CancellationToken.None);
            while (!committed.IsCompleted) yield return null;
            if (committed.IsFaulted) throw committed.Exception.GetBaseException();
            Assert.That(committed.Result.LocalDirection, Is.EqualTo(displayed));
            Assert.That(committed.Result.PresentationDirection, Is.EqualTo(displayed));
            Assert.That(committed.Result.Png, Is.EqualTo(displayedPng));
        }

        [Test] public void NativeImportersAreEditorOnlyAndNeverPreloaded()
        {
            string root = "Packages/com.convai.convai-sdk-for-unity/Plugins/SceneAutoTaggerCore/Editor/";
            foreach (string file in new[] { "Windows/x86_64/convai_scene_auto_tagger_core.dll", "Windows/x86_64/convai_http_helper.dll", "macOS/libconvai_scene_auto_tagger_core.dylib", "macOS/libconvai_http_helper.dylib" })
            {
                var importer = AssetImporter.GetAtPath(root + file) as PluginImporter;
                Assert.That(importer, Is.Not.Null, file);
                Assert.That(importer.GetCompatibleWithEditor(), Is.True, file);
                Assert.That(importer.GetCompatibleWithAnyPlatform(), Is.False, file);
                Assert.That(importer.isPreloaded, Is.False, file);
                foreach (BuildTarget target in new[] { BuildTarget.StandaloneWindows64, BuildTarget.StandaloneOSX, BuildTarget.Android, BuildTarget.iOS, BuildTarget.WebGL })
                    Assert.That(importer.GetCompatibleWithPlatform(target), Is.False, file + " / " + target);
            }
        }

        [UnityTest, Explicit("Uploads a generated fixture image to the configured Convai Vision endpoint."), Category("LiveBackend"), Category("LiveValidation")]
        public IEnumerator LiveCaptureAnalyzeCorrectRetakeApplyUndo()
        {
            var character = ConvaiObjectFind.All<Convai.Runtime.Components.ConvaiCharacter>(FindObjectsInactive.Exclude)
                .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.CharacterId));
            var projectSettings = Convai.Runtime.ConvaiSettings.Instance;
            if (string.IsNullOrWhiteSpace(projectSettings?.ApiKey))
                Assert.Inconclusive("Live qualification needs project credentials.");
            string characterId = character ? character.CharacterId : null;
            if (string.IsNullOrWhiteSpace(characterId))
            {
                var options = Convai.Runtime.ConvaiRestOptionsFactory.CreateForAuthoring().CharacterApiV2 ??
                    (projectSettings.ApiEnvironment == Convai.Runtime.ConvaiApiEnvironment.Beta ?
                        Convai.CharacterApi.ConvaiCharacterApiOptions.Preview(projectSettings.ApiKey) :
                        Convai.CharacterApi.ConvaiCharacterApiOptions.Production(projectSettings.ApiKey));
                using var client = new Convai.CharacterApi.ConvaiCharacterApiClient(options);
                var catalog = client.Characters.ListAsync(1, 0);
                while (!catalog.IsCompleted) yield return null;
                if (catalog.IsFaulted) Assert.Fail("The account character catalog request failed.");
                characterId = catalog.Result.Items?.FirstOrDefault()?.CharacterId;
            }
            Assert.That(characterId, Is.Not.Null.And.Not.Empty, "Account character catalog returned no character.");
            GameObject go = Cube("Generated red box fixture");
            var material = new Material(FixtureShader());
            _assets.Add(material); material.color = Color.red; go.GetComponent<Renderer>().sharedMaterial = material;
            SceneTaggerRow row = SceneTaggerDiscovery.Create(go, true);
            var session = new SceneTaggerSession();
            session.Settings.CharacterId = characterId;
            session.Settings.Gaze = false;
            session.Rows.Add(row);
            Task task = session.RunAsync(new[] { row });
            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted) throw task.Exception.GetBaseException();
            Assert.That(row.Status, Is.EqualTo(SceneTaggerStatus.Ready), row.Error);
            Assert.That(row.Evidence.Png, Is.Not.Empty);
            row.Note = "Describe only the visible red box. Do not infer what is inside it."; row.NoteRevision++;
            task = session.RunAsync(new[] { row });
            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted) throw task.Exception.GetBaseException();
            Assert.That(row.Status, Is.EqualTo(SceneTaggerStatus.Ready), row.Error);
            Assert.That(row.Note, Is.Empty);
            using (var capture = new SceneTaggerCaptureService(row, 768))
            {
                capture.LocalDirection = new Vector3(1, .4f, 1).normalized;
                var retake = capture.CommitAsync(CancellationToken.None);
                while (!retake.IsCompleted) yield return null;
                if (retake.IsFaulted) throw retake.Exception.GetBaseException();
                session.ReplaceEvidence(row, retake.Result);
            }
            Assert.That(row.CanApply, Is.False);
            task = session.RunAsync(new[] { row });
            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted) throw task.Exception.GetBaseException();
            Assert.That(row.CanApply, Is.True, row.Error);
            string approved = row.Description;
            session.Apply();
            Assert.That(go.GetComponent<ConvaiObjectMetadata>().ObjectDescription, Is.EqualTo(approved));
            Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
            Assert.That(go.GetComponent<ConvaiObjectMetadata>(), Is.Null);
        }

        private static SceneTaggerRow Ready(GameObject go)
        {
            SceneTaggerRow row = SceneTaggerDiscovery.Create(go, true);
            row.Name = "Red box"; row.Description = "A red box."; row.Status = SceneTaggerStatus.Ready;
            return row;
        }
    }
}
