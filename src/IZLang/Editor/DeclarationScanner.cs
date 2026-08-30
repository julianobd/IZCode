using System;
using System.Collections.Generic;
using IZLang.Diagnostics;
using IZLang.Lexing;
using IZLang.Vm;

namespace IZLang.Editor
{
    public enum DeclaredKind { Device, Variable, Constant, Function, Parameter, Struct }

    /// <summary>A declaration found in the source, together with where it is.</summary>
    public sealed class DeclaredSymbol
    {
        public string Name { get; }
        public DeclaredKind Kind { get; }
        public SourceSpan NameSpan { get; }

        /// <summary>
        /// Pin 0..5, or <see cref="Vm.DevicePins.Housing"/> for 'db'. Only meaningful
        /// for <see cref="DeclaredKind.Device"/>; -1 otherwise, batch devices included.
        /// </summary>
        public int Pin { get; }

        /// <summary>
        /// The selector a batch device was declared from, as it was written -
        /// 'named(StructureDiode, "led-dev")'. null for a device on a pin.
        /// </summary>
        public string? BatchSelector { get; }

        /// <summary>
        /// The same selector taken apart into prefab and label, which is what lets the
        /// editor find the equipment behind it. Empty for a device on a pin.
        /// </summary>
        public DeviceSelector Selector { get; }

        /// <summary>
        /// Base name of the declared type, with no brackets: 'num[3][2]' gives "num"
        /// and an <see cref="ArrayDepth"/> of 2. null when the declaration has no
        /// annotation. This is what lets completion tell a struct from a device.
        /// </summary>
        public string? TypeName { get; }

        /// <summary>How many pairs of brackets the annotation carried.</summary>
        public int ArrayDepth { get; }

        /// <summary>Was the annotation a 'list'? What can be called on it depends on it.</summary>
        public bool IsList { get; }

        /// <summary>
        /// The annotation exactly as it was written, so hover can show 'num[8]'
        /// rather than the pieces it was taken apart into. Empty with no annotation.
        /// </summary>
        public SourceSpan TypeSpan { get; }

        public DeclaredSymbol(string name, DeclaredKind kind, SourceSpan nameSpan, int pin = -1,
                              string? typeName = null, int arrayDepth = 0,
                              SourceSpan typeSpan = default, bool isList = false,
                              string? batchSelector = null, DeviceSelector selector = default)
        {
            Name = name;
            Kind = kind;
            NameSpan = nameSpan;
            Pin = pin;
            TypeName = typeName;
            ArrayDepth = arrayDepth;
            TypeSpan = typeSpan;
            IsList = isList;
            BatchSelector = batchSelector;
            Selector = selector;
        }

        public override string ToString() => Kind + " " + Name;
    }

    /// <summary>One field of a scanned struct.</summary>
    public sealed class DeclaredField
    {
        public string Name { get; }
        public string TypeName { get; }
        public int ArrayDepth { get; }
        public bool IsList { get; }

        public DeclaredField(string name, string typeName, int arrayDepth, bool isList = false)
        {
            Name = name;
            TypeName = typeName;
            ArrayDepth = arrayDepth;
            IsList = isList;
        }

        public override string ToString() => Name + ": " + TypeName + new string('*', ArrayDepth);
    }

    /// <summary>A struct declaration found in the source, with its fields in order.</summary>
    public sealed class DeclaredStruct
    {
        public string Name { get; }
        public SourceSpan NameSpan { get; }
        public List<DeclaredField> Fields { get; } = new List<DeclaredField>();

        public DeclaredStruct(string name, SourceSpan nameSpan)
        {
            Name = name;
            NameSpan = nameSpan;
        }

        public DeclaredField? FindField(string name)
        {
            foreach (var field in Fields)
                if (string.Equals(field.Name, name, StringComparison.Ordinal)) return field;
            return null;
        }

        public override string ToString() => "struct " + Name;
    }

