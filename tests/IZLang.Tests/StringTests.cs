using System.Linq;
using IZLang.Binding;
using IZLang.Diagnostics;
using IZLang.Vm;
using Xunit;

namespace IZLang.Tests
{
    /// <summary>
    /// The runtime <c>str</c>: text built while the program runs, compared, joined,
    /// hashed, and collected once nobody points at it any more.
    /// </summary>
    public class StringTests
    {
        private const int LogicSetting = 12;

        // ------------------------------------------------------------------
        //  A value that survives the trip through a double
        // ------------------------------------------------------------------

        [Fact]
        public void AStringLiteralIsAValueAndNotAHash()
        {
            // "ok" used to compile to the CRC32 of "ok" and the text was gone. Now the
            // slot carries a handle that reads back as the text itself.
            Assert.Equal("ok", Text("\"ok\""));
        }

        [Fact]
        public void AZeroedCellReadsAsTheEmptyString()
        {
            // A field nobody assigned holds 0.0, which is not a handle. Reading it as
            // a str has to give "" and not whatever slot zero happens to hold.
            Assert.Equal(string.Empty, Run(
                "var blank: str;\n" +
                "result = blank;"));

            Assert.Equal(string.Empty, Run(
                "var t: Target;\n" +
                "result = t.label;",
                "struct Target { prefab: num; label: str; }\n"));

            Assert.Equal(0.0, TestHost.Eval("len(t.label)",
                "struct Target { prefab: num; label: str; }\nvar t: Target;\n"));
        }

        [Fact]
        public void AStringGoesIntoAFunctionAndComesBackOut() =>
            Assert.Equal("north-wing", Text("tag(\"north\")",
                "fn tag(prefix: str) -> str { return prefix + \"-wing\"; }\n"));

        [Fact]
        public void AStringLivesInAnArrayCell() =>
            Assert.Equal("b", Run(
                "var names: str[3];\n" +
                "names[1] = \"b\";\n" +
                "result = names[1];"));

        [Fact]
        public void AStringLivesInAStructField() =>
            Assert.Equal("pump1", Run(
                "var t: Target;\n" +
                "t.label = \"pump1\";\n" +
                "result = t.label;",
                "struct Target { prefab: num; label: str; }\n"));

        // ------------------------------------------------------------------
        //  Operators
        // ------------------------------------------------------------------

        [Fact]
        public void ConcatenationJoinsTwoStrings() =>
            Assert.Equal("abcdef", Text("\"abc\" + \"def\""));

        [Fact]
        public void ConcatenationBuildsFromRuntimeValues() =>
            Assert.Equal("t = 42.5", Run(
                "var reading = 42.5;\n" +
                "result = \"t = \" + text(reading);"));

        [Fact]
        public void PlusEqualsAppends() =>
            Assert.Equal("a-b-c", Run(
                "result = \"a\";\n" +
                "result += \"-b\";\n" +
                "result += \"-c\";"));

        [Fact]
        public void PlusEqualsAppendsToAStructField() =>
            Assert.Equal("ab", Run(
                "var t: Target;\n" +
                "t.label = \"a\";\n" +
                "t.label += \"b\";\n" +
                "result = t.label;",
                "struct Target { prefab: num; label: str; }\n"));

        [Theory]
        [InlineData("\"ab\" == \"ab\"", 1.0)]
        [InlineData("\"ab\" == \"ac\"", 0.0)]
        [InlineData("\"ab\" != \"ac\"", 1.0)]
        [InlineData("\"ab\" < \"ac\"", 1.0)]
        [InlineData("\"B\" < \"a\"", 1.0)]            // ordinal: uppercase sorts first
        [InlineData("\"ab\" >= \"ab\"", 1.0)]
        [InlineData("\"abc\" > \"ab\"", 1.0)]
        public void ComparisonsAreOrdinal(string expression, double expected) =>
            Assert.Equal(expected, TestHost.Eval(expression));

        [Fact]
        public void TwoStringsBuiltSeparatelyStillCompareEqual()
        {
            // Nothing guarantees the two took the same route, so equality has to look
            // at the text - which is also what interning makes cheap.
            Assert.Equal(1.0, TestHost.Eval(
                "left == right",
                "var part = \"pump\";\n" +
                "var left = part + \"-1\";\n" +
                "var right = \"pump\" + \"-1\";\n"));
        }

        [Fact]
        public void AStringComparedWithANumberIsAnError() =>
            TestHost.CompileError(
                "fn main() { var b = \"a\" == 1; }\n",
                IZErrorCode.TypeMismatch);

