using UnityEngine;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Minimal first-person movement for the Multi-Character Sample.
/// </summary>
/// <remarks>
/// The sample walks the player around so they can turn towards one character or another, because
/// that is what the SDK reads to decide who is being addressed. WASD and the mouse are read
/// directly rather than through a PlayerInput action map, so importing the sample needs no input
/// asset of its own. On a touch device the same two inputs come from the on-screen joysticks that
/// <see cref="MultiCharacterSampleController" /> draws, and the two sources are added rather than
/// switched between, so the Editor drives either one.
/// </remarks>
[RequireComponent(typeof(CharacterController))]
public sealed class MultiCharacterSampleFirstPersonPlayer : MonoBehaviour
{
    [Header("Movement")]
    [SerializeField, Min(0f)] private float moveSpeed = 4f;
    [SerializeField] private float gravity = -20f;

    [Header("Mouse Look")]
    [SerializeField] private Camera playerCamera;
    [SerializeField, Min(0f)] private float mouseSensitivity = 0.08f;
    [SerializeField] private float minimumPitch = -85f;
    [SerializeField] private float maximumPitch = 85f;
    [SerializeField] private bool lockCursorOnStart = true;

    [Header("Mobile Controls")]
    [SerializeField] private MultiCharacterSampleController mobileControls;
    [SerializeField, Min(0f)] private float mobileLookSpeed = 120f;

    private CharacterController _characterController;
    private float _verticalVelocity;
    private float _pitch;

    private void Awake()
    {
        _characterController = GetComponent<CharacterController>();
        if (playerCamera == null) playerCamera = GetComponentInChildren<Camera>(true);
        if (playerCamera == null) playerCamera = Camera.main;
        if (mobileControls == null) mobileControls = FindAnyObjectByType<MultiCharacterSampleController>();
        if (playerCamera != null) _pitch = NormalizePitch(playerCamera.transform.localEulerAngles.x);
    }

    private void Start()
    {
        if (lockCursorOnStart) SetCursorLocked(true);
    }

    private void Update()
    {
        HandleCursorLock();

        bool desktopControlsEnabled = Cursor.lockState == CursorLockMode.Locked;
        bool mobileControlsEnabled = mobileControls != null && mobileControls.MobileControlsEnabled;
        if (!desktopControlsEnabled && !mobileControlsEnabled) return;

        MovePlayer();
        RotateView(desktopControlsEnabled, mobileControlsEnabled);
    }

    private void MovePlayer()
    {
        Vector2 input = ReadMoveInput();
        Vector3 horizontal = transform.right * input.x + transform.forward * input.y;
        if (horizontal.sqrMagnitude > 1f) horizontal.Normalize();

        if (_characterController.isGrounded && _verticalVelocity < 0f)
            _verticalVelocity = -2f;
        else
            _verticalVelocity += gravity * Time.deltaTime;

        Vector3 velocity = horizontal * moveSpeed + Vector3.up * _verticalVelocity;
        _characterController.Move(velocity * Time.deltaTime);
    }

    private void RotateView(bool desktopControlsEnabled, bool mobileControlsEnabled)
    {
        if (playerCamera == null) return;

        // The mouse delta is already a per-frame amount; the joystick is a held position, so only
        // the joystick term is scaled by delta time. Adding them keeps one code path for both.
        Vector2 desktopLook = desktopControlsEnabled ? ReadLookInput() : Vector2.zero;
        Vector2 mobileLook = mobileControlsEnabled ? mobileControls.MobileLookInput : Vector2.zero;
        float yaw = desktopLook.x * mouseSensitivity + mobileLook.x * mobileLookSpeed * Time.deltaTime;
        float pitch = desktopLook.y * mouseSensitivity + mobileLook.y * mobileLookSpeed * Time.deltaTime;
        if (Mathf.Abs(yaw) <= Mathf.Epsilon && Mathf.Abs(pitch) <= Mathf.Epsilon) return;

        transform.Rotate(Vector3.up, yaw, Space.Self);
        _pitch = Mathf.Clamp(_pitch - pitch, minimumPitch, maximumPitch);
        playerCamera.transform.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
    }

    private Vector2 ReadMoveInput()
    {
        Vector2 desktopInput;
#if ENABLE_INPUT_SYSTEM
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
        {
            desktopInput = Vector2.zero;
        }
        else
        {
            float horizontal = (keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f);
            float vertical = (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f);
            desktopInput = new Vector2(horizontal, vertical);
        }
#else
        desktopInput = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
#endif
        Vector2 mobileInput = mobileControls != null ? mobileControls.MobileMoveInput : Vector2.zero;
        return Vector2.ClampMagnitude(desktopInput + mobileInput, 1f);
    }

    private static Vector2 ReadLookInput()
    {
#if ENABLE_INPUT_SYSTEM
        return Mouse.current?.delta.ReadValue() ?? Vector2.zero;
#else
        return new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y"));
#endif
    }

    private static void HandleCursorLock()
    {
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current?.escapeKey.wasPressedThisFrame == true) SetCursorLocked(false);
        bool primaryClickPressed = Mouse.current?.leftButton.wasPressedThisFrame == true;
#else
        if (Input.GetKeyDown(KeyCode.Escape)) SetCursorLocked(false);
        bool primaryClickPressed = Input.GetMouseButtonDown(0);
#endif

        bool pointerOverUi = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
        if (ShouldLockCursorForPrimaryClick(primaryClickPressed, pointerOverUi)) SetCursorLocked(true);
    }

    private static bool ShouldLockCursorForPrimaryClick(bool primaryClickPressed, bool pointerOverUi) =>
        primaryClickPressed && !pointerOverUi;

    private static void SetCursorLocked(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    private static float NormalizePitch(float angle) => angle > 180f ? angle - 360f : angle;
}
