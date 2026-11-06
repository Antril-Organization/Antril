using System.Text;
using System.Text.Json.Nodes;
using Antril.Binding;
using Antril.Services;
using Antril.Syntax;
using Larva.Hir;
using Larva.Hir.Encoding;
using Larva.Hir.Services;

namespace Antril.Pack
{
    /// <summary>
    /// Antril as a Larva language: what <c>larva</c> asks of the language pack, one JSON request at a time
    /// (<c>{"method", "params"}</c> in, <c>{"result"}</c> or <c>{"error"}</c> out, through <see cref="LanguageExports"/>).
    /// <list type="bullet">
    /// <item><c>info</c>: the language's name, extensions, keywords and the editor's token legend.</item>
    /// <item><c>compile</c>: sources to a Larva HIR assembly (its <c>.lva</c> bytes) and the diagnostics.</item>
    /// <item><c>source</c>: an assembly written as Antril files (bindings, <c>larva new</c>'s templates).</item>
    /// <item><c>catalog</c>: <c>larva types</c>, <c>show</c> and <c>search</c> through Antril's view.</item>
    /// <item><c>lsp.*</c>: the editor's features over a document (larva's language server asks, and answers the editor).</item>
    /// </list>
    /// Assemblies come as <c>{"path"}</c> (a <c>.dll</c> or <c>.lva</c> file, read once while it does not change) or
    /// <c>{"data"}</c> (its bytes, base64).
    /// </summary>
    public sealed class AntrilLanguage
    {
        /// <summary>The version of the requests this pack answers (the manifest's <c>abi</c>).</summary>
        public const int Abi = 1;

        private readonly Dictionary<string, (string Stamp, HirAssembly Assembly)> loaded = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AntrilWorkspace> workspaces = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AntrilAnalysis> analyses = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AntrilWorkspace> analysisWorkspace = new(StringComparer.Ordinal);
        private AntrilWorkspace? lastWorkspace;

        /// <summary>One request's answer (the whole response object).</summary>
        public JsonObject Handle(JsonObject request)
        {
            try
            {
                string method = request["method"]?.GetValue<string>() ?? "";
                var parameters = request["params"] as JsonObject ?? [];
                return new JsonObject { ["result"] = Dispatch(method, parameters) };
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return new JsonObject { ["error"] = e is LanguageException ? e.Message : e.ToString() };
            }
        }

        private JsonNode? Dispatch(string method, JsonObject parameters) => method switch
        {
            "info" => Info(),
            "compile" => Compile(parameters),
            "source" => Source(parameters),
            "catalog" => Catalog(parameters),
            "lsp.analyze" => Analyze(parameters),
            "lsp.close" => Close(parameters),
            "lsp.reset" => Reset(),
            "lsp.completion" => At(parameters, Completion),
            "lsp.hover" => At(parameters, Hover),
            "lsp.definition" => At(parameters, Definition),
            "lsp.signatures" => At(parameters, Signatures),
            "lsp.symbols" => AnalysisOf(parameters) is { } a ? Symbols(a.Outline) : new JsonArray(),
            "lsp.tokens" => AnalysisOf(parameters) is { } a ? SemanticTokens(a) : new JsonObject { ["data"] = new JsonArray() },
            "lsp.workspaceSymbols" => WorkspaceSymbols(parameters["query"]?.GetValue<string>() ?? ""),
            "lsp.decompile" => Decompile(parameters),
            _ => throw new LanguageException($"antril: no request `{method}`"),
        };

        // ── info ───────────────────────────────────────

        private static JsonObject Info() => new()
        {
            ["name"] = "antril",
            ["abi"] = Abi,
            ["hir"] = LvaFormat.Version,
            ["extensions"] = new JsonArray(".ant"),
            ["keywords"] = new JsonArray([.. Lexer.Keywords.Keys.Order(StringComparer.Ordinal).Select(k => (JsonNode)k)]),
            ["tokenTypes"] = new JsonArray([.. Enum.GetNames<SemanticKind>().Select(n => (JsonNode)(char.ToLowerInvariant(n[0]) + n[1..]))]),
            ["tokenModifiers"] = new JsonArray("declaration", "static", "readonly", "defaultLibrary"),
        };

