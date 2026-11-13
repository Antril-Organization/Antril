using System.Diagnostics;
using System.Text;
using Antril.Binding;
using Antril.Syntax;
using Larva.Hir.Lowering;
using Larva.Llvm;
using Larva.Runtime.Vm;
using Larva.X64;

namespace Antril.Tests;

/// <summary>
/// Phase 3 of the HIR rebuild: Antril source → Larva HIR (the new frontend) → IR → LVM, x64 and LLVM, with the same
/// results; diagnostics for wrong programs; and how fast the frontend is.
/// </summary>
public sealed unsafe class AntrilTests : CheckedTest
{
    [Fact]
    public void Phase3_program_runs_alike_on_the_VM_x64_and_LLVM() => Programs();

    [Fact]
    public void Partial_imports_aliases_and_constants_of_every_kind() => ImportsAndConstants();

    [Fact]
    public void Variadic_templates_expand_per_pack_size() => Variadics();

    [Fact]
    public void Wrong_programs_get_their_diagnostics() => Diagnostics();

    [Fact]
    public void Frontend_speed_on_a_large_program() => Speed();

    [Fact]
    public void Pathological_programs_end_with_a_diagnostic_or_compile() => Pathological();

    [Fact]
    public void Benchmarks_built_as_executables_give_the_right_results() => Executables();

    internal const string Phase3Source = """
        into namespace Phase3
        {
            public class Error
            {
                public:
                    i32 Code;
                    new(i32 code) { Code = code; }
            }

            public base class Animal
            {
                public:
                    i32 Legs;
                    new(i32 legs) { Legs = legs; }
                    virtual Speak() : i32 => 1;
                    Kind() : i32;
            }

            public class Dog : Animal
            {
                public:
                    new() : base(4) { }
                    override Speak() : i32 => 10 + base.Speak();
                    override Kind() : i32 => 2;
            }

            public class Puppy : Dog
            {
                public:
                    new() : base() { }
                    override Speak() : i32 => 100 + base.Speak();
            }

            public value Point
            {
                public:
                    i32 X, Y;
                    new(i32 X, i32 Y);
                    Dot() : i32 => X * X + Y * Y;
                    Scale(i32 k)
                    {
                        X *= k;
                        Y *= k;
                    }
            }

            public enum Color { Red, Green, Blue }

            public class Counter
            {
                public:
                    i32 Count = 5;
                    i32 Step = 2;
                    Next() : i32
                    {
                        Count += Step;
                        return Count;
                    }
            }

            public static class State
            {
                public:
                    i32 Total = 0;
            }

            public Sum(i32 n) : i32
            {
                i32 i = 0, s = 0;
                loop (i < n)
                {
                    s += i;
                    i++;
                }
                return s;
            }

            public Fib(i32 n) : i32 => n < 2 ? n : Fib(n - 1) + Fib(n - 2);

            public Loops() : i32
            {
                i32 s = 0;
                loop (i32 k = 0; k < 10; k++)
                {
                    if (k == 3)
                        continue;
                    if (k == 7)
                        break;
                    s += k;
                }
                loop (i32 j = 0..=3)
                    s += j * 100;
                i32 m = 0;
                do m += 7; loop (m < 20);
                loop (i32 c = 1)
                {
                    if (c > 4)
                        break;
                    s += c * 1000;
                }
                return s + m;
            }

            public Classify(i32 n) : i32
            {
                switch (n)
                {
                    0 => return 10;
                    1 or 2 => return 20;
                    < 0 => return 30;
                    _ => return 40;
                }
            }

            public Grade(i32 n) : i32 => n switch { >= 90 => 4, 80..90 => 3, 70..90 => 2, _ => 0 };

            public Structs() : i32
            {
                Point p = new Point(3, 4);
                p.Scale(2);
                Point q = p;
                q.X = 0;
                return p.Dot() * 1000 + q.Dot();
            }

            public Animals() : i32
            {
                Animal a = new Dog();
                Animal b = new Puppy();
                return a.Speak() * 1000 + b.Speak() + a.Kind() * 1000000 + a.Legs;
            }

            public Types() : i32
            {
                i32 r = 0;
                Animal a = new Puppy();
                if (a is Dog)
                    r += 1;
                if (a is Puppy)
                    r += 10;
                Dog? d = a as Dog;
                if (d != null)
                    r += 100;
                Animal c = new Dog();
                if (!(c is Puppy))
                    r += 1000;
                if (a is Dog dog && dog.Legs == 4)
                    r += 10000;
                return r;
            }

            Throws(i32 n) : i32
            {
                if (n < 0)
                    throw new Error(n);
                return n * 2;
            }

            public Catches(i32 n) : i32
            {
                try
                {
                    return Throws(n);
                }
                catch (Error e)
                {
                    return e.Code - 1000;
                }
            }

            public Finally(i32 n) : i32
            {
                State.Total = 0;
                try
                {
                    try
                    {
                        if (n < 0)
                            throw new Error(1);
                        State.Total += 1;
                    }
                    finally
                    {
                        State.Total += 10;
                    }
                }
                catch
                {
                    State.Total += 100;
                }
                return State.Total;
            }

            ReturnFinallyInner() : i32
            {
                try
                {
                    return 5;
                }
                finally
                {
                    State.Total = 7;
                }
            }

            public ReturnFinally() : i32
            {
                State.Total = 0;
                i32 r = ReturnFinallyInner();
                return r * 10 + State.Total;
            }

            public BreakFinally() : i32
            {
                i32 s = 0;
                loop (true)
                {
                    try
                    {
                        s += 1;
                        if (s == 203)
                            break;
                    }
                    finally
                    {
                        s += 100;
                    }
                }
                return s;
            }

            public Rethrow(i32 n) : i32
            {
                try
                {
                    try
                    {
                        Throws(n);
                    }
                    catch (Error e)
                    {
                        State.Total = e.Code;
                        throw;
                    }
                }
                catch (Error e2)
                {
                    return e2.Code * 2 + State.Total;
                }
                return 0;
            }

            public Nulls() : i32
            {
                Animal? a = null;
                i32 r = 0;
                if (a == null)
                    r += 1;
                Animal x = a ?? new Dog();
                r += x.Speak();
                return r;
            }

            public Statics() : i32
            {
                State.Total = 5;
                State.Total += 3;
                return State.Total;
            }

            public Enums() : i32
            {
                Color c = Color.Green;
                i32 r = (i32)c;
                switch (c)
                {
                    Color.Red => r += 10,
                    Color.Green => r += 20,
                    _ => r += 30,
                }
                if (c == Color.Green)
                    r += 100;
                return r;
            }

            public Arrays() : i32
            {
                i32[] a = new i32[5];
                loop (i32 i = 0..5)
                    a[i] = i * i;
                i32[] b = [1, 2, 3];
                return a[4] + b[2];
            }

            public Fields() : i32
            {
                Counter c = new Counter();
                c.Next();
                return c.Next() * 10 + c.Step;
            }

            public Numbers() : i64
            {
                i64 big = 3000000000;
                i32 small = 7;
                i64 sum = big + small;
                u8 b = 200;
                i32 widened = b + 100;
                f64 half = 0.5;
                f64 scaled = small * half;
                return sum + widened + (i64)scaled;
            }

            public Wraps() : i32
            {
                u8 x = 200;
                u8 y = x + 100;
                i8 z = 127;
                z++;
                return y * 1000 + z;
            }

            Pick(i32 x) : i32 => 1;
            Pick(i64 x) : i32 => 2;
            Pick(f64 x) : i32 => 3;
            Near(i64 x) : i32 => 1;
            Near(f64 x) : i32 => 2;
            Wide(u64 x) : i32 => 1;
            Wide(i128 x) : i32 => 2;

            Pair<T>(T a, T b) : T => b;
            Held<T>(Counter<T> c) : T => c.Held;
            public class Counter<T> { public: T Held; }
            public class Tally : Counter<i32> { }

            public Inference() : i64
            {
                i64 l = 5;
                u8 b = 7;
                Tally t = new Tally();
                t.Held = 9;
                return Pair(1, l) * 1000 + Pair(l, 2) * 100 + Pair(b, 300) + Held(t) * 10000;
            }

            public Overloads() : i32
            {
                i16 s = 3;
                u8 b = 4;
                u32 u = 5;
                return Pick(s) * 1000 + Pick(b) * 100 + Near(s) * 10 + Wide(u);
            }

            public Main() : i32 => Sum(10);
        }
        """;

