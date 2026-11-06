using System.Text;
using Larva.Hir;
using Larva.Hir.Services;
using H = Larva.Hir;

namespace Antril.Services
{
    /// <summary>
    /// A method body (HIR) written back as Antril: statements, expressions with the fewest parentheses their precedence
    /// needs, and the sugar the binder took apart put back where it can be seen (<c>a + b</c> on strings,
    /// <c>list[i]</c>, method groups).
    /// </summary>
    internal sealed class BodyPrinter
    {
        private readonly AntrilView view;
        private readonly CatalogType owner;
        private readonly HirCatalog catalog;
        private readonly CatalogAssembly assembly;
        private readonly H.HirBody body;
        private readonly ISet<string> imports;
        private readonly GenericNames names;
        private readonly IReadOnlyList<H.Parameter> parameters;
        private readonly string[] localNames;

        public BodyPrinter(AntrilView view, CatalogType owner, CatalogMember member, H.HirBody body, ISet<string> imports, H.MethodDef? accessor = null)
        {
            this.view = view;
            this.owner = owner;
            catalog = owner.Catalog;
            assembly = owner.Assembly;
            this.body = body;
            this.imports = imports;
            names = GenericNames.Of(owner, member);
            parameters = (accessor ?? member.Method)?.Parameters ?? [];
            localNames = [.. body.Locals.Select(l => l.Name.StartsWith('$') ? "_" + l.Name[1..] : l.Name)];
        }

        private string Type(H.HirType local) => view.TypeIn(local, owner, names, imports);

        /// <summary>The body after a signature: <c> =&gt; value;</c> when it only returns a value, else a block.</summary>
        public void Function(StringBuilder text, string indent, H.BlockStmt block)
        {
            if (block.Statements is [H.ReturnStmt { Value: { } value }])
            {
                string written = Expr(value, 0);
                if (!written.Contains('\n') && AntrilView.Unmarked(written).Length < 90)
                {
                    text.Append(" => ").Append(written).Append(";\n");
                    return;
                }
            }
            text.Append('\n');
            Block(text, block, indent);
        }

        // ── Statements ─────────────────────────────────────

        private void Block(StringBuilder text, H.BlockStmt block, string indent)
        {
            text.Append(indent).Append("{\n");
            foreach (var statement in block.Statements)
                Statement(text, statement, indent + "\t");
            text.Append(indent).Append("}\n");
        }

        /// <summary>The body of an if or a loop: a block as it is, one statement on the next line, indented.</summary>
        private void Nested(StringBuilder text, H.Stmt statement, string indent)
        {
            if (statement is H.BlockStmt block)
                Block(text, block, indent);
            else
                Statement(text, statement, indent + "\t");
        }