        // ── Assemblies ─────────────────────────────────

        /// <summary>An assembly given as <c>{"path"}</c> (cached while its file does not change) or <c>{"data"}</c>.</summary>
        private HirAssembly Assembly(JsonNode? reference)
        {
            if (reference?["data"]?.GetValue<string>() is { } data)
                return LvaFormat.Read(Convert.FromBase64String(data));
            string path = reference?["path"]?.GetValue<string>() ?? throw new LanguageException("antril: an assembly needs `path` or `data`");
            var info = new FileInfo(path);
            if (!info.Exists)
                throw new LanguageException($"{path} not found");
            string stamp = $"{info.LastWriteTimeUtc.Ticks}:{info.Length}";
            if (loaded.TryGetValue(path, out var cached) && cached.Stamp == stamp)
                return cached.Assembly;
            var assembly = LvaFormat.Load(path);
            loaded[path] = (stamp, assembly);
            return assembly;
        }

        private List<HirAssembly> Assemblies(JsonNode? list) => list is JsonArray array ? [.. array.Select(Assembly)] : [];

        private static IEnumerable<string> Strings(JsonNode? list) => list is JsonArray array ? array.Select(n => n!.GetValue<string>()) : [];

        // ── compile ────────────────────────────────────

        /// <summary>
        /// <c>{name, sources: [{path, text?}], references, resources?: [{name, data}], external?: [names], library?}</c>:
        /// <c>{assembly (base64) or null, diagnostics: [{text, error, code, file, line, column}], timings}</c>. A
        /// <c>library</c> names its files by their folder and file (System.Core/List.ant), not where they were on disk.
        /// </summary>
        private JsonObject Compile(JsonObject parameters)
        {
            string name = parameters["name"]?.GetValue<string>() ?? "Program";
            var sources = new List<SourceText>();
            foreach (var source in parameters["sources"] as JsonArray ?? [])
            {
                string path = source!["path"]!.GetValue<string>();
                sources.Add(source["text"]?.GetValue<string>() is { } text ? new SourceText(text, path) : SourceText.FromFile(path));
            }
            var references = Assemblies(parameters["references"]);
            var resources = (parameters["resources"] as JsonArray ?? []).Select(r => new HirResource(r!["name"]!.GetValue<string>(), Convert.FromBase64String(r["data"]!.GetValue<string>()))).ToList();
            var result = AntrilCompiler.Compile(sources, references, name, resources: resources.Count > 0 ? resources : null, externalAssemblies: Strings(parameters["external"]),
                options: Options(parameters["options"]));
            var diagnostics = new JsonArray();
            foreach (var diagnostic in result.Diagnostics)
            {
                var (line, column) = diagnostic.Source.LineColumn(diagnostic.Span.Start);
                diagnostics.Add((JsonNode)new JsonObject
                {
                    ["text"] = diagnostic.ToString(),
                    ["error"] = diagnostic.IsError,
                    ["code"] = diagnostic.Code,
                    ["file"] = diagnostic.Source.Path,
                    ["line"] = line,
                    ["column"] = column,
                });
            }
            string? assembly = null;
            if (result.Assembly is { } compiled)
            {
                if (parameters["library"]?.GetValue<bool>() is true)
                {
                    var files = compiled.Files;
                    for (int f = 0; f < files.Count; f++)
                    {
                        string full = Path.GetFullPath(files[f]);
                        files[f] = Path.GetFileName(Path.GetDirectoryName(full)) is { Length: > 0 } folder ? folder + "/" + Path.GetFileName(full) : Path.GetFileName(full);
                    }
                }
                assembly = Convert.ToBase64String(LvaFormat.Write(compiled));
            }
            var timings = new JsonObject();
            foreach (var (phase, ms) in result.Timings)
                timings[phase] = ms;
            return new JsonObject { ["assembly"] = assembly, ["diagnostics"] = diagnostics, ["timings"] = timings, ["types"] = result.Assembly?.Types.Count ?? 0 };
        }

