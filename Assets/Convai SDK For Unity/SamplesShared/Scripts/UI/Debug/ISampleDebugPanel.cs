using UnityEngine;

namespace Convai.SampleCommon.UI.Debug
{
    /// <summary>
    ///     Contract for sample-scene debug panels hosted by <see cref="SampleDebugHub" />.
    ///     The hub calls <see cref="ConfigureHosted" /> once during its own setup (before the
    ///     panel builds any UI), then drives <see cref="EnsureUiBuilt" /> /
    ///     <see cref="OnPanelShown" /> / <see cref="OnPanelHidden" /> as its drawer opens and closes.
    /// </summary>
    public interface ISampleDebugPanel
    {
        /// <summary>Short label shown on the hub's rail button (e.g. "Context", "Vision").</summary>
        string PanelLabel { get; }

        /// <summary>Requested drawer size in canvas units when this panel is open.</summary>
        Vector2 PreferredDrawerSize { get; }

        /// <summary>True once the panel has built its UI (hosted or standalone).</summary>
        bool IsUiBuilt { get; }

        /// <summary>
        ///     Marks the panel as hub-hosted and hands it the drawer content root to build into.
        ///     Must be called before <see cref="EnsureUiBuilt" />; a panel that already built its
        ///     standalone UI cannot be re-hosted.
        /// </summary>
        void ConfigureHosted(RectTransform contentRoot);

        /// <summary>Builds the panel UI if it hasn't been built yet; safe to call repeatedly.</summary>
        void EnsureUiBuilt();

        /// <summary>Called when the hub opens this panel's drawer.</summary>
        void OnPanelShown();

        /// <summary>Called when the hub closes this panel's drawer.</summary>
        void OnPanelHidden();
    }
}
