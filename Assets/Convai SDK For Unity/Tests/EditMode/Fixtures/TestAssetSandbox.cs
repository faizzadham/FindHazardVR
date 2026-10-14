using System.IO;
using UnityEditor;

namespace Convai.Tests.EditMode.Fixtures
{
    /// <summary>
    ///     Where a test puts an asset it has to create, and how that asset stops existing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Some tests genuinely need a file on disk — an editor tool that writes a profile, a
    ///         prefab whose serialized form is the thing under test. What they must not do is leave
    ///         it behind. Deleting in <c>TearDown</c> is most of that and every fixture here already
    ///         did it, but it only holds while the run finishes: a run that is killed — an editor
    ///         that hangs, a domain reload mid-suite — never reaches the teardown, and the files stay
    ///         in the developer's project looking like something they authored.
    ///     </para>
    ///     <para>
    ///         So the folder is claimed rather than created: <see cref="Folder" /> deletes whatever
    ///         is there before handing it back, which means the next run cleans up after the one that
    ///         died. And it is one root instead of a folder per fixture at the top of
    ///         <c>Assets/</c>, so anything that does survive is recognisable at a glance as a test's
    ///         and can be deleted in one move.
    ///     </para>
    /// </remarks>
    public static class TestAssetSandbox
    {
        /// <summary>The one folder tests write into. Nothing outside it is a test's to create.</summary>
        public const string Root = "Assets/ConvaiTestSandbox";

        /// <summary>
        ///     Whether this editor session has already swept what an earlier run may have left.
        /// </summary>
        private static bool _sweptLeftovers;

        /// <summary>
        ///     An empty folder for one fixture, created if it is missing and emptied if an earlier
        ///     run left something in it.
        /// </summary>
        /// <param name="fixtureName">
        ///     Names the folder — use the fixture's own type name so a survivor says where it came from.
        /// </param>
        /// <returns>The folder's asset path, without a trailing slash.</returns>
        public static string Folder(string fixtureName)
        {
            SweepLeftoversOnce();
            string path = $"{Root}/{fixtureName}";
            if (AssetDatabase.IsValidFolder(path)) AssetDatabase.DeleteAsset(path);

            EnsureRoot();
            AssetDatabase.CreateFolder(Root, fixtureName);
            return path;
        }

        /// <summary>
        ///     <paramref name="fixtureName" />'s folder, created if it is not there yet and left
        ///     alone if it is — for a fixture that fills it one asset at a time.
        /// </summary>
        public static string Ensure(string fixtureName)
        {
            SweepLeftoversOnce();
            string path = $"{Root}/{fixtureName}";
            if (AssetDatabase.IsValidFolder(path)) return path;

            EnsureRoot();
            AssetDatabase.CreateFolder(Root, fixtureName);
            return path;
        }

        /// <summary>
        ///     A path inside <paramref name="fixtureName" />'s folder for an asset about to be
        ///     created, with the folder in place.
        /// </summary>
        /// <remarks>
        ///     The folder is created here rather than left to the caller because Unity's asset APIs
        ///     do not fail loudly on a missing one: <c>GenerateUniqueAssetPath</c> returns an empty
        ///     string, <c>CreateAsset</c> then reports "Creating asset at path  failed", and
        ///     <c>SaveScene</c> takes an empty path as a request to ask the user — a modal dialog in
        ///     the middle of a test run.
        /// </remarks>
        public static string Path(string fixtureName, string assetName) =>
            $"{Ensure(fixtureName)}/{assetName}";

        /// <summary>Removes one fixture's folder and everything in it.</summary>
        public static void Clear(string fixtureName)
        {
            string path = $"{Root}/{fixtureName}";
            if (AssetDatabase.IsValidFolder(path)) AssetDatabase.DeleteAsset(path);

            RemoveRootIfEmpty();
        }

        /// <summary>Removes the sandbox entirely, so nothing a test wrote outlives the run.</summary>
        public static void ClearAll()
        {
            if (AssetDatabase.IsValidFolder(Root)) AssetDatabase.DeleteAsset(Root);
        }

        /// <summary>
        ///     Removes the whole sandbox the first time a fixture asks for space in this editor
        ///     session — that is how a run killed before its teardown gets cleaned up, by the next
        ///     run rather than by hand.
        /// </summary>
        private static void SweepLeftoversOnce()
        {
            if (_sweptLeftovers) return;

            _sweptLeftovers = true;
            ClearAll();
        }

        private static void EnsureRoot()
        {
            if (!AssetDatabase.IsValidFolder(Root))
                AssetDatabase.CreateFolder("Assets", Root["Assets/".Length..]);
        }

        /// <summary>
        ///     Leaves nothing behind once the last fixture is done, so a finished run is invisible in
        ///     the Project window.
        /// </summary>
        private static void RemoveRootIfEmpty()
        {
            if (!AssetDatabase.IsValidFolder(Root)) return;

            string absolute = System.IO.Path.Combine(
                Directory.GetParent(UnityEngine.Application.dataPath)!.FullName, Root);
            if (!Directory.Exists(absolute)) return;

            bool empty = Directory.GetFileSystemEntries(absolute).Length == 0;
            if (empty) AssetDatabase.DeleteAsset(Root);
        }
    }
}
