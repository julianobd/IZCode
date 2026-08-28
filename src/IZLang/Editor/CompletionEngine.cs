using System;
using System.Collections.Generic;
using IZLang.Binding;
using IZLang.Devices;
using IZLang.Diagnostics;
using IZLang.Lexing;
using IZLang.Vm;

namespace IZLang.Editor
{
    public enum CompletionKind
    {
        Keyword,
        Builtin,
        Variable,
        Constant,
        Function,
        Parameter,
        Device,
        Pin,
        Property,
        SlotProperty,
        Prefab,
        /// <summary>A query method over a list: where, sum, orderBy...</summary>
        Method,
    }

    /// <summary>A suggestion. <see cref="ReplaceSpan"/> says what to replace in the text when accepted.</summary>
    public sealed class CompletionItem
    {
        public string Label { get; }
        public CompletionKind Kind { get; }

        /// <summary>Short text on the right: property access, current value, type.</summary>
        public string Detail { get; }

        /// <summary>The source range the Label replaces.</summary>
        public SourceSpan ReplaceSpan { get; }

        /// <summary>Sort weight; lower comes first.</summary>
        public int Order { get; }

        public CompletionItem(string label, CompletionKind kind, string detail,
                              SourceSpan replaceSpan, int order = 0)
        {
            Label = label;
            Kind = kind;
            Detail = detail ?? string.Empty;
            ReplaceSpan = replaceSpan;
            Order = order;
        }

        public override string ToString() => Label + (Detail.Length > 0 ? "  " + Detail : string.Empty);
    }

    /// <summary>Who asked for the list.</summary>
    public enum CompletionTrigger
    {
        /// <summary>The player asked for it (Ctrl+Space): show everything that fits there.</summary>
        Explicit,

        /// <summary>
        /// The list would open by itself while typing. In that case it only shows up
        /// when there is something to go on - a typed prefix, or a context that is
        /// already a request in itself ('.', 'device x = ', 'all(', '#").
        /// </summary>
        Automatic,
    }

    /// <summary>What kind of thing fits at the caret position.</summary>
    public enum CompletionContext
    {
        /// <summary>Start of an expression or statement: names, keywords, builtins.</summary>
        General,
        /// <summary>After 'device x = ': pins only.</summary>
        Pin,
        /// <summary>After 'somedevice.': logic properties.</summary>
        DeviceProperty,
        /// <summary>After 'x.slot[i].': slot properties.</summary>
        SlotProperty,
        /// <summary>Inside all(...) or the first argument of named(...): prefabs.</summary>
        Prefab,
        /// <summary>Inside #"...": prefabs.</summary>
        PrefabString,
        /// <summary>After a struct value and a '.': the fields of that struct.</summary>
        StructField,
        /// <summary>After a list, an array or a query and a '.': the query methods.</summary>
        ListMethod,
    }

    /// <summary>
    /// Computes the suggestions for a caret position.
    ///
    /// It depends on neither Unity nor the game: it takes the text, the offset and
    /// an <see cref="IEditorEnvironment"/>. That is why the whole completion
    /// behaviour can be tested without opening Stationeers - all that is left for
    /// the Unity layer is drawing the list.
    /// </summary>
    public static class CompletionEngine
    {
        /// <summary>
        /// How far the popup can be scrolled. It has to hold the whole general list -
        /// every declared name, every builtin and every keyword - or the trim would
        /// eat the keywords at the end of it, and 'while' is worth more to the player
        /// than the last builtin in the table.
        /// </summary>
        private const int MaxItems = 80;

        private static readonly CompletionItem[] EmptyItems = new CompletionItem[0];

        private static readonly string[] Keywords =
        {
            "var", "const", "device", "fn", "return",
            "if", "else", "while", "loop", "for", "in",
            "break", "continue", "yield",
            "true", "false",
            "num", "bool", "str", "dev", "list",
            "all", "named", "struct", "len",
        };

