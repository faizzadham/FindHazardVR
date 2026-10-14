using System.Collections.Generic;
using System.Linq;
using UnityEngine.Scripting;

namespace LiveKit
{
    public class JSEventEmitter<T> : JSObject
    {
        internal class EventWrapper : JSObject
        {
            public T Event;
            public JSRef FncRef;
        }
        
        internal readonly Dictionary<T, EventWrapper> Events = new Dictionary<T, EventWrapper>();

        [Preserve]
        internal JSEventEmitter(JSHandle handle) : base(handle)
        {
            SetKeepAlive(NativeHandle, true);
        }

        internal JSEventEmitter()
        {
            SetKeepAlive(NativeHandle, true);
        }
        
        protected override void Dispose(bool disposing)
        {
            // Remove the listeners while this emitter's handle is still open. base.Dispose closes it, and
            // RemoveListener then threw after pushing its arguments: the stale arguments corrupted the next
            // native call, and the listeners left registered kept calling into released references.
            foreach (var k in Events.Keys.ToArray())
                RemoveListener(k);

            SetKeepAlive(NativeHandle, false);
            base.Dispose(disposing);
        }

        // Similar to "on" but we only accepts one listener (No need for multiple in internal use)
        internal void SetListener(T eventt, JSNative.JSDelegate fnc)
        {
            var wrapper = new EventWrapper
            {
                Event = eventt,
                FncRef = new JSRef()
            };
            
            SetKeepAlive(wrapper.FncRef, true);
            
            Events.Add(eventt, wrapper);
            
            JSNative.PushFunction(wrapper.NativeHandle, fnc, $"JSEventEmitter - Listener: {Utils.ToEnumString(eventt)}");
            JSNative.SetRef(wrapper.FncRef.NativeHandle);
            
            JSNative.PushString(Utils.ToEnumString(eventt));
            JSNative.PushObject(wrapper.FncRef.NativeHandle);
            JSNative.CallMethod(NativeHandle, "on");
        }

        internal void RemoveListener(T eventt)
        {
            if (!Events.TryGetValue(eventt, out var wrapper))
                return;
            
            JSNative.PushString(Utils.ToEnumString(eventt));
            JSNative.PushObject(wrapper.FncRef.NativeHandle);
            JSNative.CallMethod(NativeHandle, "removeListener");

            SetKeepAlive(wrapper.FncRef, false);
            Events.Remove(eventt);
        }
    }
}