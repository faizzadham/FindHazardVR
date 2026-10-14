using Convai.Domain.DomainEvents.Vision;
using Convai.Domain.EventSystem;
using Convai.Domain.Logging;
using Convai.Runtime;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Components;
using Convai.Runtime.Logging;
using Convai.Runtime.Vision.Context;
using UnityEngine;

namespace Convai.SampleCommon.UI.Vision
{
    /// <summary>
    ///     Sample scene driver for exercising dynamic vision context: send a text message, query the
    ///     backend vision buffer, and fire an explicit vision trigger from a small IMGUI overlay or
    ///     the component context menu. Prefer the uGUI <see cref="VisionContextDebugPanel" /> for the
    ///     Sample Debug Hub; this overlay exists for quick scene-only checks.
    /// </summary>
    [AddComponentMenu("Convai/Samples/Dynamic Vision Context Scene Tool")]
    public sealed class DynamicVisionContextSceneTool : MonoBehaviour
    {
        [SerializeField] private ConvaiPlayer _player;
        [SerializeField] private ConvaiRoomManager _roomManager;
        [SerializeField] private string _textMessage = "Describe the objects in front of you.";
        [SerializeField] private string _visionPrompt = "What objects can you see in the scene?";
        [SerializeField] private ConvaiRespondMode _visionRespondMode = ConvaiRespondMode.MustRespond;
        [SerializeField] private bool _showOverlay = true;

        private string _lastAction = "Idle";
        private IEventHub _eventHub;
        private SubscriptionToken _visionStatusToken;
        private SubscriptionToken _visionTriggerToken;

        private void Awake()
        {
            ResolveReferences();
        }

        private void OnDestroy()
        {
            if (_eventHub == null)
                return;

            if (_visionStatusToken != default)
                _eventHub.Unsubscribe(_visionStatusToken);
            if (_visionTriggerToken != default)
                _eventHub.Unsubscribe(_visionTriggerToken);
        }

        private void OnGUI()
        {
            if (!_showOverlay)
                return;

            GUILayout.BeginArea(new Rect(16f, 16f, 420f, 210f), GUI.skin.box);
            GUILayout.Label("Dynamic Vision Context");

            GUILayout.Label("Text message");
            _textMessage = GUILayout.TextField(_textMessage);
            if (GUILayout.Button("Send Text"))
                SendTextMessage();

            GUILayout.Label("Vision prompt");
            _visionPrompt = GUILayout.TextField(_visionPrompt);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Vision Status"))
                RequestVisionStatus();
            if (GUILayout.Button($"Vision Trigger ({_visionRespondMode})"))
                TriggerVision();
            GUILayout.EndHorizontal();

            GUILayout.Label($"Last: {_lastAction}");
            GUILayout.EndArea();
        }

        [ContextMenu("Send Text Message")]
        public void SendTextMessage()
        {
            ResolveReferences();
            TrySubscribeVisionEvents();
            if (_player == null)
            {
                _lastAction = "No ConvaiPlayer found";
                return;
            }

            _player.SendTextMessage(_textMessage);
            _lastAction = "Sent text";
        }

        [ContextMenu("Request Vision Status")]
        public void RequestVisionStatus()
        {
            if (!TryResolveRoomManager())
                return;

            bool sent = _roomManager.RequestVisionStatus();
            _lastAction = sent ? "Requested vision status" : "Vision status not sent";
        }

        [ContextMenu("Trigger Vision")]
        public void TriggerVision()
        {
            if (!TryResolveRoomManager())
                return;

            bool sent = _roomManager.TriggerVision(new ConvaiVisionTriggerRequest
            {
                Text = _visionPrompt,
                RespondMode = _visionRespondMode
            });
            _lastAction = sent ? "Triggered vision" : "Vision trigger not sent";
        }

        private bool TryResolveRoomManager()
        {
            ResolveReferences();
            TrySubscribeVisionEvents();
            if (_roomManager != null)
                return true;

            _lastAction = "No ConvaiRoomManager found";
            return false;
        }

        private void ResolveReferences()
        {
            if (_player == null)
                _player = FindAnyObjectByType<ConvaiPlayer>();
            if (_roomManager == null)
                _roomManager = FindAnyObjectByType<ConvaiRoomManager>();
        }

        private void TrySubscribeVisionEvents()
        {
            if (_eventHub != null)
                return;

            ConvaiManager manager = FindAnyObjectByType<ConvaiManager>();
            if (manager == null || !manager.TryGetEventHub(out IEventHub eventHub))
                return;

            _eventHub = eventHub;
            _visionStatusToken = _eventHub.Subscribe<VisionContextStatusReceived>(OnVisionStatusReceived);
            _visionTriggerToken = _eventHub.Subscribe<VisionContextTriggerReceived>(OnVisionTriggerReceived);
        }

        private void OnVisionStatusReceived(VisionContextStatusReceived evt)
        {
            _lastAction =
                $"Vision status: status={evt.Status}, outcome={evt.Outcome}, source={evt.ActiveSourceLabel}, ageMs={evt.LastFrameAgeMs}";
            ConvaiLogger.Info($"[DynamicVisionContextSceneTool] {_lastAction}", LogCategory.Vision);
        }

        private void OnVisionTriggerReceived(VisionContextTriggerReceived evt)
        {
            _lastAction =
                $"Vision trigger: status={evt.Status}, outcome={evt.Outcome}, attach={evt.AttachOutcome}, frames={evt.FramesAttached}, downgraded={evt.Downgraded}";
            ConvaiLogger.Info($"[DynamicVisionContextSceneTool] {_lastAction}", LogCategory.Vision);
        }
    }
}
