using NUnit.Framework;
using Convai.Runtime.Embodiment;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Fixtures
{
    /// <summary>
    ///     Builds a headless <see cref="EmbodimentContext" /> on a single owned
    ///     <see cref="GameObject" /> for use in EditMode unit and component tests.
    ///     Call <see cref="Dispose" /> (or use in a <c>using</c> block) to destroy the
    ///     root <see cref="GameObject" /> and prevent scene leaks.
    /// </summary>
    public sealed class EmbodimentTestRig : System.IDisposable
    {
        private readonly GameObject _root;
        private bool _disposed;

        public EmbodimentContext Context { get; }
        public FakeEventHub EventHub { get; }
        public GameObject Root => _root;

        private EmbodimentTestRig(string name, FakeEventHub eventHub)
        {
            EventHub = eventHub;
            _root = new GameObject(name);
            Context = _root.AddComponent<EmbodimentContext>();
            Context.Populate(EventHub, null);
        }

        /// <summary>
        ///     Creates an <see cref="EmbodimentTestRig" /> with a fresh
        ///     <see cref="FakeEventHub" />. Call <see cref="Dispose" /> when done.
        /// </summary>
        public static EmbodimentTestRig Create(string name = "EmbodimentTestRig")
            => new(name, new FakeEventHub());

        /// <summary>
        ///     Creates a rig, runs the provided test body, then disposes the rig.
        ///     Convenience for one-shot tests that don't use <c>using</c>.
        /// </summary>
        public static void Run(string name, System.Action<EmbodimentTestRig> testBody)
        {
            using EmbodimentTestRig rig = Create(name);
            testBody(rig);
        }

        /// <summary>Adds a <typeparamref name="T" /> component to the rig root.</summary>
        public T AddComponent<T>() where T : MonoBehaviour => _root.AddComponent<T>();

        /// <summary>Creates a child <see cref="GameObject" /> under the rig root.</summary>
        public GameObject CreateChildObject(string childName = "child")
        {
            GameObject child = new(childName);
            child.transform.SetParent(_root.transform);
            return child;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            LogAssert.NoUnexpectedReceived();
            if (_root != null)
                Object.DestroyImmediate(_root);
        }
    }
}
