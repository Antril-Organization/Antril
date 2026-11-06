using System.Globalization;
using System.Text;
using Antril.Syntax;
using Larva.Hir;
using Larva.Hir.Services;
using H = Larva.Hir;

namespace Antril.Services
{
    /// <summary>
    /// Antril's view of a Larva catalog: types and signatures as Antril writes them (<c>str</c>, <c>func&lt;A&gt; : R</c>,
    /// <c>Name(i32 x) : R</c>, <c>public:</c> sections), and any type of any assembly decompiled to Antril source from its
    /// HIR.
    /// </summary>
    public sealed class AntrilView : LanguageView
    {
        public static readonly AntrilView Instance = new();

        public override string Name => "antril";
        public override string Extension => ".ant";

        private static readonly string[] contextual =
            ["value", "record", "final", "base", "var", "private", "file", "extern", "unsafe", "when", "and", "or", "not", "delete", "func", "str", "any", "this", "const", "from", "nameof",
                "attribute", "exception"];

        private static readonly string[] keywords = [.. Lexer.Keywords.Keys.Concat(contextual).Distinct().Order(StringComparer.Ordinal)];

        public override IReadOnlyList<string> Keywords => keywords;

        // ── Marks (what each name of a decompiled source is) ─

        /// <summary>
        /// While a type is decompiled for the editor (<see cref="DecompileMarked"/>), each name is written between marks
        /// (<c>\u0001id\u0002name\u0003</c>) that record what it is; taken out at the end, they give the spans the editor
        /// colors and the links it follows. Elsewhere (hovers, completion) names are written as they are.
        /// </summary>
        [ThreadStatic]
        private static List<DecompiledMark>? marks;

        private const char MarkOpen = '\u0001', MarkMiddle = '\u0002', MarkClose = '\u0003';

        internal static string Mark(string name, SemanticKind kind, SemanticModifiers modifiers = SemanticModifiers.None, CatalogType? type = null, CatalogMember? member = null, bool colored = true)
        {
            if (marks is null || name.Length == 0)
                return name;
            marks.Add(new DecompiledMark(kind, modifiers, type, member, colored));
            return $"{MarkOpen}{marks.Count - 1}{MarkMiddle}{name}{MarkClose}";
        }

        /// <summary>A type's name as a decompiled source shows it (its kind, a link to it).</summary>
        internal static string MarkType(string name, CatalogType type, bool declaration = false) =>
            Mark(name, Semantic.Of(type), SemanticModifiers.DefaultLibrary | (declaration ? SemanticModifiers.Declaration : 0), type);

        /// <summary>A keyword that names a type (<c>i32</c>, <c>str</c>): a link to the type, colored as the keyword it is.</summary>
        internal static string MarkKeyword(string keyword, CatalogType? type) =>
            type is null ? keyword : Mark(keyword, Semantic.Of(type), SemanticModifiers.DefaultLibrary, type, colored: false);

        /// <summary>A member's name as a decompiled source shows it (its kind and modifiers, a link to it).</summary>
        internal static string MarkMember(string name, CatalogMember? member, SemanticKind fallback, bool declaration = false) =>
            member is null ? Mark(name, fallback, declaration ? SemanticModifiers.Declaration : 0)
                : Mark(name, Semantic.Of(member), Semantic.Modifiers(member, null!) | (declaration ? SemanticModifiers.Declaration : 0), member.Type, member);

        /// <summary>Text without its marks (what it measures once written).</summary>
        internal static string Unmarked(string text) => marks is null || text.IndexOf(MarkOpen) < 0 ? text : Strip(text, null);