    private static readonly (string Function, int? Argument, long Expected, string What)[] Cases =
    [
        ("Sum", 10, 45, "while loop, a group of locals, ++"),
        ("Fib", 20, 6765, "expression body, conditional, recursion"),
        ("Loops", null, 10639, "every loop form: for, range, do, counter, with break and continue"),
        ("Classify", 0, 10, "switch statement: constant"),
        ("Classify", 2, 20, "switch statement: or-pattern"),
        ("Classify", -5, 30, "switch statement: relational"),
        ("Classify", 9, 40, "switch statement: discard"),
        ("Grade", 85, 3, "switch expression with ranges"),
        ("Structs", null, 100064, "value type: stored constructor, method changing this, copies"),
        ("Animals", null, 2011115, "base class, virtual and abstract methods, overrides, base calls, inherited fields"),
        ("Types", null, 11111, "is, as, is with a binding, null checks"),
        ("Catches", 5, 10, "try without an exception"),
        ("Catches", -3, -1003, "typed catch"),
        ("Finally", 1, 11, "finally on the normal path"),
        ("Finally", -1, 110, "finally as the exception passes"),
        ("ReturnFinally", null, 57, "return inside try runs finally"),
        ("BreakFinally", null, 303, "break inside try runs finally"),
        ("Rethrow", -4, -12, "throw; inside a catch"),
        ("Nulls", null, 12, "null, == null, ??"),
        ("Statics", null, 8, "top-level field"),
        ("Enums", null, 121, "enum members, casts, switch over an enum"),
        ("Arrays", null, 19, "new arrays, array literals, indexing"),
        ("Fields", null, 92, "field initializers run in the implicit constructor"),
        ("Numbers", null, 3000000310, "implicit widening, literals typed by context, casts"),
        ("Wraps", null, 43872, "u8 and i8 arithmetic wraps around"),
        ("Inference", null, 95500, "T from every argument (a literal first, a wider second, u8 with 300) and a derived class against Counter<T>"),
        ("Overloads", null, 1111, "the nearest wider integer wins: i16 takes i32 over i64 (and f64), u32 takes u64 over i128"),
    ];

    private void Programs()
    {
        var watch = Stopwatch.StartNew();
        var compiled = AntrilCompiler.Compile(Phase3Source, "phase3.ant");
        long frontend = watch.ElapsedMilliseconds;
        Check("the phase 3 program compiles", compiled.Success, string.Join("\n    ", compiled.Diagnostics.Take(12)));
        if (!compiled.Success)
            return;
        var lowered = HirLowerer.Lower(compiled.Assembly!);
        Check("its HIR lowers", lowered.Success, string.Join("; ", lowered.Errors.Take(6)));
        if (!lowered.Success)
            return;
        var vm = Larva.IR.Compiler.Compile(lowered.Module!);
        Check("its IR passes the type and borrow checks", vm.Success, string.Join("; ", vm.TypeErrors.Concat<object>(vm.Diagnostics).Take(6)));
        var x64 = X64Backend.Compile(lowered.Module!);
        var llvm = LlvmBackend.Compile(lowered.Module!, new LlvmOptions { OptimizationLevel = 2 });
        Check("x64 and LLVM compile it", x64.Success && llvm.Success, $"{x64.Error} {llvm}");
        if (!vm.Success || !x64.Success || !llvm.Success)
            return;
        var interpreter = new Interpreter(vm.Il!);
        using var image = x64.Program!.Load();
        using var program = llvm.Program!;
        using var jit = program.Jit();
        foreach (var (name, argument, expected, what) in Cases)
        {
            string function = $"Phase3.$Module.{name}";
            object? onVm;
            try
            {
                onVm = argument is { } a ? interpreter.Run(function, a) : interpreter.Run(function);
            }
            catch (Exception e)
            {
                onVm = e.GetType().Name + ": " + e.Message;
            }
            long onX64, onLlvm;
            if (name == "Numbers")
            {
                onX64 = ((delegate* unmanaged<long>)image.Address(function))();
                onLlvm = ((delegate* unmanaged<long>)jit.Address(function))();
            }
            else
            {
                onX64 = argument is { } ax ? ((delegate* unmanaged<int, int>)image.Address(function))(ax) : ((delegate* unmanaged<int>)image.Address(function))();
                onLlvm = argument is { } al ? ((delegate* unmanaged<int, int>)jit.Address(function))(al) : ((delegate* unmanaged<int>)jit.Address(function))();
            }
            bool same = Equals(onVm, expected) && onX64 == expected && onLlvm == expected;
            Check($"{name}({argument}) = {expected}: {what}", same, $"LVM {onVm}, x64 {onX64}, LLVM {onLlvm}");
        }
    }

