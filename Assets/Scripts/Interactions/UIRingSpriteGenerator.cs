using UnityEngine;
using UnityEngine.UI;

[ExecuteAlways]
[RequireComponent(typeof(Image))]
public class UIRingSpriteGenerator : MonoBehaviour
{
    [Header("Ring Dimensions")]
    [Tooltip("Thickness of the ring relative to its radius (0.12 to 0.18 matches the dashboard style)")]
    [Range(0.05f, 0.45f)]
    public float ringThickness = 0.15f;

    [Tooltip("Texture resolution (512 gives crisp edges in VR)")]
    public int resolution = 512;

    [Header("Rounded End Caps (Use on Ring_Fill only)")]
    [Tooltip("Adds rounded caps to the start and end of a Radial 360 filled ring")]
    public bool enableRoundedCaps = false;

    private Image ringImage;
    private RectTransform rectTransform;
    private Image startCapImage;
    private Image endCapImage;

    private static Sprite cachedRingSprite;
    private static Sprite cachedCapSprite;
    private static float cachedThickness = -1f;

    private void OnEnable()
    {
        ringImage = GetComponent<Image>();
        rectTransform = GetComponent<RectTransform>();
        ApplyRingSprite();
        SetupOrUpdateCaps();
    }

    private void OnValidate()
    {
        ringImage = GetComponent<Image>();
        rectTransform = GetComponent<RectTransform>();
        cachedRingSprite = null; // Force regenerate when thickness slider changes
        ApplyRingSprite();
        SetupOrUpdateCaps();
    }

    private void LateUpdate()
    {
        if (enableRoundedCaps)
        {
            UpdateCapTransforms();
        }
    }

    public void ApplyRingSprite()
    {
        if (ringImage == null) return;

        if (cachedRingSprite == null || !Mathf.Approximately(cachedThickness, ringThickness))
        {
            cachedRingSprite = GenerateHollowRingSprite(resolution, ringThickness);
            cachedThickness = ringThickness;
        }

        if (cachedCapSprite == null)
        {
            cachedCapSprite = GenerateSolidCircleSprite(128);
        }

        ringImage.sprite = cachedRingSprite;
    }

    private void SetupOrUpdateCaps()
    {
        Transform startTransform = transform.Find("Cap_Start");
        Transform endTransform = transform.Find("Cap_End");

        if (!enableRoundedCaps)
        {
            if (startTransform != null) startTransform.gameObject.SetActive(false);
            if (endTransform != null) endTransform.gameObject.SetActive(false);
            return;
        }

        if (startTransform == null)
        {
            GameObject startObj = new GameObject("Cap_Start", typeof(RectTransform), typeof(Image));
            startObj.transform.SetParent(transform, false);
            startTransform = startObj.transform;
        }

        if (endTransform == null)
        {
            GameObject endObj = new GameObject("Cap_End", typeof(RectTransform), typeof(Image));
            endObj.transform.SetParent(transform, false);
            endTransform = endObj.transform;
        }

        startTransform.gameObject.SetActive(true);
        endTransform.gameObject.SetActive(true);

        startCapImage = startTransform.GetComponent<Image>();
        endCapImage = endTransform.GetComponent<Image>();

        if (cachedCapSprite == null) cachedCapSprite = GenerateSolidCircleSprite(128);

        startCapImage.sprite = cachedCapSprite;
        endCapImage.sprite = cachedCapSprite;
        startCapImage.raycastTarget = false;
        endCapImage.raycastTarget = false;

        UpdateCapTransforms();
    }

    private void UpdateCapTransforms()
    {
        if (ringImage == null || rectTransform == null) return;
        if (startCapImage == null || endCapImage == null) return;

        float fill = ringImage.type == Image.Type.Filled ? ringImage.fillAmount : 1f;
        bool showCaps = fill > 0.005f && fill < 0.995f;

        startCapImage.enabled = showCaps;
        endCapImage.enabled = showCaps;

        if (!showCaps) return;

        startCapImage.color = ringImage.color;
        endCapImage.color = ringImage.color;

        float size = Mathf.Min(rectTransform.rect.width, rectTransform.rect.height);
        float outerRadius = size * 0.5f;
        float capDiameter = outerRadius * ringThickness;
        float midRadius = outerRadius - (capDiameter * 0.5f);

        RectTransform startRT = startCapImage.rectTransform;
        RectTransform endRT = endCapImage.rectTransform;

        startRT.sizeDelta = new Vector2(capDiameter, capDiameter);
        endRT.sizeDelta = new Vector2(capDiameter, capDiameter);

        // Calculate starting angle based on Unity Image Fill Origin (0=Bottom, 1=Right, 2=Top, 3=Left)
        float startAngleDeg = 90f; // Default Top
        if (ringImage.fillOrigin == (int)Image.Origin360.Bottom) startAngleDeg = -90f;
        else if (ringImage.fillOrigin == (int)Image.Origin360.Right) startAngleDeg = 0f;
        else if (ringImage.fillOrigin == (int)Image.Origin360.Left) startAngleDeg = 180f;

        float dir = ringImage.fillClockwise ? -1f : 1f;
        float endAngleDeg = startAngleDeg + (dir * fill * 360f);

        float startRad = startAngleDeg * Mathf.Deg2Rad;
        float endRad = endAngleDeg * Mathf.Deg2Rad;

        startRT.anchoredPosition = new Vector2(Mathf.Cos(startRad), Mathf.Sin(startRad)) * midRadius;
        endRT.anchoredPosition = new Vector2(Mathf.Cos(endRad), Mathf.Sin(endRad)) * midRadius;
    }

    private static Sprite GenerateHollowRingSprite(int size, float thickness)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;

        Color[] pixels = new Color[size * size];
        float center = (size - 1) * 0.5f;
        float outerR = size * 0.5f - 2f;
        float innerR = outerR * (1f - thickness);
        float aaWidth = 1.5f; // Smooth anti-aliased edge

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - center;
                float dy = y - center;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);

                float outerAlpha = Mathf.Clamp01((outerR - dist) / aaWidth);
                float innerAlpha = Mathf.Clamp01((dist - innerR) / aaWidth);
                float alpha = outerAlpha * innerAlpha;

                pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
            }
        }

        tex.SetPixels(pixels);
        tex.Apply();

        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
    }

    private static Sprite GenerateSolidCircleSprite(int size)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;

        Color[] pixels = new Color[size * size];
        float center = (size - 1) * 0.5f;
        float radius = size * 0.5f - 1.5f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dist = Vector2.Distance(new Vector2(x, y), new Vector2(center, center));
                float alpha = Mathf.Clamp01((radius - dist) / 1.5f);
                pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
            }
        }

        tex.SetPixels(pixels);
        tex.Apply();

        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
    }
}