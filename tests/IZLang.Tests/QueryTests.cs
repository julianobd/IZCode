using IZLang.Diagnostics;
using IZLang.Vm;
using Xunit;

namespace IZLang.Tests
{
    /// <summary>
    /// Lists and the query methods over them: the loop the compiler writes in place
    /// of a chain, and the rules that keep a query from costing more than the loop
    /// it replaces.
    /// </summary>
    public class QueryTests
    {
        /// <summary>Runs a body that writes to d0.Setting and gives back the last value written.</summary>
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

        /// <summary>A list of eight numbers holding 10, 20, 30, 40.</summary>
        private const string Numbers =
            "var xs: list num[8] = [10, 20, 30, 40];\n";

        // ------------------------------------------------------------------
        //  The list itself
        // ------------------------------------------------------------------

        [Fact]
        public void AListStartsEmpty()
        {
            Assert.Equal(0.0, Run("var xs: list num[8];\nout.Setting = xs.count;"));
        }

        [Fact]
        public void ALiteralFillsTheFirstItemsAndLeavesTheRestAsRoom()
        {
            Assert.Equal(4.0, Run(Numbers + "out.Setting = xs.count;"));
            Assert.Equal(8.0, Run(Numbers + "out.Setting = len(xs);"));
        }

        [Fact]
        public void ItemsAreReadByIndex()
        {
            Assert.Equal(30.0, Run(Numbers + "out.Setting = xs[2];"));
        }

        [Fact]
        public void ItemsAreWrittenByIndex()
        {
            Assert.Equal(99.0, Run(Numbers + "xs[1] = 99;\nout.Setting = xs[1];"));
        }

        [Fact]
        public void AddAppendsAndMovesTheCountUp()
        {
            Assert.Equal(50.0, Run(
                "var xs: list num[4];\n" +
                "xs.add(50);\n" +
                "out.Setting = xs[0] + xs.count - 1;"));
        }

        [Fact]
        public void AddAnswersFalseWhenTheListIsFull()
        {
            Assert.Equal(0.0, Run(
                "var xs: list num[2] = [1, 2];\n" +
                "var ok = xs.add(3);\n" +
                "out.Setting = ok;"));
            Assert.Equal(2.0, Run(
                "var xs: list num[2] = [1, 2];\n" +
                "xs.add(3);\n" +
                "out.Setting = xs.count;"));
        }

        [Fact]
        public void ReadingPastTheCountIsARuntimeError()
        {
            // The cells exist - it is a list of eight - but four of them are room,
            // not content, and that is the whole difference from an array.
            var program = TestHost.CompileOk(
                "device out = d0;\n" +
                "fn main() {\n" + Numbers + "out.Setting = xs[5];\n}\n");

            var host = new MemoryDeviceHost();
            host.Connect(0);
            var vm = new IZVm(program, host);
            TestHost.RunToCompletion(vm);

            Assert.Equal(ExecutionResult.Error, vm.State);
            Assert.Equal(RuntimeErrorKind.IndexOutOfRange, vm.Error!.Kind);
        }

        [Fact]
        public void ClearEmptiesTheList()
        {
            Assert.Equal(0.0, Run(Numbers + "xs.clear();\nout.Setting = xs.count;"));
        }

        [Fact]
        public void RemoveAtKeepsTheOrder()
        {
            Assert.Equal(30.0, Run(Numbers + "xs.removeAt(1);\nout.Setting = xs[1];"));
            Assert.Equal(3.0, Run(Numbers + "xs.removeAt(1);\nout.Setting = xs.count;"));
            Assert.Equal(40.0, Run(Numbers + "xs.removeAt(0);\nout.Setting = xs[2];"));
        }

        [Fact]
        public void RemoveAtAnswersFalseOutsideTheList()
        {
            Assert.Equal(0.0, Run(Numbers + "out.Setting = xs.removeAt(9);"));
            Assert.Equal(4.0, Run(Numbers + "xs.removeAt(9);\nout.Setting = xs.count;"));
        }

        [Fact]
        public void TheCountCannotBeAssigned()
        {
            TestHost.CompileError(
                "device out = d0;\nfn main() { var xs: list num[4]; xs.count = 2; out.Setting = 1; }",
                IZErrorCode.InvalidAssignmentTarget);
        }

        // ------------------------------------------------------------------
        //  Terminals
        // ------------------------------------------------------------------

        [Fact]
        public void SumAndAverage()
        {
            Assert.Equal(100.0, Run(Numbers + "out.Setting = xs.sum();"));
            Assert.Equal(25.0, Run(Numbers + "out.Setting = xs.avg();"));
        }

