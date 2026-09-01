using System;
using System.Collections.Generic;
using System.Text;
using IZLang.Binding;
using IZLang.Devices;
using IZLang.Diagnostics;
using IZLang.Lexing;
using IZLang.Vm;

namespace IZLang.Editor
{
    public enum HoverKind
    {
        None,
        Device,
        DeviceProperty,
        SlotProperty,
        Variable,
        Constant,
        Function,
        Parameter,
        Builtin,
        Keyword,
        Prefab,
    }

    /// <summary>Tooltip content. The Unity layer only draws it; nothing is decided there.</summary>
    public sealed class HoverInfo
    {
        public HoverKind Kind { get; }

        /// <summary>First line, highlighted: the name of whatever is under the caret.</summary>
        public string Title { get; }

        /// <summary>Following lines: type, wired device, current values.</summary>
        public IReadOnlyList<string> Lines { get; }

        /// <summary>The source range the tooltip came from.</summary>
        public SourceSpan Span { get; }

        public HoverInfo(HoverKind kind, string title, IReadOnlyList<string> lines, SourceSpan span)
        {
            Kind = kind;
            Title = title;
            Lines = lines;
            Span = span;
        }

        public static HoverInfo None { get; } =
            new HoverInfo(HoverKind.None, string.Empty, Array.Empty<string>(), new SourceSpan(0, 0));

        public bool IsEmpty => Kind == HoverKind.None;

        /// <summary>Plain text, one line per item. Used by the tooltip and by the tests.</summary>
        public string ToText()
        {
            var sb = new StringBuilder(Title);
            foreach (var line in Lines) sb.Append('\n').Append(line);
            return sb.ToString();
        }

        public override string ToString() => ToText();
    }

    /// <summary>
    /// Works out what is under the caret and builds the tooltip.
    ///
    /// The case that matters is hovering a device variable: the tooltip shows which
    /// equipment is on that pin and the value of each of its properties right now,
    /// without having to leave the editor to check.
    /// </summary>
    public static class HoverEngine
    {
        /// <summary>How many properties to list for a device before summarizing.</summary>
        private const int MaxPropertiesShown = 12;

        public static HoverInfo GetHover(string source, int offset, IEditorEnvironment? environment = null)
        {
            environment ??= EmptyEditorEnvironment.Instance;
            source ??= string.Empty;
            if (offset < 0 || offset >= source.Length) return HoverInfo.None;

            var diagnostics = new DiagnosticBag();
            var tokens = new Lexer(source, diagnostics).Tokenize();

            int index = FindTokenAt(tokens, offset);
            if (index < 0) return HoverInfo.None;

            var token = tokens[index];
            var declarations = DeclarationScanner.Scan(tokens);

            switch (token.Kind)
            {
                case TokenKind.Identifier:
                    return DescribeIdentifier(tokens, index, declarations, environment, source);

                case TokenKind.HashLiteral:
                    return DescribePrefab(token.StringValue, token.Span, environment);

                default:
                    return DescribeKeyword(token);
            }
        }

        // ==================================================================
        //  Identifiers
        // ==================================================================

        private static HoverInfo DescribeIdentifier(List<Token> tokens, int index,
                                                    List<DeclaredSymbol> declarations,
                                                    IEditorEnvironment environment,
                                                    string source)
        {
            var token = tokens[index];

            // Preceded by '.', it is a property - the meaning comes from what is on the left.
            if (index >= 1 && tokens[index - 1].Kind == TokenKind.Dot)
                return DescribeMember(tokens, index, declarations, environment);

            var symbol = DeclarationScanner.Find(declarations, token.Text);

            if (symbol != null && symbol.Kind == DeclaredKind.Device)
                return DescribeDevice(symbol, token.Span, environment);

            if (symbol != null)
                return DescribeDeclared(symbol, token.Span, source);

            // An undeclared name inside all(...)/named(...) counts as a prefab.
            if (IsInsideSelector(tokens, index))
                return DescribePrefab(token.Text, token.Span, environment);

            // 'Color' on its own: the group, not a value of it.
            var constantGroup = GameEnums.FindConstantGroup(token.Text);
            if (constantGroup != null)
            {
                return new HoverInfo(HoverKind.Constant, token.Text,
                    new[]
                    {
                        "a group of the game's values - write " + token.Text + ".<value>",
                        constantGroup.Count + " values",
                    },
                    token.Span);
            }

            if (Vm.Builtins.TryGet(token.Text, out var builtin))
            {
                return new HoverInfo(HoverKind.Builtin, builtin.Name + "(" + builtin.Arity + " arg)",
                    new[] { "native function", builtin.Signature() },
                    token.Span);
            }

            if (string.Equals(token.Text, "sleep", StringComparison.Ordinal))
            {
                return new HoverInfo(HoverKind.Builtin, "sleep(seconds)",
                    new[] { "suspends the program and gives the tick back to the game" }, token.Span);
            }

            if (string.Equals(token.Text, "isset", StringComparison.Ordinal))
            {
                return new HoverInfo(HoverKind.Builtin, "isset(dev) -> bool",
                    new[]
                    {
                        "true when the pin has a device connected right now",
                        "takes a device declared with 'device', or a pin like 'd0' and 'db'",
                    },
                    token.Span);
            }

            if (DeclarationScanner.TryParsePin(token.Text, out int pin))
                return DescribePin(pin, token.Span, environment);

            return HoverInfo.None;
        }

