using System.Numerics;
using H = Larva.Hir;

namespace Antril.Syntax
{
    /// <summary>How an operator with one operand is written: before it, after it, or either side (<c>op</c> without <c>left</c> or <c>right</c>).</summary>
    public enum OperatorForm : byte { Binary, Prefix, Postfix, Both }

    /// <summary>
    /// The operators a program may write besides the built-in ones: symbols declared by the libraries it references and by
    /// its own types (<c>op ^^(…) : … precedence(PrecedenceLevel.Power) associativity(Associativity.Right)</c>). The parser
    /// needs them to read <c>a ^^ b</c>: a symbol is a run of operator characters written without spaces, and its precedence
    /// (a level number: the higher, the tighter it binds) says how the expression groups. Precedence belongs to the symbol:
    /// every declaration of it agrees, and the built-in symbols keep theirs. Operators with one operand bind tighter than
    /// any binary one and declare no precedence.
    /// </summary>
    public sealed class OperatorTable
    {
        private readonly Dictionary<string, (int Level, H.Associativity Associativity)> binary = [];
        private readonly HashSet<string> prefix = [], postfix = [];

        /// <summary>The built-in table: no declared symbols.</summary>
        public static OperatorTable Empty { get; } = new();

        /// <summary>Whether any symbol was declared (when none, the parser skips the lookups).</summary>
        public bool HasDeclared => binary.Count > 0 || prefix.Count > 0 || postfix.Count > 0;

        /// <summary>The levels of <c>PrecedenceLevel</c> (System.Core), known to the compiler too.</summary>
        public static readonly IReadOnlyDictionary<string, int> Levels = new Dictionary<string, int>
        {
            ["Assignment"] = 10, ["Conditional"] = 20, ["Coalesce"] = 30, ["LogicalOr"] = 40, ["LogicalAnd"] = 50,
            ["BitwiseOr"] = 60, ["BitwiseXor"] = 70, ["BitwiseAnd"] = 80, ["Equality"] = 90, ["Relational"] = 100,
            ["Range"] = 110, ["Shift"] = 120, ["Additive"] = 130, ["Multiplicative"] = 140, ["Power"] = 141,
        };

        /// <summary>The binary symbols the language has (they keep their precedence).</summary>
        public static readonly HashSet<string> BuiltinBinary =
            ["??", "||", "&&", "|", "^", "&", "==", "!=", "<", "<=", ">", ">=", "..", "..=", "<<", ">>", "+", "-", "*", "/", "%"];

        /// <summary>The symbols the language writes before an operand.</summary>
        public static readonly HashSet<string> BuiltinPrefix = ["-", "+", "!", "~", "++", "--", "&", "*"];

        /// <summary>Symbols that cannot be an operator (they mean something else on their own).</summary>
        public static readonly HashSet<string> Reserved = ["=", ".", "=>", "?", "..", "->", "...", "..="];

        /// <summary>Tokens made of operator characters: a run of adjacent ones is a symbol.</summary>
        public static bool IsSymbolToken(TokenKind kind) => kind is TokenKind.Plus or TokenKind.PlusPlus or TokenKind.PlusEqual
            or TokenKind.Minus or TokenKind.MinusMinus or TokenKind.MinusEqual or TokenKind.ThinArrow
            or TokenKind.Star or TokenKind.StarEqual or TokenKind.Slash or TokenKind.SlashEqual or TokenKind.Percent or TokenKind.PercentEqual
            or TokenKind.Ampersand or TokenKind.AmpersandAmpersand or TokenKind.AmpersandEqual
            or TokenKind.Pipe or TokenKind.PipePipe or TokenKind.PipeEqual or TokenKind.Caret or TokenKind.CaretEqual
            or TokenKind.Tilde or TokenKind.Bang or TokenKind.BangEqual or TokenKind.Equal or TokenKind.EqualEqual or TokenKind.Arrow
            or TokenKind.Less or TokenKind.LessEqual or TokenKind.LessLess or TokenKind.LessLessEqual
            or TokenKind.Greater or TokenKind.GreaterEqual or TokenKind.Question or TokenKind.QuestionQuestion
            or TokenKind.At or TokenKind.Hash or TokenKind.Dollar;

        /// <summary>A declared binary symbol's precedence and associativity.</summary>
        public bool TryBinary(string symbol, out int level, out H.Associativity associativity)
        {
            if (binary.TryGetValue(symbol, out var found))
            {
                (level, associativity) = found;
                return true;
            }
            (level, associativity) = (0, H.Associativity.Left);
            return false;
        }

        public bool IsPrefix(string symbol) => prefix.Contains(symbol);

