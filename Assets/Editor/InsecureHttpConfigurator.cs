using UnityEditor;
using UnityEngine;

/// <summary>
/// Automatically ensures that HTTP connections are enabled across both PC (XR Simulator)
/// and Android (Meta Quest 3 Headset) without manual Player Settings configuration.
/// </summary>
[InitializeOnLoad]
public static class InsecureHttpConfigurator
{
    static InsecureHttpConfigurator()
    {
        ApplyHttpSetting();
    }

    [MenuItem("FindHazard/Enable HTTP Network Access")]
    public static void ApplyHttpSetting()
    {
        if (PlayerSettings.insecureHttpOption != InsecureHttpOption.AlwaysAllowed)
        {
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
            Debug.Log("<color=green>[FindHazard] InsecureHttpOption successfully set to AlwaysAllowed for XR Simulator & VR Headset.</color>");
        }
    }
}
