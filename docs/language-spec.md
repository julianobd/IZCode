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
| `dev` | device handle (pin `d0`–`d5`, or a reference by id) |
| `T[N]` | array of exactly `N` values of type `T` (section 10) |
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
```

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
on the same data network, identified by the **prefab hash**, with no cable
needed to each one.

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

A function **cannot return an array or a struct**. Its cells belong to the call
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
never a `dev` - a device is a pin known at compile time, not a value.

### 10.3 What cannot be done

| | why |
|---|---|
| `a = b;` between arrays or structs | the name is bound once, at the declaration; assign to the elements or the fields |
| `a == b` between arrays or structs | it would compare addresses, which is never the question |
| returning an array or a struct | the cells are released on the return |
| `const a: num[4]` | a `const` is folded into every use, and an aggregate has nothing to fold |
| an array of `dev` | same reason a field is never a `dev` |

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
