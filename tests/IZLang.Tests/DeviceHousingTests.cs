using System.Linq;
using IZLang.Binding;
using IZLang.Devices;
using IZLang.Diagnostics;
using IZLang.Editor;
using IZLang.Vm;
using Xunit;

namespace IZLang.Tests
{
    /// <summary>
    /// 'db' - the device the chip is installed in.
    ///
    /// It matters most outside a wall housing: a hardsuit holds the chip in its own
    /// slot, so 'db' is the suit and is the only way a program can read the suit's own
    /// external pressure or drive its own AC. Reaching it through a pin is impossible,
    /// because the suit is not wired to itself.
    /// </summary>
    public class DeviceHousingTests
    {
        // ------------------------------------------------------------------
        //  Parsing and compiling
        // ------------------------------------------------------------------

        [Fact]
        public void DbParsesToTheHousingPin()
        {
            Assert.True(DevicePins.TryParse("db", out int pin));
            Assert.Equal(DevicePins.Housing, pin);
        }

        [Fact]
        public void TheHousingIsNotOneOfTheNumberedPins()
        {
            // 'db' has to sit past d5 so that no pin can be mistaken for it.
            Assert.True(DevicePins.Housing > DevicePins.Last);
            Assert.True(DevicePins.IsValid(DevicePins.Housing));
            Assert.False(DevicePins.IsValid(DevicePins.Housing + 1));
        }

        [Fact]
        public void DbIsNamedBackAsItWasWritten() =>
            Assert.Equal("db", DevicePins.Name(DevicePins.Housing));

        [Theory]
        [InlineData("d6")]
        [InlineData("da")]
        [InlineData("dbb")]
        [InlineData("b")]
        public void OnlyDbAndTheSixPinsParse(string text) =>
            Assert.False(DevicePins.TryParse(text, out _));

        [Fact]
        public void ADeviceCanBeDeclaredOnDb() =>
            TestHost.CompileOk(
                "device suit = db;\n" +
                "fn main() { suit.On = true; }\n");

        [Fact]
        public void AnUnknownPinStillFails() =>
            TestHost.CompileError(
                "device suit = d9;\n" +
                "fn main() { }\n",
                IZErrorCode.InvalidDevicePin);

        [Fact]
        public void TheErrorForABadPinMentionsDb()
        {
            var error = TestHost.CompileError(
                "device suit = dx;\n" +
                "fn main() { }\n",
                IZErrorCode.InvalidDevicePin);

            Assert.Contains("db", error.Message);
        }

        // ------------------------------------------------------------------
        //  Execution
        // ------------------------------------------------------------------

        private const int LogicOn = 28;
        private const int LogicSetting = 12;

        [Fact]
        public void ReadingDbGoesToTheHousing()
        {
            var host = TestHost.Execute(
                "device suit = db;\n" +
                "device out  = d0;\n" +
                "fn main() { out.Setting = suit.Setting; }\n",
                h =>
                {
                    h.Connect(0);
                    h.Set(DevicePins.Housing, LogicSetting, 42.0);
                });

            Assert.Equal(42.0, host.Writes.Single(w => w.Pin == 0).Value);
        }

        [Fact]
        public void WritingToDbGoesToTheHousing()
        {
            var host = TestHost.Execute(
                "device suit = db;\n" +
                "fn main() { suit.On = true; }\n",
                h => h.Connect(DevicePins.Housing));

            var write = Assert.Single(host.Writes);
            Assert.Equal(DevicePins.Housing, write.Pin);
            Assert.Equal(LogicOn, write.LogicType);
            Assert.Equal(1.0, write.Value);
        }

        [Fact]
        public void DbAndTheNumberedPinsAreDifferentDevices()
        {
            // The bug this guards against is 'db' quietly aliasing a pin: writing to
            // one would then show up on the other.
            var host = TestHost.Execute(
                "device suit  = db;\n" +
                "device other = d0;\n" +
                "fn main() {\n" +
                "    suit.Setting = 1;\n" +
                "    other.Setting = 2;\n" +
                "}\n",
                h => { h.Connect(DevicePins.Housing); h.Connect(0); });

            Assert.Equal(1.0, host.Get(DevicePins.Housing, LogicSetting));
            Assert.Equal(2.0, host.Get(0, LogicSetting));
        }

