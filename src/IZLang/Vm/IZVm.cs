using System;
using System.Globalization;
using IZLang.Binding;

namespace IZLang.Vm
{
    /// <summary>Runtime limits. They mirror IC10's order of magnitude, with room to spare.</summary>
    public static class IZLimits
    {
        /// <summary>Instructions executed per tick before automatic preemption.</summary>
        public const int DefaultOpsPerTick = 512;

        /// <summary>Maximum call stack depth.</summary>
        public const int MaxCallDepth = 64;

        /// <summary>Operand stack size.</summary>
        public const int OperandStackSize = 512;

        /// <summary>Total local slots across all live frames.</summary>
        public const int LocalStackSize = 1024;

        /// <summary>
        /// Cells available to arrays and structs, shared by every live frame.
        /// Each call reserves what its own declarations need and gives it back on
        /// the return, so this is the peak of one path down the call tree, not the
        /// sum of the whole program.
        /// </summary>
        public const int HeapSize = 2048;

        /// <summary>
        /// Slots in the string heap, the program's own literals included. Reaching
        /// this is what triggers a collection; only if nothing can be freed does the
        /// program stop with an error.
        /// </summary>
        public const int MaxStrings = 512;

        /// <summary>Characters in one string. A bound the error message can name beats an out of memory.</summary>
        public const int MaxStringLength = 256;
    }

    /// <summary>Why the VM gave control back.</summary>
    public enum ExecutionResult
    {
        /// <summary>Instruction budget exhausted. Resumes on the next tick.</summary>
        BudgetExhausted,
        /// <summary>Hit a 'yield'. Resumes on the next tick.</summary>
        Yielded,
        /// <summary>Sleeping until <see cref="IZVm.WakeTime"/>.</summary>
        Sleeping,
        /// <summary>The program finished normally.</summary>
        Halted,
        /// <summary>Runtime error; see <see cref="IZVm.Error"/>.</summary>
        Error,
    }

    public enum RuntimeErrorKind
    {
        None = 0,
        StackOverflow,
        StackUnderflow,
        CallStackOverflow,
        DeviceNotConnected,
        DeviceNotReadable,
        DeviceNotWritable,
        InvalidInstruction,
        LocalStackOverflow,
        HeapOverflow,
        IndexOutOfRange,
        StringOverflow,
        /// <summary>first() or last() over a query that matched nothing.</summary>
        EmptySequence,
    }

    public sealed class RuntimeError
    {
        public RuntimeErrorKind Kind { get; }
        public string Message { get; }
        public int Line { get; }
        public int InstructionPointer { get; }

        public RuntimeError(RuntimeErrorKind kind, string message, int line, int instructionPointer)
        {
            Kind = kind;
            Message = message;
            Line = line;
            InstructionPointer = instructionPointer;
        }

        public override string ToString() => "line " + Line + ": " + Message;
    }

    /// <summary>
    /// The IZ virtual machine.
    ///
    /// The central design point: <see cref="Run"/> executes at most
    /// <c>budget</c> instructions and returns. All state - instruction pointer,
    /// operand stack, frames and locals - lives in object fields, so the program
    /// simply carries on from where it stopped on the next call. That is what
    /// makes it possible to write an infinite loop without freezing the game, and
    /// what sets IZ apart from IC10 (which restarts from scratch every tick).
    /// </summary>
    public sealed class IZVm
    {
        private readonly IZProgram _program;
        private readonly IDeviceHost _host;
        private readonly Random _random;

        private readonly double[] _stack;
        private readonly double[] _locals;
        private readonly double[] _globals;
        private readonly double[] _heap;
        private readonly Frame[] _frames;
        private readonly StringHeap _strings;

        private int _stackTop;        // next free slot of the operand stack
        private int _localTop;        // next free slot of the local stack
        private int _heapTop;         // next free cell of the heap
        private int _frameCount;
        private int _ip;

        private struct Frame
        {
            public int ReturnAddress;
            public int LocalBase;
            public int LocalCount;
            public int StackBase;
            public int HeapBase;
            public bool ReturnsValue;
        }

        public IZVm(IZProgram program, IDeviceHost host, int? randomSeed = null)
        {
            _program = program ?? throw new ArgumentNullException(nameof(program));
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _random = randomSeed.HasValue ? new Random(randomSeed.Value) : new Random();

            _stack = new double[IZLimits.OperandStackSize];
            _locals = new double[IZLimits.LocalStackSize];
            _heap = new double[IZLimits.HeapSize];
            _globals = new double[Math.Max(1, program.GlobalCount)];
            _frames = new Frame[IZLimits.MaxCallDepth];
            _strings = new StringHeap(program.Strings);

            Reset();
        }

        /// <summary>Current state. While it is Error or Halted, Run does nothing.</summary>
        public ExecutionResult State { get; private set; }

