using Antril.Syntax;
using H = Larva.Hir;
using static Larva.Hir.HirTypes;

namespace Antril.Binding
{
    /// <summary>
    /// Where a declaration is written: its file, its namespace, the namespaces its file imports, and the type parameters in
    /// scope (its type's, then its method's).
    /// </summary>
    internal sealed record DeclarationScope(SourceText Source, string Namespace, string[] Imports)
    {
        /// <summary>The namespaces the file names by an alias (<c>import D as X;</c>): alias → namespace.</summary>
        public IReadOnlyDictionary<string, string> NamespaceAliases { get; init; } = new Dictionary<string, string>();

        /// <summary>The names the file takes one by one (<c>import A from D as E;</c>): the name here → namespace and name there.</summary>
        public IReadOnlyDictionary<string, (string Namespace, string Name)> Picked { get; init; } = new Dictionary<string, (string Namespace, string Name)>();

        /// <summary>The local constants a body's constant may name (<c>const i32 b = a * 2;</c>), by name.</summary>
        public Func<string, (H.HirConstant Value, H.HirType Type)?>? LocalConstants { get; init; }

        public string[] TypeParameters { get; init; } = [];
        public string[] MethodParameters { get; init; } = [];

        /// <summary>The types the declaration is inside (full names, innermost first): their nested types are found by their own names.</summary>
        public string[] EnclosingTypes { get; init; } = [];

        /// <summary>The innermost type the declaration is inside (what access checks are made from), if any.</summary>
        public TypeSymbol? EnclosingSymbol { get; set; }

        /// <summary>The namespace and its parents, innermost first (<c>A.B</c>, <c>A</c>, the root).</summary>
        public IEnumerable<string> Enclosing()
        {
            string ns = Namespace;
            while (true)
            {
                yield return ns;
                if (ns.Length == 0)
                    yield break;
                int dot = ns.LastIndexOf('.');
                ns = dot < 0 ? "" : ns[..dot];
            }
        }

        /// <summary>The type parameter a name means here (the method's hide the type's), if any.</summary>
        public H.HirType? TypeParameter(string name)
        {
            int m = Array.IndexOf(MethodParameters, name);
            if (m >= 0)
                return new H.GenericParamType(H.GenericOwner.Method, m);
            int t = Array.IndexOf(TypeParameters, name);
            return t >= 0 ? new H.GenericParamType(H.GenericOwner.Type, t) : null;
        }
    }

    /// <summary>A type of the program (or the synthetic class holding a namespace's top-level functions and fields).</summary>
    internal sealed class TypeSymbol(string ns, string name, TypeDecl? declaration, DeclarationScope scope, H.TypeDef definition, H.TypeToken token)
    {
        public string Namespace { get; } = ns;
        public string Name { get; } = name;
        public string FullName => Namespace.Length == 0 ? Name : $"{Namespace}.{Name}";
        public TypeDecl? Declaration { get; } = declaration;
        public DeclarationScope Scope { get; } = scope;
        public H.TypeDef Definition { get; } = definition;
        public H.TypeToken Token { get; } = token;

        /// <summary>The type seen from inside itself: its own parameters as arguments (a primitive's provider: the primitive).</summary>
        public H.HirType Type { get; set; } = H.HirType.Named(token, [.. Enumerable.Range(0, definition.Generics.Count).Select(i => (H.HirType)new H.GenericParamType(H.GenericOwner.Type, i))]);

        public int GenericCount => Definition.Generics.Count;

        /// <summary>A nested type of a generic type: how many of its type parameters are its outer types' (they come first).</summary>
        public int OuterGenericCount { get; init; }

        /// <summary>The type this one is nested in, if any.</summary>
        public TypeSymbol? Outer { get; set; }

        /// <summary>Types made friends of this one with <c>[FriendOf(T)]</c> (on either side): they see its <c>friendly</c> members.</summary>
        public HashSet<TypeSymbol> Friends { get; } = [];

        /// <summary>This type and the types it is nested in, innermost first.</summary>
        public IEnumerable<TypeSymbol> Outers()
        {
            for (var t = this; t is not null; t = t.Outer)
                yield return t;
        }

        /// <summary>Whether this type is <paramref name="other"/> or nested in it.</summary>
        public bool IsWithin(TypeSymbol other) => Outers().Contains(other);

        /// <summary>The synthetic class of a namespace's top-level functions (<c>$Module</c>).</summary>
        public bool IsModule { get; init; }

        /// <summary>
        /// The referenced assembly this type comes from when that assembly is built for another GC (a domain of its own):
        /// its objects cannot be used here; only its static functions over numbers, bools and packets can be called.
        /// </summary>
        public string? Domain { get; set; }

        /// <summary>
        /// Made from a referenced assembly's metadata: its token is a reference, and so are its members' (made by
        /// <see cref="ReferenceMember"/> the first time each is used).
        /// </summary>
        public Func<H.MemberRef, H.MemberToken>? ReferenceMember { get; init; }

        public bool IsExternal => ReferenceMember is not null;

