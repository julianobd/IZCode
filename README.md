# StationeersIZCode

A Stationeers mod that replaces IC10 with **IZ**: a structured language, with
types, real functions and recursion, running on its own virtual machine inside
the Programmable Chip.

```iz
#iz
device sensor = d0;
device inlet  = d1;

const TARGET = 101.325;
const MARGIN = 5.0;

fn main() {
    loop {
        var p = sensor.Pressure;
        inlet.On = p < TARGET - MARGIN;
        yield;
    }
}
```

## What changes compared to IC10

| | IC10 | IZ |
|---|---|---|
| Code limit | 128 lines | bounded only by the source size |
| Registers | 16, managed by hand | named variables, allocated by the compiler |
| Functions | `jal`/`ra`, one practical level | `fn` with parameters, return values and recursion |
| State between ticks | restarts from the top every tick | **the VM freezes and resumes where it stopped** |
| Infinite loop | freezes the chip | preempted by budget, the game carries on |
| Wrong property name | you find out at runtime | compile error, with a suggestion for the right name |
| Types | everything is a `double` | `num`, `bool`, `str`, `dev` checked at compile time |
| Text | `HASH("name")`, and the text is gone | a real `str`: joined, compared and hashed while it runs |
| Data structures | 16 loose registers | `struct`, arrays and lists, in a heap the compiler lays out |
| Going through them | a loop and a counter, by hand | `where`, `orderBy`, `sum`, compiled into one loop |

The line that matters most is *State between ticks*: **the IZVm is
preemptive**. A `loop { }` with no `yield` freezes nothing: the VM runs its
instruction budget, hands the tick back to the game, and carries on at the next
instruction on the following tick.

The one below it is newer. In IC10 a piece of text only ever became a number:
`HASH("name")` at compile time, and the text itself was gone. In IZ a `str` is a
value like any other, so the devices a program talks to can be chosen while it
runs:

```iz
var side = "north";

named("vent-" + side).On = true;          // hashed on the spot
all("Structure" + kind).Setting = 0;      // so is a prefab name
if label == "vent-north" { ... }          // compares the text, not an address
```

It joins with `+`, compares with the six operators, and comes with a small text
library (`text`, `sub`, `find`, `chr`, `char`, `fixed`, `parse`, `hash`, `len`).
A literal label still folds to its hash at compile time, so nothing that was
free before became expensive. See section 11 of
[docs/language-spec.md](docs/language-spec.md) and `samples/text.iz`.

Newer still is the last row. A `list` is an array plus how much of it is in use,
and it comes with the query methods, in the shape of LINQ's:

```iz
var worst = rooms.where(x => x.pressure > LIMIT)
                 .orderByDesc(x => x.pressure)
                 .first();
```

