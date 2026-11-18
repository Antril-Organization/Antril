// `larva tool`: a tool written in Antril, published to a folder registry, installed for a project and for the user, run
// as `larva <command>`.
namespace Antril.Tests;

/// <summary>Tools from Hive: packed compiled, built once for this machine when installed, run with their arguments as text.</summary>
public sealed class ToolTests : CheckedTest
{
    [Fact]
    public void A_tool_is_published_installed_and_run_as_a_larva_command()
    {
        string root = TestEnvironment.TempFolder("tools");
        string tool = Path.Combine(root, "greet"), app = Path.Combine(root, "app"), registry = Path.Combine(root, "registry");
        foreach (string folder in new[] { tool, app, registry, Path.Combine(root, "elsewhere") })
            Directory.CreateDirectory(folder);
        // The user's settings, tools and packages of this test only.
        var environment = new Dictionary<string, string>
        {
            ["LARVA_CONFIG"] = Path.Combine(root, "defaults.json"),
            ["LARVA_TOOLS"] = Path.Combine(root, "store"),
            ["HIVE_CACHE"] = Path.Combine(root, "cache"),
        };
        (int Code, string Output) Larva(string folder, params string[] arguments) => LarvaCommand.Run(environment, folder, arguments);

        var (keyCode, keyText) = Larva(tool, "hive", "key", "-o", "hive.key");
        Check("a signing key", keyCode == 0, keyText.Trim());
        File.WriteAllText(Path.Combine(tool, "Greet.queen"), """
            queen 1.0;

            chunk PROJ:
               name: Greet,
               type: tool,
               command: greet,
               version: v1.2,
               author: Test

            chunk HIVE:
               provider: ../registry,
               sign: hive.key
            """);
        File.WriteAllText(Path.Combine(tool, "Main.ant"), """
            import System;

            Main() : i32
            {
                List<str> words = Environment.GetCommandLineArgs();
                str joined = "";
                loop (str w in words)
                    joined = joined + "[" + w + "]";
                Console.WriteLine("greet " + joined);
                Console.WriteLine("project " + (Environment.GetEnvironmentVariable("LARVA_PROJECT") ?? "none"));
                return words.Count;
            }
            """);
        var (publishCode, publishText) = Larva(tool, "hive", "publish");
        Check("larva hive publish packs a `type: tool` project", publishCode == 0 && publishText.Contains("published Test/Greet 1.2"), publishText.Trim());

        File.WriteAllText(Path.Combine(app, "App.queen"), "queen 1.0;\n\nchunk PROJ:\n   name: App\n\nchunk HIVE:\n   provider: ../registry\n");
        var (installCode, installText) = Larva(app, "tool", "install", "Test/Greet");
        string queen = File.ReadAllText(Path.Combine(app, "App.queen"));
        Check("larva tool install (in a project): built, and written to TOOLS with its version", installCode == 0 && queen.Contains("chunk TOOLS:") && queen.Contains("tool: Test/Greet@^1.2"),
            installText.Trim() + "\n" + queen);
        Check("it is pinned in hive.lock", File.ReadAllText(Path.Combine(app, "hive.lock")).Contains("Test/Greet"));

        var (runCode, runText) = Larva(app, "greet", "one", "two words");
        Check("larva greet: the tool runs with its arguments as text, told the project", runCode == 2 && runText.Contains("greet [one][two words]") && runText.Contains("App.queen"), $"exit {runCode}: {runText.Trim()}");
        var (listCode, listText) = Larva(app, "tool", "list");
        Check("larva tool list names it", listCode == 0 && listText.Contains("larva greet") && listText.Contains("Test/Greet 1.2"), listText.Trim());

        var (uninstallCode, uninstallText) = Larva(app, "tool", "uninstall", "greet");
        Check("larva tool uninstall takes it out of TOOLS", uninstallCode == 0 && !File.ReadAllText(Path.Combine(app, "App.queen")).Contains("TOOLS"), uninstallText.Trim());
        var (goneCode, goneText) = Larva(app, "greet");
        Check("then `larva greet` is not a command (larva's usage)", goneCode == 2 && goneText.Contains("usage: larva"), goneText.Trim());

        // For the user: from this machine's registry, run from any folder.
        Larva(root, "config", "hive.provider", registry);
        var (globalCode, globalText) = Larva(root, "tool", "install", "Test/Greet", "-g");
        Check("larva tool install -g", globalCode == 0, globalText.Trim());
        var (anywhereCode, anywhereText) = Larva(Path.Combine(root, "elsewhere"), "greet", "x");
        Check("a user's tool runs from any folder", anywhereCode == 1 && anywhereText.Contains("greet [x]"), $"exit {anywhereCode}: {anywhereText.Trim()}");
        var (builtinCode, _) = Larva(root, "tool", "run", "build");
        Check("larva's own commands are not tools", builtinCode != 0);
        var (removeCode, removeText) = Larva(root, "tool", "uninstall", "greet", "-g");
        Check("larva tool uninstall -g", removeCode == 0 && Larva(root, "tool", "list", "-g").Output.Contains("no tools"), removeText.Trim());
    }
}