        private void Statement(StringBuilder text, H.Stmt statement, string indent)
        {
            switch (statement)
            {
                case H.BlockStmt { Statements.Length: > 1 } group when group.Statements.All(s => s is H.LocalStmt):
                    // `T a = 1, b = 2;`: its locals belong to the enclosing block.
                    foreach (var local in group.Statements)
                        Statement(text, local, indent);
                    return;
                case H.BlockStmt block:
                    Block(text, block, indent);
                    return;
                case H.LocalStmt local:
                    text.Append(indent).Append(Type(body.Locals[local.Local].Type)).Append(' ').Append(localNames[local.Local]);
                    if (local.Value is { } initial)
                        text.Append(" = ").Append(Expr(initial, 0));
                    text.Append(";\n");
                    return;
                case H.ExprStmt e:
                    text.Append(indent).Append(Expr(e.Expression, 0)).Append(";\n");
                    return;
                case H.IfStmt s:
                    text.Append(indent).Append("if (").Append(Expr(s.Condition, 0)).Append(")\n");
                    Nested(text, s.Then, indent);
                    while (s.Else is not null)
                    {
                        if (s.Else is H.IfStmt elseIf)
                        {
                            text.Append(indent).Append("else if (").Append(Expr(elseIf.Condition, 0)).Append(")\n");
                            Nested(text, elseIf.Then, indent);
                            s = elseIf;
                            continue;
                        }
                        text.Append(indent).Append("else\n");
                        Nested(text, s.Else, indent);
                        break;
                    }
                    return;
                case H.WhileStmt w:
                    text.Append(indent).Append("loop (").Append(Expr(w.Condition, 0)).Append(")\n");
                    Nested(text, w.Body, indent);
                    return;
                case H.DoWhileStmt d:
                    text.Append(indent).Append("do\n");
                    Nested(text, d.Body, indent);
                    text.Append(indent).Append("loop (").Append(Expr(d.Condition, 0)).Append(");\n");
                    return;
                case H.ForStmt f:
                    For(text, f, indent);
                    return;
                case H.ForeachStmt f:
                {
                    string variable = $"{Type(body.Locals[f.Local].Type)} {localNames[f.Local]}";
                    if (f.Collection is H.RangeExpr { Low: { } low, High: { } high } range)
                        text.Append(indent).Append("loop (").Append(variable).Append(" = ").Append(Expr(low, 12)).Append(range.Inclusive ? "..=" : "..").Append(Expr(high, 12)).Append(")\n");
                    else
                        text.Append(indent).Append("loop (").Append(variable).Append(" in ").Append(Expr(f.Collection, 0)).Append(")\n");
                    Nested(text, f.Body, indent);
                    return;
                }
                case H.SwitchStmt s:
                    text.Append(indent).Append("switch (").Append(Expr(s.Value, 0)).Append(")\n").Append(indent).Append("{\n");
                    foreach (var section in s.Sections)
                    {
                        string patterns = string.Join(" or ", section.Patterns.Select(Pattern));
                        text.Append(indent).Append('\t').Append(patterns);
                        if (section.Guard is { } guard)
                            text.Append(" when ").Append(Expr(guard, 0));
                        text.Append(" =>\n");
                        Nested(text, section.Body, indent + "\t");
                    }
                    text.Append(indent).Append("}\n");
                    return;
                case H.BreakStmt:
                    text.Append(indent).Append("break;\n");
                    return;
                case H.ContinueStmt:
                    text.Append(indent).Append("continue;\n");
                    return;
                case H.ReturnStmt r:
                    text.Append(indent).Append(r.Value is null ? "return" : "return " + Expr(r.Value, 0)).Append(";\n");
                    return;
                case H.ThrowStmt t:
                    text.Append(indent).Append(t.Value is null ? "throw" : "throw " + Expr(t.Value, 0)).Append(";\n");
                    return;
                case H.TryStmt t:
                    text.Append(indent).Append("try\n");
                    Block(text, t.Body, indent);
                    foreach (var clause in t.Catches)
                    {
                        text.Append(indent).Append("catch");
                        if (clause.Type is { } caught)
                        {
                            text.Append(" (").Append(Type(caught));
                            if (clause.Local >= 0)
                                text.Append(' ').Append(localNames[clause.Local]);
                            text.Append(')');
                        }
                        text.Append('\n');
                        Block(text, clause.Body, indent);
                    }
                    if (t.Finally is { } final)
                    {
                        text.Append(indent).Append("finally\n");
                        Block(text, final, indent);
                    }
                    return;
                case H.UsingStmt u:
                    text.Append(indent).Append("using (").Append(Type(body.Locals[u.Local].Type)).Append(' ').Append(localNames[u.Local])
                        .Append(" = ").Append(Expr(u.Resource, 0)).Append(")\n");
                    Nested(text, u.Body, indent);
                    return;
                case H.DeleteStmt d:
                    text.Append(indent).Append("delete ").Append(Expr(d.Value, 0)).Append(";\n");
                    return;
                case H.RegionStmt r:
                    text.Append(indent).Append("region\n");
                    Block(text, r.Body, indent);
                    return;
                case H.UnsafeStmt u:
                    text.Append(indent).Append("unsafe\n");
                    Block(text, u.Body, indent);
                    return;
                case H.CheckedStmt c:
                    text.Append(indent).Append(c.Checked ? "checked\n" : "unchecked\n");
                    Block(text, c.Body, indent);
                    return;
                case H.YieldStmt y:
                    text.Append(indent).Append(y.Value is null ? "yield break" : "yield " + Expr(y.Value, 0)).Append(";\n");
                    return;
                default:
                    text.Append(indent).Append("// ").Append(statement.GetType().Name).Append('\n');
                    return;
            }
        }

