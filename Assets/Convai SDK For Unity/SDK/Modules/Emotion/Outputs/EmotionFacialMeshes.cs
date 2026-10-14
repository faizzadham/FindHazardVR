using System;
using System.Collections.Generic;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Runtime.Embodiment;
using UnityEngine;

namespace Convai.Modules.Emotion.Outputs
{
    internal static class EmotionFacialMeshes
    {
        internal static IReadOnlyList<SkinnedMeshRenderer> Resolve(
            IStandardRigBinding rig,
            Component fallbackContext)
        {
            if (rig != null && rig.FacialMeshes != null && rig.FacialMeshes.Count > 0)
                return rig.FacialMeshes;

            if (fallbackContext == null) return Array.Empty<SkinnedMeshRenderer>();

            Transform root = rig?.Root;
            if (root == null)
            {
                EmbodimentContext context = fallbackContext.GetComponentInParent<EmbodimentContext>(true);
                root = context != null ? context.CharacterRoot : fallbackContext.transform;
            }

            SkinnedMeshRenderer[] discovered = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);

            var filtered = new List<SkinnedMeshRenderer>(discovered.Length);
            for (int i = 0; i < discovered.Length; i++)
            {
                SkinnedMeshRenderer smr = discovered[i];
                if (smr == null || smr.sharedMesh == null) continue;
                if (smr.sharedMesh.blendShapeCount == 0) continue;
                filtered.Add(smr);
            }
            return filtered;
        }
    }
}
