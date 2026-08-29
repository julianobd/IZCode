using System.Collections.Generic;
using IZLang.Diagnostics;
using IZLang.Lexing;

namespace IZLang.Parsing
{
    public abstract class SyntaxNode
    {
        public SourceSpan Span { get; protected set; }
    }

    // ======================================================================
    //  Expressions
    // ======================================================================

    public abstract class ExpressionSyntax : SyntaxNode { }

    /// <summary>Number, string, true/false.</summary>
    public sealed class LiteralExpression : ExpressionSyntax
    {
        public Token Token { get; }
        public object? Value { get; }

        public LiteralExpression(Token token, object? value)
        {
            Token = token;
            Value = value;
            Span = token.Span;
        }
    }

    /// <summary>#"StructureWallLight" - becomes the CRC32 of the name at compile time.</summary>
    public sealed class HashLiteralExpression : ExpressionSyntax
    {
        public string PrefabName { get; }

        public HashLiteralExpression(Token token)
        {
            PrefabName = token.StringValue;
            Span = token.Span;
        }
    }

    /// <summary>Reference to a name: variable, constant, device or function.</summary>
    public sealed class NameExpression : ExpressionSyntax
    {
        public string Name { get; }
        public Token Token { get; }

        public NameExpression(Token token)
        {
            Token = token;
            Name = token.Text;
            Span = token.Span;
        }
    }

    public sealed class UnaryExpression : ExpressionSyntax
    {
        public Token OperatorToken { get; }
        public ExpressionSyntax Operand { get; }

        public UnaryExpression(Token op, ExpressionSyntax operand)
        {
            OperatorToken = op;
            Operand = operand;
            Span = op.Span.To(operand.Span);
        }
    }

    public sealed class BinaryExpression : ExpressionSyntax
    {
        public ExpressionSyntax Left { get; }
        public Token OperatorToken { get; }
        public ExpressionSyntax Right { get; }

        public BinaryExpression(ExpressionSyntax left, Token op, ExpressionSyntax right)
        {
            Left = left;
            OperatorToken = op;
            Right = right;
            Span = left.Span.To(right.Span);
        }
    }

    public sealed class CallExpression : ExpressionSyntax
    {
        public ExpressionSyntax Callee { get; }
        public List<ExpressionSyntax> Arguments { get; }

        public CallExpression(ExpressionSyntax callee, List<ExpressionSyntax> args, SourceSpan span)
        {
            Callee = callee;
            Arguments = args;
            Span = span;
        }
    }

    /// <summary>target.Name - reads a device logic property.</summary>
    public sealed class MemberExpression : ExpressionSyntax
    {
        public ExpressionSyntax Target { get; }
        public string MemberName { get; }
        public Token MemberToken { get; }

        public MemberExpression(ExpressionSyntax target, Token member)
        {
            Target = target;
            MemberName = member.Text;
            MemberToken = member;
            Span = target.Span.To(member.Span);
        }
    }

    /// <summary>target[index] - used for device slots.</summary>
    public sealed class IndexExpression : ExpressionSyntax
    {
        public ExpressionSyntax Target { get; }
        public ExpressionSyntax Index { get; }

        public IndexExpression(ExpressionSyntax target, ExpressionSyntax index, SourceSpan span)
        {
            Target = target;
            Index = index;
            Span = span;
        }
    }

    /// <summary>[1, 2, 3] - initializes an array in one go.</summary>
    public sealed class ArrayLiteralExpression : ExpressionSyntax
    {
        public List<ExpressionSyntax> Elements { get; }

        public ArrayLiteralExpression(List<ExpressionSyntax> elements, SourceSpan span)
        {
            Elements = elements;
            Span = span;
        }
    }

