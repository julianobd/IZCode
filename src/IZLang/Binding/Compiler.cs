using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using IZLang.Diagnostics;
using IZLang.Lexing;
using IZLang.Parsing;
using IZLang.Vm;

namespace IZLang.Binding
{
    /// <summary>
    /// Resolves names, checks types and emits bytecode in a single pass over the AST.
    ///
    /// Before that it runs a pre-pass that registers every top-level declaration
    /// (globals, devices and function signatures), so a function can call another
    /// one declared further down the file.
    /// </summary>
    public sealed partial class Compiler
    {
        private const int MaxGlobals = 256;
        private const int MaxLocalsPerFunction = 128;
        private const int MaxConstants = 1024;

        /// <summary>Distinct string literals one program may carry.</summary>
        private const int MaxStrings = 256;

        /// <summary>
        /// Heap cells one function may declare. The VM has more than this, but the
        /// budget is per function: what nests on top of it still has to fit.
        /// </summary>
        private const int MaxHeapPerFunction = 1024;

        /// <summary>Elements one array may hold. A bound the message can name beats a heap overflow at runtime.</summary>
        private const int MaxArrayLength = 1024;

        private readonly DiagnosticBag _diagnostics;

        private readonly List<Instruction> _code = new List<Instruction>();
        private readonly List<int> _lines = new List<int>();
        private readonly List<double> _constants = new List<double>();
        private readonly Dictionary<double, int> _constantIndex = new Dictionary<double, int>();
        private readonly List<string> _strings = new List<string>();
        private readonly Dictionary<string, int> _stringIndex =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<FunctionInfo> _functions = new List<FunctionInfo>();

        private readonly Scope _globalScope;
        private Scope _scope;

        /// <summary>
        /// Every data name declared in the program, in the order it appeared.
        ///
        /// Kept for the final "declared and never used" pass: the scope disappears
        /// when the function closes, so the list has to live outside it.
        /// </summary>
        private readonly List<Symbol> _declaredData = new List<Symbol>();

        /// <summary>
        /// Where a bad 'Group.Value' was already reported, by the offset of the name
        /// after the dot.
        ///
        /// The constant folder and the emission pass walk the same expression, and
        /// the folder also runs speculatively in a few places - without this, one
        /// typo would be reported two or three times.
        /// </summary>
        private readonly HashSet<int> _reportedConstants = new HashSet<int>();

        /// <summary>Body of each declared struct, kept so the fields can be resolved on demand.</summary>
        private readonly Dictionary<StructSymbol, StructDeclaration> _structBodies =
            new Dictionary<StructSymbol, StructDeclaration>();

        /// <summary>Structs being resolved right now. This is what catches a struct that contains itself.</summary>
        private readonly HashSet<StructSymbol> _structsInProgress = new HashSet<StructSymbol>();

        private readonly SourceText _source;
        private int _globalCount;

        private FunctionContext? _function;

        /// <summary>Emission state for the function being compiled.</summary>
        private sealed class FunctionContext
        {
            public FunctionSymbol Symbol = null!;
            public int NextLocalSlot;
            public int MaxLocalSlot;

            /// <summary>
            /// Next free cell of the frame's heap region, and the peak it reached.
            /// Like the local slots, the offsets are reused when a scope closes, so
            /// two sibling blocks share the same cells.
            /// </summary>
            public int NextHeapOffset;
            public int MaxHeapOffset;

            public readonly List<LoopContext> Loops = new List<LoopContext>();
        }

        /// <summary>Pending jump targets for a loop, patched when the loop closes.</summary>
        private sealed class LoopContext
        {
            public readonly List<int> BreakJumps = new List<int>();
            public readonly List<int> ContinueJumps = new List<int>();
            /// <summary>Where 'continue' jumps to. In a 'for' it is the increment, not the condition.</summary>
            public int ContinueTarget = -1;
        }

        public Compiler(SourceText source, DiagnosticBag diagnostics)
        {
            _source = source;
            _diagnostics = diagnostics;
            _globalScope = new Scope(null);
            _scope = _globalScope;
        }

        // ==================================================================
        //  Entry point
        // ==================================================================

        public IZProgram? Compile(CompilationUnit unit)
        {
            var functionDeclarations = new List<FunctionDeclaration>();
            var globalStatements = new List<StatementSyntax>();

            CollectDeclarations(unit, functionDeclarations, globalStatements);

            var mainSymbol = _globalScope.Lookup("main") as FunctionSymbol;
            if (mainSymbol == null)
            {
                _diagnostics.Report(IZErrorCode.MissingMainFunction, new SourceSpan(0, 0),
                    "every IZ program needs an 'fn main()' function");
            }
            else if (mainSymbol.Parameters.Count != 0)
            {
                _diagnostics.Report(IZErrorCode.WrongArgumentCount, new SourceSpan(0, 0),
                    "'main' cannot take parameters");
            }

            EmitEntryFunction(globalStatements, mainSymbol);

            foreach (var declaration in functionDeclarations)
                EmitFunctionBody(declaration);

            ReportUnusedDeclarations();

            if (_diagnostics.HasErrors) return null;

            return new IZProgram(
                _code.ToArray(),
                _constants.ToArray(),
                _strings.ToArray(),
                _functions.ToArray(),
                _globalCount,
                mainFunctionIndex: 0,          // 0 is always the synthetic <entry>
                lines: _lines.ToArray());
        }

        /// <summary>
        /// Warns about a declared name nobody mentions.
        ///
        /// Only runs when compilation came out clean: next to a real error the
        /// warning is just noise, and the name may look "unused" only because the
        /// code that would use it is the part that fails to compile.
        /// </summary>
        private void ReportUnusedDeclarations()
        {
            if (_diagnostics.HasErrors) return;

            foreach (var symbol in _declaredData)
            {
                if (symbol.IsUsed) continue;

                string kind = symbol is DeviceSymbol ? "device"
                            : symbol is StructSymbol ? "struct"
                            : symbol is VariableSymbol variable && variable.IsConst ? "const"
                            : "variable";

                _diagnostics.Warn(IZErrorCode.UnusedVariable, symbol.DeclarationSpan,
                    kind + " '" + symbol.Name + "' was declared and never used");
            }
        }

        /// <summary>Declares the symbol and registers it for the unused-name pass.</summary>
        private void DeclareTracked(Symbol symbol, SourceSpan declarationSpan)
        {
            symbol.DeclarationSpan = declarationSpan;
            _scope.TryDeclare(symbol);
            _declaredData.Add(symbol);
        }

        /// <summary>
        /// Pre-pass: registers globals, devices and function signatures without
        /// emitting code. This is what makes forward calls possible.
        /// </summary>
        private void CollectDeclarations(CompilationUnit unit,
                                         List<FunctionDeclaration> functions,
                                         List<StatementSyntax> globals)
        {
            // Global constants come before everything else. They fold to a value and
            // emit no code, and an array length is allowed to be one of them, so
            // 'num[WINDOW]' has to work inside a struct field and in a signature.
            foreach (var declaration in unit.Declarations)
            {
                if (declaration is GlobalStatementDeclaration statement &&
                    statement.Statement is VariableDeclaration variable && variable.IsConst)
                {
                    EmitVariableDeclaration(variable, isGlobal: true);
                }
            }

            // Structs come next, and in two steps: every name is registered before
            // any field type is bound, so one struct may name another declared
            // further down the file - the same courtesy functions already get.
            foreach (var declaration in unit.Declarations)
            {
                if (!(declaration is StructDeclaration structDeclaration)) continue;

                if (_globalScope.LookupLocal(structDeclaration.Name) != null)
                {
                    _diagnostics.Report(IZErrorCode.DuplicateName, structDeclaration.NameToken.Span,
                        "'" + structDeclaration.Name + "' was already declared");
                    continue;
                }

                var symbol = new StructSymbol(structDeclaration.Name);
                _structBodies[symbol] = structDeclaration;
                DeclareTracked(symbol, structDeclaration.NameToken.Span);
            }

            foreach (var pair in _structBodies) ResolveStruct(pair.Key);

            // Index 0 is reserved for <entry>; the user functions come after it.
            int nextFunctionIndex = 1;

            foreach (var declaration in unit.Declarations)
            {
                switch (declaration)
                {
                    case FunctionDeclaration function:
                    {
                        var parameters = new List<ParameterSymbol>();
                        var seen = new HashSet<string>(StringComparer.Ordinal);

                        for (int i = 0; i < function.Parameters.Count; i++)
                        {
                            var parameter = function.Parameters[i];
                            var type = ResolveTypeAnnotation(parameter.DeclaredType, IZType.Num);

                            if (!seen.Add(parameter.Name))
                            {
                                _diagnostics.Report(IZErrorCode.DuplicateName, parameter.Span,
                                    "parameter '" + parameter.Name + "' appears twice");
                            }
                            parameters.Add(new ParameterSymbol(parameter.Name, type, i));
                        }

                        var returnType = ResolveTypeAnnotation(function.ReturnType, IZType.Void);
                        if (returnType.IsAggregate)
                        {
                            // The frame's heap region goes away on the return, so the
                            // address would point at cells the next call reuses. Taking
                            // the aggregate as a parameter and filling it in is the way.
                            _diagnostics.Report(IZErrorCode.TypeMismatch,
                                function.ReturnType?.Span ?? function.NameToken.Span,
                                "a function cannot return " + returnType.Display() +
                                "; take it as a parameter and fill it in");
                        }

                        var symbol = new FunctionSymbol(function.Name, parameters, returnType, nextFunctionIndex);

                        if (_globalScope.LookupLocal(function.Name) != null)
                        {
                            _diagnostics.Report(IZErrorCode.DuplicateName, function.NameToken.Span,
                                "'" + function.Name + "' was already declared");
                        }
                        else if (Builtins.TryGet(function.Name, out _) ||
                                 string.Equals(function.Name, "sleep", StringComparison.Ordinal) ||
                                 string.Equals(function.Name, "isset", StringComparison.Ordinal))
                        {
                            // A call resolves to the builtin first, so a function with
                            // the same name would never be reached. Saying so beats
                            // letting the body sit there doing nothing.
                            _diagnostics.Report(IZErrorCode.DuplicateName, function.NameToken.Span,
                                "'" + function.Name + "' is a builtin function; pick another name");
                        }
                        else
                        {
                            _globalScope.TryDeclare(symbol);
                            nextFunctionIndex++;
                            functions.Add(function);
                        }
                        break;
                    }

                    case GlobalStatementDeclaration global:
                        globals.Add(global.Statement);
                        break;

                    case StructDeclaration _:
                        break;      // already handled above
                }
            }
        }

        /// <summary>
        /// Binds the fields of a struct, resolving the ones it depends on first.
        ///
        /// A struct that contains itself has no finite size, so the cycle is broken
        /// here and reported once, on the field that closes it.
        /// </summary>
        private void ResolveStruct(StructSymbol symbol)
        {
            if (symbol.IsResolved) return;
            if (!_structBodies.TryGetValue(symbol, out var declaration)) { symbol.IsResolved = true; return; }

            if (!_structsInProgress.Add(symbol)) return;    // a cycle; the field that closed it reports

            foreach (var field in declaration.Fields)
            {
                var type = ResolveTypeAnnotation(field.DeclaredType, IZType.Num);

                if (type.Kind == IZTypeKind.Struct && type.Struct != null &&
                    _structsInProgress.Contains(type.Struct))
                {
                    _diagnostics.Report(IZErrorCode.TypeMismatch, field.Span,
                        "'" + symbol.Name + "' would contain itself through '" + field.Name +
                        "'; a struct field holds the value, not a reference to it");
                    type = IZType.Num;
                }
                else if (type.Kind == IZTypeKind.Dev || type.Kind == IZTypeKind.Void ||
                         type.Kind == IZTypeKind.Batch)
                {
                    _diagnostics.Report(IZErrorCode.TypeMismatch, field.Span,
                        "a struct field cannot be " + type.Display() + "; use num, bool, str, " +
                        "an array or another struct");
                    type = IZType.Num;
                }

                if (!symbol.TryAddField(new FieldSymbol(field.Name, type, field.NameToken.Span)))
                {
                    _diagnostics.Report(IZErrorCode.DuplicateName, field.NameToken.Span,
                        "'" + symbol.Name + "' already has a field called '" + field.Name + "'");
                }
            }

            if (symbol.Size > MaxHeapPerFunction)
            {
                _diagnostics.Report(IZErrorCode.TooMuchMemory, declaration.NameToken.Span,
                    "'" + symbol.Name + "' takes " + symbol.Size + " cells, past the limit of " +
                    MaxHeapPerFunction);
            }

            _structsInProgress.Remove(symbol);
            symbol.IsResolved = true;
        }

