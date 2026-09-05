namespace IZLang.Vm
{
    /// <summary>
    /// The IZVm instruction set.
    ///
    /// The VM is a stack machine over <c>double</c>. Because types are checked at
    /// compile time there is no runtime type tag: a bool is 0.0/1.0, a device is
    /// the pin index, and a str is a handle into the string heap, NaN boxed into
    /// the slot (see <see cref="StrValue"/>). That keeps the stack a plain
    /// <c>double[]</c> - no boxing, and nothing to collect while a tick runs.
    ///
    /// An array or a struct does not fit in one <c>double</c>, so it lives in the
    /// heap and the stack carries its address. The address is a plain number too,
    /// which is why the heap needs no new value representation - only the five
    /// opcodes that address it.
    ///
    /// Operand fields: every instruction carries two integers (A and B), whose
    /// meaning is documented per opcode.
    /// </summary>
    public enum OpCode
    {
        /// <summary>Does nothing. Used as filler when patching jumps.</summary>
        Nop = 0,

        // ---- constants and stack ----

        /// <summary>Pushes Constants[A].</summary>
        PushConst,
        /// <summary>Pushes 0.0.</summary>
        PushZero,
        /// <summary>Pushes 1.0.</summary>
        PushOne,
        /// <summary>Drops the top of the stack.</summary>
        Pop,
        /// <summary>Duplicates the top of the stack.</summary>
        Dup,

        // ---- variables ----

        /// <summary>Pushes the current frame's local in slot A.</summary>
        LoadLocal,
        /// <summary>Pops and stores into the current frame's local, slot A.</summary>
        StoreLocal,
        /// <summary>Pushes the global in slot A.</summary>
        LoadGlobal,
        /// <summary>Pops and stores into the global in slot A.</summary>
        StoreGlobal,

        // ---- arithmetic ----

        Add, Subtract, Multiply, Divide, Modulo,
        /// <summary>Negates the top of the stack.</summary>
        Negate,

        // ---- bitwise (convert to 64-bit integer, operate, convert back) ----

        BitAnd, BitOr, BitXor, BitNot,
        ShiftLeft, ShiftRight,

        // ---- comparison: consume two, push 0.0 or 1.0 ----

        Equal, NotEqual,
        Less, LessEqual, Greater, GreaterEqual,

        /// <summary>Logical negation: 0.0 becomes 1.0, anything else becomes 0.0.</summary>
        Not,

        // ---- control flow (A = absolute address) ----

        Jump,
        /// <summary>Jumps when the top is false; always pops.</summary>
        JumpIfFalse,
        /// <summary>Jumps when the top is true; always pops.</summary>
        JumpIfTrue,
        /// <summary>Short circuit for '&amp;&amp;': jumps when false, keeping the top.</summary>
        JumpIfFalseKeep,
        /// <summary>Short circuit for '||': jumps when true, keeping the top.</summary>
        JumpIfTrueKeep,

        // ---- functions ----

        /// <summary>Calls Functions[A] with B arguments already on the stack.</summary>
        Call,
        /// <summary>Returns without a value.</summary>
        Return,
        /// <summary>Returns the value on the top of the stack.</summary>
        ReturnValue,
        /// <summary>Calls the builtin with id A using B arguments.</summary>
        CallBuiltin,

        // ---- devices ----

        /// <summary>Reads pin A, LogicType B. Pushes the value.</summary>
        DeviceLoad,
        /// <summary>Pops a value and writes it to pin A, LogicType B.</summary>
        DeviceStore,
        /// <summary>Reads pin A, LogicSlotType B, with the slot index on top of the stack.</summary>
        DeviceSlotLoad,
        /// <summary>Batch read: prefab hash on top, LogicType A, aggregation mode B.</summary>
        BatchLoad,
        /// <summary>Batch write: value on top, hash below it, LogicType A.</summary>
        BatchStore,
        /// <summary>Named batch read: name hash on top, prefab hash below it, LogicType A, aggregation B.</summary>
        BatchNamedLoad,
        /// <summary>Named batch write: value on top, name hash below it, LogicType A.</summary>
        BatchNamedStore,
        /// <summary>
        /// Batch slot read: slot index on top, prefab hash below it, LogicSlotType A,
        /// aggregation mode B. The slot counterpart of <see cref="BatchLoad"/>.
        /// </summary>
        BatchSlotLoad,
        /// <summary>
        /// Named batch slot read: slot index on top, name hash below it, prefab hash
        /// below that, LogicSlotType A, aggregation mode B.
        /// </summary>
        BatchNamedSlotLoad,

        // ---- heap: arrays and structs ----

        /// <summary>
        /// Reserves the aggregate at offset A of the current frame's heap region,
        /// B cells long: zeroes it and pushes its address. Re-running the
        /// declaration in a loop hands back the same address, cleared again.
        /// </summary>
        NewAggregate,
        /// <summary>Pops an address and pushes it offset by A: the address of a struct field.</summary>
        FieldRef,
        /// <summary>
        /// Pops the index and the base address and pushes the address of the element:
        /// base + index * A. B is the array length, checked against the index.
        /// </summary>
        IndexRef,
        /// <summary>Pops an address and pushes the cell it points at.</summary>
        LoadHeap,
        /// <summary>Pops the value and then the address, and writes the cell.</summary>
        StoreHeap,

        // ---- tick control ----

        /// <summary>Gives the tick back to the game; resumes at the next instruction.</summary>
        Yield,
        /// <summary>Sleeps for N seconds (top of the stack) and resumes afterwards.</summary>
        Sleep,
        /// <summary>Ends the program.</summary>
        Halt,

        // ---- strings ----
        // Appended at the end on purpose: the ids above keep the values they had.

        /// <summary>Pushes the handle of the program's string A.</summary>
        PushStr,
        /// <summary>Pops two strings and pushes the handle of the two joined.</summary>
        StrConcat,
        /// <summary>
        /// Pops two strings and pushes -1, 0 or 1, ordinal. Every str comparison is
        /// built on this one: '==' is a compare against zero, and so is '&lt;'.
        /// </summary>
        StrCompare,

        // ---- lists ----
        // A list is a run of cells opening with its count, which is the one thing
        // the heap opcodes above could not already express.

        /// <summary>
        /// Pops the index and the address of a list, and pushes the address of the
        /// element: list + 1 + index * A. The index is checked against the count in
        /// the list's first cell, not against the capacity in B - which is what
        /// makes reading past the end an error instead of stale capacity.
        /// </summary>
        ListIndexRef,
        /// <summary>Pops the source address and then the destination, and copies A cells.</summary>
        CopyHeap,
        /// <summary>Pops an address and zeroes A cells from it.</summary>
        ClearHeap,

        /// <summary>Stops the program with the message in Strings[A].</summary>
        Trap,

        /// <summary>
        /// Pushes 1.0 when pin A has a device connected, and 0.0 when it is empty.
        /// The one device instruction that cannot fail: an empty pin is an answer,
        /// not a runtime error.
        /// </summary>
        DevicePresent,
    }
}
