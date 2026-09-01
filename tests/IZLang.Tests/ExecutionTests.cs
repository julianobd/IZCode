using System.Linq;
using IZLang.Vm;
using Xunit;

namespace IZLang.Tests
{
    /// <summary>Really compiles and runs: arithmetic, control flow, functions, devices.</summary>
    public class ExecutionTests
    {
        // ------------------------------------------------------------------
        //  Arithmetic and precedence
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("1 + 2", 3.0)]
        [InlineData("10 - 4", 6.0)]
        [InlineData("6 * 7", 42.0)]
        [InlineData("10 / 4", 2.5)]
        [InlineData("1 + 2 * 3", 7.0)]
        [InlineData("(1 + 2) * 3", 9.0)]
        [InlineData("10 - 3 - 2", 5.0)]          // left associative
        [InlineData("100 / 10 / 2", 5.0)]
        [InlineData("-3 + 5", 2.0)]
        [InlineData("2 * -3", -6.0)]
        public void Arithmetic(string expression, double expected) =>
            Assert.Equal(expected, TestHost.Eval(expression), 10);

        [Theory]
        [InlineData("7 % 3", 1.0)]
        [InlineData("-7 % 3", 2.0)]              // modulo takes the divisor sign, as in IC10
        [InlineData("7 % -3", -2.0)]
        public void ModuloFollowsTheDivisorSign(string expression, double expected) =>
            Assert.Equal(expected, TestHost.Eval(expression), 10);

        [Theory]
        [InlineData("0xF0 & 0x3C", 0x30)]
        [InlineData("0xF0 | 0x0F", 0xFF)]
        [InlineData("0xFF ^ 0x0F", 0xF0)]
        [InlineData("1 << 8", 256)]
        [InlineData("1024 >> 4", 64)]
        [InlineData("~0", -1)]
        public void BitwiseOperations(string expression, double expected) =>
            Assert.Equal(expected, TestHost.Eval(expression), 10);

        [Fact]
        public void BitwisePrecedenceSitsBelowArithmetic()
        {
            // 1 | 2 + 4  ==  1 | 6  ==  7   (and not (1|2)+4 == 7 by coincidence)
            Assert.Equal(7.0, TestHost.Eval("1 | 2 + 4"), 10);
            // 1 << 2 + 1  ==  1 << 3  ==  8
            Assert.Equal(8.0, TestHost.Eval("1 << 2 + 1"), 10);
        }

        [Fact]
        public void DivisionByZeroFollowsIeee()
        {
            Assert.True(double.IsPositiveInfinity(TestHost.Eval("1.0 / 0.0")));
            Assert.True(double.IsNaN(TestHost.Eval("0.0 / 0.0")));
        }

