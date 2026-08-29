using System;
using System.Collections.Generic;
using IZLang.Diagnostics;

namespace IZLang.Binding
{
    /// <summary>The shape of a type. The details (element type, struct) live in <see cref="IZType"/>.</summary>
    public enum IZTypeKind
    {
        /// <summary>An error already happened; suppresses derived diagnostics.</summary>
        Error = 0,
        Void,
        Num,
        Bool,
        Str,
        Dev,
        /// <summary>Batch operation target (all(...) / named(...)).</summary>
        Batch,
        /// <summary>Fixed length array: num[8].</summary>
        Array,
        /// <summary>Fixed capacity list: list num[8] - an array plus how much of it is in use.</summary>
        List,
        /// <summary>An instance of a declared 'struct'.</summary>
        Struct,
    }

    /// <summary>
    /// A type in the IZ type system.
    ///
    /// The scalars are singletons, so comparing with '==' against <see cref="Num"/>
    /// and friends keeps working. Arrays and structs are built by the factories and
    /// compare structurally: two num[4] are the same type, num[4] and num[5] are not.
    /// </summary>
    public sealed class IZType
    {
        public IZTypeKind Kind { get; }

        /// <summary>Element type of an array; null otherwise.</summary>
        public IZType? ElementType { get; }

        /// <summary>Number of elements of an array; 0 otherwise.</summary>
        public int Length { get; }

        /// <summary>The declaration behind a struct type; null otherwise.</summary>
        public StructSymbol? Struct { get; }

        private IZType(IZTypeKind kind, IZType? elementType = null, int length = 0,
                       StructSymbol? structSymbol = null)
        {
            Kind = kind;
            ElementType = elementType;
            Length = length;
            Struct = structSymbol;
        }

        public static readonly IZType Error = new IZType(IZTypeKind.Error);
        public static readonly IZType Void = new IZType(IZTypeKind.Void);
        public static readonly IZType Num = new IZType(IZTypeKind.Num);
        public static readonly IZType Bool = new IZType(IZTypeKind.Bool);
        public static readonly IZType Str = new IZType(IZTypeKind.Str);
        public static readonly IZType Dev = new IZType(IZTypeKind.Dev);
        public static readonly IZType Batch = new IZType(IZTypeKind.Batch);

        public static IZType ArrayOf(IZType elementType, int length) =>
            new IZType(IZTypeKind.Array, elementType, length);

        /// <summary>
        /// A list of <paramref name="capacity"/> cells at most. <see cref="Length"/>
        /// is that capacity: how many are in use is the count cell, and only exists
        /// while the program runs.
        /// </summary>
        public static IZType ListOf(IZType elementType, int capacity) =>
            new IZType(IZTypeKind.List, elementType, capacity);

        public static IZType Of(StructSymbol structSymbol) =>
            new IZType(IZTypeKind.Struct, structSymbol: structSymbol);

        /// <summary>
        /// Does the value live in the heap? An aggregate is handled through a
        /// reference: the stack carries the address, never the contents.
        /// </summary>
        public bool IsAggregate =>
            Kind == IZTypeKind.Array || Kind == IZTypeKind.Struct || Kind == IZTypeKind.List;

        /// <summary>
        /// Where the first element sits, counted from the address of the value.
        /// A list opens with its count, so its items start one cell in.
        /// </summary>
        public int ItemsOffset => Kind == IZTypeKind.List ? 1 : 0;

        /// <summary>
        /// How many heap cells one value of this type takes. A scalar is 1: it shows
        /// up as an element of an array or as a field, never on its own.
        /// </summary>
        public int Size
        {
            get
            {
                switch (Kind)
                {
                    case IZTypeKind.Array: return Length * (ElementType?.Size ?? 1);
                    // One cell for the count, then the capacity.
                    case IZTypeKind.List: return 1 + Length * (ElementType?.Size ?? 1);
                    case IZTypeKind.Struct: return Struct?.Size ?? 0;
                    default: return 1;
                }
            }
        }

