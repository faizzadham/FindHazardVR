using UnityEngine;
using UnityEditor;
using System.IO;

public class BatchMaterialTextureFixer : EditorWindow
{
    private DefaultAsset materialFolder;
    private DefaultAsset textureFolder;
    private float defaultSmoothness = 0.25f;

    [MenuItem("Tools/Fix Plain URP Materials")]
    public static void ShowWindow()
    {
        GetWindow<BatchMaterialTextureFixer>("URP Material Fixer");
    }

    private void OnGUI()
    {
        GUILayout.Label("Batch Fix Converted URP Materials", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        materialFolder = (DefaultAsset)EditorGUILayout.ObjectField("Materials Folder", materialFolder, typeof(DefaultAsset), false);
        textureFolder = (DefaultAsset)EditorGUILayout.ObjectField("Textures Folder", textureFolder, typeof(DefaultAsset), false);
        defaultSmoothness = EditorGUILayout.Slider("Smoothness", defaultSmoothness, 0f, 1f);

        EditorGUILayout.Space();

        if (GUILayout.Button("Fix & Assign Textures", GUILayout.Height(35)))
        {
            if (materialFolder == null || textureFolder == null)
            {
                EditorUtility.DisplayDialog("Error", "Please assign both the Materials and Textures folders.", "OK");
                return;
            }

            FixMaterials();
        }
    }

    private void FixMaterials()
    {
        string matPath = AssetDatabase.GetAssetPath(materialFolder);
        string texPath = AssetDatabase.GetAssetPath(textureFolder);

        string[] matGuids = AssetDatabase.FindAssets("t:Material", new[] { matPath });
        string[] texGuids = AssetDatabase.FindAssets("t:Texture2D", new[] { texPath });

        int fixedCount = 0;

        foreach (string mGuid in matGuids)
        {
            string mPath = AssetDatabase.GUIDToAssetPath(mGuid);
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(mPath);

            if (mat == null) continue;

            string matBaseName = Path.GetFileNameWithoutExtension(mPath).ToLower();

            // Check if material already lost its main texture or still has legacy _MainTex in memory
            Texture currentTex = mat.GetTexture("_BaseMap");
            if (currentTex == null && mat.HasProperty("_MainTex"))
            {
                currentTex = mat.GetTexture("_MainTex");
            }

            // If still null, search the texture folder for a matching name
            if (currentTex == null)
            {
                foreach (string tGuid in texGuids)
                {
                    string tPath = AssetDatabase.GUIDToAssetPath(tGuid);
                    string texName = Path.GetFileNameWithoutExtension(tPath).ToLower();

                    // Match base names (e.g. material "urban_building_01" -> texture containing "urban_building_01" or "albedo"/"diffuse")
                    if (texName.Contains(matBaseName) && !texName.Contains("normal") && !texName.Contains("bump") && !texName.Contains("_n"))
                    {
                        currentTex = AssetDatabase.LoadAssetAtPath<Texture2D>(tPath);
                        break;
                    }
                }
            }

            // Assign base texture and parameters
            if (currentTex != null)
            {
                mat.SetTexture("_BaseMap", currentTex);
                mat.SetFloat("_Smoothness", defaultSmoothness);
                EditorUtility.SetDirty(mat);
                fixedCount++;
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog("Success", $"Updated {fixedCount} materials with textures!", "OK");
    }
}