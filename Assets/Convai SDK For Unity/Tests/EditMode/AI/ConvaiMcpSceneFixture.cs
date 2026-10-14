using System;
using System.Linq;
using Convai.Shared.Compatibility;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Convai.Tests.EditMode.AI
{
    /// <summary>
    ///     The scene a Convai MCP tool test builds its objects in, and the editor state it puts back
    ///     afterwards. One instance per test: <see cref="Begin" /> in <c>SetUp</c>,
    ///     <see cref="End" /> in <c>TearDown</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why a scene at all.</b> These tests drive editor tools that read and write the
    ///         loaded scene. They need somewhere to put a character that is not the developer's own
    ///         work, and they must leave the editor exactly as they found it.
    ///     </para>
    ///     <para>
    ///         <b>How the strategy is chosen.</b> Additively, whenever the editor allows it — that
    ///         keeps the developer's scenes open and their selection intact. Unity refuses to create
    ///         any additive scene while an <em>untitled</em> scene is loaded, and it refuses on the
    ///         path alone: a brand-new empty scene that has never been touched is untitled and still
    ///         blocks it. That is the ordinary state of a freshly opened editor, so the fallback is
    ///         not an exotic path — it is the common one, and it takes the editor over for the
    ///         length of one test.
    ///     </para>
    ///     <para>
    ///         <b>What it will never do.</b> Take over anything that cannot be given back. A scene
    ///         with unsaved changes that has no path cannot be reopened, so rather than discard a
    ///         developer's work the test is ignored with a message saying what to do. An untitled
    ///         scene with nothing unsaved in it costs nothing to replace and is recreated in the
    ///         same shape — empty or with Unity's default objects — when the test ends.
    ///     </para>
    ///     <para>
    ///         An earlier revision decided all of this from <c>-batchmode</c> on the command line and
    ///         tried to defeat the refusal by clearing the scene's dirty flag. Both were guesses
    ///         about a rule that is really about the path, so every one of these tests failed in an
    ///         ordinary editor session while passing in CI. The strategy is now chosen by asking
    ///         what the editor can actually do.
    ///     </para>
    /// </remarks>
    internal sealed class ConvaiMcpSceneFixture
    {
        private Scene _previousActiveScene;
        private SceneSetup[] _setupToReopen;
        private NewSceneSetup _untitledSceneToRecreate;
        private bool _tookTheEditorOver;

        /// <summary>The scene this test owns. Objects a test creates belong in here.</summary>
        public Scene TestScene { get; private set; }

        /// <summary>Opens a scene for one test and remembers how to put the editor back.</summary>
        public static ConvaiMcpSceneFixture Begin()
        {
            var fixture = new ConvaiMcpSceneFixture { _previousActiveScene = SceneManager.GetActiveScene() };

            if (AdditiveScenesAreAllowed())
            {
                fixture.TestScene = CreateAdditiveScene();
                SceneManager.SetActiveScene(fixture.TestScene);
                return fixture;
            }

            fixture.TakeTheEditorOver();
            return fixture;
        }

        /// <summary>
        ///     Gives this test the whole loaded scene population to itself, for the questions the
        ///     product answers by looking at every open scene.
        /// </summary>
        /// <remarks>
        ///     "This is the only Convai character loaded" is a claim about the fixture, not about
        ///     the product, and any scene the developer happens to have open falsifies it — the
        ///     shipped LipSync Sample scene alone is enough. Only a contaminated setup is disturbed,
        ///     so a run that is already alone with its own scene does nothing here.
        /// </remarks>
        /// <returns>Whether the scene setup was taken over.</returns>
        public bool IsolateScenePopulation<T>() where T : Component
        {
            if (_tookTheEditorOver) return false;

            Scene ours = TestScene;
            T[] loaded = ConvaiObjectFind.All<T>(FindObjectsInactive.Include);
            if (!loaded.Any(item => item != null && item.gameObject.scene != ours)) return false;

            if (TestScene.IsValid() && TestScene.isLoaded)
                EditorSceneManager.CloseScene(TestScene, true);

            TakeTheEditorOver();
            return true;
        }

        /// <summary>Puts back what <see cref="Begin" /> found.</summary>
        public void End()
        {
            if (_tookTheEditorOver)
            {
                // Reopening replaces every open scene in one step, so closing the test's scene first
                // would only unload the last loaded scene and warn about it.
                if (_setupToReopen is { Length: > 0 })
                    EditorSceneManager.RestoreSceneManagerSetup(_setupToReopen);
                else
                    EditorSceneManager.NewScene(_untitledSceneToRecreate, NewSceneMode.Single);

                _tookTheEditorOver = false;
                _setupToReopen = null;
                return;
            }

            if (_previousActiveScene.IsValid() && _previousActiveScene.isLoaded)
                SceneManager.SetActiveScene(_previousActiveScene);

            if (TestScene.IsValid() && TestScene.isLoaded)
                EditorSceneManager.CloseScene(TestScene, true);
        }

        /// <summary>
        ///     Replaces the editor's scene setup with an empty scene for this test, after recording
        ///     enough to give it back.
        /// </summary>
        private void TakeTheEditorOver()
        {
            SceneSetup[] setup = EditorSceneManager.GetSceneManagerSetup();
            bool everyOpenSceneCanBeReopened = setup.Length > 0 && setup.All(entry => !string.IsNullOrEmpty(entry.path));

            if (!everyOpenSceneCanBeReopened && AnyOpenSceneHasUnsavedChanges())
                Assert.Ignore(
                    "This test needs a scene of its own, and the scene currently open has unsaved " +
                    "changes but has never been saved, so it cannot be closed and reopened. Save " +
                    "the open scene (or close it) and run the test again.");

            // A never-saved scene has no path to reopen, so remember its shape instead. It is known
            // to hold nothing unsaved by the check above, which leaves only the two shapes Unity
            // itself creates.
            _untitledSceneToRecreate = SceneManager.GetActiveScene().rootCount == 0
                ? NewSceneSetup.EmptyScene
                : NewSceneSetup.DefaultGameObjects;

            _setupToReopen = everyOpenSceneCanBeReopened ? setup : null;
            _tookTheEditorOver = true;

            TestScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        /// <summary>
        ///     Whether Unity will create an additive scene right now. It refuses while any loaded
        ///     scene is untitled, whether or not that scene has been modified.
        /// </summary>
        private static bool AdditiveScenesAreAllowed()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (string.IsNullOrEmpty(SceneManager.GetSceneAt(i).path))
                    return false;

            return true;
        }

        /// <summary>Adds an empty scene beside the developer's.</summary>
        /// <remarks>
        ///     Nothing is done to the open scenes' dirty flags on the way through, because adding a
        ///     scene does not touch them — measured both ways on 6000.4, with a saved scene clean
        ///     and with the same scene modified. A previous revision cleared and restored them
        ///     through a reflected, undocumented <c>EditorSceneManager.ClearSceneDirtiness</c>,
        ///     believing that was what the refusal was about. It is not, and a private editor API
        ///     the SDK cannot see across its supported editor range is not something to keep for a
        ///     problem that does not exist.
        /// </remarks>
        private static Scene CreateAdditiveScene() =>
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);

        private static bool AnyOpenSceneHasUnsavedChanges()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isDirty)
                    return true;

            return false;
        }
    }
}
