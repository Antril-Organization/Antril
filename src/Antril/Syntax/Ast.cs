using System.Numerics;

namespace Antril.Syntax
{
    public abstract record Node(TextSpan Span);

    public sealed record CompilationUnit(SourceText Source, ImportDirective[] Imports, MemberDecl[] Members, TextSpan Span) : Node(Span);

    public sealed record QualifiedName(string[] Parts, TextSpan Span) : Node(Span)
    {
        public override string ToString() => string.Join('.', Parts);
    }

    /// <summary>
    /// <c>import D;</c> (every name of the namespace), <c>import D as X;</c> (the namespace by another name: <c>X.Type</c>),
    /// <c>import A, B from D;</c> (only those names of it) and <c>import A, B from D as E, F;</c> (those names, each by the
    /// alias in its place). <see cref="Name"/> is the namespace.
    /// </summary>
    public sealed record ImportDirective(QualifiedName Name, TextSpan Span) : Node(Span)
    {
        /// <summary>The namespace's alias (<c>import D as X</c>), or null.</summary>
        public string? Alias { get; init; }
        public TextSpan AliasSpan { get; init; }

        /// <summary>The names taken from the namespace (<c>import A, B from D</c>); empty when it is imported whole.</summary>
        public ImportedName[] Picked { get; init; } = [];
    }

    /// <summary>A name an import takes from a namespace, and the name it has here (its alias, or its own).</summary>
    public sealed record ImportedName(string Name, string? Alias, TextSpan Span, TextSpan AliasSpan) : Node(Span)
    {
        public string Visible => Alias ?? Name;
    }

    [Flags]
    public enum Modifiers : uint
    {
        None = 0,
        Public = 1 << 0, Friendly = 1 << 1, Internal = 1 << 2, Inherited = 1 << 3, File = 1 << 4, Private = 1 << 5,
        Fixed = 1 << 6, Readonly = 1 << 7, Static = 1 << 8, Virtual = 1 << 9, Override = 1 << 10,
        Extern = 1 << 11, Unsafe = 1 << 12, Final = 1 << 13, Async = 1 << 14,
        /// <summary><c>reactive</c> field, <c>computed</c> property, <c>reacts</c> method (System.Reactive).</summary>
        Reactive = 1 << 15, Computed = 1 << 16, Reacts = 1 << 17,
        /// <summary><c>const</c>: a value known when compiling (a field of a namespace or a type, or a local).</summary>
        Const = 1 << 18,
        Access = Public | Friendly | Internal | Inherited | File | Private,
    }

    public sealed record AttributeSyntax(QualifiedName Name, Expr[] Arguments, TextSpan Span) : Node(Span);

    // ── Declarations ───────────────────────────────────────

    public abstract record MemberDecl(AttributeSyntax[] Attributes, Modifiers Modifiers, TextSpan Span) : Node(Span);

    /// <summary><c>into namespace A.B { … }</c>.</summary>
    public sealed record NamespaceDecl(QualifiedName Name, MemberDecl[] Members, TextSpan Span) : MemberDecl([], Modifiers.None, Span);

    /// <summary>How a type was declared: <c>class</c>, <c>base class</c> (abstract), <c>record class</c>, <c>value</c>, …</summary>
    public enum TypeKind : byte { Class, BaseClass, RecordClass, Value, Interface, Enum, BitflagEnum, Extension, RecordValue }

    public sealed record TypeDecl(AttributeSyntax[] Attributes, Modifiers Modifiers, TypeKind Kind, string Name, TextSpan NameSpan,
        GenericParamSyntax[] Generics, TypeSyntax[] Bases, MemberDecl[] Members, TextSpan Span) : MemberDecl(Attributes, Modifiers, Span)
    {
        /// <summary>
        /// "Attribute" for <c>attribute Tag</c>, "Exception" for <c>exception NotFound</c>: the class (named with that ending)
        /// derives from <c>System.Attribute</c> / <c>System.Exception</c> unless it names a class of that kind as its base.
        /// </summary>
        public string? Implied { get; init; }

        /// <summary>An extension block's extended type (<c>ext(i32 x)</c>: <c>i32</c>) and receiver name (<c>x</c>; null in <c>static ext(i32)</c>).</summary>
        public TypeSyntax? ExtensionTarget { get; init; }
        public string? ExtensionReceiver { get; init; }
    }

