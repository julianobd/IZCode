using System;
using System.Collections.Generic;
using IZLang.Diagnostics;
using IZLang.Parsing;
using IZLang.Vm;

namespace IZLang.Binding
{
    /// <summary>
    /// What closes a query: the terminals that turn a sequence into one value, the
    /// two blocking stages that need cells of their own (sorting and distinct), and
    /// the four operations that change a list instead of reading it.
    /// </summary>
    public sealed partial class Compiler
    {
        // ==================================================================
        //  Terminals
        // ==================================================================

        private IZType EmitTerminal(QueryCursor cursor, List<QueryStep> steps, int start, int end,
                                    QueryStep terminal, SourceSpan span, int line)
        {
            switch (terminal.Method.Kind)
            {
                case QueryKind.Count: return EmitCount(cursor, steps, start, end, span, line);
                case QueryKind.Sum: return EmitSum(cursor, steps, start, end, terminal, span, line);
                case QueryKind.Avg: return EmitAverage(cursor, steps, start, end, terminal, span, line);
                case QueryKind.Min: return EmitExtreme(cursor, steps, start, end, terminal, span, line, biggest: false);
                case QueryKind.Max: return EmitExtreme(cursor, steps, start, end, terminal, span, line, biggest: true);
                case QueryKind.Any: return EmitAny(cursor, steps, start, end, span, line);
                case QueryKind.All: return EmitAll(cursor, steps, start, end, terminal, span, line);
                case QueryKind.First: return EmitEdge(cursor, steps, start, end, terminal, span, line, fromStart: true);
                case QueryKind.Last: return EmitEdge(cursor, steps, start, end, terminal, span, line, fromStart: false);
                case QueryKind.FirstOr: return EmitEdgeOr(cursor, steps, start, end, terminal, span, line, fromStart: true);
                case QueryKind.LastOr: return EmitEdgeOr(cursor, steps, start, end, terminal, span, line, fromStart: false);
                case QueryKind.Contains: return EmitContains(cursor, steps, start, end, terminal, span, line);
                case QueryKind.IndexOf: return EmitIndexOf(cursor, steps, start, end, terminal, span, line);
                default: return EmitInto(cursor, steps, start, end, terminal, span, line);
            }
        }

        private IZType EmitCount(QueryCursor cursor, List<QueryStep> steps, int start, int end,
                                 SourceSpan span, int line)
        {
            int total = AllocateLocal(span);
            Emit(OpCode.PushZero, 0, 0, line);
            Emit(OpCode.StoreLocal, total, 0, line);

            EmitPipeline(cursor, steps, start, end, span, line, (item, loop) =>
            {
                Emit(OpCode.LoadLocal, total, 0, line);
                Emit(OpCode.PushOne, 0, 0, line);
                Emit(OpCode.Add, 0, 0, line);
                Emit(OpCode.StoreLocal, total, 0, line);
            });

            Emit(OpCode.LoadLocal, total, 0, line);
            return IZType.Num;
        }

        private IZType EmitSum(QueryCursor cursor, List<QueryStep> steps, int start, int end,
                               QueryStep terminal, SourceSpan span, int line)
        {
            int total = AllocateLocal(span);
            Emit(OpCode.PushZero, 0, 0, line);
            Emit(OpCode.StoreLocal, total, 0, line);

            EmitPipeline(cursor, steps, start, end, span, line, (item, loop) =>
            {
                if (!RequireQueryNumber(item, terminal)) return;

                Emit(OpCode.LoadLocal, total, 0, line);
                Emit(OpCode.LoadLocal, item.Slot, 0, line);
                Emit(OpCode.Add, 0, 0, line);
                Emit(OpCode.StoreLocal, total, 0, line);
            });

            Emit(OpCode.LoadLocal, total, 0, line);
            return IZType.Num;
        }

        /// <summary>The average of nothing is 0, the same answer a batch read gives.</summary>
        private IZType EmitAverage(QueryCursor cursor, List<QueryStep> steps, int start, int end,
                                   QueryStep terminal, SourceSpan span, int line)
        {
            int total = AllocateLocal(span);
            int seen = AllocateLocal(span);
            Emit(OpCode.PushZero, 0, 0, line);
            Emit(OpCode.StoreLocal, total, 0, line);
            Emit(OpCode.PushZero, 0, 0, line);
            Emit(OpCode.StoreLocal, seen, 0, line);

            EmitPipeline(cursor, steps, start, end, span, line, (item, loop) =>
            {
                if (!RequireQueryNumber(item, terminal)) return;

                Emit(OpCode.LoadLocal, total, 0, line);
                Emit(OpCode.LoadLocal, item.Slot, 0, line);
                Emit(OpCode.Add, 0, 0, line);
                Emit(OpCode.StoreLocal, total, 0, line);

                Emit(OpCode.LoadLocal, seen, 0, line);
                Emit(OpCode.PushOne, 0, 0, line);
                Emit(OpCode.Add, 0, 0, line);
                Emit(OpCode.StoreLocal, seen, 0, line);
            });

            Emit(OpCode.LoadLocal, seen, 0, line);
            Emit(OpCode.PushZero, 0, 0, line);
            Emit(OpCode.Greater, 0, 0, line);
            int empty = EmitJump(OpCode.JumpIfFalse, line);

            Emit(OpCode.LoadLocal, total, 0, line);
            Emit(OpCode.LoadLocal, seen, 0, line);
            Emit(OpCode.Divide, 0, 0, line);
            int done = EmitJump(OpCode.Jump, line);

            PatchJump(empty);
            Emit(OpCode.PushZero, 0, 0, line);
            PatchJump(done);

            return IZType.Num;
        }

