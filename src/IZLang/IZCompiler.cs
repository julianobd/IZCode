using System.Collections.Generic;
using System.Text;
using IZLang.Binding;
using IZLang.Diagnostics;
using IZLang.Lexing;
using IZLang.Parsing;
using IZLang.Vm;

namespace IZLang
{
    /// <summary>The result of a compilation: the program, or the errors that kept it from being produced.</summary>
    public sealed class CompilationResult
    {
        public IZProgram? Program { get; }
        public IReadOnlyList<Diagnostic> Diagnostics { get; }
        public SourceText Source { get; }

        public bool Success => Program != null;

        public CompilationResult(IZProgram? program, IReadOnlyList<Diagnostic> diagnostics, SourceText source)
        {
            Program = program;
            Diagnostics = diagnostics;
            Source = source;
        }

        /// <summary>One-based line of the first error, or 0 when it compiled. This is what the chip shows on the error LED.</summary>
        public int FirstErrorLine
        {
            get
            {
                foreach (var diagnostic in Diagnostics)
                    if (diagnostic.IsError)
                        return Source.GetLinePosition(diagnostic.Span.Start).Line;
                return 0;
            }
        }

        /// <summary>Every diagnostic, formatted, one block per error line.</summary>
        public string FormatDiagnostics()
        {
            var sb = new StringBuilder();
            foreach (var diagnostic in Diagnostics)
                sb.Append(diagnostic.Format(Source)).Append('\n');
            return sb.ToString();
        }
    }

    /// <summary>
    /// The language entry point: text goes in, an <see cref="IZProgram"/> comes out.
    ///
    /// The whole chain (lexer, parser, binder, emitter) is free of Unity, so the
    /// same code path runs in the tests and inside the game.
    /// </summary>
    public static class IZCompiler
    {
        public static CompilationResult Compile(string sourceCode)
        {
            var source = new SourceText(sourceCode ?? string.Empty);
            var diagnostics = new DiagnosticBag();

            var tokens = new Lexer(source.Text, diagnostics).Tokenize();
            var unit = new Parser(tokens, diagnostics).ParseCompilationUnit();

            // Without a trustworthy AST, going on to the binder only produces derived errors.
            if (diagnostics.HasErrors)
                return new CompilationResult(null, ToList(diagnostics), source);

            var program = new Compiler(source, diagnostics).Compile(unit);
            return new CompilationResult(program, ToList(diagnostics), source);
        }

        private static List<Diagnostic> ToList(DiagnosticBag bag)
        {
            var list = new List<Diagnostic>(bag.Count);
            foreach (var diagnostic in bag) list.Add(diagnostic);
            return list;
        }
    }
}
