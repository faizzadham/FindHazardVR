#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

[InitializeOnLoad]
public class AutoAdbReverseTunnel : IPreprocessBuildWithReport, IPostprocessBuildWithReport
{
    public int callbackOrder => 0;

    static AutoAdbReverseTunnel()
    {
        // Automatically run when Unity Editor loads or recompiles scripts
        EditorApplication.delayCall += () =>
        {
            SetupReverseTunnel(silent: true);
        };
    }

    public void OnPreprocessBuild(BuildReport report)
    {
        if (report.summary.platform == BuildTarget.Android)
        {
            SetupReverseTunnel(silent: false);
        }
    }

    public void OnPostprocessBuild(BuildReport report)
    {
        if (report.summary.platform == BuildTarget.Android)
        {
            SetupReverseTunnel(silent: false);
        }
    }

    [MenuItem("Tools/FindHazard/Setup ADB USB Reverse Tunnel (Port 8000)")]
    public static void ManualSetupTunnel()
    {
        bool success = SetupReverseTunnel(silent: false);
        if (success)
        {
            EditorUtility.DisplayDialog("FindHazard VR Tunnel", "ADB Reverse Tunnel established successfully!\n\nQuest 3 port 8000 is now linked to PC localhost:8000.", "OK");
        }
        else
        {
            EditorUtility.DisplayDialog("FindHazard VR Tunnel", "Could not establish reverse tunnel. Please ensure your Meta Quest 3 is connected via USB and recognized in Developer Hub / ADB.", "OK");
        }
    }

    public static bool SetupReverseTunnel(bool silent = false)
    {
        string adbPath = FindAdbExecutable();
        if (string.IsNullOrEmpty(adbPath) || !File.Exists(adbPath))
        {
            if (!silent) Debug.LogWarning("[AutoAdbReverseTunnel] adb.exe not found in Unity Android SDK.");
            return false;
        }

        try
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = "reverse tcp:8000 tcp:8000",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (Process process = Process.Start(psi))
            {
                process.WaitForExit(3000);
                string output = process.StandardOutput.ReadToEnd().Trim();
                string error = process.StandardError.ReadToEnd().Trim();

                if (process.ExitCode == 0)
                {
                    Debug.Log($"<color=green>[AutoAdbReverseTunnel] Successfully mapped Quest 3 USB port 8000 -> PC port 8000: {output}</color>");
                    return true;
                }
                else
                {
                    if (!silent) Debug.LogWarning($"[AutoAdbReverseTunnel] adb reverse returned code {process.ExitCode}: {error}");
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            if (!silent) Debug.LogWarning($"[AutoAdbReverseTunnel] Could not run adb: {ex.Message}");
            return false;
        }
    }

    private static string FindAdbExecutable()
    {
        // 1. Try Unity's built-in Android SDK path
        string unityDataPath = EditorApplication.applicationContentsPath;
        string adbInUnity = Path.Combine(unityDataPath, "PlaybackEngines", "AndroidPlayer", "SDK", "platform-tools", "adb.exe");
        if (File.Exists(adbInUnity)) return adbInUnity;

        // 2. Try Android SDK Root from EditorPrefs
        string sdkPath = EditorPrefs.GetString("AndroidSdkRoot", "");
        if (!string.IsNullOrEmpty(sdkPath))
        {
            string candidate = Path.Combine(sdkPath, "platform-tools", "adb.exe");
            if (File.Exists(candidate)) return candidate;
        }

        // 3. Fallback to standard local AppData path
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string defaultSdkAdb = Path.Combine(localAppData, "Android", "Sdk", "platform-tools", "adb.exe");
        if (File.Exists(defaultSdkAdb)) return defaultSdkAdb;

        return "adb.exe";
    }
}
#endif