    /// <summary>
    /// x =&gt; expression - the body of a query method.
    ///
    /// It is not a value: there are no function pointers in IZ. It only ever appears
    /// as the argument of a query method, and the compiler inlines it into the loop
    /// it generates, which is why it costs no call.
    /// </summary>
    public sealed class LambdaExpression : ExpressionSyntax
    {
        public Token ParameterToken { get; }
        public string ParameterName => ParameterToken.Text;
        public ExpressionSyntax Body { get; }

        public LambdaExpression(Token parameter, ExpressionSyntax body, SourceSpan span)
        {
            ParameterToken = parameter;
            Body = body;
            Span = span;
        }
    }

    public enum BatchSelectorKind { All, Named }

    /// <summary>
    /// Batch operation target. Three forms:
    ///
    ///   all(Prefab)            -> Prefab set, Label null
    ///   named("label")         -> Prefab null (any type), Label set
    ///   named(Prefab, "lbl")   -> both set
    /// </summary>
    public sealed class BatchSelectorExpression : ExpressionSyntax
    {
        public BatchSelectorKind Kind { get; }

        /// <summary>Prefab to filter on. null in a one-argument 'named': matches any type.</summary>
        public ExpressionSyntax? Prefab { get; }

        /// <summary>Device label. Always null in 'all'.</summary>
        public ExpressionSyntax? Label { get; }

        public BatchSelectorExpression(BatchSelectorKind kind, ExpressionSyntax? prefab,
                                       ExpressionSyntax? label, SourceSpan span)
        {
            Kind = kind;
            Prefab = prefab;
            Label = label;
            Span = span;
        }
    }

    // ======================================================================
    //  Type annotations
    // ======================================================================

    public abstract class TypeSyntax : SyntaxNode { }

    /// <summary>A type written by name: 'num', 'bool', 'str', 'dev' or a struct.</summary>
    public sealed class NamedTypeSyntax : TypeSyntax
    {
        public Token Token { get; }
        public string Name { get; }

        public NamedTypeSyntax(Token token)
        {
            Token = token;
            Name = token.Text;
            Span = token.Span;
        }
    }

    /// <summary>
    /// An array type: 'num[8]'.
    ///
    /// The length is an expression because a constant is allowed there
    /// ('num[SAMPLES]'); the binder folds it and demands a whole number.
    /// </summary>
    public sealed class ArrayTypeSyntax : TypeSyntax
    {
        public TypeSyntax ElementType { get; }
        public ExpressionSyntax Length { get; }

        public ArrayTypeSyntax(TypeSyntax elementType, ExpressionSyntax length, SourceSpan span)
        {
            ElementType = elementType;
            Length = length;
            Span = span;
        }
    }

    /// <summary>
    /// A list type: 'list num[8]'. The inner type carries the capacity, which is
    /// why what follows 'list' is always an array annotation.
    /// </summary>
    public sealed class ListTypeSyntax : TypeSyntax
    {
        public TypeSyntax Inner { get; }

        public ListTypeSyntax(TypeSyntax inner, SourceSpan span)
        {
            Inner = inner;
            Span = span;
        }
    }

    // ======================================================================
    //  Statements
    // ======================================================================

    public abstract class StatementSyntax : SyntaxNode { }

    public sealed class BlockStatement : StatementSyntax
    {
        public List<StatementSyntax> Statements { get; }

        public BlockStatement(List<StatementSyntax> statements, SourceSpan span)
        {
            Statements = statements;
            Span = span;
        }
    }

    /// <summary>var x = expr;  /  var x: num = expr;  /  const X = expr;</summary>
    public sealed class VariableDeclaration : StatementSyntax
    {
        public bool IsConst { get; }
        public Token NameToken { get; }
        public string Name => NameToken.Text;
        public TypeSyntax? DeclaredType { get; }

        /// <summary>
        /// null when the declaration has no '=', which only an array or a struct
        /// allows: 'var a: num[8];' starts zeroed.
        /// </summary>
        public ExpressionSyntax? Initializer { get; }