        public bool IsPostfix(string symbol) => postfix.Contains(symbol);

        /// <summary>Records a symbol; a binary one that was recorded with another precedence is reported (the first wins).</summary>
        public void Add(string symbol, OperatorForm form, int level, H.Associativity associativity, SourceText? source, TextSpan span, DiagnosticBag? diagnostics)
        {
            switch (form)
            {
                case OperatorForm.Binary:
                    if (binary.TryGetValue(symbol, out var existing))
                    {
                        if ((existing.Level != level || existing.Associativity != associativity) && source is not null)
                            diagnostics?.Error("AN1065", $"`{symbol}` already has precedence {existing.Level} ({existing.Associativity.ToString().ToLowerInvariant()}): a symbol's precedence is the same in every declaration", source, span);
                        return;
                    }
                    binary[symbol] = (level, associativity);
                    break;
                case OperatorForm.Prefix:
                    prefix.Add(symbol);
                    break;
                case OperatorForm.Postfix:
                    postfix.Add(symbol);
                    break;
                default:
                    prefix.Add(symbol);
                    postfix.Add(symbol);
                    break;
            }
        }

        /// <summary>The symbols the referenced assemblies' types declare (their precedence travels in the HIR).</summary>
        public static OperatorTable FromReferences(IReadOnlyList<H.HirAssembly>? references)
        {
            var table = new OperatorTable();
            foreach (var assembly in references ?? [])
            {
                foreach (var type in assembly.Types)
                {
                    foreach (var op in type.Operators)
                    {
                        var form = op.Fixity switch { H.Fixity.Prefix => OperatorForm.Prefix, H.Fixity.Postfix => OperatorForm.Postfix, _ => OperatorForm.Binary };
                        if (form == OperatorForm.Binary ? BuiltinBinary.Contains(op.Symbol) : BuiltinPrefix.Contains(op.Symbol) && form == OperatorForm.Prefix)
                            continue;
                        table.Add(op.Symbol, form, op.Precedence, op.Associativity, null, default, null);
                    }
                }
            }
            return table;
        }

        /// <summary>
        /// Records the operators <paramref name="units"/> declare, with their precedence evaluated (a level of
        /// <c>PrecedenceLevel</c>, a number, or a member of an enum of the program or its references). Returns whether any
        /// new symbol was added: the units are then parsed again, since their bodies may use it.
        /// </summary>
        public bool AddDeclared(IReadOnlyList<CompilationUnit> units, IReadOnlyList<H.HirAssembly>? references, DiagnosticBag diagnostics)
        {
            bool added = false;
            foreach (var unit in units)
            {
                foreach (var function in Operators(unit.Members))
                {
                    string symbol = function.OperatorSymbol!;
                    var source = unit.Source;
                    bool builtin = function.Form == OperatorForm.Binary ? BuiltinBinary.Contains(symbol) : BuiltinPrefix.Contains(symbol) && function.Form != OperatorForm.Postfix;
                    if (builtin)
                    {
                        if (function.Precedence is { } p)
                            diagnostics.Error("AN1066", $"`{symbol}` is built in: it keeps its precedence and associativity", source, p.Span);
                        else if (function.Associativity is { } a)
                            diagnostics.Error("AN1066", $"`{symbol}` is built in: it keeps its precedence and associativity", source, a.Span);
                        continue;
                    }
                    if (function.Form != OperatorForm.Binary)
                    {
                        if ((function.Precedence ?? function.Associativity) is { } given)
                            diagnostics.Error("AN1068", "an operator with one operand declares no precedence: it binds tighter than any binary operator", source, given.Span);
                        if (function.Form == OperatorForm.Both ? !prefix.Contains(symbol) || !postfix.Contains(symbol) : function.Form == OperatorForm.Prefix ? !prefix.Contains(symbol) : !postfix.Contains(symbol))
                            added = true;
                        Add(symbol, function.Form, 0, H.Associativity.Left, source, function.NameSpan, diagnostics);
                        continue;
                    }
                    int level;
                    if (function.Precedence is null)
                    {
                        if (binary.ContainsKey(symbol))
                            continue; // Declared with its precedence elsewhere (another type, a library).
                        diagnostics.Error("AN1067", $"`{symbol}` is a new symbol: write its precedence (`precedence(PrecedenceLevel.Additive)`, a number, or a member of an enum)", source, function.NameSpan);
                        continue;
                    }
                    if (Level(function.Precedence, units, references) is not { } found)
                    {
                        diagnostics.Error("AN1069", "a precedence is a level of `PrecedenceLevel`, a number, or a constant member of an enum", source, function.Precedence.Span);
                        continue;
                    }
                    level = found;
                    var associativity = H.Associativity.Left;
                    if (function.Associativity is { } written)
                    {
                        string? word = written switch
                        {
                            MemberExpr { Target: NameExpr { Name: "Associativity" }, Name: var n } => n,
                            NameExpr { Name: var n } => n,
                            _ => null,
                        };
                        if (word is "Left" or "Right" or "None")
                            associativity = Enum.Parse<H.Associativity>(word);
                        else
                            diagnostics.Error("AN1069", "an associativity is `Associativity.Left`, `Associativity.Right` or `Associativity.None`", source, written.Span);
                    }
                    if (!binary.ContainsKey(symbol))
                        added = true;
                    Add(symbol, OperatorForm.Binary, level, associativity, source, function.Precedence.Span, diagnostics);
                }
            }
            return added;
        }

