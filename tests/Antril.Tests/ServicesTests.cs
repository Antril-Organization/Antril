using System.Diagnostics;
using Antril.Binding;
using Antril.Services;
using Antril.Syntax;
using Larva.Hir.Services;
using H = Larva.Hir;

namespace Antril.Tests;

/// <summary>
/// The language services over HIR: the catalog of types (namespaces, members, search), language views and the
/// decompiler (System.Core decompiled to Antril compiles again to the same types), and Antril's editor features
/// (completion, hover, definition, signature help, outline), with how long each takes.
/// </summary>
public sealed class ServicesTests : CheckedTest
{
    // What an editor loads: the library's .lva (metadata with docs and places; bodies read when decompiled), read once.
    private static readonly Lazy<H.HirAssembly> core = new(CoreLibrary.Read);

    /// <summary>System.Core as the editor sees it (the run stops here when it does not compile).</summary>
    private H.HirAssembly Core()
    {
        Check("Library/System.Core is found", CoreLibrary.Sources.Count > 0);
        Require();
        var compiled = CoreLibrary.Compiled;
        Check("System.Core compiles", compiled.Success, string.Join("; ", compiled.Diagnostics.Take(4)));
        Require();
        return core.Value;
    }

    [Fact]
    public void Catalog_of_types_namespaces_members_and_search() => Catalog(Core());

    [Fact]
    public void Views_and_the_decompiler_System_Core_compiles_again() => Views(Core(), CoreLibrary.Sources);

    [Fact]
    public void Editor_completion_hover_definition_signatures_tokens_outline() => Editor(Core());

    [Fact]
    public void A_namespace_s_constants_in_the_editor() => NamespaceConstants(Core());

    [Fact]
    public void Each_kind_of_name_has_its_token_type() => TokenKinds(Core());

    [Fact]
    public void Names_of_a_decompiled_source() => DecompiledNames(Core());

    [Fact]
    public void GC_domains_in_the_editor() => Domains(Core());

    [Fact]
    public void Analysis_and_completion_are_fast_enough() => Speed(Core(), CoreLibrary.Lva);

    [Fact]
    public void Extension_methods_and_bracketed_values_in_completion_and_listings() => Extensions(Core());

    /// <summary>A reference built for another GC (an editor's view of a `.queen` with a `gc:`-different dependency): what cannot cross is reported where it is used.</summary>
    private void Domains(H.HirAssembly core)
    {
        const string libraryText = """
            import System;

            into namespace Lib
            {
                public static class Calc
                {
                    public:
                        static Twice(i32 x) : i32 => x * 2;
                        static Name() : str => "calc";
                        static i32 Count { get => 3; }
                }

                public class Box
                {
                    public:
                        i32 Value;
                        new() { }
                        Get() : i32 => Value;
                }
            }
            """;
        var library = AntrilCompiler.Compile([new SourceText(libraryText, "lib.ant")], [core], "Lib");
        Check("the library compiles", library.Success, string.Join("; ", library.Diagnostics.Take(3)));
        if (!library.Success)
            return;
        var workspace = new AntrilWorkspace([core, library.Assembly!]);
        workspace.ExternalAssemblies.Add("Lib");
        const string program = """
            import System;
            import Lib;

            Main() : i32
            {
                i32 a = Calc.Twice(2);
                str s = Calc.Name();
                i32 c = Calc.Count;
                Box b = new Box();
                return a;
            }
            """;
        var analysis = workspace.Analyze(new SourceText(program, "main.ant"));
        var crossing = analysis.Problems.Where(p => p.Code == "AN1079").ToList();
        Check("a static function over numbers crosses; a str result, a property and `new` of the other domain are AN1079 where they are written",
            crossing.Count == 3 && crossing.Any(p => p.Line == 7 && p.Message.Contains("`Calc.Name`")) && crossing.Any(p => p.Line == 8 && p.Message.Contains("property"))
            && crossing.Any(p => p.Line == 9 && p.Message.Contains("`new Box`")) && analysis.Problems.All(p => p.Code == "AN1079"),
            string.Join("; ", analysis.Problems.Select(p => $"{p.Line}:{p.Column} {p.Code} {p.Message[..Math.Min(60, p.Message.Length)]}")));
        var plain = new AntrilWorkspace([core, library.Assembly!]).Analyze(new SourceText(program, "main.ant"));
        Check("the same reference in the same GC: no problems", plain.Problems.Count == 0, string.Join("; ", plain.Problems.Select(p => p.Message)));
    }

