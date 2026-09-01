using System.Linq;
using IZLang.Binding;
using IZLang.Diagnostics;
using Xunit;

namespace IZLang.Tests
{
    public class DiagnosticTests
    {
        [Fact]
        public void ProgramWithoutMainDoesNotCompile() =>
            TestHost.CompileError("fn outra() { }\n", IZErrorCode.MissingMainFunction);

        [Fact]
        public void UndeclaredName() =>
            TestHost.CompileError(
                "fn main() { var x = doesNotExist; }\n",
                IZErrorCode.UndefinedName);

        [Fact]
        public void RedeclarationInTheSameScope() =>
            TestHost.CompileError(
                "fn main() {\n" +
                "    var x = 1;\n" +
                "    var x = 2;\n" +
                "}\n",
                IZErrorCode.DuplicateName);

        [Fact]
        public void AssigningToAConstant() =>
            TestHost.CompileError(
                "const K = 1;\n" +
                "fn main() { K = 2; }\n",
                IZErrorCode.AssignToConst);

        [Fact]
        public void ConstNeedsAConstantValue() =>
            TestHost.CompileError(
                "device sensor = d0;\n" +
                "const K = sensor.Pressure;\n" +
                "fn main() { }\n",
                IZErrorCode.ConstExpressionRequired);

        [Fact]
        public void NumDoesNotBecomeBoolOnItsOwn()
        {
            var error = TestHost.CompileError(
                "fn main() {\n" +
                "    var x = 1;\n" +
                "    if x { }\n" +
                "}\n",
                IZErrorCode.TypeMismatch);

            // The message has to teach the way out, not just point at the error.
            Assert.Contains("x != 0", error.Message);
        }

        [Fact]
        public void BoolBecomesNum()
        {
            // The allowed direction: it must not raise an error.
            var result = IZCompiler.Compile(
                "device out = d0;\n" +
                "fn main() { var x: num = true; out.Setting = x; }\n");

            Assert.True(result.Success, result.FormatDiagnostics());
        }

        [Fact]
        public void AddingBoolToNumIsNotAnError()
        {
            // bool widens to num, so counting true conditions works.
            Assert.Equal(2.0, TestHost.Eval("(1 < 2) + (3 < 4)"), 10);
        }

        [Fact]
        public void AssignmentIsNotAnExpression() =>
            TestHost.CompileError(
                "fn main() {\n" +
                "    var a = 1;\n" +
                "    var b = 2;\n" +
                "    if a = b { }\n" +
                "}\n",
                IZErrorCode.AssignmentIsNotExpression);

        [Fact]
        public void WrongArgumentCount() =>
            TestHost.CompileError(
                "fn f(a: num, b: num) -> num { return a + b; }\n" +
                "fn main() { var x = f(1); }\n",
                IZErrorCode.WrongArgumentCount);

        [Fact]
        public void BuiltinWithWrongArity() =>
            TestHost.CompileError(
                "fn main() { var x = abs(1, 2); }\n",
                IZErrorCode.WrongArgumentCount);

        [Fact]
        public void CallingSomethingThatIsNotAFunction() =>
            TestHost.CompileError(
                "fn main() { var x = 1; var y = x(); }\n",
                IZErrorCode.UndefinedName);

        [Fact]
        public void BreakOutsideALoop() =>
            TestHost.CompileError("fn main() { break; }\n", IZErrorCode.BreakOutsideLoop);

        [Fact]
        public void ContinueOutsideALoop() =>
            TestHost.CompileError("fn main() { continue; }\n", IZErrorCode.ContinueOutsideLoop);

        [Fact]
        public void PathWithoutReturnInAFunctionThatReturns() =>
            TestHost.CompileError(
                "fn f(x: num) -> num {\n" +
                "    if x > 0 { return 1; }\n" +
                "}\n" +
                "fn main() { var y = f(1); }\n",
                IZErrorCode.MissingReturn);

        [Fact]
        public void ACompleteIfElseSatisfiesTheReturn()
        {
            var result = IZCompiler.Compile(
                "fn f(x: num) -> num {\n" +
                "    if x > 0 { return 1; } else { return -1; }\n" +
                "}\n" +
                "device out = d0;\n" +
                "fn main() { out.Setting = f(1); }\n");

            Assert.True(result.Success, result.FormatDiagnostics());
        }

        [Fact]
        public void ReturnWithValueInAFunctionWithoutReturnType() =>
            TestHost.CompileError(
                "fn f() { return 1; }\n" +
                "fn main() { f(); }\n",
                IZErrorCode.ReturnValueFromVoid);

        [Fact]
        public void IncompatibleReturnType() =>
            TestHost.CompileError(
                "fn f() -> bool { return 1; }\n" +
                "fn main() { var x = f(); }\n",
                IZErrorCode.TypeMismatch);

        [Fact]
        public void InvalidDevicePin() =>
            TestHost.CompileError(
                "device pump = d9;\n" +
                "fn main() { }\n",
                IZErrorCode.InvalidDevicePin);