        /// <summary><c>loop (T i = a; condition; step)</c> when the loop has that shape, else its parts written out.</summary>
        private void For(StringBuilder text, H.ForStmt f, string indent)
        {
            if (f.Initializers is [H.LocalStmt { Value: { } initial } local] && f.Condition is { } condition && f.Steps is [var step])
            {
                text.Append(indent).Append("loop (").Append(Type(body.Locals[local.Local].Type)).Append(' ').Append(localNames[local.Local])
                    .Append(" = ").Append(Expr(initial, 0)).Append("; ").Append(Expr(condition, 0)).Append("; ").Append(Expr(step, 0)).Append(")\n");
                Nested(text, f.Body, indent);
                return;
            }
            text.Append(indent).Append("{\n");
            string inner = indent + "\t";
            foreach (var initializer in f.Initializers)
                Statement(text, initializer, inner);
            text.Append(inner).Append("loop (").Append(f.Condition is { } c ? Expr(c, 0) : "true").Append(")\n").Append(inner).Append("{\n");
            if (f.Body is H.BlockStmt block)
                foreach (var s in block.Statements)
                    Statement(text, s, inner + "\t");
            else
                Statement(text, f.Body, inner + "\t");
            foreach (var s in f.Steps)
                text.Append(inner).Append('\t').Append(Expr(s, 0)).Append(";\n");
            text.Append(inner).Append("}\n").Append(indent).Append("}\n");
        }

        // ── Expressions ────────────────────────────────────

        // Precedence, low to high: assignment and lambdas 1, ?: 2, ?? 3, || 4, && 5, | 6, ^ 7, & 8, == 9, < is as 10,
        // shifts 11, + 12, * 13, unary 14, postfix 15.

        private static int Precedence(H.BinaryOp op) => op switch
        {
            H.BinaryOp.Or => 4,
            H.BinaryOp.And => 5,
            H.BinaryOp.BitOr => 6,
            H.BinaryOp.BitXor => 7,
            H.BinaryOp.BitAnd => 8,
            H.BinaryOp.Equal or H.BinaryOp.NotEqual => 9,
            H.BinaryOp.Less or H.BinaryOp.LessEqual or H.BinaryOp.Greater or H.BinaryOp.GreaterEqual => 10,
            H.BinaryOp.ShiftLeft or H.BinaryOp.ShiftRight => 11,
            H.BinaryOp.Add or H.BinaryOp.Subtract => 12,
            _ => 13,
        };

        private static string Symbol(H.BinaryOp op) => op switch
        {
            H.BinaryOp.Add => "+", H.BinaryOp.Subtract => "-", H.BinaryOp.Multiply => "*", H.BinaryOp.Divide => "/",
            H.BinaryOp.Remainder => "%", H.BinaryOp.Power => "**", H.BinaryOp.BitAnd => "&", H.BinaryOp.BitOr => "|",
            H.BinaryOp.BitXor => "^", H.BinaryOp.ShiftLeft => "<<", H.BinaryOp.ShiftRight => ">>", H.BinaryOp.Equal => "==",
            H.BinaryOp.NotEqual => "!=", H.BinaryOp.Less => "<", H.BinaryOp.LessEqual => "<=", H.BinaryOp.Greater => ">",
            H.BinaryOp.GreaterEqual => ">=", H.BinaryOp.And => "&&", _ => "||",
        };

        private static string Wrap(string text, int precedence, int minimum) => precedence < minimum ? $"({text})" : text;

        /// <summary>An expression of the body as Antril.</summary>
        public string Expression(H.Expr e) => Expr(e, 0);