    /// <summary>
    /// Two files: names taken from a namespace with aliases (`import A, B from D as E, F;`), a namespace's alias, a
    /// namespace taken by name; and constants of every kind (declared after their use, from other constants, operators,
    /// casts, `?:`, a value made of constants, a type's, a local one, one as a switch arm), run on the VM.
    /// </summary>
    private void ImportsAndConstants()
    {
        const string library = """
            into namespace Lib
            {
                public enum Level { Low = 1, Mid = 2, High = 4 }

                public value Point
                {
                    public:
                        i32 x;
                        i32 y;
                        new(i32 x, i32 y) { this.x = x; this.y = y; }
                }

                const i32 Limit = Base * 4 + 2;
                const i32 Base = 10;
                const f64 Half = 1.0 / 2.0;
                const u256 Huge = (u256)1 << 200;
                const bool Debug = Limit > 40 && Half < 1.0;
                const Level Default = Level.Mid;
                const i8 Wrapped = (i8)300;
                const i32 Chosen = Debug ? 7 : 8;
                const i32 Flags = (i32)Level.High | (i32)Level.Low;
                const Point Origin = new Point(Base, -Base);

                Twice(i32 v) : i32 => v * 2;

                public class Settings
                {
                    public:
                        const i32 Retries = Limit / 2;
                }
            }

            into namespace Lib.Shapes
            {
                Sides() : i32 => 4;
            }
            """;
        const string program = """
            import Limit, Twice, Origin, Settings from Lib as Max, Double, Start, S;
            import Lib as L;
            import Shapes from Lib;

            Main() : i32 => App.Run();

            into namespace App
            {
                Run() : i32
                {
                    const i32 local = Max * 2;
                    const i32 next = local + 1;
                    i32 r = 0;
                    r += Max == 42 && next == 85 ? 1 : 0;
                    r += Double(21) == 42 && Shapes.Sides() == 4 && L.Twice(2) == 4 ? 10 : 0;
                    r += L.Half == 0.5 && L.Huge == (u256)1 << 200 && L.Debug && L.Wrapped == 44 && L.Chosen == 7 && (i32)L.Default == 2 && L.Flags == 5 ? 100 : 0;
                    r += Start.x + Start.y == 0 && Start.x == 10 && S.Retries == 21 ? 1000 : 0;
                    i32 seen = 0;
                    switch (Max)
                    {
                        L.Limit => seen = 1;
                        _ => seen = 2;
                    }
                    r += seen == 1 ? 10000 : 0;
                    return r;
                }
            }
            """;
        var compiled = AntrilCompiler.Compile([new SourceText(library, "lib.ant"), new SourceText(program, "app.ant")], "Imports");
        Check("partial imports with aliases, a namespace alias and constants of every kind compile", compiled.Success, string.Join("\n    ", compiled.Diagnostics.Take(12)));
        if (!compiled.Success)
            return;
        var lowered = HirLowerer.Lower(compiled.Assembly!, new LoweringOptions { StaticConstructorsFromEntry = true });
        var vm = lowered.Success ? Larva.IR.Compiler.Compile(lowered.Module!) : null;
        Check("they lower and pass the IR checks", vm is { Success: true }, string.Join("; ", lowered.Errors.Take(4)));
        if (vm is not { Success: true })
            return;
        object? result;
        try
        {
            result = new Interpreter(vm.Il!).Run(lowered.FunctionName(compiled.Assembly!.EntryPoint!.Value));
        }
        catch (Exception e)
        {
            result = e.Message;
        }
        Check("Run() = 11111: names taken and aliased, constants by every kind, a value constant, a type's, local ones, a switch arm", Equals(result, 11111L) || Equals(result, 11111), $"{result}");
    }

    /// <summary>
    /// Variadic templates: a value whose `static loop` declares a field per type (`T item;` gives item0, item1…), a
    /// constructor taking the pack, a body unrolled per type, a class with a fixed parameter before the pack, a function
    /// template expanded per call arity; each pack size its own generic declaration, run on the VM.
    /// </summary>
    private void Variadics()
    {
        const string program = """
            into namespace Var
            {
                public value Tuple<TArgs...>
                {
                    public:
                        static loop (T in TArgs)
                        {
                            T item;
                        }

                        new(TArgs value)
                        {
                            static loop (T in TArgs)
                                item = value;
                        }

                        Count() : i32
                        {
                            i32 n = 0;
                            static loop (T in TArgs)
                                n = n + 1;
                            return n;
                        }
                }

                public value Numbered<TArgs...>
                {
                    public:
                        static loop (T in TArgs from 1)
                        {
                            T Item;
                        }

                        new(TArgs value)
                        {
                            static loop (T in TArgs)
                                Item = value;
                        }
                }

                public class Keyed<K, TRest...>
                {
                    public:
                        K key;
                        TRest rest;
                }

                Arity<TArgs...>(TArgs values) : i32
                {
                    i32 n = 0;
                    static loop (T in TArgs)
                        n = n + 10;
                    return n;
                }

                Run() : i32
                {
                    Tuple<i32, f64, bool> three = new Tuple<i32, f64, bool>(7, 2.5, true);
                    Tuple<i64> one = new Tuple<i64>(40);
                    Keyed<i32, i32, i32> keyed = new Keyed<i32, i32, i32>();
                    keyed.key = 100;
                    keyed.rest0 = 20;
                    keyed.rest1 = 3;
                    i32 r = 0;
                    r += three.item0 == 7 && three.item1 == 2.5 && three.item2 && three.Count() == 3 ? 1 : 0;
                    r += one.item0 == 40 && one.Count() == 1 ? 10 : 0;
                    r += keyed.key + keyed.rest0 + keyed.rest1 == 123 ? 100 : 0;
                    r += Arity(1, 2.0, true) == 30 && Arity(5) == 10 ? 1000 : 0;
                    Numbered<i32, i32> two = new Numbered<i32, i32>(4, 5);
                    r += two.Item1 * 10 + two.Item2 == 45 ? 10000 : 0;
                    return r;
                }
            }

            Main() : i32 => Var.Run();
            """;
        var compiled = AntrilCompiler.Compile(program, "variadic.ant");
        Check("a value, a class and a function as variadic templates compile (one declaration per pack size)", compiled.Success, string.Join("\n    ", compiled.Diagnostics.Take(12)));
        if (!compiled.Success)
            return;
        var lowered = HirLowerer.Lower(compiled.Assembly!, new LoweringOptions { StaticConstructorsFromEntry = true });
        var vm = lowered.Success ? Larva.IR.Compiler.Compile(lowered.Module!) : null;
        Check("they lower and pass the IR checks", vm is { Success: true }, string.Join("; ", lowered.Errors.Take(4)));
        if (vm is not { Success: true })
            return;
        object? result;
        try
        {
            result = new Interpreter(vm.Il!).Run(lowered.FunctionName(compiled.Assembly!.EntryPoint!.Value));
        }
        catch (Exception e)
        {
            result = e.Message;
        }
        Check("Run() = 11111: fields per type, the pack's constructor and loops, a fixed parameter before the pack, a function per arity, members numbered `from 1`",
            Equals(result, 11111L) || Equals(result, 11111), $"{result}");
    }

