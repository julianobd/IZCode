using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace IZLang.Vm
{
    /// <summary>A decoded instruction. A and B are operands; their meaning depends on the opcode.</summary>
    public readonly struct Instruction
    {
        public readonly OpCode Op;
        public readonly int A;
        public readonly int B;

        public Instruction(OpCode op, int a = 0, int b = 0)
        {
            Op = op;
            A = a;
            B = b;
        }

        public override string ToString() => Op + " " + A + " " + B;
    }

    /// <summary>Metadata for a compiled function.</summary>
    public sealed class FunctionInfo
    {
        public string Name { get; }
        /// <summary>Address of the first instruction in the program code.</summary>
        public int EntryPoint { get; }
        public int ParameterCount { get; }
        /// <summary>Total local slots, parameters included.</summary>
        public int LocalCount { get; }
        public bool ReturnsValue { get; }

        /// <summary>
        /// Heap cells the arrays and structs declared in this function take.
        /// Reserved on the call and released on the return, which is what keeps a
        /// recursive call from sharing its caller's arrays.
        /// </summary>
        public int HeapSize { get; }

        public FunctionInfo(string name, int entryPoint, int parameterCount, int localCount,
                            bool returnsValue, int heapSize = 0)
        {
            Name = name;
            EntryPoint = entryPoint;
            ParameterCount = parameterCount;
            LocalCount = localCount;
            ReturnsValue = returnsValue;
            HeapSize = heapSize;
        }
    }

    /// <summary>
    /// A compiled program: code, constant pool, function table and the instruction
    /// to source line map (used to report a runtime error on the right line of the
    /// editor).
    /// </summary>
    public sealed class IZProgram
    {
        public Instruction[] Code { get; }
        public double[] Constants { get; }
        public string[] Strings { get; }
        public FunctionInfo[] Functions { get; }
        public int GlobalCount { get; }
        public int MainFunctionIndex { get; }

        /// <summary>One-based source line of each instruction. Same length as Code.</summary>
        public int[] Lines { get; }

        public IZProgram(Instruction[] code, double[] constants, string[] strings,
                         FunctionInfo[] functions, int globalCount, int mainFunctionIndex, int[] lines)
        {
            Code = code ?? throw new ArgumentNullException(nameof(code));
            Constants = constants ?? throw new ArgumentNullException(nameof(constants));
            Strings = strings ?? throw new ArgumentNullException(nameof(strings));
            Functions = functions ?? throw new ArgumentNullException(nameof(functions));
            GlobalCount = globalCount;
            MainFunctionIndex = mainFunctionIndex;
            Lines = lines ?? throw new ArgumentNullException(nameof(lines));
        }

        public int GetLine(int instructionPointer) =>
            instructionPointer >= 0 && instructionPointer < Lines.Length ? Lines[instructionPointer] : 0;

        /// <summary>Human readable disassembly. Used by the tests and by the mod's debug command.</summary>
        public string Disassemble()
        {
            var sb = new StringBuilder();
            var entryPoints = new Dictionary<int, string>();
            foreach (var fn in Functions)
            {
                // Two functions never share an entry point, but an empty body can
                // collide with the start of the next one; concatenate so nothing is lost.
                entryPoints[fn.EntryPoint] = entryPoints.TryGetValue(fn.EntryPoint, out var existing)
                    ? existing + ", " + fn.Name
                    : fn.Name;
            }

            for (int i = 0; i < Code.Length; i++)
            {
                if (entryPoints.TryGetValue(i, out var name))
                    sb.Append("\n").Append(name).Append(":\n");

                var instruction = Code[i];
                sb.Append(i.ToString("D4")).Append("  ")
                  .Append("L").Append(Lines[i].ToString("D3")).Append("  ")
                  .Append(instruction.Op.ToString().PadRight(16));

                sb.Append(DescribeOperands(instruction));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private string DescribeOperands(Instruction instruction)
        {
            switch (instruction.Op)
            {
                case OpCode.PushConst:
                {
                    double value = instruction.A < Constants.Length ? Constants[instruction.A] : 0;
                    return instruction.A + "   ; " + value.ToString("R", CultureInfo.InvariantCulture);
                }
                case OpCode.Call:
                {
                    string name = instruction.A < Functions.Length ? Functions[instruction.A].Name : "?";
                    return instruction.A + " " + instruction.B + "   ; " + name;
                }
                case OpCode.CallBuiltin:
                    return instruction.A + " " + instruction.B + "   ; " + Builtins.GetName(instruction.A);

                case OpCode.PushStr:
                case OpCode.Trap:
                {
                    string text = instruction.A < Strings.Length ? Strings[instruction.A] : "?";
                    return instruction.A + "   ; \"" + text + "\"";
                }

                case OpCode.Jump:
                case OpCode.JumpIfFalse:
                case OpCode.JumpIfTrue:
                case OpCode.JumpIfFalseKeep:
                case OpCode.JumpIfTrueKeep:
                    return "-> " + instruction.A.ToString("D4");

                case OpCode.DeviceLoad:
                case OpCode.DeviceStore:
                    return "d" + instruction.A + " logic:" + instruction.B;

                case OpCode.LoadLocal:
                case OpCode.StoreLocal:
                case OpCode.LoadGlobal:
                case OpCode.StoreGlobal:
                case OpCode.FieldRef:
                    return instruction.A.ToString(CultureInfo.InvariantCulture);

                case OpCode.NewAggregate:
                    return "heap+" + instruction.A + " x" + instruction.B;

                case OpCode.IndexRef:
                    return "stride:" + instruction.A + " len:" + instruction.B;

                case OpCode.ListIndexRef:
                    return "stride:" + instruction.A + " cap:" + instruction.B;

                case OpCode.CopyHeap:
                case OpCode.ClearHeap:
                    return instruction.A + " cell(s)";

                default:
                    return instruction.A == 0 && instruction.B == 0
                        ? string.Empty
                        : instruction.A + " " + instruction.B;
            }
        }
    }
}