        public RuntimeError? Error { get; private set; }

        /// <summary>The moment the sleep ends, when State == Sleeping.</summary>
        public double WakeTime { get; private set; }

        /// <summary>Instructions executed since the last <see cref="Reset"/>. Useful for diagnostics.</summary>
        public long TotalInstructions { get; private set; }

        public int InstructionPointer => _ip;
        public int CurrentLine => _program.GetLine(_ip);

        /// <summary>Puts the program back in its initial state, ready to run 'main' again.</summary>
        public void Reset()
        {
            Array.Clear(_stack, 0, _stack.Length);
            Array.Clear(_locals, 0, _locals.Length);
            Array.Clear(_globals, 0, _globals.Length);
            Array.Clear(_heap, 0, _heap.Length);
            _strings.Reset();

            _stackTop = 0;
            _localTop = 0;
            _heapTop = 0;
            _frameCount = 0;
            TotalInstructions = 0;
            Error = null;
            WakeTime = 0.0;

            int mainIndex = _program.MainFunctionIndex;
            if (mainIndex < 0 || mainIndex >= _program.Functions.Length)
            {
                State = ExecutionResult.Halted;
                _ip = 0;
                return;
            }

            var main = _program.Functions[mainIndex];
            _ip = main.EntryPoint;

            // Root frame for 'main'. ReturnAddress == -1 is the "end of program" marker.
            _frames[0] = new Frame
            {
                ReturnAddress = -1,
                LocalBase = 0,
                LocalCount = main.LocalCount,
                StackBase = 0,
                HeapBase = 0,
                ReturnsValue = false,
            };
            _frameCount = 1;
            _localTop = main.LocalCount;

            // The root frame holds the global arrays and structs; it only unwinds
            // when the program ends, so those live for the whole run.
            _heapTop = Math.Min(main.HeapSize, _heap.Length);

            State = ExecutionResult.BudgetExhausted;   // "ready to run"
        }

