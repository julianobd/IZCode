using System;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using IZCode.Mod.Diagnostics;
using IZCode.Mod.Runtime;
using IZLang.Vm;

namespace IZCode.Mod.Patches
{
    /// <summary>
    /// The mod's only two grafts onto the chip's behaviour.
    ///
    /// The strategy is to reuse the Programmable Chip that already exists instead of
    /// creating a new item: that way we inherit the in-game editor, source
    /// serialization in the save and multiplayer replication for free - none of it
    /// has to be rewritten. A chip becomes "IZ" when the first source line is the
    /// <c>#iz</c> marker; without the marker, IC10 behaviour stays exactly as it was.
    /// </summary>
    [HarmonyPatch(typeof(ProgrammableChip))]
    internal static class ProgrammableChipPatches
    {
        /// <summary>
        /// Once the game has processed the source, take over when it is IZ.
        ///
        /// It runs as a postfix, not a prefix, on purpose: letting the original run
        /// keeps <c>SourceCode</c>, the save and the network sync untouched. Only
        /// afterwards do we undo what the IC10 parser concluded - which, for IZ code,
        /// is always a compile error.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(ProgrammableChip.SetSourceCode), typeof(string))]
        public static void SetSourceCode_Postfix(ProgrammableChip __instance, string sourceCode)
        {
            try
            {
                if (!IZChipRuntime.IsIZSource(sourceCode))
                {
                    // Back to IC10 (or emptied): drop the IZ state and let the game
                    // carry on with what it compiled itself.
                    if (IZChipRuntime.TryGet(__instance, out var previous))
                    {
                        previous.Clear();
                        IZChipRuntime.Remove(__instance);
                        IZLog.Info(IZLogArea.Chip, "chip went back to IC10; IZ state discarded");
                    }
                    return;
                }

                var housing = ChipAccess.GetHousing(__instance);
                if (housing == null)
                    IZLog.Debug(IZLogArea.Chip,
                        "IZ chip in nothing that holds a chip; it compiles, but reads no device");

                var runtime = IZChipRuntime.GetOrCreate(__instance);
                runtime.Compile(sourceCode, housing!);

                // The IC10 parser has just rejected the IZ source. Without clearing
                // that, the housing would never call Execute.
                ChipAccess.ClearIC10Lines(__instance);
                ChipAccess.ClearCompileError(__instance);

                if (runtime.HasError)
                {
                    ChipAccess.SetRuntimeError(__instance, runtime.EditorErrorLine);
                    housing?.RaiseError(1);
                    IZLog.Warn(IZLogArea.Chip,
                        "IZ did not compile (line " + runtime.ErrorLine + "): " + runtime.ErrorMessage);
                }
                else
                {
                    housing?.ClearError();
                    IZLog.Info(IZLogArea.Chip, "IZ compiled: " + sourceCode.Length + " bytes of source, " +
                                               (runtime.Compilation?.Program?.Code.Length ?? 0) + " instructions");
                }
            }
            catch (Exception ex)
            {
                // A mod must never take the game down because of one chip.
                IZLog.Exception(IZLogArea.Chip, "failed to compile IZ code", ex);
            }
        }

        /// <summary>
        /// Replaces the IC10 interpreter with the IZVm when the chip runs IZ code.
        ///
        /// <paramref name="runCount"/> arrives as 128 (IC10's budget). We use the
        /// configured value, larger by default, since IZ instructions are more granular
        /// than IC10's.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ProgrammableChip.Execute))]
        public static bool Execute_Prefix(ProgrammableChip __instance, int runCount)
        {
            if (!IZChipRuntime.TryGet(__instance, out var runtime) || !runtime.IsRunnable)
                return true;                                  // follow the normal IC10 flow

            try
            {
                var state = runtime.Tick(IZCodePlugin.OpsPerTick);

                if (state == ExecutionResult.Error)
                {
                    ChipAccess.SetRuntimeError(__instance, runtime.EditorErrorLine);
                    ChipAccess.GetHousing(__instance)?.RaiseError(1);
                    IZLog.Warn(IZLogArea.Vm,
                        "IZ runtime error (line " + runtime.ErrorLine + "): " + runtime.ErrorMessage);
                }
                else
                {
                    // One tick per chip every 0.5s: without throttling, this alone would
                    // fill the log of any base with half a dozen chips.
                    IZLog.Throttled(IZLogArea.Vm, IZLogLevel.Trace, "tick", 5f,
                        () => "IZ tick finished with state " + state);
                }
            }
            catch (Exception ex)
            {
                IZLog.Exception(IZLogArea.Vm, "failed to run IZ code", ex);
                runtime.Clear();
            }

            return false;                                     // do not run IC10
        }
    }
}