        // ── source ─────────────────────────────────────

        /// <summary><c>{assembly, references, files?: {type: file}, defaultFile, nonPublic?}</c>: <c>{files: {file: text}}</c>.</summary>
        private JsonObject Source(JsonObject parameters)
        {
            var assembly = Assembly(parameters["assembly"]);
            var references = Assemblies(parameters["references"]);
            var fileOf = new Dictionary<string, string>(StringComparer.Ordinal);
            if (parameters["files"] is JsonObject files)
                foreach (var (type, file) in files)
                    fileOf[type] = file!.GetValue<string>();
            var written = Sources(assembly, references, fileOf, parameters["defaultFile"]?.GetValue<string>() ?? "", parameters["nonPublic"]?.GetValue<bool>() ?? false,
                parameters["header"]?.GetValue<bool>() ?? true);
            var result = new JsonObject();
            foreach (var (file, text) in written)
                result[file] = text;
            return new JsonObject { ["files"] = result };
        }

        /// <summary>The assembly written as Antril in one file, <paramref name="references"/> (System.Core) resolved in the catalog.</summary>
        public static string Source(HirAssembly assembly, IEnumerable<HirAssembly>? references = null) =>
            Sources(assembly, references, null, "")[""];

        /// <summary>
        /// The assembly written as Antril, one text per file: each type goes to the file <paramref name="fileOf"/> gives its
        /// full name (else to <paramref name="defaultFile"/>), every file with its own `import`s at the top, once.
        /// </summary>
        public static Dictionary<string, string> Sources(HirAssembly assembly, IEnumerable<HirAssembly>? references, IReadOnlyDictionary<string, string>? fileOf,
            string defaultFile, bool nonPublic = false, bool header = true)
        {
            var view = AntrilView.Instance;
            var catalog = HirCatalog.Create([.. references ?? [], assembly]);
            var parts = new Dictionary<string, (List<string> Imports, StringBuilder Text)>(StringComparer.OrdinalIgnoreCase);
            foreach (var definition in assembly.Types)
            {
                string fullName = definition.Namespace.Length == 0 ? definition.Name : $"{definition.Namespace}.{definition.Name}";
                // A namespace's functions and fields are its module (`$Module`), found by its identity.
                var type = definition.Name == "$Module"
                    ? catalog.Find(assembly.Name, definition.Namespace, definition.Name, definition.Generics.Count)
                    : catalog.Find(fullName);
                if (type is null)
                    continue;
                string file = fileOf is not null && fileOf.TryGetValue(fullName, out var placed) ? placed : defaultFile;
                if (!parts.TryGetValue(file, out var part))
                    parts[file] = part = ([], new StringBuilder());
                bool afterHeader = false;
                foreach (string line in view.Decompile(type, new DecompileOptions { Bodies = true, NonPublic = nonPublic }).Split('\n'))
                {
                    // Source written to be worked on (a new project) does not say it was decompiled.
                    if (!header && line.StartsWith("// ", StringComparison.Ordinal) && line.EndsWith("(decompiled from Larva HIR)", StringComparison.Ordinal))
                    {
                        afterHeader = true;
                        continue;
                    }
                    if (afterHeader && line.Length == 0)
                    {
                        afterHeader = false;
                        continue;
                    }
                    afterHeader = false;
                    if (line.StartsWith("import ", StringComparison.Ordinal))
                    {
                        if (!part.Imports.Contains(line))
                            part.Imports.Add(line);
                        continue;
                    }
                    part.Text.Append(line).Append('\n');
                }
            }
            if (parts.Count == 0)
                parts[defaultFile] = ([], new StringBuilder());
            return parts.ToDictionary(p => p.Key,
                p => (p.Value.Imports.Count > 0 ? string.Join("\n", p.Value.Imports) + "\n\n" : "") + p.Value.Text.ToString().Trim('\n') + "\n", StringComparer.OrdinalIgnoreCase);
        }

