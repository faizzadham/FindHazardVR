using Convai.Runtime.SceneMetadata;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Convai.Samples.SceneTagger.Editor
{
    internal static class SceneTaggerSample
    {
        [MenuItem("Convai/Samples/Create Scene Tagger Fixture")]
        private static void Create()
        {
            string folder = AssetDatabase.GenerateUniqueAssetPath("Assets/SceneTaggerFixture");
            AssetDatabase.CreateFolder("Assets", System.IO.Path.GetFileName(folder));
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            Shader shader = Shader.Find(!pipeline ? "Standard" : pipeline.GetType().Name.Contains("HDRender") ? "HDRP/Lit" : "Universal Render Pipeline/Lit");
            Material red = Material("Red exhibit", new Color(.7f, .08f, .04f), 0);
            Material metal = Material("Metal sculpture", new Color(.5f, .5f, .55f), .95f);
            Material dark = Material("Dark sculpture", new Color(.025f, .03f, .025f), 0);
            Material grey = Material("Floor", new Color(.3f, .3f, .3f), 0);
            Shape("Floor", PrimitiveType.Cube, new Vector3(0, -.2f, 0), new Vector3(14, .4f, 12), grey);
            Shape("Artifact red box", PrimitiveType.Cube, new Vector3(-3, .7f, 0), new Vector3(1.3f, 1.4f, 1.1f), red);
            Shape("Metal sculpture", PrimitiveType.Sphere, new Vector3(0, 1, 0), Vector3.one * 2, metal);
            Shape("Dark sculpture", PrimitiveType.Capsule, new Vector3(3, 1, 0), Vector3.one, dark);
            for (int i = 0; i < 3; i++) Shape("Repeated exhibit " + (i + 1), PrimitiveType.Cylinder, new Vector3(-3 + i * 3, .6f, 3), new Vector3(.8f, .6f, .8f), red);
            GameObject artwork = Shape("Artwork panel", PrimitiveType.Cube, new Vector3(0, 2, -3), new Vector3(2.4f, 2, .08f), red);
            artwork.transform.rotation = Quaternion.Euler(0, 25, 0);
            GameObject authored = Shape("Authored exhibit", PrimitiveType.Cube, new Vector3(5, .5f, 2), Vector3.one, grey);
            var metadata = authored.AddComponent<ConvaiObjectMetadata>();
            metadata.ObjectName = "Hand-authored exhibit";
            metadata.ObjectDescription = "This description should survive discovery, Apply and generated-data cleanup.";
            var lightObject = new GameObject("Fixture sunlight");
            SceneManager.MoveGameObjectToScene(lightObject, scene);
            Light light = lightObject.AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1.5f;
            light.transform.rotation = Quaternion.Euler(45, -25, 0);
            EditorSceneManager.SaveScene(scene, folder + "/SceneTaggerFixture.unity");
            if (SceneView.lastActiveSceneView) SceneView.lastActiveSceneView.LookAt(new Vector3(0, 1, 0), Quaternion.Euler(25, -20, 0), 12);
            EditorApplication.ExecuteMenuItem("Convai/Scene Auto Tagger");

            Material Material(string name, Color color, float metallic)
            {
                var material = new Material(shader) { name = name, color = color };
                if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metallic);
                AssetDatabase.CreateAsset(material, folder + "/" + name + ".mat");
                return material;
            }
            GameObject Shape(string name, PrimitiveType type, Vector3 position, Vector3 scale, Material material)
            {
                GameObject go = GameObject.CreatePrimitive(type); go.name = name;
                SceneManager.MoveGameObjectToScene(go, scene);
                go.transform.position = position; go.transform.localScale = scale;
                go.GetComponent<Renderer>().sharedMaterial = material;
                return go;
            }
        }
    }
}
