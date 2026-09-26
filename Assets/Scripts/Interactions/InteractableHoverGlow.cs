using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

[RequireComponent(typeof(XRBaseInteractable))]
public class InteractableHoverGlow : MonoBehaviour
{
    [Header("Outline Reference")]
    [Tooltip("The Quick Outline component on this object")]
    public Outline outline;

    private XRBaseInteractable interactable;
    private HazardFeedback hazardFeedback;

    private void Awake()
    {
        interactable = GetComponent<XRBaseInteractable>();
        hazardFeedback = GetComponent<HazardFeedback>();

        if (outline == null)
        {
            outline = GetComponentInChildren<Outline>();
        }

        // Keep outline hidden at startup
        if (outline != null)
        {
            outline.enabled = false;
        }
    }

    private void OnEnable()
    {
        if (interactable != null)
        {
            interactable.hoverEntered.AddListener(OnHoverEntered);
            interactable.hoverExited.AddListener(OnHoverExited);
        }
    }

    private void OnDisable()
    {
        if (interactable != null)
        {
            interactable.hoverEntered.RemoveListener(OnHoverEntered);
            interactable.hoverExited.RemoveListener(OnHoverExited);
        }
        DisableGlow();
    }

    private void OnHoverEntered(HoverEnterEventArgs args)
    {
        EnableGlow();
    }

    private void OnHoverExited(HoverExitEventArgs args)
    {
        DisableGlow();
    }

    public void EnableGlow()
    {
        // Never show hover outline if the object was already identified/clicked
        if (hazardFeedback != null && hazardFeedback.isIdentified) return;

        if (outline != null)
        {
            outline.enabled = true;
        }
    }

    public void DisableGlow()
    {
        if (outline != null)
        {
            outline.enabled = false;
        }
    }

    /// <summary>
    /// Locks off the outline permanently once clicked
    /// </summary>
    public void LockFeedback()
    {
        DisableGlow();
        enabled = false;
    }
}