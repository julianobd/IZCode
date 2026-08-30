using System.Linq;
using IZLang.Binding;
using IZLang.Diagnostics;
using IZLang.Editor;
using Xunit;

namespace IZLang.Tests
{
    /// <summary>
    /// The game's named values: Color.Black, AirCon.Cold, GasType.Oxygen.
    ///
    /// The numbers come out of Assembly-CSharp through the generator, so most of
    /// what is checked here is the spelling a script uses and the fact that the
    /// value is folded. The handful of numbers pinned below are the exception: if
    /// the game renumbers those, every script written against them changes
    /// meaning, and a red test is the right way to find that out.
    /// </summary>
    public class GameConstantTests
    {
        // ------------------------------------------------------------------
        //  The tables
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("Color", "Black", 7)]
        [InlineData("Color", "White", 6)]
        [InlineData("AirCon", "Cold", 0)]
        [InlineData("AirCon", "Hot", 1)]
        [InlineData("GasType", "Oxygen", 1)]
        [InlineData("PowerMode", "Idle", 0)]
        [InlineData("Vent", "Outward", 0)]
        [InlineData("Vent", "Inward", 1)]
        [InlineData("LogicType", "Pressure", 5)]
        [InlineData("LogicSlotType", "Occupied", 1)]
        public void KnownValuesKeepTheirNumber(string group, string member, int expected)
        {
            Assert.True(GameEnums.TryGetConstant(group, member, out int value));
            Assert.Equal(expected, value);
        }

        [Fact]
        public void EveryGroupTheGameExposesIsThere()
        {
            // The list the game's ProgrammableChip registers. Losing one of these
            // silently would only show up as 'unknown name' in someone's script.
            string[] expected =
            {
                "LogicType", "LogicSlotType", "LogicReagentMode", "LogicBatchMethod",
                "Sound", "TransmitterMode", "ElevatorMode", "Color", "EntityState",
                "AirControl", "DaylightSensorMode", "ConditionOperation", "AirCon",
                "Vent", "FiltrationMode", "PowerMode", "RobotMode", "SortingClass",
                "SlotClass", "GasType", "RocketMode", "ReEntryProfile",
                "SorterInstruction", "PrinterInstruction", "TraderInstruction",
                "ShuttleType", "HashType", "DisplayMode", "SettingDisplayMode", "NodeType",
            };

            foreach (var group in expected)
                Assert.True(GameEnums.IsConstantGroup(group), group + " is missing");
        }

        [Fact]
        public void NoGroupIsEmpty()
        {
            foreach (var group in GameEnums.ConstantGroupNames)
                Assert.NotEmpty(GameEnums.FindConstantGroup(group)!);
        }

        // ------------------------------------------------------------------
        //  In a program
        // ------------------------------------------------------------------

        [Fact]
        public void AValueEvaluatesToItsNumber() =>
            Assert.Equal(7.0, TestHost.Eval("Color.Black"), 10);

        [Fact]
        public void ValuesTakePartInArithmetic() =>
            Assert.Equal(8.0, TestHost.Eval("Color.Black + AirCon.Hot"), 10);

        [Fact]
        public void AValueComparesLikeAnyNumber() =>
            Assert.Equal(1.0, TestHost.Eval("Vent.Inward == 1"), 10);

        [Fact]
        public void AValueCanSeedAConst() =>
            Assert.Equal(7.0, TestHost.Eval("warning", "const warning = Color.Black;"), 10);

        [Fact]
        public void AValueCanSizeAnArray()
        {
            // ElevatorMode.Downward is 2, so this declares num[5].
            var program = TestHost.CompileOk(
                "fn main() {\n" +
                "    var counts: num[ElevatorMode.Downward + 3];\n" +
                "    counts[4] = 1;\n" +
                "}\n");

            Assert.NotNull(program);
        }

        [Fact]
        public void AValueIsFoldedAtCompileTime()
        {
            // Nothing is computed at runtime: the number lands in the constant pool
            // exactly like a literal would, so both programs are the same size.
            var withConstant = TestHost.CompileOk(
                "device out = d0;\n" +
                "fn main() { out.Setting = Color.Black; }\n");

            var withLiteral = TestHost.CompileOk(
                "device out = d0;\n" +
                "fn main() { out.Setting = 7; }\n");

            Assert.Equal(withLiteral.Code.Length, withConstant.Code.Length);
        }