        private string Expr(H.Expr e, int minimum)
        {
            switch (e)
            {
                case H.LiteralExpr literal:
                    return view.Constant(literal.Value, owner, names, imports);
                case H.LocalExpr l:
                    return l.Local >= 0 && l.Local < localNames.Length ? AntrilView.Mark(localNames[l.Local], SemanticKind.Variable) : "?";
                case H.ParamExpr p:
                    return p.Parameter < parameters.Count ? AntrilView.Mark(parameters[p.Parameter].Name, SemanticKind.Parameter) : $"p{p.Parameter}";
                case H.ThisExpr:
                    return "this";
                case H.FieldExpr f:
                {
                    var (plain, declaring, _) = Member(f.Field, H.MemberKind.Field);
                    string name = Marked(plain, f.Field, H.MemberKind.Field);
                    if (f.Instance is null)
                    {
                        // `Counter<i64>.Total`: a generic type's static field names the instantiation.
                        string prefix = StaticPrefix(declaring);
                        if (f.TypeArguments is { Length: > 0 } written && prefix.Length > 0)
                            prefix = prefix[..^1] + "<" + string.Join(", ", written.Select(Type)) + ">.";
                        return prefix + name;
                    }
                    if (f.Instance is H.ThisExpr && !Shadowed(plain))
                        return name;
                    return Expr(f.Instance, 15) + "." + name;
                }
                case H.PropertyExpr p:
                {
                    var (plain, declaring, _) = Member(p.Property, H.MemberKind.Property);
                    string name = Marked(plain, p.Property, H.MemberKind.Property);
                    string receiver = p.Instance is null ? StaticPrefix(declaring).TrimEnd('.') : p.Instance is H.ThisExpr && !Shadowed(plain) ? "" : Expr(p.Instance, 15);
                    if (p.Indices.Length > 0)
                        return $"{(receiver.Length == 0 ? "this" : receiver)}[{Arguments(p.Indices)}]";
                    return receiver.Length == 0 ? name : receiver + "." + name;
                }
                case H.ElementExpr x:
                    return $"{Expr(x.Array, 15)}[{Arguments(x.Indices)}]";
                case H.ArrayLengthExpr { Dimension: { } d } a:
                    return Expr(a.Array, 15) + ".GetLength(" + Expr(d, 0) + ")";
                case H.ArrayLengthExpr a:
                    return Expr(a.Array, 15) + ".Length";
                case H.CallExpr call:
                    return Call(call, minimum);
                case H.InvokeExpr i:
                    return $"{Expr(i.Function, 15)}({Arguments(i.Arguments)})";
                case H.NewExpr created:
                {
                    string written = Type(created.Type);
                    // A tuple type prints as `(A, B)`: one made from its items is the literal `(a, b)`.
                    if (written.StartsWith('(') && created.Initializers.Length == 0 && created.Arguments.Length >= 2
                        && created.Type is H.NamedType { Arguments.Length: var count } && count == created.Arguments.Length)
                        return $"({Arguments(created.Arguments)})";
                    string made = $"new {written}({Arguments(created.Arguments)})";
                    if (created.Initializers.Length > 0)
                        made += " { " + string.Join(", ", created.Initializers.Select(i => $"{Marked(Member(i.Member, i.Kind).Name, i.Member, i.Kind)} = {Expr(i.Value, 1)}")) + " }";
                    return made;
                }
                case H.NewArrayExpr array:
                    return $"new {Type(array.Element)}[{Arguments(array.Lengths)}]";
                case H.ArrayLiteralExpr a:
                    return $"[{Arguments(a.Items)}]";
                case H.ResourceExpr r:
                    return $"$resource({AntrilView.Quote(r.Name)})";
                case H.UnaryExpr u:
                {
                    if (u.Op == H.UnaryOp.Not && Equality(u.Operand) is var (left, right))
                        return Wrap($"{left} != {right}", 9, minimum);
                    if (u.Op is H.UnaryOp.PostIncrement or H.UnaryOp.PostDecrement)
                        return Wrap(Expr(u.Operand, 15) + (u.Op == H.UnaryOp.PostIncrement ? "++" : "--"), 15, minimum);
                    string prefix = u.Op switch
                    {
                        H.UnaryOp.Negate => "-", H.UnaryOp.Plus => "+", H.UnaryOp.Not => "!", H.UnaryOp.BitNot => "~",
                        H.UnaryOp.PreIncrement => "++", _ => "--",
                    };
                    return Wrap(prefix + Expr(u.Operand, 14), 14, minimum);
                }
                case H.BinaryExpr b:
                {
                    int precedence = Precedence(b.Op);
                    return Wrap($"{Expr(b.Left, precedence)} {Symbol(b.Op)} {Expr(b.Right, precedence + 1)}", precedence, minimum);
                }
                case H.AssignExpr a:
                    return Wrap($"{Expr(a.Target, 2)} {(a.Compound is { } op ? Symbol(op) + "=" : "=")} {Expr(a.Value, 1)}", 1, minimum);
                case H.ConvertExpr c:
                    return Conversion(c, minimum);
                case H.IsExpr i:
                    return Wrap($"{Expr(i.Value, 11)} is {Pattern(i.Pattern)}", 10, minimum);
                case H.AsExpr a:
                    return Wrap($"{Expr(a.Value, 11)} as {Type(a.Type is H.NullableType n ? n.Inner : a.Type)}", 10, minimum);
                case H.SwitchExpr s:
                    return Wrap($"{Expr(s.Value, 15)} switch {{ {string.Join(", ", s.Arms.Select(arm => Pattern(arm.Pattern) + (arm.Guard is { } g ? " when " + Expr(g, 0) : "") + " => " + Expr(arm.Value, 1)))} }}", 1, minimum);
                case H.ConditionalExpr c:
                    return Wrap($"{Expr(c.Condition, 3)} ? {Expr(c.Then, 2)} : {Expr(c.Else, 2)}", 2, minimum);
                case H.CoalesceExpr c:
                    return Wrap($"{Expr(c.Left, 4)} ?? {Expr(c.Right, 3)}", 3, minimum);
                case H.ConditionalAccessExpr c:
                    return Expr(c.Receiver, 15) + "?" + Expr(c.Access, 15).TrimStart('.');
                case H.ReceiverExpr:
                    return "";
                case H.LambdaExpr l:
                    return Wrap(Lambda(l), 1, minimum);
                case H.MethodRefExpr m:
                {
                    var (name, declaring, _) = Member(m.Method, H.MemberKind.Method);
                    return m.Target is null ? StaticPrefix(declaring) + name : Expr(m.Target, 15) + "." + name;
                }
                case H.AwaitExpr a:
                    return Wrap("await " + Expr(a.Task, 14), 14, minimum);
                case H.TypeOfExpr t:
                    return $"typeof({Type(t.Operand)})";
                case H.SizeOfExpr s:
                    return $"sizeof({Type(s.Operand)})";
                case H.DefaultExpr d:
                    return d.Type is H.NullableType ? "null" : d.Type is H.PrimitiveType { Kind: H.Primitive.Bool } ? "false"
                        : d.Type is H.PrimitiveType ? "0" : $"default({Type(d.Type)})";
                case H.RangeExpr r:
                    return Wrap($"{(r.Low is { } low ? Expr(r.Low, 12) : "")}{(r.Inclusive ? "..=" : "..")}{(r.High is { } high ? Expr(high, 12) : "")}", 11, minimum);
                case H.InterpolatedExpr i:
                    return Wrap(string.Join(" + ", i.Parts.Select(p => Expr(p.Value, 13))), 12, minimum);
                case H.BorrowExpr b:
                    return Wrap((b.Mutable ? "&mut " : "&") + Expr(b.Place, 14), 14, minimum);
                case H.AddressOfExpr a:
                    return Wrap("&" + Expr(a.Place, 14), 14, minimum);
                case H.DerefExpr d:
                    return Wrap("*" + Expr(d.Pointer, 14), 14, minimum);
                case H.CheckedExpr c:
                    return (c.Checked ? "checked(" : "unchecked(") + Expr(c.Value, 0) + ")";
                case H.WithExpr w:
                    return $"{Expr(w.Value, 15)} with {{ {string.Join(", ", w.Changes.Select(c => $"{Member(c.Member, c.Kind).Name} = {Expr(c.Value, 1)}"))} }}";
                case H.BlockExpr b:
                {
                    var inner = new StringBuilder();
                    foreach (var statement in b.Statements)
                        Statement(inner, statement, "");
                    return $"{{ {inner.ToString().Replace("\n", " ").Trim()} {Expr(b.Value, 0)} }}";
                }
                default:
                    return $"/* {e.GetType().Name} */";
            }
        }