        private IZType ResolveTypeAnnotation(TypeSyntax? annotation, IZType fallback)
        {
            switch (annotation)
            {
                case null:
                    return fallback;

                case ArrayTypeSyntax array:
                {
                    var element = ResolveTypeAnnotation(array.ElementType, IZType.Num);

                    if (element.Kind == IZTypeKind.Dev || element.Kind == IZTypeKind.Void ||
                        element.Kind == IZTypeKind.Batch)
                    {
                        _diagnostics.Report(IZErrorCode.TypeMismatch, array.Span,
                            "an array cannot hold " + element.Display() +
                            "; use num, bool, str, another array or a struct");
                        element = IZType.Num;
                    }

                    var lengthType = TryEvaluateConstant(array.Length, out double lengthValue);
                    if (lengthType == IZType.Error)
                    {
                        _diagnostics.Report(IZErrorCode.ConstExpressionRequired, array.Length.Span,
                            "the length of an array has to be known at compile time");
                        return IZType.ArrayOf(element, 1);
                    }

                    int length = (int)lengthValue;
                    if (lengthValue != length || length < 1 || length > MaxArrayLength)
                    {
                        _diagnostics.Report(IZErrorCode.InvalidArrayLength, array.Length.Span,
                            "the length of an array is a whole number from 1 to " + MaxArrayLength +
                            ", not " + lengthValue.ToString(CultureInfo.InvariantCulture));
                        length = 1;
                    }

                    return IZType.ArrayOf(element, length);
                }

                case ListTypeSyntax list:
                {
                    var inner = ResolveTypeAnnotation(list.Inner, IZType.Error);

                    if (inner.Kind != IZTypeKind.Array)
                    {
                        _diagnostics.Report(IZErrorCode.InvalidArrayLength, list.Span,
                            "a list is declared with the room it has: 'list num[8]'");
                        return IZType.ListOf(IZType.Num, 1);
                    }

                    // A struct is fine - it is one run of cells like any other item.
                    // An array or another list is not: the item would carry a length
                    // of its own, and the count already answers that question once.
                    var item = inner.ElementType!;
                    if (item.Kind == IZTypeKind.Array || item.Kind == IZTypeKind.List)
                    {
                        _diagnostics.Report(IZErrorCode.TypeMismatch, list.Span,
                            "a list holds num, bool, str or a struct, not " + item.Display());
                        item = IZType.Num;
                    }

                    return IZType.ListOf(item, inner.Length);
                }

                case NamedTypeSyntax named:
                    switch (named.Token.Kind)
                    {
                        case TokenKind.KwNum: return IZType.Num;
                        case TokenKind.KwBool: return IZType.Bool;
                        case TokenKind.KwStr: return IZType.Str;
                        case TokenKind.KwDev: return IZType.Dev;
                        default:
                        {
                            if (_globalScope.Lookup(named.Name) is StructSymbol structSymbol)
                            {
                                ResolveStruct(structSymbol);
                                return IZType.Of(structSymbol);
                            }

                            _diagnostics.Report(IZErrorCode.UndefinedName, named.Span,
                                "'" + named.Name + "' is not a type; expected num, bool, str, dev " +
                                "or the name of a struct");
                            return fallback;
                        }
                    }

                default:
                    return fallback;
            }
        }

        /// <summary>
        /// Emits the synthetic <c>&lt;entry&gt;</c> function: it initializes the globals
        /// in declaration order, calls 'main' and halts.
        /// </summary>
        private void EmitEntryFunction(List<StatementSyntax> globalStatements, FunctionSymbol? main)
        {
            int entryPoint = _code.Count;

            _function = new FunctionContext
            {
                Symbol = new FunctionSymbol("<entry>", new List<ParameterSymbol>(), IZType.Void, 0),
            };

            _functions.Add(new FunctionInfo("<entry>", entryPoint, 0, 0, returnsValue: false));

            foreach (var statement in globalStatements)
                EmitGlobalDeclaration(statement);

            if (main != null)
                Emit(OpCode.Call, main.Index, 0, 0);

            Emit(OpCode.Halt, 0, 0, 0);

            // The entry frame is the one that never unwinds, so the global arrays and
            // structs live in it. Its size is only known now that they are all emitted.
            _functions[0] = new FunctionInfo("<entry>", entryPoint, 0,
                _function.MaxLocalSlot, returnsValue: false, heapSize: _function.MaxHeapOffset);
            _function = null;
        }

        private void EmitGlobalDeclaration(StatementSyntax statement)
        {
            switch (statement)
            {
                case DeviceDeclaration device:
                    DeclareDevice(device);
                    break;

                case VariableDeclaration variable:
                    // The constants were already folded in the collection pass.
                    if (!variable.IsConst) EmitVariableDeclaration(variable, isGlobal: true);
                    break;

                default:
                    _diagnostics.Report(IZErrorCode.ExpectedDeclaration, statement.Span,
                        "only 'fn', 'var', 'const' and 'device' are allowed at the top level");
                    break;
            }
        }

        private void EmitFunctionBody(FunctionDeclaration declaration)
        {
            var symbol = (FunctionSymbol)_globalScope.Lookup(declaration.Name)!;
            int entryPoint = _code.Count;

            _function = new FunctionContext { Symbol = symbol };
            _scope = new Scope(_globalScope);

            foreach (var parameter in symbol.Parameters)
            {
                var local = new VariableSymbol(parameter.Name, parameter.Type,
                    isConst: false, isGlobal: false, slot: parameter.Slot);
                _scope.TryDeclare(local);
            }
            _function.NextLocalSlot = symbol.Parameters.Count;
            _function.MaxLocalSlot = symbol.Parameters.Count;

            EmitBlock(declaration.Body, newScope: false);

            // Every function ends with a return, even if the author forgot one:
            // without it execution would fall through into the next function body.
            if (symbol.ReturnType == IZType.Void)
            {
                Emit(OpCode.Return, 0, 0, LineOf(declaration.Body.Span));
            }
            else
            {
                if (!AlwaysReturns(declaration.Body))
                {
                    _diagnostics.Report(IZErrorCode.MissingReturn, declaration.NameToken.Span,
                        "'" + declaration.Name + "' declares return type " + symbol.ReturnType.Display() +
                        ", but there is a path that ends without a 'return'");
                }
                Emit(OpCode.PushZero, 0, 0, LineOf(declaration.Body.Span));
                Emit(OpCode.ReturnValue, 0, 0, LineOf(declaration.Body.Span));
            }

            _functions.Add(new FunctionInfo(
                symbol.Name,
                entryPoint,
                symbol.Parameters.Count,
                _function.MaxLocalSlot,
                symbol.ReturnType != IZType.Void,
                _function.MaxHeapOffset));

            _scope = _globalScope;
            _function = null;
        }

        /// <summary>
        /// Conservative "every path returns" analysis. A false negative costs an
        /// unwarranted warning; a false positive costs execution falling into
        /// nowhere - so when in doubt it answers false.
        /// </summary>
        private static bool AlwaysReturns(StatementSyntax statement)
        {
            switch (statement)
            {
                case ReturnStatement _:
                    return true;

                case BlockStatement block:
                    foreach (var inner in block.Statements)
                        if (AlwaysReturns(inner)) return true;
                    return false;

                case IfStatement conditional:
                    return conditional.ElseBranch != null
                        && AlwaysReturns(conditional.ThenBlock)
                        && AlwaysReturns(conditional.ElseBranch);

                case LoopStatement loop:
                    // A 'loop' with no break never exits: the code after it is unreachable.
                    return !ContainsBreak(loop.Body);

                default:
                    return false;
            }
        }

        private static bool ContainsBreak(StatementSyntax statement)
        {
            switch (statement)
            {
                case BreakStatement _:
                    return true;

                case BlockStatement block:
                    foreach (var inner in block.Statements)
                        if (ContainsBreak(inner)) return true;
                    return false;

                case IfStatement conditional:
                    return ContainsBreak(conditional.ThenBlock)
                        || (conditional.ElseBranch != null && ContainsBreak(conditional.ElseBranch));

                // A break inside a nested loop belongs to that loop, not to this one.
                default:
                    return false;
            }
        }

        // ==================================================================
        //  Statement emission
        // ==================================================================

        private void EmitBlock(BlockStatement block, bool newScope = true)
        {
            var saved = _scope;
            int savedSlot = _function!.NextLocalSlot;
            int savedHeap = _function.NextHeapOffset;

            if (newScope) _scope = new Scope(_scope);

            foreach (var statement in block.Statements)
                EmitStatement(statement);

            if (newScope)
            {
                _scope = saved;
                // Local slots and heap cells are reused when the scope ends; the two
                // Max fields keep the peak, which is what the frame has to reserve.
                _function.NextLocalSlot = savedSlot;
                _function.NextHeapOffset = savedHeap;
            }
        }

        private void EmitStatement(StatementSyntax statement)
        {
            switch (statement)
            {
                case BlockStatement block:
                    EmitBlock(block);
                    break;
                case VariableDeclaration variable:
                    EmitVariableDeclaration(variable, isGlobal: false);
                    break;
                case DeviceDeclaration device:
                    DeclareDevice(device);
                    break;
                case AssignmentStatement assignment:
                    EmitAssignment(assignment);
                    break;
                case ExpressionStatement expression:
                    EmitExpressionStatement(expression);
                    break;
                case IfStatement conditional:
                    EmitIf(conditional);
                    break;
                case WhileStatement loop:
                    EmitWhile(loop);
                    break;
                case LoopStatement loop:
                    EmitLoop(loop);
                    break;
                case ForStatement loop:
                    EmitFor(loop);
                    break;
                case BreakStatement statement2:
                    EmitBreak(statement2);
                    break;
                case ContinueStatement statement2:
                    EmitContinue(statement2);
                    break;
                case YieldStatement statement2:
                    Emit(OpCode.Yield, 0, 0, LineOf(statement2.Span));
                    break;
                case ReturnStatement statement2:
                    EmitReturn(statement2);
                    break;
            }
        }

        private void DeclareDevice(DeviceDeclaration declaration)
        {
            // The parser already reported a pin it could not read, and a device with
            // neither a pin nor a selector has nothing left to declare.
            if (declaration.Pin < 0 && declaration.Selector == null) return;

            if (_scope.LookupLocal(declaration.Name) != null)
            {
                _diagnostics.Report(IZErrorCode.DuplicateName, declaration.NameToken.Span,
                    "'" + declaration.Name + "' was already declared in this scope");
                return;
            }

            var symbol = declaration.Selector != null
                ? BuildBatchDeviceSymbol(declaration.Name, declaration.Selector)
                : new DeviceSymbol(declaration.Name, declaration.Pin);

            if (symbol == null) return;

            DeclareTracked(symbol, declaration.NameToken.Span);
        }

        /// <summary>
        /// Folds 'device led = named(StructureDiode, "led-dev");' into the two hashes
        /// the batch instructions take.
        ///
        /// Both operands have to be known at compile time. A device is a name for a
        /// place in the world, fixed for the whole program: letting the selector
        /// depend on a running value would make the same name mean different devices
        /// on different lines, which is what the inline 'named(...)' is for. Returns
        /// null when the selector cannot be folded, after reporting why.
        /// </summary>
        private DeviceSymbol? BuildBatchDeviceSymbol(string name, BatchSelectorExpression selector)
        {
            bool named = selector.Kind == BatchSelectorKind.Named;
            double prefabHash = 0.0;                    // 0 matches any prefab
            var description = new StringBuilder(named ? "named(" : "all(");

            if (selector.Prefab != null)
            {
                if (!TryFoldSelectorOperand(selector.Prefab, name, "prefab",
                                            out prefabHash, out string prefabText))
                    return null;
                description.Append(prefabText);
            }
            else if (selector.Kind == BatchSelectorKind.All)
            {
                return null;                            // the parser reported the missing prefab
            }

            double labelHash = 0.0;
            if (named)
            {
                if (selector.Label == null) return null;    // reported by the parser

                if (!TryFoldSelectorOperand(selector.Label, name, "label",
                                            out labelHash, out string labelText))
                    return null;

                if (selector.Prefab != null) description.Append(", ");
                description.Append(labelText);
            }

            description.Append(')');
            return new DeviceSymbol(name, named, prefabHash, labelHash, description.ToString());
        }