        /// <summary>
        /// Runs at most <paramref name="budget"/> instructions.
        /// Call it once per game tick.
        /// </summary>
        public ExecutionResult Run(int budget = IZLimits.DefaultOpsPerTick)
        {
            if (State == ExecutionResult.Halted || State == ExecutionResult.Error)
                return State;

            if (State == ExecutionResult.Sleeping)
            {
                if (_host.CurrentTime < WakeTime) return ExecutionResult.Sleeping;
                State = ExecutionResult.BudgetExhausted;
            }

            var code = _program.Code;

            for (int executed = 0; executed < budget; executed++)
            {
                if ((uint)_ip >= (uint)code.Length)
                    return Fail(RuntimeErrorKind.InvalidInstruction, "instruction pointer outside the program");

                var instruction = code[_ip++];
                TotalInstructions++;

                switch (instruction.Op)
                {
                    case OpCode.Nop:
                        break;

                    // ---- constants and stack ----
                    case OpCode.PushConst:
                        if (!Push(_program.Constants[instruction.A])) return State;
                        break;
                    case OpCode.PushZero:
                        if (!Push(0.0)) return State;
                        break;
                    case OpCode.PushOne:
                        if (!Push(1.0)) return State;
                        break;
                    case OpCode.Pop:
                        if (_stackTop <= 0) return Underflow();
                        _stackTop--;
                        break;
                    case OpCode.Dup:
                        if (_stackTop <= 0) return Underflow();
                        if (!Push(_stack[_stackTop - 1])) return State;
                        break;

                    // ---- variables ----
                    case OpCode.LoadLocal:
                        if (!Push(_locals[_frames[_frameCount - 1].LocalBase + instruction.A])) return State;
                        break;
                    case OpCode.StoreLocal:
                        if (_stackTop <= 0) return Underflow();
                        _locals[_frames[_frameCount - 1].LocalBase + instruction.A] = _stack[--_stackTop];
                        break;
                    case OpCode.LoadGlobal:
                        if (!Push(_globals[instruction.A])) return State;
                        break;
                    case OpCode.StoreGlobal:
                        if (_stackTop <= 0) return Underflow();
                        _globals[instruction.A] = _stack[--_stackTop];
                        break;

                    // ---- arithmetic ----
                    case OpCode.Add:
                        if (!Binary(out double la, out double ra)) return State;
                        _stack[_stackTop++] = la + ra;
                        break;
                    case OpCode.Subtract:
                        if (!Binary(out double ls, out double rs)) return State;
                        _stack[_stackTop++] = ls - rs;
                        break;
                    case OpCode.Multiply:
                        if (!Binary(out double lm, out double rm)) return State;
                        _stack[_stackTop++] = lm * rm;
                        break;
                    case OpCode.Divide:
                        // Division by zero follows IEEE-754 (inf/nan), just like IC10 -
                        // it is not a runtime error.
                        if (!Binary(out double ld, out double rd)) return State;
                        _stack[_stackTop++] = ld / rd;
                        break;
                    case OpCode.Modulo:
                        if (!Binary(out double lmo, out double rmo)) return State;
                        _stack[_stackTop++] = Modulo(lmo, rmo);
                        break;
                    case OpCode.Negate:
                        if (_stackTop <= 0) return Underflow();
                        _stack[_stackTop - 1] = -_stack[_stackTop - 1];
                        break;

                    // ---- bitwise ----
                    case OpCode.BitAnd:
                        if (!Binary(out double lba, out double rba)) return State;
                        _stack[_stackTop++] = ToDouble(ToInt64(lba) & ToInt64(rba));
                        break;
                    case OpCode.BitOr:
                        if (!Binary(out double lbo, out double rbo)) return State;
                        _stack[_stackTop++] = ToDouble(ToInt64(lbo) | ToInt64(rbo));
                        break;
                    case OpCode.BitXor:
                        if (!Binary(out double lbx, out double rbx)) return State;
                        _stack[_stackTop++] = ToDouble(ToInt64(lbx) ^ ToInt64(rbx));
                        break;
                    case OpCode.BitNot:
                        if (_stackTop <= 0) return Underflow();
                        _stack[_stackTop - 1] = ToDouble(~ToInt64(_stack[_stackTop - 1]));
                        break;
                    case OpCode.ShiftLeft:
                        if (!Binary(out double lsl, out double rsl)) return State;
                        _stack[_stackTop++] = ToDouble(ToInt64(lsl) << (int)(ToInt64(rsl) & 63));
                        break;
                    case OpCode.ShiftRight:
                        if (!Binary(out double lsr, out double rsr)) return State;
                        _stack[_stackTop++] = ToDouble(ToInt64(lsr) >> (int)(ToInt64(rsr) & 63));
                        break;

                    // ---- comparison ----
                    case OpCode.Equal:
                        if (!Binary(out double le, out double re)) return State;
                        _stack[_stackTop++] = le == re ? 1.0 : 0.0;
                        break;
                    case OpCode.NotEqual:
                        if (!Binary(out double lne, out double rne)) return State;
                        _stack[_stackTop++] = lne != rne ? 1.0 : 0.0;
                        break;
                    case OpCode.Less:
                        if (!Binary(out double ll, out double rl)) return State;
                        _stack[_stackTop++] = ll < rl ? 1.0 : 0.0;
                        break;
                    case OpCode.LessEqual:
                        if (!Binary(out double lle, out double rle)) return State;
                        _stack[_stackTop++] = lle <= rle ? 1.0 : 0.0;
                        break;
                    case OpCode.Greater:
                        if (!Binary(out double lg, out double rg)) return State;
                        _stack[_stackTop++] = lg > rg ? 1.0 : 0.0;
                        break;
                    case OpCode.GreaterEqual:
                        if (!Binary(out double lge, out double rge)) return State;
                        _stack[_stackTop++] = lge >= rge ? 1.0 : 0.0;
                        break;
                    case OpCode.Not:
                        if (_stackTop <= 0) return Underflow();
                        _stack[_stackTop - 1] = IsTruthy(_stack[_stackTop - 1]) ? 0.0 : 1.0;
                        break;

                    // ---- control flow ----
                    case OpCode.Jump:
                        _ip = instruction.A;
                        break;
                    case OpCode.JumpIfFalse:
                        if (_stackTop <= 0) return Underflow();
                        if (!IsTruthy(_stack[--_stackTop])) _ip = instruction.A;
                        break;
                    case OpCode.JumpIfTrue:
                        if (_stackTop <= 0) return Underflow();
                        if (IsTruthy(_stack[--_stackTop])) _ip = instruction.A;
                        break;
                    case OpCode.JumpIfFalseKeep:
                        if (_stackTop <= 0) return Underflow();
                        if (!IsTruthy(_stack[_stackTop - 1])) _ip = instruction.A; else _stackTop--;
                        break;
                    case OpCode.JumpIfTrueKeep:
                        if (_stackTop <= 0) return Underflow();
                        if (IsTruthy(_stack[_stackTop - 1])) _ip = instruction.A; else _stackTop--;
                        break;

                    // ---- functions ----
                    case OpCode.Call:
                        if (!DoCall(instruction.A, instruction.B)) return State;
                        break;
                    case OpCode.Return:
                        if (!DoReturn(false)) return State;
                        if (State == ExecutionResult.Halted) return State;
                        break;
                    case OpCode.ReturnValue:
                        if (!DoReturn(true)) return State;
                        if (State == ExecutionResult.Halted) return State;
                        break;
                    case OpCode.CallBuiltin:
                        if (!DoBuiltin((BuiltinId)instruction.A, instruction.B)) return State;
                        break;

                    // ---- devices ----
                    case OpCode.DeviceLoad:
                    {
                        if (!_host.TryReadDevice(instruction.A, instruction.B, out double value))
                            return Fail(RuntimeErrorKind.DeviceNotConnected,
                                NotConnected(instruction.A));
                        if (!Push(value)) return State;
                        break;
                    }
                    case OpCode.DeviceStore:
                    {
                        if (_stackTop <= 0) return Underflow();
                        double value = _stack[--_stackTop];
                        if (!_host.TryWriteDevice(instruction.A, instruction.B, value))
                            return Fail(RuntimeErrorKind.DeviceNotConnected,
                                NotConnected(instruction.A));
                        break;
                    }
                    case OpCode.DevicePresent:
                        if (!Push(_host.IsDeviceConnected(instruction.A) ? 1.0 : 0.0)) return State;
                        break;
                    case OpCode.DeviceSlotLoad:
                    {
                        if (_stackTop <= 0) return Underflow();
                        int slotIndex = (int)_stack[--_stackTop];
                        if (!_host.TryReadSlot(instruction.A, slotIndex, instruction.B, out double value))
                            return Fail(RuntimeErrorKind.DeviceNotConnected,
                                NotConnected(instruction.A));
                        if (!Push(value)) return State;
                        break;
                    }
                    case OpCode.BatchLoad:
                    {
                        if (_stackTop <= 0) return Underflow();
                        double prefabHash = _stack[--_stackTop];
                        _host.TryBatchRead(prefabHash, instruction.A, (BatchAggregation)instruction.B, out double value);
                        if (!Push(value)) return State;
                        break;
                    }
                    case OpCode.BatchStore:
                    {
                        if (_stackTop < 2) return Underflow();
                        double value = _stack[--_stackTop];
                        double prefabHash = _stack[--_stackTop];
                        _host.BatchWrite(prefabHash, instruction.A, value);
                        break;
                    }
                    case OpCode.BatchNamedLoad:
                    {
                        if (_stackTop < 2) return Underflow();
                        double nameHash = _stack[--_stackTop];
                        double prefabHash = _stack[--_stackTop];
                        _host.TryBatchNamedRead(prefabHash, nameHash, instruction.A,
                            (BatchAggregation)instruction.B, out double value);
                        if (!Push(value)) return State;
                        break;
                    }
                    case OpCode.BatchNamedStore:
                    {
                        if (_stackTop < 3) return Underflow();
                        double value = _stack[--_stackTop];
                        double nameHash = _stack[--_stackTop];
                        double prefabHash = _stack[--_stackTop];
                        _host.BatchNamedWrite(prefabHash, nameHash, instruction.A, value);
                        break;
                    }
                    case OpCode.BatchSlotLoad:
                    {
                        if (_stackTop < 2) return Underflow();
                        int slotIndex = (int)_stack[--_stackTop];
                        double prefabHash = _stack[--_stackTop];
                        // Like every batch read: nothing matched is 0, not a stop. The
                        // pin form errors because an empty pin is a wiring mistake; a
                        // selector that matches nothing is an ordinary answer.
                        _host.TryBatchSlotRead(prefabHash, slotIndex, instruction.A,
                                               (BatchAggregation)instruction.B, out double value);
                        if (!Push(value)) return State;
                        break;
                    }
                    case OpCode.BatchNamedSlotLoad:
                    {
                        if (_stackTop < 3) return Underflow();
                        int slotIndex = (int)_stack[--_stackTop];
                        double nameHash = _stack[--_stackTop];
                        double prefabHash = _stack[--_stackTop];
                        _host.TryBatchNamedSlotRead(prefabHash, nameHash, slotIndex, instruction.A,
                                                    (BatchAggregation)instruction.B, out double value);
                        if (!Push(value)) return State;
                        break;
                    }

                    // ---- heap: arrays and structs ----
                    case OpCode.NewAggregate:
                    {
                        int address = _frames[_frameCount - 1].HeapBase + instruction.A;
                        if (address < 0 || address + instruction.B > _heap.Length)
                            return Fail(RuntimeErrorKind.HeapOverflow, "ran out of memory for arrays and structs");
                        Array.Clear(_heap, address, instruction.B);
                        if (!Push(address)) return State;
                        break;
                    }
                    case OpCode.FieldRef:
                        if (_stackTop <= 0) return Underflow();
                        _stack[_stackTop - 1] += instruction.A;
                        break;
                    case OpCode.IndexRef:
                    {
                        if (_stackTop < 2) return Underflow();
                        double rawIndex = _stack[--_stackTop];
                        double baseAddress = _stack[_stackTop - 1];

                        // Truncated, not rounded: a[1.9] is a[1], the same way a
                        // slot index behaves. NaN falls outside the range and is caught.
                        double index = Math.Truncate(rawIndex);
                        if (!(index >= 0.0) || index >= instruction.B)
                            return Fail(RuntimeErrorKind.IndexOutOfRange,
                                "index " + rawIndex.ToString(CultureInfo.InvariantCulture) +
                                " is outside the array, which holds " + instruction.B);

                        _stack[_stackTop - 1] = baseAddress + index * instruction.A;
                        break;
                    }
                    case OpCode.LoadHeap:
                    {
                        if (_stackTop <= 0) return Underflow();
                        int address = (int)_stack[_stackTop - 1];
                        if ((uint)address >= (uint)_heap.Length)
                            return Fail(RuntimeErrorKind.IndexOutOfRange, "read outside the heap");
                        _stack[_stackTop - 1] = _heap[address];
                        break;
                    }
                    case OpCode.StoreHeap:
                    {
                        if (_stackTop < 2) return Underflow();
                        double value = _stack[--_stackTop];
                        int address = (int)_stack[--_stackTop];
                        if ((uint)address >= (uint)_heap.Length)
                            return Fail(RuntimeErrorKind.IndexOutOfRange, "write outside the heap");
                        _heap[address] = value;
                        break;
                    }

                    // ---- lists ----
                    case OpCode.ListIndexRef:
                    {
                        if (_stackTop < 2) return Underflow();
                        double rawIndex = _stack[--_stackTop];
                        int listAddress = (int)_stack[_stackTop - 1];

                        if ((uint)listAddress >= (uint)_heap.Length)
                            return Fail(RuntimeErrorKind.IndexOutOfRange, "read outside the heap");

                        double count = _heap[listAddress];
                        double index = Math.Truncate(rawIndex);

                        // The count is the bound, not the capacity: the cells past it
                        // are room, not content.
                        if (!(index >= 0.0) || index >= count)
                            return Fail(RuntimeErrorKind.IndexOutOfRange,
                                "index " + rawIndex.ToString(CultureInfo.InvariantCulture) +
                                " is outside the list, which holds " +
                                count.ToString(CultureInfo.InvariantCulture));

                        _stack[_stackTop - 1] = listAddress + 1 + index * instruction.A;
                        break;
                    }
                    case OpCode.CopyHeap:
                    {
                        if (_stackTop < 2) return Underflow();
                        int source = (int)_stack[--_stackTop];
                        int destination = (int)_stack[--_stackTop];

                        if ((uint)source > (uint)(_heap.Length - instruction.A) ||
                            (uint)destination > (uint)(_heap.Length - instruction.A))
                            return Fail(RuntimeErrorKind.IndexOutOfRange, "copy outside the heap");

                        Array.Copy(_heap, source, _heap, destination, instruction.A);
                        break;
                    }
                    case OpCode.ClearHeap:
                    {
                        if (_stackTop <= 0) return Underflow();
                        int target = (int)_stack[--_stackTop];

                        if ((uint)target > (uint)(_heap.Length - instruction.A))
                            return Fail(RuntimeErrorKind.IndexOutOfRange, "clear outside the heap");

                        Array.Clear(_heap, target, instruction.A);
                        break;
                    }
                    case OpCode.Trap:
                    {
                        var messages = _program.Strings;
                        return Fail(RuntimeErrorKind.EmptySequence,
                            (uint)instruction.A < (uint)messages.Length
                                ? messages[instruction.A]
                                : "the program stopped");
                    }

                    // ---- strings ----
                    case OpCode.PushStr:
                    {
                        var strings = _program.Strings;
                        if ((uint)instruction.A >= (uint)strings.Length)
                            return Fail(RuntimeErrorKind.InvalidInstruction,
                                "invalid string index: " + instruction.A);
                        if (!Push(StrValue.FromIndex(instruction.A))) return State;
                        break;
                    }
                    case OpCode.StrConcat:
                    {
                        if (_stackTop < 2) return Underflow();
                        // Both are read out as text before anything is allocated: after
                        // this the two handles are gone, and the collector is free to
                        // take their slots back.
                        string right = _strings.Read(_stack[--_stackTop]);
                        string left = _strings.Read(_stack[--_stackTop]);
                        if (!PushString(left + right)) return State;
                        break;
                    }
                    case OpCode.StrCompare:
                    {
                        if (_stackTop < 2) return Underflow();
                        string rightText = _strings.Read(_stack[--_stackTop]);
                        string leftText = _strings.Read(_stack[--_stackTop]);
                        if (!Push(Math.Sign(string.CompareOrdinal(leftText, rightText)))) return State;
                        break;
                    }

                    // ---- tick control ----
                    case OpCode.Yield:
                        State = ExecutionResult.Yielded;
                        return State;

                    case OpCode.Sleep:
                    {
                        if (_stackTop <= 0) return Underflow();
                        double seconds = _stack[--_stackTop];
                        WakeTime = _host.CurrentTime + Math.Max(0.0, seconds);
                        State = ExecutionResult.Sleeping;
                        return State;
                    }

                    case OpCode.Halt:
                        State = ExecutionResult.Halted;
                        return State;

                    default:
                        return Fail(RuntimeErrorKind.InvalidInstruction,
                            "unknown opcode: " + instruction.Op);
                }
            }

            State = ExecutionResult.BudgetExhausted;
            return State;
        }

