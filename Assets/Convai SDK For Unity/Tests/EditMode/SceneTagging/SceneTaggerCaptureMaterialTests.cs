using System.Collections;
using System.Threading;
using Convai.Editor.SceneTagging;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.SceneTagging
{
    public sealed class SceneTaggerCaptureMaterialTests
    {
        [UnityTest] public IEnumerator CachedMaskRefreshesSharedMaterialAlphaDuringEditView()
        {
            Scene scene = EditorSceneManager.NewPreviewScene();
            Material material = null;
            try
            {
                var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
                Shader shader = Shader.Find(!pipeline ? "Standard" : pipeline.GetType().Name.Contains("HDRender") ? "HDRP/Lit" : "Universal Render Pipeline/Lit");
                material = new Material(shader);
                material.EnableKeyword("_ALPHATEST_ON");
                if (material.HasProperty("_AlphaClip")) material.SetFloat("_AlphaClip", 1);
                if (material.HasProperty("_AlphaCutoffEnable")) material.SetFloat("_AlphaCutoffEnable", 1);
                string color = material.HasProperty("_BaseColor") ? "_BaseColor" : "_Color";
                var target = GameObject.CreatePrimitive(PrimitiveType.Cube);
                SceneManager.MoveGameObjectToScene(target, scene);
                target.GetComponent<Renderer>().sharedMaterial = material;
                using var capture = new SceneTaggerCaptureService(SceneTaggerDiscovery.Create(target, true), 128);
                foreach (bool opaque in new[] { true, false, true })
                {
                    material.SetColor(color, opaque ? Color.white : Color.clear);
                    var pixels = capture.ReadAsync(CancellationToken.None);
                    while (!pixels.IsCompleted) yield return null;
                    if (pixels.IsFaulted) throw pixels.Exception.GetBaseException();
                    Assert.That(SceneTaggerImageQuality.Measure(pixels.Result).HasGeometry, Is.EqualTo(opaque));
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                if (material) Object.DestroyImmediate(material);
            }
        }
    }
}
