using System.Numerics;
using System.Runtime.CompilerServices;
using Antril.Syntax;
using H = Larva.Hir;

namespace Antril.Binding
{
    /// <summary>
    /// <c>derive</c> members, by expansion before binding. An interface may write a member with <c>derive</c> and a body:
    /// the interface keeps it as an abstract member (static or not), and every type that names the interface among its bases
    /// gets a copy of the member with its body, made for that type:
    /// <list type="bullet">
    /// <item>the interface's type parameters become the arguments the type wrote (<c>IEntity&lt;User&gt;</c>: <c>TSelf</c> is <c>User</c>);</item>
    /// <item><c>static loop (field F f in TSelf) statement</c> repeats the statement once per field of the type (its own
    /// instance fields and auto-properties, in order), with <c>F</c> the field's type and <c>f</c> the field: <c>x.f</c> is
    /// that field of <c>x</c>, <c>nameof(f)</c> and <c>f.Name</c> its name, <c>f.Index</c> its position, <c>f.Count</c> how many
    /// fields there are, <c>f.Nullable</c> whether its type is nullable, <c>f.Has&lt;A&gt;()</c> whether it has the attribute
    /// <c>A</c>, <c>f.Argument&lt;A&gt;(n, fallback)</c> the attribute's argument <c>n</c> (or <c>fallback</c>), <c>f.TypeName</c>
    /// its type as written (without a nullable's <c>?</c>: <c>"i64"</c>, <c>"str"</c>, <c>"Money"</c>);</item>
    /// <item><c>static if (condition)</c> keeps only the branch the condition (made of those facts and literals) chooses.</item>
    /// </list>
    /// A member the type writes itself is kept instead of the copy. Nothing is looked up at run time: each type's copy is
    /// ordinary code. A compiled library carries the text of its interfaces with <c>derive</c> members (<see cref="Recorded"/>),
    /// so the types of a program that implement them get their copies.
    /// </summary>
    internal static class Derive
    {
        /// <summary>Where a library keeps an interface's text (<see cref="H.HirAssembly.LanguageData"/>): this, then the file it is from.</summary>
        public const string DataPrefix = "antril.derive:";

        private sealed record Source(TypeDecl Interface, string Namespace, CompilationUnit Unit);

        /// <summary>Where a generated member was written: its file (whose imports its body names things by) and namespace.</summary>
        internal sealed record Origin(CompilationUnit Unit, string Namespace);

        private static readonly ConditionalWeakTable<MemberDecl, Origin> origins = new();

        /// <summary>The interface file a member was generated from, or null for a member the program wrote.</summary>
        public static Origin? OriginOf(MemberDecl member) => origins.TryGetValue(member, out var origin) ? origin : null;

        /// <summary>A field of the implementing type; <see cref="TypeName"/>: its type as written, without a nullable's <c>?</c>.</summary>
        private sealed record Field(string Name, TypeSyntax Type, AttributeSyntax[] Attributes, string TypeName);

        private static bool HasDerived(TypeDecl type) => type.Kind == TypeKind.Interface && type.Members.Any(m => (m.Modifiers & Modifiers.Derive) != 0);

        /// <summary>The text of each interface with <c>derive</c> members <paramref name="units"/> declare: the file's imports and the interface in its namespace.</summary>
        public static IEnumerable<H.HirResource> Recorded(CompilationUnit[] units)
        {
            var sources = new List<Source>();
            foreach (var unit in units)
                Collect(unit.Members, unit, "", sources);
            foreach (var source in sources)
            {
                var unit = source.Unit;
                var text = new System.Text.StringBuilder();
                foreach (var import in unit.Imports)
                    text.Append(unit.Source[import.Span]).Append('\n');
                string declaration = unit.Source[source.Interface.Span];
                if (source.Namespace.Length == 0)
                    text.Append(declaration).Append('\n');
                else
                    text.Append("into namespace ").Append(source.Namespace).Append("\n{\n").Append(declaration).Append("\n}\n");
                yield return new H.HirResource($"{DataPrefix}{unit.Source.Path}#{source.Interface.Name}", System.Text.Encoding.UTF8.GetBytes(text.ToString()));
            }
        }