        /// <summary>
        /// What can follow a list and a dot. The order is the one they are useful in,
        /// not the alphabet: what a player reaches for first comes first.
        /// </summary>
        private static readonly string[,] QueryMethods =
        {
            { "where", "keeps what passes a test" },
            { "select", "one value out of each item" },
            { "sum", "adds them up" },
            { "avg", "their average, 0 when empty" },
            { "min", "the smallest" },
            { "max", "the biggest" },
            { "count", "how many" },
            { "first", "the first one; stops the chip if there is none" },
            { "last", "the last one; stops the chip if there is none" },
            { "firstOr", "the first one, or the value given" },
            { "lastOr", "the last one, or the value given" },
            { "any", "is there one?" },
            { "all", "do they all pass?" },
            { "contains", "is this value in it?" },
            { "indexOf", "where it is, or -1" },
            { "take", "the first N" },
            { "skip", "everything after the first N" },
            { "takeWhile", "from the start, while a test passes" },
            { "skipWhile", "from the first item that fails a test" },
            { "orderBy", "sorted by a key, ascending" },
            { "orderByDesc", "sorted by a key, descending" },
            { "reverse", "back to front" },
            { "distinct", "drops repeats" },
            { "into", "writes the result into a list that already exists" },
        };

        /// <summary>Only a list has these: an array is always full.</summary>
        private static readonly string[,] ListOnlyMembers =
        {
            { "count", "how many items it holds" },
            { "add", "appends the item; false when it is full" },
            { "remove", "takes the first one equal to the value out" },
            { "removeAt", "takes the one at an index out, keeping the order" },
            { "clear", "empties it" },
        };

        public sealed class CompletionResult
        {
            public CompletionContext Context { get; }
            public IReadOnlyList<CompletionItem> Items { get; }

            /// <summary>The already typed fragment that was used as a filter.</summary>
            public string Prefix { get; }

            public CompletionResult(CompletionContext context, IReadOnlyList<CompletionItem> items, string prefix)
            {
                Context = context;
                Items = items;
                Prefix = prefix;
            }

            public bool IsEmpty => Items.Count == 0;
        }

        public static CompletionResult GetCompletions(string source, int caretOffset,
                                                      IEditorEnvironment? environment = null,
                                                      CompletionTrigger trigger = CompletionTrigger.Explicit)
        {
            environment ??= EmptyEditorEnvironment.Instance;
            source ??= string.Empty;
            caretOffset = Math.Max(0, Math.Min(caretOffset, source.Length));

            var diagnostics = new DiagnosticBag();
            var tokens = new Lexer(source, diagnostics).Tokenize();
            var declarations = DeclarationScanner.Scan(tokens);
            var structs = DeclarationScanner.ScanStructs(tokens);

            // What has already been typed of the current word: it filters, and it will be replaced.
            var prefixSpan = GetPrefixSpan(source, caretOffset);
            string prefix = source.Substring(prefixSpan.Start, prefixSpan.Length);

            var items = new List<CompletionItem>();
            var context = Classify(source, tokens, prefixSpan.Start, declarations, structs,
                                   out int pin, out string? prefabName, out var structType,
                                   out bool wholeList);

            // Blank line, nobody asked for anything: dumping the whole vocabulary over
            // the code gets in the way more than it helps. The other contexts came from
            // a '.', from 'device x = ' or from 'all(' - there the full list is exactly
            // what is wanted, even with no prefix.
            if (trigger == CompletionTrigger.Automatic &&
                context == CompletionContext.General &&
                prefix.Length == 0)
            {
                return new CompletionResult(context, EmptyItems, prefix);
            }

            switch (context)
            {
                case CompletionContext.Pin:
                    AddPins(items, prefixSpan, environment);
                    break;

                case CompletionContext.DeviceProperty:
                    AddDeviceProperties(items, prefixSpan, environment, pin);
                    break;

                case CompletionContext.SlotProperty:
                    AddSlotProperties(items, prefixSpan, environment, pin);
                    break;

                case CompletionContext.StructField:
                    AddStructFields(items, prefixSpan, structType);
                    break;

                case CompletionContext.ListMethod:
                    AddQueryMethods(items, prefixSpan, wholeList);
                    break;

                case CompletionContext.Prefab:
                case CompletionContext.PrefabString:
                    AddPrefabs(items, prefixSpan, environment, prefix);
                    break;

                default:
                    AddGeneral(items, prefixSpan, declarations, environment);
                    break;
            }

            var filtered = Filter(items, prefix);
            return new CompletionResult(context, filtered, prefix);
        }

