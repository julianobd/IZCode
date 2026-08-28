using System;
using System.Collections.Generic;
using IZLang.Diagnostics;
using IZLang.Lexing;

namespace IZLang.Parsing
{
    /// <summary>
    /// Recursive descent parser with precedence climbing for expressions.
    ///
    /// On error it recovers in panic mode: it synchronizes at the next statement
    /// boundary, so a single forgotten semicolon does not produce a cascade of
    /// derived errors.
    /// </summary>
    public sealed class Parser
    {
        private const int MaxNestingDepth = 64;

        private readonly List<Token> _tokens;
        private readonly DiagnosticBag _diagnostics;
        private int _position;
        private int _depth;

        public Parser(List<Token> tokens, DiagnosticBag diagnostics)
        {
            _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        }

        private Token Current => Peek(0);

        private Token Peek(int offset)
        {
            int index = _position + offset;
            return index < _tokens.Count ? _tokens[index] : _tokens[_tokens.Count - 1];
        }

        private Token Advance()
        {
            var token = Current;
            if (_position < _tokens.Count - 1) _position++;
            return token;
        }

        private bool Check(TokenKind kind) => Current.Kind == kind;

        private bool Match(TokenKind kind)
        {
            if (!Check(kind)) return false;
            Advance();
            return true;
        }

        private Token Expect(TokenKind kind)
        {
            if (Check(kind)) return Advance();

            _diagnostics.Report(IZErrorCode.ExpectedToken, Current.Span,
                "expected " + kind.Display() + ", found " + Describe(Current));

            // Synthetic token: keeps the AST well formed for the later phases.
            return new Token(kind, new SourceSpan(Current.Span.Start, 0), string.Empty);
        }

        private static string Describe(Token token) =>
            token.Kind == TokenKind.EndOfFile ? "end of file" : "'" + token.Text + "'";

        // ==================================================================
        //  Compilation unit
        // ==================================================================

        public CompilationUnit ParseCompilationUnit()
        {
            var declarations = new List<DeclarationSyntax>();
            int start = Current.Span.Start;

            while (!Check(TokenKind.EndOfFile))
            {
                int before = _position;

                var declaration = ParseDeclaration();
                if (declaration != null) declarations.Add(declaration);

                // Safety latch: if nothing was consumed, force an advance so
                // malformed input cannot spin in an infinite loop.
                if (_position == before) Advance();
            }

            return new CompilationUnit(declarations, SourceSpan.FromBounds(start, Current.Span.End));
        }

        private DeclarationSyntax? ParseDeclaration()
        {
            if (Check(TokenKind.KwFn)) return ParseFunctionDeclaration();
            if (Check(TokenKind.KwStruct)) return ParseStructDeclaration();

            if (Check(TokenKind.KwVar) || Check(TokenKind.KwConst) || Check(TokenKind.KwDevice))
                return new GlobalStatementDeclaration(ParseStatement());

            _diagnostics.Report(IZErrorCode.ExpectedDeclaration, Current.Span,
                "only 'fn', 'struct', 'var', 'const' and 'device' are allowed at the top level; found " + Describe(Current));
            SynchronizeToDeclaration();
            return null;
        }

        private void SynchronizeToDeclaration()
        {
            while (!Check(TokenKind.EndOfFile) &&
                   !Check(TokenKind.KwFn) && !Check(TokenKind.KwVar) &&
                   !Check(TokenKind.KwConst) && !Check(TokenKind.KwDevice) &&
                   !Check(TokenKind.KwStruct))
            {
                Advance();
            }
        }

