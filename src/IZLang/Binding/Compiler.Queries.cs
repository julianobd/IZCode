using System;
using System.Collections.Generic;
using IZLang.Diagnostics;
using IZLang.Parsing;
using IZLang.Vm;

namespace IZLang.Binding
{
    /// <summary>
    /// The query methods a list understands: where, select, orderBy, sum, first...
    ///
    /// None of them exists at runtime. A chain is compiled into a single loop over
    /// the source cells, with every lambda inlined into it: there are no function
    /// pointers in IZ, no closures to build and no intermediate list between one
    /// method and the next. 'xs.where(f).take(3).sum()' walks the cells once.
    ///
    /// Four of them cannot work that way, because they have to see every element
    /// before they can hand the first one over: orderBy, orderByDesc, distinct and
    /// reverse. Those materialize what came before them into a list the compiler
    /// reserves, and the rest of the chain reads the cells it produced.
    /// </summary>
    public sealed partial class Compiler
    {
        /// <summary>Where a method may appear in a chain.</summary>
        private enum QueryCategory
        {
            /// <summary>Transforms elements one at a time, inside the loop.</summary>
            Stage,
            /// <summary>Needs the whole sequence, so it materializes what came before.</summary>
            Blocking,
            /// <summary>Ends the chain with a single value.</summary>
            Terminal,
            /// <summary>Changes the list itself; only ever the whole chain.</summary>
            ListOp,
        }

        private enum QueryKind
        {
            Where, Select, Take, Skip, TakeWhile, SkipWhile,
            OrderBy, OrderByDesc, Reverse, Distinct,
            Count, Sum, Avg, Min, Max, Any, All, First, Last, FirstOr, LastOr,
            Contains, IndexOf, Into,
            Add, Clear, Remove, RemoveAt,
        }

        /// <summary>What the argument of a method is, when it takes one.</summary>
        private enum QueryArgument { None, Lambda, Value }

        private sealed class QueryMethod
        {
            public readonly string Name;
            public readonly QueryKind Kind;
            public readonly QueryCategory Category;
            public readonly QueryArgument Argument;

            /// <summary>Shown whenever the call does not match: a shape beats a rule.</summary>
            public readonly string Usage;

            public QueryMethod(string name, QueryKind kind, QueryCategory category,
                               QueryArgument argument, string usage)
            {
                Name = name;
                Kind = kind;
                Category = category;
                Argument = argument;
                Usage = usage;
            }
        }

        private static readonly Dictionary<string, QueryMethod> QueryMethods = BuildQueryMethods();

        private static Dictionary<string, QueryMethod> BuildQueryMethods()
        {
            var map = new Dictionary<string, QueryMethod>(StringComparer.Ordinal);

            void Add(string name, QueryKind kind, QueryCategory category,
                     QueryArgument argument, string usage) =>
                map[name] = new QueryMethod(name, kind, category, argument, usage);

            Add("where", QueryKind.Where, QueryCategory.Stage, QueryArgument.Lambda,
                "xs.where(x => x.temp > 30)");
            Add("select", QueryKind.Select, QueryCategory.Stage, QueryArgument.Lambda,
                "xs.select(x => x.temp)");
            Add("take", QueryKind.Take, QueryCategory.Stage, QueryArgument.Value,
                "xs.take(3)");
            Add("skip", QueryKind.Skip, QueryCategory.Stage, QueryArgument.Value,
                "xs.skip(3)");
            Add("takeWhile", QueryKind.TakeWhile, QueryCategory.Stage, QueryArgument.Lambda,
                "xs.takeWhile(x => x > 0)");
            Add("skipWhile", QueryKind.SkipWhile, QueryCategory.Stage, QueryArgument.Lambda,
                "xs.skipWhile(x => x == 0)");

            Add("orderBy", QueryKind.OrderBy, QueryCategory.Blocking, QueryArgument.Lambda,
                "xs.orderBy(x => x.temp)");
            Add("orderByDesc", QueryKind.OrderByDesc, QueryCategory.Blocking, QueryArgument.Lambda,
                "xs.orderByDesc(x => x.temp)");
            Add("reverse", QueryKind.Reverse, QueryCategory.Blocking, QueryArgument.None,
                "xs.reverse()");
            Add("distinct", QueryKind.Distinct, QueryCategory.Blocking, QueryArgument.None,
                "xs.distinct()");

            Add("count", QueryKind.Count, QueryCategory.Terminal, QueryArgument.Lambda,
                "xs.count() or xs.count(x => x.done)");
            Add("sum", QueryKind.Sum, QueryCategory.Terminal, QueryArgument.Lambda,
                "xs.sum() or xs.sum(x => x.temp)");
            Add("avg", QueryKind.Avg, QueryCategory.Terminal, QueryArgument.Lambda,
                "xs.avg() or xs.avg(x => x.temp)");
            Add("min", QueryKind.Min, QueryCategory.Terminal, QueryArgument.Lambda,
                "xs.min() or xs.min(x => x.temp)");
            Add("max", QueryKind.Max, QueryCategory.Terminal, QueryArgument.Lambda,
                "xs.max() or xs.max(x => x.temp)");
            Add("any", QueryKind.Any, QueryCategory.Terminal, QueryArgument.Lambda,
                "xs.any() or xs.any(x => x.done)");
            Add("all", QueryKind.All, QueryCategory.Terminal, QueryArgument.Lambda,
                "xs.all(x => x.done)");
            Add("first", QueryKind.First, QueryCategory.Terminal, QueryArgument.Lambda,
                "xs.first() or xs.first(x => x.done)");
            Add("last", QueryKind.Last, QueryCategory.Terminal, QueryArgument.Lambda,
                "xs.last() or xs.last(x => x.done)");
            Add("firstOr", QueryKind.FirstOr, QueryCategory.Terminal, QueryArgument.Value,
                "xs.firstOr(0)");
            Add("lastOr", QueryKind.LastOr, QueryCategory.Terminal, QueryArgument.Value,
                "xs.lastOr(0)");
            Add("contains", QueryKind.Contains, QueryCategory.Terminal, QueryArgument.Value,
                "xs.contains(20)");
            Add("indexOf", QueryKind.IndexOf, QueryCategory.Terminal, QueryArgument.Value,
                "xs.indexOf(20)");
            Add("into", QueryKind.Into, QueryCategory.Terminal, QueryArgument.Value,
                "xs.where(f).into(target)");

            Add("add", QueryKind.Add, QueryCategory.ListOp, QueryArgument.Value,
                "xs.add(10), and jobs.add(job) for a struct");
            Add("clear", QueryKind.Clear, QueryCategory.ListOp, QueryArgument.None,
                "xs.clear()");
            Add("remove", QueryKind.Remove, QueryCategory.ListOp, QueryArgument.Value,
                "xs.remove(20)");
            Add("removeAt", QueryKind.RemoveAt, QueryCategory.ListOp, QueryArgument.Value,
                "xs.removeAt(0)");

            return map;
        }