        public VariableDeclaration(bool isConst, Token name, TypeSyntax? type,
                                   ExpressionSyntax? initializer, SourceSpan span)
        {
            IsConst = isConst;
            NameToken = name;
            DeclaredType = type;
            Initializer = initializer;
            Span = span;
        }
    }

    /// <summary>
    /// device pump = d0;  /  device suit = db;  /  device led = named(StructureDiode, "led");
    ///
    /// The first two forms bind a housing pin. The third binds a batch selector, so
    /// the name stands for every device the selector reaches rather than for one
    /// cable.
    /// </summary>
    public sealed class DeviceDeclaration : StatementSyntax
    {
        public Token NameToken { get; }
        public string Name => NameToken.Text;

        /// <summary>
        /// Pin index: 0 for d0 ... 5 for d5, <see cref="Vm.DevicePins.Housing"/> for
        /// 'db'. -1 when invalid, and also when the device is a batch selector.
        /// </summary>
        public int Pin { get; }
        public Token PinToken { get; }

        /// <summary>The 'all(...)' or 'named(...)' this name stands for. null for a pin.</summary>
        public BatchSelectorExpression? Selector { get; }

        public DeviceDeclaration(Token name, int pin, Token pinToken, SourceSpan span)
        {
            NameToken = name;
            Pin = pin;
            PinToken = pinToken;
            Span = span;
        }

        public DeviceDeclaration(Token name, BatchSelectorExpression selector, SourceSpan span)
        {
            NameToken = name;
            Pin = -1;
            PinToken = name;
            Selector = selector;
            Span = span;
        }
    }

    public enum AssignmentKind { Assign, Add, Subtract, Multiply, Divide, Modulo }

    /// <summary>
    /// Assignment is a statement, not an expression - which is why it lives here
    /// and not in ExpressionSyntax. The target is validated in the binder (name or device member).
    /// </summary>
    public sealed class AssignmentStatement : StatementSyntax
    {
        public ExpressionSyntax Target { get; }
        public AssignmentKind Kind { get; }
        public Token OperatorToken { get; }
        public ExpressionSyntax Value { get; }

        public AssignmentStatement(ExpressionSyntax target, AssignmentKind kind,
                                   Token op, ExpressionSyntax value, SourceSpan span)
        {
            Target = target;
            Kind = kind;
            OperatorToken = op;
            Value = value;
            Span = span;
        }
    }

    /// <summary>Call used as a statement: side effect only, result discarded.</summary>
    public sealed class ExpressionStatement : StatementSyntax
    {
        public ExpressionSyntax Expression { get; }

        public ExpressionStatement(ExpressionSyntax expression, SourceSpan span)
        {
            Expression = expression;
            Span = span;
        }
    }

    public sealed class IfStatement : StatementSyntax
    {
        public ExpressionSyntax Condition { get; }
        public BlockStatement ThenBlock { get; }

        /// <summary>The else block, or another IfStatement in the 'else if' case.</summary>
        public StatementSyntax? ElseBranch { get; }

        public IfStatement(ExpressionSyntax condition, BlockStatement thenBlock,
                           StatementSyntax? elseBranch, SourceSpan span)
        {
            Condition = condition;
            ThenBlock = thenBlock;
            ElseBranch = elseBranch;
            Span = span;
        }
    }

    public sealed class WhileStatement : StatementSyntax
    {
        public ExpressionSyntax Condition { get; }
        public BlockStatement Body { get; }

        public WhileStatement(ExpressionSyntax condition, BlockStatement body, SourceSpan span)
        {
            Condition = condition;
            Body = body;
            Span = span;
        }
    }

    public sealed class LoopStatement : StatementSyntax
    {
        public BlockStatement Body { get; }

        public LoopStatement(BlockStatement body, SourceSpan span)
        {
            Body = body;
            Span = span;
        }
    }

