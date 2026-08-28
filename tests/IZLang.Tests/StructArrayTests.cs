using System.Linq;
using IZLang.Diagnostics;
using IZLang.Vm;
using Xunit;

namespace IZLang.Tests
{
    /// <summary>
    /// Arrays and structs: the heap, its addressing, and the rules that keep an
    /// address from outliving the cells it points at.
    /// </summary>
    public class StructArrayTests
    {
        /// <summary>
        /// Compiles a program whose body writes to d0.Setting and gives back the
        /// last value written. Same idea as TestHost.Eval, but with room for a body
        /// of several statements.
        /// </summary>
        private static double Run(string body, string prelude = "")
        {
            string source =
                "device out = d0;\n" +
                prelude + "\n" +
                "fn main() {\n" + body + "\n}\n";

            var host = TestHost.Execute(source, h => h.Connect(0));
            Assert.True(host.Writes.Count > 0, "the program wrote nothing to d0");
            return host.Writes[host.Writes.Count - 1].Value;
        }

        // ------------------------------------------------------------------
        //  Arrays
        // ------------------------------------------------------------------

        [Fact]
        public void AnArrayStartsZeroed()
        {
            Assert.Equal(0.0, Run("var a: num[4];\nout.Setting = a[0] + a[3];"));
        }

        [Fact]
        public void ElementsAreWrittenAndReadBack()
        {
            Assert.Equal(30.0, Run(
                "var a: num[4];\n" +
                "a[0] = 10;\n" +
                "a[1] = 20;\n" +
                "out.Setting = a[0] + a[1];"));
        }

        [Fact]
        public void ElementsAreAddressedIndependently()
        {
            // Writing one cell must not touch its neighbours: that is the whole
            // point of the stride, and the easiest thing to get wrong.
            Assert.Equal(7.0, Run(
                "var a: num[3];\n" +
                "a[0] = 1;\n" +
                "a[1] = 2;\n" +
                "a[2] = 4;\n" +
                "out.Setting = a[0] + a[1] + a[2];"));
        }

        [Fact]
        public void TheIndexCanBeComputedAtRuntime()
        {
            Assert.Equal(45.0, Run(
                "var a: num[10];\n" +
                "for i in 0..10 { a[i] = i; }\n" +
                "var total = 0;\n" +
                "for i in 0..10 { total += a[i]; }\n" +
                "out.Setting = total;"));
        }

        [Fact]
        public void AnIndexIsTruncated()
        {
            // Truncated, not rounded, the same way a slot index behaves. A constant
            // like this would be caught at compile time, so it comes from a variable.
            Assert.Equal(9.0, Run(
                "var a: num[4];\n" +
                "var i = 1.9;\n" +
                "a[1] = 9;\n" +
                "out.Setting = a[i];"));
        }

        [Fact]
        public void AFractionalConstantIndexIsCaughtAtCompileTime()
        {
            TestHost.CompileError(
                "fn main() { var a: num[4]; var x = a[1.5]; }\n",
                IZErrorCode.IndexOutOfRange);
        }

        [Fact]
        public void CompoundAssignmentWorksOnAnElement()
        {
            Assert.Equal(11.0, Run(
                "var a: num[2];\n" +
                "a[0] = 5;\n" +
                "a[0] += 6;\n" +
                "out.Setting = a[0];"));
        }

        [Fact]
        public void CompoundAssignmentEvaluatesTheIndexOnce()
        {
            // 'a[next()] += 1' must move one cell, not read one and write another.
            Assert.Equal(1.0, Run(
                "var a: num[3];\n" +
                "a[next()] += 1;\n" +
                "out.Setting = a[0] + a[1] + a[2];",
                "var cursor = 0;\n" +
                "fn next() -> num {\n" +
                "    cursor = cursor + 1;\n" +
                "    return cursor - 1;\n" +
                "}\n"));
        }

        [Fact]
        public void AnArrayLiteralInitializesIt()
        {
            Assert.Equal(6.0, Run(
                "var a = [1, 2, 3];\n" +
                "out.Setting = a[0] + a[1] + a[2];"));
        }

        [Fact]
        public void AnArrayLiteralAcceptsAnAnnotation()
        {
            Assert.Equal(1.0, Run(
                "var flags: bool[2] = [false, true];\n" +
                "out.Setting = flags[1];"));
        }

