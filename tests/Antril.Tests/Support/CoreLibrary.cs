using System.Diagnostics;
using Antril.Binding;
using Antril.Syntax;
using Larva.Hir;

namespace Antril.Tests;

/// <summary>
/// System.Core (Larva's standard library, written in Antril), read and compiled once for the whole run: its sources, the
/// library compiled on its own (and how long that took), and its <c>.lva</c> as an editor loads it.
/// </summary>
internal static class CoreLibrary
{
    private static readonly Lazy<List<SourceText>> sources = new(() => Repositories.LibrarySources());
    private static readonly Lazy<(AntrilResult Result, long Ms)> compiled = new(() =>
    {
        var watch = Stopwatch.StartNew();
        var result = AntrilCompiler.Compile(Sources, "System.Core");
        return (result, watch.ElapsedMilliseconds);
    });
    private static readonly Lazy<byte[]> lva = new(() => Larva.Hir.Encoding.LvaFormat.Write(Compiled.Assembly!));

    /// <summary>System.Core's source files.</summary>
    public static List<SourceText> Sources => sources.Value;

    /// <summary>System.Core compiled on its own.</summary>
    public static AntrilResult Compiled => compiled.Value.Result;

    /// <summary>How long compiling System.Core on its own took (the first time: with the JIT).</summary>
    public static long CompileMs => compiled.Value.Ms;

    /// <summary>System.Core's <c>.lva</c> (written once).</summary>
    public static byte[] Lva => lva.Value;

    /// <summary>System.Core as an editor loads it: its <c>.lva</c> read back (a new assembly each time).</summary>
    public static HirAssembly Read() => Larva.Hir.Encoding.LvaFormat.Read(Lva);
}