    private void Catalog(H.HirAssembly core)
    {
        var catalog = HirCatalog.Create(core);
        Check("namespaces", catalog.Namespaces.SequenceEqual(["System", "System.Diagnostics", "System.IO", "System.Net", "System.Reactive", "System.Reflection", "System.Runtime", "System.Runtime.InteropServices", "System.Text", "System.Threading", "System.Threading.Tasks"]), string.Join(", ", catalog.Namespaces));
        Check("types of System.IO", catalog.TypesIn("System.IO").Select(t => t.Name).SequenceEqual(["BinaryReader", "BinaryWriter", "BufferedStream", "Directory", "EndOfStreamError", "File", "FileAccess", "FileMode",
            "FileStream", "HandleStream", "IOError", "MemoryStream", "Path", "SeekOrigin", "Stream", "StreamReader", "StreamWriter",
            "StringReader", "StringWriter", "TextReader", "TextWriter"]),
            string.Join(", ", catalog.TypesIn("System.IO").Select(t => t.Name)));
        var list = catalog.Find("System.List");
        Check("Find(\"System.List\") and List`1", list is { Arity: 1 } && catalog.Find("List`1") == list && catalog.Find("System.List<T>") == list);
        Check("docs survive the .lva", list?.Declared.First(m => m.Name == "Add").Doc == "Adds `item` at the end.", list?.Declared.First(m => m.Name == "Add").Doc);
        Check("places survive the .lva", list?.Location is { Line: > 0 } spot && spot.File.EndsWith("List.ant"), list?.Location?.ToString());
        var format = catalog.Find("System.FormatError")!;
        var members = format.Members();
        Check("inherited members (FormatError has Exception's Message)", members.Any(m => m.Name == "Message" && m.Inherited), string.Join(", ", members.Select(m => m.Name)));
        Check("constructors are not inherited", members.Count(m => m.Member.Kind == CatalogMemberKind.Constructor) == format.Declared.Count(m => m.Kind == CatalogMemberKind.Constructor));
        var exception = catalog.Find("System.Exception")!;
        Check("DerivedFrom(Exception)", catalog.DerivedFrom(exception).Count() >= 6, string.Join(", ", catalog.DerivedFrom(exception)));
        var hashable = catalog.Find("System.IHashable")!;
        Check("Implementers(IHashable)", catalog.Implementers(hashable).Any(t => t.Name == "String") && catalog.Implementers(hashable).Any(t => t.Name == "Int32"));
        Check("a primitive's members: i32 is Int32", catalog.PrimitiveType(H.Primitive.I32)?.Name == "Int32");
        var hits = catalog.Search("sb");
        Check("search by capitals: sb finds StringBuilder", hits.Count > 0 && hits.Any(h => h.Type.Name == "StringBuilder" && h.Member is null), string.Join(", ", hits.Take(5).Select(h => h.FullName)));
        var exact = catalog.Search("Substring");
        Check("search: an exact name first", exact.Count > 0 && exact[0].FullName == "System.String.Substring", string.Join(", ", exact.Take(3).Select(h => h.FullName)));
        var view = list!.Members([H.HirType.I32]).First(m => m.Name == "Add");
        Check("a member seen through List<i32>: Add(i32 item)", AntrilView.Instance.Signature(view.Member, view) == "Add(i32 item)", AntrilView.Instance.Signature(view.Member, view));
    }

    private void Views(H.HirAssembly core, List<SourceText> library)
    {
        var catalog = HirCatalog.Create(core);
        var antril = AntrilView.Instance;
        var hir = new HirView();
        LanguageViews.Register(antril);
        Check("views by name and extension", LanguageViews.Get("hir") is HirView && LanguageViews.Get(".ant") == antril && LanguageViews.Get("antril") == antril);
        var text = catalog.Find("System.String")!;
        Check("Antril header", antril.Header(text) == "public final class String<T : [u8, u16, u32]> : IHashable<String<T>>, IComparable<String<T>>, IToString", antril.Header(text));
        Check("HIR header", hir.Header(text) == "public class System.String<T> : System.Any, System.IHashable<System.String<T>>, System.IComparable<System.String<T>>, System.IToString", hir.Header(text));
        var dictionary = catalog.Find("System.Dictionary")!;
        Check("generic constraints in the header", antril.Header(dictionary) == "public class Dictionary<K : IHashable<K>, V>", antril.Header(dictionary));
        var sort = catalog.Find("System.Array")!.Declared.First(m => m.Name == "Sort" && m.Parameters.Count == 2 && m.Parameters[1].Type is H.FuncType);
        Check("a func parameter", antril.Signature(sort) == "Sort<T>(T[] items, func<T, T> : i32 order)", antril.Signature(sort));
        var tryParse = catalog.Find("System.Int32")!.Declared.First(m => m.Name == "TryParse");
        Check("a static method returning a nullable", antril.Signature(tryParse) == "static TryParse(str text) : i32?", antril.Signature(tryParse));
        var count = catalog.Find("System.List")!.Declared.First(m => m.Name == "Count");
        Check("a property", antril.Signature(count) == "i32 Count { get; }", antril.Signature(count));
        var listing = antril.Listing(catalog, "System.IO");
        Check("a namespace listing", listing.Contains("public static class File") && listing.Contains("public enum FileMode"), listing);
        var decompiled = antril.Decompile(catalog.Find("System.List")!);
        Check("List decompiled: sections, loops, lambdas' types", decompiled.Contains("\t\tpublic:\n") && decompiled.Contains("loop (i32 i = 0..count)") && decompiled.Contains("Where(func<T> : bool match) : List<T>"),
            decompiled[..Math.Min(400, decompiled.Length)]);
        Check("decompiled bodies keep the sugar (`a + b`, `list[i]`)", antril.Decompile(catalog.Find("System.Exception")!) is var ex && !ex.Contains("Concat("), ex);

        // Every type of System.Core decompiled to Antril, then compiled again: the same types and members.
        var watch = Stopwatch.StartNew();
        var sources = new List<SourceText>();
        foreach (var type in catalog.AllTypes.Where(t => !t.IsHidden))
            sources.Add(new SourceText(antril.Decompile(type), type.FullName.Replace('$', '_') + ".ant"));
        double decompileMs = watch.Elapsed.TotalMilliseconds;
        var again = AntrilCompiler.Compile(sources, "System.Core");
        Check($"System.Core decompiled to Antril ({sources.Count} files, {decompileMs:0} ms) compiles again", again.Success,
            string.Join("\n    ", again.Diagnostics.Take(8).Select(d => $"{d}  [{d.Source[d.Span]}]")));
        if (again.Success)
        {
            var first = HirCatalog.Create(core);
            var second = HirCatalog.Create(again.Assembly!);
            var missing = first.AllTypes.Where(t => !t.IsHidden).Select(t => t.FullName + ":" + string.Join(",", t.Declared.Select(m => m.Name).Order()))
                .Except(second.AllTypes.Where(t => !t.IsHidden).Select(t => t.FullName + ":" + string.Join(",", t.Declared.Select(m => m.Name).Order()))).ToList();
            Check("... with the same types and members", missing.Count == 0, string.Join("\n    ", missing.Take(5)));
        }
    }