        [Fact]
        public void UnknownDeviceProperty() =>
            TestHost.CompileError(
                "device pump = d0;\n" +
                "fn main() { var x = pump.NoSuchThing; }\n",
                IZErrorCode.UnknownLogicType);

        [Fact]
        public void ATypoInAPropertySuggestsTheRightName()
        {
            var error = TestHost.CompileError(
                "device sensor = d0;\n" +
                "fn main() { var x = sensor.Presure; }\n",
                IZErrorCode.UnknownLogicType);

            Assert.Contains("Pressure", error.Message);
        }

        [Fact]
        public void DeviceUsedAsAValue() =>
            TestHost.CompileError(
                "device pump = d0;\n" +
                "fn main() { var x = pump; }\n",
                IZErrorCode.NotADevice);

        [Fact]
        public void LooseExpressionAsAStatement() =>
            TestHost.CompileError(
                "fn main() { 1 + 2; }\n",
                IZErrorCode.ExpectedStatement);

        [Fact]
        public void UnclosedParenthesis() =>
            TestHost.CompileError(
                "fn main() { var x = (1 + 2; }\n",
                IZErrorCode.ExpectedToken);

        [Fact]
        public void BracesAreMandatoryInIf() =>
            TestHost.CompileError(
                "fn main() { if true out.Setting = 1; }\n",
                IZErrorCode.ExpectedToken);

        // ------------------------------------------------------------------
        //  Message quality
        // ------------------------------------------------------------------

        [Fact]
        public void DiagnosticPointsAtTheRightLine()
        {
            var result = IZCompiler.Compile(
                "fn main() {\n" +      // 1
                "    var a = 1;\n" +   // 2
                "    var b = 2;\n" +   // 3
                "    var c = zzz;\n" + // 4  <- here
                "}\n");

            Assert.False(result.Success);
            Assert.Equal(4, result.FirstErrorLine);
        }

        [Fact]
        public void FormattingShowsTheLineAndTheCaret()
        {
            var result = IZCompiler.Compile(
                "fn main() {\n" +
                "    var c = zzz;\n" +
                "}\n");

            string text = result.FormatDiagnostics();

            Assert.Contains("var c = zzz;", text);     // the source line
            Assert.Contains("^^^", text);              // the caret under the span
            Assert.Contains("2:13", text);             // line:column
        }

        [Fact]
        public void OneForgottenSemicolonDoesNotBecomeACascadeOfErrors()
        {
            var result = IZCompiler.Compile(
                "fn main() {\n" +
                "    var a = 1\n" +      // missing ';'
                "    var b = 2;\n" +
                "    var c = 3;\n" +
                "    var d = 4;\n" +
                "}\n");

            Assert.False(result.Success);
            int errors = result.Diagnostics.Count(d => d.IsError);
            Assert.True(errors <= 2, "expected at most 2 errors, got " + errors +
                ":\n" + result.FormatDiagnostics());
        }

        // ==================================================================
        //  Unused name warnings
        // ==================================================================

        private static Diagnostic[] Warnings(string source) =>
            IZCompiler.Compile(source).Diagnostics.Where(d => !d.IsError).ToArray();

        [Fact]
        public void ConstDeclaredAndNeverUsedBecomesAWarning()
        {
            var warnings = Warnings(
                "const LED = 5;\n" +
                "fn main() { }\n");

            var warning = Assert.Single(warnings);
            Assert.Equal(IZErrorCode.UnusedVariable, warning.Code);
            Assert.Contains("LED", warning.Message);
            Assert.Contains("const", warning.Message);
        }

        [Fact]
        public void AnUnusedWarningDoesNotStopCompilation()
        {
            var result = IZCompiler.Compile(
                "const LED = 5;\n" +
                "fn main() { }\n");

            // A warning is a warning: the chip still runs the program.
            Assert.True(result.Success, result.FormatDiagnostics());
        }

        [Fact]
        public void UsedDeviceAndVariableDoNotBecomeWarnings()
        {
            var warnings = Warnings(
                "device pump = d0;\n" +
                "const TARGET = 50;\n" +
                "fn main() {\n" +
                "    var p = pump.Setting;\n" +
                "    pump.On = p < TARGET;\n" +
                "}\n");

            Assert.Empty(warnings);
        }

        [Fact]
        public void DeviceDeclaredAndNeverUsedBecomesAWarning()
        {
            var warnings = Warnings(
                "device leftover = d3;\n" +
                "fn main() { }\n");

            var warning = Assert.Single(warnings);
            Assert.Equal(IZErrorCode.UnusedVariable, warning.Code);
            Assert.Contains("device", warning.Message);
        }

        [Fact]
        public void ParameterAndForIndexDoNotBecomeWarnings()
        {
            // An unused parameter is a signature, not carelessness; and the 'for' index
            // is often just a repetition counter.
            var warnings = Warnings(
                "fn ignore(x: num) { }\n" +
                "fn main() {\n" +
                "    for i in 0..3 { ignore(1); }\n" +
                "}\n");

            Assert.Empty(warnings);
        }

