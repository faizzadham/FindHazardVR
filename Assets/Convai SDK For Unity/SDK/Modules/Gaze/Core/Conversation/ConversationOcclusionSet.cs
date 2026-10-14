using Convai.Modules.Gaze.Providers;
using UnityEngine;

namespace Convai.Modules.Gaze.Core.Conversation
{
    /// <summary>
    ///     The people in the room this character cannot actually see. One of these per
    ///     character; it re-measures on a throttle and everything that decides who to look at
    ///     asks it the same question.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A room is a physical place before it is a conversation. Two characters in
    ///         adjoining rooms are both participants — they are reported, they hold the floor,
    ///         they are heard — and without this a listener turns its head, and eventually its
    ///         body, toward a wall. The set is the one thing that knows the difference, and both
    ///         readers consult it: the conversation director (so an unseen speaker is never
    ///         attended, glanced at or reflexively checked) and
    ///         <see cref="CharacterGazeTargetProvider" /> (so the arbiter never sees the
    ///         candidate either).
    ///     </para>
    ///     <para>
    ///         The player is deliberately not measured here. The player anchor has owned an
    ///         unobstructed-line check of its own since E5, with smoothed visibility and the
    ///         "lost you / found you again" beat that goes with it; a second, binary gate on the
    ///         same person would fight it — the director would stand down on the frame the ray
    ///         broke while the anchor was still a third of a second into its decay.
    ///     </para>
    ///     <para>
    ///         Cost is one <c>RaycastNonAlloc</c> per other character per interval (so at most
    ///         seven per character per 0.1 s in a room of eight), into a buffer owned by this
    ///         instance. Nothing here allocates after construction.
    ///     </para>
    /// </remarks>
    internal sealed class ConversationOcclusionSet
    {
        /// <summary>Room capacity the keys start at; a bigger room grows it once and keeps it.</summary>
        private const int InitialCapacity = 8;

        /// <summary>Hits one ray may report. Deep enough that a wall is never missed behind props.</summary>
        private const int HitBufferSize = 8;

        /// <summary>Floor under the throttle, so a mis-authored interval cannot raycast every frame.</summary>
        private const float MinimumIntervalSeconds = 0.02f;

        /// <summary>Sentinel in <see cref="_timer" /> meaning "never measured, phase not yet applied".</summary>
        private const float Unprimed = -1f;

        private int[] _keys = new int[InitialCapacity];
        private float[] _since = new float[InitialCapacity];
        private int[] _previousKeys = new int[InitialCapacity];
        private float[] _previousSince = new float[InitialCapacity];
        private readonly RaycastHit[] _hits = new RaycastHit[HitBufferSize];
        private int _count;
        private int _previousCount;
        private float _timer = Unprimed;

        /// <summary>How many people this character currently cannot see. Diagnostics and tests.</summary>
        public int Count => _count;

        /// <summary>
        ///     Whether a wall stands between this character and <paramref name="key" /> as of the
        ///     last measurement. False for anybody never measured — an occlusion this set could
        ///     not verify is never invented.
        /// </summary>
        /// <param name="key">Room key of the person being asked about.</param>
        public bool IsOccluded(int key)
        {
            if (key == 0) return false;

            for (int i = 0; i < _count; i++)
                if (_keys[i] == key)
                    return true;

            return false;
        }

        /// <summary>
        ///     States that somebody is out of sight without casting anything at it. The only
        ///     caller is the test suite: a case about what a character <i>decides</i> when it
        ///     cannot see a speaker is about the decision, and building a physics scene to prove
        ///     the premise would put a second subject in it.
        /// </summary>
        /// <param name="key">Room key of the person to hide.</param>
        /// <param name="occludedSince">
        ///     Room time they went out of sight. The default is "they always were", which is what
        ///     a case that only cares about the decision means by hiding somebody.
        /// </param>
        internal void MarkOccludedForTests(int key, float occludedSince = float.NegativeInfinity) =>
            Add(key, occludedSince);

        /// <summary>
        ///     Whether <paramref name="key" /> has been out of sight continuously for at least
        ///     <paramref name="seconds" />.
        /// </summary>
        /// <remarks>
        ///     A ray is a binary answer sampled ten times a second, and a shoulder, a doorway or
        ///     somebody walking between two people breaks one for a measurement at a time. That is
        ///     enough to drop a speaker the character is in the middle of listening to, and
        ///     dropping a look only to take it again is the flick this question exists to prevent:
        ///     not "is there something in the way this instant" but "have they been gone long
        ///     enough to count as gone".
        /// </remarks>
        /// <param name="key">Room key of the person being asked about.</param>
        /// <param name="now">Room time.</param>
        /// <param name="seconds">How long the occlusion has to have lasted to count.</param>
        public bool IsOccludedFor(int key, float now, float seconds)
        {
            if (key == 0) return false;

            for (int i = 0; i < _count; i++)
                if (_keys[i] == key)
                    return now - _since[i] >= seconds;

            return false;
        }