        public string Display()
        {
            switch (Kind)
            {
                case IZTypeKind.Void: return "nothing";
                case IZTypeKind.Num: return "num";
                case IZTypeKind.Bool: return "bool";
                case IZTypeKind.Str: return "str";
                case IZTypeKind.Dev: return "dev";
                case IZTypeKind.Batch: return "batch";
                case IZTypeKind.Array: return (ElementType?.Display() ?? "?") + "[" + Length + "]";
                case IZTypeKind.List: return "list " + (ElementType?.Display() ?? "?") + "[" + Length + "]";
                case IZTypeKind.Struct: return Struct?.Name ?? "?";
                default: return "?";
            }
        }

        /// <summary>
        /// Implicit conversion: bool becomes num (false=0, true=1), never the other way.
        /// The asymmetry is deliberate - it is what makes 'pump.On = p &lt; 100' work
        /// without letting 'if x' through when x is a num.
        ///
        /// An aggregate only goes where exactly the same aggregate is expected:
        /// num[4] is not num[5], and two structs match by declaration, not by shape.
        /// </summary>
        public bool IsAssignableTo(IZType to)
        {
            if (to is null) return false;
            if (Kind == IZTypeKind.Error || to.Kind == IZTypeKind.Error) return true;   // already reported
            if (Equals(to)) return true;
            if (Kind == IZTypeKind.Bool && to.Kind == IZTypeKind.Num) return true;
            return false;
        }

        public override bool Equals(object? obj)
        {
            if (!(obj is IZType other)) return false;
            if (ReferenceEquals(this, other)) return true;
            if (Kind != other.Kind) return false;

            switch (Kind)
            {
                case IZTypeKind.Array:
                case IZTypeKind.List:
                    return Length == other.Length &&
                           ElementType != null && ElementType.Equals(other.ElementType);
                case IZTypeKind.Struct:
                    return ReferenceEquals(Struct, other.Struct);
                default:
                    return true;
            }
        }

        public override int GetHashCode()
        {
            int hash = (int)Kind * 397;
            if (Kind == IZTypeKind.Array || Kind == IZTypeKind.List)
                hash ^= Length * 31 + (ElementType?.GetHashCode() ?? 0);
            if (Kind == IZTypeKind.Struct && Struct != null) hash ^= Struct.Name.GetHashCode();
            return hash;
        }

        public static bool operator ==(IZType? left, IZType? right) =>
            left is null ? right is null : left.Equals(right);

        public static bool operator !=(IZType? left, IZType? right) => !(left == right);

        public override string ToString() => Display();
    }

    public abstract class Symbol
    {
        public string Name { get; }

        /// <summary>Where the name was declared. This is the span the unused warning highlights.</summary>
        public SourceSpan DeclarationSpan { get; internal set; }

        /// <summary>
        /// Did anyone actually mention this name after it was declared?
        ///
        /// Set in <see cref="Scope.Lookup"/>, the only path through which a
        /// reference resolves a name - redeclaration goes through <see cref="Scope.LookupLocal"/>
        /// and deliberately does not set it.
        /// </summary>
        public bool IsUsed { get; internal set; }

        protected Symbol(string name) { Name = name; }
    }

    public sealed class VariableSymbol : Symbol
    {
        public IZType Type { get; internal set; }
        public bool IsConst { get; }
        public bool IsGlobal { get; }

        /// <summary>Index into the globals array, or the local slot inside the frame.</summary>
        public int Slot { get; internal set; }

        /// <summary>Value of a const folded at compile time. null for 'var'.</summary>
        public double? ConstantValue { get; internal set; }

        /// <summary>
        /// The text of a str const. It cannot ride in <see cref="ConstantValue"/>:
        /// a str is a handle into a table the VM only builds when it starts, so the
        /// compiler keeps the text and emits a fresh handle at each use.
        /// </summary>
        public string? ConstantString { get; internal set; }

        public VariableSymbol(string name, IZType type, bool isConst, bool isGlobal, int slot)
            : base(name)
        {
            Type = type;
            IsConst = isConst;
            IsGlobal = isGlobal;
            Slot = slot;
        }
    }

    public sealed class DeviceSymbol : Symbol
    {
        /// <summary>
        /// 0..5, matching d0..d5 on the housing, or <see cref="Vm.DevicePins.Housing"/>
        /// for 'db' - the device the chip is installed in.
        /// </summary>
        public int Pin { get; }

        public DeviceSymbol(string name, int pin) : base(name) { Pin = pin; }
    }

    /// <summary>One field of a struct, at a fixed offset from the start of the instance.</summary>
    public sealed class FieldSymbol
    {
        public string Name { get; }
        public IZType Type { get; }

