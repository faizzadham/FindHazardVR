using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Convai.Runtime.Components
{
    /// <summary>
    ///     Reads button state from one Input System device type through reflection, without
    ///     allocating once a control has been resolved.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The SDK cannot reference <c>Unity.InputSystem</c> directly: the package is optional,
    ///         and a hard reference would break every project that ships with the legacy Input
    ///         Manager only. Reflection is therefore the only portable way to read the new backend.
    ///     </para>
    ///     <para>
    ///         Doing that reflection per frame is what made idle polling expensive. Every step of a
    ///         lookup — <see cref="Type.GetType(string)" />, <see cref="Type.GetProperty(string)" />,
    ///         the <c>object[]</c> an indexer call needs, and the boxed <see cref="bool" /> a
    ///         <see cref="PropertyInfo.GetValue(object)" /> hands back — allocates. This type pays
    ///         that cost once per control and then calls a <see cref="Func{Boolean}" /> bound
    ///         directly to the control instance, which allocates nothing and returns an unboxed
    ///         <see cref="bool" />.
    ///     </para>
    ///     <para>
    ///         Bound delegates cannot outlive the device they read. The device is re-read from the
    ///         static <c>current</c> property on every query — a delegate call, not a reflection
    ///         call — and every cached control is dropped as soon as that reference changes, so an
    ///         unplugged, replaced or newly appeared keyboard is picked up on the next query rather
    ///         than reporting a dead device forever.
    ///     </para>
    ///     <para>
    ///         Unresolvable names are cached as failures, so a missing package or an unknown key
    ///         costs one failed lookup rather than one per frame. Main thread only, like the Unity
    ///         input APIs it stands in for.
    ///     </para>
    /// </remarks>
    internal sealed class ConvaiInputDeviceProbe
    {
        internal const string WasPressedThisFrameProperty = "wasPressedThisFrame";
        internal const string IsPressedProperty = "isPressed";

        private readonly string _deviceTypeName;
        private readonly string _keyEnumTypeName;

        private readonly Dictionary<KeyCode, Func<bool>> _keyHeld = new(KeyCodeComparer.Instance);
        private readonly Dictionary<KeyCode, Func<bool>> _keyPressedThisFrame = new(KeyCodeComparer.Instance);
        private readonly Dictionary<string, Func<bool>> _pathPressedThisFrame = new(StringComparer.Ordinal);

        private object _boundDevice;
        private Func<object> _currentDeviceGetter;
        private Type _deviceType;
        private PropertyInfo _keyIndexer;
        private Type _keyEnumType;
        private bool _typesResolved;

        /// <param name="deviceTypeName">
        ///     Assembly-qualified name of the device type, for example
        ///     <c>UnityEngine.InputSystem.Keyboard, Unity.InputSystem</c>.
        /// </param>
        /// <param name="keyEnumTypeName">
        ///     Assembly-qualified name of the key enum used by the device indexer, or
        ///     <see langword="null" /> for devices that are only addressed by control path.
        /// </param>
        internal ConvaiInputDeviceProbe(string deviceTypeName, string keyEnumTypeName = null)
        {
            _deviceTypeName = deviceTypeName;
            _keyEnumTypeName = keyEnumTypeName;
        }

        /// <summary>Whether the device type and its <c>current</c> accessor resolved.</summary>
        internal bool IsAvailable
        {
            get
            {
                EnsureTypesResolved();
                return _currentDeviceGetter != null;
            }
        }

        /// <summary>Whether a device of this type is currently connected.</summary>
        internal bool HasDevice => TryGetDevice(out _);

        /// <summary>Was the control at <paramref name="controlPath" /> pressed during this frame?</summary>
        /// <param name="controlPath">
        ///     Slash-separated property path from the device, for example <c>leftButton</c> or
        ///     <c>primaryTouch/press</c>. Pass a literal or cached string; this is a hot path.
        /// </param>
        internal bool WasControlPressedThisFrame(string controlPath)
        {
            if (!TryGetDevice(out object device)) return false;

            if (!_pathPressedThisFrame.TryGetValue(controlPath, out Func<bool> probe))
            {
                probe = CreateControlProbe(ResolveControl(device, controlPath), WasPressedThisFrameProperty);
                _pathPressedThisFrame[controlPath] = probe;
            }

            return probe != null && probe();
        }

        /// <summary>Was <paramref name="keyCode" /> pressed during this frame?</summary>
        internal bool WasKeyPressedThisFrame(KeyCode keyCode) =>
            QueryKey(_keyPressedThisFrame, keyCode, WasPressedThisFrameProperty);

        /// <summary>Is <paramref name="keyCode" /> held down right now?</summary>
        internal bool IsKeyHeld(KeyCode keyCode) => QueryKey(_keyHeld, keyCode, IsPressedProperty);

        private bool QueryKey(Dictionary<KeyCode, Func<bool>> cache, KeyCode keyCode, string statePropertyName)
        {
            if (!TryGetDevice(out object device)) return false;

            if (!cache.TryGetValue(keyCode, out Func<bool> probe))
            {
                probe = CreateControlProbe(ResolveKeyControl(device, keyCode), statePropertyName);
                cache[keyCode] = probe;
            }

            return probe != null && probe();
        }

        /// <summary>
        ///     Reads the live device and drops every bound control when it is not the one the cached
        ///     delegates read from.
        /// </summary>
        private bool TryGetDevice(out object device)
        {
            EnsureTypesResolved();

            device = _currentDeviceGetter?.Invoke();
            if (!ReferenceEquals(device, _boundDevice))
            {
                _boundDevice = device;
                _keyPressedThisFrame.Clear();
                _keyHeld.Clear();
                _pathPressedThisFrame.Clear();
            }

            return device != null;
        }

        private void EnsureTypesResolved()
        {
            if (_typesResolved) return;
            _typesResolved = true;

            try
            {
                _deviceType = Type.GetType(_deviceTypeName, false);
                if (_deviceType == null) return;

                _currentDeviceGetter = CreateCurrentDeviceGetter(_deviceType);

                if (string.IsNullOrEmpty(_keyEnumTypeName)) return;
                _keyEnumType = Type.GetType(_keyEnumTypeName, false);
                if (_keyEnumType != null)
                    _keyIndexer = _deviceType.GetProperty("Item", new[] { _keyEnumType });
            }
            catch (Exception)
            {
                // An optional package that is present but unusable must not take input down with it.
                _currentDeviceGetter = null;
                _keyIndexer = null;
            }
        }

        private static Func<object> CreateCurrentDeviceGetter(Type deviceType)
        {
            MethodInfo getter = deviceType
                .GetProperty("current", BindingFlags.Public | BindingFlags.Static)?
                .GetGetMethod();
            if (getter == null || getter.ReturnType.IsValueType) return null;

            // Reference-type return values bind covariantly, so the static accessor becomes a plain
            // delegate call instead of a reflection call.
            var bound = Delegate.CreateDelegate(typeof(Func<object>), getter, false) as Func<object>;
            if (bound != null) return bound;

            // Binding can only fail for a shape the Input System does not currently use; reflection
            // still answers correctly, just not for free.
            return () => getter.Invoke(null, null);
        }

        private object ResolveKeyControl(object device, KeyCode keyCode)
        {
            if (_keyIndexer == null || _keyEnumType == null) return null;

            string keyName = ConvaiInputSystem.ConvertToInputSystemKeyName(keyCode);
            if (string.IsNullOrEmpty(keyName)) return null;

            try
            {
                object keyValue = Enum.Parse(_keyEnumType, keyName, false);
                return _keyIndexer.GetValue(device, new[] { keyValue });
            }
            catch (Exception)
            {
                // A KeyCode the new backend has no equivalent for simply never reports a press.
                return null;
            }
        }

        private static object ResolveControl(object device, string controlPath)
        {
            if (string.IsNullOrEmpty(controlPath)) return null;

            object current = device;
            string[] segments = controlPath.Split('/');
            for (int i = 0; i < segments.Length && current != null; i++)
            {
                current = current.GetType()
                    .GetProperty(segments[i], BindingFlags.Public | BindingFlags.Instance)?
                    .GetValue(current);
            }

            return current;
        }

        private static Func<bool> CreateControlProbe(object control, string statePropertyName)
        {
            if (control == null) return null;

            MethodInfo getter = control.GetType()
                .GetProperty(statePropertyName, BindingFlags.Public | BindingFlags.Instance)?
                .GetGetMethod();
            if (getter == null || getter.ReturnType != typeof(bool)) return null;

            // Closed over the control instance: no argument array, no boxed return value.
            return (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), control, getter, false);
        }

        /// <summary>Keeps <see cref="KeyCode" /> keys out of the boxing path of the default comparer.</summary>
        private sealed class KeyCodeComparer : IEqualityComparer<KeyCode>
        {
            internal static readonly KeyCodeComparer Instance = new();

            public bool Equals(KeyCode x, KeyCode y) => x == y;

            public int GetHashCode(KeyCode obj) => (int)obj;
        }
    }
}