        /// <summary>
        /// Folds one operand of a device selector to its hash, following the same
        /// rules as the inline form: a bare undeclared identifier is a raw prefab
        /// name, text and str consts are hashed, and a num const passes through.
        /// </summary>
        private bool TryFoldSelectorOperand(ExpressionSyntax expression, string deviceName,
                                            string role, out double hash, out string text)
        {
            hash = 0.0;
            text = string.Empty;

            if (expression is NameExpression bareName && _scope.Lookup(bareName.Name) == null)
            {
                InternString(bareName.Name, expression.Span);
                hash = PrefabHash.Compute(bareName.Name);
                text = bareName.Name;
                return true;
            }

            if (TryEvaluateConstantString(expression, out string? constantText))
            {
                InternString(constantText!, expression.Span);
                hash = PrefabHash.Compute(constantText!);
                text = "\"" + constantText + "\"";
                return true;
            }

            var type = TryEvaluateConstant(expression, out double value);
            if (type == IZType.Num || type == IZType.Bool)
            {
                hash = value;
                text = value.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            _diagnostics.Report(IZErrorCode.ConstExpressionRequired, expression.Span,
                "the " + role + " of a device selector has to be known at compile time; " +
                "'" + deviceName + "' cannot depend on a value the program computes. " +
                "Write the selector where it is used instead.");
            return false;
        }

        private void EmitVariableDeclaration(VariableDeclaration declaration, bool isGlobal)
        {
            if (_scope.LookupLocal(declaration.Name) != null)
            {
                _diagnostics.Report(IZErrorCode.DuplicateName, declaration.NameToken.Span,
                    "'" + declaration.Name + "' was already declared in this scope");
            }

            if (declaration.IsConst)
            {
                var constAnnotation = ResolveTypeAnnotation(declaration.DeclaredType, IZType.Num);
                if (constAnnotation.IsAggregate)
                {
                    _diagnostics.Report(IZErrorCode.TypeMismatch, declaration.Span,
                        "a 'const' cannot be " + constAnnotation.Display() +
                        "; declare it as 'var', which starts zeroed");
                    return;
                }

                // A const holding text takes the same route, with the text kept
                // beside the symbol instead of a number.
                if (TryEvaluateConstantString(declaration.Initializer, out string? constantText))
                {
                    if (declaration.DeclaredType != null && constAnnotation != IZType.Str)
                    {
                        _diagnostics.Report(IZErrorCode.TypeMismatch, declaration.Initializer!.Span,
                            "'" + declaration.Name + "' was declared as " + constAnnotation.Display() +
                            ", but the value is str");
                    }

                    InternString(constantText!, declaration.Initializer!.Span);

                    var textConstant = new VariableSymbol(declaration.Name, IZType.Str,
                        isConst: true, isGlobal: isGlobal, slot: -1)
                    {
                        ConstantString = constantText,
                    };
                    DeclareTracked(textConstant, declaration.NameToken.Span);
                    return;
                }

                // A const takes no slot: the value is folded into every use.
                double value = 0.0;
                var constantType = declaration.Initializer != null
                    ? TryEvaluateConstant(declaration.Initializer, out value)
                    : IZType.Error;

                if (constantType == IZType.Error)
                {
                    // With no initializer the parser already said so; do not say it twice.
                    if (declaration.Initializer != null)
                    {
                        _diagnostics.Report(IZErrorCode.ConstExpressionRequired, declaration.Initializer.Span,
                            "the value of a 'const' must be computable at compile time");
                    }
                    constantType = IZType.Num;
                }

                var declaredConstType = ResolveTypeAnnotation(declaration.DeclaredType, constantType);
                if (declaration.Initializer != null && !constantType.IsAssignableTo(declaredConstType))
                {
                    _diagnostics.Report(IZErrorCode.TypeMismatch, declaration.Initializer.Span,
                        "'" + declaration.Name + "' was declared as " + declaredConstType.Display() +
                        ", but the value is " + constantType.Display());
                }

                var constant = new VariableSymbol(declaration.Name, declaredConstType,
                    isConst: true, isGlobal: isGlobal, slot: -1)
                {
                    ConstantValue = value,
                };
                DeclareTracked(constant, declaration.NameToken.Span);
                return;
            }

            // A query settles what it hands back only once its lambdas are
            // compiled, so there is nothing to resolve in advance: emit it, and let
            // the value name the type of the variable.
            if (declaration.DeclaredType == null && declaration.Initializer != null &&
                IsQueryCall(declaration.Initializer))
            {
                var queryType = EmitExpression(declaration.Initializer);

                if (queryType == IZType.Void)
                {
                    _diagnostics.Report(IZErrorCode.TypeMismatch, declaration.Initializer.Span,
                        "this gives nothing back, so there is nothing for '" +
                        declaration.Name + "' to hold");
                    Emit(OpCode.PushZero, 0, 0, LineOf(declaration.Span));
                    queryType = IZType.Error;
                }

                int querySlot = DeclareVariable(declaration, queryType, isGlobal);
                Emit(isGlobal ? OpCode.StoreGlobal : OpCode.StoreLocal, querySlot, 0,
                     LineOf(declaration.Span));
                return;
            }

            var declaredType = ResolveDeclaredType(declaration);

            if (declaredType.IsAggregate)
            {
                EmitAggregateDeclaration(declaration, declaredType, isGlobal);
                return;
            }

            if (declaration.Initializer == null)
            {
                // The parser already said a scalar needs a value; keep the stack sane.
                Emit(OpCode.PushZero, 0, 0, LineOf(declaration.Span));
            }
            else
            {
                var initializerType = EmitExpression(declaration.Initializer);

                if (declaration.DeclaredType == null)
                    declaredType = initializerType;            // no annotation: the value decides
                else if (!initializerType.IsAssignableTo(declaredType))
                {
                    _diagnostics.Report(IZErrorCode.TypeMismatch, declaration.Initializer.Span,
                        "'" + declaration.Name + "' was declared as " + declaredType.Display() +
                        ", but it receives " + initializerType.Display());
                }
            }

            if (declaredType == IZType.Dev || declaredType == IZType.Batch)
            {
                _diagnostics.Report(IZErrorCode.TypeMismatch, declaration.Span,
                    "a device does not go in a 'var'; use 'device " + declaration.Name + " = d0;'");
            }

            int slot = DeclareVariable(declaration, declaredType, isGlobal);
            Emit(isGlobal ? OpCode.StoreGlobal : OpCode.StoreLocal, slot, 0, LineOf(declaration.Span));
        }

        private int AllocateLocal(SourceSpan span)
        {
            if (_function!.NextLocalSlot >= MaxLocalsPerFunction)
            {
                _diagnostics.Report(IZErrorCode.TooManyLocals, span,
                    "went past " + MaxLocalsPerFunction + " local variables in this function");
                return 0;
            }
            int slot = _function.NextLocalSlot++;
            if (_function.NextLocalSlot > _function.MaxLocalSlot)
                _function.MaxLocalSlot = _function.NextLocalSlot;
            return slot;
        }

        /// <summary>
        /// The type of a declaration before anything is emitted.
        ///
        /// An annotation always wins. Without one the type comes from the initializer,
        /// and an array literal with nothing to go on gives num: writing
        /// 'var a: bool[2] = [true, false];' is how the element type gets said out loud.
        /// </summary>
        private IZType ResolveDeclaredType(VariableDeclaration declaration)
        {
            if (declaration.DeclaredType != null)
                return ResolveTypeAnnotation(declaration.DeclaredType, IZType.Num);

            if (declaration.Initializer is ArrayLiteralExpression literal)
            {
                if (literal.Elements.Count == 0)
                {
                    _diagnostics.Report(IZErrorCode.TypeMismatch, literal.Span,
                        "an empty array literal says nothing about its type; write " +
                        "'var " + declaration.Name + ": num[8];' instead");
                    return IZType.ArrayOf(IZType.Num, 1);
                }
                return IZType.ArrayOf(IZType.Num, literal.Elements.Count);
            }

            return TypeOfInitializer(declaration.Initializer);
        }

        /// <summary>
        /// Types an initializer without emitting anything, for the cases where the
        /// decision on how to emit comes before the emission itself. Only the shapes
        /// that can produce an aggregate need to be exact; anything else falls back
        /// to num and is checked again when the code is actually emitted.
        /// </summary>
        private IZType TypeOfInitializer(ExpressionSyntax? expression)
        {
            switch (expression)
            {
                case null:
                    return IZType.Error;

                case NameExpression name:
                    return _scope.LookupNoUse(name.Name) is VariableSymbol variable
                        ? variable.Type
                        : IZType.Num;

                case IndexExpression index:
                {
                    var target = TypeOfInitializer(index.Target);
                    return target.Kind == IZTypeKind.Array || target.Kind == IZTypeKind.List
                        ? target.ElementType!
                        : IZType.Num;
                }

                case MemberExpression member:
                {
                    var target = TypeOfInitializer(member.Target);
                    var field = target.Kind == IZTypeKind.Struct
                        ? target.Struct?.FindField(member.MemberName)
                        : null;
                    return field?.Type ?? IZType.Num;
                }

                default:
                    return IZType.Num;
            }
        }

        /// <summary>
        /// Emits an array or struct declaration.
        ///
        /// Two shapes, and they are not the same thing: with no initializer, or with
        /// an array literal, the variable gets its own cells; with another aggregate
        /// on the right it becomes a second name for those same cells.
        /// </summary>
        private void EmitAggregateDeclaration(VariableDeclaration declaration, IZType type, bool isGlobal)
        {
            int line = LineOf(declaration.Span);
            bool ownStorage = declaration.Initializer == null ||
                              declaration.Initializer is ArrayLiteralExpression;

            if (ownStorage)
            {
                int offset = AllocateAggregate(type.Size, declaration.Span);
                Emit(OpCode.NewAggregate, offset, type.Size, line);

                if (declaration.Initializer != null)
                {
                    Emit(OpCode.Dup, 0, 0, line);
                    EmitFill(declaration.Initializer, type, line);
                }
            }
            else
            {
                var initializerType = EmitExpression(declaration.Initializer!);
                if (!initializerType.IsAssignableTo(type))
                {
                    _diagnostics.Report(IZErrorCode.TypeMismatch, declaration.Initializer!.Span,
                        "'" + declaration.Name + "' was declared as " + type.Display() +
                        ", but it receives " + initializerType.Display());
                }
            }

            int slot = DeclareVariable(declaration, type, isGlobal);
            Emit(isGlobal ? OpCode.StoreGlobal : OpCode.StoreLocal, slot, 0, line);
        }

        /// <summary>Declares the variable and hands back the slot its address goes into.</summary>
        private int DeclareVariable(VariableDeclaration declaration, IZType type, bool isGlobal)
        {
            int slot;
            if (isGlobal)
            {
                if (_globalCount >= MaxGlobals)
                {
                    _diagnostics.Report(IZErrorCode.TooManyGlobals, declaration.Span,
                        "went past " + MaxGlobals + " global variables");
                    slot = 0;
                }
                else
                {
                    slot = _globalCount++;
                }
            }
            else
            {
                slot = AllocateLocal(declaration.Span);
            }

            DeclareTracked(new VariableSymbol(declaration.Name, type, false, isGlobal, slot),
                           declaration.NameToken.Span);
            return slot;
        }

        /// <summary>Reserves cells in the frame's heap region and returns the offset into it.</summary>
        private int AllocateAggregate(int cells, SourceSpan span)
        {
            var function = _function!;

            if (function.NextHeapOffset + cells > MaxHeapPerFunction)
            {
                _diagnostics.Report(IZErrorCode.TooMuchMemory, span,
                    "the arrays and structs declared here go past " + MaxHeapPerFunction + " cells");
                return 0;
            }

            int offset = function.NextHeapOffset;
            function.NextHeapOffset += cells;
            if (function.NextHeapOffset > function.MaxHeapOffset)
                function.MaxHeapOffset = function.NextHeapOffset;
            return offset;
        }

        /// <summary>
        /// Writes an initial value into the heap. The address it writes to is already
        /// on the stack, and this consumes it.
        /// </summary>
        private void EmitFill(ExpressionSyntax value, IZType target, int line)
        {
            if (target.Kind == IZTypeKind.Array && value is ArrayLiteralExpression literal)
            {
                if (literal.Elements.Count != target.Length)
                {
                    _diagnostics.Report(IZErrorCode.TypeMismatch, literal.Span,
                        "this array holds " + target.Length + " element(s), but the literal has " +
                        literal.Elements.Count);
                }

                var element = target.ElementType!;
                int shared = Math.Min(literal.Elements.Count, target.Length);

                for (int i = 0; i < shared; i++)
                {
                    Emit(OpCode.Dup, 0, 0, line);
                    EmitConstant(i, line);
                    Emit(OpCode.IndexRef, element.Size, target.Length, line);
                    EmitFill(literal.Elements[i], element, line);
                }

                Emit(OpCode.Pop, 0, 0, line);       // the base address, now filled
                return;
            }

            // A list starts with what the literal holds and keeps the rest as room:
            // 'var xs: list num[4] = [1, 2, 3];' is three items in four cells.
            if (target.Kind == IZTypeKind.List && value is ArrayLiteralExpression items)
            {
                if (items.Elements.Count > target.Length)
                {
                    _diagnostics.Report(IZErrorCode.TypeMismatch, items.Span,
                        "this list has room for " + target.Length + " item(s), and the " +
                        "literal has " + items.Elements.Count);
                }

                var item = target.ElementType!;
                int used = Math.Min(items.Elements.Count, target.Length);

                Emit(OpCode.Dup, 0, 0, line);
                EmitConstant(used, line);
                Emit(OpCode.StoreHeap, 0, 0, line);          // the count comes first

                for (int i = 0; i < used; i++)
                {
                    Emit(OpCode.Dup, 0, 0, line);
                    Emit(OpCode.FieldRef, 1, 0, line);
                    EmitConstant(i, line);
                    Emit(OpCode.IndexRef, item.Size, target.Length, line);
                    EmitFill(items.Elements[i], item, line);
                }

                Emit(OpCode.Pop, 0, 0, line);
                return;
            }

            if (value is ArrayLiteralExpression stray)
            {
                _diagnostics.Report(IZErrorCode.TypeMismatch, stray.Span,
                    "an array literal does not fit a " + target.Display());
                Emit(OpCode.Pop, 0, 0, line);
                return;
            }

            if (target.IsAggregate)
            {
                _diagnostics.Report(IZErrorCode.TypeMismatch, value.Span,
                    target.Kind == IZTypeKind.Struct
                        ? "a struct has no literal form; declare it and fill its fields one by one"
                        : "an array is initialized with a literal like [1, 2, 3]");
                Emit(OpCode.Pop, 0, 0, line);
                return;
            }

            var valueType = EmitExpression(value);
            if (!valueType.IsAssignableTo(target))
            {
                _diagnostics.Report(IZErrorCode.TypeMismatch, value.Span,
                    "this holds " + target.Display() + ", but it receives " + valueType.Display());
            }
            Emit(OpCode.StoreHeap, 0, 0, line);
        }

        private void EmitAssignment(AssignmentStatement assignment)
        {
            int line = LineOf(assignment.Span);

            switch (assignment.Target)
            {
                case NameExpression name:
                {
                    var symbol = _scope.Lookup(name.Name);
                    if (symbol == null)
                    {
                        _diagnostics.Report(IZErrorCode.UndefinedName, name.Span,
                            "'" + name.Name + "' was not declared");
                        EmitExpression(assignment.Value);
                        Emit(OpCode.Pop, 0, 0, line);
                        return;
                    }
                    if (!(symbol is VariableSymbol variable))
                    {
                        _diagnostics.Report(IZErrorCode.InvalidAssignmentTarget, name.Span,
                            "'" + name.Name + "' is not a variable");
                        EmitExpression(assignment.Value);
                        Emit(OpCode.Pop, 0, 0, line);
                        return;
                    }
                    if (variable.IsConst)
                    {
                        _diagnostics.Report(IZErrorCode.AssignToConst, name.Span,
                            "'" + name.Name + "' is a constant and cannot be reassigned");
                    }
                    if (variable.Type.IsAggregate)
                    {
                        // Rebinding the name would leave the old cells with no owner and
                        // could point a global at a frame that is about to unwind.
                        _diagnostics.Report(IZErrorCode.InvalidAssignmentTarget, name.Span,
                            "'" + name.Name + "' is " + variable.Type.Display() +
                            " and cannot be assigned as a whole; assign to its " +
                            PartsOf(variable.Type));
                        EmitExpression(assignment.Value);
                        Emit(OpCode.Pop, 0, 0, line);
                        return;
                    }

                    if (assignment.Kind != AssignmentKind.Assign)
                        EmitLoadVariable(variable, line);

                    var valueType = EmitExpression(assignment.Value);

                    if (assignment.Kind != AssignmentKind.Assign)
                    {
                        EmitCompound(assignment, variable.Type, valueType, name.Span, line);
                    }
                    else if (!valueType.IsAssignableTo(variable.Type))
                    {
                        _diagnostics.Report(IZErrorCode.TypeMismatch, assignment.Value.Span,
                            "'" + name.Name + "' is " + variable.Type.Display() +
                            ", but the assigned value is " + valueType.Display());
                    }

                    Emit(variable.IsGlobal ? OpCode.StoreGlobal : OpCode.StoreLocal, variable.Slot, 0, line);
                    return;
                }

                case MemberExpression member:
                    EmitMemberAssignment(member, assignment, line);
                    return;

                case IndexExpression index:
                    if (IsDeviceSlotAccess(index))
                    {
                        _diagnostics.Report(IZErrorCode.InvalidAssignmentTarget, index.Span,
                            "a device slot is read only");
                        EmitExpression(assignment.Value);
                        Emit(OpCode.Pop, 0, 0, line);
                        return;
                    }
                    EmitHeapAssignment(EmitElementAddress(index), assignment, line);
                    return;

                default:
                    // The parser already reported the invalid target; just consume the value.
                    EmitExpression(assignment.Value);
                    Emit(OpCode.Pop, 0, 0, line);
                    return;
            }
        }

        private void EmitMemberAssignment(MemberExpression member, AssignmentStatement assignment, int line)
        {
            // chute.slot[0].Quantity = 1 - a slot says what the device is holding; it
            // is not a setting, on a pin or on a batch.
            if (member.Target is IndexExpression slotTarget && IsDeviceSlotAccess(slotTarget))
            {
                _diagnostics.Report(IZErrorCode.InvalidAssignmentTarget, assignment.Target.Span,
                    "a device slot is read only");
                EmitExpression(assignment.Value);
                Emit(OpCode.Pop, 0, 0, line);
                return;
            }

            // pump.On = ...
            if (member.Target is NameExpression targetName &&
                _scope.Lookup(targetName.Name) is DeviceSymbol device)
            {
                if (!TryResolveLogicType(member, out int logicType)) return;

                if (device.IsBatch)
                {
                    EmitBatchDeviceAssignment(device, member, assignment, logicType, line);
                    return;
                }

                if (assignment.Kind != AssignmentKind.Assign)
                    Emit(OpCode.DeviceLoad, device.Pin, logicType, line);

                var valueType = EmitExpression(assignment.Value);

                if (assignment.Kind != AssignmentKind.Assign)
                {
                    RequireNumeric(valueType, assignment.Value.Span, assignment.OperatorToken.Text);
                    Emit(CompoundOpCode(assignment.Kind), 0, 0, line);
                }
                else if (!valueType.IsAssignableTo(IZType.Num))
                {
                    _diagnostics.Report(IZErrorCode.TypeMismatch, assignment.Value.Span,
                        "a device property takes num (or bool), not " + valueType.Display());
                }

                Emit(OpCode.DeviceStore, device.Pin, logicType, line);
                return;
            }

            // all(X).On = ...  /  named("y").On = ...
            if (member.Target is BatchSelectorExpression selector)
            {
                if (!TryResolveLogicType(member, out int logicType)) return;

                if (assignment.Kind != AssignmentKind.Assign)
                {
                    _diagnostics.Report(IZErrorCode.InvalidAssignmentTarget, assignment.Span,
                        "a batch write does not accept '" + assignment.OperatorToken.Text +
                        "'; read and write in separate steps");
                    return;
                }

                EmitBatchSelectorOperands(selector, line);
                var valueType = EmitExpression(assignment.Value);
                if (!valueType.IsAssignableTo(IZType.Num))
                {
                    _diagnostics.Report(IZErrorCode.TypeMismatch, assignment.Value.Span,
                        "a batch write takes num (or bool), not " + valueType.Display());
                }

                Emit(selector.Kind == BatchSelectorKind.All ? OpCode.BatchStore : OpCode.BatchNamedStore,
                    logicType, 0, line);
                return;
            }

            // Color.Black = 1 - the game's named values are fixed numbers.
            if (IsConstantGroupAccess(member))
            {
                _diagnostics.Report(IZErrorCode.AssignToConst, assignment.Target.Span,
                    "'" + ((NameExpression)member.Target).Name + "." + member.MemberName +
                    "' is one of the game's values and cannot be assigned to");
                return;
            }

            // p.x = 1 - a struct field.
            EmitHeapAssignment(EmitFieldAddress(member, forWrite: true), assignment, line);
        }

        /// <summary>
        /// Finishes an assignment whose target is a heap cell. The address is already
        /// on the stack; <paramref name="targetType"/> is what lives there.
        /// </summary>
        private void EmitHeapAssignment(IZType targetType, AssignmentStatement assignment, int line)
        {
            if (targetType.IsAggregate)
            {
                _diagnostics.Report(IZErrorCode.InvalidAssignmentTarget, assignment.Target.Span,
                    "this is " + targetType.Display() + " and cannot be assigned as a whole; " +
                    "assign to its " + PartsOf(targetType));
                Emit(OpCode.Pop, 0, 0, line);
                EmitExpression(assignment.Value);
                Emit(OpCode.Pop, 0, 0, line);
                return;
            }

            if (assignment.Kind != AssignmentKind.Assign)
            {
                // The address is computed once and used twice: an index with a side
                // effect must not run again for the read.
                Emit(OpCode.Dup, 0, 0, line);
                Emit(OpCode.LoadHeap, 0, 0, line);
            }

            var valueType = EmitExpression(assignment.Value);

            if (assignment.Kind != AssignmentKind.Assign)
            {
                EmitCompound(assignment, targetType, valueType, assignment.Target.Span, line);
            }
            else if (!valueType.IsAssignableTo(targetType))
            {
                _diagnostics.Report(IZErrorCode.TypeMismatch, assignment.Value.Span,
                    "this holds " + targetType.Display() + ", but the assigned value is " +
                    valueType.Display());
            }

            Emit(OpCode.StoreHeap, 0, 0, line);
        }

        /// <summary>
        /// Finishes a compound assignment, with the old value and the new one already
        /// on the stack. <c>s += "!"</c> joins text; everything else is arithmetic.
        /// </summary>
        private void EmitCompound(AssignmentStatement assignment, IZType targetType,
                                  IZType valueType, SourceSpan targetSpan, int line)
        {
            if (targetType == IZType.Str && assignment.Kind == AssignmentKind.Add)
            {
                if (valueType != IZType.Str && valueType != IZType.Error)
                {
                    string hint = valueType == IZType.Num || valueType == IZType.Bool
                        ? "; a number becomes text with 'text(x)'"
                        : string.Empty;

                    _diagnostics.Report(IZErrorCode.TypeMismatch, assignment.Value.Span,
                        "'+=' on a str takes str, not " + valueType.Display() + hint);
                }
                Emit(OpCode.StrConcat, 0, 0, line);
                return;
            }

            RequireNumeric(valueType, assignment.Value.Span, assignment.OperatorToken.Text);
            RequireNumeric(targetType, targetSpan, assignment.OperatorToken.Text);
            Emit(CompoundOpCode(assignment.Kind), 0, 0, line);
        }

        private static OpCode CompoundOpCode(AssignmentKind kind)
        {
            switch (kind)
            {
                case AssignmentKind.Add: return OpCode.Add;
                case AssignmentKind.Subtract: return OpCode.Subtract;
                case AssignmentKind.Multiply: return OpCode.Multiply;
                case AssignmentKind.Divide: return OpCode.Divide;
                case AssignmentKind.Modulo: return OpCode.Modulo;
                default: return OpCode.Nop;
            }
        }

        private void EmitExpressionStatement(ExpressionStatement statement)
        {
            var type = EmitExpression(statement.Expression);
            // A value-returning call used as a statement: discard the result.
            if (type != IZType.Void && type != IZType.Error)
                Emit(OpCode.Pop, 0, 0, LineOf(statement.Span));
        }

        private void EmitIf(IfStatement statement)
        {
            int line = LineOf(statement.Span);

            var conditionType = EmitExpression(statement.Condition);
            RequireBool(conditionType, statement.Condition.Span, "the 'if' condition");

            int jumpOverThen = EmitJump(OpCode.JumpIfFalse, line);
            EmitBlock(statement.ThenBlock);

            if (statement.ElseBranch != null)
            {
                int jumpOverElse = EmitJump(OpCode.Jump, line);
                PatchJump(jumpOverThen);
                EmitStatement(statement.ElseBranch);
                PatchJump(jumpOverElse);
            }
            else
            {
                PatchJump(jumpOverThen);
            }
        }

        private void EmitWhile(WhileStatement statement)
        {
            int line = LineOf(statement.Span);
            int conditionStart = _code.Count;

            var conditionType = EmitExpression(statement.Condition);
            RequireBool(conditionType, statement.Condition.Span, "the 'while' condition");

            int exitJump = EmitJump(OpCode.JumpIfFalse, line);

            var loop = new LoopContext { ContinueTarget = conditionStart };
            _function!.Loops.Add(loop);

            EmitBlock(statement.Body);
            Emit(OpCode.Jump, conditionStart, 0, line);

            PatchJump(exitJump);
            CloseLoop(loop);
        }

        private void EmitLoop(LoopStatement statement)
        {
            int line = LineOf(statement.Span);
            int bodyStart = _code.Count;

            var loop = new LoopContext { ContinueTarget = bodyStart };
            _function!.Loops.Add(loop);

            EmitBlock(statement.Body);
            Emit(OpCode.Jump, bodyStart, 0, line);

            CloseLoop(loop);
        }

        private void EmitFor(ForStatement statement)
        {
            int line = LineOf(statement.Span);

            // The loop variable and the limit live in their own scope, so the limit
            // is evaluated exactly once and the name does not leak outside.
            var saved = _scope;
            int savedSlot = _function!.NextLocalSlot;
            int savedHeap = _function.NextHeapOffset;
            _scope = new Scope(_scope);

            var startType = EmitExpression(statement.Start);
            RequireNumeric(startType, statement.Start.Span, "the start of the range");
            int indexSlot = AllocateLocal(statement.Span);
            Emit(OpCode.StoreLocal, indexSlot, 0, line);

            var endType = EmitExpression(statement.End);
            RequireNumeric(endType, statement.End.Span, "the end of the range");
            int limitSlot = AllocateLocal(statement.Span);
            Emit(OpCode.StoreLocal, limitSlot, 0, line);

            var indexSymbol = new VariableSymbol(statement.VariableName, IZType.Num, false, false, indexSlot);
            if (_scope.LookupLocal(statement.VariableName) != null)
            {
                _diagnostics.Report(IZErrorCode.DuplicateName, statement.VariableToken.Span,
                    "'" + statement.VariableName + "' was already declared in this scope");
            }
            _scope.TryDeclare(indexSymbol);

            int conditionStart = _code.Count;
            Emit(OpCode.LoadLocal, indexSlot, 0, line);
            Emit(OpCode.LoadLocal, limitSlot, 0, line);
            Emit(statement.IsInclusive ? OpCode.LessEqual : OpCode.Less, 0, 0, line);
            int exitJump = EmitJump(OpCode.JumpIfFalse, line);

            var loop = new LoopContext();
            _function.Loops.Add(loop);

            EmitBlock(statement.Body);

            // 'continue' lands here: it skips the rest of the body but still increments.
            loop.ContinueTarget = _code.Count;
            Emit(OpCode.LoadLocal, indexSlot, 0, line);
            Emit(OpCode.PushOne, 0, 0, line);
            Emit(OpCode.Add, 0, 0, line);
            Emit(OpCode.StoreLocal, indexSlot, 0, line);
            Emit(OpCode.Jump, conditionStart, 0, line);

            PatchJump(exitJump);
            CloseLoop(loop);

            _scope = saved;
            _function.NextLocalSlot = savedSlot;
            _function.NextHeapOffset = savedHeap;
        }

        private void CloseLoop(LoopContext loop)
        {
            foreach (int jump in loop.BreakJumps) PatchJump(jump);
            foreach (int jump in loop.ContinueJumps) PatchJumpTo(jump, loop.ContinueTarget);
            _function!.Loops.RemoveAt(_function.Loops.Count - 1);
        }

        private void EmitBreak(BreakStatement statement)
        {
            var loop = CurrentLoop();
            if (loop == null)
            {
                _diagnostics.Report(IZErrorCode.BreakOutsideLoop, statement.Span,
                    "'break' only works inside 'while', 'loop' or 'for'");
                return;
            }
            loop.BreakJumps.Add(EmitJump(OpCode.Jump, LineOf(statement.Span)));
        }

        private void EmitContinue(ContinueStatement statement)
        {
            var loop = CurrentLoop();
            if (loop == null)
            {
                _diagnostics.Report(IZErrorCode.ContinueOutsideLoop, statement.Span,
                    "'continue' only works inside 'while', 'loop' or 'for'");
                return;
            }
            loop.ContinueJumps.Add(EmitJump(OpCode.Jump, LineOf(statement.Span)));
        }

        private LoopContext? CurrentLoop() =>
            _function != null && _function.Loops.Count > 0
                ? _function.Loops[_function.Loops.Count - 1]
                : null;

        private void EmitReturn(ReturnStatement statement)
        {
            int line = LineOf(statement.Span);
            var symbol = _function!.Symbol;

            if (symbol.Name == "<entry>")
            {
                _diagnostics.Report(IZErrorCode.ReturnOutsideFunction, statement.Span,
                    "'return' only works inside a function");
                return;
            }

            if (statement.Value == null)
            {
                if (symbol.ReturnType != IZType.Void)
                {
                    _diagnostics.Report(IZErrorCode.MissingReturn, statement.Span,
                        "'" + symbol.Name + "' must return " + symbol.ReturnType.Display());
                }
                Emit(OpCode.Return, 0, 0, line);
                return;
            }

            var valueType = EmitExpression(statement.Value);

            if (symbol.ReturnType == IZType.Void)
            {
                _diagnostics.Report(IZErrorCode.ReturnValueFromVoid, statement.Value.Span,
                    "'" + symbol.Name + "' declares no return type; use plain 'return;'");
                Emit(OpCode.Pop, 0, 0, line);
                Emit(OpCode.Return, 0, 0, line);
                return;
            }

            if (!valueType.IsAssignableTo(symbol.ReturnType))
            {
                _diagnostics.Report(IZErrorCode.TypeMismatch, statement.Value.Span,
                    "'" + symbol.Name + "' returns " + symbol.ReturnType.Display() +
                    ", but this 'return' gives back " + valueType.Display());
            }

            Emit(OpCode.ReturnValue, 0, 0, line);
        }

        // ==================================================================
        //  Expression emission
        // ==================================================================

        private IZType EmitExpression(ExpressionSyntax expression)
        {
            switch (expression)
            {
                case LiteralExpression literal: return EmitLiteral(literal);
                case HashLiteralExpression hash: return EmitHashLiteral(hash);
                case NameExpression name: return EmitName(name);
                case UnaryExpression unary: return EmitUnary(unary);
                case BinaryExpression binary: return EmitBinary(binary);
                case ConditionalExpression conditional: return EmitConditional(conditional);
                case CallExpression call: return EmitCall(call);
                case MemberExpression member: return EmitMemberRead(member);
                case IndexExpression index: return EmitIndexRead(index);
                case ArrayLiteralExpression literal:
                    _diagnostics.Report(IZErrorCode.TypeMismatch, literal.Span,
                        "an array literal only works as the value of a declaration");
                    Emit(OpCode.PushZero, 0, 0, LineOf(literal.Span));
                    return IZType.Error;
                case BatchSelectorExpression selector:
                    _diagnostics.Report(IZErrorCode.NotADevice, selector.Span,
                        "a batch selector needs a property: 'all(X).On'");
                    Emit(OpCode.PushZero, 0, 0, LineOf(selector.Span));
                    return IZType.Error;
                case LambdaExpression lambda:
                    _diagnostics.Report(IZErrorCode.TypeMismatch, lambda.Span,
                        "'=>' only writes the body of a query method, like " +
                        "'xs.where(x => x > 0)'; it is not a value on its own");
                    Emit(OpCode.PushZero, 0, 0, LineOf(lambda.Span));
                    return IZType.Error;
                default:
                    Emit(OpCode.PushZero, 0, 0, LineOf(expression.Span));
                    return IZType.Error;
            }
        }

        private IZType EmitLiteral(LiteralExpression literal)
        {
            int line = LineOf(literal.Span);

            switch (literal.Value)
            {
                case double number:
                    EmitConstant(number, line);
                    return IZType.Num;

                case bool flag:
                    Emit(flag ? OpCode.PushOne : OpCode.PushZero, 0, 0, line);
                    return IZType.Bool;

                case string text:
                    EmitString(text, line, literal.Span);
                    return IZType.Str;

                default:
                    Emit(OpCode.PushZero, 0, 0, line);
                    return IZType.Error;
            }
        }

        private IZType EmitHashLiteral(HashLiteralExpression hash)
        {
            InternString(hash.PrefabName, hash.Span);
            EmitConstant(PrefabHash.Compute(hash.PrefabName), LineOf(hash.Span));
            return IZType.Num;
        }

        private IZType EmitName(NameExpression name)
        {
            int line = LineOf(name.Span);
            var symbol = _scope.Lookup(name.Name);

            switch (symbol)
            {
                case VariableSymbol variable:
                    EmitLoadVariable(variable, line);
                    return variable.Type;

                case DeviceSymbol _:
                    _diagnostics.Report(IZErrorCode.NotADevice, name.Span,
                        "'" + name.Name + "' is a device; read one of its properties, like '" +
                        name.Name + ".Pressure'");
                    Emit(OpCode.PushZero, 0, 0, line);
                    return IZType.Error;

                case FunctionSymbol _:
                    _diagnostics.Report(IZErrorCode.NotCallable, name.Span,
                        "'" + name.Name + "' is a function; to call it write '" + name.Name + "()'");
                    Emit(OpCode.PushZero, 0, 0, line);
                    return IZType.Error;

                default:
                    _diagnostics.Report(IZErrorCode.UndefinedName, name.Span,
                        "'" + name.Name + "' was not declared");
                    Emit(OpCode.PushZero, 0, 0, line);
                    return IZType.Error;
            }
        }

        private void EmitLoadVariable(VariableSymbol variable, int line)
        {
            if (variable.IsConst)
            {
                if (variable.ConstantString != null)
                    EmitString(variable.ConstantString, line, variable.DeclarationSpan);
                else
                    EmitConstant(variable.ConstantValue ?? 0.0, line);
                return;
            }
            Emit(variable.IsGlobal ? OpCode.LoadGlobal : OpCode.LoadLocal, variable.Slot, 0, line);
        }

        private IZType EmitUnary(UnaryExpression unary)
        {
            int line = LineOf(unary.Span);
            var operandType = EmitExpression(unary.Operand);

            switch (unary.OperatorToken.Kind)
            {
                case TokenKind.Minus:
                    RequireNumeric(operandType, unary.Operand.Span, "'-'");
                    Emit(OpCode.Negate, 0, 0, line);
                    return IZType.Num;

                case TokenKind.Bang:
                    RequireBool(operandType, unary.Operand.Span, "'!'");
                    Emit(OpCode.Not, 0, 0, line);
                    return IZType.Bool;

                case TokenKind.Tilde:
                    RequireNumeric(operandType, unary.Operand.Span, "'~'");
                    Emit(OpCode.BitNot, 0, 0, line);
                    return IZType.Num;

                default:
                    return IZType.Error;
            }
        }

        /// <summary>
        /// 'cond ? a : b'. The same jumps the 'if' statement emits, except that both
        /// branches leave one value behind, so only the branch taken is evaluated.
        /// </summary>
        private IZType EmitConditional(ConditionalExpression conditional)
        {
            int line = LineOf(conditional.Span);

            var conditionType = EmitExpression(conditional.Condition);
            RequireBool(conditionType, conditional.Condition.Span, "the condition of '?'");

            int jumpToElse = EmitJump(OpCode.JumpIfFalse, line);
            var trueType = EmitExpression(conditional.WhenTrue);
            int jumpOverElse = EmitJump(OpCode.Jump, line);

            PatchJump(jumpToElse);
            var falseType = EmitExpression(conditional.WhenFalse);
            PatchJump(jumpOverElse);

            return ConditionalResultType(conditional, trueType, falseType);
        }

        private IZType ConditionalResultType(ConditionalExpression conditional,
                                             IZType trueType, IZType falseType)
        {
            if (trueType == IZType.Error || falseType == IZType.Error) return IZType.Error;

            if (trueType == IZType.Void || falseType == IZType.Void)
            {
                _diagnostics.Report(IZErrorCode.TypeMismatch, conditional.Span,
                    "'?' has to choose between two values, and a call that returns " +
                    "nothing is not one");
                return IZType.Error;
            }

            // An aggregate lives on the stack as the address of its cells, so choosing
            // one would hand back storage, not a value.
            if (trueType.IsAggregate || falseType.IsAggregate)
            {
                _diagnostics.Report(IZErrorCode.TypeMismatch, conditional.Span,
                    "'?' cannot choose between an array, a list or a struct; choose " +
                    "between the values inside them instead");
                return IZType.Error;
            }

            if (trueType == falseType) return trueType;

            // bool widens to num everywhere else in the language, so it widens here too.
            if (trueType.IsAssignableTo(falseType)) return falseType;
            if (falseType.IsAssignableTo(trueType)) return trueType;

            _diagnostics.Report(IZErrorCode.TypeMismatch, conditional.Span,
                "the two sides of '?' have to be the same type, but one gives " +
                trueType.Display() + " and the other " + falseType.Display());
            return IZType.Error;
        }

        private IZType EmitBinary(BinaryExpression binary)
        {
            int line = LineOf(binary.Span);
            var kind = binary.OperatorToken.Kind;

            // && and || need short circuiting, so the right side is only emitted
            // after the jump - it cannot be handled together with the others.
            if (kind == TokenKind.AmpAmp || kind == TokenKind.PipePipe)
            {
                var leftLogical = EmitExpression(binary.Left);
                RequireBool(leftLogical, binary.Left.Span, "'" + binary.OperatorToken.Text + "'");

                int shortCircuit = EmitJump(
                    kind == TokenKind.AmpAmp ? OpCode.JumpIfFalseKeep : OpCode.JumpIfTrueKeep, line);

                var rightLogical = EmitExpression(binary.Right);
                RequireBool(rightLogical, binary.Right.Span, "'" + binary.OperatorToken.Text + "'");

                PatchJump(shortCircuit);
                return IZType.Bool;
            }

            var leftType = EmitExpression(binary.Left);
            var rightType = EmitExpression(binary.Right);

            // A str answers to '+' and to the comparisons, and to nothing else. It is
            // decided here, before the numeric rules, because those would only be able
            // to say "expects num" - which is not the mistake being made.
            if (leftType == IZType.Str || rightType == IZType.Str)
            {
                var stringResult = EmitStringBinary(binary, kind, leftType, rightType, line);
                if (stringResult != null) return stringResult;
            }

            switch (kind)
            {
                case TokenKind.Plus:
                case TokenKind.Minus:
                case TokenKind.Star:
                case TokenKind.Slash:
                case TokenKind.Percent:
                    RequireNumeric(leftType, binary.Left.Span, "'" + binary.OperatorToken.Text + "'");
                    RequireNumeric(rightType, binary.Right.Span, "'" + binary.OperatorToken.Text + "'");
                    Emit(ArithmeticOpCode(kind), 0, 0, line);
                    return IZType.Num;

                case TokenKind.Amp:
                case TokenKind.Pipe:
                case TokenKind.Caret:
                case TokenKind.LessLess:
                case TokenKind.GreaterGreater:
                    RequireNumeric(leftType, binary.Left.Span, "'" + binary.OperatorToken.Text + "'");
                    RequireNumeric(rightType, binary.Right.Span, "'" + binary.OperatorToken.Text + "'");
                    Emit(BitwiseOpCode(kind), 0, 0, line);
                    return IZType.Num;

                case TokenKind.Less:
                case TokenKind.LessEquals:
                case TokenKind.Greater:
                case TokenKind.GreaterEquals:
                    RequireNumeric(leftType, binary.Left.Span, "'" + binary.OperatorToken.Text + "'");
                    RequireNumeric(rightType, binary.Right.Span, "'" + binary.OperatorToken.Text + "'");
                    Emit(ComparisonOpCode(kind), 0, 0, line);
                    return IZType.Bool;

                case TokenKind.EqualsEquals:
                case TokenKind.BangEquals:
                    if (leftType.IsAggregate || rightType.IsAggregate)
                    {
                        // Comparing addresses would answer "is it the same storage",
                        // which is never the question being asked.
                        _diagnostics.Report(IZErrorCode.TypeMismatch, binary.Span,
                            "an array or a struct cannot be compared; compare their " +
                            "elements or fields");
                    }
                    else if (!leftType.IsAssignableTo(rightType) && !rightType.IsAssignableTo(leftType))
                    {
                        _diagnostics.Report(IZErrorCode.TypeMismatch, binary.Span,
                            "cannot compare " + leftType.Display() + " with " + rightType.Display());
                    }
                    Emit(kind == TokenKind.EqualsEquals ? OpCode.Equal : OpCode.NotEqual, 0, 0, line);
                    return IZType.Bool;

                default:
                    return IZType.Error;
            }
        }

        /// <summary>
        /// The operators a str takes: '+' joins two of them, and the six comparisons
        /// all fold into one <see cref="OpCode.StrCompare"/> against zero - ordinal,
        /// so "A" comes before "a". Null means this is not a string operator and the
        /// numeric rules should have their say.
        /// </summary>
        private IZType? EmitStringBinary(BinaryExpression binary, TokenKind kind,
                                         IZType leftType, IZType rightType, int line)
        {
            bool isConcat = kind == TokenKind.Plus;
            bool isComparison =
                kind == TokenKind.EqualsEquals || kind == TokenKind.BangEquals ||
                kind == TokenKind.Less || kind == TokenKind.LessEquals ||
                kind == TokenKind.Greater || kind == TokenKind.GreaterEquals;

            if (!isConcat && !isComparison) return null;

            var other = leftType == IZType.Str ? rightType : leftType;
            if (other != IZType.Str && other != IZType.Error)
            {
                string hint = other == IZType.Num || other == IZType.Bool
                    ? "; a number becomes text with 'text(x)'"
                    : string.Empty;

                _diagnostics.Report(IZErrorCode.TypeMismatch, binary.Span,
                    "'" + binary.OperatorToken.Text + "' takes two str here, but got " +
                    leftType.Display() + " and " + rightType.Display() + hint);
            }

            if (isConcat)
            {
                Emit(OpCode.StrConcat, 0, 0, line);
                return IZType.Str;
            }

            Emit(OpCode.StrCompare, 0, 0, line);
            Emit(OpCode.PushZero, 0, 0, line);
            Emit(StringComparisonOpCode(kind), 0, 0, line);
            return IZType.Bool;
        }

        private static OpCode StringComparisonOpCode(TokenKind kind)
        {
            switch (kind)
            {
                case TokenKind.EqualsEquals: return OpCode.Equal;
                case TokenKind.BangEquals: return OpCode.NotEqual;
                default: return ComparisonOpCode(kind);
            }
        }

        private static OpCode ArithmeticOpCode(TokenKind kind)
        {
            switch (kind)
            {
                case TokenKind.Plus: return OpCode.Add;
                case TokenKind.Minus: return OpCode.Subtract;
                case TokenKind.Star: return OpCode.Multiply;
                case TokenKind.Slash: return OpCode.Divide;
                default: return OpCode.Modulo;
            }
        }

        private static OpCode BitwiseOpCode(TokenKind kind)
        {
            switch (kind)
            {
                case TokenKind.Amp: return OpCode.BitAnd;
                case TokenKind.Pipe: return OpCode.BitOr;
                case TokenKind.Caret: return OpCode.BitXor;
                case TokenKind.LessLess: return OpCode.ShiftLeft;
                default: return OpCode.ShiftRight;
            }
        }

        private static OpCode ComparisonOpCode(TokenKind kind)
        {
            switch (kind)
            {
                case TokenKind.Less: return OpCode.Less;
                case TokenKind.LessEquals: return OpCode.LessEqual;
                case TokenKind.Greater: return OpCode.Greater;
                default: return OpCode.GreaterEqual;
            }
        }

        private IZType EmitCall(CallExpression call)
        {
            int line = LineOf(call.Span);

            // xs.where(...), xs.add(...): a method on a list, compiled into a loop
            // over its cells. It is the only call whose callee is not a plain name.
            if (TryEmitQueryCall(call, out var queryResult)) return queryResult;

            if (!(call.Callee is NameExpression callee))
            {
                _diagnostics.Report(IZErrorCode.NotCallable, call.Callee.Span,
                    call.Callee is MemberExpression method
                        ? "'" + method.MemberName + "' is not a method; a function is called by name"
                        : "a function can only be called by name");
                Emit(OpCode.PushZero, 0, 0, line);
                return IZType.Error;
            }

            // 'sleep' is a scheduling statement, not a library function:
            // it needs its own opcode to suspend the VM.
            if (callee.Name == "sleep")
                return EmitSleep(call, line);

            // 'len' over an array asks the type, not the value: the length is written
            // into it, so that form folds into a constant. Over a str it does not.
            if (callee.Name == "len")
                return EmitLen(call, line);

            // 'isset' asks about a pin, not about a value on the stack: the device is
            // a compile time thing, so the pin ends up in the instruction itself.
            if (callee.Name == "isset")
                return EmitIsSet(call, line);

            if (Builtins.TryGet(callee.Name, out var builtin))
            {
                if (call.Arguments.Count != builtin.Arity)
                {
                    _diagnostics.Report(IZErrorCode.WrongArgumentCount, call.Span,
                        "'" + builtin.Name + "' takes " + builtin.Arity +
                        " argument(s), got " + call.Arguments.Count);
                }

                // 'hash("...")' is the same value as #"..." and deserves to cost the
                // same: nothing at all.
                if (builtin.Id == BuiltinId.Hash && call.Arguments.Count == 1 &&
                    EmitHashOfConstantString(call.Arguments[0], line))
                {
                    return IZType.Num;
                }

                for (int i = 0; i < call.Arguments.Count; i++)
                {
                    var argument = call.Arguments[i];
                    var argumentType = EmitExpression(argument);

                    if (i >= builtin.Arity) continue;
                    RequireBuiltinArgument(builtin, i, argumentType, argument.Span);
                }

                // Extra arguments would be stranded on the stack; drop the surplus.
                for (int i = builtin.Arity; i < call.Arguments.Count; i++)
                    Emit(OpCode.Pop, 0, 0, line);
                for (int i = call.Arguments.Count; i < builtin.Arity; i++)
                    Emit(OpCode.PushZero, 0, 0, line);

                Emit(OpCode.CallBuiltin, (int)builtin.Id, builtin.Arity, line);
                return FromBuiltinType(builtin.Returns);
            }

            if (!(_scope.Lookup(callee.Name) is FunctionSymbol function))
            {
                _diagnostics.Report(IZErrorCode.UndefinedName, callee.Span,
                    "there is no function called '" + callee.Name + "'");
                foreach (var argument in call.Arguments)
                {
                    EmitExpression(argument);
                    Emit(OpCode.Pop, 0, 0, line);
                }
                Emit(OpCode.PushZero, 0, 0, line);
                return IZType.Error;
            }

            if (call.Arguments.Count != function.Parameters.Count)
            {
                _diagnostics.Report(IZErrorCode.WrongArgumentCount, call.Span,
                    "'" + function.Name + "' takes " + function.Parameters.Count +
                    " argument(s), got " + call.Arguments.Count);
            }

            int shared = Math.Min(call.Arguments.Count, function.Parameters.Count);
            for (int i = 0; i < shared; i++)
            {
                var argumentType = EmitExpression(call.Arguments[i]);
                var parameter = function.Parameters[i];
                if (!argumentType.IsAssignableTo(parameter.Type))
                {
                    _diagnostics.Report(IZErrorCode.TypeMismatch, call.Arguments[i].Span,
                        "parameter '" + parameter.Name + "' is " + parameter.Type.Display() +
                        ", but it got " + argumentType.Display());
                }
            }

            // When the count does not match, emit anyway using the function's arity:
            // the program will not run (an error was already reported), but the stack
            // stays coherent and the following errors keep making sense.
            for (int i = shared; i < call.Arguments.Count; i++)
            {
                EmitExpression(call.Arguments[i]);
                Emit(OpCode.Pop, 0, 0, line);
            }
            for (int i = shared; i < function.Parameters.Count; i++)
                Emit(OpCode.PushZero, 0, 0, line);

            Emit(OpCode.Call, function.Index, function.Parameters.Count, line);
            return function.ReturnType;
        }

        /// <summary>
        /// 'isset(dev)' - is there a device on that pin right now?
        ///
        /// Unlike every other call the argument is not evaluated: a device is a name
        /// for a pin, settled at compile time, so the pin travels in operand A and
        /// nothing is pushed for it.
        /// </summary>
        private IZType EmitIsSet(CallExpression call, int line)
        {
            if (call.Arguments.Count != 1)
            {
                _diagnostics.Report(IZErrorCode.WrongArgumentCount, call.Span,
                    "'isset' takes 1 argument (the device), got " + call.Arguments.Count);
                foreach (var argument in call.Arguments)
                {
                    // A device name is a use of it, but not a value: evaluating it
                    // would pile a second diagnostic on top of the count.
                    if (argument is NameExpression named &&
                        _scope.Lookup(named.Name) is DeviceSymbol) continue;

                    EmitExpression(argument);
                    Emit(OpCode.Pop, 0, 0, line);
                }
                Emit(OpCode.PushZero, 0, 0, line);
                return IZType.Bool;
            }

            var target = call.Arguments[0];

            if (target is NameExpression name)
            {
                var symbol = _scope.Lookup(name.Name);

                if (symbol is DeviceSymbol device)
                {
                    if (device.IsBatch)
                    {
                        _diagnostics.Report(IZErrorCode.NotADevice, target.Span,
                            "'" + name.Name + "' is " + device.Description +
                            ", and a selector reaches a set rather than one pin; " +
                            "'isset' answers for a device declared on a pin");
                        Emit(OpCode.PushZero, 0, 0, line);
                        return IZType.Bool;
                    }

                    Emit(OpCode.DevicePresent, device.Pin, 0, line);
                    return IZType.Bool;
                }

                // 'isset(d0)' - a pin written straight into the call, with no name
                // declared for it. It means the same as a declared name would.
                if (symbol == null && DevicePins.TryParse(name.Name, out int pin))
                {
                    Emit(OpCode.DevicePresent, pin, 0, line);
                    return IZType.Bool;
                }
            }

            _diagnostics.Report(IZErrorCode.NotADevice, target.Span,
                "'isset' takes a device declared with 'device'");

            // A name has nothing to run, and evaluating it would only add a second
            // diagnostic on top of the one above. Anything else may have side effects.
            if (!(target is NameExpression))
            {
                EmitExpression(target);
                Emit(OpCode.Pop, 0, 0, line);
            }

            Emit(OpCode.PushZero, 0, 0, line);
            return IZType.Bool;
        }

        private IZType EmitLen(CallExpression call, int line)
        {
            if (call.Arguments.Count != 1)
            {
                _diagnostics.Report(IZErrorCode.WrongArgumentCount, call.Span,
                    "'len' takes 1 argument (the array), got " + call.Arguments.Count);
                foreach (var argument in call.Arguments)
                {
                    EmitExpression(argument);
                    Emit(OpCode.Pop, 0, 0, line);
                }
                Emit(OpCode.PushZero, 0, 0, line);
                return IZType.Num;
            }

            var argumentType = EmitExpression(call.Arguments[0]);

            // A string has its length only while it runs, so this one is a real call.
            if (argumentType == IZType.Str)
            {
                Emit(OpCode.CallBuiltin, (int)BuiltinId.Len, 1, line);
                return IZType.Num;
            }

            // For an array the argument is dropped instead: 'len(next())' has to keep
            // whatever the call did, even though the length itself is a constant.
            Emit(OpCode.Pop, 0, 0, line);

            if (argumentType.Kind != IZTypeKind.Array && argumentType.Kind != IZTypeKind.List)
            {
                if (argumentType != IZType.Error)
                {
                    _diagnostics.Report(IZErrorCode.TypeMismatch, call.Arguments[0].Span,
                        "'len' takes an array, a list or a str, not " + argumentType.Display());
                }
                Emit(OpCode.PushZero, 0, 0, line);
                return IZType.Num;
            }

            // The length of a list is the room it has. How much of it is in use is
            // 'count', and only the running program knows that one.

            EmitConstant(argumentType.Length, line);
            return IZType.Num;
        }

        private IZType EmitSleep(CallExpression call, int line)
        {
            if (call.Arguments.Count != 1)
            {
                _diagnostics.Report(IZErrorCode.WrongArgumentCount, call.Span,
                    "'sleep' takes 1 argument (seconds), got " + call.Arguments.Count);
                foreach (var argument in call.Arguments)
                {
                    EmitExpression(argument);
                    Emit(OpCode.Pop, 0, 0, line);
                }
                return IZType.Void;
            }

            var secondsType = EmitExpression(call.Arguments[0]);
            RequireNumeric(secondsType, call.Arguments[0].Span, "'sleep'");
            Emit(OpCode.Sleep, 0, 0, line);
            return IZType.Void;
        }

        private IZType EmitMemberRead(MemberExpression member)
        {
            int line = LineOf(member.Span);

            // pump.Pressure
            if (member.Target is NameExpression name && _scope.Lookup(name.Name) is DeviceSymbol device)
            {
                if (!TryResolveLogicType(member, out int logicType))
                {
                    Emit(OpCode.PushZero, 0, 0, line);
                    return IZType.Error;
                }

                // A batch device reads exactly like the selector written inline:
                // averaged over everything it matches.
                if (device.IsBatch)
                {
                    EmitBatchLoad(device, logicType, BatchAggregation.Average, line);
                    return IZType.Num;
                }

                Emit(OpCode.DeviceLoad, device.Pin, logicType, line);
                return IZType.Num;
            }

            // all(X).Pressure - batch read, averaged (like IC10's 'lb')
            if (member.Target is BatchSelectorExpression selector)
            {
                if (!TryResolveLogicType(member, out int logicType))
                {
                    Emit(OpCode.PushZero, 0, 0, line);
                    return IZType.Error;
                }
                EmitBatchLoad(selector, logicType, BatchAggregation.Average, line);
                return IZType.Num;
            }

            // chute.slot[0].Quantity - the IndexExpression handles this
            if (member.Target is IndexExpression indexed && IsDeviceSlotAccess(indexed))
                return EmitSlotRead(indexed, member, line);

            // Color.Black, AirCon.Cold, GasType.Oxygen - one of the game's named
            // values. It folds to the number it stands for, so it costs nothing.
            if (IsConstantGroupAccess(member))
            {
                TryResolveConstant(member, out int constant);
                EmitConstant(constant, line);
                return IZType.Num;
            }

            // p.x - a struct field. The address is the value when the field is itself
            // an array or a struct, so only a scalar is actually read.
            var fieldType = EmitFieldAddress(member);
            if (fieldType.IsAggregate || fieldType == IZType.Error) return fieldType;

            Emit(OpCode.LoadHeap, 0, 0, line);
            return fieldType;
        }

        /// <summary>
        /// Is this 'device.slot[i]'? Only then does indexing mean a slot; anything
        /// else with brackets is an array. The device may be a pin, a name declared
        /// from a selector, or a selector written where it is used.
        /// </summary>
        private bool IsDeviceSlotAccess(IndexExpression index) =>
            index.Target is MemberExpression member &&
            string.Equals(member.MemberName, "slot", StringComparison.Ordinal) &&
            (member.Target is BatchSelectorExpression ||
             (member.Target is NameExpression name && _scope.LookupNoUse(name.Name) is DeviceSymbol));

        /// <summary>
        /// Emits the address of a struct field, leaving it on the stack.
        /// Returns the type stored there, or Error when the target is not a struct.
        /// </summary>
        private IZType EmitFieldAddress(MemberExpression member, bool forWrite = false)
        {
            int line = LineOf(member.Span);
            var targetType = EmitExpression(member.Target);

            // A list opens with its count, so the address of the list is already the
            // address of that cell and there is nothing to offset.
            if (targetType.Kind == IZTypeKind.List)
            {
                if (!string.Equals(member.MemberName, "count", StringComparison.Ordinal))
                {
                    _diagnostics.Report(IZErrorCode.UnknownField, member.MemberToken.Span,
                        "a list has 'count'; its items are read with brackets, and a " +
                        "method is called with parentheses");
                    return IZType.Error;
                }

                if (forWrite)
                {
                    _diagnostics.Report(IZErrorCode.InvalidAssignmentTarget, member.MemberToken.Span,
                        "'count' says how many items the list holds; it changes through " +
                        "'add', 'removeAt' and 'clear'");
                }

                return IZType.Num;
            }

            if (targetType.Kind != IZTypeKind.Struct)
            {
                if (targetType != IZType.Error)
                {
                    _diagnostics.Report(IZErrorCode.NotADevice, member.Target.Span,
                        "'.' works on a device, a batch selector, a struct or a list, not on " +
                        targetType.Display());
                }
                return IZType.Error;
            }

            var structSymbol = targetType.Struct!;
            var field = structSymbol.FindField(member.MemberName);
            if (field == null)
            {
                _diagnostics.Report(IZErrorCode.UnknownField, member.MemberToken.Span,
                    "'" + structSymbol.Name + "' has no field called '" + member.MemberName + "'" +
                    SuggestFieldName(structSymbol, member.MemberName));
                return IZType.Error;
            }

            if (field.Offset != 0) Emit(OpCode.FieldRef, field.Offset, 0, line);
            return field.Type;
        }

        /// <summary>What the pieces of an aggregate are called, for the messages.</summary>
        private static string PartsOf(IZType type)
        {
            switch (type.Kind)
            {
                case IZTypeKind.Array: return "elements";
                case IZTypeKind.List: return "items, through 'add' and the index";
                default: return "fields";
            }
        }

        private static string SuggestFieldName(StructSymbol structSymbol, string name)
        {
            var names = new List<string>(structSymbol.Fields.Count);
            foreach (var field in structSymbol.Fields) names.Add(field.Name);

            return GameEnums.Suggest(names, name);
        }

        /// <summary>
        /// Emits the address of an array element, leaving it on the stack.
        /// Returns the element type, or Error when the target is not an array.
        /// </summary>
        private IZType EmitElementAddress(IndexExpression index)
        {
            int line = LineOf(index.Span);
            var targetType = EmitExpression(index.Target);

            // A list is bounded by its count, which only exists while it runs: there
            // is no constant to check here, and the check moves into the instruction.
            if (targetType.Kind == IZTypeKind.List)
            {
                var listIndexType = EmitExpression(index.Index);
                RequireNumeric(listIndexType, index.Index.Span, "a list index");

                var listElement = targetType.ElementType!;
                Emit(OpCode.ListIndexRef, listElement.Size, targetType.Length, line);
                return listElement;
            }

            if (targetType.Kind != IZTypeKind.Array)
            {
                if (targetType != IZType.Error)
                {
                    _diagnostics.Report(IZErrorCode.TypeMismatch, index.Target.Span,
                        "only an array or a list can be indexed, and this is " +
                        targetType.Display());
                }
                return IZType.Error;
            }

            // A constant index is checked here, where the message can point at the
            // bracket instead of stopping the chip halfway through a tick.
            if (TryEvaluateConstant(index.Index, out double constant) != IZType.Error &&
                (constant < 0 || constant >= targetType.Length || constant != Math.Truncate(constant)))
            {
                _diagnostics.Report(IZErrorCode.IndexOutOfRange, index.Index.Span,
                    "index " + constant.ToString(CultureInfo.InvariantCulture) +
                    " is outside " + targetType.Display());
            }

            var indexType = EmitExpression(index.Index);
            RequireNumeric(indexType, index.Index.Span, "an array index");

            var element = targetType.ElementType!;
            Emit(OpCode.IndexRef, element.Size, targetType.Length, line);
            return element;
        }

        /// <summary>
        /// Reads 'x.slot[i].Property'.
        ///
        /// Over a pin that is one reading. Over a selector it is the same sequence a
        /// batch property read gives - one reading per device that has that slot - and
        /// <paramref name="aggregation"/> says which of the game's five modes collapses
        /// it. That is what lets a program watch the same slot on more trays than the
        /// housing has pins.
        /// </summary>
        private IZType EmitSlotRead(IndexExpression indexed, MemberExpression member, int line,
                                    BatchAggregation aggregation = BatchAggregation.Average)
        {
            if (!TryResolveSlotSource(indexed, out var device, out var selector))
            {
                _diagnostics.Report(IZErrorCode.NotADevice, indexed.Span,
                    "indexing only works in the form 'device.slot[i].Property'");
                Emit(OpCode.PushZero, 0, 0, line);
                return IZType.Error;
            }

            if (!GameEnums.LogicSlotTypeByName.TryGetValue(member.MemberName, out int slotLogicType))
            {
                _diagnostics.Report(IZErrorCode.UnknownLogicType, member.MemberToken.Span,
                    "'" + member.MemberName + "' is not a known slot property");
                Emit(OpCode.PushZero, 0, 0, line);
                return IZType.Error;
            }

            // The selector operands go first, the slot index last: that is the order
            // the batch instructions pop them in.
            if (selector != null) EmitBatchSelectorOperands(selector, line);
            else if (device!.IsBatch) EmitDeviceSelectorOperands(device, line);

            var indexType = EmitExpression(indexed.Index);
            RequireNumeric(indexType, indexed.Index.Span, "the slot index");

            bool named = selector != null
                ? selector.Kind != BatchSelectorKind.All
                : device!.HasLabel;

            if (selector != null || device!.IsBatch)
                Emit(named ? OpCode.BatchNamedSlotLoad : OpCode.BatchSlotLoad,
                     slotLogicType, (int)aggregation, line);
            else
                Emit(OpCode.DeviceSlotLoad, device.Pin, slotLogicType, line);

            return IZType.Num;
        }

        /// <summary>
        /// What a 'x.slot[i]' reads from: a device name (a pin or a selector given a
        /// name), or a selector written where it is used. Exactly one of the two
        /// outputs is set when this answers true.
        /// </summary>
        private bool TryResolveSlotSource(IndexExpression indexed, out DeviceSymbol? device,
                                          out BatchSelectorExpression? selector)
        {
            device = null;
            selector = null;

            if (!(indexed.Target is MemberExpression slotMember) ||
                !string.Equals(slotMember.MemberName, "slot", StringComparison.Ordinal))
                return false;

            if (slotMember.Target is BatchSelectorExpression inline)
            {
                selector = inline;
                return true;
            }

            if (slotMember.Target is NameExpression deviceName &&
                _scope.Lookup(deviceName.Name) is DeviceSymbol resolved)
            {
                device = resolved;
                return true;
            }

            return false;
        }

        private IZType EmitIndexRead(IndexExpression index)
        {
            int line = LineOf(index.Span);

            if (IsDeviceSlotAccess(index))
            {
                _diagnostics.Report(IZErrorCode.NotADevice, index.Span,
                    "a slot on its own is not a value; read one of its properties, " +
                    "like '.Quantity'");
                Emit(OpCode.PushZero, 0, 0, line);
                return IZType.Error;
            }

            var elementType = EmitElementAddress(index);
            if (elementType.IsAggregate || elementType == IZType.Error) return elementType;

            Emit(OpCode.LoadHeap, 0, 0, line);
            return elementType;
        }

        /// <summary>
        /// Pushes the operands for a device declared from a selector. The hashes were
        /// folded when the name was declared, so this is two constants and no work.
        /// </summary>
        private void EmitDeviceSelectorOperands(DeviceSymbol device, int line)
        {
            EmitConstant(device.PrefabHash, line);
            if (device.HasLabel) EmitConstant(device.LabelHash, line);
        }

        /// <summary>
        /// The batch read itself, for a name declared from a selector. The mode is
        /// Average unless one of the terminals asked for another one.
        /// </summary>
        private void EmitBatchLoad(DeviceSymbol device, int logicType,
                                   BatchAggregation aggregation, int line)
        {
            EmitDeviceSelectorOperands(device, line);
            Emit(device.HasLabel ? OpCode.BatchNamedLoad : OpCode.BatchLoad,
                 logicType, (int)aggregation, line);
        }

        /// <summary>Same read, for the selector written where it is used.</summary>
        private void EmitBatchLoad(BatchSelectorExpression selector, int logicType,
                                   BatchAggregation aggregation, int line)
        {
            EmitBatchSelectorOperands(selector, line);
            Emit(selector.Kind == BatchSelectorKind.All ? OpCode.BatchLoad : OpCode.BatchNamedLoad,
                 logicType, (int)aggregation, line);
        }

        /// <summary>
        /// 'lights.On = true;' where 'lights' is a batch device - the same write the
        /// inline selector would do, and with the same restriction: a batch has no
        /// single value to read back, so '+=' and its siblings have nothing to build on.
        /// </summary>
        private void EmitBatchDeviceAssignment(DeviceSymbol device, MemberExpression member,
                                               AssignmentStatement assignment, int logicType, int line)
        {
            if (assignment.Kind != AssignmentKind.Assign)
            {
                _diagnostics.Report(IZErrorCode.InvalidAssignmentTarget, assignment.Span,
                    "'" + device.Name + "' is " + device.Description +
                    ", and a batch write does not accept '" + assignment.OperatorToken.Text +
                    "'; read and write in separate steps");
                return;
            }

            EmitDeviceSelectorOperands(device, line);

            var valueType = EmitExpression(assignment.Value);
            if (!valueType.IsAssignableTo(IZType.Num))
            {
                _diagnostics.Report(IZErrorCode.TypeMismatch, assignment.Value.Span,
                    "a batch write takes num (or bool), not " + valueType.Display());
            }

            Emit(device.HasLabel ? OpCode.BatchNamedStore : OpCode.BatchStore, logicType, 0, line);
        }

        /// <summary>
        /// Pushes the operands the batch instructions expect, in this order:
        /// the prefab hash and, for 'named', the label hash. The value to write,
        /// when there is one, is emitted by the caller after these.
        /// </summary>
        private void EmitBatchSelectorOperands(BatchSelectorExpression selector, int line)
        {
            if (selector.Prefab != null)
                EmitPrefabOperand(selector.Prefab, line, selector.Kind == BatchSelectorKind.All ? "all" : "named");
            else
                Emit(OpCode.PushZero, 0, 0, line);   // 0 = matches any prefab

            if (selector.Kind == BatchSelectorKind.All) return;

            if (selector.Label == null)
            {
                // The parser already reported the argument count.
                Emit(OpCode.PushZero, 0, 0, line);
                return;
            }

            if (EmitHashOfConstantString(selector.Label, line)) return;

            var labelType = EmitExpression(selector.Label);
            if (labelType == IZType.Str)
            {
                Emit(OpCode.CallBuiltin, (int)BuiltinId.Hash, 1, line);
                return;
            }
            if (labelType != IZType.Num && labelType != IZType.Error)
            {
                _diagnostics.Report(IZErrorCode.TypeMismatch, selector.Label.Span,
                    "the label of 'named' is text or a hash, not " + labelType.Display());
            }
        }

        /// <summary>
        /// Pushes the hash of a prefab.
        ///
        /// A bare identifier counts as a raw prefab name - <c>all(StructurePump)</c>.
        /// But if that identifier is a declared name, the declaration wins: using its
        /// value is the only defensible behaviour, and the alternative would silently
        /// hash the variable's name.
        /// </summary>
        private void EmitPrefabOperand(ExpressionSyntax expression, int line, string context)
        {
            if (expression is NameExpression name && _scope.Lookup(name.Name) == null)
            {
                InternString(name.Name, expression.Span);
                EmitConstant(PrefabHash.Compute(name.Name), line);
                return;
            }

            if (EmitHashOfConstantString(expression, line)) return;

            var type = EmitExpression(expression);
            if (type == IZType.Str)
            {
                // The text is only known while the program runs, so the hash is too.
                Emit(OpCode.CallBuiltin, (int)BuiltinId.Hash, 1, line);
                return;
            }
            if (type != IZType.Num && type != IZType.Error)
            {
                _diagnostics.Report(IZErrorCode.TypeMismatch, expression.Span,
                    "'" + context + "' takes a prefab name or a hash, not " + type.Display());
            }
        }

        private bool TryResolveLogicType(MemberExpression member, out int logicType)
        {
            if (GameEnums.LogicTypeByName.TryGetValue(member.MemberName, out logicType))
                return true;

            _diagnostics.Report(IZErrorCode.UnknownLogicType, member.MemberToken.Span,
                "'" + member.MemberName + "' is not a known device property" +
                GameEnums.Suggest(GameEnums.LogicTypeByName.Keys, member.MemberName));
            return false;
        }

        /// <summary>
        /// Is this 'Color.Black' - a name that is one of the game's constant groups
        /// and nothing else?
        ///
        /// A declaration always wins: naming a variable 'Color' shadows the group,
        /// the same way it shadows anything else. Nothing is marked as used here,
        /// because this only asks how to read the expression.
        /// </summary>
        private bool IsConstantGroupAccess(MemberExpression member) =>
            member.Target is NameExpression name &&
            _scope.LookupNoUse(name.Name) == null &&
            GameEnums.IsConstantGroup(name.Name);

        /// <summary>
        /// Resolves 'Color.Black' to its number. Only call it once
        /// <see cref="IsConstantGroupAccess"/> has said the group exists.
        /// </summary>
        private bool TryResolveConstant(MemberExpression member, out int value)
        {
            string group = ((NameExpression)member.Target).Name;
            if (GameEnums.TryGetConstant(group, member.MemberName, out value)) return true;

            if (_reportedConstants.Add(member.MemberToken.Span.Start))
            {
                var values = GameEnums.FindConstantGroup(group)!;
                _diagnostics.Report(IZErrorCode.UnknownConstant, member.MemberToken.Span,
                    "'" + group + "' has no value named '" + member.MemberName + "'" +
                    GameEnums.Suggest(values.Keys, member.MemberName));
            }

            return false;
        }

        // ==================================================================
        //  Constant folding
        // ==================================================================

        /// <summary>
        /// Evaluates a str expression at compile time: a literal, a str const, or the
        /// two joined with '+'. That is what lets a const hold text and a label keep
        /// costing nothing at runtime.
        /// </summary>
        private bool TryEvaluateConstantString(ExpressionSyntax? expression, out string? text)
        {
            text = null;

            switch (expression)
            {
                case LiteralExpression literal when literal.Value is string literalText:
                    text = literalText;
                    return true;

                case NameExpression name
                    when _scope.Lookup(name.Name) is VariableSymbol variable &&
                         variable.IsConst && variable.ConstantString != null:
                    text = variable.ConstantString;
                    return true;

                case BinaryExpression binary when binary.OperatorToken.Kind == TokenKind.Plus:
                {
                    if (!TryEvaluateConstantString(binary.Left, out string? left)) return false;
                    if (!TryEvaluateConstantString(binary.Right, out string? right)) return false;
                    text = left + right;
                    return text!.Length <= IZLimits.MaxStringLength;
                }

                default:
                    return false;
            }
        }

        /// <summary>
        /// Evaluates an expression at compile time. Returns IZType.Error when the
        /// expression depends on something only known at runtime.
        /// </summary>
        private IZType TryEvaluateConstant(ExpressionSyntax expression, out double value)
        {
            value = 0.0;

            switch (expression)
            {
                case LiteralExpression literal:
                    switch (literal.Value)
                    {
                        case double number: value = number; return IZType.Num;
                        case bool flag: value = flag ? 1.0 : 0.0; return IZType.Bool;
                        // A string is not a number any more: it folds through
                        // TryEvaluateConstantString instead.
                        default: return IZType.Error;
                    }

                case HashLiteralExpression hash:
                    value = PrefabHash.Compute(hash.PrefabName);
                    return IZType.Num;

                case NameExpression name:
                    if (_scope.Lookup(name.Name) is VariableSymbol variable &&
                        variable.IsConst && variable.ConstantValue.HasValue)
                    {
                        value = variable.ConstantValue.Value;
                        return variable.Type;
                    }
                    return IZType.Error;

                // Color.Black is as constant as 7 is, so it may size an array or
                // seed a 'const' like any other literal. An unknown value is still
                // a number - it folds to 0, and the error is already reported - so
                // that a typo does not also draw 'this is not a constant'.
                case MemberExpression member when IsConstantGroupAccess(member):
                    TryResolveConstant(member, out int constant);
                    value = constant;
                    return IZType.Num;

                case UnaryExpression unary:
                {
                    var operandType = TryEvaluateConstant(unary.Operand, out double operand);
                    if (operandType == IZType.Error) return IZType.Error;

                    switch (unary.OperatorToken.Kind)
                    {
                        case TokenKind.Minus: value = -operand; return IZType.Num;
                        case TokenKind.Bang: value = operand != 0.0 ? 0.0 : 1.0; return IZType.Bool;
                        case TokenKind.Tilde: value = ~(long)operand; return IZType.Num;
                        default: return IZType.Error;
                    }
                }

                case BinaryExpression binary:
                {
                    var leftType = TryEvaluateConstant(binary.Left, out double left);
                    if (leftType == IZType.Error) return IZType.Error;
                    var rightType = TryEvaluateConstant(binary.Right, out double right);
                    if (rightType == IZType.Error) return IZType.Error;

                    switch (binary.OperatorToken.Kind)
                    {
                        case TokenKind.Plus: value = left + right; return IZType.Num;
                        case TokenKind.Minus: value = left - right; return IZType.Num;
                        case TokenKind.Star: value = left * right; return IZType.Num;
                        case TokenKind.Slash: value = left / right; return IZType.Num;
                        case TokenKind.Percent:
                            value = right == 0.0 ? double.NaN : left % right;
                            return IZType.Num;

                        case TokenKind.Less: value = left < right ? 1 : 0; return IZType.Bool;
                        case TokenKind.LessEquals: value = left <= right ? 1 : 0; return IZType.Bool;
                        case TokenKind.Greater: value = left > right ? 1 : 0; return IZType.Bool;
                        case TokenKind.GreaterEquals: value = left >= right ? 1 : 0; return IZType.Bool;
                        case TokenKind.EqualsEquals: value = left == right ? 1 : 0; return IZType.Bool;
                        case TokenKind.BangEquals: value = left != right ? 1 : 0; return IZType.Bool;

                        case TokenKind.AmpAmp:
                            value = (left != 0.0 && right != 0.0) ? 1 : 0; return IZType.Bool;
                        case TokenKind.PipePipe:
                            value = (left != 0.0 || right != 0.0) ? 1 : 0; return IZType.Bool;

                        case TokenKind.Amp: value = (long)left & (long)right; return IZType.Num;
                        case TokenKind.Pipe: value = (long)left | (long)right; return IZType.Num;
                        case TokenKind.Caret: value = (long)left ^ (long)right; return IZType.Num;
                        case TokenKind.LessLess: value = (long)left << (int)((long)right & 63); return IZType.Num;
                        case TokenKind.GreaterGreater: value = (long)left >> (int)((long)right & 63); return IZType.Num;

                        default: return IZType.Error;
                    }
                }

                default:
                    return IZType.Error;
            }
        }

        // ==================================================================
        //  Emission helpers
        // ==================================================================

        private static IZType FromBuiltinType(BuiltinType type)
        {
            switch (type)
            {
                case BuiltinType.Bool: return IZType.Bool;
                case BuiltinType.Str: return IZType.Str;
                default: return IZType.Num;
            }
        }

        private void RequireBuiltinArgument(BuiltinInfo builtin, int position, IZType type, SourceSpan span)
        {
            string context = "argument " + (position + 1) + " of '" + builtin.Name + "'";

            switch (builtin.Parameters[position])
            {
                case BuiltinType.Str:
                    RequireString(type, span, context);
                    return;
                case BuiltinType.Bool:
                    RequireBool(type, span, context);
                    return;
                default:
                    RequireNumeric(type, span, context);
                    return;
            }
        }

        private void RequireString(IZType type, SourceSpan span, string context)
        {
            if (type == IZType.Str || type == IZType.Error) return;

            string hint = type == IZType.Num || type == IZType.Bool
                ? "; a number becomes text with 'text(x)'"
                : string.Empty;

            _diagnostics.Report(IZErrorCode.TypeMismatch, span,
                context + " expects str, but got " + type.Display() + hint);
        }

        private void RequireNumeric(IZType type, SourceSpan span, string context)
        {
            if (type == IZType.Num || type == IZType.Bool || type == IZType.Error) return;
            _diagnostics.Report(IZErrorCode.TypeMismatch, span,
                context + " expects num, but got " + type.Display());
        }

        private void RequireBool(IZType type, SourceSpan span, string context)
        {
            if (type == IZType.Bool || type == IZType.Error) return;

            string hint = type == IZType.Num
                ? "; a num does not become a bool on its own - compare it, for example 'x != 0'"
                : string.Empty;

            _diagnostics.Report(IZErrorCode.TypeMismatch, span,
                context + " expects bool, but got " + type.Display() + hint);
        }

        /// <summary>
        /// Puts a literal in the program's string pool and hands back its index.
        /// The same text always gets the same slot, so <c>"ok"</c> written twenty
        /// times costs one entry and compares equal by handle at runtime.
        /// </summary>
        private int InternString(string text, SourceSpan span)
        {
            if (_stringIndex.TryGetValue(text, out int index)) return index;

            if (_strings.Count >= MaxStrings)
            {
                _diagnostics.Report(IZErrorCode.TooManyStrings, span,
                    "went past " + MaxStrings + " distinct strings");
                return 0;
            }

            index = _strings.Count;
            _strings.Add(text);
            _stringIndex[text] = index;
            return index;
        }

        private void EmitString(string text, int line, SourceSpan span)
        {
            if (text.Length > IZLimits.MaxStringLength)
            {
                _diagnostics.Report(IZErrorCode.StringTooLong, span,
                    "a string holds at most " + IZLimits.MaxStringLength +
                    " characters, and this one has " + text.Length);
                text = text.Substring(0, IZLimits.MaxStringLength);
            }

            Emit(OpCode.PushStr, InternString(text, span), 0, line);
        }

        /// <summary>
        /// Emits the hash of a string that is already known at compile time, which is
        /// what keeps <c>all("StructurePump")</c> and <c>named(PUMP, "north")</c> as
        /// free as they were before str became a real value. False when the text only
        /// exists at runtime.
        /// </summary>
        private bool EmitHashOfConstantString(ExpressionSyntax expression, int line)
        {
            if (!TryEvaluateConstantString(expression, out string? text)) return false;

            InternString(text!, expression.Span);
            EmitConstant(PrefabHash.Compute(text!), line);
            return true;
        }

        private void EmitConstant(double value, int line)
        {
            if (value == 0.0) { Emit(OpCode.PushZero, 0, 0, line); return; }
            if (value == 1.0) { Emit(OpCode.PushOne, 0, 0, line); return; }

            if (!_constantIndex.TryGetValue(value, out int index))
            {
                if (_constants.Count >= MaxConstants)
                {
                    _diagnostics.Report(IZErrorCode.TooManyConstants, new SourceSpan(0, 0),
                        "went past " + MaxConstants + " distinct constants");
                    Emit(OpCode.PushZero, 0, 0, line);
                    return;
                }
                index = _constants.Count;
                _constants.Add(value);
                _constantIndex[value] = index;
            }
            Emit(OpCode.PushConst, index, 0, line);
        }

        private void Emit(OpCode op, int a, int b, int line)
        {
            _code.Add(new Instruction(op, a, b));
            _lines.Add(line);
        }

        /// <summary>Emits a jump whose target is not known yet; returns the index to patch.</summary>
        private int EmitJump(OpCode op, int line)
        {
            _code.Add(new Instruction(op, -1, 0));
            _lines.Add(line);
            return _code.Count - 1;
        }

        private void PatchJump(int jumpIndex) => PatchJumpTo(jumpIndex, _code.Count);

        private void PatchJumpTo(int jumpIndex, int target)
        {
            var jump = _code[jumpIndex];
            _code[jumpIndex] = new Instruction(jump.Op, target, jump.B);
        }

        private int LineOf(SourceSpan span) => _source.GetLinePosition(span.Start).Line;
    }
}