        /// <summary>One '.method(...)' of a chain, plus the slots its state needs.</summary>
        private sealed class QueryStep
        {
            public QueryMethod Method = null!;
            public MemberExpression Member = null!;
            public List<ExpressionSyntax> Arguments = null!;

            /// <summary>Counter or flag the stage keeps between elements; -1 when it needs none.</summary>
            public int StateSlot = -1;

            /// <summary>The argument of take and skip, evaluated once before the loop.</summary>
            public int LimitSlot = -1;

            public SourceSpan Span => Member.MemberToken.Span;
            public string Name => Method.Name;
        }

        /// <summary>
        /// What the loop walks: a run of cells, how many of them are in use, and what
        /// one of them holds. All of it lives in locals, so a query preempted halfway
        /// through resumes exactly where it stopped.
        /// </summary>
        private sealed class QueryCursor
        {
            public int BaseSlot;               // address of element 0
            public int CountSlot;              // how many elements
            public int ListSlot = -1;          // the list itself, when there is one
            public IZType Element = IZType.Num;
            public int Capacity;               // cells reserved, for the bound check
            public int Bound;                  // upper bound of the count, at compile time
            public bool Descending;            // walk it from the end
        }

        /// <summary>Jumps out of the loop being generated, patched when it closes.</summary>
        private sealed class QueryLoop
        {
            public readonly List<int> BreakJumps = new List<int>();
            public readonly List<int> ContinueJumps = new List<int>();
        }

        /// <summary>
        /// The element the chain is holding right now: a local slot plus its type.
        /// Same convention as any other variable - a scalar keeps the value, an
        /// aggregate keeps the address.
        /// </summary>
        private readonly struct QueryItem
        {
            public readonly int Slot;
            public readonly IZType Type;

            public QueryItem(int slot, IZType type)
            {
                Slot = slot;
                Type = type;
            }
        }

        // ==================================================================
        //  Entry point
        // ==================================================================

        /// <summary>Is this call the start of a query chain? Asked before any type is known.</summary>
        private static bool IsQueryCall(ExpressionSyntax expression) =>
            expression is CallExpression call &&
            call.Callee is MemberExpression member &&
            QueryMethods.ContainsKey(member.MemberName);

        /// <summary>
        /// Compiles 'target.method(...)' when the method is one of the query methods.
        /// False when the name is not one of them, and then the caller reports the
        /// call the way it always did.
        /// </summary>
        private bool TryEmitQueryCall(CallExpression call, out IZType result)
        {
            result = IZType.Error;

            var steps = new List<QueryStep>();
            ExpressionSyntax source = call;

            while (source is CallExpression inner &&
                   inner.Callee is MemberExpression member &&
                   QueryMethods.TryGetValue(member.MemberName, out var method))
            {
                steps.Add(new QueryStep
                {
                    Method = method,
                    Member = member,
                    Arguments = inner.Arguments,
                });
                source = member.Target;
            }

            if (steps.Count == 0) return false;
            steps.Reverse();

            if (TryEmitBatchAggregation(source, steps, out result)) return true;

            result = EmitQuery(source, steps);
            return true;
        }