        // ==================================================================
        //  Helpers
        // ==================================================================

        /// <summary>False is exactly 0.0. NaN is true, like any other non-zero.</summary>
        private static bool IsTruthy(double value) => value != 0.0;

        /// <summary>
        /// Remainder that takes the divisor's sign (divisor-signed modulo), as in IC10 -
        /// not the dividend-signed remainder of C#'s '%' operator.
        /// </summary>
        private static double Modulo(double left, double right)
        {
            if (right == 0.0) return double.NaN;
            double result = left % right;
            if (result != 0.0 && (result < 0.0) != (right < 0.0)) result += right;
            return result;
        }

        private static long ToInt64(double value)
        {
            if (double.IsNaN(value)) return 0L;
            if (value >= 9.2233720368547758E18) return long.MaxValue;
            if (value <= -9.2233720368547758E18) return long.MinValue;
            return (long)value;
        }

        private static double ToDouble(long value) => value;

        private bool Push(double value)
        {
            if (_stackTop >= _stack.Length)
            {
                Fail(RuntimeErrorKind.StackOverflow, "operand stack overflow");
                return false;
            }
            _stack[_stackTop++] = value;
            return true;
        }

        /// <summary>
        /// Interns the text and pushes its handle.
        ///
        /// The table only fills up when a program really does build new text every
        /// tick - the same text always lands back on the same slot. When it does fill
        /// up, one collection runs and the allocation is tried again; a second
        /// failure means everything in there is genuinely reachable.
        /// </summary>
        private bool PushString(string text)
        {
            if (text.Length > IZLimits.MaxStringLength)
            {
                Fail(RuntimeErrorKind.StringOverflow,
                    "a string went past " + IZLimits.MaxStringLength + " characters");
                return false;
            }

            if (!_strings.TryIntern(text, out double handle))
            {
                CollectStrings();
                if (!_strings.TryIntern(text, out handle))
                {
                    Fail(RuntimeErrorKind.StringOverflow,
                        "ran out of room for strings; " + IZLimits.MaxStrings +
                        " are alive at the same time");
                    return false;
                }
            }

            return Push(handle);
        }

