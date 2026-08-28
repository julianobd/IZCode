using System.Linq;
using IZLang.Vm;
using Xunit;

namespace IZLang.Tests
{
    /// <summary>
    /// The behaviour that separates IZ from IC10: the VM is preempted by budget and
    /// resumes exactly where it stopped, instead of restarting on every tick.
    /// </summary>
    public class PreemptionTests
    {
        [Fact]
        public void AnInfiniteLoopDoesNotFreezeTheGame()
        {
            var program = TestHost.CompileOk(
                "var n = 0;\n" +
                "fn main() { loop { n += 1; } }\n");

            var vm = new IZVm(program, new MemoryDeviceHost());

            // A loop with no yield and no exit: every tick hands control back.
            for (int tick = 0; tick < 10; tick++)
                Assert.Equal(ExecutionResult.BudgetExhausted, vm.Run(64));

            Assert.Equal(10 * 64, vm.TotalInstructions);
        }

        [Fact]
        public void StateSurvivesAcrossTicks()
        {
            var program = TestHost.CompileOk(
                "var counter = 0;\n" +
                "device out = d0;\n" +
                "fn main() {\n" +
                "    while counter < 1000 { counter += 1; }\n" +
                "    out.Setting = counter;\n" +
                "}\n");

            var host = new MemoryDeviceHost();
            host.Connect(0);
            var vm = new IZVm(program, host);

            // Tight budget: the loop stretches across dozens of ticks.
            int ticks = 0;
            while (vm.Run(32) == ExecutionResult.BudgetExhausted && ticks < 10000) ticks++;

            Assert.True(ticks > 10, "expected the loop to span several ticks, it took " + ticks);
            Assert.Equal(ExecutionResult.Halted, vm.State);
            Assert.Equal(1000.0, host.Writes.Last().Value);
        }

        [Fact]
        public void YieldHandsTheTickBackImmediately()
        {
            var program = TestHost.CompileOk(
                "var n = 0;\n" +
                "fn main() {\n" +
                "    loop {\n" +
                "        n += 1;\n" +
                "        yield;\n" +
                "    }\n" +
                "}\n");

            var vm = new IZVm(program, new MemoryDeviceHost());

            for (int tick = 1; tick <= 5; tick++)
            {
                Assert.Equal(ExecutionResult.Yielded, vm.Run(1000));
                // One turn of the loop per tick, despite the leftover budget.
                Assert.Equal((double)tick, vm.GetGlobal(0));
            }
        }

        [Fact]
        public void PreemptionInTheMiddleOfACallPreservesTheFrame()
        {
            // The cut falls inside the recursion; the frames have to survive intact.
            var program = TestHost.CompileOk(
                "device out = d0;\n" +
                "fn fact(n: num) -> num {\n" +
                "    if n <= 1 { return 1; }\n" +
                "    return n * fact(n - 1);\n" +
                "}\n" +
                "fn main() { out.Setting = fact(12); }\n");

            var host = new MemoryDeviceHost();
            host.Connect(0);
            var vm = new IZVm(program, host);

            int ticks = 0;
            while (vm.Run(7) == ExecutionResult.BudgetExhausted && ticks < 100000) ticks++;

            Assert.True(ticks > 5, "a budget of 7 should force several ticks");
            Assert.Equal(ExecutionResult.Halted, vm.State);
            Assert.Equal(479001600.0, host.Writes.Last().Value);      // 12!
        }

        [Fact]
        public void SleepSuspendsUntilTheTimePasses()
        {
            var program = TestHost.CompileOk(
                "device out = d0;\n" +
                "fn main() {\n" +
                "    out.Setting = 1;\n" +
                "    sleep(5);\n" +
                "    out.Setting = 2;\n" +
                "}\n");

            var host = new MemoryDeviceHost { CurrentTime = 100.0 };
            host.Connect(0);
            var vm = new IZVm(program, host);

            Assert.Equal(ExecutionResult.Sleeping, vm.Run(100));
            Assert.Equal(105.0, vm.WakeTime);
            Assert.Single(host.Writes);

            // Before the time is up it keeps sleeping and spends no instruction.
            host.CurrentTime = 104.9;
            Assert.Equal(ExecutionResult.Sleeping, vm.Run(100));
            Assert.Single(host.Writes);

            host.CurrentTime = 105.0;
            Assert.Equal(ExecutionResult.Halted, vm.Run(100));
            Assert.Equal(2, host.Writes.Count);
            Assert.Equal(2.0, host.Writes.Last().Value);
        }

        [Fact]
        public void ResetGoesBackToTheStart()
        {
            var program = TestHost.CompileOk(
                "var n = 0;\n" +
                "fn main() { loop { n += 1; } }\n");

            var vm = new IZVm(program, new MemoryDeviceHost());
            vm.Run(100);
            Assert.True(vm.GetGlobal(0) > 0);

            vm.Reset();
            Assert.Equal(0.0, vm.GetGlobal(0));
            Assert.Equal(0L, vm.TotalInstructions);
            Assert.Equal(0, vm.StackDepth);
        }

        [Fact]
        public void CallStackOverflowBecomesARuntimeError()
        {
            var program = TestHost.CompileOk(
                "fn endless(n: num) -> num { return endless(n + 1); }\n" +
                "fn main() { endless(0); }\n");

            var vm = new IZVm(program, new MemoryDeviceHost());
            var state = TestHost.RunToCompletion(vm, maxTicks: 1000);

            Assert.Equal(ExecutionResult.Error, state);
            Assert.Equal(RuntimeErrorKind.CallStackOverflow, vm.Error!.Kind);
        }

        [Fact]
        public void AfterHaltingItRunsNoFurther()
        {
            var program = TestHost.CompileOk("fn main() { }\n");
            var vm = new IZVm(program, new MemoryDeviceHost());

            Assert.Equal(ExecutionResult.Halted, TestHost.RunToCompletion(vm));
            long instructions = vm.TotalInstructions;

            vm.Run(1000);
            Assert.Equal(instructions, vm.TotalInstructions);
        }

        [Fact]
        public void AfterAnErrorItRunsNoFurther()
        {
            var program = TestHost.CompileOk(
                "device pump = d0;\n" +
                "fn main() { loop { pump.Setting = 1; } }\n");

            var vm = new IZVm(program, new MemoryDeviceHost());
            Assert.Equal(ExecutionResult.Error, TestHost.RunToCompletion(vm));

            long instructions = vm.TotalInstructions;
            vm.Run(1000);
            Assert.Equal(instructions, vm.TotalInstructions);
        }
    }
}