        /// <summary>The five ways the game itself can collapse a batch read.</summary>
        private static readonly Dictionary<string, BatchAggregation> BatchTerminals =
            new Dictionary<string, BatchAggregation>(StringComparer.Ordinal)
            {
                ["avg"] = BatchAggregation.Average,
                ["sum"] = BatchAggregation.Sum,
                ["min"] = BatchAggregation.Minimum,
                ["max"] = BatchAggregation.Maximum,
                ["count"] = BatchAggregation.Count,
            };

        /// <summary>
        /// 'all(StructureSolarPanel).Power.sum()' and its four siblings.
        ///
        /// A batch property is the sequence of readings of every device the selector
        /// matched, and used bare it means '.avg()'. The terminal only picks which of
        /// the five modes travels in the instruction, so the whole chain still costs
        /// the single batch read the bare property costs.
        ///
        /// It is caught here rather than in EmitCall because the chain is
        /// indistinguishable from a query over a list until the source is resolved,
        /// and letting it reach the query machinery would answer a batch read with a
        /// message about lists.
        /// </summary>
        private bool TryEmitBatchAggregation(ExpressionSyntax source, List<QueryStep> steps,
                                             out IZType result)
        {
            result = IZType.Error;

            if (!(source is MemberExpression member)) return false;
            int line = LineOf(source.Span);

            // 'pump.Pressure.sum()' - the mode only exists when the game does the
            // reading, and a pin hands over one value with nothing to collapse.
            if (member.Target is NameExpression pinName &&
                _scope.LookupNoUse(pinName.Name) is DeviceSymbol pinDevice && !pinDevice.IsBatch &&
                BatchTerminals.ContainsKey(steps[0].Name))
            {
                _diagnostics.Report(IZErrorCode.TypeMismatch, steps[0].Span,
                    "'" + steps[0].Name + "' collapses a batch read, and '" + pinName.Name +
                    "' is a single device on pin " + DevicePins.Name(pinDevice.Pin));
                Emit(OpCode.PushZero, 0, 0, line);
                return true;
            }

            if (!IsBatchMemberRead(member)) return false;

            if (!BatchTerminals.TryGetValue(steps[0].Name, out var aggregation))
            {
                _diagnostics.Report(IZErrorCode.TypeMismatch, steps[0].Span,
                    "a batch read is done by the game and only knows avg, sum, min, max " +
                    "and count; '" + steps[0].Name + "' works on a list");
                Emit(OpCode.PushZero, 0, 0, line);
                return true;
            }

            if (steps[0].Arguments.Count != 0)
            {
                _diagnostics.Report(IZErrorCode.WrongArgumentCount, steps[0].Span,
                    "'" + steps[0].Name + "' over a batch read takes no argument: the game " +
                    "collapses the readings, and there is no element to hand over");
            }

            if (steps.Count > 1)
            {
                _diagnostics.Report(IZErrorCode.TypeMismatch, steps[1].Span,
                    "'" + steps[0].Name + "' already gives back a single value, so '" +
                    steps[1].Name + "' has nothing left to work on");
            }

            result = EmitBatchMemberRead(member, aggregation, line);
            return true;
        }

        /// <summary>
        /// Is this member read a batch read - 'all(X).Power', or 'lights.Power' over a
        /// name declared from a selector? Only those can carry a terminal.
        /// </summary>
        private bool IsBatchMemberRead(MemberExpression member)
        {
            if (member.Target is BatchSelectorExpression) return true;

            return member.Target is NameExpression name &&
                   _scope.LookupNoUse(name.Name) is DeviceSymbol device && device.IsBatch;
        }

        private IZType EmitBatchMemberRead(MemberExpression member, BatchAggregation aggregation,
                                           int line)
        {
            if (!TryResolveLogicType(member, out int logicType))
            {
                Emit(OpCode.PushZero, 0, 0, line);
                return IZType.Error;
            }

            if (member.Target is BatchSelectorExpression selector)
                EmitBatchLoad(selector, logicType, aggregation, line);
            else
                EmitBatchLoad((DeviceSymbol)_scope.Lookup(((NameExpression)member.Target).Name)!,
                              logicType, aggregation, line);

            return IZType.Num;
        }

