using System;
using System.Runtime.CompilerServices;
using Assets.Scripts.Objects.Electrical;
using IZLang;
using IZLang.Vm;

namespace IZCode.Mod.Runtime
{
    /// <summary>
    /// A chip's IZ state: compiled program, VM and diagnostics.
    ///
    /// It hangs off the <see cref="ProgrammableChip"/> through a
    /// <see cref="ConditionalWeakTable{TKey,TValue}"/>, so it goes away with the chip
    /// and holds no reference that would keep it from being collected.
    /// </summary>
    public sealed class IZChipRuntime
    {
        /// <summary>
        /// The marker that identifies an IZ program. It has to start with '#' because
        /// the game's IC10 compiler treats lines starting with '#' as comments - that
        /// way a chip with IZ code does not blow up if the mod is removed.
        /// </summary>
        public const string Marker = "#iz";

        private static readonly ConditionalWeakTable<ProgrammableChip, IZChipRuntime> Table =
            new ConditionalWeakTable<ProgrammableChip, IZChipRuntime>();

        public CompilationResult? Compilation { get; private set; }
        public IZVm? Vm { get; private set; }

        /// <summary>One-based error line, compile time or runtime. 0 when all is well.</summary>
        public int ErrorLine { get; private set; }

        public string? ErrorMessage { get; private set; }

        public bool HasError => ErrorLine > 0;

        /// <summary>
        /// The same line in the game editor's numbering, which is zero-based - the
        /// compiler counts from 1. Without this conversion the error LED always points
        /// one line below the one with the problem.
        /// </summary>
        public int EditorErrorLine => ErrorLine > 0 ? ErrorLine - 1 : 0;

        /// <summary>true when there is a valid program ready to run.</summary>
        public bool IsRunnable => Vm != null && !HasError;

        // ------------------------------------------------------------------
        //  Detection and table
        // ------------------------------------------------------------------

        /// <summary>A source is IZ when its first non-empty line is the marker.</summary>
        public static bool IsIZSource(string? source)
        {
            if (string.IsNullOrEmpty(source)) return false;

            int i = 0;
            while (i < source!.Length)
            {
                // skip blank lines
                int lineEnd = source.IndexOf('\n', i);
                if (lineEnd < 0) lineEnd = source.Length;

                string line = source.Substring(i, lineEnd - i).Trim();
                if (line.Length > 0)
                    return line.StartsWith(Marker, StringComparison.OrdinalIgnoreCase);

                i = lineEnd + 1;
            }
            return false;
        }

        public static IZChipRuntime GetOrCreate(ProgrammableChip chip) =>
            Table.GetValue(chip, _ => new IZChipRuntime());

        public static bool TryGet(ProgrammableChip chip, out IZChipRuntime runtime) =>
            Table.TryGetValue(chip, out runtime);

        public static void Remove(ProgrammableChip chip) => Table.Remove(chip);

        // ------------------------------------------------------------------
        //  Lifecycle
        // ------------------------------------------------------------------

        /// <summary>
        /// Compiles the source and prepares a fresh VM. It always drops the previous
        /// state: editing the code restarts the program, which is what the player expects.
        /// </summary>
        public void Compile(string source, ICircuitHolder housing)
        {
            Vm = null;
            ErrorLine = 0;
            ErrorMessage = null;

            Compilation = IZCompiler.Compile(StripMarker(source));

            if (!Compilation.Success)
            {
                ErrorLine = Compilation.FirstErrorLine;
                ErrorMessage = FirstErrorMessage(Compilation);
                return;
            }

            Vm = new IZVm(Compilation.Program!, new HousingDeviceHost(housing));
        }

        /// <summary>
        /// Blanks out the marker line's content but keeps the line, so the compiler's
        /// line numbers keep matching the editor's.
        ///
        /// Public because the editor's error panel has to compile exactly the same text
        /// the chip will compile: two different paths would give different lines, and
        /// the player would see the error pointing at the wrong place.
        ///
        /// It searches for the line instead of assuming it is the first: the editor
        /// preserves blank lines at the start of the file, and a marker on line 2 is a
        /// perfectly valid IZ program - one that used to die with "unexpected character
        /// '#'".
        /// </summary>
        public static string StripMarker(string source)
        {
            int i = 0;
            while (i < source.Length)
            {
                int lineEnd = source.IndexOf('\n', i);
                if (lineEnd < 0) lineEnd = source.Length;

                string line = source.Substring(i, lineEnd - i);
                if (line.Trim().Length > 0)
                    return source.Substring(0, i) + new string(' ', line.Length) + source.Substring(lineEnd);

                i = lineEnd + 1;
            }
            return source;
        }

        private static string FirstErrorMessage(CompilationResult result)
        {
            foreach (var diagnostic in result.Diagnostics)
                if (diagnostic.IsError)
                    return diagnostic.Message;
            return "compile error";
        }

        /// <summary>Runs one tick. Returns the state, or null when there is nothing to run.</summary>
        public ExecutionResult? Tick(int budget)
        {
            if (Vm == null || HasError) return null;

            var state = Vm.Run(budget);

            if (state == ExecutionResult.Error && Vm.Error != null)
            {
                ErrorLine = Vm.Error.Line;
                ErrorMessage = Vm.Error.Message;
            }
            return state;
        }

        /// <summary>Restarts execution without recompiling.</summary>
        public void Restart()
        {
            if (Vm == null) return;
            Vm.Reset();
            // A compile error does not go away on restart; a runtime error does.
            if (Compilation?.Success == true)
            {
                ErrorLine = 0;
                ErrorMessage = null;
            }
        }

        public void Clear()
        {
            Vm = null;
            Compilation = null;
            ErrorLine = 0;
            ErrorMessage = null;
        }
    }
}