        private IZType EmitExtreme(QueryCursor cursor, List<QueryStep> steps, int start, int end,
                                   QueryStep terminal, SourceSpan span, int line, bool biggest)
        {
            int best = AllocateLocal(span);
            int seen = AllocateLocal(span);
            Emit(OpCode.PushZero, 0, 0, line);
            Emit(OpCode.StoreLocal, best, 0, line);
            Emit(OpCode.PushZero, 0, 0, line);
            Emit(OpCode.StoreLocal, seen, 0, line);

            var result = IZType.Num;

            EmitPipeline(cursor, steps, start, end, span, line, (item, loop) =>
            {
                if (!RequireComparable(item.Type, terminal.Span, "'" + terminal.Name + "'")) return;
                result = item.Type == IZType.Bool ? IZType.Num : item.Type;

                Emit(OpCode.LoadLocal, seen, 0, line);
                int first = EmitJump(OpCode.JumpIfFalse, line);

                Emit(OpCode.LoadLocal, item.Slot, 0, line);
                Emit(OpCode.LoadLocal, best, 0, line);
                if (biggest) EmitGreaterTest(item.Type, line);
                else EmitLessTest(item.Type, line);
                int keep = EmitJump(OpCode.JumpIfFalse, line);

                PatchJump(first);
                Emit(OpCode.LoadLocal, item.Slot, 0, line);
                Emit(OpCode.StoreLocal, best, 0, line);
                Emit(OpCode.PushOne, 0, 0, line);
                Emit(OpCode.StoreLocal, seen, 0, line);

                PatchJump(keep);
            });

            Emit(OpCode.LoadLocal, best, 0, line);
            return result;
        }

        private IZType EmitAny(QueryCursor cursor, List<QueryStep> steps, int start, int end,
                               SourceSpan span, int line)
        {
            int found = AllocateLocal(span);
            Emit(OpCode.PushZero, 0, 0, line);
            Emit(OpCode.StoreLocal, found, 0, line);

            EmitPipeline(cursor, steps, start, end, span, line, (item, loop) =>
            {
                Emit(OpCode.PushOne, 0, 0, line);
                Emit(OpCode.StoreLocal, found, 0, line);
                loop.BreakJumps.Add(EmitJump(OpCode.Jump, line));
            });

            Emit(OpCode.LoadLocal, found, 0, line);
            return IZType.Bool;
        }

        /// <summary>
        /// 'all' is the one terminal that keeps its own test: the first element that
        /// fails it is the answer, and there is nothing to accumulate.
        /// </summary>
        private IZType EmitAll(QueryCursor cursor, List<QueryStep> steps, int start, int end,
                               QueryStep terminal, SourceSpan span, int line)
        {
            int ok = AllocateLocal(span);
            Emit(OpCode.PushOne, 0, 0, line);
            Emit(OpCode.StoreLocal, ok, 0, line);

            EmitPipeline(cursor, steps, start, end, span, line, (item, loop) =>
            {
                var type = EmitLambda(terminal, item);
                RequireBool(type, terminal.Span, "'all'");
                int keep = EmitJump(OpCode.JumpIfTrue, line);

                Emit(OpCode.PushZero, 0, 0, line);
                Emit(OpCode.StoreLocal, ok, 0, line);
                loop.BreakJumps.Add(EmitJump(OpCode.Jump, line));

                PatchJump(keep);
            });

            Emit(OpCode.LoadLocal, ok, 0, line);
            return IZType.Bool;
        }

        /// <summary>
        /// first() and last(). An empty result is a runtime error, exactly like
        /// reading xs[0] of an empty list: 'firstOr' is the form that says what to
        /// answer when nothing matched.
        /// </summary>
        private IZType EmitEdge(QueryCursor cursor, List<QueryStep> steps, int start, int end,
                                QueryStep terminal, SourceSpan span, int line, bool fromStart)
        {
            if (terminal.Arguments.Count != 0)
            {
                _diagnostics.Report(IZErrorCode.WrongArgumentCount, terminal.Span,
                    "'" + terminal.Name + "' takes a test or nothing: " + terminal.Method.Usage);
            }

            int found = AllocateLocal(span);
            int value = AllocateLocal(span);
            Emit(OpCode.PushZero, 0, 0, line);
            Emit(OpCode.StoreLocal, found, 0, line);
            Emit(OpCode.PushZero, 0, 0, line);
            Emit(OpCode.StoreLocal, value, 0, line);

            var result = cursor.Element;

            EmitPipeline(cursor, steps, start, end, span, line, (item, loop) =>
            {
                result = item.Type;

                Emit(OpCode.LoadLocal, item.Slot, 0, line);
                Emit(OpCode.StoreLocal, value, 0, line);
                Emit(OpCode.PushOne, 0, 0, line);
                Emit(OpCode.StoreLocal, found, 0, line);

                // 'last' keeps overwriting; 'first' has its answer already.
                if (fromStart) loop.BreakJumps.Add(EmitJump(OpCode.Jump, line));
            });

            Emit(OpCode.LoadLocal, found, 0, line);
            int ok = EmitJump(OpCode.JumpIfTrue, line);
            Emit(OpCode.Trap, InternString("'" + terminal.Name + "' found nothing: the list is " +
                "empty, or nothing passed the test", span), 0, line);
            PatchJump(ok);

            Emit(OpCode.LoadLocal, value, 0, line);
            return result;
        }