        private FunctionDeclaration ParseFunctionDeclaration()
        {
            var fnToken = Expect(TokenKind.KwFn);
            var name = Expect(TokenKind.Identifier);

            Expect(TokenKind.LParen);
            var parameters = new List<ParameterSyntax>();
            if (!Check(TokenKind.RParen))
            {
                do
                {
                    var paramName = Expect(TokenKind.Identifier);
                    TypeSyntax? paramType = null;
                    if (Match(TokenKind.Colon)) paramType = ParseType();

                    var paramSpan = paramType != null
                        ? paramName.Span.To(paramType.Span)
                        : paramName.Span;
                    parameters.Add(new ParameterSyntax(paramName, paramType, paramSpan));
                }
                while (Match(TokenKind.Comma));
            }
            Expect(TokenKind.RParen);

            TypeSyntax? returnType = null;
            if (Match(TokenKind.Arrow)) returnType = ParseType();

            var body = ParseBlock();
            return new FunctionDeclaration(name, parameters, returnType, body,
                fnToken.Span.To(body.Span));
        }

        private StructDeclaration ParseStructDeclaration()
        {
            var keyword = Expect(TokenKind.KwStruct);
            var name = Expect(TokenKind.Identifier);
            Expect(TokenKind.LBrace);

            var fields = new List<FieldSyntax>();
            while (!Check(TokenKind.RBrace) && !Check(TokenKind.EndOfFile))
            {
                int before = _position;

                var fieldName = Expect(TokenKind.Identifier);
                Expect(TokenKind.Colon);
                var fieldType = ParseType();
                var semi = Expect(TokenKind.Semicolon);
                fields.Add(new FieldSyntax(fieldName, fieldType, fieldName.Span.To(semi.Span)));

                // Safety latch: a malformed field must not spin the loop forever.
                if (_position == before) Advance();
            }

            var close = Expect(TokenKind.RBrace);
            return new StructDeclaration(name, fields, keyword.Span.To(close.Span));
        }

        /// <summary>
        /// A type name, optionally followed by array dimensions.
        ///
        /// The dimensions read left to right, as in C: 'num[3][2]' is 3 groups of 2,
        /// so 'm[i][j]' walks them in the order they were written.
        /// </summary>
        private TypeSyntax ParseType()
        {
            TypeSyntax type;

            switch (Current.Kind)
            {
                case TokenKind.KwNum:
                case TokenKind.KwBool:
                case TokenKind.KwStr:
                case TokenKind.KwDev:
                case TokenKind.Identifier:          // a declared struct
                    type = new NamedTypeSyntax(Advance());
                    break;
                default:
                    _diagnostics.Report(IZErrorCode.ExpectedToken, Current.Span,
                        "expected a type ('num', 'bool', 'str', 'dev' or a struct name), found " + Describe(Current));
                    return new NamedTypeSyntax(new Token(TokenKind.KwNum, new SourceSpan(Current.Span.Start, 0), "num"));
            }

            var lengths = new List<ExpressionSyntax>();
            var ends = new List<SourceSpan>();
            while (Check(TokenKind.LBracket))
            {
                Advance();
                lengths.Add(ParseExpression());
                ends.Add(Expect(TokenKind.RBracket).Span);
            }

            // Folded from the inside out, so the first pair of brackets ends up being
            // the outermost dimension.
            for (int i = lengths.Count - 1; i >= 0; i--)
                type = new ArrayTypeSyntax(type, lengths[i], type.Span.To(ends[i]));

            return type;
        }

        // ==================================================================
        //  Statements
        // ==================================================================

        private BlockStatement ParseBlock()
        {
            var open = Expect(TokenKind.LBrace);
            var statements = new List<StatementSyntax>();

            while (!Check(TokenKind.RBrace) && !Check(TokenKind.EndOfFile))
            {
                int before = _position;
                statements.Add(ParseStatement());
                if (_position == before) Advance();
            }

            var close = Expect(TokenKind.RBrace);
            return new BlockStatement(statements, open.Span.To(close.Span));
        }