    /// <summary>
    /// Scans the tokens looking for declarations.
    ///
    /// It deliberately does not use the parser. In the editor the source is almost
    /// always half written - the player just typed 'pump.' and the line does not even
    /// close - and the parser, however forgiving, would produce a tree truncated
    /// exactly where it matters. Pattern matching over the tokens keeps finding the
    /// declarations both before and after the caret.
    /// </summary>
    public static class DeclarationScanner
    {
        public static List<DeclaredSymbol> Scan(IReadOnlyList<Token> tokens)
        {
            var symbols = new List<DeclaredSymbol>();

            for (int i = 0; i < tokens.Count; i++)
            {
                switch (tokens[i].Kind)
                {
                    // device <name> = d<N> ;   or   device <name> = named(...) ;
                    case TokenKind.KwDevice:
                        if (i + 1 < tokens.Count && tokens[i + 1].Kind == TokenKind.Identifier)
                        {
                            int pin = -1;
                            string? selectorText = null;
                            var selector = default(DeviceSelector);

                            // Neither the pin nor the selector may have been typed yet.
                            if (i + 3 < tokens.Count && tokens[i + 2].Kind == TokenKind.Equals)
                            {
                                if (tokens[i + 3].Kind == TokenKind.Identifier)
                                    TryParsePin(tokens[i + 3].Text, out pin);
                                else if (tokens[i + 3].Kind == TokenKind.KwAll ||
                                         tokens[i + 3].Kind == TokenKind.KwNamed)
                                {
                                    selectorText = ReadSelectorText(tokens, i + 3);
                                    selector = ParseSelector(tokens, i + 3);
                                }
                            }

                            symbols.Add(new DeclaredSymbol(
                                tokens[i + 1].Text, DeclaredKind.Device, tokens[i + 1].Span, pin,
                                batchSelector: selectorText, selector: selector));
                        }
                        break;

                    case TokenKind.KwVar:
                        AddSimple(tokens, i, DeclaredKind.Variable, symbols);
                        break;

                    case TokenKind.KwConst:
                        AddSimple(tokens, i, DeclaredKind.Constant, symbols);
                        break;

                    // struct <name> { ... }
                    case TokenKind.KwStruct:
                        if (i + 1 < tokens.Count && tokens[i + 1].Kind == TokenKind.Identifier)
                        {
                            symbols.Add(new DeclaredSymbol(
                                tokens[i + 1].Text, DeclaredKind.Struct, tokens[i + 1].Span));
                        }
                        break;

                    // fn <name> ( <param> [: type] , ... )
                    case TokenKind.KwFn:
                        if (i + 1 < tokens.Count && tokens[i + 1].Kind == TokenKind.Identifier)
                        {
                            symbols.Add(new DeclaredSymbol(
                                tokens[i + 1].Text, DeclaredKind.Function, tokens[i + 1].Span));
                            ScanParameters(tokens, i + 2, symbols);
                        }
                        break;

                    // for <name> in ...
                    case TokenKind.KwFor:
                        if (i + 1 < tokens.Count && tokens[i + 1].Kind == TokenKind.Identifier)
                        {
                            symbols.Add(new DeclaredSymbol(
                                tokens[i + 1].Text, DeclaredKind.Variable, tokens[i + 1].Span));
                        }
                        break;
                }
            }

            return symbols;
        }

        /// <summary>
        /// Reads the struct bodies.
        ///
        /// Separate from <see cref="Scan"/> because the fields are not names in scope:
        /// they only mean something after a '.', and that is the only place they are
        /// offered.
        /// </summary>
        public static List<DeclaredStruct> ScanStructs(IReadOnlyList<Token> tokens)
        {
            var structs = new List<DeclaredStruct>();

            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Kind != TokenKind.KwStruct) continue;
                if (i + 2 >= tokens.Count) break;
                if (tokens[i + 1].Kind != TokenKind.Identifier) continue;
                if (tokens[i + 2].Kind != TokenKind.LBrace) continue;

                var declared = new DeclaredStruct(tokens[i + 1].Text, tokens[i + 1].Span);
                int j = i + 3;

                // A half typed body is the normal case in an editor: read what is
                // there and stop at the closing brace, or at whatever ends the file.
                while (j < tokens.Count &&
                       tokens[j].Kind != TokenKind.RBrace &&
                       tokens[j].Kind != TokenKind.EndOfFile &&
                       tokens[j].Kind != TokenKind.KwStruct &&
                       tokens[j].Kind != TokenKind.KwFn)
                {
                    if (tokens[j].Kind == TokenKind.Identifier &&
                        j + 1 < tokens.Count && tokens[j + 1].Kind == TokenKind.Colon)
                    {
                        string fieldName = tokens[j].Text;
                        int k = j + 2;
                        if (TryReadType(tokens, ref k, out string typeName, out int depth,
                                        out bool fieldIsList))
                            declared.Fields.Add(new DeclaredField(fieldName, typeName, depth, fieldIsList));
                        j = k;
                        continue;
                    }
                    j++;
                }

                structs.Add(declared);
                i = j;
            }

