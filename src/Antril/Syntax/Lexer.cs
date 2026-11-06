using System.Globalization;
using System.Numerics;
using System.Text;

namespace Antril.Syntax
{
    public enum TokenKind : byte
    {
        EndOfFile, Identifier, Integer, Float, String, Char,

        // Interpolated strings: $" … {expression,alignment:format} … " is InterpolationStart, then String tokens for the
        // text and HoleStart, the expression's tokens, an optional Comma and alignment, an optional HoleFormat, HoleEnd
        // for each hole, then InterpolationEnd.
        InterpolationStart, InterpolationEnd, HoleStart, HoleFormat, HoleEnd,

        // Punctuation
        OpenParen, CloseParen, OpenBracket, CloseBracket, OpenBrace, CloseBrace,
        Semicolon, Comma, Dot, Colon, Question, At, Hash, Dollar,
        DotDot, DotDotEqual, Ellipsis, Arrow, QuestionDot, QuestionQuestion, ThinArrow,

        // Operators ('>' never merges with a following '>': the parser joins adjacent ones into shifts, so `A<B<C>>` closes)
        Plus, Minus, Star, Slash, Percent, Ampersand, Pipe, Caret, Tilde, Bang,
        Equal, EqualEqual, BangEqual, Less, LessEqual, Greater, GreaterEqual, LessLess,
        AmpersandAmpersand, PipePipe, PlusPlus, MinusMinus,
        PlusEqual, MinusEqual, StarEqual, SlashEqual, PercentEqual, AmpersandEqual, PipeEqual, CaretEqual, LessLessEqual,

        // Keywords
        Import, Into, Namespace, Public, Friendly, Internal, Inherited, Fixed, Readonly,
        Class, Interface, Enum, Bitflag, New, Destroy, Static, Virtual, Override,
        If, Else, Loop, Do, In, Switch, Try, Catch, Finally, Throw, Return, Break, Continue,
        True, False, Null, TypeOf, SizeOf, Is, As, Void, DeclBits, AllBits,

        // Primitive type keywords
        Bool, I8, I16, I32, I64, I128, I256, U8, U16, U32, U64, U128, U256, IPtr, UPtr, F16, F32, F64, F128, F256,
    }

    /// <summary>A token: its kind, where it is, its text (identifiers are interned), and its value (numbers, strings).</summary>
    public readonly record struct Token(TokenKind Kind, TextSpan Span, string Text, object? Value = null)
    {
        /// <summary>A number's type suffix (<c>12u32</c>, <c>1.5f32</c>): the keyword written, or null.</summary>
        public string? Suffix { get; init; }

        public override string ToString() => Kind == TokenKind.EndOfFile ? "end of file" : $"'{Text}'";
    }