        [Fact]
        public void TheLengthCanComeFromAConst()
        {
            Assert.Equal(4.0, Run(
                "var a: num[SIZE];\n" +
                "out.Setting = len(a);",
                "const SIZE = 2 * 2;\n"));
        }

        [Fact]
        public void LenIsTheDeclaredLength()
        {
            Assert.Equal(8.0, Run("var a: num[8];\nout.Setting = len(a);"));
        }

        [Fact]
        public void LenKeepsWhatItsArgumentDid()
        {
            // The length is a constant, but the expression that names the array still
            // has to run: dropping it would drop the side effect with it.
            Assert.Equal(1.0, Run(
                "var grid: num[2][3];\n" +
                "var width = len(grid[bump()]);\n" +
                "out.Setting = calls;",
                "var calls = 0;\n" +
                "fn bump() -> num {\n" +
                "    calls = calls + 1;\n" +
                "    return 0;\n" +
                "}\n"));
        }

        // ------------------------------------------------------------------
        //  Structs
        // ------------------------------------------------------------------

        [Fact]
        public void FieldsAreWrittenAndReadBack()
        {
            Assert.Equal(7.0, Run(
                "var p: Point;\n" +
                "p.x = 3;\n" +
                "p.y = 4;\n" +
                "out.Setting = p.x + p.y;",
                "struct Point { x: num; y: num; }\n"));
        }

        [Fact]
        public void AStructStartsZeroed()
        {
            Assert.Equal(0.0, Run(
                "var p: Point;\n" +
                "out.Setting = p.x + p.y;",
                "struct Point { x: num; y: num; }\n"));
        }

        [Fact]
        public void AFieldMayBeBool()
        {
            Assert.Equal(1.0, Run(
                "var s: State;\n" +
                "s.active = true;\n" +
                "out.Setting = s.active;",
                "struct State { active: bool; level: num; }\n"));
        }

        [Fact]
        public void AStructNestsInsideAnother()
        {
            Assert.Equal(25.0, Run(
                "var t: Tank;\n" +
                "t.reading.value = 25;\n" +
                "out.Setting = t.reading.value;",
                "struct Sample { at: num; value: num; }\n" +
                "struct Tank { id: num; reading: Sample; }\n"));
        }

        [Fact]
        public void AStructMayNameOneDeclaredBelowIt()
        {
            Assert.Equal(3.0, Run(
                "var t: Tank;\n" +
                "t.reading.value = 3;\n" +
                "out.Setting = t.reading.value;",
                "struct Tank { reading: Sample; }\n" +
                "struct Sample { value: num; }\n"));
        }

        [Fact]
        public void AStructMayHoldAnArray()
        {
            Assert.Equal(30.0, Run(
                "var w: Window;\n" +
                "w.samples[0] = 10;\n" +
                "w.samples[2] = 20;\n" +
                "out.Setting = w.samples[0] + w.samples[1] + w.samples[2];",
                "struct Window { count: num; samples: num[3]; }\n"));
        }

        [Fact]
        public void AnArrayMayHoldStructs()
        {
            Assert.Equal(12.0, Run(
                "var ps: Point[3];\n" +
                "ps[0].x = 1;\n" +
                "ps[2].x = 11;\n" +
                "ps[2].y = 0;\n" +
                "out.Setting = ps[0].x + ps[2].x;",
                "struct Point { x: num; y: num; }\n"));
        }

        [Fact]
        public void ATwoDimensionalArrayReadsLeftToRight()
        {
            // 'num[2][3]' is 2 groups of 3, as in C: m[1][2] is the last cell.
            Assert.Equal(5.0, Run(
                "var m: num[2][3];\n" +
                "m[0][0] = 1;\n" +
                "m[1][2] = 4;\n" +
                "out.Setting = m[0][0] + m[1][2];"));
        }

        [Fact]
        public void ANestedArrayLiteralFillsEveryRow()
        {
            Assert.Equal(10.0, Run(
                "var m: num[2][2] = [[1, 2], [3, 4]];\n" +
                "out.Setting = m[0][0] + m[0][1] + m[1][0] + m[1][1];"));
        }

