using UnityEngine;

/// <summary>
/// Changes the visual feedback (color/material) when clicked by an XR Interactor.
/// </summary>
public class HazardFeedback : MonoBehaviour
{
    [Header("Hazard Configuration")]
    [Tooltip("Check if this object is a genuine hazard (Green), uncheck if safe (Red)")]
    public bool isHazard = true;

    [Header("Feedback Materials")]
    public Material hazardFoundMaterial; // Drag Mat_Hazard_Green here
    public Material nonHazardMaterial;   // Drag Mat_Safe_Red here

    private MeshRenderer meshRenderer;
    private bool isIdentified = false;

    private void Awake()
    {
        // Finds the MeshRenderer on this GameObject OR any child object (e.g., Visuals)
        meshRenderer = GetComponentInChildren<MeshRenderer>();

        if (meshRenderer == null)
        {
            Debug.LogError($"[HazardFeedback] No MeshRenderer found on '{gameObject.name}' or its children!");
        }
    }

    /// <summary>
    /// Triggered by XR Interactable Select / Activate events.
    /// </summary>
    public void OnObjectClicked()
    {
        if (isIdentified)
        {
            Debug.Log($"[HazardFeedback] '{gameObject.name}' was already clicked.");
            return;
        }

        if (meshRenderer == null)
        {
            meshRenderer = GetComponentInChildren<MeshRenderer>();
            if (meshRenderer == null)
            {
                Debug.LogError($"[HazardFeedback] Cannot change material: MeshRenderer missing on '{gameObject.name}'.");
                return;
            }
        }

        isIdentified = true;

        if (isHazard)
        {
            if (hazardFoundMaterial != null)
            {
                meshRenderer.material = hazardFoundMaterial;
                Debug.Log($"<color=green>[HazardFeedback] SUCCESS: '{gameObject.name}' changed to GREEN (Hazard Found)</color>");
            }
            else
            {
                Debug.LogError($"[HazardFeedback] 'Hazard Found Material' slot is EMPTY on '{gameObject.name}' Inspector!");
            }
        }
        else
        {
            if (nonHazardMaterial != null)
            {
                meshRenderer.material = nonHazardMaterial;
                Debug.Log($"<color=red>[HazardFeedback] SUCCESS: '{gameObject.name}' changed to RED (Non-Hazard)</color>");
            }
            else
            {
                Debug.LogError($"[HazardFeedback] 'Non Hazard Material' slot is EMPTY on '{gameObject.name}' Inspector!");
            }
        }
    }

    /// <summary>
    /// Helper to reset state when restarting testing.
    /// </summary>
    public void ResetFeedback(Material defaultMaterial)
    {
        isIdentified = false;
        if (meshRenderer != null && defaultMaterial != null)
        {
            meshRenderer.material = defaultMaterial;
        }
    }
}
