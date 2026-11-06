using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Antril.Tests;

/// <summary>
/// A project of the <see cref="LspTests"/>: a library compiled to Geo.dll with <c>larva lib</c>, and an app whose
/// <c>.queen</c> names it as <c>assembly:</c>, made once for the class.
/// </summary>
public sealed class QueenProject
{
    public string Root { get; } = TestEnvironment.TempFolder("queen-lsp");
    public string QueenPath => Path.Combine(Root, "app", "App.queen");
    public string MainPath => Path.Combine(Root, "app", "main.ant");
    public const string Queen = "queen 1.0;\nchunk PROJ:\n   name: App\nchunk BUILD:\n   backend: x64,\n   optimization: 1,\n   output: out/App.exe\nchunk DEPS:\n   assembly: ../Geo.dll\n";
    public const string Main = "import System;\nimport Geo;\n\nMain()\n{\n    Console.WriteLine(Shapes.Area(6, 7));\n}\n";

    /// <summary>What <c>larva lib</c> did.</summary>
    public (int Code, string Output) Library { get; }

    public QueenProject()
    {
        Directory.CreateDirectory(Path.Combine(Root, "app"));
        // A library compiled to a .dll, named by the project as `assembly:`.
        File.WriteAllText(Path.Combine(Root, "geo.ant"), """
            import System;

            into namespace Geo
            {
                public static class Shapes
                {
                    public:
                        static Area(i32 w, i32 h) : i32 => w * h;
                        static Total(List<i32> areas) : i32 { i32 sum = 0; loop (i32 a in areas) sum += a; return sum; }
                }
            }
            """);
        Library = LarvaCommand.Run("lib", "-o", Path.Combine(Root, "Geo.dll"), Path.Combine(Root, "geo.ant"));
        File.WriteAllText(QueenPath, Queen);
        File.WriteAllText(MainPath, Main);
    }
}

/// <summary>
/// `.queen` project files end to end: `larva lsp` (the real server, over stdio) on a project file and on a source of
/// that project, and `larva build` taking the project's BUILD settings.
/// </summary>
public sealed class LspTests(QueenProject project) : CheckedTest, IClassFixture<QueenProject>
{
    private string root => project.Root;

    /// <summary>`larva lib` and `larva build App.queen`: back end, optimization and output from BUILD.</summary>
    [Fact]
    public void Larva_build_takes_the_project_s_BUILD_settings()
    {
        var (libCode, libText) = project.Library;
        Check("larva lib writes Geo.dll, compiled against System.Core (it takes a List<i32>)", libCode == 0, libText.Trim());
        string queenPath = project.QueenPath;
        var (buildCode, buildText) = Larva("build", queenPath);
        string exe = Path.Combine(root, "app", "out", "App.exe");
        Check("larva build App.queen takes backend, optimization and output from BUILD", buildCode == 0 && File.Exists(exe) && buildText.Contains("x64"), buildText.Trim());
        if (File.Exists(exe))
            Check("…and the program runs with the assembly: library → 42", Output(exe) == "42", Output(exe));
    }

