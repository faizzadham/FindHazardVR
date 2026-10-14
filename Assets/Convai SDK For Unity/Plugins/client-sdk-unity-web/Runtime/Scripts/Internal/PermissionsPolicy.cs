namespace LiveKit
{
    /// <summary>
    ///     Reads the page's Permissions Policy, which decides whether a document may use a powerful
    ///     feature such as the microphone at all, before any user prompt.
    /// </summary>
    public static class PermissionsPolicy
    {
        /// <summary>
        ///     Returns whether the current document's Permissions Policy allows <paramref name="feature" />
        ///     (for example <c>"microphone"</c>), or <c>null</c> when the browser does not expose the policy.
        /// </summary>
        /// <remarks>
        ///     A <c>false</c> result means the browser rejects the feature without prompting, typically
        ///     because the build runs in an iframe whose <c>allow</c> attribute does not grant it.
        ///     Only Chromium-based browsers expose the policy, so treat <c>null</c> as "unknown".
        /// </remarks>
        public static bool? AllowsFeature(string feature)
        {
            JSNative.PushString("document");
            var document = JSNative.GetProperty(JSNative.Window);
            if (!JSNative.IsObject(document))
                return null;

            var policy = GetObjectProperty(document, "permissionsPolicy") ?? GetObjectProperty(document, "featurePolicy");
            if (policy == null)
                return null;

            JSNative.PushString(feature);
            var allowed = JSNative.CallMethod(policy, "allowsFeature");
            if (!JSNative.IsBoolean(allowed))
                return null;

            return JSNative.GetBoolean(allowed);
        }

        private static JSHandle GetObjectProperty(JSHandle owner, string name)
        {
            JSNative.PushString(name);
            var value = JSNative.GetProperty(owner);
            return JSNative.IsObject(value) ? value : null;
        }
    }
}