        // ==================================================================
        //  Context classification
        // ==================================================================

        private static CompletionContext Classify(string source, List<Token> tokens, int prefixStart,
                                                  List<DeclaredSymbol> declarations,
                                                  List<DeclaredStruct> structs,
                                                  out int pin, out string? prefabName,
                                                  out DeclaredStruct? structType,
                                                  out bool wholeList)
        {
            pin = -1;
            prefabName = null;
            structType = null;
            wholeList = false;

            // Inside #"..."? The lexer swallows it all into one token, so looking back
            // at the raw text is more direct than looking at the tokens.
            if (IsInsideHashString(source, prefixStart))
                return CompletionContext.PrefabString;

            int index = LastTokenIndexBefore(tokens, prefixStart);
            if (index < 0) return CompletionContext.General;

            var previous = tokens[index];

            // ... '.' -> property
            if (previous.Kind == TokenKind.Dot)
            {
                // A struct answers first: 'p.' and 'grid[0].' are fields, and only
                // what does not resolve to a struct falls through to the device paths.
                structType = ResolveStructChain(tokens, index - 1, declarations, structs);
                if (structType != null) return CompletionContext.StructField;

                // 'xs.' and 'xs.where(f).' -> the query methods. 'wholeList' is what
                // separates the two: only the list itself can be added to or emptied.
                if (IsQuerySubject(tokens, index - 1, declarations, out wholeList))
                    return CompletionContext.ListMethod;

                // x.slot[i].  ->  the token before the '.' is ']'
                if (index >= 1 && tokens[index - 1].Kind == TokenKind.RBracket)
                {
                    pin = FindPinForSlotChain(tokens, index - 1, declarations);
                    return CompletionContext.SlotProperty;
                }

                // all(X).  /  named(...).  -> the prefab's properties, when we know which
                if (index >= 1 && tokens[index - 1].Kind == TokenKind.RParen)
                {
                    prefabName = FindPrefabForSelector(tokens, index - 1);
                    return CompletionContext.DeviceProperty;
                }

                // <name>.  -> when it is a declared device, we know the pin
                if (index >= 1 && tokens[index - 1].Kind == TokenKind.Identifier)
                {
                    var symbol = DeclarationScanner.Find(declarations, tokens[index - 1].Text);
                    if (symbol != null && symbol.Kind == DeclaredKind.Device) pin = symbol.Pin;
                    return CompletionContext.DeviceProperty;
                }

                return CompletionContext.DeviceProperty;
            }

            // 'device x = ' -> pin
            if (previous.Kind == TokenKind.Equals && index >= 2 &&
                tokens[index - 2].Kind == TokenKind.KwDevice)
            {
                return CompletionContext.Pin;
            }

            // 'all(' , 'named('  -> prefab
            if (previous.Kind == TokenKind.LParen && index >= 1 &&
                (tokens[index - 1].Kind == TokenKind.KwAll || tokens[index - 1].Kind == TokenKind.KwNamed))
            {
                return CompletionContext.Prefab;
            }

            return CompletionContext.General;
        }