        [Fact]
        public void WithARealErrorTheUnusedWarningsGoQuiet()
        {
            // Next to an error the warning is noise: the name may be "unused" only
            // because the code that would use it is exactly what fails to compile.
            var result = IZCompiler.Compile(
                "const LED = 5;\n" +
                "fn main() { doesNotExist(); }\n");

            Assert.False(result.Success);
            Assert.DoesNotContain(result.Diagnostics, d => d.Code == IZErrorCode.UnusedVariable);
        }

        [Fact]
        public void TheWarningPointsAtTheDeclaredName()
        {
            var result = IZCompiler.Compile(
                "const LED = 5;\n" +
                "fn main() { }\n");

            // The program has no errors at all: the only diagnostic is the warning.
            var warning = Assert.Single(result.Diagnostics);
            Assert.Equal("LED", result.Source.Text.Substring(warning.Span.Start, warning.Span.Length));
        }

        [Fact]
        public void CompilationDoesNotThrowOnRandomInput()
        {
            // Robustness: the in-game editor compiles on every keystroke, so half
            // written input is the common case, not the exception.
            string[] fragments =
            {
                "", " ", "fn", "fn main", "fn main(", "fn main()", "fn main() {",
                "fn main() { if", "fn main() { if (", "var", "var x", "var x =",
                "device", "device p =", "device p = d", "}", "{{{{", "))))",
                "fn main() { for i in", "fn main() { x.", "#\"", "\"", "/*",
                "fn main() { return", "0x", "0b", "1e", "..", "..=", "->",
            };

            foreach (var fragment in fragments)
            {
                var result = IZCompiler.Compile(fragment);
                Assert.NotNull(result);          // it did not throw: that is what matters here
            }
        }

        // ------------------------------------------------------------------
        //  The ternary operator
        // ------------------------------------------------------------------

        [Fact]
        public void ATernaryConditionIsABoolAndNotANum()
        {
            var error = TestHost.CompileError(
                "device out = d0;\n" +
                "fn main() {\n" +
                "    var x = 1;\n" +
                "    out.Setting = x ? 10 : 20;\n" +
                "}\n",
                IZErrorCode.TypeMismatch);

            Assert.Contains("x != 0", error.Message);
        }

        [Fact]
        public void TheTwoSidesOfATernaryHaveTheSameType()
        {
            var error = TestHost.CompileError(
                "device out = d0;\n" +
                "fn main() {\n" +
                "    var c = true;\n" +
                "    out.Setting = len(c ? 1 : \"a\");\n" +
                "}\n",
                IZErrorCode.TypeMismatch);

            // The message has to name both types, or the player has to guess which side is wrong.
            Assert.Contains("num", error.Message);
            Assert.Contains("str", error.Message);
        }

        [Fact]
        public void ATernaryCannotChooseBetweenArrays()
        {
            var error = TestHost.CompileError(
                "device out = d0;\n" +
                "fn main() {\n" +
                "    var a: num[2];\n" +
                "    var b: num[2];\n" +
                "    var c = true;\n" +
                "    out.Setting = (c ? a : b)[0];\n" +
                "}\n",
                IZErrorCode.TypeMismatch);

            Assert.Contains("array", error.Message);
        }

        [Fact]
        public void ATernaryCannotChooseBetweenCallsThatReturnNothing()
        {
            var error = TestHost.CompileError(
                "device out = d0;\n" +
                "fn nothing() { }\n" +
                "fn main() {\n" +
                "    var c = true;\n" +
                "    out.Setting = c ? nothing() : nothing();\n" +
                "}\n",
                IZErrorCode.TypeMismatch);

            Assert.Contains("nothing", error.Message);
        }

        [Fact]
        public void ATernaryWithoutItsColonIsOneError()
        {
            var result = IZCompiler.Compile(
                "device out = d0;\n" +
                "fn main() {\n" +
                "    out.Setting = true ? 1;\n" +
                "}\n");

            Assert.False(result.Success);
            Assert.Equal(1, result.Diagnostics.Count(d => d.IsError));
        }

        // ------------------------------------------------------------------
        //  Prefab hashing
        // ------------------------------------------------------------------

        [Fact]
        public void Crc32MatchesTheStandardCheckValue()
        {
            // The CRC-32/ISO-HDLC check value, defined by the standard itself.
            Assert.Equal(0xCBF43926u, PrefabHash.ComputeUnsigned("123456789"));
        }

        [Fact]
        public void PrefabHashIsStableAndSigned()
        {
            int hash = PrefabHash.Compute("StructureWallLight");
            Assert.Equal(hash, PrefabHash.Compute("StructureWallLight"));
            Assert.NotEqual(0, hash);
        }

        [Fact]
        public void HashLiteralIsFoldedAtCompileTime()
        {
            var program = TestHost.CompileOk(
                "device out = d0;\n" +
                "fn main() { out.Setting = #\"StructureWallLight\"; }\n");

            Assert.Contains((double)PrefabHash.Compute("StructureWallLight"), program.Constants);
        }
    }
}