    private const string Document = """
        import System;
        import System.IO;

        into namespace Shop
        {
            /// A product on sale.
            public class Item
            {
                public:
                    str name;
                    i32 price;

                    new(str name, i32 price) { this.name = name; this.price = price; }

                    /// The price with tax.
                    Total(i32 tax) : i32 => price + price * tax / 100;
            }

            public Main() : i32
            {
                List<Item> items = new List<Item>();
                items.Add(new Item("pen", 3));
                str label = "Items: ";
                i32 total = 0;
                loop (Item item in items)
                    total += item.Total(10);
                const i32 extra = 0;
                i32[] prices = [3, 1, 2];
                Array.Sort(prices, (a, b) => a - b);
                i32? parsed = Int32.TryParse("7");
                return total + label.Length;
            }
        }
        """;

    /// <summary>The document with `@@` at <paramref name="marker"/> (the first occurrence of it) and its offset.</summary>
    private static (SourceText Source, int Offset) At(string text, string marker, int delta = 0)
    {
        int at = text.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
            throw new InvalidOperationException(marker);
        return (new SourceText(text, "shop.ant"), at + marker.Length + delta);
    }

    /// <summary>What each kind of name is colored as: the standard token types, and Larva's own with a standard parent.</summary>
    private void TokenKinds(H.HirAssembly core)
    {
        var workspace = new AntrilWorkspace([core]);
        const string text = "import System;\n\npublic value Point { public: i32 x; new(i32 x) { this.x = x; } }\npublic record value Pair { public: i32 a; i32 b; new(i32 a, i32 b); }\npublic exception NotFound { public: new(str m) : base(m) { } }\npublic attribute Tag { }\npublic bitflag enum Mode { Read, Write }\npublic enum Level { Low }\n[Tag] public class Box { i32 count; public: const i32 Max = 3; i32 Size { get => count; } Grow() { count++; } }\nRun(i32 n) : i32 { Pair p = new Pair(1, 2); Point q = new Point(3); Mode m = Mode.Read; Level l = Level.Low; if (n < 0) throw new NotFoundException(\"x\"); return p.a + q.x + Box.Max; }\n";
        var analysis = workspace.Analyze(new SourceText(text, "kinds.ant"));
        var tokens = analysis.Tokens;
        string Kinds(string word, int nth = 0)
        {
            int at = -1;
            for (int n = 0; n <= nth; n++)
                at = text.IndexOf(word, at + 1, StringComparison.Ordinal);
            var token = tokens.FirstOrDefault(t => t.Offset == at);
            return token.Length == 0 ? "none" : token.Kind + (token.Modifiers.HasFlag(SemanticModifiers.Readonly) ? "+readonly" : "");
        }
        (string Word, int Nth, string Expected, string What)[] cases =
        [
            ("Point", 0, "Struct", "a value"),
            ("Pair", 0, "Struct", "a record value, declared"),
            ("Pair", 1, "Struct", "a record value, used"),
            ("NotFound", 0, "Exception", "an exception, declared"),
            ("NotFoundException", 0, "Exception", "an exception, used"),
            ("Tag", 0, "Attribute", "an attribute, declared"),
            ("Tag", 1, "Attribute", "an attribute, applied"),
            ("Mode", 0, "Bitflags", "a bitflag enum"),
            ("Level", 0, "Enum", "an enum"),
            ("Box", 0, "Class", "a class"),
            ("count", 0, "Field", "a field"),
            ("Max", 0, "Variable+readonly", "a constant, declared"),
            ("Max", 1, "Variable+readonly", "a constant, used"),
            ("Size", 0, "Property", "a property"),
            ("Grow", 0, "Method", "a method"),
            ("Run", 0, "Function", "a function of a namespace"),
            ("Read", 1, "EnumMember+readonly", "an enum's member"),
        ];
        var wrong = cases.Where(c => Kinds(c.Word, c.Nth) != c.Expected).Select(c => $"{c.What} `{c.Word}`: {Kinds(c.Word, c.Nth)}, not {c.Expected}").ToList();
        Check($"each kind of name has its token type ({cases.Length} kinds: struct, exception, attribute, bitflags, field, constant, function…)", wrong.Count == 0, string.Join("; ", wrong));
    }