            return structs;
        }

        public static DeclaredStruct? FindStruct(List<DeclaredStruct> structs, string name)
        {
            foreach (var declared in structs)
                if (string.Equals(declared.Name, name, StringComparison.Ordinal)) return declared;
            return null;
        }

        /// <summary>
        /// Reads a type annotation: a name followed by any number of '[...]'.
        /// Only the depth matters here, not the lengths, so the brackets are skipped
        /// as balanced groups without looking inside.
        /// </summary>
        private static bool TryReadType(IReadOnlyList<Token> tokens, ref int index,
                                        out string typeName, out int arrayDepth,
                                        out bool isList)
        {
            typeName = string.Empty;
            arrayDepth = 0;
            isList = false;

            if (index >= tokens.Count) return false;

            // 'list num[8]': the brackets after it are the room it has, and the item
            // type is what the rest of this reads.
            if (tokens[index].Kind == TokenKind.KwList)
            {
                isList = true;
                index++;
                if (index >= tokens.Count) return false;
            }

            switch (tokens[index].Kind)
            {
                case TokenKind.Identifier:
                case TokenKind.KwNum:
                case TokenKind.KwBool:
                case TokenKind.KwStr:
                case TokenKind.KwDev:
                    typeName = tokens[index].Text;
                    index++;
                    break;
                default:
                    return false;
            }

            while (index < tokens.Count && tokens[index].Kind == TokenKind.LBracket)
            {
                int depth = 0;
                while (index < tokens.Count)
                {
                    if (tokens[index].Kind == TokenKind.LBracket) depth++;
                    else if (tokens[index].Kind == TokenKind.RBracket) depth--;
                    else if (tokens[index].Kind == TokenKind.EndOfFile) return true;

                    index++;
                    if (depth == 0) break;
                }
                arrayDepth++;
            }

            return true;
        }

        /// <summary>Span from the first token of a type annotation to the last one it used.</summary>
        private static SourceSpan SpanOfType(IReadOnlyList<Token> tokens, int start, int end)
        {
            if (start >= tokens.Count || end <= start) return default;
            int last = Math.Min(end, tokens.Count) - 1;
            return SourceSpan.FromBounds(tokens[start].Span.Start, tokens[last].Span.End);
        }

        private static void AddSimple(IReadOnlyList<Token> tokens, int index,
                                      DeclaredKind kind, List<DeclaredSymbol> symbols)
        {
            if (index + 1 >= tokens.Count || tokens[index + 1].Kind != TokenKind.Identifier) return;

            string? typeName = null;
            int arrayDepth = 0;
            bool isList = false;
            SourceSpan typeSpan = default;

            if (index + 2 < tokens.Count && tokens[index + 2].Kind == TokenKind.Colon)
            {
                int i = index + 3;
                if (TryReadType(tokens, ref i, out string parsed, out int depth, out bool list))
                {
                    typeName = parsed;
                    arrayDepth = depth;
                    isList = list;
                    typeSpan = SpanOfType(tokens, index + 3, i);
                }
            }

            symbols.Add(new DeclaredSymbol(tokens[index + 1].Text, kind, tokens[index + 1].Span,
                pin: -1, typeName: typeName, arrayDepth: arrayDepth, typeSpan: typeSpan,
                isList: isList));
        }

        /// <summary>Reads the parameter list starting at the '(' in <paramref name="start"/>.</summary>
        private static void ScanParameters(IReadOnlyList<Token> tokens, int start, List<DeclaredSymbol> symbols)
        {
            if (start >= tokens.Count || tokens[start].Kind != TokenKind.LParen) return;

            int i = start + 1;
            bool expectingName = true;

            while (i < tokens.Count && tokens[i].Kind != TokenKind.RParen)
            {
                if (tokens[i].Kind == TokenKind.EndOfFile) return;

                if (expectingName && tokens[i].Kind == TokenKind.Identifier)
                {
                    string? typeName = null;
                    int arrayDepth = 0;
                    bool isList = false;
                    SourceSpan typeSpan = default;

                    if (i + 1 < tokens.Count && tokens[i + 1].Kind == TokenKind.Colon)
                    {
                        int k = i + 2;
                        if (TryReadType(tokens, ref k, out string parsed, out int depth, out bool list))
                        {
                            typeName = parsed;
                            arrayDepth = depth;
                            isList = list;
                            typeSpan = SpanOfType(tokens, i + 2, k);
                        }
                    }

                    symbols.Add(new DeclaredSymbol(tokens[i].Text, DeclaredKind.Parameter, tokens[i].Span,
                        pin: -1, typeName: typeName, arrayDepth: arrayDepth, typeSpan: typeSpan,
                        isList: isList));
                    expectingName = false;
                }
                else if (tokens[i].Kind == TokenKind.Comma)
                {
                    expectingName = true;
                }

                i++;
            }
        }

        /// <summary>
        /// Rebuilds 'named(StructureDiode, "led-dev")' from the tokens, for the
        /// tooltip. Stops at the closing parenthesis, or at the end of the statement
        /// when it is still being typed.
        /// </summary>
        private static string ReadSelectorText(IReadOnlyList<Token> tokens, int keywordIndex)
        {
            var text = new System.Text.StringBuilder(tokens[keywordIndex].Text);
            int depth = 0;

            for (int i = keywordIndex + 1; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Kind == TokenKind.Semicolon || token.Kind == TokenKind.EndOfFile) break;

                switch (token.Kind)
                {
                    case TokenKind.LParen: depth++; break;
                    case TokenKind.RParen: depth--; break;
                    case TokenKind.Comma: text.Append(','); continue;
                }

                if (token.Kind != TokenKind.LParen && token.Kind != TokenKind.RParen &&
                    text.Length > 0 && text[text.Length - 1] != '(')
                {
                    text.Append(' ');
                }

                text.Append(token.Kind == TokenKind.String ? "\"" + token.StringValue + "\"" : token.Text);
                if (depth == 0) break;
            }

            return text.ToString();
        }

        /// <summary>
        /// Takes 'all(...)' / 'named(...)' apart into the prefab and the label.
        ///
        /// Only literals count: a selector built from a const or a variable cannot be
        /// followed here, and the editor learns nothing about that device - exactly as
        /// it did before. The arguments are read as they were written, so a half typed
        /// 'named(StructureDiode' still gives back the prefab.
        /// </summary>
        public static DeviceSelector ParseSelector(IReadOnlyList<Token> tokens, int keywordIndex)
        {
            if (keywordIndex < 0 || keywordIndex >= tokens.Count) return default;

            var keyword = tokens[keywordIndex].Kind;
            if (keyword != TokenKind.KwAll && keyword != TokenKind.KwNamed) return default;
            if (keywordIndex + 1 >= tokens.Count ||
                tokens[keywordIndex + 1].Kind != TokenKind.LParen) return default;

            // The first token of each top level argument. Anything more elaborate than
            // a literal stays null, and the caller falls back to the full property list.
            bool hasFirst = false, hasSecond = false;
            Token first = default, second = default;
            int depth = 0;
            bool atArgumentStart = true;

            for (int i = keywordIndex + 1; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Kind == TokenKind.Semicolon || token.Kind == TokenKind.EndOfFile) break;

                if (token.Kind == TokenKind.LParen)
                {
                    depth++;
                    atArgumentStart = depth == 1;
                    continue;
                }
                if (token.Kind == TokenKind.RParen)
                {
                    if (--depth == 0) break;
                    continue;
                }
                if (token.Kind == TokenKind.Comma && depth == 1)
                {
                    atArgumentStart = true;
                    continue;
                }

                if (!atArgumentStart) continue;
                atArgumentStart = false;

                if (!hasFirst) { first = token; hasFirst = true; }
                else { second = token; hasSecond = true; break; }
            }

            if (keyword == TokenKind.KwAll)
                return new DeviceSelector(hasFirst ? PrefabOf(first) : null, null);

            // named("label")  or  named(Prefab, "label")
            if (!hasFirst) return default;
            if (!hasSecond) return new DeviceSelector(null, LabelOf(first));
            return new DeviceSelector(PrefabOf(first), LabelOf(second));
        }

        /// <summary>A prefab argument: a bare name, or the #"..." form.</summary>
        private static string? PrefabOf(Token token)
        {
            if (token.Kind == TokenKind.Identifier) return token.Text;
            if (token.Kind == TokenKind.HashLiteral) return token.StringValue;
            return null;
        }

        /// <summary>
        /// A label argument. The #"..." form is as common as the plain string here:
        /// what the compiler hashes is the text either way.
        /// </summary>
        private static string? LabelOf(Token token) =>
            token.Kind == TokenKind.String || token.Kind == TokenKind.HashLiteral
                ? token.StringValue
                : null;

        /// <summary>'d0'..'d5' - the housing pins - and 'db', the device holding the chip.</summary>
        public static bool TryParsePin(string text, out int pin) =>
            DevicePins.TryParse(text, out pin);

        /// <summary>Looks up the declaration of a name. Devices win over variables of the same name.</summary>
        public static DeclaredSymbol? Find(List<DeclaredSymbol> symbols, string name)
        {
            DeclaredSymbol? fallback = null;

            foreach (var symbol in symbols)
            {
                if (!string.Equals(symbol.Name, name, StringComparison.Ordinal)) continue;
                if (symbol.Kind == DeclaredKind.Device) return symbol;
                if (fallback == null) fallback = symbol;
            }

            return fallback;
        }
    }
}