        [Fact]
        public void AddingANumberToAStringPointsAtText()
        {
            var error = TestHost.CompileError(
                "fn main() { var s = \"t = \" + 42; }\n",
                IZErrorCode.TypeMismatch);

            Assert.Contains("text(x)", error.Message);
        }

        [Fact]
        public void AStringDoesNoArithmetic() =>
            TestHost.CompileError(
                "fn main() { var s = \"a\" * 2; }\n",
                IZErrorCode.TypeMismatch);

        [Fact]
        public void MinusEqualsOnAStringIsAnError() =>
            TestHost.CompileError(
                "var s = \"a\";\nfn main() { s -= \"b\"; }\n",
                IZErrorCode.TypeMismatch);

        [Fact]
        public void TheTernaryChoosesBetweenTwoStrings()
        {
            Assert.Equal("open", Text("locked ? \"shut\" : \"open\"", "var locked = false;\n"));
            Assert.Equal("shut", Text("locked ? \"shut\" : \"open\"", "var locked = true;\n"));
        }

        [Fact]
        public void AStringChosenByATernaryStillJoins() =>
            Assert.Equal("vent-north", Text("\"vent-\" + (north ? \"north\" : \"south\")",
                                            "var north = true;\n"));

        // ------------------------------------------------------------------
        //  The text library
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("len(\"hello\")", 5.0)]
        [InlineData("len(\"\")", 0.0)]
        [InlineData("char(\"abc\", 1)", 98.0)]
        [InlineData("char(\"abc\", 9)", -1.0)]
        [InlineData("find(\"corridor\", \"rid\")", 3.0)]
        [InlineData("find(\"corridor\", \"zz\")", -1.0)]
        [InlineData("find(\"corridor\", \"\")", 0.0)]
        [InlineData("parse(\"12.5\")", 12.5)]
        public void TheBuiltinsThatAnswerWithANumber(string expression, double expected) =>
            Assert.Equal(expected, TestHost.Eval(expression), 10);

        [Fact]
        public void ParseOfSomethingThatIsNotANumberIsNan() =>
            Assert.True(double.IsNaN(TestHost.Eval("parse(\"north\")")));

        [Theory]
        [InlineData("sub(\"corridor\", 0, 4)", "corr")]
        [InlineData("sub(\"corridor\", 5, 99)", "dor")]      // the count is clamped
        [InlineData("sub(\"corridor\", 99, 1)", "")]        // and so is the start
        [InlineData("chr(65)", "A")]
        [InlineData("chr(7)", "")]                          // control codes are refused
        [InlineData("text(101.325)", "101.325")]
        [InlineData("text(42)", "42")]
        [InlineData("text(1.0 / 0.0)", "inf")]
        [InlineData("fixed(101.325, 1)", "101.3")]
        [InlineData("fixed(2, 3)", "2.000")]
        public void TheBuiltinsThatAnswerWithText(string expression, string expected) =>
            Assert.Equal(expected, Text(expression));

        [Fact]
        public void LenStillFoldsForAnArrayAndCallsForAString()
        {
            var array = TestHost.CompileOk(
                "device out = d0;\n" +
                "fn main() { var a: num[8]; out.Setting = len(a); }\n");
            Assert.DoesNotContain(array.Code, i => i.Op == OpCode.CallBuiltin);

            var text = TestHost.CompileOk(
                "device out = d0;\n" +
                "fn main() { out.Setting = len(\"hello\"); }\n");
            Assert.Contains(text.Code, i => i.Op == OpCode.CallBuiltin && i.A == (int)BuiltinId.Len);
        }

        [Fact]
        public void ABuiltinThatWantsTextRefusesANumber() =>
            TestHost.CompileError(
                "fn main() { var n = find(1, 2); }\n",
                IZErrorCode.TypeMismatch);

        [Fact]
        public void ABuiltinThatWantsANumberRefusesText() =>
            TestHost.CompileError(
                "fn main() { var n = abs(\"a\"); }\n",
                IZErrorCode.TypeMismatch);

        // ------------------------------------------------------------------
        //  Hashing: what ties a str to the device network
        // ------------------------------------------------------------------

        [Fact]
        public void HashOfALiteralIsFoldedAtCompileTime()
        {
            var program = TestHost.CompileOk(
                "device out = d0;\n" +
                "fn main() { out.Setting = hash(\"StructureVolumePump\"); }\n");

            Assert.Contains((double)PrefabHash.Compute("StructureVolumePump"), program.Constants);
            Assert.DoesNotContain(program.Code, i => i.Op == OpCode.CallBuiltin);
        }