        private IZType EmitQuery(ExpressionSyntax source, List<QueryStep> steps)
        {
            int line = LineOf(source.Span);
            var span = source.Span;

            CheckChainShape(steps);
            CheckIntoTarget(source, steps);
            ExpandTerminalLambdas(steps);

            int savedLocals = _function!.NextLocalSlot;
            int savedHeap = _function.NextHeapOffset;

            var sourceType = EmitExpression(source);

            if (sourceType.Kind != IZTypeKind.List && sourceType.Kind != IZTypeKind.Array)
            {
                Emit(OpCode.Pop, 0, 0, line);
                if (sourceType != IZType.Error)
                {
                    _diagnostics.Report(IZErrorCode.TypeMismatch, source.Span,
                        "'" + steps[0].Name + "' works on a list or an array, not on " +
                        sourceType.Display());
                }
                Emit(OpCode.PushZero, 0, 0, line);
                _function.NextLocalSlot = savedLocals;
                return IZType.Error;
            }

            IZType result;

            if (steps[0].Method.Category == QueryCategory.ListOp)
            {
                result = EmitListOperation(steps[0], sourceType, source, line);
            }
            else
            {
                var cursor = EmitSourceCursor(sourceType, span, line);
                result = EmitChain(cursor, steps, span, line);
            }

            // The locals a query uses die with it. Its cells do not: a chain that
            // hands back a list hands back cells, and those have to outlive it.
            _function.NextLocalSlot = savedLocals;
            if (!result.IsAggregate) _function.NextHeapOffset = savedHeap;

            return result;
        }

        /// <summary>
        /// A terminal ends the chain, and a list operation is the whole of it.
        /// Anything written after one of those is dropped, with the reason said once.
        /// </summary>
        private void CheckChainShape(List<QueryStep> steps)
        {
            for (int i = 0; i < steps.Count - 1; i++)
            {
                var category = steps[i].Method.Category;
                if (category != QueryCategory.Terminal && category != QueryCategory.ListOp) continue;

                _diagnostics.Report(IZErrorCode.TypeMismatch, steps[i + 1].Span,
                    "'" + steps[i].Name + "' already gives back a single value, so '" +
                    steps[i + 1].Name + "' has nothing left to work on");

                steps.RemoveRange(i + 1, steps.Count - i - 1);
                break;
            }

            for (int i = 1; i < steps.Count; i++)
            {
                if (steps[i].Method.Category != QueryCategory.ListOp) continue;

                _diagnostics.Report(IZErrorCode.TypeMismatch, steps[i].Span,
                    "'" + steps[i].Name + "' changes a list, and a query hands back a " +
                    "result instead of the list itself; call it on the list directly");

                steps.RemoveRange(i, steps.Count - i);
                break;
            }
        }

        /// <summary>
        /// Filling a list from itself has no defined answer: the loop would be
        /// walking cells that are being written under it. Only the plain case is
        /// caught, which is the one anybody writes by accident.
        /// </summary>
        private void CheckIntoTarget(ExpressionSyntax source, List<QueryStep> steps)
        {
            if (steps.Count == 0) return;

            var last = steps[steps.Count - 1];
            if (last.Method.Kind != QueryKind.Into || last.Arguments.Count != 1) return;

            if (source is NameExpression from && last.Arguments[0] is NameExpression to &&
                string.Equals(from.Name, to.Name, StringComparison.Ordinal))
            {
                _diagnostics.Report(IZErrorCode.InvalidAssignmentTarget, last.Arguments[0].Span,
                    "'into' would be filling '" + to.Name + "' from itself; write the result " +
                    "into another list");
            }
        }

        /// <summary>
        /// 'xs.count(f)' is 'xs.where(f).count()', and 'xs.sum(f)' is
        /// 'xs.select(f).sum()'. Rewriting them here is what keeps one implementation
        /// of each terminal instead of two.
        /// </summary>
        private static void ExpandTerminalLambdas(List<QueryStep> steps)
        {
            if (steps.Count == 0) return;

            var last = steps[steps.Count - 1];
            if (last.Method.Category != QueryCategory.Terminal) return;
            if (last.Arguments.Count != 1 || !(last.Arguments[0] is LambdaExpression)) return;

            string wrapper;
            switch (last.Method.Kind)
            {
                case QueryKind.Count:
                case QueryKind.Any:
                case QueryKind.First:
                case QueryKind.Last:
                    wrapper = "where";
                    break;
                case QueryKind.Sum:
                case QueryKind.Avg:
                case QueryKind.Min:
                case QueryKind.Max:
                    wrapper = "select";
                    break;
                default:
                    return;
            }

            steps.Insert(steps.Count - 1, new QueryStep
            {
                Method = QueryMethods[wrapper],
                Member = last.Member,
                Arguments = last.Arguments,
            });
            last.Arguments = new List<ExpressionSyntax>();
        }

        // ==================================================================
        //  The chain
        // ==================================================================

