using Convai.Shared.Compatibility;
using System.Collections.Generic;
using UnityEngine;
using Convai.Runtime.Actions;

namespace Convai.Runtime.SceneMetadata
{
    /// <summary>A logical scene object with several physical instances. Distinct from action tour groups.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(ConvaiObjectMetadata)), AddComponentMenu("Convai/Logical Object Group")]
    public sealed class ConvaiLogicalObjectGroup : MonoBehaviour
    {
        private static readonly List<ConvaiLogicalObjectGroup> Groups = new();
        [ConvaiInspectorSection("Physical Members"), Tooltip("Physical objects represented by the group's World Object name and description.")]
        public List<ConvaiObjectMetadata> Members = new();
        private ConvaiObjectMetadata _metadata;
        public ConvaiObjectMetadata Metadata => _metadata ? _metadata : _metadata = GetComponent<ConvaiObjectMetadata>();
        private int _membershipHash;

        private void OnValidate() => NotifyMembershipChanged();
        private void OnEnable() { if (!Groups.Contains(this)) Groups.Add(this); NotifyMembershipChanged(); }
        private void OnDisable() { Groups.Remove(this); ConvaiSceneObjectProjection.NotifyChanged(); }
        public void NotifyMembershipChanged() { _membershipHash = MembershipHash(); ConvaiSceneObjectProjection.NotifyChanged(); }
        internal static void PollMembership()
        {
            foreach (ConvaiLogicalObjectGroup group in Groups)
                if (group && group._membershipHash != group.MembershipHash()) group.NotifyMembershipChanged();
        }
        private int MembershipHash()
        {
            unchecked
            {
                int hash = 17;
                foreach (ConvaiObjectMetadata member in Members) hash = hash * 31 + (member && member.isActiveAndEnabled ? ConvaiObjectId.Of(member).GetHashCode() : 0);
                return hash;
            }
        }
        public bool HasActiveMembers
        {
            get { foreach (ConvaiObjectMetadata member in Members) if (member && member.isActiveAndEnabled) return true; return false; }
        }
        public ConvaiObjectMetadata ClosestMember(Vector3 observer)
        {
            ConvaiObjectMetadata best = null;
            float distance = float.PositiveInfinity;
            foreach (ConvaiObjectMetadata member in Members)
            {
                if (!member || !member.isActiveAndEnabled) continue;
                float candidate = (ConvaiSceneObjectProjection.AimPoint(member.gameObject) - observer).sqrMagnitude;
                if (candidate < distance) { distance = candidate; best = member; }
            }
            return best;
        }
        public static ConvaiLogicalObjectGroup ForMember(ConvaiObjectMetadata metadata)
        {
            foreach (ConvaiLogicalObjectGroup group in Groups) if (group && group.isActiveAndEnabled && group.Members.Contains(metadata)) return group;
            return null;
        }
    }
}
