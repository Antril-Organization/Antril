using H = Larva.Hir;

namespace Antril.Binding
{
    /// <summary>What a project sets for its compilation (its <c>.queen</c>'s BUILD chunk): the language's defaults otherwise.</summary>
    public sealed record AntrilOptions
    {
        public static readonly AntrilOptions Default = new();

        /// <summary>The access of declarations that say none (BUILD <c>access.default</c>); null: the language's (types internal, members private).</summary>
        public H.Access? DefaultAccess { get; init; }

        /// <summary>
        /// Whether the program may borrow (<c>&amp;x</c>, <c>&amp;mut x</c>, <c>&amp;T</c> and <c>&amp;mut T</c> types): off when the
        /// project turns the borrow checker off (BUILD <c>borrowcheck: off</c>), so a borrow nothing checks is an error.
        /// </summary>
        public bool Borrows { get; init; } = true;

        /// <summary>
        /// Whether the program may use unsafe code (<c>unsafe</c> types, functions and blocks, pointer types): off with BUILD
        /// <c>unsafe: deny</c>, for <see cref="SafeFiles"/> (the project's own files; null: every file).
        /// </summary>
        public bool Unsafe { get; init; } = true;

        /// <summary>With <see cref="Unsafe"/> off, the files it applies to (full paths): its dependencies' keep what they allow.</summary>
        public IReadOnlySet<string>? SafeFiles { get; init; }

        /// <summary>Whether code in <paramref name="file"/> may not be unsafe.</summary>
        public bool DeniesUnsafe(string file) =>
            !Unsafe && (SafeFiles is null || file.Length > 0 && SafeFiles.Contains(System.IO.Path.GetFullPath(file)));

        /// <summary>What a file that denies it is told when it uses unsafe code.</summary>
        public const string UnsafeDenied = "this project denies unsafe code (BUILD `unsafe: deny`): no `unsafe` types, functions or blocks, and no pointers; allow it with `unsafe: allow`";

        /// <summary>The access a word names (`public`, `file`…), or null.</summary>
        public static H.Access? AccessNamed(string? word) => word switch
        {
            "public" => H.Access.Public,
            "private" => H.Access.Private,
            "internal" => H.Access.Internal,
            "friendly" => H.Access.Friendly,
            "inherited" => H.Access.Inherited,
            "file" => H.Access.File,
            _ => null,
        };
    }
}
