using Antril.Syntax;
using H = Larva.Hir;

namespace Antril.Binding
{
    /// <summary>
    /// Access checks. <c>public</c> is seen everywhere; <c>internal</c> in the assembly; <c>file</c> in the file; <c>private</c>
    /// in the type and its nested types; <c>inherited</c> there and in the types derived from it (from any assembly);
    /// <c>friendly</c> there and in its friends: <c>[FriendOf(T)]</c> on either type makes the two friends, from any
    /// assembly (the attribute is kept in the HIR, so a referenced assembly's friendships are read back). Interface
    /// members, operators, conversions and enum members are public. A type that is not accessible is AN1400, a member AN1401.
    /// </summary>
    internal sealed partial class Binder
    {
        private static bool IsFriendOf(string name) =>
            name is "FriendOf" or "System.FriendOf" or "FriendOfAttribute" or "System.FriendOfAttribute";

        /// <summary>Reads every <c>[FriendOf(T)]</c> of the program's types (before members are declared: their types are checked).</summary>
        private void DeclareFriends()
        {
            foreach (var type in Types.Values.ToList())
            {
                if (type.Declaration is not { } declaration)
                    continue;
                foreach (var attribute in declaration.Attributes)
                {
                    if (!IsFriendOf(attribute.Name.ToString()))
                        continue;
                    TypeSymbol? friend = attribute.Arguments is [TypeOfExpr typeOf]
                        ? ResolveType(typeOf.Type, type.Scope, new DiagnosticBag()) is { } resolved ? SymbolOf(resolved) : null
                        : attribute.Arguments is [var argument] ? TypeNamed(argument, type.Scope) : null;
                    if (friend is null)
                    {
                        Error("AN1070", "`[FriendOf(T)]` names one type", type.Scope.Source, attribute.Span);
                        continue;
                    }
                    if (friend == type)
                    {
                        Error("AN1071", $"`{type.Name}` sees its own members already", type.Scope.Source, attribute.Span);
                        continue;
                    }
                    type.Friends.Add(friend);
                    friend.Friends.Add(type);
                }
            }
        }

        /// <summary>The full name of the type whose constructor an attribute of <paramref name="reference"/> calls.</summary>
        private static string AttributeOwner(H.HirAssembly reference, H.MemberToken constructor)
        {
            var parent = constructor.IsDefinition ? H.TypeToken.Def(constructor.Type) : reference.MemberRefs[constructor.Member].Parent;
            if (parent.IsDefinition)
            {
                var type = reference.Types[parent.Index];
                return type.Namespace.Length == 0 ? type.Name : $"{type.Namespace}.{type.Name}";
            }
            var other = reference.TypeRefs[parent.Index];
            return other.Namespace.Length == 0 ? other.Name : $"{other.Namespace}.{other.Name}";
        }

        /// <summary>
        /// Whether code in <paramref name="from"/> (null: top-level code), written in <paramref name="source"/>, may use something
        /// of <paramref name="declaring"/> with <paramref name="access"/>, declared in <paramref name="declaredIn"/>.
        /// </summary>
        internal static bool Accessible(H.Access access, TypeSymbol declaring, SourceText? declaredIn, TypeSymbol? from, SourceText source)
        {
            switch (access)
            {
                case H.Access.Public:
                    return true;
                case H.Access.Internal:
                    return !declaring.IsExternal;
                case H.Access.File:
                    return !declaring.IsExternal && declaredIn is not null && (ReferenceEquals(declaredIn, source) || declaredIn.Path.Length > 0 && declaredIn.Path == source.Path);
                case H.Access.Private:
                    return from is not null && from.IsWithin(declaring);
                case H.Access.Inherited:
                    return from is not null && from.Outers().Any(t => t.IsOrDerives(declaring));
                default:
                    return from is not null && from.Outers().Any(t => t.IsWithin(declaring) || declaring.Outers().Any(d => d.Friends.Contains(t)));
            }
        }

        /// <summary>Why something with <paramref name="access"/> of <paramref name="declaring"/> is not seen, for an error.</summary>
        internal static string Describe(H.Access access, TypeSymbol declaring) => declaring.IsModule ? access switch
        {
            // A namespace's own functions, fields and constants (no type to name).
            H.Access.Private => "private to its namespace",
            H.Access.Internal => "internal to its assembly",
            H.Access.File => "only seen in the file that declares it",
            _ => $"{access.ToString().ToLowerInvariant()}: not seen here",
        } : access switch
        {
            H.Access.Private => $"private to `{declaring.Name}`",
            H.Access.Internal => $"internal to the assembly of `{declaring.Name}`",
            H.Access.File => $"only seen in the file that declares `{declaring.Name}`",
            H.Access.Inherited => $"inherited: only for `{declaring.Name}` and the types derived from it",
            _ => $"friendly: only for `{declaring.Name}` and its friends (`[FriendOf({declaring.Name})]`)",
        };

        /// <summary>Reports AN1400 when <paramref name="type"/>, named at <paramref name="span"/>, is not accessible from <paramref name="scope"/>.</summary>
        internal void CheckTypeAccess(TypeSymbol type, DeclarationScope scope, TextSpan span, DiagnosticBag bag)
        {
            var declaring = type.Outer ?? type;
            if (Accessible(type.Definition.Access, declaring, type.Scope.Source, scope.EnclosingSymbol, scope.Source))
                return;
            bag.Error("AN1400", $"`{type.Name}` is {Describe(type.Definition.Access, declaring)}", scope.Source, span);
        }
    }
}