    /// <summary>Wrong programs: each gets the diagnostic it should (by code), and nothing is produced.</summary>
    private void Diagnostics()
    {
        (string Source, string Code, string What)[] wrong =
        [
            ("F() : i32 { return x; }", "AN2033", "unknown name"),
            ("F() : u32 => '😀';", "", "a character beyond the BMP is its code point (a u32)"),
            ("F() : u16 { u16 c = '😀'; return c; }", "AN2084", "…which a u16 does not hold"),
            ("F() : u32 => 'ab';", "AN0005", "a character literal is one character"),
            ("F(i32 x) : str { str s = $\"{x} {\n return s; }", "AN0007", "an unterminated hole ends with its line (the parser went on forever)"),
            ("F(i32 x) : str { str s = $\"{x} {x\n return s; }", "AN0007", "a hole without its `}` ends with its line"),
            ("public static class Tools { } F() { Tools t = default(Tools); }", "AN1105", "a variable of a static class"),
            ("public static class Tools { } public class Box { Tools inner; }", "AN1105", "a field of a static class"),
            ("public static class Tools { } F(Tools t) { }", "AN1105", "a parameter of a static class"),
            ("public static class Tools { } F() : Tools[]? => null;", "AN1105", "a static class as an element type"),
            ("public static class Tools { public: static N() : i32 => 1; } F() : i32 => Tools.N();", "", "a static class's members are used through its name"),
            ("F() : i32 { i32 a = true; return a; }", "AN2084", "a bool is not an i32"),
            ("F(i32 n) : i32 { if (n > 0) return 1; }", "AN2001", "not every path returns"),
            ("class A { public: Speak() : i32 => 1; } class B : A { public: override Speak() : i32 => 2; }", "AN1036", "override without a virtual"),
            ("base class A { public: Area() : i32; } F() : i32 { A a = new A(); return 0; }", "AN2061", "new of a base class"),
            ("G(i32 a) : i32 => a; F() : i32 => G(1, 2);", "AN2056", "wrong number of arguments"),
            ("F() { throw; }", "AN2006", "throw; outside a catch"),
            ("F() : i32 { i64 big = 1; i32 small = big; return small; }", "AN2084", "narrowing needs a cast"),
            ("class A { } class B : A { } class C { } F(A a) : bool => a is C;", "AN2088", "a type test that can never hold"),
            ("F(i32 x) { switch (x) { 1 => return, ) => return, } }", "AN0019", "a stray `)` in a switch arm (the parser went on forever)"),
            ("class Box<T> { } F() { Box<i32> b = new Box<bool>(); }", "AN2084", "a class to the same class with other type arguments (no variance)"),
            ("class Box<T> { } class Derived<T> : Box<T> { } F() { Box<i32> b = new Derived<bool>(); }", "AN2084", "a class to a base with other type arguments"),
            ("class Box<T> { } class Fixed : Box<i32> { } F() { Box<bool> b = new Fixed(); }", "AN2084", "a class whose base fixes the type arguments, to the base with others"),
            ("class Box<T> { } F() { Box<Box<i32>> b = new Box<Box<bool>>(); }", "AN2084", "nested type arguments compared"),
            ("class Box<T> { } G(Box<i32> b) { } F() { G(new Box<bool>()); }", "AN2084", "an argument of the class with other type arguments"),
            ("class Box<T> { } F() : Box<i32> => new Box<bool>();", "AN2084", "a return of the class with other type arguments"),
            ("class Box<T> { } F() { Box<i32>? b = new Box<bool>(); }", "AN2084", "to a nullable of the class with other type arguments"),
            ("interface I<T> { } class Holder<T> : I<T> { } F() { I<bool> h = new Holder<i32>(); }", "AN2084", "an interface with other type arguments"),
            ("class Box<T> { } F(Box<i32> a) : bool => a is Box<bool>;", "AN2088", "a type test with other type arguments can never hold"),
            ("class Box<T> { } F(Box<i32> a) : Box<bool>? => a as Box<bool>;", "AN2088", "`as` with other type arguments can never hold"),
            ("class Box<T> { public: Bytes() : bool => this is Box<u8>; Narrow() : Box<u8>? => this as Box<u8>; }", "",
                "a type test of an open `Box<T>` against `Box<u8>` may hold (`T` may be `u8`)"),
            ("class Box<T> { } class Pair<A, B> { public: Test(Box<A> a) : bool => a is Box<B>; }", "", "two type parameters may be the same type"),
            ("class Box<T> { } class Pair<A, B> { public: Test(Box<Box<A>> a) : bool => a is Box<i32>; }", "AN2088",
                "a type parameter nested where the other side is closed and different can never hold"),
            ("public interface I : J { } public interface J : I { }", "AN1010", "interfaces that extend each other (a stack overflow)"),
            ("public interface I : J { } public interface J : K { } public interface K : I { }", "AN1010", "a cycle of three interfaces"),
            ("public interface I { } public interface J : I { } public interface K : I, J { }", "", "an interface reached twice is no cycle"),
            ("class Box<T> { } F(Box<i32> a) : Box<bool> => (Box<bool>)a;", "AN2084", "a cast to the class with other type arguments"),
            ("class Box<T> { } class Derived<T> : Box<T> { } F(Box<i32> a) : Derived<bool> => (Derived<bool>)a;", "AN2084", "a downcast to a derived class with other type arguments"),
            ("class Box<T> { } F(bool c) : Box<i32> => c ? new Box<i32>() : new Box<bool>();", "AN2080", "the branches of ?: with other type arguments (no common type)"),
            ("class Box<T> { } class Derived<T> : Box<T> { } class Fixed : Box<i32> { } interface I<T> { } class Holder<T> : I<T> { } "
                + "class Wrap<T> { public: Same(Box<T> b) : Box<T> => b; Up(Derived<T> d) : Box<T> => d; Nest(Box<Box<T>> b) : Box<Box<T>> => b; Pick(bool c, Derived<T> a, Box<T> b) : Box<T> => c ? a : b; } "
                + "G(Box<i32> b) { } F(Box<i32> a) : bool { Box<i32> d = new Derived<i32>(); Box<i32> f = new Fixed(); I<i32> h = new Holder<i32>(); Box<Box<i32>> n = new Box<Box<i32>>(); "
                + "Box<i32>? q = new Derived<i32>(); Box<i32> c = true ? new Derived<i32>() : a; G(new Derived<i32>()); Derived<i32>? s = d as Derived<i32>; Derived<i32> w = (Derived<i32>)d; return d is Derived<i32>; }",
                "", "the same type arguments: to the class, a base, an interface, nested, nullable, in arguments, ?:, is, as and casts, and `T` for `T`"),
            ("F() : i32 { loop (true) { } }", "", "an endless loop needs no return"),
            ("F(i32 n) : i32 { switch (n) { 1 => return 1, _ => return 0 } }", "", "a switch that returns on every arm"),
            ("class A { public: virtual F() : i32 => 1; } class B : A { public: F() : i32 => 2; }", "AN1038", "replacing a virtual without override"),
            ("F() : i32 { i32 a = 1; i32 a = 2; return a; }", "AN2003", "a local declared twice"),
            ("F() : f128 => 1e99999999;", "AN0041", "an exponent beyond every float (the compiler hung)"),
            ("F() : f32 => 1e-99999999;", "AN0041", "a negative exponent beyond every float"),
            ("F() : f128 { f128 a = 1e-400; f128 b = 1e4000; f16 c = 1e-400; return a + b + c; }", "", "exponents within the formats stay exact (and below f16 give zero)"),
            ("F() : i32 { return 1 }", "AN0010", "a missing semicolon"),
            ("public value Box<TArgs..., K> { }", "AN1420", "only the last type parameter is a pack"),
            ("public private class Both { }", "AN0038", "two access words"),
            ("public class C { public: Area() : i32 into namespace Geo => 1; }", "AN0039", "`into namespace` on a member of a type"),
            ("public attribute Tag { }", "AN1096", "an attribute without System.Attribute"),
            ("public class Open { fixed u8[] units; public: new(i32 n) : fixed(n) { } }", "AN1097", "an array kept in place in a class that is not final"),
            ("public final class Late { fixed u8[] units; i32 after; public: new(i32 n) : fixed(n) { } }", "AN1097", "a field after the array kept in place"),
            ("public class Item { } public final class Refs { fixed Item[] items; public: new(i32 n) : fixed(n) { } }", "AN1097", "an array kept in place of references"),
            ("public final class NoLength { fixed u8[] units; public: new(i32 n) { } }", "AN1098", "a constructor without `: fixed(n)`"),
            ("public final class NoMaker { fixed u8[] units; }", "AN1098", "no constructor for a class with an array kept in place"),
            ("public final class Leak { fixed u8[] units; public: new(i32 n) : fixed(n) { } Raw() : u8[] => units; }", "AN1099", "an array kept in place used as an array value"),
            ("public final class Ok { u64 tag; fixed u16[] units; public: new(i32 n) : fixed(n) { units[0] = 1; } N() : i32 => units.Length; }", "", "an array kept in place, read by index and Length"),
            ("private class Top { }", "AN1093", "a namespace's type that is private"),
            ("inherited F() : i32 => 1;", "AN1093", "a namespace's function that is inherited"),
            ("F(i32 n) : i32 { loop (true) { checked { if (n > 0) break; } return 1; } }", "AN2001", "a `break` in a `checked` block leaves the loop (the function fell off its end)"),
            ("F(i32 n) : i32 { loop (true) { unchecked { if (n > 0) break; } return 1; } }", "AN2001", "a `break` in an `unchecked` block"),
            ("F(i32 n) : i32 { loop (true) { checked { if (n > 0) return 2; } return 1; } }", "", "a `checked` block that leaves no path out of the loop"),
            ("F(i32 n) : i32 { checked { return n + 1; } }", "", "a `checked` block that returns"),
            ("public value V { inherited i32 x; }", "AN1094", "inherited in a value (nothing derives from it)"),
            ("public final class Fin { inherited G() { } }", "AN1094", "inherited in a final class"),
            ("public interface I { private H() : i32; }", "AN1095", "an interface member that is not public"),
            ("public class Ok { inherited K() { } } file class F { } public class P { file:\n G() { } }", "", "inherited in a class, file on a type and as a section"),
            ("public value P<TArgs...> { public: F() { static loop (T in TArgs from 1) { } } } G() { P<i32> p = new P<i32>(); }", "AN0036", "`from` on a loop of statements"),
            ("public value V { (i32) x; }", "AN0037", "a tuple type of one type"),
            ("F() : i32 { return (1, 2).Item1; }", "AN2142", "a tuple without System.ValueTuple"),
            ("into namespace System { public value ValueTuple<A, B> { public: A Item1; B Item2; new(A Item1, B Item2); } } F() : i32 { return (null, 1).Item2; }",
                "AN2143", "null in a tuple with nothing to type it"),
            ("public value Plain { static loop (T in TArgs) { T x; } }", "AN1420", "a static loop outside a variadic template"),
            ("public value Pack<TArgs...> { public: Bad() : i32 { TArgs wrong = default; return 0; } } F() { Pack<i32> p = new Pack<i32>(); }", "AN1421", "the pack used as one type"),
            ("public value Pack<TArgs...> { public: Bad() { static loop (T in Other) { } } } F() { Pack<i32> p = new Pack<i32>(); }", "AN1422", "a static loop over another pack"),
            ("const i32 A = B + 1; const i32 B = A + 1;", "AN1090", "a cycle of constants"),
            ("i32 counter = 0; const i32 K = counter;", "AN1091", "a constant made of a variable"),
            ("const i32[] T = [1];", "AN1088", "an array is not a constant"),
            ("const i32 NoValue;", "AN1087", "a constant without its value"),
            ("F() { const i32 x = 3; x = 4; }", "AN2140", "a local constant is never assigned"),
            ("public value R { public: i32 X; Bump() { X++; } } public class C { public: readonly R R; new() { } } F(C c) { c.R.X = 5; }", "AN2070", "a field of a value held in a readonly field (written through it)"),
            ("public value R { public: i32 X; Bump() { X++; } } public class C { public: readonly R R; new() { } } F(C c) { c.R.X++; }", "AN2070", "counting a field of a value held in a readonly field"),
            ("public value R { public: i32 X; Bump() { X++; } } public class C { public: readonly R R; new() { } } F(C c) { c.R.Bump(); }", "AN2070", "a method that changes a value held in a readonly field"),
            ("public value R { public: i32 X; Bump() { X++; } } F() { const R k = new R(); k.Bump(); }", "AN2140", "a method that changes a constant value"),
            ("public value R { public: i32 X; Bump() { X++; } Get() : i32 => X; } public class C { public: readonly R R; new() { } } F(C c) : i32 => c.R.Get() + c.R.X;", "", "reading a value held in a readonly field, and a method that only reads"),
            ("public value R { public: i32 X; Bump() { X++; } } public class C { public: readonly R R; new() { R.X = 1; R.Bump(); } }", "", "a readonly field's value changes in the constructor"),
            ("public value R { public: i32 X; Bump() { X++; } Twice() { Bump(); } } public class C { public: readonly R R; new() { } } F(C c) { c.R.Twice(); }", "AN2070", "a method that changes the value through another of its own"),
            ("public value R { public: i32 X; i32 P { get => X; set => X = value; } } public class C { public: readonly R R; new() { } } F(C c) { c.R.P = 1; }", "AN2070", "a value's setter on a value held in a readonly field"),
            ("public class Inner { public: i32 X; } public value R { public: Inner In; Touch() { In.X = 1; } } public class C { public: readonly R R; new() { } } F(C c) { c.R.In.X = 2; c.R.Touch(); }", "", "an object held in a readonly value changes freely"),
            ("import A, B from Lib as X; F() { }", "AN0034", "one alias for each name imported"),
            ("import Nope from Lib; into namespace Lib { G() : i32 => 1; } F() { }", "AN1084", "a name the namespace does not have"),
            ("import G from Lib as H; into namespace Lib { G() : i32 => 1; } F() : i32 => H();", "", "a function taken by name, by its alias"),
            ("const i32 Ok = (i32)3.9 + 1; const bool B = !(Ok > 3) || Ok == 4; F() : i32 => B ? Ok : 0;", "", "a cast and logic in constants"),
            ("public final class Fin { public: virtual G() : i32 => 1; }", "AN1100", "virtual in a final class (nothing overrides it)"),
            ("public final class Fin { public: virtual i32 P { get => 1; } }", "AN1100", "a virtual property in a final class"),
            ("public static class S { public: virtual G() : i32 => 1; }", "AN1100", "virtual in a static class"),
            ("public class A { public: final G() : i32 => 1; }", "AN1101", "final without override"),
            ("public class A { public: final virtual G() : i32 => 1; }", "AN1101", "final virtual (final goes with override)"),
            ("public class A { public: final i32 P { get => 1; } }", "AN1101", "a final property that does not override"),
            ("public class A { virtual i32 x; }", "AN1102", "a virtual field"),
            ("public class A { override i32 x; }", "AN1102", "an override field"),
            ("public class A { final i32 x; }", "AN1102", "a final field"),
            ("i32 counter;", "AN1103", "a field in a namespace"),
            ("public class A { public: virtual G() : i32 => 1; virtual i32 P { get => 1; } } public final class B : A { public: final override G() : i32 => 2; override i32 P { get => 2; } }",
                "", "final override of a method, override of a property in a final class"),
            ("public bitflag enum Access { Read, Write, Run, ReadWrite = Read | Write, All = Access.ReadWrite | Run, Masked = All & ~Write } F() : i32 => (i32)Access.Masked;",
                "", "a bitflag enum's members made of the members before them (| & ~, by name or through the enum)"),
            ("public bitflag enum A { X, Y } public bitflag enum B { Z, Both = A.X | A.Y } public enum C : i32 { Low = 2, High = Low * 10 } F() : i32 => (i32)B.Both + (i32)C.High;",
                "", "members made of another enum's members, and arithmetic on them"),
            ("public enum E : i32 { One = 1, Two = Three - 1, Three = 3 }", "AN1019", "a member made of one declared after it"),
            ("public value Rgb { public: u8 r; new(u8 r) { this.r = r; } } public enum Color : Rgb { Red = new Rgb(255) } const Color Fav = Color.Red; const Rgb Raw = (Rgb)Color.Red; F() : i32 => Raw.r;",
                "", "the members of an enum of values are constants (and so is their value)"),
            // Medium findings 2026-11-12 (M5): what the binder used to leave to the lowering, without a position.
            ("F() { f64 d = 1.0; d &= 2.0; }", "AN2074", "`&=` on a float"),
            ("F() { bool b = true; b |= false; i32 n = 1; n ^= 2; }", "", "`|=` on a bool, `^=` on an integer"),
            ("public bitflag enum Fl { A, B } F() { Fl f = Fl.A; f |= Fl.B; }", "", "`|=` on a bitflag enum"),
            ("public class C { public: i32 Q { get; set; } } F(C c) : i32 { c.Q++; --c.Q; return c.Q--; }", "", "`++` and `--` on a property (through its accessors)"),
            ("public value V { V inner; }", "AN1106", "a value that holds itself"),
            ("public value A { B b; } public value B { A a; }", "AN1106", "a value that holds itself through another value"),
            ("public value W<T> { T item; } public value S { W<S> w; }", "AN1106", "a value that holds itself through a generic value"),
            ("public value W<T> { T item; } public value S { W<i32> w; S? maybe; S[] many; }", "", "a value holding other values, a nullable and an array of itself"),
            ("F(i32 x) : i32 => 1; F(i64 x) : i32 => 2; G() : i32 { i16 s = 3; return F(s); }", "", "F(i16) with F(i32) and F(i64) is not ambiguous"),
            ("F(i64 x) : i32 => 1; F(f64 x) : i32 => 2; G() : i32 { i16 s = 3; return F(s); }", "", "nor with F(i64) and F(f64)"),
            ("F(i16 x) : i32 => 1; F(u16 x) : i32 => 2; G() : i32 { u8 s = 3; return F(s); }", "AN2058", "F(u8) with F(i16) and F(u16): the same width, still ambiguous"),
            ("Pair<T>(T a, T b) : T => b; G() : i64 { i64 l = 1; return Pair(1, l) + Pair(l, 1); }", "", "T inferred from every argument: a literal first or second"),
            ("Pair<T>(T a, T b) : T => b; G() : f64 => Pair(1, 2.5);", "", "T is the type the other argument widens to"),
            ("Pair<T>(T a, T b) : T => b; G() : i32 => Pair(1, true);", "AN2084", "two arguments that convert neither way: the second does not fit the first's T"),
            ("public class B<T> { public: T V; } public class D : B<i32> { } Take<T>(B<T> b) : T => b.V; G(D d) : i32 => Take(d);", "", "a derived class against a parameter of its generic base"),
            ("F() { i8 j = 100 + 100; }", "AN2028", "constant arithmetic outside the type does not wrap"),
            ("F() { i8 m = 127 + 1; }", "AN2028", "…by one"),
            ("F() { u8 n = 3 - 5; }", "AN2028", "…below zero in an unsigned type"),
            ("F() { i8 s = 1 << 7; }", "AN2028", "…by a shift"),
            ("const i8 K = 100 + 100;", "AN1092", "…in a const declaration"),
            ("F() { const i8 k = 50 * 3; }", "AN1092", "…in a local const"),
            ("public enum E : i8 { A = 100 + 100 }", "AN1019", "…in an enum member's value"),
            ("F() : i64 { i64 big = 2147483647 + 1; i32 ok = 1 << 7; i8 fits = 60 + 60; i8 wrapped = (i8)300; return big + ok + fits + wrapped; }", "", "constant arithmetic that fits (the literals take the type wanted), and a cast that wraps"),
            ("F() : u256 => (u256)0 - (u256)1;", "", "cast literals say their type themselves: the run time's arithmetic (wraps)"),
            ("F() : i128 => 1i128 << 127;", "", "…and so do suffixed ones"),
            ("import A; import B; into namespace A { public class T { } } into namespace B { public class T { } } F() { T t = new T(); }", "AN1107", "a type two imported namespaces both have"),
            ("import A; import B; into namespace A { public class T { public: static N() : i32 => 1; } } into namespace B { public class T { } } F() : i32 => T.N();", "AN1107", "…reached through its static members"),
            ("import A; import B; into namespace A { public class T { } } into namespace B { public class T { } } into namespace A { F() { T t = new T(); } }", "", "…but the current namespace's wins"),
            ("import T from A; import B; into namespace A { public class T { } } into namespace B { public class T { } public class U { } } F() { T t = new T(); U u = new U(); }", "", "…and one imported by name wins"),
            ("import Nope; F() { }", "AN1084", "an import of a namespace that does not exist"),
            ("import A; into namespace A { public class T { } } F() { T t = new T(); }", "", "an import of a namespace that exists"),
            ("public enum C { R, G, B } F(C c) : i32 => c switch { C.R => 1, C.G => 2 };", "AN2147", "a switch expression over an enum that misses a member, without `_`"),
            ("F(bool b) : i32 => b switch { true => 1 };", "AN2147", "…over a bool that misses `false`"),
            ("public enum C { R, G, B } F(C c) : i32 => c switch { C.R => 1, C.G or C.B => 2 };", "", "every member named (in an or-pattern too)"),
            ("F(bool b) : i32 => b switch { true => 1, false => 0 };", "", "both bools named"),
            ("public enum C { R, G, B } F(C c) : i32 => c switch { C.R => 1, _ => 2 };", "", "a `_` arm covers the rest"),
            ("public enum C { R, G, B } F(C c) : i32 => c switch { C.R => 1, var o when o == C.G => 2, C.B => 3 };", "", "a guarded arm: what it covers is not counted (the run time says)"),
            ("F() : str { return \"\\x41\"; }", "AN0008", "an unknown escape"),
            ("F() : u16 => '\\q';", "AN0008", "…in a character literal"),
            ("F() : u16 => '\\u12';", "AN0008", "`\\u` with too few digits"),
            ("F() : u32 => '\\U00110000';", "AN0008", "`\\U` beyond U+10FFFF"),
            ("F() : u16 { u16 a = '\\u0041'; return a; }", "", "`\\uXXXX`"),
            ("F() : u32 { u32 a = '\\U0001F600'; return a; }", "", "`\\U000XXXXX` beyond the BMP"),
        ];
        foreach (var (text, code, what) in wrong)
        {
            var result = AntrilCompiler.Compile(text, "wrong.ant");
            if (code.Length == 0)
            {
                Check($"accepted: {what}", result.Success, string.Join("; ", result.Diagnostics));
                continue;
            }
            Check($"{code}: {what}", !result.Success && result.Diagnostics.Any(d => d.Code == code),
                string.Join("; ", result.Diagnostics.Select(d => d.ToString())));
        }
        var located = AntrilCompiler.Compile("F() : i32\n{\n    return nope;\n}\n", "where.ant");
        Check("diagnostics say file, line and column", located.Diagnostics.FirstOrDefault()?.ToString().StartsWith("where.ant(3,12): error AN2033", StringComparison.Ordinal) == true,
            located.Diagnostics.FirstOrDefault()?.ToString());
    }