        // ── catalog ────────────────────────────────────

        /// <summary><c>{command, words, bodies?, nonPublic?, assemblies}</c>: <c>{output, error, exit}</c>.</summary>
        private JsonObject Catalog(JsonObject parameters)
        {
            var catalog = HirCatalog.Create(Assemblies(parameters["assemblies"]));
            var options = new DecompileOptions { Bodies = parameters["bodies"]?.GetValue<bool>() ?? true, NonPublic = parameters["nonPublic"]?.GetValue<bool>() ?? true };
            var output = new StringWriter { NewLine = "\n" };
            var error = new StringWriter { NewLine = "\n" };
            int exit = CatalogCommands.Run(parameters["command"]?.GetValue<string>() ?? "types", catalog, AntrilView.Instance, [.. Strings(parameters["words"])], options, output, error);
            return new JsonObject { ["output"] = output.ToString(), ["error"] = error.ToString(), ["exit"] = exit };
        }

        // ── Editor ─────────────────────────────────────

        /// <summary>
        /// <c>{uri, path, text, others: [paths], open: {path: text}, workspace: {key, references, external?, foreign?: [{name,
        /// sources}]}}</c>: the document analyzed with the rest of its program (open documents as they are in the editor),
        /// against the workspace's libraries (made once per key: System.Core, the project's assemblies, its dependencies
        /// built for another GC compiled here as references of their own domain). The diagnostics, as the editor shows them.
        /// </summary>
        private JsonArray Analyze(JsonObject parameters)
        {
            string uri = parameters["uri"]!.GetValue<string>();
            string path = parameters["path"]!.GetValue<string>();
            var ws = Workspace(parameters["workspace"] as JsonObject ?? []);
            var open = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (parameters["open"] is JsonObject texts)
                foreach (var (file, text) in texts)
                    open[Path.GetFullPath(file)] = text!.GetValue<string>();
            var others = new List<SourceText>();
            foreach (string file in Strings(parameters["others"]))
            {
                string full = Path.GetFullPath(file);
                if (string.Equals(full, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                    continue;
                // A file deleted since the project was read is no longer part of it.
                if (open.TryGetValue(full, out var text) || File.Exists(full) && TryRead(full, out text))
                    others.Add(new SourceText(text, file));
            }
            var analysis = ws.Analyze(new SourceText(parameters["text"]!.GetValue<string>(), path), others);
            analyses[uri] = analysis;
            analysisWorkspace[uri] = ws;
            var diagnostics = new JsonArray();
            foreach (var problem in analysis.Problems)
            {
                diagnostics.Add((JsonNode)new JsonObject
                {
                    ["range"] = Range(problem.Line - 1, problem.Column - 1, problem.EndLine - 1, problem.EndColumn - 1),
                    ["severity"] = problem.Severity == ProblemSeverity.Error ? 1 : 2,
                    ["code"] = problem.Code,
                    ["source"] = "antril",
                    ["message"] = problem.Message,
                });
            }
            return diagnostics;
        }

        private AntrilWorkspace Workspace(JsonObject description)
        {
            string key = description["key"]?.GetValue<string>() ?? "";
            if (workspaces.TryGetValue(key, out var cached))
                return lastWorkspace = cached;
            var references = Assemblies(description["references"]);
            var options = Options(description["options"]);
            var ws = new AntrilWorkspace(references) { Options = options };
            ws.ExternalAssemblies.UnionWith(Strings(description["external"]));
            foreach (var dependency in description["foreign"] as JsonArray ?? [])
            {
                // Its own GC domain: compiled as a library of its own, referenced here only for its declarations.
                string name = dependency!["name"]!.GetValue<string>();
                var compiled = AntrilCompiler.Compile(Strings(dependency["sources"]).Select(SourceText.FromFile), references.Take(1).ToList(), name);
                if (compiled.Assembly is { } library)
                {
                    references.Add(library);
                    var external = ws.ExternalAssemblies;
                    ws = new AntrilWorkspace(references) { Options = options };
                    ws.ExternalAssemblies.UnionWith(external);
                }
                ws.ExternalAssemblies.Add(name);
            }
            workspaces[key] = ws;
            return lastWorkspace = ws;
        }

        /// <summary>
        /// What a project sets, as larva sends it: <c>{ "access": "file", "borrows": false, "unsafe": false, "safe": [files] }</c>
        /// (BUILD <c>access.default</c>, whether its borrow checker is on, and <c>unsafe: deny</c> for its own files); the
        /// language's defaults for what is missing.
        /// </summary>
        private static AntrilOptions Options(JsonNode? node) => node is JsonObject o
            ? new AntrilOptions
            {
                DefaultAccess = AntrilOptions.AccessNamed(o["access"]?.GetValue<string>()),
                Borrows = o["borrows"]?.GetValue<bool>() ?? true,
                Unsafe = o["unsafe"]?.GetValue<bool>() ?? true,
                SafeFiles = o["safe"] is JsonArray safe
                    ? new HashSet<string>(safe.Select(f => Path.GetFullPath(f!.GetValue<string>())), StringComparer.OrdinalIgnoreCase)
                    : null,
            }
            : AntrilOptions.Default;

        private JsonNode? Close(JsonObject parameters)
        {
            string uri = parameters["uri"]!.GetValue<string>();
            analyses.Remove(uri);
            analysisWorkspace.Remove(uri);
            return null;
        }

        private JsonNode? Reset()
        {
            workspaces.Clear();
            analyses.Clear();
            analysisWorkspace.Clear();
            lastWorkspace = null;
            return null;
        }

        private AntrilAnalysis? AnalysisOf(JsonObject parameters) =>
            analyses.GetValueOrDefault(parameters["uri"]?.GetValue<string>() ?? "");

        private JsonNode? At(JsonObject parameters, Func<AntrilAnalysis, AntrilWorkspace, int, JsonNode?> feature)
        {
            string uri = parameters["uri"]!.GetValue<string>();
            if (!analyses.TryGetValue(uri, out var analysis))
                return null;
            int offset = Offset(analysis.Text, parameters["line"]!.GetValue<int>(), parameters["character"]!.GetValue<int>());
            return feature(analysis, analysisWorkspace[uri], offset);
        }

        private static bool TryRead(string file, out string text)
        {
            try
            {
                text = File.ReadAllText(file);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                text = "";
                return false;
            }
        }

        private HirCatalog? AnyCatalog() => analyses.Values.FirstOrDefault()?.Catalog
            ?? (lastWorkspace is { } ws ? HirCatalog.Create(ws.References) : null);

        private static int Offset(string text, int line, int character)
        {
            int offset = 0;
            for (int l = 0; l < line && offset < text.Length; l++)
            {
                int next = text.IndexOf('\n', offset);
                if (next < 0)
                    return text.Length;
                offset = next + 1;
            }
            return Math.Min(text.Length, offset + character);
        }

        private static (int Line, int Character) Position(string text, int offset)
        {
            int line = 0, start = 0;
            for (int i = 0; i < offset && i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    start = i + 1;
                }
            }
            return (line, offset - start);
        }

        private static JsonObject Range(int line, int character, int endLine, int endCharacter) => new()
        {
            ["start"] = new JsonObject { ["line"] = Math.Max(0, line), ["character"] = Math.Max(0, character) },
            ["end"] = new JsonObject { ["line"] = Math.Max(0, endLine), ["character"] = Math.Max(0, endCharacter) },
        };

        private static int LspKind(CompletionKind kind) => kind switch
        {
            CompletionKind.Keyword => 14,
            CompletionKind.Namespace => 9,
            CompletionKind.Class => 7,
            CompletionKind.Struct => 22,
            CompletionKind.Interface => 8,
            CompletionKind.Enum => 13,
            CompletionKind.Field => 5,
            CompletionKind.Constant => 21,
            CompletionKind.EnumValue => 20,
            CompletionKind.Property => 10,
            CompletionKind.Method => 2,
            CompletionKind.Function => 3,
            CompletionKind.Constructor => 4,
            CompletionKind.Event => 23,
            CompletionKind.Local or CompletionKind.Parameter => 6,
            CompletionKind.TypeParameter => 25,
            _ => 15,
        };

        private static JsonNode Completion(AntrilAnalysis analysis, AntrilWorkspace ws, int offset)
        {
            var list = ws.Service.Complete(analysis, offset);
            var items = new JsonArray();
            var (line, start) = Position(analysis.Text, list.ReplaceStart);
            foreach (var item in list.Items)
            {
                var entry = new JsonObject
                {
                    ["label"] = item.Label,
                    ["kind"] = LspKind(item.Kind),
                    ["sortText"] = item.Sort.ToString("D6"),
                    ["textEdit"] = new JsonObject { ["range"] = Range(line, start, line, start + list.ReplaceLength), ["newText"] = item.InsertText },
                };
                if (item.Detail is { } detail)
                    entry["detail"] = item.Overloads > 1 ? $"{detail} (+{item.Overloads - 1} overloads)" : detail;
                if (item.Doc is { } doc)
                    entry["documentation"] = new JsonObject { ["kind"] = "markdown", ["value"] = doc };
                if (item.Import is { } ns)
                {
                    // A type of a namespace not imported yet: choosing it adds the import.
                    var (importLine, importColumn) = Position(analysis.Text, analysis.ImportOffset);
                    entry["additionalTextEdits"] = new JsonArray((JsonNode)new JsonObject
                    {
                        ["range"] = Range(importLine, importColumn, importLine, importColumn),
                        ["newText"] = $"import {ns};\n" + (analysis.HasImports ? "" : "\n"),
                    });
                    entry["filterText"] = item.Label;
                }
                items.Add((JsonNode)entry);
            }
            return new JsonObject { ["isIncomplete"] = false, ["items"] = items };
        }

        private static JsonNode? Hover(AntrilAnalysis analysis, AntrilWorkspace ws, int offset)
        {
            if (ws.Service.Hover(analysis, offset) is not { } hover)
                return null;
            string value = $"```antril\n{hover.Signature}\n```" + (hover.Doc is { } doc ? "\n\n" + doc : "");
            return new JsonObject { ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = value } };
        }

