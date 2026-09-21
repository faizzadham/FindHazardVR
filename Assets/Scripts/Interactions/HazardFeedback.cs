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

    private bool isIdentified = false;

    public void OnObjectClicked()
    {
        if (isIdentified)
        {
            Debug.Log($"[HazardFeedback] '{gameObject.name}' was already clicked.");
            return;
        }

        isIdentified = true;

        if (isHazard)
        {
            // Apply Red material to ALL sub-material slots
            ApplyMaterialToAll(hazardFoundMaterial);

            if (TrainingGameManager.Instance != null)
            {
                TrainingGameManager.Instance.AddHazardFound();
            }

            Debug.Log($"<color=red>[HazardFeedback] Hazard Tagged (Red)! +1 Point awarded.</color>");
        }
        else
        {
            // Apply Green material to ALL sub-material slots
            ApplyMaterialToAll(nonHazardMaterial);

            Debug.Log($"<color=green>[HazardFeedback] Safe Non-Hazard Tagged (Green).</color>");
        }
    }

    private void ApplyMaterialToAll(Material targetMaterial)
    {
        if (targetMaterial == null) return;

        // Grab all MeshRenderers on this object and any children
        MeshRenderer[] renderers = GetComponentsInChildren<MeshRenderer>();

        foreach (MeshRenderer rend in renderers)
        {
            // Create an array matching the exact number of sub-material slots
            Material[] newMaterials = new Material[rend.sharedMaterials.Length];

            for (int i = 0; i < newMaterials.Length; i++)
            {
                newMaterials[i] = targetMaterial;
            }

            // Assign the entire array back to the renderer
            rend.materials = newMaterials;
        }
    }
}