        // ------------------------------------------------------------------
        //  Booleans and comparisons
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("1 < 2", 1.0)]
        [InlineData("2 < 1", 0.0)]
        [InlineData("2 <= 2", 1.0)]
        [InlineData("3 > 2", 1.0)]
        [InlineData("3 >= 4", 0.0)]
        [InlineData("2 == 2", 1.0)]
        [InlineData("2 != 2", 0.0)]
        [InlineData("true && false", 0.0)]
        [InlineData("true || false", 1.0)]
        [InlineData("!true", 0.0)]
        [InlineData("!(1 > 2)", 1.0)]
        [InlineData("1 < 2 && 3 < 4", 1.0)]
        public void Comparisons(string expression, double expected) =>
            Assert.Equal(expected, TestHost.Eval(expression), 10);

        [Fact]
        public void TheAndOperatorShortCircuits()
        {
            // If '&&' evaluated the right side, d1 would be written to.
            var host = TestHost.Execute(
                "device flag = d1;\n" +
                "device out = d0;\n" +
                "fn mark() -> bool { flag.Setting = 1; return true; }\n" +
                "fn main() {\n" +
                "    var r = false && mark();\n" +
                "    out.Setting = r;\n" +
                "}\n",
                h => { h.Connect(0); h.Connect(1); });

            Assert.DoesNotContain(host.Writes, w => w.Pin == 1);
            Assert.Equal(0.0, host.Writes.Last().Value);
        }

        [Fact]
        public void TheOrOperatorShortCircuits()
        {
            var host = TestHost.Execute(
                "device flag = d1;\n" +
                "device out = d0;\n" +
                "fn mark() -> bool { flag.Setting = 1; return false; }\n" +
                "fn main() {\n" +
                "    var r = true || mark();\n" +
                "    out.Setting = r;\n" +
                "}\n",
                h => { h.Connect(0); h.Connect(1); });

            Assert.DoesNotContain(host.Writes, w => w.Pin == 1);
            Assert.Equal(1.0, host.Writes.Last().Value);
        }

        // ------------------------------------------------------------------
        //  Variables and constants
        // ------------------------------------------------------------------

        [Fact]
        public void GlobalsDeclaredAtTheTop() =>
            Assert.Equal(30.0, TestHost.Eval("a + b", "var a = 10;\nvar b = 20;"), 10);

        [Fact]
        public void AConstantIsFoldedAtCompileTime()
        {
            var program = TestHost.CompileOk(
                "const K = 2 * 3 + 4;\n" +
                "device out = d0;\n" +
                "fn main() { out.Setting = K; }\n");

            // 10 has to be in the pool as a single, already computed value.
            Assert.Contains(10.0, program.Constants);
            // And no multiplication may be left in the code.
            Assert.DoesNotContain(program.Code, i => i.Op == OpCode.Multiply);
        }

        [Fact]
        public void AGlobalPersistsAcrossFunctionCalls()
        {
            var host = TestHost.Execute(
                "var counter = 0;\n" +
                "device out = d0;\n" +
                "fn increment() { counter += 1; }\n" +
                "fn main() {\n" +
                "    increment();\n" +
                "    increment();\n" +
                "    increment();\n" +
                "    out.Setting = counter;\n" +
                "}\n",
                h => h.Connect(0));

            Assert.Equal(3.0, host.Writes.Last().Value);
        }

        [Fact]
        public void AnInnerScopeShadowsTheOuterOne()
        {
            var host = TestHost.Execute(
                "device out = d0;\n" +
                "fn main() {\n" +
                "    var x = 1;\n" +
                "    {\n" +
                "        var x = 2;\n" +
                "        out.Setting = x;\n" +
                "    }\n" +
                "    out.Setting = x;\n" +
                "}\n",
                h => h.Connect(0));

            Assert.Equal(2.0, host.Writes[0].Value);
            Assert.Equal(1.0, host.Writes[1].Value);
        }

        [Theory]
        [InlineData("+=", 7.0)]
        [InlineData("-=", 3.0)]
        [InlineData("*=", 10.0)]
        [InlineData("/=", 2.5)]
        public void CompoundAssignment(string op, double expected)
        {
            var host = TestHost.Execute(
                "device out = d0;\n" +
                "fn main() {\n" +
                "    var x = 5;\n" +
                "    x " + op + " 2;\n" +
                "    out.Setting = x;\n" +
                "}\n",
                h => h.Connect(0));

            Assert.Equal(expected, host.Writes.Last().Value, 10);
        }

        // ------------------------------------------------------------------
        //  Control flow
        // ------------------------------------------------------------------

        [Fact]
        public void ChainedIfElse()
        {
            const string template =
                "device sensor = d1;\n" +
                "device out = d0;\n" +
                "fn main() {\n" +
                "    var p = sensor.Pressure;\n" +
                "    if p > 100 { out.Setting = 3; }\n" +
                "    else if p > 50 { out.Setting = 2; }\n" +
                "    else { out.Setting = 1; }\n" +
                "}\n";

            Assert.Equal(3.0, RunWithPressure(template, 150));
            Assert.Equal(2.0, RunWithPressure(template, 75));
            Assert.Equal(1.0, RunWithPressure(template, 10));
        }

        private static double RunWithPressure(string source, double pressure)
        {
            var host = TestHost.Execute(source, h =>
            {
                h.Connect(0);
                h.Set(1, 5, pressure);          // 5 == LogicType.Pressure
            });
            return host.Writes.Last().Value;
        }

        [Fact]
        public void WhileSums()
        {
            var host = TestHost.Execute(
                "device out = d0;\n" +
                "fn main() {\n" +
                "    var i = 0;\n" +
                "    var sum = 0;\n" +
                "    while i < 10 {\n" +
                "        sum += i;\n" +
                "        i += 1;\n" +
                "    }\n" +
                "    out.Setting = sum;\n" +
                "}\n",
                h => h.Connect(0));

            Assert.Equal(45.0, host.Writes.Last().Value);
        }

        [Fact]
        public void ExclusiveAndInclusiveFor()
        {
            Assert.Equal(45.0, SumRange("0..10"));      // 0..9
            Assert.Equal(55.0, SumRange("0..=10"));     // 0..10
        }

        private static double SumRange(string range)
        {
            var host = TestHost.Execute(
                "device out = d0;\n" +
                "fn main() {\n" +
                "    var sum = 0;\n" +
                "    for i in " + range + " { sum += i; }\n" +
                "    out.Setting = sum;\n" +
                "}\n",
                h => h.Connect(0));
            return host.Writes.Last().Value;
        }

        [Fact]
        public void ForEvaluatesTheLimitExactlyOnce()
        {
            // If the limit were re-evaluated on every turn, the loop would never end.
            var host = TestHost.Execute(
                "device out = d0;\n" +
                "var limit = 3;\n" +
                "fn main() {\n" +
                "    var turns = 0;\n" +
                "    for i in 0..limit {\n" +
                "        limit += 1;\n" +
                "        turns += 1;\n" +
                "    }\n" +
                "    out.Setting = turns;\n" +
                "}\n",
                h => h.Connect(0));

            Assert.Equal(3.0, host.Writes.Last().Value);
        }

        [Fact]
        public void BreakLeavesTheLoop()
        {
            var host = TestHost.Execute(
                "device out = d0;\n" +
                "fn main() {\n" +
                "    var i = 0;\n" +
                "    loop {\n" +
                "        i += 1;\n" +
                "        if i >= 5 { break; }\n" +
                "    }\n" +
                "    out.Setting = i;\n" +
                "}\n",
                h => h.Connect(0));

            Assert.Equal(5.0, host.Writes.Last().Value);
        }

        [Fact]
        public void ContinueInAForStillIncrements()
        {
            // Sums only the even numbers in 0..9. If 'continue' skipped the increment, it would hang.
            var host = TestHost.Execute(
                "device out = d0;\n" +
                "fn main() {\n" +
                "    var sum = 0;\n" +
                "    for i in 0..10 {\n" +
                "        if i % 2 != 0 { continue; }\n" +
                "        sum += i;\n" +
                "    }\n" +
                "    out.Setting = sum;\n" +
                "}\n",
                h => h.Connect(0));

            Assert.Equal(20.0, host.Writes.Last().Value);
        }

        [Fact]
        public void ContinueInAWhileGoesBackToTheCondition()
        {
            var host = TestHost.Execute(
                "device out = d0;\n" +
                "fn main() {\n" +
                "    var i = 0;\n" +
                "    var sum = 0;\n" +
                "    while i < 10 {\n" +
                "        i += 1;\n" +
                "        if i % 2 != 0 { continue; }\n" +
                "        sum += i;\n" +
                "    }\n" +
                "    out.Setting = sum;\n" +
                "}\n",
                h => h.Connect(0));

            Assert.Equal(30.0, host.Writes.Last().Value);     // 2+4+6+8+10
        }

        [Fact]
        public void BreakInANestedLoopOnlyLeavesTheInnerOne()
        {
            var host = TestHost.Execute(
                "device out = d0;\n" +
                "fn main() {\n" +
                "    var total = 0;\n" +
                "    for i in 0..3 {\n" +
                "        for j in 0..10 {\n" +
                "            if j >= 2 { break; }\n" +
                "            total += 1;\n" +
                "        }\n" +
                "    }\n" +
                "    out.Setting = total;\n" +
                "}\n",
                h => h.Connect(0));

            Assert.Equal(6.0, host.Writes.Last().Value);      // 3 turns x 2
        }

        // ------------------------------------------------------------------
        //  The ternary operator
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("true ? 1 : 2", 1.0)]
        [InlineData("false ? 1 : 2", 2.0)]
        [InlineData("5 > 3 ? 10 : 20", 10.0)]
        [InlineData("5 < 3 ? 10 : 20", 20.0)]
        public void TheTernaryPicksTheSideTheConditionAsksFor(string expression, double expected) =>
            Assert.Equal(expected, TestHost.Eval(expression), 10);

        [Fact]
        public void TheTernaryBindsWeakerThanEveryOtherOperator()
        {
            // 1 + 1 == 2 ? 3 + 4 : 5 + 6  reads as  ((1+1) == 2) ? (3+4) : (5+6)
            Assert.Equal(7.0, TestHost.Eval("1 + 1 == 2 ? 3 + 4 : 5 + 6"), 10);
            Assert.Equal(11.0, TestHost.Eval("1 + 1 == 3 ? 3 + 4 : 5 + 6"), 10);
        }

        [Theory]
        [InlineData(1.0, 10.0)]
        [InlineData(2.0, 20.0)]
        [InlineData(3.0, 30.0)]
        public void TheTernaryIsRightAssociative(double input, double expected)
        {
            // Without parentheses this is  x == 1 ? 10 : (x == 2 ? 20 : 30).
            Assert.Equal(expected,
                TestHost.Eval("x == 1 ? 10 : x == 2 ? 20 : 30", "var x = " + input + ";"), 10);
        }

        [Fact]
        public void TheTernaryOnlyEvaluatesTheBranchItTakes()
        {
            // If the branch not taken ran, d1 would be written to.
            var host = TestHost.Execute(
                "device flag = d1;\n" +
                "device out = d0;\n" +
                "fn mark() -> num { flag.Setting = 1; return 99; }\n" +
                "fn main() {\n" +
                "    out.Setting = true ? 7 : mark();\n" +
                "}\n",
                h => { h.Connect(0); h.Connect(1); });

            Assert.DoesNotContain(host.Writes, w => w.Pin == 1);
            Assert.Equal(7.0, host.Writes.Last().Value);
        }

        [Fact]
        public void TheTernaryOnlyEvaluatesTheBranchItTakesOnTheElseSide()
        {
            var host = TestHost.Execute(
                "device flag = d1;\n" +
                "device out = d0;\n" +
                "fn mark() -> num { flag.Setting = 1; return 99; }\n" +
                "fn main() {\n" +
                "    out.Setting = false ? mark() : 7;\n" +
                "}\n",
                h => { h.Connect(0); h.Connect(1); });

            Assert.DoesNotContain(host.Writes, w => w.Pin == 1);
            Assert.Equal(7.0, host.Writes.Last().Value);
        }

        [Fact]
        public void TheTernaryIsAnExpressionAndComposes()
        {
            Assert.Equal(25.0, TestHost.Eval("(hot ? 10 : 20) + 15", "var hot = true;"), 10);
            Assert.Equal(6.0, TestHost.Eval("abs(hot ? -6 : 3)", "var hot = true;"), 10);
        }

        [Fact]
        public void TheTernaryGivesBackABool()
        {
            var host = TestHost.Execute(
                "device out = d0;\n" +
                "fn main() {\n" +
                "    var open = true;\n" +
                "    out.On = open ? false : true;\n" +
                "}\n",
                h => h.Connect(0));

            Assert.Equal(0.0, host.Writes.Last().Value);
        }

        [Fact]
        public void TheTernaryMixingBoolAndNumWidensToNum() =>
            Assert.Equal(1.0, TestHost.Eval("hot ? true : 5", "var hot = true;"), 10);

        [Fact]
        public void ATernaryWritesToADeviceProperty()
        {
            var host = TestHost.Execute(
                "device pump = d0;\n" +
                "fn main() {\n" +
                "    var full = true;\n" +
                "    pump.Setting = full ? 0 : 100;\n" +
                "}\n",
                h => h.Connect(0));

            Assert.Equal(0.0, host.Writes.Last().Value);
        }

        // ------------------------------------------------------------------
        //  Functions
        // ------------------------------------------------------------------

        [Fact]
        public void FunctionWithAReturnValue()
        {
            var host = TestHost.Execute(
                "device out = d0;\n" +
                "fn twice(x: num) -> num { return x * 2; }\n" +
                "fn main() { out.Setting = twice(21); }\n",
                h => h.Connect(0));

            Assert.Equal(42.0, host.Writes.Last().Value);
        }

        [Fact]
        public void AFunctionCanCallAnotherDeclaredLater()
        {
            var host = TestHost.Execute(
                "device out = d0;\n" +
                "fn main() { out.Setting = a(10); }\n" +
                "fn a(x: num) -> num { return b(x) + 1; }\n" +
                "fn b(x: num) -> num { return x * 2; }\n",
                h => h.Connect(0));

            Assert.Equal(21.0, host.Writes.Last().Value);
        }

        [Fact]
        public void FactorialRecursion()
        {
            var host = TestHost.Execute(
                "device out = d0;\n" +
                "fn fact(n: num) -> num {\n" +
                "    if n <= 1 { return 1; }\n" +
                "    return n * fact(n - 1);\n" +
                "}\n" +
                "fn main() { out.Setting = fact(10); }\n",
                h => h.Connect(0));

            Assert.Equal(3628800.0, host.Writes.Last().Value);
        }

        [Fact]
        public void FibonacciRecursion()
        {
            var host = TestHost.Execute(
                "device out = d0;\n" +
                "fn fib(n: num) -> num {\n" +
                "    if n < 2 { return n; }\n" +
                "    return fib(n - 1) + fib(n - 2);\n" +
                "}\n" +
                "fn main() { out.Setting = fib(15); }\n",
                h => h.Connect(0),
                maxTicks: 100000);

            Assert.Equal(610.0, host.Writes.Last().Value);
        }

        [Fact]
        public void ParametersDoNotLeakBetweenCalls()
        {
            var host = TestHost.Execute(
                "device out = d0;\n" +
                "fn f(a: num, b: num) -> num {\n" +
                "    var local = a * 100 + b;\n" +
                "    return local;\n" +
                "}\n" +
                "fn main() {\n" +
                "    var first = f(1, 2);\n" +
                "    var second = f(3, 4);\n" +
                "    out.Setting = first * 10000 + second;\n" +
                "}\n",
                h => h.Connect(0));

            Assert.Equal(102.0 * 10000 + 304.0, host.Writes.Last().Value);
        }

        [Fact]
        public void AFunctionWithoutAReturnValueCanBeCalledAsAStatement()
        {
            var host = TestHost.Execute(
                "device out = d0;\n" +
                "fn turnOn() { out.Setting = 1; }\n" +
                "fn main() { turnOn(); }\n",
                h => h.Connect(0));

            Assert.Equal(1.0, host.Writes.Last().Value);
        }

        [Fact]
        public void ADiscardedReturnValueDoesNotLeakOnTheStack()
        {
            // Calling a value-returning function as a statement, many times over, must
            // not pile garbage onto the operand stack.
            var program = TestHost.CompileOk(
                "device out = d0;\n" +
                "fn f() -> num { return 1; }\n" +
                "fn main() {\n" +
                "    for i in 0..200 { f(); }\n" +
                "    out.Setting = 9;\n" +
                "}\n");

            var host = new MemoryDeviceHost();
            host.Connect(0);
            var vm = new IZVm(program, host);
            var state = TestHost.RunToCompletion(vm, maxTicks: 10000);

            Assert.Equal(ExecutionResult.Halted, state);
            Assert.Equal(0, vm.StackDepth);
        }

        // ------------------------------------------------------------------
        //  Builtins
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("abs(-5)", 5.0)]
        [InlineData("floor(3.7)", 3.0)]
        [InlineData("ceil(3.2)", 4.0)]
        [InlineData("round(3.5)", 4.0)]
        [InlineData("trunc(-3.7)", -3.0)]
        [InlineData("sqrt(16)", 4.0)]
        [InlineData("min(3, 7)", 3.0)]
        [InlineData("max(3, 7)", 7.0)]
        [InlineData("pow(2, 10)", 1024.0)]
        [InlineData("sign(-42)", -1.0)]
        [InlineData("clamp(15, 0, 10)", 10.0)]
        [InlineData("clamp(-5, 0, 10)", 0.0)]
        [InlineData("clamp(5, 0, 10)", 5.0)]
        public void Builtins(string expression, double expected) =>
            Assert.Equal(expected, TestHost.Eval(expression), 10);

        [Fact]
        public void NestedBuiltin() =>
            Assert.Equal(5.0, TestHost.Eval("max(min(10, 5), abs(-3))"), 10);

        // ------------------------------------------------------------------
        //  Devices
        // ------------------------------------------------------------------

        [Fact]
        public void ReadingAndWritingADevice()
        {
            var host = TestHost.Execute(
                "device sensor = d1;\n" +
                "device pump = d0;\n" +
                "fn main() { pump.Setting = sensor.Pressure * 2; }\n",
                h =>
                {
                    h.Connect(0);
                    h.Set(1, 5, 50.0);
                });

            Assert.Equal(100.0, host.Writes.Last().Value);
        }

        [Fact]
        public void BoolBecomesNumWhenWrittenToADevice()
        {
            var host = TestHost.Execute(
                "device sensor = d1;\n" +
                "device pump = d0;\n" +
                "fn main() { pump.On = sensor.Pressure < 100; }\n",
                h =>
                {
                    h.Connect(0);
                    h.Set(1, 5, 50.0);
                });

            var write = host.Writes.Last();
            Assert.Equal(28, write.LogicType);          // 28 == LogicType.On
            Assert.Equal(1.0, write.Value);
        }

        [Fact]
        public void CompoundAssignmentOnADeviceReadsAndWrites()
        {
            var host = TestHost.Execute(
                "device pump = d0;\n" +
                "fn main() { pump.Setting += 5; }\n",
                h =>
                {
                    h.Connect(0);
                    h.Set(0, 12, 10.0);                 // 12 == LogicType.Setting
                });

            Assert.Equal(15.0, host.Writes.Last().Value);
        }

        [Fact]
        public void ReadingASlot()
        {
            var host = TestHost.Execute(
                "device chute = d1;\n" +
                "device out = d0;\n" +
                "fn main() { out.Setting = chute.slot[2].Quantity; }\n",
                h =>
                {
                    h.Connect(0);
                    h.SetSlot(1, 2, 3, 17.0);           // 3 == LogicSlotType.Quantity
                });

            Assert.Equal(17.0, host.Writes.Last().Value);
        }

        [Fact]
        public void ADisconnectedPinGivesARuntimeError()
        {
            var program = TestHost.CompileOk(
                "device pump = d3;\n" +
                "fn main() { pump.Setting = 1; }\n");

            var vm = new IZVm(program, new MemoryDeviceHost());
            var state = TestHost.RunToCompletion(vm);

            Assert.Equal(ExecutionResult.Error, state);
            Assert.Equal(RuntimeErrorKind.DeviceNotConnected, vm.Error!.Kind);
            Assert.Equal(2, vm.Error.Line);             // the line of 'pump.Setting = 1;'
        }
    }
}
