using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

[RequireComponent(typeof(XRBaseInteractable))]
public class HazardFeedback : MonoBehaviour
{
    [Header("Hazard Configuration")]
    [Tooltip("Check if this object is an actual warehouse hazard. Uncheck for safe distractors.")]
    public bool isHazard = false;

    [Tooltip("Human-readable hazard name displayed in the Laravel Admin Dashboard (e.g., 'Fluid & Oil Leak Slip Hazard'). Defaults to GameObject name if left blank.")]
    public string hazardDisplayName = "";

    [Header("Feedback Materials")]
    [Tooltip("Material applied when a hazard is identified (Red)")]
    public Material hazardFoundMaterial;

    [Tooltip("Material applied when a non-hazard is clicked (Green)")]
    public Material nonHazardMaterial;

    [Header("Feedback Messages")]
    public string hazardMessage = "HAZARD IDENTIFIED (+1)";
    public string nonHazardMessage = "SAFE OBJECT — NO HAZARD FOUND";

    [Header("Audio Feedback")]
    [Tooltip("Sound played when the controller laser aims/hovers over this object")]
    public AudioClip hoverSound;
    [Range(0f, 1f)] public float hoverVolume = 0.35f;

    [Tooltip("Sound played when this object is clicked")]
    public AudioClip hazardSound;
    public AudioClip nonHazardSound;
    [Range(0f, 1f)] public float clickVolume = 0.8f;

    public AudioSource audioSource;

    [HideInInspector]
    public bool isIdentified = false;

    private XRBaseInteractable interactable;

    private void Awake()
    {
        interactable = GetComponent<XRBaseInteractable>();
        if (audioSource == null)
        {
            audioSource = GetComponent<AudioSource>();
        }

        // Auto-fallback: if hazardDisplayName is not entered in Inspector, use GameObject name
        if (string.IsNullOrWhiteSpace(hazardDisplayName))
        {
            hazardDisplayName = gameObject.name;
        }
    }

    private void OnEnable()
    {
        if (interactable != null)
        {
            interactable.firstHoverEntered.AddListener(OnHoverEntered);
            // Automatically listen to trigger activation so you don't have to manually wire every single object in Inspector
            interactable.activated.AddListener(OnActivated);
        }
    }

    private void OnDisable()
    {
        if (interactable != null)
        {
            interactable.firstHoverEntered.RemoveListener(OnHoverEntered);
            interactable.activated.RemoveListener(OnActivated);
        }
    }

    private void OnHoverEntered(HoverEnterEventArgs args)
    {
        // Do not play hover sound if the object has already been inspected
        if (isIdentified) return;

        PlaySound(hoverSound, hoverVolume);
    }

    private void OnActivated(ActivateEventArgs args)
    {
        OnObjectClicked();
    }

    // Overload allowing direct mapping from XRI Inspector UnityEvents (Activated)
    public void OnObjectClicked(ActivateEventArgs args)
    {
        OnObjectClicked();
    }

    public void OnObjectClicked()
    {
        if (isIdentified)
        {
            Debug.Log($"[HazardFeedback] '{hazardDisplayName}' was already clicked.");
            return;
        }

        isIdentified = true;

        // 1. Permanently lock hover glow and outlines
        LockHoverEffects();

        // 2. Process feedback based on hazard classification
        if (isHazard)
        {
            ApplyMaterialToAll(hazardFoundMaterial);
            PlaySound(hazardSound, clickVolume);

            // Pass the exact hazard name to TrainingGameManager for the Laravel Admin Dashboard
            if (TrainingGameManager.Instance != null)
            {
                TrainingGameManager.Instance.AddHazardFound(hazardDisplayName);
            }

            HUDNotificationManager.Instance?.ShowNotification(hazardMessage, new Color(0.95f, 0.25f, 0.25f));
            Debug.Log($"<color=#E74C3C>[HazardFeedback] {hazardMessage} | Identified: '{hazardDisplayName}'</color>");
        }
        else
        {
            ApplyMaterialToAll(nonHazardMaterial);
            PlaySound(nonHazardSound, clickVolume);

            HUDNotificationManager.Instance?.ShowNotification(nonHazardMessage, new Color(0.18f, 0.85f, 0.45f));
            Debug.Log($"<color=#2ECC71>[HazardFeedback] {nonHazardMessage} | Object: '{hazardDisplayName}'</color>");
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

    private void PlaySound(AudioClip clip, float volume)
    {
        if (clip == null) return;

        if (audioSource != null)
        {
            audioSource.PlayOneShot(clip, volume);
        }
        else
        {
            Vector3 soundPos = Camera.main != null ? Camera.main.transform.position : transform.position;
            AudioSource.PlayClipAtPoint(clip, soundPos, volume);
        }
    }
}