        /// <summary>A member's token: its definition here, or a reference to it in the assembly that defines it.</summary>
        public H.MemberToken MemberToken(int index, H.MemberKind kind, string name, int genericArity, H.HirType[] parameters, H.HirType result) =>
            ReferenceMember is { } reference
                ? reference(new H.MemberRef(kind, Token, name, genericArity, parameters, result))
                : H.MemberToken.Def(Token.Index, index);

        public TypeSymbol? Base { get; set; }

        /// <summary>The base class with its type arguments, in this type's parameters.</summary>
        public H.NamedType? BaseType { get; set; }

        /// <summary>Interfaces this type implements (or an interface extends), in its parameters.</summary>
        public List<H.NamedType> Interfaces { get; } = [];

        public bool IsClass => Definition.Kind == H.TypeKind.Class;
        public bool IsPrimitive => (Definition.Flags & H.TypeFlags.Primitive) != 0;
        public bool IsStruct => Definition.Kind == H.TypeKind.Struct && !IsPrimitive;
        public bool IsInterface => Definition.Kind == H.TypeKind.Interface;
        public bool IsEnum => Definition.Kind is H.TypeKind.Enum or H.TypeKind.Bitflags;
        public bool IsAbstract => (Definition.Flags & H.TypeFlags.Abstract) != 0;
        public bool IsStatic => (Definition.Flags & H.TypeFlags.Static) != 0;
        /// <summary>An extension block: its methods extend <see cref="H.TypeDef.Underlying"/> (the receiver is the first parameter unless static).</summary>
        public bool IsExtension => (Definition.Flags & H.TypeFlags.Extension) != 0;
        public string? ExtensionReceiver { get; set; }

