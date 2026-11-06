namespace Antril.Syntax
{
    /// <summary>A range of characters in a source file.</summary>
    public readonly record struct TextSpan(int Start, int Length)
    {
        public int End => Start + Length;
        public static TextSpan FromBounds(int start, int end) => new(start, Math.Max(0, end - start));
        public override string ToString() => $"{Start}..{End}";
    }

    /// <summary>A source file: its text, its path, and where its lines start.</summary>
    public sealed class SourceText
    {
        private int[]? lineStarts;

        public SourceText(string text, string path = "")
        {
            Text = text;
            Path = path;
        }

        public string Text { get; }
        public string Path { get; }

        public static SourceText FromFile(string path) => new(File.ReadAllText(path), path);

        public string this[TextSpan span] => Text.Substring(span.Start, Math.Min(span.Length, Text.Length - span.Start));

        /// <summary>1-based line and column of <paramref name="position"/>.</summary>
        public (int Line, int Column) LineColumn(int position)
        {
            lineStarts ??= LineStarts(Text);
            int index = Array.BinarySearch(lineStarts, position);
            if (index < 0)
                index = ~index - 1;
            return (index + 1, position - lineStarts[index] + 1);
        }

        private static int[] LineStarts(string text)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                    starts.Add(i + 1);
            }
            return [.. starts];
        }

        /// <summary>
        /// The <c>///</c> lines just above <paramref name="position"/> (attribute lines between them and it skipped), joined
        /// with line breaks; null when there are none.
        /// </summary>
        public string? DocBefore(int position)
        {
            int end = position;
            while (end > 0 && Text[end - 1] != '\n')
                end--;
            List<string>? lines = null;
            while (end > 0)
            {
                int lineEnd = end - 1; // the line break ending the line above
                int lineStart = lineEnd;
                while (lineStart > 0 && Text[lineStart - 1] != '\n')
                    lineStart--;
                var line = Text.AsSpan(lineStart, lineEnd - lineStart).Trim();
                if (line.StartsWith("///"))
                    (lines ??= []).Add(line[3..].Trim().ToString());
                else if (lines is not null || !(line.StartsWith("[") && line.EndsWith("]")))
                    break;
                end = lineStart;
            }
            if (lines is null)
                return null;
            lines.Reverse();
            return string.Join("\n", lines);
        }

        public override string ToString() => Path;
    }

    public enum DiagnosticSeverity : byte { Error, Warning }

    public sealed record Diagnostic(DiagnosticSeverity Severity, string Code, string Message, SourceText Source, TextSpan Span)
    {
        public bool IsError => Severity == DiagnosticSeverity.Error;

        public override string ToString()
        {
            var (line, column) = Source.LineColumn(Span.Start);
            string where = Source.Path.Length > 0 ? $"{Source.Path}({line},{column})" : $"({line},{column})";
            return $"{where}: {(IsError ? "error" : "warning")} {Code}: {Message}";
        }
    }

    /// <summary>Diagnostics in the order they were reported. Not thread-safe: each file reports into its own bag.</summary>
    public sealed class DiagnosticBag : List<Diagnostic>
    {
        public bool HasErrors => this.Any(d => d.IsError);

        public void Error(string code, string message, SourceText source, TextSpan span) =>
            Add(new Diagnostic(DiagnosticSeverity.Error, code, message, source, span));

        public void Warning(string code, string message, SourceText source, TextSpan span) =>
            Add(new Diagnostic(DiagnosticSeverity.Warning, code, message, source, span));
    }
}
