using System;
using Assets.Scripts.UI;
using HarmonyLib;
using IZCode.Mod.Diagnostics;
using IZCode.Mod.UI;

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

            IZLog.Debug(IZLogArea.Editor, "code editor opened");
            Install();
        }

        /// <summary>
        /// The Paste and Clear buttons, and loading a script from the Library, all end
        /// here: they replace the 128 lines behind the IZ code area's back.
        ///
        /// The panel has to be told, or it would hand its own text back on the next
        /// frame and undo the button. The mode cache goes with it: the pasted program
        /// may be IC10 where the previous one was IZ, or the other way around.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(InputSourceCode.Paste))]
        public static void Paste_Postfix()
        {
            IZCodePanel.ReloadFromGameLines();
            EditorContext.InvalidateModeCache();
        }

        /// <summary>
        /// Copy() reads the 128 lines, and everything that leaves the editor goes
        /// through it: the copy button, the save button and Save As. In IZ mode the
        /// lines are only written in LateUpdate, so without this the copy could be one
        /// frame behind what is on screen.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(InputSourceCode.Copy))]
        public static void Copy_Prefix()
        {
            IZCodePanel.FlushToGame();
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