        private StatementSyntax ParseStatement()
        {
            if (++_depth > MaxNestingDepth)
            {
                _diagnostics.Report(IZErrorCode.NestingTooDeep, Current.Span,
                    "nesting went past " + MaxNestingDepth + " levels");
                _depth--;
                SynchronizeToStatement();
                return new BlockStatement(new List<StatementSyntax>(), Current.Span);
            }

            try
            {
                switch (Current.Kind)
                {
                    case TokenKind.KwVar:
                    case TokenKind.KwConst:
                        return ParseVariableDeclaration();
                    case TokenKind.KwDevice:
                        return ParseDeviceDeclaration();
                    case TokenKind.KwIf:
                        return ParseIf();
                    case TokenKind.KwWhile:
                        return ParseWhile();
                    case TokenKind.KwLoop:
                        return ParseLoop();
                    case TokenKind.KwFor:
                        return ParseFor();
                    case TokenKind.KwReturn:
                        return ParseReturn();
                    case TokenKind.KwBreak:
                    {
                        var token = Advance();
                        var semi = Expect(TokenKind.Semicolon);
                        return new BreakStatement(token.Span.To(semi.Span));
                    }
                    case TokenKind.KwContinue:
                    {
                        var token = Advance();
                        var semi = Expect(TokenKind.Semicolon);
                        return new ContinueStatement(token.Span.To(semi.Span));
                    }
                    case TokenKind.KwYield:
                    {
                        var token = Advance();
                        var semi = Expect(TokenKind.Semicolon);
                        return new YieldStatement(token.Span.To(semi.Span));
                    }
                    case TokenKind.KwStruct:
                    {
                        // Parsed and dropped: carrying on as if it were not there gives
                        // better messages than resynchronizing in the middle of the body.
                        _diagnostics.Report(IZErrorCode.ExpectedDeclaration, Current.Span,
                            "a 'struct' can only be declared at the top level, outside any function");
                        var discarded = ParseStructDeclaration();
                        return new BlockStatement(new List<StatementSyntax>(), discarded.Span);
                    }
                    case TokenKind.LBrace:
                        return ParseBlock();
                    default:
                        return ParseExpressionOrAssignment();
                }
            }
            finally
            {
                _depth--;
            }
        }

        private void SynchronizeToStatement()
        {
            while (!Check(TokenKind.EndOfFile))
            {
                if (Check(TokenKind.Semicolon)) { Advance(); return; }
                if (Check(TokenKind.RBrace)) return;

                switch (Current.Kind)
                {
                    case TokenKind.KwVar:
                    case TokenKind.KwConst:
                    case TokenKind.KwDevice:
                    case TokenKind.KwFn:
                    case TokenKind.KwIf:
                    case TokenKind.KwWhile:
                    case TokenKind.KwLoop:
                    case TokenKind.KwFor:
                    case TokenKind.KwReturn:
                    case TokenKind.KwBreak:
                    case TokenKind.KwContinue:
                    case TokenKind.KwYield:
                        return;
                }
                Advance();
            }
        }

        private StatementSyntax ParseVariableDeclaration()
        {
            var keyword = Advance();                       // 'var' or 'const'
            bool isConst = keyword.Kind == TokenKind.KwConst;

            var name = Expect(TokenKind.Identifier);

            TypeSyntax? declaredType = null;
            if (Match(TokenKind.Colon)) declaredType = ParseType();

            // 'var a: num[8];' is legal - an array or a struct starts zeroed. Anything
            // else needs a value, since there would be nothing left to infer a type from.
            ExpressionSyntax? initializer = null;
            if (Match(TokenKind.Equals))
            {
                initializer = ParseExpression();
            }
            else if (declaredType == null || isConst)
            {
                _diagnostics.Report(IZErrorCode.ExpectedToken, Current.Span,
                    isConst
                        ? "a 'const' needs a value: 'const " + name.Text + " = ...;'"
                        : "'" + name.Text + "' needs a value, or a type to start zeroed from");
            }

            var semi = Expect(TokenKind.Semicolon);

            return new VariableDeclaration(isConst, name, declaredType, initializer,
                keyword.Span.To(semi.Span));
        }