    public sealed record GenericParamSyntax(string Name, TextSpan Span) : Node(Span)
    {
        public ConstraintSyntax[] Constraints { get; init; } = [];

        /// <summary><c>T...</c>: a pack, any number of types (the last parameter of a variadic template).</summary>
        public bool Variadic { get; init; }
    }

    /// <summary>
    /// <c>static loop (E in T) { members }</c> in a variadic template: its members once per type of the pack, with
    /// <c>E</c> that type and each name numbered (<c>T item;</c> gives <c>item0</c>, <c>item1</c>…).
    /// </summary>
    public sealed record StaticLoopDecl(string Name, string Pack, MemberDecl[] Members, TextSpan Span) : MemberDecl([], Modifiers.None, Span)
    {
        /// <summary>The number of the first element's members (<c>from 1</c>: <c>Item1</c>, <c>Item2</c>…); 0 by default.</summary>
        public int First { get; init; }
    }

    /// <summary>
    /// One constraint of a type parameter: a type (an interface or base class), a word for a group of types (<c>copy</c>,
    /// <c>class</c>, <c>value</c>, <c>integer</c>…), or a list of the types allowed (<c>[u8, u16]</c>).
    /// </summary>
    public sealed record ConstraintSyntax(TypeSyntax? Type, string? Word, TypeSyntax[]? OneOf, TextSpan Span) : Node(Span)
    {
        /// <summary><c>T : (i64)T</c>: the parameter converts to this type (explicitly).</summary>
        public TypeSyntax? ConvertsTo { get; init; }
        /// <summary><c>T : new(i32, str)</c>: a constructor with these parameter types (<c>new()</c>: none).</summary>
        public TypeSyntax[]? Constructor { get; init; }
        /// <summary><c>T : fixed(N)</c>: a type of exactly N bytes.</summary>
        public int? FixedSize { get; init; }
        /// <summary><c>T : op +(T, T) : T</c>: the argument supports the operator.</summary>
        public OperatorConstraintSyntax? Operator { get; init; }
    }

    /// <summary>
    /// <c>op +(T, T) : T</c>, <c>left op -(T) : T</c>, <c>right op !(T) : T</c>: an operator a type argument must have,
    /// with its operands and result in terms of the parameter (<c>left</c> is a prefix operator, the default for one
    /// operand; <c>right</c> a postfix one).
    /// </summary>
    public sealed record OperatorConstraintSyntax(string Symbol, bool Postfix, TypeSyntax[] Operands, TypeSyntax Result, TextSpan Span) : Node(Span);

    /// <summary>An operator of the program's own (<c>a ^^ b</c>, <c>a!</c> with <c>right op !</c>), resolved by the operands' types.</summary>
    public sealed record OperatorExpr(string Symbol, Expr[] Operands, bool Postfix, TextSpan Span) : Expr(Span);

    /// <summary><c>T Name { get => …; set { … } }</c>; an accessor without a body (<c>get;</c>) is backed by a hidden field.</summary>
    public sealed record PropertyDecl(AttributeSyntax[] Attributes, Modifiers Modifiers, TypeSyntax Type, string Name, TextSpan NameSpan,
        AccessorSyntax[] Accessors, TextSpan Span) : MemberDecl(Attributes, Modifiers, Span);

    public sealed record AccessorSyntax(bool IsGetter, Modifiers Modifiers, BlockStmt? Body, TextSpan Span) : Node(Span)
    {
        /// <summary><c>base get;</c>: declared without a body for the implementing types to write (an interface's).</summary>
        public bool IsAbstract { get; init; }
    }

    public sealed record ParameterSyntax(TypeSyntax Type, string Name, TextSpan Span) : Node(Span)
    {
        /// <summary>The attributes written before it (<c>[Tag("n")] i32 n</c>).</summary>
        public AttributeSyntax[] Attributes { get; init; } = [];
    }

