using Convai.Runtime.Presentation.Events;
using UnityEngine;

namespace Convai.Sample.Events
{
    /// <summary>
    ///     Designer-facing sample target for ConvaiTranscriptEventRelay.
    /// </summary>
    public sealed class RelayDrivenDoorExample : MonoBehaviour
    {
        [SerializeField] private string keyword = "open sesame";
        [SerializeField] private Transform door;
        [SerializeField] private Vector3 openEulerAngles = new Vector3(0f, 90f, 0f);
        [SerializeField] private Vector3 closedEulerAngles = Vector3.zero;

        public void HandleFinalCharacterTranscript(CharacterTranscriptRelayData data)
        {
            if (door == null || data == null || !data.IsFinal) return;
            if (string.IsNullOrWhiteSpace(keyword) ||
                data.Text.IndexOf(keyword, System.StringComparison.OrdinalIgnoreCase) < 0)
                return;

            door.localRotation = Quaternion.Euler(openEulerAngles);
        }

        public void CloseDoor()
        {
            if (door == null) return;
            door.localRotation = Quaternion.Euler(closedEulerAngles);
        }
    }
}
