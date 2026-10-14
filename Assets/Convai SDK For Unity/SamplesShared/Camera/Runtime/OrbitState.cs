// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

using System;
using UnityEngine;

namespace Convai.Sample.Camera
{
    /// <summary>
    ///     Immutable snapshot of orbit camera state at a point in time.
    ///     Separates state data from behavior, enabling cleaner testing and composition.
    /// </summary>
    [Serializable]
    public readonly struct OrbitState : IEquatable<OrbitState>
    {
        /// <summary>Yaw angle in degrees (rotation around world Y axis).</summary>
        public readonly float Yaw;

        /// <summary>Pitch angle in degrees (rotation around camera's right axis).</summary>
        public readonly float Pitch;

        /// <summary>Orbit distance in meters from the pivot point.</summary>
        public readonly float Distance;

        /// <summary>Camera-relative pan offset in meters (X = right, Y = up).</summary>
        public readonly Vector2 Pan;

        /// <summary>
        ///     Creates a new orbit state with the specified parameters.
        /// </summary>
        public OrbitState(float yaw, float pitch, float distance, Vector2 pan)
        {
            Yaw = yaw;
            Pitch = pitch;
            Distance = distance;
            Pan = pan;
        }

        /// <summary>Returns a copy with the yaw replaced.</summary>
        public OrbitState WithYaw(float yaw) => new OrbitState(yaw, Pitch, Distance, Pan);

        /// <summary>Returns a copy with the pitch replaced.</summary>
        public OrbitState WithPitch(float pitch) => new OrbitState(Yaw, pitch, Distance, Pan);

        /// <summary>Returns a copy with the distance replaced.</summary>
        public OrbitState WithDistance(float distance) => new OrbitState(Yaw, Pitch, distance, Pan);

        /// <summary>Returns a copy with the pan replaced.</summary>
        public OrbitState WithPan(Vector2 pan) => new OrbitState(Yaw, Pitch, Distance, pan);

        /// <summary>Default state: zero yaw/pitch, 5m distance, no pan.</summary>
        public static OrbitState Default => new OrbitState(0f, 0f, 5f, Vector2.zero);

        /// <inheritdoc />
        public bool Equals(OrbitState other)
        {
            return Mathf.Approximately(Yaw, other.Yaw) &&
                   Mathf.Approximately(Pitch, other.Pitch) &&
                   Mathf.Approximately(Distance, other.Distance) &&
                   Pan.Equals(other.Pan);
        }

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is OrbitState other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Yaw.GetHashCode();
                hash = (hash * 397) ^ Pitch.GetHashCode();
                hash = (hash * 397) ^ Distance.GetHashCode();
                hash = (hash * 397) ^ Pan.GetHashCode();
                return hash;
            }
        }

        /// <inheritdoc />
        public override string ToString() =>
            $"OrbitState(Yaw: {Yaw:F1}, Pitch: {Pitch:F1}, Distance: {Distance:F2}, Pan: {Pan})";

        public static bool operator ==(OrbitState left, OrbitState right) => left.Equals(right);
        public static bool operator !=(OrbitState left, OrbitState right) => !left.Equals(right);
    }
}
