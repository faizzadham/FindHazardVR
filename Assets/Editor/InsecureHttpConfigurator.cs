using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Automatically ensures that HTTP connections are enabled across both PC (XR Simulator)
/// and Android (Meta Quest 3 Headset), and disables background MCP server auto-start
/// to prevent port collision errors during Play Mode.
/// </summary>
[InitializeOnLoad]
public static class InsecureHttpConfigurator
{
    static InsecureHttpConfigurator()
    {
        ApplyHttpSetting();
        DisableMcpAutoStart();
    }

    [MenuItem("FindHazard/Enable HTTP & Fix Simulation Settings")]
    public static void ApplyAllSettings()
    {
        ApplyHttpSetting();
        DisableMcpAutoStart();
        Debug.Log("<color=green>[FindHazard] All simulation & VR headset settings verified and applied!</color>");
    }

    public static void ApplyHttpSetting()
    {
        if (PlayerSettings.insecureHttpOption != InsecureHttpOption.AlwaysAllowed)
        {
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
            Debug.Log("<color=green>[FindHazard] InsecureHttpOption set to AlwaysAllowed for XR Simulator & VR Headset.</color>");
        }
    }

    public static void DisableMcpAutoStart()
    {
        try
        {
            // 1. Update McpUnitySettings in memory
            Type settingsType = Type.GetType("McpUnity.Unity.McpUnitySettings, McpUnity.Editor");
            if (settingsType != null)
            {
                PropertyInfo instanceProp = settingsType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                object instance = instanceProp?.GetValue(null);
                if (instance != null)
                {
                    FieldInfo autoStartField = settingsType.GetField("AutoStartServer", BindingFlags.Public | BindingFlags.Instance);
                    if (autoStartField != null)
                    {
                        autoStartField.SetValue(instance, false);
                    }
                    MethodInfo saveMethod = settingsType.GetMethod("SaveSettings", BindingFlags.Public | BindingFlags.Instance);
                    saveMethod?.Invoke(instance, null);
                }
            }

            // 2. Stop running McpUnityServer if listening so port is released
            Type serverType = Type.GetType("McpUnity.Unity.McpUnityServer, McpUnity.Editor");
            if (serverType != null)
            {
                PropertyInfo instanceProp = serverType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                object serverInstance = instanceProp?.GetValue(null);
                if (serverInstance != null)
                {
                    MethodInfo stopMethod = serverType.GetMethod("StopServer", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    stopMethod?.Invoke(serverInstance, null);
                }
            }
        }
        catch
        {
            // Silently handle if MCP Unity is not present
        }
    }
}
