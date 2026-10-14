using UnityEngine;

namespace Convai.Runtime.Conversation
{
    /// <summary>
    ///     Where on a character the player is taken to be looking.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Angles are measured to a point, and which point is not a detail. A character's transform
    ///         origin sits at its feet; at two and a half metres the feet and the head are about thirty
    ///         degrees apart, which is most of a typical eligibility cone. Measure at the wrong point
    ///         and looking somebody in the face does not count as addressing them.
    ///     </para>
    ///     <para>
    ///         The first version of this took the bounds of whatever <c>GetComponentInChildren</c>
    ///         returned first, which is a lottery over hierarchy order: on one character it resolved to
    ///         a mesh near the head and on another, standing beside it in the same pose, to something
    ///         at the feet. The two were measured a metre and a half apart vertically, so one of them
    ///         could not be selected from places the other could.
    ///     </para>
    ///     <para>
    ///         The rule is deterministic and body-shaped now: the humanoid head bone when the rig
    ///         exposes one, otherwise the centre of every renderer's combined bounds, otherwise the
    ///         transform itself. None of it depends on the order components happen to appear in.
    ///     </para>
    /// </remarks>
    internal sealed class ConversationAimPoint
    {
        private readonly Transform _fallback;
        private readonly Transform _headBone;
        private readonly Renderer[] _renderers;

        private ConversationAimPoint(Transform fallback, Transform headBone, Renderer[] renderers)
        {
            _fallback = fallback;
            _headBone = headBone;
            _renderers = renderers;
        }

        /// <summary>Which source this settled on. Diagnostics only.</summary>
        public string Source => _headBone != null
            ? "head bone"
            : _renderers is { Length: > 0 }
                ? $"{_renderers.Length} renderers"
                : "transform";

        /// <summary>Works out how to measure this character, once.</summary>
        public static ConversationAimPoint For(Transform root)
        {
            if (root == null) return new ConversationAimPoint(null, null, null);

            Transform head = null;
            var animator = root.GetComponentInChildren<Animator>();
            if (animator != null && animator.isHuman)
                head = animator.GetBoneTransform(HumanBodyBones.Head);

            return new ConversationAimPoint(
                root,
                head,
                head != null ? null : root.GetComponentsInChildren<Renderer>());
        }

        /// <summary>The current world-space point to measure this character by.</summary>
        public Vector3 Resolve()
        {
            if (_headBone != null) return _headBone.position;

            if (_renderers is { Length: > 0 })
            {
                var bounds = new Bounds();
                bool started = false;
                for (int i = 0; i < _renderers.Length; i++)
                {
                    Renderer candidate = _renderers[i];
                    if (candidate == null || !candidate.enabled) continue;

                    if (!started)
                    {
                        bounds = candidate.bounds;
                        started = true;
                        continue;
                    }

                    bounds.Encapsulate(candidate.bounds);
                }

                if (started) return bounds.center;
            }

            return _fallback != null ? _fallback.position : Vector3.zero;
        }
    }
}
