using System.Linq;
using IZLang.Binding;
using IZLang.Diagnostics;
using IZLang.Vm;
using Xunit;

namespace IZLang.Tests
{
    /// <summary>
    /// Referring to devices by prefab hash: the <c>#"..."</c> literal, a bare name, a
    /// constant, and the label filter of <c>named</c>.
    /// </summary>
    public class HashTests
    {
        private const int LogicOn = 28;
        private const int LogicSetting = 12;
        private const int LogicPressure = 5;

        private static readonly int PumpHash = PrefabHash.Compute("StructureVolumePump");
        private static readonly int LightHash = PrefabHash.Compute("StructureWallLight");

        // ------------------------------------------------------------------
        //  The three forms produce the same hash
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("all(StructureVolumePump)")]                                   // bare name
        [InlineData("all(#\"StructureVolumePump\")")]                              // hash literal
        public void EquivalentWaysOfReferringToThePrefab(string selector)
        {
            var host = Run("fn main() { " + selector + ".On = true; }");

            var write = Assert.Single(host.BatchWrites);
            Assert.Equal(PumpHash, write.PrefabHash);
            Assert.Equal(LogicOn, write.LogicType);
            Assert.Equal(1.0, write.Value);
        }

        [Fact]
        public void AHashConstantUsesItsValueAndNotTheConstantName()
        {
            // Regression: 'all(PUMP)' used to silently hash the string "PUMP",
            // pointing the batch at a prefab that does not exist.
            var host = Run(
                "const PUMP = #\"StructureVolumePump\";\n" +
                "fn main() { all(PUMP).On = true; }\n");

            var write = Assert.Single(host.BatchWrites);
            Assert.Equal(PumpHash, write.PrefabHash);
            Assert.NotEqual(PrefabHash.Compute("PUMP"), write.PrefabHash);
        }

        [Fact]
        public void AVariableWithAHashComputedAtRuntime()
        {
            var host = Run(
                "var target = 0;\n" +
                "fn main() {\n" +
                "    target = #\"StructureWallLight\";\n" +
                "    all(target).On = true;\n" +
                "}\n");

            var write = Assert.Single(host.BatchWrites);
            Assert.Equal(LightHash, write.PrefabHash);
        }

        [Fact]
        public void ABareNameOnlyCountsWhenNoDeclarationSharesIt()
        {
            // 'StructureVolumePump' is not declared: it counts as a prefab name.
            var host = Run("fn main() { all(StructureVolumePump).On = true; }");
            Assert.Equal(PumpHash, host.BatchWrites.Single().PrefabHash);
        }

        // ------------------------------------------------------------------
        //  Batch reading
        // ------------------------------------------------------------------

        [Fact]
        public void ABatchReadAveragesTheOnesMatchingTheHash()
        {
            var host = new MemoryDeviceHost();
            host.Connect(0);

            host.AddNetworkDevice(PumpHash).Values[LogicPressure] = 100.0;
            host.AddNetworkDevice(PumpHash).Values[LogicPressure] = 200.0;
            host.AddNetworkDevice(LightHash).Values[LogicPressure] = 999.0;   // not part of the average

            RunOn(host,
                "device out = d0;\n" +
                "fn main() { out.Setting = all(#\"StructureVolumePump\").Pressure; }\n");

            Assert.Equal(150.0, host.Writes.Last().Value);
        }

        [Fact]
        public void ABatchWriteOnlyReachesTheMatchingOnes()
        {
            var host = new MemoryDeviceHost();

            var pumpA = host.AddNetworkDevice(PumpHash);
            var pumpB = host.AddNetworkDevice(PumpHash);
            var light = host.AddNetworkDevice(LightHash);

            RunOn(host, "fn main() { all(#\"StructureVolumePump\").Setting = 42; }");

            Assert.Equal(42.0, pumpA.Get(LogicSetting));
            Assert.Equal(42.0, pumpB.Get(LogicSetting));
            Assert.Equal(0.0, light.Get(LogicSetting));
            Assert.Equal(2, host.BatchWrites.Single().Matched);
        }

        [Fact]
        public void ABatchWithNoMatchingDeviceReadsZero()
        {
            var host = new MemoryDeviceHost();
            host.Connect(0);
            host.AddNetworkDevice(LightHash).Values[LogicPressure] = 500.0;

            RunOn(host,
                "device out = d0;\n" +
                "fn main() { out.Setting = all(#\"StructureVolumePump\").Pressure; }\n");

            Assert.Equal(0.0, host.Writes.Last().Value);
        }

        // ------------------------------------------------------------------
        //  named: label, with and without a prefab
        // ------------------------------------------------------------------

        [Fact]
        public void NamedWithOneArgumentMatchesAnyPrefab()
        {
            var host = Run("fn main() { named(\"pump1\").On = true; }");

            var write = host.BatchWrites.Single();
            Assert.Equal(0, write.PrefabHash);                          // 0 = anything
            Assert.Equal(PrefabHash.Compute("pump1"), write.NameHash);
        }

