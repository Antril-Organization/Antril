using System.Diagnostics;

namespace Antril.Tests;

/// <summary>
/// Benchmark executables: how one is run (which benchmark, its size; what it prints and how long it took), and the same
/// benchmarks written for .NET, the reference for results and speed.
/// </summary>
internal static class Benchmarks
{
    /// <summary>Runs <paramref name="exe"/> with the benchmark number and its size; its output, trimmed, and the time it took.</summary>
    public static (string Output, double Ms) Execute(string exe, int which, int n)
    {
        var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(which.ToString());
        start.ArgumentList.Add(n.ToString());
        var watch = Stopwatch.StartNew();
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
        return (output.Result.Trim(), watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>The same programs written for .NET, as the reference for results and speed.</summary>
    public static class Reference
    {
        private abstract class Shape { public abstract int Area(); }
        private sealed class Square(int s) : Shape { public int S = s; public override int Area() => S * S; }
        private sealed class Triangle(int b) : Shape { public int B = b; public override int Area() => B * 3; }
        private sealed class Cell(int v) { public int V = v; }
        private struct Point(int x, int y)
        {
            public int X = x, Y = y;
            public readonly int Dot() => X * X + Y * Y;
        }
        private sealed class Failure : Exception;

        public static int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);

        public static int Dispatch(int n)
        {
            Shape a = new Square(3), b = new Triangle(5);
            int s = 0;
            for (int i = 0; i < n; i++)
                s += ((i & 1) == 0 ? a : b).Area();
            return s;
        }

        public static int Mono(int n)
        {
            Shape a = new Square(3);
            int s = 0;
            for (int i = 0; i < n; i++)
                s += a.Area();
            return s;
        }

        public static int Alloc(int n)
        {
            int s = 0;
            for (int i = 0; i < n; i++)
                s += new Cell(i).V;
            return s;
        }

        public static int Structs(int n)
        {
            var p = new Point(1, 2);
            int s = 0;
            for (int i = 0; i < n; i++)
            {
                p.X = i & 1023;
                s += p.Dot();
            }
            return s;
        }

        public static int Throw(int n)
        {
            int s = 0;
            for (int i = 0; i < n; i++)
            {
                try { throw new Failure(); }
                catch (Failure) { s++; }
            }
            return s;
        }
    }
}
