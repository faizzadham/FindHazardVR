using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace LiveKit.Editor
{
    [Serializable]
    public sealed class FfiLockEntry
    {
        public string folder;
        public string sha256;
    }

    /// <summary>
    /// Per-project record of which native FFI zips are installed and from which release.
    /// Lives beside the ffi-* folders so a project checkout, not a machine, owns it.
    /// </summary>
    [Serializable]
    public sealed class FfiLockFile
    {
        public const string FileName = "livekit-ffi.lock.json";

        public string ffiTag;
        public List<FfiLockEntry> installed = new List<FfiLockEntry>();

        public string FfiTag => ffiTag;

        public string HashFor(string folder)
        {
            foreach (FfiLockEntry entry in installed)
            {
                if (string.Equals(entry.folder, folder, StringComparison.OrdinalIgnoreCase))
                    return entry.sha256;
            }

            return null;
        }

        public void Record(string folder, string sha256)
        {
            installed.RemoveAll(e => string.Equals(e.folder, folder, StringComparison.OrdinalIgnoreCase));
            installed.Add(new FfiLockEntry { folder = folder, sha256 = sha256 });
        }

        public void Remove(string folder)
        {
            installed.RemoveAll(e => string.Equals(e.folder, folder, StringComparison.OrdinalIgnoreCase));
        }

        public static FfiLockFile Read(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return null;

                FfiLockFile lockFile = JsonUtility.FromJson<FfiLockFile>(File.ReadAllText(path));
                return string.IsNullOrEmpty(lockFile?.ffiTag) ? null : lockFile;
            }
            catch
            {
                return null;
            }
        }

        public void Write(string path)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, JsonUtility.ToJson(this, true));
        }
    }
}