        public Dictionary<string, FieldSymbol> Fields { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<MethodSymbol>> Methods { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, PropertySymbol> Properties { get; } = new(StringComparer.Ordinal);
        public List<MethodSymbol> Constructors { get; } = [];

        /// <summary>Its <c>fixed T[]</c> field (an array kept in the object, the last field), if it has one.</summary>
        public FieldSymbol? InPlace { get; set; }

        /// <summary>Instance fields with an initializer, in declaration order: every constructor sets them first.</summary>
        public List<FieldSymbol> InitializedFields { get; } = [];

        /// <summary>Static fields whose initializer is not a constant, in declaration order: the static constructor sets them first.</summary>
        public List<FieldSymbol> StaticInitializedFields { get; } = [];

        /// <summary>A value-based enum's members with the values written for them (`Red = new Rgb(255, 0, 0)`), in order.</summary>
        public List<(string Name, Expr Value)>? EnumValues { get; set; }

        /// <summary>The static constructor (written as <c>static new() { … }</c>, or made for <see cref="StaticInitializedFields"/>).</summary>
        public MethodSymbol? StaticConstructor { get; set; }

        /// <summary>Whether this class is <paramref name="other"/> or derives from it.</summary>
        public bool IsOrDerives(TypeSymbol other)
        {
            for (var t = this; t is not null; t = t.Base)
            {
                if (t == other)
                    return true;
            }
            return false;
        }

        /// <summary>This type and its base classes, each with its type arguments in this type's parameters.</summary>
        public IEnumerable<(TypeSymbol Type, H.HirType[] Arguments)> Chain()
        {
            H.HirType[] arguments = ((H.NamedType)H.HirType.Named(Token, [.. Enumerable.Range(0, GenericCount).Select(i => (H.HirType)new H.GenericParamType(H.GenericOwner.Type, i))])).Arguments;
            for (var t = this; t is not null; t = t.Base)
            {
                yield return (t, arguments);
                if (t.BaseType is { } b)
                    arguments = [.. b.Arguments.Select(a => a.Substitute(arguments))];
            }
        }

        public FieldSymbol? FindField(string name)
        {
            for (var t = this; t is not null; t = t.Base)
            {
                if (t.Fields.TryGetValue(name, out var field))
                    return field;
            }
            return null;
        }

        public PropertySymbol? FindProperty(string name)
        {
            for (var t = this; t is not null; t = t.Base)
            {
                if (t.Properties.TryGetValue(name, out var property))
                    return property;
            }
            return null;
        }

        private Dictionary<string, List<MethodSymbol>>? visible;

        /// <summary>Every method name of this type and its bases, with what <see cref="FindMethods"/> gives (made once declared).</summary>
        public void Seal()
        {
            var table = new Dictionary<string, List<MethodSymbol>>(StringComparer.Ordinal);
            for (var t = this; t is not null; t = t.Base)
            {
                foreach (var name in t.Methods.Keys)
                {
                    if (!table.ContainsKey(name))
                        table[name] = Collect(name);
                }
            }
            visible = table;
        }

        private static readonly List<MethodSymbol> None = [];

        /// <summary>Methods named <paramref name="name"/> of this type and its bases (an override hides what it overrides).</summary>
        public List<MethodSymbol> FindMethods(string name) =>
            visible is not null ? visible.TryGetValue(name, out var found) ? found : None : Collect(name);

        private List<MethodSymbol> Collect(string name)
        {
            var found = new List<MethodSymbol>();
            var signatures = new List<H.HirType[]>();
            foreach (var (t, arguments) in Chain())
            {
                if (!t.Methods.TryGetValue(name, out var methods))
                    continue;
                foreach (var method in methods)
                {
                    // Signatures compare in this type's terms (a base's parameters substituted).
                    H.HirType[] signature = [.. method.Parameters.Select(p => p.Substitute(arguments))];
                    if (!signatures.Any(s => s.AsSpan().SequenceEqual(signature)))
                    {
                        found.Add(method);
                        signatures.Add(signature);
                    }
                }
            }
            return found;
        }

        public override string ToString() => FullName;
    }

    internal sealed class FieldSymbol(TypeSymbol owner, string name, H.HirType type, int index, bool isStatic)
    {
        public TypeSymbol Owner { get; } = owner;
        public string Name { get; } = name;
        public H.HirType Type { get; } = type;
        public int Index { get; } = index;
        public bool IsStatic { get; } = isStatic;
        /// <summary>A constant written into the code where it is used (its <see cref="Value"/>).</summary>
        public bool IsConst { get; init; }

        /// <summary>Declared `const`: written into the code, or (a value) made once and never changed.</summary>
        public bool IsConstant { get; init; }

        /// <summary>A constant whose value could not be worked out (reported once).</summary>
        public bool ConstantFailed { get; set; }

        public H.HirConstant? Value { get; set; }
        public FieldDecl? Declaration { get; init; }
        public DeclarationScope? Scope { get; init; }
        public H.MemberToken Token => Owner.MemberToken(Index, H.MemberKind.Field, Name, 0, [], Type);
        public H.Access Access => Owner.Definition.Fields[Index].Access;
    }

    internal sealed class PropertySymbol(TypeSymbol owner, string name, H.HirType type, int index, bool isStatic)
    {
        public TypeSymbol Owner { get; } = owner;
        public string Name { get; } = name;
        public H.HirType Type { get; } = type;
        public int Index { get; } = index;
        public bool IsStatic { get; } = isStatic;
        public MethodSymbol? Getter { get; set; }
        public MethodSymbol? Setter { get; set; }
        /// <summary>Its declaration, for properties of the assembly being compiled.</summary>
        public Syntax.PropertyDecl? Declaration { get; init; }
        public H.MemberToken Token => Owner.MemberToken(Index, H.MemberKind.Property, Name, 0, [], Type);
        public H.Access Access => Owner.Definition.Properties[Index].Access;
    }

    /// <summary>A body the compiler writes (a record's members).</summary>
    internal enum SyntheticBody : byte { None, ToString, Hash, Equals, EnumValue, EnumOf, EnumText }

    internal sealed class MethodSymbol(TypeSymbol owner, string name, H.HirType[] parameters, string[] parameterNames, H.HirType result, int index)
    {
        /// <summary>Whether the method is a conversion (<c>op (T)(S s)</c>: named <c>$conv_…</c>).</summary>
        public bool IsConversion => Name.StartsWith("$conv_", StringComparison.Ordinal);

        public TypeSymbol Owner { get; } = owner;
        public string Name { get; } = name;
        public H.HirType[] Parameters { get; } = parameters;
        public string[] ParameterNames { get; } = parameterNames;
        public H.HirType Result { get; } = result;
        public int Index { get; } = index;
        public bool IsStatic { get; init; }
        public bool IsVirtual { get; init; }
        public bool IsAbstract { get; init; }
        public bool IsConstructor { get; init; }

        /// <summary>The type's static constructor: runs once, before the program's <c>Main</c>.</summary>
        public bool IsStaticConstructor { get; init; }
        public FunctionDecl? Function { get; init; }
        public ConstructorDecl? Constructor { get; init; }

        /// <summary>A property accessor's body (or the one made for <c>get;</c> / <c>set;</c>).</summary>
        public BlockStmt? AccessorBody { get; init; }

        /// <summary>Where errors about the method point.</summary>
        public TextSpan NameSpan { get; init; }

        /// <summary>The body the compiler writes for this method, when it has no declaration of its own.</summary>
        public SyntheticBody Synthetic { get; init; }

        /// <summary>The method's own type parameters.</summary>
        public int GenericCount => Definition.Generics.Count;

        /// <summary>The file, namespace and imports the method is written in (names in its body are found from there).</summary>
        public required DeclarationScope Scope { get; init; }
        public H.MemberToken Token => Owner.MemberToken(Index, H.MemberKind.Method, Definition.Name, GenericCount, Parameters, Result);
        public H.MethodDef Definition => Owner.Definition.Methods[Index];
        public H.Access Access => Definition.Access;

        public bool SameSignature(MethodSymbol other) => Name == other.Name && Parameters.SequenceEqual(other.Parameters);

        public override string ToString() => $"{Owner.FullName}.{Name}";
    }
}