        private StatementSyntax ParseDeviceDeclaration()
        {
            var keyword = Advance();                       // 'device'
            var name = Expect(TokenKind.Identifier);
            Expect(TokenKind.Equals);

            var pinToken = Current;
            int pin = -1;

            if (Check(TokenKind.Identifier) && IsPinName(pinToken.Text, out pin))
            {
                Advance();
            }
            else
            {
                _diagnostics.Report(IZErrorCode.InvalidDevicePin, pinToken.Span,
                    "expected a pin from 'd0' to 'd5', found " + Describe(pinToken));
                if (!Check(TokenKind.Semicolon)) Advance();
            }

            var semi = Expect(TokenKind.Semicolon);
            return new DeviceDeclaration(name, pin, pinToken, keyword.Span.To(semi.Span));
        }

        /// <summary>d0..d5 - the six pins of the circuit housing.</summary>
        private static bool IsPinName(string text, out int pin)
        {
            pin = -1;
            if (text.Length != 2 || text[0] != 'd') return false;
            char digit = text[1];
            if (digit < '0' || digit > '5') return false;
            pin = digit - '0';
            return true;
        }

        private StatementSyntax ParseIf()
        {
            var keyword = Advance();
            var condition = ParseExpression();
            var thenBlock = ParseBlock();

            StatementSyntax? elseBranch = null;
            if (Match(TokenKind.KwElse))
            {
                // 'else if' chains; anything else requires a block.
                elseBranch = Check(TokenKind.KwIf) ? ParseIf() : ParseBlock();
            }

            var end = elseBranch?.Span ?? thenBlock.Span;
            return new IfStatement(condition, thenBlock, elseBranch, keyword.Span.To(end));
        }

        private StatementSyntax ParseWhile()
        {
            var keyword = Advance();
            var condition = ParseExpression();
            var body = ParseBlock();
            return new WhileStatement(condition, body, keyword.Span.To(body.Span));
        }

        private StatementSyntax ParseLoop()
        {
            var keyword = Advance();
            var body = ParseBlock();
            return new LoopStatement(body, keyword.Span.To(body.Span));
        }

        private StatementSyntax ParseFor()
        {
            var keyword = Advance();
            var variable = Expect(TokenKind.Identifier);
            Expect(TokenKind.KwIn);

            var start = ParseExpression();

            bool inclusive = false;
            if (Match(TokenKind.DotDotEquals)) inclusive = true;
            else Expect(TokenKind.DotDot);

            var end = ParseExpression();
            var body = ParseBlock();

            return new ForStatement(variable, start, end, inclusive, body,
                keyword.Span.To(body.Span));
        }

        private StatementSyntax ParseReturn()
        {
            var keyword = Advance();

            ExpressionSyntax? value = null;
            if (!Check(TokenKind.Semicolon)) value = ParseExpression();

            var semi = Expect(TokenKind.Semicolon);
            return new ReturnStatement(value, keyword.Span.To(semi.Span));
        }

        private StatementSyntax ParseExpressionOrAssignment()
        {
            var start = Current.Span;
            var expression = ParseExpression();

            var assignKind = GetAssignmentKind(Current.Kind);
            if (assignKind.HasValue)
            {
                var op = Advance();
                var value = ParseExpression();
                var semi = Expect(TokenKind.Semicolon);

                if (!IsAssignable(expression))
                {
                    _diagnostics.Report(IZErrorCode.InvalidAssignmentTarget, expression.Span,
                        "the left side of '" + op.Text + "' must be a variable or a device property");
                }

                return new AssignmentStatement(expression, assignKind.Value, op, value,
                    start.To(semi.Span));
            }

            var endSemi = Expect(TokenKind.Semicolon);

            if (!(expression is CallExpression))
            {
                _diagnostics.Report(IZErrorCode.ExpectedStatement, expression.Span,
                    "this expression does nothing; only a function call or an assignment works as a statement");
            }

            return new ExpressionStatement(expression, start.To(endSemi.Span));
        }

