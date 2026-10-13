using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

[RequireComponent(typeof(XRGrabInteractable))]
public class SpatialBriefingStation : MonoBehaviour
{
    [Header("Spatial Alignment")]
    [Tooltip("Reference to the trainee's VR camera / head")]
    public Transform playerCamera;

    [Tooltip("Ideal distance when repositioning in front of trainee")]
    public float defaultDistance = 2.4f;
    public float defaultHeight = 1.35f;

    private XRGrabInteractable grabInteractable;

    private void Awake()
    {
        grabInteractable = GetComponent<XRGrabInteractable>();

        if (playerCamera == null && Camera.main != null)
        {
            playerCamera = Camera.main.transform;
        }
    }

    /// <summary>
    /// Snaps the briefing station directly in front of the player's current view.
    /// Wire this to a 'Reset to My View' button on the HUD.
    /// </summary>
    public void ResetPositionInFrontOfPlayer()
    {
        if (playerCamera == null) return;

        // Calculate forward direction on horizontal plane
        Vector3 forwardXZ = playerCamera.forward;
        forwardXZ.y = 0f;
        forwardXZ.Normalize();

        Vector3 targetPosition = playerCamera.position + (forwardXZ * defaultDistance);
        targetPosition.y = defaultHeight;

        transform.position = targetPosition;

        // Face the player directly
        Vector3 lookDir = transform.position - playerCamera.position;
        lookDir.y = 0f;
        if (lookDir != Vector3.zero)
        {
            transform.rotation = Quaternion.LookRotation(lookDir);
        }
    }
}