    /// <summary>for i in start..end { }  - end is exclusive, or inclusive with '..='.</summary>
    public sealed class ForStatement : StatementSyntax
    {
        public Token VariableToken { get; }
        public string VariableName => VariableToken.Text;
        public ExpressionSyntax Start { get; }
        public ExpressionSyntax End { get; }
        public bool IsInclusive { get; }
        public BlockStatement Body { get; }

        public ForStatement(Token variable, ExpressionSyntax start, ExpressionSyntax end,
                            bool isInclusive, BlockStatement body, SourceSpan span)
        {
            VariableToken = variable;
            Start = start;
            End = end;
            IsInclusive = isInclusive;
            Body = body;
            Span = span;
        }
    }

    public sealed class BreakStatement : StatementSyntax
    {
        public BreakStatement(SourceSpan span) { Span = span; }
    }

    public sealed class ContinueStatement : StatementSyntax
    {
        public ContinueStatement(SourceSpan span) { Span = span; }
    }

    public sealed class YieldStatement : StatementSyntax
    {
        public YieldStatement(SourceSpan span) { Span = span; }
    }

    public sealed class ReturnStatement : StatementSyntax
    {
        public ExpressionSyntax? Value { get; }

        public ReturnStatement(ExpressionSyntax? value, SourceSpan span)
        {
            Value = value;
            Span = span;
        }
    }

    // ======================================================================
    //  Top-level declarations
    // ======================================================================

    public abstract class DeclarationSyntax : SyntaxNode { }

    public sealed class ParameterSyntax : SyntaxNode
    {
        public Token NameToken { get; }
        public string Name => NameToken.Text;
        public TypeSyntax? DeclaredType { get; }

        public ParameterSyntax(Token name, TypeSyntax? type, SourceSpan span)
        {
            NameToken = name;
            DeclaredType = type;
            Span = span;
        }
    }

    /// <summary>One field inside a 'struct' body: 'x: num;'.</summary>
    public sealed class FieldSyntax : SyntaxNode
    {
        public Token NameToken { get; }
        public string Name => NameToken.Text;
        public TypeSyntax DeclaredType { get; }

        public FieldSyntax(Token name, TypeSyntax type, SourceSpan span)
        {
            NameToken = name;
            DeclaredType = type;
            Span = span;
        }
    }

    /// <summary>struct Point { x: num; y: num; }</summary>
    public sealed class StructDeclaration : DeclarationSyntax
    {
        public Token NameToken { get; }
        public string Name => NameToken.Text;
        public List<FieldSyntax> Fields { get; }

        public StructDeclaration(Token name, List<FieldSyntax> fields, SourceSpan span)
        {
            NameToken = name;
            Fields = fields;
            Span = span;
        }
    }

    public sealed class FunctionDeclaration : DeclarationSyntax
    {
        public Token NameToken { get; }
        public string Name => NameToken.Text;
        public List<ParameterSyntax> Parameters { get; }
        public TypeSyntax? ReturnType { get; }
        public BlockStatement Body { get; }

        public FunctionDeclaration(Token name, List<ParameterSyntax> parameters,
                                   TypeSyntax? returnType, BlockStatement body, SourceSpan span)
        {
            NameToken = name;
            Parameters = parameters;
            ReturnType = returnType;
            Body = body;
            Span = span;
        }
    }

    /// <summary>Wrapper for a top-level statement (global var/const/device).</summary>
    public sealed class GlobalStatementDeclaration : DeclarationSyntax
    {
        public StatementSyntax Statement { get; }

        public GlobalStatementDeclaration(StatementSyntax statement)
        {
            Statement = statement;
            Span = statement.Span;
        }
    }

    public sealed class CompilationUnit : SyntaxNode
    {
        public List<DeclarationSyntax> Declarations { get; }

        public CompilationUnit(List<DeclarationSyntax> declarations, SourceSpan span)
        {
            Declarations = declarations;
            Span = span;
        }
    }
}