        private static void Collect(MemberDecl[] members, CompilationUnit unit, string ns, List<Source> sources)
        {
            foreach (var member in members)
            {
                if (member is NamespaceDecl inner)
                    Collect(inner.Members, unit, ns.Length == 0 ? inner.Name.ToString() : $"{ns}.{inner.Name}", sources);
                else if (member is TypeDecl type && HasDerived(type))
                    sources.Add(new Source(type, ns, unit));
            }
        }

        /// <summary>
        /// The units with each interface's <c>derive</c> members made abstract and every type that implements one given its
        /// copies (the units as they are when nothing derives).
        /// </summary>
        public static CompilationUnit[] Expand(CompilationUnit[] units, DiagnosticBag diagnostics, IReadOnlyList<H.HirAssembly>? references = null, OperatorTable? table = null)
        {
            var sources = new List<Source>();
            foreach (var unit in units)
                Collect(unit.Members, unit, "", sources);
            foreach (var assembly in references ?? [])
            {
                foreach (var data in assembly.LanguageData)
                {
                    if (!data.Name.StartsWith(DataPrefix, StringComparison.Ordinal))
                        continue;
                    var text = new SourceText(System.Text.Encoding.UTF8.GetString(data.Data), $"{assembly.Name}:{data.Name[DataPrefix.Length..]}");
                    var unit = Parser.Parse(text, diagnostics, table);
                    Collect(unit.Members, unit, "", sources);
                }
            }
            // Nothing derives: the units as they are (a `derive` written elsewhere is reported by the binder).
            if (sources.Count == 0)
                return units;
            var result = new CompilationUnit[units.Length];
            for (int u = 0; u < units.Length; u++)
                result[u] = units[u] with { Members = Members(units[u].Members, units[u], sources, diagnostics) };
            return result;
        }

        private static MemberDecl[] Members(MemberDecl[] members, CompilationUnit unit, List<Source> sources, DiagnosticBag diagnostics)
        {
            var result = new MemberDecl[members.Length];
            for (int m = 0; m < members.Length; m++)
            {
                result[m] = members[m] switch
                {
                    NamespaceDecl ns => ns with { Members = Members(ns.Members, unit, sources, diagnostics) },
                    TypeDecl type => Type(type, unit, sources, diagnostics),
                    var other => Misplaced(other, unit, diagnostics),
                };
            }
            return result;
        }

        private static MemberDecl Misplaced(MemberDecl member, CompilationUnit unit, DiagnosticBag diagnostics)
        {
            if ((member.Modifiers & Modifiers.Derive) == 0)
                return member;
            diagnostics.Error("AN1425", "`derive` goes on an interface's function: every type that implements the interface gets it", unit.Source, member.Span);
            return member with { Modifiers = member.Modifiers & ~Modifiers.Derive };
        }

        private static TypeDecl Type(TypeDecl type, CompilationUnit unit, List<Source> sources, DiagnosticBag diagnostics)
        {
            var members = new List<MemberDecl>();
            foreach (var member in type.Members)
            {
                if (member is TypeDecl nested)
                {
                    members.Add(Type(nested, unit, sources, diagnostics));
                    continue;
                }
                if ((member.Modifiers & Modifiers.Derive) == 0)
                {
                    members.Add(member);
                    continue;
                }
                // The interface keeps the member without its body: abstract (static or not), what its implementations write.
                if (type.Kind == TypeKind.Interface && member is FunctionDecl { Body: not null } function)
                    members.Add(function with { Modifiers = function.Modifiers & ~Modifiers.Derive, Body = null });
                else if (type.Kind == TypeKind.Interface)
                {
                    diagnostics.Error("AN1425", member is FunctionDecl
                        ? "a `derive` function has a body: the code each implementing type gets"
                        : "`derive` goes on an interface's function: every type that implements the interface gets it", unit.Source, member.Span);
                    members.Add(member with { Modifiers = member.Modifiers & ~Modifiers.Derive });
                }
                else
                    members.Add(Misplaced(member, unit, diagnostics));
            }
            if (type.Kind is TypeKind.Class or TypeKind.BaseClass or TypeKind.RecordClass or TypeKind.Value or TypeKind.RecordValue)
            {
                foreach (var written in type.Bases)
                {
                    if (written is not NamedTypeSyntax named)
                        continue;
                    foreach (var source in sources.Where(s => s.Interface.Name == named.Name.Parts[^1] && s.Interface.Generics.Length == named.Arguments.Length))
                        members.AddRange(Generated(type, unit, named, source, members, diagnostics));
                }
            }
            return type with { Members = [.. members] };
        }

