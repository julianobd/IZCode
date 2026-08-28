using System;
using Assets.Scripts.UI;
using HarmonyLib;
using IZCode.Mod.Diagnostics;
using IZCode.Mod.UI;
using IZLang.Editor;

namespace IZCode.Mod.Patches
{
    /// <summary>
    /// Swaps the editor's syntax highlighter when the program is IZ.
    ///
    /// The editor paints each line with <c>Localization.ParseScript</c>, which only
    /// knows IC10. On IZ source it gets it wrong in two visible ways:
    ///
    ///   * whatever it does not recognize keeps the field's default color, which is
    ///     red - and it recognizes nothing of IZ, so the whole program turns red;
    ///   * <c>#</c> opens a comment in IC10, so <c>const X = #"Prefab"</c> loses half
    ///     the line to comment gray.
    ///
    /// This prefix takes over the line when the buffer is IZ and lets the original
    /// through when it is IC10 - so a chip without the marker stays painted exactly as
    /// before.
    /// </summary>
    [HarmonyPatch(typeof(EditorLineOfCode))]
    internal static class SyntaxHighlightPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(EditorLineOfCode.ReformatText), new Type[] { typeof(string) })]
        public static bool ReformatText_Prefix(EditorLineOfCode __instance, string inputString)
        {
            try
            {
                if (!EditorContext.IsIZBuffer()) return true;      // IC10: follow the game

                string text = inputString ?? string.Empty;
                string trimmed = text.TrimEnd();

                if (__instance.FormattedText != null)
                    __instance.FormattedText.text = trimmed.Length == 0
                        ? string.Empty
                        : SyntaxHighlighter.HighlightLine(trimmed);

                if (trimmed.Length > 0) __instance.IsVoidLine = false;

                IZLog.Trace(IZLogArea.Highlight, "line painted in IZ: " + trimmed);
                return false;
            }
            catch (Exception ex)
            {
                // Never worth taking the editor down over color: hand control back to
                // the game's highlighter.
                IZLog.Throttled(IZLogArea.Highlight, IZLogLevel.Error, "highlight-failed", 10f,
                    () => "IZ highlighting failed, falling back to the game's: " + ex);
                return true;
            }
        }
    }
}
