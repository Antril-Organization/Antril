using Antril.Syntax;
using H = Larva.Hir;

namespace Antril.Binding
{
    /// <summary>
    /// Custom attributes: <c>[Name(arguments, Field = value)]</c> on a type, method, constructor, field or property names a
    /// class derived from <c>System.Attribute</c> (<c>Name</c> or <c>NameAttribute</c>); its arguments are constants
    /// (numbers, bools, strings, enum members, <c>typeof</c>, <c>null</c>). They are kept in the HIR, and reflection makes
    /// their objects (<c>GetCustomAttributes()</c>). The compiler's own attributes (<c>Inline</c>, <c>NoInline</c>,
    /// <c>Primitive</c>, <c>Import</c>, <c>Export</c>) are read where they apply and are not kept.
    /// </summary>
    internal sealed partial class Binder
    {
        private static readonly HashSet<string> CompilerAttributes = new(StringComparer.Ordinal)
        {
            "Inline", "NoInline", "Primitive", "Import", "Export",
            "System.Inline", "System.NoInline", "System.Primitive", "System.Import", "System.Export",
            "System.Runtime.InteropServices.Import", "System.Runtime.InteropServices.Export",
        };

        /// <summary>Binds the attributes of every declaration of this assembly (after members are declared).</summary>
        /// <summary>The full name of the class an attribute of this program constructs (`System.Runtime.InteropServices.FieldOffsetAttribute`).</summary>
        private string AttributeName(H.HirAttribute attribute)
        {
            var parent = attribute.Constructor.IsDefinition ? H.TypeToken.Def(attribute.Constructor.Type) : Assembly.MemberRefs[attribute.Constructor.Member].Parent;
            if (parent.IsDefinition)
            {
                var type = Assembly.Types[parent.Index];
                return type.Namespace.Length == 0 ? type.Name : $"{type.Namespace}.{type.Name}";
            }
            var other = Assembly.TypeRefs[parent.Index];
            return other.Namespace.Length == 0 ? other.Name : $"{other.Namespace}.{other.Name}";
        }

        internal const string ValueLayoutName = "System.Runtime.InteropServices.ValueLayoutAttribute";
        internal const string FieldOffsetName = "System.Runtime.InteropServices.FieldOffsetAttribute";

        /// <summary>
        /// `[ValueLayout]` and `[FieldOffset]`: on a value that is not generic, over fields without objects; with
        /// `Layout.Explicit` every instance field says its offset, otherwise none does; Pack is 0, 1, 2, 4, 8 or 16.
        /// </summary>
        private void CheckValueLayouts()
        {
            foreach (var type in Types.Values)
            {
                // Most types carry no attributes at all: nothing to look at.
                if (type.IsExternal || type.Definition.Attributes.Count == 0 && type.Definition.Fields.All(f => f.Attributes.Length == 0))
                    continue;
                var source = type.Scope.Source;
                var span = type.Declaration?.NameSpan ?? default;
                var layout = type.Definition.Attributes.FirstOrDefault(a => AttributeName(a) == ValueLayoutName);
                var offsets = type.Fields.Values.Where(f => !f.IsStatic).Select(f => (Field: f, Offset: type.Definition.Fields[f.Index].Attributes.FirstOrDefault(a => AttributeName(a) == FieldOffsetName))).ToList();
                if (layout is null)
                {
                    foreach (var (field, offset) in offsets.Where(o => o.Offset is not null))
                        Error("AN1082", $"`[FieldOffset]` on `{field.Name}` needs `[ValueLayout(Layout.Explicit)]` on `{type.Name}`", source, field.Declaration?.Span ?? span);
                    continue;
                }
                if (!type.IsStruct || type.GenericCount > 0)
                {
                    Error("AN1080", $"`[ValueLayout]` goes on a `value` without type parameters, not on `{type.Name}`", source, span);
                    continue;
                }
                bool isExplicit = layout.Arguments is [H.EnumConstant kind] && kind.Value == 1;
                foreach (var (name, value) in layout.Named)
                {
                    long number = value is H.IntegerConstant i ? (long)i.Value : -1;
                    if (name == "Pack" && number is not (0 or 1 or 2 or 4 or 8 or 16))
                        Error("AN1083", $"`Pack` is 0, 1, 2, 4, 8 or 16, not {number}", source, span);
                    if (name == "Size" && number < 0)
                        Error("AN1083", $"`Size` is a number of bytes (0 or more), not {number}", source, span);
                }
                foreach (var (field, offset) in offsets)
                {
                    var fieldType = type.Definition.Fields[field.Index].Type;
                    if (HoldsObjects(fieldType))
                        Error("AN1081", $"`{field.Name}` is a `{H.Text.HirPrinter.Print(fieldType, Assembly)}`: a value with `[ValueLayout]` holds numbers, bools, pointers, `func*`, inline arrays and such values, not objects", source, field.Declaration?.Span ?? span);
                    if (isExplicit && offset is null)
                        Error("AN1082", $"`{field.Name}` needs `[FieldOffset(n)]`: `{type.Name}` is `Layout.Explicit`", source, field.Declaration?.Span ?? span);
                    else if (!isExplicit && offset is not null)
                        Error("AN1082", $"`[FieldOffset]` on `{field.Name}` needs `Layout.Explicit` on `{type.Name}`", source, field.Declaration?.Span ?? span);
                    else if (offset is { Arguments: [H.IntegerConstant { Value: var at }] } && at < 0)
                        Error("AN1082", $"`{field.Name}` is at {at}: an offset is 0 or more", source, field.Declaration?.Span ?? span);
                }
            }
        }

