using System;
using System.Reflection;
using Assets.Scripts.Objects.Motherboards;
using HarmonyLib;
using IZCode.Mod.Diagnostics;
using IZCode.Mod.Runtime;
using IZLang.Editor;
using UnityEngine.UI;

namespace IZCode.Mod.Patches
{
    /// <summary>
    /// The same syntax highlighting, now on the Programmable Chip Motherboard screen.
    ///
    /// It is the second surface where the code shows up: the editor shows what is being
    /// typed, this screen shows what was saved. Without it, fixing only the editor
    /// leaves the player with a program that is colored while editing and red as soon
    /// as the window closes.
    ///
    /// It runs as a postfix rather than a prefix because the original does more than
    /// paint - it stores <c>_sourceCode</c> and sets the network update flag. We let it
    /// do its job and only repaint on top; the cost is building the text twice in an
    /// action that only happens when the player saves.
    ///
    /// The screen uses uGUI's old <c>Text</c>, not TextMeshPro - hence
    /// <see cref="RichTextFlavor.LegacyText"/>: same painting, a different way to
    /// escape the <c>&lt;</c>.
    /// </summary>
    [HarmonyPatch(typeof(ProgrammableChipMotherboard))]
    internal static class MotherboardScreenPatches
    {
        private static FieldInfo? _sourceCodeText;
        private static bool _resolved;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;

            _sourceCodeText = AccessTools.Field(typeof(ProgrammableChipMotherboard), "SourceCode");

            if (_sourceCodeText == null)
                IZLog.Warn(IZLogArea.Highlight,
                    "motherboard screen: field 'SourceCode' not found; it will keep painting " +
                    "IZ code with the IC10 highlighter");
            else
                IZLog.Debug(IZLogArea.Highlight, "motherboard screen: field 'SourceCode' resolved");
        }

        /// <summary>
        /// How much of the program the screen shows.
        ///
        /// This is uGUI's old <c>Text</c>: one mesh, and a mesh stops at 65000-odd
        /// vertices, four to a character. An IZ program can now be longer than that, so
        /// it is cut here - on a screen where the code is a few pixels tall, what is
        /// past this point was never going to be read anyway.
        /// </summary>
        private const int ScreenCharacters = 8000;

        /// <summary>The program as much of it as the screen can draw.</summary>
        private static string ForTheScreen(string? sourceCode)
        {
            if (string.IsNullOrEmpty(sourceCode)) return string.Empty;
            if (sourceCode!.Length <= ScreenCharacters) return sourceCode;

            return sourceCode.Substring(0, ScreenCharacters) + "\n...";
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(ProgrammableChipMotherboard.SetSourceCode), typeof(string))]
        public static void SetSourceCode_Postfix(ProgrammableChipMotherboard __instance, string sourceCode)
        {
            try
            {
                if (!IZChipRuntime.IsIZSource(sourceCode)) return;   // IC10: leave it as the game left it

                Resolve();
                if (!(_sourceCodeText?.GetValue(__instance) is Text screen)) return;

                screen.text = SyntaxHighlighter.Highlight(ForTheScreen(sourceCode), null,
                                                          RichTextFlavor.LegacyText);
                screen.raycastTarget = false;

                IZLog.Debug(IZLogArea.Highlight,
                    "motherboard screen repainted in IZ (" + (sourceCode?.Length ?? 0) + " bytes)");
            }
            catch (Exception ex)
            {
                // Cosmetic: when in doubt, keep what the game painted.
                IZLog.Throttled(IZLogArea.Highlight, IZLogLevel.Error, "motherboard-highlight", 30f,
                    () => "failed to repaint the motherboard screen: " + ex);
            }
        }
    }
}
