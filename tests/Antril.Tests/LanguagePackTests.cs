using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Antril.Tests;

/// <summary>
/// Languages as packs and templates for <c>larva new</c>: larva finds the packs (Languages/&lt;Name&gt;/ and
/// LARVA_LANGUAGES), checks each one (its files' SHA-256, its signature, what it needs) before loading it, compiles
/// through the one a source's extension names, and writes a template (Larva HIR, before any language) as a project in
/// the language chosen; the identifier of a template is its folder's name with `-` for spaces.
/// The pack used is Antril's own (this repository's build, which LARVA_LANGUAGES names for the run); the tests that change
/// a pack change a copy of it.
/// </summary>
public sealed class LanguagePackTests : CheckedTest
{
    private readonly string root = TestEnvironment.TempFolder("languages");

    private static (int Code, string Output) LarvaIn(IReadOnlyDictionary<string, string>? environment, string? folder, params string[] arguments) =>
        LarvaCommand.Run(environment, folder, arguments);

    private static (int Code, string Output) Larva(params string[] arguments) => LarvaIn(null, null, arguments);

    private static string RunExe(string exe) => LarvaCommand.Execute(exe);

    /// <summary>Antril's pack as this repository's build writes it.</summary>
    private static string InstalledPack => Path.Combine(Repositories.PackFolder, "Antril");

