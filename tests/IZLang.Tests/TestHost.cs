using System;
using System.Linq;
using IZLang;
using IZLang.Diagnostics;
using IZLang.Vm;
using Xunit;

namespace IZLang.Tests
{
    /// <summary>Shortcuts used by every test: compile, run and inspect.</summary>
    public static class TestHost
    {
        /// <summary>Compiles and fails the test with the formatted diagnostics when it does not pass.</summary>
        public static IZProgram CompileOk(string source)
        {
            var result = IZCompiler.Compile(source);
            Assert.True(result.Success,
                "expected it to compile, but there were errors:\n" + result.FormatDiagnostics());
            return result.Program!;
        }

        /// <summary>Compiles expecting a failure, and returns the first error.</summary>
        public static Diagnostic CompileError(string source, IZErrorCode expected)
        {
            var result = IZCompiler.Compile(source);
            Assert.False(result.Success,
                "expected error " + expected + ", but the program compiled");

            var error = result.Diagnostics.FirstOrDefault(d => d.IsError && d.Code == expected);
            Assert.True(error != null,
                "expected error " + expected + ", but got these:\n" + result.FormatDiagnostics());
            return error!;
        }

        /// <summary>
        /// Runs until it stops. Fails the test when it does not finish within
        /// <paramref name="maxTicks"/> - a program that never stops is a bug, not a timeout.
        /// </summary>
        public static ExecutionResult RunToCompletion(IZVm vm, int maxTicks = 1000,
                                                      int budget = IZLimits.DefaultOpsPerTick)
        {
            for (int tick = 0; tick < maxTicks; tick++)
            {
                var state = vm.Run(budget);
                if (state == ExecutionResult.Halted || state == ExecutionResult.Error)
                    return state;
            }
            Assert.Fail("the program did not finish in " + maxTicks + " ticks");
            return ExecutionResult.Error;
        }

        /// <summary>Compiles, runs to the end, and returns the host with the recorded writes.</summary>
        public static MemoryDeviceHost Execute(string source, Action<MemoryDeviceHost>? setup = null,
                                               int maxTicks = 1000)
        {
            var program = CompileOk(source);
            var host = new MemoryDeviceHost();
            setup?.Invoke(host);

            var vm = new IZVm(program, host, randomSeed: 12345);
            var state = RunToCompletion(vm, maxTicks);

            Assert.True(state == ExecutionResult.Halted,
                "expected it to finish without an error, but: " + (vm.Error?.ToString() ?? state.ToString()));
            return host;
        }

        /// <summary>
        /// Runs the program and returns the value it wrote to d0.Setting - the default
        /// output channel of the expression tests.
        /// </summary>
        public static double Eval(string expression, string prelude = "")
        {
            string source =
                "device out = d0;\n" +
                prelude + "\n" +
                "fn main() {\n" +
                "    out.Setting = " + expression + ";\n" +
                "}\n";

            var host = Execute(source, h => h.Connect(0));
            Assert.True(host.Writes.Count > 0, "the program wrote nothing to d0");
            return host.Writes[host.Writes.Count - 1].Value;
        }
    }
}
