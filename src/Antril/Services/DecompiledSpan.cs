namespace Antril.Services
{
    using Larva.Hir.Services;

    /// <summary>What a name written into a decompiled source is (recorded while it is written).</summary>
    internal readonly record struct DecompiledMark(SemanticKind Kind, SemanticModifiers Modifiers, CatalogType? Type, CatalogMember? Member, bool Colored = true);

    /// <summary>
    /// A name of a decompiled source: where it is in the text, how the editor colors it, and the type or member it names
    /// (where the editor goes from it). A keyword that names a type (<c>i32</c>) is not <see cref="Colored"/>: only a link.
    /// </summary>
    public readonly record struct DecompiledSpan(int Offset, int Length, SemanticKind Kind, SemanticModifiers Modifiers, CatalogType? Type, CatalogMember? Member, bool Colored = true);
}