    /// <summary><c>Name(params) : Result</c> with a body (a block or <c>=&gt; e;</c>), or <c>;</c> (abstract, extern).</summary>
    public sealed record FunctionDecl(AttributeSyntax[] Attributes, Modifiers Modifiers, TypeSyntax ReturnType, string Name, TextSpan NameSpan,
        GenericParamSyntax[] Generics, ParameterSyntax[] Parameters, BlockStmt? Body, TextSpan Span) : MemberDecl(Attributes, Modifiers, Span)
    {
        public string? ExternName { get; init; }
        /// <summary>An operator's symbol (<c>op ^^</c>), its form, and its precedence and associativity as written.</summary>
        public string? OperatorSymbol { get; init; }
        public OperatorForm Form { get; init; }
        public Expr? Precedence { get; init; }
        public Expr? Associativity { get; init; }
        /// <summary>A conversion's target (<c>op (i64)(Money m)</c>): the method's result type.</summary>
        public TypeSyntax? ConversionTarget { get; init; }
    }

    public sealed record FieldDecl(AttributeSyntax[] Attributes, Modifiers Modifiers, TypeSyntax Type, string Name, TextSpan NameSpan, Expr? Initializer, TextSpan Span)
        : MemberDecl(Attributes, Modifiers, Span);

    /// <summary><c>new(params) [: base(args) | : (args)] { … }</c>, or <c>new(T a, U b);</c> storing each parameter in its field.</summary>
    public sealed record ConstructorDecl(AttributeSyntax[] Attributes, Modifiers Modifiers, ParameterSyntax[] Parameters, BlockStmt Body, TextSpan Span)
        : MemberDecl(Attributes, Modifiers, Span)
    {
        public Expr[]? BaseArguments { get; init; }
        public Expr[]? ThisArguments { get; init; }
        /// <summary><c>: fixed(n)</c>: the length of the class's <c>fixed T[]</c> field, from the parameters (before the object exists).</summary>
        public Expr? FixedLength { get; init; }
        /// <summary><c>new(i32 x, i32 y);</c>: no body; each parameter goes into the field of its name (the rest to <c>: base(…)</c>).</summary>
        public bool Bodiless { get; init; }
        public TextSpan HeadSpan { get; init; }
    }

    public sealed record EnumMemberDecl(AttributeSyntax[] Attributes, string Name, Expr? Value, TextSpan Span) : MemberDecl(Attributes, Modifiers.None, Span);

    // ── Types ──────────────────────────────────────────────

    public abstract record TypeSyntax(TextSpan Span) : Node(Span);

    public sealed record PrimitiveTypeSyntax(TokenKind Keyword, TextSpan Span) : TypeSyntax(Span);

    public sealed record VoidTypeSyntax(TextSpan Span) : TypeSyntax(Span);

    public sealed record NamedTypeSyntax(QualifiedName Name, TypeSyntax[] Arguments, TextSpan Span) : TypeSyntax(Span);

    public sealed record ArrayTypeSyntax(TypeSyntax Element, int Rank, TextSpan Span) : TypeSyntax(Span);

    /// <summary>An inline array of a fixed length: <c>u8[4]</c>, <c>u8[sizeof(T)]</c>, <c>u8[2 * sizeof(T)]</c> (a <c>fixed</c> field).</summary>
    public sealed record FixedArrayTypeSyntax(TypeSyntax Element, Expr Length, TextSpan Span) : TypeSyntax(Span);

    public sealed record NullableTypeSyntax(TypeSyntax Inner, TextSpan Span) : TypeSyntax(Span);

    public sealed record PointerTypeSyntax(TypeSyntax Target, TextSpan Span) : TypeSyntax(Span);

    public sealed record ReferenceTypeSyntax(TypeSyntax Target, bool Mutable, TextSpan Span) : TypeSyntax(Span);

    /// <summary><c>func&lt;T1, T2&gt; : R</c> (no <c>: R</c>: nothing returned): a function value's type.</summary>
    public sealed record FuncTypeSyntax(TypeSyntax[] Parameters, TypeSyntax? Result, TextSpan Span) : TypeSyntax(Span)
    {
        /// <summary><c>func*&lt;T1, T2&gt; : R</c>: the address of a C function (or of a static function C may call).</summary>
        public bool Native { get; init; }
    }

    // ── Statements ─────────────────────────────────────────

    public abstract record Stmt(TextSpan Span) : Node(Span);

    public sealed record BlockStmt(Stmt[] Statements, TextSpan Span) : Stmt(Span);

