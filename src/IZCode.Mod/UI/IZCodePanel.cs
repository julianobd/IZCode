using System;
using System.Text;
using Assets.Scripts.UI;
using IZCode.Mod.Diagnostics;
using IZLang.Editor;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace IZCode.Mod.UI
{
    /// <summary>
    /// The IZ mode code area: a single, dark text field with multi-line selection and
    /// automatic indentation.
    ///
    /// <para><b>Why replace the game's editor.</b> The Stationeers editor is not a text
    /// field: it is 128 independent <see cref="TMP_InputField"/>s, one per line. That is
    /// what prevents dragging the mouse across three lines, pressing Ctrl+A over the
    /// whole program or cutting a block - each field only knows about its own line, and
    /// no amount of code on top changes that. With a single field in multi-line mode,
    /// all of that becomes TextMeshPro's native behaviour: dragging, Shift+arrows,
    /// double click, Ctrl+A/C/X/V, PageUp/PageDown and mouse wheel scrolling.</para>
    ///
    /// <para><b>How the code gets colored.</b> The same technique the game itself uses
    /// per line: the field's text is transparent and a second TextMeshPro, its child,
    /// draws the version with the color tags. Both use the same font, the same size and
    /// the same rectangle, and the tags take no width - so the glyphs line up exactly
    /// and the caret always lands on the right column.</para>
    ///
    /// <para><b>What the game still sees.</b> The original 128 lines do not go away:
    /// they become invisible and non-interactive, and get the panel's text back on
    /// every frame it changes. That way <c>InputSourceCode.Copy()</c>, the save button,
    /// the byte count and the chip keep working exactly as before - and, if this panel
    /// fails at any point, the original editor is still there, whole.</para>
    /// </summary>
    internal sealed class IZCodePanel : MonoBehaviour
    {
        // ==================================================================
        //  Appearance
        // ==================================================================
        //  The colors are VS Code's Dark+ theme, which is what the request describes
        //  and what most people recognize at once.

        private static readonly Color Background = new Color32(0x1E, 0x1E, 0x1E, 0xFF);
        private static readonly Color GutterBackground = new Color32(0x1E, 0x1E, 0x1E, 0xFF);
        private static readonly Color GutterForeground = new Color32(0x85, 0x85, 0x85, 0xFF);
        private static readonly Color CurrentLine = new Color32(0x28, 0x2C, 0x34, 0xFF);
        private static readonly Color Selection = new Color32(0x26, 0x4F, 0x78, 0xFF);
        private static readonly Color Caret = new Color32(0xAE, 0xAF, 0xAD, 0xFF);

        /// <summary>Width of the line-number gutter, in canvas units.</summary>
        private const float GutterWidth = 42f;

        /// <summary>Breathing room between the code and the edge of the area.</summary>
        private const float TextPadding = 6f;

        private const float FallbackFontSize = 14f;

        // ==================================================================
        //  Game limits
        // ==================================================================
        //  The same ones InputSourceCode has: the source ends up stored in the original
        //  128 fields, so going past them here would only postpone losing text until
        //  save time.

        private const int MaxLines = 1280;
        private const int MaxLineLength = 90;
        private const int MaxFileSize = 131072;

        // ==================================================================
        //  State
        // ==================================================================

        public static IZCodePanel? Instance { get; private set; }

        /// <summary>true when the panel is built, on and ready to respond.</summary>
        public static bool IsActive =>
            Instance != null && Instance._built && Instance._on && Instance._input != null;

        private TMP_InputField? _input;
        private TextMeshProUGUI? _text;        // the field's text, transparent
        private TextMeshProUGUI? _code;        // the colored copy, on top
        private TextMeshProUGUI? _numbers;     // the line-number gutter
        private RectTransform? _numbersRect;
        private RectTransform? _viewport;
        private Image? _currentLine;
        private CanvasGroup? _gameLines;       // the original 128 lines, hidden

        private bool _built;
        private bool _on;

        /// <summary>The text changed and has not been repainted or handed back to the game yet.</summary>
        private bool _dirty;

        private string _paintedText = "\0";   // impossible: forces the first paint
        private int _paintedLines = -1;

        /// <summary>The player typed '}' on a whitespace-only line; the outdent is pending.</summary>
        private bool _pendingCloseBrace;

        /// <summary>Esc took focus away from the field and we are going to give it back.</summary>
        private bool _refocus;

        // ==================================================================
        //  Switching on and off
        // ==================================================================

        /// <summary>
        /// Switches the panel on or off. Called every frame by the overlay, which
        /// already knows whether the buffer is IZ.
        /// </summary>
        public static void SetEnabled(bool on)
        {
            if (on)
            {
                var panel = Ensure();
                if (panel != null) panel.TurnOn();
                return;
            }

            if (Instance != null) Instance.TurnOff();
        }

        /// <summary>
        /// The build already failed once in this session.
        ///
        /// Retrying every frame would only fill the log: whatever was missing (a
        /// rectangle, a font) does not appear on its own. The original editor keeps
        /// working, which is what matters.
        /// </summary>
        private static bool _broken;

        /// <summary>Builds the panel if it does not exist yet. Returns null when it cannot.</summary>
        private static IZCodePanel? Ensure()
        {
            if (Instance != null && Instance._built) return Instance;
            if (_broken) return null;

            var editor = InputSourceCode.Instance;
            if (editor == null || editor.LinesOfCode == null || editor.LinesOfCode.Count == 0)
                return null;

            var host = ResolveHost(editor);
            if (host == null)
            {
                _broken = true;
                IZLog.Warn(IZLogArea.Editor, "IZ panel not built: could not find where to fit the code area");
                return null;
            }

            try
            {
                var root = new GameObject("~IZCodeSurface", typeof(RectTransform));
                var rect = (RectTransform)root.transform;
                rect.SetParent(host, worldPositionStays: false);
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
                rect.localScale = Vector3.one;
                rect.SetAsLastSibling();

                // It starts disabled: TMP_InputField creates the caret object in
                // OnEnable, and it needs textComponent and textViewport already filled
                // in - so we only enable it after everything is built.
                root.SetActive(false);

                var panel = root.AddComponent<IZCodePanel>();
                panel.Build(editor, rect);

                Instance = panel;
                IZLog.Info(IZLogArea.Editor, "IZ panel built in '" + host.name + "'");
                return panel;
            }
            catch (Exception ex)
            {
                _broken = true;
                IZLog.Exception(IZLogArea.Editor,
                    "IZ panel not built; the original editor stays in place", ex);
                return null;
            }
        }

        /// <summary>
        /// Where to fit the panel: the rectangle that clips the list of lines.
        ///
        /// It is the same clip the overlay already uses to avoid drawing errors outside
        /// the visible area. Fitting into <c>LineParent</c> does not work: it has the
        /// vertical layout that stacks the 128 lines, and the panel would become one
        /// more line in the stack.
        /// </summary>
        private static RectTransform? ResolveHost(InputSourceCode editor)
        {
            var first = editor.LinesOfCode[0];
            if (first == null) return null;

            for (var t = first.transform.parent; t != null; t = t.parent)
            {
                // The whole window will not do, even though it clips too: the panel
                // would cover the save and cancel buttons.
                if (t == editor.transform) break;

                bool masks = t.GetComponent<RectMask2D>() != null || t.GetComponent<Mask>() != null;
                if (masks && t is RectTransform masked) return masked;
            }

            // No mask: the direct parent of the line stack is the safe guess - it is the
            // area the game reserved for the code.
            return editor.LineParent != null ? editor.LineParent.parent as RectTransform : null;
        }

        private void TurnOn()
        {
            if (!_built || _on) return;

            _on = true;
            gameObject.SetActive(true);      // TMP_InputField only builds the caret here
            LoadFromGameLines();
            HideGameLines(true);

            _input?.ActivateInputField();

            IZLog.Info(IZLogArea.Editor, "IZ code area switched on");
        }

        private void TurnOff()
        {
            if (!_built || !_on) return;

            _on = false;

            int caret = CaretOffset;
            string source = Text;

            FlushToGameLines(force: true);
            gameObject.SetActive(false);
            HideGameLines(false);
            RestoreGameCaret(source, caret);

            IZLog.Info(IZLogArea.Editor, "IZ code area switched off");
        }

        /// <summary>
        /// Hands the caret back to the equivalent line of the game's editor.
        ///
        /// Leaving IZ mode happens mid-typing - by deleting the <c>#iz</c>, usually.
        /// Without this the player would be left with no caret at all and would have to
        /// click back on the line to carry on.
        /// </summary>
        private static void RestoreGameCaret(string source, int caret)
        {
            var editor = InputSourceCode.Instance;
            if (editor == null || editor.LinesOfCode == null || editor.LinesOfCode.Count == 0) return;

            int line = 0;
            int lineStart = 0;
            for (int i = 0; i < caret && i < source.Length; i++)
                if (source[i] == '\n') { line++; lineStart = i + 1; }

            if (line >= editor.LinesOfCode.Count) return;

            var target = editor.LinesOfCode[line];
            if (target == null || target.InputField == null) return;

            try
            {
                EditorLineOfCode.CurrentLine = target;
                target.Activate();

                int column = Mathf.Clamp(caret - lineStart, 0, (target.Text ?? string.Empty).Length);
                target.InputField.caretPosition = column;
                target.InputField.selectionAnchorPosition = column;
                target.InputField.selectionFocusPosition = column;
            }
            catch (Exception ex)
            {
                IZLog.Debug(IZLogArea.Editor, "caret not handed back to the game editor: " + ex.Message);
            }
        }

        private void OnDestroy()
        {
            HideGameLines(false);
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// Hides (or brings back) the game's 128 lines.
        ///
        /// They keep existing and keep holding the text - that is how <c>Copy()</c>, the
        /// save button and the byte count keep working. They just stop showing up and
        /// stop accepting clicks, so they do not fight the panel for the keyboard.
        /// </summary>
        private void HideGameLines(bool hidden)
        {
            var editor = InputSourceCode.Instance;
            if (editor == null || editor.LineParent == null) return;

            try
            {
                if (_gameLines == null)
                    _gameLines = editor.LineParent.GetComponent<CanvasGroup>()
                              ?? editor.LineParent.gameObject.AddComponent<CanvasGroup>();

                _gameLines.alpha = hidden ? 0f : 1f;
                _gameLines.interactable = !hidden;
                _gameLines.blocksRaycasts = !hidden;
            }
            catch (Exception ex)
            {
                IZLog.Warn(IZLogArea.Editor, "could not hide the game lines: " + ex.Message);
            }
        }

        // ==================================================================
        //  Building
        // ==================================================================

        private void Build(InputSourceCode editor, RectTransform root)
        {
            var source = editor.LinesOfCode[0].InputText;
            TMP_FontAsset? font = source != null ? source.font : null;
            float fontSize = source != null && source.fontSize > 1f ? source.fontSize : FallbackFontSize;

            var background = root.gameObject.AddComponent<Image>();
            background.color = Background;
            background.raycastTarget = true;      // this is what lets the click reach the field

            // ---- line-number gutter ----
            var gutter = NewRect("Gutter", root);
            gutter.anchorMin = new Vector2(0f, 0f);
            gutter.anchorMax = new Vector2(0f, 1f);
            gutter.pivot = new Vector2(0f, 1f);
            gutter.sizeDelta = new Vector2(GutterWidth, 0f);
            gutter.anchoredPosition = Vector2.zero;
            gutter.gameObject.AddComponent<RectMask2D>();

            var gutterFill = gutter.gameObject.AddComponent<Image>();
            gutterFill.color = GutterBackground;
            gutterFill.raycastTarget = false;

            _numbers = NewText("Numbers", gutter, font, fontSize);
            _numbersRect = _numbers.rectTransform;
            _numbers.color = GutterForeground;
            _numbers.alignment = TextAlignmentOptions.TopRight;
            _numbers.margin = new Vector4(0f, TextPadding, TextPadding, 0f);

            // ---- scrollable code area ----
            _viewport = NewRect("Viewport", root);
            _viewport.anchorMin = Vector2.zero;
            _viewport.anchorMax = Vector2.one;
            _viewport.offsetMin = new Vector2(GutterWidth, 0f);
            _viewport.offsetMax = Vector2.zero;
            _viewport.gameObject.AddComponent<RectMask2D>();

            // The current line strip. It is off when there is a selection, which
            // already has its own color, and UpdateCurrentLine keeps it below the
            // caret - which TMP only creates later, when the field is activated.
            var highlight = NewRect("CurrentLine", _viewport);
            highlight.anchorMin = new Vector2(0f, 1f);
            highlight.anchorMax = new Vector2(1f, 1f);
            highlight.pivot = new Vector2(0f, 1f);
            _currentLine = highlight.gameObject.AddComponent<Image>();
            _currentLine.color = CurrentLine;
            _currentLine.raycastTarget = false;
            highlight.gameObject.SetActive(false);

            _text = NewText("Text", _viewport, font, fontSize);
            _text.margin = new Vector4(TextPadding, TextPadding, TextPadding, TextPadding);
            // Transparent on purpose: what shows up is the colored copy just below.
            // This is still the text that defines the geometry, the caret and the
            // selection - erasing it will not do, it has to be invisible.
            _text.color = new Color(1f, 1f, 1f, 0f);
            _text.richText = false;

            _code = NewText("Code", _text.rectTransform, font, fontSize);
            _code.margin = _text.margin;
            _code.richText = true;

            // ---- the field ----
            _input = root.gameObject.AddComponent<TMP_InputField>();
            _input.textComponent = _text;
            _input.textViewport = _viewport;
            _input.transition = Selectable.Transition.None;
            _input.lineType = TMP_InputField.LineType.MultiLineNewline;
            _input.lineLimit = 0;
            _input.characterLimit = MaxFileSize;
            _input.richText = false;
            _input.isRichTextEditingAllowed = false;
            _input.onFocusSelectAll = false;
            _input.resetOnDeActivation = false;
            _input.restoreOriginalTextOnEscape = false;
            _input.customCaretColor = true;
            _input.caretColor = Caret;
            _input.caretWidth = 2;
            _input.caretBlinkRate = 0.7f;
            _input.selectionColor = Selection;
            _input.scrollSensitivity = 3f;
            _input.onValidateInput = Validate;
            _input.onValueChanged.AddListener(OnTextChanged);
            _input.onEndEdit.AddListener(OnEndEdit);

            // lineType turns on word wrapping; code does not wrap, it scrolls sideways
            // - otherwise a long line would become two and the line-number gutter would
            // stop matching the code.
            _text.enableWordWrapping = false;
            _text.overflowMode = TextOverflowModes.Overflow;
            _code.enableWordWrapping = false;
            _code.overflowMode = TextOverflowModes.Overflow;
            _numbers.enableWordWrapping = false;
            _numbers.overflowMode = TextOverflowModes.Overflow;

            _built = true;
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, worldPositionStays: false);
            rect.localScale = Vector3.one;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return rect;
        }

        private static TextMeshProUGUI NewText(string name, Transform parent,
                                               TMP_FontAsset? font, float fontSize)
        {
            var rect = NewRect(name, parent);
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();

            if (font != null) text.font = font;
            text.enableAutoSizing = false;
            text.fontSize = fontSize;
            text.alignment = TextAlignmentOptions.TopLeft;
            text.raycastTarget = false;
            text.parseCtrlCharacters = false;
            text.text = string.Empty;
            return text;
        }

        // ==================================================================
        //  Text
        // ==================================================================

        public string Text => _input != null ? _input.text ?? string.Empty : string.Empty;

        /// <summary>Absolute caret offset in the source.</summary>
        public int CaretOffset => _input != null ? Clamp(_input.stringPosition) : 0;

        public int SelectionStart => _input != null ? Clamp(_input.selectionStringAnchorPosition) : 0;
        public int SelectionEnd => _input != null ? Clamp(_input.selectionStringFocusPosition) : 0;

        public bool HasSelection => SelectionStart != SelectionEnd;

        private int Clamp(int offset) => Mathf.Clamp(offset, 0, Text.Length);

        /// <summary>Zero-based index of the line the caret is on.</summary>
        public int CaretLine
        {
            get
            {
                string text = Text;
                int caret = CaretOffset;
                int line = 0;
                for (int i = 0; i < caret && i < text.Length; i++)
                    if (text[i] == '\n') line++;
                return line;
            }
        }

        /// <summary>Replaces a range of the source and repositions the caret. Used by completion.</summary>
        public void Replace(int start, int length, string replacement, int caret)
        {
            var edit = new TextEdit(start, length, replacement, caret, caret);
            ApplyEdit(edit);
        }

        private void ApplyEdit(TextEdit edit)
        {
            if (_input == null || edit.IsEmpty) return;

            string updated = edit.Apply(Text);
            if (updated.Length > MaxFileSize)
            {
                IZLog.Warn(IZLogArea.Editor, "edit refused: it would go past " + MaxFileSize + " bytes");
                return;
            }

            _input.text = updated;
            _input.selectionStringAnchorPosition = Mathf.Clamp(edit.SelectionStart, 0, updated.Length);
            _input.selectionStringFocusPosition = Mathf.Clamp(edit.SelectionEnd, 0, updated.Length);
            _input.ActivateInputField();
            _dirty = true;
        }

        private void OnTextChanged(string value) => _dirty = true;

        /// <summary>
        /// Esc takes focus away from the field (that is TextMeshPro, there is no way to
        /// stop it). When it only served to close the suggestion list, we give focus
        /// back on the next frame - otherwise the player would have to click back on the
        /// code after every Esc.
        /// </summary>
        private void OnEndEdit(string value)
        {
            if (_on && _input != null && _input.wasCanceled) _refocus = true;
        }

        // ==================================================================
        //  Bridge to the game's 128 lines
        // ==================================================================

        private void LoadFromGameLines()
        {
            var editor = InputSourceCode.Instance;
            if (_input == null || editor == null || editor.LinesOfCode == null) return;

            var lines = editor.LinesOfCode;
            var texts = new string?[lines.Count];
            for (int i = 0; i < lines.Count; i++) texts[i] = lines[i]?.Text;

            string source = LineOffsets.Join(texts);

            // The caret comes from where it was in the game editor: whoever just typed
            // '#iz' keeps the caret at the end of '#iz', not thrown to the start.
            int caret = 0;
            var current = EditorLineOfCode.CurrentLine;
            if (current != null)
            {
                int index = lines.IndexOf(current);
                if (index >= 0)
                {
                    int column = 0;
                    try { column = Mathf.Max(0, current.InputField.caretPosition); }
                    catch { /* unfocused field: the start of the line will do */ }
                    caret = LineOffsets.ToOffset(texts, index, column);
                }
            }

            _input.SetTextWithoutNotify(source);
            _input.stringPosition = Mathf.Clamp(caret, 0, source.Length);
            _dirty = true;
        }

        /// <summary>
        /// Hands the panel's text back to the game's 128 lines.
        ///
        /// It only writes to the lines that changed: writing to all 128 rebuilds 128
        /// text meshes, and doing that on every keystroke would be felt.
        /// <c>SetTextWithoutNotify</c> because the game's highlighter does not need to
        /// run on an invisible line.
        /// </summary>
        private void FlushToGameLines(bool force)
        {
            var editor = InputSourceCode.Instance;
            if (editor == null || editor.LinesOfCode == null) return;

            string[] source = Text.Split('\n');
            var lines = editor.LinesOfCode;
            bool changed = false;

            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line == null || line.InputField == null) continue;

                string wanted = i < source.Length ? source[i] : string.Empty;
                if (wanted.Length > MaxLineLength) wanted = wanted.Substring(0, MaxLineLength);

                if (string.Equals(line.InputField.text, wanted, StringComparison.Ordinal)) continue;

                line.InputField.SetTextWithoutNotify(wanted);
                changed = true;
            }

            if (source.Length > lines.Count)
                IZLog.Throttled(IZLogArea.Editor, IZLogLevel.Warn, "too-many-lines", 10f,
                    () => "the source has " + source.Length + " lines and the chip stores " +
                          lines.Count + "; the surplus will not be saved");

            if (changed || force)
            {
                try { editor.UpdateFileSize(); }
                catch (Exception ex)
                {
                    IZLog.Throttled(IZLogArea.Editor, IZLogLevel.Warn, "file-size", 10f,
                        () => "could not update the byte count: " + ex.Message);
                }
            }
        }

        // ==================================================================
        //  Keys
        // ==================================================================
        //  Everything in LateUpdate, after the overlay: when the suggestion list is
        //  open the Tab belongs to it, and only here can we know whether it took it.

        private void LateUpdate()
        {
            if (!_on || _input == null) return;

            try
            {
                HandleKeys();
                Refresh();
            }
            catch (Exception ex)
            {
                IZLog.Throttled(IZLogArea.Editor, IZLogLevel.Error, "panel-update", 5f,
                    () => "IZ panel failed on this frame: " + ex);
            }
        }

        private void HandleKeys()
        {
            if (_input == null) return;

            if (_refocus)
            {
                _refocus = false;
                if (!_input.isFocused) _input.ActivateInputField();
            }

            if (_pendingCloseBrace)
            {
                _pendingCloseBrace = false;
                int caret = CaretOffset;
                var edit = IndentEngine.CloseBrace(Text, caret, caret);
                ApplyEdit(edit.IsEmpty ? new TextEdit(caret, 0, "}", caret + 1, caret + 1) : edit);
            }

            if (!_input.isFocused) return;

            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                HandleEnter();

            if (Input.GetKeyDown(KeyCode.Tab) && !IZEditorOverlay.ConsumedTabThisFrame())
                ApplyEdit(IndentEngine.Indent(Text, SelectionStart, SelectionEnd, IsShiftHeld()));
        }

        private void HandleEnter()
        {
            string text = Text;
            if (CountLines(text) >= MaxLines)
            {
                IZLog.Throttled(IZLogArea.Editor, IZLogLevel.Info, "line-limit", 5f,
                    () => "the chip stores " + MaxLines + " lines; no room to open another one");
                return;
            }

            ApplyEdit(IndentEngine.NewLine(text, SelectionStart, SelectionEnd));
        }

        private static bool IsShiftHeld() =>
            Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        /// <summary>
        /// Filters what enters the field.
        ///
        /// Three keys are rejected here so they can be handled in full in
        /// <see cref="HandleKeys"/>, where a multi-character edit is possible: Enter
        /// (which inherits the indentation), Tab (which shifts the block) and
        /// <c>}</c> (which outdents the line). The test is <c>Input.GetKey</c> and not a
        /// flag of our own because the same path serves pasting: a '\n' arriving from
        /// Ctrl+V has no Enter key held down and goes straight through.
        /// </summary>
        private char Validate(string text, int index, char added)
        {
            try
            {
                if (added == '\v') return '\0';        // Shift+Enter, which TMP invents
                if (added == '\r') return '\0';        // Windows line ending: the '\n' is enough

                if (added == '\t')
                    return Input.GetKey(KeyCode.Tab) ? '\0' : ' ';

                if (added == '\n')
                {
                    if (Input.GetKey(KeyCode.Return) || Input.GetKey(KeyCode.KeypadEnter)) return '\0';
                    return CountLines(text) >= MaxLines ? '\0' : added;
                }

                if (added == '}' && !HasSelection)
                {
                    var edit = IndentEngine.CloseBrace(text, index, index);
                    if (!edit.IsEmpty)
                    {
                        _pendingCloseBrace = true;
                        return '\0';
                    }
                }

                int start = IndentEngine.LineStart(text, index);
                int end = IndentEngine.LineEnd(text, index);
                return end - start >= MaxLineLength ? '\0' : added;
            }
            catch (Exception ex)
            {
                IZLog.Throttled(IZLogArea.Editor, IZLogLevel.Warn, "validate", 10f,
                    () => "input filter failed, letting the character through: " + ex.Message);
                return added;
            }
        }

        private static int CountLines(string text)
        {
            int count = 1;
            for (int i = 0; i < text.Length; i++) if (text[i] == '\n') count++;
            return count;
        }

        // ==================================================================
        //  Drawing
        // ==================================================================

        private void Refresh()
        {
            if (_input == null || _code == null) return;

            if (_dirty)
            {
                _dirty = false;
                Repaint();
                FlushToGameLines(force: false);
            }

            // The gutter cannot be a child of the text: the text also moves
            // horizontally when a line is long, and the numbers would slide out of
            // view. It follows the vertical offset only.
            if (_numbersRect != null && _text != null)
                _numbersRect.anchoredPosition = new Vector2(0f, _text.rectTransform.anchoredPosition.y);

            UpdateCurrentLine();
        }

        /// <summary>Repaints the colored code and the line numbers.</summary>
        public void Repaint()
        {
            if (_code == null) return;

            string text = Text;
            if (string.Equals(text, _paintedText, StringComparison.Ordinal)) return;

            _paintedText = text;
            _code.text = SyntaxHighlighter.Highlight(text);

            int lines = CountLines(text);
            if (lines != _paintedLines && _numbers != null)
            {
                _paintedLines = lines;
                _numbers.text = BuildLineNumbers(lines);
            }
        }

        /// <summary>
        /// Numbering starts at zero, like the game editor's - the same one the error
        /// panel uses when it says "error line 12".
        /// </summary>
        private static string BuildLineNumbers(int count)
        {
            var sb = new StringBuilder(count * 4);
            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(i);
            }
            return sb.ToString();
        }

        private void UpdateCurrentLine()
        {
            if (_currentLine == null || _text == null) return;

            // TMP only creates the caret object when the field is activated, and drops
            // it as the viewport's first sibling - which pushes this strip on top of
            // it. Since the strip is opaque, the caret and the selection used to vanish
            // on exactly the line being typed, and only came back on losing focus, when
            // the strip switches off. Going back to index 0 costs one comparison per
            // frame, and the actual move happens exactly once.
            if (_currentLine.transform.GetSiblingIndex() != 0)
                _currentLine.transform.SetAsFirstSibling();

            // With a selection the strip gets in the way: the selection color already says where you are.
            if (HasSelection || _input == null || !_input.isFocused)
            {
                if (_currentLine.gameObject.activeSelf) _currentLine.gameObject.SetActive(false);
                return;
            }

            if (_viewport == null || !TryGetLineBounds(CaretLine, out float top, out float bottom))
            {
                if (_currentLine.gameObject.activeSelf) _currentLine.gameObject.SetActive(false);
                return;
            }

            // 'top' comes from the text's local space, whose origin is the centre of the
            // rectangle; the strip is anchored to the top of the viewport. Half the
            // height is exactly what separates the two origins - without it the strip
            // would sit half a screen below the line.
            float y = _text.rectTransform.anchoredPosition.y + top - _viewport.rect.height * 0.5f;

            var rect = _currentLine.rectTransform;
            rect.anchoredPosition = new Vector2(0f, y);
            rect.sizeDelta = new Vector2(0f, Mathf.Max(1f, top - bottom));

            if (!_currentLine.gameObject.activeSelf) _currentLine.gameObject.SetActive(true);
        }

        // ==================================================================
        //  Geometry, for the overlay
        // ==================================================================

        /// <summary>Screen rectangle of the code area, used to clip what the overlay draws.</summary>
        public bool TryGetAreaScreenRect(Camera? camera, out Vector2 min, out Vector2 max)
        {
            min = max = Vector2.zero;
            if (_viewport == null) return false;

            var corners = new Vector3[4];
            _viewport.GetWorldCorners(corners);

            Vector2 a = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            Vector2 b = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);

            min = new Vector2(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y));
            max = new Vector2(Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
            return true;
        }

        /// <summary>Bottom and top of the caret in screen coordinates - the suggestion list's anchor.</summary>
        public bool TryGetCaretScreenSpan(Camera? camera, out Vector2 bottom, out Vector2 top)
        {
            bottom = top = Vector2.zero;
            if (_text == null) return false;

            try
            {
                var info = EnsureGeometry();
                if (info == null) return false;

                int line = CaretLine;
                if (line < 0 || line >= info.lineCount) return false;

                var lineInfo = info.lineInfo[line];
                int column = CaretOffset - IndentEngine.LineStart(Text, CaretOffset);

                float x = lineInfo.lineExtents.min.x;
                if (column > 0)
                {
                    int character = Mathf.Clamp(lineInfo.firstCharacterIndex + column - 1,
                                                0, info.characterCount - 1);
                    x = info.characterInfo[character].xAdvance;
                }
                if (float.IsNaN(x) || float.IsInfinity(x)) return false;

                bottom = ToScreen(camera, new Vector3(x, lineInfo.descender, 0f));
                top = ToScreen(camera, new Vector3(x, lineInfo.ascender, 0f));
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The ends of a line's baseline, used to underline errors.</summary>
        public bool TryGetLineScreenSpan(int lineIndex, Camera? camera,
                                         out Vector2 left, out Vector2 right)
        {
            left = right = Vector2.zero;
            if (_text == null) return false;

            try
            {
                var info = EnsureGeometry();
                if (info == null || lineIndex < 0 || lineIndex >= info.lineCount) return false;

                var lineInfo = info.lineInfo[lineIndex];
                float min = lineInfo.lineExtents.min.x;
                float max = lineInfo.lineExtents.max.x;

                if (float.IsNaN(min) || float.IsInfinity(min) || max - min < 1f)
                {
                    // Empty line: TMP gives no useful extent. A short mark in the left
                    // margin still says which line is wrong.
                    min = _text.rectTransform.rect.xMin + _text.margin.x;
                    max = min + _text.fontSize * 2f;
                }

                left = ToScreen(camera, new Vector3(min, lineInfo.descender, 0f));
                right = ToScreen(camera, new Vector3(max, lineInfo.descender, 0f));
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Source offset under the pointer, or -1 when the mouse is not over code.</summary>
        public int OffsetAtScreenPoint(Vector2 screen, Camera? camera)
        {
            if (_text == null || _viewport == null) return -1;

            try
            {
                if (!RectTransformUtility.RectangleContainsScreenPoint(_viewport, screen, camera))
                    return -1;

                var info = EnsureGeometry();
                if (info == null || info.characterCount == 0) return -1;

                int character = TMP_TextUtilities.FindIntersectingCharacter(_text, screen, camera, true);
                if (character < 0 || character >= info.characterCount) return -1;

                return Mathf.Clamp(info.characterInfo[character].index, 0, Text.Length);
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>Top and bottom of a line in the text's local space.</summary>
        private bool TryGetLineBounds(int lineIndex, out float top, out float bottom)
        {
            top = bottom = 0f;

            var info = EnsureGeometry();
            if (info == null || lineIndex < 0 || lineIndex >= info.lineCount) return false;

            var lineInfo = info.lineInfo[lineIndex];
            top = lineInfo.ascender;
            bottom = lineInfo.descender;
            return !float.IsNaN(top) && !float.IsNaN(bottom);
        }

        /// <summary>
        /// The raw text's geometry - never the colored one's.
        ///
        /// The colored text's character indices are shifted by the rich text tags, and
        /// using them would point at the wrong column.
        /// </summary>
        private TMP_TextInfo? EnsureGeometry()
        {
            if (_text == null) return null;

            var info = _text.textInfo;
            if (info == null || info.characterCount == 0)
            {
                _text.ForceMeshUpdate();
                info = _text.textInfo;
            }
            return info;
        }

        private Vector2 ToScreen(Camera? camera, Vector3 local) =>
            RectTransformUtility.WorldToScreenPoint(camera, _text!.transform.TransformPoint(local));
    }
}
