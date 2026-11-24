using System.Diagnostics;

// A program's Main as executables start it: the command line as text (Main(str[] args)), its integer as the exit code,
// and the project's own collector (gc: custom) installed before it.
namespace Antril.Tests;

public sealed class EntryTests : CheckedTest
{
    private static readonly IReadOnlyDictionary<string, string> NoEnvironment = new Dictionary<string, string>();

    private const string Words = """
        import System;

        into namespace T
        {
            Main(str[] args) : i32
            {
                loop (str a in args)
                    Console.WriteLine("[" + a + "]");
                return args.Length;
            }
        }
        """;

    [Fact]
    public void Main_takes_the_command_line_and_returns_the_exit_code()
    {
        string root = TestEnvironment.TempFolder("entry");
        File.WriteAllText(Path.Combine(root, "Words.ant"), Words);
        File.WriteAllText(Path.Combine(root, "Code.ant"), """
            import System;

            into namespace T
            {
                Main() : i32
                {
                    Console.WriteLine("hola");
                    return 7;
                }
            }
            """);
        File.WriteAllText(Path.Combine(root, "Nothing.ant"), """
            import System;

            into namespace T
            {
                Main(str[] args) { Console.WriteLine("args " + args.Length); }
            }
            """);
        File.WriteAllText(Path.Combine(root, "Numbers.ant"), """
            into namespace T
            {
                Main(i64 n) : i64 => n * 2;
            }
            """);
        foreach (string backend in new[] { "llvm", "x64" })
        {
            string Build(string name)
            {
                string exe = Path.Combine(root, $"{name}-{backend}.exe");
                var (code, log) = LarvaCommand.Run(NoEnvironment, root, "build", "--backend", backend, name + ".ant", "-o", exe);
                Check($"{name}.ant builds ({backend})", code == 0, log.Trim());
                return exe;
            }
            var (exit, output) = Start(Build("Words"), "uno", "dos tres", "ñandú");
            Check($"Main(str[] args) gets each word, spaces and accents too ({backend})", exit == 3 && output.Replace("\r", "") == "[uno]\n[dos tres]\n[ñandú]\n", $"exit {exit}: {output}");
            (exit, output) = Start(Build("Code"), "a", "b");
            Check($"Main() : i32 is the exit code, not printed, and takes any words ({backend})", exit == 7 && output.Trim() == "hola", $"exit {exit}: {output}");
            (exit, output) = Start(Build("Nothing"), "1", "2");
            Check($"Main(str[] args) without a result exits with 0 ({backend})", exit == 0 && output.Trim() == "args 2", $"exit {exit}: {output}");
            (exit, output) = Start(Build("Numbers"), "21");
            Check($"Main(i64 n) still reads a number and prints its result ({backend})", exit == 0 && output.Trim() == "42", $"exit {exit}: {output}");
        }
    }

    [Fact]
    public void A_program_built_for_Linux_runs_there()
    {
        var (toolchain, problem) = Larva.Native.PosixToolchain.Find(Larva.Native.PosixSystem.Linux);
        Assert.SkipWhen(toolchain is null, $"no Linux toolchain: {problem}");
        string root = TestEnvironment.TempFolder("entry-linux");
        File.WriteAllText(Path.Combine(root, "Words.ant"), Words);
        string program = Path.Combine(root, "words");
        var (code, log) = LarvaCommand.Run(NoEnvironment, root, "build", "--target", "linux-x64", "Words.ant", "-o", program);
        Check("larva build --target linux-x64 builds", code == 0 && log.Contains("linux-x64"), log.Trim());
        if (code != 0)
            return;
        var (exit, output) = toolchain!.Execute(program, ["uno", "dos tres", "ñandú"]);
        Check("the Linux program gets its words and exits with its result", exit == 3 && output.Replace("\r", "") == "[uno]\n[dos tres]\n[ñandú]\n", $"exit {exit}: {output}");
        (code, log) = LarvaCommand.Run(NoEnvironment, root, "build", "--target", "linux-x64", "--backend", "x64", "Words.ant", "-o", program);
        Check("the x64 back end says it makes Windows code", code != 0 && log.Contains("built with LLVM"), log.Trim());
    }

    [Fact]
    public void A_custom_collector_is_installed_before_Main()
    {
        string root = TestEnvironment.TempFolder("entry-gc");
        File.WriteAllText(Path.Combine(root, "Main.ant"), """
            import System;
            import System.Runtime.InteropServices;

            into namespace T
            {
                public class Counter : IGarbageCollector
                {
                    public:
                        i64 taken;
                        i64 given;
                        i64 asked;
                        unsafe Allocate(u64 size) : u8* { taken++; return (u8*)Marshal.AllocHGlobal((i64)size); }
                        unsafe Release(u8* block) { given++; Marshal.FreeHGlobal(block); }
                        ShouldCollect(i64 allocated, i64 live) : bool { asked++; return allocated > 2000; }
                }

                public class Node
                {
                    public:
                        i64 value;
                        Node? next;
                        u8[] load;
                        new(i64 value) { this.value = value; load = new u8[4096]; }
                }

                Main() : i32
                {
                    if (GC.Installed is not Counter counter)
                        return 2;
                    Node? kept = null;
                    i64 sum = 0;
                    loop (i64 i = 0..20000)
                    {
                        Node n = new Node(i);
                        if (i % 100 == 0) { n.next = kept; kept = n; }
                        sum += n.value;
                    }
                    i64 alive = 0;
                    loop (kept is Node k) { alive++; kept = k.next; }
                    GC.Install(null);
                    Console.WriteLine(sum + " " + alive);
                    return counter.taken > 0 && counter.given > 0 && counter.asked > 0 && GC.CollectionCount() > 0 ? 0 : 3;
                }
            }
            """);
        string Project(string collector) => $"""
            queen 1.0;

            chunk PROJ:
               name: Collected,
               type: exe

            chunk BUILD:
               gc: custom,
               gc.collector: {collector},
               unsafe: allow
            """;
        File.WriteAllText(Path.Combine(root, "Collected.queen"), Project("T.Counter"));
        var (code, log) = LarvaCommand.Run(NoEnvironment, root, "build");
        Check("a gc: custom project builds", code == 0, log.Trim());
        var (exit, output) = Start(Path.Combine(root, "bin", "Collected.exe"));
        Check("its collector is installed before Main, gives the memory, is asked and lets objects live",
            exit == 0 && output.Trim() == "199990000 200", $"exit {exit}: {output}");

        foreach (var (collector, message) in new[]
        {
            ("T.Node", "is not a class that implements System.IGarbageCollector"),
            ("T.Missing", "no class `T.Missing`"),
        })
        {
            File.WriteAllText(Path.Combine(root, "Collected.queen"), Project(collector));
            (code, log) = LarvaCommand.Run(NoEnvironment, root, "build");
            Check($"gc.collector: {collector} is an error that says why", code != 0 && log.Contains(message), log.Trim());
        }
    }

    /// <summary>Runs <paramref name="exe"/> with <paramref name="arguments"/>: its exit code and what it printed.</summary>
    private static (int Exit, string Output) Start(string exe, params string[] arguments)
    {
        if (!File.Exists(exe))
            return (-1, "(not built)");
        var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            return (-1, "(did not end)");
        }
        process.WaitForExit();
        return (process.ExitCode, output.Result + error.Result);
    }
}