        private IZType EmitEdgeOr(QueryCursor cursor, List<QueryStep> steps, int start, int end,
                                  QueryStep terminal, SourceSpan span, int line, bool fromStart)
        {
            int value = AllocateLocal(span);
            var fallback = EmitValueArgument(terminal, IZType.Error);
            Emit(OpCode.StoreLocal, value, 0, line);

            if (fallback.IsAggregate)
            {
                _diagnostics.Report(IZErrorCode.TypeMismatch, terminal.Span,
                    "'" + terminal.Name + "' answers with a single value when nothing matched, " +
                    "and " + fallback.Display() + " is a group of cells");
                fallback = IZType.Error;
            }

            EmitPipeline(cursor, steps, start, end, span, line, (item, loop) =>
            {
                if (!item.Type.IsAssignableTo(fallback) && fallback != IZType.Error)
                {
                    _diagnostics.Report(IZErrorCode.TypeMismatch, terminal.Span,
                        "the elements are " + item.Type.Display() + ", but the value to fall " +
                        "back on is " + fallback.Display());
                }

                Emit(OpCode.LoadLocal, item.Slot, 0, line);
                Emit(OpCode.StoreLocal, value, 0, line);

                if (fromStart) loop.BreakJumps.Add(EmitJump(OpCode.Jump, line));
            });

            Emit(OpCode.LoadLocal, value, 0, line);
            return fallback;
        }

        private IZType EmitContains(QueryCursor cursor, List<QueryStep> steps, int start, int end,
                                    QueryStep terminal, SourceSpan span, int line)
        {
            int wanted = AllocateLocal(span);
            var wantedType = EmitValueArgument(terminal, IZType.Error);
            Emit(OpCode.StoreLocal, wanted, 0, line);

            int found = AllocateLocal(span);
            Emit(OpCode.PushZero, 0, 0, line);
            Emit(OpCode.StoreLocal, found, 0, line);

            EmitPipeline(cursor, steps, start, end, span, line, (item, loop) =>
            {
                if (!CheckSearchedValue(item, wantedType, terminal)) return;

                Emit(OpCode.LoadLocal, item.Slot, 0, line);
                Emit(OpCode.LoadLocal, wanted, 0, line);
                EmitEqualTest(item.Type, line);
                int no = EmitJump(OpCode.JumpIfFalse, line);

                Emit(OpCode.PushOne, 0, 0, line);
                Emit(OpCode.StoreLocal, found, 0, line);
                loop.BreakJumps.Add(EmitJump(OpCode.Jump, line));

                PatchJump(no);
            });

            Emit(OpCode.LoadLocal, found, 0, line);
            return IZType.Bool;
        }

        /// <summary>Where the value sits in the result, or -1 when it is not in it.</summary>
        private IZType EmitIndexOf(QueryCursor cursor, List<QueryStep> steps, int start, int end,
                                   QueryStep terminal, SourceSpan span, int line)
        {
            int wanted = AllocateLocal(span);
            var wantedType = EmitValueArgument(terminal, IZType.Error);
            Emit(OpCode.StoreLocal, wanted, 0, line);

            int position = AllocateLocal(span);
            int answer = AllocateLocal(span);
            Emit(OpCode.PushZero, 0, 0, line);
            Emit(OpCode.StoreLocal, position, 0, line);
            EmitConstant(-1, line);
            Emit(OpCode.StoreLocal, answer, 0, line);

            EmitPipeline(cursor, steps, start, end, span, line, (item, loop) =>
            {
                if (!CheckSearchedValue(item, wantedType, terminal)) return;

                Emit(OpCode.LoadLocal, item.Slot, 0, line);
                Emit(OpCode.LoadLocal, wanted, 0, line);
                EmitEqualTest(item.Type, line);
                int no = EmitJump(OpCode.JumpIfFalse, line);

                Emit(OpCode.LoadLocal, position, 0, line);
                Emit(OpCode.StoreLocal, answer, 0, line);
                loop.BreakJumps.Add(EmitJump(OpCode.Jump, line));

                PatchJump(no);
                Emit(OpCode.LoadLocal, position, 0, line);
                Emit(OpCode.PushOne, 0, 0, line);
                Emit(OpCode.Add, 0, 0, line);
                Emit(OpCode.StoreLocal, position, 0, line);
            });

            Emit(OpCode.LoadLocal, answer, 0, line);
            return IZType.Num;
        }

