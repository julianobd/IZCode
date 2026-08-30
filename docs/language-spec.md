# IZ Language Specification (v0.1)

IZ is the language of the **StationeersIZCode** mod. It replaces IC10 with a
structured language, with types, real functions and recursive calls, executed by
its own VM (`IZVm`) hosted inside the game's Programmable Chip.

File extension: `.iz`

---

## 1. Execution model

The chip executes **up to `IZLimits.OpsPerTick` instructions per game tick**.
When the budget runs out, the VM **freezes its complete state** (PC, operand
stack, call frames, locals) and resumes on the next tick exactly where it
stopped. That is different from IC10, which restarts from the top every tick.

An IZ program has two ways of handing the tick back to the game:

| Way | Effect |
|---|---|
| `yield;` | hands the tick back immediately; resumes at the next instruction |
| budget exhausted | automatic preemption; resumes at the next instruction |

In other words: `loop { ... yield; }` is the idiomatic equivalent of the IC10
main loop, but you are **not required** to write `yield`: a loop without
`yield` is simply preempted and keeps running, without freezing the game.

### 1.1 Entry point

Execution starts at `fn main()`. When `main` returns, the chip stops (state
`Halted`) until it is restarted.

---

## 2. Types

| Type | Description |
|---|---|
| `num` | 64-bit floating point (IEEE-754). The one numeric type. |
| `bool` | `true` / `false` |
| `str` | ASCII text, immutable, built and compared while the program runs (section 11) |
| `dev` | device handle (pin `d0`–`d5`, `db`, or a reference by id) |
| `T[N]` | array of exactly `N` values of type `T` (section 10) |
| `list T[N]` | up to `N` values of type `T`, and how many are in use (section 12) |
| a `struct` | a group of named fields, declared by the program (section 10) |

There are no separate integers: as in IC10, everything is `num`. Bitwise
operations (`&`, `|`, `^`, `<<`, `>>`) convert to a 64-bit integer, operate, and
convert back.

`num` and `bool` are **not** implicitly interchangeable. `if x` requires a
`bool`; write `if x != 0`. That removes IC10's biggest source of silent bugs.

### 2.1 Declarations

```iz
var  counter = 0;              // inferred: num, mutable
var  active: bool = false;     // explicit annotation
const MAX = 101.325;           // constant, folded at compile time
```

`const` only accepts expressions that can be evaluated at compile time.

A declaration that names its type may leave the value out, and starts zeroed:

```iz
var samples: num[8];           // eight cells, all 0
var p: Point;                  // every field 0
var label: str;                // ""
```

The type annotation is what makes that legal: with neither a type nor a value
there would be nothing left to infer from.

---

## 3. Devices

```iz
device pump   = d0;            // housing pin
device sensor = d1;
device self   = db;            // the device the chip is installed in
device lights = named(StructureWallLight, #"hangar");   // a group on the network
```

The six pins reach whatever is wired to the housing. `db` reaches the housing
itself, and is the only way to it: nothing can be wired to the thing the chip is
already inside.

What `db` turns out to be depends on where the chip went. In a circuit housing
it is the housing. In a hardsuit, which holds a chip in its own slot, it is the
suit - so a program can read the suit's `PressureExternal` and drive its AC.
There the six pins are the wearer's slots rather than cables:

| pin | on a circuit housing | on a suit |
|---|---|---|
| `d0`–`d5` | the six wired devices | helmet, backpack, toolbelt, glasses, left hand, right hand |
| `db` | the housing | the suit |

