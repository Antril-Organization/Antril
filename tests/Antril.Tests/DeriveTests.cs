// `derive` functions and `static loop (field …)`: an interface in one library, the types that implement it in a program,
// each given its own copy of the interface's code, made over its fields when compiling.
using Antril.Binding;
using Antril.Syntax;
using Larva.Hir.Encoding;
using Larva.Hir.Linking;
using Larva.Hir.Lowering;
using Larva.Runtime.Vm;

namespace Antril.Tests;

/// <summary>Compile-time code per field: derived members across assemblies, field facts, `static if`, generic calls through the interface, and the diagnostics.</summary>
public sealed class DeriveTests : CheckedTest
{
    private const string Library = """
        import System;

        into namespace Shapes
        {
            public attribute KeyAttribute { }

            public attribute WeightAttribute
            {
                public:
                    i32 Value;
                    new(i32 Value);
            }

            public attribute SkipAttribute { }

            public interface IMeasured<TSelf : IMeasured<TSelf>>
            {
                /// How many fields are measured (not skipped).
                derive static Columns() : i32
                {
                    i32 n = 0;
                    static loop (field F f in TSelf)
                    {
                        static if (!f.Has<SkipAttribute>())
                            n++;
                    }
                    return n;
                }

                /// The fields' names (the key's marked), as one text.
                derive static Names() : str
                {
                    str text = "";
                    static loop (field F f in TSelf)
                    {
                        static if (f.Index > 0)
                            text = text + ",";
                        text = text + nameof(f);
                        static if (f.Has<KeyAttribute>())
                            text = text + "*";
                        static if (f.Nullable)
                            text = text + "?";
                        text = text + ":" + f.TypeName;
                    }
                    return text;
                }

                /// Every integer field times its weight (1 without one).
                derive Weighed() : i64
                {
                    i64 sum = 0;
                    static loop (field F f in TSelf)
                    {
                        static if (f.TypeName == "i32" || f.TypeName == "i64")
                            sum += (i64)this.f * f.Argument<WeightAttribute>(0, 1);
                    }
                    return sum;
                }

                /// A copy made field by field (the class has a constructor without parameters).
                derive Copy() : TSelf
                {
                    TSelf made = new TSelf();
                    static loop (field F f in TSelf)
                        made.f = this.f;
                    return made;
                }

                /// The number of fields.
                derive static Count() : i32
                {
                    i32 count = 0;
                    static loop (field F f in TSelf)
                        count = f.Count;
                    return count;
                }
            }

            /// Through the interface, for any measured type.
            public static class Measure
            {
                public:
                    static Describe<T : IMeasured<T>>(T item) : i64 => T.Columns() * 1000 + item.Weighed();
            }
        }
        """;

    private const string Program = """
        import System;
        import Shapes;

        class Box : IMeasured<Box>
        {
            public:
                [Key] i64 Id;
                [Weight(10)] i32 Width;
                i32 Height;
                str? Label;
                [Skip] bool Dirty;

                new() { }
                new(i64 Id, i32 Width, i32 Height) { this.Id = Id; this.Width = Width; this.Height = Height; }
        }

        class Point : IMeasured<Point>
        {
            public:
                i32 X;
                i32 Y;
                new() { }

                // The type's own: kept instead of the interface's.
                static Count() : i32 => 99;
        }

        Main() : i32
        {
            Box box = new Box(7, 3, 4);
            Box copy = box.Copy();
            i32 r = 0;
            r += Box.Columns() == 4 && Point.Columns() == 2 ? 1 : 0;
            r += Box.Names() == "Id*:i64,Width:i32,Height:i32,Label?:str,Dirty:bool" && Point.Names() == "X:i32,Y:i32" ? 10 : 0;
            r += box.Weighed() == 7 + 30 + 4 ? 100 : 0;
            r += copy.Id == 7 && copy.Width == 3 && copy.Height == 4 && copy != box ? 1000 : 0;
            r += Measure.Describe(box) == 4041 && Measure.Describe(copy) == 4041 ? 10000 : 0;
            r += Box.Count() == 5 && Point.Count() == 99 ? 100000 : 0;
            return r;
        }
        """;

    [Fact]
    public void Derived_members_are_made_per_type_across_assemblies_and_run()
    {
        var core = AntrilCompiler.Compile(Repositories.LibrarySources("System.Core"), "System.Core");
        Check("System.Core compiles", core.Success, string.Join("; ", core.Diagnostics.Take(4)));
        if (!core.Success)
            return;
        byte[] coreBytes = LvaFormat.Write(core.Assembly!);
        var shapes = AntrilCompiler.Compile([new SourceText(Library, "shapes.ant")], [LvaFormat.Read(coreBytes)], "Shapes");
        Check("the library with the interface compiles", shapes.Success, string.Join("\n    ", shapes.Diagnostics.Take(8)));
        if (!shapes.Success)
            return;
        Check("it carries the interface's text for the programs that implement it",
            shapes.Assembly!.LanguageData.Any(d => d.Name.StartsWith(Derive.DataPrefix, StringComparison.Ordinal)));
        byte[] shapesBytes = LvaFormat.Write(shapes.Assembly!);
        var program = AntrilCompiler.Compile([new SourceText(Program, "boxes.ant")], [LvaFormat.Read(coreBytes), LvaFormat.Read(shapesBytes)], "Boxes");
        Check("a program whose types implement it compiles (their copies made over their fields)", program.Success, string.Join("\n    ", program.Diagnostics.Take(8)));
        if (!program.Success)
            return;
        var linked = HirLinker.Link("Boxes", [coreBytes, shapesBytes, LvaFormat.Write(program.Assembly!)]);
        Check("they link", linked.Success, string.Join("; ", linked.Errors.Take(4)));
        if (!linked.Success)
            return;
        var lowered = HirLowerer.Lower(linked.Assembly!, new LoweringOptions { StaticConstructorsFromEntry = true });
        var vm = lowered.Success ? Larva.IR.Compiler.Compile(lowered.Module!) : null;
        Check("they lower and pass the IR checks", vm is { Success: true }, string.Join("; ", lowered.Errors.Take(4)) + string.Join("; ", vm?.TypeErrors.Take(4) ?? []));
        if (vm is not { Success: true })
            return;
        object? result;
        try
        {
            result = new Interpreter(vm.Il!).Run(lowered.FunctionName(linked.Assembly!.EntryPoint!.Value));
        }
        catch (Exception e)
        {
            result = e.Message;
        }
        Check("Main() = 111111: counts with a skip, names and facts, weights from attributes, a copy, generic calls through the interface, the type's own member kept",
            Equals(result, 111111L) || Equals(result, 111111), $"{result}");
    }