        /// <summary>
        /// Emits the whole chain over an open cursor and leaves its result on the
        /// stack. A blocking stage splits it: what came before is materialized, and
        /// the rest of the chain reads the cells that produced.
        /// </summary>
        private IZType EmitChain(QueryCursor cursor, List<QueryStep> steps, SourceSpan span, int line)
        {
            var terminal = steps[steps.Count - 1].Method.Category == QueryCategory.Terminal
                ? steps[steps.Count - 1]
                : null;

            int stageCount = terminal != null ? steps.Count - 1 : steps.Count;
            int start = 0;

            while (true)
            {
                int blocking = -1;
                for (int i = start; i < stageCount; i++)
                {
                    if (steps[i].Method.Category == QueryCategory.Blocking) { blocking = i; break; }
                }
                if (blocking < 0) break;

                var step = steps[blocking];

                // A 'reverse' with nothing pending in front of it is only a change of
                // direction: the cells are already there to be read backwards.
                if (step.Method.Kind == QueryKind.Reverse && blocking == start)
                {
                    cursor.Descending = !cursor.Descending;
                    start = blocking + 1;
                    continue;
                }

                cursor = EmitMaterialize(cursor, steps, start, blocking, span, line);

                switch (step.Method.Kind)
                {
                    case QueryKind.Reverse:
                        cursor.Descending = true;
                        break;
                    case QueryKind.OrderBy:
                        EmitSort(cursor, step, descending: false, line: line);
                        break;
                    case QueryKind.OrderByDesc:
                        EmitSort(cursor, step, descending: true, line: line);
                        break;
                    case QueryKind.Distinct:
                        EmitDistinct(cursor, step, line);
                        break;
                }

                start = blocking + 1;
            }

            return terminal != null
                ? EmitTerminal(cursor, steps, start, stageCount, terminal, span, line)
                : EmitMaterializeValue(cursor, steps, start, stageCount, span, line);
        }

        /// <summary>Opens a cursor over the value whose address is on top of the stack.</summary>
        private QueryCursor EmitSourceCursor(IZType sourceType, SourceSpan span, int line)
        {
            int address = AllocateLocal(span);
            Emit(OpCode.StoreLocal, address, 0, line);

            var cursor = new QueryCursor
            {
                BaseSlot = AllocateLocal(span),
                CountSlot = AllocateLocal(span),
                Element = sourceType.ElementType ?? IZType.Num,
                Capacity = Math.Max(1, sourceType.Length),
                Bound = Math.Max(0, sourceType.Length),
            };

            if (sourceType.Kind == IZTypeKind.List)
            {
                cursor.ListSlot = address;

                Emit(OpCode.LoadLocal, address, 0, line);
                Emit(OpCode.LoadHeap, 0, 0, line);                  // the count is cell 0
                Emit(OpCode.StoreLocal, cursor.CountSlot, 0, line);

                Emit(OpCode.LoadLocal, address, 0, line);
                Emit(OpCode.FieldRef, 1, 0, line);                  // the items start after it
                Emit(OpCode.StoreLocal, cursor.BaseSlot, 0, line);
            }
            else
            {
                // An array is a list that is always full: every cell is content.
                EmitConstant(sourceType.Length, line);
                Emit(OpCode.StoreLocal, cursor.CountSlot, 0, line);

                Emit(OpCode.LoadLocal, address, 0, line);
                Emit(OpCode.StoreLocal, cursor.BaseSlot, 0, line);
            }

            return cursor;
        }

        /// <summary>
        /// Emits the loop over the cursor, runs the stages in [start, end) on each
        /// element, and hands whatever survives to <paramref name="sink"/>.
        /// </summary>
        private void EmitPipeline(QueryCursor cursor, List<QueryStep> steps, int start, int end,
                                  SourceSpan span, int line, Action<QueryItem, QueryLoop> sink)
        {
            PrepareStageState(steps, start, end, span, line);

            var loop = new QueryLoop();
            int stride = cursor.Element.Size;

            int index = AllocateLocal(span);
            Emit(OpCode.PushZero, 0, 0, line);
            Emit(OpCode.StoreLocal, index, 0, line);

            int top = _code.Count;
            Emit(OpCode.LoadLocal, index, 0, line);
            Emit(OpCode.LoadLocal, cursor.CountSlot, 0, line);
            Emit(OpCode.Less, 0, 0, line);
            int exit = EmitJump(OpCode.JumpIfFalse, line);

            int element = AllocateLocal(span);
            Emit(OpCode.LoadLocal, cursor.BaseSlot, 0, line);

            if (cursor.Descending)
            {
                // count - 1 - index: the same loop, read from the other end.
                Emit(OpCode.LoadLocal, cursor.CountSlot, 0, line);
                Emit(OpCode.PushOne, 0, 0, line);
                Emit(OpCode.Subtract, 0, 0, line);
                Emit(OpCode.LoadLocal, index, 0, line);
                Emit(OpCode.Subtract, 0, 0, line);
            }
            else
            {
                Emit(OpCode.LoadLocal, index, 0, line);
            }

            Emit(OpCode.IndexRef, stride, cursor.Capacity, line);
            if (!cursor.Element.IsAggregate) Emit(OpCode.LoadHeap, 0, 0, line);
            Emit(OpCode.StoreLocal, element, 0, line);

            EmitStageChain(steps, start, end, new QueryItem(element, cursor.Element), loop, sink);

            foreach (int jump in loop.ContinueJumps) PatchJump(jump);

            Emit(OpCode.LoadLocal, index, 0, line);
            Emit(OpCode.PushOne, 0, 0, line);
            Emit(OpCode.Add, 0, 0, line);
            Emit(OpCode.StoreLocal, index, 0, line);
            Emit(OpCode.Jump, top, 0, line);

            PatchJump(exit);
            foreach (int jump in loop.BreakJumps) PatchJump(jump);
        }