        [Fact]
        public void TheAverageOfNothingIsZero()
        {
            Assert.Equal(0.0, Run("var xs: list num[4];\nout.Setting = xs.avg();"));
        }

        [Fact]
        public void CountAndCountWithATest()
        {
            Assert.Equal(4.0, Run(Numbers + "out.Setting = xs.count();"));
            Assert.Equal(2.0, Run(Numbers + "out.Setting = xs.count(x => x > 20);"));
        }

        [Fact]
        public void MinAndMax()
        {
            Assert.Equal(10.0, Run(Numbers + "out.Setting = xs.min();"));
            Assert.Equal(40.0, Run(Numbers + "out.Setting = xs.max();"));
        }

        [Fact]
        public void AnyAndAll()
        {
            Assert.Equal(1.0, Run(Numbers + "out.Setting = xs.any(x => x > 35);"));
            Assert.Equal(0.0, Run(Numbers + "out.Setting = xs.any(x => x > 45);"));
            Assert.Equal(1.0, Run(Numbers + "out.Setting = xs.all(x => x >= 10);"));
            Assert.Equal(0.0, Run(Numbers + "out.Setting = xs.all(x => x >= 20);"));
            Assert.Equal(0.0, Run("var xs: list num[4];\nout.Setting = xs.any();"));
        }

        [Fact]
        public void FirstAndLast()
        {
            Assert.Equal(10.0, Run(Numbers + "out.Setting = xs.first();"));
            Assert.Equal(40.0, Run(Numbers + "out.Setting = xs.last();"));
            Assert.Equal(30.0, Run(Numbers + "out.Setting = xs.first(x => x > 20);"));
        }

        [Fact]
        public void FirstOfNothingStopsTheChip()
        {
            var program = TestHost.CompileOk(
                "device out = d0;\n" +
                "fn main() {\n" + Numbers + "out.Setting = xs.first(x => x > 100);\n}\n");

            var host = new MemoryDeviceHost();
            host.Connect(0);
            var vm = new IZVm(program, host);
            TestHost.RunToCompletion(vm);

            Assert.Equal(ExecutionResult.Error, vm.State);
            Assert.Equal(RuntimeErrorKind.EmptySequence, vm.Error!.Kind);
        }

        [Fact]
        public void FirstOrAnswersWithTheFallback()
        {
            Assert.Equal(-1.0, Run(Numbers + "out.Setting = xs.where(x => x > 100).firstOr(-1);"));
            Assert.Equal(10.0, Run(Numbers + "out.Setting = xs.firstOr(-1);"));
            Assert.Equal(40.0, Run(Numbers + "out.Setting = xs.lastOr(-1);"));
        }

        [Fact]
        public void ContainsAndIndexOf()
        {
            Assert.Equal(1.0, Run(Numbers + "out.Setting = xs.contains(30);"));
            Assert.Equal(0.0, Run(Numbers + "out.Setting = xs.contains(35);"));
            Assert.Equal(2.0, Run(Numbers + "out.Setting = xs.indexOf(30);"));
            Assert.Equal(-1.0, Run(Numbers + "out.Setting = xs.indexOf(35);"));
        }

        // ------------------------------------------------------------------
        //  Stages
        // ------------------------------------------------------------------

        [Fact]
        public void WhereFilters()
        {
            Assert.Equal(70.0, Run(Numbers + "out.Setting = xs.where(x => x > 20).sum();"));
        }

        [Fact]
        public void SelectProjects()
        {
            Assert.Equal(10.0, Run(Numbers + "out.Setting = xs.select(x => x / 10).sum();"));
        }

        [Fact]
        public void TakeAndSkip()
        {
            Assert.Equal(30.0, Run(Numbers + "out.Setting = xs.take(2).sum();"));
            Assert.Equal(70.0, Run(Numbers + "out.Setting = xs.skip(2).sum();"));
            Assert.Equal(30.0, Run(Numbers + "out.Setting = xs.skip(2).take(1).first();"));
        }

        [Fact]
        public void TakeWhileAndSkipWhile()
        {
            Assert.Equal(30.0, Run(Numbers + "out.Setting = xs.takeWhile(x => x < 30).sum();"));
            Assert.Equal(70.0, Run(Numbers + "out.Setting = xs.skipWhile(x => x < 30).sum();"));

            // skipWhile stops skipping for good: the 10 in the middle stays in.
            Assert.Equal(40.0, Run(
                "var xs: list num[4] = [1, 30, 10];\n" +
                "out.Setting = xs.skipWhile(x => x < 10).sum();"));
        }

