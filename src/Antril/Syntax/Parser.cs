namespace Antril.Syntax
{
    /// <summary>
    /// Antril's parser: recursive descent, binary operators by precedence climbing over Antril's levels
    /// (<c>??</c> 30, <c>||</c> 40, <c>&amp;&amp;</c> 50, <c>|</c> 60, <c>^</c> 70, <c>&amp;</c> 80, equality 90, relational and
    /// <c>is</c>/<c>as</c> 100, <c>switch</c> 105, ranges 110, shifts 120, additive 130, multiplicative 140). A statement that
    /// may be a declaration or an expression is tried as a declaration first, without side effects when it is not one.
    /// </summary>
    public sealed class Parser
    {
        private readonly SourceText source;
        private readonly List<Token> tokens;
        private DiagnosticBag diagnostics;
        private int position;
        private Modifiers sectionAccess;
        private string? pendingExtern;
        /// <summary>The kind of the type whose members are being parsed (null at namespace level or in an extension).</summary>
        private TypeKind? enclosing;
        private readonly List<MemberDecl> moreMembers = [];
        private bool inArm;

        /// <summary>Above zero while parsing patterns and guards, where <c>name =&gt;</c> ends a switch arm (not a lambda).</summary>
        private int noLambda;

        /// <summary>Above zero while a type pattern's type is parsed: `?` there is not a nullable type.</summary>
        private int typePatterns;

        private readonly OperatorTable table;

        private Parser(SourceText source, List<Token> tokens, DiagnosticBag diagnostics, OperatorTable table)
        {
            this.source = source;
            this.tokens = tokens;
            this.diagnostics = diagnostics;
            this.table = table;
        }

        /// <summary>Parses a file; <paramref name="table"/> holds the operators the program declares beyond the built-in ones.</summary>
        public static CompilationUnit Parse(SourceText source, DiagnosticBag diagnostics, OperatorTable? table = null)
        {
            var tokens = Lexer.Tokenize(source, diagnostics);
            return new Parser(source, tokens, diagnostics, table ?? OperatorTable.Empty).ParseUnit();
        }

        /// <summary>
        /// Parses a program's files with the operators its references declare; when the files declare new symbols, they
        /// are parsed again knowing them (their bodies may use them). Each file's diagnostics go to its bag; the operator
        /// declarations' to <paramref name="diagnostics"/>.
        /// </summary>
        public static (CompilationUnit[] Units, OperatorTable Table) ParseAll(SourceText[] files, DiagnosticBag[] bags, IReadOnlyList<Larva.Hir.HirAssembly>? references, DiagnosticBag diagnostics, bool parallel)
        {
            var table = OperatorTable.FromReferences(references);
            var units = new CompilationUnit[files.Length];
            void ParseOne(int i)
            {
                bags[i] = [];
                units[i] = Parse(files[i], bags[i], table);
            }
            void All()
            {
                if (parallel && files.Length > 1)
                    Parallel.For(0, files.Length, ParseOne);
                else
                    for (int i = 0; i < files.Length; i++)
                        ParseOne(i);
            }
            All();
            if (table.AddDeclared(units, references, diagnostics))
                All();
            return (units, table);
        }

        // ── Tokens ─────────────────────────────────────────

        private Token Current => tokens[position];
        private Token Previous => tokens[Math.Max(0, position - 1)];
        private Token Peek(int ahead) => tokens[Math.Min(position + ahead, tokens.Count - 1)];
        private bool At(TokenKind kind) => Current.Kind == kind;
        private bool AtWord(string word) => Current.Kind == TokenKind.Identifier && Current.Text == word;

        private Token Next()
        {
            var token = Current;
            if (position < tokens.Count - 1)
                position++;
            return token;
        }

        private bool Accept(TokenKind kind)
        {
            if (!At(kind))
                return false;
            Next();
            return true;
        }

        private Token Expect(TokenKind kind, string what)
        {
            if (At(kind))
                return Next();
            Error("AN0010", $"expected {what}, found {Current}", Current.Span);
            return new Token(kind, new TextSpan(Current.Span.Start, 0), "");
        }

        private string ExpectIdentifier(string what) => Expect(TokenKind.Identifier, what).Text;

        private void ExpectStatementEnd()
        {
            if (inArm && (At(TokenKind.Comma) || At(TokenKind.CloseBrace)))
                return;
            Expect(TokenKind.Semicolon, "';'");
        }

        private void Error(string code, string message, TextSpan span) => diagnostics.Error(code, message, source, span);

        private TextSpan From(int startToken) => TextSpan.FromBounds(tokens[startToken].Span.Start, Previous.Span.End);

        private static bool Adjacent(Token a, Token b) => a.Span.End == b.Span.Start;

        /// <summary>Runs <paramref name="attempt"/>; on failure (null or errors) restores the position and drops its diagnostics.</summary>
        private T? Try<T>(Func<T?> attempt) where T : class
        {
            int saved = position;
            var outer = diagnostics;
            diagnostics = [];
            T? result = null;
            try
            {
                result = attempt();
            }
            finally
            {
                bool failed = result is null || diagnostics.HasErrors;
                var inner = diagnostics;
                diagnostics = outer;
                if (failed)
                {
                    position = saved;
                    result = null;
                }
                else
                {
                    diagnostics.AddRange(inner);
                }
            }
            return result;
        }

        // ── Compilation unit ───────────────────────────────

        private CompilationUnit ParseUnit()
        {
            var imports = new List<ImportDirective>();
            while (At(TokenKind.Import))
                imports.Add(ParseImport());
            var members = ParseMembers(TokenKind.EndOfFile, inType: false);
            return new CompilationUnit(source, [.. imports], [.. members], new TextSpan(0, source.Text.Length));
        }

        /// <summary>
        /// <c>import D;</c>, <c>import D as X;</c>, <c>import A, B, C from D;</c> or <c>import A, B, C from D as E, F, G;</c>
        /// (one alias for each name, in their order).
        /// </summary>
        private ImportDirective ParseImport()
        {
            int start = position;
            Next();
            var first = ParseQualifiedName();
            if (first.Parts.Length == 1 && (At(TokenKind.Comma) || AtFrom()))
            {
                var names = new List<(string Name, TextSpan Span)> { (first.Parts[0], first.Span) };
                while (At(TokenKind.Comma))
                {
                    Next();
                    var token = Current;
                    names.Add((ExpectIdentifier("a name to import"), token.Span));
                }
                if (!AtFrom())
                    Error("AN0034", "a list of names is imported `from` a namespace: `import A, B from D;`", Current.Span);
                else
                    Next();
                var ns = ParseQualifiedName();
                var aliases = new List<(string Name, TextSpan Span)>();
                if (At(TokenKind.As))
                {
                    Next();
                    do
                    {
                        if (aliases.Count > 0)
                            Next();
                        var token = Current;
                        aliases.Add((ExpectIdentifier("an alias"), token.Span));
                    }
                    while (At(TokenKind.Comma));
                    if (aliases.Count != names.Count)
                        Error("AN0034", $"one alias for each name imported, in their order: {names.Count} name{(names.Count == 1 ? "" : "s")} and {aliases.Count} alias{(aliases.Count == 1 ? "" : "es")}", TextSpan.FromBounds(aliases[0].Span.Start, aliases[^1].Span.End));
                }
                Expect(TokenKind.Semicolon, "';'");
                var picked = names.Select((n, i) => new ImportedName(n.Name, i < aliases.Count ? aliases[i].Name : null, n.Span, i < aliases.Count ? aliases[i].Span : default)).ToArray();
                return new ImportDirective(ns, From(start)) { Picked = picked };
            }
            string? alias = null;
            TextSpan aliasSpan = default;
            if (At(TokenKind.As))
            {
                Next();
                aliasSpan = Current.Span;
                alias = ExpectIdentifier("an alias for the namespace");
            }
            Expect(TokenKind.Semicolon, "';'");
            return new ImportDirective(first, From(start)) { Alias = alias, AliasSpan = aliasSpan };
        }

        /// <summary><c>static loop (E in T)</c>: the element's name and the pack's.</summary>
        private (string Element, string Pack, int First) ParseStaticLoopHead(bool members)
        {
            Next();
            Next();
            Expect(TokenKind.OpenParen, "'('");
            string element = ExpectIdentifier("the name of each type of the pack");
            Expect(TokenKind.In, "'in'");
            string pack = ExpectIdentifier("a pack (`T...`)");
            int first = 0;
            if (AtFrom())
            {
                // `static loop (T in TArgs from 1) { T Item; }`: the members it declares are numbered from 1 (Item1, Item2…).
                var word = Next();
                if (!members)
                    Error("AN0036", "`from` numbers the members a `static loop` declares: it goes on a loop among a type's members", word.Span);
                var number = Expect(TokenKind.Integer, "the first number (`from 1`)");
                if (!int.TryParse(number.Text.Replace("_", ""), out first) || first < 0)
                {
                    Error("AN0036", "`from` takes a small number that is not negative (`from 1`)", number.Span);
                    first = 0;
                }
            }
            Expect(TokenKind.CloseParen, "')'");
            return (element, pack, first);
        }

        /// <summary>`from` in an import (a word only there: elsewhere it is a name like any other).</summary>
        private bool AtFrom() => Current.Kind == TokenKind.Identifier && Current.Text == "from";

        private QualifiedName ParseQualifiedName()
        {
            int start = position;
            var parts = new List<string> { ExpectIdentifier("a name") };
            while (At(TokenKind.Dot) && Peek(1).Kind == TokenKind.Identifier)
            {
                Next();
                parts.Add(Next().Text);
            }
            return new QualifiedName([.. parts], From(start));
        }

        private Modifiers AccessLabel()
        {
            if (Peek(1).Kind != TokenKind.Colon)
                return Modifiers.None;
            return Current.Kind switch
            {
                TokenKind.Public => Modifiers.Public,
                TokenKind.Friendly => Modifiers.Friendly,
                TokenKind.Internal => Modifiers.Internal,
                TokenKind.Inherited => Modifiers.Inherited,
                TokenKind.Identifier when Current.Text == "private" => Modifiers.Private,
                TokenKind.Identifier when Current.Text == "file" => Modifiers.File,
                _ => Modifiers.None,
            };
        }

        /// <summary>
        /// Members up to <paramref name="end"/>. An access section (<c>public:</c>) gives its access to every following member
        /// that does not say its own, to the end of the block.
        /// </summary>
        private List<MemberDecl> ParseMembers(TokenKind end, bool inType)
        {
            var members = new List<MemberDecl>();
            var section = inType ? Modifiers.None : sectionAccess;
            while (!At(end) && !At(TokenKind.EndOfFile))
            {
                if (AccessLabel() is var label and not Modifiers.None)
                {
                    section = label;
                    Next();
                    Next();
                    continue;
                }
                int before = position;
                var outer = sectionAccess;
                sectionAccess = section;
                var member = ParseMember(inType);
                sectionAccess = outer;
                MemberDecl InSection(MemberDecl m) =>
                    section == Modifiers.None || (m.Modifiers & Modifiers.Access) != 0 || m is NamespaceDecl or EnumMemberDecl ? m : m with { Modifiers = m.Modifiers | section };
                if (member is not null)
                    members.Add(InSection(member));
                members.AddRange(moreMembers.Select(InSection));
                moreMembers.Clear();
                if (position == before)
                {
                    Error("AN0012", $"unexpected {Current}", Current.Span);
                    Next();
                }
            }
            return members;
        }

        private AttributeSyntax[] ParseAttributes()
        {
            var attributes = new List<AttributeSyntax>();
            while (At(TokenKind.OpenBracket) && Peek(1).Kind == TokenKind.Identifier)
            {
                Next();
                do
                {
                    int start = position;
                    var name = ParseQualifiedName();
                    var arguments = At(TokenKind.OpenParen) ? ParseArguments() : [];
                    attributes.Add(new AttributeSyntax(name, arguments, From(start)));
                }
                while (Accept(TokenKind.Comma));
                Expect(TokenKind.CloseBracket, "']'");
            }
            return [.. attributes];
        }

        private Modifiers ParseModifiers()
        {
            var modifiers = Modifiers.None;
            while (true)
            {
                Modifiers next = Current.Kind switch
                {
                    TokenKind.Public => Modifiers.Public,
                    TokenKind.Friendly => Modifiers.Friendly,
                    TokenKind.Internal => Modifiers.Internal,
                    TokenKind.Inherited => Modifiers.Inherited,
                    TokenKind.Fixed => Modifiers.Fixed,
                    TokenKind.Readonly => Modifiers.Readonly,
                    TokenKind.Static when Peek(1).Kind != TokenKind.Loop => Modifiers.Static,
                    TokenKind.Virtual => Modifiers.Virtual,
                    TokenKind.Override => Modifiers.Override,
                    TokenKind.Identifier when Current.Text == "file" && StartsDeclaration(Peek(1)) => Modifiers.File,
                    TokenKind.Identifier when Current.Text == "private" && StartsDeclaration(Peek(1)) => Modifiers.Private,
                    TokenKind.Identifier when Current.Text == "extern" && Peek(1).Kind == TokenKind.OpenParen && Peek(2).Kind == TokenKind.String => Modifiers.Extern,
                    TokenKind.Identifier when Current.Text == "unsafe" && StartsDeclaration(Peek(1)) => Modifiers.Unsafe,
                    TokenKind.Identifier when Current.Text == "final" && StartsDeclaration(Peek(1)) => Modifiers.Final,
                    TokenKind.Identifier when Current.Text == "const" && StartsDeclaration(Peek(1)) => Modifiers.Const,
                    TokenKind.Identifier when Current.Text == "async" && StartsDeclaration(Peek(1)) => Modifiers.Async,
                    TokenKind.Identifier when Current.Text == "reactive" && StartsDeclaration(Peek(1)) => Modifiers.Reactive,
                    TokenKind.Identifier when Current.Text == "computed" && StartsDeclaration(Peek(1)) => Modifiers.Computed,
                    TokenKind.Identifier when Current.Text == "reacts" && Peek(1).Kind == TokenKind.Identifier && Peek(2).Kind == TokenKind.OpenParen => Modifiers.Reacts,
                    _ => Modifiers.None,
                };
                if (next == Modifiers.None)
                    return modifiers;
                if ((modifiers & next) != 0)
                    Error("AN0013", $"duplicate modifier '{Current.Text}'", Current.Span);
                else if ((next & Modifiers.Access) != 0 && (modifiers & Modifiers.Access) != 0)
                    Error("AN0038", $"a declaration has one access: `{Current.Text}` after another access word", Current.Span);
                modifiers |= next;
                if (next == Modifiers.Extern)
                {
                    Next();
                    Next();
                    pendingExtern = (string)Next().Value!;
                    Expect(TokenKind.CloseParen, "')'");
                    continue;
                }
                Next();
            }
        }

        private static bool StartsDeclaration(Token token) =>
            token.Kind is TokenKind.Identifier or TokenKind.Void or TokenKind.Class or TokenKind.Interface or TokenKind.Enum
                or TokenKind.Bitflag or TokenKind.Public or TokenKind.Friendly or TokenKind.Internal or TokenKind.Inherited
                or TokenKind.Fixed or TokenKind.Readonly or TokenKind.Static or TokenKind.Virtual or TokenKind.Override or TokenKind.New
            || Lexer.IsPrimitive(token.Kind);

        private const Modifiers ReactiveWords = Modifiers.Reactive | Modifiers.Computed | Modifiers.Reacts;

        private MemberDecl? ParseMember(bool inType)
        {
            int start = position;
            if (At(TokenKind.Static) && Peek(1).Kind == TokenKind.Loop)
            {
                // `static loop (E in T) { members }`: members per type of a pack.
                var (element, pack, numberedFrom) = ParseStaticLoopHead(members: true);
                Expect(TokenKind.OpenBrace, "'{'");
                var loopMembers = ParseMembers(TokenKind.CloseBrace, inType);
                Expect(TokenKind.CloseBrace, "'}'");
                return new StaticLoopDecl(element, pack, [.. loopMembers], From(start)) { First = numberedFrom };
            }
            var attributes = ParseAttributes();
            var modifiers = ParseModifiers();
            if ((modifiers & ReactiveWords) != 0)
            {
                // `reactive`, `computed` and `reacts` make instance members of a class (a Source per object, effects on it).
                string word = (modifiers & Modifiers.Reactive) != 0 ? "reactive" : (modifiers & Modifiers.Computed) != 0 ? "computed" : "reacts";
                if (enclosing is not (TypeKind.Class or TypeKind.BaseClass))
                    Error("AN0030", $"`{word}` goes in a class (not {(enclosing is null ? "at namespace level or in an extension" : "in a " + Describe(enclosing.Value))})", Previous.Span);
                else if ((modifiers & Modifiers.Static) != 0)
                    Error("AN0031", $"`{word}` is per object: it cannot be `static`", Previous.Span);
                if ((modifiers & ReactiveWords & (modifiers & ReactiveWords) - 1) != 0)
                    Error("AN0031", "`reactive`, `computed` and `reacts` exclude each other", Previous.Span);
            }

            if (At(TokenKind.Into))
            {
                Next();
                Expect(TokenKind.Namespace, "'namespace'");
                var name = ParseQualifiedName();
                Expect(TokenKind.OpenBrace, "'{'");
                var members = ParseMembers(TokenKind.CloseBrace, inType: false);
                Expect(TokenKind.CloseBrace, "'}'");
                return new NamespaceDecl(name, [.. members], From(start));
            }

            if (!inType && At(TokenKind.Identifier) && Current.Text == "ext" && Peek(1).Kind is TokenKind.OpenParen or TokenKind.Less)
                return ParseExtension(start, attributes, modifiers);

            TypeKind? kind = Current.Kind switch
            {
                TokenKind.Class => TypeKind.Class,
                TokenKind.Interface => TypeKind.Interface,
                TokenKind.Enum => TypeKind.Enum,
                TokenKind.Bitflag when Peek(1).Kind == TokenKind.Enum => TypeKind.BitflagEnum,
                TokenKind.Identifier when Current.Text == "base" && Peek(1).Kind == TokenKind.Class => TypeKind.BaseClass,
                TokenKind.Identifier when Current.Text == "record" && Peek(1).Kind == TokenKind.Class => TypeKind.RecordClass,
                TokenKind.Identifier when Current.Text == "record" && Peek(1).Kind == TokenKind.Identifier && Peek(1).Text == "value" && Peek(2).Kind == TokenKind.Identifier => TypeKind.RecordValue,
                TokenKind.Identifier when Current.Text == "value" && Peek(1).Kind == TokenKind.Identifier => TypeKind.Value,
                // `attribute Tag { … }`: the class TagAttribute (an attribute, written [Tag]); `exception NotFound { … }`:
                // the class NotFoundException (thrown and caught).
                TokenKind.Identifier when Current.Text is "attribute" or "exception" && Peek(1).Kind == TokenKind.Identifier => TypeKind.Class,
                _ => null,
            };
            if (kind is { } typeKind)
                return Placed(ParseTypeDecl(start, attributes, modifiers, typeKind), inType);

            if (inType && At(TokenKind.New) && Peek(1).Kind == TokenKind.OpenParen)
                return ParseConstructor(start, attributes, modifiers);

            if (AtOperatorDecl())
                return ParseOperator(start, attributes, modifiers, inType);

            if (At(TokenKind.Identifier) && (Peek(1).Kind == TokenKind.OpenParen || Peek(1).Kind == TokenKind.Less && IsGenericFunctionHeader()))
                return Placed(ParseFunction(start, attributes, modifiers), inType);

            if (!StartsType(Current) && !At(TokenKind.OpenParen))
            {
                if (attributes.Length > 0 || modifiers != Modifiers.None)
                    Error("AN0014", $"expected a declaration, found {Current}", Current.Span);
                return null;
            }
            var type = ParseType();
            var nameToken = Expect(TokenKind.Identifier, "a name");
            if (At(TokenKind.OpenParen))
            {
                Error("AN0020", $"functions are declared as `{nameToken.Text}(…) : {source[type.Span]}`: the return type goes after the parameters", type.Span);
                ParseParameters();
                if (!Accept(TokenKind.Semicolon))
                    ParseBlock();
                return null;
            }
            if (At(TokenKind.OpenBrace))
            {
                if ((modifiers & Modifiers.Computed) != 0)
                    Error("AN0032", $"`computed` takes an expression: `computed {source[type.Span]} {nameToken.Text} => …;` (it has no accessors)", nameToken.Span);
                else if ((modifiers & Modifiers.Reactive) != 0)
                    Error("AN0032", $"`reactive` makes a field (its getter and setter are written for it): `reactive {source[type.Span]} {nameToken.Text};`", nameToken.Span);
                return ParseProperty(start, attributes, modifiers & ~ReactiveWords, type, nameToken);
            }
            if ((modifiers & Modifiers.Reacts) != 0)
                Error("AN0032", $"`reacts` goes on a method: `reacts {nameToken.Text}() {{ … }}`", nameToken.Span);
            if ((modifiers & Modifiers.Computed) != 0)
            {
                // `computed T Name => expr;`: a private `$Name` of System.Reactive.Computed<T> made with `() => expr`, and a
                // property reading its Value (kept until something `expr` read changes; computed again on the next read).
                Expect(TokenKind.Arrow, "'=>'");
                var computed = ParseExpression();
                Expect(TokenKind.Semicolon, "';'");
                var span = nameToken.Span;
                var computedType = new NamedTypeSyntax(new QualifiedName(["System", "Reactive", "Computed"], span), [type], span);
                var made = new NewExpr(computedType, [new LambdaExpr([], computed, null, computed.Span)], span);
                moreMembers.Add(new FieldDecl([], Modifiers.Private, computedType, "$" + nameToken.Text, span, made, span));
                var read = new MemberExpr(new NameExpr("$" + nameToken.Text, [], span), "Value", [], span);
                var getter = new AccessorSyntax(true, Modifiers.None, new BlockStmt([new ReturnStmt(read, span)], span), span);
                return new PropertyDecl(attributes, (modifiers & ~Modifiers.Computed) & (Modifiers.Access | Modifiers.Static), type, nameToken.Text, span, [getter], From(start));
            }
            Expr? initializer = Accept(TokenKind.Equal) ? ParseExpression() : null;
            if ((modifiers & Modifiers.Reactive) != 0)
            {
                Expect(TokenKind.Semicolon, "';'");
                return ReactiveField(start, attributes, modifiers & ~Modifiers.Reactive, type, nameToken, initializer);
            }
            var first = new FieldDecl(attributes, modifiers, type, nameToken.Text, nameToken.Span, initializer, From(start));
            while (Accept(TokenKind.Comma))
            {
                int fieldStart = position;
                var name = Expect(TokenKind.Identifier, "a field name");
                Expr? value = Accept(TokenKind.Equal) ? ParseExpression() : null;
                moreMembers.Add(new FieldDecl(attributes, modifiers, type, name.Text, name.Span, value, From(fieldStart)));
            }
            Expect(TokenKind.Semicolon, "';'");
            return first;
        }

        /// <summary>
        /// `reactive T name = v;` becomes a hidden field `$name`, a hidden `System.Reactive.Source` `$name_source`, and a
        /// property `name` whose getter tracks (`$name_source.Track()`) and whose setter notifies (`$name_source.Notify()`).
        /// </summary>
        private MemberDecl ReactiveField(int start, AttributeSyntax[] attributes, Modifiers modifiers, TypeSyntax type, Token nameToken, Expr? initializer)
        {
            var span = nameToken.Span;
            string hidden = "$" + nameToken.Text, sourceName = "$" + nameToken.Text + "_source";
            var sourceType = new NamedTypeSyntax(new QualifiedName(["System", "Reactive", "Source"], span), [], span);
            var access = modifiers & Modifiers.Access;
            moreMembers.Add(new FieldDecl([], Modifiers.Private, type, hidden, span, initializer, span));
            moreMembers.Add(new FieldDecl([], Modifiers.Private, sourceType, sourceName, span, new NewExpr(sourceType, [], span), span));
            Expr Field(string name) => new NameExpr(name, [], span);
            Expr SourceCall(string method) => new CallExpr(new MemberExpr(Field(sourceName), method, [], span), [], span);
            var getter = new AccessorSyntax(true, Modifiers.None, new BlockStmt([
                new ExprStmt(SourceCall("Track"), span),
                new ReturnStmt(Field(hidden), span)], span), span);
            var setter = new AccessorSyntax(false, Modifiers.None, new BlockStmt([
                new ExprStmt(new AssignExpr(null, Field(hidden), Field("value"), span), span),
                new ExprStmt(SourceCall("Notify"), span)], span), span);
            return new PropertyDecl(attributes, access | (modifiers & Modifiers.Static), type, nameToken.Text, span, [getter, setter], From(start));
        }

        /// <summary>Lookahead: <c>Name&lt;…&gt;(</c> is a generic function header.</summary>
        private bool IsGenericFunctionHeader()
        {
            int saved = position;
            var outer = diagnostics;
            diagnostics = [];
            try
            {
                Next();
                ParseGenericParams();
                return !diagnostics.HasErrors && At(TokenKind.OpenParen);
            }
            finally
            {
                position = saved;
                diagnostics = outer;
            }
        }

        private ConstructorDecl ParseConstructor(int start, AttributeSyntax[] attributes, Modifiers modifiers)
        {
            int headStart = position;
            Next();
            var parameters = ParseParameters();
            var head = From(headStart);
            Expr[]? baseArguments = null, thisArguments = null;
            Expr? fixedLength = null;
            if (At(TokenKind.Colon) && Peek(1).Kind == TokenKind.Fixed && Peek(2).Kind == TokenKind.OpenParen)
            {
                // `: fixed(n)`: how many elements the object's `fixed T[]` field holds.
                Next();
                Next();
                Expect(TokenKind.OpenParen, "`(` after `fixed`");
                fixedLength = ParseExpression();
                Expect(TokenKind.CloseParen, "`)` after the length");
            }
            else if (At(TokenKind.Colon) && Peek(1).Kind == TokenKind.Identifier && Peek(1).Text == "base")
            {
                Next();
                Next();
                baseArguments = ParseArguments();
            }
            else if (At(TokenKind.Colon) && Peek(1).Kind == TokenKind.OpenParen)
            {
                Next();
                thisArguments = ParseArguments();
            }
            if (thisArguments is null && At(TokenKind.Semicolon))
            {
                // `new(i32 x, i32 y);`, `new(i32 id, f64 r) : base(id);`: each parameter that names a field goes into it
                // (the binder writes the stores, knowing the fields); the others go to the base constructor.
                var end = Next().Span;
                return new ConstructorDecl(attributes, modifiers, parameters, new BlockStmt([], end), From(start)) { BaseArguments = baseArguments, HeadSpan = head, Bodiless = true, FixedLength = fixedLength };
            }
            var body = thisArguments is not null && At(TokenKind.Semicolon) ? new BlockStmt([], Next().Span) : ParseBody(returnsValue: false);
            return new ConstructorDecl(attributes, modifiers, parameters, body, From(start)) { BaseArguments = baseArguments, ThisArguments = thisArguments, HeadSpan = head, FixedLength = fixedLength };
        }

        /// <summary><c>Name&lt;T&gt;(params) : Result</c> (no <c>: Result</c> means void), with a body or <c>;</c>.</summary>
        private FunctionDecl ParseFunction(int start, AttributeSyntax[] attributes, Modifiers modifiers)
        {
            var nameToken = Next();
            var generics = At(TokenKind.Less) ? ParseGenericParams() : [];
            var parameters = ParseParameters();
            TypeSyntax returnType = Accept(TokenKind.Colon) ? ParseType() : new VoidTypeSyntax(new TextSpan(Previous.Span.End, 0));
            var into = ParseHeaderInto();
            BlockStmt? body = Accept(TokenKind.Semicolon) ? null : ParseBody(returnsValue: returnType is not VoidTypeSyntax);
            pendingInto = into;
            if ((modifiers & Modifiers.Reacts) != 0)
            {
                // An effect: run with nothing, giving nothing, with a body to run.
                if (parameters.Length > 0)
                    Error("AN0033", $"a `reacts` method takes no parameters: it runs by itself whenever what it reads changes", parameters[0].Span);
                else if (returnType is not VoidTypeSyntax)
                    Error("AN0033", $"a `reacts` method gives nothing (nobody reads its result)", returnType.Span);
                else if (body is null)
                    Error("AN0033", $"a `reacts` method has a body: what runs when what it reads changes", nameToken.Span);
            }
            string? externName = (modifiers & Modifiers.Extern) != 0 ? pendingExtern : null;
            pendingExtern = null;
            return new FunctionDecl(attributes, modifiers, returnType, nameToken.Text, nameToken.Span, generics, parameters, body, From(start)) { ExternName = externName };
        }

        private static string Describe(TypeKind kind) => kind switch
        {
            TypeKind.Interface => "interface",
            TypeKind.Enum or TypeKind.BitflagEnum => "enum",
            TypeKind.Value or TypeKind.RecordValue => "value",
            TypeKind.RecordClass => "record class (its fields are readonly)",
            _ => "type",
        };

        private static bool IsOperatorSymbol(TokenKind kind) => kind is TokenKind.Plus or TokenKind.Minus or TokenKind.Star
            or TokenKind.Slash or TokenKind.Percent or TokenKind.Ampersand or TokenKind.Pipe or TokenKind.Caret or TokenKind.Tilde
            or TokenKind.Bang or TokenKind.EqualEqual or TokenKind.BangEqual or TokenKind.Less or TokenKind.LessEqual
            or TokenKind.Greater or TokenKind.GreaterEqual;

        /// <summary>
        /// The method name an operator is declared under (<c>op +(a, b)</c> is <c>$op_Add</c>), by its symbol and how many
        /// operands it takes; null when the symbol has no form with that many.
        /// </summary>
        public static string? OperatorMethodName(string symbol, int operands) => (symbol, operands) switch
        {
            ("+", 2) => "$op_Add", ("-", 2) => "$op_Subtract", ("*", 2) => "$op_Multiply", ("/", 2) => "$op_Divide",
            ("%", 2) => "$op_Remainder", ("&", 2) => "$op_BitAnd", ("|", 2) => "$op_BitOr", ("^", 2) => "$op_BitXor",
            ("==", 2) => "$op_Equal", ("!=", 2) => "$op_NotEqual", ("<", 2) => "$op_Less", ("<=", 2) => "$op_LessEqual",
            (">", 2) => "$op_Greater", (">=", 2) => "$op_GreaterEqual",
            ("-", 1) => "$op_Negate", ("+", 1) => "$op_Plus", ("!", 1) => "$op_Not", ("~", 1) => "$op_Complement",
            ("++", 1) => "$op_Increment", ("--", 1) => "$op_Decrement",
            _ => null,
        };

        /// <summary>The symbol of an operator method (<c>$op_Add</c> is <c>+</c>, <c>$op_CaretCaret</c> is <c>^^</c>); null for other names.</summary>
        public static string? OperatorSymbol(string name) => name switch
        {
            "$op_Add" or "$op_Plus" => "+", "$op_Subtract" or "$op_Negate" => "-", "$op_Multiply" => "*", "$op_Divide" => "/",
            "$op_Remainder" => "%", "$op_BitAnd" => "&", "$op_BitOr" => "|", "$op_BitXor" => "^", "$op_Equal" => "==",
            "$op_NotEqual" => "!=", "$op_Less" => "<", "$op_LessEqual" => "<=", "$op_Greater" => ">", "$op_GreaterEqual" => ">=",
            "$op_Not" => "!", "$op_Complement" => "~", "$op_Increment" => "++", "$op_Decrement" => "--",
            _ => CustomOperatorOf(name)?.Symbol,
        };

        /// <summary><c>op SYMBOL(</c>, <c>left op</c> / <c>right op</c>, or a conversion <c>op (T)(</c>.</summary>
        private bool AtOperatorDecl() =>
            At(TokenKind.Identifier) && (Current.Text == "op" && (OperatorTable.IsSymbolToken(Peek(1).Kind) || Peek(1).Kind == TokenKind.OpenParen)
                || Current.Text is "left" or "right" && Peek(1).Kind == TokenKind.Identifier && Peek(1).Text == "op" && OperatorTable.IsSymbolToken(Peek(2).Kind));

        /// <summary>A symbol: a run of operator characters written without spaces (<c>^^</c>, <c>&lt;=&gt;</c>).</summary>
        private (string Symbol, TextSpan Span) ParseOperatorSymbol()
        {
            var first = Next();
            if (!OperatorTable.IsSymbolToken(first.Kind))
                Error("AN0021", "an operator symbol is made of `+ - * / % & | ^ ~ ! < > = ? @ # $`", first.Span);
            string symbol = first.Text;
            var last = first;
            while (OperatorTable.IsSymbolToken(Current.Kind) && Adjacent(last, Current))
            {
                // A `<` followed by a name and `>`, `,` or `:` opens the operator's type parameters (`op +<T : int>(…)`),
                // so a symbol that ends in `<` keeps it apart: `op < <T>`.
                if (Current.Kind == TokenKind.Less && Peek(1).Kind == TokenKind.Identifier && Peek(2).Kind is TokenKind.Greater or TokenKind.Comma or TokenKind.Colon)
                    break;
                last = Next();
                symbol += last.Text;
            }
            return (symbol, new TextSpan(first.Span.Start, last.Span.End - first.Span.Start));
        }

        /// <summary>
        /// <c>op +(T a, T b) : T { … }</c>: an operator, a static method of its type (always public) found by the types of
        /// the operands. A symbol of the program's own declares its precedence after the result type
        /// (<c>precedence(PrecedenceLevel.Power) associativity(Associativity.Right)</c>); <c>left op</c> / <c>right op</c>
        /// say where an operator with one operand goes (without them, either side). <c>op (T)(S s) { … }</c> is a conversion.
        /// </summary>
        private MemberDecl? ParseOperator(int start, AttributeSyntax[] attributes, Modifiers modifiers, bool inType)
        {
            var form = OperatorForm.Binary;
            bool sideGiven = false;
            if (Current.Text is "left" or "right")
            {
                form = Next().Text == "left" ? OperatorForm.Prefix : OperatorForm.Postfix;
                sideGiven = true;
            }
            var opToken = Next();
            if (!inType)
                Error("AN0022", "operators are declared inside the type of an operand for now", opToken.Span);
            modifiers = (modifiers & ~Modifiers.Access) | Modifiers.Public | Modifiers.Static;
            if (At(TokenKind.OpenParen))
            {
                // A conversion: op (T)(S s).
                Next();
                var target = ParseType();
                Expect(TokenKind.CloseParen, "')'");
                var generics = At(TokenKind.Less) ? ParseGenericParams() : [];
                var parameters = ParseParameters();
                BlockStmt? body = Accept(TokenKind.Semicolon) ? null : ParseBody(returnsValue: true);
                if (sideGiven)
                    Error("AN0025", "`left` and `right` say where an operator with one operand goes", opToken.Span);
                if (parameters.Length != 1)
                    Error("AN0026", "a conversion takes one operand", target.Span);
                if (!inType)
                    return null;
                return new FunctionDecl(attributes, modifiers, target, ConversionMethodName(source[target.Span]), target.Span, generics, parameters, body, From(start)) { ConversionTarget = target };
            }
            var (symbol, symbolSpan) = ParseOperatorSymbol();
            var operatorGenerics = At(TokenKind.Less) ? ParseGenericParams() : [];
            var operands = ParseParameters();
            TypeSyntax returnType = Accept(TokenKind.Colon) ? ParseType() : new VoidTypeSyntax(new TextSpan(Previous.Span.End, 0));
            Expr? precedence = null, associativity = null;
            while (At(TokenKind.Identifier) && Current.Text is "precedence" or "associativity" && Peek(1).Kind == TokenKind.OpenParen)
            {
                string which = Next().Text;
                Next();
                var written = ParseExpression();
                Expect(TokenKind.CloseParen, "')'");
                if (which == "precedence")
                    precedence = written;
                else
                    associativity = written;
            }
            BlockStmt? operatorBody = Accept(TokenKind.Semicolon) ? null : ParseBody(returnsValue: returnType is not VoidTypeSyntax);
            bool builtin = OperatorTable.BuiltinBinary.Contains(symbol) || OperatorTable.BuiltinPrefix.Contains(symbol);
            if (OperatorTable.Reserved.Contains(symbol))
            {
                Error("AN0021", $"`{symbol}` cannot be an operator", symbolSpan);
                return null;
            }
            if (operands.Length is not (1 or 2) || builtin && form != OperatorForm.Postfix && OperatorMethodName(symbol, operands.Length) is null)
            {
                Error("AN0021", $"`op {symbol}` takes {(!builtin ? "one or two operands" : symbol is "-" or "+" ? "one or two operands" : symbol is "!" or "~" ? "one operand" : "two operands")}", symbolSpan);
                return null;
            }
            if (sideGiven && operands.Length != 1)
                Error("AN0025", "`left` and `right` say where an operator with one operand goes", symbolSpan);
            if (operands.Length == 1 && !sideGiven)
                form = builtin ? OperatorForm.Prefix : OperatorForm.Both;
            if (operands.Length == 2)
                form = OperatorForm.Binary;
            if (symbol is "++" or "--" && operands.Length == 1)
                form = OperatorForm.Both; // one `op ++` serves `++x` and `x++`: the side only says which value the expression has
            if (returnType is VoidTypeSyntax)
                Error("AN0023", $"`op {symbol}` needs a result type", symbolSpan);
            if (!inType)
                return null;
            string name = builtin && form != OperatorForm.Postfix ? OperatorMethodName(symbol, operands.Length)! : CustomOperatorName(symbol, form);
            return new FunctionDecl(attributes, modifiers, returnType, name, symbolSpan, operatorGenerics, operands, operatorBody, From(start))
            {
                OperatorSymbol = symbol,
                Form = form,
                Precedence = precedence,
                Associativity = associativity,
            };
        }

        private static readonly Dictionary<char, string> SymbolCharNames = new()
        {
            ['+'] = "Plus", ['-'] = "Minus", ['*'] = "Star", ['/'] = "Slash", ['%'] = "Percent", ['&'] = "Amp", ['|'] = "Pipe",
            ['^'] = "Caret", ['~'] = "Tilde", ['!'] = "Bang", ['='] = "Eq", ['<'] = "Lt", ['>'] = "Gt", ['?'] = "Quest",
            ['@'] = "At", ['#'] = "Hash", ['$'] = "Dollar",
        };

        /// <summary>
        /// The method name of an operator with a symbol of the program's own, or a built-in symbol used after its operand:
        /// <c>$op_CaretCaret</c> for <c>^^</c>, with <c>_pre</c> for one written before its operand (or either side) and
        /// <c>_post</c> after it.
        /// </summary>
        public static string CustomOperatorName(string symbol, OperatorForm form) =>
            "$op_" + string.Concat(symbol.Select(c => SymbolCharNames.TryGetValue(c, out var n) ? n : "_"))
            + form switch { OperatorForm.Prefix or OperatorForm.Both => "_pre", OperatorForm.Postfix => "_post", _ => "" };

        /// <summary>The method name of a conversion to a type (<c>$conv_i64</c>).</summary>
        public static string ConversionMethodName(string targetText) =>
            "$conv_" + new string(targetText.Where(c => !char.IsWhiteSpace(c)).Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());

        /// <summary>A custom operator method's symbol and form (<see cref="CustomOperatorName"/>), or null for other names.</summary>
        public static (string Symbol, OperatorForm Form)? CustomOperatorOf(string name)
        {
            if (!name.StartsWith("$op_", StringComparison.Ordinal))
                return null;
            string rest = name[4..];
            var form = OperatorForm.Binary;
            if (rest.EndsWith("_pre", StringComparison.Ordinal))
                (rest, form) = (rest[..^4], OperatorForm.Prefix);
            else if (rest.EndsWith("_post", StringComparison.Ordinal))
                (rest, form) = (rest[..^5], OperatorForm.Postfix);
            var symbol = new System.Text.StringBuilder();
            while (rest.Length > 0)
            {
                var part = SymbolCharNames.FirstOrDefault(kv => rest.StartsWith(kv.Value, StringComparison.Ordinal));
                if (part.Value is null)
                    return null;
                symbol.Append(part.Key);
                rest = rest[part.Value.Length..];
            }
            return symbol.Length == 0 ? null : (symbol.ToString(), form);
        }

        /// <summary>A body: a block, or <c>=&gt; expression;</c> (returned when the member returns a value).</summary>
        private BlockStmt ParseBody(bool returnsValue)
        {
            if (At(TokenKind.Arrow))
            {
                int start = position;
                Next();
                var expression = ParseExpression();
                Expect(TokenKind.Semicolon, "';'");
                Stmt statement = returnsValue ? new ReturnStmt(expression, expression.Span) : new ExprStmt(expression, expression.Span);
                return new BlockStmt([statement], From(start));
            }
            return ParseBlock();
        }

        /// <summary>
        /// <c>ext(i32 x) { … }</c>, <c>ext&lt;T : bitflag enum&gt;(T flags) { … }</c>: functions every value of the type gets
        /// (<c>x</c> is the receiver); <c>static ext(i32) { … }</c>, <c>static ext&lt;T&gt;(T) { … }</c>: functions of the type
        /// itself (<c>i32.F()</c>). Parsed as a type declaration of its own kind, named after its place in the file.
        /// </summary>
        private TypeDecl ParseExtension(int start, AttributeSyntax[] attributes, Modifiers modifiers)
        {
            var word = Next(); // ext
            var generics = At(TokenKind.Less) ? ParseGenericParams() : [];
            Expect(TokenKind.OpenParen, "'('");
            var target = ParseType();
            string? receiver = At(TokenKind.Identifier) ? Next().Text : null;
            Expect(TokenKind.CloseParen, "')'");
            bool isStatic = (modifiers & Modifiers.Static) != 0;
            if (isStatic && receiver is not null)
                Error("AN0029", "a static extension names no receiver: `static ext(i32)`", word.Span);
            else if (!isStatic && receiver is null)
                Error("AN0029", "an extension names its receiver: `ext(i32 value)`", word.Span);
            Expect(TokenKind.OpenBrace, "'{'");
            var outerKind = enclosing;
            enclosing = null;
            var members = ParseMembers(TokenKind.CloseBrace, inType: true);
            enclosing = outerKind;
            Expect(TokenKind.CloseBrace, "'}'");
            string file = System.IO.Path.GetFileNameWithoutExtension(source.Path);
            string name = $"$ext.{(file.Length == 0 ? "program" : file)}.{start}";
            return new TypeDecl(attributes, modifiers, TypeKind.Extension, name, word.Span, generics, [], [.. members], From(start))
            {
                ExtensionTarget = target,
                ExtensionReceiver = receiver,
            };
        }

        /// <summary>
        /// A declaration with <c>into namespace X</c> in its header (<c>class Shape into namespace Geo { … }</c>,
        /// <c>Area() : f64 into namespace Geo => …</c>): in that namespace, as if written in an <c>into namespace</c> block.
        /// A member of a type stays in its type (AN0039).
        /// </summary>
        private MemberDecl Placed(MemberDecl declaration, bool inType)
        {
            if (pendingInto is not { } ns)
                return declaration;
            pendingInto = null;
            if (inType)
            {
                Error("AN0039", "`into namespace` places a declaration of a namespace: a member stays in its type", ns.Span);
                return declaration;
            }
            return new NamespaceDecl(ns, [declaration], declaration.Span);
        }

        /// <summary>The <c>into namespace X</c> of the header just read, for <see cref="Placed"/>.</summary>
        private QualifiedName? pendingInto;

        /// <summary>`into namespace X` after a header, when there (set as pending once the declaration is read).</summary>
        private QualifiedName? ParseHeaderInto()
        {
            if (!At(TokenKind.Into))
                return null;
            Next();
            Expect(TokenKind.Namespace, "'namespace'");
            return ParseQualifiedName();
        }

        private TypeDecl ParseTypeDecl(int start, AttributeSyntax[] attributes, Modifiers modifiers, TypeKind kind)
        {
            // `attribute` / `exception`: a class whose name ends in Attribute / Exception (added when not written).
            string? implied = AtWord("attribute") ? "Attribute" : AtWord("exception") ? "Exception" : null;
            if (kind is TypeKind.BaseClass or TypeKind.RecordClass or TypeKind.RecordValue or TypeKind.BitflagEnum)
                Next();
            Next(); // class / interface / enum / value / attribute / exception
            var nameToken = Expect(TokenKind.Identifier, "a type name");
            string typeName = implied is not null && !nameToken.Text.EndsWith(implied, StringComparison.Ordinal) ? nameToken.Text + implied : nameToken.Text;
            var generics = At(TokenKind.Less) ? ParseGenericParams() : [];
            var bases = new List<TypeSyntax>();
            if (Accept(TokenKind.Colon))
            {
                do
                    bases.Add(ParseType());
                while (Accept(TokenKind.Comma));
            }
            var into = ParseHeaderInto();
            Expect(TokenKind.OpenBrace, "'{'");
            List<MemberDecl> members;
            if (kind is TypeKind.Enum or TypeKind.BitflagEnum)
            {
                members = [];
                while (!At(TokenKind.CloseBrace) && !At(TokenKind.EndOfFile))
                {
                    int memberStart = position;
                    var memberAttributes = ParseAttributes();
                    string name = ExpectIdentifier("an enum member");
                    Expr? value = Accept(TokenKind.Equal) ? ParseExpression() : null;
                    members.Add(new EnumMemberDecl(memberAttributes, name, value, From(memberStart)));
                    if (!Accept(TokenKind.Comma))
                        break;
                }
            }
            else
            {
                var outer = enclosing;
                enclosing = kind;
                members = ParseMembers(TokenKind.CloseBrace, inType: true);
                enclosing = outer;
            }
            Expect(TokenKind.CloseBrace, "'}'");
            pendingInto = into;
            return new TypeDecl(attributes, modifiers, kind, typeName, nameToken.Span, generics, [.. bases], [.. members], From(start)) { Implied = implied };
        }

        private PropertyDecl ParseProperty(int start, AttributeSyntax[] attributes, Modifiers modifiers, TypeSyntax type, Token name)
        {
            Expect(TokenKind.OpenBrace, "'{'");
            var accessors = new List<AccessorSyntax>();
            while (!At(TokenKind.CloseBrace) && !At(TokenKind.EndOfFile))
            {
                int accessorStart = position;
                var accessorModifiers = ParseModifiers();
                bool isAbstract = AtWord("base") && (Peek(1).Text is "get" or "set");
                if (isAbstract)
                    Next();
                if (!(AtWord("get") || AtWord("set")))
                {
                    Error("AN0015", $"expected 'get' or 'set', found {Current}", Current.Span);
                    Next();
                    continue;
                }
                bool getter = Next().Text == "get";
                BlockStmt? body = Accept(TokenKind.Semicolon) ? null : ParseBody(returnsValue: getter);
                if (isAbstract && body is not null)
                    Error("AN0024", "a `base` accessor has no body: the types that implement the interface write it", body.Span);
                accessors.Add(new AccessorSyntax(getter, accessorModifiers, body, From(accessorStart)) { IsAbstract = isAbstract });
            }
            Expect(TokenKind.CloseBrace, "'}'");
            return new PropertyDecl(attributes, modifiers, type, name.Text, name.Span, [.. accessors], From(start));
        }

        private GenericParamSyntax[] ParseGenericParams()
        {
            Expect(TokenKind.Less, "'<'");
            var generics = new List<GenericParamSyntax>();
            do
            {
                var name = Expect(TokenKind.Identifier, "a type parameter");
                bool variadic = Accept(TokenKind.Ellipsis);
                var constraints = new List<ConstraintSyntax>();
                if (Accept(TokenKind.Colon))
                {
                    constraints.Add(ParseConstraint());
                    // After ',' only words for groups of types continue the list: any other name starts the next parameter.
                    while (At(TokenKind.Comma) && (Peek(1).Kind is TokenKind.Class or TokenKind.Enum or TokenKind.Bitflag || AtOperatorConstraint(1)
                        || Peek(1).Kind is TokenKind.New or TokenKind.Fixed && Peek(2).Kind == TokenKind.OpenParen || Peek(1).Kind == TokenKind.OpenParen
                        || Peek(1).Kind == TokenKind.Identifier && ConstraintWords.Contains(Peek(1).Text) && Peek(2).Kind is TokenKind.Comma or TokenKind.Greater))
                    {
                        Next();
                        constraints.Add(ParseConstraint());
                    }
                }
                generics.Add(new GenericParamSyntax(name.Text, name.Span) { Constraints = [.. constraints], Variadic = variadic });
            }
            while (Accept(TokenKind.Comma));
            Expect(TokenKind.Greater, "'>'");
            return [.. generics];
        }

        /// <summary>
        /// Words for groups of types (any other word in a constraint is a type). <c>number</c> and <c>snumber</c> name
        /// the interfaces <c>System.INumber&lt;T&gt;</c> and <c>System.ISignedNumber&lt;T&gt;</c>.
        /// </summary>
        internal static readonly HashSet<string> ConstraintWords =
            ["value", "plain", "copy", "unmanaged", "managed", "fixedsize", "numeric", "integer", "int", "uint", "float", "number", "snumber", "attribute", "exception"];

        /// <summary><c>op +(…)</c>, <c>left op -(…)</c>, <c>right op !(…)</c> starting <paramref name="ahead"/> tokens on.</summary>
        private bool AtOperatorConstraint(int ahead) =>
            Peek(ahead).Kind == TokenKind.Identifier && (Peek(ahead).Text == "op" && OperatorTable.IsSymbolToken(Peek(ahead + 1).Kind)
                || Peek(ahead).Text is "left" or "right" && Peek(ahead + 1).Kind == TokenKind.Identifier && Peek(ahead + 1).Text == "op");

        /// <summary><c>[left | right] op SYMBOL(operand types) : result</c>.</summary>
        private ConstraintSyntax ParseOperatorConstraint()
        {
            int start = position;
            bool postfix = false, fixityGiven = false;
            if (AtWord("left") || AtWord("right"))
            {
                postfix = Next().Text == "right";
                fixityGiven = true;
            }
            Next(); // op
            var (symbolText, symbolSpan) = ParseOperatorSymbol();
            var symbol = (Text: symbolText, Span: symbolSpan);
            Expect(TokenKind.OpenParen, "'('");
            var operands = new List<TypeSyntax>();
            if (!At(TokenKind.CloseParen))
            {
                do
                    operands.Add(ParseType());
                while (Accept(TokenKind.Comma));
            }
            Expect(TokenKind.CloseParen, "')'");
            Expect(TokenKind.Colon, "':' and the operator's result type");
            var result = ParseType();
            bool builtin = OperatorTable.BuiltinBinary.Contains(symbol.Text) || OperatorTable.BuiltinPrefix.Contains(symbol.Text);
            if (operands.Count is not (1 or 2) || builtin && !postfix && OperatorMethodName(symbol.Text, operands.Count) is null)
                Error("AN0021", $"`op {symbol.Text}` takes {(!builtin || symbol.Text is "-" or "+" ? "one or two operands" : symbol.Text is "!" or "~" ? "one operand" : "two operands")}", symbol.Span);
            else if (fixityGiven && operands.Count != 1)
                Error("AN0025", "`left` and `right` say where an operator with one operand goes", symbol.Span);
            return new ConstraintSyntax(null, null, null, From(start)) { Operator = new OperatorConstraintSyntax(symbol.Text, postfix, [.. operands], result, From(start)) };
        }

        private ConstraintSyntax ParseConstraint()
        {
            int start = position;
            if (AtOperatorConstraint(0))
                return ParseOperatorConstraint();
            if (At(TokenKind.New) && Peek(1).Kind == TokenKind.OpenParen)
            {
                // `new(i32, str)`: a constructor with these parameter types; `new()`: one without parameters.
                Next();
                Next();
                var types = new List<TypeSyntax>();
                if (!At(TokenKind.CloseParen))
                {
                    do
                        types.Add(ParseType());
                    while (Accept(TokenKind.Comma));
                }
                Expect(TokenKind.CloseParen, "')'");
                return new ConstraintSyntax(null, null, null, From(start)) { Constructor = [.. types] };
            }
            if (At(TokenKind.Fixed) && Peek(1).Kind == TokenKind.OpenParen)
            {
                // `fixed(N)`: a type of exactly N bytes.
                Next();
                Next();
                var size = Expect(TokenKind.Integer, "a size in bytes");
                Expect(TokenKind.CloseParen, "')'");
                int bytes = size.Value is System.Numerics.BigInteger big && big > 0 && big <= int.MaxValue ? (int)big : 0;
                if (bytes == 0)
                    Error("AN0028", "`fixed(N)` takes a positive number of bytes", size.Span);
                return new ConstraintSyntax(null, null, null, From(start)) { FixedSize = bytes };
            }
            if (At(TokenKind.OpenParen))
            {
                // `(i64)T`: the parameter converts to the type.
                Next();
                var target = ParseType();
                Expect(TokenKind.CloseParen, "')'");
                if (At(TokenKind.Identifier))
                    Next();
                return new ConstraintSyntax(null, null, null, From(start)) { ConvertsTo = target };
            }
            if (At(TokenKind.Class))
            {
                Next();
                return new ConstraintSyntax(null, "class", null, From(start));
            }
            if (At(TokenKind.Bitflag) && Peek(1).Kind == TokenKind.Enum)
            {
                Next();
                Next();
                return new ConstraintSyntax(null, "bitflag enum", null, From(start));
            }
            if (At(TokenKind.Enum))
            {
                Next();
                return new ConstraintSyntax(null, "enum", null, From(start));
            }
            if (Accept(TokenKind.OpenBracket))
            {
                var types = new List<TypeSyntax>();
                do
                    types.Add(ParseType());
                while (Accept(TokenKind.Comma));
                Expect(TokenKind.CloseBracket, "']'");
                return new ConstraintSyntax(null, null, [.. types], From(start));
            }
            if (At(TokenKind.Identifier) && ConstraintWords.Contains(Current.Text) && Peek(1).Kind is TokenKind.Comma or TokenKind.Greater)
                return new ConstraintSyntax(null, Next().Text, null, From(start));
            return new ConstraintSyntax(ParseType(), null, null, From(start));
        }

        private ParameterSyntax[] ParseParameters()
        {
            Expect(TokenKind.OpenParen, "'('");
            var parameters = new List<ParameterSyntax>();
            if (!At(TokenKind.CloseParen))
            {
                do
                {
                    int start = position;
                    var attributes = ParseAttributes();
                    var type = ParseType();
                    string name = ExpectIdentifier("a parameter name");
                    parameters.Add(new ParameterSyntax(type, name, From(start)) { Attributes = attributes });
                }
                while (Accept(TokenKind.Comma));
            }
            Expect(TokenKind.CloseParen, "')'");
            return [.. parameters];
        }

        // ── Types ──────────────────────────────────────────

        private static bool StartsType(Token token) =>
            token.Kind is TokenKind.Identifier or TokenKind.Void or TokenKind.Ampersand || Lexer.IsPrimitive(token.Kind);

        private TypeSyntax ParseType()
        {
            int start = position;
            TypeSyntax type;
            if (At(TokenKind.OpenParen))
            {
                // `(i32, str)`: System.ValueTuple<i32, str> (two or more types).
                Next();
                var items = new List<TypeSyntax> { ParseType() };
                while (Accept(TokenKind.Comma))
                    items.Add(ParseType());
                Expect(TokenKind.CloseParen, "')'");
                if (items.Count < 2)
                    Error("AN0037", "a tuple type has two or more types: `(i32, str)`", From(start));
                type = TupleType([.. items], From(start));
                return ParseTypeSuffixes(type, start);
            }
            if (At(TokenKind.Ampersand))
            {
                Next();
                bool mutable = AtWord("mut") && StartsType(Peek(1));
                if (mutable)
                    Next();
                return new ReferenceTypeSyntax(ParseType(), mutable, From(start));
            }
            if (Lexer.IsPrimitive(Current.Kind))
            {
                type = new PrimitiveTypeSyntax(Next().Kind, From(start));
            }
            else if (At(TokenKind.Void))
            {
                Next();
                type = new VoidTypeSyntax(From(start));
            }
            else if (AtWord("func") && Peek(1).Kind is not (TokenKind.Dot or TokenKind.OpenParen))
            {
                // `func<T1, T2> : R`, `func : R`, or without `: R`: a function returning nothing. `func*…`: a C function's address.
                Next();
                bool native = Accept(TokenKind.Star);
                var parameters = At(TokenKind.Less) ? ParseTypeArguments() : [];
                TypeSyntax? result = Accept(TokenKind.Colon) ? ParseType() : null;
                type = new FuncTypeSyntax(parameters, result, From(start)) { Native = native };
            }
            else
            {
                var name = ParseQualifiedName();
                var arguments = At(TokenKind.Less) ? ParseTypeArguments() : [];
                type = new NamedTypeSyntax(name, arguments, From(start));
            }
            return ParseTypeSuffixes(type, start);
        }

        /// <summary>The type a tuple's types make: <c>System.ValueTuple&lt;…&gt;</c>.</summary>
        internal static NamedTypeSyntax TupleType(TypeSyntax[] items, TextSpan span) =>
            new(new QualifiedName(["System", "ValueTuple"], new TextSpan(span.Start, 0)), items, span); // the name is not written

        /// <summary>`[]`, `[n]`, `[,]`, `?` and `*` after a type.</summary>
        private TypeSyntax ParseTypeSuffixes(TypeSyntax type, int start)
        {
            while (true)
            {
                if (At(TokenKind.OpenBracket) && Peek(1).Kind == TokenKind.CloseBracket)
                {
                    Next();
                    Next();
                    type = new ArrayTypeSyntax(type, 1, From(start));
                }
                else if (At(TokenKind.OpenBracket) && Peek(1).Kind is TokenKind.Integer or TokenKind.SizeOf)
                {
                    // `u8[4]`, `u8[sizeof(T)]`: an inline array of that many elements (a `fixed` field).
                    Next();
                    var length = ParseExpression();
                    Expect(TokenKind.CloseBracket, "']'");
                    type = new FixedArrayTypeSyntax(type, length, From(start));
                }
                else if (At(TokenKind.OpenBracket) && Peek(1).Kind == TokenKind.Comma && RankAhead() is int rank)
                {
                    for (int t = 0; t < rank + 1; t++)
                        Next();
                    type = new ArrayTypeSyntax(type, rank, From(start));
                }
                else if (At(TokenKind.Question) && type is not NullableTypeSyntax && typePatterns == 0)
                {
                    Next();
                    type = new NullableTypeSyntax(type, From(start));
                }
                else if (At(TokenKind.Star))
                {
                    Next();
                    type = new PointerTypeSyntax(type, From(start));
                }
                else
                {
                    return type;
                }
            }
        }

        /// <summary>At <c>[</c> followed by commas and <c>]</c>: the rank (commas + 1).</summary>
        private int? RankAhead()
        {
            int offset = 1;
            while (Peek(offset).Kind == TokenKind.Comma)
                offset++;
            return Peek(offset).Kind == TokenKind.CloseBracket ? offset : null;
        }

        /// <summary>
        /// <c>F&lt;i32&gt;(…)</c>, <c>List&lt;i32&gt;.Make()</c>: type arguments after a name when what follows them is a call or a
        /// member (else <c>&lt;</c> is a comparison and nothing is read).
        /// </summary>
        private TypeSyntax[] TypeArgumentsBeforeCall()
        {
            if (!At(TokenKind.Less))
                return [];
            int saved = position;
            var outer = diagnostics;
            diagnostics = [];
            try
            {
                var arguments = ParseTypeArguments();
                if (!diagnostics.HasErrors && (At(TokenKind.OpenParen) || At(TokenKind.Dot)))
                    return arguments;
            }
            finally
            {
                diagnostics = outer;
            }
            position = saved;
            return [];
        }

        /// <summary>Lookahead: <c>( … ) =&gt;</c> (a lambda's parameter list), balanced.</summary>
        /// <summary>
        /// At <c>nameof(</c>: <c>nameof(i32)</c>, <c>nameof(List&lt;i32&gt;)</c>, <c>nameof(T[])</c> (a type that is more than a name) as
        /// a <see cref="NameOfTypeExpr"/>; null for anything else (a name, which binding looks up as a value or a type).
        /// </summary>
        private Expr? NameOfType()
        {
            int start = position;
            return Try<Expr>(() =>
            {
                Next();
                Next();
                var type = ParseType();
                Expect(TokenKind.CloseParen, "')'");
                return type is NamedTypeSyntax { Arguments.Length: 0 } ? null : new NameOfTypeExpr(type, From(start));
            });
        }

        private bool AtLambdaParameters()
        {
            int depth = 0;
            for (int i = 0; ; i++)
            {
                var kind = Peek(i).Kind;
                if (kind == TokenKind.OpenParen)
                    depth++;
                else if (kind == TokenKind.CloseParen && --depth == 0)
                    return Peek(i + 1).Kind == TokenKind.Arrow;
                else if (kind is TokenKind.EndOfFile or TokenKind.Semicolon or TokenKind.OpenBrace or TokenKind.CloseBrace)
                    return false;
            }
        }

        /// <summary><c>x =&gt; …</c>, <c>(x, y) =&gt; …</c>, <c>(i32 x) =&gt; …</c>, <c>() =&gt; …</c>; the body an expression or a block.</summary>
        private LambdaExpr ParseLambda()
        {
            int start = position;
            var parameters = new List<LambdaParameter>();
            if (At(TokenKind.Identifier))
            {
                var name = Next();
                parameters.Add(new LambdaParameter(null, name.Text, name.Span));
            }
            else
            {
                Expect(TokenKind.OpenParen, "'('");
                if (!At(TokenKind.CloseParen))
                {
                    do
                    {
                        int parameterStart = position;
                        TypeSyntax? type = At(TokenKind.Identifier) && Peek(1).Kind is TokenKind.Comma or TokenKind.CloseParen ? null : ParseType();
                        var name = Expect(TokenKind.Identifier, "a parameter name");
                        parameters.Add(new LambdaParameter(type, name.Text, From(parameterStart)));
                    }
                    while (Accept(TokenKind.Comma));
                }
                Expect(TokenKind.CloseParen, "')'");
            }
            Expect(TokenKind.Arrow, "'=>'");
            int outerNoLambda = noLambda;
            bool outerArm = inArm;
            noLambda = 0;
            inArm = false;
            try
            {
                if (At(TokenKind.OpenBrace))
                    return new LambdaExpr([.. parameters], null, ParseBlock(), From(start));
                var body = ParseExpression();
                return new LambdaExpr([.. parameters], body, null, From(start));
            }
            finally
            {
                noLambda = outerNoLambda;
                inArm = outerArm;
            }
        }

        private TypeSyntax[] ParseTypeArguments()
        {
            Expect(TokenKind.Less, "'<'");
            var arguments = new List<TypeSyntax>();
            if (!At(TokenKind.Greater))
            {
                do
                    arguments.Add(ParseType());
                while (Accept(TokenKind.Comma));
            }
            Expect(TokenKind.Greater, "'>'");
            return [.. arguments];
        }

        // ── Statements ─────────────────────────────────────

        private BlockStmt ParseBlock()
        {
            int start = position;
            Expect(TokenKind.OpenBrace, "'{'");
            var statements = new List<Stmt>();
            while (!At(TokenKind.CloseBrace) && !At(TokenKind.EndOfFile))
            {
                int before = position;
                statements.Add(ParseStatement());
                if (position == before)
                {
                    Error("AN0016", $"unexpected {Current}", Current.Span);
                    Next();
                }
            }
            Expect(TokenKind.CloseBrace, "'}'");
            return new BlockStmt([.. statements], From(start));
        }

        private Stmt ParseStatement()
        {
            int start = position;
            switch (Current.Kind)
            {
                case TokenKind.OpenBrace:
                    return ParseBlock();
                case TokenKind.Semicolon:
                    Next();
                    return new BlockStmt([], From(start));
                case TokenKind.Static when Peek(1).Kind == TokenKind.Loop:
                {
                    var (element, pack, _) = ParseStaticLoopHead(members: false);
                    var body = ParseStatement();
                    return new StaticLoopStmt(element, pack, body, From(start));
                }
                case TokenKind.Identifier when Current.Text == "effect" && Peek(1).Kind == TokenKind.OpenBrace:
                {
                    // `effect { … }` as a statement: the effect starts and is not kept (no `;` after the block).
                    var effect = ParsePrimary();
                    return new ExprStmt(effect, From(start));
                }
                case TokenKind.If:
                {
                    Next();
                    Expect(TokenKind.OpenParen, "'('");
                    var condition = ParseExpression();
                    Expect(TokenKind.CloseParen, "')'");
                    var then = ParseStatement();
                    Stmt? otherwise = Accept(TokenKind.Else) ? ParseStatement() : null;
                    return new IfStmt(condition, then, otherwise, From(start));
                }
                case TokenKind.Loop:
                    return ParseLoop(start);
                case TokenKind.Do:
                {
                    Next();
                    var body = ParseStatement();
                    Expect(TokenKind.Loop, "'loop'");
                    Expect(TokenKind.OpenParen, "'('");
                    var condition = ParseExpression();
                    Expect(TokenKind.CloseParen, "')'");
                    Expect(TokenKind.Semicolon, "';'");
                    return new DoLoop(body, condition, From(start));
                }
                case TokenKind.Switch:
                {
                    Next();
                    Expect(TokenKind.OpenParen, "'('");
                    var value = ParseExpression();
                    Expect(TokenKind.CloseParen, "')'");
                    return new SwitchStmt(value, ParseSwitchArms(statements: true), From(start));
                }
                case TokenKind.Try:
                    return ParseTry(start);
                case TokenKind.Throw:
                {
                    Next();
                    Expr? value = At(TokenKind.Semicolon) ? null : ParseExpression();
                    ExpectStatementEnd();
                    return new ThrowStmt(value, From(start));
                }
                case TokenKind.Identifier when Current.Text is "checked" or "unchecked" && Peek(1).Kind == TokenKind.OpenBrace:
                {
                    bool isChecked = Next().Text == "checked";
                    return new CheckedStmt(ParseBlock(), isChecked, From(start));
                }
                case TokenKind.Identifier when Current.Text == "yield" && (Peek(1).Kind == TokenKind.Break || StartsOperand(Peek(1).Kind) || Peek(1).Kind is TokenKind.Minus or TokenKind.Ampersand or TokenKind.Star):
                {
                    // `yield value;` / `yield break;` (`yield` is a word only here).
                    Next();
                    Expr? value = null;
                    if (!Accept(TokenKind.Break))
                        value = ParseExpression();
                    ExpectStatementEnd();
                    return new YieldStmt(value, From(start));
                }
                case TokenKind.Identifier when Current.Text == "await" && StartsOperand(Peek(1).Kind):
                {
                    // `await task;` is an await, not a local `task` of a type named `await`.
                    var awaited = ParseExpression();
                    ExpectStatementEnd();
                    return new ExprStmt(awaited, From(start));
                }
                case TokenKind.Identifier when Current.Text == "delete" && Peek(1).Kind is TokenKind.Identifier or TokenKind.OpenParen:
                {
                    Next();
                    var value = ParseExpression();
                    ExpectStatementEnd();
                    return new DeleteStmt(value, From(start));
                }
                case TokenKind.Return:
                {
                    Next();
                    Expr? value = At(TokenKind.Semicolon) || inArm && (At(TokenKind.Comma) || At(TokenKind.CloseBrace)) ? null : ParseExpression();
                    ExpectStatementEnd();
                    return new ReturnStmt(value, From(start));
                }
                case TokenKind.Break:
                    Next();
                    ExpectStatementEnd();
                    return new BreakStmt(From(start));
                case TokenKind.Continue:
                    Next();
                    ExpectStatementEnd();
                    return new ContinueStmt(From(start));
            }

            if (AtWord("unsafe") && Peek(1).Kind == TokenKind.OpenBrace)
            {
                Next();
                return new UnsafeStmt(ParseBlock(), From(start));
            }
            if (AtWord("region") && Peek(1).Kind == TokenKind.OpenBrace)
            {
                Next();
                return new RegionStmt(ParseBlock(), From(start));
            }

            // `const T name = value;`: a local constant.
            bool isConst = AtWord("const") && StartsType(Peek(1));
            if (isConst)
                Next();
            var declaration = Try(() =>
            {
                if (!StartsType(Current) && !At(TokenKind.OpenParen))
                    return null;
                var type = ParseType();
                if (!At(TokenKind.Identifier) || Peek(1).Kind is not (TokenKind.Equal or TokenKind.Semicolon or TokenKind.Comma))
                    return null;
                var name = Next();
                return new { Type = type, Name = name };
            });
            if (declaration is not null)
            {
                Expr? initializer = Accept(TokenKind.Equal) ? ParseExpression() : null;
                var first = new LocalDeclStmt(declaration.Type, declaration.Name.Text, declaration.Name.Span, initializer, From(start)) { IsConst = isConst };
                if (!At(TokenKind.Comma))
                {
                    ExpectStatementEnd();
                    return first;
                }
                var locals = new List<LocalDeclStmt> { first };
                while (Accept(TokenKind.Comma))
                {
                    int localStart = position;
                    var name = Expect(TokenKind.Identifier, "a variable name");
                    Expr? value = Accept(TokenKind.Equal) ? ParseExpression() : null;
                    locals.Add(new LocalDeclStmt(declaration.Type, name.Text, name.Span, value, From(localStart)) { IsConst = isConst });
                }
                ExpectStatementEnd();
                return new LocalDeclGroup([.. locals], From(start));
            }

            if (isConst)
                Error("AN0035", "`const` declares a local constant: `const i32 limit = 10;`", Current.Span);
            var expression = ParseExpression();
            ExpectStatementEnd();
            return new ExprStmt(expression, From(start));
        }

        /// <summary>The forms of <c>loop</c>; the header decides which.</summary>
        private Stmt ParseLoop(int start)
        {
            Next();
            Expect(TokenKind.OpenParen, "'('");
            var header = Try(() =>
            {
                if (!StartsType(Current))
                    return null;
                var type = ParseType();
                if (!At(TokenKind.Identifier) || Peek(1).Kind is not (TokenKind.In or TokenKind.Equal))
                    return null;
                string name = Next().Text;
                return new { Type = type, Name = name, In = Next().Kind == TokenKind.In };
            });
            if (header is null)
            {
                var condition = ParseExpression();
                Expect(TokenKind.CloseParen, "')'");
                return new WhileLoop(condition, ParseStatement(), From(start));
            }
            if (header.In)
            {
                var collection = ParseExpression();
                Expect(TokenKind.CloseParen, "')'");
                return new ForeachLoop(header.Type, header.Name, collection, ParseStatement(), From(start));
            }
            var initial = ParseExpression();
            if (initial is RangeExpr range && At(TokenKind.CloseParen))
            {
                Next();
                return new RangeLoop(header.Type, header.Name, range.From, range.To, range.Inclusive, ParseStatement(), From(start));
            }
            if (Accept(TokenKind.Semicolon))
            {
                var condition = ParseExpression();
                if (!Accept(TokenKind.Semicolon) && !Accept(TokenKind.Comma))
                    Error("AN0017", $"expected ';' between the condition and the step, found {Current}", Current.Span);
                var step = ParseExpression();
                Expect(TokenKind.CloseParen, "')'");
                return new ForLoop(header.Type, header.Name, initial, condition, step, ParseStatement(), From(start));
            }
            Expect(TokenKind.CloseParen, "')'");
            return new CounterLoop(header.Type, header.Name, initial, ParseStatement(), From(start));
        }

        private TryStmt ParseTry(int start)
        {
            Next();
            var body = ParseBlock();
            var catches = new List<CatchClauseSyntax>();
            while (At(TokenKind.Catch))
            {
                int catchStart = position;
                Next();
                TypeSyntax? type = null;
                string? name = null;
                if (Accept(TokenKind.OpenParen))
                {
                    type = ParseType();
                    if (At(TokenKind.Identifier))
                        name = Next().Text;
                    Expect(TokenKind.CloseParen, "')'");
                }
                catches.Add(new CatchClauseSyntax(type, name, ParseBlock(), From(catchStart)));
            }
            BlockStmt? @finally = Accept(TokenKind.Finally) ? ParseBlock() : null;
            if (catches.Count == 0 && @finally is null)
                Error("AN0018", "a 'try' needs at least one 'catch' or a 'finally'", From(start));
            return new TryStmt(body, [.. catches], @finally, From(start));
        }

        /// <summary><c>{ pattern [when guard] =&gt; body, … }</c>: statements in a switch statement, expressions in a switch expression.</summary>
        private SwitchArm[] ParseSwitchArms(bool statements)
        {
            Expect(TokenKind.OpenBrace, "'{'");
            var arms = new List<SwitchArm>();
            while (!At(TokenKind.CloseBrace) && !At(TokenKind.EndOfFile))
            {
                int start = position;
                noLambda++;
                Pattern pattern;
                Expr? guard = null;
                try
                {
                    pattern = ParsePattern();
                    if (AtWord("when"))
                    {
                        Next();
                        guard = ParseExpression();
                    }
                }
                finally
                {
                    noLambda--;
                }
                Expect(TokenKind.Arrow, "'=>'");
                Node body;
                if (statements)
                {
                    bool outer = inArm;
                    inArm = !At(TokenKind.OpenBrace);
                    body = ParseStatement();
                    inArm = outer;
                }
                else
                {
                    body = ParseExpression();
                }
                arms.Add(new SwitchArm(pattern, guard, body, From(start)));
                if (!Accept(TokenKind.Comma) && !statements)
                    break;
            }
            Expect(TokenKind.CloseBrace, "'}'");
            return [.. arms];
        }

        // ── Expressions ────────────────────────────────────

        private Expr ParseExpression() => ParseAssignment();

        private Expr ParseAssignment()
        {
            int start = position;
            var target = ParseConditional();
            BinaryOperator? op = Current.Kind switch
            {
                TokenKind.PlusEqual => BinaryOperator.Add,
                TokenKind.MinusEqual => BinaryOperator.Subtract,
                TokenKind.StarEqual => BinaryOperator.Multiply,
                TokenKind.SlashEqual => BinaryOperator.Divide,
                TokenKind.PercentEqual => BinaryOperator.Remainder,
                TokenKind.AmpersandEqual => BinaryOperator.And,
                TokenKind.PipeEqual => BinaryOperator.Or,
                TokenKind.CaretEqual => BinaryOperator.Xor,
                TokenKind.LessLessEqual => BinaryOperator.ShiftLeft,
                _ => null,
            };
            if (At(TokenKind.Equal) || op is not null)
            {
                Next();
                return new AssignExpr(op, target, ParseAssignment(), From(start));
            }
            if (At(TokenKind.Greater) && Peek(1).Kind == TokenKind.GreaterEqual && Adjacent(Current, Peek(1)))
            {
                Next();
                Next();
                return new AssignExpr(BinaryOperator.ShiftRight, target, ParseAssignment(), From(start));
            }
            return target;
        }

        private Expr ParseConditional()
        {
            int start = position;
            var condition = ParseBinary(0);
            if (!Accept(TokenKind.Question))
                return condition;
            var then = ParseExpression();
            Expect(TokenKind.Colon, "':'");
            var otherwise = ParseConditional();
            return new ConditionalExpr(condition, then, otherwise, From(start));
        }

        private const int RelationalLevel = 100, SwitchLevel = 105, ShiftLevel = 120;

        /// <summary>The binary operator at the current position: its operator, level, right associativity, and tokens.</summary>
        private (BinaryOperator? Op, bool Range, bool Inclusive, int Level, bool Right, int Tokens, string? Symbol, bool NoChain)? PeekBinary()
        {
            if (Declared(binary: true) is var (declared, level, associativity, count))
                return (null, false, false, level, associativity == Larva.Hir.Associativity.Right, count, declared, associativity == Larva.Hir.Associativity.None);
            return PeekBuiltinBinary() is var (op, range, inclusive, builtinLevel, right, tokens) ? (op, range, inclusive, builtinLevel, right, tokens, null, false) : null;
        }

        /// <summary>
        /// A declared symbol at the current token: the longest run of adjacent operator tokens the table knows (as a binary
        /// operator, or as one written before / after an operand). A run of one built-in token is not a declared symbol.
        /// </summary>
        private (string Symbol, int Level, Larva.Hir.Associativity Associativity, int Tokens)? Declared(bool binary, bool prefix = false, bool postfix = false)
        {
            if (!table.HasDeclared || !OperatorTable.IsSymbolToken(Current.Kind))
                return null;
            (string, int, Larva.Hir.Associativity, int)? best = null;
            string text = Current.Text;
            for (int n = 1; n <= 6; n++)
            {
                bool builtin = binary ? OperatorTable.BuiltinBinary.Contains(text) : OperatorTable.BuiltinPrefix.Contains(text) && !postfix;
                if (!builtin || n > 1)
                {
                    if (binary && table.TryBinary(text, out int level, out var associativity))
                        best = (text, level, associativity, n);
                    else if (prefix && table.IsPrefix(text) || postfix && table.IsPostfix(text))
                        best = (text, 0, Larva.Hir.Associativity.Left, n);
                }
                var next = Peek(n);
                if (!OperatorTable.IsSymbolToken(next.Kind) || !Adjacent(Peek(n - 1), next))
                    break;
                text += next.Text;
            }
            return best;
        }

        private (BinaryOperator? Op, bool Range, bool Inclusive, int Level, bool Right, int Tokens)? PeekBuiltinBinary()
        {
            switch (Current.Kind)
            {
                case TokenKind.QuestionQuestion: return (BinaryOperator.Coalesce, false, false, 30, true, 1);
                case TokenKind.PipePipe: return (BinaryOperator.LogicalOr, false, false, 40, false, 1);
                case TokenKind.AmpersandAmpersand: return (BinaryOperator.LogicalAnd, false, false, 50, false, 1);
                case TokenKind.Pipe: return (BinaryOperator.Or, false, false, 60, false, 1);
                case TokenKind.Caret: return (BinaryOperator.Xor, false, false, 70, false, 1);
                case TokenKind.Ampersand: return (BinaryOperator.And, false, false, 80, false, 1);
                case TokenKind.EqualEqual: return (BinaryOperator.Equal, false, false, 90, false, 1);
                case TokenKind.BangEqual: return (BinaryOperator.NotEqual, false, false, 90, false, 1);
                case TokenKind.Less: return (BinaryOperator.Less, false, false, RelationalLevel, false, 1);
                case TokenKind.LessEqual: return (BinaryOperator.LessEqual, false, false, RelationalLevel, false, 1);
                case TokenKind.Greater when Peek(1).Kind == TokenKind.Greater && Adjacent(Current, Peek(1)):
                    return (BinaryOperator.ShiftRight, false, false, ShiftLevel, false, 2);
                case TokenKind.Greater when Peek(1).Kind == TokenKind.GreaterEqual && Adjacent(Current, Peek(1)):
                    return null; // `>>=`: an assignment
                case TokenKind.Greater: return (BinaryOperator.Greater, false, false, RelationalLevel, false, 1);
                case TokenKind.GreaterEqual: return (BinaryOperator.GreaterEqual, false, false, RelationalLevel, false, 1);
                case TokenKind.DotDot: return (null, true, false, 110, false, 1);
                case TokenKind.DotDotEqual: return (null, true, true, 110, false, 1);
                case TokenKind.LessLess: return (BinaryOperator.ShiftLeft, false, false, ShiftLevel, false, 1);
                case TokenKind.Plus: return (BinaryOperator.Add, false, false, 130, false, 1);
                case TokenKind.Minus: return (BinaryOperator.Subtract, false, false, 130, false, 1);
                case TokenKind.Star: return (BinaryOperator.Multiply, false, false, 140, false, 1);
                case TokenKind.Slash: return (BinaryOperator.Divide, false, false, 140, false, 1);
                case TokenKind.Percent: return (BinaryOperator.Remainder, false, false, 140, false, 1);
            }
            return null;
        }

        private Expr ParseBinary(int minimum)
        {
            int start = position;
            var left = ParseUnary();
            while (true)
            {
                if (At(TokenKind.Is) && RelationalLevel >= minimum)
                {
                    Next();
                    left = new IsExpr(left, ParsePattern(), From(start));
                    continue;
                }
                if (At(TokenKind.As) && RelationalLevel >= minimum)
                {
                    Next();
                    left = new AsExpr(left, ParseType(), From(start));
                    continue;
                }
                if (At(TokenKind.Switch) && SwitchLevel >= minimum)
                {
                    Next();
                    left = new SwitchExpr(left, ParseSwitchArms(statements: false), From(start));
                    continue;
                }
                if (PeekBinary() is not { } op || op.Level < minimum)
                    return left;
                var symbolSpan = new TextSpan(Current.Span.Start, Peek(op.Tokens - 1).Span.End - Current.Span.Start);
                for (int t = 0; t < op.Tokens; t++)
                    Next();
                var right = ParseBinary(op.Right ? op.Level : op.Level + 1);
                if (op.Symbol is { } symbol)
                {
                    if (op.NoChain && left is OperatorExpr { Symbol: var before, Operands.Length: 2 } && before == symbol)
                        Error("AN0027", $"`{symbol}` does not chain (`a {symbol} b {symbol} c`): write parentheses", symbolSpan);
                    left = new OperatorExpr(symbol, [left, right], false, From(start));
                    continue;
                }
                left = op.Range ? new RangeExpr(left, right, op.Inclusive, From(start)) : new BinaryExpr(op.Op!.Value, left, right, From(start));
            }
        }

        private static bool StartsOperand(TokenKind kind) =>
            kind is TokenKind.Identifier or TokenKind.Integer or TokenKind.Float or TokenKind.String or TokenKind.Char or TokenKind.True
                or TokenKind.False or TokenKind.Null or TokenKind.OpenParen or TokenKind.Bang or TokenKind.Tilde or TokenKind.New
                or TokenKind.TypeOf or TokenKind.SizeOf or TokenKind.OpenBracket
            || Lexer.IsPrimitive(kind);

        private Expr ParseUnary()
        {
            int start = position;
            if (Declared(binary: false, prefix: true) is var (declaredSymbol, _, _, declaredTokens))
            {
                for (int t = 0; t < declaredTokens; t++)
                    Next();
                var declaredOperand = ParseUnary();
                return new OperatorExpr(declaredSymbol, [declaredOperand], false, From(start));
            }
            UnaryOperator? prefix = Current.Kind switch
            {
                TokenKind.Minus => UnaryOperator.Negate,
                TokenKind.Bang => UnaryOperator.Not,
                TokenKind.Tilde => UnaryOperator.Complement,
                TokenKind.PlusPlus => UnaryOperator.PreIncrement,
                TokenKind.MinusMinus => UnaryOperator.PreDecrement,
                TokenKind.Ampersand when Peek(1).Kind == TokenKind.Identifier && Peek(1).Text == "mut" && StartsOperand(Peek(2).Kind) => UnaryOperator.BorrowMut,
                TokenKind.Ampersand => UnaryOperator.AddressOf,
                TokenKind.Star => UnaryOperator.Dereference,
                _ => null,
            };
            if (prefix is { } op)
            {
                Next();
                if (op == UnaryOperator.BorrowMut)
                    Next();
                var operand = ParseUnary();
                // A literal that is negated is a negative literal (so i32's minimum fits).
                if (op == UnaryOperator.Negate && operand is IntegerLiteral literal)
                    return new IntegerLiteral(-literal.Value, From(start)) { Suffix = literal.Suffix };
                if (op == UnaryOperator.Negate && operand is FloatLiteral real)
                    return new FloatLiteral(-real.Value, From(start)) { Suffix = real.Suffix, Text = real.Text is null ? null : "-" + real.Text };
                return new UnaryExpr(op, operand, From(start));
            }
            if (At(TokenKind.Plus))
            {
                Next();
                return ParseUnary();
            }
            if (At(TokenKind.Identifier) && Current.Text == "await" && StartsOperand(Peek(1).Kind))
            {
                // `await task`: binds like a prefix operator.
                Next();
                var awaited = ParseUnary();
                return new AwaitExpr(awaited, From(start));
            }
            // (i32)x: a primitive in parentheses is a cast; (T)x with a named type is a cast when an operand follows.
            if (At(TokenKind.OpenParen) && (Lexer.IsPrimitive(Peek(1).Kind) || Peek(1).Kind is TokenKind.Identifier || Peek(1).Kind == TokenKind.Void && Peek(2).Kind == TokenKind.Star) && !(noLambda == 0 && AtLambdaParameters()))
            {
                bool primitive = Lexer.IsPrimitive(Peek(1).Kind);
                var cast = Try(() =>
                {
                    Next();
                    var type = ParseType();
                    Expect(TokenKind.CloseParen, "')'");
                    return primitive || type is PointerTypeSyntax || StartsOperand(Current.Kind) && !At(TokenKind.OpenBracket) ? type : null;
                });
                if (cast is not null)
                    return new CastExpr(cast, ParseUnary(), From(start));
            }
            return ParsePostfix();
        }

        private Expr ParsePostfix()
        {
            int start = position;
            var expression = ParsePrimary();
            while (true)
            {
                if (Declared(binary: false, postfix: true) is var (postfixSymbol, _, _, postfixTokens)
                    && !(StartsOperand(Peek(postfixTokens).Kind) && (table.TryBinary(postfixSymbol, out _, out _) || table.IsPrefix(postfixSymbol))))
                {
                    for (int t = 0; t < postfixTokens; t++)
                        Next();
                    expression = new OperatorExpr(postfixSymbol, [expression], true, From(start));
                }
                else if (At(TokenKind.OpenParen))
                {
                    expression = new CallExpr(expression, ParseArguments(), From(start));
                }
                else if (AtWord("with") && Peek(1).Kind == TokenKind.OpenBrace)
                {
                    // `x with { A = 1, B = 2 }`: a record copied with some fields replaced.
                    Next();
                    Next();
                    var changes = new List<WithChange>();
                    while (!At(TokenKind.CloseBrace) && !At(TokenKind.EndOfFile))
                    {
                        int changeStart = position;
                        var name = Expect(TokenKind.Identifier, "a field name");
                        Expect(TokenKind.Equal, "'='");
                        var value = ParseExpression();
                        changes.Add(new WithChange(name.Text, name.Span, value, From(changeStart)));
                        if (!Accept(TokenKind.Comma))
                            break;
                    }
                    Expect(TokenKind.CloseBrace, "'}'");
                    expression = new WithExpr(expression, [.. changes], From(start));
                }
                else if (At(TokenKind.Dot) || At(TokenKind.QuestionDot))
                {
                    bool conditional = Next().Kind == TokenKind.QuestionDot;
                    var name = Expect(TokenKind.Identifier, "a member name");
                    expression = new MemberExpr(expression, name.Text, TypeArgumentsBeforeCall(), From(start)) { NullConditional = conditional, NameSpan = name.Span };
                }
                else if (At(TokenKind.OpenBracket))
                {
                    Next();
                    var indices = new List<Expr> { ParseExpression() };
                    while (Accept(TokenKind.Comma))
                        indices.Add(ParseExpression());
                    Expect(TokenKind.CloseBracket, "']'");
                    expression = new IndexExpr(expression, [.. indices], From(start));
                }
                else if (At(TokenKind.ThinArrow))
                {
                    Next();
                    var name = Expect(TokenKind.Identifier, "a member name");
                    var pointee = new UnaryExpr(UnaryOperator.Dereference, expression, From(start));
                    expression = new MemberExpr(pointee, name.Text, [], From(start)) { NameSpan = name.Span };
                }
                else if ((At(TokenKind.PlusPlus) || At(TokenKind.MinusMinus)) && !StartsOperand(Peek(1).Kind))
                {
                    var op = Next().Kind == TokenKind.PlusPlus ? UnaryOperator.PostIncrement : UnaryOperator.PostDecrement;
                    expression = new UnaryExpr(op, expression, From(start));
                }
                else
                {
                    return expression;
                }
            }
        }

        private Expr[] ParseArguments()
        {
            Expect(TokenKind.OpenParen, "'('");
            var arguments = new List<Expr>();
            if (!At(TokenKind.CloseParen))
            {
                do
                    arguments.Add(ParseExpression());
                while (Accept(TokenKind.Comma));
            }
            Expect(TokenKind.CloseParen, "')'");
            return [.. arguments];
        }

        /// <summary>
        /// <c>$"a {x} b {y,8:F2}"</c>: written as the concatenation <c>"a " + x + " b " + y.ToString("F2").PadLeft(8)</c>
        /// (a format calls <c>ToString(format)</c>; an alignment pads, to the left when positive).
        /// </summary>
        private Expr ParseInterpolated()
        {
            int start = position;
            Next();
            Expr? result = null;
            void Add(Expr part)
            {
                result = result is null ? part : new BinaryExpr(BinaryOperator.Add, result, part, TextSpan.FromBounds(result.Span.Start, part.Span.End));
            }
            while (!At(TokenKind.InterpolationEnd) && !At(TokenKind.EndOfFile))
            {
                if (At(TokenKind.String))
                {
                    var piece = Next();
                    string value = (string)piece.Value!;
                    // The first piece stays even when empty: it makes `+` join text.
                    if (value.Length > 0 || result is null)
                        Add(new StringLiteral(value, piece.Span));
                    continue;
                }
                Expect(TokenKind.HoleStart, "'{'");
                var value2 = ParseExpression();
                Expr? alignment = null;
                if (Accept(TokenKind.Comma))
                    alignment = ParseUnary();
                Expr part = value2;
                if (At(TokenKind.HoleFormat))
                {
                    var format = Next();
                    part = new CallExpr(new MemberExpr(part, "ToString", [], part.Span) { NameSpan = format.Span }, [new StringLiteral((string)format.Value!, format.Span)], TextSpan.FromBounds(part.Span.Start, format.Span.End));
                }
                if (alignment is not null)
                {
                    // Positive: padded on the left (right-aligned); negative: on the right.
                    bool left = alignment is IntegerLiteral { Value.Sign: < 0 };
                    Expr width = alignment is IntegerLiteral literal && literal.Value.Sign < 0 ? new IntegerLiteral(-literal.Value, literal.Span) : alignment;
                    if (part == value2)
                        part = new BinaryExpr(BinaryOperator.Add, new StringLiteral("", part.Span), part, part.Span);
                    part = new CallExpr(new MemberExpr(part, left ? "PadRight" : "PadLeft", [], alignment.Span) { NameSpan = alignment.Span }, [width], TextSpan.FromBounds(part.Span.Start, alignment.Span.End));
                }
                Expect(TokenKind.HoleEnd, "'}'");
                Add(part);
            }
            Expect(TokenKind.InterpolationEnd, "'\"'");
            return result ?? new StringLiteral("", From(start));
        }

        private Expr ParsePrimary()
        {
            int start = position;
            var token = Current;
            switch (token.Kind)
            {
                case TokenKind.InterpolationStart:
                    return ParseInterpolated();
                case TokenKind.Identifier when token.Text is "checked" or "unchecked" && Peek(1).Kind == TokenKind.OpenParen:
                {
                    bool isChecked = Next().Text == "checked";
                    Next();
                    var inner = ParseExpression();
                    Expect(TokenKind.CloseParen, "')'");
                    return new CheckedExpr(inner, isChecked, From(start));
                }
                case TokenKind.Identifier when token.Text == "effect" && Peek(1).Kind == TokenKind.OpenBrace:
                {
                    // `effect { … }`: System.Reactive.Reactive.Watch(() => { … }), which gives the Effect.
                    Next();
                    var block = ParseBlock();
                    var span = From(start);
                    Expr watch = new MemberExpr(new MemberExpr(new MemberExpr(new NameExpr("System", [], span), "Reactive", [], span), "Reactive", [], span), "Watch", [], span);
                    return new CallExpr(watch, [new LambdaExpr([], null, block, span)], span);
                }
                case TokenKind.Integer:
                    Next();
                    return new IntegerLiteral((System.Numerics.BigInteger)token.Value!, token.Span) { Suffix = token.Suffix };
                case TokenKind.Float:
                    Next();
                    return new FloatLiteral((double)token.Value!, token.Span) { Suffix = token.Suffix, Text = token.Text };
                case TokenKind.True or TokenKind.False:
                    Next();
                    return new BoolLiteral(token.Kind == TokenKind.True, token.Span);
                case TokenKind.String:
                    Next();
                    return new StringLiteral((string)token.Value!, token.Span);
                case TokenKind.Dollar when Peek(1).Kind == TokenKind.Identifier && Peek(1).Text == "resource" && Peek(2).Kind == TokenKind.OpenParen:
                {
                    // `$resource("data/raw.bin")`: the bytes of a resource built into the program (checked by name when bound).
                    Next();
                    Next();
                    Next();
                    var name = Expect(TokenKind.String, "the resource's name (a string)");
                    Expect(TokenKind.CloseParen, "')'");
                    return new ResourceLiteral(name.Value as string ?? "", From(start));
                }
                case TokenKind.Char:
                    Next();
                    return new CharLiteral((string)token.Value!, token.Span);
                case TokenKind.Null:
                    Next();
                    return new NullLiteral(token.Span);
                case TokenKind.DeclBits or TokenKind.AllBits:
                    Next();
                    return new NameExpr(token.Text, [], token.Span);
                case TokenKind.Identifier when token.Text == "default" && Peek(1).Kind == TokenKind.OpenParen:
                {
                    Next();
                    Next();
                    var type = ParseType();
                    Expect(TokenKind.CloseParen, "')'");
                    return new DefaultExpr(type, From(start));
                }
                case TokenKind.Identifier when token.Text == "nameof" && Peek(1).Kind == TokenKind.OpenParen && NameOfType() is { } named:
                    return named;
                case TokenKind.Identifier when noLambda == 0 && Peek(1).Kind == TokenKind.Arrow:
                case TokenKind.OpenParen when noLambda == 0 && AtLambdaParameters():
                    return ParseLambda();
                case TokenKind.Identifier:
                    Next();
                    return new NameExpr(token.Text, TypeArgumentsBeforeCall(), From(start));
                case TokenKind.OpenParen:
                {
                    Next();
                    var inner = ParseExpression();
                    if (At(TokenKind.Comma))
                    {
                        // `(a, b)`: a tuple.
                        var items = new List<Expr> { inner };
                        while (Accept(TokenKind.Comma))
                            items.Add(ParseExpression());
                        Expect(TokenKind.CloseParen, "')'");
                        return new TupleExpr([.. items], From(start));
                    }
                    Expect(TokenKind.CloseParen, "')'");
                    return inner;
                }
                case TokenKind.OpenBracket:
                {
                    Next();
                    var elements = new List<Expr>();
                    while (!At(TokenKind.CloseBracket) && !At(TokenKind.EndOfFile))
                    {
                        elements.Add(ParseExpression());
                        if (!Accept(TokenKind.Comma))
                            break;
                    }
                    Expect(TokenKind.CloseBracket, "']'");
                    return new ArrayLiteralExpr([.. elements], From(start));
                }
                case TokenKind.New:
                {
                    Next();
                    var type = ParseTypeWithoutArraySuffix();
                    if (Accept(TokenKind.OpenBracket))
                    {
                        var lengths = new List<Expr> { ParseExpression() };
                        while (Accept(TokenKind.Comma))
                            lengths.Add(ParseExpression());
                        Expect(TokenKind.CloseBracket, "']'");
                        return new NewArrayExpr(type, [.. lengths], From(start));
                    }
                    var arguments = At(TokenKind.OpenParen) ? ParseArguments() : [];
                    return new NewExpr(type, arguments, From(start));
                }
                case TokenKind.TypeOf or TokenKind.SizeOf:
                {
                    Next();
                    Expect(TokenKind.OpenParen, "'('");
                    var type = ParseType();
                    Expect(TokenKind.CloseParen, "')'");
                    return token.Kind == TokenKind.TypeOf ? new TypeOfExpr(type, From(start)) : new SizeOfExpr(type, From(start));
                }
                default:
                    if (Lexer.IsPrimitive(token.Kind) && Peek(1).Kind == TokenKind.Dot)
                    {
                        Next();
                        return new NameExpr(token.Text, [], token.Span);
                    }
                    Error("AN0019", $"expected an expression, found {token}", token.Span);
                    if (token.Kind is not (TokenKind.EndOfFile or TokenKind.Semicolon or TokenKind.CloseBrace or TokenKind.CloseParen))
                        Next();
                    return new NullLiteral(new TextSpan(token.Span.Start, 0));
            }
        }

        /// <summary>The type after <c>new</c>: <c>new T[n]</c> makes an array of n elements, so <c>[</c> is not a type suffix.</summary>
        private TypeSyntax ParseTypeWithoutArraySuffix()
        {
            int start = position;
            if (Lexer.IsPrimitive(Current.Kind))
                return new PrimitiveTypeSyntax(Next().Kind, From(start));
            var name = ParseQualifiedName();
            var arguments = At(TokenKind.Less) ? ParseTypeArguments() : [];
            TypeSyntax type = new NamedTypeSyntax(name, arguments, From(start));
            while (At(TokenKind.Question) && Peek(1).Kind == TokenKind.OpenBracket)
            {
                Next();
                type = new NullableTypeSyntax(type, From(start));
            }
            return type;
        }

        // ── Patterns ───────────────────────────────────────

        private Pattern ParsePattern() => ParseOrPattern();

        /// <summary><c>{ A: p, B: q }</c> after an optional type.</summary>
        private Pattern ParsePropertyPattern(TypeSyntax? type, int start)
        {
            Expect(TokenKind.OpenBrace, "'{'");
            var members = new List<PropertySubpatternSyntax>();
            while (!At(TokenKind.CloseBrace) && !At(TokenKind.EndOfFile))
            {
                int memberStart = position;
                var name = Expect(TokenKind.Identifier, "a member name");
                Expect(TokenKind.Colon, "':'");
                var pattern = ParsePattern();
                members.Add(new PropertySubpatternSyntax(name.Text, name.Span, pattern, From(memberStart)));
                if (!Accept(TokenKind.Comma))
                    break;
            }
            Expect(TokenKind.CloseBrace, "'}'");
            return new PropertyPattern(type, [.. members], From(start));
        }

        private Pattern ParseOrPattern()
        {
            int start = position;
            var left = ParseAndPattern();
            while (AtWord("or"))
            {
                Next();
                left = new OrPattern(left, ParseAndPattern(), From(start));
            }
            return left;
        }

        private Pattern ParseAndPattern()
        {
            int start = position;
            var left = ParseNotPattern();
            while (AtWord("and"))
            {
                Next();
                left = new AndPattern(left, ParseNotPattern(), From(start));
            }
            return left;
        }

        private Pattern ParseNotPattern()
        {
            int start = position;
            if (AtWord("not"))
            {
                Next();
                return new NotPattern(ParseNotPattern(), From(start));
            }
            return ParsePrimaryPattern();
        }

        private Pattern ParsePrimaryPattern()
        {
            int start = position;
            if (AtWord("_"))
            {
                Next();
                return new DiscardPattern(From(start));
            }
            if (AtWord("var") && Peek(1).Kind == TokenKind.Identifier)
            {
                Next();
                return new BindingPattern(Next().Text, From(start));
            }
            if (At(TokenKind.OpenParen))
            {
                // `(p)` groups; `(p, q)` matches the positions of the value's own type.
                Next();
                var items = new List<Pattern> { ParsePattern() };
                while (Accept(TokenKind.Comma))
                    items.Add(ParsePattern());
                Expect(TokenKind.CloseParen, "')'");
                return items.Count == 1 ? items[0] : new PositionalPattern(null, [.. items], From(start));
            }
            if (At(TokenKind.OpenBrace))
                return ParsePropertyPattern(null, start);
            BinaryOperator? relational = Current.Kind switch
            {
                TokenKind.Less => BinaryOperator.Less,
                TokenKind.LessEqual => BinaryOperator.LessEqual,
                TokenKind.Greater => BinaryOperator.Greater,
                TokenKind.GreaterEqual => BinaryOperator.GreaterEqual,
                TokenKind.BangEqual => BinaryOperator.NotEqual,
                _ => null,
            };
            if (relational is { } op)
            {
                Next();
                return new RelationalPattern(op, ParseBinary(ShiftLevel), From(start));
            }
            // A type pattern: a primitive keyword, or a type name followed by a binding name.
            var typePattern = Try<Pattern>(() =>
            {
                if (!StartsType(Current))
                    return null;
                bool primitive = Lexer.IsPrimitive(Current.Kind);
                // `x is T ? a : b`: in a pattern `?` is the conditional, not a nullable type (as in C#).
                typePatterns++;
                TypeSyntax type;
                try
                {
                    type = ParseType();
                }
                finally
                {
                    typePatterns--;
                }
                if (At(TokenKind.OpenParen) && !primitive)
                {
                    // `Point(1, var y)`: the type's positions.
                    Next();
                    var items = new List<Pattern>();
                    if (!At(TokenKind.CloseParen))
                    {
                        items.Add(ParsePattern());
                        while (Accept(TokenKind.Comma))
                            items.Add(ParsePattern());
                    }
                    Expect(TokenKind.CloseParen, "')'");
                    return new PositionalPattern(type, [.. items], From(start));
                }
                if (At(TokenKind.OpenBrace) && !primitive)
                    return ParsePropertyPattern(type, start);
                if (At(TokenKind.Identifier) && Current.Text is not ("and" or "or" or "when"))
                    return new TypePattern(type, Next().Text, From(start));
                return primitive || type is NamedTypeSyntax { Arguments.Length: > 0 } or NullableTypeSyntax ? new TypePattern(type, null, From(start)) : null;
            });
            if (typePattern is not null)
                return typePattern;
            var value = ParseBinary(ShiftLevel);
            if (At(TokenKind.DotDot) || At(TokenKind.DotDotEqual))
            {
                bool inclusive = Next().Kind == TokenKind.DotDotEqual;
                return new RangePattern(value, ParseBinary(ShiftLevel), inclusive, From(start));
            }
            return new ConstantPattern(value, From(start));
        }
    }
}