    /// <summary>
    /// Turns a <c>.ant</c> file into tokens. Contextual words (<c>private</c>, <c>file</c>, <c>base</c>, <c>value</c>,
    /// <c>record</c>, <c>final</c>, <c>unsafe</c>, <c>extern</c>, <c>when</c>, <c>and</c>, <c>or</c>, <c>not</c>, <c>var</c>,
    /// <c>mut</c>, <c>delete</c>…) stay identifiers: the parser gives them meaning where they apply.
    /// </summary>
    public sealed class Lexer
    {
        public static readonly IReadOnlyDictionary<string, TokenKind> Keywords = new Dictionary<string, TokenKind>
        {
            ["import"] = TokenKind.Import, ["into"] = TokenKind.Into, ["namespace"] = TokenKind.Namespace,
            ["public"] = TokenKind.Public, ["friendly"] = TokenKind.Friendly, ["internal"] = TokenKind.Internal,
            ["inherited"] = TokenKind.Inherited, ["fixed"] = TokenKind.Fixed, ["readonly"] = TokenKind.Readonly,
            ["class"] = TokenKind.Class, ["interface"] = TokenKind.Interface, ["enum"] = TokenKind.Enum,
            ["bitflag"] = TokenKind.Bitflag, ["new"] = TokenKind.New,
            ["static"] = TokenKind.Static, ["virtual"] = TokenKind.Virtual, ["override"] = TokenKind.Override,
            ["if"] = TokenKind.If, ["else"] = TokenKind.Else, ["loop"] = TokenKind.Loop, ["do"] = TokenKind.Do,
            ["in"] = TokenKind.In, ["switch"] = TokenKind.Switch, ["try"] = TokenKind.Try, ["catch"] = TokenKind.Catch,
            ["finally"] = TokenKind.Finally, ["throw"] = TokenKind.Throw, ["return"] = TokenKind.Return,
            ["break"] = TokenKind.Break, ["continue"] = TokenKind.Continue, ["true"] = TokenKind.True,
            ["false"] = TokenKind.False, ["null"] = TokenKind.Null, ["typeof"] = TokenKind.TypeOf,
            ["sizeof"] = TokenKind.SizeOf, ["is"] = TokenKind.Is, ["as"] = TokenKind.As, ["void"] = TokenKind.Void,
            ["declbits"] = TokenKind.DeclBits, ["allbits"] = TokenKind.AllBits,
            ["bool"] = TokenKind.Bool,
            ["i8"] = TokenKind.I8, ["i16"] = TokenKind.I16, ["i32"] = TokenKind.I32, ["i64"] = TokenKind.I64,
            ["i128"] = TokenKind.I128, ["i256"] = TokenKind.I256, ["u8"] = TokenKind.U8, ["u16"] = TokenKind.U16,
            ["u32"] = TokenKind.U32, ["u64"] = TokenKind.U64, ["u128"] = TokenKind.U128, ["u256"] = TokenKind.U256,
            ["iptr"] = TokenKind.IPtr, ["uptr"] = TokenKind.UPtr, ["f16"] = TokenKind.F16, ["f32"] = TokenKind.F32,
            ["f64"] = TokenKind.F64, ["f128"] = TokenKind.F128, ["f256"] = TokenKind.F256,
        };

        public static bool IsPrimitive(TokenKind kind) => kind is >= TokenKind.Bool and <= TokenKind.F256;

        private readonly SourceText source;
        private readonly DiagnosticBag diagnostics;
        private readonly string text;
        /// <summary>Words seen so far (keywords included from the start): one lookup gives the interned text and the kind.</summary>
        private readonly Dictionary<string, (string Text, TokenKind Kind)> words = NewWords();

        private static Dictionary<string, (string Text, TokenKind Kind)> NewWords()
        {
            var table = new Dictionary<string, (string, TokenKind)>(256, StringComparer.Ordinal);
            foreach (var (word, kind) in Keywords)
                table[word] = (word, kind);
            return table;
        }

        /// <summary>The text of each punctuation kind, shared by every token of that kind.</summary>
        private static readonly string[] Punctuation = MakePunctuation();

