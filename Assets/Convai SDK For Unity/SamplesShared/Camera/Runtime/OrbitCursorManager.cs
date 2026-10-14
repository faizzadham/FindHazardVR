// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

using UnityEngine;

namespace Convai.Sample.Camera
{
    /// <summary>
    ///     Manages cursor visibility and lock state during camera input.
    ///     Caches the application's cursor state before locking and restores it cleanly
    ///     when input stops or the camera is disabled.
    /// </summary>
    public sealed class OrbitCursorManager
    {
        private bool _cursorStateCached;
        private bool _cachedCursorVisible;
        private CursorLockMode _cachedCursorLockMode;
        private bool _cursorLockActive;

        /// <summary>
        ///     Updates cursor state based on whether camera input is active this frame.
        /// </summary>
        /// <param name="inputActive">True if orbit or pan input is held.</param>
        /// <param name="settings">Camera settings containing lockCursorDuringInput flag.</param>
        public void Update(bool inputActive, OrbitCameraSettings settings)
        {
            if (!settings.lockCursorDuringInput)
            {
                if (_cursorLockActive)
                    Restore();
                return;
            }

            if (inputActive)
            {
                if (!_cursorLockActive)
                {
                    CacheCursorState();
                    _cursorLockActive = true;
                }

                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            else if (_cursorLockActive)
            {
                Restore();
            }
        }

        /// <summary>
        ///     Restores the cached cursor state. Called when input stops or camera is disabled.
        /// </summary>
        public void Restore()
        {
            _cursorLockActive = false;

            if (!_cursorStateCached)
                return;

            Cursor.visible = _cachedCursorVisible;
            Cursor.lockState = _cachedCursorLockMode;
            _cursorStateCached = false;
        }

        /// <summary>
        ///     Resets internal state. Call when re-enabling the camera or changing targets.
        /// </summary>
        public void Reset()
        {
            _cursorStateCached = false;
            _cursorLockActive = false;
        }

        private void CacheCursorState()
        {
            if (_cursorStateCached)
                return;

            _cachedCursorVisible = Cursor.visible;
            _cachedCursorLockMode = Cursor.lockState;
            _cursorStateCached = true;
        }
    }
}