A device name can also stand for a batch selector rather than a pin, which is
how a program reaches more than six devices. See [3.4](#34-devices-on-the-network).

Reading and writing go through property access, validated at compile time
against the game's `LogicType` table:

```iz
var p = sensor.Pressure;       // compile error if a GasSensor cannot read Pressure
pump.On = true;                // error if the property is read-only
```

### 3.1 Slots

```iz
var q = chute.slot[0].Quantity;
```

### 3.2 Hash literals

`#"StructureWallLight"` produces the CRC32 of the prefab name at compile time,
with no runtime cost. It is the same value as IC10's `HASH("...")`.

### 3.3 Batch operations

The housing has only six pins. A batch operation reaches any number of devices
on the same data network, identified by the **prefab hash**, with no pin spent
on any of them. It can be written where it is used, as below, or given a
name once with `device` - see [3.4](#34-devices-on-the-network).

```iz
all(StructureWallLight).On = true;             // every device of that prefab
named("Corridor Light").On = false;            // any prefab, by label
named(StructureVolumePump, "north").On = true; // prefab AND label
```

A batch read aggregates by **average**. If no device matches the filter, the
result is `0`.

```iz
var average = all(StructureGasSensor).Pressure;
```

#### How the prefab is written

Three forms, all producing the same constant at compile time:

```iz
all(StructureWallLight)                  // bare name
all(#"StructureWallLight")               // hash literal
const LIGHT = #"StructureWallLight";
all(LIGHT)                               // constant
```

**Resolution rule:** an identifier counts as a raw prefab name *only if nothing
is declared with that name*. If a `const`, `var` or parameter with that name
exists, its value is used.

```iz
const PUMP = #"StructureVolumePump";
all(PUMP).On = true;                  // uses the hash of StructureVolumePump
all(StructureActiveVent).On = true;   // nothing declared: raw prefab name
```

The constant is the preferred form when the same prefab appears more than once:
a typo in the name produces a perfectly valid hash that matches nothing, and
fails silently. Declaring it once keeps the mistake in a single place.

The `named` label written as a literal or as a `const` also becomes a hash at
compile time, so it can be kept in a constant and passed as a function
parameter:

```iz
const NORTH = #"north";

fn turn_off(wing: num) {
    named(StructureVolumePump, wing).On = false;
}

fn main() { turn_off(NORTH); }
```

A prefab or a label whose text is only known while the program runs is hashed
then, by the same CRC32 - which is what makes a group of devices addressable by
a name the program itself assembles:

```iz
var wing = "north";
named("vent-" + wing).On = true;      // hashed at runtime
```

### 3.4 Devices on the network

A selector can be given a name, with the same `device` declaration that binds a
pin:

```iz
device led     = named(StructureDiode, #"led-dev");
device lights  = all(StructureWallLight);
device hangar  = named("hangar");            // any prefab carrying that label
```

From there the name is used exactly like a device on a pin:

```iz
led.On = true;                    // reaches every matching device
var p = lights.Setting;           // averaged over all of them, as a batch read is
```

This is what a base with forty lights needs. The six pins run out long before
the devices do, and writing the prefab and the label once at the top of the file
keeps a typo in one place instead of on every line that mentions them.

A selector spends no pin, but it reaches no further than a pin does: it matches
the devices on the same data network as the housing, so the data cable still has
to get there. A device the network never reached is not addressable at all, by a
name or otherwise, and a batch that matches nothing writes nothing - the VM logs
a warning rather than letting it pass for a value of zero.

Both operands have to be known at compile time - a prefab name, a hash literal,
a `const`, or text joined from them. A device is a name for a fixed place in the
world, so it cannot depend on a value the program computes:

```iz
var wing = "north";
device vents = named("vent-" + wing);   // error: not known at compile time
named("vent-" + wing).On = true;        // fine: the selector written where it is used
```

The declaration costs nothing at runtime: the two hashes are folded when the
name is declared, so `led.On = true` compiles to exactly what the inline
`named(...)` form compiles to.

Two things a pin can do that a selector cannot:

| | pin | selector |
|---|---|---|
| `pump.Setting += 1` | yes | no - a batch has no single value to read back |
| `chute.slot[0].Quantity` | yes | no - a slot belongs to one device |

---

## 4. Control flow

```iz
if cond { ... } else if other { ... } else { ... }

while cond { ... }

loop { ... }                   // infinite loop

for i in 0..10 { ... }         // 0 inclusive, 10 exclusive
for i in 0..=10 { ... }        // 10 inclusive

break;  continue;
```

There is no `goto` and there are no jump labels. Parentheses around the
condition are not required, but the braces **are**, which removes the
dangling-else.

---

## 5. Functions

```iz
fn clamp(x: num, lo: num, hi: num) -> num {
    if x < lo { return lo; }
    if x > hi { return hi; }
    return x;
}

fn warn() {                    // no return value
    beacon.On = true;
}
```

Recursion is allowed, bounded by `IZLimits.MaxCallDepth`. Overflowing it raises
the `CallStackOverflow` runtime error.

A function **cannot return an array, a list or a struct**. Its cells belong to the call
frame and are released on the return, so the address would point at memory the
next call reuses. Pass the aggregate in as a parameter and fill it in instead:
parameters travel by reference, so what the function writes is what the caller
reads back (section 10.4).

---

## 6. Operators

By precedence, from weakest to strongest:

| Level | Operators | Associativity |
|---|---|---|
| 1 | `\|\|` | left (short circuit) |
| 2 | `&&` | left (short circuit) |
| 3 | `==` `!=` | left |
| 4 | `<` `<=` `>` `>=` | left |
| 5 | `\|` | left |
| 6 | `^` | left |
| 7 | `&` | left |
| 8 | `<<` `>>` | left |
| 9 | `+` `-` | left |
| 10 | `*` `/` `%` | left |
| 11 | `-x` `!x` `~x` | prefix, right |
| 12 | `a.b` `a[i]` `f(x)` | postfix |

Assignment (`=`, `+=`, `-=`, `*=`, `/=`, `%=`) is a **statement**, not an
expression: `if (a = b)` does not compile.

---

## 7. Builtins

```
abs ceil floor round trunc sqrt exp log
sin cos tan asin acos atan atan2
min max pow sign clamp
len(array)                  // the declared length, folded at compile time
len(list)                   // the capacity; xs.count is how much is in use
rand()                      // [0,1)
nan() inf() isnan(x)
sleep(seconds)              // hands the tick back for N seconds
yield                       // a statement, not a function
```

Over text (section 11):

```
len(s)                      // characters
hash(s)                     // the CRC32 all() and named() use
char(s, i)                  // ASCII code at i, -1 outside the string
chr(code)                   // one character from its code
sub(s, start, count)        // both bounds clamped
find(s, needle)             // where it starts, -1 when absent
text(x)                     // a num as text
fixed(x, decimals)          // a num as text with a fixed number of decimals
parse(s)                    // text back into a num, nan when it is not one
```

---

## 8. Errors

Compile errors are reported with the line, the column and the snippet of source
code, and they show up in the in-game editor while you type, underlining the
line and listing the message at the bottom, before any CONFIRM. Runtime errors
stop the chip and light the housing's error LED, exactly like an IC10 exception.

There are also **warnings**, which do not stop the program from running. Today
there is one: `IZ320`, a name declared and never used; it applies to `var`,
`const` and `device`, but not to function parameters nor to a `for` index. The
warnings go quiet when the compilation already has an error: there the name may
be "unused" only because the code that would use it is exactly what fails to
compile.

---

## 9. Complete example

```iz
device sensor = d0;
device pump   = d1;
device display = d2;

const TARGET = 101.325;
const MARGIN = 5.0;

fn main() {
    loop {
        var p = sensor.Pressure;

        if p > TARGET + MARGIN {
            pump.On = true;
        } else if p < TARGET - MARGIN {
            pump.On = false;
        }

        display.Setting = round(p);
        yield;
    }
}
```

---

## 10. Arrays and structs

Everything up to here fits in one `double`. An array or a struct does not, so
it lives in the VM's **heap** and what the variable holds is its address. All of
the addressing is resolved at compile time; there is no allocator to call and no
garbage to collect at runtime.

### 10.1 Arrays

```iz
var samples: num[8];               // eight cells, zeroed
var levels  = [10, 20, 30];        // inferred: num[3]
var flags: bool[2] = [false, true];

samples[0] = sensor.Pressure;
samples[1] += 5;
var first = samples[0];
```

The length is part of the type and has to be known at compile time, so it can be
a literal or a `const`, never a variable. `num[3]` and `num[4]` are **different
types**: a function taking `num[3]` does not accept a `num[4]`.

`len(a)` answers that length. It is folded into a constant, so it costs nothing:

```iz
for i in 0..len(samples) { total += samples[i]; }
```

Dimensions read left to right, as in C: `num[2][3]` is 2 groups of 3, and
`m[1][2]` is the last cell.

```iz
var m: num[2][3];
var grid: num[2][2] = [[1, 2], [3, 4]];
```

An index is truncated, not rounded (`a[1.9]` is `a[1]`, the same way a slot
index behaves). An index outside the array is the `IndexOutOfRange` runtime
error - and when the index is a constant, a compile error instead.

### 10.2 Structs

```iz
struct Gauge {
    total:   num;
    cursor:  num;
    samples: num[8];
}

var a: Gauge;
a.total = 0;
a.samples[3] = 12;
```

A `struct` is declared outside any function, and may name another one declared
further down the file. A field holds the value, not a reference to it, so a
struct cannot contain itself: the layout is flat, and reading `t.reading.value`
is one addition, not a chain of indirections.

Two structs with the same fields are still different types: what matches is the
declaration, not the shape.

Fields may be `num`, `bool`, `str`, an array, or another struct. A field is
never a `dev` - a device is a pin (or a selector) settled at compile time, not a
value.

### 10.3 What cannot be done

| | why |
|---|---|
| `a = b;` between arrays or structs | the name is bound once, at the declaration; assign to the elements or the fields |
| `a == b` between arrays or structs | it would compare addresses, which is never the question |
| returning an array or a struct | the cells are released on the return |
| `const a: num[4]` | a `const` is folded into every use, and an aggregate has nothing to fold |
| an array of `dev` | same reason a field is never a `dev` |
| a list of arrays, or of lists | the item would carry a length of its own, which `count` already answers |

### 10.4 Lifetime and passing

A parameter of aggregate type carries the address, so the function works on the
caller's cells:

```iz
fn fill(xs: num[3]) { xs[1] = 42; }

fn main() {
    var a: num[3];
    fill(a);                       // a[1] is 42 from here on
}
```

`var b = a;` between aggregates is the same thing: `b` becomes a second name for
the same cells, not a copy.

The cells of a declaration belong to the call that ran it, and are released when
it returns, which has three consequences worth knowing:

- a recursive call gets **its own** array, not the caller's;
- a declaration inside a loop hands back the same cells on every lap, **cleared
  again** - it never leaks;
- a global aggregate lives in the entry frame, which only unwinds when the
  program ends, so it keeps its values across ticks like any other global.

Running out of cells is the `HeapOverflow` runtime error, which is what deep
recursion over big arrays hits (`IZLimits.HeapSize`, 2048 cells shared by every
live frame).

---

## 11. Text

A `str` is an ordinary value: it goes in a variable, a parameter, a return, an
array cell or a struct field, and travels through the same slots a `num` does.
What it holds is a handle into the VM's string table, NaN boxed into the
`double` every slot is - which is why a str needs no separate storage and no
new kind of variable.

A cell that was never assigned reads as the empty string, so a fresh struct
field or an untouched global is `""` and not a surprise.

### 11.1 Operators

| | |
|---|---|
| `a + b` | joins two str; it does **not** mix with a num, `text(x)` is how a number gets in |
| `s += b` | appends |
| `==` `!=` | compares the text, not the storage: two strings built separately are equal when they read the same |
| `<` `<=` `>` `>=` | ordinal order, so `"B" < "a"` |

```iz
var side = "north";
var label = "vent-" + side;          // "vent-north"

if label == "vent-north" { ... }
```

A `const` may hold text, and two of them join at compile time:

```iz
const SIDE = "north";
const LABEL = SIDE + "-wing";        // folded; costs nothing at runtime
```

### 11.2 Where the text goes

A str is not a number, so a device property and a batch write still refuse it -
the game's logic network carries numbers only. What a str reaches the world
through is the hash:

```iz
all("Structure" + kind).On = true;   // hashed while it runs
named("vent-" + side).On = true;
var h = hash("StructureWallLight");  // the same value as #"StructureWallLight"
```

`hash` of a literal or of a str `const` is folded at compile time, so the forms
that were free before str became a value are still free.

### 11.3 Memory

Text built at runtime lives in a table of `IZLimits.MaxStrings` slots (512),
each up to `IZLimits.MaxStringLength` characters (256). Two rules keep it
bounded:

- **the same text always lands on the same slot**, so a loop that rebuilds
  `"tag-" + suffix` every tick allocates once, not once per tick;
- when the table fills, the VM **collects**: it marks every handle reachable
  from the operand stack, the locals, the globals and the heap, and frees the
  slots nobody points at. It only runs when there is no room left, so an
  ordinary tick never pays for it.

A program that genuinely holds on to more than 512 distinct strings at once, or
builds one longer than 256 characters, stops with the `StringOverflow` runtime
error rather than growing without a bound.

---

## 12. Lists and queries

An array has a length and nothing else: every cell is content. A **list** is the
same run of cells with one number in front of it saying how many are in use.

```iz
var jobs: list num[8];             // room for eight, holding none
var seed: list num[8] = [10, 20];  // count 2, six cells still free
```

The capacity is part of the type and is decided at compile time, exactly like an
array's length: `list num[4]` and `list num[8]` are different types. What
changes while the program runs is the count.

| | |
|---|---|
| `len(xs)` | the capacity, folded into a constant |
| `xs.count` | how many items are in it right now; read only |
| `xs[i]` | the item at `i`, checked **against the count** |
| `xs.add(v)` | appends `v`; `false` when the list is full |
| `xs.remove(v)` | takes the first item equal to `v` out; `false` when it is not there |
| `xs.removeAt(i)` | takes item `i` out and slides the rest down; `false` outside |
| `xs.clear()` | empties it |

`xs[5]` on a list holding three items is the `IndexOutOfRange` runtime error,
even when the capacity is eight: the cells past the count are room, not content.
That is the whole difference from an array, and it is why an index cannot be
checked at compile time here the way an array's constant index is.

An item is anything a struct field may be - `num`, `bool`, `str` or a struct -
but not an array or another list: an item that carried a length of its own would
answer a question `count` already answers once.

```iz
struct Job { id: num; temp: num; done: bool; }

var queue: list Job[8];

var job: Job;                      // a Job of its own, zeroed
job.id = 7;
queue.add(job);                    // the list keeps a copy of its cells

var last = queue[queue.count - 1]; // reading gives a name for the cells, not a copy
last.done = true;                  // so this marks it in the list
```

`add` copies and the index does not, and the difference is the same one arrays
already have: an aggregate travels by its address, so `add` has to write the
cells somewhere, and the somewhere is the list. `remove` compares items, so a
list of structs removes by index, or finds the item with `indexOf` over a field
first.

### 12.1 The query methods

A list, and an array, answer the questions you would otherwise write a loop for.

**Stages** - they hand back a sequence, so more may follow:

```
where(x => bool)        keeps what passes the test
select(x => value)      one value out of each item
take(n)  skip(n)        the first n, everything after the first n
takeWhile(x => bool)    from the start, while the test passes
skipWhile(x => bool)    from the first item that fails the test
orderBy(x => key)       sorted by the key, ascending
orderByDesc(x => key)   sorted by the key, descending
reverse()               back to front
distinct()              drops repeats, keeping the first of each
```

**Terminals** - they hand back one value, and end the chain:

```
count()  count(x => bool)          how many
sum(...)  avg(...)                 added up, averaged; 0 over nothing
min(...)  max(...)                 the smallest, the biggest; 0 over nothing
any()  any(x => bool)  all(f)      is there one, do they all pass
first(...)  last(...)              the item itself; a runtime error over nothing
firstOr(v)  lastOr(v)              the same, answering v when nothing matched
contains(v)  indexOf(v)            is it in there, and where
into(target)                       fills an existing list; how many got there
```

`sum`, `avg`, `min` and `max` take an optional selector, so `xs.sum(f)` is
`xs.select(f).sum()`. `count`, `any`, `first` and `last` take an optional test,
so `xs.first(f)` is `xs.where(f).first()`.

```iz
var mean  = rooms.avg(x => x.temp);
var hot   = rooms.count(x => x.temp > 30);
var worst = rooms.orderByDesc(x => x.temp).first();
var top3  = rooms.orderByDesc(x => x.temp).take(3).sum(x => x.temp);
```

### 12.2 What `x => ...` is, and what it is not

The `x => expression` written inside a query method is not a value. There are no
function pointers in IZ: it may only appear as the argument of one of the
methods above, it has exactly one parameter, and its body is an expression.
`var f = x => x + 1;` does not compile.

What it is, is a name for the item the loop is holding. The compiler inlines the
body into the loop it generates, so the parameter costs a local slot and the
body costs what the same expression would cost anywhere else - there is no call
per element.

### 12.3 What a chain costs

A whole chain becomes **one loop** over the source cells. Nothing is built
between one method and the next:

```iz
var n = readings.where(x => x > 30).take(4).sum();
```

walks the readings once, stops as soon as it has four, and keeps a running
total. It is the loop you would have written, and the compiler wrote it.

Four methods cannot work that way, because they have to see every element before
they can hand the first one over: `orderBy`, `orderByDesc`, `distinct` and
`reverse` after a stage. Those **materialize**: the compiler reserves a list of
its own in the frame, fills it with what came before them, does its work there,
and the rest of the chain reads those cells. A `reverse()` at the start of a
chain does not, since reading the cells backwards is enough.

The sort is an insertion sort, and it is **stable**: two items with the same key
keep the order they were in, so `orderBy(a)` after `orderBy(b)` sorts by `a` and
breaks the ties by `b`.

The cells any of that reserves are sized at compile time, from the source
capacity, narrowed by whatever `take` and `skip` say. They belong to the frame,
like every other declaration, and are cleared again on each lap of a loop.

### 12.4 A query that hands back a list

A chain that ends on a stage is a list, and it can be held:

```iz
var open = queue.where(x => !x.done);      // list Job[8]
for i in 0..open.count { open[i].id = 0; }
```

Its capacity is the source's, narrowed by `take` and `skip`, and its items are
**copies**: writing into `open` above does not touch `queue`. Nothing else would
be safe, since a query may sort what it hands back.

The cells belong to the call that ran the query, exactly like `var a: num[8]`.
To keep a result past the tick, write it into a list that lives longer:

```iz
var flagged: list num[8];                  // outside any function

fn main() {
    loop {
        queue.where(x => x.temp > 30).select(x => x.id).into(flagged);
        yield;
    }
}
```

`into` replaces the contents of the target and gives back how many items got
there. When the target has less room than the query has results, the extra ones
are dropped: the count says what fits. The target must not be the list being
read - a query walking cells that are being written under it has no defined
answer.

### 12.5 The edges

- Over an empty list, `sum`, `avg`, `min`, `max` and `count` are `0`, `any` is
  false and `all` is true - the same answer a batch read of nothing gives.
- `first()` and `last()` over nothing stop the chip, the way `xs[0]` of an empty
  list does. `firstOr(v)` and `lastOr(v)` are the forms that say what to answer
  instead.
- `sum` and `avg` take numbers, so a list of structs goes through `select`
  first. `min`, `max`, `contains`, `indexOf`, `distinct` and `remove` compare
  values, so they do too.
- `indexOf` answers with the position **in the result**, not in the source: a
  `where` in front of it changes what the number means.
- An array works everywhere a list does, minus the four methods that change it:
  every cell of an array is content, and there is no count to move.

## 13. The game's named values

Stationeers gives its logic values names, and IC10 writes them behind a group:
`Color.Black`, `AirCon.Cold`, `GasType.Oxygen`. IZ spells them exactly the same
way, so what is written on the wiki keeps working here.

```iz
device light  = d0;
device cooler = d1;

light.Color = Color.Green;
cooler.Mode = AirCon.Cold;
```

A value **is** a number, not a type of its own. It folds at compile time, so
`Color.Black` costs precisely what `7` costs, and it goes anywhere a number
goes: arithmetic, comparisons, the value of a `const`, the length of an array.

```iz
const WARN = Color.Red;            // a const takes one
var same = light.Color == WARN;    // and so does a comparison
```

The groups are read out of the game's own assembly, so they follow the game:

| group | what it names |
|---|---|
| `LogicType` | every device property: `Pressure`, `Setting`, `On`... |
| `LogicSlotType` | every slot property: `Occupied`, `Quantity`, `Charge`... |
| `LogicBatchMethod` | `Average`, `Sum`, `Minimum`, `Maximum` |
| `LogicReagentMode` | `Contents`, `Required`, `Recipe`, `TotalContents` |
| `Color` | the twelve paintable colors |
| `GasType` | `Oxygen`, `Nitrogen`, `Volatiles`, the liquids... |
| `SlotClass` | what a slot accepts: `Battery`, `Ore`, `Tool`... |
| `SortingClass` | what a sorter matches on |
| `Sound` | every alarm an alarm speaker can play |
| `AirCon`, `AirControl`, `Vent`, `FiltrationMode` | atmospherics modes |
| `PowerMode`, `TransmitterMode`, `ElevatorMode`, `RobotMode` | device modes |
| `EntityState`, `DaylightSensorMode`, `ConditionOperation` | sensors and logic |
| `PrinterInstruction`, `SorterInstruction`, `TraderInstruction` | chip instructions |
| `RocketMode`, `ReEntryProfile`, `NodeType`, `ShuttleType` | rockets and trading |
| `HashType`, `DisplayMode`, `SettingDisplayMode` | display and hash modes |

Where IC10 writes a value with no group at all - `Average`, `Contents` - IZ
requires the group in front of it, so that reaching one of the game's values
always looks the same.

Two rules keep them out of the way of a program's own names:

- A declaration wins. `var Color = 3;` shadows the group, exactly as it shadows
  anything else; `Color.Black` after that is a field access on a `num` and fails
  as one.
- A group name on its own is not a value. `var c = Color;` is an undefined name:
  only `Group.Value` means something.

A misspelled value is a compile error with the nearest name attached, the same
treatment a misspelled property gets:

```
error IZ324 (4:21): 'Color' has no value named 'Blck'; did you mean 'Black'?
```

In the editor, `Color.` opens the list of that group's values with the number
each one stands for, and hovering either half says what it is.
