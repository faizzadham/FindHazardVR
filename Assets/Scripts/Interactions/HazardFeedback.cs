using UnityEngine;

/// <summary>
/// Changes the visual feedback (color/material) when clicked by an XR Interactor.
/// </summary>
public class HazardFeedback : MonoBehaviour
{
    [Header("Hazard Configuration")]
    [Tooltip("Check if this object is a genuine hazard (Red), uncheck if safe (Green)")]
    public bool isHazard = true;

    [Header("Feedback Materials")]
    public Material hazardFoundMaterial; // Drag Mat_Hazard_Red here
    public Material nonHazardMaterial;   // Drag Mat_Safe_Green here

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

        isIdentified = true;

        if (isHazard)
        {
            if (hazardFoundMaterial != null && meshRenderer != null)
            {
                meshRenderer.material = hazardFoundMaterial;
            }

            // Award +1 point to the HUD scoreboard
            if (TrainingGameManager.Instance != null)
            {
                TrainingGameManager.Instance.AddHazardFound();
            }

            Debug.Log($"<color=green>[HazardFeedback] Correct Hazard! +1 Point awarded.</color>");
        }
        else
        {
            if (nonHazardMaterial != null && meshRenderer != null)
            {
                meshRenderer.material = nonHazardMaterial;
            }
            Debug.Log($"<color=red>[HazardFeedback] Incorrect object tagged.</color>");
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