        // ------------------------------------------------------------------
        //  Lifetime
        // ------------------------------------------------------------------

        [Fact]
        public void AGlobalArrayKeepsItsValuesAcrossCalls()
        {
            Assert.Equal(3.0, Run(
                "bump();\n" +
                "bump();\n" +
                "bump();\n" +
                "out.Setting = counters[0];",
                "var counters: num[2];\n" +
                "fn bump() { counters[0] += 1; }\n"));
        }

        [Fact]
        public void AnArrayIsPassedByReference()
        {
            // The address travels, not the contents: what the function writes is
            // what the caller reads back.
            Assert.Equal(42.0, Run(
                "var a: num[3];\n" +
                "fill(a);\n" +
                "out.Setting = a[1];",
                "fn fill(xs: num[3]) { xs[1] = 42; }\n"));
        }

        [Fact]
        public void AStructIsPassedByReference()
        {
            Assert.Equal(9.0, Run(
                "var p: Point;\n" +
                "move(p);\n" +
                "out.Setting = p.x;",
                "struct Point { x: num; y: num; }\n" +
                "fn move(target: Point) { target.x = 9; }\n"));
        }

        [Fact]
        public void AnAliasSharesTheSameCells()
        {
            // 'var b = a' does not copy: the declaration binds a second name to the
            // same storage, which is the same thing a parameter does.
            Assert.Equal(5.0, Run(
                "var a: num[3];\n" +
                "var b = a;\n" +
                "b[2] = 5;\n" +
                "out.Setting = a[2];"));
        }

        [Fact]
        public void ARowOfAMatrixCanBeNamed()
        {
            // 'm[0]' is a num[3], so it can be bound to a name and passed around
            // like any other array - still the same cells.
            Assert.Equal(8.0, Run(
                "var m: num[2][3];\n" +
                "var row = m[1];\n" +
                "row[2] = 8;\n" +
                "out.Setting = m[1][2];"));
        }

        [Fact]
        public void LenWorksAsARangeLimit()
        {
            Assert.Equal(10.0, Run(
                "var a: num[5];\n" +
                "for i in 0..len(a) { a[i] = i; }\n" +
                "var total = 0;\n" +
                "for i in 0..len(a) { total += a[i]; }\n" +
                "out.Setting = total;"));
        }

        [Fact]
        public void AnArrayInsideAStructCanBePassedOn()
        {
            Assert.Equal(21.0, Run(
                "var w: Window;\n" +
                "fill(w.samples);\n" +
                "out.Setting = w.samples[0] + w.samples[1] + w.samples[2];",
                "struct Window { count: num; samples: num[3]; }\n" +
                "fn fill(xs: num[3]) {\n" +
                "    for i in 0..len(xs) { xs[i] = 7; }\n" +
                "}\n"));
        }

        [Fact]
        public void AFieldMayHoldAHash()
        {
            Assert.Equal(0.0, Run(
                "var t: Target;\n" +
                "t.prefab = #\"StructureWallLight\";\n" +
                "out.Setting = t.prefab - #\"StructureWallLight\";",
                "struct Target { prefab: num; label: str; }\n"));
        }

        [Fact]
        public void EachCallGetsItsOwnArray()
        {
            // Recursion is where a frame-shared heap region would show up first:
            // the inner call must not overwrite the outer one's cells.
            Assert.Equal(2.0, Run(
                "out.Setting = depth(2);",
                "fn depth(n: num) -> num {\n" +
                "    var mine: num[2];\n" +
                "    mine[0] = n;\n" +
                "    if n > 0 { depth(n - 1); }\n" +
                "    return mine[0];\n" +
                "}\n"));
        }

        [Fact]
        public void TheHeapIsGivenBackOnTheReturn()
        {
            var program = TestHost.CompileOk(
                "fn use_it() { var a: num[64]; a[0] = 1; }\n" +
                "fn main() {\n" +
                "    for i in 0..500 { use_it(); }\n" +
                "}\n");

            var vm = new IZVm(program, new MemoryDeviceHost());
            var state = TestHost.RunToCompletion(vm, maxTicks: 100000);

            // 500 calls of 64 cells each is well past the heap; it only fits because
            // every return unwinds what its call reserved.
            Assert.Equal(ExecutionResult.Halted, state);
            Assert.Equal(0, vm.HeapUsed);
        }

