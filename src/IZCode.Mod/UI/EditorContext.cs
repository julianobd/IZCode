using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Assets.Scripts.Objects.Clothing;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.UI;
using HarmonyLib;
using IZCode.Mod.Diagnostics;
using IZCode.Mod.Runtime;
using IZLang.Editor;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace IZCode.Mod.UI
{
    /// <summary>
    /// Works out what the code editor is editing right now.
    ///
    /// The game's editor (<see cref="InputSourceCode"/>) is opened by the Programmable
    /// Chip Motherboard, not by the holder - and the motherboard picks the target
    /// holder through a dropdown. The whole chain is private, so we get there by
    /// reflection:
    ///
    ///   InputSourceCode.Instance.PCM             the open motherboard
    ///     ._circuitHolders[_dropdown.Selected]   the selected holder
    ///       .GetLogicableFromIndex(0..5, db)     what completion needs
    ///
    /// The holder is kept as an <see cref="ICircuitHolder"/>, never narrowed to a
    /// CircuitHousing: anything that takes a chip can be selected there, a hardsuit
    /// included, and narrowing used to throw all of its context away.
    /// </summary>
    internal static class EditorContext
    {
        private static FieldInfo? _circuitHolders;
        private static FieldInfo? _dropdown;
        private static PropertyInfo? _selectedIndex;
        private static PropertyInfo? _housingChip;

        private static bool _initialized;

        public static string Missing { get; private set; } = string.Empty;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            var missing = new StringBuilder();

            var motherboard = typeof(ProgrammableChipMotherboard);
            _circuitHolders = AccessTools.Field(motherboard, "_circuitHolders");
            if (_circuitHolders == null) missing.Append("_circuitHolders ");

            _dropdown = AccessTools.Field(motherboard, "_dropdown");
            if (_dropdown == null) missing.Append("_dropdown ");

            if (_dropdown != null)
            {
                _selectedIndex = _dropdown.FieldType.GetProperty("SelectedIndex",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_selectedIndex == null) missing.Append("SelectedIndex ");
            }

            _housingChip = typeof(CircuitHousing).GetProperty("ProgrammableChip",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (_housingChip == null) missing.Append("CircuitHousing.ProgrammableChip ");

            Missing = missing.ToString().TrimEnd();

            IZLog.Info(IZLogArea.Editor, Missing.Length == 0
                ? "editor context resolved (circuitHolders, dropdown, housing chip)"
                : "editor context incomplete, missing: " + Missing);
        }

        // ==================================================================
        //  IZ mode
        // ==================================================================
        //  Queried by the syntax highlighter on every keystroke, for each of the 128
        //  lines. Joining the whole source on that path would be needlessly expensive:
        //  the first non-empty line is enough to tell whether the buffer is IZ.

        private static int _izFrame = -1;
        private static bool _izCached;

        /// <summary>true when the editor is open and the program is IZ.</summary>
        public static bool IsEditingIZ()
        {
            var editor = InputSourceCode.Instance;
            if (editor == null || !editor.IsVisible) return false;
            return IsIZBuffer();
        }

        /// <summary>
        /// Does the editor buffer start with the marker? Valid for one frame: within a
        /// frame the text does not change between the keystroke and the redraw.
        /// </summary>
        public static bool IsIZBuffer()
        {
            int frame;
            try { frame = Time.frameCount; }
            catch { frame = -1; }

            if (frame >= 0 && frame == _izFrame) return _izCached;

            bool result = ComputeIsIZBuffer();
            _izFrame = frame;
            _izCached = result;
            return result;
        }

        private static bool ComputeIsIZBuffer()
        {
            // With the panel on, its text is the one that counts: the game's 128 lines
            // only get the copy later, and asking them would delay leaving IZ mode by
            // one frame - enough for the panel to switch itself off and back on in a
            // loop.
            if (IZCodePanel.IsActive)
                return StartsWithMarker(IZCodePanel.Instance!.Text);

            var editor = InputSourceCode.Instance;
            if (editor == null || editor.LinesOfCode == null) return false;

            var lines = editor.LinesOfCode;
            for (int i = 0; i < lines.Count; i++)
            {
                string? text = lines[i]?.Text;
                if (string.IsNullOrEmpty(text)) continue;

                string trimmed = text!.Trim();
                if (trimmed.Length == 0) continue;

                return trimmed.StartsWith(IZChipRuntime.Marker, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        /// <summary>Is the first line with content the <c>#iz</c> marker?</summary>
        private static bool StartsWithMarker(string source)
        {
            int start = 0;
            while (start < source.Length)
            {
                int end = source.IndexOf('\n', start);
                if (end < 0) end = source.Length;

                string trimmed = source.Substring(start, end - start).Trim();
                if (trimmed.Length > 0)
                    return trimmed.StartsWith(IZChipRuntime.Marker, StringComparison.OrdinalIgnoreCase);

                start = end + 1;
            }
            return false;
        }

        /// <summary>Forgets the IZ mode cache. Called when the editor opens or closes.</summary>
        public static void InvalidateModeCache() => _izFrame = -1;

        // ==================================================================
        //  Selected housing
        // ==================================================================

        /// <summary>The holder selected in the motherboard dropdown, or null.</summary>
        public static ICircuitHolder? GetHousing()
        {
            try
            {
                var editor = InputSourceCode.Instance;
                var motherboard = editor != null ? editor.PCM : null;
                if (motherboard == null)
                {
                    IZLog.Throttled(IZLogArea.Editor, IZLogLevel.Debug, "no-pcm", 5f,
                        () => "editor open with no associated motherboard; completion has no device context");
                    return null;
                }

                if (!(_circuitHolders?.GetValue(motherboard) is IList<ICircuitHolder> holders)) return null;
                if (holders.Count == 0) return null;

                int index = SelectedHolderIndex(motherboard, holders.Count);
                return holders[index];
            }
            catch (Exception ex)
            {
                IZLog.Throttled(IZLogArea.Editor, IZLogLevel.Warn, "housing-failed", 5f,
                    () => "could not find the selected housing: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// The game's dropdown starts at -1 and only gets an index once the player
        /// chooses. Until then the motherboard shows the first housing, so that is what
        /// completion has to go by.
        /// </summary>
        private static int SelectedHolderIndex(object motherboard, int count)
        {
            var dropdown = _dropdown?.GetValue(motherboard);
            if (dropdown == null || _selectedIndex == null) return 0;

            object? value = _selectedIndex.GetValue(dropdown, null);
            if (!(value is int selected) || selected < 0 || selected >= count) return 0;

            return selected;
        }

        /// <summary>
        /// The chip inside the holder. Only used to read the values of the running
        /// program's globals, so every failure here just means a plainer tooltip.
        ///
        /// A circuit housing publishes its chip as a property; a suit keeps it in a
        /// slot instead, which is why there are two ways in.
        /// </summary>
        public static Assets.Scripts.Objects.Electrical.ProgrammableChip? GetChip(ICircuitHolder? housing)
        {
            if (housing == null) return null;
            try
            {
                if (housing is CircuitHousing && _housingChip != null)
                    return _housingChip.GetValue(housing, null)
                        as Assets.Scripts.Objects.Electrical.ProgrammableChip;

                if (housing is SuitBase suit && suit.HasChipSlot)
                    return suit.ChipSlot?.Get<Assets.Scripts.Objects.Electrical.ProgrammableChip>();

                return null;
            }
            catch (Exception ex)
            {
                IZLog.Throttled(IZLogArea.Editor, IZLogLevel.Debug, "chip-failed", 5f,
                    () => "could not read the holder's chip: " + ex.Message);
                return null;
            }
        }

        /// <summary>Environment for completion and hover, already bound to the current housing.</summary>
        public static IEditorEnvironment GetEnvironment()
        {
            var housing = GetHousing();
            return new HousingEditorEnvironment(housing, GetChip(housing));
        }

        // ==================================================================
        //  Text and caret
        // ==================================================================
        //  The editor has no single field: there are up to 128 TMP_InputFields, one per
        //  line. The completion and hover engines work over the whole source with
        //  absolute offsets, so the lines have to be stitched together and (line,
        //  column) converted into an offset.

        private static int _linesFrame = -1;
        private static List<string?> _linesCache = new List<string?>();

        /// <summary>
        /// The text of each editor field, in order, cached per frame - a single overlay
        /// frame asks for it four or five times. The stitching itself lives in
        /// <see cref="LineOffsets"/>, which is pure code and covered by tests.
        /// </summary>
        private static List<string?> GetLineTexts()
        {
            int frame;
            try { frame = Time.frameCount; }
            catch { frame = -1; }

            if (frame >= 0 && frame == _linesFrame) return _linesCache;

            var editor = InputSourceCode.Instance;
            var texts = new List<string?>();

            if (editor != null && editor.LinesOfCode != null)
            {
                var lines = editor.LinesOfCode;
                texts.Capacity = lines.Count;
                for (int i = 0; i < lines.Count; i++) texts.Add(lines[i]?.Text);
            }

            _linesFrame = frame;
            _linesCache = texts;
            return texts;
        }

        public static string GetSource() =>
            IZCodePanel.IsActive ? IZCodePanel.Instance!.Text : LineOffsets.Join(GetLineTexts());

        /// <summary>Absolute offset of the start of an editor line.</summary>
        public static int GetLineStartOffset(int lineIndex)
        {
            if (!IZCodePanel.IsActive) return LineOffsets.GetLineStart(GetLineTexts(), lineIndex);

            string source = IZCodePanel.Instance!.Text;
            int offset = 0;
            for (int line = 0; line < lineIndex; line++)
            {
                int next = source.IndexOf('\n', offset);
                if (next < 0) return source.Length;
                offset = next + 1;
            }
            return Math.Min(offset, source.Length);
        }

        /// <summary>Absolute caret offset, or -1 when there is nowhere to type.</summary>
        public static int GetCaretOffset()
        {
            if (IZCodePanel.IsActive) return IZCodePanel.Instance!.CaretOffset;

            var current = EditorLineOfCode.CurrentLine;
            if (current == null) return -1;

            int lineIndex = GetCurrentLineIndex();
            if (lineIndex < 0) return -1;

            int column = 0;
            try
            {
                column = Math.Max(0, current.InputField.caretPosition);
            }
            catch
            {
                // caretPosition can throw when the field is not focused.
            }

            return LineOffsets.ToOffset(GetLineTexts(), lineIndex, column);
        }

        public static EditorLineOfCode? GetCurrentLine() => EditorLineOfCode.CurrentLine;

        public static int GetCurrentLineIndex()
        {
            if (IZCodePanel.IsActive) return IZCodePanel.Instance!.CaretLine;

            var current = EditorLineOfCode.CurrentLine;
            var editor = InputSourceCode.Instance;
            if (current == null || editor == null || editor.LinesOfCode == null) return -1;
            return editor.LinesOfCode.IndexOf(current);
        }

        // ==================================================================
        //  Editing
        // ==================================================================

        /// <summary>
        /// Replaces a range of the source with another and leaves the caret at its end.
        ///
        /// The offsets are absolute, which is how completion thinks. When the IZ panel
        /// is on the edit is direct; in the original editor it has to be clipped into a
        /// single line, because that is how that editor stores the text.
        /// </summary>
        public static bool ReplaceRange(int start, int length, string replacement)
        {
            if (IZCodePanel.IsActive)
            {
                IZCodePanel.Instance!.Replace(start, length, replacement, start + replacement.Length);
                return true;
            }

            var line = EditorLineOfCode.CurrentLine;
            int lineIndex = GetCurrentLineIndex();
            if (line == null || lineIndex < 0) return false;

            int lineStart = GetLineStartOffset(lineIndex);
            int from = start - lineStart;
            string text = line.Text ?? string.Empty;

            if (from < 0 || from > text.Length) return false;
            if (from + length > text.Length) length = text.Length - from;

            line.Text = text.Substring(0, from) + replacement + text.Substring(from + length);

            int caret = from + replacement.Length;
            try
            {
                line.InputField.caretPosition = caret;
                line.InputField.selectionAnchorPosition = caret;
                line.InputField.selectionFocusPosition = caret;
                // Tab takes focus away from the field in uGUI; without this the player
                // would have to click back on the line to keep typing.
                line.InputField.ActivateInputField();
            }
            catch (Exception ex)
            {
                IZLog.Debug(IZLogArea.Completion, "caret not repositioned after accepting: " + ex.Message);
            }
            return true;
        }

        // ==================================================================
        //  Visible code area
        // ==================================================================

        private static RectTransform? _codeArea;

        /// <summary>
        /// The screen rectangle where the code actually shows up.
        ///
        /// The 128 lines always exist, including the ones scrolled out of view: without
        /// this clipping, marking an error on line 90 would draw the red strip loose
        /// over the rest of the interface. We look for the mask the editor itself uses
        /// to clip the list; when there is none, the window rectangle will do - wider
        /// than ideal, but it never points outside the editor.
        /// </summary>
        public static bool TryGetCodeAreaScreenRect(Camera? camera, out Vector2 min, out Vector2 max)
        {
            min = max = Vector2.zero;

            if (IZCodePanel.IsActive)
                return IZCodePanel.Instance!.TryGetAreaScreenRect(camera, out min, out max);

            var area = ResolveCodeArea();
            if (area == null) return false;

            var corners = new Vector3[4];
            area.GetWorldCorners(corners);

            Vector2 bottomLeft = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            Vector2 topRight = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);

            min = new Vector2(Mathf.Min(bottomLeft.x, topRight.x), Mathf.Min(bottomLeft.y, topRight.y));
            max = new Vector2(Mathf.Max(bottomLeft.x, topRight.x), Mathf.Max(bottomLeft.y, topRight.y));
            return true;
        }

        private static RectTransform? ResolveCodeArea()
        {
            if (_codeArea != null) return _codeArea;

            var editor = InputSourceCode.Instance;
            if (editor == null) return null;

            var lines = editor.LinesOfCode;
            if (lines != null && lines.Count > 0 && lines[0] != null)
            {
                for (var t = lines[0].transform.parent; t != null; t = t.parent)
                {
                    bool masks = t.GetComponent<RectMask2D>() != null || t.GetComponent<Mask>() != null;
                    if (masks && t is RectTransform rect)
                    {
                        _codeArea = rect;
                        IZLog.Debug(IZLogArea.Editor, "code area clipped by '" + t.name + "'");
                        return _codeArea;
                    }
                }
            }

            _codeArea = editor.transform as RectTransform;
            if (_codeArea != null)
                IZLog.Debug(IZLogArea.Editor, "no mask in the editor; using the whole window as the code area");

            return _codeArea;
        }

        /// <summary>Forgets the clipped area. The editor may be rebuilt between openings.</summary>
        public static void InvalidateCodeArea() => _codeArea = null;

        /// <summary>
        /// Bottom and top of the caret, in screen coordinates.
        ///
        /// It is the anchor for the suggestion list: it opens downwards from the bottom
        /// and, when it does not fit there, upwards from the top.
        /// </summary>
        public static bool TryGetCaretScreenSpan(Camera? camera, out Vector2 bottom, out Vector2 top)
        {
            bottom = top = Vector2.zero;

            if (IZCodePanel.IsActive)
                return IZCodePanel.Instance!.TryGetCaretScreenSpan(camera, out bottom, out top);

            var line = EditorLineOfCode.CurrentLine;
            if (line == null) return false;

            // The anchor is the line's text rectangle, not the whole line's - the whole
            // line includes the margin with the number, and the panel would appear over
            // the line numbers.
            var text = line.InputText;
            var rect = text != null ? text.rectTransform : line.transform as RectTransform;
            if (rect == null) return false;

            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);      // 0=bottom-left, 1=top-left

            bottom = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            top = RectTransformUtility.WorldToScreenPoint(camera, corners[1]);

            float caretX = CaretScreenX(line, text, camera);
            if (caretX > 0f)
            {
                bottom.x = caretX;
                top.x = caretX;
            }
            return true;
        }

        /// <summary>Screen X of the caret in the original editor, or 0 when the geometry does not help.</summary>
        private static float CaretScreenX(EditorLineOfCode line, TMP_Text? text, Camera? camera)
        {
            if (text == null) return 0f;

            try
            {
                int column = Math.Max(0, line.InputField.caretPosition);
                if (column == 0) return 0f;

                var info = text.textInfo;
                if (info == null || info.characterCount == 0)
                {
                    text.ForceMeshUpdate();
                    info = text.textInfo;
                }
                if (info == null || info.characterCount == 0) return 0f;

                var character = info.characterInfo[Math.Min(column, info.characterCount) - 1];
                Vector3 world = text.transform.TransformPoint(character.topRight);
                return RectTransformUtility.WorldToScreenPoint(camera, world).x;
            }
            catch
            {
                // Missing geometry or an unfocused field: the left edge of the line will do.
                return 0f;
            }
        }

        /// <summary>The ends of a line's baseline, in screen coordinates - the error marker.</summary>
        public static bool TryGetLineScreenSpan(int lineIndex, Camera? camera,
                                                out Vector2 left, out Vector2 right)
        {
            left = right = Vector2.zero;

            if (IZCodePanel.IsActive)
                return IZCodePanel.Instance!.TryGetLineScreenSpan(lineIndex, camera, out left, out right);

            var editor = InputSourceCode.Instance;
            if (editor == null || editor.LinesOfCode == null) return false;
            if (lineIndex < 0 || lineIndex >= editor.LinesOfCode.Count) return false;

            var line = editor.LinesOfCode[lineIndex];
            var rect = line != null ? line.transform as RectTransform : null;
            if (rect == null) return false;

            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);      // 0=bottom-left, 3=bottom-right

            left = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            right = RectTransformUtility.WorldToScreenPoint(camera, corners[3]);
            return true;
        }

        /// <summary>
        /// Source offset under the pointer, or -1 when the mouse is not over code.
        ///
        /// It always uses the raw text, never the colored one: the colored text's
        /// character indices are shifted by the rich text tags and would point at the
        /// wrong column.
        /// </summary>
        public static int OffsetAtScreenPoint(Vector2 screen, Camera? camera)
        {
            if (IZCodePanel.IsActive)
                return IZCodePanel.Instance!.OffsetAtScreenPoint(screen, camera);

            var editor = InputSourceCode.Instance;
            if (editor == null || editor.LinesOfCode == null) return -1;

            var lines = editor.LinesOfCode;
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line == null || string.IsNullOrEmpty(line.Text)) continue;

                var rect = line.transform as RectTransform;
                if (rect == null) continue;
                if (!RectTransformUtility.RectangleContainsScreenPoint(rect, screen, camera)) continue;

                var text = line.InputText;
                if (text == null) return -1;

                // The component may have no geometry because it is not being drawn;
                // without this TMP cannot locate the character.
                if (text.textInfo == null || text.textInfo.characterCount == 0)
                    text.ForceMeshUpdate();

                int character = TMP_TextUtilities.FindIntersectingCharacter(text, screen, camera, true);
                if (character < 0) return -1;

                int column = Mathf.Clamp(character, 0, Math.Max(0, line.Text.Length - 1));
                return GetLineStartOffset(i) + column;
            }

            return -1;
        }

        /// <summary>
        /// Repaints every line. Needed when the buffer switches from IC10 to IZ or back:
        /// the lines already on screen were colored by the wrong highlighter and nobody
        /// touches them again until they are edited.
        /// </summary>
        public static void RefreshAllLines()
        {
            if (IZCodePanel.IsActive) IZCodePanel.Instance!.Repaint();

            var editor = InputSourceCode.Instance;
            if (editor == null || editor.LinesOfCode == null) return;

            var lines = editor.LinesOfCode;
            for (int i = 0; i < lines.Count; i++)
            {
                try { lines[i]?.ReformatText(lines[i].Text ?? string.Empty); }
                catch (Exception ex)
                {
                    IZLog.Throttled(IZLogArea.Highlight, IZLogLevel.Warn, "refresh-line", 10f,
                        () => "failed to repaint line " + i + ": " + ex.Message);
                }
            }
        }
    }
}
