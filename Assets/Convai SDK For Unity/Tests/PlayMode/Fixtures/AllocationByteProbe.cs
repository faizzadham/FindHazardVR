#if UNITY_EDITOR
using System;
using Unity.Profiling;
using UnityEngine;

namespace Convai.Tests.PlayMode.Fixtures
{
    public sealed class AllocationByteProbe : MonoBehaviour
    {
        public string Scope;
        public ProfilerMarker Marker;
        private byte[] _inside, _outside;

        private void LateUpdate()
        {
            _outside = new byte[4096];
            using (Marker.Auto()) _inside = new byte[128];
        }

        private void OnDestroy()
        {
            GC.KeepAlive(_inside);
            GC.KeepAlive(_outside);
        }
    }
}
#endif