        [Fact]
        public void ADeclarationInsideALoopStartsZeroedEveryTime()
        {
            Assert.Equal(1.0, Run(
                "var last = 0;\n" +
                "for i in 0..3 {\n" +
                "    var scratch: num[2];\n" +
                "    last = scratch[0];\n" +
                "    scratch[0] = 100;\n" +
                "}\n" +
                "out.Setting = last + 1;"));
        }

        [Fact]
        public void TwoSiblingBlocksShareTheSameCells()
        {
            // Nothing observable, but it is what keeps the frame small: the second
            // block reuses what the first one gave back.
            var program = TestHost.CompileOk(
                "fn main() {\n" +
                "    { var a: num[8]; a[0] = 1; }\n" +
                "    { var b: num[8]; b[0] = 2; }\n" +
                "}\n");

            var main = program.Functions.First(f => f.Name == "main");
            Assert.Equal(8, main.HeapSize);
        }

        [Fact]
        public void ARingBufferAveragesTheLastReadings()
        {
            // What samples/tank-window.iz does, in miniature: a struct holding an
            // array, walked as a ring, with the total carried along.
            Assert.Equal(9.0, Run(
                "push(3);\n" +
                "push(6);\n" +
                "push(9);\n" +
                "out.Setting = push(12);",
                "struct Gauge { total: num; cursor: num; filled: num; samples: num[3]; }\n" +
                "var g: Gauge;\n" +
                "fn push(value: num) -> num {\n" +
                "    g.total = g.total - g.samples[g.cursor];\n" +
                "    g.samples[g.cursor] = value;\n" +
                "    g.total = g.total + value;\n" +
                "    g.cursor = (g.cursor + 1) % len(g.samples);\n" +
                "    if g.filled < len(g.samples) { g.filled = g.filled + 1; }\n" +
                "    return g.total / g.filled;\n" +
                "}\n"));
        }

        // ------------------------------------------------------------------
        //  Runtime errors
        // ------------------------------------------------------------------

        [Fact]
        public void AnIndexPastTheEndIsARuntimeError()
        {
            var program = TestHost.CompileOk(
                "fn main() {\n" +
                "    var a: num[4];\n" +
                "    var i = 4;\n" +
                "    a[i] = 1;\n" +
                "}\n");

            var vm = new IZVm(program, new MemoryDeviceHost());
            var state = TestHost.RunToCompletion(vm);

            Assert.Equal(ExecutionResult.Error, state);
            Assert.Equal(RuntimeErrorKind.IndexOutOfRange, vm.Error!.Kind);
            Assert.Equal(4, vm.Error.Line);
        }

        [Fact]
        public void ANegativeIndexIsARuntimeError()
        {
            var program = TestHost.CompileOk(
                "fn main() { var a: num[4]; var i = 0 - 1; var x = a[i]; }\n");

            var vm = new IZVm(program, new MemoryDeviceHost());

            Assert.Equal(ExecutionResult.Error, TestHost.RunToCompletion(vm));
            Assert.Equal(RuntimeErrorKind.IndexOutOfRange, vm.Error!.Kind);
        }

        [Fact]
        public void RunningOutOfHeapIsARuntimeError()
        {
            var program = TestHost.CompileOk(
                "fn deeper(n: num) {\n" +
                "    var block: num[512];\n" +
                "    block[0] = n;\n" +
                "    if n > 0 { deeper(n - 1); }\n" +
                "}\n" +
                "fn main() { deeper(20); }\n");

            var vm = new IZVm(program, new MemoryDeviceHost());

            Assert.Equal(ExecutionResult.Error, TestHost.RunToCompletion(vm));
            Assert.Equal(RuntimeErrorKind.HeapOverflow, vm.Error!.Kind);
        }

        // ------------------------------------------------------------------
        //  Compile errors
        // ------------------------------------------------------------------

        [Fact]
        public void AConstantIndexPastTheEndIsCaughtAtCompileTime()
        {
            TestHost.CompileError(
                "fn main() { var a: num[4]; a[4] = 1; }\n",
                IZErrorCode.IndexOutOfRange);
        }