        [Fact]
        public void StagesChainInOrder()
        {
            Assert.Equal(25.0, Run(Numbers + "out.Setting = xs.where(x => x > 15).take(2).avg();"));
        }

        // ------------------------------------------------------------------
        //  Ordering
        // ------------------------------------------------------------------

        private const string Unsorted = "var xs: list num[8] = [30, 10, 40, 20];\n";

        [Fact]
        public void OrderByAscendingAndDescending()
        {
            Assert.Equal(10.0, Run(Unsorted + "out.Setting = xs.orderBy(x => x).first();"));
            Assert.Equal(40.0, Run(Unsorted + "out.Setting = xs.orderByDesc(x => x).first();"));
            Assert.Equal(20.0, Run(Unsorted + "out.Setting = xs.orderBy(x => x).skip(1).first();"));
            Assert.Equal(30.0, Run(Unsorted + "out.Setting = xs.orderByDesc(x => x).skip(1).first();"));
        }

        [Fact]
        public void OrderingLeavesTheSourceAlone()
        {
            // The query works on cells of its own, so the list it read is untouched.
            Assert.Equal(30.0, Run(Unsorted + "var s = xs.orderBy(x => x);\nout.Setting = xs[0];"));
        }

        [Fact]
        public void ReverseWalksItBackwards()
        {
            Assert.Equal(40.0, Run(Numbers + "out.Setting = xs.reverse().first();"));
            Assert.Equal(10.0, Run(Numbers + "out.Setting = xs.reverse().last();"));
            Assert.Equal(30.0, Run(Numbers + "out.Setting = xs.where(x => x < 40).reverse().first();"));
        }

        [Fact]
        public void DistinctKeepsTheFirstOfEach()
        {
            Assert.Equal(3.0, Run(
                "var xs: list num[8] = [5, 7, 5, 9, 7];\n" +
                "out.Setting = xs.distinct().count();"));
            Assert.Equal(21.0, Run(
                "var xs: list num[8] = [5, 7, 5, 9, 7];\n" +
                "out.Setting = xs.distinct().sum();"));
        }

        [Fact]
        public void OrderingIsStable()
        {
            // Two jobs with the same priority keep the order they were added in.
            Assert.Equal(2.0, Run(
                "push(1, 5); push(2, 1); push(3, 5);\n" +
                "out.Setting = jobs.orderBy(j => j.priority).first().id;",
                "struct Job { id: num; priority: num; }\n" +
                "var jobs: list Job[4];\n" +
                "fn push(id: num, priority: num) {\n" +
                "    var job: Job;\n" +
                "    job.id = id;\n" +
                "    job.priority = priority;\n" +
                "    jobs.add(job);\n" +
                "}"));
        }

        // ------------------------------------------------------------------
        //  Lists of structs
        // ------------------------------------------------------------------

        private const string Jobs =
            "struct Job { id: num; temp: num; done: bool; }\n" +
            "var jobs: list Job[4];\n" +
            "fn push(id: num, temp: num, done: bool) {\n" +
            "    var job: Job;\n" +           // cleared again on every call
            "    job.id = id;\n" +
            "    job.temp = temp;\n" +
            "    job.done = done;\n" +
            "    jobs.add(job);\n" +          // the list keeps a copy of the cells
            "}\n" +
            "fn seed() {\n" +
            "    push(1, 40, true);\n" +
            "    push(2, 10, false);\n" +
            "    push(3, 25, false);\n" +
            "}\n";

        [Fact]
        public void AStructGoesIntoAListByValue()
        {
            Assert.Equal(3.0, Run("seed();\nout.Setting = jobs.count;", Jobs));
            Assert.Equal(10.0, Run("seed();\nout.Setting = jobs[1].temp;", Jobs));

            // A copy of the cells: filling the same variable again for the next
            // item does not reach back into the one already in the list.
            Assert.Equal(1.0, Run(
                "var job: Job;\n" +
                "job.id = 1;\n" +
                "jobs.add(job);\n" +
                "job.id = 2;\n" +
                "out.Setting = jobs[0].id;", Jobs));
        }