        /// <summary>
        /// Frees every runtime string nobody points at any more.
        ///
        /// The roots are the four places a value can be: the live part of the operand
        /// stack, of the local stack and of the heap, plus the globals. There is
        /// nowhere else a handle can hide, which is what makes the sweep exact -
        /// <see cref="StrValue"/> is what tells a handle from a number inside arrays
        /// that carry no type tag.
        /// </summary>
        private void CollectStrings()
        {
            _strings.BeginMark();
            _strings.Mark(_stack, _stackTop);
            _strings.Mark(_locals, _localTop);
            _strings.Mark(_heap, _heapTop);
            _strings.Mark(_globals, _globals.Length);
            _strings.Sweep();
        }

        /// <summary>Pops two operands. The caller pushes the result straight into _stack[_stackTop++].</summary>
        private bool Binary(out double left, out double right)
        {
            if (_stackTop < 2)
            {
                left = right = 0.0;
                Underflow();
                return false;
            }
            right = _stack[--_stackTop];
            left = _stack[--_stackTop];
            return true;
        }

        private bool DoCall(int functionIndex, int argumentCount)
        {
            if ((uint)functionIndex >= (uint)_program.Functions.Length)
            {
                Fail(RuntimeErrorKind.InvalidInstruction, "invalid function index: " + functionIndex);
                return false;
            }
            if (_frameCount >= _frames.Length)
            {
                Fail(RuntimeErrorKind.CallStackOverflow,
                    "recursion went past " + IZLimits.MaxCallDepth + " levels");
                return false;
            }

            var function = _program.Functions[functionIndex];

            if (_localTop + function.LocalCount > _locals.Length)
            {
                Fail(RuntimeErrorKind.LocalStackOverflow, "ran out of local variable slots");
                return false;
            }
            if (_heapTop + function.HeapSize > _heap.Length)
            {
                Fail(RuntimeErrorKind.HeapOverflow,
                    "ran out of memory for arrays and structs; " + function.Name +
                    " needs " + function.HeapSize + " more cells");
                return false;
            }
            if (_stackTop < argumentCount)
            {
                Underflow();
                return false;
            }

            int localBase = _localTop;

            // Arguments leave the operand stack and become the first locals.
            for (int i = argumentCount - 1; i >= 0; i--)
                _locals[localBase + i] = _stack[--_stackTop];

            // Locals that are not parameters start out zeroed.
            for (int i = argumentCount; i < function.LocalCount; i++)
                _locals[localBase + i] = 0.0;

            _localTop += function.LocalCount;

            int heapBase = _heapTop;
            _heapTop += function.HeapSize;

            _frames[_frameCount++] = new Frame
            {
                ReturnAddress = _ip,
                LocalBase = localBase,
                LocalCount = function.LocalCount,
                StackBase = _stackTop,
                HeapBase = heapBase,
                ReturnsValue = function.ReturnsValue,
            };

            _ip = function.EntryPoint;
            return true;
        }