    /// <summary>
    /// Programs that used to hang the compiler or overflow its stack (audit 2026-11-12, A30-A33): each ends, with a
    /// diagnostic when it is wrong or compiled when it is right. Each case runs with a time limit.
    /// </summary>
    private void Pathological()
    {
        static string Program(string body, string more = "") => "into namespace P {\n" + more + "F(i32 a) : i32 {\n" + body + "\n}\n}\n";
        string deepType = string.Concat(Enumerable.Repeat("System.Collections.List<", 1000)) + "i32" + new string('>', 1000);
        (string What, string Source, string Code)[] cases =
        [
            ("1000 nested parentheses", Program("return " + new string('(', 1000) + "a" + new string(')', 1000) + ";"), "AN0040"),
            ("500 nested parentheses", Program("return " + new string('(', 500) + "a" + new string(')', 500) + ";"), ""),
            ("500 nested calls", Program("return " + string.Concat(Enumerable.Repeat("G(", 500)) + "a" + new string(')', 500) + ";", "G(i32 x) : i32 => x + 1;\n"), ""),
            ("500 nested blocks", Program(new string('{', 500) + "a = 2;" + new string('}', 500) + " return a;"), ""),
            ("1000 nested blocks", Program(new string('{', 1000) + "a = 2;" + new string('}', 1000) + " return a;"), "AN0040"),
            ("500 prefix operators", Program("return " + new string('~', 500) + "a;"), ""),
            ("2000 prefix operators", Program("return " + new string('~', 2000) + "a;"), "AN0040"),
            ("a chain of 100000 terms", Program("return " + string.Join(" + ", Enumerable.Repeat("a", 100_000)) + ";"), ""),
            ("a chain of 50000 comparisons and conditions", Program("return " + string.Join(" && ", Enumerable.Repeat("a < 1", 50_000)) + " ? 1 : 0;"), ""),
            ("1000 nested type arguments", Program("return a;", deepType + " x;\n"), "AN0040"),
        ];
        foreach (var (what, text, code) in cases)
        {
            var task = Task.Run(() => AntrilCompiler.Compile(text, "deep.ant"));
            bool ended = task.Wait(TimeSpan.FromSeconds(60));
            Check($"{what}: the compiler ends", ended);
            if (!ended)
                continue;
            var result = task.Result;
            if (code.Length == 0)
                Check($"{what}: compiles", result.Success, string.Join("; ", result.Diagnostics.Take(3)));
            else
                Check($"{what}: {code}", !result.Success && result.Diagnostics.Any(d => d.Code == code), string.Join("; ", result.Diagnostics.Take(3)));
        }
        // After a nesting error the rest of the file is read: its other declarations are found.
        var after = AntrilCompiler.Compile(Program("return a;", deepType + " x;\nH() : i32 => 1;\n") + "into namespace Q { K() : i32 => P.H(); }", "deep.ant");
        Check("after a nesting error the rest of the file is read (only AN0040 is reported)", after.Diagnostics.Count > 0 && after.Diagnostics.All(d => d.Code == "AN0040"), string.Join("; ", after.Diagnostics.Take(3)));
    }