    /// <summary>The language server on the project: the .queen checked, completed and hovered; its sources analyzed with what it allows; definitions into decompiled sources.</summary>
    [Fact]
    public void Language_server_on_a_project_and_its_sources()
    {
        Assert.SkipWhen(project.Library.Code != 0, "larva lib did not write Geo.dll: " + project.Library.Output);
        string queenPath = project.QueenPath, mainPath = project.MainPath;
        const string queen = QueenProject.Queen, main = QueenProject.Main;
        using var server = new Server();
        server.Request("initialize", new JsonObject { ["processId"] = null, ["rootUri"] = Uri(root), ["capabilities"] = new JsonObject() });
        server.Notify("initialized", new JsonObject());
        string bad = queen.Replace("backend: x64", "backend: x64,\n   gc: magic");
        server.Notify("textDocument/didOpen", Open(queenPath, "queen", bad));
        var queenDiagnostics = server.Diagnostics(Uri(queenPath));
        Check("a .queen is checked as a project file: QN124 on `gc: magic`, no Antril errors",
            queenDiagnostics is { Count: 1 } && queenDiagnostics[0]!["code"]!.GetValue<string>() == "QN124" && queenDiagnostics[0]!["range"]!["start"]!["line"]!.GetValue<int>() == 5,
            queenDiagnostics?.ToJsonString());
        server.Notify("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri(queenPath), ["version"] = 2 },
            ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = queen }),
        });
        Check("…and the error goes away when it is fixed", server.Diagnostics(Uri(queenPath)) is { Count: 0 });
        var values = server.Request("textDocument/completion", At(queenPath, 4, 12));
        var labels = (values?["items"] as JsonArray)?.Select(i => i!["label"]!.GetValue<string>()).ToList() ?? [];
        Check("completion after `backend: ` offers vm, llvm, x64", labels.Contains("vm") && labels.Contains("llvm") && labels.Contains("x64"), string.Join(", ", labels));
        var keys = server.Request("textDocument/completion", At(queenPath, 8, 3));
        labels = (keys?["items"] as JsonArray)?.Select(i => i!["label"]!.GetValue<string>()).ToList() ?? [];
        Check("completion at a DEPS entry offers its keys, `assembly` among them", labels.Contains("assembly") && labels.Contains("package") && labels.Contains("lib"), string.Join(", ", labels));
        var hover = server.Request("textDocument/hover", At(queenPath, 4, 5));
        string hoverText = hover?["contents"]?["value"]?.GetValue<string>() ?? "";
        Check("hover on a key explains it with its values", hoverText.Contains("BUILD backend") && hoverText.Contains("`x64`"), hoverText);

        server.Notify("textDocument/didOpen", Open(mainPath, "antril", main));
        var sourceDiagnostics = server.Diagnostics(Uri(mainPath));
        Check("a source of the project sees the assembly: library (no errors)", sourceDiagnostics is { Count: 0 }, sourceDiagnostics?.ToJsonString());
        var members = server.Request("textDocument/completion", At(mainPath, 5, 29));
        labels = (members?["items"] as JsonArray)?.Select(i => i!["label"]!.GetValue<string>()).ToList() ?? [];
        Check("completion after `Shapes.` lists the library's Area", labels.Contains("Area"), string.Join(", ", labels.Take(10)));

        // What the .queen allows applies when it is saved: borrows are errors with the checker off (the default), not once
        // `borrowcheck: enforce` is saved.
        string borrowPath = Path.Combine(root, "app", "borrow.ant");
        string borrow = "value Counter { public: i32 n; }\nBump(&mut Counter c) { c.n = c.n + 1; }\n";
        File.WriteAllText(borrowPath, borrow);
        try
        {
            server.Notify("textDocument/didOpen", Open(borrowPath, "antril", borrow));
            var off = server.Diagnostics(Uri(borrowPath));
            Check("with the borrow checker off, `&mut` is an error (AN1092)", off?.Any(d => d!["code"]!.GetValue<string>() == "AN1092") == true, off?.ToJsonString());
            File.WriteAllText(queenPath, queen.Replace("optimization: 1,", "optimization: 1,\n   borrowcheck: enforce,"));
            server.Notify("textDocument/didSave", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = Uri(queenPath) } });
            JsonArray? on = null;
            for (int tries = 0; tries < 4 && (on = server.Diagnostics(Uri(borrowPath))) is { Count: > 0 }; tries++)
            {
            }
            Check("…and saving the .queen with `borrowcheck: enforce` analyzes the open documents again: no error", on is { Count: 0 }, on?.ToJsonString());
        }
        finally
        {
            File.WriteAllText(queenPath, queen);
        }
        server.Notify("textDocument/didSave", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = Uri(queenPath) } });

        // An open .queen applies as it is edited, before it is saved: `unsafe: deny` makes the unsafe block an error.
        string unsafePath = Path.Combine(root, "app", "pointers.ant");
        string pointers = "Peek() : i32\n{\n    i32 x = 4;\n    unsafe\n    {\n        i32* p = &x;\n        return *p;\n    }\n}\n";
        File.WriteAllText(unsafePath, pointers);
        server.Notify("textDocument/didOpen", Open(unsafePath, "antril", pointers));
        var allowed = server.Diagnostics(Uri(unsafePath));
        Check("unsafe code is fine where the project allows it (the default)", allowed is { Count: 0 }, allowed?.ToJsonString());
        server.Notify("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri(queenPath), ["version"] = 5 },
            ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = queen.Replace("optimization: 1,", "optimization: 1,\n   unsafe: deny,") }),
        });
        var denied = server.Diagnostics(Uri(unsafePath));
        Check("…editing the open .queen to `unsafe: deny` (not saved) analyzes it again: AN1104 on the block and the pointer",
            denied?.Count(d => d!["code"]!.GetValue<string>() == "AN1104") == 2, denied?.ToJsonString());
        server.Notify("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri(queenPath), ["version"] = 6 },
            ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = queen }),
        });
        Check("…and back to allowing it, no error", server.Diagnostics(Uri(unsafePath)) is { Count: 0 });
        server.Notify("textDocument/didClose", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = Uri(unsafePath) } });
        File.Delete(unsafePath);

        // From the program to a library's decompiled source and on: the editor sends `larva:` URIs escaped as it likes.
        // (larva writes decompiled sources under %LOCALAPPDATA%\Larva\decompiled, as it did for the old suite.)
        var console = server.Request("textDocument/definition", At(mainPath, 5, 6));
        string consoleUri = console?["uri"]?.GetValue<string>() ?? "";
        Check("definition of `Console` opens System.Console's decompiled source", consoleUri.StartsWith("larva:", StringComparison.Ordinal) && consoleUri.EndsWith("System.Console.ant", StringComparison.Ordinal), console?.ToJsonString());
        var writeLine = server.Request("textDocument/definition", At(mainPath, 5, 14));
        string consoleFile = consoleUri.Length > 0 ? new Uri("file:" + consoleUri["larva:".Length..]).LocalPath : "";
        string consoleText = File.Exists(consoleFile) ? File.ReadAllText(consoleFile) : "";
        string[] consoleLines = consoleText.Split('\n');
        int writeLineAt = writeLine?["range"]?["start"]?["line"]?.GetValue<int>() ?? -1;
        Check("definition of `WriteLine(i32)` lands on that overload's declaration",
            writeLineAt >= 0 && writeLineAt < consoleLines.Length && consoleLines[writeLineAt].Contains("WriteLine(i32 value)"), writeLineAt >= 0 && writeLineAt < consoleLines.Length ? consoleLines[writeLineAt] : writeLine?.ToJsonString());
        string decompiledUri = "larva:" + Editor("file:" + (consoleUri.Length > 6 ? consoleUri["larva:".Length..] : ""))["file:".Length..];
        server.Notify("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = decompiledUri, ["languageId"] = "antril", ["version"] = 1, ["text"] = consoleText },
        });
        int strLine = Array.FindIndex(consoleLines, l => l.Contains("WriteLine(str "));
        var tokens = server.Request("textDocument/semanticTokens/full", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = decompiledUri } });
        Check("the decompiled source (its URI escaped by the editor) is colored", (tokens?["data"] as JsonArray)?.Count > 100, tokens?.ToJsonString()[..Math.Min(200, tokens?.ToJsonString().Length ?? 0)]);
        var toString = strLine < 0 ? null : server.Request("textDocument/definition", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = decompiledUri },
            ["position"] = new JsonObject { ["line"] = strLine, ["character"] = consoleLines[strLine].IndexOf("str ", StringComparison.Ordinal) + 1 },
        });
        Check("…and `str` in it goes to System.String", toString?["uri"]?.GetValue<string>()?.EndsWith("System.Core/System.String.ant", StringComparison.Ordinal) == true, toString?.ToJsonString());
        try
        {
            File.WriteAllText(mainPath, main.Replace("Main()", "Main() : i32").Replace("Area(6, 7));", "Area(6, 7));\n    return 0;"));
            string typed = File.ReadAllText(mainPath);
            server.Notify("textDocument/didChange", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = Uri(mainPath), ["version"] = 2 },
                ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = typed }),
            });
            var watch = Stopwatch.StartNew();
            var int32 = server.Request("textDocument/definition", At(mainPath, 3, 10));
            var int32Hover = server.Request("textDocument/hover", At(mainPath, 3, 10));
            Check("definition of the keyword `i32` goes to System.Int32", int32?["uri"]?.GetValue<string>()?.EndsWith("System.Int32.ant", StringComparison.Ordinal) == true,
                $"{int32?.ToJsonString()} / hover {int32Hover?.ToJsonString()} ({watch.ElapsedMilliseconds} ms)");
        }
        finally
        {
            File.WriteAllText(mainPath, main);
        }

        server.Request("shutdown", null);
        server.Notify("exit", null);
    }

    /// <summary>
    /// The larva the installer ships is compiled ahead of time (no reflection for JSON): what the editor does most,
    /// checked on it when it has been published (tools/publish-larva.ps1) with its languages.
    /// </summary>
    [Fact]
    public void The_shipped_native_larva_serves_the_editor()
    {
        string native = Path.Combine(Repositories.Larva, "artifacts", "larva", "larva.exe");
        Assert.SkipUnless(File.Exists(native) && Directory.Exists(Path.Combine(Path.GetDirectoryName(native)!, "Languages", "Antril")),
            "no published larva (artifacts/larva/larva.exe with Languages/Antril): the shipped build is not checked");
        Assert.SkipWhen(project.Library.Code != 0, "larva lib did not write Geo.dll: " + project.Library.Output);
        string mainPath = project.MainPath;
        const string main = QueenProject.Main;
        // The shipped larva uses the languages installed with it, not the pack of this repository's build.
        using var shipped = new Server(native, withoutDevelopmentPack: true);
        shipped.Request("initialize", new JsonObject { ["processId"] = null, ["rootUri"] = Uri(root), ["capabilities"] = new JsonObject() });
        shipped.Notify("initialized", new JsonObject());
        shipped.Notify("textDocument/didOpen", Open(mainPath, "antril", main));
        var nativeConsole = shipped.Request("textDocument/definition", At(mainPath, 5, 6));
        string nativeUri = nativeConsole?["uri"]?.GetValue<string>() ?? "";
        string nativeFile = nativeUri.StartsWith("larva:", StringComparison.Ordinal) ? new Uri("file:" + nativeUri["larva:".Length..]).LocalPath : "";
        string nativeText = File.Exists(nativeFile) ? File.ReadAllText(nativeFile) : "";
        string nativeDoc = "larva:" + Editor("file:" + (nativeUri.Length > 6 ? nativeUri["larva:".Length..] : ""))["file:".Length..];
        shipped.Notify("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = nativeDoc, ["languageId"] = "antril", ["version"] = 1, ["text"] = nativeText },
        });
        var nativeTokens = shipped.Request("textDocument/semanticTokens/full", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = nativeDoc } });
        var nativeCompletion = shipped.Request("textDocument/completion", At(mainPath, 5, 29));
        Check("the shipped (native) larva: definition opens the decompiled source, which is colored, and completion works",
            nativeText.Length > 0 && (nativeTokens?["data"] as JsonArray)?.Count > 100
            && (nativeCompletion?["items"] as JsonArray)?.Any(i => i!["label"]!.GetValue<string>() == "Area") == true,
            $"{nativeConsole?.ToJsonString()} / {(nativeTokens?["data"] as JsonArray)?.Count} tokens");
        shipped.Request("shutdown", null);
        shipped.Notify("exit", null);
    }

    /// <summary>A file's URI as VS Code writes it: the drive's letter in lower case and its colon escaped (<c>file:///c%3A/…</c>).</summary>
    private static string Uri(string path) => Editor(new Uri(Path.GetFullPath(path)).AbsoluteUri);

    private static string Editor(string uri)
    {
        int at = uri.IndexOf(":///", StringComparison.Ordinal) + 4;
        return at > 3 && uri.Length > at + 1 && uri[at + 1] == ':' ? uri[..at] + char.ToLowerInvariant(uri[at]) + "%3A" + uri[(at + 2)..] : uri;
    }

    private static JsonObject Open(string path, string language, string text) => new()
    {
        ["textDocument"] = new JsonObject { ["uri"] = Uri(path), ["languageId"] = language, ["version"] = 1, ["text"] = text },
    };

    private static JsonObject At(string path, int line, int character) => new()
    {
        ["textDocument"] = new JsonObject { ["uri"] = Uri(path) },
        ["position"] = new JsonObject { ["line"] = line, ["character"] = character },
    };

    /// <summary>`larva lsp` over stdio: framed requests and notifications, and the diagnostics it publishes.</summary>
    private sealed class Server : IDisposable
    {
        private readonly Process process;
        private readonly Stream input, output;
        private int nextId = 1;
        private readonly List<JsonObject> pending = [];

        public Server(string? executable = null, bool withoutDevelopmentPack = false)
        {
            var start = new ProcessStartInfo(executable ?? "dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            if (executable is null)
                start.ArgumentList.Add(Repositories.LarvaCli);
            start.ArgumentList.Add("lsp");
            if (withoutDevelopmentPack)
                start.Environment.Remove("LARVA_LANGUAGES");
            process = Process.Start(start)!;
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            input = process.StandardInput.BaseStream;
            output = process.StandardOutput.BaseStream;
            // Messages are read on their own thread: one that never comes fails its check after a while, not the suite.
            new Thread(() =>
            {
                try
                {
                    while (Read() is { } message)
                        inbox.Add(message);
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
                {
                }
                inbox.CompleteAdding();
            }) { IsBackground = true }.Start();
        }

        private readonly System.Collections.Concurrent.BlockingCollection<JsonObject> inbox = [];

        private JsonObject? Receive()
        {
            try
            {
                return inbox.TryTake(out var message, TimeSpan.FromSeconds(30)) ? message : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private void Send(JsonObject message)
        {
            message["jsonrpc"] = "2.0";
            byte[] body = Encoding.UTF8.GetBytes(message.ToJsonString());
            byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            try
            {
                input.Write(header);
                input.Write(body);
                input.Flush();
            }
            catch (IOException)
            {
                // The server has ended: what was asked gets no answer, and its check fails.
            }
        }

        private JsonObject? Read()
        {
            var header = new StringBuilder();
            int length = -1;
            while (true)
            {
                int b = output.ReadByte();
                if (b < 0)
                    return null;
                header.Append((char)b);
                if (header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    foreach (string line in header.ToString().Split("\r\n"))
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            length = int.Parse(line["Content-Length:".Length..].Trim());
                    }
                    break;
                }
            }
            var body = new byte[length];
            for (int read = 0; read < length;)
            {
                int n = output.Read(body, read, length - read);
                if (n <= 0)
                    return null;
                read += n;
            }
            return JsonNode.Parse(body) as JsonObject;
        }

        public JsonNode? Request(string method, JsonNode? parameters)
        {
            int id = nextId++;
            Send(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters?.DeepClone() });
            while (Receive() is { } message)
            {
                if (message["id"] is { } got && got.GetValue<int>() == id && message["method"] is null)
                    return message["result"];
                pending.Add(message);
            }
            return null;
        }

        public void Notify(string method, JsonNode? parameters) =>
            Send(new JsonObject { ["method"] = method, ["params"] = parameters?.DeepClone() });

        /// <summary>The next diagnostics published for <paramref name="uri"/> (waiting for them).</summary>
        public JsonArray? Diagnostics(string uri)
        {
            for (int i = 0; i < pending.Count; i++)
            {
                if (pending[i]["method"]?.GetValue<string>() == "textDocument/publishDiagnostics" && pending[i]["params"]!["uri"]!.GetValue<string>() == uri)
                {
                    var found = pending[i]["params"]!["diagnostics"] as JsonArray;
                    pending.RemoveAt(i);
                    return found;
                }
            }
            while (Receive() is { } message)
            {
                if (message["method"]?.GetValue<string>() == "textDocument/publishDiagnostics" && message["params"]!["uri"]!.GetValue<string>() == uri)
                    return message["params"]!["diagnostics"] as JsonArray;
                pending.Add(message);
            }
            return null;
        }

        public void Dispose()
        {
            // A server that did not exit after `exit` (or never got it: a failed test) is stopped with its children.
            try
            {
                input.Close();
            }
            catch (IOException)
            {
            }
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
            process.Dispose();
        }
    }

    private static (int Code, string Output) Larva(params string[] arguments) => LarvaCommand.Run(arguments);

    private static string Output(string exe) => LarvaCommand.Execute(exe).Split((char)10)[0].Trim();
}