        /// <summary>
        /// Walks back from ']' to the device that opens the <c>name.slot[...]</c>
        /// chain, and returns its pin.
        /// </summary>
        private static int FindPinForSlotChain(List<Token> tokens, int closeBracketIndex,
                                               List<DeclaredSymbol> declarations)
        {
            int depth = 0;
            for (int i = closeBracketIndex; i >= 0; i--)
            {
                if (tokens[i].Kind == TokenKind.RBracket) depth++;
                else if (tokens[i].Kind == TokenKind.LBracket)
                {
                    depth--;
                    if (depth != 0) continue;

                    // '[' ... before it we expect  <name> . slot
                    if (i >= 3 &&
                        tokens[i - 1].Kind == TokenKind.Identifier &&
                        tokens[i - 2].Kind == TokenKind.Dot &&
                        tokens[i - 3].Kind == TokenKind.Identifier)
                    {
                        var symbol = DeclarationScanner.Find(declarations, tokens[i - 3].Text);
                        return symbol != null && symbol.Kind == DeclaredKind.Device ? symbol.Pin : -1;
                    }
                    return -1;
                }
            }
            return -1;
        }

        /// <summary>
        /// Follows a postfix chain backwards from <paramref name="endIndex"/> - the
        /// token just before the '.' - and answers which struct it lands on.
        ///
        /// It walks the declared types, not values: 'w.samples[0].' means the base
        /// name, then a field, then one dimension off an array. Anything it cannot
        /// account for gives null, and the caller carries on as before.
        /// </summary>
        private static DeclaredStruct? ResolveStructChain(List<Token> tokens, int endIndex,
                                                          List<DeclaredSymbol> declarations,
                                                          List<DeclaredStruct> structs)
        {
            if (endIndex < 0 || structs.Count == 0) return null;

            // Back to the name that opens the chain.
            int start = -1;
            for (int i = endIndex; i >= 0;)
            {
                if (tokens[i].Kind == TokenKind.RBracket)
                {
                    i = SkipBracketsBackwards(tokens, i);
                    continue;
                }
                if (tokens[i].Kind != TokenKind.Identifier) return null;

                if (i >= 1 && tokens[i - 1].Kind == TokenKind.Dot) { i -= 2; continue; }
                start = i;
                break;
            }
            if (start < 0) return null;

            var symbol = DeclarationScanner.Find(declarations, tokens[start].Text);
            if (symbol == null || symbol.TypeName == null) return null;

            string typeName = symbol.TypeName;
            int depth = symbol.ArrayDepth;

            for (int i = start + 1; i <= endIndex; )
            {
                if (tokens[i].Kind == TokenKind.LBracket)
                {
                    if (depth == 0) return null;
                    depth--;
                    i = SkipBracketsForwards(tokens, i, endIndex);
                    continue;
                }

                if (tokens[i].Kind != TokenKind.Dot || i + 1 > endIndex ||
                    tokens[i + 1].Kind != TokenKind.Identifier) return null;

                if (depth != 0) return null;
                var owner = DeclarationScanner.FindStruct(structs, typeName);
                var field = owner?.FindField(tokens[i + 1].Text);
                if (field == null) return null;

                typeName = field.TypeName;
                depth = field.ArrayDepth;
                i += 2;
            }

            return depth == 0 ? DeclarationScanner.FindStruct(structs, typeName) : null;
        }

