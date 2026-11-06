using System.Diagnostics;

namespace Antril.Tests;

/// <summary>
/// The larva command line of Larva's working copy, run as a child process (with <c>dotnet</c>): its exit code and
/// everything it wrote. A run that does not end in time is killed with its children (a program <c>larva run</c> started).
/// </summary>
internal static class LarvaCommand
{
    /// <summary>The longest a command may take (a build with LLVM included).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    /// <summary>Runs larva in <paramref name="folder"/> (the current folder when null), with <paramref name="environment"/> on top of the process's.</summary>
    public static (int Code, string Output) Run(IReadOnlyDictionary<string, string>? environment, string? folder, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = folder ?? Environment.CurrentDirectory,
        };
        start.ArgumentList.Add(Repositories.LarvaCli);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
            start.Environment[name] = value;
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        // Both pipes at once: a full one would stop the child.
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(Timeout))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            return (-1, $"(larva {string.Join(' ', arguments)} did not end in {Timeout.TotalSeconds:0} s and was stopped)\n" + output.Result + error.Result);
        }
        process.WaitForExit();
        return (process.ExitCode, output.Result + error.Result);
    }

    /// <summary>Runs larva in the current folder with the process's environment.</summary>
    public static (int Code, string Output) Run(params string[] arguments) => Run(null, null, arguments);

    /// <summary>Runs a program built by a test: what it printed (waiting for it to end, killing it when it does not).</summary>
    public static string Execute(string exe)
    {
        using var process = Process.Start(new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
        return output.Result;
    }
}
