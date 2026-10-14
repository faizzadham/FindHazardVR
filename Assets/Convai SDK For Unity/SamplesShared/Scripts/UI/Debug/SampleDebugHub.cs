using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Convai.SampleCommon.UI.Debug
{
    /// <summary>
    ///     Sample-only debug launcher with an accordion drawer for hosted debug panels.
    /// </summary>
    public sealed class SampleDebugHub : MonoBehaviour
    {
        private const float AnimationDuration = 0.22f;
        private const float ClosedDrawerHeight = 280f;
        private const float MaxDrawerHeight = 520f;

        [SerializeField] private int _sortingOrder = 700;
        [SerializeField] private bool _showOnStart = true;
        [SerializeField] private List<MonoBehaviour> _panelBehaviours = new();

        private readonly List<ISampleDebugPanel> _panels = new();
        private readonly Dictionary<ISampleDebugPanel, Image> _buttonBackgrounds = new();
        private readonly Dictionary<ISampleDebugPanel, TMP_Text> _buttonLabels = new();

        private RectTransform _drawerRect;
        private CanvasGroup _drawerCanvasGroup;
        private RectTransform _drawerContentRoot;
        private TMP_Text _drawerTitle;
        private ISampleDebugPanel _activePanel;
        private Coroutine _animationRoutine;
        private bool _built;
        private bool _drawerOpen;

        private void Awake()
        {
            CachePanels();
            BuildUi();
            gameObject.SetActive(_showOnStart);
        }

        private void CachePanels()
        {
            _panels.Clear();
            foreach (MonoBehaviour behaviour in _panelBehaviours)
            {
                if (behaviour is not ISampleDebugPanel panel)
                    continue;

                _panels.Add(panel);
            }

            if (_panels.Count > 0)
                return;

            foreach (MonoBehaviour behaviour in GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour is ISampleDebugPanel panel && !_panels.Contains(panel))
                    _panels.Add(panel);
            }
        }

        private void BuildUi()
        {
            if (_built)
                return;

            Canvas canvas = GetComponent<Canvas>();
            if (canvas == null)
                canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = _sortingOrder;

            if (GetComponent<CanvasScaler>() == null)
            {
                CanvasScaler scaler = gameObject.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 0.5f;
            }

            if (GetComponent<GraphicRaycaster>() == null)
                gameObject.AddComponent<GraphicRaycaster>();

            GameObject root = SampleDebugUi.CreateUiObject("Root", transform);
            RectTransform rootRect = root.GetComponent<RectTransform>();
            rootRect.anchorMin = new Vector2(0f, 1f);
            rootRect.anchorMax = new Vector2(0f, 1f);
            rootRect.pivot = new Vector2(0f, 1f);
            rootRect.anchoredPosition = new Vector2(20f, -36f);

            HorizontalLayoutGroup rootLayout = root.AddComponent<HorizontalLayoutGroup>();
            rootLayout.spacing = 10f;
            rootLayout.childAlignment = TextAnchor.UpperLeft;
            rootLayout.childControlWidth = true;
            rootLayout.childControlHeight = true;
            rootLayout.childForceExpandWidth = false;
            rootLayout.childForceExpandHeight = false;
            ContentSizeFitter rootFitter = root.AddComponent<ContentSizeFitter>();
            rootFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            rootFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            GameObject rail = SampleDebugUi.CreateUiObject("Rail", root.transform);
            Image railImage = rail.AddComponent<Image>();
            railImage.color = new Color(0.04f, 0.043f, 0.05f, 0.94f);
            Shadow railShadow = rail.AddComponent<Shadow>();
            railShadow.effectColor = new Color(0f, 0f, 0f, 0.42f);
            railShadow.effectDistance = new Vector2(0f, -3f);
            VerticalLayoutGroup railLayout = rail.AddComponent<VerticalLayoutGroup>();
            railLayout.padding = new RectOffset(10, 10, 12, 12);
            railLayout.spacing = 8f;
            railLayout.childAlignment = TextAnchor.UpperCenter;
            railLayout.childControlWidth = true;
            railLayout.childControlHeight = true;
            railLayout.childForceExpandWidth = true;
            railLayout.childForceExpandHeight = false;
            LayoutElement railLayoutElement = rail.AddComponent<LayoutElement>();
            railLayoutElement.minWidth = 148f;
            railLayoutElement.preferredWidth = 148f;

            SampleDebugUi.AddText(rail.transform, "DEBUG TOOLS", 13f, FontStyles.Bold, new Color(0.68f, 0.86f, 0.73f, 1f));

            foreach (ISampleDebugPanel panel in _panels)
            {
                ISampleDebugPanel captured = panel;
                Button button = CreateRailButton(rail.transform, panel.PanelLabel, () => TogglePanel(captured));
                _buttonBackgrounds[panel] = button.GetComponent<Image>();
                _buttonLabels[panel] = button.GetComponentInChildren<TMP_Text>();
            }

            GameObject drawer = SampleDebugUi.CreateUiObject("Drawer", root.transform);
            _drawerRect = drawer.GetComponent<RectTransform>();
            Image drawerImage = drawer.AddComponent<Image>();
            drawerImage.color = new Color(0.045f, 0.048f, 0.055f, 0.96f);
            Shadow drawerShadow = drawer.AddComponent<Shadow>();
            drawerShadow.effectColor = new Color(0f, 0f, 0f, 0.46f);
            drawerShadow.effectDistance = new Vector2(0f, -3f);
            _drawerCanvasGroup = drawer.AddComponent<CanvasGroup>();
            _drawerCanvasGroup.alpha = 0f;
            _drawerCanvasGroup.interactable = false;
            _drawerCanvasGroup.blocksRaycasts = false;

            LayoutElement drawerLayout = drawer.AddComponent<LayoutElement>();
            drawerLayout.minWidth = 0f;
            drawerLayout.preferredWidth = 0f;
            drawerLayout.flexibleWidth = 0f;
            drawerLayout.minHeight = 280f;
            drawerLayout.preferredHeight = 280f;

            GameObject drawerHeader = SampleDebugUi.CreateUiObject("Header", drawer.transform);
            RectTransform headerRect = drawerHeader.GetComponent<RectTransform>();
            headerRect.anchorMin = new Vector2(0f, 1f);
            headerRect.anchorMax = new Vector2(1f, 1f);
            headerRect.pivot = new Vector2(0.5f, 1f);
            headerRect.sizeDelta = new Vector2(0f, 42f);
            headerRect.anchoredPosition = Vector2.zero;
            Image headerImage = drawerHeader.AddComponent<Image>();
            headerImage.color = new Color(0.03f, 0.032f, 0.038f, 0.9f);

            GameObject accent = SampleDebugUi.CreateUiObject("Accent", drawerHeader.transform);
            RectTransform accentRect = accent.GetComponent<RectTransform>();
            accentRect.anchorMin = new Vector2(0f, 0f);
            accentRect.anchorMax = new Vector2(0f, 1f);
            accentRect.pivot = new Vector2(0f, 0.5f);
            accentRect.sizeDelta = new Vector2(4f, 0f);
            accentRect.anchoredPosition = Vector2.zero;
            Image accentImage = accent.AddComponent<Image>();
            accentImage.color = new Color(0.31f, 0.74f, 0.44f, 1f);
            accentImage.raycastTarget = false;

            _drawerTitle = SampleDebugUi.AddText(drawerHeader.transform, "Debug", 14f, FontStyles.Bold,
                new Color(0.90f, 0.93f, 0.95f, 1f));
            RectTransform drawerTitleRect = _drawerTitle.GetComponent<RectTransform>();
            drawerTitleRect.anchorMin = Vector2.zero;
            drawerTitleRect.anchorMax = Vector2.one;
            drawerTitleRect.offsetMin = new Vector2(14f, 0f);
            drawerTitleRect.offsetMax = new Vector2(-44f, 0f);
            _drawerTitle.alignment = TextAlignmentOptions.MidlineLeft;

            CreateCloseButton(drawerHeader.transform, CloseDrawer);

            GameObject scrollRoot = SampleDebugUi.CreateUiObject("Scroll", drawer.transform);
            RectTransform scrollRectTransform = scrollRoot.GetComponent<RectTransform>();
            scrollRectTransform.anchorMin = Vector2.zero;
            scrollRectTransform.anchorMax = Vector2.one;
            scrollRectTransform.offsetMin = new Vector2(0f, 0f);
            scrollRectTransform.offsetMax = new Vector2(0f, -42f);
            Image scrollImage = scrollRoot.AddComponent<Image>();
            scrollImage.color = new Color(0.02f, 0.022f, 0.026f, 0.35f);
            scrollRoot.AddComponent<Mask>().showMaskGraphic = false;
            ScrollRect scrollRect = scrollRoot.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 24f;

            GameObject content = SampleDebugUi.CreateUiObject("Content", scrollRoot.transform);
            _drawerContentRoot = content.GetComponent<RectTransform>();
            _drawerContentRoot.anchorMin = new Vector2(0f, 1f);
            _drawerContentRoot.anchorMax = new Vector2(1f, 1f);
            _drawerContentRoot.pivot = new Vector2(0.5f, 1f);
            _drawerContentRoot.offsetMin = Vector2.zero;
            _drawerContentRoot.offsetMax = Vector2.zero;
            VerticalLayoutGroup contentLayout = content.AddComponent<VerticalLayoutGroup>();
            contentLayout.padding = new RectOffset(14, 14, 12, 12);
            contentLayout.spacing = 8f;
            contentLayout.childForceExpandWidth = true;
            contentLayout.childForceExpandHeight = false;
            content.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scrollRect.content = _drawerContentRoot;
            scrollRect.viewport = scrollRectTransform;

            foreach (ISampleDebugPanel panel in _panels)
                panel.ConfigureHosted(_drawerContentRoot);

            RefreshButtonStyles();
            _built = true;
        }

        private void TogglePanel(ISampleDebugPanel panel)
        {
            if (_activePanel == panel && _drawerOpen)
            {
                CloseDrawer();
                return;
            }

            if (_activePanel != null && _activePanel != panel)
                _activePanel.OnPanelHidden();

            _activePanel = panel;
            if (_drawerTitle != null)
                _drawerTitle.text = $"{panel.PanelLabel} Debug";
            Vector2 drawerSize = ResolveDrawerSize(panel.PreferredDrawerSize);
            PrepareDrawerLayout(drawerSize);
            _activePanel.EnsureUiBuilt();
            _activePanel.OnPanelShown();
            RefreshDrawerLayout();
            RefreshButtonStyles();
            OpenDrawer(drawerSize);
        }

        private void OpenDrawer(Vector2 size)
        {
            if (_animationRoutine != null)
                StopCoroutine(_animationRoutine);

            _drawerOpen = true;
            _animationRoutine = StartCoroutine(AnimateDrawer(size.x, size.y, 1f, true));
        }

        private void CloseDrawer()
        {
            if (_animationRoutine != null)
                StopCoroutine(_animationRoutine);

            if (_activePanel != null)
            {
                _activePanel.OnPanelHidden();
                _activePanel = null;
            }

            _drawerOpen = false;
            if (_drawerTitle != null)
                _drawerTitle.text = "Debug";
            RefreshButtonStyles();
            _animationRoutine = StartCoroutine(AnimateDrawer(0f, ClosedDrawerHeight, 0f, false));
        }

        private static Vector2 ResolveDrawerSize(Vector2 preferredSize) =>
            new(preferredSize.x, Mathf.Min(preferredSize.y, MaxDrawerHeight));

        private void PrepareDrawerLayout(Vector2 size)
        {
            LayoutElement drawerLayout = _drawerRect.GetComponent<LayoutElement>();
            drawerLayout.preferredWidth = size.x;
            drawerLayout.minWidth = size.x;
            drawerLayout.preferredHeight = size.y;
            drawerLayout.minHeight = size.y;
            ApplyDrawerSize(size.x, size.y);
            RefreshDrawerLayout();
        }

        private void RefreshDrawerLayout()
        {
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(_drawerRect);
            LayoutRebuilder.ForceRebuildLayoutImmediate(_drawerContentRoot);
        }

        private IEnumerator AnimateDrawer(float targetWidth, float targetHeight, float targetAlpha, bool interactive)
        {
            LayoutElement drawerLayout = _drawerRect.GetComponent<LayoutElement>();
            float startWidth = drawerLayout.preferredWidth;
            float startHeight = drawerLayout.preferredHeight;
            float startAlpha = _drawerCanvasGroup.alpha;
            float elapsed = 0f;

            while (elapsed < AnimationDuration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / AnimationDuration));
                float width = Mathf.Lerp(startWidth, targetWidth, t);
                float height = Mathf.Lerp(startHeight, targetHeight, t);
                drawerLayout.preferredWidth = width;
                drawerLayout.minWidth = width;
                drawerLayout.preferredHeight = height;
                drawerLayout.minHeight = height;
                ApplyDrawerSize(width, height);
                _drawerCanvasGroup.alpha = Mathf.Lerp(startAlpha, targetAlpha, t);
                yield return null;
            }

            drawerLayout.preferredWidth = targetWidth;
            drawerLayout.minWidth = targetWidth;
            drawerLayout.preferredHeight = targetHeight;
            drawerLayout.minHeight = targetHeight;
            ApplyDrawerSize(targetWidth, targetHeight);
            _drawerCanvasGroup.alpha = targetAlpha;
            _drawerCanvasGroup.interactable = interactive;
            _drawerCanvasGroup.blocksRaycasts = interactive;
            _animationRoutine = null;
        }

        private void ApplyDrawerSize(float width, float height)
        {
            _drawerRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            _drawerRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
        }

        private void RefreshButtonStyles()
        {
            foreach (KeyValuePair<ISampleDebugPanel, Image> entry in _buttonBackgrounds)
            {
                bool active = _drawerOpen && ReferenceEquals(entry.Key, _activePanel);
                entry.Value.color = active
                    ? new Color(0.18f, 0.42f, 0.31f, 1f)
                    : new Color(0.11f, 0.13f, 0.15f, 1f);

                if (_buttonLabels.TryGetValue(entry.Key, out TMP_Text label))
                    label.color = active
                        ? Color.white
                        : new Color(0.78f, 0.82f, 0.86f, 1f);
            }
        }

        private Button CreateRailButton(Transform parent, string label, UnityEngine.Events.UnityAction onClick)
        {
            GameObject buttonObject = SampleDebugUi.CreateUiObject("Button", parent);
            Image image = buttonObject.AddComponent<Image>();
            image.color = new Color(0.11f, 0.13f, 0.15f, 1f);
            LayoutElement layout = buttonObject.AddComponent<LayoutElement>();
            layout.minHeight = 40f;
            layout.preferredHeight = 40f;

            Button button = buttonObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(onClick);

            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.14f, 1.17f, 1.15f, 1f);
            colors.pressedColor = new Color(0.78f, 0.83f, 0.80f, 1f);
            colors.selectedColor = new Color(1.08f, 1.12f, 1.09f, 1f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            TMP_Text text = SampleDebugUi.AddText(buttonObject.transform, label, 13f, FontStyles.Bold);
            RectTransform textRect = text.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(8f, 0f);
            textRect.offsetMax = new Vector2(-8f, 0f);
            text.alignment = TextAlignmentOptions.Center;
            return button;
        }

        private static void CreateCloseButton(Transform parent, UnityEngine.Events.UnityAction onClick)
        {
            GameObject buttonObject = SampleDebugUi.CreateUiObject("Close", parent);
            RectTransform buttonRect = buttonObject.GetComponent<RectTransform>();
            buttonRect.anchorMin = new Vector2(1f, 0.5f);
            buttonRect.anchorMax = new Vector2(1f, 0.5f);
            buttonRect.pivot = new Vector2(1f, 0.5f);
            buttonRect.anchoredPosition = new Vector2(-8f, 0f);
            buttonRect.sizeDelta = new Vector2(30f, 30f);

            Image image = buttonObject.AddComponent<Image>();
            image.color = new Color(0.12f, 0.14f, 0.16f, 1f);
            Button button = buttonObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(onClick);

            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.28f, 1.28f, 1.28f, 1f);
            colors.pressedColor = new Color(0.72f, 0.72f, 0.72f, 1f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            TMP_Text label = SampleDebugUi.AddText(buttonObject.transform, "×", 20f, FontStyles.Normal,
                new Color(0.82f, 0.86f, 0.89f, 1f));
            RectTransform labelRect = label.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
            label.alignment = TextAlignmentOptions.Center;
        }

    }
}
