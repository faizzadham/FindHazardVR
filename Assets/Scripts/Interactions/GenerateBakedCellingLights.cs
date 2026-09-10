using UnityEngine;
using UnityEditor;

public class GenerateBakedCeilingLights : MonoBehaviour
{
    [MenuItem("Tools/Generate Baked Ceiling Lights")]
    public static void SpawnLights()
    {
        GameObject[] bulbs = GameObject.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
        int count = 0;

        foreach (GameObject bulb in bulbs)
        {
            if (bulb.name.StartsWith("LightBulb") && bulb.transform.Find("PointLight_Baked") == null)
            {
                GameObject lightObj = new GameObject("PointLight_Baked");
                lightObj.transform.SetParent(bulb.transform);
                lightObj.transform.localPosition = new Vector3(0, -0.2f, 0);

                Light lightComp = lightObj.AddComponent<Light>();
                lightComp.type = LightType.Point;
                lightComp.lightmapBakeType = LightmapBakeType.Baked;
                lightComp.color = new Color(1.0f, 0.93f, 0.82f); // Warm white
                lightComp.intensity = 15f;
                lightComp.range = 14f;

                GameObjectUtility.SetStaticEditorFlags(lightObj, StaticEditorFlags.ContributeGI);
                count++;
            }
        }
        Debug.Log($"[FindHazard] Successfully spawned {count} Baked Point Lights!");
    }
}