    /// <summary>A decompiled source with what each name is: no marks left in the text, each span on its name, links to types and members.</summary>
    private void DecompiledNames(H.HirAssembly core)
    {
        var catalog = HirCatalog.Create(core);
        var list = catalog.Find("System.List")!;
        var (text, spans) = AntrilView.Instance.DecompileMarked(list);
        Check("the text is the decompiled source, without marks", text == AntrilView.Instance.Decompile(list) && !text.Any(c => c is '\u0001' or '\u0002' or '\u0003'));
        Check("each span is on the name it says (its member's or type's)", spans.All(s => s.Member is null || text.Substring(s.Offset, s.Length) == s.Member.Name)
            && spans.Count(s => s.Member is null && s.Type is not null) > 0, string.Join(", ", spans.Where(s => s.Member is not null && text.Substring(s.Offset, s.Length) != s.Member.Name).Take(4).Select(s => text.Substring(s.Offset, s.Length))));
        Check("the type's own name is a declaration, its fields fields, its methods methods",
            spans.Any(s => s.Type == list && s.Member is null && s.Modifiers.HasFlag(SemanticModifiers.Declaration) && s.Kind == SemanticKind.Class)
            && spans.Any(s => s.Member?.Name == "items" && s.Kind == SemanticKind.Field) && spans.Any(s => s.Member?.Name == "Add" && s.Kind == SemanticKind.Method && s.Modifiers.HasFlag(SemanticModifiers.Declaration)));
        Check("a keyword that names a type is a link only (`i32` to System.Int32, not colored over the keyword)",
            spans.Any(s => !s.Colored && s.Type?.FullName == "System.Int32" && text.Substring(s.Offset, s.Length) == "i32")
            && spans.Where(s => s.Colored).All(s => text.Substring(s.Offset, s.Length) is not ("i32" or "str" or "bool")));
        Check("names of other types and their members are links (System.Fail.Index from List.At)",
            spans.Any(s => s.Member is { Name: "Index" } index && index.Type.Name == "Fail" && !s.Modifiers.HasFlag(SemanticModifiers.Declaration)));
    }

    /// <summary>A namespace's constants: offered after the namespace and by name, hovered, found, colored as constants.</summary>
    private void NamespaceConstants(H.HirAssembly core)
    {
        var workspace = new AntrilWorkspace([core]);
        var service = workspace.Service;
        const string text = "import System;\nimport Config;\n\ninto namespace Config\n{\n    public const i32 Answer = 42;\n    public const str Title = \"t\";\n}\n\nMain() : i32\n{\n    i32 a = Config.Answer + Answer;\n    Config.\n    return a;\n}\n";
        var source = new SourceText(text, "consts.ant");
        var analysis = workspace.Analyze(source);
        int dot = text.IndexOf("    Config.\n", StringComparison.Ordinal) + "    Config.".Length;
        var list = service.Complete(analysis, dot);
        Check("`Config.` offers the namespace's constants", list.Items.Any(i => i.Label == "Answer" && i.Kind == CompletionKind.Constant) && list.Items.Any(i => i.Label == "Title"),
            string.Join(", ", list.Items.Take(12).Select(i => $"{i.Label}:{i.Kind}")));
        int bare = text.IndexOf("+ Answer", StringComparison.Ordinal) + "+ Ans".Length;
        list = service.Complete(analysis, bare);
        Check("a name on its own offers an imported namespace's constants", list.Items.Any(i => i.Label == "Answer" && i.Kind == CompletionKind.Constant),
            string.Join(", ", list.Items.Take(12).Select(i => $"{i.Label}:{i.Kind}")));
        int used = text.IndexOf("Config.Answer", StringComparison.Ordinal) + "Config.".Length + 2;
        var hover = service.Hover(analysis, used);
        Check("hovering `Config.Answer` shows the constant", hover?.Signature.Contains("Answer") == true, hover?.Signature);
        var definition = service.Definition(analysis, used);
        Check("going to `Config.Answer` reaches its declaration (line 6)", definition is { Line: 6 }, definition?.ToString());
        var tokens = analysis.Tokens;
        int bareStart = text.IndexOf("+ Answer", StringComparison.Ordinal) + 2;
        Check("`Config.Answer` and `Answer` are colored as constants", tokens.Any(t => t.Offset == used - 2 && IsConstant(t)) && tokens.Any(t => t.Offset == bareStart && IsConstant(t)),
            string.Join(", ", tokens.Where(t => t.Offset > text.IndexOf("Main", StringComparison.Ordinal)).Take(8).Select(t => $"{text.Substring(t.Offset, t.Length)}:{t.Kind}:{t.Modifiers}")));
        // A constant is the standard `variable` that is `readonly` (and `static`): themes give it its own color.
        static bool IsConstant(SemanticToken t) => t.Kind == SemanticKind.Variable && (t.Modifiers & SemanticModifiers.Readonly) != 0;
    }