        private static AssignmentKind? GetAssignmentKind(TokenKind kind)
        {
            switch (kind)
            {
                case TokenKind.Equals: return AssignmentKind.Assign;
                case TokenKind.PlusEquals: return AssignmentKind.Add;
                case TokenKind.MinusEquals: return AssignmentKind.Subtract;
                case TokenKind.StarEquals: return AssignmentKind.Multiply;
                case TokenKind.SlashEquals: return AssignmentKind.Divide;
                case TokenKind.PercentEquals: return AssignmentKind.Modulo;
                default: return null;
            }
        }

        private static bool IsAssignable(ExpressionSyntax expression) =>
            expression is NameExpression || expression is MemberExpression ||
            expression is IndexExpression;

        // ==================================================================
        //  Expressions (precedence climbing)
        // ==================================================================

        /// <summary>
        /// Binary precedence; 0 means "not a binary operator".
        /// Every level is left associative.
        /// </summary>
        private static int GetBinaryPrecedence(TokenKind kind)
        {
            switch (kind)
            {
                case TokenKind.PipePipe: return 1;
                case TokenKind.AmpAmp: return 2;

                case TokenKind.EqualsEquals:
                case TokenKind.BangEquals: return 3;

                case TokenKind.Less:
                case TokenKind.LessEquals:
                case TokenKind.Greater:
                case TokenKind.GreaterEquals: return 4;

                case TokenKind.Pipe: return 5;
                case TokenKind.Caret: return 6;
                case TokenKind.Amp: return 7;

                case TokenKind.LessLess:
                case TokenKind.GreaterGreater: return 8;

                case TokenKind.Plus:
                case TokenKind.Minus: return 9;

                case TokenKind.Star:
                case TokenKind.Slash:
                case TokenKind.Percent: return 10;

                default: return 0;
            }
        }

        private const int UnaryPrecedence = 11;

        public ExpressionSyntax ParseExpression() => ParseBinary(0);

        private ExpressionSyntax ParseBinary(int parentPrecedence)
        {
            ExpressionSyntax left;

            if (IsUnaryOperator(Current.Kind))
            {
                var op = Advance();
                var operand = ParseBinary(UnaryPrecedence);
                left = new UnaryExpression(op, operand);
            }
            else
            {
                left = ParsePostfix();
            }

            while (true)
            {
                int precedence = GetBinaryPrecedence(Current.Kind);
                if (precedence == 0 || precedence <= parentPrecedence) break;

                var op = Advance();
                var right = ParseBinary(precedence);
                left = new BinaryExpression(left, op, right);
            }

            return left;
        }

        private static bool IsUnaryOperator(TokenKind kind) =>
            kind == TokenKind.Minus || kind == TokenKind.Bang || kind == TokenKind.Tilde;

        private ExpressionSyntax ParsePostfix()
        {
            var expression = ParsePrimary();

            while (true)
            {
                if (Match(TokenKind.Dot))
                {
                    var member = Expect(TokenKind.Identifier);
                    expression = new MemberExpression(expression, member);
                }
                else if (Check(TokenKind.LBracket))
                {
                    Advance();
                    var index = ParseExpression();
                    var close = Expect(TokenKind.RBracket);
                    expression = new IndexExpression(expression, index, expression.Span.To(close.Span));
                }
                else if (Check(TokenKind.LParen))
                {
                    Advance();
                    var arguments = new List<ExpressionSyntax>();
                    if (!Check(TokenKind.RParen))
                    {
                        do { arguments.Add(ParseExpression()); }
                        while (Match(TokenKind.Comma));
                    }
                    var close = Expect(TokenKind.RParen);
                    expression = new CallExpression(expression, arguments, expression.Span.To(close.Span));
                }
                else
                {
                    break;
                }
            }

            return expression;
        }