        [Fact]
        public void AnUnknownFieldSuggestsTheRightOne()
        {
            var error = TestHost.CompileError(
                "struct Point { x: num; y: num; }\n" +
                "fn main() { var p: Point; p.z = 1; }\n",
                IZErrorCode.UnknownField);

            Assert.Contains("did you mean 'x'", error.Message);
        }

        [Fact]
        public void AnArrayCannotBeAssignedAsAWhole()
        {
            TestHost.CompileError(
                "fn main() {\n" +
                "    var a: num[2];\n" +
                "    var b: num[2];\n" +
                "    a = b;\n" +
                "}\n",
                IZErrorCode.InvalidAssignmentTarget);
        }

        [Fact]
        public void AStructFieldCannotBeAssignedAsAWhole()
        {
            TestHost.CompileError(
                "struct Sample { value: num; }\n" +
                "struct Tank { a: Sample; b: Sample; }\n" +
                "fn main() { var t: Tank; t.a = t.b; }\n",
                IZErrorCode.InvalidAssignmentTarget);
        }

        [Fact]
        public void AFunctionCannotReturnAnArray()
        {
            TestHost.CompileError(
                "fn make() -> num[4] { var a: num[4]; return a; }\n" +
                "fn main() { var b = make(); }\n",
                IZErrorCode.TypeMismatch);
        }

        [Fact]
        public void AStructCannotContainItself()
        {
            TestHost.CompileError(
                "struct Node { value: num; next: Node; }\n" +
                "fn main() { var n: Node; n.value = 1; }\n",
                IZErrorCode.TypeMismatch);
        }

        [Fact]
        public void AConstCannotBeAnArray()
        {
            TestHost.CompileError(
                "const A: num[4] = 0;\n" +
                "fn main() { }\n",
                IZErrorCode.TypeMismatch);
        }

        [Fact]
        public void ALiteralOfTheWrongLengthIsRejected()
        {
            TestHost.CompileError(
                "fn main() { var a: num[3] = [1, 2]; }\n",
                IZErrorCode.TypeMismatch);
        }

        [Fact]
        public void ArraysOfDifferentLengthsAreDifferentTypes()
        {
            TestHost.CompileError(
                "fn take(xs: num[3]) { xs[0] = 1; }\n" +
                "fn main() { var a: num[4]; take(a); }\n",
                IZErrorCode.TypeMismatch);
        }

        [Fact]
        public void TwoStructsWithTheSameShapeAreStillDifferentTypes()
        {
            TestHost.CompileError(
                "struct A { x: num; }\n" +
                "struct B { x: num; }\n" +
                "fn take(value: A) { value.x = 1; }\n" +
                "fn main() { var b: B; take(b); }\n",
                IZErrorCode.TypeMismatch);
        }

        [Fact]
        public void AnArrayIsNotANumber()
        {
            TestHost.CompileError(
                "fn main() { var a: num[2]; var x = a + 1; }\n",
                IZErrorCode.TypeMismatch);
        }

        [Fact]
        public void StructsCannotBeCompared()
        {
            TestHost.CompileError(
                "struct Point { x: num; }\n" +
                "fn main() { var a: Point; var b: Point; if a == b { } }\n",
                IZErrorCode.TypeMismatch);
        }

        [Fact]
        public void OnlyAnArrayCanBeIndexed()
        {
            TestHost.CompileError(
                "fn main() { var x = 1; var y = x[0]; }\n",
                IZErrorCode.TypeMismatch);
        }

        [Fact]
        public void AnArrayCannotHoldDevices()
        {
            TestHost.CompileError(
                "fn main() { var a: dev[2]; }\n",
                IZErrorCode.TypeMismatch);
        }

        [Fact]
        public void TheLengthHasToBeKnownAtCompileTime()
        {
            TestHost.CompileError(
                "fn main() { var n = 4; var a: num[n]; }\n",
                IZErrorCode.ConstExpressionRequired);
        }

        [Fact]
        public void ALengthOfZeroIsRejected()
        {
            TestHost.CompileError(
                "fn main() { var a: num[0]; }\n",
                IZErrorCode.InvalidArrayLength);
        }