        private static string[] MakePunctuation()
        {
            var table = new string[(int)TokenKind.F256 + 1];
            void Set(TokenKind kind, string text) => table[(int)kind] = text;
            Set(TokenKind.OpenParen, "("); Set(TokenKind.CloseParen, ")"); Set(TokenKind.OpenBracket, "["); Set(TokenKind.CloseBracket, "]");
            Set(TokenKind.OpenBrace, "{"); Set(TokenKind.CloseBrace, "}"); Set(TokenKind.Semicolon, ";"); Set(TokenKind.Comma, ",");
            Set(TokenKind.Dot, "."); Set(TokenKind.Colon, ":"); Set(TokenKind.Question, "?"); Set(TokenKind.At, "@"); Set(TokenKind.Hash, "#");
            Set(TokenKind.Dollar, "$"); Set(TokenKind.DotDot, ".."); Set(TokenKind.DotDotEqual, "..="); Set(TokenKind.Ellipsis, "...");
            Set(TokenKind.Arrow, "=>"); Set(TokenKind.QuestionDot, "?."); Set(TokenKind.QuestionQuestion, "??"); Set(TokenKind.ThinArrow, "->");
            Set(TokenKind.Plus, "+"); Set(TokenKind.Minus, "-"); Set(TokenKind.Star, "*"); Set(TokenKind.Slash, "/"); Set(TokenKind.Percent, "%");
            Set(TokenKind.Ampersand, "&"); Set(TokenKind.Pipe, "|"); Set(TokenKind.Caret, "^"); Set(TokenKind.Tilde, "~"); Set(TokenKind.Bang, "!");
            Set(TokenKind.Equal, "="); Set(TokenKind.EqualEqual, "=="); Set(TokenKind.BangEqual, "!="); Set(TokenKind.Less, "<");
            Set(TokenKind.LessEqual, "<="); Set(TokenKind.Greater, ">"); Set(TokenKind.GreaterEqual, ">="); Set(TokenKind.LessLess, "<<");
            Set(TokenKind.AmpersandAmpersand, "&&"); Set(TokenKind.PipePipe, "||"); Set(TokenKind.PlusPlus, "++"); Set(TokenKind.MinusMinus, "--");
            Set(TokenKind.PlusEqual, "+="); Set(TokenKind.MinusEqual, "-="); Set(TokenKind.StarEqual, "*="); Set(TokenKind.SlashEqual, "/=");
            Set(TokenKind.PercentEqual, "%="); Set(TokenKind.AmpersandEqual, "&="); Set(TokenKind.PipeEqual, "|="); Set(TokenKind.CaretEqual, "^=");
            Set(TokenKind.LessLessEqual, "<<=");
            return table;
        }
        private int position;

        private Lexer(SourceText source, DiagnosticBag diagnostics)
        {
            this.source = source;
            this.diagnostics = diagnostics;
            text = source.Text;
        }

        public static List<Token> Tokenize(SourceText source, DiagnosticBag diagnostics) => new Lexer(source, diagnostics).Run();

        private List<Token> Run()
        {
            var tokens = new List<Token>(text.Length / 4 + 16);
            while (true)
            {
                SkipTrivia();
                if (position >= text.Length)
                {
                    tokens.Add(new Token(TokenKind.EndOfFile, new TextSpan(text.Length, 0), ""));
                    return tokens;
                }
                if (text[position] == '$' && Peek(1) == '"')
                    Interpolated(tokens);
                else if (Next() is { } token)
                    tokens.Add(token);
            }
        }

        /// <summary><c>$"…"</c>: its text pieces as strings (<c>{{</c> and <c>}}</c> stand for braces), its holes' tokens.</summary>
        private void Interpolated(List<Token> tokens)
        {
            int start = position;
            position += 2;
            tokens.Add(new Token(TokenKind.InterpolationStart, new TextSpan(start, 2), "$\""));
            var piece = new StringBuilder();
            int pieceStart = position;
            void Flush()
            {
                tokens.Add(new Token(TokenKind.String, new TextSpan(pieceStart, position - pieceStart), text[pieceStart..position], piece.ToString()));
                piece.Clear();
            }
            while (true)
            {
                if (position >= text.Length || text[position] == '\n')
                {
                    diagnostics.Error("AN0004", "unterminated string", source, Span(start));
                    Flush();
                    tokens.Add(new Token(TokenKind.InterpolationEnd, new TextSpan(position, 0), ""));
                    return;
                }
                char c = text[position];
                if (c == '"')
                {
                    Flush();
                    tokens.Add(new Token(TokenKind.InterpolationEnd, new TextSpan(position, 1), "\""));
                    position++;
                    return;
                }
                if (c is '{' or '}' && Peek(1) == c)
                {
                    piece.Append(c);
                    position += 2;
                    continue;
                }
                if (c == '}')
                {
                    diagnostics.Error("AN0006", "a `}` in an interpolated string is written `}}`", source, new TextSpan(position, 1));
                    position++;
                    continue;
                }
                if (c != '{')
                {
                    piece.Append(Escaped());
                    continue;
                }
                Flush();
                tokens.Add(new Token(TokenKind.HoleStart, new TextSpan(position, 1), "{"));
                position++;
                // The hole's tokens, to the `:` or `}` outside any brackets.
                int depth = 0;
                while (true)
                {
                    SkipTrivia();
                    if (position >= text.Length || text[position] == '"' && depth == 0)
                    {
                        diagnostics.Error("AN0007", "an interpolated string's hole ends with `}`", source, Span(start));
                        tokens.Add(new Token(TokenKind.HoleEnd, new TextSpan(position, 0), "}"));
                        break;
                    }
                    char h = text[position];
                    if (depth == 0 && h == '}')
                    {
                        tokens.Add(new Token(TokenKind.HoleEnd, new TextSpan(position, 1), "}"));
                        position++;
                        break;
                    }
                    if (depth == 0 && h == ':')
                    {
                        int formatStart = ++position;
                        while (position < text.Length && text[position] != '}' && text[position] != '"' && text[position] != '\n')
                            position++;
                        tokens.Add(new Token(TokenKind.HoleFormat, new TextSpan(formatStart, position - formatStart), text[formatStart..position], text[formatStart..position]));
                        continue;
                    }
                    if (h is '(' or '[' or '{')
                        depth++;
                    else if (h is ')' or ']' or '}')
                        depth--;
                    if (h == '$' && Peek(1) == '"')
                        Interpolated(tokens);
                    else if (Next() is { } token)
                        tokens.Add(token);
                }
                pieceStart = position;
            }
        }

