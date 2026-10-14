using System;

namespace Convai.Runtime
{
    /// <summary>Selects the live character or an API-provided released character reference.</summary>
    public enum ConvaiCharacterVersionMode
    {
        /// <summary>Use the current/live reference; a bare character ID also works before discovery.</summary>
        Live = 0,

        /// <summary>Source-compatible name for the former default. Serialized value zero now selects Live.</summary>
        [Obsolete("Use Live. The default now uses the bare live reference, not the -latest alias.")]
        Latest = Live,

        /// <summary>Use the explicitly configured released version.</summary>
        Specific = 1
    }
}
