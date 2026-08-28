using IZLang.Vm;
using Xunit;

namespace IZLang.Tests
{
    /// <summary>
    /// The corners of a query: empty sources, chains that nest inside each other,
    /// cells that have to be reused, and a query cut in half by the tick budget.
    /// </summary>
    public class QueryEdgeTests
    {
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

        private const string Numbers = "var xs: list num[8] = [10, 20, 30, 40];\n";

        // ------------------------------------------------------------------
        //  Nothing to walk
        // ------------------------------------------------------------------

        [Fact]
        public void EveryTerminalHasAnAnswerForAnEmptyList()
        {
            const string empty = "var xs: list num[4];\n";

            Assert.Equal(0.0, Run(empty + "out.Setting = xs.count();"));
            Assert.Equal(0.0, Run(empty + "out.Setting = xs.sum();"));
            Assert.Equal(0.0, Run(empty + "out.Setting = xs.avg();"));
            Assert.Equal(0.0, Run(empty + "out.Setting = xs.min();"));
            Assert.Equal(0.0, Run(empty + "out.Setting = xs.max();"));
            Assert.Equal(0.0, Run(empty + "out.Setting = xs.any();"));
            Assert.Equal(0.0, Run(empty + "out.Setting = xs.contains(1);"));
            Assert.Equal(-1.0, Run(empty + "out.Setting = xs.indexOf(1);"));
            Assert.Equal(7.0, Run(empty + "out.Setting = xs.firstOr(7);"));

            // 'all' over nothing is true, the way an empty conjunction always is.
            Assert.Equal(1.0, Run(empty + "out.Setting = xs.all(x => x > 100);"));
        }

        [Fact]
        public void SortingAndDistinctSurviveAnEmptyList()
        {
            Assert.Equal(0.0, Run("var xs: list num[4];\nout.Setting = xs.orderBy(x => x).count();"));
            Assert.Equal(0.0, Run("var xs: list num[4];\nout.Setting = xs.distinct().count();"));
            Assert.Equal(0.0, Run("var xs: list num[4];\nout.Setting = xs.reverse().count();"));
        }

        [Fact]
        public void SortingOneItemIsStillSorting()
        {
            Assert.Equal(5.0, Run("var xs: list num[4] = [5];\nout.Setting = xs.orderBy(x => x).first();"));
        }

        [Fact]
        public void TakeAndSkipGoPastTheEndsWithoutComplaining()
        {
            Assert.Equal(0.0, Run(Numbers + "out.Setting = xs.take(0).count();"));
            Assert.Equal(4.0, Run(Numbers + "out.Setting = xs.take(99).count();"));
            Assert.Equal(0.0, Run(Numbers + "out.Setting = xs.skip(99).count();"));
            Assert.Equal(0.0, Run(Numbers + "out.Setting = xs.skip(2).take(0).count();"));
        }

        // ------------------------------------------------------------------
        //  Chains inside chains
        // ------------------------------------------------------------------

        [Fact]
        public void AQueryRunsInsideTheLambdaOfAnother()
        {
            Assert.Equal(2.0, Run(
                "var xs: list num[4] = [10, 20, 30];\n" +
                "var ys: list num[4] = [20, 30, 99];\n" +
                "out.Setting = xs.count(x => ys.contains(x));"));
        }

        [Fact]
        public void AQueryRunsInsideTheLambdaOfASort()
        {
            // The key of each element is itself a query over another list.
            Assert.Equal(30.0, Run(
                "var xs: list num[4] = [10, 30, 20];\n" +
                "var w: list num[4] = [3, 1, 2];\n" +
                "out.Setting = xs.orderByDesc(x => w.take(x / 10).sum()).first();"));
        }

        [Fact]
        public void BlockingStagesChainWithEachOther()
        {
            Assert.Equal(9.0, Run(
                "var xs: list num[8] = [5, 9, 5, 7, 9];\n" +
                "out.Setting = xs.distinct().orderBy(x => x).last();"));

            Assert.Equal(5.0, Run(
                "var xs: list num[8] = [5, 9, 5, 7, 9];\n" +
                "out.Setting = xs.distinct().orderByDesc(x => x).last();"));

            Assert.Equal(3.0, Run(
                "var xs: list num[8] = [5, 9, 5, 7, 9];\n" +
                "out.Setting = xs.orderBy(x => x).distinct().count();"));
        }