        private bool DoReturn(bool withValue)
        {
            if (_frameCount <= 0)
            {
                Fail(RuntimeErrorKind.StackUnderflow, "return with no call frame");
                return false;
            }

            var frame = _frames[_frameCount - 1];

            double result = 0.0;
            if (withValue)
            {
                if (_stackTop <= 0) { Underflow(); return false; }
                result = _stack[--_stackTop];
            }

            // Drop whatever the function left on the stack: the one leaving cleans up its own mess.
            // Its heap region goes the same way, which is why a function may not hand
            // back an array or a struct - the compiler refuses that at compile time.
            _stackTop = frame.StackBase;
            _localTop = frame.LocalBase;
            _heapTop = frame.HeapBase;
            _frameCount--;

            if (frame.ReturnAddress < 0)
            {
                // Returned from 'main': the program is over.
                State = ExecutionResult.Halted;
                return true;
            }

            _ip = frame.ReturnAddress;

            if (frame.ReturnsValue && !Push(result)) return false;
            return true;
        }

        private bool DoBuiltin(BuiltinId id, int argumentCount)
        {
            if (_stackTop < argumentCount)
            {
                Underflow();
                return false;
            }

            double a = argumentCount > 0 ? _stack[_stackTop - argumentCount] : 0.0;
            double b = argumentCount > 1 ? _stack[_stackTop - argumentCount + 1] : 0.0;
            double c = argumentCount > 2 ? _stack[_stackTop - argumentCount + 2] : 0.0;
            _stackTop -= argumentCount;

            double result;
            switch (id)
            {
                case BuiltinId.Abs: result = Math.Abs(a); break;
                case BuiltinId.Ceil: result = Math.Ceiling(a); break;
                case BuiltinId.Floor: result = Math.Floor(a); break;
                case BuiltinId.Round: result = Math.Round(a, MidpointRounding.AwayFromZero); break;
                case BuiltinId.Trunc: result = Math.Truncate(a); break;
                case BuiltinId.Sqrt: result = Math.Sqrt(a); break;
                case BuiltinId.Exp: result = Math.Exp(a); break;
                case BuiltinId.Log: result = Math.Log(a); break;
                case BuiltinId.Sin: result = Math.Sin(a); break;
                case BuiltinId.Cos: result = Math.Cos(a); break;
                case BuiltinId.Tan: result = Math.Tan(a); break;
                case BuiltinId.Asin: result = Math.Asin(a); break;
                case BuiltinId.Acos: result = Math.Acos(a); break;
                case BuiltinId.Atan: result = Math.Atan(a); break;
                case BuiltinId.Atan2: result = Math.Atan2(a, b); break;
                case BuiltinId.Min: result = Math.Min(a, b); break;
                case BuiltinId.Max: result = Math.Max(a, b); break;
                case BuiltinId.Rand: result = _random.NextDouble(); break;
                case BuiltinId.Nan: result = double.NaN; break;
                case BuiltinId.Inf: result = double.PositiveInfinity; break;
                case BuiltinId.IsNan: result = double.IsNaN(a) ? 1.0 : 0.0; break;
                case BuiltinId.Pow: result = Math.Pow(a, b); break;
                case BuiltinId.Sign: result = Math.Sign(a); break;
                case BuiltinId.Clamp: result = a < b ? b : (a > c ? c : a); break;

                // ---- strings ----
                // The str arguments arrive as handles in a, b and c, like any other
                // value; Read turns them back into text.
                case BuiltinId.Len: result = _strings.Read(a).Length; break;
                case BuiltinId.Hash: result = PrefabHash.Compute(_strings.Read(a)); break;
                case BuiltinId.Char: result = CharAt(_strings.Read(a), b); break;
                case BuiltinId.Find: result = Find(_strings.Read(a), _strings.Read(b)); break;
                case BuiltinId.Parse: result = Parse(_strings.Read(a)); break;

                // These build text instead of a number, so they push it themselves.
                case BuiltinId.Chr: return PushString(FromCharCode(a));
                case BuiltinId.Sub: return PushString(Substring(_strings.Read(a), b, c));
                case BuiltinId.Text: return PushString(FormatNumber(a));
                case BuiltinId.Fixed: return PushString(FormatFixed(a, b));

                default:
                    Fail(RuntimeErrorKind.InvalidInstruction, "unknown builtin: " + (int)id);
                    return false;
            }

            return Push(result);
        }