    // ── Speed ──────────────────────────────────────────

    /// <summary>
    /// A large program (classes in chains of three with virtual methods, loops, calls and fields, plus top-level
    /// functions): how fast it parses, binds and lowers, in lines per second.
    /// </summary>
    private void Speed()
    {
        var files = Generate(files: 40, classesPerFile: 50, methodsPerClass: 8);
        int lines = files.Sum(f => f.Text.Count(c => c == '\n'));
        AntrilCompiler.Compile(Generate(2, 3, 2)); // warm the JIT
        var watch = Stopwatch.StartNew();
        foreach (var file in files)
            Lexer.Tokenize(file, []);
        long lexing = watch.ElapsedMilliseconds;
        watch.Restart();
        foreach (var file in files)
            Parser.Parse(file, []);
        long parsing = watch.ElapsedMilliseconds;
        // The best of six runs: the machine's noise is large next to one run, and the first runs still meet code the JIT
        // has not optimized yet (tiered compilation).
        AntrilResult result = null!;
        long frontend = long.MaxValue;
        for (int run = 0; run < 6; run++)
        {
            GC.Collect();
            watch.Restart();
            var attempt = AntrilCompiler.Compile(files, "Big");
            if (watch.ElapsedMilliseconds < frontend)
            {
                frontend = watch.ElapsedMilliseconds;
                result = attempt;
            }
        }
        Check($"a {lines:N0}-line program compiles", result.Success, string.Join("; ", result.Diagnostics.Take(4)));
        if (!result.Success)
            return;
        watch.Restart();
        var lowered = HirLowerer.Lower(result.Assembly!);
        long lowering = watch.ElapsedMilliseconds;
        Check("and lowers", lowered.Success, string.Join("; ", lowered.Errors.Take(3)));
        GC.Collect();
        watch.Restart();
        var sequential = AntrilCompiler.Compile(files, "Big", parallel: false);
        long oneThread = watch.ElapsedMilliseconds;
        Check("one thread gives the same HIR", Larva.Hir.Text.HirPrinter.Print(sequential.Assembly!, types: true) == Larva.Hir.Text.HirPrinter.Print(result.Assembly!, types: true));
        var t = result.Timings;
        Check("the frontend runs at over 500,000 lines per second", lines * 1000L / Math.Max(1, frontend) > 500_000, $"{lines * 1000L / Math.Max(1, frontend):N0} lines/s");
    }