        /// <summary>
        /// Writes the result into a list that already exists, replacing what was in
        /// it, and answers how many elements got there. It is how a query result
        /// survives the tick: the target can be a global, and the cells a chain
        /// reserves for itself belong to the frame that ran it.
        /// </summary>
        private IZType EmitInto(QueryCursor cursor, List<QueryStep> steps, int start, int end,
                                QueryStep terminal, SourceSpan span, int line)
        {
            int target = AllocateLocal(span);
            var targetType = EmitValueArgument(terminal, IZType.Error);
            Emit(OpCode.StoreLocal, target, 0, line);

            if (targetType.Kind != IZTypeKind.List)
            {
                if (targetType != IZType.Error)
                {
                    _diagnostics.Report(IZErrorCode.TypeMismatch, terminal.Span,
                        "'into' fills a list, and this is " + targetType.Display());
                }
                Emit(OpCode.PushZero, 0, 0, line);
                return IZType.Error;
            }

            // Emptying the target first is what makes 'into' a replacement and not an
            // append: the query decides the whole contents.
            Emit(OpCode.LoadLocal, target, 0, line);
            Emit(OpCode.PushZero, 0, 0, line);
            Emit(OpCode.StoreHeap, 0, 0, line);

            EmitPipeline(cursor, steps, start, end, span, line, (item, loop) =>
            {
                if (!item.Type.IsAssignableTo(targetType.ElementType!))
                {
                    _diagnostics.Report(IZErrorCode.TypeMismatch, terminal.Span,
                        "the result holds " + item.Type.Display() + ", and '" +
                        targetType.Display() + "' holds " + targetType.ElementType!.Display());
                    return;
                }

                EmitAppend(target, item, targetType.Length, loop, line);
            });

            Emit(OpCode.LoadLocal, target, 0, line);
            Emit(OpCode.LoadHeap, 0, 0, line);
            return IZType.Num;
        }

        /// <summary>The element has to be a number for sum and avg to mean anything.</summary>
        private bool RequireQueryNumber(QueryItem item, QueryStep terminal)
        {
            if (item.Type == IZType.Error) return false;

            if (item.Type == IZType.Num || item.Type == IZType.Bool) return true;

            _diagnostics.Report(IZErrorCode.TypeMismatch, terminal.Span,
                "'" + terminal.Name + "' adds numbers, and the elements are " +
                item.Type.Display() +
                (item.Type.IsAggregate ? "; pick a field with 'select'" : string.Empty));
            return false;
        }

        private bool CheckSearchedValue(QueryItem item, IZType wanted, QueryStep terminal)
        {
            if (item.Type == IZType.Error || wanted == IZType.Error) return false;
            if (!RequireComparable(item.Type, terminal.Span, "'" + terminal.Name + "'")) return false;

            if (!wanted.IsAssignableTo(item.Type) && !item.Type.IsAssignableTo(wanted))
            {
                _diagnostics.Report(IZErrorCode.TypeMismatch, terminal.Span,
                    "the elements are " + item.Type.Display() + ", and this looks for " +
                    wanted.Display());
                return false;
            }
            return true;
        }

        // ==================================================================
        //  Sorting
        // ==================================================================