        /// <summary>ASCII code at an index; -1 when the index is outside the string.</summary>
        private static double CharAt(string text, double rawIndex)
        {
            double index = Math.Truncate(rawIndex);
            if (!(index >= 0.0) || index >= text.Length) return -1.0;
            return text[(int)index];
        }

        /// <summary>
        /// One character from its ASCII code. Anything outside printable ASCII gives
        /// the empty string: the source is ASCII, and a control character in a label
        /// would only ever be a mistake.
        /// </summary>
        private static string FromCharCode(double rawCode)
        {
            double code = Math.Truncate(rawCode);
            if (!(code >= 32.0) || code > 126.0) return string.Empty;
            return ((char)(int)code).ToString();
        }

        /// <summary>Where the needle starts, or -1. An empty needle is at 0, as everywhere else.</summary>
        private static double Find(string text, string needle) =>
            needle.Length == 0 ? 0.0 : text.IndexOf(needle, StringComparison.Ordinal);

        /// <summary>Substring with both bounds clamped, so no index is ever an error.</summary>
        private static string Substring(string text, double rawStart, double rawCount)
        {
            double start = Math.Truncate(rawStart);
            if (double.IsNaN(start) || start < 0.0) start = 0.0;
            if (start > text.Length) start = text.Length;

            double count = Math.Truncate(rawCount);
            if (double.IsNaN(count) || count < 0.0) count = 0.0;
            if (count > text.Length - start) count = text.Length - start;

            return text.Substring((int)start, (int)count);
        }