        /// <summary>
        /// Reserves and initializes what the stages keep between elements: the
        /// counters of take and skip, and the flag of skipWhile. Their arguments are
        /// evaluated here too, once, instead of on every lap.
        /// </summary>
        private void PrepareStageState(List<QueryStep> steps, int start, int end,
                                       SourceSpan span, int line)
        {
            for (int i = start; i < end; i++)
            {
                var step = steps[i];
                switch (step.Method.Kind)
                {
                    case QueryKind.Take:
                    case QueryKind.Skip:
                        step.LimitSlot = AllocateLocal(span);
                        EmitValueArgument(step, IZType.Num);
                        Emit(OpCode.StoreLocal, step.LimitSlot, 0, line);

                        step.StateSlot = AllocateLocal(span);
                        Emit(OpCode.PushZero, 0, 0, line);
                        Emit(OpCode.StoreLocal, step.StateSlot, 0, line);
                        break;

                    case QueryKind.SkipWhile:
                        step.StateSlot = AllocateLocal(span);
                        Emit(OpCode.PushOne, 0, 0, line);           // still skipping
                        Emit(OpCode.StoreLocal, step.StateSlot, 0, line);
                        break;
                }
            }
        }

        private void EmitStageChain(List<QueryStep> steps, int index, int end,
                                    QueryItem item, QueryLoop loop,
                                    Action<QueryItem, QueryLoop> sink)
        {
            if (index >= end)
            {
                sink(item, loop);
                return;
            }

            var step = steps[index];
            int line = LineOf(step.Span);

            switch (step.Method.Kind)
            {
                case QueryKind.Where:
                {
                    var type = EmitLambda(step, item);
                    RequireBool(type, step.Span, "'where'");
                    loop.ContinueJumps.Add(EmitJump(OpCode.JumpIfFalse, line));
                    break;
                }

                case QueryKind.TakeWhile:
                {
                    var type = EmitLambda(step, item);
                    RequireBool(type, step.Span, "'takeWhile'");
                    loop.BreakJumps.Add(EmitJump(OpCode.JumpIfFalse, line));
                    break;
                }

                case QueryKind.SkipWhile:
                {
                    Emit(OpCode.LoadLocal, step.StateSlot, 0, line);
                    int done = EmitJump(OpCode.JumpIfFalse, line);      // already past it

                    var type = EmitLambda(step, item);
                    RequireBool(type, step.Span, "'skipWhile'");
                    int stop = EmitJump(OpCode.JumpIfFalse, line);
                    loop.ContinueJumps.Add(EmitJump(OpCode.Jump, line));

                    PatchJump(stop);
                    Emit(OpCode.PushZero, 0, 0, line);
                    Emit(OpCode.StoreLocal, step.StateSlot, 0, line);

                    PatchJump(done);
                    break;
                }

                case QueryKind.Take:
                {
                    Emit(OpCode.LoadLocal, step.StateSlot, 0, line);
                    Emit(OpCode.LoadLocal, step.LimitSlot, 0, line);
                    Emit(OpCode.Less, 0, 0, line);
                    loop.BreakJumps.Add(EmitJump(OpCode.JumpIfFalse, line));

                    Emit(OpCode.LoadLocal, step.StateSlot, 0, line);
                    Emit(OpCode.PushOne, 0, 0, line);
                    Emit(OpCode.Add, 0, 0, line);
                    Emit(OpCode.StoreLocal, step.StateSlot, 0, line);
                    break;
                }

                case QueryKind.Skip:
                {
                    Emit(OpCode.LoadLocal, step.StateSlot, 0, line);
                    Emit(OpCode.LoadLocal, step.LimitSlot, 0, line);
                    Emit(OpCode.Less, 0, 0, line);
                    int through = EmitJump(OpCode.JumpIfFalse, line);

                    Emit(OpCode.LoadLocal, step.StateSlot, 0, line);
                    Emit(OpCode.PushOne, 0, 0, line);
                    Emit(OpCode.Add, 0, 0, line);
                    Emit(OpCode.StoreLocal, step.StateSlot, 0, line);
                    loop.ContinueJumps.Add(EmitJump(OpCode.Jump, line));

                    PatchJump(through);
                    break;
                }

                case QueryKind.Select:
                {
                    var type = EmitLambda(step, item);
                    if (type.IsAggregate)
                    {
                        _diagnostics.Report(IZErrorCode.TypeMismatch, step.Span,
                            "'select' gives back one value per element, and " + type.Display() +
                            " is a group of cells; pick a field out of it");
                        type = IZType.Error;
                    }

                    int slot = AllocateLocal(step.Span);
                    Emit(OpCode.StoreLocal, slot, 0, line);
                    item = new QueryItem(slot, type);
                    break;
                }
            }

            EmitStageChain(steps, index + 1, end, item, loop, sink);
        }