        [Fact]
        public void ASlotOnDbIsReadFromTheHousing()
        {
            const int slotQuantity = 3;

            var host = TestHost.Execute(
                "device suit = db;\n" +
                "device out  = d0;\n" +
                "fn main() { out.Setting = suit.slot[2].Quantity; }\n",
                h =>
                {
                    h.Connect(0);
                    h.SetSlot(DevicePins.Housing, 2, slotQuantity, 7.0);
                });

            Assert.Equal(7.0, host.Writes.Single(w => w.Pin == 0).Value);
        }

        [Fact]
        public void AChipInNothingReportsThatInsteadOfAnEmptyPin()
        {
            // Nothing is connected on the host, which is what a chip sitting in a
            // player inventory looks like.
            var program = TestHost.CompileOk(
                "device suit = db;\n" +
                "fn main() { suit.On = true; }\n");

            var vm = new IZVm(program, new MemoryDeviceHost());
            Assert.Equal(ExecutionResult.Error, TestHost.RunToCompletion(vm));

            Assert.Equal(RuntimeErrorKind.DeviceNotConnected, vm.Error!.Kind);
            Assert.Contains("db", vm.Error.Message);
            // "no device connected on pin db" would be nonsense: there is no pin.
            Assert.DoesNotContain("pin", vm.Error.Message);
        }

        // ------------------------------------------------------------------
        //  Editor
        // ------------------------------------------------------------------

        private const int LogicPressureExternal = 40;

        private static DeviceInfo Suit() => new DeviceInfo(
            "ItemHardSuit", PrefabHash.Compute("ItemHardSuit"), "Hardsuit", 0,
            new[]
            {
                new LogicProperty("On", LogicOn, LogicAccess.ReadWrite),
                new LogicProperty("PressureExternal", LogicPressureExternal, LogicAccess.Read),
            },
            new SlotProperty[0]);

        private static MemoryEditorEnvironment SuitEnvironment()
        {
            var environment = new MemoryEditorEnvironment
            {
                Catalog = new DeviceCatalog(new[] { Suit() }, "test"),
            };
            environment.Wire(DevicePins.Housing, Suit());
            return environment;
        }

        [Fact]
        public void CompletionOffersDbAfterDeviceEquals()
        {
            const string source = "device suit = ";

            var result = CompletionEngine.GetCompletions(source, source.Length, SuitEnvironment());

            Assert.Equal(CompletionContext.Pin, result.Context);
            Assert.Contains(result.Items, i => i.Label == "db");
        }

        [Fact]
        public void CompletionOnADbDeviceOffersTheHousingProperties()
        {
            const string source = "device suit = db;\nfn main() { suit. }\n";
            int caret = source.IndexOf("suit.") + "suit.".Length;

            var result = CompletionEngine.GetCompletions(source, caret, SuitEnvironment());

            Assert.Equal(CompletionContext.DeviceProperty, result.Context);
            Assert.Contains(result.Items, i => i.Label == "PressureExternal");
        }

        [Fact]
        public void HoverOnADbDeviceExplainsWhatItIs()
        {
            const string source = "device suit = db;\nfn main() { suit.On = true; }\n";
            int caret = source.IndexOf("suit.On") + 2;

            var hover = HoverEngine.GetHover(source, caret, SuitEnvironment());

            Assert.Equal(HoverKind.Device, hover.Kind);
            Assert.Equal("suit = db", hover.Title);

            string text = hover.ToText();
            Assert.Contains("the chip is installed in", text);
            Assert.Contains("Hardsuit", text);
        }

        // ------------------------------------------------------------------
        //  isset - is there a device on the pin?
        // ------------------------------------------------------------------

        [Fact]
        public void IsSetIsFalseOnAPinThatWasNeverConnected()
        {
            var host = TestHost.Execute(
                "device probe = d1;\n" +
                "device out   = d0;\n" +
                "fn main() { out.Setting = isset(probe); }\n",
                h => h.Connect(0));

            Assert.Equal(0.0, host.Writes.Single(w => w.Pin == 0).Value);
        }

        [Fact]
        public void IsSetIsTrueOnceThePinIsConnected()
        {
            var host = TestHost.Execute(
                "device probe = d1;\n" +
                "device out   = d0;\n" +
                "fn main() { out.Setting = isset(probe); }\n",
                h => { h.Connect(0); h.Connect(1); });

            Assert.Equal(1.0, host.Writes.Single(w => w.Pin == 0).Value);
        }