        private char Peek(int ahead = 0) => position + ahead < text.Length ? text[position + ahead] : '\0';

        private void SkipTrivia()
        {
            while (position < text.Length)
            {
                char c = text[position];
                if (c is ' ' or '\t' or '\r' or '\n' || char.IsWhiteSpace(c))
                {
                    position++;
                }
                else if (c == '/' && Peek(1) == '/')
                {
                    int newline = text.IndexOf('\n', position);
                    position = newline < 0 ? text.Length : newline;
                }
                else if (c == '/' && Peek(1) == '*')
                {
                    int start = position;
                    int end = text.IndexOf("*/", position + 2, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        diagnostics.Error("AN0002", "unterminated comment", source, new TextSpan(start, 2));
                        position = text.Length;
                    }
                    else
                    {
                        position = end + 2;
                    }
                }
                else
                {
                    return;
                }
            }
        }

        private Token? Next()
        {
            int start = position;
            char c = text[position];
            if (char.IsAsciiLetter(c) || c == '_' || c > 127 && char.IsLetter(c))
            {
                position++;
                while (position < text.Length && (char.IsAsciiLetterOrDigit(text[position]) || text[position] == '_' || text[position] > 127 && char.IsLetterOrDigit(text[position])))
                    position++;
                var word = text.AsSpan(start, position - start);
                var lookup = words.GetAlternateLookup<ReadOnlySpan<char>>();
                if (!lookup.TryGetValue(word, out var known))
                {
                    string made = word.ToString();
                    known = (made, TokenKind.Identifier);
                    words[made] = known;
                }
                return new Token(known.Kind, new TextSpan(start, position - start), known.Text);
            }
            if (char.IsAsciiDigit(c))
                return Number(start);
            if (c == '"')
                return StringLiteral(start);
            if (c == '\'')
                return CharLiteral(start);
            position++;
            TokenKind kind = c switch
            {
                '(' => TokenKind.OpenParen,
                ')' => TokenKind.CloseParen,
                '[' => TokenKind.OpenBracket,
                ']' => TokenKind.CloseBracket,
                '{' => TokenKind.OpenBrace,
                '}' => TokenKind.CloseBrace,
                ';' => TokenKind.Semicolon,
                ',' => TokenKind.Comma,
                ':' => TokenKind.Colon,
                '@' => TokenKind.At,
                '#' => TokenKind.Hash,
                '$' => TokenKind.Dollar,
                '~' => TokenKind.Tilde,
                '.' => Match('.') ? Match('.') ? TokenKind.Ellipsis : Match('=') ? TokenKind.DotDotEqual : TokenKind.DotDot : TokenKind.Dot,
                '?' => Match('.') ? TokenKind.QuestionDot : Match('?') ? TokenKind.QuestionQuestion : TokenKind.Question,
                '+' => Match('+') ? TokenKind.PlusPlus : Match('=') ? TokenKind.PlusEqual : TokenKind.Plus,
                '-' => Match('-') ? TokenKind.MinusMinus : Match('=') ? TokenKind.MinusEqual : Match('>') ? TokenKind.ThinArrow : TokenKind.Minus,
                '*' => Match('=') ? TokenKind.StarEqual : TokenKind.Star,
                '/' => Match('=') ? TokenKind.SlashEqual : TokenKind.Slash,
                '%' => Match('=') ? TokenKind.PercentEqual : TokenKind.Percent,
                '^' => Match('=') ? TokenKind.CaretEqual : TokenKind.Caret,
                '&' => Match('&') ? TokenKind.AmpersandAmpersand : Match('=') ? TokenKind.AmpersandEqual : TokenKind.Ampersand,
                '|' => Match('|') ? TokenKind.PipePipe : Match('=') ? TokenKind.PipeEqual : TokenKind.Pipe,
                '!' => Match('=') ? TokenKind.BangEqual : TokenKind.Bang,
                '=' => Match('=') ? TokenKind.EqualEqual : Match('>') ? TokenKind.Arrow : TokenKind.Equal,
                '<' => Match('<') ? Match('=') ? TokenKind.LessLessEqual : TokenKind.LessLess : Match('=') ? TokenKind.LessEqual : TokenKind.Less,
                '>' => Match('=') ? TokenKind.GreaterEqual : TokenKind.Greater,
                _ => TokenKind.EndOfFile,
            };
            if (kind == TokenKind.EndOfFile)
            {
                diagnostics.Error("AN0001", $"unexpected character '{c}'", source, Span(start));
                return null;
            }
            return new Token(kind, new TextSpan(start, position - start), Punctuation[(int)kind]);
        }