        /// <summary>`a.Equals(b)` on strings, as the binder makes `a == b`: written back as `a == b` (null otherwise).</summary>
        private (string Left, string Right)? Equality(H.Expr e)
        {
            if (e is H.CallExpr { Receiver: { } left, Arguments: [var right] } call && IsString(left.Type) && IsString(right.Type)
                && Member(call.Method, H.MemberKind.Method) is { Name: "Equals", Declaring.FullName: "System.String" })
                return (Expr(left, 10), Expr(right, 10));
            return null;
        }

        /// <summary>Whether a type is `str` (the string class, or String&lt;u8&gt; when it is generic): `+` and `==` work on it.</summary>
        private bool IsString(H.HirType type) => type is H.NamedType { Arguments: [] or [H.PrimitiveType { Kind: H.Primitive.U8 }] }
            && catalog.Resolve(type, assembly)?.FullName == "System.String";

        private string Call(H.CallExpr call, int minimum)
        {
            var (name, declaring, isStatic) = Member(call.Method, H.MemberKind.Method);
            var arguments = call.Arguments;
            // Sugar the binder took apart.
            if (declaring?.FullName == "System.String" && name == "Concat" && call.Receiver is null && arguments.Length == 2 && IsString(call.Type))
                return Wrap($"{Expr(Unconverted(arguments[0]), 12)} + {Expr(Unconverted(arguments[1]), 13)}", 12, minimum);
            if (Equality(call) is var (equalLeft, equalRight))
                return Wrap($"{equalLeft} == {equalRight}", 9, minimum);
            if (call.Receiver is { } indexed && !isStatic && name == "At" && arguments.Length == 1)
                return $"{Expr(indexed, 15)}[{Expr(arguments[0], 0)}]";
            if (call.Receiver is { } stored && !isStatic && name == "SetAt" && arguments.Length == 2)
                return Wrap($"{Expr(stored, 15)}[{Expr(arguments[0], 0)}] = {Expr(arguments[1], 1)}", 1, minimum);
            if (call.Receiver is null && name.StartsWith("$conv_", StringComparison.Ordinal) && arguments.Length == 1)
                return Wrap($"({Type(call.Type)}){Expr(arguments[0], 14)}", 14, minimum);
            if (call.Receiver is null && Syntax.Parser.OperatorSymbol(name) is { } symbol)
            {
                var custom = Syntax.Parser.CustomOperatorOf(name);
                if (arguments.Length == 1)
                    return custom?.Form == Syntax.OperatorForm.Postfix ? Wrap($"{Expr(arguments[0], 14)}{symbol}", 14, minimum) : Wrap($"{symbol}{Expr(arguments[0], 14)}", 14, minimum);
                if (arguments.Length == 2)
                {
                    if (custom is not null)
                        return Wrap($"{Expr(arguments[0], 14)} {symbol} {Expr(arguments[1], 14)}", 0, minimum);
                    int level = symbol switch
                    {
                        "|" => 6, "^" => 7, "&" => 8, "==" or "!=" => 9, "<" or "<=" or ">" or ">=" => 10, "+" or "-" => 12, _ => 13,
                    };
                    return Wrap($"{Expr(arguments[0], level)} {symbol} {Expr(arguments[1], level + 1)}", level, minimum);
                }
            }
            if (name == ".new")
                return $"{(call.Receiver is H.ThisExpr t && declaring != owner ? "base" : "this")}({Arguments(arguments)})";
            // `T.Zero`: a static member an interface requires, through the type argument that provides it.
            if (call.Receiver is null && declaring is { IsInterface: true } && call.TypeArguments.Length > 0)
            {
                string through = Type(call.TypeArguments[0]);
                if (name.StartsWith("get_", StringComparison.Ordinal) && arguments.Length == 0)
                    return $"{through}.{name[4..]}";
                return $"{through}.{name}({Arguments(arguments)})";
            }
            string typeArguments = call.TypeArguments.Length > 0 && !Inferable(call) ? "<" + string.Join(", ", call.TypeArguments.Skip(declaring?.Arity ?? 0).Select(Type)) + ">" : "";
            if (typeArguments == "<>")
                typeArguments = "";
            string target, marked = Marked(name, call.Method, H.MemberKind.Method);
            if (call.Receiver is null)
                target = StaticPrefix(declaring) + marked;
            else if (call.Receiver is H.ThisExpr && call.NonVirtual && declaring != owner)
                target = "base." + marked;
            else if (call.Receiver is H.ThisExpr && !Shadowed(name))
                target = marked;
            else
                target = Expr(call.Receiver, 15) + "." + marked;
            return $"{target}{typeArguments}({Arguments(arguments)})";
        }

