using Convai.Editor.SceneTagging;
using Convai.Runtime.SceneMetadata;
using Convai.Modules.Gaze.Providers;
using UnityEditor;
using UnityEngine;

namespace Convai.Editor.Embodiment
{
    [InitializeOnLoad]
    internal static class SceneTaggerEmbodimentIntegration
    {
        static SceneTaggerEmbodimentIntegration() => SceneTaggerApplyService.ConfigureCompanions += Configure;
        private static void Configure(GameObject target, ConvaiAutoTagOwnership owner, SceneTaggerSettings settings)
        {
            if (!settings.Gaze) return;
            WorldObjectGazeTargetProvider provider = target.GetComponent<WorldObjectGazeTargetProvider>();
            if (!provider)
            {
                provider = Undo.AddComponent<WorldObjectGazeTargetProvider>(target);
                SceneTaggerApplyService.Own(owner, provider);
            }
        }
    }
}
