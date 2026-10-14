using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;
using System.Text.RegularExpressions;
using UnityEngine.TestTools;
using Convai.Runtime.Components;

namespace Convai.Tests.EditMode.Embodiment
{
    public sealed class EmbodimentContextLifecycleTests
    {
        [Test]
        public void TryResolve_WithNullOrigin_ReturnsFalse()
        {
            bool result = EmbodimentContext.TryResolve(null, out EmbodimentContext ctx);
            Assert.IsFalse(result);
            Assert.IsNull(ctx);
        }

        [Test]
        public void TryResolve_OnPlainGameObject_RefusesQuietly()
        {
            // Inverted deliberately. This used to grow a composition root on whatever object it was
            // handed, so an embodiment component dropped somewhere wrong half-worked.
            //
            // Quiet, because this overload is also the "is there one?" lookup: for callers like the
            // preset binding, which works fine without a context, a missing one is a normal answer
            // and not something to shout about. The loud version is TryResolveFor, below.
            GameObject root = new("LifecycleTest_Create");
            try
            {
                Stub stub = root.AddComponent<Stub>();

                bool resolved = EmbodimentContext.TryResolve(stub, out EmbodimentContext ctx);

                Assert.IsFalse(resolved, "A non-character object must not receive a composition root.");
                Assert.IsNull(ctx);
                Assert.IsNull(root.GetComponent<EmbodimentContext>());
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void TryResolveFor_OnPlainGameObject_ReportsTheMistake()
        {
            // The counterpart: callers that cannot work without a context get told, so a user whose
            // component "just does nothing" learns why instead of guessing.
            GameObject root = new("LifecycleTest_ResolveFor");
            try
            {
                Stub stub = root.AddComponent<Stub>();

                LogAssert.Expect(LogType.Error, new Regex("is not on a Convai character"));
                bool resolved = EmbodimentContext.TryResolveFor(stub, out EmbodimentContext ctx);

                Assert.IsFalse(resolved);
                Assert.IsNull(ctx);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void TryResolve_CalledTwice_ReturnsSameInstance()
        {
            GameObject root = new("LifecycleTest_Idempotent");
            try
            {
                root.AddComponent<ConvaiCharacter>();
                Stub stub = root.AddComponent<Stub>();
                EmbodimentContext.TryResolve(stub, out EmbodimentContext first);
                EmbodimentContext.TryResolve(stub, out EmbodimentContext second);

                Assert.IsNotNull(first);
                Assert.AreSame(first, second);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void TryResolve_ChildComponent_FindsParentContext()
        {
            GameObject parent = new("LifecycleTest_Parent");
            GameObject child = new("LifecycleTest_Child");
            child.transform.SetParent(parent.transform);
            try
            {
                EmbodimentContext parentCtx = parent.AddComponent<EmbodimentContext>();
                Stub childStub = child.AddComponent<Stub>();

                EmbodimentContext.TryResolve(childStub, out EmbodimentContext resolved);

                Assert.AreSame(parentCtx, resolved);
            }
            finally { Object.DestroyImmediate(parent); }
        }

        [Test]
        public void CreatedContext_IsVisibleInTheInspector()
        {
            // Inverted deliberately, and this is the regression guard for a defect that shipped:
            // because the module base class runs in Edit Mode, the old HideInInspector stamp was
            // serialized into every scene a Convai module was added to — including both samples,
            // which shipped with a composition root the user could not see, select, or remove.
            GameObject root = new("LifecycleTest_HideFlags");
            try
            {
                root.AddComponent<ConvaiCharacter>();
                Stub stub = root.AddComponent<Stub>();
                EmbodimentContext.TryResolve(stub, out EmbodimentContext ctx);

                Assert.IsNotNull(ctx);
                Assert.AreEqual(HideFlags.None, ctx.hideFlags,
                    "A component the user cannot see is a component they cannot debug.");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void Populate_FiresDependenciesPopulated()
        {
            GameObject root = new("LifecycleTest_Populate");
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                bool fired = false;
                ctx.DependenciesPopulated += () => fired = true;

                ctx.Populate(null, null);

                Assert.IsTrue(fired);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void NotifyEmbodimentConfigurationChanged_FiresEventOnEachCall()
        {
            GameObject root = new("LifecycleTest_ConfigChanged");
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                int count = 0;
                ctx.EmbodimentConfigurationChanged += () => count++;

                ctx.NotifyEmbodimentConfigurationChanged();
                ctx.NotifyEmbodimentConfigurationChanged();

                Assert.AreEqual(2, count);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void AddedManuallyToGameObject_ContextIsNotNull()
        {
            GameObject root = new("LifecycleTest_ManualAdd");
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                Assert.IsNotNull(ctx);
                Assert.AreSame(root, ctx.gameObject);
            }
            finally { Object.DestroyImmediate(root); }
        }

        private sealed class Stub : MonoBehaviour { }
    }
}