        /// <summary>
        /// A call of a constructor from a constructor, as its head writes it: <c> : base(…)</c> (the base class's; nothing
        /// when it takes no arguments) or <c> : (…)</c> (another of this type's). Null when the call is something else.
        /// </summary>
        public string? ConstructorChain(H.CallExpr call)
        {
            if (call is not { Receiver: H.ThisExpr, NonVirtual: true } || Member(call.Method, H.MemberKind.Method) is not { Name: ".new" } target)
                return null;
            if (target.Declaring == owner)
                return $" : ({Arguments(call.Arguments)})";
            return call.Arguments.Length == 0 ? "" : $" : base({Arguments(call.Arguments)})";
        }

        /// <summary>The value an implicit ToString() was added to, when it is one (`"n" + i` keeps no `.ToString()`).</summary>
        private H.Expr Unconverted(H.Expr e) =>
            e is H.CallExpr { Receiver: { } value, Arguments.Length: 0 } call && Member(call.Method, H.MemberKind.Method).Name == "ToString" && !IsString(value.Type) ? value : e;

        /// <summary>Whether a generic call's method type arguments can be left to inference (each appears in a parameter).</summary>
        private bool Inferable(H.CallExpr call)
        {
            var member = catalog.Resolve(call.Method, H.MemberKind.Method, assembly);
            if (member?.Method is not { } method || method.Generics.Count == 0)
                return true;
            for (int g = 0; g < method.Generics.Count; g++)
            {
                if (!method.Parameters.Any(p => Mentions(p.Type, g)))
                    return false;
            }
            return true;
        }