        [Fact]
        public void NamedWithPrefabAndLabelFiltersBoth()
        {
            var host = Run("fn main() { named(#\"StructureVolumePump\", \"pump1\").On = true; }");

            var write = host.BatchWrites.Single();
            Assert.Equal(PumpHash, write.PrefabHash);
            Assert.Equal(PrefabHash.Compute("pump1"), write.NameHash);
        }

        [Fact]
        public void NamedWithThePrefabAsABareName()
        {
            var host = Run("fn main() { named(StructureVolumePump, \"pump1\").On = true; }");

            var write = host.BatchWrites.Single();
            Assert.Equal(PumpHash, write.PrefabHash);
            Assert.Equal(PrefabHash.Compute("pump1"), write.NameHash);
        }

        [Fact]
        public void NamedWithAPrefabOnlyReachesTheRightDevice()
        {
            var host = new MemoryDeviceHost();

            int label = PrefabHash.Compute("pump1");
            var target = host.AddNetworkDevice(PumpHash, label);
            var sameLabelOtherType = host.AddNetworkDevice(LightHash, label);
            var sameTypeOtherLabel = host.AddNetworkDevice(PumpHash, PrefabHash.Compute("pump2"));

            RunOn(host, "fn main() { named(#\"StructureVolumePump\", \"pump1\").Setting = 7; }");

            Assert.Equal(7.0, target.Get(LogicSetting));
            Assert.Equal(0.0, sameLabelOtherType.Get(LogicSetting));
            Assert.Equal(0.0, sameTypeOtherLabel.Get(LogicSetting));
        }

        [Fact]
        public void ANamedReadAggregatesOnlyTheFilteredGroup()
        {
            var host = new MemoryDeviceHost();
            host.Connect(0);

            int label = PrefabHash.Compute("north");
            host.AddNetworkDevice(PumpHash, label).Values[LogicPressure] = 10.0;
            host.AddNetworkDevice(PumpHash, label).Values[LogicPressure] = 30.0;
            host.AddNetworkDevice(PumpHash, PrefabHash.Compute("south")).Values[LogicPressure] = 900.0;

            RunOn(host,
                "device out = d0;\n" +
                "fn main() { out.Setting = named(#\"StructureVolumePump\", \"north\").Pressure; }\n");

            Assert.Equal(20.0, host.Writes.Last().Value);
        }

        // ------------------------------------------------------------------
        //  Errors
        // ------------------------------------------------------------------

        [Fact]
        public void AllWithTwoArgumentsIsAnError() =>
            TestHost.CompileError(
                "fn main() { all(#\"StructureVolumePump\", \"x\").On = true; }\n",
                IZErrorCode.WrongArgumentCount);

        [Fact]
        public void NamedWithThreeArgumentsIsAnError() =>
            TestHost.CompileError(
                "fn main() { named(#\"StructureVolumePump\", \"a\", \"b\").On = true; }\n",
                IZErrorCode.WrongArgumentCount);

        [Fact]
        public void APrefabCannotBeABool() =>
            TestHost.CompileError(
                "fn main() { all(true).On = true; }\n",
                IZErrorCode.TypeMismatch);

        [Fact]
        public void ADeviceHashIsFoldedAtCompileTime()
        {
            var program = TestHost.CompileOk("fn main() { all(#\"StructureVolumePump\").On = true; }");

            Assert.Contains((double)PumpHash, program.Constants);
            // No hashing work is left for the runtime.
            Assert.DoesNotContain(program.Code, i => i.Op == OpCode.CallBuiltin);
        }

        // ------------------------------------------------------------------
        //  Packed text: the other number a str can become
        // ------------------------------------------------------------------
        //  A hash identifies a device and never comes back as text. Packed text is
        //  the opposite: six characters, one per byte, exactly reversible - it is what
        //  a display shows. Getting the two mixed up is the difference between "Ok" on
        //  the screen and a nine digit number, so the encoding is pinned here.

        [Theory]
        [InlineData("O", 0x4F)]
        [InlineData("Ok", 0x4F6B)]
        [InlineData("ABC", 0x414243)]
        public void PackedTextIsOneBytePerCharacter(string text, long expected) =>
            Assert.Equal((double)expected, PackedText.Pack(text));

        [Theory]
        [InlineData("O")]
        [InlineData("Ok")]
        [InlineData("Status")]
        [InlineData("a b c ")]
        public void PackedTextComesBackWhole(string text) =>
            Assert.Equal(text, PackedText.Unpack(PackedText.Pack(text)));

        [Theory]
        [InlineData("Standby")]      // seven characters
        [InlineData("")]             // nothing to show
        [InlineData("oké")]     // not ASCII: one byte cannot hold it
        public void WhatADisplayCannotHold(string text) =>
            Assert.False(PackedText.CanPack(text));

        [Fact]
        public void AnEmptyReadingIsEmptyText() =>
            Assert.Equal(string.Empty, PackedText.Unpack(0.0));

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
