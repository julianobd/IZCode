using System;
using System.Collections;
using System.Reflection;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using IZCode.Mod.Diagnostics;

namespace IZCode.Mod.Patches
{
    /// <summary>
    /// Access to the private <see cref="ProgrammableChip"/> members the mod needs to
    /// touch.
    ///
    /// Everything is resolved once, at load time, and every member is optional: if a
    /// game update renames a field, the mod loses that specific feature and says so in
    /// the log, instead of taking the whole game down.
    /// </summary>
    internal static class ChipAccess
    {
        private static PropertyInfo? _circuitHousing;
        private static FieldInfo? _linesOfCode;
        private static FieldInfo? _compileErrorLineNumber;
        private static FieldInfo? _compileErrorType;
        private static FieldInfo? _errorLineNumberSynced;
        private static FieldInfo? _errorTypeSynced;

        /// <summary>Type of the IC exception enum, resolved at runtime.</summary>
        private static Type? _exceptionType;

        public static bool Initialized { get; private set; }

        /// <summary>List of the members that were not found. Empty when everything matched.</summary>
        public static string Missing { get; private set; } = string.Empty;

        public static void Initialize()
        {
            if (Initialized) return;

            var chip = typeof(ProgrammableChip);
            var missing = new System.Text.StringBuilder();

            _circuitHousing = chip.GetProperty("CircuitHousing",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (_circuitHousing == null) missing.Append("CircuitHousing ");

            _linesOfCode = AccessTools.Field(chip, "_LinesOfCode");
            if (_linesOfCode == null) missing.Append("_LinesOfCode ");

            _compileErrorLineNumber = AccessTools.Field(chip, "_compileErrorLineNumber");
            if (_compileErrorLineNumber == null) missing.Append("_compileErrorLineNumber ");

            _compileErrorType = AccessTools.Field(chip, "_compileErrorType");
            if (_compileErrorType == null) missing.Append("_compileErrorType ");

            _errorLineNumberSynced = AccessTools.Field(chip, "_ErrorLineNumberSynced");
            if (_errorLineNumberSynced == null) missing.Append("_ErrorLineNumberSynced ");

            _errorTypeSynced = AccessTools.Field(chip, "_ErrorTypeSynced");
            if (_errorTypeSynced == null) missing.Append("_ErrorTypeSynced ");

            _exceptionType = _compileErrorType?.FieldType;

            Missing = missing.ToString().TrimEnd();
            Initialized = true;

            IZLog.Debug(IZLogArea.Load,
                "ChipAccess: _compileErrorType=" + (_compileErrorType?.FieldType.Name ?? "-") +
                " _ErrorTypeSynced=" + (_errorTypeSynced?.FieldType.Name ?? "-"));
        }

        public static ICircuitHolder? GetHousing(ProgrammableChip chip) =>
            _circuitHousing?.GetValue(chip, null) as ICircuitHolder;

        /// <summary>
        /// Empties the IC10 instruction list. An IZ chip has none, and with the list
        /// empty the original <c>Execute</c> returns immediately - which makes the mod
        /// an additive layer rather than a detour off the normal path.
        /// </summary>
        public static void ClearIC10Lines(ProgrammableChip chip)
        {
            try
            {
                if (_linesOfCode?.GetValue(chip) is IList lines) lines.Clear();
            }
            catch (Exception ex)
            {
                IZLog.Warn(IZLogArea.Chip, "could not clear the IC10 lines: " + ex.Message);
            }
        }

        /// <summary>
        /// Clears the IC10 compile error. Indispensable: <c>CircuitHousing.Execute</c>
        /// only calls the chip when <c>CompilationError</c> is false, and the IC10
        /// parser always rejects IZ code.
        /// </summary>
        public static void ClearCompileError(ProgrammableChip chip)
        {
            SetNumericField(_compileErrorLineNumber, chip, 0);
            SetNumericField(_compileErrorType, chip, 0);
            SetNumericField(_errorLineNumberSynced, chip, 0);
            SetNumericField(_errorTypeSynced, chip, 0);
        }

        /// <summary>
        /// Writes an IZ runtime error into the fields the game already displays (error
        /// line in the editor and the housing LED), without inventing new UI.
        /// </summary>
        public static void SetRuntimeError(ProgrammableChip chip, int line)
        {
            int clamped = line < 0 ? 0 : (line > ushort.MaxValue ? ushort.MaxValue : line);
            SetNumericField(_errorLineNumberSynced, chip, clamped);
            // 'Unknown' is the most generic value of the game's enum; our detailed
            // message goes through the log and the editor panel.
            SetNumericField(_errorTypeSynced, chip, UnknownExceptionValue());
        }

        /// <summary>
        /// Puts out a runtime error that has been left behind.
        ///
        /// The counterpart of <see cref="SetRuntimeError"/>: a device error clears by
        /// itself as soon as the device is back, and the LED has to follow. IC10 does
        /// the same after every instruction that does not throw.
        /// </summary>
        public static void ClearRuntimeError(ProgrammableChip chip)
        {
            SetNumericField(_errorLineNumberSynced, chip, 0);
            SetNumericField(_errorTypeSynced, chip, 0);
        }

        /// <summary>
        /// Writes a number into any field, respecting its actual type.
        ///
        /// This is fiddlier than it looks because the game mixes them: the error line
        /// is a <c>ushort</c>, the compiled error type is the <c>ICExceptionType</c>
        /// enum, but the error type synchronized over the network is a raw
        /// <c>byte</c>. Writing the wrong value raises no compile error - it raises an
        /// <c>ArgumentException</c> at runtime, inside a try/catch, and the chip simply
        /// stops running without explaining why.
        /// </summary>
        private static void SetNumericField(FieldInfo? field, ProgrammableChip chip, int value)
        {
            if (field == null) return;

            try
            {
                var type = field.FieldType;

                if (type.IsEnum)
                {
                    field.SetValue(chip, Enum.ToObject(type, value));
                    return;
                }

                field.SetValue(chip, Convert.ChangeType(value, type));
            }
            catch (Exception ex)
            {
                IZLog.Throttled(IZLogArea.Chip, IZLogLevel.Warn, "set-field-" + field.Name, 30f,
                    () => "could not write " + field.Name + " (" + field.FieldType.Name + "): " + ex.Message);
            }
        }

        /// <summary>Value of 'Unknown' in the game's enum, or 0 when the name no longer exists.</summary>
        private static int UnknownExceptionValue()
        {
            if (_exceptionType == null) return 0;
            try
            {
                return Convert.ToInt32(Enum.Parse(_exceptionType, "Unknown"));
            }
            catch (ArgumentException)
            {
                return 0;
            }
        }
    }
}