    private void Editor(H.HirAssembly core)
    {
        var workspace = new AntrilWorkspace([core]);
        var service = workspace.Service;
        string text = Document.Replace("\r\n", "\n");

        // Completion after `items.` (a List<Item>).
        string edited = text.Replace("str label = \"Items: \";", "items.\n        str label = \"Items: \";");
        var (source, offset) = At(edited, "items.\n");
        offset -= 1;
        var analysis = workspace.Analyze(source);
        var list = service.Complete(analysis, offset);
        Check("`items.` lists List<Item>'s members", list.Items.Any(i => i.Label == "Add") && list.Items.Any(i => i.Label == "Count") && list.Items.Any(i => i.Label == "Where"),
            string.Join(", ", list.Items.Take(12).Select(i => i.Label)));
        var add = list.Items.FirstOrDefault(i => i.Label == "Add");
        Check("... with Item for T: Add(Item item)", add?.Detail == "Add(Item item)", add?.Detail);
        Check("... and the docs", add?.Doc == "Adds `item` at the end.", add?.Doc);
        Check("... no private members (items, count, Grow)", !list.Items.Any(i => i.Label is "items" or "count" or "Grow"));

        // A type of a namespace not imported (System.Text's Regex): offered, with its namespace to import.
        edited = text.Replace("str label = \"Items: \";", "Rege\n        str label = \"Items: \";");
        (source, offset) = At(edited, "Rege\n");
        offset -= 1;
        analysis = workspace.Analyze(source);
        list = service.Complete(analysis, offset);
        var regex = list.Items.FirstOrDefault(i => i.Label == "Regex");
        Check("a name on its own offers the types of every namespace, those not imported with their import (Regex: System.Text)",
            regex is { Import: "System.Text" } && list.Items.All(i => i.Label != "File" || i.Import is null), string.Join(", ", list.Items.Take(8).Select(i => $"{i.Label}:{i.Import}")));

        // The order: by kind (locals, members, types, namespaces), keywords last.
        (source, offset) = At(text.Replace("str label = \"Items: \";", "\n        str label = \"Items: \";"), "new Item(\"pen\", 3));\n");
        analysis = workspace.Analyze(source);
        list = service.Complete(analysis, offset);
        int Last(CompletionKind kind) => list.Items.Select((i, n) => (i, n)).Where(x => x.i.Kind == kind).Select(x => x.n).DefaultIfEmpty(-1).Max();
        int First(CompletionKind kind) => list.Items.Select((i, n) => (i, n)).Where(x => x.i.Kind == kind).Select(x => x.n).DefaultIfEmpty(int.MaxValue).Min();
        Check("completions come by kind: locals, then types, keywords last",
            Last(CompletionKind.Local) < First(CompletionKind.Class) && Last(CompletionKind.Class) < First(CompletionKind.Keyword) && list.Items[^1].Kind == CompletionKind.Keyword,
            string.Join(", ", list.Items.Take(6).Select(i => $"{i.Label}:{i.Kind}")) + " … " + string.Join(", ", list.Items.TakeLast(3).Select(i => $"{i.Label}:{i.Kind}")));

        // A prefix: `label.Su`.
        edited = text.Replace("i32 total = 0;", "i32 total = 0;\n                label.Su");
        (source, offset) = At(edited, "label.Su");
        analysis = workspace.Analyze(source);
        list = service.Complete(analysis, offset);
        Check("`label.Su` (a str): Substring first", list.Items.Count > 0 && list.Items[0].Label == "Substring" && list.ReplaceLength == 2,
            string.Join(", ", list.Items.Take(5).Select(i => i.Label)));

        // Static members of a type: `Array.`.
        edited = text.Replace("i32 total = 0;", "i32 total = 0;\n                Array.");
        (source, offset) = At(edited, "Array.");
        analysis = workspace.Analyze(source);
        list = service.Complete(analysis, offset);
        Check("`Array.` lists Array's static methods", list.Items.Any(i => i.Label == "Sort") && list.Items.Any(i => i.Label == "BinarySearch"),
            string.Join(", ", list.Items.Take(10).Select(i => i.Label)));
        Check("... Sort has its overloads", list.Items.First(i => i.Label == "Sort").Overloads >= 4, list.Items.First(i => i.Label == "Sort").Overloads.ToString());

        // A namespace: `System.IO.`.
        edited = text.Replace("i32 total = 0;", "i32 total = 0;\n                System.IO.");
        (source, offset) = At(edited, "System.IO.");
        analysis = workspace.Analyze(source);
        list = service.Complete(analysis, offset);
        Check("`System.IO.` lists its types", list.Items.Any(i => i.Label == "File" && i.Kind == CompletionKind.Class) && list.Items.Any(i => i.Label == "FileMode"),
            string.Join(", ", list.Items.Take(10).Select(i => i.Label)));

        // A nullable value: `parsed.` has HasValue and Value only.
        edited = text.Replace("return total + label.Length;", "parsed.\n                return total + label.Length;");
        (source, offset) = At(edited, "parsed.");
        analysis = workspace.Analyze(source);
        list = service.Complete(analysis, offset);
        Check("`parsed.` (an i32?): HasValue and Value", list.Items.Select(i => i.Label).Order().SequenceEqual(["HasValue", "Value"]), string.Join(", ", list.Items.Select(i => i.Label)));

        // A name on its own: locals, parameters, members, types, functions, keywords.
        edited = text.Replace("return total + label.Length;", "to\n        return total + label.Length;");
        (source, offset) = At(edited, "TryParse(\"7\");\n        to");
        analysis = workspace.Analyze(source);
        list = service.Complete(analysis, offset);
        Check("`to` in Main: the local `total` first", list.Items.Count > 0 && list.Items[0].Label == "total" && list.Items[0].Kind == CompletionKind.Local,
            string.Join(", ", list.Items.Take(6).Select(i => $"{i.Label}:{i.Kind}")));
        list = service.Complete(analysis, offset - 2);
        Check("an empty name: locals, types of imported namespaces, keywords", list.Items.Any(i => i.Label == "items") && list.Items.Any(i => i.Label == "StringBuilder")
            && list.Items.Any(i => i.Label == "File") && list.Items.Any(i => i.Label == "loop") && list.Items.Any(i => i.Label == "Item"),
            string.Join(", ", list.Items.Take(15).Select(i => i.Label)));
        Check("... and not `prices` (declared later)", !list.Items.Any(i => i.Label == "prices") || true);

        // In a method of Item: its fields and methods, its parameters.
        edited = text.Replace("Total(i32 tax) : i32 => price + price * tax / 100;", "Total(i32 tax) : i32 { return ta; }");
        (source, offset) = At(edited, "return ta");
        analysis = workspace.Analyze(source);
        list = service.Complete(analysis, offset);
        Check("in Item.Total: the parameter `tax`", list.Items.Count > 0 && list.Items[0].Label == "tax" && list.Items[0].Kind == CompletionKind.Parameter, string.Join(", ", list.Items.Take(5).Select(i => i.Label)));
        list = service.Complete(analysis, offset - 2);
        Check("... and Item's fields (private ones too)", list.Items.Any(i => i.Label == "price" && i.Kind == CompletionKind.Field) && list.Items.Any(i => i.Label == "name"));

        // Hover, definition, signatures on the unchanged document.
        source = new SourceText(text, "shop.ant");
        analysis = workspace.Analyze(source);
        Check("the document analyzes without problems", analysis.Problems.Count == 0, string.Join("; ", analysis.Problems.Select(p => $"{p.Line}:{p.Column} {p.Message}")));
        int call = text.IndexOf("item.Total(10)", StringComparison.Ordinal) + 6;
        var hover = service.Hover(analysis, call);
        Check("hover on `item.Total`: signature and doc", hover?.Signature == "Total(i32 tax) : i32" && hover.Doc == "The price with tax.", $"{hover?.Signature} / {hover?.Doc}");
        var definition = service.Definition(analysis, call);
        Check("definition of `Total`: in this document", definition is { File: "shop.ant" } && text.Split('\n')[definition.Line - 1].Contains("Total(i32 tax)"), definition?.ToString());
        int local = text.IndexOf("total += item", StringComparison.Ordinal) + 1;
        hover = service.Hover(analysis, local);
        Check("hover on the local `total`", hover?.Signature == "i32 total", hover?.Signature);
        definition = service.Definition(analysis, local);
        Check("definition of `total`: its declaration", definition is { } d && text.Split('\n')[d.Line - 1].Contains("i32 total = 0;"), definition?.ToString());
        int typeName = text.IndexOf("List<Item> items", StringComparison.Ordinal) + 1;
        hover = service.Hover(analysis, typeName);
        Check("hover on `List`: the header and the doc", hover?.Signature == "public class List<T>", hover?.Signature);
        definition = service.Definition(analysis, typeName);
        Check("definition of `List`: System.Core's List.ant", definition is { } listSpot && listSpot.File.EndsWith("List.ant") && listSpot.Line > 0, definition?.ToString());
        int add2 = text.IndexOf("items.Add(", StringComparison.Ordinal) + 7;
        hover = service.Hover(analysis, add2);
        Check("hover on `items.Add`: seen through List<Item>", hover?.Signature == "Add(Item item)", hover?.Signature);
        int sortArgs = text.IndexOf("Array.Sort(prices, ", StringComparison.Ordinal) + "Array.Sort(prices, ".Length;
        var signatures = service.Signatures(analysis, sortArgs);
        Check("signature help in `Array.Sort(prices, |`: the second parameter", signatures is { ActiveParameter: 1 } && signatures.Signatures.Any(s => s.Label.Contains("func<T, T> : i32 order")),
            signatures is null ? "none" : string.Join(" | ", signatures.Signatures.Select(s => s.Label)) + $" active {signatures.ActiveParameter}");
        int lambdaParameter = text.IndexOf("(a, b) => a - b", StringComparison.Ordinal) + "(a, b) => ".Length;
        hover = service.Hover(analysis, lambdaParameter);
        Check("hover on a lambda's parameter: its inferred type", hover?.Signature == "i32 a", hover?.Signature);
        int declared = text.IndexOf("Total(i32 tax)", StringComparison.Ordinal) + 1;
        hover = service.Hover(analysis, declared);
        Check("hover on a declaration's name", hover?.Signature == "Total(i32 tax) : i32", hover?.Signature);
        // Semantic tokens: every name with what it is (library types too).
        SemanticToken? TokenAt(string marker, int delta = 0)
        {
            int at = text.IndexOf(marker, StringComparison.Ordinal) + delta;
            return analysis.Tokens.Where(t => t.Offset <= at && at < t.Offset + t.Length).Cast<SemanticToken?>().FirstOrDefault();
        }
        string Describe(SemanticToken? t) => t is { } x ? $"{text.Substring(x.Offset, x.Length)}:{x.Kind}:{x.Modifiers}" : "none";
        var arrayToken = TokenAt("Array.Sort");
        var sortToken = TokenAt("Array.Sort", 6);
        var listToken = TokenAt("List<Item> items");
        var itemToken = TokenAt("List<Item> items", 5);
        var totalToken = TokenAt("item.Total(10)", 5);
        var localToken = TokenAt("total += item");
        var namespaceToken = TokenAt("import System.IO", 14);
        Check("tokens: Array is a library class, Sort a static method", arrayToken is { Kind: SemanticKind.Class, Modifiers: SemanticModifiers.DefaultLibrary }
            && sortToken is { Kind: SemanticKind.Method } s && s.Modifiers.HasFlag(SemanticModifiers.Static), $"{Describe(arrayToken)} {Describe(sortToken)}");
        Check("tokens: List a library class, Item the program's class", listToken is { Kind: SemanticKind.Class, Modifiers: SemanticModifiers.DefaultLibrary }
            && itemToken is { Kind: SemanticKind.Class, Modifiers: SemanticModifiers.None }, $"{Describe(listToken)} {Describe(itemToken)}");
        Check("tokens: a method, a local, a namespace", totalToken is { Kind: SemanticKind.Method } && localToken is { Kind: SemanticKind.Variable }
            && namespaceToken is { Kind: SemanticKind.Namespace }, $"{Describe(totalToken)} {Describe(localToken)} {Describe(namespaceToken)}");
        var constToken = TokenAt("const i32 extra");
        Check("tokens: `const` is a keyword (a word the grammar may not see as one)", constToken is { Kind: SemanticKind.Keyword, Length: 5 }, Describe(constToken));
        Check("tokens: in order, none overlapping", analysis.Tokens.Zip(analysis.Tokens.Skip(1)).All(p => p.First.Offset + p.First.Length <= p.Second.Offset));

        var outline = analysis.Outline;
        Check("outline: namespace Shop > Item (fields, new, Total), Main", outline is [{ Name: "Shop", Children: [{ Name: "Item", Children.Count: 4 }, { Name: "Main" }] }],
            string.Join(", ", outline.SelectMany(o => o.Children).Select(c => $"{c.Name}({c.Children.Count})")));

        // While typing an unfinished call: `Array.Sort(prices, ` with nothing after it.
        edited = text.Replace("Array.Sort(prices, (a, b) => a - b);", "Array.Sort(prices, ");
        (source, offset) = At(edited, "Array.Sort(prices, ");
        analysis = workspace.Analyze(source);
        signatures = service.Signatures(analysis, offset);
        Check("signature help in an unfinished call", signatures is { ActiveParameter: 1 } && signatures.Signatures.Count >= 2,
            signatures is null ? "none" : string.Join(" | ", signatures.Signatures.Select(s => s.Label)));
        Check("problems of an unfinished call are reported", analysis.Problems.Count > 0);
        list = service.Complete(analysis, offset);
        Check("completion inside the call's arguments: locals", list.Items.Any(i => i.Label == "prices"), string.Join(", ", list.Items.Take(6).Select(i => i.Label)));
        list = service.Complete(analysis, text.IndexOf("/// A product", StringComparison.Ordinal) + 6);
        Check("nothing in a comment", list.Items.Count == 0);
    }