        /// <summary>
        /// The tooltip that motivated the feature: which equipment is on the pin, and
        /// what each of its properties is worth at this moment.
        /// </summary>
        private static HoverInfo DescribeDevice(DeclaredSymbol symbol, SourceSpan span,
                                                IEditorEnvironment environment)
        {
            var lines = new List<string>();
            string title = symbol.Name;

            // A batch device is not on a cable: there is no pin to inspect and no
            // single reading to show, so the tooltip explains what it reaches.
            if (symbol.BatchSelector != null)
            {
                lines.Add("every device matching this selector, on the same data network");
                lines.Add("a read averages them; a write reaches all of them");

                // When the selector lands on a single kind of equipment, the tooltip can
                // say which one it is and what it reads right now, exactly as a pin does.
                var matched = CompletionEngine.ResolveDevice(environment, -1, symbol.Selector);
                if (matched != null)
                {
                    lines.Add(matched.DisplayName);
                    lines.Add(matched.PrefabName + "  (" + matched.PrefabHash + ")");
                    AppendLiveValues(lines, matched, -1, symbol.Selector, environment);
                }

                return new HoverInfo(HoverKind.Device, symbol.Name + " = " + symbol.BatchSelector,
                                     lines, span);
            }

            if (symbol.Pin < 0)
            {
                lines.Add("device with no valid pin");
                return new HoverInfo(HoverKind.Device, title, lines, span);
            }

            title = symbol.Name + " = " + DevicePins.Name(symbol.Pin);

            var device = environment.GetWiredDevice(symbol.Pin);
            if (device == null)
            {
                lines.Add(symbol.Pin == DevicePins.Housing
                    ? "the chip is not installed in a device"
                    : "pin " + DevicePins.Name(symbol.Pin) + " is empty - nothing connected");
                return new HoverInfo(HoverKind.Device, title, lines, span);
            }

            if (symbol.Pin == DevicePins.Housing)
                lines.Add("the device the chip is installed in");

            string? label = environment.GetWiredDeviceLabel(symbol.Pin);
            lines.Add(label != null
                ? device.DisplayName + "  \"" + label + "\""
                : device.DisplayName);
            lines.Add(device.PrefabName + "  (" + device.PrefabHash + ")");

            AppendLiveValues(lines, device, symbol.Pin, default, environment);
            return new HoverInfo(HoverKind.Device, title, lines, span);
        }

        private static void AppendLiveValues(List<string> lines, DeviceInfo device, int pin,
                                             DeviceSelector selector, IEditorEnvironment environment)
        {
            var readable = new List<string>();
            int hidden = 0;

            foreach (var property in device.Properties)
            {
                if (!property.Access.CanRead()) continue;

                double? value = CompletionEngine.ReadValue(environment, pin, selector, property.LogicType);
                if (!value.HasValue) continue;

                if (readable.Count >= MaxPropertiesShown) { hidden++; continue; }

                readable.Add("  " + property.Name.PadRight(18) +
                             CompletionEngine.FormatValue(value.Value) +
                             (property.Access.CanWrite() ? "" : "   (read only)"));
            }

            if (readable.Count == 0)
            {
                lines.Add("");
                lines.Add(device.Properties.Count + " properties, none readable right now");
                return;
            }

            lines.Add("");
            lines.AddRange(readable);
            if (hidden > 0) lines.Add("  ... and " + hidden + " more");
        }