        private static bool Mentions(H.HirType type, int generic) => type switch
        {
            H.GenericParamType { Owner: H.GenericOwner.Method } g => g.Index == generic,
            H.NamedType n => n.Arguments.Any(a => Mentions(a, generic)),
            H.ArrayType a => Mentions(a.Element, generic),
            H.NullableType n => Mentions(n.Inner, generic),
            H.FuncType f => f.Parameters.Any(p => Mentions(p, generic)) || Mentions(f.Result, generic),
            _ => false,
        };

        private string Conversion(H.ConvertExpr c, int minimum)
        {
            switch (c.Kind)
            {
                case H.ConversionKind.Upcast or H.ConversionKind.Wrap or H.ConversionKind.Function:
                    return Expr(c.Value, minimum);
                case H.ConversionKind.Numeric when Widening(c.Value.Type, c.Type):
                    return Expr(c.Value, minimum);
                case H.ConversionKind.Unwrap when c.Value is H.LocalExpr or H.ParamExpr or H.FieldExpr or H.PropertyExpr:
                    return Expr(c.Value, 15) + ".Value";
                default:
                    return Wrap($"({Type(c.Type)}){Expr(c.Value, 14)}", 14, minimum);
            }
        }

        private static bool Widening(H.HirType from, H.HirType to)
        {
            if (from is not H.PrimitiveType { Kind: var f } || to is not H.PrimitiveType { Kind: var t })
                return false;
            // Unsigned to a wider signed type stays written: `i32 + u32` has no type in common, `i32 + (i64)u32` has.
            if (f.IsInteger() && t.IsInteger())
                return f.IsSigned() == t.IsSigned() && t.Bits() > f.Bits();
            // A wide integer (128, 256 bits) goes to a float only by a cast (the binder gives it no implicit conversion).
            if (f.IsInteger() && t.IsFloat())
                return f.Bits() <= 64;
            return f.IsFloat() && t.IsFloat() && t.Bits() > f.Bits();
        }

        private string Lambda(H.LambdaExpr l)
        {
            // A method group the binder turned into a lambda that only calls it: the method's name again.
            var call = l.Body switch
            {
                H.ReturnStmt { Value: H.CallExpr c } => c,
                H.ExprStmt { Expression: H.CallExpr c } => c,
                _ => null,
            };
            if (call is not null && l.Parameters.Length > 0 && localNames[l.Parameters[0]].StartsWith("_g", StringComparison.Ordinal)
                && call.Arguments.Length == l.Parameters.Length && call.Arguments.Select((a, i) => a is H.LocalExpr local && local.Local == l.Parameters[i]).All(x => x))
            {
                var (name, declaring, _) = Member(call.Method, H.MemberKind.Method);
                return call.Receiver is null ? StaticPrefix(declaring) + name : Expr(call.Receiver, 15) + "." + name;
            }
            string head = l.Parameters.Length == 1 ? localNames[l.Parameters[0]] : "(" + string.Join(", ", l.Parameters.Select(p => localNames[p])) + ")";
            switch (l.Body)
            {
                case H.ReturnStmt { Value: { } value }:
                    return $"{head} => {Expr(value, 1)}";
                case H.ExprStmt { Expression: var expression }:
                    return $"{head} => {{ {Expr(expression, 0)}; }}";
                default:
                {
                    var inner = new StringBuilder();
                    Statement(inner, l.Body is H.BlockStmt ? l.Body : new H.BlockStmt([l.Body]), "");
                    return $"{head} => {inner.ToString().Replace("\n", " ").Replace("\t", "").Trim()}";
                }
            }
        }

        private string Arguments(H.Expr[] arguments) => string.Join(", ", arguments.Select(a => Expr(a, 1)));

        // ── Patterns ───────────────────────────────────────

