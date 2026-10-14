using Convai.Domain.DomainEvents.Session;
using Convai.Runtime.Components;
using UnityEngine;
using UnityEngine.Serialization;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Shows which character the player is addressing, and drives the sample's on-screen joysticks.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here makes the multi-character conversation work — that is the SDK's job, and it needs no
/// setup: a room holds every active Convai Character in the loaded scenes, and
/// <see cref="ConvaiManager"/> keeps the conversation pointed at whoever the player is looking at.
/// This script only reads the result, and exists to show the two questions a real indicator asks:
/// <see cref="ConvaiManager.AddressedCharacter"/> — who — and
/// <see cref="ConvaiManager.ConversationAvailability"/> — and can they hear me.
/// </para>
/// <para>
/// The second one is the one worth copying. A connected room does not mean the addressed character
/// has been announced by the service yet, and anything said in that gap reaches nobody, so this is
/// what a chat field or a microphone button should be gated on rather than on the connection.
/// </para>
/// <para>
/// To move the conversation from your own code instead — a dialogue menu, a quest step, a trigger
/// volume — call <c>ConvaiManager.TalkTo(character)</c>. To change how the SDK chooses, use
/// <b>Convai Manager → Who The Player Talks To</b>. Both are covered in
/// <c>Documentation~/MULTI-CHARACTER.md</c>.
/// </para>
/// <para>
/// The touch joysticks below are sample input plumbing rather than SDK behaviour. They live on this
/// component because the scene wires its joystick canvas to it;
/// <see cref="MultiCharacterSampleFirstPersonPlayer"/> reads <see cref="MobileMoveInput"/> and
/// <see cref="MobileLookInput"/> and adds them to the keyboard and mouse.
/// </para>
/// </remarks>
public sealed class MultiCharacterSampleController : MonoBehaviour
{
    private const int NoPointerId = -1;
    private const int EditorMousePointerId = -2;

    [Header("Addressed Character Readout")]
    [SerializeField] [Tooltip("Show the on-screen readout of who is being addressed.")]
    [FormerlySerializedAs("showAddressedCharacter")]
    [FormerlySerializedAs("showTestOverlay")]
    private bool _showAddressedCharacter = true;

    [Header("Mobile Controls")]
    [SerializeField] private Canvas joystickCanvas;
    [SerializeField] private RectTransform moveJoystick;
    [SerializeField] private RectTransform moveJoystickHandle;
    [SerializeField] private RectTransform lookJoystick;
    [SerializeField] private RectTransform lookJoystickHandle;
    [SerializeField, Range(0f, 0.95f)] private float joystickDeadZone = 0.1f;

    private ConvaiManager _manager;
    private GUIStyle _style;
    private int _moveTouchId = NoPointerId;
    private int _lookTouchId = NoPointerId;

    /// <summary>Normalized movement requested by the left mobile joystick.</summary>
    public Vector2 MobileMoveInput { get; private set; }

    /// <summary>Normalized camera rotation requested by the right mobile joystick.</summary>
    public Vector2 MobileLookInput { get; private set; }

    /// <summary>True when the joystick UI can receive touch input or Editor mouse simulation.</summary>
    public bool MobileControlsEnabled =>
        joystickCanvas != null &&
        joystickCanvas.isActiveAndEnabled &&
        moveJoystick != null &&
        lookJoystick != null &&
        IsMobilePointerInputAvailable();

    private void Awake()
    {
        _manager = FindAnyObjectByType<ConvaiManager>();
        ResolveMobileControlReferences();
    }

    private void OnDisable() => ResetMobileControls();

    private void Update() => UpdateMobileControls();

    private void OnGUI()
    {
        if (!_showAddressedCharacter || _manager == null) return;

        _style ??= new GUIStyle(GUI.skin.label) { fontSize = 18, alignment = TextAnchor.UpperLeft };

        ConvaiCharacter addressed = _manager.AddressedCharacter;
        ConvaiConversationAvailability availability = _manager.ConversationAvailability;
        string who = addressed != null ? addressed.CharacterName : "nobody";

        // Read every frame rather than cached from an event, because the sample wants the live
        // answer and reading it is an enum comparison. A game with a UI to update would subscribe to
        // ConvaiManager.ConversationAvailabilityChanged instead.
        GUI.Label(
            new Rect(16f, 16f, 640f, 28f),
            availability.CanAcceptPlayerInput()
                ? $"Talking to: {who}"
                : $"Talking to: {who} — {availability}",
            _style);
    }

    private void ResolveMobileControlReferences()
    {
        if (joystickCanvas == null)
        {
            GameObject canvasObject = GameObject.Find("JoyStickCanvas");
            if (canvasObject != null) joystickCanvas = canvasObject.GetComponent<Canvas>();
        }

        if (joystickCanvas == null) return;

        if (moveJoystick == null)
            moveJoystick = joystickCanvas.transform.Find("JoyStickLeft") as RectTransform;
        if (lookJoystick == null)
            lookJoystick = joystickCanvas.transform.Find("JoyStickRight") as RectTransform;
        if (moveJoystickHandle == null && moveJoystick != null && moveJoystick.childCount > 0)
            moveJoystickHandle = moveJoystick.GetChild(0) as RectTransform;
        if (lookJoystickHandle == null && lookJoystick != null && lookJoystick.childCount > 0)
            lookJoystickHandle = lookJoystick.GetChild(0) as RectTransform;
    }