        private ExpressionSyntax ParsePrimary()
        {
            switch (Current.Kind)
            {
                case TokenKind.Number:
                {
                    var token = Advance();
                    return new LiteralExpression(token, token.NumberValue);
                }
                case TokenKind.String:
                {
                    var token = Advance();
                    return new LiteralExpression(token, token.StringValue);
                }
                case TokenKind.HashLiteral:
                    return new HashLiteralExpression(Advance());

                case TokenKind.KwTrue:
                    return new LiteralExpression(Advance(), true);
                case TokenKind.KwFalse:
                    return new LiteralExpression(Advance(), false);

                case TokenKind.Identifier:
                    return new NameExpression(Advance());

                case TokenKind.KwAll:
                case TokenKind.KwNamed:
                    return ParseBatchSelector();

                case TokenKind.LBracket:
                {
                    var open = Advance();
                    var elements = new List<ExpressionSyntax>();
                    if (!Check(TokenKind.RBracket))
                    {
                        do { elements.Add(ParseExpression()); }
                        while (Match(TokenKind.Comma));
                    }
                    var closeBracket = Expect(TokenKind.RBracket);
                    return new ArrayLiteralExpression(elements, open.Span.To(closeBracket.Span));
                }

                case TokenKind.LParen:
                {
                    Advance();
                    var inner = ParseExpression();
                    Expect(TokenKind.RParen);
                    return inner;
                }

                case TokenKind.Equals:
                    // The classic mistake for people coming from C: 'if a = b'.
                    _diagnostics.Report(IZErrorCode.AssignmentIsNotExpression, Current.Span,
                        "assignment is not an expression in IZ; use '==' to compare");
                    Advance();
                    return ParsePrimary();

                default:
                {
                    _diagnostics.Report(IZErrorCode.ExpectedExpression, Current.Span,
                        "expected an expression, found " + Describe(Current));
                    var bad = Current;
                    if (!Check(TokenKind.EndOfFile) && !Check(TokenKind.Semicolon) && !Check(TokenKind.RBrace))
                        Advance();
                    return new LiteralExpression(new Token(TokenKind.Number, new SourceSpan(bad.Span.Start, 0), "0"), 0.0);
                }
            }
        }

        private ExpressionSyntax ParseBatchSelector()
        {
            var keyword = Advance();
            bool isAll = keyword.Kind == TokenKind.KwAll;
            var kind = isAll ? BatchSelectorKind.All : BatchSelectorKind.Named;

            Expect(TokenKind.LParen);

            var arguments = new List<ExpressionSyntax>();
            if (!Check(TokenKind.RParen))
            {
                do { arguments.Add(ParseExpression()); }
                while (Match(TokenKind.Comma));
            }
            var close = Expect(TokenKind.RParen);
            var span = keyword.Span.To(close.Span);

            if (isAll)
            {
                if (arguments.Count != 1)
                {
                    _diagnostics.Report(IZErrorCode.WrongArgumentCount, span,
                        "'all' takes 1 argument (the prefab), got " + arguments.Count);
                }
                return new BatchSelectorExpression(kind,
                    arguments.Count > 0 ? arguments[0] : null, null, span);
            }

            // named("label")  or  named(Prefab, "label")
            switch (arguments.Count)
            {
                case 1:
                    return new BatchSelectorExpression(kind, null, arguments[0], span);
                case 2:
                    return new BatchSelectorExpression(kind, arguments[0], arguments[1], span);
                default:
                    _diagnostics.Report(IZErrorCode.WrongArgumentCount, span,
                        "'named' takes the label, or the prefab and the label; got " +
                        arguments.Count + " arguments");
                    return new BatchSelectorExpression(kind, null,
                        arguments.Count > 0 ? arguments[0] : null, span);
            }
        }
    }
}