        /// <summary>
        /// Where a name is declared: <c>{file, line, character}</c> in the program's own source, or <c>{decompiled: {assembly,
        /// type, text}, name}</c> for a library's (larva writes the text to a read-only file and finds the name in it).
        /// </summary>
        private static JsonNode? Definition(AntrilAnalysis analysis, AntrilWorkspace ws, int offset)
        {
            var symbol = analysis.SymbolAt(offset);
            var spot = ws.Service.Definition(analysis, offset);
            // The program's own assembly is the catalog's first: a library's member goes to its decompiled text even when the
            // library's source is on disk (opened as a file it would be analysed as a second copy of the type, and editable).
            var type = symbol?.Member?.Type ?? symbol?.CatalogType;
            bool ownSource = symbol?.Declared is not null || type is { Assembly.Index: 0 };
            if (ownSource && spot is not null)
                return new JsonObject { ["file"] = spot.File, ["line"] = spot.Line - 1, ["character"] = spot.Column - 1 };
            if (type is null)
                return null;
            return new JsonObject { ["decompiled"] = Decompiled(type, focus: symbol?.Member?.Key), ["name"] = symbol?.Member?.Name ?? type.Name };
        }

        /// <summary>
        /// A type as source: <c>{assembly, type, text, extension}</c>, and in Antril what each name is: <c>tokens</c> (offset,
        /// length, kind, modifiers, in fives: the editor colors the read-only file with them) and <c>links</c> (offset,
        /// length, the type and member a name stands for: the editor goes there, to that type's decompiled source).
        /// </summary>
        private static JsonObject Decompiled(CatalogType type, bool bodies = true, LanguageView? view = null, string? focus = null)
        {
            var options = new DecompileOptions { Bodies = bodies };
            var chosen = view ?? AntrilView.Instance;
            var result = new JsonObject
            {
                ["assembly"] = type.Assembly.Name,
                ["type"] = type.FullName,
                ["extension"] = chosen.Extension,
            };
            if (chosen != AntrilView.Instance)
            {
                result["text"] = chosen.Decompile(type, options);
                return result;
            }
            var (text, spans) = AntrilView.Instance.DecompileMarked(type, options);
            var tokens = new JsonArray();
            var links = new JsonArray();
            // Where the editor lands: the declaration of the member asked for (its overload), else the type's own name.
            int at = -1, byName = -1, typeAt = -1;
            string? focusName = focus?.Split('(')[0];
            foreach (var span in spans)
            {
                // A keyword that names a type (`i32`, `str`) is only a link: the theme colors it as the keyword it is.
                if (span.Colored)
                {
                    tokens.Add(JsonValue.Create(span.Offset));
                    tokens.Add(JsonValue.Create(span.Length));
                    tokens.Add(JsonValue.Create((int)span.Kind));
                    tokens.Add(JsonValue.Create((int)span.Modifiers));
                }
                bool declaration = (span.Modifiers & SemanticModifiers.Declaration) != 0;
                if (declaration)
                {
                    if (span.Member is null && span.Type == type && typeAt < 0)
                        typeAt = span.Offset;
                    else if (focus is not null && span.Member is { } declared && declared.Type == type)
                    {
                        if (at < 0 && declared.Key == focus)
                            at = span.Offset;
                        if (byName < 0 && declared.Name == focusName)
                            byName = span.Offset;
                    }
                    continue;
                }
                if (span.Type is { IsHidden: false } target)
                {
                    var link = new JsonObject
                    {
                        ["offset"] = span.Offset,
                        ["length"] = span.Length,
                        ["type"] = target.FullName,
                        ["assembly"] = target.Assembly.Name,
                        ["arity"] = target.Arity,
                    };
                    if (span.Member is { } member)
                    {
                        link["member"] = member.Name;
                        link["key"] = member.Key;
                    }
                    links.Add((JsonNode)link);
                }
            }
            result["at"] = at >= 0 ? at : byName >= 0 ? byName : Math.Max(0, typeAt);
            result["text"] = text;
            result["tokens"] = tokens;
            result["links"] = links;
            return result;
        }