        /// <summary>The copies of <paramref name="source"/>'s <c>derive</c> members <paramref name="type"/> gets (not those it writes itself).</summary>
        private static IEnumerable<MemberDecl> Generated(TypeDecl type, CompilationUnit unit, NamedTypeSyntax written, Source source, List<MemberDecl> own, DiagnosticBag diagnostics)
        {
            var map = new Dictionary<string, TypeSyntax>(StringComparer.Ordinal);
            for (int g = 0; g < source.Interface.Generics.Length; g++)
                map[source.Interface.Generics[g].Name] = written.Arguments[g];
            // The parameter the type itself is given for (`IEntity<User>`: TSelf): field loops go over its fields.
            string? self = map.FirstOrDefault(p => p.Value is NamedTypeSyntax n && n.Name.Parts[^1] == type.Name && n.Arguments.Length == type.Generics.Length).Key;
            var fields = Fields(type, unit);
            foreach (var member in source.Interface.Members)
            {
                if ((member.Modifiers & Modifiers.Derive) == 0 || member is not FunctionDecl { Body: not null } function)
                    continue;
                // The type's own member of that name and arity is kept: it replaces the copy.
                if (own.Any(m => m is FunctionDecl f && f.Name == function.Name && f.Parameters.Length == function.Parameters.Length && (f.Modifiers & Modifiers.Derive) == 0))
                    continue;
                var copy = AstRewriter.Rewrite(function, n => Substitute(n, map));
                copy = AstRewriter.Rewrite(copy, n => Unroll(n, self, fields, source, diagnostics));
                copy = AstRewriter.Rewrite(copy, n => Decide(n, source, diagnostics));
                copy = copy with { Modifiers = (copy.Modifiers & ~(Modifiers.Derive | Modifiers.Access)) | Modifiers.Public };
                origins.AddOrUpdate(copy, new Origin(source.Unit, source.Namespace));
                yield return copy;
            }
        }

        /// <summary>A type's fields for <c>static loop (field …)</c>: its instance fields and auto-properties, in order.</summary>
        private static List<Field> Fields(TypeDecl type, CompilationUnit unit)
        {
            string Text(TypeSyntax written) => string.Concat(unit.Source[(written as NullableTypeSyntax)?.Inner.Span ?? written.Span].Where(c => !char.IsWhiteSpace(c)));
            var fields = new List<Field>();
            foreach (var member in type.Members)
            {
                switch (member)
                {
                    case FieldDecl field when (field.Modifiers & (Modifiers.Static | Modifiers.Const)) == 0:
                        fields.Add(new Field(field.Name, field.Type, field.Attributes, Text(field.Type)));
                        break;
                    case PropertyDecl property when (property.Modifiers & Modifiers.Static) == 0 && property.Accessors.Length > 0 && property.Accessors.All(a => a.Body is null && !a.IsAbstract):
                        fields.Add(new Field(property.Name, property.Type, property.Attributes, Text(property.Type)));
                        break;
                }
            }
            return fields;
        }

        // ── One copy ──

        /// <summary>The interface's type parameters, as types and as names (<c>TSelf.Read(…)</c>), become what the type wrote.</summary>
        private static Node? Substitute(Node node, Dictionary<string, TypeSyntax> map) => node switch
        {
            NamedTypeSyntax { Name.Parts: [var only], Arguments.Length: 0 } named when map.TryGetValue(only, out var argument) => argument,
            NameExpr { TypeArguments.Length: 0 } name when map.TryGetValue(name.Name, out var argument) && AsExpression(argument, name.Span) is { } expression => expression,
            _ => null,
        };

