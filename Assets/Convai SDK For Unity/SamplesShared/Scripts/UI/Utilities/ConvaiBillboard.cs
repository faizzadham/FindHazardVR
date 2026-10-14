using UnityEngine;

namespace Convai.Samples
{
    /// <summary>
    ///     AAA billboard component that keeps a GameObject facing the camera.
    ///     Supports multiple billboard modes and configurable axis constraints.
    /// </summary>
    [AddComponentMenu("Convai/Samples/Convai Billboard")]
    [DisallowMultipleComponent]
    public class ConvaiBillboard : MonoBehaviour
    {
        /// <summary>
        ///     Billboard orientation mode.
        /// </summary>
        public enum BillboardMode
        {
            /// <summary>
            ///     Face camera with full rotation on all axes.
            /// </summary>
            Full,

            /// <summary>
            ///     Face camera but lock Y-axis (world up). Best for world-space UI that stays upright.
            /// </summary>
            YAxisLocked,

            /// <summary>
            ///     Only rotate around Y-axis (horizontal rotation). Good for objects that should stay grounded.
            /// </summary>
            YAxisOnly,

            /// <summary>
            ///     Camera-facing billboard aligned to world forward. Never flips.
            /// </summary>
            CameraFacing
        }

        [Header("Billboard Settings")]
        [SerializeField]
        [Tooltip("How the object orients toward the camera.")]
        private BillboardMode billboardMode = BillboardMode.YAxisLocked;

        [SerializeField]
        [Tooltip("If true, billboard faces away from camera (useful for UI elements).")]
        private bool reverseFacing;

        [SerializeField]
        [Tooltip("If true, use late update for smoother camera tracking.")]
        private bool useLateUpdate = true;

        [Header("Camera Reference")]
        [SerializeField]
        [Tooltip("Target camera. If null, uses Camera.main.")]
        private Camera targetCamera;

        [Header("Performance")]
        [SerializeField]
        [Tooltip("If true, caches camera transform for better performance.")]
        private bool cacheCamera = true;

        [SerializeField]
        [Tooltip("Minimum angle change in degrees before updating rotation (0 = always update).")]
        [Range(0f, 10f)]
        private float angleThreshold;

        private Transform _cachedCameraTransform;
        private Transform _transform;
        private Quaternion _lastRotation;
        private bool _isInitialized;

        private void Awake()
        {
            _transform = transform;
            Initialize();
        }

        private void OnEnable()
        {
            if (!_isInitialized)
                Initialize();
        }

        private void Update()
        {
            if (!useLateUpdate)
                UpdateBillboard();
        }

        private void LateUpdate()
        {
            if (useLateUpdate)
                UpdateBillboard();
        }

        /// <summary>
        ///     Manually initialize billboard. Called automatically on Awake/OnEnable.
        /// </summary>
        public void Initialize()
        {
            if (targetCamera == null)
                targetCamera = Camera.main;

            if (targetCamera != null && cacheCamera)
                _cachedCameraTransform = targetCamera.transform;

            _lastRotation = _transform.rotation;
            _isInitialized = true;
        }

        /// <summary>
        ///     Force immediate billboard update.
        /// </summary>
        public void ForceUpdate()
        {
            UpdateBillboard(true);
        }

        private void UpdateBillboard(bool force = false)
        {
            Transform camTransform = GetCameraTransform();
            if (camTransform == null)
                return;

            Quaternion targetRotation = CalculateTargetRotation(camTransform);

            // Angle threshold optimization
            if (!force && angleThreshold > 0f)
            {
                float angleDelta = Quaternion.Angle(_lastRotation, targetRotation);
                if (angleDelta < angleThreshold)
                    return;
            }

            _transform.rotation = targetRotation;
            _lastRotation = targetRotation;
        }

        private Quaternion CalculateTargetRotation(Transform camTransform)
        {
            Vector3 directionToCamera = camTransform.position - _transform.position;

            if (reverseFacing)
                directionToCamera = -directionToCamera;

            switch (billboardMode)
            {
                case BillboardMode.Full:
                    return Quaternion.LookRotation(directionToCamera);

                case BillboardMode.YAxisLocked:
                    directionToCamera.y = 0f;
                    if (directionToCamera.sqrMagnitude < 0.001f)
                        return _transform.rotation;
                    return Quaternion.LookRotation(directionToCamera, Vector3.up);

                case BillboardMode.YAxisOnly:
                    Vector3 flatDirection = new Vector3(directionToCamera.x, 0f, directionToCamera.z);
                    if (flatDirection.sqrMagnitude < 0.001f)
                        return _transform.rotation;
                    return Quaternion.LookRotation(flatDirection, Vector3.up);

                case BillboardMode.CameraFacing:
                    return Quaternion.LookRotation(camTransform.forward, camTransform.up);

                default:
                    return _transform.rotation;
            }
        }

        private Transform GetCameraTransform()
        {
            if (_cachedCameraTransform != null)
                return _cachedCameraTransform;

            if (targetCamera == null)
            {
                targetCamera = Camera.main;
                if (targetCamera != null && cacheCamera)
                    _cachedCameraTransform = targetCamera.transform;
            }

            return targetCamera != null ? targetCamera.transform : null;
        }

        /// <summary>
        ///     Set billboard mode at runtime.
        /// </summary>
        public void SetBillboardMode(BillboardMode mode)
        {
            billboardMode = mode;
        }

        /// <summary>
        ///     Set target camera at runtime.
        /// </summary>
        public void SetTargetCamera(Camera camera)
        {
            targetCamera = camera;
            _cachedCameraTransform = camera != null ? camera.transform : null;
        }
    }
}