        [Fact]
        public void HashOfTextBuiltAtRuntimeMatchesTheLiteral() =>
            Assert.Equal(
                (double)PrefabHash.Compute("north-1"),
                TestHost.Eval("hash(prefix + \"-1\")", "var prefix = \"north\";\n"));

        [Fact]
        public void ALabelBuiltAtRuntimeReachesTheRightDevice()
        {
            // This is what a compile time hash could never do: the label is decided
            // while the program runs.
            var host = new MemoryDeviceHost();
            var north = host.AddNetworkDevice(0, PrefabHash.Compute("wing-north"));
            var south = host.AddNetworkDevice(0, PrefabHash.Compute("wing-south"));

            RunOn(host,
                "fn main() {\n" +
                "    var side = \"north\";\n" +
                "    named(\"wing-\" + side).Setting = 5;\n" +
                "}\n");

            Assert.Equal(5.0, north.Get(LogicSetting));
            Assert.Equal(0.0, south.Get(LogicSetting));
        }

        [Fact]
        public void ALiteralLabelStillCostsNothingAtRuntime()
        {
            // named("x") folded to a hash before str was a value, and it still has to:
            // hashing the label again on every batch call would be a tax per tick.
            var program = TestHost.CompileOk("fn main() { named(\"pump1\").On = true; }");

            Assert.Contains((double)PrefabHash.Compute("pump1"), program.Constants);
            Assert.DoesNotContain(program.Code, i => i.Op == OpCode.CallBuiltin);
            Assert.DoesNotContain(program.Code, i => i.Op == OpCode.PushStr);
        }

        [Fact]
        public void AStringConstFoldsIntoTheLabelToo()
        {
            var host = new MemoryDeviceHost();
            RunOn(host,
                "const NORTH = \"north\";\n" +
                "fn main() { named(NORTH).On = true; }\n");

            Assert.Equal(PrefabHash.Compute("north"), host.BatchWrites.Single().NameHash);
        }

        [Fact]
        public void AStringConstCarriesItsTextAndNotItsHash() =>
            Assert.Equal("north", Text("NORTH", "const NORTH = \"north\";\n"));

        [Fact]
        public void TwoStringConstsJoinAtCompileTime() =>
            Assert.Equal("north-wing", Text("LABEL",
                "const SIDE = \"north\";\nconst LABEL = SIDE + \"-wing\";\n"));

        [Fact]
        public void APrefabNameCanBeBuiltAtRuntime()
        {
            var host = new MemoryDeviceHost();
            var pump = host.AddNetworkDevice(PrefabHash.Compute("StructureVolumePump"));

            RunOn(host,
                "fn main() {\n" +
                "    var kind = \"Structure\" + \"VolumePump\";\n" +
                "    all(kind).Setting = 3;\n" +
                "}\n");

            Assert.Equal(3.0, pump.Get(LogicSetting));
        }

        // ------------------------------------------------------------------
        //  Text on a display: what 'Setting' shows
        // ------------------------------------------------------------------
        //  A LED display in DisplayMode.String, and the circuit housing's own screen
        //  in SettingDisplayMode.String, read Setting back as up to six characters
        //  packed one per byte. That is the only place the game turns a reading into
        //  text, and the only property that accepts a str.

        [Fact]
        public void TextOnSettingIsPackedAtCompileTime()
        {
            var program = TestHost.CompileOk(
                "device out = d0;\n" +
                "fn main() { out.Setting = \"Ok\"; }\n");

            Assert.Contains(PackedText.Pack("Ok"), program.Constants);
            Assert.DoesNotContain(program.Code, i => i.Op == OpCode.CallBuiltin);
        }

        [Fact]
        public void TextOnSettingReachesTheDeviceAsANumber()
        {
            var host = TestHost.Execute(
                "device out = d0;\n" +
                "fn main() { out.Setting = \"Ok\"; }\n",
                h => h.Connect(0));

            Assert.Equal(PackedText.Pack("Ok"), host.Get(0, LogicSetting));
        }

        [Fact]
        public void TextBuiltAtRuntimeIsPackedWhileItRuns()
        {
            var host = TestHost.Execute(
                "device out = d0;\n" +
                "var half = \"O\";\n" +
                "fn main() { out.Setting = half + \"k\"; }\n",
                h => h.Connect(0));

            Assert.Equal(PackedText.Pack("Ok"), host.Get(0, LogicSetting));
        }