That is **one loop** over the cells, with the lambdas inlined into it: nothing is
allocated between one method and the next, and there is no call per element. See
[Lists](#lists), [Queries](#queries) and `samples/queries.iz`.

## Lists

The chip has no allocator, and the heap is laid out at compile time. A `list` is
what that allows: a fixed run of cells, reserved once, plus one number in front
of them saying how many are in use.

```iz
const CAP = 8;

var jobs: list num[CAP];           // room for eight, holding none
var seed: list num[8] = [10, 20];  // count 2, six cells still free
```

The capacity is part of the type, exactly like an array's length, and is decided
when the program is compiled. What moves while it runs is the count.

| | |
|---|---|
| `len(jobs)` | the capacity - folded into a constant, costs nothing |
| `jobs.count` | how many items are in it right now; read only |
| `jobs[i]` | the item at `i`, checked **against the count** |
| `jobs.add(v)` | appends `v`; `false` when the list is full |
| `jobs.remove(v)` | takes the first item equal to `v` out; `false` when it is not there |
| `jobs.removeAt(i)` | takes item `i` out and slides the rest down; `false` outside |
| `jobs.clear()` | empties it |

```iz
jobs.add(10);
jobs.add(20);
jobs.add(30);              // jobs.count is 3

var first = jobs[0];
jobs.remove(20);           // by value: 20 leaves, 30 slides down
jobs.removeAt(0);          // by position: 10 leaves too
```

`add` gives back a `bool` rather than stopping the chip, because a full list is
something a program can still do something about. Reading past the count is the
other way round: `jobs[5]` on a list holding three is a runtime error, even
though the eight cells are all there. Past the count they are room, not content,
and that is the whole difference between a list and an array.

### Lists of structs

An item is anything a struct field may be - `num`, `bool`, `str`, or a struct.
That last one is how a list of numbers becomes a work queue:

```iz
struct Job { id: num; temp: num; done: bool; }

var queue: list Job[8];

fn push(id: num, temp: num) {
    var job: Job;                            // cleared again on every call
    job.id   = id;
    job.temp = temp;
    job.done = false;

    queue.add(job);                          // false when there was no room
}
```

`add` takes the item, a struct like anything else, and what lands in the list is
a **copy** of its cells - so filling `job` again for the next one does not reach
back into the one already in there.

Reading is the other way round: `var job = queue[i];` binds a name to the item's
own cells, so `job.done = true;` marks it in the list.

## Queries

A list, and an array, answer the questions you would otherwise write a loop for.
The shapes are LINQ's:

```iz
var mean  = rooms.avg(x => x.temp);                    // one number
var hot   = rooms.count(x => x.temp > 30);             // how many
var worst = rooms.orderByDesc(x => x.temp).first();    // the item itself
var top3  = rooms.orderByDesc(x => x.temp).take(3).sum(x => x.temp);

var open  = queue.where(x => !x.done);                 // a list of its own
```

**Stages** hand back a sequence, so more may follow:

| | |
|---|---|
| `where(x => bool)` | keeps what passes the test |
| `select(x => value)` | one value out of each item |
| `take(n)` / `skip(n)` | the first n / everything after the first n |
| `takeWhile(f)` / `skipWhile(f)` | from the start while it passes / from the first that fails |
| `orderBy(x => key)` / `orderByDesc(x => key)` | sorted by a key |
| `reverse()` | back to front |
| `distinct()` | drops repeats, keeping the first of each |

**Terminals** hand back one value, and end the chain:

| | |
|---|---|
| `count()` `sum()` `avg()` `min()` `max()` | the numbers; `0` over nothing |
| `any()` `all(f)` | is there one, do they all pass |
| `first()` `last()` | the item itself; a runtime error over nothing |
| `firstOr(v)` `lastOr(v)` | the same, answering `v` when nothing matched |
| `contains(v)` `indexOf(v)` | is it in there, and where |
| `into(target)` | fills a list that already exists; how many got there |

`sum`, `avg`, `min` and `max` take an optional selector, and `count`, `any`,
`first` and `last` take an optional test, so `xs.sum(f)` is `xs.select(f).sum()`
and `xs.first(f)` is `xs.where(f).first()`.

### What it costs

Nothing that the loop would not have cost. **A whole chain is compiled into one
loop** over the source cells, with every lambda inlined into it:

```iz
var n = readings.where(x => x > 30).take(4).sum();
```

walks the readings once, stops as soon as it has four, and keeps a running
total. There is no list built between one method and the next, and no call per
element - `x => x > 30` is not a value, it is a name for the item the loop is
holding, and its body is compiled straight into the loop body.

Four methods cannot work that way, because they have to see every element before
they can hand the first one over: `orderBy`, `orderByDesc`, `distinct`, and
`reverse` when something came before it. Those reserve a list of their own,
sized at compile time from the source capacity, fill it, do their work there,
and the rest of the chain reads those cells. The sort is stable, so
`orderBy(a)` after `orderBy(b)` breaks ties by `b`.

### What a query hands back

A chain that ends on a stage is a list, and it can be held:

```iz
var open = queue.where(x => !x.done);        // list Job[8]
for i in 0..open.count { open[i].done = true; }
```

Its items are **copies**: the loop above does not touch `queue`. Nothing else
would be safe, since a query is allowed to sort what it hands back.

Its cells belong to the call that ran the query, like every other declaration,
so they are gone when the function returns and cleared again on each lap of a
loop. A result that has to survive the tick goes into a list that lives longer:

```iz
var flagged: list num[8];                    // outside any function, so global

fn main() {
    loop {
        queue.where(x => x.temp > 30).select(x => x.id).into(flagged);
        yield;
    }
}
```

`into` replaces the contents of the target and answers how many items got there.
What does not fit is dropped, so the count is the answer to "how many fit".

### What the fixed capacity costs

| | |
|---|---|
| the capacity is decided at compile time | `list num[CAP]` takes a literal or a `const`, never a variable. Size CAP for the worst case, and read what `add` gives back |
| two capacities are two types | `list num[4]` and `list num[8]` do not match, so a function written for one does not take the other |
| `a = b` between lists does not compile | there is no whole-aggregate assignment; `b.into(a)` copies, or loop over the items |
| a function cannot hand a list back | the cells belong to the call and are released on the return. Pass the list in and fill it, or use `into` |
| where it is declared decides how long it lives | a list declared inside a function comes back cleared on every lap of a loop. One that has to survive the tick is declared outside `main` |
| the heap is 2048 cells, shared | a `list num[8]` costs 9 cells (the count plus the room), a `list Job[8]` of a three-field Job costs 25. Going over is the `HeapOverflow` runtime error |

Section 12 of [docs/language-spec.md](docs/language-spec.md) has the full rules,
and `samples/queries.iz` wires all of this to four sensors.

## How it works inside

```
.iz source
    │
    ├─ Lexer ────────► tokens
    ├─ Parser ───────► AST
    ├─ Compiler ─────► bytecode + function table        (resolves names, checks types)
    │
    └─ IZVm ─────────► runs N instructions per tick, then freezes the state
                          │
                          └─ IDeviceHost ──► the game's CircuitHousing
```

The separation the project rests on: **`IZLang` references neither Unity nor
`Assembly-CSharp`**. All contact with the game goes through the `IDeviceHost`
interface. That is why the compiler and the VM are entirely testable without
opening Stationeers: the 513 tests run in ~40 ms.

### Why reuse the Programmable Chip

The mod does not create a new item. A chip becomes IZ when the first line of the
source is the `#iz` marker; without the marker, IC10 behaviour is untouched.

That is deliberate: it inherits the in-game editor, saving the code into the
save file, and multiplayer replication for free. A new prefab would need a Unity
asset bundle and a reimplementation of all of that. As a bonus, `#iz` starts
with `#`, which the IC10 parser treats as a comment, so a chip carrying IZ code
does not break the game for someone without the mod installed.

The chip's behaviour comes down to just two Harmony grafts, both on
`ProgrammableChip`:

- **`SetSourceCode` (postfix)**: compiles the IZ and undoes the error the IC10
  parser inevitably produced.
- **`Execute` (prefix)**: runs the IZVm in place of the IC10 interpreter.

The other three are interface only, and none of them changes behaviour, only
what shows on screen: `InputSourceCode` to hang the overlay off,
`EditorLineOfCode` and `ProgrammableChipMotherboard` to paint IZ code with the
right highlighter.

## Tools in the game's editor

Once you write `#iz`, the code editor gains four things.

**A real code area.** The Stationeers editor is not a text field: it is 128
independent `TMP_InputField`s, one per line. That is what prevents dragging the
mouse across three lines, pressing `Ctrl+A` over the whole program or cutting a
block: each field only sees its own line.

In IZ mode it is replaced by **a single, multi-line field with a dark gray
background**, and all of that becomes native behaviour: selecting by dragging
the mouse, `Shift`+arrows, double clicking a word, `Ctrl+A`/`C`/`X`/`V`,
`PageUp`/`PageDown`, the mouse wheel, a line-number gutter and a current-line
highlight.

Along with it comes VS Code's automatic indentation:

| key | what it does |
|---|---|
| `Enter` | repeats the line's indent; goes in one level if it opened a block |
| `Enter` between `{` and `}` | opens both lines and leaves the caret in between |
| `}` on a blank line | goes back one level, to line up with the opening brace |
| `Tab` / `Shift+Tab` | shifts the whole selected block |

The original 128 lines do not go away: they become invisible and get the text
back on every change, so `Copy()`, the save button, the byte count and the chip
keep working as before. Deleting the `#iz` gives the game's editor back, whole,
with the caret on the same line. And if building the panel fails for any reason,
the original editor never leaves the stage.

**Completion that knows your wiring.** The editor is opened by the Programmable
Chip Motherboard, which knows which CircuitHousing is selected, and the housing
knows what is wired to each pin. Typing `pump.` suggests the properties of
**that pump**, with its current value alongside, instead of all 358 in the game:

```
pump.|
      On          rw
      Setting     rw  = 45
      Pressure    r   = 101.325
```

It also completes pins (`device x = ` shows what is on each one), prefab names
inside `all(...)` and `#"..."`, slot properties, and the names declared in the
program itself.

`Tab` accepts the suggestion, `Ctrl`+arrows move through the list, `Esc` closes
it, `Ctrl+Space` opens the full list. With the list closed, `Tab` goes back to
indenting. On a blank line, with nothing typed, the list does **not** open by
itself: dumping the whole vocabulary over the code gets in the way more than it
helps. After a `.`, after `device x = ` or after `all(`, it does open: there
the context is already the request.

**Compile errors while you type.** The same compiler the chip runs on CONFIRM
compiles what is on screen, with a 0.35 s breath after the last keystroke. Lines
with problems get an underline (red for an error, amber for a warning), and a
panel at the bottom says what it is:

```
error line 5     a batch write takes num (or bool), not str
warning line 2   const 'LED' was declared and never used
```

The goal is for `all(DISPLAY).Setting = "OK"` to be caught right there, not on
CONFIRM.

**Hover with real values.** Hovering a device variable shows the equipment, the
label, the hash and the value of every readable property at that moment. Over a
property, it says whether that device accepts it and whether it is read only.
Over a `#"..."`, it shows the hash and warns when the prefab does not exist.

That is fed by the [device catalog](docs/device-catalog.md), scanned from the
game once per version and written to
`Documents\My Games\Stationeers\izcode\` as `devices.txt` and `devices.json`.
The console has `izcode_devices rescan` to force it.

## Structure

```
src/IZLang/            compiler + VM, with no game dependency (netstandard2.0)
  Diagnostics/           spans, errors with line/column and a caret
  Lexing/                tokens
  Parsing/               AST and recursive descent parser
  Binding/               symbols, types, bytecode emission
  Vm/                    opcodes, program, IZVm, IDeviceHost
  Devices/               device catalog and its file format
  Editor/                completion, hover, highlighting and indentation - pure, no Unity
src/IZCode.Mod/        the game layer: Harmony, device host, UI (net472)
  Diagnostics/           log and on-disk paths - the switch lives here
  Patches/               the grafts onto the game
  Runtime/               per-chip VM, bridge to the housing
  Devices/               prefab scanning, on-disk cache
  UI/                    IZ code area, completion, tooltip and error panel
tests/IZLang.Tests/    513 tests
tools/GenLogicTypes/   generates the LogicType table from the game
samples/               example .iz programs
docs/                  language specification, catalog, roadmap
```

The same boundary that already held for the VM holds for the editor tools:
**what decides is pure code in `IZLang`, what draws is Unity in `IZCode.Mod`.**
The completion and hover engines take `(text, offset, IEditorEnvironment)` and
return a list, so the whole behaviour is tested without opening the game, and
the blind part is only the drawing.

## Build

Requires the .NET SDK and an installed copy of Stationeers.

```bash
dotnet test                      # runs the tests
pwsh build/pack.ps1              # assembles dist/IZCode/
pwsh build/pack.ps1 -Deploy      # installs into the game's mods folder
```

If the game is not in the default Steam path:

```bash
dotnet build -p:StationeersDir="D:\Steam\steamapps\common\Stationeers"
```

### After a Stationeers update

The device property table (`LogicType`) is generated from the game itself. When
Stationeers updates, regenerate it:

```bash
dotnet run tools/GenLogicTypes/gen.cs
```

## Installation

**IZCode is a C# plugin, and Stationeers does not load C# on its own.** The game
only reads `GameData` (XML and prefabs) from mods; nothing in
`mods\IZCode\*.dll` runs without a loader. So, before the mod, these have to be
installed:

1. **BepInEx** (the loader; it puts `winhttp.dll` and the `BepInEx\` folder in
   the game root, next to `rocketstation.exe`);
2. **[StationeersLaunchPad](https://github.com/StationeersLaunchPad/StationeersLaunchPad)**,
   which runs on top of BepInEx and is what calls the mod's `OnLoaded()`.

With both in place, `build/pack.ps1 -Deploy` copies the mod to
`Documents\My Games\Stationeers\mods\IZCode`; from there just enable it in the
mods menu.

### Checking that it loaded

Open the game and look for `[IZCode]` in
`%USERPROFILE%\AppData\LocalLow\Rocketwerkz\rocketstation\Player.log`:

```
[IZCode] ======================================================
[IZCode] IZCode 0.1.0.0 starting through OnLoaded (StationeersLaunchPad)
```

That line always comes out, even with the log switched off. If it is not there,
the mod was never even called: the problem is in the loader, not in IZCode. If
it is there, the rest of the diagnosis is in
`Documents\My Games\Stationeers\izcode\izcode.log`.

## Log

Everything the mod does goes through `IZLog`, and the switch lives in one place:
`Documents\My Games\Stationeers\izcode\log.cfg`, created on the first run.

```
enabled=true
level=info                       # off | error | warn | info | debug | trace
areas=load,chip,vm,editor,completion,catalog
file=true                        # also write to izcode.log
```

The subjects exist so only the part being investigated can be switched on:
`hover` and `highlight` talk on every frame and every keystroke, which is why
they start off. Inside the game, the console does the same without leaving the
session:

```
izcode_log                          shows the current state
izcode_log off                      switches everything off
izcode_log level debug              talks more
izcode_log areas completion,editor  only completion and the overlay
izcode_log path                     where the log and the config are
```

Every command rewrites `log.cfg`, so what you choose applies to the next session
too.

## Current state

Working and covered by tests:

- lexer, parser, type checking, bytecode and VM, all complete
- `if`/`else`/`while`/`loop`/`for`/`break`/`continue`
- functions with parameters, typed return values and recursion
- `struct` and fixed length arrays, with a heap that is laid out at compile time
  and released when the call that declared it returns
- a `list` type - an array plus its count - with `add`, `removeAt` and `clear`,
  and an index checked against the count rather than the capacity
- the query methods over a list or an array (`where`, `select`, `orderBy`,
  `sum`, `first`, `into`, and the rest), compiled into a single loop with the
  lambdas inlined, and cells reserved at compile time for the four that have to
  see everything before they answer
- device reads and writes, slots, batch operations by hash and by label
- `yield`, `sleep`, preemption by budget
- a runtime `str`: `+`, `+=`, the six comparisons, and a text library, with a
  string table that interns what repeats and collects what nobody points at
- 33 native functions (`abs`, `sqrt`, `clamp`, `text`, `sub`, …), and `len` over
  an array, a list or a string
- errors with line, column, source snippet and a name suggestion
- device catalog: format, reading that tolerates a truncated file, JSON export
- completion and hover engines, including the offset seam with the editor
- completion of struct fields, following `t.reading.` and `ps[0].` through the
  declared types, and of the query methods after a list or another query
- a warning for a name declared and never used (and its silence when there is
  already an error)
- IZ syntax highlighting, including the `#"Prefab"` that IC10 read as a comment
- automatic indentation: `Enter`, `}` and `Tab`/`Shift+Tab` over a selected block
- logging with level and subject, switchable by file or from the console

Written but **not tested inside the game** (see
[docs/roadmap.md](docs/roadmap.md)):

- the prefab scan that generates the catalog
- the IZ mode code area (the single field that replaces the 128 lines)
- drawing the completion popup, the tooltip and the error panel
- the Harmony grafts (they compile against the real assemblies, but have never
  run)

Not done yet:

- persisting the VM state in the save
- validating the prefab hash against a running game
- multiplayer testing