        /// <summary>Forgets every measurement and re-applies the stagger on the next refresh.</summary>
        public void Clear()
        {
            _count = 0;
            _previousCount = 0;
            _timer = Unprimed;
        }

        /// <summary>
        ///     Re-measures the room if the throttle is due, and returns whether it did.
        /// </summary>
        /// <remarks>
        ///     The first measurement is pushed out by <paramref name="phase" /> of an interval, so
        ///     eight characters that woke up on the same frame do not all raycast on the same one
        ///     for the rest of the scene — the offset is kept, not just applied once.
        /// </remarks>
        /// <param name="room">This frame's room. An invalid one clears the set.</param>
        /// <param name="selfKey">This character's room key, so it never tests itself.</param>
        /// <param name="origin">Where this character's eyes are, in world space.</param>
        /// <param name="selfRoot">This character's root; its own colliders never count as walls.</param>
        /// <param name="obstructionMask">Layers that count as vision obstructions.</param>
        /// <param name="intervalSeconds">Seconds between measurements.</param>
        /// <param name="deltaTime">Seconds since this character's last cognition tick.</param>
        /// <param name="phase">This character's 0..1 share of the interval, for the stagger.</param>
        /// <returns>Whether the rays were cast this tick.</returns>
        public bool Refresh(
            in ConversationRoomSnapshot room,
            int selfKey,
            Vector3 origin,
            Transform selfRoot,
            int obstructionMask,
            float intervalSeconds,
            float deltaTime,
            float phase)
        {
            if (!room.IsValid)
            {
                Clear();
                return false;
            }

            float interval = Mathf.Max(MinimumIntervalSeconds, intervalSeconds);
            if (_timer <= Unprimed) _timer = Mathf.Clamp01(phase) * interval;

            _timer -= Mathf.Max(0f, deltaTime);
            if (_timer > 0f) return false;

            _timer = interval;
            Measure(in room, selfKey, origin, selfRoot, obstructionMask);
            return true;
        }

        /// <summary>Casts one ray per other character and records the ones a wall stands in front of.</summary>
        private void Measure(
            in ConversationRoomSnapshot room,
            int selfKey,
            Vector3 origin,
            Transform selfRoot,
            int obstructionMask)
        {
            // This measurement replaces the last one's answers and keeps its clocks: somebody
            // still behind the same wall has been behind it since the first ray that found them,
            // not since this one. A swap of preallocated arrays, so nothing is allocated here.
            (_keys, _previousKeys) = (_previousKeys, _keys);
            (_since, _previousSince) = (_previousSince, _since);
            _previousCount = _count;
            _count = 0;

            for (int i = 0; i < room.ParticipantCount; i++)
            {
                ConversationParticipant participant = room.GetParticipant(i);
                if (participant == null) continue;

                int key = participant.Key;
                if (key == 0 || key == selfKey || participant.IsPlayer) continue;

                // The other character's own body is not a wall, and the ray ends inside it — a
                // capsule around the head would report a hit on every participant in the room.
                // Their root comes from the registry, which is where a lookable character's
                // transform lives; somebody the registry has never heard of is left visible
                // rather than guessed at, because there is nothing to exclude and the test would
                // return the wrong answer rather than no answer.
                if (!ConvaiCharacterGazeRegistry.TryGetByKey(key, out ConvaiCharacterGazeRegistry.Entry entry, out _))
                    continue;

                Transform otherRoot = entry != null ? entry.Root : null;
                if (otherRoot == null) continue;

                if (!GazeLineOfSight.Occluded(
                        origin, participant.HeadPoint, obstructionMask, selfRoot, otherRoot, _hits))
                    continue;

                Add(key, PreviousSince(key, room.Time));
            }
        }

        /// <summary>
        ///     When somebody went out of sight: whatever the previous measurement recorded for
        ///     them, or <paramref name="now" /> for somebody who has only just gone.
        /// </summary>
        /// <param name="key">Room key that this measurement found occluded.</param>
        /// <param name="now">Room time of this measurement.</param>
        private float PreviousSince(int key, float now)
        {
            for (int i = 0; i < _previousCount; i++)
                if (_previousKeys[i] == key)
                    return _previousSince[i];

            return now;
        }

        /// <summary>Records one occluded key, growing the (preallocated) store only for a bigger room.</summary>
        /// <param name="key">Room key that is out of sight.</param>
        /// <param name="since">Room time they went out of sight.</param>
        private void Add(int key, float since)
        {
            if (_count == _keys.Length)
            {
                var grownKeys = new int[_keys.Length * 2];
                System.Array.Copy(_keys, grownKeys, _keys.Length);
                _keys = grownKeys;

                var grownSince = new float[_since.Length * 2];
                System.Array.Copy(_since, grownSince, _since.Length);
                _since = grownSince;
            }

            _since[_count] = since;
            _keys[_count++] = key;
        }
    }
}