        private static JsonNode? Signatures(AntrilAnalysis analysis, AntrilWorkspace ws, int offset)
        {
            if (ws.Service.Signatures(analysis, offset) is not { } help)
                return null;
            var signatures = new JsonArray();
            foreach (var signature in help.Signatures)
            {
                var parameters = new JsonArray();
                foreach (var (start, length) in signature.Parameters)
                    parameters.Add((JsonNode)new JsonObject { ["label"] = new JsonArray(start, start + length) });
                var entry = new JsonObject { ["label"] = signature.Label, ["parameters"] = parameters };
                if (signature.Doc is { } doc)
                    entry["documentation"] = new JsonObject { ["kind"] = "markdown", ["value"] = doc };
                signatures.Add((JsonNode)entry);
            }
            return new JsonObject { ["signatures"] = signatures, ["activeSignature"] = help.ActiveSignature, ["activeParameter"] = help.ActiveParameter };
        }

        private static int SymbolKind(CompletionKind kind) => kind switch
        {
            CompletionKind.Namespace => 3,
            CompletionKind.Class => 5,
            CompletionKind.Struct => 23,
            CompletionKind.Interface => 11,
            CompletionKind.Enum => 10,
            CompletionKind.EnumValue => 22,
            CompletionKind.Field => 8,
            CompletionKind.Property => 7,
            CompletionKind.Constructor => 9,
            CompletionKind.Function => 12,
            _ => 6,
        };