        private static HoverInfo DescribePin(int pin, SourceSpan span, IEditorEnvironment environment)
        {
            var device = environment.GetWiredDevice(pin);
            var lines = new List<string>();

            if (pin == DevicePins.Housing)
                lines.Add("the device the chip is installed in");

            if (device == null)
            {
                lines.Add(pin == DevicePins.Housing
                    ? "the chip is not installed in a device"
                    : "empty pin");
            }
            else
            {
                string? label = environment.GetWiredDeviceLabel(pin);
                lines.Add(label != null ? device.DisplayName + "  \"" + label + "\"" : device.DisplayName);
                lines.Add(device.PrefabName);
            }

            return new HoverInfo(HoverKind.Device, DevicePins.Name(pin), lines, span);
        }

        /// <summary>Property after the dot: 'pump.Pressure', 'x.slot[0].Quantity'.</summary>
        private static HoverInfo DescribeMember(List<Token> tokens, int index,
                                                List<DeclaredSymbol> declarations,
                                                IEditorEnvironment environment)
        {
            var token = tokens[index];
            string name = token.Text;

            // 'Color.Black' - one of the game's named values, not a device property.
            if (index >= 2 && tokens[index - 2].Kind == TokenKind.Identifier &&
                DeclarationScanner.Find(declarations, tokens[index - 2].Text) == null)
            {
                string group = tokens[index - 2].Text;
                var values = GameEnums.FindConstantGroup(group);

                if (values != null)
                {
                    var found = values.TryGetValue(name, out int constant);
                    return new HoverInfo(HoverKind.Constant, group + "." + name,
                        found
                            ? new[] { "one of the game's values", "= " + constant }
                            : new[] { "'" + group + "' has no value named '" + name + "'" +
                                      GameEnums.Suggest(values.Keys, name) },
                        token.Span);
                }
            }

            bool isSlot = index >= 2 && tokens[index - 2].Kind == TokenKind.RBracket;

            if (isSlot)
            {
                if (!GameEnums.LogicSlotTypeByName.TryGetValue(name, out int slotType))
                    return HoverInfo.None;

                return new HoverInfo(HoverKind.SlotProperty, name,
                    new[] { "slot property", "LogicSlotType " + slotType, "read only" },
                    token.Span);
            }

            if (!GameEnums.LogicTypeByName.TryGetValue(name, out int logicType))
                return HoverInfo.None;

            var lines = new List<string> { "LogicType " + logicType };

            // When the left side is a declared device, we can say whether THIS
            // equipment accepts the property, and what it is worth right now.
            int pin = -1;
            var selector = default(DeviceSelector);
            if (index >= 2 && tokens[index - 2].Kind == TokenKind.Identifier)
            {
                var symbol = DeclarationScanner.Find(declarations, tokens[index - 2].Text);
                if (symbol != null && symbol.Kind == DeclaredKind.Device)
                {
                    pin = symbol.Pin;
                    selector = symbol.Selector;
                }
            }

            var device = CompletionEngine.ResolveDevice(environment, pin, selector);
            if (device != null)
            {
                var property = device.FindProperty(name);
                if (property == null)
                {
                    lines.Add(device.DisplayName + " does NOT accept this property");
                }
                else
                {
                    lines.Add(device.DisplayName + " - " + AccessText(property.Access));

                    double? value = CompletionEngine.ReadValue(environment, pin, selector, logicType);
                    if (value.HasValue) lines.Add("current value: " + CompletionEngine.FormatValue(value.Value));
                }
            }

            return new HoverInfo(HoverKind.DeviceProperty, name, lines, token.Span);
        }

        private static string AccessText(LogicAccess access)
        {
            switch (access)
            {
                case LogicAccess.ReadWrite: return "read and write";
                case LogicAccess.Read: return "read only";
                case LogicAccess.Write: return "write only";
                default: return "no access";
            }
        }

