using Antril.Syntax;
using H = Larva.Hir;

namespace Antril.Binding
{
    /// <summary>
    /// Quoted functions: a parameter of type <c>System.Quoted&lt;F&gt;</c> (written <c>const func&lt;T&gt; : R name</c>) given a
    /// lambda gets the lambda itself, its body as a tree of <c>System.QuoteNode</c>s (made once per place, through
    /// <c>System.Quotes.Tree</c>), the values of what the body uses from around it, and a number for the place. A
    /// sub-expression that does not name the lambda's parameters is a capture: worked out where the lambda is written (as
    /// any other argument), at each call, and handed over in <c>Captures</c>; the tree says where it goes.
    /// </summary>
    internal sealed partial class BodyBinder
    {
        // The node kinds of System.QuoteKind, by their order there.
        private const int QuoteParameter = 0, QuoteMember = 1, QuoteCapture = 2, QuoteNull = 3, QuoteBinary = 4, QuoteUnary = 5,
            QuoteCall = 6, QuoteFunction = 7, QuoteConditional = 8, QuoteUnsupported = 9;

        private TypeSymbol? QuotedSymbol => binder.Types.GetValueOrDefault("System.Quoted");

        /// <summary>The function type a <c>Quoted&lt;F&gt;</c> holds, or null when <paramref name="type"/> is not one.</summary>
        private H.FuncType? QuotedFunction(H.HirType? type) =>
            type is H.NamedType { Arguments: [H.FuncType function] } named && QuotedSymbol is { } quoted && named.Type == quoted.Token ? function : null;

        /// <summary>A function type, or the one a <c>Quoted&lt;F&gt;</c> holds.</summary>
        private H.FuncType? FunctionOf(H.HirType type) => type as H.FuncType ?? QuotedFunction(type);

        private H.NamedType? QuotedType(H.FuncType function) => QuotedSymbol is { } quoted ? H.HirType.Named(quoted.Token, function) : null;

        /// <summary><c>new Quoted&lt;F&gt;(function, Quotes.Tree(site, () =&gt; tree), [captures…], site)</c>.</summary>
        private H.Expr Quote(LambdaExpr written, H.Expr function, H.NamedType quotedType)
        {
            if (function is not H.LambdaExpr || IsError(function.Type))
                return function;
            if (written.Body is null)
                return Failed("AN2148", "a quoted function (a `const func` parameter) is one expression: `u => u.Age > 18`", written.Span);
            var quoted = QuotedSymbol!;
            if (binder.Types.GetValueOrDefault("System.QuoteNode") is not { } nodeSymbol || binder.Types.GetValueOrDefault("System.Quotes") is not { } quotes
                || binder.ObjectSymbol is not { } any || binder.StringType is not { } text)
                return Failed("AN2148", "quoted functions need System.Core (System.Quoted, QuoteNode, Quotes)", written.Span);
            var nodeType = nodeSymbol.Type;
            var nodeConstructor = nodeSymbol.Constructors.First(c => c.Parameters.Length == 4);
            var treeMethod = quotes.FindMethods("Tree").First(m => m.IsStatic && m.Parameters.Length == 2);
            var quotedConstructor = quoted.Constructors.First(c => c.Parameters.Length == 6);
            var anyNullable = new H.NullableType(any.Type);
            var i64 = H.HirType.Of(H.Primitive.I64);
            string[] names = [.. written.Parameters.Select(p => p.Name)];
            var captures = new List<H.Expr>();

            H.Expr Literal(long value, H.HirType type) => new H.LiteralExpr(new H.IntegerConstant(value, ((H.PrimitiveType)type).Kind), type);
            H.Expr Node(int kind, string name, int index, IEnumerable<H.Expr> children) =>
                new H.NewExpr(nodeConstructor.Token,
                    [Literal(kind, H.HirType.I32), new H.LiteralExpr(new H.StringConstant(name), text), Literal(index, H.HirType.I32),
                        new H.ArrayLiteralExpr(nodeType, [.. children], new H.ArrayType(nodeType))], [], nodeType);
            bool Names(Expr e) => AstRewriter.Descendants(e).Any(n => n is NameExpr { TypeArguments.Length: 0 } named && names.Contains(named.Name));

            H.Expr Tree(Expr e)
            {
                if (e is NullLiteral)
                    return Node(QuoteNull, "null", 0, []);
                if (!Names(e))
                {
                    // Worked out here, at each call: a capture.
                    var value = Expression(e, null);
                    captures.Add(Convert(value, anyNullable, e.Span));
                    return Node(QuoteCapture, source[e.Span], captures.Count - 1, []);
                }
                switch (e)
                {
                    case NameExpr name when Array.IndexOf(names, name.Name) is var at and >= 0:
                        return Node(QuoteParameter, name.Name, at, []);
                    case MemberExpr { NullConditional: false } member:
                        return Node(QuoteMember, member.Name, 0, [Tree(member.Target)]);
                    case BinaryExpr binary:
                        return Node(QuoteBinary, Symbol(binary.Operator), 0, [Tree(binary.Left), Tree(binary.Right)]);
                    case UnaryExpr { Operator: UnaryOperator.Not or UnaryOperator.Negate or UnaryOperator.Complement } unary:
                        return Node(QuoteUnary, unary.Operator switch { UnaryOperator.Not => "!", UnaryOperator.Negate => "-", _ => "~" }, 0, [Tree(unary.Operand)]);
                    case CallExpr { Target: MemberExpr { NullConditional: false } method } call:
                        return Node(QuoteCall, method.Name, 0, [Tree(method.Target), .. call.Arguments.Select(Tree)]);
                    case CallExpr { Target: NameExpr callee } call:
                        return Node(QuoteFunction, callee.Name, 0, [.. call.Arguments.Select(Tree)]);
                    case ConditionalExpr conditional:
                        return Node(QuoteConditional, "?:", 0, [Tree(conditional.Condition), Tree(conditional.Then), Tree(conditional.Else)]);
                    default:
                        return Node(QuoteUnsupported, source[e.Span], 0, []);
                }
            }

            var tree = Tree(written.Body);
            long site = Site(written.Span);
            var make = new H.LambdaExpr([], new H.ReturnStmt(tree), new H.FuncType([], nodeType));
            var kept = new H.CallExpr(null, treeMethod.Token, [], [Literal(site, i64), make], nodeType);
            var values = new H.ArrayLiteralExpr(anyNullable, [.. captures], new H.ArrayType(anyNullable));
            var (line, column) = source.LineColumn(written.Span.Start);
            var place = new H.LiteralExpr(new H.StringConstant($"{source.Path}({line},{column})"), text);
            var compiled = new H.LiteralExpr(new H.NullConstant(), new H.NullableType(text));
            return new H.NewExpr(quotedConstructor.Token, [function, kept, values, Literal(site, i64), place, compiled], [], quotedType);
        }

        /// <summary>A number for a place in the program: the assembly, the file and the position (FNV-1a, 63 bits).</summary>
        private long Site(TextSpan span)
        {
            ulong hash = 14695981039346656037;
            foreach (char c in $"{binder.Assembly.Name}|{source.Path}|{span.Start}")
            {
                hash ^= c;
                hash *= 1099511628211;
            }
            return (long)(hash & long.MaxValue);
        }
    }
}