        /// <summary>
        /// The shortest form a player would write by hand: no exponent for the usual
        /// range, no trailing zeros. nan and inf come out as the builtins that make them.
        /// </summary>
        private static string FormatNumber(double value)
        {
            if (double.IsNaN(value)) return "nan";
            if (double.IsPositiveInfinity(value)) return "inf";
            if (double.IsNegativeInfinity(value)) return "-inf";
            return value.ToString("0.######", CultureInfo.InvariantCulture);
        }

        private static string FormatFixed(double value, double rawDecimals)
        {
            if (double.IsNaN(value)) return "nan";
            if (double.IsPositiveInfinity(value)) return "inf";
            if (double.IsNegativeInfinity(value)) return "-inf";

            double decimals = Math.Truncate(rawDecimals);
            if (double.IsNaN(decimals) || decimals < 0.0) decimals = 0.0;
            if (decimals > 8.0) decimals = 8.0;

            return value.ToString("F" + (int)decimals, CultureInfo.InvariantCulture);
        }

        /// <summary>Text back into a number. What is not a number is nan, never an error.</summary>
        private static double Parse(string text) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? value
                : double.NaN;

        /// <summary>
        /// 'db' fails for a different reason than a pin does - there is no cable to
        /// check - so it gets its own wording instead of "pin db is empty".
        /// </summary>
        private static string NotConnected(int pin) =>
            pin == DevicePins.Housing
                ? "the chip is not installed in a device, so 'db' reads nothing"
                : "no device connected on pin " + DevicePins.Name(pin);

        private ExecutionResult Underflow() =>
            Fail(RuntimeErrorKind.StackUnderflow, "empty stack (a compiler bug, not a bug in your code)");

        private ExecutionResult Fail(RuntimeErrorKind kind, string message)
        {
            // _ip has already advanced; the error belongs to the instruction that just ran.
            int faultingIp = Math.Max(0, _ip - 1);
            Error = new RuntimeError(kind, message, _program.GetLine(faultingIp), faultingIp);
            State = ExecutionResult.Error;
            return State;
        }

        // ---- inspection, for tests and for the mod's debug panel ----

        public int StackDepth => _stackTop;

        public double PeekStack(int offsetFromTop = 0)
        {
            int index = _stackTop - 1 - offsetFromTop;
            return index >= 0 && index < _stack.Length ? _stack[index] : 0.0;
        }

        public double GetGlobal(int index) =>
            index >= 0 && index < _globals.Length ? _globals[index] : 0.0;

        /// <summary>Cells of the heap in use right now. Diagnostics only.</summary>
        public int HeapUsed => _heapTop;

        public double GetHeap(int address) =>
            address >= 0 && address < _heap.Length ? _heap[address] : 0.0;

        /// <summary>
        /// The text behind a str value - what the hover panel and the tests need to
        /// see a string, since every slot the VM exposes is a bare double.
        /// </summary>
        public string ReadString(double value) => _strings.Read(value);

        /// <summary>Strings alive right now, the program's own literals included.</summary>
        public int StringsUsed => _strings.Count;
    }
}
