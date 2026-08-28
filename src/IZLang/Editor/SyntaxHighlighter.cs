using System;
using System.Collections.Generic;
using System.Text;
using IZLang.Diagnostics;
using IZLang.Lexing;

namespace IZLang.Editor
{
    /// <summary>
    /// Highlight colors, in hexadecimal without the '#'.
    ///
    /// The property and function tones are deliberately the same as the completion
    /// popup's: what the list shows in light blue shows up in light blue once
    /// accepted.
    /// </summary>
    public sealed class HighlightTheme
    {
        public string Marker = "6C8CD5";      // the '#iz' line
        public string Comment = "5A6B7C";
        public string Keyword = "F0C674";     // var, fn, if, loop, return...
        public string TypeName = "8BE9FD";    // num, bool, str, dev
        public string Selector = "C792EA";    // all, named
        public string BoolLiteral = "FF8FC7"; // true, false
        public string Number = "20B2AA";      // the same teal the IC10 editor uses
        public string String = "98C379";
        public string Hash = "C792EA";        // #"StructureWallLight"
        public string Identifier = "D8E1EA";
        public string Property = "7DD3FC";    // after a '.'
        public string Function = "86EFAC";    // followed by '('
        public string Operator = "8FA1B3";
        public string Invalid = "FF5555";

        public static HighlightTheme Default { get; } = new HighlightTheme();
    }

    /// <summary>
    /// Which rich text parser will read the output.
    ///
    /// The only difference is how to neutralize the <c>&lt;</c> in the player's code:
    /// TextMeshPro has <c>&lt;noparse&gt;</c>, uGUI's old Text does not - there the way
    /// to escape is the same trick the game itself uses: splice in an empty tag right
    /// after so the parser gives up on reading a tag name.
    /// </summary>
    public enum RichTextFlavor
    {
        /// <summary>The code editor: 128 TextMeshProUGUI fields.</summary>
        TextMeshPro,

        /// <summary>The Programmable Chip Motherboard screen: UnityEngine.UI.Text.</summary>
        LegacyText,
    }

    /// <summary>
    /// Paints a line of IZ code with TextMeshPro rich text tags.
    ///
    /// It exists because the game editor paints with the IC10 highlighter, which gets
    /// IZ source wrong twice over: it treats anything that is not an IC10 instruction
    /// as unrecognized text (which the editor shows in red) and treats <c>#</c> as the
    /// start of a comment - which wipes out half of <c>const X = #"Prefab"</c>.
    ///
    /// It works one line at a time because that is how the game editor works: there
    /// are 128 independent text fields, each with its own colored TextMeshPro. The
    /// consequence is that a block comment opened on one line and closed on another
    /// only paints the first one - no single-line highlighter solves that, and the
    /// cost of keeping state across 128 fields does not pay off.
    ///
    /// Pure code: it does not reference Unity, so the whole output is testable.
    /// </summary>
    public static class SyntaxHighlighter
    {
        /// <summary>The marker that switches the chip into IZ mode.</summary>
        public const string Marker = "#iz";

        /// <summary>
        /// Returns the line with color tags. It preserves the visible characters one
        /// by one: the tags are invisible, so the text field's caret still lands on
        /// the right column.
        /// </summary>
        public static string HighlightLine(string? line, HighlightTheme? theme = null,
                                           RichTextFlavor flavor = RichTextFlavor.TextMeshPro)
        {
            theme ??= HighlightTheme.Default;

            if (string.IsNullOrEmpty(line)) return string.Empty;
            if (line!.TrimEnd().Length == 0) return string.Empty;

            // The marker line is not code: the compiler replaces it with an empty
            // line before it sees the source.
            if (IsMarkerLine(line))
                return Colored(theme.Marker, Escape(line, flavor));

            var sb = new StringBuilder(line.Length + 64);

            try
            {
                var tokens = new Lexer(line, new DiagnosticBag()).Tokenize();
                int position = 0;

                for (int i = 0; i < tokens.Count; i++)
                {
                    var token = tokens[i];
                    if (token.Kind == TokenKind.EndOfFile) break;

                    int start = token.Span.Start;
                    if (start > position)
                        AppendTrivia(sb, line.Substring(position, start - position), theme, flavor);

                    string text = line.Substring(start, token.Span.Length);
                    sb.Append(Colored(ColorFor(token.Kind, PreviousKind(tokens, i), NextKind(tokens, i), theme),
                                      Escape(text, flavor)));

                    position = start + token.Span.Length;
                }

                if (position < line.Length)
                    AppendTrivia(sb, line.Substring(position), theme, flavor);
            }
            catch
            {
                // Highlighting is cosmetic: if the lexer trips on something, the raw
                // line is infinitely better than a blank one.
                return Escape(line, flavor);
            }

            return sb.ToString();
        }