        /// <summary>Whether a value of this type holds an object the collector must see (a handle somewhere inside).</summary>
        private bool HoldsObjects(H.HirType type, int depth = 0) => type switch
        {
            H.PrimitiveType => false,
            H.PointerType or H.NativeFuncType => false,
            H.InlineArrayType inline => HoldsObjects(inline.Element, depth),
            H.NamedType named when SymbolOf(named) is { } symbol => symbol.IsEnum ? false
                : symbol.IsStruct && !symbol.IsPrimitive && depth < 16
                    ? symbol.Definition.Fields.Where(f => (f.Flags & (H.FieldFlags.Static | H.FieldFlags.Const)) == 0).Any(f => HoldsObjects(f.Type, depth + 1))
                    : !symbol.IsPrimitive,
            _ => true,
        };

        private void BindAttributes()
        {
            if (Types.GetValueOrDefault("System.Attribute") is not { } attributeClass)
            {
                attributeClass = null;
            }
            foreach (var type in Types.Values.Concat(Modules.Values).Where(t => !t.IsExternal).ToList())
            {
                if (type.Declaration is { } declaration)
                    type.Definition.Attributes.AddRange(Bind(declaration.Attributes, type.Scope, attributeClass));
                foreach (var method in type.Methods.Values.SelectMany(m => m).Concat(type.Constructors))
                {
                    var syntax = method.Function?.Attributes ?? method.Constructor?.Attributes;
                    if (syntax is { Length: > 0 })
                        method.Definition.Attributes.AddRange(Bind(syntax, method.Scope, attributeClass));
                    // `F([Tag("n")] i32 n)`: each parameter's own.
                    var parameters = method.Function?.Parameters ?? method.Constructor?.Parameters ?? [];
                    var defined = method.Definition.Parameters;
                    for (int p = 0; p < parameters.Length && p < defined.Count; p++)
                    {
                        if (parameters[p].Attributes.Length == 0 || method.Scope is not { } scope)
                            continue;
                        var bound = Bind(parameters[p].Attributes, scope, attributeClass).ToArray();
                        if (bound.Length > 0)
                            defined[p] = defined[p] with { Attributes = bound };
                    }
                }
                // An enum's members: `[Tag("rojo")] Red`.
                if (type.IsEnum && type.Declaration is { } enumDeclaration)
                {
                    foreach (var member in enumDeclaration.Members.OfType<EnumMemberDecl>())
                    {
                        if (member.Attributes.Length == 0 || !type.Fields.TryGetValue(member.Name, out var field))
                            continue;
                        var bound = Bind(member.Attributes, type.Scope, attributeClass).ToArray();
                        if (bound.Length > 0)
                            type.Definition.Fields[field.Index] = type.Definition.Fields[field.Index] with { Attributes = bound };
                    }
                }
                foreach (var field in type.Fields.Values)
                {
                    if (field.Declaration is { Attributes.Length: > 0 } fieldDecl && field.Scope is { } scope)
                    {
                        var bound = Bind(fieldDecl.Attributes, scope, attributeClass).ToArray();
                        if (bound.Length > 0)
                            type.Definition.Fields[field.Index] = type.Definition.Fields[field.Index] with { Attributes = bound };
                    }
                }
                foreach (var property in type.Properties.Values)
                {
                    if (property.Declaration is { Attributes.Length: > 0 } propertyDecl)
                    {
                        var bound = Bind(propertyDecl.Attributes, type.Scope, attributeClass).ToArray();
                        if (bound.Length > 0)
                            type.Definition.Properties[property.Index] = type.Definition.Properties[property.Index] with { Attributes = bound };
                    }
                }
            }
        }