        /// <summary>Cells between the start of the instance and this field.</summary>
        public int Offset { get; internal set; }

        public SourceSpan NameSpan { get; }

        public FieldSymbol(string name, IZType type, SourceSpan nameSpan)
        {
            Name = name;
            Type = type;
            NameSpan = nameSpan;
        }
    }

    /// <summary>
    /// A 'struct' declaration.
    ///
    /// The layout is flat: a struct field sits inline inside the outer struct, so
    /// an instance is always one run of cells and reading a nested field costs an
    /// addition, never a second indirection.
    /// </summary>
    public sealed class StructSymbol : Symbol
    {
        private readonly List<FieldSymbol> _fields = new List<FieldSymbol>();
        private readonly Dictionary<string, FieldSymbol> _byName =
            new Dictionary<string, FieldSymbol>(StringComparer.Ordinal);

        public StructSymbol(string name) : base(name) { }

        public IReadOnlyList<FieldSymbol> Fields => _fields;

        /// <summary>Total cells of one instance. Only final once the fields are resolved.</summary>
        public int Size { get; private set; }

        /// <summary>
        /// False while the field types have not been bound yet. A struct may name
        /// another one declared further down the file, so the fields are resolved in
        /// a second pass, after every name exists.
        /// </summary>
        public bool IsResolved { get; internal set; }

        /// <summary>Appends a field at the end of the layout. False when the name repeats.</summary>
        public bool TryAddField(FieldSymbol field)
        {
            if (_byName.ContainsKey(field.Name)) return false;
            field.Offset = Size;
            Size += field.Type.Size;
            _fields.Add(field);
            _byName[field.Name] = field;
            return true;
        }

        public FieldSymbol? FindField(string name) =>
            _byName.TryGetValue(name, out var field) ? field : null;
    }

    public sealed class ParameterSymbol
    {
        public string Name { get; }
        public IZType Type { get; }
        public int Slot { get; }

        public ParameterSymbol(string name, IZType type, int slot)
        {
            Name = name;
            Type = type;
            Slot = slot;
        }
    }

    public sealed class FunctionSymbol : Symbol
    {
        public List<ParameterSymbol> Parameters { get; }
        public IZType ReturnType { get; }

        /// <summary>Index into the compiled program's function table.</summary>
        public int Index { get; internal set; }

        public FunctionSymbol(string name, List<ParameterSymbol> parameters, IZType returnType, int index)
            : base(name)
        {
            Parameters = parameters;
            ReturnType = returnType;
            Index = index;
        }
    }

    /// <summary>
    /// Chained lexical scope. Names in an inner scope shadow the outer ones
    /// (shadowing is allowed), but redeclaring in the same scope is an error.
    /// </summary>
    public sealed class Scope
    {
        private readonly Dictionary<string, Symbol> _symbols = new Dictionary<string, Symbol>(StringComparer.Ordinal);

        public Scope? Parent { get; }

        public Scope(Scope? parent) { Parent = parent; }

        public bool TryDeclare(Symbol symbol) =>
            !_symbols.ContainsKey(symbol.Name) && Add(symbol);

        private bool Add(Symbol symbol)
        {
            _symbols[symbol.Name] = symbol;
            return true;
        }

        /// <summary>Looks up the scope chain and marks the symbol as used.</summary>
        public Symbol? Lookup(string name)
        {
            for (var scope = this; scope != null; scope = scope.Parent)
                if (scope._symbols.TryGetValue(name, out var symbol))
                {
                    symbol.IsUsed = true;
                    return symbol;
                }
            return null;
        }

        /// <summary>
        /// Looks up the chain without marking the symbol as used.
        ///
        /// For the questions the compiler asks itself - "is this name a device?",
        /// "what type would this be?" - before deciding how to read the expression.
        /// The real read comes later and is what counts as a use.
        /// </summary>
        public Symbol? LookupNoUse(string name)
        {
            for (var scope = this; scope != null; scope = scope.Parent)
                if (scope._symbols.TryGetValue(name, out var symbol))
                    return symbol;
            return null;
        }

        /// <summary>Looks only in the current scope - used to detect redeclaration.</summary>
        public Symbol? LookupLocal(string name) =>
            _symbols.TryGetValue(name, out var symbol) ? symbol : null;
    }
}