    /// <summary>
    /// <c>static loop (E in T) body</c> in a variadic template: the body once per type of the pack, with <c>E</c> that type
    /// and the pack's fields and parameters naming that type's (<c>item</c> is <c>item0</c>, then <c>item1</c>…).
    /// </summary>
    public sealed record StaticLoopStmt(string Name, string Pack, Stmt Body, TextSpan Span) : Stmt(Span);

    public sealed record LocalDeclStmt(TypeSyntax Type, string Name, TextSpan NameSpan, Expr? Initializer, TextSpan Span) : Stmt(Span)
    {
        /// <summary><c>const T name = value;</c>: a local whose value is known when compiling, never changed.</summary>
        public bool IsConst { get; init; }
    }

    /// <summary><c>T a, b = 1;</c>: locals declared in order in the enclosing scope.</summary>
    public sealed record LocalDeclGroup(LocalDeclStmt[] Locals, TextSpan Span) : Stmt(Span);

    public sealed record ExprStmt(Expr Expression, TextSpan Span) : Stmt(Span);

    public sealed record IfStmt(Expr Condition, Stmt Then, Stmt? Else, TextSpan Span) : Stmt(Span);

    /// <summary><c>loop (condition) body</c>.</summary>
    public sealed record WhileLoop(Expr Condition, Stmt Body, TextSpan Span) : Stmt(Span);

    /// <summary><c>do body loop (condition);</c>.</summary>
    public sealed record DoLoop(Stmt Body, Expr Condition, TextSpan Span) : Stmt(Span);

    /// <summary><c>loop (T i = a..b) body</c> (<c>..=</c> includes b).</summary>
    public sealed record RangeLoop(TypeSyntax Type, string Name, Expr From, Expr To, bool Inclusive, Stmt Body, TextSpan Span) : Stmt(Span);

    /// <summary><c>loop (T i = start; condition; step) body</c>.</summary>
    public sealed record ForLoop(TypeSyntax Type, string Name, Expr Initial, Expr Condition, Expr Step, Stmt Body, TextSpan Span) : Stmt(Span);

    /// <summary><c>loop (T i = start) body</c>: counts up from start, forever (until a break).</summary>
    public sealed record CounterLoop(TypeSyntax Type, string Name, Expr Start, Stmt Body, TextSpan Span) : Stmt(Span);

    /// <summary><c>loop (T x in collection) body</c>.</summary>
    public sealed record ForeachLoop(TypeSyntax Type, string Name, Expr Collection, Stmt Body, TextSpan Span) : Stmt(Span);

    public sealed record SwitchArm(Pattern Pattern, Expr? Guard, Node Body, TextSpan Span) : Node(Span);

    public sealed record SwitchStmt(Expr Value, SwitchArm[] Arms, TextSpan Span) : Stmt(Span);

    public sealed record CatchClauseSyntax(TypeSyntax? Type, string? Name, BlockStmt Body, TextSpan Span) : Node(Span);

    public sealed record TryStmt(BlockStmt Body, CatchClauseSyntax[] Catches, BlockStmt? Finally, TextSpan Span) : Stmt(Span);

    /// <summary><c>throw value;</c>, or <c>throw;</c> inside a catch (the exception being handled).</summary>
    public sealed record ThrowStmt(Expr? Value, TextSpan Span) : Stmt(Span);

    public sealed record ReturnStmt(Expr? Value, TextSpan Span) : Stmt(Span);

    public sealed record BreakStmt(TextSpan Span) : Stmt(Span);

    public sealed record ContinueStmt(TextSpan Span) : Stmt(Span);

    public sealed record DeleteStmt(Expr Value, TextSpan Span) : Stmt(Span);

    public sealed record UnsafeStmt(BlockStmt Body, TextSpan Span) : Stmt(Span);

    /// <summary><c>region { … }</c>: under arena collection, what the block allocates is freed (destructors first) when it ends.</summary>
    public sealed record RegionStmt(BlockStmt Body, TextSpan Span) : Stmt(Span);

    /// <summary><c>checked { … }</c> (Checked) or <c>unchecked { … }</c>.</summary>
    public sealed record CheckedStmt(BlockStmt Body, bool Checked, TextSpan Span) : Stmt(Span);