        [Fact]
        public void ReverseTwiceIsTheOrderItStartedIn()
        {
            Assert.Equal(10.0, Run(Numbers + "out.Setting = xs.reverse().reverse().first();"));
        }

        [Fact]
        public void SelectFeedsTheStagesThatFollowIt()
        {
            Assert.Equal(2.0, Run(
                "var xs: list num[8] = [11, 21, 12, 22];\n" +
                "out.Setting = xs.select(x => x % 10).distinct().count();"));

            Assert.Equal(22.0, Run(
                "var xs: list num[8] = [11, 21, 12, 22];\n" +
                "out.Setting = xs.select(x => x + 0).orderBy(x => x).last();"));
        }

        [Fact]
        public void AStageAfterASortSeesTheSortedOrder()
        {
            Assert.Equal(30.0, Run(
                "var xs: list num[8] = [30, 10, 40, 20];\n" +
                "out.Setting = xs.orderBy(x => x).skip(2).first();"));

            Assert.Equal(70.0, Run(
                "var xs: list num[8] = [30, 10, 40, 20];\n" +
                "out.Setting = xs.orderByDesc(x => x).take(2).sum();"));
        }

        // ------------------------------------------------------------------
        //  Cells
        // ------------------------------------------------------------------

        [Fact]
        public void TwoQueriesInARowDoNotShareCells()
        {
            Assert.Equal(3.0, Run(
                Numbers +
                "var a = xs.where(x => x > 15);\n" +
                "var b = xs.where(x => x > 25);\n" +
                "out.Setting = a.count - b.count + 2;"));
        }

        [Fact]
        public void AQueryResultOutlivesTheStatementThatBuiltIt()
        {
            Assert.Equal(90.0, Run(
                Numbers +
                "var big = xs.where(x => x > 15);\n" +
                "var total = 0.0;\n" +
                "for i in 0..big.count { total += big[i]; }\n" +
                "out.Setting = total;"));
        }

        [Fact]
        public void AGlobalListKeepsAQueryResultAcrossTicks()
        {
            // The cells a query reserves belong to the frame that ran it, so 'into'
            // is how a result reaches a list that lives longer than the tick.
            Assert.Equal(70.0, Run(
                "xs.where(x => x > 20).into(kept);\n" +
                "yield;\n" +
                "out.Setting = kept.sum();",
                Numbers + "var kept: list num[8];"));
        }

        [Fact]
        public void ARecursiveCallGetsItsOwnCells()
        {
            Assert.Equal(6.0, Run(
                "out.Setting = walk(3);",
                "fn walk(n: num) -> num {\n" +
                "    if n <= 0 { return 0; }\n" +
                "    var mine: list num[4];\n" +
                "    mine.add(n);\n" +
                "    var deeper = walk(n - 1);\n" +
                "    return mine.sum() + deeper;\n" +   // mine must survive the call
                "}"));
        }

        [Fact]
        public void AQueryWorksInAGlobalInitializer()
        {
            Assert.Equal(60.0, Run("out.Setting = total;",
                "var xs: list num[4] = [10, 20, 30];\nvar total = xs.sum();"));
        }

        // ------------------------------------------------------------------
        //  Preemption
        // ------------------------------------------------------------------