        [Fact]
        public void APinCanBeWrittenStraightIntoTheCall()
        {
            var host = TestHost.Execute(
                "device out = d0;\n" +
                "fn main() { out.Setting = isset(d3); }\n",
                h => { h.Connect(0); h.Connect(3); });

            Assert.Equal(1.0, host.Writes.Single(w => w.Pin == 0).Value);
        }

        [Fact]
        public void AGuardedProgramRunsToTheEndWithNothingConnected()
        {
            // The point of the whole feature: nothing connected anywhere, and still
            // no error - the program simply skips the block.
            var program = TestHost.CompileOk(
                "device helmet = d0;\n" +
                "fn main() {\n" +
                "    if isset(helmet) { helmet.On = true; }\n" +
                "}\n");

            var host = new MemoryDeviceHost();
            var vm = new IZVm(program, host);

            Assert.Equal(ExecutionResult.Halted, TestHost.RunToCompletion(vm));
            Assert.Null(vm.Error);
            Assert.Empty(host.Writes);
        }

        [Fact]
        public void IsSetOnDbFollowsTheHousing()
        {
            const string source =
                "device suit = db;\n" +
                "device out  = d0;\n" +
                "fn main() { out.Setting = isset(suit); }\n";

            var installed = TestHost.Execute(source,
                h => { h.Connect(0); h.Connect(DevicePins.Housing); });
            Assert.Equal(1.0, installed.Writes.Single(w => w.Pin == 0).Value);

            var loose = TestHost.Execute(source, h => h.Connect(0));
            Assert.Equal(0.0, loose.Writes.Single(w => w.Pin == 0).Value);
        }

        [Fact]
        public void ASelectorIsNotAPin()
        {
            var error = TestHost.CompileError(
                "device lights = all(StructureWallLight);\n" +
                "device out    = d0;\n" +
                "fn main() { out.Setting = isset(lights); }\n",
                IZErrorCode.NotADevice);

            Assert.Contains("selector", error.Message);
        }

        [Fact]
        public void ANumberIsNotADevice()
        {
            var error = TestHost.CompileError(
                "device out = d0;\n" +
                "fn main() {\n" +
                "    var x = 1;\n" +
                "    out.Setting = isset(x);\n" +
                "}\n",
                IZErrorCode.NotADevice);

            Assert.Contains("declared with 'device'", error.Message);
        }

        [Fact]
        public void IsSetTakesExactlyOneDevice()
        {
            var error = TestHost.CompileError(
                "device a   = d0;\n" +
                "device b   = d1;\n" +
                "device out = d2;\n" +
                "fn main() { out.Setting = isset(a, b); }\n",
                IZErrorCode.WrongArgumentCount);

            Assert.Contains("'isset'", error.Message);
        }

        [Fact]
        public void AFunctionCannotBeCalledIsSet() =>
            TestHost.CompileError(
                "fn isset(x: num) -> num { return x; }\n" +
                "fn main() { }\n",
                IZErrorCode.DuplicateName);

        [Fact]
        public void IsSetIsOfferedByCompletion()
        {
            const string source = "fn main() { var x = is }\n";
            int caret = source.IndexOf("is }") + 2;

            var result = CompletionEngine.GetCompletions(source, caret, EmptyEditorEnvironment.Instance);

            Assert.Contains(result.Items, i => i.Label == "isset");
        }

        [Fact]
        public void HoverOnIsSetExplainsWhatItAnswers()
        {
            const string source = "device helmet = d0;\nfn main() { if isset(helmet) { } }\n";
            int caret = source.IndexOf("isset") + 2;

            var hover = HoverEngine.GetHover(source, caret, EmptyEditorEnvironment.Instance);

            Assert.Equal(HoverKind.Builtin, hover.Kind);
            Assert.Contains("dev", hover.Title);
            Assert.Contains("connected", hover.ToText());
        }

        [Fact]
        public void HoverOnDbWithNoHousingSaysSo()
        {
            const string source = "device suit = db;\nfn main() { suit.On = true; }\n";
            int caret = source.IndexOf("suit.On") + 2;

            var hover = HoverEngine.GetHover(source, caret, EmptyEditorEnvironment.Instance);

            Assert.Contains("not installed in a device", hover.ToText());
        }
    }
}
