using System;
using HarmonyLib;
using IZCode.Mod.Diagnostics;
using IZCode.Mod.UI;
using TMPro;
using UnityEngine;

namespace IZCode.Mod.Patches
{
    /// <summary>
    /// The two keystrokes the IZ code area cannot handle from the outside.
    ///
    /// <para><b>Up and Down with the suggestion list open.</b> TextMeshPro moves the
    /// caret on an arrow before anything else sees the key, and moving the caret is
    /// what makes the list recompute for the new position - which is why the list
    /// could not be walked with the arrows at all, Ctrl held or not. The key has to be
    /// taken away from the field, and this is the only place it can be taken.</para>
    ///
    /// <para><b>Ctrl+V.</b> TextMeshPro pastes by handing the clipboard to the input
    /// filter one character at a time, which is not a paste but a very fast typist:
    /// the filter's per-character decisions (a tab becomes one space, a '}' re-indents
    /// its line) all fire, and a program pasted from a real editor comes out mangled.
    /// The code area pastes the whole fragment as one edit instead.</para>
    ///
    /// It only ever touches the code area's own field: every other input field in the
    /// game goes through untouched. If the patch does not apply, both keys fall back
    /// to TextMeshPro's own behaviour.
    /// </summary>
    [HarmonyPatch(typeof(TMP_InputField), "KeyPressed")]
    internal static class CodeAreaKeyPatches
    {
        [HarmonyPrefix]
        public static void Prefix(TMP_InputField __instance, Event evt)
        {
            try
            {
                if (evt == null || !IZCodePanel.Owns(__instance)) return;

                var modifiers = evt.modifiers;
                bool shift = (modifiers & EventModifiers.Shift) != 0;
                bool alt = (modifiers & EventModifiers.Alt) != 0;
                bool command = SystemInfo.operatingSystemFamily == OperatingSystemFamily.MacOSX
                    ? (modifiers & EventModifiers.Command) != 0
                    : (modifiers & EventModifiers.Control) != 0;

                switch (evt.keyCode)
                {
                    case KeyCode.UpArrow:
                        // Shift+Up still extends the selection: only the plain move
                        // through the list is taken.
                        if (!shift && !alt && IZEditorOverlay.MoveCompletionSelection(-1)) Swallow(evt);
                        return;

                    case KeyCode.DownArrow:
                        if (!shift && !alt && IZEditorOverlay.MoveCompletionSelection(1)) Swallow(evt);
                        return;

                    case KeyCode.V:
                        if (command && !shift && !alt && IZCodePanel.Instance!.PasteFromClipboard())
                            Swallow(evt);
                        return;
                }
            }
            catch (Exception ex)
            {
                // A failure here must never cost the keystroke: leaving the event
                // untouched hands it back to TextMeshPro, whole.
                IZLog.Throttled(IZLogArea.Editor, IZLogLevel.Warn, "key-patch", 10f,
                    () => "could not take over the key: " + ex.Message);
            }
        }

        /// <summary>
        /// Empties the event instead of skipping the original method.
        ///
        /// KeyPressed answers with a type the game keeps private, so there is no return
        /// value to fabricate. An event with no key and no character walks through it
        /// doing nothing, which is the same result and costs no reflection.
        /// </summary>
        private static void Swallow(Event evt)
        {
            evt.keyCode = KeyCode.None;
            evt.character = '\0';
            evt.modifiers = EventModifiers.None;
        }
    }
}