        [Fact]
        public void RemoveTakesOutTheFirstItemEqualToTheValue()
        {
            Assert.Equal(1.0, Run(Numbers + "out.Setting = xs.remove(20);"));
            Assert.Equal(30.0, Run(Numbers + "xs.remove(20);\nout.Setting = xs[1];"));
            Assert.Equal(3.0, Run(Numbers + "xs.remove(20);\nout.Setting = xs.count;"));

            // Only the first one: the second 7 is still there.
            Assert.Equal(7.0, Run(
                "var xs: list num[4] = [7, 7, 9];\nxs.remove(7);\nout.Setting = xs[0];"));
            Assert.Equal(2.0, Run(
                "var xs: list num[4] = [7, 7, 9];\nxs.remove(7);\nout.Setting = xs.count;"));

            // False when it is not in there at all.
            Assert.Equal(0.0, Run(Numbers + "out.Setting = xs.remove(99);"));
            Assert.Equal(4.0, Run(Numbers + "xs.remove(99);\nout.Setting = xs.count;"));
        }

        [Fact]
        public void RemoveNeedsSomethingItCanCompare()
        {
            TestHost.CompileError(
                "device out = d0; " +
                "struct P { x: num; } " +
                "fn main() { var ps: list P[4]; var p: P; ps.add(p); " +
                "out.Setting = ps.remove(p); }",
                IZErrorCode.TypeMismatch);
        }

        [Fact]
        public void AddChecksWhatItIsGiven()
        {
            TestHost.CompileError(
                "device out = d0; " +
                "fn main() { var xs: list num[4]; xs.add(\"nope\"); out.Setting = 1; }",
                IZErrorCode.TypeMismatch);

            TestHost.CompileError(
                "device out = d0; " +
                "fn main() { var xs: list num[4]; xs.add(); out.Setting = 1; }",
                IZErrorCode.WrongArgumentCount);
        }

        [Fact]
        public void QueriesReachIntoTheFields()
        {
            Assert.Equal(75.0, Run("seed();\nout.Setting = jobs.sum(x => x.temp);", Jobs));
            Assert.Equal(2.0, Run("seed();\nout.Setting = jobs.count(x => !x.done);", Jobs));
            Assert.Equal(25.0, Run("seed();\nout.Setting = jobs.avg(x => x.temp);", Jobs));
        }

        [Fact]
        public void ATerminalCanHandBackTheItemItself()
        {
            Assert.Equal(2.0, Run("seed();\nout.Setting = jobs.first(x => !x.done).id;", Jobs));

            // ordered by temp: 10 (id 2), 25 (id 3), 40 (id 1)
            Assert.Equal(2.0, Run("seed();\nout.Setting = jobs.orderBy(x => x.temp).first().id;", Jobs));
            Assert.Equal(1.0, Run("seed();\nout.Setting = jobs.orderBy(x => x.temp).last().id;", Jobs));
            Assert.Equal(3.0, Run("seed();\nout.Setting = jobs.orderBy(x => x.temp).skip(1).first().id;", Jobs));
        }

        [Fact]
        public void AQueryResultIsAListOfItsOwn()
        {
            Assert.Equal(2.0, Run(
                "seed();\n" +
                "var open = jobs.where(x => !x.done);\n" +
                "out.Setting = open.count;", Jobs));

            // A copy, not a view: writing into the result leaves the source alone.
            Assert.Equal(0.0, Run(
                "seed();\n" +
                "var open = jobs.where(x => !x.done);\n" +
                "open[0].done = true;\n" +
                "out.Setting = jobs[1].done;", Jobs));
        }

        // ------------------------------------------------------------------
        //  into
        // ------------------------------------------------------------------

        [Fact]
        public void IntoReplacesTheContentsOfAnExistingList()
        {
            Assert.Equal(2.0, Run(
                Numbers +
                "var big: list num[8];\n" +
                "out.Setting = xs.where(x => x > 20).into(big);"));

            Assert.Equal(70.0, Run(
                Numbers +
                "var big: list num[8];\n" +
                "xs.where(x => x > 20).into(big);\n" +
                "out.Setting = big.sum();"));
        }

        [Fact]
        public void IntoStopsAtTheRoomTheTargetHas()
        {
            Assert.Equal(2.0, Run(
                Numbers +
                "var small: list num[2];\n" +
                "out.Setting = xs.into(small);"));
        }

        // ------------------------------------------------------------------
        //  Arrays and text
        // ------------------------------------------------------------------

        [Fact]
        public void AnArrayIsAListThatIsAlwaysFull()
        {
            Assert.Equal(6.0, Run("var a = [1, 2, 3];\nout.Setting = a.sum();"));
            Assert.Equal(2.0, Run("var a = [1, 2, 3];\nout.Setting = a.avg();"));
            Assert.Equal(3.0, Run("var a: num[3];\nout.Setting = a.count();"));
        }