    internal const string BenchSource = """
        into namespace Bench
        {
            public base class Shape
            {
                public:
                    Area() : i32;
            }

            public final class Square : Shape
            {
                public:
                    i32 S;
                    new(i32 S);
                    override Area() : i32 => S * S;
            }

            public final class Triangle : Shape
            {
                public:
                    i32 B;
                    new(i32 B);
                    override Area() : i32 => B * 3;
            }

            public final class Cell
            {
                public:
                    i32 V;
                    new(i32 V);
            }

            public value Point
            {
                public:
                    i32 X, Y;
                    Dot() : i32 => X * X + Y * Y;
            }

            public final class Failure { }

            public Fib(i32 n) : i32 => n < 2 ? n : Fib(n - 1) + Fib(n - 2);

            public Dispatch(i32 n) : i32
            {
                Shape a = new Square(3);
                Shape b = new Triangle(5);
                i32 s = 0;
                loop (i32 i = 0..n)
                    s += ((i & 1) == 0 ? a : b).Area();
                return s;
            }

            public Alloc(i32 n) : i32
            {
                i32 s = 0;
                loop (i32 i = 0..n)
                    s += new Cell(i).V;
                return s;
            }

            public Structs(i32 n) : i32
            {
                Point p = new Point();
                p.Y = 2;
                i32 s = 0;
                loop (i32 i = 0..n)
                {
                    p.X = i & 1023;
                    s += p.Dot();
                }
                return s;
            }

            public Throw(i32 n) : i32
            {
                i32 s = 0;
                loop (i32 i = 0..n)
                {
                    try
                    {
                        throw new Failure();
                    }
                    catch (Failure f)
                    {
                        s++;
                    }
                }
                return s;
            }

            public Mono(i32 n) : i32
            {
                Shape a = new Square(3);
                i32 s = 0;
                loop (i32 i = 0..n)
                    s += a.Area();
                return s;
            }

            public Main(i32 which, i32 n) : i32 => which switch
            {
                0 => Fib(n),
                1 => Dispatch(n),
                2 => Alloc(n),
                3 => Structs(n),
                4 => Throw(n),
                _ => Mono(n),
            };
        }
        """;