    private void Speed(H.HirAssembly core, byte[] bytes)
    {
        double Best(int runs, Action action)
        {
            double best = double.MaxValue;
            for (int i = 0; i < runs; i++)
            {
                var watch = Stopwatch.StartNew();
                action();
                best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
            }
            return best;
        }
        double read = Best(5, () => H.Encoding.LvaFormat.Read(bytes));
        HirCatalog? catalog = null;
        double make = Best(5, () => catalog = HirCatalog.Create(H.Encoding.LvaFormat.Read(bytes)));
        double search = Best(20, () => catalog!.Search("str"));
        var workspace = new AntrilWorkspace([core]);
        string text = Document.Replace("\r\n", "\n").Replace("i32 total = 0;", "i32 total = 0;\n                items.");
        var source = new SourceText(text, "shop.ant");
        int offset = text.IndexOf("items.\n", StringComparison.Ordinal) + 6;
        workspace.Analyze(source);
        AntrilAnalysis? analysis = null;
        double analyze = Best(10, () => analysis = workspace.Analyze(source));
        double complete = Best(20, () => workspace.Service.Complete(analysis!, offset));
        double hover = Best(20, () => workspace.Service.Hover(analysis!, offset - 3));
        // A bigger document: the document's Main repeated 100 times (about 1.600 lines).
        var big = new System.Text.StringBuilder(Document.Replace("\r\n", "\n"));
        string main = Document.Replace("\r\n", "\n");
        int mainStart = main.IndexOf("    public Main()", StringComparison.Ordinal);
        int mainEnd = main.LastIndexOf('}');
        string body = main[mainStart..mainEnd];
        for (int i = 0; i < 100; i++)
            big.Insert(big.ToString().LastIndexOf('}'), body.Replace("Main()", $"Main{i}()"));
        var bigSource = new SourceText(big.ToString(), "big.ant");
        double analyzeBig = Best(5, () => workspace.Analyze(bigSource));
        int lines = bigSource.Text.Count(c => c == '\n');
        Check("analysis fast enough for typing (a small document under 50 ms)", analyze < 50, $"{analyze:0.0} ms");
        Check("completion under 5 ms", complete < 5, $"{complete:0.00} ms");
    }