        /// <summary>
        /// Emits the body of a lambda with its parameter bound to the element the
        /// loop is holding. There is no call: the body is compiled straight into the
        /// loop, so the parameter is only a name for a local that already exists.
        /// </summary>
        private IZType EmitLambda(QueryStep step, QueryItem item)
        {
            int line = LineOf(step.Span);

            if (step.Arguments.Count != 1 || !(step.Arguments[0] is LambdaExpression lambda))
            {
                _diagnostics.Report(IZErrorCode.TypeMismatch, step.Span,
                    "'" + step.Name + "' takes a function of one element: " + step.Method.Usage);

                foreach (var argument in step.Arguments)
                {
                    EmitExpression(argument);
                    Emit(OpCode.Pop, 0, 0, line);
                }
                Emit(OpCode.PushZero, 0, 0, line);
                return IZType.Error;
            }

            var saved = _scope;
            _scope = new Scope(saved);

            // The parameter is used by definition - it is the whole point of the
            // lambda - so it never takes part in the unused-name pass.
            var parameter = new VariableSymbol(lambda.ParameterName, item.Type,
                isConst: false, isGlobal: false, slot: item.Slot)
            {
                IsUsed = true,
            };
            _scope.TryDeclare(parameter);

            var type = EmitExpression(lambda.Body);

            _scope = saved;
            return type;
        }

        /// <summary>Emits an argument that is a plain value, and refuses a lambda there.</summary>
        private IZType EmitValueArgument(QueryStep step, IZType expected)
        {
            int line = LineOf(step.Span);

            if (step.Arguments.Count != 1)
            {
                _diagnostics.Report(IZErrorCode.WrongArgumentCount, step.Span,
                    "'" + step.Name + "' takes 1 argument: " + step.Method.Usage);
                Emit(OpCode.PushZero, 0, 0, line);
                return IZType.Error;
            }

            if (step.Arguments[0] is LambdaExpression)
            {
                _diagnostics.Report(IZErrorCode.TypeMismatch, step.Arguments[0].Span,
                    "'" + step.Name + "' takes a value, not a function: " + step.Method.Usage);
                Emit(OpCode.PushZero, 0, 0, line);
                return IZType.Error;
            }

            var type = EmitExpression(step.Arguments[0]);
            if (expected == IZType.Num)
                RequireNumeric(type, step.Arguments[0].Span, "'" + step.Name + "'");
            return type;
        }

        // ==================================================================
        //  Materializing
        // ==================================================================

        /// <summary>
        /// How many elements can still come out of the chain, counted at compile
        /// time. It is what sizes every list the compiler reserves for a query:
        /// after 'take(3)' three cells are enough.
        /// </summary>
        private int BoundAfter(int bound, List<QueryStep> steps, int start, int end)
        {
            for (int i = start; i < end; i++)
            {
                var step = steps[i];
                if (step.Arguments.Count != 1) continue;

                if (step.Method.Kind == QueryKind.Take &&
                    TryEvaluateConstant(step.Arguments[0], out double take) != IZType.Error)
                {
                    bound = Math.Min(bound, Math.Max(0, (int)Math.Truncate(take)));
                }
                else if (step.Method.Kind == QueryKind.Skip &&
                         TryEvaluateConstant(step.Arguments[0], out double skip) != IZType.Error)
                {
                    bound = Math.Max(0, bound - Math.Max(0, (int)Math.Truncate(skip)));
                }
            }
            return bound;
        }

        /// <summary>
        /// Runs the stages in [start, end) and writes what survives into a list the
        /// compiler reserves, then hands back a cursor over it.
        /// </summary>
        private QueryCursor EmitMaterialize(QueryCursor cursor, List<QueryStep> steps,
                                            int start, int end, SourceSpan span, int line)
        {
            int capacity = Math.Max(1, BoundAfter(cursor.Bound, steps, start, end));

            int listSlot = AllocateLocal(span);
            int reservation = EmitReservation(line);
            Emit(OpCode.StoreLocal, listSlot, 0, line);

            var element = cursor.Element;
            EmitPipeline(cursor, steps, start, end, span, line,
                (item, loop) =>
                {
                    element = item.Type;
                    EmitAppend(listSlot, item, capacity, loop, line);
                });

            PatchReservation(reservation, IZType.ListOf(element, capacity).Size, span);

            return new QueryCursor
            {
                ListSlot = listSlot,
                BaseSlot = EmitItemsAddress(listSlot, span, line),
                CountSlot = EmitCountOf(listSlot, span, line),
                Element = element,
                Capacity = capacity,
                Bound = capacity,
            };
        }

        /// <summary>The chain ends without a terminal, so its value is the list itself.</summary>
        private IZType EmitMaterializeValue(QueryCursor cursor, List<QueryStep> steps,
                                            int start, int end, SourceSpan span, int line)
        {
            var result = EmitMaterialize(cursor, steps, start, end, span, line);
            Emit(OpCode.LoadLocal, result.ListSlot, 0, line);
            return IZType.ListOf(result.Element, result.Capacity);
        }