    private static string Sha256(string file) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));

    /// <summary>The working copy's pack (Antril, .NET code), checked and listed.</summary>
    [Fact]
    public void Installed_pack_is_listed_verified_and_used()
    {
        var (code, text) = Larva("languages");
        Check("larva languages lists the Antril pack, its files verified", code == 0 && text.Contains("Antril 0.8.0") && text.Contains("(antril: .ant)")
            && text.Contains("ok (files verified)") && text.Contains("author:"), text.Trim());
        // The pack as Antril's build writes it: its library, its file icon and its manifest, which lists both with their SHA-256.
        string pack = InstalledPack;
        string manifestPath = Path.Combine(pack, "language.json");
        var files = File.Exists(manifestPath) ? JsonNode.Parse(File.ReadAllText(manifestPath))?["files"] as JsonObject : null;
        Check("the pack is its own folder: its library and its manifest (with the library's SHA-256)",
            File.Exists(Path.Combine(pack, "Antril.dll")) && File.Exists(Path.Combine(pack, "ant-file.ico")) && files is { Count: 2 }
            && files["Antril.dll"]?.GetValue<string>() == Sha256(Path.Combine(pack, "Antril.dll"))
            && files["ant-file.ico"]?.GetValue<string>() == Sha256(Path.Combine(pack, "ant-file.ico")),
            files?.ToJsonString() ?? $"no manifest in {pack}");
        var (unknown, unknownText) = LarvaIn(null, root, "build", "-o", "x.exe", "x.foo");
        Check("a source no installed language reads is told, with the languages installed", unknown == 1 && unknownText.Contains("no installed language reads .foo files")
            && unknownText.Contains("Antril (.ant)"), unknownText.Trim());
    }

    /// <summary>A pack signed, then changed in each way that matters: never loaded, with why.</summary>
    [Fact]
    public void A_signed_pack_changed_in_any_way_is_never_loaded()
    {
        string languages = Path.Combine(root, "packs");
        string pack = Path.Combine(languages, "Antril");
        Directory.CreateDirectory(pack);
        // A copy of the pack, its manifest without the `files` it lists (as a pack's sources have it, before `larva languages pack`).
        foreach (string file in Directory.GetFiles(InstalledPack))
            File.Copy(file, Path.Combine(pack, Path.GetFileName(file)));
        var unpacked = JsonNode.Parse(File.ReadAllText(Path.Combine(pack, "language.json")))!.AsObject();
        unpacked.Remove("files");
        File.WriteAllText(Path.Combine(pack, "language.json"), unpacked.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        var environment = new Dictionary<string, string> { ["LARVA_LANGUAGES"] = languages };

        var (unlisted, unlistedText) = LarvaIn(environment, null, "languages");
        Check("a pack whose manifest lists no files is not used", unlisted == 1 && unlistedText.Contains("lists no `files`"), unlistedText.Trim());

        string key = Path.Combine(root, "key.pem");
        var (keyCode, keyText) = Larva("languages", "key", key);
        var (packCode, packText) = Larva("languages", "pack", pack, "--key", key);
        // The pack has two files: its library and its file icon.
        Check("larva languages pack lists the files (SHA-256) and signs the manifest", keyCode == 0 && packCode == 0 && packText.Contains("2 file(s), signed"), keyText + packText);
        string manifest = File.ReadAllText(Path.Combine(pack, "language.json"));
        Check("the signed manifest has the key, its fingerprint, the signature and the HIR format",
            manifest.Contains("\"publicKey\"") && manifest.Contains("\"fingerprint\"") && manifest.Contains("\"signature\"") && manifest.Contains("\"hir\""));
        var (signed, signedText) = LarvaIn(environment, null, "languages");
        Check("the signed pack is listed as signed and verified (it is the one LARVA_LANGUAGES names)",
            signed == 0 && signedText.Contains("signed, key") && signedText.Contains(pack) && signedText.Contains("ok (files verified)"), signedText.Trim());
        string program = Path.Combine(root, "p.ant");
        File.WriteAllText(program, "Main() : i32 => 7;\n");
        var (checkCode, checkText) = LarvaIn(environment, null, "build", "--check", program);
        Check("a program compiles through the signed pack", checkCode == 0 && checkText.Contains("no errors"), checkText.Trim());

        // The manifest changed after it was signed (its author): the signature no longer matches.
        File.WriteAllText(Path.Combine(pack, "language.json"), manifest.Replace("Antril Organization", "Someone Else"));
        var (forged, forgedText) = LarvaIn(environment, null, "languages");
        Check("a manifest changed after signing is not used", forged == 1 && forgedText.Contains("signature does not match"), forgedText.Trim());
        var (forgedBuild, forgedBuildText) = LarvaIn(environment, null, "build", "--check", program);
        Check("and nothing compiles through it", forgedBuild == 1 && forgedBuildText.Contains("cannot be used") && forgedBuildText.Contains("signature does not match"), forgedBuildText.Trim());
        File.WriteAllText(Path.Combine(pack, "language.json"), manifest);

        // The library changed: its SHA-256 is not the manifest's.
        byte[] library = File.ReadAllBytes(Path.Combine(pack, "Antril.dll"));
        File.WriteAllBytes(Path.Combine(pack, "Antril.dll"), [.. library, 0]);
        var (changed, changedText) = LarvaIn(environment, null, "languages");
        Check("a library changed since the pack was made is not loaded", changed == 1 && changedText.Contains("Antril.dll is not the file its manifest lists"), changedText.Trim());
        File.WriteAllBytes(Path.Combine(pack, "Antril.dll"), library);

        // Code the manifest does not list.
        File.WriteAllBytes(Path.Combine(pack, "extra.dll"), [0x4D, 0x5A]);
        var (extra, extraText) = LarvaIn(environment, null, "languages");
        Check("a library the manifest does not list makes the pack unusable", extra == 1 && extraText.Contains("extra.dll is not among its `files`"), extraText.Trim());
        File.Delete(Path.Combine(pack, "extra.dll"));

        // What it needs: a newer larva, another ABI.
        File.WriteAllText(Path.Combine(pack, "language.json"), manifest.Replace("\"larva\": \"0.8.0\"", "\"larva\": \"9.0\""));
        var (newer, newerText) = Larva("languages", "pack", pack);
        var (newerList, newerListText) = LarvaIn(environment, null, "languages");
        Check("a pack that needs a newer larva says so", newer == 1 && newerList == 1 && newerListText.Contains("needs larva 9.0 or later"), newerText + newerListText);
        File.WriteAllText(Path.Combine(pack, "language.json"), manifest);
        var (restored, restoredText) = LarvaIn(environment, null, "languages");
        Check("restored, the pack verifies again", restored == 0 && restoredText.Contains("ok (files verified)"), restoredText.Trim());
    }

    /// <summary>`larva new` and `larva run`: the templates listed, a console project in Antril written, built and run, run again and after an edit.</summary>
    [Fact]
    public void New_console_project_is_written_built_and_run()
    {
        var (listCode, list) = Larva("new");
        Check("larva new lists the templates by identifier (spaces as -) and the languages",
            listCode == 0 && list.Contains("Console-App") && list.Contains("Library") && list.Contains("antril"), list.Trim());

        string projects = Path.Combine(root, "projects");
        Directory.CreateDirectory(projects);
        var (newCode, newText) = LarvaIn(null, projects, "new", "console-app", "Demo", "--lang", "antril");
        string demo = Path.Combine(projects, "Demo");
        string program = Path.Combine(demo, "Program.ant");
        Check("larva new console-app Demo writes the project (its .queen and the program in Antril)",
            newCode == 0 && File.Exists(Path.Combine(demo, "Demo.queen")) && File.Exists(program), newText.Trim());
        string source = File.Exists(program) ? File.ReadAllText(program) : "";
        Check("the template's namespace is the project's, written by Antril's view (not marked as decompiled)",
            source.Contains("into namespace Demo") && source.Contains("Main()") && !source.Contains("decompiled"), source);
        string queen = File.Exists(Path.Combine(demo, "Demo.queen")) ? File.ReadAllText(Path.Combine(demo, "Demo.queen")) : "";
        Check("the .queen names the project and its type", queen.Contains("name: Demo") && queen.Contains("type: exe"), queen);
        var (again, againText) = LarvaIn(null, projects, "new", "console-app", "Demo");
        Check("larva new does not write over a project (without --force)", again == 1 && againText.Contains("exists"), againText.Trim());
        var (buildCode, buildText) = LarvaIn(null, demo, "build", "Demo.queen", "--backend", "x64");
        string exe = Path.Combine(demo, "bin", "Demo.exe");
        string run = File.Exists(exe) ? RunExe(exe) : "";
        Check("the new project builds and runs", buildCode == 0 && run.Contains("Hello from Larva!"), buildText.Trim() + run);

        // `larva run`: the project the current folder is in, built when something changed, run.
        string inside = Path.Combine(demo, "deep", "er");
        Directory.CreateDirectory(inside);
        var (runCode, runText) = LarvaIn(null, inside, "run", "--backend", "x64");
        Check("larva run in a folder of the project builds it and runs it", runCode == 0 && runText.Contains("Hello from Larva!"), runText.Trim());
        var watch = Stopwatch.StartNew();
        var (againRun, againRunText) = LarvaIn(null, inside, "run");
        Check("larva run again: up to date, run without building", againRun == 0 && againRunText.Contains("Hello from Larva!") && !againRunText.Contains("back end"),
            $"{watch.ElapsedMilliseconds} ms: {againRunText.Trim()}");
        File.WriteAllText(program, File.ReadAllText(program).Replace("Hello from Larva!", "Changed!"));
        var (changedRun, changedRunText) = LarvaIn(null, inside, "run");
        Check("larva run after an edit builds again", changedRun == 0 && changedRunText.Contains("Changed!") && changedRunText.Contains("back end"), changedRunText.Trim());
        var (noProject, noProjectText) = LarvaIn(null, Path.GetPathRoot(root), "run");
        Check("larva run outside a project says so", noProject == 1 && noProjectText.Contains("no project here"), noProjectText.Trim());
    }

    /// <summary>The Library template, a template found by its folder's name, an unknown one.</summary>
    [Fact]
    public void Library_template_and_template_names()
    {
        string projects = Path.Combine(root, "projects");
        Directory.CreateDirectory(projects);
        var (libraryCode, libraryText) = LarvaIn(null, projects, "new", "Library", "Greetings");
        string greeter = Path.Combine(projects, "Greetings", "Greeter.ant");
        Check("the Library template puts its class in the file its manifest names",
            libraryCode == 0 && File.Exists(greeter) && File.ReadAllText(greeter).Contains("into namespace Greetings") && File.ReadAllText(greeter).Contains("class Greeter"), libraryText.Trim());
        var (libraryBuild, libraryBuildText) = LarvaIn(null, Path.Combine(projects, "Greetings"), "build", "--check", "Greetings.queen");
        Check("and it compiles", libraryBuild == 0, libraryBuildText.Trim());
        var (runLibrary, runLibraryText) = LarvaIn(null, Path.Combine(projects, "Greetings"), "run");
        Check("larva run on a library says there is nothing to run", runLibrary == 1 && runLibraryText.Contains("is a library"), runLibraryText.Trim());
        var (spaces, spacesText) = LarvaIn(null, projects, "new", "Console App", "Spaced");
        Check("a template is also found by its folder's name, spaces and all", spaces == 0 && File.Exists(Path.Combine(projects, "Spaced", "Program.ant")), spacesText.Trim());
        var (missing, missingText) = LarvaIn(null, projects, "new", "web-app", "X");
        Check("an unknown template is told, with those installed", missing == 1 && missingText.Contains("no template `web-app`") && missingText.Contains("Console-App"), missingText.Trim());
    }

    /// <summary>A template as an installation ships it: its manifest and template.lva (HIR), no sources; it writes what the template's sources write.</summary>
    [Fact]
    public void A_packed_template_writes_the_same_program()
    {
        string projects = Path.Combine(root, "projects");
        Directory.CreateDirectory(projects);
        // The program the template's sources write (for a project named Demo).
        LarvaIn(null, projects, "new", "console-app", "Demo", "--lang", "antril");
        string program = Path.Combine(projects, "Demo", "Program.ant");
        string source = File.Exists(program) ? File.ReadAllText(program) : "";

        string templates = Path.Combine(root, "templates");
        var (packCode, packText) = Larva("new", "--pack", Path.Combine(Repositories.Larva, "Templates", "Console App"), "-o", templates);
        string packed = Path.Combine(templates, "Console App");
        Check("larva new --pack compiles a template to template.lva, without its sources",
            packCode == 0 && File.Exists(Path.Combine(packed, "template.lva")) && File.Exists(Path.Combine(packed, "template.json")) && !Directory.Exists(Path.Combine(packed, "src")), packText.Trim());
        var environment = new Dictionary<string, string> { ["LARVA_TEMPLATES"] = templates };
        var (fromHir, fromHirText) = LarvaIn(environment, projects, "new", "Console-App", "FromHir");
        string written = Path.Combine(projects, "FromHir", "Program.ant");
        Check("the packed template writes the same program", fromHir == 0 && File.Exists(written)
            && File.ReadAllText(written) == source.Replace("Demo", "FromHir"), fromHirText.Trim());
    }

    /// <summary>
    /// <c>larva config</c>: this machine's BUILD defaults (in the file <c>LARVA_CONFIG</c> names: here a temporary one),
    /// the built-in borrow checker off, what <c>larva new</c> writes from them; <c>larva hive</c>; <c>larva build</c> with no source.
    /// </summary>
    [Fact]
    public void Config_defaults_and_what_larva_new_writes_from_them()
    {
        var (environment, projects) = Defaults();
        string config = environment["LARVA_CONFIG"];
        var (listCode, list) = LarvaIn(environment, null, "config");
        Check("larva config lists the defaults: the borrow checker off, built in", listCode == 0 && list.Contains("borrowcheck: off   (built in)"), list.Trim());
        var (setCode, setText) = LarvaIn(environment, null, "config", "optimization", "1");
        var (badCode, badText) = LarvaIn(environment, null, "config", "gc", "nope");
        Check("larva config sets a default in its file, and refuses a value the key does not take",
            setCode == 0 && File.Exists(config) && File.ReadAllText(config).Contains("\"optimization\": \"1\"") && badCode == 1 && badText.Contains("`gc` is"), setText + badText);
        // hive.provider: the registries of projects that name none (the setup offers https://www.antril.org/hive-api).
        var (hiveSet, hiveSetText) = LarvaIn(environment, null, "config", "hive.provider", "https://www.antril.org/hive-api");
        var (hiveGet, hiveGetText) = LarvaIn(environment, null, "config", "hive.provider");
        var (hiveBad, hiveBadText) = LarvaIn(environment, null, "config", "hive.provider", "a b");
        Check("larva config hive.provider sets this machine's package registry, and refuses one with spaces",
            hiveSet == 0 && hiveGet == 0 && hiveGetText.Trim() == "https://www.antril.org/hive-api" && hiveBad == 1, hiveSetText + hiveGetText + hiveBadText);
        var (newCode, newText) = LarvaIn(environment, projects, "new", "console-app", "Configured");
        string queen = Path.Combine(projects, "Configured", "Configured.queen");
        string text = File.Exists(queen) ? File.ReadAllText(queen) : "";
        Check("larva new writes a BUILD chunk with the defaults in effect (the borrow checker off, the optimization set)",
            newCode == 0 && text.Contains("chunk BUILD:") && text.Contains("borrowcheck: off") && text.Contains("optimization: 1") && text.Contains("backend: llvm") && !text.Contains("hive.provider"), newText + text);
        string keyFile = Path.Combine(projects, "signing.key");
        var (keyCode, keyText) = LarvaIn(environment, projects, "hive", "key", "-o", keyFile);
        Check("larva hive is the packager: `larva hive key` writes a signing key", keyCode == 0 && File.Exists(keyFile) && keyText.Contains("fingerprint"), keyText);
        var (usageCode, usageText) = LarvaIn(environment, null, "hive");
        Check("…and `larva hive` alone lists its commands", usageCode == 2 && usageText.Contains("larva hive install"), usageText);
        LarvaIn(environment, null, "config", "--unset", "hive.provider");
        var (buildHere, buildHereText) = LarvaIn(environment, Path.Combine(projects, "Configured"), "build");
        Check("larva build with no source builds the project of the folder", buildHere == 0 && File.Exists(Path.Combine(projects, "Configured", "bin", "Configured.exe")), buildHereText.Trim());
    }

    /// <summary>What a build does with borrows when the checker is off (the default) or on, from the command line or from <c>larva config</c>.</summary>
    [Fact]
    public void Borrows_with_the_checker_off_and_on()
    {
        var (environment, projects) = Defaults();
        string borrows = Path.Combine(projects, "borrows.ant");
        File.WriteAllText(borrows, """
            import System;
            value Counter { public: i32 n; }
            Both(&mut Counter a, &mut Counter b) { a.n = 1; b.n = 2; }
            Main()
            {
                Counter x = new Counter();
                Both(&mut x, &mut x);
                Console.WriteLine(x.n);
            }
            """);
        var (offCode, offText) = LarvaIn(environment, projects, "build", "--check", "borrows.ant");
        Check("with the borrow checker off (the default), a borrow is an error (AN1092, AN2144)",
            offCode == 1 && offText.Contains("AN1092") && offText.Contains("AN2144"), offText.Trim());
        var (onCode, onText) = LarvaIn(environment, projects, "build", "--check", "--borrowcheck", "enforce", "borrows.ant");
        Check("with it on, two `&mut x` at once are found, on their line", onCode == 1 && onText.Contains("ConflictingBorrow") && onText.Contains("borrows.ant(7,"), onText.Trim());
        var (configOn, configOnText) = LarvaIn(environment, null, "config", "borrowcheck", "enforce");
        var (fromConfig, fromConfigText) = LarvaIn(environment, projects, "build", "--check", "borrows.ant");
        Check("larva config borrowcheck enforce turns it on for builds that do not say", configOn == 0 && fromConfig == 1 && fromConfigText.Contains("ConflictingBorrow"), fromConfigText.Trim());
        LarvaIn(environment, null, "config", "--unset", "borrowcheck");
        var (builtCode, builtText) = LarvaIn(environment, projects, "build", "--borrowcheck", "enforce", "-o", Path.Combine(projects, "borrows.exe"), "borrows.ant");
        Check("a build (not only --check) stops at the borrow checker's errors when it enforces them",
            builtCode == 1 && builtText.Contains("ConflictingBorrow") && !File.Exists(Path.Combine(projects, "borrows.exe")), builtText.Trim());
    }

    /// <summary>`unsafe: deny`: the project's own unsafe code is an error; a dependency that allows it keeps it.</summary>
    [Fact]
    public void Unsafe_deny_applies_to_the_project_s_own_code()
    {
        var (environment, projects) = Defaults();
        string safeRoot = Path.Combine(projects, "Safe");
        Directory.CreateDirectory(Path.Combine(safeRoot, "app"));
        Directory.CreateDirectory(Path.Combine(safeRoot, "raw"));
        File.WriteAllText(Path.Combine(safeRoot, "raw", "Raw.queen"), "queen 1.0;\nchunk PROJ:\n   name: Raw,\n   type: library\n");
        File.WriteAllText(Path.Combine(safeRoot, "raw", "raw.ant"), "into namespace Raw { public unsafe Peek(i32 v) : i32 { i32* p = &v; return *p; } }\n");
        string safeQueen = Path.Combine(safeRoot, "app", "App.queen");
        File.WriteAllText(safeQueen, "queen 1.0;\nchunk PROJ:\n   name: App\nchunk BUILD:\n   unsafe: deny\nchunk DEPS:\n   proj: ../raw\n");
        File.WriteAllText(Path.Combine(safeRoot, "app", "main.ant"), "import System;\nimport Raw;\nMain() { Console.WriteLine(Peek(5)); }\n");
        var (safeCode, safeText) = LarvaIn(environment, Path.Combine(safeRoot, "app"), "build", "--check");
        Check("unsafe: deny: the project's own code may use a dependency's unsafe functions", safeCode == 0, safeText.Trim());
        File.WriteAllText(Path.Combine(safeRoot, "app", "main.ant"), "import System;\nMain() { i32 v = 5; unsafe { i32* p = &v; Console.WriteLine(*p); } }\n");
        (safeCode, safeText) = LarvaIn(environment, Path.Combine(safeRoot, "app"), "build", "--check");
        Check("…but not be unsafe itself (AN1104 on the block and the pointer)", safeCode == 1 && safeText.Split("AN1104").Length == 3 && safeText.Contains("main.ant(2,"), safeText.Trim());
    }

    /// <summary><c>access.default: file</c> of a project.</summary>
    [Fact]
    public void Access_default_file_makes_members_file_private()
    {
        var (environment, projects) = Defaults();
        string fileProject = Path.Combine(projects, "FileAccess");
        Directory.CreateDirectory(fileProject);
        File.WriteAllText(Path.Combine(fileProject, "FileAccess.queen"), "queen 1.0;\n\nchunk PROJ:\n   name: FileAccess,\n   type: exe,\n   version: v1.0\n\nchunk BUILD:\n   access.default: file\n");
        File.WriteAllText(Path.Combine(fileProject, "One.ant"), "class Hidden { static F() : i32 => 1; }\n");
        File.WriteAllText(Path.Combine(fileProject, "Two.ant"), "import System;\nMain()\n{\n    Console.WriteLine(Hidden.F());\n}\n");
        var (fileCode, fileText) = LarvaIn(environment, fileProject, "build", "--check");
        Check("access.default: file makes what says no access file-private (AN1400 from another file)", fileCode == 1 && fileText.Contains("AN1400"), fileText.Trim());
    }

    /// <summary>The processor: BUILD `cpu` (native: this machine's, AVX2 and all), `--cpu`, and as a default of this machine.</summary>
    [Fact]
    public void Cpu_from_the_project_the_command_line_and_the_defaults()
    {
        var (environment, projects) = Defaults();
        string cpuProject = Path.Combine(projects, "Cpu");
        Directory.CreateDirectory(cpuProject);
        File.WriteAllText(Path.Combine(cpuProject, "Cpu.queen"), "queen 1.0;\n\nchunk PROJ:\n   name: Cpu,\n   type: exe,\n   version: v1.0\n\nchunk BUILD:\n   backend: llvm,\n   cpu: native\n");
        File.WriteAllText(Path.Combine(cpuProject, "Main.ant"),
            "import System;\nMain()\n{\n    u64 sum = 0;\n    loop (u32 i in 0..1000000)\n        if ((i & 1) == 0) sum += i;\n    Console.WriteLine(sum);\n}\n");
        var (nativeCode, nativeText) = LarvaIn(environment, cpuProject, "run");
        Check("BUILD cpu: native builds for this machine, and the program runs", nativeCode == 0 && nativeText.Contains("249999500000"), nativeText.Trim());
        var (flagCode, flagText) = LarvaIn(environment, cpuProject, "run", "--rebuild", "--cpu", "x86-64-v2");
        Check("larva run --cpu x86-64-v2 (an LLVM processor name) overrides it", flagCode == 0 && flagText.Contains("249999500000"), flagText.Trim());
        var (cpuSet, cpuSetText) = LarvaIn(environment, null, "config", "cpu", "native");
        var (cpuBad, cpuBadText) = LarvaIn(environment, null, "config", "cpu", "two words");
        Check("larva config cpu native is a default; a name with spaces is refused", cpuSet == 0 && cpuBad == 1 && cpuBadText.Contains("`cpu` is"), cpuSetText + cpuBadText);
        LarvaIn(environment, null, "config", "--unset", "cpu");
    }

    /// <summary>This test's own defaults file (LARVA_CONFIG, not yet written) and a folder for its projects.</summary>
    private (Dictionary<string, string> Environment, string Projects) Defaults()
    {
        string config = Path.Combine(root, "config", "defaults.json");
        string projects = Path.Combine(root, "defaults");
        Directory.CreateDirectory(projects);
        return (new Dictionary<string, string> { ["LARVA_CONFIG"] = config }, projects);
    }
}
