namespace Antril.Syntax
{
    /// <summary>
    /// The files an editor's program was parsed from, kept between analyses: the program is parsed again on every change,
    /// and a file whose path and text are the ones parsed before (with the same declared operators, an
    /// <see cref="OperatorTable.Key"/>) gives the unit and the diagnostics it gave then. Units are immutable, so one serves
    /// every analysis that has the file as it was. Safe to use from several threads.
    /// </summary>
    public sealed class ParseCache
    {
        private sealed record Entry(CompilationUnit Unit, DiagnosticBag Bag);

        private readonly Dictionary<(string Path, string Table), Entry> entries = [];

        /// <summary>How many files were found parsed already, and how many were parsed (for tests and timings).</summary>
        public int Hits { get; private set; }
        public int Misses { get; private set; }

        internal bool TryGet(SourceText file, string table, out CompilationUnit unit, out DiagnosticBag bag)
        {
            lock (entries)
            {
                if (entries.TryGetValue((file.Path, table), out var entry) && string.Equals(entry.Unit.Source.Text, file.Text, StringComparison.Ordinal))
                {
                    Hits++;
                    (unit, bag) = (entry.Unit, entry.Bag);
                    return true;
                }
                Misses++;
            }
            (unit, bag) = (null!, null!);
            return false;
        }

        internal void Add(string table, CompilationUnit unit, DiagnosticBag bag)
        {
            lock (entries)
                entries[(unit.Source.Path, table)] = new Entry(unit, bag);
        }

        /// <summary>Forgets the files that are not among <paramref name="paths"/> (no longer part of the program).</summary>
        public void Keep(IEnumerable<string> paths)
        {
            var kept = new HashSet<string>(paths, StringComparer.Ordinal);
            lock (entries)
            {
                foreach (var key in entries.Keys.Where(k => !kept.Contains(k.Path)).ToList())
                    entries.Remove(key);
            }
        }

        public void Clear()
        {
            lock (entries)
                entries.Clear();
        }
    }
}
