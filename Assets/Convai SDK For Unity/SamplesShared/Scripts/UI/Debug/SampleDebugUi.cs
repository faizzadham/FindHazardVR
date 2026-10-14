using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Convai.SampleCommon.UI.Debug
{
    /// <summary>
    ///     Shared builders and styling for sample-only runtime debug controls.
    /// </summary>
    internal static class SampleDebugUi
    {
        private static readonly Color InputBackground = new(0.105f, 0.112f, 0.125f, 0.96f);
        private static readonly Color InputBorder = new(0.30f, 0.58f, 0.40f, 0.42f);
        private static readonly Color PlaceholderColor = new(0.48f, 0.52f, 0.56f, 1f);

        public static TMP_InputField CreateInput(
            Transform parent,
            string placeholder,
            string value,
            bool multiline = false)
        {
            GameObject inputObject = CreateUiObject("Input", parent);
            inputObject.SetActive(false);

            Image image = inputObject.AddComponent<Image>();
            image.color = InputBackground;

            Outline outline = inputObject.AddComponent<Outline>();
            outline.effectColor = InputBorder;
            outline.effectDistance = new Vector2(1f, -1f);
            outline.useGraphicAlpha = true;

            LayoutElement layout = inputObject.AddComponent<LayoutElement>();
            layout.minHeight = multiline ? 78f : 36f;
            layout.preferredHeight = layout.minHeight;

            GameObject viewportObject = CreateUiObject("Text Area", inputObject.transform);
            RectTransform viewport = viewportObject.GetComponent<RectTransform>();
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = new Vector2(10f, 5f);
            viewport.offsetMax = new Vector2(-10f, -5f);
            viewportObject.AddComponent<RectMask2D>();

            TextMeshProUGUI text = CreateText(viewportObject.transform, value, Color.white);
            text.alignment = multiline ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.MidlineLeft;

            TextMeshProUGUI placeholderText = CreateText(viewportObject.transform, placeholder, PlaceholderColor);
            placeholderText.fontStyle = FontStyles.Italic;
            placeholderText.alignment = multiline
                ? TextAlignmentOptions.TopLeft
                : TextAlignmentOptions.MidlineLeft;

            TMP_InputField input = inputObject.AddComponent<TMP_InputField>();
            input.lineType = multiline
                ? TMP_InputField.LineType.MultiLineNewline
                : TMP_InputField.LineType.SingleLine;
            input.textViewport = viewport;
            input.textComponent = text;
            input.placeholder = placeholderText;
            input.targetGraphic = image;
            input.customCaretColor = true;
            input.caretColor = new Color(0.55f, 0.92f, 0.66f, 1f);
            input.selectionColor = new Color(0.25f, 0.60f, 0.38f, 0.55f);
            input.scrollSensitivity = 1f;
            input.text = value;

            ColorBlock colors = input.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.06f, 1.10f, 1.07f, 1f);
            colors.pressedColor = new Color(0.88f, 0.94f, 0.90f, 1f);
            colors.selectedColor = new Color(1.04f, 1.10f, 1.06f, 1f);
            colors.disabledColor = new Color(0.55f, 0.58f, 0.60f, 0.65f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            input.colors = colors;

            inputObject.SetActive(true);
            return input;
        }

        public static void StylePrimaryButton(Button button, Image image)
        {
            image.color = new Color(0.18f, 0.34f, 0.25f, 1f);

            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.16f, 1.20f, 1.16f, 1f);
            colors.pressedColor = new Color(0.80f, 0.88f, 0.82f, 1f);
            colors.selectedColor = new Color(1.10f, 1.16f, 1.11f, 1f);
            colors.disabledColor = new Color(0.50f, 0.52f, 0.50f, 0.65f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            button.colors = colors;
        }

        public static void EnsureStandaloneCanvas(GameObject gameObject, int sortingOrder)
        {
            Canvas canvas = gameObject.GetComponent<Canvas>();
            if (canvas == null)
                canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;

            if (gameObject.GetComponent<CanvasScaler>() == null)
            {
                CanvasScaler scaler = gameObject.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 0.5f;
            }

            if (gameObject.GetComponent<GraphicRaycaster>() == null)
                gameObject.AddComponent<GraphicRaycaster>();
        }

        public static GameObject CreateHostedRoot(string name, Transform parent, float preferredHeight)
        {
            GameObject root = CreateUiObject(name, parent);
            LayoutElement layout = root.AddComponent<LayoutElement>();
            layout.minHeight = preferredHeight;
            layout.preferredHeight = preferredHeight;
            layout.flexibleWidth = 1f;
            return root;
        }

        public static TMP_Text AddText(
            Transform parent,
            string text,
            float size,
            FontStyles style = FontStyles.Normal,
            Color? color = null)
        {
            GameObject go = CreateUiObject("Text", parent);
            TextMeshProUGUI label = go.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = size;
            label.fontStyle = style;
            label.color = color ?? Color.white;
            label.textWrappingMode = TextWrappingModes.Normal;
            label.raycastTarget = false;
            return label;
        }

        public static GameObject CreateUiObject(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private static TextMeshProUGUI CreateText(Transform parent, string value, Color color)
        {
            GameObject textObject = CreateUiObject("Text", parent);
            TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();
            text.text = value;
            text.fontSize = 13f;
            text.color = color;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.raycastTarget = false;

            RectTransform rect = text.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return text;
        }
    }
}
