using System.Linq;
using IZLang.Binding;
using IZLang.Diagnostics;
using IZLang.Editor;
using IZLang.Lexing;
using IZLang.Vm;
using Xunit;

namespace IZLang.Tests
{
    /// <summary>
    /// A device name bound to a batch selector instead of a housing pin:
    /// <c>device led = named(StructureDiode, #"led-dev");</c>
    ///
    /// The point is that the six pins run out long before the devices do. A selector
    /// declared once reads and writes exactly like the inline form, so the name of
    /// the group is written in a single place and the code that uses it looks the
    /// same whether the target is on a cable or on the network.
    /// </summary>
    public class BatchDeviceTests
    {
        private const int LogicOn = 28;
        private const int LogicSetting = 12;
        private const int LogicPressure = 5;

        private static readonly int DiodeHash = PrefabHash.Compute("StructureDiode");
        private static readonly int PumpHash = PrefabHash.Compute("StructureVolumePump");
        private static readonly int LightHash = PrefabHash.Compute("StructureWallLight");

        // ------------------------------------------------------------------
        //  Writing
        // ------------------------------------------------------------------

        [Fact]
        public void TheDeclarationCompilesExactlyAsItWasAskedFor()
        {
            TestHost.CompileOk(
                "device led = named(StructureDiode, #\"led-dev\");\n" +
                "fn main() { led.On = true; }\n");
        }

        [Fact]
        public void ANamedDeviceWritesToThePrefabAndLabelItWasDeclaredWith()
        {
            var host = Run(
                "device led = named(StructureDiode, #\"led-dev\");\n" +
                "fn main() { led.On = true; }\n");

            var write = Assert.Single(host.BatchWrites);
            Assert.Equal(DiodeHash, write.PrefabHash);
            Assert.Equal(PrefabHash.Compute("led-dev"), write.NameHash);
            Assert.Equal(LogicOn, write.LogicType);
            Assert.Equal(1.0, write.Value);
        }

        [Fact]
        public void AnAllDeviceWritesToEveryDeviceOfThePrefab()
        {
            var host = new MemoryDeviceHost();
            var first = host.AddNetworkDevice(LightHash);
            var second = host.AddNetworkDevice(LightHash);
            var other = host.AddNetworkDevice(PumpHash);

            RunOn(host,
                "device lights = all(StructureWallLight);\n" +
                "fn main() { lights.On = true; }\n");

            Assert.Equal(1.0, first.Get(LogicOn));
            Assert.Equal(1.0, second.Get(LogicOn));
            Assert.Equal(0.0, other.Get(LogicOn));
        }

        [Fact]
        public void ALabelOnlyDeviceMatchesAnyPrefabWithThatLabel()
        {
            var host = new MemoryDeviceHost();
            int label = PrefabHash.Compute("corridor");
            var light = host.AddNetworkDevice(LightHash, label);
            var pump = host.AddNetworkDevice(PumpHash, label);
            var elsewhere = host.AddNetworkDevice(LightHash, PrefabHash.Compute("hangar"));

            RunOn(host,
                "device corridor = named(\"corridor\");\n" +
                "fn main() { corridor.Setting = 5; }\n");

            Assert.Equal(5.0, light.Get(LogicSetting));
            Assert.Equal(5.0, pump.Get(LogicSetting));
            Assert.Equal(0.0, elsewhere.Get(LogicSetting));
        }

        // ------------------------------------------------------------------
        //  Reading
        // ------------------------------------------------------------------

        [Fact]
        public void AReadAveragesEveryDeviceTheSelectorReaches()
        {
            var host = new MemoryDeviceHost();
            host.Connect(0);
            int label = PrefabHash.Compute("north");
            host.AddNetworkDevice(PumpHash, label).Values[LogicPressure] = 10.0;
            host.AddNetworkDevice(PumpHash, label).Values[LogicPressure] = 30.0;
            host.AddNetworkDevice(PumpHash, PrefabHash.Compute("south")).Values[LogicPressure] = 900.0;

            RunOn(host,
                "device out = d0;\n" +
                "device north = named(StructureVolumePump, \"north\");\n" +
                "fn main() { out.Setting = north.Pressure; }\n");

            Assert.Equal(20.0, host.Writes.Last().Value);
        }

        [Fact]
        public void ADeviceDeclaredFromASelectorCostsWhatTheInlineFormCosts()
        {
            var declared = TestHost.CompileOk(
                "device led = named(StructureDiode, \"led-dev\");\n" +
                "fn main() { led.On = true; }\n");

            var inline = TestHost.CompileOk(
                "fn main() { named(StructureDiode, \"led-dev\").On = true; }\n");

            Assert.Equal(inline.Code.Length, declared.Code.Length);
        }

        // ------------------------------------------------------------------
        //  How the selector is written
        // ------------------------------------------------------------------