        [Fact]
        public void AQueryCutInHalfByTheBudgetResumesWhereItStopped()
        {
            // Everything a query holds lives in locals and in the heap, which is
            // exactly the state the VM freezes: five instructions per tick has to
            // give the same answer as running it in one go.
            var program = TestHost.CompileOk(
                "device out = d0;\n" +
                "fn main() {\n" +
                "    var xs: list num[16];\n" +
                "    for i in 0..16 { xs.add(i); }\n" +
                "    out.Setting = xs.orderByDesc(x => x % 5).where(x => x > 3).take(4).sum();\n" +
                "}\n");

            var host = new MemoryDeviceHost();
            host.Connect(0);

            var whole = new IZVm(program, host, randomSeed: 1);
            TestHost.RunToCompletion(whole);
            double expected = host.Writes[host.Writes.Count - 1].Value;

            var piecemeal = new IZVm(program, host, randomSeed: 1);
            TestHost.RunToCompletion(piecemeal, maxTicks: 100000, budget: 5);

            Assert.Equal(ExecutionResult.Halted, piecemeal.State);
            Assert.Equal(expected, host.Writes[host.Writes.Count - 1].Value);
        }

        // ------------------------------------------------------------------
        //  What it costs
        // ------------------------------------------------------------------

        /// <summary>Runs a program and answers how many instructions it took.</summary>
        private static long Cost(string body)
        {
            var program = TestHost.CompileOk(
                "device out = d0;\n" +
                "fn main() {\n" +
                "    var xs: list num[16];\n" +
                "    for i in 0..16 { xs.add(i); }\n" +
                body + "\n}\n");

            var host = new MemoryDeviceHost();
            host.Connect(0);
            var vm = new IZVm(program, host);
            Assert.Equal(ExecutionResult.Halted, TestHost.RunToCompletion(vm));
            return vm.TotalInstructions;
        }

        [Fact]
        public void AChainCostsWhatTheLoopWouldHaveCost()
        {
            // The claim the documentation makes: a chain is the loop you would have
            // written, so it has to stay within reach of the hand written one. A
            // second list between one method and the next would show up here.
            long byHand = Cost(
                "var total = 0.0;\n" +
                "for i in 0..xs.count {\n" +
                "    if xs[i] > 5 { total += xs[i]; }\n" +
                "}\n" +
                "out.Setting = total;");

            long byQuery = Cost("out.Setting = xs.where(x => x > 5).sum();");

            Assert.True(byQuery < byHand * 2,
                "the query took " + byQuery + " instructions and the loop " + byHand);
        }

        [Fact]
        public void ATerminalThatHasItsAnswerStopsWalking()
        {
            // 'first' over a list of sixteen must not read the other fifteen.
            long all = Cost("out.Setting = xs.sum();");
            long one = Cost("out.Setting = xs.first();");

            Assert.True(one < all, "first() took " + one + " and sum() took " + all);
        }

        // ------------------------------------------------------------------
        //  Text
        // ------------------------------------------------------------------

        [Fact]
        public void TextSortsByItsOrdinalOrder()
        {
            Assert.Equal(1.0, Run(
                "var names: list str[4] = [\"pump\", \"Alpha\", \"vent\"];\n" +
                "out.Setting = names.orderBy(s => s).first() == \"Alpha\";"));

            Assert.Equal(1.0, Run(
                "var names: list str[4] = [\"pump\", \"alpha\", \"vent\"];\n" +
                "out.Setting = names.orderByDesc(s => s).first() == \"vent\";"));
        }

        [Fact]
        public void TextIsComparedByItsCharactersAndNotByItsStorage()
        {
            // The two strings are built separately, so this only passes if 'distinct'
            // and 'contains' compare the text.
            Assert.Equal(2.0, Run(
                "var side = \"north\";\n" +
                "var names: list str[4] = [\"vent-north\", \"vent-south\"];\n" +
                "names.add(\"vent-\" + side);\n" +
                "out.Setting = names.distinct().count();"));
        }

        [Fact]
        public void AStringKeyedSortStillOrdersTheStructs()
        {
            Assert.Equal(2.0, Run(
                "push(1, \"b\"); push(2, \"a\");\n" +
                "out.Setting = rooms.orderBy(r => r.name).first().id;",
                "struct Room { id: num; name: str; }\n" +
                "var rooms: list Room[4];\n" +
                "fn push(id: num, name: str) {\n" +
                "    var room: Room;\n" +
                "    room.id = id;\n" +
                "    room.name = name;\n" +
                "    rooms.add(room);\n" +
                "}"));
        }
    }
}