        [Fact]
        public void AStructIsDeclaredOutsideAnyFunction()
        {
            TestHost.CompileError(
                "fn main() { struct Point { x: num; } }\n",
                IZErrorCode.ExpectedDeclaration);
        }

        [Fact]
        public void AnUnknownTypeNameIsReported()
        {
            TestHost.CompileError(
                "fn main() { var p: Nowhere; }\n",
                IZErrorCode.UndefinedName);
        }

        // ------------------------------------------------------------------
        //  The editor
        // ------------------------------------------------------------------

        /// <summary>The '|' marks the caret and is removed before completing.</summary>
        private static IZLang.Editor.CompletionEngine.CompletionResult Complete(string marked)
        {
            int caret = marked.IndexOf('|');
            Assert.True(caret >= 0, "the test source has to mark the caret with '|'");
            return IZLang.Editor.CompletionEngine.GetCompletions(marked.Remove(caret, 1), caret);
        }

        private static string[] Labels(IZLang.Editor.CompletionEngine.CompletionResult result) =>
            result.Items.Select(i => i.Label).ToArray();

        [Fact]
        public void AfterAStructVariableItSuggestsTheFields()
        {
            var result = Complete(
                "struct Point { x: num; y: num; }\n" +
                "fn main() { var p: Point; p.| }\n");

            Assert.Equal(IZLang.Editor.CompletionContext.StructField, result.Context);
            Assert.Equal(new[] { "x", "y" }, Labels(result));
        }

        [Fact]
        public void ItFollowsTheFieldsIntoANestedStruct()
        {
            var result = Complete(
                "struct Sample { at: num; value: num; }\n" +
                "struct Tank { id: num; reading: Sample; }\n" +
                "fn main() { var t: Tank; t.reading.| }\n");

            Assert.Equal(new[] { "at", "value" }, Labels(result));
        }

        [Fact]
        public void ItFollowsAnIndexIntoAnArrayOfStructs()
        {
            var result = Complete(
                "struct Point { x: num; y: num; }\n" +
                "fn main() { var ps: Point[4]; ps[0].| }\n");

            Assert.Equal(new[] { "x", "y" }, Labels(result));
        }

        [Fact]
        public void AnArrayItselfHasNoFields()
        {
            // 'ps.' without an index is not a struct; the list falls back to device
            // properties rather than pretending the array has fields.
            var result = Complete(
                "struct Point { x: num; }\n" +
                "fn main() { var ps: Point[4]; ps.| }\n");

            Assert.NotEqual(IZLang.Editor.CompletionContext.StructField, result.Context);
        }

        [Fact]
        public void ADeviceStillWinsAfterTheDot()
        {
            var result = Complete(
                "struct Point { x: num; }\n" +
                "device pump = d0;\n" +
                "fn main() { pump.| }\n");

            Assert.Equal(IZLang.Editor.CompletionContext.DeviceProperty, result.Context);
        }

        [Fact]
        public void HoverShowsTheDeclaredType()
        {
            const string source = "fn main() { var samples: num[8]; samples[0] = 1; }\n";
            var hover = IZLang.Editor.HoverEngine.GetHover(source, source.IndexOf("samples[0]"));

            Assert.Equal("var samples: num[8]", hover.Title);
        }

        [Fact]
        public void HoverOnAStructNameSaysSo()
        {
            const string source = "struct Point { x: num; }\nfn main() { var p: Point; p.x = 1; }\n";
            var hover = IZLang.Editor.HoverEngine.GetHover(source, source.IndexOf("Point;"));

            Assert.Equal("struct Point", hover.Title);
        }

        [Fact]
        public void AStructNameIsOfferedAmongTheDeclarations()
        {
            var result = Complete(
                "struct Point { x: num; }\n" +
                "fn main() { var p: Poi| }\n");

            Assert.Contains("Point", Labels(result));
        }

        [Fact]
        public void AnUnusedStructIsWarnedAbout()
        {
            var result = IZLang.IZCompiler.Compile(
                "struct Point { x: num; }\n" +
                "fn main() { }\n");

            Assert.True(result.Success);
            var warning = result.Diagnostics.Single(d => !d.IsError);
            Assert.Equal(IZErrorCode.UnusedVariable, warning.Code);
            Assert.Contains("struct 'Point'", warning.Message);
        }
    }
}