        [Fact]
        public void QueriesWorkOverText()
        {
            Assert.Equal(1.0, Run(
                "var names: list str[4] = [\"north\", \"south\"];\n" +
                "out.Setting = names.contains(\"south\");"));

            Assert.Equal(1.0, Run(
                "var names: list str[4] = [\"north\", \"south\"];\n" +
                "out.Setting = names.orderBy(s => s).first() == \"north\";"));

            Assert.Equal(1.0, Run(
                "var names: list str[4] = [\"north\", \"south\"];\n" +
                "out.Setting = names.select(s => \"vent-\" + s).contains(\"vent-north\");"));
        }

        // ------------------------------------------------------------------
        //  Lists in functions
        // ------------------------------------------------------------------

        [Fact]
        public void AListTravelsAsAParameter()
        {
            Assert.Equal(100.0, Run(
                Numbers + "out.Setting = total(xs);",
                "fn total(l: list num[8]) -> num { return l.sum(); }"));
        }

        [Fact]
        public void AFunctionWritesIntoTheCallersList()
        {
            Assert.Equal(7.0, Run(
                "var xs: list num[4];\nfill(xs);\nout.Setting = xs.sum();",
                "fn fill(l: list num[4]) { l.add(3); l.add(4); }"));
        }

        [Fact]
        public void TwoCapacitiesAreTwoTypes()
        {
            TestHost.CompileError(
                "device out = d0;\n" +
                "fn total(l: list num[8]) -> num { return l.sum(); }\n" +
                "fn main() { var xs: list num[4]; out.Setting = total(xs); }",
                IZErrorCode.TypeMismatch);
        }

        [Fact]
        public void AQueryInsideALoopReusesItsCells()
        {
            // The cells a query reserves belong to the frame, and are cleared again
            // on every lap: the result must not grow from one tick to the next.
            Assert.Equal(2.0, Run(
                Numbers +
                "var n = 0.0;\n" +
                "for i in 0..3 {\n" +
                "    var big = xs.where(x => x > 20);\n" +
                "    n = big.count;\n" +
                "}\n" +
                "out.Setting = n;"));
        }

        // ------------------------------------------------------------------
        //  What the compiler refuses
        // ------------------------------------------------------------------

        [Fact]
        public void AQueryNeedsAListOrAnArray()
        {
            TestHost.CompileError(
                "device out = d0;\nfn main() { var x = 3; out.Setting = x.sum(); }",
                IZErrorCode.TypeMismatch);
        }

        [Fact]
        public void ATerminalEndsTheChain()
        {
            TestHost.CompileError(
                "device out = d0;\n" +
                "fn main() { var xs: list num[4]; out.Setting = xs.sum().where(x => x > 1); }",
                IZErrorCode.TypeMismatch);
        }

        [Fact]
        public void SumNeedsNumbers()
        {
            TestHost.CompileError(
                "device out = d0;\n" +
                "struct P { x: num; }\n" +
                "fn main() { var ps: list P[4]; out.Setting = ps.sum(); }",
                IZErrorCode.TypeMismatch);
        }

        [Fact]
        public void ALambdaIsNotAValue()
        {
            TestHost.CompileError(
                "device out = d0;\nfn main() { var f = x => x + 1; out.Setting = 1; }",
                IZErrorCode.ExpectedToken);
        }

        [Fact]
        public void AListHoldsScalarsOrStructs()
        {
            TestHost.CompileError(
                "device out = d0;\nfn main() { var xs: list num[2][3]; out.Setting = 1; }",
                IZErrorCode.TypeMismatch);
        }

        [Fact]
        public void AListNeedsItsRoomSaidOutLoud()
        {
            TestHost.CompileError(
                "device out = d0;\nfn main() { var xs: list num; out.Setting = 1; }",
                IZErrorCode.InvalidArrayLength);
        }

        [Fact]
        public void IntoRefusesToFillAListFromItself()
        {
            TestHost.CompileError(
                "device out = d0; " +
                "fn main() { var xs: list num[4]; out.Setting = xs.where(x => x > 1).into(xs); }",
                IZErrorCode.InvalidAssignmentTarget);
        }

        [Fact]
        public void AWholeListIsNotAssigned()
        {
            TestHost.CompileError(
                "device out = d0;\n" +
                "fn main() { var a: list num[2]; var b: list num[2]; a = b; out.Setting = 1; }",
                IZErrorCode.InvalidAssignmentTarget);
        }
    }
}