        private bool Match(char expected)
        {
            if (Peek() != expected)
                return false;
            position++;
            return true;
        }

        private TextSpan Span(int start) => TextSpan.FromBounds(start, position);

        private Token Number(int start)
        {
            // Plain decimals (most numbers): read directly.
            int end = position;
            long fast = 0;
            while (end < text.Length && char.IsAsciiDigit(text[end]) && end - start < 18)
                fast = fast * 10 + (text[end++] - '0');
            if (end == text.Length || !(char.IsAsciiLetterOrDigit(text[end]) || text[end] is '_' or '.') || text[end] == '.' && !(end + 1 < text.Length && char.IsAsciiDigit(text[end + 1])))
            {
                position = end;
                return new Token(TokenKind.Integer, new TextSpan(start, end - start), text[start..end], new BigInteger(fast));
            }
            int radix = 10;
            if (text[position] == '0' && Peek(1) is 'x' or 'X' or 'b' or 'B')
            {
                radix = Peek(1) is 'x' or 'X' ? 16 : 2;
                position += 2;
            }
            var digits = new StringBuilder();
            while (IsDigit(Peek(), radix) || Peek() == '_')
            {
                if (Peek() != '_')
                    digits.Append(Peek());
                position++;
            }
            bool isFloat = false;
            // "1.5" is a float, "0..10" a range: '.' continues the number only before a digit.
            if (radix == 10 && Peek() == '.' && char.IsAsciiDigit(Peek(1)))
            {
                isFloat = true;
                digits.Append('.');
                position++;
                while (char.IsAsciiDigit(Peek()) || Peek() == '_')
                {
                    if (Peek() != '_')
                        digits.Append(Peek());
                    position++;
                }
            }
            if (radix == 10 && Peek() is 'e' or 'E' && (char.IsAsciiDigit(Peek(1)) || Peek(1) is '+' or '-' && char.IsAsciiDigit(Peek(2))))
            {
                isFloat = true;
                digits.Append('e');
                position++;
                if (Peek() is '+' or '-')
                    digits.Append(text[position++]);
                while (char.IsAsciiDigit(Peek()))
                    digits.Append(text[position++]);
            }
            // A type suffix: `12u32`, `1_000_i64`, `2.5f32`, `3f16` (underscores may separate it from the digits).
            string? suffix = null;
            {
                int at = position;
                while (at < text.Length && text[at] == '_')
                    at++;
                if (at < text.Length && text[at] is 'i' or 'u' or 'f')
                {
                    int widthStart = at + 1, widthEnd = widthStart;
                    while (widthEnd < text.Length && char.IsAsciiDigit(text[widthEnd]))
                        widthEnd++;
                    string candidate = text[at..widthEnd];
                    if (candidate is "i8" or "i16" or "i32" or "i64" or "i128" or "i256" or "u8" or "u16" or "u32" or "u64" or "u128" or "u256"
                        or "f16" or "f32" or "f64" or "f128" or "f256" && !(widthEnd < text.Length && (char.IsAsciiLetterOrDigit(text[widthEnd]) || text[widthEnd] == '_')))
                    {
                        suffix = candidate;
                        position = widthEnd;
                        if (suffix[0] == 'f')
                            isFloat = true;
                    }
                }
            }
            while (Peek() == '_')
                position++; // `1_000_`: trailing separators are allowed
            string literal = text[start..position];
            if (digits.Length == 0)
            {
                diagnostics.Error("AN0003", $"invalid number '{literal}'", source, Span(start));
                return new Token(TokenKind.Integer, Span(start), literal, BigInteger.Zero);
            }
            if (isFloat && radix != 10)
            {
                diagnostics.Error("AN0003", $"invalid number '{literal}': a float is written in decimal", source, Span(start));
                return new Token(TokenKind.Float, Span(start), literal, 0.0) { Suffix = suffix };
            }
            if (isFloat)
                return new Token(TokenKind.Float, Span(start), literal, double.Parse(digits.ToString(), CultureInfo.InvariantCulture)) { Suffix = suffix };
            BigInteger value = BigInteger.Zero;
            foreach (char d in digits.ToString())
                value = value * radix + (char.IsAsciiDigit(d) ? d - '0' : char.ToLowerInvariant(d) - 'a' + 10);
            return new Token(TokenKind.Integer, Span(start), literal, value) { Suffix = suffix };
        }