        private static JsonArray Symbols(IReadOnlyList<DocumentSymbol> symbols)
        {
            var array = new JsonArray();
            foreach (var symbol in symbols)
            {
                var range = Range(symbol.Line - 1, symbol.Column - 1, symbol.Line - 1, symbol.Column - 1 + symbol.Name.Length);
                array.Add((JsonNode)new JsonObject
                {
                    ["name"] = symbol.Name,
                    ["detail"] = symbol.Detail,
                    ["kind"] = SymbolKind(symbol.Kind),
                    ["range"] = range,
                    ["selectionRange"] = range.DeepClone(),
                    ["children"] = Symbols(symbol.Children),
                });
            }
            return array;
        }

        /// <summary>The document's names as the protocol packs them: five numbers each, positions relative to the one before.</summary>
        private static JsonObject SemanticTokens(AntrilAnalysis analysis)
        {
            var data = new JsonArray();
            string text = analysis.Text;
            int line = 0, lineStart = 0, scanned = 0, previousLine = 0, previousCharacter = 0;
            foreach (var token in analysis.Tokens)
            {
                for (; scanned < token.Offset; scanned++)
                {
                    if (text[scanned] == '\n')
                    {
                        line++;
                        lineStart = scanned + 1;
                    }
                }
                int character = token.Offset - lineStart;
                data.Add((JsonNode)(line - previousLine));
                data.Add((JsonNode)(line == previousLine ? character - previousCharacter : character));
                data.Add((JsonNode)token.Length);
                data.Add((JsonNode)(int)token.Kind);
                data.Add((JsonNode)(int)token.Modifiers);
                previousLine = line;
                previousCharacter = character;
            }
            return new JsonObject { ["data"] = data };
        }