        /// <summary>
        /// Sorts the cells the cursor walks, in place.
        ///
        /// The keys are computed once, into a run of cells beside the items: an
        /// insertion sort compares far more often than it moves, and the key of an
        /// element is whatever the lambda says - possibly a whole expression.
        /// It is a stable sort, so 'orderBy(a).orderBy(b)' keeps the first order
        /// inside equal values of the second.
        /// </summary>
        private void EmitSort(QueryCursor cursor, QueryStep step, bool descending, int line)
        {
            var span = step.Span;
            int stride = cursor.Element.Size;
            int capacity = cursor.Capacity;

            int keys = AllocateLocal(span);
            Emit(OpCode.NewAggregate, AllocateAggregate(capacity, span), capacity, line);
            Emit(OpCode.StoreLocal, keys, 0, line);

            var keyType = IZType.Num;

            // ---- the keys, one pass over the items ----
            {
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
                Emit(OpCode.LoadLocal, index, 0, line);
                Emit(OpCode.IndexRef, stride, capacity, line);
                if (!cursor.Element.IsAggregate) Emit(OpCode.LoadHeap, 0, 0, line);
                Emit(OpCode.StoreLocal, element, 0, line);

                Emit(OpCode.LoadLocal, keys, 0, line);
                Emit(OpCode.LoadLocal, index, 0, line);
                Emit(OpCode.IndexRef, 1, capacity, line);

                keyType = EmitLambda(step, new QueryItem(element, cursor.Element));
                if (keyType.IsAggregate)
                {
                    _diagnostics.Report(IZErrorCode.TypeMismatch, span,
                        "'" + step.Name + "' orders by one value per element, and " +
                        keyType.Display() + " is a group of cells");
                    keyType = IZType.Error;
                }
                Emit(OpCode.StoreHeap, 0, 0, line);

                Emit(OpCode.LoadLocal, index, 0, line);
                Emit(OpCode.PushOne, 0, 0, line);
                Emit(OpCode.Add, 0, 0, line);
                Emit(OpCode.StoreLocal, index, 0, line);
                Emit(OpCode.Jump, top, 0, line);
                PatchJump(exit);
            }

            // ---- insertion sort over items and keys together ----
            int scratch = AllocateLocal(span);
            Emit(OpCode.NewAggregate, AllocateAggregate(stride, span), stride, line);
            Emit(OpCode.StoreLocal, scratch, 0, line);

            int key = AllocateLocal(span);
            int outer = AllocateLocal(span);
            int inner = AllocateLocal(span);

            Emit(OpCode.PushOne, 0, 0, line);
            Emit(OpCode.StoreLocal, outer, 0, line);

            int outerTop = _code.Count;
            Emit(OpCode.LoadLocal, outer, 0, line);
            Emit(OpCode.LoadLocal, cursor.CountSlot, 0, line);
            Emit(OpCode.Less, 0, 0, line);
            int outerExit = EmitJump(OpCode.JumpIfFalse, line);

            // the element that is looking for its place, kept aside
            Emit(OpCode.LoadLocal, keys, 0, line);
            Emit(OpCode.LoadLocal, outer, 0, line);
            Emit(OpCode.IndexRef, 1, capacity, line);
            Emit(OpCode.LoadHeap, 0, 0, line);
            Emit(OpCode.StoreLocal, key, 0, line);

            Emit(OpCode.LoadLocal, scratch, 0, line);
            Emit(OpCode.LoadLocal, cursor.BaseSlot, 0, line);
            Emit(OpCode.LoadLocal, outer, 0, line);
            Emit(OpCode.IndexRef, stride, capacity, line);
            Emit(OpCode.CopyHeap, stride, 0, line);

            Emit(OpCode.LoadLocal, outer, 0, line);
            Emit(OpCode.PushOne, 0, 0, line);
            Emit(OpCode.Subtract, 0, 0, line);
            Emit(OpCode.StoreLocal, inner, 0, line);

            int innerTop = _code.Count;
            Emit(OpCode.LoadLocal, inner, 0, line);
            Emit(OpCode.PushZero, 0, 0, line);
            Emit(OpCode.GreaterEqual, 0, 0, line);
            int innerExit = EmitJump(OpCode.JumpIfFalse, line);

            Emit(OpCode.LoadLocal, keys, 0, line);
            Emit(OpCode.LoadLocal, inner, 0, line);
            Emit(OpCode.IndexRef, 1, capacity, line);
            Emit(OpCode.LoadHeap, 0, 0, line);
            Emit(OpCode.LoadLocal, key, 0, line);
            if (descending) EmitLessTest(keyType, line);
            else EmitGreaterTest(keyType, line);
            int innerDone = EmitJump(OpCode.JumpIfFalse, line);

            // one place up, key and item together
            Emit(OpCode.LoadLocal, keys, 0, line);
            Emit(OpCode.LoadLocal, inner, 0, line);
            Emit(OpCode.PushOne, 0, 0, line);
            Emit(OpCode.Add, 0, 0, line);
            Emit(OpCode.IndexRef, 1, capacity, line);
            Emit(OpCode.LoadLocal, keys, 0, line);
            Emit(OpCode.LoadLocal, inner, 0, line);
            Emit(OpCode.IndexRef, 1, capacity, line);
            Emit(OpCode.LoadHeap, 0, 0, line);
            Emit(OpCode.StoreHeap, 0, 0, line);

            Emit(OpCode.LoadLocal, cursor.BaseSlot, 0, line);
            Emit(OpCode.LoadLocal, inner, 0, line);
            Emit(OpCode.PushOne, 0, 0, line);
            Emit(OpCode.Add, 0, 0, line);
            Emit(OpCode.IndexRef, stride, capacity, line);
            Emit(OpCode.LoadLocal, cursor.BaseSlot, 0, line);
            Emit(OpCode.LoadLocal, inner, 0, line);
            Emit(OpCode.IndexRef, stride, capacity, line);
            Emit(OpCode.CopyHeap, stride, 0, line);

            Emit(OpCode.LoadLocal, inner, 0, line);
            Emit(OpCode.PushOne, 0, 0, line);
            Emit(OpCode.Subtract, 0, 0, line);
            Emit(OpCode.StoreLocal, inner, 0, line);
            Emit(OpCode.Jump, innerTop, 0, line);

            PatchJump(innerExit);
            PatchJump(innerDone);

            // and into the hole it opened
            Emit(OpCode.LoadLocal, keys, 0, line);
            Emit(OpCode.LoadLocal, inner, 0, line);
            Emit(OpCode.PushOne, 0, 0, line);
            Emit(OpCode.Add, 0, 0, line);
            Emit(OpCode.IndexRef, 1, capacity, line);
            Emit(OpCode.LoadLocal, key, 0, line);
            Emit(OpCode.StoreHeap, 0, 0, line);

            Emit(OpCode.LoadLocal, cursor.BaseSlot, 0, line);
            Emit(OpCode.LoadLocal, inner, 0, line);
            Emit(OpCode.PushOne, 0, 0, line);
            Emit(OpCode.Add, 0, 0, line);
            Emit(OpCode.IndexRef, stride, capacity, line);
            Emit(OpCode.LoadLocal, scratch, 0, line);
            Emit(OpCode.CopyHeap, stride, 0, line);

            Emit(OpCode.LoadLocal, outer, 0, line);
            Emit(OpCode.PushOne, 0, 0, line);
            Emit(OpCode.Add, 0, 0, line);
            Emit(OpCode.StoreLocal, outer, 0, line);
            Emit(OpCode.Jump, outerTop, 0, line);

            PatchJump(outerExit);
        }

        // ==================================================================
        //  distinct
        // ==================================================================

