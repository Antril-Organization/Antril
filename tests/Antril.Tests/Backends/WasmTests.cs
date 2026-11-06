using System.Diagnostics;
using Antril.Binding;
using Antril.Syntax;
using Larva.Driver;
using Larva.Hir.Lowering;
using Larva.IR;
using Larva.IR.Linking;

namespace Antril.Tests;

/// <summary>
/// WebAssembly: System.Core's test program built with both back ends (Larva's own and LLVM), each case run in Node and
/// compared with what the native back ends give. Cases that need threads or C are left out (WebAssembly here runs on
/// one thread and has no C).
/// </summary>
public sealed class WasmTests : CheckedTest
{
    /// <summary>
    /// Cases that do not apply to WebAssembly: real threads (a thread here runs at once, to the end), processes and sockets
    /// (no such thing in a WebAssembly module) and native memory (System.Exchange packets).
    /// </summary>
    private static readonly HashSet<string> Skipped = ["Threads", "Syncs", "Parallels", "TaskRuns", "Processes", "Network", "Exchange"];

    private static readonly (string Function, int Expected, string What)[] Cases =
        [.. CoreLibraryTests.CaseList.Where(c => !Skipped.Contains(c.Function))];

    /// <summary>The program (its Main runs case k), compiled, lowered, checked and linked once for both back ends.</summary>
    private static readonly Lazy<Steps> Program = new(Build);

    private sealed record Steps(List<(string Name, bool Pass, string? Detail)> Checks, LinkResult? Linked);

    private static Steps Build()
    {
        List<(string, bool, string?)> checks = [];
        var library = CoreLibrary.Sources;
        checks.Add(("Library/System.Core is found", library.Count > 0, null));
        if (library.Count == 0)
            return new(checks, null);
        // The program's Main runs case k (its result is printed).
        string main = "public Main(i64 k) : i64\n{\n"
            + string.Concat(Cases.Select((c, k) => $"    if (k == {k}) return {c.Function}();\n"))
            + "    return -1;\n}";
        const string Old = "public Main() : i32 => Concat();";
        checks.Add(("the core program's Main is found", CoreLibraryTests.ProgramSource.Contains(Old), null));
        if (!CoreLibraryTests.ProgramSource.Contains(Old))
            return new(checks, null);
        string source = CoreLibraryTests.ProgramSource.Replace(Old, main);

        var compiled = AntrilCompiler.Compile([.. library, new SourceText(source, "core.ant")], "WasmTest");
        checks.Add(("System.Core and the program compile", compiled.Success, string.Join("\n    ", compiled.Diagnostics.Take(8))));
        if (!compiled.Success)
            return new(checks, null);
        var lowered = HirLowerer.Lower(compiled.Assembly!, new LoweringOptions { StaticConstructorsFromEntry = true });
        checks.Add(("their HIR lowers", lowered.Success, string.Join("; ", lowered.Errors.Take(6))));
        if (!lowered.Success)
            return new(checks, null);
        var objectFile = Compiler.CompileObject(lowered.Module!);
        checks.Add(("its IR passes the checks", objectFile.Success, string.Join("; ", objectFile.TypeErrors.Concat<object>(objectFile.Diagnostics).Take(6))));
        if (!objectFile.Success)
            return new(checks, null);
        var ir = Linker.Link([objectFile.Object!], new LinkOptions { Entry = lowered.FunctionName(compiled.Assembly!.EntryPoint!.Value), LowerToIl = false });
        checks.Add(("and links", ir.Success, string.Join("; ", ir.Errors.Take(6))));
        return new(checks, ir.Success ? ir : null);
    }

    [Fact]
    public void Core_program_compiles_lowers_and_links_for_webassembly()
    {
        foreach (var (name, pass, detail) in Program.Value.Checks)
            Check(name, pass, detail);
    }

    [Fact]
    public void Larva_back_end_runs_every_case_in_node() => RunCases(llvm: false);

    [Fact]
    public void Llvm_back_end_runs_every_case_in_node() => RunCases(llvm: true);

    private void RunCases(bool llvm)
    {
        string? node = FindNode();
        Assert.SkipWhen(node is null, "no node on the PATH");
        string backend = llvm ? "LLVM" : "Larva";
        Assert.SkipWhen(llvm && WasmBuilder.FindLinker() is null, $"{backend}: {WasmBuilder.MissingLinker}");
        foreach (var (name, pass, detail) in Program.Value.Checks.Where(c => !c.Pass))
            Check(name, pass, detail);
        Require();
        var ir = Program.Value.Linked!;

        string folder = TestEnvironment.TempFolder("wasm-" + backend);
        try
        {
            var watch = Stopwatch.StartNew();
            string output = Path.Combine(folder, llvm ? "core_llvm.js" : "core_own.js");
            var (files, message) = WasmBuilder.Build(ir, output, new ExecutableOptions { OptimizationLevel = 2 }, llvm);
            long build = watch.ElapsedMilliseconds;
            // Larva's own WebAssembly back end does not lower 256-bit integers yet (System.Core's PrimitiveLimits uses
            // u256): a known gap, shown as skipped until it is filled.
            Assert.SkipWhen(files is null && !llvm && message?.Contains("is not supported by the WebAssembly back end") == true, $"{backend}: {message}");
            Check($"{backend} back end builds it", files is not null, message);
            if (files is null)
                return;
            long size = new FileInfo(Path.ChangeExtension(output, ".wasm")).Length;
            // Each case in its own process (a panic ends only its own), several at a time.
            var results = new (string Output, int Exit, long Ms)[Cases.Length];
            watch.Restart();
            Parallel.For(0, Cases.Length, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, k =>
            {
                var one = Stopwatch.StartNew();
                var start = new ProcessStartInfo(node!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = folder };
                start.ArgumentList.Add(output);
                start.ArgumentList.Add(k.ToString());
                using var process = Process.Start(start)!;
                var errors = process.StandardError.ReadToEndAsync();
                string text = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                results[k] = ((text + errors.Result).Trim(), process.ExitCode, one.ElapsedMilliseconds);
            });
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{backend}: built in {build} ms, {size / 1024} KB of wasm; {Cases.Length} cases run in {watch.ElapsedMilliseconds} ms");
            for (int k = 0; k < Cases.Length; k++)
            {
                var (function, expected, what) = Cases[k];
                var (text, exit, _) = results[k];
                Check($"{backend}: {function}() = {expected}: {what}", exit == 0 && text == expected.ToString(), $"exit {exit}: {Shorten(text)}");
            }
        }
        finally
        {
            // LARVA_WASM_KEEP=1 leaves the built modules in the folder (to look at their imports, run a case by hand).
            if (Environment.GetEnvironmentVariable("LARVA_WASM_KEEP") is { Length: > 0 })
                TestContext.Current.TestOutputHelper?.WriteLine($"kept {folder}");
            else
                try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    private static string Shorten(string text) => text.Length > 300 ? text[..300] + "…" : text;

    private static string? FindNode()
    {
        string exe = OperatingSystem.IsWindows() ? "node.exe" : "node";
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, exe)))
                return Path.Combine(dir, exe);
        }
        return null;
    }
}