    /// <summary><c>yield value;</c> (the next value of an iterator), or <c>yield break;</c> (Value null: no more values).</summary>
    public sealed record YieldStmt(Expr? Value, TextSpan Span) : Stmt(Span);

    // ── Expressions ────────────────────────────────────────

    public abstract record Expr(TextSpan Span) : Node(Span);

    public sealed record IntegerLiteral(BigInteger Value, TextSpan Span) : Expr(Span)
    {
        /// <summary>The type suffix written (<c>12u32</c>), or null: the literal takes the type expected of it.</summary>
        public string? Suffix { get; init; }
    }

    public sealed record FloatLiteral(double Value, TextSpan Span) : Expr(Span)
    {
        /// <summary>The type suffix written (<c>1.5f32</c>), or null: <c>f64</c>, or <c>f32</c> when that is expected.</summary>
        public string? Suffix { get; init; }

        /// <summary>The literal's text as written (separators and suffix included, a leading '-' when negated): f16, f128 and f256 literals are read from it exactly.</summary>
        public string? Text { get; init; }
    }

    public sealed record BoolLiteral(bool Value, TextSpan Span) : Expr(Span);

    public sealed record CharLiteral(string Value, TextSpan Span) : Expr(Span);

    public sealed record StringLiteral(string Value, TextSpan Span) : Expr(Span);

    public sealed record NullLiteral(TextSpan Span) : Expr(Span);

    /// <summary><c>$resource("name")</c>: the bytes of a resource built into the program, as a new <c>u8[]</c>.</summary>
    public sealed record ResourceLiteral(string Name, TextSpan Span) : Expr(Span);

    public sealed record NameExpr(string Name, TypeSyntax[] TypeArguments, TextSpan Span) : Expr(Span);

    public sealed record MemberExpr(Expr Target, string Name, TypeSyntax[] TypeArguments, TextSpan Span) : Expr(Span)
    {
        /// <summary><c>a?.b</c>.</summary>
        public bool NullConditional { get; init; }
        public TextSpan NameSpan { get; init; }
    }

    public sealed record CallExpr(Expr Target, Expr[] Arguments, TextSpan Span) : Expr(Span);

    public sealed record IndexExpr(Expr Target, Expr[] Indices, TextSpan Span) : Expr(Span);

    public enum UnaryOperator : byte { Negate, Not, Complement, PreIncrement, PreDecrement, PostIncrement, PostDecrement, AddressOf, Dereference, BorrowMut }

    public sealed record UnaryExpr(UnaryOperator Operator, Expr Operand, TextSpan Span) : Expr(Span);

    public enum BinaryOperator : byte
    {
        Add, Subtract, Multiply, Divide, Remainder, And, Or, Xor, ShiftLeft, ShiftRight,
        Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual, LogicalAnd, LogicalOr, Coalesce,
    }

    public sealed record BinaryExpr(BinaryOperator Operator, Expr Left, Expr Right, TextSpan Span) : Expr(Span);

    /// <summary>A binary operator the program declares (<c>a &lt;+&gt; b</c>).</summary>
    public sealed record CustomBinaryExpr(string Symbol, Expr Left, Expr Right, TextSpan Span) : Expr(Span);

    /// <summary><c>target = value</c>, or <c>target op= value</c>.</summary>
    public sealed record AssignExpr(BinaryOperator? Operator, Expr Target, Expr Value, TextSpan Span) : Expr(Span);

    public sealed record ConditionalExpr(Expr Condition, Expr Then, Expr Else, TextSpan Span) : Expr(Span);

    public sealed record CastExpr(TypeSyntax Type, Expr Operand, TextSpan Span) : Expr(Span);

    public sealed record IsExpr(Expr Value, Pattern Pattern, TextSpan Span) : Expr(Span);

    public sealed record AsExpr(Expr Value, TypeSyntax Type, TextSpan Span) : Expr(Span);

    public sealed record SwitchExpr(Expr Value, SwitchArm[] Arms, TextSpan Span) : Expr(Span);

    public sealed record NewExpr(TypeSyntax Type, Expr[] Arguments, TextSpan Span) : Expr(Span);

    /// <summary><c>value with { Name = expr, … }</c>: a copy of a record with some fields replaced.</summary>
    public sealed record WithExpr(Expr Value, WithChange[] Changes, TextSpan Span) : Expr(Span);