        /// <summary>A type written as an expression (<c>A.B.User</c> → member accesses), or null when it cannot be one.</summary>
        private static Expr? AsExpression(TypeSyntax type, TextSpan span)
        {
            if (type is not NamedTypeSyntax named)
                return null;
            var parts = named.Name.Parts;
            Expr result = new NameExpr(parts[0], parts.Length == 1 ? named.Arguments : [], span);
            for (int p = 1; p < parts.Length; p++)
                result = new MemberExpr(result, parts[p], p == parts.Length - 1 ? named.Arguments : [], span) { NameSpan = span };
            return result;
        }

        private static Node? Unroll(Node node, string? self, List<Field> fields, Source source, DiagnosticBag diagnostics)
        {
            if (node is not StaticLoopStmt { Field: { } fieldName } loop)
                return null;
            if (self is null || loop.Pack != self)
            {
                diagnostics.Error("AN1423", self is null
                    ? "`static loop (field …)` goes over the fields of the type that implements the interface: one of the interface's type parameters must be that type (`IEntity<TSelf>`, implemented as `IEntity<User>`)"
                    : $"`static loop (field …)` goes over the fields of the implementing type: write `in {self}`", source.Unit.Source, loop.Span);
                return new BlockStmt([], loop.Span);
            }
            var copies = new List<Stmt>();
            for (int i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                int index = i;
                var body = AstRewriter.Rewrite(loop.Body, n => Fact(n, loop.Name, fieldName, field, index, fields.Count));
                // A loop inside the loop (over the same fields: pairs of fields) unrolls inside each copy.
                copies.Add(AstRewriter.Rewrite(body, n => Unroll(n, self, fields, source, diagnostics)));
            }
            return new BlockStmt([.. copies], loop.Span);
        }

        /// <summary>What a field loop's names stand for in the copy for one field.</summary>
        private static Node? Fact(Node node, string typeName, string fieldName, Field field, int index, int count)
        {
            bool IsField(Expr e) => e is NameExpr { TypeArguments.Length: 0 } n && n.Name == fieldName;
            switch (node)
            {
                case CallExpr { Target: NameExpr { Name: "nameof", TypeArguments.Length: 0 }, Arguments: [var argument] } call when IsField(argument):
                    return new StringLiteral(field.Name, call.Span);
                case CallExpr { Target: MemberExpr { Name: "Has", TypeArguments: [NamedTypeSyntax attribute] } member, Arguments: [] } call when IsField(member.Target):
                    return new BoolLiteral(Attribute(field, attribute) is not null, call.Span);
                case CallExpr { Target: MemberExpr { Name: "Argument", TypeArguments: [NamedTypeSyntax attribute] } member, Arguments: [IntegerLiteral position, var fallback] } call when IsField(member.Target):
                {
                    var found = Attribute(field, attribute);
                    int at = (int)position.Value;
                    return found is not null && at >= 0 && at < found.Arguments.Length
                        ? found.Arguments[at]
                        : AstRewriter.Rewrite(fallback, n => Fact(n, typeName, fieldName, field, index, count));
                }
                case MemberExpr { Name: "Name" } member when IsField(member.Target):
                    return new StringLiteral(field.Name, member.Span);
                case MemberExpr { Name: "Index" } member when IsField(member.Target):
                    return new IntegerLiteral(new BigInteger(index), member.Span);
                case MemberExpr { Name: "Count" } member when IsField(member.Target):
                    return new IntegerLiteral(new BigInteger(count), member.Span);
                case MemberExpr { Name: "TypeName" } member when IsField(member.Target):
                    return new StringLiteral(field.TypeName, member.Span);
                case MemberExpr { Name: "Nullable" } member when IsField(member.Target):
                    return new BoolLiteral(field.Type is NullableTypeSyntax, member.Span);
                case MemberExpr member when member.Name == fieldName && member.TypeArguments.Length == 0:
                    return member with { Name = field.Name, Target = AstRewriter.Rewrite(member.Target, n => Fact(n, typeName, fieldName, field, index, count)) };
                case NameExpr name when IsField(name):
                    return name with { Name = field.Name };
                case NamedTypeSyntax { Name.Parts: [var only], Arguments.Length: 0 } when only == typeName:
                    return field.Type;
                // The field's type as an expression: `F.Schema()`, a static function of it.
                case NameExpr { TypeArguments.Length: 0 } name when name.Name == typeName && AsExpression(field.Type, name.Span) is { } typeExpression:
                    return typeExpression;
                default:
                    return null;
            }
        }