        private string Pattern(H.Pattern pattern) => pattern switch
        {
            H.DiscardPattern => "_",
            H.ConstantPattern c => view.Constant(c.Value, owner, names, imports),
            H.RelationalPattern r => $"{Symbol(r.Op)} {view.Constant(r.Value, owner, names, imports)}",
            H.RangePattern r => $"{view.Constant(r.Low, owner, names, imports)}{(r.Inclusive ? "..=" : "..")}{view.Constant(r.High, owner, names, imports)}",
            H.TypePattern t => Type(t.Type),
            H.BindingPattern { Inner: H.TypePattern t } b => $"{Type(t.Type)} {localNames[b.Local]}",
            H.BindingPattern { Inner: H.NotPattern { Operand: H.ConstantPattern { Value: H.NullConstant } } } b => $"{Type(body.Locals[b.Local].Type)} {localNames[b.Local]}",
            H.BindingPattern { Inner: null } b => $"var {localNames[b.Local]}",
            H.BindingPattern b => $"{Pattern(b.Inner!)} and var {localNames[b.Local]}",
            H.PositionalPattern p => $"{(p.Type is { } pt ? Type(pt) : "")}({string.Join(", ", p.Items.Select(Pattern))})",
            H.PropertyPattern p => $"{(p.Type is { } qt ? Type(qt) + " " : "")}{{ {string.Join(", ", p.Members.Select(m => $"{Member(m.Member, m.Kind).Name}: {Pattern(m.Pattern)}"))} }}",
            H.AndPattern a => $"{Pattern(a.Left)} and {Pattern(a.Right)}",
            H.OrPattern o => $"{Pattern(o.Left)} or {Pattern(o.Right)}",
            H.NotPattern n => $"not {Pattern(n.Operand)}",
            _ => "_",
        };

        // ── Members ────────────────────────────────────────

        /// <summary>A member's name as a decompiled source shows it (what it is, a link to it).</summary>
        private string Marked(string name, H.MemberToken token, H.MemberKind kind) =>
            AntrilView.MarkMember(name, catalog.Resolve(token, kind, assembly), kind switch
            {
                H.MemberKind.Field => SemanticKind.Field,
                H.MemberKind.Property => SemanticKind.Property,
                H.MemberKind.Event => SemanticKind.Event,
                _ => SemanticKind.Method,
            });

        /// <summary>A member's name, the type that declares it, and whether it is static (references resolved by the catalog).</summary>
        private (string Name, CatalogType? Declaring, bool Static) Member(H.MemberToken token, H.MemberKind kind)
        {
            var hir = assembly.Hir;
            if (token.IsDefinition)
            {
                var type = token.Type < hir.Types.Count ? hir.Types[token.Type] : null;
                var declaring = token.Type < assembly.Types.Count ? assembly.Types[token.Type] : null;
                return kind switch
                {
                    H.MemberKind.Field when type?.Fields.ElementAtOrDefault(token.Member) is { } f => (f.Name, declaring, (f.Flags & (H.FieldFlags.Static | H.FieldFlags.Const)) != 0),
                    H.MemberKind.Property when type?.Properties.ElementAtOrDefault(token.Member) is { } p => (p.Name, declaring, (p.Flags & H.MemberFlags.Static) != 0),
                    H.MemberKind.Event when type?.Events.ElementAtOrDefault(token.Member) is { } e => (e.Name, declaring, (e.Flags & H.MemberFlags.Static) != 0),
                    H.MemberKind.Method when type?.Methods.ElementAtOrDefault(token.Member) is { } m => (m.Name, declaring, m.IsStatic),
                    _ => ("?", declaring, false),
                };
            }
            if (token.Member < hir.MemberRefs.Count)
            {
                var reference = hir.MemberRefs[token.Member];
                var resolved = catalog.Resolve(token, kind, assembly);
                return (reference.Name, resolved?.Type ?? catalog.Resolve(reference.Parent, assembly), resolved?.IsStatic ?? false);
            }
            return ("?", null, false);
        }

        /// <summary>What a static member is reached through: nothing in its own type or a module, else its type's name.</summary>
        private string StaticPrefix(CatalogType? declaring)
        {
            if (declaring is null || declaring == owner)
                return "";
            if (declaring.IsModule)
            {
                if (declaring.Namespace.Length > 0)
                    imports.Add(declaring.Namespace);
                return "";
            }
            // A primitive's static members go through its type's name (Single.Bits, not f32.Bits: a keyword is no receiver).
            if (declaring.Namespace.Length > 0)
                imports.Add(declaring.Namespace);
            return AntrilView.MarkType(declaring.FullName == "System.String" ? "String" : declaring.Name, declaring) + ".";
        }

        /// <summary>Whether a member name is also a parameter's or a local's (then `this.` is written).</summary>
        private bool Shadowed(string name) => parameters.Any(p => p.Name == name) || localNames.Contains(name);
    }
}