        /// <summary>
        /// Reserves cells whose size is not known yet: a 'select' only decides what
        /// an element is once its body has been compiled. The instruction is emitted
        /// here and completed by <see cref="PatchReservation"/>.
        /// </summary>
        private int EmitReservation(int line)
        {
            Emit(OpCode.NewAggregate, 0, 0, line);
            return _code.Count - 1;
        }

        private void PatchReservation(int index, int cells, SourceSpan span)
        {
            int offset = AllocateAggregate(cells, span);
            _code[index] = new Instruction(OpCode.NewAggregate, offset, cells);
        }

        /// <summary>Reads the count of a list into a fresh local.</summary>
        private int EmitCountOf(int listSlot, SourceSpan span, int line)
        {
            int slot = AllocateLocal(span);
            Emit(OpCode.LoadLocal, listSlot, 0, line);
            Emit(OpCode.LoadHeap, 0, 0, line);
            Emit(OpCode.StoreLocal, slot, 0, line);
            return slot;
        }

        /// <summary>Puts the address of the first item of a list into a fresh local.</summary>
        private int EmitItemsAddress(int listSlot, SourceSpan span, int line)
        {
            int slot = AllocateLocal(span);
            Emit(OpCode.LoadLocal, listSlot, 0, line);
            Emit(OpCode.FieldRef, 1, 0, line);
            Emit(OpCode.StoreLocal, slot, 0, line);
            return slot;
        }

        /// <summary>
        /// Appends the element to the list in <paramref name="listSlot"/>. A full
        /// list ends the loop: nothing coming after it would fit either.
        /// </summary>
        private void EmitAppend(int listSlot, QueryItem item, int capacity, QueryLoop loop, int line)
        {
            int stride = item.Type == IZType.Error ? 1 : item.Type.Size;

            Emit(OpCode.LoadLocal, listSlot, 0, line);
            Emit(OpCode.LoadHeap, 0, 0, line);
            EmitConstant(capacity, line);
            Emit(OpCode.Less, 0, 0, line);
            loop.BreakJumps.Add(EmitJump(OpCode.JumpIfFalse, line));

            Emit(OpCode.LoadLocal, listSlot, 0, line);
            Emit(OpCode.FieldRef, 1, 0, line);
            Emit(OpCode.LoadLocal, listSlot, 0, line);
            Emit(OpCode.LoadHeap, 0, 0, line);
            Emit(OpCode.IndexRef, stride, capacity, line);

            Emit(OpCode.LoadLocal, item.Slot, 0, line);
            Emit(item.Type.IsAggregate ? OpCode.CopyHeap : OpCode.StoreHeap, stride, 0, line);

            Emit(OpCode.LoadLocal, listSlot, 0, line);
            Emit(OpCode.LoadLocal, listSlot, 0, line);
            Emit(OpCode.LoadHeap, 0, 0, line);
            Emit(OpCode.PushOne, 0, 0, line);
            Emit(OpCode.Add, 0, 0, line);
            Emit(OpCode.StoreHeap, 0, 0, line);
        }

        // ==================================================================
        //  Comparing
        // ==================================================================

        /// <summary>
        /// Consumes two values of the same type and pushes whether they are equal.
        /// A str compares by its text, which is the only case that is not one opcode.
        /// </summary>
        private void EmitEqualTest(IZType type, int line)
        {
            if (type == IZType.Str)
            {
                Emit(OpCode.StrCompare, 0, 0, line);
                Emit(OpCode.PushZero, 0, 0, line);
            }
            Emit(OpCode.Equal, 0, 0, line);
        }

        /// <summary>Consumes two values and pushes whether the first comes before the second.</summary>
        private void EmitLessTest(IZType type, int line)
        {
            if (type == IZType.Str)
            {
                Emit(OpCode.StrCompare, 0, 0, line);
                Emit(OpCode.PushZero, 0, 0, line);
            }
            Emit(OpCode.Less, 0, 0, line);
        }

        /// <summary>Consumes two values and pushes whether the first comes after the second.</summary>
        private void EmitGreaterTest(IZType type, int line)
        {
            if (type == IZType.Str)
            {
                Emit(OpCode.StrCompare, 0, 0, line);
                Emit(OpCode.PushZero, 0, 0, line);
            }
            Emit(OpCode.Greater, 0, 0, line);
        }

        /// <summary>
        /// Refuses a comparison the VM has no way of making: two structs are two runs
        /// of cells, and what "equal" or "smaller" would mean there is a decision the
        /// program has to make, not the language.
        /// </summary>
        private bool RequireComparable(IZType type, SourceSpan span, string context)
        {
            if (type == IZType.Error) return false;
            if (!type.IsAggregate) return true;

            _diagnostics.Report(IZErrorCode.TypeMismatch, span,
                context + " compares values, and " + type.Display() +
                " is a group of cells; go through 'select' to pick what to compare");
            return false;
        }
    }
}