        /// <summary>The field's attribute named by <paramref name="attribute"/> (with or without its <c>Attribute</c> ending), or null.</summary>
        private static AttributeSyntax? Attribute(Field field, NamedTypeSyntax attribute)
        {
            string wanted = attribute.Name.Parts[^1];
            string bare = wanted.EndsWith("Attribute", StringComparison.Ordinal) ? wanted[..^"Attribute".Length] : wanted;
            return field.Attributes.FirstOrDefault(a => a.Name.Parts[^1] is var written && (written == bare || written == bare + "Attribute"));
        }

        /// <summary><c>static if</c>: the branch its condition chooses (the condition must be known now).</summary>
        private static Node? Decide(Node node, Source source, DiagnosticBag diagnostics)
        {
            if (node is not IfStmt { Static: true } decided)
                return null;
            if (Known(decided.Condition) is not { } value)
            {
                diagnostics.Error("AN1424", "a `static if` is decided when the member is made for a type: its condition is made of what a field loop knows (`f.Has<A>()`, `f.Name`, `f.Nullable`, `f.Index`, `f.TypeName`…), literals, `!`, `&&`, `||`, `==`, `!=`, comparisons of numbers and `StartsWith`/`EndsWith`/`Contains` of texts",
                    source.Unit.Source, decided.Condition.Span);
                return new BlockStmt([], decided.Span);
            }
            Stmt? chosen = value ? decided.Then : decided.Else;
            return chosen is null ? new BlockStmt([], decided.Span) : AstRewriter.Rewrite(chosen, n => Decide(n, source, diagnostics));
        }

        /// <summary>A condition's value when it is made of literals, or null.</summary>
        private static bool? Known(Expr condition)
        {
            switch (condition)
            {
                case BoolLiteral b:
                    return b.Value;
                case UnaryExpr { Operator: UnaryOperator.Not } not:
                    return Known(not.Operand) is { } inner ? !inner : null;
                case BinaryExpr { Operator: BinaryOperator.LogicalAnd } and:
                    return Known(and.Left) is { } l1 && Known(and.Right) is { } r1 ? l1 && r1 : null;
                case BinaryExpr { Operator: BinaryOperator.LogicalOr } or:
                    return Known(or.Left) is { } l2 && Known(or.Right) is { } r2 ? l2 || r2 : null;
                case BinaryExpr { Operator: BinaryOperator.Equal or BinaryOperator.NotEqual } compare:
                {
                    object? left = Literal(compare.Left), right = Literal(compare.Right);
                    if (left is null || right is null)
                        return null;
                    bool same = Equals(left, right);
                    return compare.Operator == BinaryOperator.Equal ? same : !same;
                }
                // `f.TypeName.StartsWith("Table<")`: text facts.
                case CallExpr { Target: MemberExpr { Target: StringLiteral text, Name: "StartsWith" or "EndsWith" or "Contains" } member, Arguments: [StringLiteral part] }:
                    return member.Name switch
                    {
                        "StartsWith" => text.Value.StartsWith(part.Value, StringComparison.Ordinal),
                        "EndsWith" => text.Value.EndsWith(part.Value, StringComparison.Ordinal),
                        _ => text.Value.Contains(part.Value, StringComparison.Ordinal),
                    };
                case BinaryExpr { Operator: BinaryOperator.Less or BinaryOperator.LessEqual or BinaryOperator.Greater or BinaryOperator.GreaterEqual } order
                    when order.Left is IntegerLiteral a && order.Right is IntegerLiteral b:
                    return order.Operator switch
                    {
                        BinaryOperator.Less => a.Value < b.Value,
                        BinaryOperator.LessEqual => a.Value <= b.Value,
                        BinaryOperator.Greater => a.Value > b.Value,
                        _ => a.Value >= b.Value,
                    };
                default:
                    return null;
            }
        }

        private static object? Literal(Expr e) => e switch
        {
            StringLiteral s => s.Value,
            IntegerLiteral i => i.Value,
            BoolLiteral b => b.Value,
            _ => null,
        };
    }
}