        [Fact]
        public void AConstantHoldingTheHashIsUsedForItsValue()
        {
            var host = Run(
                "const DIODE = #\"StructureDiode\";\n" +
                "const LABEL = \"led-dev\";\n" +
                "device led = named(DIODE, LABEL);\n" +
                "fn main() { led.On = true; }\n");

            var write = Assert.Single(host.BatchWrites);
            Assert.Equal(DiodeHash, write.PrefabHash);
            Assert.Equal(PrefabHash.Compute("led-dev"), write.NameHash);
        }

        [Fact]
        public void ADeviceCanBeDeclaredInsideAFunction()
        {
            var host = Run(
                "fn main() {\n" +
                "    device led = all(StructureDiode);\n" +
                "    led.On = true;\n" +
                "}\n");

            Assert.Equal(DiodeHash, Assert.Single(host.BatchWrites).PrefabHash);
        }

        // ------------------------------------------------------------------
        //  Errors
        // ------------------------------------------------------------------

        [Fact]
        public void ASelectorThatDependsOnARunningValueIsRejected()
        {
            // The name has to mean the same devices on every line, so the label
            // cannot come from something the program computes.
            TestHost.CompileError(
                "var wing: str;\n" +
                "device vents = named(\"vent-\" + wing);\n" +
                "fn main() { vents.On = true; }\n",
                IZErrorCode.ConstExpressionRequired);
        }

        [Fact]
        public void ABatchDeviceRefusesACompoundAssignment()
        {
            TestHost.CompileError(
                "device lights = all(StructureWallLight);\n" +
                "fn main() { lights.Setting += 1; }\n",
                IZErrorCode.InvalidAssignmentTarget);
        }

        [Fact]
        public void ABatchDeviceHasNoSlots()
        {
            TestHost.CompileError(
                "device chutes = all(StructureChuteBin);\n" +
                "fn main() { var q = chutes.slot[0].Quantity; }\n",
                IZErrorCode.NotADevice);
        }

        [Fact]
        public void AnUnknownPropertyIsStillCaught()
        {
            TestHost.CompileError(
                "device led = all(StructureDiode);\n" +
                "fn main() { led.Nonsense = 1; }\n",
                IZErrorCode.UnknownLogicType);
        }

        [Fact]
        public void ADeviceNameIsStillNotAValue()
        {
            TestHost.CompileError(
                "device led = all(StructureDiode);\n" +
                "device out = d0;\n" +
                "fn main() { out.Setting = led; }\n",
                IZErrorCode.NotADevice);
        }

        // ------------------------------------------------------------------
        //  Editor
        // ------------------------------------------------------------------

        [Fact]
        public void TheScannerRecordsTheSelectorItWasDeclaredFrom()
        {
            var tokens = new Lexer("device led = named(StructureDiode, \"led-dev\");",
                                   new DiagnosticBag()).Tokenize();

            var led = Assert.Single(DeclarationScanner.Scan(tokens), s => s.Name == "led");
            Assert.Equal(DeclaredKind.Device, led.Kind);
            Assert.Equal(-1, led.Pin);
            Assert.Equal("named(StructureDiode, \"led-dev\")", led.BatchSelector);
        }

        [Fact]
        public void HoverShowsTheSelectorRatherThanAMissingPin()
        {
            string source =
                "device led = named(StructureDiode, \"led-dev\");\n" +
                "fn main() { led.On = true; }\n";

            var hover = HoverEngine.GetHover(source, source.IndexOf("led.On") + 1);

            Assert.Equal(HoverKind.Device, hover.Kind);
            Assert.Equal("led = named(StructureDiode, \"led-dev\")", hover.Title);
            Assert.DoesNotContain("no valid pin", hover.ToText());
        }

        [Fact]
        public void CompletionOffersTheSelectorsNextToThePins()
        {
            var result = CompletionEngine.GetCompletions("device x = ", 11);

            var labels = result.Items.Select(i => i.Label).ToList();
            Assert.Contains("db", labels);
            Assert.Contains("all", labels);
            Assert.Contains("named", labels);
        }

        [Fact]
        public void CompletionShowsTheSelectorAsTheDetailOfTheName()
        {
            string source =
                "device led = all(StructureDiode);\n" +
                "fn main() { l }\n";

            var result = CompletionEngine.GetCompletions(source, source.IndexOf("{ l") + 3);

            var led = Assert.Single(result.Items, i => i.Label == "led");
            Assert.Equal("all(StructureDiode)", led.Detail);
        }

        // ------------------------------------------------------------------
        //  Helpers
        // ------------------------------------------------------------------

        private static MemoryDeviceHost Run(string source)
        {
            var host = new MemoryDeviceHost();
            RunOn(host, source);
            return host;
        }

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