        private static string Strip(string text, List<DecompiledSpan>? spans)
        {
            var output = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != MarkOpen)
                {
                    output.Append(text[i]);
                    continue;
                }
                int middle = text.IndexOf(MarkMiddle, i), close = text.IndexOf(MarkClose, middle);
                int id = int.Parse(text.AsSpan(i + 1, middle - i - 1), CultureInfo.InvariantCulture);
                string name = text[(middle + 1)..close];
                if (spans is not null && marks is not null && id < marks.Count)
                {
                    var mark = marks[id];
                    spans.Add(new DecompiledSpan(output.Length, name.Length, mark.Kind, mark.Modifiers, mark.Type, mark.Member, mark.Colored));
                }
                output.Append(name);
                i = close;
            }
            return output.ToString();
        }

        /// <summary>A type decompiled with what each of its names is: the spans to color, and the types and members they name.</summary>
        public (string Text, IReadOnlyList<DecompiledSpan> Spans) DecompileMarked(CatalogType type, DecompileOptions? options = null)
        {
            var saved = marks;
            marks = [];
            try
            {
                string text = Decompile(type, options);
                var spans = new List<DecompiledSpan>();
                return (Strip(text, spans), spans);
            }
            finally
            {
                marks = saved;
            }
        }

        // ── Types ──────────────────────────────────────────

        public override string Type(H.HirType type, HirCatalog catalog, GenericNames names) => Type(type, catalog, names, null);

        private string Type(H.HirType type, HirCatalog catalog, GenericNames names, ISet<string>? imports) => type switch
        {
            H.PrimitiveType p => p.Kind == H.Primitive.Void ? "void" : MarkKeyword(p.Kind == H.Primitive.Char ? "char" : p.Kind.Keyword(), catalog.PrimitiveType(p.Kind)),
            H.NamedType n => Named(n, catalog, names, imports),
            H.ArrayType a => $"{Type(a.Element, catalog, names, imports)}[{new string(',', a.Rank - 1)}]",
            H.InlineArrayType i => $"{Type(i.Element, catalog, names, imports)}[{(i.SizeOf is null ? i.Count.ToString() : (i.Count == 1 ? "" : $"{i.Count} * ") + $"sizeof({Type(i.SizeOf, catalog, names, imports)})")}]",
            H.NullableType n => Type(n.Inner, catalog, names, imports) + "?",
            H.ReferenceType r => (r.Mutable ? "&mut " : "&") + Type(r.Target, catalog, names, imports),
            H.PointerType p => Type(p.Target, catalog, names, imports) + "*",
            H.FuncType f => Func("func", f.Parameters, f.Result, catalog, names, imports),
            H.NativeFuncType f => Func("func*", f.Parameters, f.Result, catalog, names, imports),
            H.GenericParamType g => Mark(names.Name(g), SemanticKind.TypeParameter),
            H.PackExpansionType e => Type(e.Pattern, catalog, names, imports) + "...",
            _ => "?",
        };

        private string Func(string word, H.HirType[] parameters, H.HirType result, HirCatalog catalog, GenericNames names, ISet<string>? imports)
        {
            var text = new StringBuilder(word);
            if (parameters.Length > 0)
                text.Append('<').Append(string.Join(", ", parameters.Select(p => Type(p, catalog, names, imports)))).Append('>');
            if (result != H.HirType.Void)
            {
                // A func type as a result is bracketed so the colons read one way.
                string written = Type(result, catalog, names, imports);
                text.Append(" : ").Append(written);
            }
            return text.ToString();
        }

        private string Named(H.NamedType n, HirCatalog catalog, GenericNames names, ISet<string>? imports)
        {
            if (catalog.TypeOf(n) is not { } type)
                return "?";
            if (type.Primitive is { } primitive)
                return MarkKeyword(primitive.Keyword(), type);
            switch (type.FullName)
            {
                case "System.String" when n.Arguments.Length == 0 || n.Arguments is [H.PrimitiveType { Kind: H.Primitive.U8 }]:
                    return MarkKeyword("str", type);
                case "System.Any" when n.Arguments.Length == 0:
                    return MarkKeyword("any", type);
                case "System.ValueTuple" when n.Arguments.Length >= 2:
                    return $"({string.Join(", ", n.Arguments.Select(a => Type(a, catalog, names, imports)))})";
            }
            if (type.Namespace.Length > 0)
                imports?.Add(type.Namespace);
            string marked = MarkType(type.Name, type);
            return n.Arguments.Length == 0 ? marked : $"{marked}<{string.Join(", ", n.Arguments.Select(a => Type(a, catalog, names, imports)))}>";
        }

        public override string KindWord(CatalogType type)
        {
            if (type.IsModule)
                return "namespace";
            return type.Definition.Kind switch
            {
                H.TypeKind.Interface => "interface",
                H.TypeKind.Enum => "enum",
                H.TypeKind.Bitflags => "bitflag enum",
                H.TypeKind.Struct => (type.Definition.Flags & H.TypeFlags.Record) != 0 ? "record value" : "value",
                H.TypeKind.Func => "func",
                _ => type.IsStatic ? "static class"
                    : (type.Definition.Flags & H.TypeFlags.Record) != 0 ? "record class"
                    : type.IsAbstract ? "base class"
                    : Declared(type) is { } word ? (type.IsFinal ? "final " : "") + word.Word
                    : type.IsFinal ? "final class"
                    : "class",
            };
        }

        /// <summary>
        /// A class written with <c>attribute</c> or <c>exception</c>: derived from System.Attribute / System.Exception, its name
        /// ending in Attribute / Exception (written without it).
        /// </summary>
        private static (string Word, string Ending)? Declared(CatalogType type)
        {
            foreach (var (word, ending) in new[] { ("attribute", "Attribute"), ("exception", "Exception") })
            {
                if (type.Name.Length > ending.Length && type.Name.EndsWith(ending, StringComparison.Ordinal) && type.FullName != "System." + ending
                    && type.BaseChain().Any(b => b.FullName == "System." + ending))
                    return (word, ending);
            }
            return null;
        }

        private static string AccessWord(H.Access access) => access switch
        {
            H.Access.Public => "public",
            H.Access.Internal => "internal",
            H.Access.Friendly => "friendly",
            H.Access.Inherited => "inherited",
            H.Access.File => "file",
            _ => "private",
        };

        public override string Header(CatalogType type) => Header(type, null);

        /// <summary>Set while an extension block is written: its methods drop the receiver parameter and `static`.</summary>
        [ThreadStatic]
        private static bool receiverless;

        public override string ExtensionSignature(CatalogMember member)
        {
            bool was = receiverless;
            receiverless = !member.Type.IsStatic;
            try
            {
                return Signature(member, null, null, withModifiers: false);
            }
            finally
            {
                receiverless = was;
            }
        }

        /// <summary>The receiver's name in an extension block (the first parameter of its methods).</summary>
        private static string ReceiverName(CatalogType extension) =>
            extension.Declared.FirstOrDefault(m => m.Kind == CatalogMemberKind.Method && m.Parameters.Count > 0)?.Parameters[0].Name ?? "value";

        private string Header(CatalogType type, ISet<string>? imports)
        {
            var catalog = type.Catalog;
            var names = GenericNames.Of(type);
            var text = new StringBuilder();
            if (type.IsExtension && type.Extended is { } extended)
            {
                // `ext(i32 n)`, `static ext(i32)`, `ext<T : bitflag enum>(T flags)`.
                text.Append(type.IsStatic ? "static ext" : "ext");
                Generics(text, type.Definition.Generics.Select(g => g with { Types = [.. g.Types.Select(type.Global)], OneOf = [.. g.OneOf.Select(type.Global)] }).ToList(), catalog, names, imports);
                text.Append('(').Append(Type(extended, catalog, names, imports));
                if (!type.IsStatic)
                    text.Append(' ').Append(ReceiverName(type));
                return text.Append(')').ToString();
            }
            if (type.Access != H.Access.Internal)
                text.Append(AccessWord(type.Access)).Append(' ');
            // `attribute Tag` for TagAttribute (System.Attribute as its base goes unsaid), `exception NotFound` likewise.
            var declared = type.IsAbstract || type.IsStatic || (type.Definition.Flags & H.TypeFlags.Record) != 0 ? null : Declared(type);
            text.Append(KindWord(type)).Append(' ').Append(MarkType(declared is { } d ? type.Name[..^d.Ending.Length] : type.Name, type, declaration: true));
            Generics(text, type.Definition.Generics.Select(g => g with { Types = [.. g.Types.Select(type.Global)], OneOf = [.. g.OneOf.Select(type.Global)] }).ToList(), catalog, names, imports);
            var bases = new List<string>();
            if (type.BaseType is { } baseType && type.Base?.FullName != "System.Any" && !(declared is { } implied && type.Base?.FullName == "System." + implied.Ending))
                bases.Add(Type(baseType, catalog, names, imports));
            bases.AddRange(type.InterfaceTypes.Select(i => Type(i, catalog, names, imports)));
            if (type.IsEnum && type.Definition.Underlying is { } underlying && underlying != H.HirType.I32)
                bases.Insert(0, Type(type.Global(underlying), catalog, names, imports));
            if (bases.Count > 0)
                text.Append(" : ").Append(string.Join(", ", bases));
            return text.ToString();
        }

        private void Generics(StringBuilder text, IReadOnlyList<H.GenericParam> generics, HirCatalog catalog, GenericNames names, ISet<string>? imports)
        {
            if (generics.Count == 0)
                return;
            text.Append('<');
            for (int g = 0; g < generics.Count; g++)
            {
                if (g > 0)
                    text.Append(", ");
                var generic = generics[g];
                text.Append(Mark(generic.Name, SemanticKind.TypeParameter, SemanticModifiers.Declaration));
                // `T : attribute`, `T : exception`: the words for System.Attribute and System.Exception.
                var constraints = generic.Types.Select(t => catalog.TypeOf(t)?.FullName switch
                {
                    "System.Attribute" => "attribute",
                    "System.Exception" => "exception",
                    _ => Type(t, catalog, names, imports),
                }).ToList();
                if (generic.OneOf.Length > 0)
                    constraints.Insert(0, "[" + string.Join(", ", generic.OneOf.Select(t => Type(t, catalog, names, imports))) + "]");
                // `enum` and `bitflag enum` are words that imply their operators: written as the word, the operators left out.
                bool bitflags = (generic.Kinds & H.ConstraintKinds.Bitflags) != 0, isEnum = (generic.Kinds & H.ConstraintKinds.Enum) != 0;
                if (bitflags || isEnum)
                    constraints.Add(bitflags ? "bitflag enum" : "enum");
                var implied = bitflags ? ImpliedByBitflags : isEnum ? ImpliedByEnum : [];
                foreach (H.ConstraintKinds kind in Enum.GetValues<H.ConstraintKinds>())
                    if (kind is not (H.ConstraintKinds.None or H.ConstraintKinds.Enum or H.ConstraintKinds.Bitflags or H.ConstraintKinds.Object) && (generic.Kinds & kind) != 0)
                        if (kind != H.ConstraintKinds.FixedSize || generic.FixedSize == 0)
                            constraints.Add(kind == H.ConstraintKinds.New ? "new()" : kind.ToString().ToLowerInvariant());
                foreach (var target in generic.ConvertsTo)
                    constraints.Add($"({Type(target, catalog, names, imports)}){generic.Name}");
                foreach (var constructor in generic.Constructors)
                    constraints.Add($"new({string.Join(", ", constructor.Select(t => Type(t, catalog, names, imports)))})");
                if (generic.FixedSize > 0)
                    constraints.Add($"fixed({generic.FixedSize})");
                foreach (var op in generic.Operators)
                {
                    if (implied.Contains(op.Symbol))
                        continue;
                    bool postfix = op.Symbol.EndsWith(".postfix", StringComparison.Ordinal);
                    string symbol = postfix ? op.Symbol[..^".postfix".Length] : op.Symbol;
                    constraints.Add($"{(postfix ? "right " : op.Operands.Length == 1 ? "left " : "")}op {symbol}({string.Join(", ", op.Operands.Select(t => Type(t, catalog, names, imports)))}) : {Type(op.Result, catalog, names, imports)}");
                }
                if (constraints.Count > 0)
                    text.Append(" : ").Append(string.Join(", ", constraints));
            }
            text.Append('>');
        }

        private static readonly HashSet<string> ImpliedByEnum = ["==", "!="];
        private static readonly HashSet<string> ImpliedByBitflags = ["==", "!=", "&", "|", "^", "~"];

        // ── Members ────────────────────────────────────────

        /// <summary>
        /// <c> { get; set; }</c>; an interface's required accessors as <c>base get;</c>, its defaults (accessors with a
        /// body) as <c>get => …;</c>.
        /// </summary>
        private static string Accessors(CatalogMember member)
        {
            var methods = member.Type.Definition.Methods;
            string Accessor(int index, string word)
            {
                if (index < 0)
                    return "";
                if ((methods[index].Flags & H.MethodFlags.Abstract) != 0)
                    return $"base {word}; ";
                return member.Type.IsInterface && methods[index].Body is not null ? $"{word} => …; " : $"{word}; ";
            }
            var p = member.Property;
            return p is null ? " { get; }" : $" {{ {Accessor(p.Getter, "get")}{Accessor(p.Setter, "set")}}}";
        }

        public override string Local(string name, H.HirType type, HirCatalog catalog, bool parameter) =>
            $"{Type(type, catalog, GenericNames.None)} {name}";

        public override string Parameter(H.Parameter parameter, HirCatalog catalog, GenericNames names) =>
            $"{Type(parameter.Type, catalog, names)} {parameter.Name}";

        public override string Signature(CatalogMember member, MemberView? view = null) => Signature(member, view, null, withModifiers: true);

        private string Signature(CatalogMember member, MemberView? view, ISet<string>? imports, bool withModifiers)
        {
            var catalog = member.Type.Catalog;
            var names = GenericNames.Of(member.Type, member);
            var value = view?.ValueType ?? member.ValueType;
            var parameters = view?.Parameters ?? [.. member.Parameters];
            if (receiverless && member.Kind == CatalogMemberKind.Method && parameters.Length > 0)
                parameters = parameters[1..];
            var text = new StringBuilder();
            if (withModifiers)
                Modifiers(text, member);
            string Params() => string.Join(", ", parameters.Select(p =>
                string.Concat(p.Attributes.Select(a => Attribute(member.Type, a, imports) + " "))
                + (p.Flags.HasFlag(H.ParameterFlags.Params) ? "params " : "") + $"{Type(p.Type, catalog, names, imports)} {Mark(p.Name, SemanticKind.Parameter, SemanticModifiers.Declaration)}"
                + (p.Default is { } d ? " = " + Constant(d, member.Type, names, imports) : "")));
            switch (member.Kind)
            {
                case CatalogMemberKind.Constructor:
                    text.Append("new(").Append(Params()).Append(')');
                    break;
                case CatalogMemberKind.Conversion:
                    text.Append("op (").Append(Type(value, catalog, names, imports)).Append(')');
                    Generics(text, member.Generics, catalog, names, imports);
                    text.Append('(').Append(Params()).Append(')');
                    break;
                case CatalogMemberKind.Method or CatalogMemberKind.Operator:
                {
                    var declared = member.Kind == CatalogMemberKind.Operator ? member.Type.Definition.Operators.Where(o => o.Method == member.Index).ToList() : [];
                    string head = MarkMember(member.Name, member, SemanticKind.Method, declaration: true);
                    if (declared.Count > 0)
                    {
                        bool prefix = declared.Any(o => o.Fixity == H.Fixity.Prefix), postfix = declared.Any(o => o.Fixity == H.Fixity.Postfix);
                        bool builtinPrefix = prefix && !postfix && OperatorTable.BuiltinPrefix.Contains(declared[0].Symbol);
                        head = (prefix && postfix || builtinPrefix || declared[0].Fixity == H.Fixity.Binary ? "" : prefix ? "left " : "right ") + "op " + declared[0].Symbol;
                    }
                    else if (member.Kind == CatalogMemberKind.Operator && Syntax.Parser.OperatorSymbol(member.Name) is { } symbol)
                        head = "op " + symbol;
                    text.Append(head);
                    Generics(text, member.Generics, catalog, names, imports);
                    text.Append('(').Append(Params()).Append(')');
                    if (value != H.HirType.Void)
                        text.Append(" : ").Append(Type(value, catalog, names, imports));
                    if (declared.FirstOrDefault(o => o.Fixity == H.Fixity.Binary) is { } binary && !OperatorTable.BuiltinBinary.Contains(binary.Symbol))
                    {
                        string level = OperatorTable.Levels.FirstOrDefault(l => l.Value == binary.Precedence).Key is { } known ? "PrecedenceLevel." + known : binary.Precedence.ToString(CultureInfo.InvariantCulture);
                        text.Append(" precedence(").Append(level).Append(')');
                        if (binary.Associativity != H.Associativity.Left)
                            text.Append(" associativity(Associativity.").Append(binary.Associativity).Append(')');
                    }
                    break;
                }
                case CatalogMemberKind.Property:
                    text.Append(Type(value, catalog, names, imports)).Append(' ').Append(MarkMember(member.Name, member, SemanticKind.Property, declaration: true)).Append(Accessors(member));
                    break;
                case CatalogMemberKind.Indexer:
                    text.Append(Type(value, catalog, names, imports)).Append(" this[").Append(Params()).Append(']').Append(Accessors(member));
                    break;
                case CatalogMemberKind.EnumValue:
                    text.Append(MarkMember(member.Name, member, SemanticKind.EnumMember, declaration: true));
                    if (EnumMemberValue(member, imports) is { } written)
                        text.Append(" = ").Append(written);
                    else if (member.Field?.Value is { } enumValue)
                        text.Append(" = ").Append(Constant(enumValue, member.Type, names, imports, bare: true));
                    break;
                default:
                    text.Append(Type(value, catalog, names, imports)).Append(' ').Append(MarkMember(member.Name, member, SemanticKind.Field, declaration: true));
                    if (member.Field?.Value is { } constant)
                        text.Append(" = ").Append(Constant(constant, member.Type, names, imports));
                    break;
            }
            return text.ToString();
        }

        /// <summary>
        /// What a member of a value-based enum (<c>enum Color : Rgb</c>) stands for, as written: the arm of the enum's
        /// <c>$Value</c> for that member (<c>new Rgb(255, 0, 0)</c>). Null for other enums.
        /// </summary>
        private string? EnumMemberValue(CatalogMember member, ISet<string>? imports)
        {
            var def = member.Type.Definition;
            if (def.Underlying is not H.NamedType || member.Field?.Value is not H.EnumConstant constant
                || def.Methods.FirstOrDefault(m => m.Name == "$Value" && m.IsStatic) is not { Body: { } body }
                || body.Block.Statements is not [H.ReturnStmt { Value: H.SwitchExpr choice }])
                return null;
            var arm = choice.Arms.FirstOrDefault(a => a.Pattern is H.ConstantPattern { Value: H.EnumConstant c } && c.Value == constant.Value);
            return arm is null ? null : new BodyPrinter(this, member.Type, member, body, imports ?? new HashSet<string>()).Expression(arm.Value);
        }

        private static void Modifiers(StringBuilder text, CatalogMember member)
        {
            if (member.Kind == CatalogMemberKind.EnumValue)
                return;
            if (member.IsStatic && !member.Type.IsStatic && !member.Type.IsModule && !receiverless && member.Kind is not (CatalogMemberKind.Constant or CatalogMemberKind.Operator or CatalogMemberKind.Conversion))
                text.Append("static ");
            if (member.Kind == CatalogMemberKind.Constant)
                text.Append("const ");
            if (member.Field is { } field && (field.Flags & H.FieldFlags.Readonly) != 0)
                text.Append("readonly ");
            if (member.Field is { } fixedField && (fixedField.Type is H.InlineArrayType || (fixedField.Flags & H.FieldFlags.InPlace) != 0))
                text.Append("fixed ");
            if (member.Method is { } method)
            {
                // Interface methods and abstract ones (bodiless in a base class) say nothing: having no body says it.
                bool implicitVirtual = member.Type.IsInterface || (method.Flags & H.MethodFlags.Abstract) != 0;
                if ((method.Flags & H.MethodFlags.Override) != 0)
                    text.Append("override ");
                else if ((method.Flags & H.MethodFlags.Virtual) != 0 && !implicitVirtual)
                    text.Append("virtual ");
                if ((method.Flags & H.MethodFlags.Async) != 0)
                    text.Append("async ");
                if ((method.Flags & H.MethodFlags.Unsafe) != 0)
                    text.Append("unsafe ");
                if ((method.Flags & H.MethodFlags.Extern) != 0)
                    text.Append("extern(").Append(Quote(method.ExternName ?? method.Name)).Append(") ");
            }
            else if (member.Property is { Getter: >= 0 } property)
            {
                // A virtual property: its accessors carry the flags.
                var getter = member.Type.Definition.Methods[property.Getter];
                if ((getter.Flags & H.MethodFlags.Override) != 0)
                    text.Append("override ");
                else if ((getter.Flags & H.MethodFlags.Virtual) != 0 && !member.Type.IsInterface)
                    text.Append("virtual "); // an interface's properties are virtual without saying it
            }
        }

        // ── Decompiling ────────────────────────────────────

        public override string Decompile(CatalogType type, DecompileOptions? options = null)
        {
            options ??= DecompileOptions.Default;
            var imports = new SortedSet<string>(StringComparer.Ordinal);
            var body = new StringBuilder();
            const string indent = "\t";
            if (type.IsModule)
            {
                foreach (var member in type.Declared)
                {
                    if (options.NonPublic || member.Access == H.Access.Public)
                        MemberSource(body, member, indent, options, imports, sectioned: false);
                }
            }
            else
            {
                TypeSource(body, type, indent, options, imports);
                // The type's own extension blocks (exact ones, declared in its namespace) go with it.
                foreach (var extension in type.Catalog.ExtensionsOf(type.SelfType))
                {
                    if (extension.Extended is H.GenericParamType || extension.Namespace != type.Namespace || extension == type)
                        continue;
                    body.Append('\n');
                    TypeSource(body, extension, indent, options, imports);
                }
            }
            var text = new StringBuilder();
            text.Append("// ").Append(type.IsModule ? $"{type.Namespace}: functions and fields" : type.FullName)
                .Append(" — ").Append(type.Assembly.Name).Append(" (decompiled from Larva HIR)\n\n");
            imports.Remove(type.Namespace);
            foreach (var ns in imports)
                text.Append("import ").Append(Mark(ns, SemanticKind.Namespace)).Append(";\n");
            if (imports.Count > 0)
                text.Append('\n');
            text.Append("into namespace ").Append(Mark(type.Namespace.Length == 0 ? "Global" : type.Namespace, SemanticKind.Namespace, SemanticModifiers.Declaration)).Append("\n{\n");
            text.Append(body);
            text.Append("}\n");
            return text.ToString();
        }

        private void TypeSource(StringBuilder text, CatalogType type, string indent, DecompileOptions options, ISet<string> imports)
        {
            if (type.IsExtension)
            {
                bool was = receiverless;
                receiverless = !type.IsStatic;
                try
                {
                    TypeBody(text, type, indent, options, imports);
                }
                finally
                {
                    receiverless = was;
                }
                return;
            }
            TypeBody(text, type, indent, options, imports);
        }

        private void TypeBody(StringBuilder text, CatalogType type, string indent, DecompileOptions options, ISet<string> imports)
        {
            Doc(text, options.Docs ? type.Doc : null, indent);
            if (type.Primitive is { } primitive)
                text.Append(indent).Append("[Primitive(\"").Append(primitive.Keyword()).Append("\")]\n");
            foreach (var attribute in type.Definition.Attributes)
                text.Append(indent).Append(Attribute(type, attribute, imports)).Append('\n');
            text.Append(indent).Append(Header(type, imports));
            if (type.IsEnum)
            {
                var values = type.Declared.Where(m => m.Kind == CatalogMemberKind.EnumValue).ToList();
                bool texts = values.Any(m => m.Field?.Value is H.EnumConstant { Text: not null });
                bool plain = !texts && values.Select((m, i) => m.Field?.Value is H.EnumConstant { Value: var v } && v == (type.Definition.Kind == H.TypeKind.Bitflags ? 1L << i : i)).All(x => x);
                bool valued = type.Definition.Underlying is H.NamedType && values.Any(v => EnumMemberValue(v, imports) is not null);
                text.Append(" { ").Append(string.Join(", ", values.Select(v =>
                    string.Concat((v.Field?.Attributes ?? []).Select(a => Attribute(type, a, imports) + " "))
                    + (!valued && (plain || v.Field?.Value is H.EnumConstant { Text: { } t } && t == v.Name) ? MarkMember(v.Name, v, SemanticKind.EnumMember, declaration: true) : Signature(v, null, imports, false))))).Append(" }\n");
                return;
            }
            text.Append('\n').Append(indent).Append("{\n");
            string inner = indent + "\t";
            // Members without a section first (the type's default access), then one section per access.
            var defaultAccess = H.Access.Private;
            // A record's ToString(), Hash() and Equals() that the compiler wrote (they carry the record's own place) are
            // written again when the record compiles: they are left out.
            bool written = (type.Definition.Flags & H.TypeFlags.Record) != 0 || type.Definition.Kind is H.TypeKind.Enum or H.TypeKind.Bitflags;
            bool Synthesized(CatalogMember m) => written && m.Method is { Name: "ToString" or "Hash" or "Equals" } method
                && method.Location.IsKnown && method.Location == type.Definition.Location;
            var groups = type.Declared
                .Where(m => !Synthesized(m))
                .Where(m => options.NonPublic || m.Access is H.Access.Public or H.Access.Inherited || type.IsInterface)
                .GroupBy(m => type.IsInterface ? defaultAccess : m.Access).OrderBy(g => g.Key == defaultAccess ? -1 : AccessOrder(g.Key)).ToList();
            bool first = true;
            foreach (var group in groups)
            {
                bool sectioned = group.Key != defaultAccess;
                if (!first)
                    text.Append('\n');
                if (sectioned)
                    text.Append(inner).Append(AccessWord(group.Key)).Append(":\n");
                string memberIndent = sectioned ? inner + "\t" : inner;
                CatalogMember? previous = null;
                foreach (var member in group)
                {
                    // Fields stay together; anything with a body or a comment gets a line of its own.
                    bool compact = member.Doc is null && member.Kind is CatalogMemberKind.Field or CatalogMemberKind.Constant
                        && previous?.Kind is CatalogMemberKind.Field or CatalogMemberKind.Constant;
                    if (previous is not null && (options.Bodies || member.Doc is not null) && !compact)
                        text.Append('\n');
                    MemberSource(text, member, memberIndent, options, imports, sectioned);
                    previous = member;
                }
                first = false;
            }
            text.Append(indent).Append("}\n");
        }

        private static int AccessOrder(H.Access access) => access switch
        {
            H.Access.File => 0,
            H.Access.Internal => 1,
            H.Access.Friendly => 2,
            H.Access.Inherited => 3,
            H.Access.Public => 4,
            _ => 5,
        };

        private string Attribute(CatalogType type, H.HirAttribute attribute, ISet<string>? imports = null)
        {
            var catalog = type.Catalog;
            var owner = catalog.Resolve(attribute.Constructor, H.MemberKind.Method, type.Assembly)?.Type;
            string name = owner?.Name
                ?? (attribute.Constructor.IsDefinition && attribute.Constructor.Type < type.Assembly.Hir.Types.Count ? type.Assembly.Hir.Types[attribute.Constructor.Type].Name : "?");
            if (owner is { Namespace.Length: > 0 })
                imports?.Add(owner.Namespace);
            if (name.EndsWith("Attribute", StringComparison.Ordinal) && name.Length > 9)
                name = name[..^9];
            name = owner is null ? name : MarkType(name, owner);
            var arguments = attribute.Arguments.Select(a => Constant(a, type, GenericNames.None, imports))
                .Concat(attribute.Named.Select(n => $"{n.Name} = {Constant(n.Value, type, GenericNames.None, imports)}")).ToList();
            return arguments.Count == 0 ? $"[{name}]" : $"[{name}({string.Join(", ", arguments)})]";
        }

        private void MemberSource(StringBuilder text, CatalogMember member, string indent, DecompileOptions options, ISet<string> imports, bool sectioned)
        {
            Doc(text, options.Docs ? member.Doc : null, indent);
            // The custom attributes written on a method, constructor or property, one per line.
            var custom = (IEnumerable<H.HirAttribute>?)member.Method?.Attributes ?? member.Property?.Attributes ?? [];
            foreach (var attribute in custom)
                text.Append(indent).Append(Attribute(member.Type, attribute, imports)).Append('\n');
            if (member.Method is { } attributed)
            {
                if ((attributed.Flags & H.MethodFlags.Inline) != 0)
                    text.Append(indent).Append("[Inline]\n");
                if ((attributed.Flags & H.MethodFlags.NoInline) != 0)
                    text.Append(indent).Append("[NoInline]\n");
                if ((attributed.Flags & H.MethodFlags.Import) != 0 && attributed.Library is { } library)
                {
                    // A C function: its library, and its entry when the method is called otherwise.
                    text.Append(indent).Append("[Import(").Append(Quote(library));
                    if (attributed.ExternName is { } entry && entry != attributed.Name)
                        text.Append(", ").Append(Quote(entry));
                    text.Append(")]\n");
                }
            }
            string signature = Signature(member, null, imports, withModifiers: true);
            if (member.Field is { Attributes.Length: > 0 } attributedField)
                signature = string.Concat(attributedField.Attributes.Select(a => Attribute(member.Type, a, imports) + " ")) + signature;
            var catalog = member.Type.Catalog;
            switch (member.Kind)
            {
                case CatalogMemberKind.Method or CatalogMemberKind.Constructor or CatalogMemberKind.Operator or CatalogMemberKind.Conversion:
                {
                    var method = member.Method!;
                    text.Append(indent).Append(signature);
                    if (!options.Bodies || method.Body is not { } body || (method.Flags & H.MethodFlags.Extern) != 0)
                    {
                        text.Append(";\n");
                        return;
                    }
                    var printer = new BodyPrinter(this, member.Type, member, body, imports);
                    var block = body.Block;
                    if (member.Kind == CatalogMemberKind.Constructor && FixedLength(member) is { } length)
                        text.Append(" : fixed(").Append(length).Append(')');
                    if (member.Kind == CatalogMemberKind.Constructor)
                    {
                        // The base constructor (or another of this type) it calls: `: base(…)` / `: (…)` in its head.
                        for (int s = 0; s < block.Statements.Length; s++)
                        {
                            if (block.Statements[s] is H.ExprStmt { Expression: H.CallExpr call } && printer.ConstructorChain(call) is { } chain)
                            {
                                text.Append(chain);
                                block = new H.BlockStmt([.. block.Statements.Take(s), .. block.Statements.Skip(s + 1)]);
                                break;
                            }
                        }
                    }
                    printer.Function(text, indent, block);
                    return;
                }
                case CatalogMemberKind.Property or CatalogMemberKind.Indexer:
                {
                    var property = member.Property!;
                    var definition = member.Type.Definition;
                    var getter = property.Getter >= 0 ? definition.Methods[property.Getter] : null;
                    var setter = property.Setter >= 0 ? definition.Methods[property.Setter] : null;
                    bool auto = getter?.Body is { Block.Statements: [H.ReturnStmt { Value: H.FieldExpr { Field: var f } }] } && f.IsDefinition
                        && f.Type == member.Type.Index && definition.Fields[f.Member].Name == "$" + property.Name;
                    if (!options.Bodies || auto || getter?.Body is null && setter?.Body is null)
                    {
                        text.Append(indent).Append(signature).Append('\n');
                        return;
                    }
                    string head = signature[..signature.LastIndexOf(" {", StringComparison.Ordinal)];
                    text.Append(indent).Append(head).Append('\n').Append(indent).Append("{\n");
                    if (getter?.Body is { } get)
                    {
                        text.Append(indent).Append("\tget");
                        new BodyPrinter(this, member.Type, member, get, imports, getter).Function(text, indent + "\t", get.Block);
                    }
                    if (setter?.Body is { } set)
                    {
                        text.Append(indent).Append("\tset");
                        new BodyPrinter(this, member.Type, member, set, imports, setter).Function(text, indent + "\t", set.Block);
                    }
                    text.Append(indent).Append("}\n");
                    return;
                }
                default:
                    text.Append(indent).Append(signature).Append(";\n");
                    return;
            }
        }

        // ── Constants ──────────────────────────────────────

        /// <summary>A constant of <paramref name="owner"/>'s assembly (its types in that assembly's tokens).</summary>
        internal string Constant(H.HirConstant constant, CatalogType owner, GenericNames names, ISet<string>? imports, bool bare = false) => constant switch
        {
            H.IntegerConstant i => i.Value.ToString(CultureInfo.InvariantCulture),
            H.FloatConstant f => Float(f),
            H.BoolConstant b => b.Value ? "true" : "false",
            H.CharConstant c => c.Value is >= 32 and < 127 and not '\'' and not '\\' ? $"'{(char)c.Value}'" : c.Value.ToString(CultureInfo.InvariantCulture),
            H.StringConstant s => Quote(s.Value),
            H.NullConstant => "null",
            H.TypeConstant t => $"typeof({Type(owner.Global(t.Type), owner.Catalog, names, imports)})",
            H.EnumConstant { Text: { } text } when bare => Quote(text),
            H.EnumConstant e when bare => e.Value.ToString(CultureInfo.InvariantCulture),
            H.EnumConstant e => EnumValue(new H.EnumConstant(owner.Global(e.Type), e.Value), owner.Catalog, names, imports),
            H.ArrayConstant a => "[" + string.Join(", ", a.Items.Select(i => Constant(i, owner, names, imports))) + "]",
            _ => constant.ToString() ?? "?",
        };

        private string EnumValue(H.EnumConstant e, HirCatalog catalog, GenericNames names, ISet<string>? imports)
        {
            var enumType = catalog.TypeOf(e.Type);
            var field = enumType?.Declared.FirstOrDefault(m => m.Field?.Value is H.EnumConstant v && v.Value == e.Value);
            string typeName = Type(e.Type, catalog, names, imports);
            return field is null ? $"({typeName}){e.Value}" : $"{typeName}.{MarkMember(field.Name, field, SemanticKind.EnumMember)}";
        }

        private static string Float(H.FloatConstant f)
        {
            string suffix = f.Type is H.Primitive.F32 or H.Primitive.F64 ? "" : f.Type.ToString().ToLowerInvariant();
            // f128 and f256: exact, from their bits (through a double they would round, overflow or vanish).
            if (f.Type is H.Primitive.F128 or H.Primitive.F256)
                return Wide(f.Bits, f.Type == H.Primitive.F128 ? 15 : 19, f.Type == H.Primitive.F128 ? 112 : 236, f.Type == H.Primitive.F128 ? 40 : 75, suffix);
            // f16, f32 and f64 print through the nearest double (exact for them), with their suffix.
            double value = f.Type == H.Primitive.F32 ? BitConverter.Int32BitsToSingle(unchecked((int)(uint)(f.Bits & uint.MaxValue)))
                : f.Type == H.Primitive.F64 ? BitConverter.Int64BitsToDouble(unchecked((long)(ulong)(f.Bits & ulong.MaxValue)))
                : Larva.SoftFloat.ToDouble(f.Bits, Larva.FloatFormat.Of(f.Type.Bits()));
            if (double.IsNaN(value) || double.IsInfinity(value))
                return Special(double.IsNaN(value) ? 0 : value > 0 ? 1 : -1, suffix);
            string text = value.ToString("R", CultureInfo.InvariantCulture);
            if (!text.Contains('.') && !text.Contains('E'))
                text += ".0";
            return text + suffix;
        }

        /// <summary>NaN and the infinities as constant expressions: <c>(0.0 / 0.0)</c>, <c>(1.0 / 0.0)</c>, <c>(-1.0 / 0.0)</c>.</summary>
        private static string Special(int sign, string suffix) =>
            $"({(sign == 0 ? "0.0" : sign > 0 ? "1.0" : "-1.0")}{suffix} / 0.0{suffix})";

        /// <summary>A binary float of <paramref name="exponentBits"/> and <paramref name="fractionBits"/> as a decimal with that many digits (enough to read back exactly).</summary>
        private static string Wide(System.Numerics.BigInteger bits, int exponentBits, int fractionBits, int digits, string suffix)
        {
            var one = System.Numerics.BigInteger.One;
            bool negative = !(bits >> (exponentBits + fractionBits)).IsZero;
            var fraction = bits & ((one << fractionBits) - 1);
            int exponent = (int)((bits >> fractionBits) & ((one << exponentBits) - 1));
            int maxExponent = (1 << exponentBits) - 1, bias = maxExponent >> 1;
            if (exponent == maxExponent)
                return Special(fraction.IsZero ? (negative ? -1 : 1) : 0, suffix);
            if (exponent == 0 && fraction.IsZero)
                return (negative ? "-0.0" : "0.0") + suffix;
            // value = mantissa × 2^power, exactly.
            var mantissa = exponent == 0 ? fraction : fraction | (one << fractionBits);
            int power = (exponent == 0 ? 1 : exponent) - bias - fractionBits;
            System.Numerics.BigInteger numerator = power >= 0 ? mantissa << power : mantissa, denominator = power >= 0 ? one : one << -power;
            // The decimal exponent: the value is in [10^e, 10^(e+1)).
            int e = (int)Math.Floor(System.Numerics.BigInteger.Log10(numerator) - System.Numerics.BigInteger.Log10(denominator));
            System.Numerics.BigInteger Scaled(int at) => at >= 0 ? numerator * System.Numerics.BigInteger.Pow(10, at) / denominator : numerator / (denominator * System.Numerics.BigInteger.Pow(10, -at));
            var significand = Scaled(digits - 1 - e);
            if (significand >= System.Numerics.BigInteger.Pow(10, digits))
            {
                e++;
                significand = Scaled(digits - 1 - e);
            }
            else if (significand < System.Numerics.BigInteger.Pow(10, digits - 1))
            {
                e--;
                significand = Scaled(digits - 1 - e);
            }
            // Rounded to nearest on the last digit (the next one decides).
            var tenth = Scaled(digits - e) % 10;
            if (tenth >= 5)
                significand += 1;
            string text = significand.ToString(CultureInfo.InvariantCulture);
            if (text.Length > digits)
            {
                e++;
                text = text[..digits];
            }
            text = text.TrimEnd('0');
            string mantissaText = text.Length > 1 ? text[0] + "." + text[1..] : text + ".0";
            return (negative ? "-" : "") + mantissaText + "e" + e.ToString(CultureInfo.InvariantCulture) + suffix;
        }

        internal static string Quote(string value)
        {
            var text = new StringBuilder("\"");
            foreach (char c in value)
            {
                text.Append(c switch
                {
                    '"' => "\\\"",
                    '\\' => "\\\\",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    '\0' => "\\0",
                    < ' ' => $"\\u{(int)c:x4}",
                    _ => c.ToString(),
                });
            }
            return text.Append('"').ToString();
        }

        /// <summary>
        /// The length a constructor gives its class's array kept in place (<c>: fixed(n)</c>): the return of the type's
        /// <c>$fixed{n}</c> method for it, the n-th constructor. Null for other constructors.
        /// </summary>
        private string? FixedLength(CatalogMember member)
        {
            var methods = member.Type.Definition.Methods;
            int ordinal = methods.Take(member.Index).Count(m => m.Kind == H.MethodKind.Constructor);
            var made = methods.FirstOrDefault(m => m.Name == $"$fixed{ordinal}" && m.IsStatic);
            if (made?.Body is not { Block.Statements: [H.ReturnStmt { Value: { } value }] } body)
                return null;
            var imports = new HashSet<string>();
            return new BodyPrinter(this, member.Type, member, body, imports, made).Expression(value);
        }

        internal string TypeIn(H.HirType local, CatalogType owner, GenericNames names, ISet<string> imports) =>
            Type(owner.Global(local), owner.Catalog, names, imports);
    }
}