        /// <summary>
        /// Drops repeats, keeping the first of each. It compares against what has
        /// already been kept rather than building an index: a chip list is small, and
        /// a hash table would cost cells that have to be reserved at compile time.
        /// </summary>
        private void EmitDistinct(QueryCursor cursor, QueryStep step, int line)
        {
            var span = step.Span;

            if (cursor.Element.IsAggregate)
            {
                _diagnostics.Report(IZErrorCode.TypeMismatch, span,
                    "'distinct' compares elements, and " + cursor.Element.Display() +
                    " is a group of cells; go through 'select' first");
                return;
            }

            int capacity = cursor.Capacity;
            int kept = AllocateLocal(span);
            int index = AllocateLocal(span);
            int scan = AllocateLocal(span);
            int value = AllocateLocal(span);
            int repeated = AllocateLocal(span);

            Emit(OpCode.PushZero, 0, 0, line);
            Emit(OpCode.StoreLocal, kept, 0, line);
            Emit(OpCode.PushZero, 0, 0, line);
            Emit(OpCode.StoreLocal, index, 0, line);

            int top = _code.Count;
            Emit(OpCode.LoadLocal, index, 0, line);
            Emit(OpCode.LoadLocal, cursor.CountSlot, 0, line);
            Emit(OpCode.Less, 0, 0, line);
            int exit = EmitJump(OpCode.JumpIfFalse, line);

            Emit(OpCode.LoadLocal, cursor.BaseSlot, 0, line);
            Emit(OpCode.LoadLocal, index, 0, line);
            Emit(OpCode.IndexRef, 1, capacity, line);
            Emit(OpCode.LoadHeap, 0, 0, line);
            Emit(OpCode.StoreLocal, value, 0, line);

            Emit(OpCode.PushZero, 0, 0, line);
            Emit(OpCode.StoreLocal, repeated, 0, line);
            Emit(OpCode.PushZero, 0, 0, line);
            Emit(OpCode.StoreLocal, scan, 0, line);

            int scanTop = _code.Count;
            Emit(OpCode.LoadLocal, scan, 0, line);
            Emit(OpCode.LoadLocal, kept, 0, line);
            Emit(OpCode.Less, 0, 0, line);
            int scanExit = EmitJump(OpCode.JumpIfFalse, line);

            Emit(OpCode.LoadLocal, cursor.BaseSlot, 0, line);
            Emit(OpCode.LoadLocal, scan, 0, line);
            Emit(OpCode.IndexRef, 1, capacity, line);
            Emit(OpCode.LoadHeap, 0, 0, line);
            Emit(OpCode.LoadLocal, value, 0, line);
            EmitEqualTest(cursor.Element, line);
            int notEqual = EmitJump(OpCode.JumpIfFalse, line);

            Emit(OpCode.PushOne, 0, 0, line);
            Emit(OpCode.StoreLocal, repeated, 0, line);
            int foundIt = EmitJump(OpCode.Jump, line);

            PatchJump(notEqual);
            Emit(OpCode.LoadLocal, scan, 0, line);
            Emit(OpCode.PushOne, 0, 0, line);
            Emit(OpCode.Add, 0, 0, line);
            Emit(OpCode.StoreLocal, scan, 0, line);
            Emit(OpCode.Jump, scanTop, 0, line);

            PatchJump(scanExit);
            PatchJump(foundIt);

            Emit(OpCode.LoadLocal, repeated, 0, line);
            int skip = EmitJump(OpCode.JumpIfTrue, line);

            Emit(OpCode.LoadLocal, cursor.BaseSlot, 0, line);
            Emit(OpCode.LoadLocal, kept, 0, line);
            Emit(OpCode.IndexRef, 1, capacity, line);
            Emit(OpCode.LoadLocal, value, 0, line);
            Emit(OpCode.StoreHeap, 0, 0, line);

            Emit(OpCode.LoadLocal, kept, 0, line);
            Emit(OpCode.PushOne, 0, 0, line);
            Emit(OpCode.Add, 0, 0, line);
            Emit(OpCode.StoreLocal, kept, 0, line);

            PatchJump(skip);

            Emit(OpCode.LoadLocal, index, 0, line);
            Emit(OpCode.PushOne, 0, 0, line);
            Emit(OpCode.Add, 0, 0, line);
            Emit(OpCode.StoreLocal, index, 0, line);
            Emit(OpCode.Jump, top, 0, line);

            PatchJump(exit);

            // What is left is shorter, in the cells and in the cursor.
            Emit(OpCode.LoadLocal, kept, 0, line);
            Emit(OpCode.StoreLocal, cursor.CountSlot, 0, line);

            if (cursor.ListSlot >= 0)
            {
                Emit(OpCode.LoadLocal, cursor.ListSlot, 0, line);
                Emit(OpCode.LoadLocal, kept, 0, line);
                Emit(OpCode.StoreHeap, 0, 0, line);
            }
        }

        // ==================================================================
        //  Operations on the list itself
        // ==================================================================

        /// <summary>
        /// add, remove, removeAt and clear. The address of the list is already on
        /// the stack.
        /// </summary>
        private IZType EmitListOperation(QueryStep step, IZType sourceType,
                                         ExpressionSyntax source, int line)
        {
            var span = step.Span;

            if (step.Method.Kind == QueryKind.Clear)
            {
                if (step.Arguments.Count != 0)
                {
                    _diagnostics.Report(IZErrorCode.WrongArgumentCount, span,
                        "'clear' takes no arguments");
                }

                // One instruction zeroes the count and every cell behind it.
                Emit(OpCode.ClearHeap, sourceType.Size, 0, line);
                return IZType.Void;
            }

            if (sourceType.Kind != IZTypeKind.List)
            {
                Emit(OpCode.Pop, 0, 0, line);
                _diagnostics.Report(IZErrorCode.TypeMismatch, source.Span,
                    "'" + step.Name + "' works on a list; " + sourceType.Display() +
                    " has a fixed length, and every cell of it is content");
                Emit(OpCode.PushZero, 0, 0, line);
                return IZType.Error;
            }

            switch (step.Method.Kind)
            {
                case QueryKind.Add: return EmitListAdd(step, sourceType, line);
                case QueryKind.Remove: return EmitListRemove(step, sourceType, line);
                default: return EmitListRemoveAt(step, sourceType, line);
            }
        }