    /// <summary>The benchmarks written in Antril, built into real executables (LLVM -O3 and x64), against .NET.</summary>
    private void Executables()
    {
        Assert.SkipWhen(Larva.Native.Toolchain.Find() is null, "no MSVC toolchain: executables not built");
        var watch = Stopwatch.StartNew();
        var compiled = AntrilCompiler.Compile(BenchSource, "bench.ant");
        Check("the benchmark source compiles", compiled.Success, string.Join("; ", compiled.Diagnostics.Take(6)));
        if (!compiled.Success)
            return;
        var lowered = HirLowerer.Lower(compiled.Assembly!);
        Check("and lowers", lowered.Success, string.Join("; ", lowered.Errors.Take(4)));
        if (!lowered.Success)
            return;
        var objectFile = Larva.IR.Compiler.CompileObject(lowered.Module!);
        Check("its IR checks", objectFile.Success, string.Join("; ", objectFile.TypeErrors.Concat<object>(objectFile.Diagnostics).Take(4)));
        if (!objectFile.Success)
            return;
        var linked = Larva.IR.Linking.Linker.Link([objectFile.Object!], new Larva.IR.Linking.LinkOptions { Entry = "Bench.$Module.Main", LowerToIl = false });
        string folder = TestEnvironment.TempFolder("antril-bench");
        Directory.CreateDirectory(folder);
        var exes = new List<(string Name, string Path)>();
        foreach (var (name, backend, level) in new[] { ("LLVM -O3", Larva.Driver.Backend.Llvm, 3), ("x64", Larva.Driver.Backend.X64, 0) })
        {
            string exe = Path.Combine(folder, $"bench-{backend}.exe".ToLowerInvariant());
            var (ok, message) = Larva.Driver.ExecutableBuilder.Build(linked, exe, new Larva.Driver.ExecutableOptions { Backend = backend, OptimizationLevel = level });
            Check($"it builds an executable ({name})", ok, message);
            if (ok)
                exes.Add((name, exe));
        }
        (string Name, int N, Func<int, int> Reference)[] benchmarks =
        [
            ("Fib(32)", 32, Benchmarks.Reference.Fib),
            ("Dispatch", 50_000_000, Benchmarks.Reference.Dispatch),
            ("Alloc", 10_000_000, Benchmarks.Reference.Alloc),
            ("Structs", 50_000_000, Benchmarks.Reference.Structs),
            ("Throw", 20_000, Benchmarks.Reference.Throw),
            ("Mono", 50_000_000, Benchmarks.Reference.Mono),
        ];
        for (int which = 0; which < benchmarks.Length; which++)
        {
            var (name, n, reference) = benchmarks[which];
            int expected = reference(n);
            var cells = new List<string>();
            bool same = true;
            foreach (var (_, exe) in exes)
            {
                var (_, baseline) = Benchmarks.Execute(exe, which, 0);
                var (output, ms) = Benchmarks.Execute(exe, which, n);
                same &= output == expected.ToString();
                cells.Add($"{Math.Max(0, ms - baseline),9:0} ms");
            }
            var timer = Stopwatch.StartNew();
            reference(n);
            double netMs = timer.Elapsed.TotalMilliseconds;
            Check($"{name}: the Antril executables give {expected}", same);
        }
    }

    internal static List<SourceText> Generate(int files, int classesPerFile, int methodsPerClass)
    {
        var sources = new List<SourceText>();
        for (int f = 0; f < files; f++)
        {
            var text = new StringBuilder();
            text.Append($"into namespace Big.N{f}\n{{\n");
            for (int c = 0; c < classesPerFile; c++)
            {
                bool root = c % 3 == 0;
                string baseName = root ? "" : $" : C{c - 1}";
                text.Append($"    public {(root ? "base class" : "class")} C{c}{baseName}\n    {{\n        public:\n");
                text.Append($"            i32 value{c} = {c};\n");
                if (root)
                    text.Append("            Area() : i32;\n");
                else
                    text.Append($"            override Area() : i32 => value{c} * 2;\n");
                for (int m = 0; m < methodsPerClass; m++)
                {
                    text.Append($"            M{m}(i32 x) : i32\n            {{\n");
                    text.Append($"                i32 acc = value{c};\n");
                    text.Append("                loop (i32 k = 0; k < x; k++)\n                {\n");
                    text.Append("                    if (k % 3 == 0)\n                        continue;\n");
                    text.Append($"                    acc += k * {m + 1} + Area();\n");
                    text.Append("                }\n");
                    text.Append($"                return acc > 1000 ? 1000 : acc + Helper{f}(x, {m});\n            }}\n");
                }
                text.Append("    }\n");
            }
            text.Append($"    Helper{f}(i32 a, i32 b) : i32\n    {{\n        i32 s = 0;\n        loop (i32 i = 0..a)\n            s += i * b;\n        return s;\n    }}\n");
            text.Append("}\n");
            sources.Add(new SourceText(text.ToString(), $"big{f}.ant"));
        }
        return sources;
    }
}