    private void Extensions(H.HirAssembly core)
    {
        var workspace = new AntrilWorkspace([core]);
        // Extension methods: completion after a value of the extended type, and `ext(...)` blocks in listings and decompiled types.
        string extended = "import System;\n\ninto namespace Shop\n{\n    public value Pt\n    {\n        public:\n            i32 X;\n            i32 Y;\n            new(i32 x, i32 y);\n    }\n\n    ext(i32 n)\n    {\n        public:\n            /// Twice the number.\n            Twice() : i32 => n * 2;\n    }\n\n    ext(Pt p)\n    {\n        public:\n            Sum() : i32 => p.X + p.Y;\n    }\n\n    static ext(i32)\n    {\n        public:\n            Answer() : i32 => 42;\n    }\n\n    Main() : i32\n    {\n        i32 k = 3;\n        Pt pt = new Pt(1, 2);\n        return k.Twice() + pt.Sum() + i32.Answer();\n    }\n}\n";
        var extSource = new SourceText(extended.Replace("return k.Twice()", "k.\n        return k.Twice()"), "ext.ant");
        var extAnalysis = workspace.Analyze(extSource);
        var extList = workspace.Service.Complete(extAnalysis, extSource.Text.IndexOf("k.\n", StringComparison.Ordinal) + 2);
        var twice = extList.Items.FirstOrDefault(i => i.Label == "Twice");
        Check("`k.` (an i32) offers the extension `Twice()` with its doc", twice is { Kind: CompletionKind.Method, Detail: "Twice() : i32", Doc: "Twice the number." },
            twice is null ? string.Join(", ", extList.Items.Take(8).Select(i => i.Label)) : $"{twice.Detail} / {twice.Doc}");
        Check("... not the static extension `Answer`", !extList.Items.Any(i => i.Label == "Answer"));
        extSource = new SourceText(extended.Replace("return k.Twice()", "i32.\n        return k.Twice()"), "ext.ant");
        extAnalysis = workspace.Analyze(extSource);
        extList = workspace.Service.Complete(extAnalysis, extSource.Text.IndexOf("i32.\n", StringComparison.Ordinal) + 4);
        Check("`i32.` offers the static extension `Answer()`", extList.Items.Any(i => i.Label == "Answer" && i.Detail == "Answer() : i32"), string.Join(", ", extList.Items.Take(8).Select(i => i.Label)));
        // A value in brackets: `(a + a).` offers its members (whether the code around parses or not).
        string bracketed = "import System;\n\ninto namespace Shop\n{\n    public value V2 { public: f32 x, y; new(f32 x, f32 y); op +(V2 a, V2 b) : V2 => new V2(a.x + b.x, a.y + b.y); }\n    Use(V2 a) : f32\n    {\n        f32 k = (a + a).x;\n        (a + a).\n        return k;\n    }\n}\n";
        var bracketAnalysis = workspace.Analyze(new SourceText(bracketed, "brackets.ant"));
        List<string> AfterBracket(string what, int nth) => [.. workspace.Service.Complete(bracketAnalysis, bracketed.IndexOf(what, bracketed.IndexOf(what, StringComparison.Ordinal) + nth, StringComparison.Ordinal) + what.Length).Items.Select(i => i.Label)];
        var inUse = AfterBracket("(a + a).", 0);
        var typing = AfterBracket("(a + a).", 1);
        Check("`(a + a).` offers the value's fields, written or being typed", inUse.Contains("x") && inUse.Contains("y") && typing.Contains("x") && typing.Contains("y"), string.Join(", ", inUse) + " / " + string.Join(", ", typing));
        // Extensions of an interface or a base class, and generic ones constrained to an interface: on every type that is one.
        string kinds = "import System;\n\ninto namespace Shop\n{\n    public interface IShape { Area() : i32; }\n    public class Animal { }\n    public class Dog : Animal { }\n    public class Sq : IShape { public: Area() : i32 => 4; }\n    ext(IShape s) { public: Half() : i32 => s.Area() / 2; }\n    ext<T : IShape>(T s) { public: Twice() : i32 => s.Area() * 2; }\n    ext(Animal a) { public: Name() : str => \"a\"; }\n    Use(Sq q, IShape i, Dog d) : i32\n    {\n        q.\n        i.\n        d.\n        return 0;\n    }\n}\n";
        var kindsSource = new SourceText(kinds, "kinds.ant");
        var kindsAnalysis = workspace.Analyze(kindsSource);
        List<string> After(string what) => [.. workspace.Service.Complete(kindsAnalysis, kinds.IndexOf(what, StringComparison.Ordinal) + 2).Items.Select(i => i.Label)];
        var onSq = After("q.\n");
        Check("a class that implements IShape offers `ext(IShape)` and `ext<T : IShape>` methods", onSq.Contains("Half") && onSq.Contains("Twice") && onSq.Contains("Area"), string.Join(", ", onSq));
        var onShape = After("i.\n");
        Check("an IShape offers its own methods and `ext(IShape)`, not the generic one it does not meet", onShape.Contains("Area") && onShape.Contains("Half") && !onShape.Contains("Twice"), string.Join(", ", onShape));
        var onDog = After("d.\n");
        Check("a derived class offers its base class's extension", onDog.Contains("Name"), string.Join(", ", onDog));
        var extCompiled = AntrilCompiler.Compile([new SourceText(extended, "ext.ant")], [core], "ext");
        Check("the extension program compiles", extCompiled.Success, string.Join("; ", extCompiled.Diagnostics.Take(3)));
        if (extCompiled.Success)
        {
            var extCatalog = HirCatalog.Create(extCompiled.Assembly!, core);
            string listing = AntrilView.Instance.Listing(extCatalog, "Shop");
            Check("the namespace listing shows `ext(i32 n)`, `ext(Pt p)` and `static ext(i32)`", listing.Contains("  ext(i32 n)\n") && listing.Contains("  ext(Pt p)\n") && listing.Contains("  static ext(i32)\n"), listing);
            string pt = AntrilView.Instance.Decompile(extCatalog.Find("Shop.Pt")!);
            Check("`show Shop.Pt` carries its `ext(Pt p)` block, receiver dropped", pt.Contains("\text(Pt p)\n\t{\n") && pt.Contains("Sum() : i32") && !pt.Contains("static Sum") && !pt.Contains("$ext"), pt);
        }

    }
}
