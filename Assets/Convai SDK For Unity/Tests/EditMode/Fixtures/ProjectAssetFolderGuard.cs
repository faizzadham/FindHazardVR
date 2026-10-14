using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace Convai.Tests.EditMode.Fixtures
{
    /// <summary>
    ///     Removes what a test caused the SDK to write into the project's own asset folder, and
    ///     nothing else.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Some editor services write for the user, not for the caller: copy-on-write gives a
    ///         character its own profile under <c>Assets/Convai/…</c> when there is no prefab to
    ///         keep it beside. A test that exercises one of those services therefore creates a file
    ///         in a folder it does not own — the folder the SDK reserves for the developer — and
    ///         deleting the test's GameObjects does nothing about it.
    ///     </para>
    ///     <para>
    ///         Left alone it accumulates: this project was carrying
    ///         <c>SetupTestCharacter_BodyAnimation 31.asset</c>, which is to say thirty-one earlier
    ///         runs each left one behind, in the one folder a developer is most likely to mistake
    ///         for their own work.
    ///     </para>
    ///     <para>
    ///         So the folder is photographed before the test and compared after: anything that was
    ///         not there before is the test's and goes, anything that was is the developer's and
    ///         stays. A folder the test caused to exist goes with it.
    ///     </para>
    /// </remarks>
    public sealed class ProjectAssetFolderGuard
    {
        /// <summary>The folder the SDK writes a user's generated assets into.</summary>
        public const string ProjectAssetRoot = "Assets/Convai";

        private readonly string _root;
        private readonly bool _rootExistedBefore;
        private readonly HashSet<string> _before = new();
        private readonly HashSet<string> _foldersBefore = new();

        private ProjectAssetFolderGuard(string root)
        {
            _root = root;
            _rootExistedBefore = AssetDatabase.IsValidFolder(root);
            if (!_rootExistedBefore) return;

            foreach (string guid in AssetDatabase.FindAssets(string.Empty, new[] { root }))
                _before.Add(AssetDatabase.GUIDToAssetPath(guid));

            CollectFolders(root, _foldersBefore);
        }

        /// <summary>
        ///     Folders are recorded separately because <see cref="AssetDatabase.FindAssets(string,string[])" />
        ///     does not return them — and copy-on-write creates one per module, so without this the
        ///     assets go and four empty folders stay.
        /// </summary>
        private static void CollectFolders(string root, ISet<string> into)
        {
            foreach (string sub in AssetDatabase.GetSubFolders(root))
            {
                into.Add(sub);
                CollectFolders(sub, into);
            }
        }

        /// <summary>Photographs <see cref="ProjectAssetRoot" /> as it is now. Call from <c>SetUp</c>.</summary>
        public static ProjectAssetFolderGuard Watch() => new(ProjectAssetRoot);

        /// <summary>Photographs <paramref name="root" /> as it is now.</summary>
        public static ProjectAssetFolderGuard Watch(string root) => new(root);

        /// <summary>
        ///     Deletes everything that appeared under the watched folder since <see cref="Watch()" />.
        ///     Call from <c>TearDown</c>.
        /// </summary>
        public void RemoveWhatTheTestWrote()
        {
            if (!AssetDatabase.IsValidFolder(_root)) return;

            if (!_rootExistedBefore)
            {
                AssetDatabase.DeleteAsset(_root);
                return;
            }

            var added = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets(string.Empty, new[] { _root }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!_before.Contains(path)) added.Add(path);
            }

            // Deepest first, so a folder is empty by the time its own turn comes.
            added.Sort((left, right) => right.Length.CompareTo(left.Length));
            foreach (string path in added)
                AssetDatabase.DeleteAsset(path);

            var foldersNow = new HashSet<string>();
            CollectFolders(_root, foldersNow);

            var addedFolders = new List<string>();
            foreach (string folder in foldersNow)
                if (!_foldersBefore.Contains(folder))
                    addedFolders.Add(folder);

            // Folders go through FileUtil with their meta named explicitly. DeleteAsset on a folder
            // created earlier in the same run leaves the meta behind often enough to matter, and an
            // orphaned meta is exactly the kind of thing a developer finds and cannot explain.
            addedFolders.Sort((left, right) => right.Length.CompareTo(left.Length));
            foreach (string folder in addedFolders)
            {
                FileUtil.DeleteFileOrDirectory(folder);
                FileUtil.DeleteFileOrDirectory(folder + ".meta");
            }

            RemoveOrphanedMetaFiles();
            if (added.Count > 0 || addedFolders.Count > 0) AssetDatabase.Refresh();
        }

        /// <summary>
        ///     Deletes any <c>.meta</c> under the watched folder whose asset is gone.
        /// </summary>
        /// <remarks>
        ///     Deleting a folder the same frame it was created can leave its meta behind, and an
        ///     orphaned meta is exactly the kind of thing a developer finds in their project and
        ///     cannot explain. Only files with nothing beside them are touched.
        /// </remarks>
        private void RemoveOrphanedMetaFiles()
        {
            string projectRoot = Directory.GetParent(UnityEngine.Application.dataPath)!.FullName;
            string absolute = Path.Combine(projectRoot, _root);
            if (!Directory.Exists(absolute)) return;

            foreach (string meta in Directory.GetFiles(absolute, "*.meta", SearchOption.AllDirectories))
            {
                string owned = meta[..^".meta".Length];
                if (File.Exists(owned) || Directory.Exists(owned)) continue;

                File.Delete(meta);
            }
        }
    }
}