        /// <summary>
        /// Is what sits just before the dot something a query reads: a list, an array,
        /// or the result of another query method?
        ///
        /// <paramref name="wholeList"/> comes back true only for a list named as it
        /// is, which is the one case where 'add', 'removeAt' and 'clear' apply.
        /// </summary>
        private static bool IsQuerySubject(List<Token> tokens, int index,
                                           List<DeclaredSymbol> declarations, out bool wholeList)
        {
            wholeList = false;
            if (index < 0) return false;

            if (tokens[index].Kind == TokenKind.Identifier)
            {
                var symbol = DeclarationScanner.Find(declarations, tokens[index].Text);
                if (symbol == null || symbol.ArrayDepth == 0) return false;
                if (symbol.Kind == DeclaredKind.Device || symbol.Kind == DeclaredKind.Function) return false;

                wholeList = symbol.IsList;
                return true;
            }

            // '...)' - a method call, if a dot opened it. 'all(X).' ends the same way
            // and is a batch selector, so the dot before the name is what tells them apart.
            if (tokens[index].Kind == TokenKind.RParen)
            {
                int open = MatchingOpenParen(tokens, index);
                if (open < 2) return false;
                if (tokens[open - 2].Kind != TokenKind.Dot) return false;

                string name = tokens[open - 1].Text;
                for (int i = 0; i < QueryMethods.GetLength(0); i++)
                    if (string.Equals(QueryMethods[i, 0], name, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>From a ')', the index of its matching '('.</summary>
        private static int MatchingOpenParen(List<Token> tokens, int closeIndex)
        {
            int depth = 0;
            for (int i = closeIndex; i >= 0; i--)
            {
                if (tokens[i].Kind == TokenKind.RParen) depth++;
                else if (tokens[i].Kind == TokenKind.LParen && --depth == 0) return i;
            }
            return -1;
        }

        /// <summary>From a ']', the index just before its matching '['.</summary>
        private static int SkipBracketsBackwards(List<Token> tokens, int closeIndex)
        {
            int depth = 0;
            for (int i = closeIndex; i >= 0; i--)
            {
                if (tokens[i].Kind == TokenKind.RBracket) depth++;
                else if (tokens[i].Kind == TokenKind.LBracket && --depth == 0) return i - 1;
            }
            return -1;
        }

        /// <summary>From a '[', the index just after its matching ']'.</summary>
        private static int SkipBracketsForwards(List<Token> tokens, int openIndex, int limit)
        {
            int depth = 0;
            for (int i = openIndex; i <= limit; i++)
            {
                if (tokens[i].Kind == TokenKind.LBracket) depth++;
                else if (tokens[i].Kind == TokenKind.RBracket && --depth == 0) return i + 1;
            }
            return limit + 1;
        }

        /// <summary>Prefab name inside all(...) / named(...), when written literally.</summary>
        private static string? FindPrefabForSelector(List<Token> tokens, int closeParenIndex)
        {
            int depth = 0;
            for (int i = closeParenIndex; i >= 0; i--)
            {
                if (tokens[i].Kind == TokenKind.RParen) depth++;
                else if (tokens[i].Kind == TokenKind.LParen)
                {
                    depth--;
                    if (depth != 0) continue;

                    if (i + 1 >= tokens.Count) return null;

                    var first = tokens[i + 1];
                    if (first.Kind == TokenKind.Identifier) return first.Text;
                    if (first.Kind == TokenKind.HashLiteral) return first.StringValue;
                    return null;
                }
            }
            return null;
        }

        private static bool IsInsideHashString(string source, int position)
        {
            // Looks back, on the same line, for a #" with no closing quote.
            for (int i = position - 1; i >= 0; i--)
            {
                char c = source[i];
                if (c == '\n') return false;
                if (c == '"')
                    return i >= 1 && source[i - 1] == '#';
            }
            return false;
        }

        private static int LastTokenIndexBefore(List<Token> tokens, int position)
        {
            int result = -1;
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Kind == TokenKind.EndOfFile) break;
                if (tokens[i].Span.End <= position) result = i;
                else break;
            }
            return result;
        }

        /// <summary>Partial identifier immediately before the caret.</summary>
        private static SourceSpan GetPrefixSpan(string source, int caretOffset)
        {
            int start = caretOffset;
            while (start > 0 && IsIdentifierChar(source[start - 1])) start--;
            return SourceSpan.FromBounds(start, caretOffset);
        }

        private static bool IsIdentifierChar(char c) =>
            c == '_' || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

        // ==================================================================
        //  Suggestion generators
        // ==================================================================

        private static void AddPins(List<CompletionItem> items, SourceSpan span, IEditorEnvironment environment)
        {
            for (int pin = 0; pin <= 5; pin++)
            {
                var device = environment.GetWiredDevice(pin);
                string? label = environment.GetWiredDeviceLabel(pin);

                // Showing what is wired to each pin is the whole point: without it the
                // player has to leave the editor to check the wiring.
                string detail = device == null
                    ? "(empty)"
                    : (label != null ? label + " - " + device.DisplayName : device.DisplayName);

                items.Add(new CompletionItem("d" + pin, CompletionKind.Pin, detail, span, pin));
            }
        }

        private static void AddDeviceProperties(List<CompletionItem> items, SourceSpan span,
                                                IEditorEnvironment environment, int pin)
        {
            var device = pin >= 0 ? environment.GetWiredDevice(pin) : null;

            if (device != null)
            {
                // We know the equipment: suggest only what it accepts, with its current value.
                foreach (var property in device.Properties)
                {
                    double? value = property.Access.CanRead()
                        ? environment.GetLiveValue(pin, property.LogicType)
                        : null;

                    string detail = property.Access.Label();
                    if (value.HasValue) detail += "  = " + FormatValue(value.Value);

                    items.Add(new CompletionItem(property.Name, CompletionKind.Property, detail, span));
                }

                if (device.SlotCount > 0)
                    items.Add(new CompletionItem("slot", CompletionKind.SlotProperty,
                        device.SlotCount + " slots", span, 1));

                return;
            }

            // Empty pin or missing catalog: fall back to the game's full list.
            foreach (var pair in GameEnums.LogicTypeByName)
            {
                if (pair.Value == 0) continue;               // LogicType.None
                items.Add(new CompletionItem(pair.Key, CompletionKind.Property, string.Empty, span));
            }
            items.Add(new CompletionItem("slot", CompletionKind.SlotProperty, string.Empty, span, 1));
        }

        private static void AddSlotProperties(List<CompletionItem> items, SourceSpan span,
                                              IEditorEnvironment environment, int pin)
        {
            var device = pin >= 0 ? environment.GetWiredDevice(pin) : null;

            if (device != null && device.SlotProperties.Count > 0)
            {
                foreach (var slot in device.SlotProperties)
                    items.Add(new CompletionItem(slot.Name, CompletionKind.SlotProperty, "r", span));
                return;
            }

            foreach (var pair in GameEnums.LogicSlotTypeByName)
            {
                if (pair.Value == 0) continue;
                items.Add(new CompletionItem(pair.Key, CompletionKind.SlotProperty, string.Empty, span));
            }
        }

        private static void AddPrefabs(List<CompletionItem> items, SourceSpan span,
                                       IEditorEnvironment environment, string prefix)
        {
            foreach (var device in environment.Catalog.Search(prefix, MaxItems))
                items.Add(new CompletionItem(device.PrefabName, CompletionKind.Prefab,
                    device.DisplayName, span));
        }

        private static void AddStructFields(List<CompletionItem> items, SourceSpan span,
                                            DeclaredStruct? declared)
        {
            if (declared == null) return;

            foreach (var field in declared.Fields)
            {
                string detail = field.TypeName + (field.ArrayDepth > 0 ? "[]" : string.Empty);
                items.Add(new CompletionItem(field.Name, CompletionKind.Property, detail, span));
            }
        }

        /// <summary>
        /// The methods a list understands. The ones that change it are only offered
        /// on the list itself: a query hands back a result, not the list.
        /// </summary>
        private static void AddQueryMethods(List<CompletionItem> items, SourceSpan span, bool wholeList)
        {
            if (wholeList)
            {
                for (int i = 0; i < ListOnlyMembers.GetLength(0); i++)
                {
                    items.Add(new CompletionItem(ListOnlyMembers[i, 0], CompletionKind.Method,
                        ListOnlyMembers[i, 1], span, i));
                }
            }

            for (int i = 0; i < QueryMethods.GetLength(0); i++)
            {
                // 'count' is both the number of items and a method over a query; on the
                // list itself it was already offered above.
                if (wholeList && string.Equals(QueryMethods[i, 0], "count", StringComparison.Ordinal))
                    continue;

                items.Add(new CompletionItem(QueryMethods[i, 0], CompletionKind.Method,
                    QueryMethods[i, 1], span, 10 + i));
            }
        }

        private static void AddGeneral(List<CompletionItem> items, SourceSpan span,
                                       List<DeclaredSymbol> declarations, IEditorEnvironment environment)
        {
            // Declared names come first: they are the most likely ones.
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var symbol in declarations)
            {
                if (!seen.Add(symbol.Name)) continue;

                switch (symbol.Kind)
                {
                    case DeclaredKind.Device:
                    {
                        var device = symbol.Pin >= 0 ? environment.GetWiredDevice(symbol.Pin) : null;
                        string detail = "d" + (symbol.Pin >= 0 ? symbol.Pin.ToString() : "?");
                        if (device != null) detail += " - " + device.DisplayName;
                        items.Add(new CompletionItem(symbol.Name, CompletionKind.Device, detail, span, 0));
                        break;
                    }
                    case DeclaredKind.Function:
                        items.Add(new CompletionItem(symbol.Name, CompletionKind.Function, "fn", span, 1));
                        break;
                    case DeclaredKind.Constant:
                        items.Add(new CompletionItem(symbol.Name, CompletionKind.Constant, "const", span, 1));
                        break;
                    case DeclaredKind.Parameter:
                        items.Add(new CompletionItem(symbol.Name, CompletionKind.Parameter, "param", span, 1));
                        break;
                    case DeclaredKind.Struct:
                        items.Add(new CompletionItem(symbol.Name, CompletionKind.Keyword, "struct", span, 1));
                        break;
                    default:
                        items.Add(new CompletionItem(symbol.Name, CompletionKind.Variable, "var", span, 1));
                        break;
                }
            }

            foreach (var builtin in Builtins.AllBuiltins)
            {
                if (!seen.Add(builtin.Name)) continue;
                items.Add(new CompletionItem(builtin.Name, CompletionKind.Builtin,
                    builtin.Arity + " arg", span, 2));
            }

            if (seen.Add("sleep"))
                items.Add(new CompletionItem("sleep", CompletionKind.Builtin, "1 arg", span, 2));

            foreach (var keyword in Keywords)
            {
                if (!seen.Add(keyword)) continue;
                items.Add(new CompletionItem(keyword, CompletionKind.Keyword, string.Empty, span, 3));
            }
        }

        // ==================================================================
        //  Filtering and sorting
        // ==================================================================

        private static List<CompletionItem> Filter(List<CompletionItem> items, string prefix)
        {
            var result = new List<CompletionItem>(items.Count);

            foreach (var item in items)
            {
                if (prefix.Length == 0)
                {
                    result.Add(item);
                    continue;
                }

                int index = item.Label.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                if (index < 0) continue;

                // Whatever starts with what was typed goes ahead of the rest.
                int bonus = index == 0 ? 0 : 100;
                result.Add(new CompletionItem(item.Label, item.Kind, item.Detail,
                    item.ReplaceSpan, item.Order + bonus));
            }

            result.Sort((a, b) =>
            {
                if (a.Order != b.Order) return a.Order.CompareTo(b.Order);
                return string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
            });

            if (result.Count > MaxItems) result.RemoveRange(MaxItems, result.Count - MaxItems);
            return result;
        }

        internal static string FormatValue(double value)
        {
            if (double.IsNaN(value)) return "nan";
            if (double.IsPositiveInfinity(value)) return "inf";
            if (double.IsNegativeInfinity(value)) return "-inf";
            if (value == Math.Floor(value) && Math.Abs(value) < 1e15)
                return value.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
            return value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