        private static bool IsDigit(char c, int radix) => radix switch
        {
            2 => c is '0' or '1',
            16 => char.IsAsciiHexDigit(c),
            _ => char.IsAsciiDigit(c),
        };

        private Token StringLiteral(int start)
        {
            position++;
            var value = new StringBuilder();
            while (position < text.Length && text[position] != '"' && text[position] != '\n')
                value.Append(Escaped());
            if (Peek() != '"')
                diagnostics.Error("AN0004", "unterminated string", source, Span(start));
            else
                position++;
            return new Token(TokenKind.String, Span(start), text[start..position], value.ToString());
        }

        private Token CharLiteral(int start)
        {
            position++;
            string value = position < text.Length && text[position] != '\'' ? Escaped() : "";
            // A character beyond the BMP is two UTF-16 units in the source ('😀'): one code point.
            if (value.Length == 1 && char.IsHighSurrogate(value[0]) && position < text.Length && char.IsLowSurrogate(text[position]))
                value += text[position++];
            if (Peek() != '\'' || value.Length == 0)
                diagnostics.Error("AN0005", "invalid character literal", source, Span(start));
            else
                position++;
            return new Token(TokenKind.Char, Span(start), text[start..position], value);
        }

        private string Escaped()
        {
            char c = text[position++];
            if (c != '\\' || position >= text.Length)
                return c.ToString();
            char e = text[position++];
            return e switch
            {
                'n' => "\n",
                't' => "\t",
                'r' => "\r",
                '0' => "\0",
                '\\' => "\\",
                '"' => "\"",
                '\'' => "'",
                _ => e.ToString(),
            };
        }
    }
}
