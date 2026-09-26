using UnityEngine;

public class BillboardToPlayer : MonoBehaviour
{
    [Header("Orientation Settings")]
    [Tooltip("Keep checked to rotate horizontally only (prevents the sign from pitching up/down).")]
    public bool lockVerticalPitch = true;

    [Tooltip("Invert direction if your canvas or text faces backward.")]
    public bool invertForward = false;

    private Transform targetCamera;

    private void Start()
    {
        // Cache the main VR headset camera
        if (Camera.main != null)
        {
            targetCamera = Camera.main.transform;
        }
    }

    private void LateUpdate()
    {
        // Fallback search if camera was initialized late
        if (targetCamera == null)
        {
            if (Camera.main != null) targetCamera = Camera.main.transform;
            return;
        }

        Vector3 directionToPlayer = targetCamera.position - transform.position;

        if (invertForward)
        {
            directionToPlayer = -directionToPlayer;
        }

        if (lockVerticalPitch)
        {
            // Lock rotation strictly to the Y-axis so the sign stays upright
            directionToPlayer.y = 0;
        }

        if (directionToPlayer.sqrMagnitude > 0.001f)
        {
            transform.rotation = Quaternion.LookRotation(directionToPlayer);
        }
    }
}
