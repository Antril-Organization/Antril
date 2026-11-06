// `larva build` of a .ant project with RESOURCES (from Larva's Hive tests: the rest of them stays in Larva.Build.Tests).
namespace Antril.Tests;

/// <summary>Hive's project chunks in a build: RESOURCES built into an Antril program and read with System.Resources.</summary>
public sealed class HiveTests : CheckedTest
{
    /// <summary><c>larva build P.queen</c>: the project's RESOURCES are built into the program and read with <c>System.Resources</c>.</summary>
    [Fact]
    public void Larva_build_puts_RESOURCES_in_the_program_and_it_reads_them()
    {
        string folder = TestEnvironment.TempFolder("hive-built");
        Directory.CreateDirectory(Path.Combine(folder, "data"));
        File.WriteAllText(Path.Combine(folder, "data", "hello.txt"), "hi");
        File.WriteAllBytes(Path.Combine(folder, "data", "raw.bin"), [.. Enumerable.Range(0, 20).Select(i => (byte)i), 255]);
        File.WriteAllText(Path.Combine(folder, "Main.ant"), """
            import System;

            Main() : i32
            {
                i32 r = 0;
                r += Resources.Has("data/hello.txt") && Resources.Has("raw") && Resources.Names().Length == 2 ? 1 : 0;
                r += Resources.ReadText("data/hello.txt") == "hi" && Resources.Read("nope") == null ? 10 : 0;
                if (Resources.Read("raw") is u8[] raw)
                    r += raw.Length == 21 && raw[20] == 255 && raw[7] == 7 ? 100 : 0;
                u8[] direct = $resource("data/hello.txt");
                r += direct.Length == 2 && direct[0] == 104 ? 1000 : 0;
                if (Resources.View("raw") is ResourceData view)
                    r += view.Length == 21 && view[20] == 255 && view[7] == 7 && view.ToArray().Length == 21 && Resources.View("nope") == null ? 10000 : 0;
                Resources.Install("extra", [1, 2, 3]);
                if (Resources.View("extra") is ResourceData extra)
                    r += extra.Length == 3 && extra[2] == 3 && Resources.Names().Length == 3 && Resources.ReadText("data/hello.txt") == "hi" ? 100000 : 0;
                if (Resources.View("raw") is ResourceData whole)
                {
                    i32 sum = 0;
                    loop (u8 b in whole)
                        sum += b;
                    ResourceData tail = whole.Slice(18);
                    u8[] expected = [18, 19, 255];
                    u8[] other = [0, 1, 3];
                    r += sum == 445 && tail.Length == 3 && tail[2] == 255 && tail == expected && whole.Slice(0, 3) != other && expected == tail ? 1000000 : 0;
                    r += tail == [18, 19, 255] && [0, 1, 3] != whole.Slice(0, 3) && [0, 1, 2] == whole.Slice(0, 3) && whole.Slice(21).ToArray().Length == 0 ? 10000000 : 0;
                }
                Console.WriteLine(r);
                return 0;
            }
            """);
        File.WriteAllText(Path.Combine(folder, "P.queen"), """
            queen 1.0;

            chunk PROJ:
               name: P,
               type: exe

            chunk RESOURCES:
               embed: data/*.txt,
               raw: data/raw.bin
            """);
        string exe = Path.Combine(folder, "p.exe");
        var (code, text) = Larva("build", "-o", exe, "--backend", "x64", Path.Combine(folder, "P.queen"));
        Check("larva build P.queen (x64) succeeds", code == 0 && File.Exists(exe), text.Trim());
        if (code != 0)
            return;
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { RedirectStandardOutput = true })!;
        string output = process.StandardOutput.ReadToEnd().Split((char)10)[0].Trim(); // the first line (the runtime prints the exit code after)
        process.WaitForExit(30_000);
        Check("the program reads its resources from the assembly (Has, Names, ReadText, Read, `$resource`, View in place, Install at run time, `loop … in`, Slice, `==` (array literals too), mem.copy; 11111111)", output == "11111111", output);
    }

    private static (int Code, string Output) Larva(params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Repositories.LarvaCli);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync(); // both pipes at once: a full one would stop the child
        string output = process.StandardOutput.ReadToEnd() + stderr.Result;
        process.WaitForExit(120_000);
        return (process.ExitCode, output);
    }
}