        private static HoverInfo DescribeDeclared(DeclaredSymbol symbol, SourceSpan span, string source)
        {
            switch (symbol.Kind)
            {
                case DeclaredKind.Function:
                    return new HoverInfo(HoverKind.Function, "fn " + symbol.Name,
                        new[] { "function declared in this program" }, span);
                case DeclaredKind.Constant:
                    return new HoverInfo(HoverKind.Constant, "const " + symbol.Name,
                        new[] { "constant, folded at compile time" }, span);
                case DeclaredKind.Struct:
                    return new HoverInfo(HoverKind.Variable, "struct " + symbol.Name,
                        new[] { "struct declared in this program" }, span);
                case DeclaredKind.Parameter:
                    return new HoverInfo(HoverKind.Parameter, symbol.Name + TypeSuffix(symbol, source),
                        new[] { "function parameter" }, span);
                default:
                    return new HoverInfo(HoverKind.Variable, "var " + symbol.Name + TypeSuffix(symbol, source),
                        new[] { "variable" }, span);
            }
        }

        /// <summary>": num[8]" when the declaration said so, and nothing otherwise.</summary>
        private static string TypeSuffix(DeclaredSymbol symbol, string source)
        {
            var span = symbol.TypeSpan;
            if (span.Length <= 0 || span.End > source.Length) return string.Empty;
            return ": " + source.Substring(span.Start, span.Length);
        }

        private static HoverInfo DescribePrefab(string prefabName, SourceSpan span,
                                                IEditorEnvironment environment)
        {
            int hash = PrefabHash.Compute(prefabName);
            var lines = new List<string> { "hash " + hash };

            var device = environment.Catalog.FindByName(prefabName);
            if (device != null)
            {
                lines.Insert(0, device.DisplayName);
                lines.Add(device.Properties.Count + " properties, " + device.SlotCount + " slots");
            }
            else if (!environment.Catalog.IsEmpty)
            {
                // The catalog is loaded and the name is not in it: almost always a typo,
                // which would otherwise only show up as an empty batch.
                lines.Add("no prefab exists with this name");
            }

            return new HoverInfo(HoverKind.Prefab, prefabName, lines, span);
        }

        private static HoverInfo DescribeKeyword(Token token)
        {
            string? description = KeywordHelp(token.Kind);
            if (description == null) return HoverInfo.None;

            return new HoverInfo(HoverKind.Keyword, token.Text, new[] { description }, token.Span);
        }

        private static string? KeywordHelp(TokenKind kind)
        {
            switch (kind)
            {
                case TokenKind.KwDevice:
                    return "binds a name to a housing pin (d0..d5), or to 'db' - " +
                           "the device the chip is installed in";
                case TokenKind.KwVar: return "declares a variable";
                case TokenKind.KwConst: return "declares a constant, computed at compile time";
                case TokenKind.KwFn: return "declares a function";
                case TokenKind.KwLoop: return "infinite loop; without 'yield' it is preempted by the budget";
                case TokenKind.KwYield: return "gives the tick back to the game; resumes at the next instruction";
                case TokenKind.KwWhile: return "repeats while the condition is true";
                case TokenKind.KwFor: return "walks a range: 'for i in 0..10'";
                case TokenKind.KwBreak: return "leaves the current loop";
                case TokenKind.KwContinue: return "skips to the next turn of the loop";
                case TokenKind.KwReturn: return "returns from the function";
                case TokenKind.KwList: return "a list: an array plus how much of it is in use, 'list num[8]'";
                case TokenKind.KwAll: return "selects every device of a prefab";
                case TokenKind.KwNamed: return "selects devices by label: named(Prefab, \"label\")";
                default: return null;
            }
        }

        // ==================================================================
        //  Helpers
        // ==================================================================

        private static int FindTokenAt(List<Token> tokens, int offset)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Kind == TokenKind.EndOfFile) break;

                var span = tokens[i].Span;
                if (offset >= span.Start && offset < span.End) return i;
                if (span.Start > offset) break;
            }
            return -1;
        }

        /// <summary>true when the token is the first argument of all(...) or named(...).</summary>
        private static bool IsInsideSelector(List<Token> tokens, int index)
        {
            if (index < 2) return false;
            return tokens[index - 1].Kind == TokenKind.LParen &&
                   (tokens[index - 2].Kind == TokenKind.KwAll || tokens[index - 2].Kind == TokenKind.KwNamed);
        }
    }
}
