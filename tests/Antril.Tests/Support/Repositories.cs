using System.Reflection;
using System.Runtime.CompilerServices;
using Antril.Syntax;

namespace Antril.Tests;

/// <summary>
/// Where the tests find what they work with: Larva's working copy (its standard library, written in Antril, and its
/// larva command line) and this repository (Antril's language pack as .NET code, which larva loads through
/// LARVA_LANGUAGES, set for the whole run).
/// </summary>
internal static class Repositories
{
    private static string Metadata(string key) =>
        typeof(Repositories).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == key).Value!;

    /// <summary>Larva's working copy (LarvaRoot in the build).</summary>
    public static string Larva => Metadata("LarvaRoot");

    /// <summary>This repository.</summary>
    public static string Antril => Metadata("AntrilRoot");

    /// <summary>Antril's language pack as .NET code (what src/Antril's build writes), for LARVA_LANGUAGES.</summary>
    public static string PackFolder => Path.Combine(Antril, "artifacts", "pack");

    /// <summary>The larva command line of Larva's working copy (larva.dll, run with dotnet).</summary>
    public static string LarvaCli => Path.Combine(Larva, "Larva.Cli", "bin", Metadata("Configuration"), "net10.0", "larva.dll");

    /// <summary>A standard library's sources (System.Core, System.Bindings), in file name order.</summary>
    public static List<SourceText> LibrarySources(string library = "System.Core") =>
        [.. Directory.GetFiles(Path.Combine(Larva, "Library", library), "*.ant").Order(StringComparer.Ordinal).Select(SourceText.FromFile)];

    [ModuleInitializer]
    internal static void Initialize()
    {
        // larva (in process or run as a command) finds Antril's pack here, and only here.
        Environment.SetEnvironmentVariable("LARVA_LANGUAGES", PackFolder);
        // Packages installed by the tests go to a cache of their own, never the user's.
        Environment.SetEnvironmentVariable("HIVE_CACHE", TestEnvironment.TempFolder("hive-cache"));
    }
}
