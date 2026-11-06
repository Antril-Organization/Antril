// The linker cases that compile Antril (System.Core and a library per GC domain) or build .ant projects with
// `larva build`. RunEverywhere and Object are copies of Larva.Tests' LinkerPrograms fixture.
using System.Diagnostics;
using Antril.Binding;
using Antril.Syntax;
using Larva.IR;
using Larva.IR.Linking;
using Larva.Runtime.Vm;
using static Larva.IR.Operand;

namespace Antril.Tests;

/// <summary>GC domains with Antril libraries: System.Core per domain, and `larva build` of mixed, RefCount and tracing programs.</summary>
public sealed unsafe class LinkerAntrilTests : CheckedTest
{
    private const string LibSource = """
        import System;

        public static class Lib
        {
            public:
                Twice(i32 n) : i32
                {
                    List<i32> items = new List<i32>();
                    loop (i32 i = 0..n)
                        items.Add(i * 2);
                    i32 s = 0;
                    loop (i32 x in items)
                        s += x;
                    return s;
                }

                Shout(i32 n) : i32
                {
                    str s = "";
                    loop (i32 i = 0..n)
                        s = s + "ab";
                    return s.Length;
                }

                Greet(u8* name) : u8*
                {
                    str who = Exchange.Text(name);
                    return Exchange.Pack("hi " + who);
                }

                Name() : str => "lib";
        }
        """;

    /// <summary>An Antril library compiled with System.Core, lowered and checked for <paramref name="gc"/>: its own copy of System.</summary>
    private static LarvaObject? AntrilLibrary(byte[] coreBytes, string name, GcConfig gc, out string detail)
    {
        detail = "";
        var reference = global::Larva.Hir.Encoding.LvaFormat.Read(coreBytes);
        var compiled = AntrilCompiler.Compile([new SourceText(LibSource, "lib.ant")], [reference], name);
        if (!compiled.Success)
        {
            detail = string.Join("; ", compiled.Diagnostics.Take(4));
            return null;
        }
        var linked = global::Larva.Hir.Linking.HirLinker.Link(name, [coreBytes, global::Larva.Hir.Encoding.LvaFormat.Write(compiled.Assembly!)]);
        if (!linked.Success)
        {
            detail = string.Join("; ", linked.Errors.Take(4));
            return null;
        }
        var lowered = global::Larva.Hir.Lowering.HirLowerer.Lower(linked.Assembly!, new global::Larva.Hir.Lowering.LoweringOptions { Gc = gc });
        if (!lowered.Success)
        {
            detail = string.Join("; ", lowered.Errors.Take(4));
            return null;
        }
        var obj = Compiler.CompileObject(lowered.Module!, new CompilationOptions { Gc = gc });
        if (!obj.Success)
        {
            detail = string.Join("; ", obj.TypeErrors.Concat<object>(obj.Diagnostics).Take(4));
            return null;
        }
        return obj.Object;
    }

    /// <summary>The exported function of a library object whose name ends with <paramref name="suffix"/>.</summary>
    private static string? Exported(LarvaObject library, string suffix) =>
        library.Module.Functions.FirstOrDefault(f => f.Linkage == Linkage.Export && !f.IsGeneric && f.Name.EndsWith(suffix))?.Name;