        [Fact]
        public void AValueWritesToADeviceProperty()
        {
            var host = TestHost.Execute(
                "device led = d0;\n" +
                "fn main() { led.Color = Color.Green; }\n",
                h => h.Connect(0));

            Assert.Equal(2.0, host.Writes.Last().Value, 10);
        }

        // ------------------------------------------------------------------
        //  Mistakes
        // ------------------------------------------------------------------

        [Fact]
        public void AnUnknownValueIsReportedOnce()
        {
            var result = IZCompiler.Compile(
                "device out = d0;\n" +
                "fn main() { out.Setting = Color.Blck; }\n");

            var errors = result.Diagnostics
                .Where(d => d.IsError && d.Code == IZErrorCode.UnknownConstant)
                .ToList();

            Assert.Single(errors);
            Assert.Contains("did you mean 'Black'?", errors[0].Message);
        }

        [Fact]
        public void AnUnknownValueInAConstDoesNotAlsoComplainAboutFolding()
        {
            var result = IZCompiler.Compile("const c = Color.Blck;\nfn main() { }\n");

            Assert.Contains(result.Diagnostics, d => d.Code == IZErrorCode.UnknownConstant);
            Assert.DoesNotContain(result.Diagnostics,
                d => d.Code == IZErrorCode.ConstExpressionRequired);
        }

        [Fact]
        public void AValueCannotBeAssignedTo() =>
            TestHost.CompileError("fn main() { Color.Black = 1; }\n", IZErrorCode.AssignToConst);

        [Fact]
        public void ADeclarationShadowsTheGroup()
        {
            // Someone who declares 'Color' meant their own name. The group steps
            // aside and the usual complaint follows, rather than a silent 7.
            var result = IZCompiler.Compile(
                "device out = d0;\n" +
                "fn main() {\n" +
                "    var Color = 3;\n" +
                "    out.Setting = Color.Black;\n" +
                "}\n");

            Assert.False(result.Success);
            Assert.DoesNotContain(result.Diagnostics, d => d.Code == IZErrorCode.UnknownConstant);
        }

        [Fact]
        public void AGroupNameOnItsOwnIsNotAValue() =>
            TestHost.CompileError(
                "device out = d0;\nfn main() { out.Setting = Color; }\n",
                IZErrorCode.UndefinedName);

        // ------------------------------------------------------------------
        //  Editor
        // ------------------------------------------------------------------

        [Fact]
        public void CompletionOffersTheGroupNames()
        {
            const string source = "fn main() { var x = Col";
            var result = CompletionEngine.GetCompletions(source, source.Length);

            Assert.Contains(result.Items, i => i.Label == "Color" && i.Kind == CompletionKind.Constant);
        }

        [Fact]
        public void CompletionOffersTheValuesOfAGroup()
        {
            const string source = "fn main() { var x = Color.; }";
            var result = CompletionEngine.GetCompletions(source, source.IndexOf('.') + 1);

            Assert.Equal(CompletionContext.ConstantValue, result.Context);
            Assert.Contains(result.Items, i => i.Label == "Black");
            Assert.DoesNotContain(result.Items, i => i.Label == "Pressure");
        }

        [Fact]
        public void CompletionStillOffersPropertiesAfterADevice()
        {
            const string source = "device pump = d0;\nfn main() { var x = pump.; }";
            var result = CompletionEngine.GetCompletions(source, source.IndexOf("pump.") + 5);

            Assert.Equal(CompletionContext.DeviceProperty, result.Context);
        }

        [Fact]
        public void HoverOnAValueShowsItsNumber()
        {
            const string source = "fn main() { var x = Color.Black; }";
            var hover = HoverEngine.GetHover(source, source.IndexOf("Black"));

            Assert.Equal(HoverKind.Constant, hover.Kind);
            Assert.Contains("7", hover.ToText());
        }

        [Fact]
        public void HoverOnAGroupSaysHowManyValuesItHas()
        {
            const string source = "fn main() { var x = Color.Black; }";
            var hover = HoverEngine.GetHover(source, source.IndexOf("Color"));

            Assert.Equal(HoverKind.Constant, hover.Kind);
            Assert.Contains("12", hover.ToText());
        }
    }
}