        /// <summary>
        /// Appends the item and answers whether there was room for it.
        ///
        /// A struct item goes in the same way a number does, by value: what lands in
        /// the list is a copy of the cells, so the variable it came from can be
        /// filled in again for the next one.
        /// </summary>
        private IZType EmitListAdd(QueryStep step, IZType listType, int line)
        {
            var span = step.Span;
            var element = listType.ElementType!;
            int stride = element.Size;
            int capacity = listType.Length;

            int list = AllocateLocal(span);
            Emit(OpCode.StoreLocal, list, 0, line);

            // The item is read before the capacity is checked, so a full list does
            // not quietly skip whatever computing it does.
            int value = AllocateLocal(span);
            var valueType = EmitValueArgument(step, IZType.Error);
            if (!valueType.IsAssignableTo(element))
            {
                _diagnostics.Report(IZErrorCode.TypeMismatch,
                    step.Arguments.Count > 0 ? step.Arguments[0].Span : span,
                    "this list holds " + element.Display() + ", but it is given " +
                    valueType.Display());
            }
            Emit(OpCode.StoreLocal, value, 0, line);

            Emit(OpCode.LoadLocal, list, 0, line);
            Emit(OpCode.LoadHeap, 0, 0, line);
            EmitConstant(capacity, line);
            Emit(OpCode.Less, 0, 0, line);
            int full = EmitJump(OpCode.JumpIfFalse, line);

            Emit(OpCode.LoadLocal, list, 0, line);
            Emit(OpCode.FieldRef, 1, 0, line);
            Emit(OpCode.LoadLocal, list, 0, line);
            Emit(OpCode.LoadHeap, 0, 0, line);
            Emit(OpCode.IndexRef, stride, capacity, line);

            Emit(OpCode.LoadLocal, value, 0, line);
            Emit(element.IsAggregate ? OpCode.CopyHeap : OpCode.StoreHeap, stride, 0, line);

            Emit(OpCode.LoadLocal, list, 0, line);
            Emit(OpCode.LoadLocal, list, 0, line);
            Emit(OpCode.LoadHeap, 0, 0, line);
            Emit(OpCode.PushOne, 0, 0, line);
            Emit(OpCode.Add, 0, 0, line);
            Emit(OpCode.StoreHeap, 0, 0, line);

            Emit(OpCode.PushOne, 0, 0, line);
            int done = EmitJump(OpCode.Jump, line);

            PatchJump(full);
            Emit(OpCode.PushZero, 0, 0, line);
            PatchJump(done);

            return IZType.Bool;
        }

        /// <summary>
        /// Takes the item at an index out, and slides the rest down so the order is
        /// kept. False when the index is outside the list, which is the answer a
        /// program can still do something about.
        /// </summary>
        private IZType EmitListRemoveAt(QueryStep step, IZType listType, int line)
        {
            var span = step.Span;

            int list = AllocateLocal(span);
            Emit(OpCode.StoreLocal, list, 0, line);

            int index = AllocateLocal(span);
            EmitValueArgument(step, IZType.Num);
            Emit(OpCode.StoreLocal, index, 0, line);

            return EmitRemoveIndex(list, index, listType, span, line);
        }

        /// <summary>
        /// Takes the first item equal to the value out. It is 'indexOf' followed by
        /// 'removeAt', which is what makes it false when the value is not in there.
        ///
        /// The item has to be comparable, so a list of structs removes by index or
        /// through a field: what "the same job" means is the program's decision.
        /// </summary>
        private IZType EmitListRemove(QueryStep step, IZType listType, int line)
        {
            var span = step.Span;
            var element = listType.ElementType!;
            int capacity = listType.Length;

            int list = AllocateLocal(span);
            Emit(OpCode.StoreLocal, list, 0, line);

            int wanted = AllocateLocal(span);
            var wantedType = EmitValueArgument(step, IZType.Error);
            Emit(OpCode.StoreLocal, wanted, 0, line);

            if (element.IsAggregate)
            {
                _diagnostics.Report(IZErrorCode.TypeMismatch, span,
                    "'remove' compares items, and " + element.Display() + " is a group of " +
                    "cells; find it with 'indexOf' over a field and use 'removeAt'");
                Emit(OpCode.PushZero, 0, 0, line);
                return IZType.Error;
            }

            if (wantedType != IZType.Error && !wantedType.IsAssignableTo(element))
            {
                _diagnostics.Report(IZErrorCode.TypeMismatch,
                    step.Arguments.Count > 0 ? step.Arguments[0].Span : span,
                    "this list holds " + element.Display() + ", and this looks for " +
                    wantedType.Display());
            }

            // Where it is, or -1.
            int index = AllocateLocal(span);
            int scan = AllocateLocal(span);
            int count = AllocateLocal(span);

            EmitConstant(-1, line);
            Emit(OpCode.StoreLocal, index, 0, line);
            Emit(OpCode.PushZero, 0, 0, line);
            Emit(OpCode.StoreLocal, scan, 0, line);
            Emit(OpCode.LoadLocal, list, 0, line);
            Emit(OpCode.LoadHeap, 0, 0, line);
            Emit(OpCode.StoreLocal, count, 0, line);

            int top = _code.Count;
            Emit(OpCode.LoadLocal, scan, 0, line);
            Emit(OpCode.LoadLocal, count, 0, line);
            Emit(OpCode.Less, 0, 0, line);
            int exit = EmitJump(OpCode.JumpIfFalse, line);

            Emit(OpCode.LoadLocal, list, 0, line);
            Emit(OpCode.FieldRef, 1, 0, line);
            Emit(OpCode.LoadLocal, scan, 0, line);
            Emit(OpCode.IndexRef, element.Size, capacity, line);
            Emit(OpCode.LoadHeap, 0, 0, line);
            Emit(OpCode.LoadLocal, wanted, 0, line);
            EmitEqualTest(element, line);
            int keepLooking = EmitJump(OpCode.JumpIfFalse, line);

            Emit(OpCode.LoadLocal, scan, 0, line);
            Emit(OpCode.StoreLocal, index, 0, line);
            int found = EmitJump(OpCode.Jump, line);

            PatchJump(keepLooking);
            Emit(OpCode.LoadLocal, scan, 0, line);
            Emit(OpCode.PushOne, 0, 0, line);
            Emit(OpCode.Add, 0, 0, line);
            Emit(OpCode.StoreLocal, scan, 0, line);
            Emit(OpCode.Jump, top, 0, line);

            PatchJump(exit);
            PatchJump(found);

            // -1 falls outside the list, so the same code answers false.
            return EmitRemoveIndex(list, index, listType, span, line);
        }