    public sealed record WithChange(string Name, TextSpan NameSpan, Expr Value, TextSpan Span) : Node(Span);

    public sealed record NewArrayExpr(TypeSyntax Element, Expr[] Lengths, TextSpan Span) : Expr(Span);

    public sealed record ArrayLiteralExpr(Expr[] Elements, TextSpan Span) : Expr(Span);
    /// <summary><c>(a, b, c)</c>: a <c>System.ValueTuple</c> of the items (two or more).</summary>
    public sealed record TupleExpr(Expr[] Items, TextSpan Span) : Expr(Span);
    /// <summary><c>nameof(i32)</c>, <c>nameof(List&lt;T&gt;)</c>, <c>nameof(T[])</c>: a type that is more than a name, by its name.</summary>
    public sealed record NameOfTypeExpr(TypeSyntax Type, TextSpan Span) : Expr(Span);

    public sealed record TypeOfExpr(TypeSyntax Type, TextSpan Span) : Expr(Span);

    public sealed record SizeOfExpr(TypeSyntax Type, TextSpan Span) : Expr(Span);

    public sealed record DefaultExpr(TypeSyntax Type, TextSpan Span) : Expr(Span);

    public sealed record RangeExpr(Expr From, Expr To, bool Inclusive, TextSpan Span) : Expr(Span);

    /// <summary>A lambda's parameter: its type may be left to the function type it goes to.</summary>
    public sealed record LambdaParameter(TypeSyntax? Type, string Name, TextSpan Span) : Node(Span);

    /// <summary><c>x =&gt; e</c>, <c>(x, y) =&gt; e</c>, <c>(i32 x) =&gt; { … }</c>: a function value (the body an expression or a block).</summary>
    public sealed record LambdaExpr(LambdaParameter[] Parameters, Expr? Body, BlockStmt? Block, TextSpan Span) : Expr(Span);

    /// <summary><c>checked(e)</c> (Checked) or <c>unchecked(e)</c>.</summary>
    public sealed record CheckedExpr(Expr Value, bool Checked, TextSpan Span) : Expr(Span);

    /// <summary><c>await task</c>: in an <c>async</c> function, the task's result once it is done (pausing until then).</summary>
    public sealed record AwaitExpr(Expr Task, TextSpan Span) : Expr(Span);

    // ── Patterns ───────────────────────────────────────────

    public abstract record Pattern(TextSpan Span) : Node(Span);

    public sealed record DiscardPattern(TextSpan Span) : Pattern(Span);

    public sealed record ConstantPattern(Expr Value, TextSpan Span) : Pattern(Span);

    public sealed record RelationalPattern(BinaryOperator Operator, Expr Value, TextSpan Span) : Pattern(Span);

    public sealed record RangePattern(Expr Low, Expr High, bool Inclusive, TextSpan Span) : Pattern(Span);

    /// <summary><c>T</c> or <c>T name</c>.</summary>
    public sealed record TypePattern(TypeSyntax Type, string? Name, TextSpan Span) : Pattern(Span);

    /// <summary><c>var name</c>.</summary>
    public sealed record BindingPattern(string Name, TextSpan Span) : Pattern(Span);

    /// <summary><c>T(p, q)</c> or <c>(p, q)</c>: the positions (the fields in order, or what <c>Deconstruct</c> gives) match.</summary>
    public sealed record PositionalPattern(TypeSyntax? Type, Pattern[] Items, TextSpan Span) : Pattern(Span);

    /// <summary><c>T { A: p, B: q }</c> or <c>{ A: p }</c>: the named fields or properties match.</summary>
    public sealed record PropertyPattern(TypeSyntax? Type, PropertySubpatternSyntax[] Members, TextSpan Span) : Pattern(Span);

    public sealed record PropertySubpatternSyntax(string Name, TextSpan NameSpan, Pattern Pattern, TextSpan Span) : Node(Span);

    public sealed record AndPattern(Pattern Left, Pattern Right, TextSpan Span) : Pattern(Span);

    public sealed record OrPattern(Pattern Left, Pattern Right, TextSpan Span) : Pattern(Span);

    public sealed record NotPattern(Pattern Operand, TextSpan Span) : Pattern(Span);
}