    private void UpdateMobileControls()
    {
        if (!MobileControlsEnabled)
        {
            ResetMobileControls();
            return;
        }

        bool moveTouchFound = false;
        bool lookTouchFound = false;
#if ENABLE_INPUT_SYSTEM
        Touchscreen touchscreen = Touchscreen.current;
        if (touchscreen != null)
        {
            for (int i = 0; i < touchscreen.touches.Count; i++)
            {
                var touch = touchscreen.touches[i];
                if (!touch.press.isPressed) continue;

                ProcessTouch(
                    touch.touchId.ReadValue(),
                    touch.position.ReadValue(),
                    ref moveTouchFound,
                    ref lookTouchFound);
            }
        }
#else
        for (int i = 0; i < Input.touchCount; i++)
        {
            Touch touch = Input.GetTouch(i);
            if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled) continue;

            ProcessTouch(touch.fingerId, touch.position, ref moveTouchFound, ref lookTouchFound);
        }
#endif

#if UNITY_EDITOR
#if ENABLE_INPUT_SYSTEM
        Mouse mouse = Mouse.current;
        if (mouse?.leftButton.isPressed == true)
        {
            ProcessTouch(
                EditorMousePointerId,
                mouse.position.ReadValue(),
                ref moveTouchFound,
                ref lookTouchFound);
        }
#else
        if (Input.GetMouseButton(0))
        {
            ProcessTouch(
                EditorMousePointerId,
                Input.mousePosition,
                ref moveTouchFound,
                ref lookTouchFound);
        }
#endif
#endif

        if (!moveTouchFound) ReleaseMoveJoystick();
        if (!lookTouchFound) ReleaseLookJoystick();
    }

    private void ProcessTouch(
        int touchId,
        Vector2 screenPosition,
        ref bool moveTouchFound,
        ref bool lookTouchFound)
    {
        if (touchId == _moveTouchId)
        {
            moveTouchFound = true;
            MobileMoveInput = UpdateJoystick(moveJoystick, moveJoystickHandle, screenPosition);
            return;
        }

        if (touchId == _lookTouchId)
        {
            lookTouchFound = true;
            MobileLookInput = UpdateJoystick(lookJoystick, lookJoystickHandle, screenPosition);
            return;
        }

        Camera eventCamera = joystickCanvas.renderMode == RenderMode.ScreenSpaceOverlay
            ? null
            : joystickCanvas.worldCamera;
        if (_moveTouchId == NoPointerId &&
            RectTransformUtility.RectangleContainsScreenPoint(moveJoystick, screenPosition, eventCamera))
        {
            _moveTouchId = touchId;
            moveTouchFound = true;
            MobileMoveInput = UpdateJoystick(moveJoystick, moveJoystickHandle, screenPosition);
        }
        else if (_lookTouchId == NoPointerId &&
                 RectTransformUtility.RectangleContainsScreenPoint(lookJoystick, screenPosition, eventCamera))
        {
            _lookTouchId = touchId;
            lookTouchFound = true;
            MobileLookInput = UpdateJoystick(lookJoystick, lookJoystickHandle, screenPosition);
        }
    }

    private Vector2 UpdateJoystick(
        RectTransform joystick,
        RectTransform handle,
        Vector2 screenPosition)
    {
        Camera eventCamera = joystickCanvas.renderMode == RenderMode.ScreenSpaceOverlay
            ? null
            : joystickCanvas.worldCamera;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                joystick,
                screenPosition,
                eventCamera,
                out Vector2 localPoint))
            return Vector2.zero;

        Vector2 radius = joystick.rect.size * 0.5f;
        if (radius.x <= Mathf.Epsilon || radius.y <= Mathf.Epsilon) return Vector2.zero;

        Vector2 rawInput = new(
            (localPoint.x - joystick.rect.center.x) / radius.x,
            (localPoint.y - joystick.rect.center.y) / radius.y);
        rawInput = Vector2.ClampMagnitude(rawInput, 1f);

        if (handle != null)
        {
            Vector2 handleRadius = handle.rect.size * 0.5f;
            Vector2 travel = new(
                Mathf.Max(0f, radius.x - handleRadius.x),
                Mathf.Max(0f, radius.y - handleRadius.y));
            handle.anchoredPosition = Vector2.Scale(rawInput, travel);
        }

        return ApplyJoystickDeadZone(rawInput, joystickDeadZone);
    }

    private static Vector2 ApplyJoystickDeadZone(Vector2 input, float deadZone)
    {
        float magnitude = Mathf.Clamp01(input.magnitude);
        float clampedDeadZone = Mathf.Clamp(deadZone, 0f, 0.95f);
        if (magnitude <= clampedDeadZone) return Vector2.zero;

        float scaledMagnitude = (magnitude - clampedDeadZone) / (1f - clampedDeadZone);
        return input.normalized * scaledMagnitude;
    }

    private static bool IsMobilePointerInputAvailable()
    {
#if UNITY_EDITOR
        return true;
#elif ENABLE_INPUT_SYSTEM
        return Touchscreen.current != null;
#else
        return Input.touchSupported;
#endif
    }

    private void ReleaseMoveJoystick()
    {
        _moveTouchId = NoPointerId;
        MobileMoveInput = Vector2.zero;
        if (moveJoystickHandle != null) moveJoystickHandle.anchoredPosition = Vector2.zero;
    }

    private void ReleaseLookJoystick()
    {
        _lookTouchId = NoPointerId;
        MobileLookInput = Vector2.zero;
        if (lookJoystickHandle != null) lookJoystickHandle.anchoredPosition = Vector2.zero;
    }

    private void ResetMobileControls()
    {
        ReleaseMoveJoystick();
        ReleaseLookJoystick();
    }
}
