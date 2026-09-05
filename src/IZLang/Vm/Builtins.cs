using System;
using System.Collections.Generic;

namespace IZLang.Vm
{
    /// <summary>
    /// Native functions. The id is baked into the bytecode, so existing values
    /// never change - a new function always goes at the end.
    /// </summary>
    public enum BuiltinId
    {
        Abs = 0,
        Ceil = 1,
        Floor = 2,
        Round = 3,
        Trunc = 4,
        Sqrt = 5,
        Exp = 6,
        Log = 7,
        Sin = 8,
        Cos = 9,
        Tan = 10,
        Asin = 11,
        Acos = 12,
        Atan = 13,
        Atan2 = 14,
        Min = 15,
        Max = 16,
        Rand = 17,
        Nan = 18,
        Inf = 19,
        IsNan = 20,
        Pow = 21,
        Sign = 22,
        Clamp = 23,

        // --- strings ---
        Len = 24,
        Hash = 25,
        Char = 26,
        Chr = 27,
        Sub = 28,
        Find = 29,
        Text = 30,
        Fixed = 31,
        Parse = 32,
        PackStr = 33,
        UnpackStr = 34,
    }

    /// <summary>
    /// The types a builtin talks about. It repeats three of the compiler's
    /// <c>IZType</c> kinds on purpose: <c>IZLang.Vm</c> knows nothing about the
    /// binder, and this table has to be readable by both sides.
    /// </summary>
    public enum BuiltinType
    {
        Num,
        Bool,
        Str,
    }

    public sealed class BuiltinInfo
    {
        private static readonly BuiltinType[] NoParameters = new BuiltinType[0];

        public BuiltinId Id { get; }
        public string Name { get; }
        public BuiltinType Returns { get; }
        public IReadOnlyList<BuiltinType> Parameters { get; }

        public int Arity => Parameters.Count;

        public BuiltinInfo(BuiltinId id, string name, BuiltinType returns, params BuiltinType[]? parameters)
        {
            Id = id;
            Name = name;
            Returns = returns;
            Parameters = parameters ?? NoParameters;
        }

        /// <summary>The name the player sees in a diagnostic or in the hover panel.</summary>
        public static string Label(BuiltinType type)
        {
            switch (type)
            {
                case BuiltinType.Bool: return "bool";
                case BuiltinType.Str: return "str";
                default: return "num";
            }
        }

        public string Signature()
        {
            var sb = new System.Text.StringBuilder(Name);
            sb.Append('(');
            for (int i = 0; i < Parameters.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Label(Parameters[i]));
            }
            sb.Append(") -> ").Append(Label(Returns));
            return sb.ToString();
        }
    }

    public static class Builtins
    {
        private const BuiltinType Num = BuiltinType.Num;
        private const BuiltinType Bool = BuiltinType.Bool;
        private const BuiltinType Str = BuiltinType.Str;

        private static readonly BuiltinInfo[] All =
        {
            new BuiltinInfo(BuiltinId.Abs,   "abs",   Num, Num),
            new BuiltinInfo(BuiltinId.Ceil,  "ceil",  Num, Num),
            new BuiltinInfo(BuiltinId.Floor, "floor", Num, Num),
            new BuiltinInfo(BuiltinId.Round, "round", Num, Num),
            new BuiltinInfo(BuiltinId.Trunc, "trunc", Num, Num),
            new BuiltinInfo(BuiltinId.Sqrt,  "sqrt",  Num, Num),
            new BuiltinInfo(BuiltinId.Exp,   "exp",   Num, Num),
            new BuiltinInfo(BuiltinId.Log,   "log",   Num, Num),
            new BuiltinInfo(BuiltinId.Sin,   "sin",   Num, Num),
            new BuiltinInfo(BuiltinId.Cos,   "cos",   Num, Num),
            new BuiltinInfo(BuiltinId.Tan,   "tan",   Num, Num),
            new BuiltinInfo(BuiltinId.Asin,  "asin",  Num, Num),
            new BuiltinInfo(BuiltinId.Acos,  "acos",  Num, Num),
            new BuiltinInfo(BuiltinId.Atan,  "atan",  Num, Num),
            new BuiltinInfo(BuiltinId.Atan2, "atan2", Num, Num, Num),
            new BuiltinInfo(BuiltinId.Min,   "min",   Num, Num, Num),
            new BuiltinInfo(BuiltinId.Max,   "max",   Num, Num, Num),
            new BuiltinInfo(BuiltinId.Rand,  "rand",  Num),
            new BuiltinInfo(BuiltinId.Nan,   "nan",   Num),
            new BuiltinInfo(BuiltinId.Inf,   "inf",   Num),
            new BuiltinInfo(BuiltinId.IsNan, "isnan", Bool, Num),
            new BuiltinInfo(BuiltinId.Pow,   "pow",   Num, Num, Num),
            new BuiltinInfo(BuiltinId.Sign,  "sign",  Num, Num),
            new BuiltinInfo(BuiltinId.Clamp, "clamp", Num, Num, Num, Num),

            // 'len' is also the length of an array, and that form is folded by the
            // compiler before it ever gets here - it is in the table so the editor
            // can complete it and describe it like any other builtin.
            new BuiltinInfo(BuiltinId.Len,   "len",   Num, Str),
            new BuiltinInfo(BuiltinId.Hash,  "hash",  Num, Str),
            new BuiltinInfo(BuiltinId.Char,  "char",  Num, Str, Num),
            new BuiltinInfo(BuiltinId.Chr,   "chr",   Str, Num),
            new BuiltinInfo(BuiltinId.Sub,   "sub",   Str, Str, Num, Num),
            new BuiltinInfo(BuiltinId.Find,  "find",  Num, Str, Str),
            new BuiltinInfo(BuiltinId.Text,  "text",  Str, Num),
            new BuiltinInfo(BuiltinId.Fixed, "fixed", Str, Num, Num),
            new BuiltinInfo(BuiltinId.Parse, "parse", Num, Str),

            // Up to six characters inside one number, the way a LED display in
            // DisplayMode.String reads its Setting back.
            new BuiltinInfo(BuiltinId.PackStr,   "packstr",   Num, Str),
            new BuiltinInfo(BuiltinId.UnpackStr, "unpackstr", Str, Num),
        };

        private static readonly Dictionary<string, BuiltinInfo> ByName = BuildIndex();

        private static Dictionary<string, BuiltinInfo> BuildIndex()
        {
            var map = new Dictionary<string, BuiltinInfo>(StringComparer.Ordinal);
            foreach (var info in All) map[info.Name] = info;
            return map;
        }

        public static bool TryGet(string name, out BuiltinInfo info) => ByName.TryGetValue(name, out info!);

        public static string GetName(int id)
        {
            foreach (var info in All)
                if ((int)info.Id == id) return info.Name;
            return "builtin#" + id;
        }

        public static IReadOnlyList<BuiltinInfo> AllBuiltins => All;
    }
}
