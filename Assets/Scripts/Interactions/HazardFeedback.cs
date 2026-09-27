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

    [Header("Feedback Messages")]
    [Tooltip("Message displayed on HUD and console when a correct hazard is identified")]
    public string hazardMessage = "HAZARD IDENTIFIED (+1)";

    [Tooltip("Message displayed on HUD and console when a safe distractor is clicked")]
    public string nonHazardMessage = "SAFE OBJECT — No Hazard Found";

    [Header("Optional Audio Feedback")]
    public AudioSource audioSource;
    public AudioClip hazardSound;
    public AudioClip nonHazardSound;

    [HideInInspector]
    public bool isIdentified = false; // Kept public so InteractableHoverGlow can check it

    public void OnObjectClicked()
    {
        if (isIdentified)
        {
            Debug.Log($"[HazardFeedback] '{gameObject.name}' was already clicked.");
            return;
        }

        isIdentified = true;

        // 1. Permanently lock hover glow/outlines so moving the laser away won't erase the colors
        LockHoverEffects();

        // 2. Process feedback based on hazard status
        if (isHazard)
        {
            // Apply RED material to all sub-material slots
            ApplyMaterialToAll(hazardFoundMaterial);

            // Play audio confirmation
            PlaySound(hazardSound);

            // Award +1 score in GameManager
            if (TrainingGameManager.Instance != null)
            {
                TrainingGameManager.Instance.AddHazardFound();
            }

            // Display on HUD banner (if HUDNotificationManager is present)
            HUDNotificationManager.Instance?.ShowNotification(hazardMessage, new Color(0.9f, 0.2f, 0.2f));

            // Log detailed telemetry message to Unity Console
            Debug.Log($"<color=#E74C3C>[HazardFeedback] {hazardMessage} | Object: '{gameObject.name}'</color>");
        }
        else
        {
            // Apply GREEN material to all sub-material slots
            ApplyMaterialToAll(nonHazardMaterial);

            // Play safe click audio
            PlaySound(nonHazardSound);

            // Display on HUD banner (if HUDNotificationManager is present)
            HUDNotificationManager.Instance?.ShowNotification(nonHazardMessage, new Color(0.18f, 0.8f, 0.44f));

            // Log telemetry message to Unity Console
            Debug.Log($"<color=#2ECC71>[HazardFeedback] {nonHazardMessage} | Object: '{gameObject.name}'</color>");
        }
    }

    /// <summary>
    /// Replaces every sub-material slot across this object and all child meshes
    /// </summary>
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

    /// <summary>
    /// Disables hover outline scripts so the highlight aura does not overwrite the Red/Green material
    /// </summary>
    private void LockHoverEffects()
    {
        InteractableHoverGlow hoverGlow = GetComponent<InteractableHoverGlow>();
        if (hoverGlow != null)
        {
            hoverGlow.LockFeedback();
        }

        Outline outline = GetComponentInChildren<Outline>();
        if (outline != null)
        {
            outline.enabled = false;
        }
    }

    private void PlaySound(AudioClip clip)
    {
        if (clip == null) return;

        if (audioSource != null)
        {
            audioSource.PlayOneShot(clip);
        }
        else
        {
            AudioSource.PlayClipAtPoint(clip, transform.position);
        }
    }
}