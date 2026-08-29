using System;
using Assets.Scripts.Localization2;
using Assets.Scripts.UI;
using Assets.Scripts.Util;
using HarmonyLib;
using IZCode.Mod.Diagnostics;
using IZCode.Mod.UI;
using UnityEngine;

namespace IZCode.Mod.Patches
{
    /// <summary>
    /// Installs the completion and hover overlay into the game's code editor.
    ///
    /// That is all: no editor behaviour is changed. The overlay is a sibling
    /// GameObject that reads the editor state every frame and draws on top of it - if
    /// it breaks, the editor keeps working exactly as before.
    /// </summary>
    [HarmonyPatch(typeof(InputSourceCode))]
    internal static class EditorPatches
    {
        /// <summary>
        /// Once the editor has built its 128 lines, its canvas exists and the overlay
        /// can be hung off it.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(InputSourceCode.Initialize))]
        public static void Initialize_Postfix()
        {
            IZLog.Debug(IZLogArea.Editor, "InputSourceCode.Initialize: installing overlay");
            Install();
        }

        /// <summary>
        /// Safety net: if the editor is recreated on a scene change, Initialize may
        /// have gone by before the mod loaded.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(InputSourceCode.SetVisible))]
        public static void SetVisible_Postfix(bool isVisble)
        {
            // The whole buffer changes when the editor opens; IZ mode has to be
            // re-evaluated from scratch instead of reusing the previous frame's. The
            // clipped area too: the editor may have been rebuilt in another scene.
            EditorContext.InvalidateModeCache();
            EditorContext.InvalidateCodeArea();

            // And the IZ code area: on open, the game will still dump the new source
            // into the 128 lines, and the panel has to load from there. On close,
            // switching it off hands the text back to the lines before the game calls
            // Copy() to save.
            IZCodePanel.SetEnabled(false);

            if (!isVisble)
            {
                IZLog.Debug(IZLogArea.Editor, "code editor closed");
                return;
            }

            // A new session: what the panel had belongs to the chip edited before this
            // one. The game is about to paste this chip's source into the lines, and
            // that is what the panel will pick up.
            IZCodePanel.ForgetSession();

            IZLog.Debug(IZLogArea.Editor, "code editor opened");
            Install();
        }

        /// <summary>
        /// Everything that puts a program into the editor ends here: the Paste and
        /// Clear buttons, a script loaded from the Library, and the chip's own source
        /// when the editor opens. The game writes it into its 128 lines, cut to 128
        /// lines of 90 characters, and knows nothing about the IZ code area.
        ///
        /// The panel takes the whole of it instead, so nothing is lost on the way in,
        /// and stops handing its own, stale, text back on the next frame - which is
        /// what used to make those buttons look inert.
        ///
        /// The mode cache goes with it: the program that arrived may be IC10 where the
        /// previous one was IZ, or the other way around.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(InputSourceCode.Paste))]
        public static void Paste_Postfix(string value)
        {
            IZCodePanel.AdoptPaste(value);
            EditorContext.InvalidateModeCache();
        }

        /// <summary>
        /// Copy() is the one way out of the editor: the copy button, the save button,
        /// Save As and Confirm all ask it for the program. It reads the 128 lines and
        /// stops at 4096 characters, which is the whole of the game's ceiling.
        ///
        /// For IZ code the panel answers instead, with the program as it stands, and
        /// those two limits stop applying. Anything else - IC10, or a program this
        /// panel never took over - falls through to the original.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(InputSourceCode.Copy))]
        public static bool Copy_Prefix(ref string __result)
        {
            string? source = IZCodePanel.TryGetSource();
            if (source == null) return true;

            __result = source;
            return false;
        }

        /// <summary>
        /// The byte counter and the Confirm button, which the game works out from its
        /// 128 lines against a ceiling of 4096.
        ///
        /// In IZ mode neither number is the right one: the program is longer than the
        /// lines can hold, and its ceiling is the code area's. Left alone, the counter
        /// would stop growing and Confirm would stay enabled while the program was
        /// already past what can be drawn.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(InputSourceCode.UpdateFileSize))]
        public static void UpdateFileSize_Postfix(InputSourceCode __instance)
        {
            try
            {
                if (!IZCodePanel.IsActive) return;

                int size = IZCodePanel.Instance!.Text.Length;
                int limit = IZCodePanel.MaxFileSize;

                if (__instance.SizeText != null)
                {
                    __instance.SizeText.text = GameStrings.CodeEditorFileSize.AsString(
                        StringManager.Get(size), StringManager.Get(limit));
                    __instance.SizeText.color = size > limit ? Color.red : Color.white;
                }

                if (__instance.SubmitButton != null)
                    __instance.SubmitButton.interactable = size <= limit;
            }
            catch (Exception ex)
            {
                IZLog.Throttled(IZLogArea.Editor, IZLogLevel.Warn, "file-size-patch", 30f,
                    () => "could not show the IZ byte count: " + ex.Message);
            }
        }

        private static void Install()
        {
            try
            {
                IZEditorOverlay.Install();
            }
            catch (Exception ex)
            {
                IZLog.Exception(IZLogArea.Editor, "could not install the editor overlay", ex);
            }
        }
    }
}
