using System.Linq;
using System.Reflection;
using Convai.Shared.Compatibility;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Runtime
{
    /// <summary>
    ///     Behavioural cover for the version-compatibility seams. The guard test proves nothing
    ///     bypasses them; this proves they answer correctly on the editor the suite runs on.
    /// </summary>
    /// <remarks>
    ///     Ids are session-scoped by contract, so these assertions deliberately check identity and
    ///     distinctness rather than any particular numeric value — the value differs by editor band
    ///     (see <see cref="ConvaiObjectId" />).
    /// </remarks>
    [TestFixture]
    public sealed class ConvaiObjectCompatibilitySeamTests
    {
        [SetUp]
        public void SetUp()
        {
            _active = new GameObject(ActiveName);
            _inactive = new GameObject(InactiveName);
            _inactive.SetActive(false);
        }

        [TearDown]
        public void TearDown()
        {
            if (_active != null) Object.DestroyImmediate(_active);
            if (_inactive != null) Object.DestroyImmediate(_inactive);
        }

        private const string ActiveName = "ConvaiSeamTests_Active";
        private const string InactiveName = "ConvaiSeamTests_Inactive";

        private GameObject _active;
        private GameObject _inactive;

        [Test]
        public void Of_IsStableForTheSameObject_DistinctAcrossObjects_AndZeroForNull()
        {
            long activeId = ConvaiObjectId.Of(_active);

            Assert.That(activeId, Is.Not.Zero, "A live object must have a non-zero id.");
            Assert.That(ConvaiObjectId.Of(_active), Is.EqualTo(activeId),
                "The same object must report the same id within a session.");
            Assert.That(ConvaiObjectId.Of(_inactive), Is.Not.EqualTo(activeId),
                "Two distinct objects must not share an id.");
            Assert.That(ConvaiObjectId.Of(null), Is.Zero, "A null object must report zero, not throw.");
        }

        [Test]
        public void TryResolve_RoundTripsAnObjectThroughItsId()
        {
            long id = ConvaiObjectId.Of(_active);

            Assert.That(ConvaiObjectId.TryResolve(id, out Object resolved), Is.True,
                "A freshly minted id must resolve back to its object.");
            Assert.That(resolved, Is.SameAs(_active));

            Assert.That(ConvaiObjectId.TryResolve(0L, out Object none), Is.False,
                "Zero is the documented 'no object' id.");
            Assert.That(none, Is.Null);
        }

        [Test]
        public void TryResolve_ReturnsFalseForAnIdThatNoLongerNamesAnything()
        {
            long id = ConvaiObjectId.Of(_active);
            Object.DestroyImmediate(_active);
            _active = null;

            Assert.That(ConvaiObjectId.TryResolve(id, out Object resolved), Is.False,
                "An id whose object is gone must report failure, not hand back a destroyed reference.");
            Assert.That(resolved, Is.Null);
        }

        [Test]
        public void TryResolveTyped_UnwrapsBetweenGameObjectAndComponent()
        {
            BoxCollider component = _active.AddComponent<BoxCollider>();

            Assert.That(ConvaiObjectId.TryResolve(ConvaiObjectId.Of(_active), out BoxCollider fromGameObject),
                Is.True, "A GameObject's id must resolve to a component it carries.");
            Assert.That(fromGameObject, Is.SameAs(component));

            Assert.That(ConvaiObjectId.TryResolve(ConvaiObjectId.Of(component), out GameObject fromComponent),
                Is.True, "A component's id must resolve to its GameObject.");
            Assert.That(fromComponent, Is.SameAs(_active));

            Assert.That(ConvaiObjectId.TryResolve(ConvaiObjectId.Of(_active), out Light absent), Is.False,
                "Asking for a component the object does not carry must fail, not throw.");
            Assert.That(absent, Is.Null);
        }

        [Test]
        public void All_BoolOverload_MatchesTheExplicitInactiveMode()
        {
            Assert.That(ConvaiObjectFind.All<GameObject>(true),
                Is.EquivalentTo(ConvaiObjectFind.All<GameObject>(FindObjectsInactive.Include)),
                "All(true) must mean Include.");
            Assert.That(ConvaiObjectFind.All<GameObject>(false),
                Is.EquivalentTo(ConvaiObjectFind.All<GameObject>(FindObjectsInactive.Exclude)),
                "All(false) must mean Exclude.");
        }

        [Test]
        public void All_HonoursTheInactiveSelection()
        {
            GameObject[] included = ConvaiObjectFind.All<GameObject>(FindObjectsInactive.Include);
            GameObject[] excluded = ConvaiObjectFind.All<GameObject>(FindObjectsInactive.Exclude);

            Assert.That(included, Has.Member(_active));
            Assert.That(included, Has.Member(_inactive),
                "Include must return objects on inactive GameObjects.");
            Assert.That(excluded, Has.Member(_active));
            Assert.That(excluded, Has.No.Member(_inactive),
                "Exclude must leave inactive GameObjects out.");
        }

        [Test]
        public void SceneId_IsStableForOneScene_SharedByItsObjects_AndZeroForAnInvalidScene()
        {
            long sceneId = ConvaiSceneId.Of(_active.scene);

            Assert.That(sceneId, Is.Not.Zero,
                "A loaded scene must get a usable id.");
            Assert.That(ConvaiSceneId.Of(_active.scene), Is.EqualTo(sceneId),
                "The id must not change between two reads of the same scene.");
            Assert.That(ConvaiSceneId.Of(_inactive.scene), Is.EqualTo(sceneId),
                "Two objects in one scene must report the same scene id.");
            Assert.That(ConvaiSceneId.Of(default), Is.Zero,
                "An invalid scene must be 0 rather than a colliding id.");
        }

        /// <remarks>
        ///     The expectation is rebuilt from Unity's own API by reflection rather than restated
        ///     from the seam, so a seam that starts folding, truncating or hashing the handle fails
        ///     here. That is the failure this test exists for: SceneHandle.GetHashCode() is a lossy
        ///     digest, and a digest held as a set member lets two scenes read as one.
        /// </remarks>
        [Test]
        public void SceneId_CarriesTheEditorsWholeSceneIdentity_NotADigestOfIt()
        {
            Scene scene = _active.scene;

            PropertyInfo handleProperty = typeof(Scene).GetProperty("handle",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(handleProperty, Is.Not.Null,
                "Unity no longer exposes the scene handle property. ConvaiSceneId has to be "
                + "remeasured against whatever replaced it before this suite means anything.");

            object handle = handleProperty.GetValue(scene);
            Assert.That(ConvaiSceneId.Of(scene), Is.EqualTo(RawIdentityOf(handle)),
                "The seam must return the editor's whole scene identity, not a digest of it.");
        }

        /// <summary>
        ///     The handle's full value, read the way each editor band offers it: the int itself up
        ///     to 6000.2, the non-deprecated int conversion on 6000.3, and GetRawData() from 6000.4
        ///     on, where the identity is 64 bits wide.
        /// </summary>
        private static long RawIdentityOf(object handle)
        {
            if (handle is int handleAsInt) return handleAsInt;

            MethodInfo getRawData = handle.GetType().GetMethod("GetRawData",
                BindingFlags.Public | BindingFlags.Instance);
            if (getRawData != null) return unchecked((long)(ulong)getRawData.Invoke(handle, null));

            MethodInfo toInt = handle.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "op_Implicit" && m.ReturnType == typeof(int));
            Assert.That(toInt, Is.Not.Null,
                "This editor offers neither GetRawData() nor an int conversion for a scene handle.");

            return (int)toInt.Invoke(null, new[] { handle });
        }
    }
}
