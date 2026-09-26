using UnityEngine;

public class HazardFeedback : MonoBehaviour
{
    [Header("Hazard Configuration")]
    [Tooltip("Check if this object is an actual warehouse hazard. Uncheck for safe distractors.")]
    public bool isHazard = false;

    [Header("Feedback Materials")]
    [Tooltip("Material applied when a hazard is identified (Red)")]
    public Material hazardFoundMaterial;

    [Tooltip("Material applied when a non-hazard is clicked (Green)")]
    public Material nonHazardMaterial;

    [HideInInspector]
    public bool isIdentified = false; // Must be public so InteractableHoverGlow can check it

    public void OnObjectClicked()
    {
        if (isIdentified)
        {
            Debug.Log($"[HazardFeedback] '{gameObject.name}' was already clicked.");
            return;
        }

        isIdentified = true;

        // Permanently lock hover glow so hover-exit won't erase the Red/Green material
        InteractableHoverGlow hoverGlow = GetComponent<InteractableHoverGlow>();
        if (hoverGlow != null)
        {
            hoverGlow.LockFeedback();
        }

        if (isHazard)
        {
            ApplyMaterialToAll(hazardFoundMaterial);

            if (TrainingGameManager.Instance != null)
            {
                TrainingGameManager.Instance.AddHazardFound();
            }

            Debug.Log($"<color=red>[HazardFeedback] Hazard Tagged (Red)! +1 Point awarded.</color>");
        }
        else
        {
            ApplyMaterialToAll(nonHazardMaterial);
            Debug.Log($"<color=green>[HazardFeedback] Safe Non-Hazard Tagged (Green).</color>");
        }
    }

    private void ApplyMaterialToAll(Material targetMaterial)
    {
        if (targetMaterial == null) return;

        MeshRenderer[] renderers = GetComponentsInChildren<MeshRenderer>();

        foreach (MeshRenderer rend in renderers)
        {
            Material[] newMaterials = new Material[rend.sharedMaterials.Length];
            for (int i = 0; i < newMaterials.Length; i++)
            {
                newMaterials[i] = targetMaterial;
            }
            rend.materials = newMaterials;
        }
    }
}