        /// <summary>
        /// The shared half of 'remove' and 'removeAt': the index is already in a
        /// local, and anything outside the list answers false.
        /// </summary>
        private IZType EmitRemoveIndex(int list, int index, IZType listType,
                                       SourceSpan span, int line)
        {
            int stride = listType.ElementType!.Size;
            int capacity = listType.Length;

            int count = AllocateLocal(span);
            Emit(OpCode.LoadLocal, list, 0, line);
            Emit(OpCode.LoadHeap, 0, 0, line);
            Emit(OpCode.StoreLocal, count, 0, line);

            var outside = new List<int>();

            Emit(OpCode.LoadLocal, index, 0, line);
            Emit(OpCode.PushZero, 0, 0, line);
            Emit(OpCode.GreaterEqual, 0, 0, line);
            outside.Add(EmitJump(OpCode.JumpIfFalse, line));

            Emit(OpCode.LoadLocal, index, 0, line);
            Emit(OpCode.LoadLocal, count, 0, line);
            Emit(OpCode.Less, 0, 0, line);
            outside.Add(EmitJump(OpCode.JumpIfFalse, line));

            int last = AllocateLocal(span);
            Emit(OpCode.LoadLocal, count, 0, line);
            Emit(OpCode.PushOne, 0, 0, line);
            Emit(OpCode.Subtract, 0, 0, line);
            Emit(OpCode.StoreLocal, last, 0, line);

            int cursor = AllocateLocal(span);
            Emit(OpCode.LoadLocal, index, 0, line);
            Emit(OpCode.StoreLocal, cursor, 0, line);

            int top = _code.Count;
            Emit(OpCode.LoadLocal, cursor, 0, line);
            Emit(OpCode.LoadLocal, last, 0, line);
            Emit(OpCode.Less, 0, 0, line);
            int exit = EmitJump(OpCode.JumpIfFalse, line);

            Emit(OpCode.LoadLocal, list, 0, line);
            Emit(OpCode.FieldRef, 1, 0, line);
            Emit(OpCode.LoadLocal, cursor, 0, line);
            Emit(OpCode.IndexRef, stride, capacity, line);
            Emit(OpCode.LoadLocal, list, 0, line);
            Emit(OpCode.FieldRef, 1, 0, line);
            Emit(OpCode.LoadLocal, cursor, 0, line);
            Emit(OpCode.PushOne, 0, 0, line);
            Emit(OpCode.Add, 0, 0, line);
            Emit(OpCode.IndexRef, stride, capacity, line);
            Emit(OpCode.CopyHeap, stride, 0, line);

            Emit(OpCode.LoadLocal, cursor, 0, line);
            Emit(OpCode.PushOne, 0, 0, line);
            Emit(OpCode.Add, 0, 0, line);
            Emit(OpCode.StoreLocal, cursor, 0, line);
            Emit(OpCode.Jump, top, 0, line);

            PatchJump(exit);

            // The cell that was freed goes back to being capacity, not content.
            Emit(OpCode.LoadLocal, list, 0, line);
            Emit(OpCode.FieldRef, 1, 0, line);
            Emit(OpCode.LoadLocal, last, 0, line);
            Emit(OpCode.IndexRef, stride, capacity, line);
            Emit(OpCode.ClearHeap, stride, 0, line);

            Emit(OpCode.LoadLocal, list, 0, line);
            Emit(OpCode.LoadLocal, last, 0, line);
            Emit(OpCode.StoreHeap, 0, 0, line);

            Emit(OpCode.PushOne, 0, 0, line);
            int done = EmitJump(OpCode.Jump, line);

            foreach (int jump in outside) PatchJump(jump);
            Emit(OpCode.PushZero, 0, 0, line);
            PatchJump(done);

            return IZType.Bool;
        }
    }
}
