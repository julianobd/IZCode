using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace IZLang.Diagnostics
{
    public enum DiagnosticSeverity { Warning, Error }

    /// <summary>
    /// Stable codes. The in-game editor and the tests reference the code, never the
    /// message text - that way the message can be translated without breaking anything.
    /// </summary>
    public enum IZErrorCode
    {
        None = 0,

        // --- lexical: 1xx ---
        UnexpectedCharacter = 100,
        UnterminatedString = 101,
        UnterminatedBlockComment = 102,
        InvalidNumber = 103,
        InvalidEscapeSequence = 104,
        NonAsciiCharacter = 105,

        // --- syntax: 2xx ---
        UnexpectedToken = 200,
        ExpectedToken = 201,
        ExpectedExpression = 202,
        ExpectedStatement = 203,
        ExpectedDeclaration = 204,
        AssignmentIsNotExpression = 205,
        InvalidAssignmentTarget = 206,

        // --- semantic: 3xx ---
        UndefinedName = 300,
        DuplicateName = 301,
        TypeMismatch = 302,
        NotCallable = 303,
        WrongArgumentCount = 304,
        AssignToConst = 305,
        BreakOutsideLoop = 306,
        ContinueOutsideLoop = 307,
        ReturnOutsideFunction = 308,
        MissingReturn = 309,
        ConstExpressionRequired = 310,
        MissingMainFunction = 311,
        UnknownLogicType = 312,
        LogicTypeNotWritable = 313,
        LogicTypeNotReadable = 314,
        InvalidDevicePin = 315,
        NotADevice = 316,
        ReturnValueFromVoid = 317,
        DivisionByZeroConst = 318,
        UnreachableCode = 319,
        UnusedVariable = 320,
        UnknownField = 321,
        IndexOutOfRange = 322,
        InvalidArrayLength = 323,
        UnknownConstant = 324,

        // --- limits: 4xx ---
        TooManyConstants = 400,
        TooManyLocals = 401,
        TooManyGlobals = 402,
        ProgramTooLarge = 403,
        NestingTooDeep = 404,
        TooMuchMemory = 405,
        TooManyStrings = 406,
        StringTooLong = 407,
    }

    public sealed class Diagnostic
    {
        public IZErrorCode Code { get; }
        public DiagnosticSeverity Severity { get; }
        public SourceSpan Span { get; }
        public string Message { get; }

        public Diagnostic(IZErrorCode code, DiagnosticSeverity severity, SourceSpan span, string message)
        {
            Code = code;
            Severity = severity;
            Span = span;
            Message = message ?? string.Empty;
        }

        public bool IsError => Severity == DiagnosticSeverity.Error;

        public override string ToString() => $"IZ{(int)Code:D3}: {Message}";

        /// <summary>
        /// Renders the diagnostic with the source line and a caret under the span -
        /// the format that goes to the log and to the editor error panel.
        /// </summary>
        public string Format(SourceText source)
        {
            if (source is null) return ToString();

            var pos = source.GetLinePosition(Span.Start);
            var sb = new StringBuilder();
            sb.Append(Severity == DiagnosticSeverity.Error ? "error" : "warning");
            sb.Append(" IZ").Append(((int)Code).ToString("D3"));
            sb.Append(" (").Append(pos.Line).Append(':').Append(pos.Column).Append("): ");
            sb.Append(Message);

            int lineIndex = pos.Line - 1;
            string lineText = source.GetLineText(lineIndex);
            sb.Append('\n').Append("  ").Append(lineText);

            int caretOffset = pos.Column - 1;
            // A span may cross lines; the caret only marks up to the end of this one.
            int available = Math.Max(0, lineText.Length - caretOffset);
            int caretLen = Math.Max(1, Math.Min(Span.Length, available));

            sb.Append('\n').Append("  ");
            for (int i = 0; i < caretOffset; i++) sb.Append(lineText.Length > i && lineText[i] == '\t' ? '\t' : ' ');
            sb.Append('^', caretLen);
            return sb.ToString();
        }
    }

    /// <summary>Diagnostic collector. The compiler never throws on user error.</summary>
    public sealed class DiagnosticBag : IEnumerable<Diagnostic>
    {
        private readonly List<Diagnostic> _items = new List<Diagnostic>();

        public int Count => _items.Count;
        public bool HasErrors { get; private set; }
        public Diagnostic this[int index] => _items[index];

        public void Report(IZErrorCode code, SourceSpan span, string message)
        {
            _items.Add(new Diagnostic(code, DiagnosticSeverity.Error, span, message));
            HasErrors = true;
        }

        public void Warn(IZErrorCode code, SourceSpan span, string message) =>
            _items.Add(new Diagnostic(code, DiagnosticSeverity.Warning, span, message));

        public void AddRange(IEnumerable<Diagnostic> other)
        {
            foreach (var d in other)
            {
                _items.Add(d);
                if (d.IsError) HasErrors = true;
            }
        }

        public IEnumerator<Diagnostic> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