    /// <summary>
    /// System.Core once per GC domain: a RefCount library and a Manual library, each with its own System (str, List) that
    /// allocates in its collector, called from a tracing app: Twice(4) × 100 + Shout(3) × 10 + Twice(4) → 1272.
    /// </summary>
    [Fact]
    public void System_core_is_compiled_once_per_gc_domain()
    {
        var sources = Repositories.LibrarySources("System.Core");
        Assert.SkipWhen(sources.Count == 0, "no System.Core sources");
        var core = AntrilCompiler.Compile(sources, "System.Core");
        Check("System.Core compiles", core.Success, string.Join("; ", core.Diagnostics.Take(4)));
        if (!core.Success)
            return;
        byte[] coreBytes = global::Larva.Hir.Encoding.LvaFormat.Write(core.Assembly!);
        var counted = AntrilLibrary(coreBytes, "librc", GcConfig.RefCount, out string rcDetail);
        Check("an Antril library with its System compiles for RefCount", counted is not null, rcDetail);
        var manual = AntrilLibrary(coreBytes, "libm", GcConfig.Manual, out string manualDetail);
        Check("…and for Manual", manual is not null, manualDetail);
        if (counted is null || manual is null)
            return;
        string? twice = Exported(counted, "Twice"), shout = Exported(counted, "Shout"), twiceManual = Exported(manual, "Twice");
        Check("the libraries export Lib's functions", twice is not null && shout is not null && twiceManual is not null,
            string.Join(", ", counted.Module.Functions.Where(f => f.Linkage == Linkage.Export).Select(f => f.Name).Take(12)));
        if (twice is null || shout is null || twiceManual is null)
            return;

        var t = new FunctionBuilder("twice", IrType.I32).ImportFrom($"librc::{twice}");
        t.Argument(IrType.I32, "n");
        var sh = new FunctionBuilder("shout", IrType.I32).ImportFrom($"librc::{shout}");
        sh.Argument(IrType.I32, "n");
        var tm = new FunctionBuilder("twice_manual", IrType.I32).ImportFrom($"libm::{twiceManual}");
        tm.Argument(IrType.I32, "n");
        var main = new FunctionBuilder("main", IrType.I32);
        int a = main.Local(IrType.I32, "a", mutable: true), b = main.Local(IrType.I32, "b", mutable: true), c = main.Local(IrType.I32, "c", mutable: true);
        int b1 = main.NewBlock(), b2 = main.NewBlock(), b3 = main.NewBlock();
        main.Call(0, [Int(4)], a, b1);
        main.At(b1).Call(1, [Int(3)], b, b2);
        main.At(b2).Call(2, [Int(4)], c, b3);
        main.At(b3).Assign(a, Rvalue.Binary(BinaryOp.Mul, Copy(a), Int(100))).Assign(b, Rvalue.Binary(BinaryOp.Mul, Copy(b), Int(10)))
            .Assign(a, Rvalue.Binary(BinaryOp.Add, Copy(a), Copy(b)))
            .Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(a), Copy(c))).Return();
        var app = Object(new IrModule([], [t.Build(), sh.Build(), tm.Build(), main.Build()]) { Name = "app" });

        var program = Linker.Link([app, counted, manual], new LinkOptions { MixedDomains = true, Entry = "app::main" });
        Check("a tracing app links with a RefCount library and a Manual library, each carrying its System", program.Success, string.Join(" | ", program.Errors.Take(6)));
        if (!program.Success)
            return;
        var merged = program.Merged!;
        Check("two copies of System, one per domain (librc's in 1, libm's in 2)",
            merged.Structs.Any(s => s.Name.StartsWith("librc::System.String") && s.Domain == 1) && merged.Structs.Any(s => s.Name.StartsWith("libm::System.String") && s.Domain == 2),
            string.Join(", ", merged.Structs.Where(s => s.Name.Contains("System.String")).Select(s => $"{s.Name}@{s.Domain}")));
        Check("link-once runtime structs: the Manual domain has its own (`*@2::Larva.Object`), the RefCount one shares the program's (`*::Larva.Object`), the handle-free ones are one for all (`*::Larva.Interface`, `*::Larva.Nullable`)",
            merged.Structs.Any(s => s.Name == "*::Larva.Object") && !merged.Structs.Any(s => s.Name == "*@1::Larva.Object") && merged.Structs.Any(s => s.Name == "*@2::Larva.Object")
            && merged.Structs.Count(s => s.Name.EndsWith("::Larva.Interface")) == 1 && merged.Structs.Count(s => s.Name.EndsWith("::Larva.Nullable")) == 1,
            string.Join(", ", merged.Structs.Where(s => s.Name.StartsWith("*")).Select(s => s.Name)));
        var (vm, llvm, x64, detail) = RunEverywhere(program, "app::main");
        Check("the libraries' str and List work in their own domains (RefCount counts, Manual frees nothing) → 1272",
            vm == 1272 && llvm == 1272 && x64 == 1272, $"vm {vm}, llvm {llvm}, x64 {x64} {detail}");
    }

    /// <summary>
    /// `larva build` with two projects: a library whose BUILD says `gc: refcount` and a tracing program that depends on it
    /// (`proj:`). The library becomes an object of its own domain (its own System); the program declares its members.
    /// </summary>
    [Fact]
    public void Larva_build_links_a_dependency_of_another_gc_domain()
    {
        string root = TestEnvironment.TempFolder("mixed-build");
        Directory.CreateDirectory(Path.Combine(root, "lib"));
        Directory.CreateDirectory(Path.Combine(root, "app"));
        File.WriteAllText(Path.Combine(root, "lib", "Lib.ant"), LibSource);
        File.WriteAllText(Path.Combine(root, "lib", "Lib.queen"), """
            queen 1.0;

            chunk PROJ:
               name: Lib,
               type: library

            chunk BUILD:
               gc: refcount
            """);
        File.WriteAllText(Path.Combine(root, "app", "App.ant"), """
            import System;

            Main() : i32
            {
                Console.WriteLine(Lib.Twice(4) * 10 + Lib.Shout(3));
                u8* reply = Lib.Greet(Exchange.Pack("ana"));
                Console.WriteLine(Exchange.Text(reply));
                return 0;
            }
            """);
        Directory.CreateDirectory(Path.Combine(root, "bad"));
        File.WriteAllText(Path.Combine(root, "bad", "Bad.ant"), """
            import System;

            Main() : i32
            {
                Console.WriteLine(Lib.Name());
                return 0;
            }
            """);
        File.WriteAllText(Path.Combine(root, "bad", "Bad.queen"), """
            queen 1.0;

            chunk PROJ:
               name: Bad,
               type: exe

            chunk DEPS:
               proj: ../lib/Lib.queen
            """);
        var (badCode, badText) = Larva("build", "-o", Path.Combine(root, "bad", "bad.exe"), "--backend", "x64", Path.Combine(root, "bad", "Bad.queen"));
        Check("a string cannot cross from the library's domain: the frontend says where (AN1079) and the build explains it", badCode != 0 && badText.Contains("AN1079") && badText.Contains("cannot cross") && badText.Contains("System.Exchange"), badText.Trim());
        File.WriteAllText(Path.Combine(root, "app", "App.queen"), """
            queen 1.0;

            chunk PROJ:
               name: App,
               type: exe

            chunk DEPS:
               proj: ../lib/Lib.queen
            """);
        foreach (string backend in new[] { "x64", "llvm" })
        {
            string exe = Path.Combine(root, "app", $"app-{backend}.exe");
            var (code, text) = Larva("build", "-o", exe, "--backend", backend, Path.Combine(root, "app", "App.queen"));
            Check($"larva build App.queen ({backend}) links the RefCount library as its own domain", code == 0 && File.Exists(exe), text.Trim());
            if (code != 0)
                continue;
            using var process = Process.Start(new ProcessStartInfo(exe) { RedirectStandardOutput = true })!;
            string[] lines = process.StandardOutput.ReadToEnd().Split((char)10).Select(l => l.Trim()).ToArray();
            process.WaitForExit(30_000);
            Check($"the program ({backend}) calls the library's List and str code in its domain: Twice(4) × 10 + Shout(3) = 126", lines.Length > 0 && lines[0] == "126", string.Join(" | ", lines.Take(2)));
            Check($"a string crosses by packet ({backend}): Exchange.Pack → Lib.Greet → Exchange.Text = \"hi ana\"", lines.Length > 1 && lines[1] == "hi ana", string.Join(" | ", lines.Take(2)));
        }
    }

    /// <summary>
    /// `larva build` of a program whose BUILD says `gc: refcount` (nothing traces): objects die when their count reaches 0
    /// and release what they hold (a tree's nodes, a list's strings), so at the end only the library's statics are live.
    /// </summary>
    [Fact]
    public void Larva_build_of_a_refcount_program_frees_as_objects_die()
    {
        string root = TestEnvironment.TempFolder("refcount-build");
        File.WriteAllText(Path.Combine(root, "Trees.ant"), """
            import System;

            public class Node
            {
                public:
                    Node? left;
                    Node? right;
                    new(Node? left, Node? right) { this.left = left; this.right = right; }
            }

            Make(i32 depth) : Node => depth == 0 ? new Node(null, null) : new Node(Make(depth - 1), Make(depth - 1));

            Count(Node node) : i32
            {
                if (node.left is Node l && node.right is Node r)
                    return 1 + Count(l) + Count(r);
                return 1;
            }

            Main() : i32
            {
                Node keep = Make(10);
                i64 total = 0;
                loop (i32 i = 0..300)
                    total += Count(Make(8));
                List<str> words = new List<str>();
                loop (i32 i = 0..1000)
                    words.Add("w" + i);
                Console.WriteLine(total + " " + Count(keep) + " " + words.Count);
                return 0;
            }
            """);
        File.WriteAllText(Path.Combine(root, "Trees.queen"), """
            queen 1.0;

            chunk PROJ:
               name: Trees,
               type: exe

            chunk BUILD:
               gc: refcount
            """);
        string? diagnostics = Environment.GetEnvironmentVariable("LARVA_DIAGNOSTICS");
        Environment.SetEnvironmentVariable("LARVA_DIAGNOSTICS", "1"); // the runtime prints LARVA_STATS
        try
        {
            foreach (string backend in new[] { "llvm", "x64" })
            {
                string exe = Path.Combine(root, $"trees-{backend}.exe");
                var (code, text) = Larva("build", "-o", exe, "--backend", backend, Path.Combine(root, "Trees.queen"));
                Check($"larva build Trees.queen ({backend}, gc: refcount)", code == 0 && File.Exists(exe), text.Trim());
                if (code != 0)
                    continue;
                var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
                start.Environment["LARVA_STATS"] = "1";
                using var process = Process.Start(start)!;
                var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
                string output = process.StandardOutput.ReadToEnd().Split((char)10)[0].Trim(); // (then the entry's result, 0)
                process.WaitForExit(60_000);
                string stats = stderr.Result;
                var live = System.Text.RegularExpressions.Regex.Match(stats, @"live gc (\d+)");
                Check($"RefCount ({backend}): the trees and the strings are freed as they die, children too (153300 2047 1000, few objects live at the end)",
                    output == "153300 2047 1000" && live.Success && int.Parse(live.Groups[1].Value) < 200 && stats.Contains("errors 0") && stats.Contains("collections 0"),
                    $"{output} | {stats.Split((char)10).FirstOrDefault()?.Trim()}");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("LARVA_DIAGNOSTICS", diagnostics);
        }
    }

    /// <summary>
    /// `larva build` of a tracing program whose minor collections copy a lot (a large tree kept, trees built and dropped,
    /// strings in a list and a map): with four copying threads and the heap verified after every collection.
    /// </summary>
    [Fact]
    public void Minor_collections_copy_with_several_threads()
    {
        string root = TestEnvironment.TempFolder("parallel-copy");
        File.WriteAllText(Path.Combine(root, "Copy.ant"), """
            import System;

            public class Node
            {
                public:
                    Node? left;
                    Node? right;
                    i32 value;
                    new(Node? left, Node? right, i32 value) { this.left = left; this.right = right; this.value = value; }
            }

            Make(i32 depth, i32 value) : Node => depth == 0 ? new Node(null, null, value) : new Node(Make(depth - 1, value * 2), Make(depth - 1, value * 2 + 1), value);

            Sum(Node node) : i64
            {
                i64 total = node.value;
                if (node.left is Node l)
                    total += Sum(l);
                if (node.right is Node r)
                    total += Sum(r);
                return total;
            }

            Main() : i32
            {
                Node keep = Make(16, 1);
                i64 dropped = 0;
                loop (i32 i = 0..40)
                    dropped += Sum(Make(12, i));
                List<str> words = new List<str>();
                Dictionary<str, i32> map = new Dictionary<str, i32>();
                loop (i32 i = 0..60000)
                {
                    words.Add("w" + i);
                    map["k" + i] = i;
                }
                i64 found = 0;
                loop (i32 i = 0..60000)
                    found += map["k" + i] + words[i].Length;
                Console.WriteLine(Sum(keep) + " " + dropped + " " + found);
                return 0;
            }
            """);
        File.WriteAllText(Path.Combine(root, "Copy.queen"), """
            queen 1.0;

            chunk PROJ:
               name: Copy,
               type: exe
            """);
        string? diagnostics = Environment.GetEnvironmentVariable("LARVA_DIAGNOSTICS");
        Environment.SetEnvironmentVariable("LARVA_DIAGNOSTICS", "1");
        try
        {
            string exe = Path.Combine(root, "copy.exe");
            var (code, text) = Larva("build", "-o", exe, "--backend", "llvm", Path.Combine(root, "Copy.queen"));
            Check("larva build Copy.queen (tracing, diagnostics)", code == 0 && File.Exists(exe), text.Trim());
            if (code != 0)
                return;
            string? expected = null;
            foreach (string threads in new[] { "1", "4" })
            {
                var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
                start.Environment["LARVA_GC_THREADS"] = threads;
                start.Environment["LARVA_GC_VERIFY"] = "1";
                start.Environment["LARVA_STATS"] = "1";
                using var process = Process.Start(start)!;
                var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
                string output = process.StandardOutput.ReadToEnd().Split((char)10)[0].Trim();
                process.WaitForExit(120_000);
                string stats = stderr.Result;
                expected ??= output;
                var promoted = System.Text.RegularExpressions.Regex.Match(stats, @"promoted (\d+)");
                Check($"{threads} copying thread(s): the same results, every collection verified, objects promoted", process.ExitCode == 0 && output == expected
                    && output.Split(' ').Length == 3 && stats.Contains("errors 0") && promoted.Success && long.Parse(promoted.Groups[1].Value) > 100000,
                    $"exit {process.ExitCode}: {output} | {stats.Split((char)10).Skip(1).FirstOrDefault()?.Trim()}");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("LARVA_DIAGNOSTICS", diagnostics);
        }
    }

    /// <summary>
    /// `larva build` of a program that throws through several frames: each exception goes to the nearest catch of its type,
    /// past catches of other types and frames that own objects; one left uncaught ends the program with its report.
    /// </summary>
    [Fact]
    public void Exceptions_in_an_executable_reach_the_nearest_catch()
    {
        string root = TestEnvironment.TempFolder("exceptions-build");
        File.WriteAllText(Path.Combine(root, "Throws.ant"), """
            import System;

            public exception Small { public: new(str message) : base(message) { } }
            public exception Large { public: new(str message) : base(message) { } }

            public class Box { public: i32 n; new(i32 n) { this.n = n; } }

            Deep(i32 i, i32 depth) : i32
            {
                Box kept = new Box(i);
                if (depth == 0)
                {
                    if (i % 3 == 0)
                        throw new SmallException("small " + i);
                    if (i % 3 == 1)
                        throw new LargeException("large " + i);
                    return kept.n;
                }
                return Deep(i, depth - 1) + kept.n - i;
            }

            Middle(i32 i) : i32
            {
                try
                {
                    return Deep(i, 5);
                }
                catch (SmallException e)
                {
                    return -1;
                }
            }

            Main() : i32
            {
                i32 small = 0;
                i32 large = 0;
                i64 sum = 0;
                loop (i32 i = 0..3000)
                {
                    try
                    {
                        i32 r = Middle(i);
                        if (r == -1)
                            small++;
                        else
                            sum += r;
                    }
                    catch (LargeException e)
                    {
                        large++;
                    }
                }
                Console.WriteLine(small + " " + large + " " + sum);
                if (small == 1000)
                    throw new LargeException("left uncaught");
                return 0;
            }
            """);
        File.WriteAllText(Path.Combine(root, "Throws.queen"), """
            queen 1.0;

            chunk PROJ:
               name: Throws,
               type: exe
            """);
        foreach (string backend in new[] { "llvm", "x64" })
        {
            string exe = Path.Combine(root, $"throws-{backend}.exe");
            var (code, text) = Larva("build", "-o", exe, "--backend", backend, Path.Combine(root, "Throws.queen"));
            Check($"larva build Throws.queen ({backend})", code == 0 && File.Exists(exe), text.Trim());
            if (code != 0)
                continue;
            var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            string output = process.StandardOutput.ReadToEnd().Split((char)10)[0].Trim();
            process.WaitForExit(60_000);
            string errors = stderr.Result;
            Check($"{backend}: each exception reaches the nearest catch of its type (1000 small, 1000 large, the rest returned), the last one is reported uncaught",
                output == "1000 1000 1500500" && process.ExitCode == 1 && errors.Contains("unhandled exception"), $"exit {process.ExitCode}: {output} | {errors.Trim()}");
        }
    }

    // ── Helpers ────────────────────────────────────────────────────

    private static (int Code, string Output) Larva(params string[] arguments)
    {
        Assert.SkipUnless(File.Exists(Repositories.LarvaCli), $"larva is not built ({Repositories.LarvaCli})");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Repositories.LarvaCli);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken); // both pipes at once: a full one would stop the child
        string output = process.StandardOutput.ReadToEnd() + stderr.Result;
        process.WaitForExit(60_000);
        return (process.ExitCode, output);
    }

    private static LarvaObject Object(IrModule module, GcConfig? gc = null)
    {
        var result = Compiler.CompileObject(module, new CompilationOptions { Gc = gc ?? GcConfig.Default });
        if (!result.Success)
            throw new InvalidOperationException(string.Join("\n", result.TypeErrors.Select(e => e.ToString()).Concat(result.Diagnostics.Select(d => d.ToString()))));
        return result.Object!;
    }

    /// <summary>Runs a linked program's entry on the VM, the LLVM JIT and the x64 JIT; the three results.</summary>
    private static (long Vm, int Llvm, int X64, string Detail) RunEverywhere(LinkResult linked, string entry, GcConfig? programGc = null)
    {
        var compilation = new CompilationOptions { Gc = programGc ?? GcConfig.Default };
        long vm = -1; int llvm = -1, x64 = -1; string detail = "";
        try { vm = Convert.ToInt64(new Interpreter(linked.Il!).Run(entry)); } catch (Exception e) { detail += $"VM: {e.Message}; "; }
        global::Larva.Native.NativeRuntime.Reset();
        var compiled = global::Larva.Llvm.LlvmBackend.Compile(linked, new global::Larva.Llvm.LlvmOptions { Compilation = compilation });
        if (compiled.Success)
        {
            using var program = compiled.Program!;
            using var jit = program.Jit();
            if (Environment.GetEnvironmentVariable("LARVA_LINK_IR") == entry)
                TestContext.Current.TestOutputHelper?.WriteLine(program.IrText); // the whole program's LLVM IR, to look at a case
            llvm = ((delegate* unmanaged<int>)jit.Address(entry))();
            int retains = global::Larva.Native.NativeRuntime.Retains, releases = global::Larva.Native.NativeRuntime.Releases, errors = global::Larva.Native.NativeRuntime.Errors;
            detail += $"[llvm retains {retains} releases {releases} errors {errors}] ";
        }
        else
            detail += $"LLVM: {compiled}; ";
        global::Larva.Native.NativeRuntime.Reset();
        var native = global::Larva.X64.X64Backend.Compile(linked, new global::Larva.X64.X64Options { Compilation = compilation });
        if (native.Success)
        {
            using var image = native.Program!.Load();
            x64 = ((delegate* unmanaged<int>)image.Address(entry))();
        }
        else
            detail += $"x64: {native}; ";
        global::Larva.Native.NativeRuntime.Reset();
        return (vm, llvm, x64, detail);
    }
}
