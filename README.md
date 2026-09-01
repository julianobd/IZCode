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
| Code limit | 128 lines of 90 characters, 4096 bytes | 2000 lines of 200 characters, 16000 bytes |
| Registers | 16, managed by hand | named variables, allocated by the compiler |
| Functions | `jal`/`ra`, one practical level | `fn` with parameters, return values and recursion |
| State between ticks | restarts from the top every tick | **the VM freezes and resumes where it stopped** |
| Infinite loop | freezes the chip | preempted by budget, the game carries on |
| Wrong property name | you find out at runtime | compile error, with a suggestion for the right name |
| Types | everything is a `double` | `num`, `bool`, `str`, `dev` checked at compile time |
| Text | `HASH("name")`, and the text is gone | a real `str`: joined, compared and hashed while it runs |
| Data structures | 16 loose registers | `struct`, arrays and lists, in a heap the compiler lays out |
| Going through them | a loop and a counter, by hand | `where`, `orderBy`, `sum`, compiled into one loop |
| The game's values | `Color.Black`, and a typo runs anyway | `Color.Black`, checked and folded at compile time |

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

## Choosing between two values

`c ? a : b` is IC10's `select`, and it is an expression, so it goes anywhere a
value goes:

```iz
var target = hot ? LOW : HIGH;
pump.Setting = full ? 0 : 100;
display.Setting = len(locked ? "shut" : "open");
```

The condition is a `bool`, as in `if`. The two sides have to be the same type -
`num`, `bool` or `str`, never an array, a list or a struct - and only the branch
taken is evaluated, so a device read on the other side never happens. It binds
weaker than every other operator and it is right associative, so a chain of
bands reads as nested without parentheses:

```iz
var band = p < LOW ? 1 : p > HIGH ? 3 : 2;
```

See section 6.1 of [docs/language-spec.md](docs/language-spec.md) and
`samples/ternary.iz`.

## The game's named values

Stationeers names its logic values, and IC10 writes them behind a group:
`Color.Black`, `AirCon.Cold`, `GasType.Oxygen`. IZ spells them the same way, so
what is written on the wiki keeps working here:

```iz
device light  = d0;
device cooler = d1;

light.Color = Color.Green;
cooler.Mode = AirCon.Cold;
```

A value **is** a number, folded at compile time. `Color.Black` costs precisely
what `7` costs, and it goes anywhere a number goes - arithmetic, comparisons,
the value of a `const`, the length of an array. What it buys is that the name is
checked:

```
error IZ324 (4:21): 'Color' has no value named 'Blck'; did you mean 'Black'?
```

All 30 groups the game exposes are there - `LogicType`, `LogicSlotType`,
`Color`, `GasType`, `SlotClass`, `SortingClass`, `Sound`, `AirCon`,
`AirControl`, `Vent`, `PowerMode`, `RobotMode`, `EntityState`,
`PrinterInstruction`, `RocketMode` and the rest - read out of the game's own
assembly by the generator, so they follow the game rather than a copy of it.
Where IC10 writes a value with no group at all (`Average`, `Contents`), IZ asks
for the group in front of it, so reaching one of the game's values always looks
the same.

A name a program declares always wins: `var Color = 3;` shadows the group the
way it shadows anything else. See section 13 of
[docs/language-spec.md](docs/language-spec.md) and `samples/game-constants.iz`.

## Devices, and the one the chip is in

The six pins reach whatever is wired to the housing. `db` reaches the housing
itself:

```iz
device sensor = d0;            // wired to the housing
device self   = db;            // the housing the chip is installed in
```

Nothing can be wired to the thing the chip is already inside, so `db` is the
only way to it. What it turns out to be depends on where the chip went: in a
circuit housing it is the housing, and in a **hardsuit**, which holds a chip in
its own slot, it is the suit. That is what lets a suit read its own
`PressureExternal` and drive its own AC. On a suit the six pins are the wearer's
slots rather than cables:

| pin | on a circuit housing | on a suit |
|---|---|---|
| `d0`–`d5` | the six wired devices | helmet, backpack, toolbelt, glasses, left hand, right hand |
| `db` | the housing | the suit |

`db` is a device like any other: its properties are checked at compile time,
completion knows them, and hover shows their live values. See
`samples/self-device.iz` and `samples/hardsuit.iz`.

The pins follow the chip. Take a chip out of a circuit housing, put it in a
suit, and `db` becomes the suit without the code being touched or recompiled.
While the program cannot reach a device - the suit is not being worn, the cable
is not plugged in yet, the property does not exist on whatever is holding the
chip - it stops with a device error and the housing lights its error LED, then
restarts on the next tick and keeps trying. The moment the device is there it
picks up again on its own and the LED goes out. Every other runtime error is a
bug in the program: it stops for good, with the error line pointing at it.

That recovery is what a forgotten cable deserves, and not what a program meant
to run with whatever is there wants. `isset` asks first:

```iz
device helmet = d0;

fn main() {
    loop {
        if isset(helmet) { helmet.On = true; }
        yield;
    }
}
```

`isset(dev) -> bool` is true while the pin has a device on it, and never an
error: an empty pin is the answer. It takes a device declared with `device`, a
pin written straight into the call (`isset(d3)`), or `db`. A name standing for
`all(...)` or `named(...)` is not a pin and is refused at compile time. This is
IC10's `sdse`. See `samples/optional-devices.iz`.

## Devices beyond the six pins

Six pins run out long before the devices do. The same `device` declaration also
takes a batch selector, so a group on the data network gets a name once and is
used like anything else:

```iz
device led     = named(StructureDiode, #"led-dev");
device lights  = all(StructureWallLight);
device hangar  = named("hangar");          // any prefab carrying that label

fn main() {
    led.On = true;                 // reaches every device the selector matches
    var s = lights.Setting;        // a batch read, averaged over all of them
}
```

That is the point of it: a base with forty lights names them in one line instead
of repeating the prefab and the label everywhere. The two hashes are folded when
the name is declared, so `led.On = true` compiles to exactly what the inline
`named(...)` form compiles to - naming the group costs nothing.

A batch property is the sequence of readings of every device the selector
matched, and used bare it means `.avg()`. Four other terminals collapse it, and
none of them costs a second read:

```iz
var total  = all(StructureSolarPanel).PowerGeneration.sum();
var worst  = all(StructureGasSensor).Pressure.max();
var lowest = lights.Setting.min();
var panels = all(StructureSolarPanel).PowerGeneration.count();
```

An empty batch gives `0` for all of them - `min()` included, which is `0` and
not infinity. `count()` is the one that still answers, so it is what separates
"nothing on the network" from "everything reading zero". See `samples/solar.iz`.

What a selector saves is pins, not cabling. It reaches the devices on the same
data network as the housing, exactly as a pin does, so the data cable still has
to get there; what it drops is having to register each device in one of the six
slots.

The editor follows a selector like it follows a pin: `led.` suggests that
prefab's properties, with the network's current reading beside each one, and
hovering `led` names the equipment. When only a label was written, the devices
carrying it are asked; when they are all the same prefab, that is what is
offered.

Because a device is a fixed place in the world, both operands have to be known
at compile time: a prefab name, a hash literal, a `const`, or text joined from
them. A selector built from a running value is written where it is used, in the
inline form. And two things a pin can do it cannot: `+=` on a property (a batch
has no single value to read back) and `slot[i]` (a slot belongs to one device).

See `samples/named-devices.iz`.

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

**A program bigger than the editor.** The game's editor is 128 lines of 90
characters, 4096 bytes in all, because that is what its 128 input fields hold. In
IZ mode the code area is what holds the program, and what leaves the editor comes
from there, so those three numbers stop applying: **2000 lines of 200 characters,
16000 bytes**. The ceiling is TextMeshPro's - one mesh draws at most 16383 glyphs,
and past that the end of the program would not be drawn at all. The chip, the
save file and the multiplayer sync take whatever comes.

The 128 lines do not go away: they become invisible and get the text back on
every change, so the byte count, the game's own editor and the chip keep working,
and if building the code area ever fails the original editor is still there,
whole. The traffic runs both ways: **Copy**, **Paste**, **Clear** and loading a
script from the **Library** all write into those lines, and the code area takes
the whole of what arrived - not only the part the 128 lines could hold.

Deleting the `#iz` gives the game's editor back, with the caret on the same line.
What is past its 128th line is not lost by that: type the marker again and the
program comes back whole.

**Completion that knows your wiring.** The editor is opened by the Programmable
Chip Motherboard, which knows which holder is selected, and the holder knows
what is on each pin and what `db` is. Typing `pump.` suggests the properties of
**that pump**, with its current value alongside, instead of all 358 in the game:

```
pump.|
      On          rw
      Setting     rw  = 45
      Pressure    r   = 101.325
```

A device declared from a selector gets the same list. `device led =
named(StructureDiode, "led-dev")` names one prefab, so `led.` offers the diode's
properties and nothing else - and the value alongside each one is what a batch
read would give, averaged over every device the selector reaches:

```
led.|
      On          rw  = 1
      Color       rw  = 2
      Setting     rw  = 0
```

`named("led-dev")` writes no prefab, so the data network is asked instead: when
every device carrying that label is the same equipment, its properties are the
ones offered. Two different prefabs answering to one label have no single list
between them, and the full vocabulary comes back, as before. The same goes for
`all(StructureWallLight).` written inline, and for `slot[i]` on a selector whose
prefab has slots.

It also completes pins and `db` (`device x = ` shows what is on each one, and
offers `all` and `named` after them), prefab names inside `all(...)` and
`#"..."`, slot properties, the names declared in the program itself, and the
game's named values - `Color.` lists the twelve colors with the number each one
stands for.

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
label, the hash and the value of every readable property at that moment - on a
selector device too, when the selector lands on a single kind of equipment. Over
a property, it says whether that device accepts it and whether it is read only.
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

The device property table (`LogicType`, `LogicSlotType`) and the game's named
values (`Color`, `GasType`, and the 27 other groups) are generated from the game
itself. When Stationeers updates, regenerate them:

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
- `device x = named(...)` and `device x = all(...)` - a name for a group on the
  network, folded to its hashes at compile time and used like a device on a pin
- `db`, the device the chip is installed in - the housing, or the hardsuit whose
  slot holds the chip, where the pins are the wearer's slots
- `yield`, `sleep`, preemption by budget
- a runtime `str`: `+`, `+=`, the six comparisons, and a text library, with a
  string table that interns what repeats and collects what nobody points at
- 33 native functions (`abs`, `sqrt`, `clamp`, `text`, `sub`, …), and `len` over
  an array, a list or a string
- the game's named values (`Color.Black`, `AirCon.Cold`, `GasType.Oxygen`),
  30 groups read out of Assembly-CSharp, folded at compile time and checked
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

Not done yet:

- persisting the VM state in the save
- multiplayer testing