        [Fact]
        public void ABatchSettingTakesTextToo()
        {
            var host = new MemoryDeviceHost();
            var display = host.AddNetworkDevice(PrefabHash.Compute("StructureConsoleLED5"));

            RunOn(host, "fn main() { all(StructureConsoleLED5).Setting = \"Ok\"; }\n");

            Assert.Equal(PackedText.Pack("Ok"), display.Get(LogicSetting));
        }

        [Fact]
        public void TextTooLongForADisplayIsRefused()
        {
            // Seven characters: one byte too many for the number the display reads.
            // Better said here than silently lost on the way to the game.
            var error = TestHost.CompileError(
                "device out = d0;\n" +
                "fn main() { out.Setting = \"Standby\"; }\n",
                IZErrorCode.TypeMismatch);

            Assert.Contains("6", error.Message);
        }

        [Fact]
        public void PackstrAndUnpackstrAreEachOthersInverse() =>
            Assert.Equal("Ok", Text("unpackstr(packstr(\"Ok\"))"));

        [Fact]
        public void PackstrMatchesTheGamePacking() =>
            Assert.Equal(PackedText.Pack("Ok"), TestHost.Eval("packstr(\"Ok\")"));

        [Fact]
        public void UnpackstrReadsBackWhatADisplayWasGiven() =>
            Assert.Equal("ABC", Text("unpackstr(" +
                PackedText.Pack("ABC").ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ")"));

        // ------------------------------------------------------------------
        //  Memory
        // ------------------------------------------------------------------

        [Fact]
        public void TheSameTextAlwaysLandsOnTheSameSlot()
        {
            // A loop rebuilding the same text must not eat a slot per turn.
            var vm = Compile(
                "fn main() {\n" +
                "    var i = 0;\n" +
                "    while i < 200 {\n" +
                "        var s = \"tag-\" + \"1\";\n" +
                "        i = i + 1;\n" +
                "    }\n" +
                "}\n");

            TestHost.RunToCompletion(vm, maxTicks: 100);

            Assert.Equal(ExecutionResult.Halted, vm.State);
            Assert.Equal(3, vm.StringsUsed);          // two literals plus what they build
        }

        [Fact]
        public void TextThatNobodyPointsAtIsCollected()
        {
            // 1200 strings through a table that holds 512. It can only get to the end
            // because the ones from the earlier turns are swept.
            var vm = Compile(
                "fn main() {\n" +
                "    var i = 0;\n" +
                "    while i < 600 {\n" +
                "        var s = \"tag-\" + text(i);\n" +
                "        i = i + 1;\n" +
                "    }\n" +
                "}\n");

            TestHost.RunToCompletion(vm, maxTicks: 2000);

            Assert.Null(vm.Error);
            Assert.Equal(ExecutionResult.Halted, vm.State);
        }

        [Fact]
        public void TextThatIsStillReachableIsNotCollected()
        {
            // The array holds on to every one of them, so the loop that survives above
            // has to stop here instead of handing back somebody else's string.
            var vm = Compile(
                "fn main() {\n" +
                "    var kept: str[600];\n" +
                "    var i = 0;\n" +
                "    while i < 600 {\n" +
                "        kept[i] = \"tag-\" + text(i);\n" +
                "        i = i + 1;\n" +
                "    }\n" +
                "}\n");

            TestHost.RunToCompletion(vm, maxTicks: 2000);

            Assert.Equal(ExecutionResult.Error, vm.State);
            Assert.Equal(RuntimeErrorKind.StringOverflow, vm.Error!.Kind);
        }

        [Fact]
        public void ACollectionKeepsTheGlobalsAndTheLocals()
        {
            // 'kept' is a global and 'held' a local while 1400 other strings come and
            // go around them. Both have to still read as themselves at the end.
            var host = TestHost.Execute(
                "device out = d0;\n" +
                "var kept = \"\";\n" +
                "fn main() {\n" +
                "    kept = \"global-\" + \"value\";\n" +
                "    var held = \"local-\" + \"value\";\n" +
                "    var i = 0;\n" +
                "    while i < 700 {\n" +
                "        var junk = \"j\" + text(i);\n" +
                "        i = i + 1;\n" +
                "    }\n" +
                "    out.Setting = len(held) + len(kept);\n" +
                "    out.Setting = find(held, \"local-value\") + find(kept, \"global-value\");\n" +
                "}\n",
                h => h.Connect(0), maxTicks: 2000);

            Assert.Equal(23.0, host.Writes[host.Writes.Count - 2].Value);    // 11 + 12
            Assert.Equal(0.0, host.Writes[host.Writes.Count - 1].Value);     // both found at 0
        }