        /// <summary>Types and members by name: <c>[{name, kind, container, file, line, character} or {…, decompiled}]</c>.</summary>
        private JsonNode WorkspaceSymbols(string query)
        {
            var result = new JsonArray();
            if (AnyCatalog() is not { } catalog || query.Length == 0)
                return result;
            foreach (var hit in catalog.Search(query, 100))
            {
                var spot = hit.Member?.Location ?? hit.Type.Location;
                var entry = new JsonObject
                {
                    ["name"] = hit.Member is null ? hit.Type.Name : hit.Member.Name,
                    ["kind"] = hit.Member is null ? SymbolKind(LanguageService.Kind(hit.Type)) : SymbolKind(LanguageService.Kind(hit.Member)),
                    ["container"] = hit.Member is null ? hit.Type.Namespace : hit.Type.FullName,
                };
                if (spot is not null && File.Exists(spot.File))
                {
                    entry["file"] = spot.File;
                    entry["line"] = spot.Line - 1;
                    entry["character"] = spot.Column - 1;
                }
                else
                    entry["decompiled"] = Decompiled(hit.Type);
                result.Add((JsonNode)entry);
            }
            return result;
        }

        /// <summary>
        /// <c>{type, bodies?, view?}</c>: a type of the editor's catalog as Antril (or through another view, <c>hir</c>):
        /// <c>{assembly, type, text, extension}</c>, or null.
        /// </summary>
        private JsonNode? Decompile(JsonObject parameters)
        {
            string name = parameters["type"]!.GetValue<string>();
            var view = parameters["view"]?.GetValue<string>() is { } chosen && LanguageViews.Get(chosen) is { } other ? other : AntrilView.Instance;
            if (AnyCatalog() is not { } catalog)
                return null;
            // The type of that library (a program may declare a type of the same name), else the first of that name.
            string? assembly = parameters["assembly"]?.GetValue<string>();
            int arity = parameters["arity"]?.GetValue<int>() ?? -1;
            var type = (assembly is null ? null : catalog.AllTypes.FirstOrDefault(t => !t.IsHidden && t.Assembly.Name == assembly && t.FullName == name && (arity < 0 || t.Arity == arity)))
                ?? catalog.Find(name);
            return type is null ? null : Decompiled(type, parameters["bodies"]?.GetValue<bool>() ?? true, view, parameters["member"]?.GetValue<string>());
        }
    }

    /// <summary>A request the pack cannot answer, told to larva as its message.</summary>
    public sealed class LanguageException(string message) : Exception(message);
}
