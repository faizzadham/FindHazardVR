using UnityEngine.Scripting;

// Nothing references this optional module directly; it registers its detector factory from a
// RuntimeInitializeOnLoadMethod. Without these, IL2CPP code stripping removes the registration and
// client-side Silero detection never starts in player builds.
[assembly: AlwaysLinkAssembly]
[assembly: Preserve]