        [Fact]
        public void AStringTooLongToHoldStopsTheProgram()
        {
            var vm = Compile(
                "fn main() {\n" +
                "    var s = \"0123456789\";\n" +
                "    var i = 0;\n" +
                "    while i < 10 {\n" +
                "        s += s;\n" +
                "        i = i + 1;\n" +
                "    }\n" +
                "}\n");

            TestHost.RunToCompletion(vm, maxTicks: 100);

            Assert.Equal(ExecutionResult.Error, vm.State);
            Assert.Equal(RuntimeErrorKind.StringOverflow, vm.Error!.Kind);
            Assert.Contains("characters", vm.Error.Message);
        }

        [Fact]
        public void ResetGivesTheStringTableBack()
        {
            var vm = Compile("fn main() { var s = \"a\" + \"b\"; }\n");

            TestHost.RunToCompletion(vm);
            int afterTheFirstRun = vm.StringsUsed;

            vm.Reset();
            TestHost.RunToCompletion(vm);

            Assert.Equal(afterTheFirstRun, vm.StringsUsed);
        }

        // ------------------------------------------------------------------
        //  What a str still may not do
        // ------------------------------------------------------------------

        [Fact]
        public void ADevicePropertyOtherThanSettingDoesNotTakeText() =>
            TestHost.CompileError(
                "device out = d0;\n" +
                "fn main() { out.On = \"ok\"; }\n",
                IZErrorCode.TypeMismatch);

        [Fact]
        public void ABatchWriteOtherThanSettingDoesNotTakeText() =>
            TestHost.CompileError(
                "fn main() { all(StructureWallLight).Color = \"red\"; }\n",
                IZErrorCode.TypeMismatch);

        [Fact]
        public void TextIsNotACondition() =>
            TestHost.CompileError(
                "fn main() { if \"a\" { } }\n",
                IZErrorCode.TypeMismatch);

        [Fact]
        public void AFunctionCannotTakeTheNameOfABuiltin()
        {
            // 'text' and 'find' are ordinary words, and a call resolves to the builtin
            // first - so the body of a function with that name would never run.
            var error = TestHost.CompileError(
                "fn text(x: num) -> num { return x; }\n" +
                "fn main() { }\n",
                IZErrorCode.DuplicateName);

            Assert.Contains("builtin", error.Message);
        }

        [Fact]
        public void ALiteralLongerThanTheLimitIsRefused()
        {
            string tooLong = new string('x', IZLimits.MaxStringLength + 1);
            TestHost.CompileError(
                "fn main() { var s = \"" + tooLong + "\"; }\n",
                IZErrorCode.StringTooLong);
        }

        [Fact]
        public void AStringIsNotAnArrayLength() =>
            TestHost.CompileError(
                "fn main() { var a: num[\"3\"]; }\n",
                IZErrorCode.ConstExpressionRequired);

        // ------------------------------------------------------------------
        //  Helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Runs a body that has to assign the global <c>result</c>, and reads the text
        /// back. It goes through a global because every slot the VM exposes is a bare
        /// double: only the VM that produced the handle can turn it into text again.
        ///
        /// <c>result</c> is declared first, so it always sits in global slot 0.
        /// </summary>
        private static string Run(string body, string prelude = "")
        {
            string source =
                "var result: str = \"\";\n" +
                prelude +
                "fn main() {\n" +
                body + "\n" +
                "}\n";

            var vm = Compile(source);
            var state = TestHost.RunToCompletion(vm, maxTicks: 2000);

            Assert.True(state == ExecutionResult.Halted,
                "expected it to finish without an error: " + (vm.Error?.ToString() ?? state.ToString()));

            return vm.ReadString(vm.GetGlobal(0));
        }

        private static string Text(string expression, string prelude = "") =>
            Run("result = " + expression + ";", prelude);

        private static IZVm Compile(string source) =>
            new IZVm(TestHost.CompileOk(source), new MemoryDeviceHost(), randomSeed: 1);

        private static void RunOn(MemoryDeviceHost host, string source)
        {
            var program = TestHost.CompileOk(source);
            var vm = new IZVm(program, host, randomSeed: 1);
            var state = TestHost.RunToCompletion(vm);
            Assert.True(state == ExecutionResult.Halted,
                "expected it to finish without an error: " + (vm.Error?.ToString() ?? state.ToString()));
        }
    }
}