    /// <summary>`T?` of a `class` T: an optional handle, the same value as the nullable of the class T stands for.</summary>
    [Fact]
    public void A_class_type_parameter_s_nullable_is_the_class_s_nullable()
    {
        const string source = """
            import System;

            class Box { public: i32 N; new(i32 N); }

            final class Holder<T : class>
            {
                T item;
                public:
                    new(T item) { this.item = item; }
                    Find(bool here) : T?
                    {
                        T? found = null;
                        if (here)
                            found = item;
                        return found;
                    }
                    Or(T? maybe) : T => maybe ?? item;
            }

            Main() : i32
            {
                Holder<Box> h = new Holder<Box>(new Box(7));
                i32 r = 0;
                Box? some = h.Find(true);
                if (some is Box b)
                    r += b.N == 7 ? 1 : 0;
                r += h.Find(false) == null ? 10 : 0;
                r += (h.Find(false) ?? new Box(3)).N == 3 ? 100 : 0;
                r += h.Or(null).N == 7 && h.Or(new Box(5)).N == 5 ? 1000 : 0;
                return r;
            }
            """;
        var core = AntrilCompiler.Compile(Repositories.LibrarySources("System.Core"), "System.Core");
        byte[] coreBytes = core.Success ? LvaFormat.Write(core.Assembly!) : [];
        var program = core.Success ? AntrilCompiler.Compile([new SourceText(source, "holder.ant")], [LvaFormat.Read(coreBytes)], "Holder") : core;
        Check("it compiles", program.Success, string.Join("\n    ", program.Diagnostics.Take(6)));
        if (!program.Success)
            return;
        var linked = HirLinker.Link("Holder", [coreBytes, LvaFormat.Write(program.Assembly!)]);
        var lowered = HirLowerer.Lower(linked.Assembly!, new LoweringOptions { StaticConstructorsFromEntry = true });
        var vm = lowered.Success ? Larva.IR.Compiler.Compile(lowered.Module!) : null;
        Check("it lowers and passes the IR checks", vm is { Success: true }, string.Join("; ", lowered.Errors.Take(4)) + string.Join("; ", vm?.TypeErrors.Take(4) ?? []));
        if (vm is not { Success: true })
            return;
        object? result = new Interpreter(vm.Il!).Run(lowered.FunctionName(linked.Assembly!.EntryPoint!.Value));
        Check("Main() = 1111: a found value, none, `??` on it, a nullable argument", Equals(result, 1111L) || Equals(result, 1111), $"{result}");
    }

    [Fact]
    public void Wrong_derive_code_gets_its_diagnostics()
    {
        (string Source, string Code, string What)[] wrong =
        [
            ("public interface I<TSelf : I<TSelf>> { derive static N() : i32 { i32 n = 0; static loop (field F f in TSelf) n++; return n; } } public class A : I<A> { public: i32 x; }",
                "", "a derive function and a type that gets it"),
            ("public interface I<T> { derive static N() : i32 { i32 n = 0; static loop (field F f in T) n++; return n; } } public class A : I<i32> { public: i32 x; }",
                "AN1423", "a field loop over a type parameter that is not the implementing type"),
            ("F() : i32 { i32 n = 0; static loop (field F f in T) n++; return n; }", "AN1423", "a field loop outside a derive function"),
            ("public interface I<TSelf : I<TSelf>> { derive static N(i32 k) : i32 { static if (k > 0) return 1; return 0; } } public class A : I<A> { }",
                "AN1424", "a static if on something not known when the copy is made"),
            ("F() : i32 { static if (true) return 1; return 0; }", "AN1424", "a static if outside a derive function"),
            ("public class A { public: derive N() : i32 => 1; }", "AN1425", "derive outside an interface"),
            ("public interface I<TSelf : I<TSelf>> { derive N() : i32; } public class A : I<A> { }", "AN1425", "a derive function without a body"),
        ];
        var core = AntrilCompiler.Compile(Repositories.LibrarySources("System.Core"), "System.Core");
        if (!core.Success)
        {
            Check("System.Core compiles", false, string.Join("; ", core.Diagnostics.Take(4)));
            return;
        }
        var reference = LvaFormat.Read(LvaFormat.Write(core.Assembly!));
        foreach (var (source, code, what) in wrong)
        {
            var result = AntrilCompiler.Compile([new SourceText(source, "wrong.ant")], [reference], "Wrong");
            var codes = result.Diagnostics.Where(d => d.IsError).Select(d => d.Code).ToList();
            Check($"{what}: {(code.Length == 0 ? "no error" : code)}", code.Length == 0 ? codes.Count == 0 : codes.Contains(code), string.Join("; ", result.Diagnostics.Take(4)));
        }
    }
}
