using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace IZCode.Mod.UI
{
    /// <summary>
    /// Builds simple text panels at runtime.
    ///
    /// The mod ships no asset bundle, so there is no prefab to instantiate: the widgets
    /// are built from uGUI primitives. The font and its size are borrowed from a
    /// TextMeshPro that already exists in the editor - that way the overlay follows the
    /// game UI's scale instead of pinning a size that only looks right at one
    /// resolution.
    ///
    /// Positioning: everything comes in as screen coordinates (that is what we have
    /// from the mouse and the lines), and the offsets are applied in canvas units -
    /// mixing the two spaces is what made the panels "slide" when the UI scale was
    /// not 1.
    /// </summary>
    internal static class OverlayWidgets
    {
        /// <summary>Panel border thickness, in canvas units.</summary>
        private const float BorderThickness = 1f;

        public sealed class Panel
        {
            public GameObject Root { get; }
            public RectTransform Rect { get; }
            public Image Border { get; }
            public Image Fill { get; }
            public TextMeshProUGUI Text { get; }

            public Panel(GameObject root, RectTransform rect, Image border, Image fill, TextMeshProUGUI text)
            {
                Root = root;
                Rect = rect;
                Border = border;
                Fill = fill;
                Text = text;
            }

            public bool Visible
            {
                get => Root.activeSelf;
                set { if (Root.activeSelf != value) Root.SetActive(value); }
            }

            /// <summary>Resizes to fit the text and brings the panel to the front.</summary>
            public void FitTo(Vector2 padding, float maxWidth)
            {
                Text.ForceMeshUpdate();
                Vector2 size = Text.GetPreferredValues(maxWidth, 0f);
                float width = Mathf.Min(size.x, maxWidth) + padding.x * 2f;
                float height = size.y + padding.y * 2f;
                Rect.sizeDelta = new Vector2(width, height);
                Rect.SetAsLastSibling();
            }
        }

        /// <summary>A flat rectangle - used to underline the line with an error.</summary>
        public sealed class Strip
        {
            public GameObject Root { get; }
            public RectTransform Rect { get; }
            public Image Image { get; }

            public Strip(GameObject root, RectTransform rect, Image image)
            {
                Root = root;
                Rect = rect;
                Image = image;
            }

            public bool Visible
            {
                get => Root.activeSelf;
                set { if (Root.activeSelf != value) Root.SetActive(value); }
            }
        }

        public static Panel CreatePanel(Transform parent, string name, TMP_FontAsset? font,
                                        float fontSize, Color background, Color foreground,
                                        Color border, Vector2 padding)
        {
            var root = new GameObject(name, typeof(RectTransform), typeof(CanvasGroup), typeof(Image));
            root.transform.SetParent(parent, worldPositionStays: false);

            var rect = (RectTransform)root.transform;
            // Anchored to the bottom-left corner: positioning is done in screen
            // coordinates, which is what we have from the mouse and the caret.
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = new Vector2(0f, 1f);

            // The panel is drawn in two layers: the outer one is the border, the inner
            // one (inset by 1px) is the background. It is cheaper than a 9-sliced
            // sprite, and the mod loads no texture at all.
            var borderImage = root.GetComponent<Image>();
            borderImage.color = border;
            borderImage.raycastTarget = false;

            var fillObject = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fillObject.transform.SetParent(root.transform, worldPositionStays: false);

            var fillRect = (RectTransform)fillObject.transform;
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = new Vector2(BorderThickness, BorderThickness);
            fillRect.offsetMax = new Vector2(-BorderThickness, -BorderThickness);

            var fill = fillObject.GetComponent<Image>();
            fill.color = background;
            fill.raycastTarget = false;

            // The panel must never steal a click from the editor.
            var group = root.GetComponent<CanvasGroup>();
            group.blocksRaycasts = false;
            group.interactable = false;

            var textObject = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            textObject.transform.SetParent(root.transform, worldPositionStays: false);

            var textRect = (RectTransform)textObject.transform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(padding.x, padding.y);
            textRect.offsetMax = new Vector2(-padding.x, -padding.y);

            var text = textObject.GetComponent<TextMeshProUGUI>();
            if (font != null) text.font = font;
            text.fontSize = fontSize;
            text.color = foreground;
            text.richText = true;
            text.raycastTarget = false;
            text.alignment = TextAlignmentOptions.TopLeft;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Overflow;

            root.SetActive(false);
            return new Panel(root, rect, borderImage, fill, text);
        }

        public static Strip CreateStrip(Transform parent, string name, Color color)
        {
            var root = new GameObject(name, typeof(RectTransform), typeof(CanvasGroup), typeof(Image));
            root.transform.SetParent(parent, worldPositionStays: false);

            var rect = (RectTransform)root.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = new Vector2(0f, 0f);

            var image = root.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;

            var group = root.GetComponent<CanvasGroup>();
            group.blocksRaycasts = false;
            group.interactable = false;

            root.SetActive(false);
            return new Strip(root, rect, image);
        }

        /// <summary>
        /// Stretches the strip between two screen points (bottom-left and top-right
        /// corners), with a maximum height.
        /// </summary>
        public static void StretchTo(Strip strip, Canvas canvas, Vector2 bottomLeftScreen,
                                     Vector2 topRightScreen, float height)
        {
            if (!TryToCanvas(canvas, bottomLeftScreen, out Vector2 bottomLeft)) return;
            if (!TryToCanvas(canvas, topRightScreen, out Vector2 topRight)) return;

            float width = Mathf.Max(0f, topRight.x - bottomLeft.x);
            strip.Rect.sizeDelta = new Vector2(width, height);
            strip.Rect.anchoredPosition = bottomLeft;
            strip.Rect.SetAsLastSibling();
        }

        /// <summary>
        /// Repositions the panel near a screen point, pushing it inwards when it would
        /// go past the canvas edge.
        /// </summary>
        public static void PlaceNear(Panel panel, Canvas canvas, Vector2 screenPoint, Vector2 canvasOffset)
        {
            if (!TryToCanvas(canvas, screenPoint, out Vector2 anchored)) return;
            Place(panel, canvas, anchored + canvasOffset);
        }

        /// <summary>
        /// Opens the panel downwards from <paramref name="belowScreen"/>; when it does
        /// not fit there, it opens upwards from <paramref name="aboveScreen"/>.
        ///
        /// That is the case for the suggestion list on a line near the editor's bottom:
        /// pushing the panel back inside the screen would make it cover exactly the line
        /// being typed.
        /// </summary>
        public static void PlaceBelowOrAbove(Panel panel, Canvas canvas, Vector2 belowScreen,
                                             Vector2 aboveScreen, Vector2 canvasOffset)
        {
            if (!TryToCanvas(canvas, belowScreen, out Vector2 below)) return;

            Vector2 anchored = below + canvasOffset;
            float height = panel.Rect.sizeDelta.y;

            if (anchored.y - height < 0f && TryToCanvas(canvas, aboveScreen, out Vector2 above))
            {
                // Pivot at the top: for the panel to end at 'above.y', its top sits
                // 'height' above it.
                anchored = new Vector2(anchored.x, above.y + height - canvasOffset.y);
            }

            Place(panel, canvas, anchored);
        }

        /// <summary>Pins the panel at a position already in canvas units, without leaving the screen.</summary>
        private static void Place(Panel panel, Canvas canvas, Vector2 anchored)
        {
            var canvasRect = canvas.transform as RectTransform;
            if (canvasRect == null) return;

            Vector2 canvasSize = canvasRect.rect.size;
            Vector2 size = panel.Rect.sizeDelta;

            anchored.x = Mathf.Clamp(anchored.x, 0f, Mathf.Max(0f, canvasSize.x - size.x));
            anchored.y = Mathf.Clamp(anchored.y, Mathf.Min(size.y, canvasSize.y), canvasSize.y);

            panel.Rect.anchoredPosition = anchored;
            panel.Rect.SetAsLastSibling();
        }

        /// <summary>
        /// Converts a screen point to the coordinate anchored at the canvas's bottom-left
        /// corner - the anchor used by every widget in here.
        /// </summary>
        private static bool TryToCanvas(Canvas canvas, Vector2 screenPoint, out Vector2 anchored)
        {
            anchored = Vector2.zero;

            var canvasRect = canvas.transform as RectTransform;
            if (canvasRect == null) return false;

            Camera? camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null
                : canvas.worldCamera;

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    canvasRect, screenPoint, camera, out Vector2 local))
                return false;

            // ScreenPointToLocalPointInRectangle returns coordinates around the canvas
            // pivot; we convert them to the bottom-left corner.
            anchored = local + canvasRect.rect.size * canvasRect.pivot;
            return true;
        }

        /// <summary>
        /// Borrows the font from any TextMeshPro in the editor, so the panels do not come
        /// out with Unity's default font.
        /// </summary>
        public static TMP_FontAsset? BorrowFont(Component? source)
        {
            var text = FindText(source);
            return text != null ? text.font : null;
        }

        /// <summary>
        /// Borrows the editor's font size.
        ///
        /// Without it the overlay would pin a size in points, and the list would come out
        /// tiny next to the code at any high resolution or UI scale other than 1. With
        /// auto-size on, <c>fontSize</c> is already the effective size TMP computed.
        /// </summary>
        public static float BorrowFontSize(Component? source, float fallback)
        {
            var text = FindText(source);
            if (text == null) return fallback;

            float size = text.fontSize;
            return size > 1f ? size : fallback;
        }

        private static TMP_Text? FindText(Component? source)
        {
            if (source == null) return null;
            return source.GetComponentInChildren<TMP_Text>(includeInactive: true);
        }
    }
}