        /// <summary>Paints the whole source, line by line. Used by the tests and by the export.</summary>
        public static string Highlight(string? source, HighlightTheme? theme = null,
                                       RichTextFlavor flavor = RichTextFlavor.TextMeshPro)
        {
            if (string.IsNullOrEmpty(source)) return string.Empty;

            string[] lines = source!.Split('\n');
            var sb = new StringBuilder(source.Length + lines.Length * 32);

            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(HighlightLine(lines[i].TrimEnd('\r'), theme, flavor));
            }
            return sb.ToString();
        }

        /// <summary>true when this is the <c>#iz</c> line that switches on IZ mode.</summary>
        public static bool IsMarkerLine(string? line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            string trimmed = line!.Trim();
            return trimmed.StartsWith(Marker, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------
        //  Trivia: whitespace and comments
        // ------------------------------------------------------------------

        /// <summary>
        /// The lexer returns tokens only; whatever is left between them is whitespace
        /// or a comment. Since the whitespace always comes before the comment within a
        /// gap, finding where the comment starts is enough.
        /// </summary>
        private static void AppendTrivia(StringBuilder sb, string gap, HighlightTheme theme,
                                         RichTextFlavor flavor)
        {
            int comment = IndexOfCommentStart(gap);

            if (comment < 0)
            {
                sb.Append(Escape(gap, flavor));
                return;
            }

            if (comment > 0) sb.Append(Escape(gap.Substring(0, comment), flavor));
            sb.Append(Colored(theme.Comment, Escape(gap.Substring(comment), flavor)));
        }

        private static int IndexOfCommentStart(string gap)
        {
            for (int i = 0; i + 1 < gap.Length; i++)
                if (gap[i] == '/' && (gap[i + 1] == '/' || gap[i + 1] == '*'))
                    return i;
            return -1;
        }

        // ------------------------------------------------------------------
        //  Color per token
        // ------------------------------------------------------------------

        private static TokenKind PreviousKind(List<Token> tokens, int index) =>
            index > 0 ? tokens[index - 1].Kind : TokenKind.EndOfFile;

        private static TokenKind NextKind(List<Token> tokens, int index) =>
            index + 1 < tokens.Count ? tokens[index + 1].Kind : TokenKind.EndOfFile;

        private static string ColorFor(TokenKind kind, TokenKind previous, TokenKind next,
                                       HighlightTheme theme)
        {
            switch (kind)
            {
                case TokenKind.Number: return theme.Number;
                case TokenKind.String: return theme.String;
                case TokenKind.HashLiteral: return theme.Hash;
                case TokenKind.Bad: return theme.Invalid;

                case TokenKind.Identifier:
                    // 'x.Setting' paints Setting as a property; 'abs(' paints abs as a
                    // function. It is the same distinction the completion engine makes.
                    if (previous == TokenKind.Dot) return theme.Property;
                    if (next == TokenKind.LParen) return theme.Function;
                    return theme.Identifier;

                case TokenKind.KwNum:
                case TokenKind.KwBool:
                case TokenKind.KwStr:
                case TokenKind.KwDev:
                case TokenKind.KwList:
                    return theme.TypeName;

                case TokenKind.KwAll:
                case TokenKind.KwNamed:
                    return theme.Selector;

                case TokenKind.KwTrue:
                case TokenKind.KwFalse:
                    return theme.BoolLiteral;

                default:
                    return IsKeyword(kind) ? theme.Keyword : theme.Operator;
            }
        }

        private static bool IsKeyword(TokenKind kind)
        {
            switch (kind)
            {
                case TokenKind.KwVar:
                case TokenKind.KwConst:
                case TokenKind.KwDevice:
                case TokenKind.KwFn:
                case TokenKind.KwReturn:
                case TokenKind.KwStruct:
                case TokenKind.KwIf:
                case TokenKind.KwElse:
                case TokenKind.KwWhile:
                case TokenKind.KwLoop:
                case TokenKind.KwFor:
                case TokenKind.KwIn:
                case TokenKind.KwBreak:
                case TokenKind.KwContinue:
                case TokenKind.KwYield:
                    return true;
                default:
                    return false;
            }
        }

        // ------------------------------------------------------------------
        //  Rich text
        // ------------------------------------------------------------------

        private static string Colored(string color, string text) =>
            text.Length == 0 ? string.Empty : "<color=#" + color + ">" + text + "</color>";

        /// <summary>
        /// Neutralizes '&lt;' so the rich text parser does not read '&lt;&lt;' or
        /// '&lt;=' as a tag and swallow the rest of the line. A lone '&gt;' opens no
        /// tag at all, so it goes through untouched.
        /// </summary>
        private static string Escape(string text, RichTextFlavor flavor)
        {
            if (text.IndexOf('<') < 0) return text;

            return flavor == RichTextFlavor.TextMeshPro
                ? text.Replace("<", "<noparse><</noparse>")
                : text.Replace("<", "<<b></b>");
        }
    }
}