        /// <summary>The attribute classes named in <paramref name="syntaxes"/>, bound (the compiler's own are skipped).</summary>
        private IEnumerable<H.HirAttribute> Bind(AttributeSyntax[] syntaxes, DeclarationScope scope, TypeSymbol? attributeClass)
        {
            foreach (var syntax in syntaxes)
            {
                string text = syntax.Name.ToString();
                if (CompilerAttributes.Contains(text))
                    continue;
                if (IsFriendOf(text) && syntax.Arguments is not [TypeOfExpr or NameExpr or MemberExpr])
                    continue; // AN1070 said it already
                var symbol = AttributeClass(syntax.Name, scope);
                if (symbol is null || attributeClass is null || !symbol.IsOrDerives(attributeClass))
                {
                    Error("AN1058", symbol is null
                        ? $"`{text}` is not an attribute: no class `{text}` or `{text}Attribute` derived from System.Attribute"
                        : $"`{symbol.Name}` is not an attribute class (it does not derive from System.Attribute)", scope.Source, syntax.Span);
                    continue;
                }
                if (symbol.IsAbstract)
                {
                    Error("AN1059", $"`{symbol.Name}` is abstract: an attribute is an object of it", scope.Source, syntax.Span);
                    continue;
                }
                var positional = syntax.Arguments.Where(a => a is not AssignExpr { Operator: null, Target: NameExpr }).ToArray();
                var namedSyntax = syntax.Arguments.OfType<AssignExpr>().Where(a => a is { Operator: null, Target: NameExpr }).ToArray();
                H.HirConstant[]? arguments = null;
                MethodSymbol? constructor = null;
                foreach (var candidate in symbol.Constructors.Where(c => c.Parameters.Length == positional.Length))
                {
                    var values = new H.HirConstant[positional.Length];
                    bool fits = true;
                    for (int i = 0; fits && i < positional.Length; i++)
                        fits = AttributeConstant(positional[i], candidate.Parameters[i], scope) is { } value && (values[i] = value) is not null;
                    if (fits)
                    {
                        constructor = candidate;
                        arguments = values;
                        break;
                    }
                }
                if (constructor is null || arguments is null)
                {
                    Error("AN1060", positional.Length == 0 && symbol.Constructors.Count == 0
                        ? $"`{symbol.Name}` has no constructor"
                        : $"no constructor of `{symbol.Name}` takes these arguments (attribute arguments are constants: numbers, bools, strings, enum members, typeof)", scope.Source, syntax.Span);
                    continue;
                }
                var named = new List<(string, H.HirConstant)>();
                foreach (var assignment in namedSyntax)
                {
                    string name = ((NameExpr)assignment.Target).Name;
                    H.HirType? memberType = symbol.Chain().Select(c => c.Type.Fields.GetValueOrDefault(name) is { IsStatic: false, IsConst: false } f ? f.Type
                        : c.Type.Properties.GetValueOrDefault(name) is { IsStatic: false, Setter: not null } p ? p.Type : null).FirstOrDefault(t => t is not null);
                    if (memberType is null)
                    {
                        Error("AN1061", $"`{symbol.Name}` has no field or settable property `{name}`", scope.Source, assignment.Target.Span);
                        continue;
                    }
                    if (AttributeConstant(assignment.Value, memberType, scope) is not { } value)
                    {
                        Error("AN1062", $"`{name}` takes a constant `{H.Text.HirPrinter.Print(memberType, Assembly)}`", scope.Source, assignment.Value.Span);
                        continue;
                    }
                    named.Add((name, value));
                }
                yield return new H.HirAttribute(constructor.Token, arguments) { Named = [.. named] };
            }
        }

        /// <summary>The class an attribute's name means: <c>Name</c>, else <c>NameAttribute</c>.</summary>
        private TypeSymbol? AttributeClass(QualifiedName name, DeclarationScope scope)
        {
            Expr Named(string[] parts)
            {
                Expr expression = new NameExpr(parts[0], [], name.Span);
                for (int i = 1; i < parts.Length; i++)
                    expression = new MemberExpr(expression, parts[i], [], name.Span);
                return expression;
            }
            if (TypeNamed(Named(name.Parts), scope) is { IsClass: true } plain)
                return plain;
            var suffixed = name.Parts.ToArray();
            suffixed[^1] += "Attribute";
            return TypeNamed(Named(suffixed), scope) is { IsClass: true } longer ? longer : null;
        }

        /// <summary>An attribute argument: a constant of <paramref name="type"/> (also strings and <c>typeof</c>).</summary>
        private H.HirConstant? AttributeConstant(Expr expression, H.HirType type, DeclarationScope scope)
        {
            switch (expression)
            {
                case StringLiteral text when type == StringType || type is H.NullableType { Inner: var inner } && inner == StringType:
                    return new H.StringConstant(text.Value);
                case TypeOfExpr typeOf when Types.GetValueOrDefault("System.Type") is { } typeClass && (type == typeClass.Type || type == new H.NullableType(typeClass.Type)):
                {
                    var bag = new DiagnosticBag();
                    return ResolveType(typeOf.Type, scope, bag) is { } described && !bag.HasErrors ? new H.TypeConstant(described) : null;
                }
                case NameExpr or MemberExpr when Types.GetValueOrDefault("System.Type") is { } typeClass && (type == typeClass.Type || type == new H.NullableType(typeClass.Type))
                    && TypeNamed(expression, scope) is { } named:
                    return new H.TypeConstant(named.Type); // `[FriendOf(Reader)]`: a type's name where a Type goes
                case NullLiteral when type is H.NullableType:
                    return new H.NullConstant();
            }
            var nonNull = type is H.NullableType { Inner: var held } ? held : type;
            return Constant(expression, nonNull, scope, new DiagnosticBag(), SymbolOf(nonNull) is { IsEnum: true } e ? e : null);
        }
    }
}