        private static IEnumerable<FunctionDecl> Operators(IEnumerable<MemberDecl> members)
        {
            foreach (var member in members)
            {
                switch (member)
                {
                    case FunctionDecl { OperatorSymbol: not null } function:
                        yield return function;
                        break;
                    case NamespaceDecl ns:
                        foreach (var inner in Operators(ns.Members))
                            yield return inner;
                        break;
                    case TypeDecl type:
                        foreach (var inner in Operators(type.Members))
                            yield return inner;
                        break;
                }
            }
        }

        /// <summary>The number a precedence expression names, evaluated without binding (see <see cref="AddDeclared"/>).</summary>
        private static int? Level(Expr expression, IReadOnlyList<CompilationUnit> units, IReadOnlyList<H.HirAssembly>? references)
        {
            switch (expression)
            {
                case IntegerLiteral i when i.Value >= 0 && i.Value <= 100000:
                    return (int)i.Value;
                case NameExpr { Name: var level } when Levels.TryGetValue(level, out int value):
                    return value;
                case MemberExpr { Target: NameExpr { Name: "PrecedenceLevel" }, Name: var level } when Levels.TryGetValue(level, out int value):
                    return value;
                case MemberExpr { Target: NameExpr { Name: var enumName }, Name: var member }:
                {
                    foreach (var unit in units)
                    {
                        if (Enums(unit.Members).FirstOrDefault(e => e.Name == enumName) is { } declared && EnumMember(declared, member) is { } value)
                            return (int)value;
                    }
                    foreach (var assembly in references ?? [])
                    {
                        foreach (var type in assembly.Types)
                        {
                            if (type.Kind is not (H.TypeKind.Enum or H.TypeKind.Bitflags) || type.Name != enumName && !type.Name.EndsWith("." + enumName, StringComparison.Ordinal))
                                continue;
                            if (type.Fields.FirstOrDefault(f => f.Name == member)?.Value is H.EnumConstant constant)
                                return (int)constant.Value;
                        }
                    }
                    return null;
                }
            }
            return null;
        }

        private static IEnumerable<TypeDecl> Enums(IEnumerable<MemberDecl> members)
        {
            foreach (var member in members)
            {
                switch (member)
                {
                    case TypeDecl { Kind: TypeKind.Enum or TypeKind.BitflagEnum } type:
                        yield return type;
                        break;
                    case NamespaceDecl ns:
                        foreach (var inner in Enums(ns.Members))
                            yield return inner;
                        break;
                    case TypeDecl type:
                        foreach (var inner in Enums(type.Members))
                            yield return inner;
                        break;
                }
            }
        }

        /// <summary>An enum member's value read from the syntax: literals, the members before it, `declbits`, `allbits`.</summary>
        private static BigInteger? EnumMember(TypeDecl declared, string member)
        {
            bool bitflags = declared.Kind == TypeKind.BitflagEnum;
            var values = new Dictionary<string, BigInteger>();
            BigInteger next = bitflags ? 1 : 0;
            foreach (var m in declared.Members.OfType<EnumMemberDecl>())
            {
                BigInteger? value = m.Value switch
                {
                    null => next,
                    IntegerLiteral i => i.Value,
                    NameExpr { Name: "allbits" } => BigInteger.MinusOne,
                    NameExpr { Name: "declbits" } => values.Values.Aggregate(BigInteger.Zero, (a, b) => a | b),
                    NameExpr { Name: var other } when values.TryGetValue(other, out var earlier) => earlier,
                    _ => null,
                };
                if (value is null)
                    return null;
                values[m.Name] = value.Value;
                if (m.Value is null or IntegerLiteral)
                    next = bitflags ? (value.Value <= 0 ? 1 : value.Value * 2) : value.Value + 1;
            }
            return values.TryGetValue(member, out var found) ? found : null;
        }
    }
}
