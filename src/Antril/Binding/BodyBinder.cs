using Larva;
using System.Numerics;
using Antril.Syntax;
using static Larva.Hir.Primitives;
using static Larva.Hir.HirTypes;
using H = Larva.Hir;

namespace Antril.Binding
{
    /// <summary>
    /// One body (a method, a function or a constructor) into HIR: names resolved, types checked, implicit conversions made
    /// explicit, overloads chosen. Errors go to the body's own bag; an expression that failed has <see cref="ErrorType"/>, which
    /// converts to anything without further reports.
    /// </summary>
    /// <summary>An expression already bound, passed where syntax is expected (an extension's receiver among the arguments).</summary>
    internal sealed record BoundExpr(H.Expr Value, TextSpan Span) : Expr(Span);

    internal sealed class BodyBinder
    {
        private static readonly H.HirType ErrorType = new H.NamedType(new H.TypeToken(int.MaxValue), []);

        private readonly Binder binder;
        private readonly MethodSymbol method;
        private readonly TypeSymbol owner;
        private readonly DiagnosticBag bag;
        private readonly DeclarationScope scope;
        private readonly SourceText source;
        private readonly List<H.HirLocal> locals = [];
        private readonly List<Dictionary<string, int>> scopes = [];
        private readonly Dictionary<string, int> parameters = new(StringComparer.Ordinal);

        /// <summary>What binding finds where (only while analyzing for an editor).</summary>
        private readonly BindingRecorder? recorder;

        /// <summary>The source span of each scope in <see cref="scopes"/> (where its locals can be used).</summary>
        private readonly List<TextSpan> containers = [];

        /// <summary>Enclosing loops and switches, innermost last (true: a loop).</summary>
        private List<bool> breakables = [];
        private int catchDepth, unsafeDepth;

        /// <summary>
        /// What `return` returns: the method's result, a lambda's while its body is bound, or null while a lambda's result
        /// is being inferred (the first `return` value decides it, in <see cref="inferredResult"/>).
        /// </summary>
        private H.HirType? returnType;
        private H.HirType? inferredResult;

        /// <summary>A function with <c>yield</c>: the type of the values it gives (null otherwise).</summary>
        private H.HirType? iteratorElement;

        /// <summary>How many lambdas the binding is inside.</summary>
        private int lambdaDepth;

        /// <summary>An async function: what its task gives (void for a plain Task); null otherwise.</summary>
        private H.HirType? asyncResult;

        /// <summary>The modules (top-level functions and fields) visible from here: the namespace, its parents, the imports.</summary>
        private readonly TypeSymbol[] modules;

        /// <summary>How many of <see cref="modules"/> are the namespace's own and its parents' (the rest are imported whole).</summary>
        private readonly int enclosingModules;
        private readonly int fileIndex;

        public BodyBinder(Binder binder, MethodSymbol method, DiagnosticBag bag)
        {
            this.binder = binder;
            this.method = method;
            owner = method.Owner;
            this.bag = bag;
            scope = method.Scope;
            source = scope.Source;
            returnType = method.Result;
            recorder = binder.Recorder;
            for (int p = 0; p < method.ParameterNames.Length; p++)
                parameters[method.ParameterNames[p]] = p;
            if ((method.Definition.Flags & H.MethodFlags.Unsafe) != 0 || (owner.Definition.Flags & H.TypeFlags.Unsafe) != 0)
                unsafeDepth++;
            var visible = new List<TypeSymbol>();
            foreach (var ns in scope.Enclosing())
                if (binder.Modules.TryGetValue(ns, out var module) && !visible.Contains(module))
                    visible.Add(module);
            enclosingModules = visible.Count;
            foreach (var ns in scope.Imports)
                if (binder.Modules.TryGetValue(ns, out var module) && !visible.Contains(module))
                    visible.Add(module);
            modules = [.. visible];
            fileIndex = binder.Assembly.Files.IndexOf(source.Path);
        }

        private bool IsStatic => method.IsStatic;

        /// <summary>The owner's own type parameters (<c>!0…</c>): the arguments of its members seen from inside it.</summary>
        private H.HirType[] OwnArguments => owner.Type is H.NamedType n ? n.Arguments : [];

        /// <summary>The interfaces and classes a type parameter in scope is constrained by.</summary>
        private IEnumerable<H.HirType> ConstraintsOf(H.GenericParamType param)
        {
            var generics = param.Owner == H.GenericOwner.Type ? owner.Definition.Generics : method.Definition.Generics;
            return param.Index < generics.Count ? generics[param.Index].Types : [];
        }

        /// <summary>
        /// Whether a type parameter is a number by `T : number` / `snumber` (INumber, ISignedNumber): its casts are promised
        /// conversions. A list of number types (`T : [u8, u16]`) is compiled per type instead and casts directly.
        /// </summary>
        private bool NumberParam(H.GenericParamType param) =>
            NumericParam(param) is null && ConstraintsOf(param).Any(c => c is H.NamedType n && binder.SymbolOf(n) is { } symbol && symbol.FullName is "System.INumber" or "System.ISignedNumber");

        /// <summary>Adds <paramref name="target"/> to what <paramref name="param"/> converts to (a requirement its arguments meet), once.</summary>
        private void PromiseConversion(H.GenericParamType param, H.HirType target)
        {
            var generics = param.Owner == H.GenericOwner.Type ? owner.Definition.Generics : method.Definition.Generics;
            if (param.Index >= generics.Count)
                return;
            lock (generics)
            {
                var current = generics[param.Index];
                if (!current.ConvertsTo.Any(t => Same(t, target)))
                    generics[param.Index] = current with { ConvertsTo = [.. current.ConvertsTo, target] };
            }
        }

        /// <summary>Adds <paramref name="source"/> to what converts into <paramref name="param"/> (a requirement its arguments meet), once.</summary>
        private void PromiseConversionFrom(H.GenericParamType param, H.HirType source)
        {
            var generics = param.Owner == H.GenericOwner.Type ? owner.Definition.Generics : method.Definition.Generics;
            if (param.Index >= generics.Count)
                return;
            lock (generics)
            {
                var current = generics[param.Index];
                if (!current.ConvertsFrom.Any(t => Same(t, source)))
                    generics[param.Index] = current with { ConvertsFrom = [.. current.ConvertsFrom, source] };
            }
        }

        private IEnumerable<H.HirType> ConvertsOf(H.GenericParamType param)
        {
            var generics = param.Owner == H.GenericOwner.Type ? owner.Definition.Generics : method.Definition.Generics;
            return param.Index < generics.Count ? generics[param.Index].ConvertsTo : [];
        }

        private void Error(string code, string message, TextSpan span) => bag.Error(code, message, source, span);

        /// <summary>
        /// A member of a type from another GC domain (<see cref="TypeSymbol.Domain"/>): AN1079 unless it is a static
        /// function over numbers, bools and packets (<paramref name="method"/> null: an object, field or property, which never cross).
        /// </summary>
        private bool CrossesDomain(TypeSymbol type, MethodSymbol? method, string what, TextSpan span)
        {
            if (type.Domain is null)
                return true;
            string? reason = method is null ? "its objects, fields and properties live in that domain's collector" : binder.NotCrossing(method, Show);
            if (reason is null)
                return true;
            Error("AN1079", Binder.CrossingMessage(what, type.Domain, reason), span);
            return false;
        }

        private H.Expr Failed(string code, string message, TextSpan span)
        {
            Error(code, message, span);
            return new H.DefaultExpr(ErrorType);
        }

        // ── Access ─────────────────────────────────────────

        /// <summary>Whether this body may use <paramref name="what"/> of <paramref name="declaring"/> with <paramref name="access"/> (AN1401 when not).</summary>
        private bool Allowed(H.Access access, TypeSymbol declaring, SourceText? declaredIn, string what, TextSpan span)
        {
            if (declaring.IsInterface || declaring.IsEnum || Binder.Accessible(access, declaring, declaredIn, owner, source))
                return true;
            Error("AN1401", $"`{what}` is {Binder.Describe(access, declaring)}", span);
            return false;
        }

        private bool Allowed(FieldSymbol field, TextSpan span) => Allowed(field.Access, field.Owner, field.Scope?.Source, field.Name, span);

        private bool Allowed(PropertySymbol property, bool write, TextSpan span) =>
            Allowed((write ? property.Setter : property.Getter)?.Access ?? property.Access, property.Owner, property.Owner.Scope.Source, property.Name, span);

        /// <summary>Whether this body may call <paramref name="method"/> (operators and conversions always).</summary>
        private bool Reachable(MethodSymbol method) =>
            method.Definition.Kind is H.MethodKind.Operator or H.MethodKind.Conversion || method.Owner.IsInterface || method.Owner.IsEnum
            || Binder.Accessible(method.Access, method.Owner, method.Scope.Source, owner, source);

        private bool Allowed(MethodSymbol method, TextSpan span) =>
            Reachable(method) || Allowed(method.Access, method.Owner, method.Scope.Source, method.IsConstructor ? $"new {method.Owner.Name}" : method.Name, span);

        private static bool IsError(H.HirType type) => ReferenceEquals(type, ErrorType) || type is H.NamedType { Type.Value: int.MaxValue };

        private string Show(H.HirType type) => IsError(type) ? "?"
            : type is H.GenericParamType p && (p.Owner == H.GenericOwner.Type ? owner.Definition.Generics : method.Definition.Generics) is { } generics && p.Index < generics.Count ? generics[p.Index].Name
            : Words(H.Text.HirPrinter.Print(type, binder.Assembly));

        /// <summary>The core library's `any` and `str` as the words a program writes, not their full names.</summary>
        private static string Words(string printed) => printed
            .Replace("[System.Core]System.String<u8>", "str").Replace("[System.Core]System.Any", "any");

        public H.HirBody Bind()
        {
            var body = method.Function?.Body ?? method.Constructor?.Body ?? method.AccessorBody;
            var bodySpan = method.Function?.Span ?? method.Constructor?.Span ?? body?.Span ?? default;
            Push(bodySpan);
            if (recorder is not null)
            {
                recorder.Add(new RecordedBody(source, method, bodySpan));
                for (int p = 0; p < method.ParameterNames.Length; p++)
                    recorder.Add(new RecordedLocal(source, method.ParameterNames[p], method.Parameters[p], method.NameSpan, bodySpan, Parameter: true));
            }
            if (method.Function is { Body: { } functionBody } function && Binder.HasYield(functionBody))
            {
                if (method.Result is H.NamedType { Arguments: [var element] } named && binder.IteratorSymbol is { } iterator && named.Type == iterator.Token)
                    iteratorElement = element;
                else
                    Error("AN2110", $"a function with `yield` gives an `Iterator<T>` (`{function.Name}` gives `{Show(method.Result)}`)", method.NameSpan);
            }
            if (method.Function is { } asyncFunction && (asyncFunction.Modifiers & Modifiers.Async) != 0)
            {
                if (method.Result is H.NamedType { Arguments: [] } plain && binder.TaskSymbol is { } task && plain.Type == task.Token)
                    asyncResult = H.HirType.Void;
                else if (method.Result is H.NamedType { Arguments: [var value] } named && binder.TaskOfSymbol is { } taskOf && named.Type == taskOf.Token)
                    asyncResult = value;
                else
                    Error("AN2114", $"an async function gives a `Task` or a `Task<T>` (`{asyncFunction.Name}` gives `{Show(method.Result)}`)", method.NameSpan);
                if (asyncResult is not null)
                    returnType = asyncResult;
            }
            var statements = new List<H.Stmt>();
            if (method.IsConstructor)
                statements.AddRange(ConstructorPrologue());
            if (method.IsConstructor && method.Constructor is { Bodiless: true } bodiless)
                statements.AddRange(FieldStores(bodiless));
            if (method.IsStaticConstructor)
            {
                foreach (var field in owner.StaticInitializedFields)
                {
                    var value = Convert(Expression(field.Declaration!.Initializer!, field.Type), field.Type, field.Declaration.Initializer!.Span);
                    statements.Add(new H.ExprStmt(new H.AssignExpr(new H.FieldExpr(null, field.Token, field.Type), value, field.Type)));
                }
            }
            if (method.Synthetic != SyntheticBody.None)
                statements.AddRange(SyntheticStatements(method.Synthetic));
            if (body is not null)
            {
                foreach (var statement in body.Statements)
                    statements.Add(Statement(statement));
            }
            // `reacts` methods start as effects once the object is constructed (not from a constructor that delegates to another).
            if (method.IsConstructor && method.Constructor is null or { ThisArguments: null } && owner.IsClass
                && binder.Types.GetValueOrDefault("System.Reactive.Reactive") is { } reactive
                && reactive.FindMethods("Watch").FirstOrDefault(m => m.IsStatic && m.Parameters.Length == 1) is { } watch)
            {
                foreach (var reacts in owner.Methods.Values.SelectMany(m => m).Where(m => m.Function is { } f && (f.Modifiers & Modifiers.Reacts) != 0 && !m.IsStatic && m.Parameters.Length == 0))
                {
                    var call = new H.ExprStmt(new H.CallExpr(new H.ThisExpr(owner.Type), reacts.Token, [], [], reacts.Result));
                    var runner = new H.LambdaExpr([], new H.BlockStmt([call]), new H.FuncType([], H.HirType.Void));
                    statements.Add(new H.ExprStmt(new H.CallExpr(null, watch.Token, [], [runner], watch.Result)));
                }
            }
            var block = new H.BlockStmt([.. statements]);
            // A destructor runs, then its base's (whatever happens in it: a finally).
            if (method.Name == "destroy" && method.Parameters.Length == 0 && !method.IsStatic && owner.IsClass && owner.Base is { } baseClass
                && owner.BaseType is { } baseType && baseClass.FindMethods("destroy").FirstOrDefault(m => !m.IsStatic && m.Parameters.Length == 0) is { } baseDestroy)
            {
                var arguments = binder.AncestorArguments(baseType, baseDestroy.Owner);
                var chained = new H.ExprStmt(new H.CallExpr(new H.ThisExpr(baseType), baseDestroy.Token, arguments, [], H.HirType.Void) { NonVirtual = true });
                block = new H.BlockStmt([new H.TryStmt(block, [], new H.BlockStmt([chained]))]);
            }
            if (iteratorElement is { } yielded)
            {
                // Made into an iterator class once every body is bound.
                var bound = new H.HirBody([.. locals], block);
                binder.AddIterator(new IteratorRequest(method, bound, yielded));
                return bound;
            }
            if (asyncResult is { } gives)
            {
                if (gives != H.HirType.Void && !Terminates(block))
                    Error("AN2001", $"`{method.Function?.Name ?? method.Name}` does not return a value on every path", method.NameSpan);
                var bound = new H.HirBody([.. locals], block);
                binder.AddIterator(new IteratorRequest(method, bound, gives, Async: true));
                return bound;
            }
            if (method.Result != H.HirType.Void && !Terminates(block))
                Error("AN2001", $"`{method.Function?.Name ?? method.Name}` does not return a value on every path", method.NameSpan);
            return new H.HirBody([.. locals], block);
        }

        // ── Constructors ───────────────────────────────────

        /// <summary>
        /// What runs before a constructor's body: the field initializers, then the base class's constructor (the one its
        /// <c>: base(…)</c> picks, else the one without parameters); a constructor that delegates with <c>: (…)</c> runs the
        /// other constructor instead.
        /// </summary>
        private IEnumerable<H.Stmt> ConstructorPrologue()
        {
            var self = new H.ThisExpr(owner.Type);
            var declaration = method.Constructor;
            if (declaration?.ThisArguments is { } delegated)
            {
                var call = ResolveCall(owner.Constructors, delegated, declaration.HeadSpan, "a constructor of this type", _ => OwnArguments);
                if (call is { } target)
                    yield return new H.ExprStmt(new H.CallExpr(self, target.Method.Token, target.TypeArguments, target.Arguments, H.HirType.Void) { NonVirtual = true });
                yield break;
            }
            foreach (var field in owner.InitializedFields)
            {
                var value = Convert(Expression(field.Declaration!.Initializer!, field.Type), field.Type, field.Declaration.Initializer!.Span);
                yield return new H.ExprStmt(new H.AssignExpr(new H.FieldExpr(self, field.Token, field.Type), value, field.Type));
            }
            if (owner.Base is { } baseType && owner.BaseType is { } baseOfOwner)
            {
                var span = declaration?.HeadSpan ?? owner.Declaration?.NameSpan ?? default;
                if (declaration?.BaseArguments is { } arguments || baseType.Constructors.Count > 0)
                {
                    var call = ResolveCall(baseType.Constructors, declaration?.BaseArguments ?? [], span, $"a constructor of `{baseType.Name}`", _ => baseOfOwner.Arguments);
                    if (call is { } target)
                        yield return new H.ExprStmt(new H.CallExpr(new H.ThisExpr(baseOfOwner), target.Method.Token, target.TypeArguments, target.Arguments, H.HirType.Void) { NonVirtual = true });
                }
            }
            else if (declaration?.BaseArguments is not null)
            {
                Error("AN2002", $"`{owner.Name}` has no base class", declaration.HeadSpan);
            }
        }

        /// <summary>
        /// A constructor without a body: each parameter goes into the instance field of its name; one that names no field
        /// must be passed on to <c>: base(…)</c> (or <c>: (…)</c>), else it is an error.
        /// </summary>
        private IEnumerable<H.Stmt> FieldStores(ConstructorDecl declaration)
        {
            var self = new H.ThisExpr(owner.Type);
            var passedOn = new HashSet<string>(StringComparer.Ordinal);
            foreach (var argument in (declaration.BaseArguments ?? []).Concat(declaration.ThisArguments ?? []))
            {
                if (argument is NameExpr { TypeArguments.Length: 0 } name)
                    passedOn.Add(name.Name);
            }
            for (int p = 0; p < declaration.Parameters.Length; p++)
            {
                var parameter = declaration.Parameters[p];
                // The field of the parameter's name; failing that, the one field whose name differs only in case (`x` into `X`).
                if (!owner.Fields.TryGetValue(parameter.Name, out var field))
                    field = owner.Fields.Values.Where(f => !f.IsStatic && string.Equals(f.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray() is [var only] ? only : null;
                if (field is not null && !field.IsStatic)
                {
                    var value = Convert(new H.ParamExpr(p, method.Parameters[p]), field.Type, parameter.Span);
                    yield return new H.ExprStmt(new H.AssignExpr(new H.FieldExpr(self, field.Token, field.Type), value, field.Type));
                }
                else if (!passedOn.Contains(parameter.Name))
                {
                    Error("AN2131", $"`{parameter.Name}` names no field of `{owner.Name}`: a constructor without a body stores each parameter in the field of its name, or passes it to `: base(…)`", parameter.Span);
                }
            }
        }

        // ── Scopes ─────────────────────────────────────────

        private void Push() => Push(containers.Count > 0 ? containers[^1] : default);

        private void Push(TextSpan span)
        {
            scopes.Add(new Dictionary<string, int>(StringComparer.Ordinal));
            containers.Add(span);
        }

        private void Pop()
        {
            scopes.RemoveAt(scopes.Count - 1);
            containers.RemoveAt(containers.Count - 1);
        }

        private int Declare(string name, H.HirType type, TextSpan span)
        {
            foreach (var level in scopes)
            {
                if (level.ContainsKey(name))
                {
                    Error("AN2003", $"`{name}` is already declared here", span);
                    break;
                }
            }
            if (parameters.ContainsKey(name))
                Error("AN2004", $"`{name}` is a parameter of this function", span);
            locals.Add(new H.HirLocal(name, IsError(type) ? H.HirType.I32 : type));
            int index = locals.Count - 1;
            scopes[^1][name] = index;
            if (recorder is not null && !name.StartsWith('$'))
            {
                recorder.Add(new RecordedLocal(source, name, type, span, containers[^1], Parameter: false));
                localSpans[index] = span;
            }
            return index;
        }

        /// <summary>Where each local is declared (recorded locals only).</summary>
        private readonly Dictionary<int, TextSpan> localSpans = [];

        private int? LookupLocal(string name)
        {
            for (int i = scopes.Count - 1; i >= 0; i--)
            {
                if (scopes[i].TryGetValue(name, out int local))
                    return local;
            }
            return null;
        }

        private H.HirType? Resolve(TypeSyntax syntax) => binder.ResolveType(syntax, scope, bag);

        // ── Statements ─────────────────────────────────────

        private H.BlockStmt Block(BlockStmt block)
        {
            Push(block.Span);
            var statements = block.Statements.Select(Statement).ToArray();
            Pop();
            return new H.BlockStmt(statements);
        }

        private H.Stmt Nested(Stmt statement)
        {
            Push(statement.Span);
            var bound = Statement(statement);
            Pop();
            return bound;
        }

        private H.Stmt At(H.Stmt statement, TextSpan span)
        {
            var (line, column) = source.LineColumn(span.Start);
            return statement with { At = new H.SourceLocation(fileIndex, line, column) };
        }

        private H.Stmt Statement(Stmt statement) => At(StatementCore(statement), statement.Span);

        private H.Stmt StatementCore(Stmt statement)
        {
            switch (statement)
            {
                case BlockStmt block:
                    return Block(block);
                case StaticLoopStmt loop:
                    Error("AN1420", $"`static loop` repeats code per type of a pack: it goes in a variadic template (`<{loop.Pack}...>`)", loop.Span);
                    return new H.BlockStmt([]);
                case LocalDeclStmt local:
                    return LocalDeclaration(local);
                case LocalDeclGroup group:
                    return new H.BlockStmt([.. group.Locals.Select(l => At(LocalDeclaration(l), l.Span))]);
                case ExprStmt e:
                    return new H.ExprStmt(Expression(e.Expression, null));
                case IfStmt s:
                {
                    var condition = Condition(s.Condition);
                    var then = Nested(s.Then);
                    var otherwise = s.Else is { } e ? Nested(e) : null;
                    return new H.IfStmt(condition, then, otherwise);
                }
                case WhileLoop loop:
                {
                    var condition = Condition(loop.Condition);
                    return new H.WhileStmt(condition, Loop(loop.Body));
                }
                case DoLoop loop:
                {
                    var body = Loop(loop.Body);
                    return new H.DoWhileStmt(body, Condition(loop.Condition));
                }
                case RangeLoop loop:
                {
                    Push(loop.Span);
                    var type = Counter(loop.Type, loop.Span);
                    var from = Convert(Expression(loop.From, type), type, loop.From.Span);
                    var to = Convert(Expression(loop.To, type), type, loop.To.Span);
                    int variable = Declare(loop.Name, type, loop.Span);
                    var body = Loop(loop.Body);
                    Pop();
                    return new H.ForeachStmt(variable, new H.RangeExpr(from, to, loop.Inclusive, type), body);
                }
                case ForLoop loop:
                {
                    Push(loop.Span);
                    var type = Resolve(loop.Type) ?? ErrorType;
                    binder.CheckDeclared(type, OwnGeneric, source, loop.Type.Span);
                    var initial = Convert(Expression(loop.Initial, type), type, loop.Initial.Span);
                    int variable = Declare(loop.Name, type, loop.Span);
                    var condition = Condition(loop.Condition);
                    var step = Expression(loop.Step, null);
                    var body = Loop(loop.Body);
                    Pop();
                    return new H.ForStmt([new H.LocalStmt(variable, initial)], condition, [step], body);
                }
                case CounterLoop loop:
                {
                    Push(loop.Span);
                    var type = Counter(loop.Type, loop.Span);
                    var start = Convert(Expression(loop.Start, type), type, loop.Start.Span);
                    int variable = Declare(loop.Name, type, loop.Span);
                    var body = Loop(loop.Body);
                    Pop();
                    var counter = new H.LocalExpr(variable, type);
                    return new H.ForStmt([new H.LocalStmt(variable, start)], null, [new H.UnaryExpr(H.UnaryOp.PostIncrement, counter, type)], body);
                }
                case ForeachLoop loop:
                {
                    if (loop.Collection is RangeExpr range)
                        return StatementCore(new RangeLoop(loop.Type, loop.Name, range.From, range.To, range.Inclusive, loop.Body, loop.Span));
                    return Foreach(loop);
                }
                case SwitchStmt s:
                {
                    var value = Expression(s.Value, null);
                    var sections = new List<H.SwitchSection>();
                    breakables.Add(false);
                    foreach (var arm in s.Arms)
                    {
                        Push(arm.Span);
                        var pattern = Pattern(arm.Pattern, value.Type);
                        var guard = arm.Guard is { } g ? Condition(g) : null;
                        var body = arm.Body is Stmt armStatement ? Statement(armStatement) : new H.ExprStmt(Expression((Expr)arm.Body, null));
                        Pop();
                        sections.Add(new H.SwitchSection([pattern], guard, body));
                    }
                    breakables.RemoveAt(breakables.Count - 1);
                    return new H.SwitchStmt(value, [.. sections]);
                }
                case TryStmt t:
                    return Try(t);
                case ThrowStmt t:
                {
                    if (t.Value is null)
                    {
                        if (catchDepth == 0)
                            Error("AN2006", "`throw;` rethrows the exception being handled: it goes inside a catch", t.Span);
                        return new H.ThrowStmt(null);
                    }
                    var value = Expression(t.Value, null);
                    // A type parameter constrained by a class (`T : exception`): thrown as that class.
                    if (value.Type is H.GenericParamType thrownParam && ConstraintsOf(thrownParam).FirstOrDefault(binder.IsClass) is { } thrownClass)
                        value = new H.ConvertExpr(value, H.ConversionKind.Upcast, thrownClass);
                    if (!IsError(value.Type) && !binder.IsClass(value.Type) && value.Type is not H.NullableType)
                        Error("AN2007", $"only objects are thrown, not a `{Show(value.Type)}`", t.Value.Span);
                    return new H.ThrowStmt(value);
                }
                case CheckedStmt c:
                    return new H.CheckedStmt(Block(c.Body), c.Checked);
                case YieldStmt y:
                {
                    if (lambdaDepth > 0 || iteratorElement is not { } element)
                    {
                        if (lambdaDepth > 0)
                            Error("AN2111", "`yield` goes in a function's own body, not in a lambda", y.Span);
                        if (y.Value is { } ignored)
                            Expression(ignored, null);
                        return new H.BlockStmt([]);
                    }
                    if (catchDepth > 0)
                        Error("AN2113", "`yield` cannot go inside a `catch`", y.Span);
                    return new H.YieldStmt(y.Value is { } value ? Convert(Expression(value, element), element, value.Span) : null);
                }
                case ReturnStmt r when iteratorElement is not null && lambdaDepth == 0:
                    if (r.Value is not null)
                        Error("AN2112", "a function with `yield` gives its values with `yield` (it ends with `yield break;` or `return;`)", r.Value.Span);
                    return new H.YieldStmt(null);
                case ReturnStmt r:
                {
                    if (returnType is null)
                    {
                        // A lambda whose result is inferred: the first return decides it.
                        if (r.Value is null)
                        {
                            inferredResult ??= H.HirType.Void;
                            return new H.ReturnStmt(null);
                        }
                        var value = Expression(r.Value, inferredResult is { } known && known != H.HirType.Void ? known : null);
                        inferredResult ??= value.Type == NullType ? ErrorType : value.Type;
                        return new H.ReturnStmt(inferredResult == H.HirType.Void ? null : Convert(value, inferredResult, r.Value.Span));
                    }
                    if (r.Value is null)
                    {
                        if (returnType != H.HirType.Void)
                            Error("AN2008", $"this function returns a `{Show(returnType)}`", r.Span);
                        return new H.ReturnStmt(null);
                    }
                    if (returnType == H.HirType.Void)
                    {
                        Error("AN2009", "this function returns nothing", r.Value.Span);
                        return new H.ReturnStmt(null);
                    }
                    return new H.ReturnStmt(Convert(Expression(r.Value, returnType), returnType, r.Value.Span));
                }
                case BreakStmt b:
                    if (breakables.Count == 0)
                        Error("AN2010", "`break` goes inside a loop or a switch", b.Span);
                    return new H.BreakStmt();
                case ContinueStmt c:
                    if (!breakables.Contains(true))
                        Error("AN2011", "`continue` goes inside a loop", c.Span);
                    return new H.ContinueStmt();
                case DeleteStmt d:
                {
                    var value = Expression(d.Value, null);
                    if (!IsError(value.Type) && !binder.IsClass(value.Type))
                        Error("AN2012", "`delete` frees an object", d.Value.Span);
                    return new H.DeleteStmt(value);
                }
                case UnsafeStmt u:
                {
                    if (binder.Options.DeniesUnsafe(source.Path))
                        Error("AN1104", "an `unsafe` block: " + AntrilOptions.UnsafeDenied, new TextSpan(u.Span.Start, Math.Min(6, u.Span.Length)));
                    unsafeDepth++;
                    var block = Block(u.Body);
                    unsafeDepth--;
                    return new H.UnsafeStmt(block);
                }
                case RegionStmt r:
                    return new H.RegionStmt(Block(r.Body));
            }
            Error("AN2013", "this statement comes in a later phase of the new compiler", statement.Span);
            return new H.BlockStmt([]);
        }

        /// <summary>
        /// <c>loop (T x in collection)</c>: over an array by index; over anything else through its <c>Iterator()</c>, whose
        /// <c>Next()</c> moves to the next element (false at the end) and <c>Current</c> is it.
        /// </summary>
        private H.Stmt Foreach(ForeachLoop loop)
        {
            Push(loop.Span);
            var collection = Expression(loop.Collection, null);
            var type = Resolve(loop.Type) ?? ErrorType;
            binder.CheckDeclared(type, OwnGeneric, source, loop.Type.Span);
            H.Stmt result;
            if (IsError(collection.Type))
            {
                Declare(loop.Name, type, loop.Span);
                Loop(loop.Body);
                result = new H.BlockStmt([]);
            }
            else if (collection.Type is H.ArrayType array)
            {
                // Over an N-dimensional array: every element in row order (one index on it counts in that order).
                int items = Declare($"$items{locals.Count}", collection.Type, loop.Span);
                int index = Declare($"$index{locals.Count}", H.HirType.I32, loop.Span);
                int variable = Declare(loop.Name, type, loop.Span);
                var body = Loop(loop.Body);
                var itemsExpr = new H.LocalExpr(items, collection.Type);
                var indexExpr = new H.LocalExpr(index, H.HirType.I32);
                var element = Convert(new H.ElementExpr(itemsExpr, [indexExpr], array.Element), type, loop.Span);
                result = new H.ForStmt(
                    [new H.LocalStmt(items, collection), new H.LocalStmt(index, new H.LiteralExpr(new H.IntegerConstant(0, H.Primitive.I32), H.HirType.I32))],
                    new H.BinaryExpr(H.BinaryOp.Less, indexExpr, new H.ArrayLengthExpr(itemsExpr, H.HirType.I32), H.HirType.Bool),
                    [new H.UnaryExpr(H.UnaryOp.PostIncrement, indexExpr, H.HirType.I32)],
                    new H.BlockStmt([new H.LocalStmt(variable, element), body]));
            }
            else
            {
                var iterator = MemberCall(collection, "Iterator", [], loop.Collection.Span, loop.Collection.Span);
                if (IsError(iterator.Type))
                {
                    Declare(loop.Name, type, loop.Span);
                    Loop(loop.Body);
                    Pop();
                    return new H.BlockStmt([]);
                }
                int it = Declare($"$iterator{locals.Count}", iterator.Type, loop.Span);
                var itExpr = new H.LocalExpr(it, iterator.Type);
                var next = MemberCall(itExpr, "Next", [], loop.Collection.Span, loop.Collection.Span);
                var current = PropertyRead(itExpr, "Current", loop.Collection.Span);
                int variable = Declare(loop.Name, type, loop.Span);
                var body = Loop(loop.Body);
                result = new H.BlockStmt([
                    new H.LocalStmt(it, iterator),
                    new H.WhileStmt(Convert(next, H.HirType.Bool, loop.Collection.Span),
                        new H.BlockStmt([new H.LocalStmt(variable, Convert(current, type, loop.Span)), body])),
                ]);
            }
            Pop();
            return result;
        }

        private H.HirType Counter(TypeSyntax syntax, TextSpan span)
        {
            var type = Resolve(syntax) ?? ErrorType;
            if (!IsError(type) && !(type is H.PrimitiveType { Kind: var k } && k.IsInteger()))
            {
                Error("AN2014", $"a loop counter is an integer, not a `{Show(type)}`", syntax.Span);
                return ErrorType;
            }
            return type;
        }

        private H.Stmt Loop(Stmt body)
        {
            breakables.Add(true);
            var bound = Nested(body);
            breakables.RemoveAt(breakables.Count - 1);
            return bound;
        }

        /// <summary>The local constants (<c>const T x = v;</c>) declared: their values (a value made of constants: none).</summary>
        private readonly Dictionary<int, (H.HirConstant? Value, H.HirType Type)> localConstants = [];

        /// <summary>The values of the local constants in scope, by name, for the constants that name them.</summary>
        private (H.HirConstant Value, H.HirType Type)? LocalConstant(string name) =>
            LookupLocal(name) is { } index && localConstants.TryGetValue(index, out var found) && found.Value is { } value ? (value, found.Type) : null;

        private H.Stmt LocalDeclaration(LocalDeclStmt local)
        {
            var type = Resolve(local.Type) ?? ErrorType;
            binder.CheckDeclared(type, OwnGeneric, source, local.Type.Span);
            if (type == H.HirType.Void)
            {
                Error("AN2015", "a variable cannot be void", local.Type.Span);
                type = ErrorType;
            }
            if (local.IsConst && !IsError(type))
                return LocalConstantDeclaration(local, type);
            H.Expr? value = local.Initializer is { } initializer ? Convert(Expression(initializer, type), type, initializer.Span) : null;
            int index = Declare(local.Name, type, local.NameSpan);
            if (value is null && !IsError(type) && !Zeroable(type))
                Error("AN2016", $"`{local.Name}` needs a value: a `{Show(type)}` has no default", local.NameSpan);
            return new H.LocalStmt(index, value ?? new H.DefaultExpr(type));
        }

        /// <summary>
        /// <c>const T x = v;</c> in a body: a number, bool, character, string or enum value worked out when compiling (other
        /// constants may name it), or a value (struct) made of constants; never assigned.
        /// </summary>
        private H.Stmt LocalConstantDeclaration(LocalDeclStmt local, H.HirType type)
        {
            if (local.Initializer is not { } initializer)
            {
                Error("AN1087", "a constant has its value where it is declared: `const i32 limit = 10;`", local.NameSpan);
                return new H.BlockStmt([]);
            }
            var constantScope = scope with { LocalConstants = LocalConstant };
            H.HirConstant? constant = null;
            if (binder.IsConstantType(type))
            {
                constant = binder.Constant(initializer, type, constantScope, bag, owner);
                if (constant is null)
                    Error("AN1091", $"the value of `{local.Name}` is not known when compiling: a constant is made of literals, other constants, enum values and operators, casts and `?:` on them", initializer.Span);
            }
            else if (binder.SymbolOf(type) is not { IsStruct: true })
                Error("AN1088", $"a constant is a number, bool, character, string, enum value or value (struct), not `{Show(type)}`", local.Type.Span);
            else if (!binder.MadeOfConstants(initializer, constantScope, owner))
                Error("AN1089", $"a constant value is made of constants: `const {Show(type)} {local.Name} = new {Show(type)}(1, 2);`", initializer.Span);
            H.Expr value = constant is not null ? new H.LiteralExpr(constant, type) : Convert(Expression(initializer, type), type, initializer.Span);
            int index = Declare(local.Name, type, local.NameSpan);
            localConstants[index] = (constant, type);
            return new H.LocalStmt(index, value);
        }

        /// <summary>Whether a type has a zero value (numbers, bool, null for nullable types, structs of those).</summary>
        private bool Zeroable(H.HirType type) => type switch
        {
            H.PrimitiveType => true,
            H.NullableType => true,
            H.PointerType => true,
            H.NativeFuncType => true,
            H.InlineArrayType inline => Zeroable(inline.Element),
            H.GenericParamType => true,
            H.NamedType when binder.SymbolOf(type) is { IsEnum: true } => true,
            H.NamedType when binder.SymbolOf(type) is { IsStruct: true } s => s.Fields.Values.Where(f => !f.IsStatic).All(f => Zeroable(f.Type)),
            _ => false,
        };

        private H.Stmt Try(TryStmt t)
        {
            var body = Block(t.Body);
            var catches = new List<H.CatchClause>();
            catchDepth++;
            foreach (var clause in t.Catches)
            {
                Push(clause.Span);
                H.HirType? type = null;
                int local = -1;
                if (clause.Type is { } typeSyntax)
                {
                    type = Resolve(typeSyntax) ?? ErrorType;
                    if (!IsError(type) && !binder.IsClass(type))
                    {
                        Error("AN2017", $"a catch takes an object type, not `{Show(type)}`", typeSyntax.Span);
                        type = ErrorType;
                    }
                    if (clause.Name is { } name)
                        local = Declare(name, type, clause.Span);
                }
                var handler = Block(clause.Body);
                Pop();
                catches.Add(new H.CatchClause(IsError(type ?? H.HirType.Void) ? null : type, local, null, handler));
            }
            catchDepth--;
            var @finally = t.Finally is { } f ? Block(f) : null;
            return new H.TryStmt(body, [.. catches], @finally);
        }

        /// <summary>Whether control never goes past <paramref name="statement"/> (it returns or throws on every path).</summary>
        private static bool Terminates(H.Stmt statement) => statement switch
        {
            H.ReturnStmt or H.ThrowStmt => true,
            H.BlockStmt block => block.Statements.Any(Terminates),
            H.IfStmt { Else: { } otherwise } s => Terminates(s.Then) && Terminates(otherwise),
            H.WhileStmt { Condition: H.LiteralExpr { Value: H.BoolConstant { Value: true } } } loop => !Breaks(loop.Body),
            H.ForStmt { Condition: null } loop => !Breaks(loop.Body),
            H.TryStmt t => t.Finally is { } f && Terminates(f) || Terminates(t.Body) && t.Catches.All(c => Terminates(c.Body)),
            H.SwitchStmt s => s.Sections.Any(x => x.Patterns.Any(p => p is H.DiscardPattern) && x.Guard is null)
                && s.Sections.All(x => Terminates(x.Body)),
            H.UnsafeStmt u => Terminates(u.Body),
            H.RegionStmt r => Terminates(r.Body),
            _ => false,
        };

        /// <summary>Whether a loop body breaks out of its loop (a break not inside a nested loop or switch).</summary>
        private static bool Breaks(H.Stmt statement) => statement switch
        {
            H.BreakStmt => true,
            H.BlockStmt block => block.Statements.Any(Breaks),
            H.IfStmt s => Breaks(s.Then) || s.Else is { } e && Breaks(e),
            H.TryStmt t => Breaks(t.Body) || t.Catches.Any(c => Breaks(c.Body)) || t.Finally is { } f && Breaks(f),
            H.UnsafeStmt u => Breaks(u.Body),
            H.RegionStmt r => Breaks(r.Body),
            _ => false,
        };

        // ── Expressions ────────────────────────────────────

        private H.Expr Condition(Expr expression)
        {
            var bound = Expression(expression, H.HirType.Bool);
            return Convert(bound, H.HirType.Bool, expression.Span);
        }

        /// <summary>An expression; <paramref name="expected"/> types literals (and <c>null</c>, array literals) where it matters.</summary>
        private H.Expr Expression(Expr expression, H.HirType? expected)
        {
            var bound = ExpressionCore(expression, expected);
            if (recorder is not null && !IsError(bound.Type))
                Record(expression, bound);
            return bound;
        }

        /// <summary>What an expression is, for editor services: its type, and the local, parameter or member a name in it means.</summary>
        private void Record(Expr expression, H.Expr bound)
        {
            recorder!.Add(new RecordedSpan(source, expression.Span, RecordedKind.Expression) { Type = bound.Type });
            var nameSpan = expression switch
            {
                NameExpr n => n.Span,
                MemberExpr m => m.NameSpan,
                CallExpr { Target: NameExpr n } => n.Span,
                CallExpr { Target: MemberExpr m } => m.NameSpan,
                NewExpr n => n.Type.Span,
                _ => (TextSpan?)null,
            };
            if (nameSpan is not { } span)
                return;
            switch (bound)
            {
                case H.LocalExpr l when localSpans.TryGetValue(l.Local, out var declared):
                    recorder.Add(new RecordedSpan(source, span, RecordedKind.Local) { Type = l.Type, Name = locals[l.Local].Name, Declared = declared });
                    break;
                case H.ParamExpr p:
                    recorder.Add(new RecordedSpan(source, span, RecordedKind.Parameter) { Type = p.Type, Name = method.ParameterNames[p.Parameter], Declared = method.NameSpan });
                    break;
                case H.FieldExpr f:
                    recorder.Add(new RecordedSpan(source, span, RecordedKind.Member) { Member = f.Field, MemberKind = H.MemberKind.Field, Receiver = f.Instance?.Type, Type = f.Type });
                    break;
                case H.PropertyExpr p:
                    recorder.Add(new RecordedSpan(source, span, RecordedKind.Member) { Member = p.Property, MemberKind = H.MemberKind.Property, Receiver = p.Instance?.Type, Type = p.Type });
                    break;
                case H.CallExpr c when expression is not NewExpr:
                    recorder.Add(new RecordedSpan(source, span, RecordedKind.Member) { Member = c.Method, MemberKind = H.MemberKind.Method, Receiver = c.Receiver?.Type, Type = c.Type });
                    break;
                case H.NewExpr { Constructor: { } constructor } n:
                    recorder.Add(new RecordedSpan(source, span, RecordedKind.Member) { Member = constructor, MemberKind = H.MemberKind.Method, Receiver = n.Type, Type = n.Type });
                    break;
            }
        }

        private H.Expr ExpressionCore(Expr expression, H.HirType? expected)
        {
            switch (expression)
            {
                case BoundExpr bound:
                    return bound.Value;
                case IntegerLiteral { Suffix: { } integerSuffix } i:
                {
                    if (Suffixed(integerSuffix, i.Span) is not { } kind)
                        return new H.DefaultExpr(ErrorType);
                    if (!Binder.Fits(i.Value, kind))
                        return Failed("AN2028", $"{i.Value} does not fit in `{integerSuffix}`", i.Span);
                    return new H.LiteralExpr(new H.IntegerConstant(i.Value, kind), H.HirType.Of(kind));
                }
                case IntegerLiteral i:
                    return Integer(i.Value, expected, i.Span);
                case FloatLiteral { Suffix: { } floatSuffix } f:
                {
                    if (Suffixed(floatSuffix, f.Span) is not { } kind)
                        return new H.DefaultExpr(ErrorType);
                    return new H.LiteralExpr(FloatOf(f, kind), H.HirType.Of(kind));
                }
                case FloatLiteral f:
                    return expected is H.PrimitiveType { Kind: var floatKind } && floatKind.IsFloat() && floatKind != H.Primitive.F64
                        ? new H.LiteralExpr(FloatOf(f, floatKind), expected)
                        : new H.LiteralExpr(H.FloatConstant.Of(f.Value), H.HirType.F64);
                case BoolLiteral b:
                    return new H.LiteralExpr(new H.BoolConstant(b.Value), H.HirType.Bool);
                case CharLiteral c:
                {
                    // A character is its number: a code point, as the integer type wanted (u16 alone; u32 beyond the BMP: '😀').
                    bool pair = c.Value.Length == 2 && char.IsSurrogatePair(c.Value[0], c.Value[1]);
                    if (c.Value.Length != 1 && !pair)
                        return Failed("AN2018", "a character literal is one character (one code point)", c.Span);
                    long point = pair ? char.ConvertToUtf32(c.Value[0], c.Value[1]) : c.Value[0];
                    return Integer(point, expected is H.PrimitiveType { Kind: var ck } && ck.IsInteger() ? expected : H.HirType.Of(pair ? H.Primitive.U32 : H.Primitive.U16), c.Span);
                }
                case StringLiteral s:
                    if (binder.StringSymbol is not { } text)
                        return Failed("AN2019", "string literals are `System.String`s of the core library, which this program does not have", s.Span);
                    return new H.LiteralExpr(new H.StringConstant(s.Value), binder.StringType!);
                case ResourceLiteral r:
                    if (!binder.Assembly.Resources.Any(x => x.Name == r.Name))
                        return Failed("AN2139", $"the program has no resource \"{r.Name}\" (a `chunk RESOURCES` entry of its .queen)", r.Span);
                    return new H.ResourceExpr(r.Name, new H.ArrayType(H.HirType.U8));
                case NullLiteral n:
                    if (expected is H.NullableType || expected is not null && IsError(expected))
                        return new H.LiteralExpr(new H.NullConstant(), expected);
                    return new H.LiteralExpr(new H.NullConstant(), NullType);
                case CheckedExpr checkedExpr:
                {
                    var value = Expression(checkedExpr.Value, expected);
                    return new H.CheckedExpr(value, checkedExpr.Checked, value.Type);
                }
                case AwaitExpr awaitExpr:
                {
                    var task = Expression(awaitExpr.Task, null);
                    if (IsError(task.Type))
                        return task;
                    if (asyncResult is null || lambdaDepth > 0)
                        return Failed("AN2115", lambdaDepth > 0 ? "`await` goes in an async function's own body, not in a lambda" : "`await` goes in an async function", awaitExpr.Span);
                    if (catchDepth > 0)
                        return Failed("AN2116", "`await` cannot go inside a `catch`", awaitExpr.Span);
                    // What it waits for: a Task (nothing) or a Task<T> (a T), found along the type's bases.
                    for (var type = task.Type; type is H.NamedType named;)
                    {
                        if (binder.TaskSymbol is { } plain && named.Type == plain.Token)
                            return new H.AwaitExpr(task, H.HirType.Void);
                        if (binder.TaskOfSymbol is { } taskOf && named.Type == taskOf.Token)
                            return new H.AwaitExpr(task, named.Arguments[0]);
                        if (binder.SymbolOf(named) is not { BaseType: { } baseType })
                            break;
                        type = baseType.Substitute(named.Arguments);
                    }
                    return Failed("AN2117", $"`await` waits for a `Task` or a `Task<T>`, not a `{Show(task.Type)}`", awaitExpr.Task.Span);
                }
                case LambdaExpr lambda:
                    return Lambda(lambda, expected as H.FuncType, null);
                case NameExpr name when expected is H.NativeFuncType native && IsMethodGroup(name):
                    return NativeFunction(name, native, name.Span);
                case MemberExpr member when expected is H.NativeFuncType native && IsMethodGroup(member):
                    return NativeFunction(member, native, member.Span);
                case NameExpr name when expected is H.FuncType wanted && IsMethodGroup(name):
                    return MethodGroup(name, wanted);
                case MemberExpr member when expected is H.FuncType wanted && IsMethodGroup(member):
                    return MethodGroup(member, wanted);
                case NameExpr name:
                    return Name(name);
                case MemberExpr member:
                    return Member(member);
                case CallExpr call:
                    expectedResult = expected;
                    return Call(call);
                case IndexExpr index:
                    return Index(index);
                case UnaryExpr unary:
                    return Unary(unary, expected);
                case BinaryExpr binary:
                    return Binary(binary, expected);
                case OperatorExpr custom:
                    return CustomOperator(custom);
                case AssignExpr assign:
                    return Assign(assign);
                case ConditionalExpr conditional:
                {
                    var condition = Condition(conditional.Condition);
                    var then = Expression(conditional.Then, expected);
                    var otherwise = Expression(conditional.Else, expected ?? then.Type);
                    var (a, b, type) = Unify(then, otherwise, expected, conditional.Span);
                    return new H.ConditionalExpr(condition, a, b, type);
                }
                case CastExpr cast:
                {
                    var type = Resolve(cast.Type) ?? ErrorType;
                    binder.CheckDeclared(type, OwnGeneric, source, cast.Type.Span);
                    var value = Expression(cast.Operand, type);
                    return Convert(value, type, cast.Span, explicitly: true);
                }
                case IsExpr test:
                {
                    var value = Expression(test.Value, null);
                    var pattern = Pattern(test.Pattern, value.Type);
                    return new H.IsExpr(value, pattern, H.HirType.Bool);
                }
                case WithExpr w:
                    return With(w, expected);
                case AsExpr test:
                {
                    var value = Expression(test.Value, null);
                    var type = Resolve(test.Type) ?? ErrorType;
                    var inner = type is H.NullableType n ? n.Inner : type;
                    if (IsError(type) || IsError(value.Type))
                        return new H.DefaultExpr(ErrorType);
                    if (!IsReference(inner) || !IsReference(value.Type is H.NullableType vn ? vn.Inner : value.Type))
                        return Failed("AN2020", "`as` converts between object types", test.Span);
                    return new H.AsExpr(value, new H.NullableType(inner));
                }
                case SwitchExpr s:
                    return Switch(s, expected);
                case NewExpr n:
                    return New(n);
                case NewArrayExpr n:
                {
                    var element = Resolve(n.Element) ?? ErrorType;
                    binder.CheckDeclared(element, OwnGeneric, source, n.Element.Span);
                    if (n.Lengths.Length > 8)
                        return Failed("AN2021", "an array has at most 8 dimensions", n.Span);
                    if (!IsError(element) && !Zeroable(element))
                        return Failed("AN2022", $"a new array starts zeroed: its elements need a zero value (`{Show(element)}` has none; use `{Show(element)}?`)", n.Element.Span);
                    var lengths = new H.Expr[n.Lengths.Length];
                    for (int d = 0; d < lengths.Length; d++)
                    {
                        lengths[d] = Expression(n.Lengths[d], H.HirType.I32);
                        if (!IsError(lengths[d].Type) && !IsInteger(lengths[d].Type))
                            Error("AN2023", "an array length is an integer", n.Lengths[d].Span);
                        else if (!IsError(lengths[d].Type))
                            lengths[d] = Convert(lengths[d], H.HirType.I32, n.Lengths[d].Span);
                    }
                    return new H.NewArrayExpr(element, lengths, new H.ArrayType(element, lengths.Length));
                }
                case ArrayLiteralExpr a when expected is H.ArrayType { Rank: > 1 } grid:
                    return GridLiteral(a, grid);
                case ArrayLiteralExpr a:
                {
                    var element = expected is H.ArrayType arrayType ? arrayType.Element : null;
                    var items = new List<H.Expr>();
                    foreach (var item in a.Elements)
                    {
                        var bound = Expression(item, element);
                        element ??= bound.Type == NullType ? null : bound.Type;
                        items.Add(bound);
                    }
                    if (element is null)
                        return Failed("AN2024", "the type of an empty array literal comes from where it goes", a.Span);
                    return new H.ArrayLiteralExpr(element, [.. items.Select((x, i) => Convert(x, element, a.Elements[i].Span))], new H.ArrayType(element));
                }
                case DefaultExpr d:
                {
                    var type = Resolve(d.Type) ?? ErrorType;
                    binder.CheckDeclared(type, OwnGeneric, source, d.Type.Span);
                    return new H.DefaultExpr(type);
                }
                case SizeOfExpr s:
                {
                    var type = Resolve(s.Type) ?? ErrorType;
                    return new H.SizeOfExpr(type, H.HirType.Of(H.Primitive.U64));
                }
                case TypeOfExpr t:
                {
                    if (binder.TypeSymbol is not { } typeClass)
                        return Failed("AN2025", "`typeof` needs the core library's System.Type", t.Span);
                    var described = Resolve(t.Type) ?? ErrorType;
                    return new H.TypeOfExpr(described, typeClass.Type);
                }
                case TupleExpr tuple:
                    return Tuple(tuple, expected);
                case NameOfTypeExpr n:
                    return NameOfType(n.Type, n.Span);
                case RangeExpr r:
                    return Failed("AN2026", "a range goes in a loop or a pattern", r.Span);
            }
            return Failed("AN2027", "this expression comes in a later phase of the new compiler", expression.Span);
        }

        /// <summary>The type of a bare <c>null</c> (no expected type): it converts to any nullable type.</summary>
        private static readonly H.HirType NullType = new H.NullableType(new H.NamedType(new H.TypeToken(int.MaxValue - 1), []));

        /// <summary>The primitive a literal's suffix names.</summary>
        private static H.Primitive? Suffixed(string suffix, TextSpan span)
        {
            _ = span;
            return Binder.PrimitiveNamed(suffix);
        }

        /// <summary>A float literal as a constant of a float type: f32 and f64 through the double read, the others from the text exactly.</summary>
        internal static H.FloatConstant FloatOf(FloatLiteral literal, H.Primitive kind) => kind switch
        {
            H.Primitive.F64 => H.FloatConstant.Of(literal.Value),
            H.Primitive.F32 => H.FloatConstant.Of((float)literal.Value),
            _ => new H.FloatConstant(SoftFloat.FromText(LiteralDigits(literal), FloatFormat.Of(kind.Bits())), kind),
        };

        /// <summary>A float literal's digits: its text without the type suffix (SoftFloat drops the separators).</summary>
        private static string LiteralDigits(FloatLiteral literal)
        {
            if (literal.Text is not { } text)
                return literal.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if (literal.Suffix is { } suffix && text.EndsWith(suffix, StringComparison.Ordinal))
                text = text[..^suffix.Length];
            return text;
        }

        /// <summary>An integer as a constant of a float type (correctly rounded).</summary>
        private static H.FloatConstant FloatOf(BigInteger value, H.Primitive kind) => kind switch
        {
            H.Primitive.F64 => H.FloatConstant.Of((double)value),
            H.Primitive.F32 => H.FloatConstant.Of((float)value),
            _ => new H.FloatConstant(SoftFloat.FromInteger(value, FloatFormat.Of(kind.Bits())), kind),
        };

        private H.Expr Integer(BigInteger value, H.HirType? expected, TextSpan span)
        {
            if (expected is not null && ParamLiteral(value, expected) is { } unit)
                return unit;
            if (expected is H.PrimitiveType { Kind: var kind } && kind.IsInteger() && Binder.Fits(value, kind))
                return new H.LiteralExpr(new H.IntegerConstant(value, kind), expected);
            if (expected is H.PrimitiveType { Kind: var floatKind } && floatKind.IsFloat())
                return new H.LiteralExpr(FloatOf(value, floatKind), expected);
            foreach (var fallback in new[] { H.Primitive.I32, H.Primitive.I64, H.Primitive.U64, H.Primitive.I128 })
            {
                if (Binder.Fits(value, fallback))
                    return new H.LiteralExpr(new H.IntegerConstant(value, fallback), H.HirType.Of(fallback));
            }
            return Failed("AN2028", $"{value} does not fit in any integer type", span);
        }

        // ── Names and members ──────────────────────────────

        private H.Expr Name(NameExpr name)
        {
            if (LookupLocal(name.Name) is { } local)
                // A `&mut T` local (`&mut Counter r = &mut x;`) reads and writes what it refers to, as such a parameter does.
                return locals[local].Type is H.ReferenceType held
                    ? new H.DerefExpr(new H.LocalExpr(local, held), held.Target)
                    : new H.LocalExpr(local, locals[local].Type);
            if (parameters.TryGetValue(name.Name, out int parameter))
                // A `&mut T` parameter (a Deconstruct's) reads and writes what it refers to.
                return method.Parameters[parameter] is H.ReferenceType reference
                    ? new H.DerefExpr(new H.ParamExpr(parameter, reference), reference.Target)
                    : new H.ParamExpr(parameter, method.Parameters[parameter]);
            if (name.Name == "this")
                return IsStatic ? Failed("AN2029", "`this` is not available in a static function", name.Span) : new H.ThisExpr(owner.Type);
            if (name.Name == "base")
                return Failed("AN2030", "`base` calls a method of the base class: `base.Name(…)`", name.Span);
            if (owner.FindField(name.Name) is { } field)
                return FieldAccess(field.IsStatic ? null : new H.ThisExpr(owner.Type), field, name.Span, binder.AncestorArguments(owner.Type, field.Owner));
            if (owner.FindProperty(name.Name) is { } property)
            {
                if (!property.IsStatic && IsStatic)
                    return Failed("AN2034", $"`{property.Name}` belongs to an object: it is not available in a static function", name.Span);
                if (!Allowed(property, false, name.Span))
                    return new H.DefaultExpr(ErrorType);
                return new H.PropertyExpr(property.IsStatic ? null : new H.ThisExpr(owner.Type), property.Token, [],
                    property.Type.Substitute(binder.AncestorArguments(owner.Type, property.Owner)));
            }
            if (ModuleField(name.Name) is { } global)
                return FieldAccess(null, global, name.Span);
            if (owner.FindMethods(name.Name).Count > 0 || ModuleFunctions(name.Name).Count > 0)
                return Failed("AN2031", $"`{name.Name}` is a function: call it with `{name.Name}(…)`", name.Span);
            if (binder.FindType(new QualifiedName([name.Name], name.Span), scope) is not null)
                return Failed("AN2032", $"`{name.Name}` is a type, not a value", name.Span);
            return Failed("AN2033", $"unknown name `{name.Name}`", name.Span);
        }

        private H.Expr FieldAccess(H.Expr? instance, FieldSymbol field, TextSpan span, H.HirType[]? arguments = null)
        {
            if (!CrossesDomain(field.Owner, null, $"the field `{field.Owner.Name}.{field.Name}`", span))
                return new H.DefaultExpr(ErrorType);
            if (!Allowed(field, span))
                return new H.DefaultExpr(ErrorType);
            if (!field.IsStatic && instance is H.ThisExpr && IsStatic)
                return Failed("AN2034", $"`{field.Name}` belongs to an object: it is not available in a static function", span);
            if (field.IsConst && binder.ConstValue(field) is { } constant)
            {
                // The value takes the name's place: the editor still sees the constant there (hover, definition, color).
                recorder?.Add(new RecordedSpan(source, span, RecordedKind.Member) { Member = field.Token, MemberKind = H.MemberKind.Field, Type = field.Type });
                return new H.LiteralExpr(constant, field.Type);
            }
            return new H.FieldExpr(field.IsStatic ? null : instance, field.Token, field.Type.Substitute(arguments ?? []),
                field.IsStatic && arguments is { Length: > 0 } ? arguments : null);
        }

        /// <summary>
        /// Top-level fields (and constants) of the namespace and its parents, then the one the file takes by name
        /// (<c>import Max from D as Top;</c>), then those of the namespaces imported whole.
        /// </summary>
        private FieldSymbol? ModuleField(string name)
        {
            for (int m = 0; m < modules.Length; m++)
            {
                if (m == enclosingModules && PickedModule(name) is var (picked, original) && picked.Fields.TryGetValue(original, out var taken))
                    return taken;
                if (modules[m].Fields.TryGetValue(name, out var field))
                    return field;
            }
            return modules.Length == enclosingModules && PickedModule(name) is var (last, lastName) && last.Fields.TryGetValue(lastName, out var lastField) ? lastField : null;
        }

        private static readonly List<MethodSymbol> NoFunctions = [];

        /// <summary>Top-level functions by name, in the order fields are found (<see cref="ModuleField"/>).</summary>
        private List<MethodSymbol> ModuleFunctions(string name)
        {
            for (int m = 0; m < modules.Length; m++)
            {
                if (m == enclosingModules && PickedModule(name) is var (picked, original) && picked.Methods.TryGetValue(original, out var taken))
                    return taken;
                if (modules[m].Methods.TryGetValue(name, out var functions))
                    return functions;
            }
            return modules.Length == enclosingModules && PickedModule(name) is var (last, lastName) && last.Methods.TryGetValue(lastName, out var lastFunctions) ? lastFunctions : NoFunctions;
        }

        /// <summary>The module of the namespace a name is taken from by the file's imports, and the name it has there.</summary>
        private (TypeSymbol Module, string Name)? PickedModule(string name) =>
            scope.Picked.TryGetValue(name, out var picked) && binder.Modules.TryGetValue(picked.Namespace, out var module) ? (module, picked.Name) : null;

        /// <summary>Whether a name in an expression means a value (a local, a parameter, a field) rather than a type.</summary>
        private bool IsValueName(string name) =>
            LookupLocal(name) is not null || parameters.ContainsKey(name) || name is "this" or "base"
            || owner.FindField(name) is not null || owner.FindProperty(name) is not null || ModuleField(name) is not null;

        private H.Expr Member(MemberExpr member)
        {
            if (member.NullConditional)
                return ConditionalAccess(member.Target, receiver => MemberOf(receiver, member), member.Span);
            if (member.Target is NameExpr { Name: "base", TypeArguments.Length: 0 })
                return BaseProperty(member);
            if (member.Target is NameExpr { TypeArguments.Length: 0 } paramName && !IsValueName(paramName.Name) && GenericParamNamed(paramName.Name) is { } param)
                return RequiredStatic(param, member.Name, null, member.Span, member.NameSpan);
            if (StaticTarget(member.Target) is { } type)
            {
                // `Box<i32>.count`, `Vector3<i32>.One`: the type's arguments go with the member.
                if (StaticArguments(member.Target, type) is not { } staticArguments)
                    return new H.DefaultExpr(ErrorType);
                if (type.Fields.TryGetValue(member.Name, out var field) || type.FindField(member.Name) is { } inherited && (field = inherited) is not null)
                {
                    if (!field.IsStatic)
                        return Failed("AN2036", $"`{member.Name}` belongs to an object of `{type.Name}`, not to the type", member.Span);
                    return FieldAccess(null, field, member.Span, staticArguments);
                }
                if (type.FindProperty(member.Name) is { } property)
                {
                    if (!property.IsStatic)
                        return Failed("AN2036", $"`{member.Name}` belongs to an object of `{type.Name}`, not to the type", member.Span);
                    if (!Allowed(property, false, member.Span))
                        return new H.DefaultExpr(ErrorType);
                    if (type.IsInterface && property.Getter is { } getter)
                    {
                        // `INumber<i32>.Zero`: the getter resolved for the type argument that implements the interface.
                        if (StaticArguments(member.Target, type) is not { } ifaceArguments)
                            return new H.DefaultExpr(ErrorType);
                        if (ConcreteInterface(type, ifaceArguments, getter.IsAbstract, member.Name, member.Span) is { } wrong)
                            return wrong;
                        return new H.CallExpr(null, getter.Token, ifaceArguments, [], property.Type.Substitute(ifaceArguments));
                    }
                    if (staticArguments.Length > 0 && property.Getter is { } typedGetter)
                        return new H.CallExpr(null, typedGetter.Token, staticArguments, [], property.Type.Substitute(staticArguments));
                    if (!CrossesDomain(type, null, $"the property `{type.Name}.{member.Name}`", member.NameSpan))
                        return new H.DefaultExpr(ErrorType);
                    return new H.PropertyExpr(null, property.Token, [], property.Type);
                }
                return Failed("AN2037", $"`{type.Name}` has no field `{member.Name}`", member.NameSpan);
            }
            // `Geo.Scale`, `G.Max` (an alias of the namespace): a top-level field or constant of a namespace.
            if (NamespaceOf(member.Target) is { } ns && binder.Modules.TryGetValue(ns, out var module) && module.Fields.TryGetValue(member.Name, out var moduleField))
                return FieldAccess(null, moduleField, member.Span);
            return MemberOf(Expression(member.Target, null), member);
        }

        /// <summary>
        /// <c>a?.b</c>, <c>a?.M()</c>: the access when <c>a</c> is not null, else null (nothing, for a call that gives
        /// nothing). The value is nullable: a <c>T</c> becomes a <c>T?</c>.
        /// </summary>
        private H.Expr ConditionalAccess(Expr targetSyntax, Func<H.Expr, H.Expr> access, TextSpan span)
        {
            var target = Expression(targetSyntax, null);
            if (IsError(target.Type))
                return target;
            if (target.Type is not H.NullableType { Inner: var inner })
                return access(target); // never null: a plain access
            var bound = access(new H.ReceiverExpr(inner));
            if (IsError(bound.Type))
                return bound;
            if (bound.Type == H.HirType.Void)
                return new H.ConditionalAccessExpr(target, bound, H.HirType.Void);
            var type = bound.Type is H.NullableType ? bound.Type : new H.NullableType(bound.Type);
            return new H.ConditionalAccessExpr(target, Convert(bound, type, span), type);
        }

        /// <summary>Member <c>.Name</c> of a value (not of a type).</summary>
        private H.Expr MemberOf(H.Expr target, MemberExpr member)
        {
            if (IsError(target.Type))
                return target;
            if (target.Type is H.NullableType { Inner: var valueType } && !IsReference(valueType))
            {
                // A nullable value: whether it has one, and the value (which throws when it has none).
                if (member.Name == "HasValue")
                    return new H.BinaryExpr(H.BinaryOp.NotEqual, target, new H.LiteralExpr(new H.NullConstant(), target.Type), H.HirType.Bool);
                if (member.Name == "Value")
                    return new H.ConvertExpr(target, H.ConversionKind.Unwrap, valueType);
                return Failed("AN2040", $"`{Show(target.Type)}` may be null: check it first (`is`, `??`, `.HasValue`)", member.Target.Span);
            }
            if (target.Type is H.NullableType)
                return Failed("AN2040", $"`{Show(target.Type)}` may be null: check it first (`is`, `??`)", member.Target.Span);
            if (target.Type is H.ArrayType && member.Name == "Length")
                return new H.ArrayLengthExpr(target, H.HirType.I32);
            if (target.Type is H.InlineArrayType fixedArray && member.Name == "Length")
            {
                if (fixedArray.SizeOf is null)
                    return new H.LiteralExpr(new H.IntegerConstant(fixedArray.Count, H.Primitive.I32), H.HirType.I32);
                var u64 = H.HirType.Of(H.Primitive.U64);
                H.Expr length = new H.SizeOfExpr(fixedArray.SizeOf, u64);
                if (fixedArray.Count != 1)
                    length = new H.BinaryExpr(H.BinaryOp.Multiply, new H.LiteralExpr(new H.IntegerConstant(fixedArray.Count, H.Primitive.U64), u64), length, u64);
                return new H.ConvertExpr(length, H.ConversionKind.Numeric, H.HirType.I32);
            }
            if (target.Type is H.ArrayType { Rank: var rank } && member.Name == "Rank")
                return new H.LiteralExpr(new H.IntegerConstant(rank, H.Primitive.I32), H.HirType.I32);
            if (binder.MemberSymbolOf(target.Type) is { } symbol)
            {
                if (symbol.FindField(member.Name) is { } field)
                    return field.IsStatic ? Failed("AN2038", $"`{member.Name}` is static: write `{symbol.Name}.{member.Name}`", member.Span) : FieldAccess(target, field, member.Span, binder.AncestorArguments(target.Type, field.Owner));
                if (symbol.FindProperty(member.Name) is not null)
                    return PropertyRead(target, member.Name, member.NameSpan);
                if (symbol.FindMethods(member.Name).Count > 0)
                    return Failed("AN2031", $"`{member.Name}` is a method: call it with `{member.Name}(…)`", member.NameSpan);
                return Failed("AN2039", $"`{symbol.Name}` has no member `{member.Name}`", member.NameSpan);
            }
            return Failed("AN2039", $"a `{Show(target.Type)}` has no member `{member.Name}`", member.NameSpan);
        }

        /// <summary>
        /// The instance methods named <paramref name="name"/> that have a body in the interfaces of the owner (and of its
        /// bases, and the interfaces those extend), each with its interface in the owner's parameters.
        /// </summary>
        private List<(MethodSymbol Method, H.NamedType Interface)> InterfaceDefaults(string name, bool abstractToo = false)
        {
            var found = new List<(MethodSymbol Method, H.NamedType Interface)>();
            foreach (var (type, arguments) in owner.Chain())
            {
                foreach (var declared in type.Interfaces)
                {
                    foreach (var (symbol, method, ifaceArguments) in binder.InterfaceMethodsOf((H.NamedType)declared.Substitute(arguments)))
                    {
                        // The first found of a signature wins: an interface's own before the defaults of those it extends.
                        H.HirType[] signature = [.. method.Parameters.Select(p => p.Substitute(ifaceArguments))];
                        if (method.Name == name && !method.IsStatic && (abstractToo || !method.IsAbstract)
                            && !found.Any(f => f.Method.Parameters.Select(p => p.Substitute(f.Interface.Arguments)).SequenceEqual(signature)))
                            found.Add((method, (H.NamedType)H.HirType.Named(symbol.Token, ifaceArguments)));
                    }
                }
            }
            return found;
        }

        /// <summary>
        /// <c>base.Name</c>: the getter of the base class's property, or of an interface's default, called as that type's own
        /// (not dispatched).
        /// </summary>
        private H.Expr BaseProperty(MemberExpr member)
        {
            if (IsStatic)
                return Failed("AN2047", "`base` names the base class of the object, or an interface whose default the property replaces", member.Target.Span);
            if (owner.Base is { } baseClass && owner.BaseType is { } baseType && baseClass.FindProperty(member.Name) is { IsStatic: false } own)
            {
                if (own.Getter is not { IsAbstract: false } getter)
                    return Failed("AN2049", $"`{member.Name}` has no body in `{own.Owner.Name}`", member.NameSpan);
                var arguments = binder.AncestorArguments(baseType, own.Owner);
                return new H.CallExpr(new H.ThisExpr(baseType), getter.Token, arguments, [], own.Type.Substitute(arguments)) { NonVirtual = true };
            }
            foreach (var (type, arguments) in owner.Chain())
            {
                foreach (var declared in type.Interfaces)
                {
                    foreach (var (symbol, property, ifaceArguments) in binder.InterfaceProperties((H.NamedType)declared.Substitute(arguments)))
                    {
                        if (property.Name != member.Name || property.IsStatic)
                            continue;
                        if (property.Getter is not { IsAbstract: false } getter)
                            return Failed("AN2049", $"`{member.Name}` has no body in `{symbol.Name}`", member.NameSpan);
                        var iface = (H.NamedType)H.HirType.Named(symbol.Token, ifaceArguments);
                        return new H.CallExpr(Convert(new H.ThisExpr(owner.Type), iface, member.Target.Span), getter.Token, ifaceArguments, [], property.Type.Substitute(ifaceArguments)) { NonVirtual = true };
                    }
                }
            }
            return Failed("AN2048", $"no base class or interface of `{owner.Name}` has a property `{member.Name}`", member.NameSpan);
        }

        /// <summary>Reads property <paramref name="name"/> of <paramref name="target"/>.</summary>
        private H.Expr PropertyRead(H.Expr target, string name, TextSpan span)
        {
            if (IsError(target.Type))
                return target;
            if (binder.MemberSymbolOf(target.Type) is not { } symbol || symbol.FindProperty(name) is not { } property)
                return Failed("AN2039", $"a `{Show(target.Type)}` has no property `{name}`", span);
            if (property.IsStatic)
                return Failed("AN2038", $"`{name}` is static: write `{symbol.Name}.{name}`", span);
            if (!Allowed(property, false, span))
                return new H.DefaultExpr(ErrorType);
            if (!CrossesDomain(property.Owner, null, $"the property `{property.Owner.Name}.{property.Name}`", span))
                return new H.DefaultExpr(ErrorType);
            return new H.PropertyExpr(target, property.Token, [], property.Type.Substitute(binder.AncestorArguments(target.Type, property.Owner)));
        }

        /// <summary>The type an expression names when it is used as a type (<c>Color.Red</c>, <c>Counter.Total</c>).</summary>
        private TypeSymbol? StaticTarget(Expr target)
        {
            var root = target;
            while (root is MemberExpr m)
                root = m.Target;
            if (root is not NameExpr name || IsValueName(name.Name))
                return null;
            // `i32.F()`: the primitive's provider (Int32), which holds its static members and extensions.
            if (root == target && Binder.PrimitiveNamed(name.Name) is { } primitive && binder.PrimitiveTypes.TryGetValue(primitive, out var provider))
                return provider;
            var type = binder.TypeNamed(target, scope);
            if (type is not null)
                binder.CheckTypeAccess(type, scope, target.Span, bag);
            if (type is not null && recorder is not null)
                recorder.Add(new RecordedSpan(source, target.Span, RecordedKind.Type) { Token = type.Token, Type = type.Type });
            return type;
        }

        private H.Expr Index(IndexExpr index)
        {
            var target = Expression(index.Target, null);
            if (IsError(target.Type))
                return target;
            if (target.Type is H.PointerType pointer)
            {
                // `p[i]` is `*(p + i)`.
                if (unsafeDepth == 0)
                    return Failed("AN2067", "`p[i]` reads through a pointer: only in unsafe code", index.Span);
                if (index.Indices.Length != 1)
                    return Failed("AN2043", "a pointer takes one index", index.Span);
                var offset = Expression(index.Indices[0], H.HirType.I64);
                if (IsError(offset.Type))
                    return offset;
                if (!IsInteger(offset.Type))
                    return Failed("AN2044", "an index is an integer", index.Indices[0].Span);
                var at = new H.BinaryExpr(H.BinaryOp.Add, target, Convert(offset, H.HirType.I64, index.Indices[0].Span, explicitly: true), pointer);
                return new H.DerefExpr(at, pointer.Target);
            }
            if (target.Type is H.InlineArrayType inline)
            {
                if (index.Indices.Length != 1)
                    return Failed("AN2043", "an inline array takes one index", index.Span);
                var at = Expression(index.Indices[0], H.HirType.I32);
                if (!IsError(at.Type) && !IsInteger(at.Type))
                    return Failed("AN2044", "an index is an integer", index.Indices[0].Span);
                return new H.ElementExpr(target, [at], inline.Element);
            }
            if (target.Type is not H.ArrayType array)
            {
                // `x[i]` on an object or value is `x.At(i)` (strings, lists…).
                if (binder.MemberSymbolOf(target.Type) is { } symbol && symbol.FindMethods("At").Count > 0)
                    return MemberCall(target, "At", index.Indices, index.Span, index.Span);
                return Failed("AN2042", $"a `{Show(target.Type)}` cannot be indexed", index.Target.Span);
            }
            if (index.Indices.Length != array.Rank)
                return Failed("AN2043", array.Rank == 1 ? "this array has one dimension" : $"this array has {array.Rank} dimensions: give an index for each", index.Span);
            var indices = new H.Expr[index.Indices.Length];
            for (int d = 0; d < indices.Length; d++)
            {
                var at = Expression(index.Indices[d], H.HirType.I32);
                if (!IsError(at.Type) && !(at.Type is H.PrimitiveType { Kind: var k } && k.IsInteger()))
                    return Failed("AN2044", "an index is an integer", index.Indices[d].Span);
                indices[d] = array.Rank > 1 && !IsError(at.Type) ? Convert(at, H.HirType.I32, index.Indices[d].Span) : at;
            }
            return new H.ElementExpr(target, indices, array.Element);
        }

        /// <summary>
        /// <c>[[1, 2], [3, 4]]</c> going to a <c>T[,]</c>: a new array of those lengths (every row as long as the first),
        /// filled in row order.
        /// </summary>
        private H.Expr GridLiteral(ArrayLiteralExpr literal, H.ArrayType grid)
        {
            var lengths = new int[grid.Rank];
            Array.Fill(lengths, -1);
            var leaves = new List<Expr>();
            bool ok = true;
            void Walk(Expr e, int depth)
            {
                if (depth == grid.Rank)
                {
                    leaves.Add(e);
                    return;
                }
                if (e is not ArrayLiteralExpr row)
                {
                    Error("AN2025", $"this array literal has {grid.Rank} levels of brackets (one per dimension)", e.Span);
                    ok = false;
                    return;
                }
                if (lengths[depth] < 0)
                    lengths[depth] = row.Elements.Length;
                else if (row.Elements.Length != lengths[depth])
                {
                    Error("AN2026", $"every row of a {grid.Rank}-dimensional array has the same length ({lengths[depth]} here)", row.Span);
                    ok = false;
                    return;
                }
                foreach (var item in row.Elements)
                {
                    Walk(item, depth + 1);
                    if (!ok)
                        return;
                }
            }
            Walk(literal, 0);
            for (int d = 0; d < lengths.Length; d++)
                lengths[d] = Math.Max(lengths[d], 0);
            if (!ok)
                return new H.DefaultExpr(ErrorType);
            int made = Declare($"$grid{locals.Count}", grid, literal.Span);
            var madeExpr = new H.LocalExpr(made, grid);
            var statements = new List<H.Stmt>
            {
                new H.LocalStmt(made, new H.NewArrayExpr(grid.Element, [.. lengths.Select(n => (H.Expr)new H.LiteralExpr(new H.IntegerConstant(n, H.Primitive.I32), H.HirType.I32))], grid)),
            };
            for (int i = 0; i < leaves.Count; i++)
            {
                // One index on an N-dimensional array: the element at that place in row order.
                var value = Convert(Expression(leaves[i], grid.Element), grid.Element, leaves[i].Span);
                var element = new H.ElementExpr(madeExpr, [new H.LiteralExpr(new H.IntegerConstant(i, H.Primitive.I32), H.HirType.I32)], grid.Element);
                statements.Add(new H.ExprStmt(new H.AssignExpr(element, value, grid.Element)));
            }
            return new H.BlockExpr([.. statements], madeExpr, grid);
        }

        // ── Calls and objects ──────────────────────────────

        /// <summary>
        /// <c>nameof(x)</c>: the last name written, as a string known when compiling (<c>nameof(list.Count)</c> is
        /// "Count"), once the name is found: a local, a parameter, a field, property or method (of this type, of a type, of
        /// a value), a type, a namespace, a top-level function or field. A function of the program named `nameof` wins.
        /// </summary>
        private H.Expr? NameOf(CallExpr call)
        {
            if (call.Target is not NameExpr { Name: "nameof", TypeArguments.Length: 0 } || owner.FindMethods("nameof").Count > 0 || ModuleFunctions("nameof").Count > 0)
                return null;
            if (binder.StringType is not { } text)
                return Failed("AN2019", "`nameof` gives a `System.String` of the core library, which this program does not have", call.Span);
            if (call.Arguments is not [NameExpr or MemberExpr] || call.Arguments[0] is NameExpr { TypeArguments.Length: > 0 } || call.Arguments[0] is MemberExpr { TypeArguments.Length: > 0 })
                return Failed("AN2141", "`nameof` takes one name: `nameof(count)`, `nameof(Point.x)`, `nameof(System.List)`", call.Span);
            var argument = call.Arguments[0];
            string name = argument is NameExpr n ? n.Name : ((MemberExpr)argument).Name;
            if (!Names(argument))
                return Failed("AN2141", $"`nameof({source[argument.Span]})`: `{source[argument.Span]}` names nothing here", argument.Span);
            // A type parameter: the name of the type it is given (`nameof(T)` is "Int32" where T is i32), not "T".
            // `any` and `str` are types written as words: their names (Any, String), like i32's.
            if (argument is NameExpr { Name: var only } && !IsValueName(only) && owner.FindMethods(only).Count == 0 && ModuleFunctions(only).Count == 0
                && (scope.TypeParameter(only) is not null || only is "any" or "str"))
                return NameOfType(new NamedTypeSyntax(new QualifiedName([only], argument.Span), [], argument.Span), argument.Span);
            return new H.LiteralExpr(new H.StringConstant(name), text);

            bool Names(Expr written)
            {
                switch (written)
                {
                    case NameExpr { Name: var single }:
                        return IsValueName(single) || owner.FindMethods(single).Count > 0 || ModuleFunctions(single).Count > 0
                            || binder.TypeNamed(written, scope) is not null || NamespaceOf(written) is not null || scope.TypeParameter(single) is not null;
                    case MemberExpr member:
                    {
                        if (binder.TypeNamed(written, scope) is not null || NamespaceOf(written) is not null)
                            return true;
                        if (StaticTarget(member.Target) is { } type)
                            return type.FindField(member.Name) is not null || type.FindProperty(member.Name) is not null || type.FindMethods(member.Name).Count > 0;
                        if (NamespaceOf(member.Target) is { } ns)
                            return binder.Modules.TryGetValue(ns, out var module) && (module.Fields.ContainsKey(member.Name) || module.Methods.ContainsKey(member.Name));
                        var receiver = Expression(member.Target, null);
                        if (IsError(receiver.Type))
                            return true; // already reported
                        return binder.MemberSymbolOf(receiver.Type) is { } symbol
                            && (symbol.FindField(member.Name) is not null || symbol.FindProperty(member.Name) is not null || symbol.FindMethods(member.Name).Count > 0)
                            || receiver.Type is H.ArrayType && member.Name == "Length";
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// <c>nameof(i32)</c>, <c>nameof(List&lt;T&gt;)</c>, <c>nameof(T)</c>: the type's name as <c>typeof(X).Name</c> gives it ("Int32",
        /// "List", "Int32[]", "Int32?"), known when compiling unless it is a type parameter's: then it is the name of the type
        /// the parameter is given, read at run time.
        /// </summary>
        private H.Expr NameOfType(TypeSyntax syntax, TextSpan span)
        {
            if (binder.StringType is not { } text)
                return Failed("AN2019", "`nameof` gives a `System.String` of the core library, which this program does not have", span);
            var type = Resolve(syntax) ?? ErrorType;
            if (IsError(type))
                return new H.DefaultExpr(ErrorType);
            if (TypeName(type) is { } known)
                return new H.LiteralExpr(new H.StringConstant(known), text);
            return Expression(new MemberExpr(new TypeOfExpr(syntax, span), "Name", [], span), text);
        }

        /// <summary>A type's name as reflection gives it, or null when it depends on a type parameter (or is a function's).</summary>
        private string? TypeName(H.HirType type) => type switch
        {
            H.PrimitiveType p => binder.PrimitiveTypes.GetValueOrDefault(p.Kind)?.Name,
            H.NamedType n => binder.SymbolOf(n)?.Name,
            H.ArrayType a => TypeName(a.Element) is { } element ? $"{element}[{new string(',', a.Rank - 1)}]" : null,
            H.NullableType n => TypeName(n.Inner) is { } inner ? inner + "?" : null,
            H.PointerType p => TypeName(p.Target) is { } target ? target + "*" : null,
            _ => null,
        };

        /// <summary>
        /// <c>(a, b, c)</c>: a <c>System.ValueTuple</c> of the items, each typed by the tuple type it goes into when there is
        /// one (<c>(i64, str) t = (1, "x")</c>), else by itself.
        /// </summary>
        private H.Expr Tuple(TupleExpr tuple, H.HirType? expected)
        {
            int count = tuple.Items.Length;
            // Keyed `N, or by the name alone when it was the first of that name declared.
            var symbol = binder.Types.GetValueOrDefault($"System.ValueTuple`{count}")
                ?? (binder.Types.GetValueOrDefault("System.ValueTuple") is { } first && first.GenericCount == count ? first : null);
            if (symbol is null
                || symbol.Constructors.FirstOrDefault(c => c.Parameters.Length == count) is not { } constructor)
            {
                foreach (var item in tuple.Items)
                    Expression(item, null);
                return Failed("AN2142", $"`(…)` makes a `System.ValueTuple` of {count} items, which this program does not have (the core library has them)", tuple.Span);
            }
            var wanted = expected is H.NamedType { Arguments.Length: var n } named && n == count && binder.SymbolOf(named) == symbol ? named.Arguments : null;
            var items = new H.Expr[count];
            var types = new H.HirType[count];
            bool failed = false;
            for (int i = 0; i < count; i++)
            {
                items[i] = Expression(tuple.Items[i], wanted?[i]);
                types[i] = wanted?[i] ?? items[i].Type;
                if (IsError(items[i].Type))
                    failed = true;
                else if (types[i] == NullType)
                {
                    Error("AN2143", "the type of `null` in a tuple comes from where the tuple goes: `(str?, i32) pair = (null, 1);`", tuple.Items[i].Span);
                    failed = true;
                }
            }
            if (failed)
                return new H.DefaultExpr(ErrorType);
            var type = new H.NamedType(symbol.Token, types);
            return new H.NewExpr(constructor.Token, [.. items.Select((x, i) => Convert(x, types[i], tuple.Items[i].Span))], [], type);
        }

        private H.Expr Call(CallExpr call)
        {
            if (NameOf(call) is { } nameOf)
                return nameOf;
            // A function value called: a local, a parameter, a field or property holding one, or any expression of a func type.
            if (call.Target is NameExpr { TypeArguments.Length: 0 } local && (LookupLocal(local.Name) is not null || parameters.ContainsKey(local.Name)
                || owner.FindMethods(local.Name).Count == 0 && ModuleFunctions(local.Name).Count == 0 && (owner.FindField(local.Name) is not null || owner.FindProperty(local.Name) is not null || ModuleField(local.Name) is not null))
                || call.Target is not (NameExpr or MemberExpr))
            {
                var function = Expression(call.Target, null);
                return Invoke(function, call.Arguments, call.Span, call.Target.Span);
            }
            switch (call.Target)
            {
                case NameExpr name when LookupLocal(name.Name) is null && !parameters.ContainsKey(name.Name):
                {
                    var candidates = owner.FindMethods(name.Name);
                    var functions = ModuleFunctions(name.Name);
                    if (candidates.Count == 0 && functions.Count == 0)
                        return Failed("AN2045", $"unknown function `{name.Name}`", name.Span);
                    var all = functions.Count == 0 ? candidates : candidates.Count == 0 ? functions : candidates.Concat(functions.Where(f => !candidates.Contains(f))).ToList();
                    if (ResolveCall(all, call.Arguments, call.Span, name.Name, m => binder.AncestorArguments(owner.Type, m.Owner), TypeArguments(name.TypeArguments)) is not { } chosen)
                        return new H.DefaultExpr(ErrorType);
                    if (!CrossesDomain(chosen.Method.Owner, chosen.Method, $"`{name.Name}`", name.Span))
                        return new H.DefaultExpr(ErrorType);
                    if (chosen.Method.IsStatic)
                        return new H.CallExpr(null, chosen.Method.Token, chosen.TypeArguments, chosen.Arguments, chosen.Result);
                    if (IsStatic)
                        return Failed("AN2046", $"`{name.Name}` belongs to an object: it is not available in a static function", name.Span);
                    return new H.CallExpr(new H.ThisExpr(owner.Type), chosen.Method.Token, chosen.TypeArguments, chosen.Arguments, chosen.Result);
                }
                case MemberExpr { Target: NameExpr { Name: "base" } } member:
                {
                    if (IsStatic)
                        return Failed("AN2047", "`base` names the base class of the object, or an interface whose default the method replaces", member.Target.Span);
                    var candidates = owner.Base?.FindMethods(member.Name).Where(m => !m.IsStatic).ToList() ?? [];
                    if (candidates.Count > 0 && owner.BaseType is { } baseType)
                    {
                        if (ResolveCall(candidates, call.Arguments, call.Span, $"`{member.Name}`", m => binder.AncestorArguments(baseType, m.Owner), TypeArguments(member.TypeArguments)) is not { } chosen)
                            return new H.DefaultExpr(ErrorType);
                        if (chosen.Method.IsAbstract)
                            return Failed("AN2049", $"`{member.Name}` has no body in `{chosen.Method.Owner.Name}`", member.NameSpan);
                        return new H.CallExpr(new H.ThisExpr(baseType), chosen.Method.Token, chosen.TypeArguments, chosen.Arguments, chosen.Result) { NonVirtual = true };
                    }
                    // No method of the base class: an interface's default, called as the interface's own (not dispatched,
                    // which would come back to the method that replaces it).
                    var defaults = InterfaceDefaults(member.Name);
                    if (defaults.Count == 0)
                    {
                        if (InterfaceDefaults(member.Name, abstractToo: true).FirstOrDefault() is { Method: { } required })
                            return Failed("AN2049", $"`{member.Name}` has no body in `{required.Owner.Name}`", member.NameSpan);
                        return Failed("AN2048", $"no base class or interface of `{owner.Name}` has a method `{member.Name}`", member.NameSpan);
                    }
                    if (ResolveCall([.. defaults.Select(d => d.Method)], call.Arguments, call.Span, $"`{member.Name}`", m => defaults.First(d => d.Method == m).Interface.Arguments, TypeArguments(member.TypeArguments)) is not { } fallback)
                        return new H.DefaultExpr(ErrorType);
                    var iface = defaults.First(d => d.Method == fallback.Method).Interface;
                    return new H.CallExpr(Convert(new H.ThisExpr(owner.Type), iface, member.Target.Span), fallback.Method.Token, fallback.TypeArguments, fallback.Arguments, fallback.Result) { NonVirtual = true };
                }
                case MemberExpr { NullConditional: true } member:
                    return ConditionalAccess(member.Target, receiver => MemberCall(receiver, member.Name, call.Arguments, call.Span, member.NameSpan, TypeArguments(member.TypeArguments)), call.Span);
                case MemberExpr member:
                {
                    if (member.Target is NameExpr { TypeArguments.Length: 0 } paramName && !IsValueName(paramName.Name) && GenericParamNamed(paramName.Name) is { } param)
                        return RequiredStatic(param, member.Name, call.Arguments, call.Span, member.NameSpan);
                    if (StaticTarget(member.Target) is { } type)
                    {
                        var statics = type.FindMethods(member.Name).Where(m => m.IsStatic).ToList();
                        if (statics.Count == 0 && StaticArguments(member.Target, type) is { } extensionArguments
                            && ExtensionCall(null, type.Type.Substitute(extensionArguments), member.Name, call.Arguments, call.Span, TypeArguments(member.TypeArguments)) is { } extended)
                            return extended;
                        if (statics.Count == 0)
                            return Failed("AN2050", type.FindMethods(member.Name).Count > 0
                                ? $"`{member.Name}` belongs to an object of `{type.Name}`, not to the type"
                                : $"`{type.Name}` has no function `{member.Name}`", member.NameSpan);
                        if (StaticArguments(member.Target, type) is not { } typeArguments)
                            return new H.DefaultExpr(ErrorType);
                        var targetType = type.Type.Substitute(typeArguments);
                        if (ResolveCall(statics, call.Arguments, call.Span, $"`{member.Name}`", m => binder.AncestorArguments(targetType, m.Owner), TypeArguments(member.TypeArguments)) is not { } chosenStatic)
                            return new H.DefaultExpr(ErrorType);
                        if (type.IsInterface && ConcreteInterface(type, typeArguments, chosenStatic.Method.IsAbstract, member.Name, member.Span) is { } wrong)
                            return wrong;
                        if (!CrossesDomain(type, chosenStatic.Method, $"`{type.Name}.{member.Name}`", member.NameSpan))
                            return new H.DefaultExpr(ErrorType);
                        return new H.CallExpr(null, chosenStatic.Method.Token, chosenStatic.TypeArguments, chosenStatic.Arguments, chosenStatic.Result);
                    }
                    if (NamespaceOf(member.Target) is { } ns)
                    {
                        if (!binder.Modules.TryGetValue(ns, out var module) || !module.Methods.TryGetValue(member.Name, out var functions))
                            return Failed("AN2051", $"namespace `{ns}` has no function `{member.Name}`", member.NameSpan);
                        if (ResolveCall(functions, call.Arguments, call.Span, $"`{member.Name}`", _ => [], TypeArguments(member.TypeArguments)) is not { } chosenFunction)
                            return new H.DefaultExpr(ErrorType);
                        if (!CrossesDomain(chosenFunction.Method.Owner, chosenFunction.Method, $"`{ns}.{member.Name}`", member.NameSpan))
                            return new H.DefaultExpr(ErrorType);
                        return new H.CallExpr(null, chosenFunction.Method.Token, chosenFunction.TypeArguments, chosenFunction.Arguments, chosenFunction.Result);
                    }
                    var receiver = Expression(member.Target, null);
                    return MemberCall(receiver, member.Name, call.Arguments, call.Span, member.NameSpan, TypeArguments(member.TypeArguments));
                }
            }
            return Failed("AN2055", "function values come in a later phase of the new compiler", call.Target.Span);
        }

        /// <summary>A call of a function value: its arguments converted to the func type's parameters.</summary>
        private H.Expr Invoke(H.Expr function, Expr[] arguments, TextSpan span, TextSpan targetSpan)
        {
            if (IsError(function.Type))
            {
                foreach (var argument in arguments)
                    Expression(argument, null);
                return function;
            }
            if (function.Type is H.NativeFuncType native)
            {
                if (unsafeDepth == 0)
                    return Failed("AN2120", "calling a `func*` runs code at an address: only in unsafe code", targetSpan);
                if (native.Parameters.Length != arguments.Length)
                    return Failed("AN2056", $"the function takes {native.Parameters.Length} argument(s), not {arguments.Length}", span);
                var passed = new H.Expr[arguments.Length];
                for (int a = 0; a < arguments.Length; a++)
                    passed[a] = Convert(Expression(arguments[a], native.Parameters[a]), native.Parameters[a], arguments[a].Span);
                return new H.InvokeExpr(function, passed, native.Result);
            }
            if (function.Type is not H.FuncType func)
                return Failed("AN2055", $"a `{Show(function.Type)}` is not a function: it cannot be called", targetSpan);
            if (func.Parameters.Length != arguments.Length)
                return Failed("AN2056", $"the function takes {func.Parameters.Length} argument(s), not {arguments.Length}", span);
            var bound = new H.Expr[arguments.Length];
            for (int a = 0; a < arguments.Length; a++)
                bound[a] = Convert(Expression(arguments[a], func.Parameters[a]), func.Parameters[a], arguments[a].Span);
            return new H.InvokeExpr(function, bound, func.Result);
        }

        /// <summary>
        /// A lambda: its parameters typed by <paramref name="expected"/> (or written), its body bound as its own function
        /// (`return` returns from it). <paramref name="expected"/>'s result may be unknown (null in <paramref name="unknownResult"/>
        /// position): the body's type is then the lambda's result.
        /// </summary>
        private H.Expr Lambda(LambdaExpr lambda, H.FuncType? expected, bool? resultUnknown)
        {
            if (expected is not null && expected.Parameters.Length != lambda.Parameters.Length)
                return Failed("AN2099", $"the lambda takes {lambda.Parameters.Length} parameter(s); a `{Show(expected)}` takes {expected.Parameters.Length}", lambda.Span);
            Push(lambda.Span);
            var types = new H.HirType[lambda.Parameters.Length];
            var locals = new int[lambda.Parameters.Length];
            for (int p = 0; p < types.Length; p++)
            {
                var parameter = lambda.Parameters[p];
                H.HirType? type = parameter.Type is { } written ? Resolve(written) : expected?.Parameters[p];
                if (type is null)
                {
                    Error("AN2100", $"the type of `{parameter.Name}` comes from where the lambda goes (write it: `(T {parameter.Name}) => …`)", parameter.Span);
                    type = ErrorType;
                }
                types[p] = type;
                locals[p] = Declare(parameter.Name, type, parameter.Span);
            }
            // The body is a function of its own: its returns, its loops.
            var outerReturn = returnType;
            var outerInferred = inferredResult;
            var outerBreakables = breakables;
            int outerCatch = catchDepth;
            returnType = resultUnknown == true ? null : expected?.Result;
            inferredResult = null;
            breakables = [];
            catchDepth = 0;
            lambdaDepth++;
            H.Stmt body;
            H.HirType result;
            try
            {
                if (lambda.Body is { } expression)
                {
                    var value = Expression(expression, returnType is { } r && r != H.HirType.Void ? r : null);
                    if (returnType is null)
                        result = value.Type == NullType ? ErrorType : value.Type;
                    else
                        result = returnType;
                    body = result == H.HirType.Void ? new H.ExprStmt(value) : new H.ReturnStmt(Convert(value, result, expression.Span));
                }
                else
                {
                    var block = Block(lambda.Block!);
                    result = returnType ?? inferredResult ?? H.HirType.Void;
                    if (result != H.HirType.Void && !IsError(result) && !Terminates(block))
                        Error("AN2001", "the lambda does not return a value on every path", lambda.Span);
                    body = block;
                }
            }
            finally
            {
                lambdaDepth--;
                returnType = outerReturn;
                inferredResult = outerInferred;
                breakables = outerBreakables;
                catchDepth = outerCatch;
                Pop();
            }
            return new H.LambdaExpr(locals, body, new H.FuncType(types, result));
        }

        /// <summary>Whether a name means methods (and nothing that holds a value).</summary>
        private bool IsMethodGroup(Expr target) => target switch
        {
            NameExpr name => !IsValueName(name.Name) && (owner.FindMethods(name.Name).Count > 0 || ModuleFunctions(name.Name).Count > 0),
            MemberExpr member => StaticTarget(member.Target) is { } type ? type.FindMethods(member.Name).Count > 0 && type.FindField(member.Name) is null && type.FindProperty(member.Name) is null
                : NamespaceOf(member.Target) is null && Expression(member.Target, null) is var receiver && binder.MemberSymbolOf(receiver.Type) is { } symbol
                    && symbol.FindMethods(member.Name).Count > 0 && symbol.FindField(member.Name) is null && symbol.FindProperty(member.Name) is null,
            _ => false,
        };

        private int methodGroups;

        /// <summary>
        /// A static function as a <c>func*</c> (its address, which C can call): the one of that name taking
        /// <paramref name="wanted"/>'s parameters (or, without one, the only static function of that name).
        /// </summary>
        private H.Expr NativeFunction(Expr target, H.NativeFuncType? wanted, TextSpan span)
        {
            string name = target is NameExpr n ? n.Name : ((MemberExpr)target).Name;
            IEnumerable<MethodSymbol> candidates = target switch
            {
                NameExpr => owner.FindMethods(name).Concat(ModuleFunctions(name)),
                MemberExpr member when StaticTarget(member.Target) is { } type => type.FindMethods(name),
                MemberExpr member when NamespaceOf(member.Target) is { } ns && binder.Modules.TryGetValue(ns, out var module) && module.Methods.TryGetValue(name, out var functions) => functions,
                _ => [],
            };
            var found = candidates.Where(m => m.IsStatic && m.GenericCount == 0
                && (wanted is null || m.Parameters.AsSpan().SequenceEqual(wanted.Parameters) && Same(m.Result, wanted.Result))).Distinct().ToList();
            if (found.Count != 1)
            {
                return Failed("AN2118", wanted is null
                    ? $"`&{name}` takes the address of one static function: `{name}` names {(found.Count == 0 ? "none" : "several")}"
                    : $"no static function `{name}` takes ({string.Join(", ", wanted.Parameters.Select(Show))}) and gives `{Show(wanted.Result)}`", span);
            }
            var method = found[0];
            if (method.Parameters.Append(method.Result).FirstOrDefault(t => t != H.HirType.Void && !binder.CrossesToC(t, strings: false)) is { } wrong)
                return Failed("AN2119", $"a `func*` points at a function taking and giving numbers, bool, pointers and func*, not `{Show(wrong)}`", span);
            return new H.MethodRefExpr(null, method.Token, [], wanted ?? new H.NativeFuncType(method.Parameters, method.Result));
        }

        /// <summary>A method used as a function value: the lambda that calls it with the function's parameters.</summary>
        private H.Expr MethodGroup(Expr target, H.FuncType wanted) => Lambda(GroupLambda(target, wanted.Parameters.Length), wanted, null);

        /// <summary>The lambda that calls a method group with its <paramref name="arity"/> parameters.</summary>
        private LambdaExpr GroupLambda(Expr target, int arity)
        {
            int group = methodGroups++;
            var parameters = new LambdaParameter[arity];
            var arguments = new Expr[arity];
            for (int p = 0; p < parameters.Length; p++)
            {
                parameters[p] = new LambdaParameter(null, $"$g{group}p{p}", target.Span);
                arguments[p] = new NameExpr(parameters[p].Name, [], target.Span);
            }
            return new LambdaExpr(parameters, new CallExpr(target, arguments, target.Span), null, target.Span);
        }

        /// <summary>The type arguments a call writes (<c>F&lt;i32&gt;(…)</c>), or null when it writes none.</summary>
        private H.HirType[]? TypeArguments(TypeSyntax[] syntax)
        {
            if (syntax.Length == 0)
                return null;
            var types = new H.HirType[syntax.Length];
            for (int i = 0; i < syntax.Length; i++)
                types[i] = Resolve(syntax[i]) ?? ErrorType;
            return types;
        }

        /// <summary>
        /// The type arguments of a type used for its static members: written (<c>List&lt;i32&gt;.Make()</c>), or the
        /// type's own parameters inside it; null (reported) when a generic type is named without them elsewhere.
        /// </summary>
        private H.HirType[]? StaticArguments(Expr target, TypeSymbol type)
        {
            if (type.GenericCount == 0)
                return [];
            var written = target switch
            {
                NameExpr n => n.TypeArguments,
                MemberExpr m => m.TypeArguments,
                _ => [],
            };
            if (written.Length == type.GenericCount)
                return TypeArguments(written);
            if (written.Length == 0 && owner.Chain().Any(c => c.Type == type))
                return binder.AncestorArguments(owner.Type, type);
            // `str.Join(…)` and `String.Join(…)`: text in UTF-8 (String<u8>) unless the arguments are written.
            if (written.Length == 0 && type == binder.StringSymbol && binder.StringType is H.NamedType text)
                return text.Arguments;
            Error("AN2091", $"`{type.Name}` takes {type.GenericCount} type argument(s): `{type.Name}<…>.Member`", target.Span);
            return null;
        }

        /// <summary>
        /// A call of method <paramref name="name"/> on an already bound receiver: a class, value or primitive's method, or
        /// an interface method through the constraint of a type parameter.
        /// </summary>
        private H.Expr MemberCall(H.Expr receiver, string name, Expr[] arguments, TextSpan span, TextSpan nameSpan, H.HirType[]? typeArguments = null)
        {
            if (IsError(receiver.Type))
            {
                foreach (var argument in arguments)
                    Expression(argument, null);
                return receiver;
            }
            if (receiver.Type is H.NullableType)
                return Failed("AN2040", $"`{Show(receiver.Type)}` may be null: check it first (`is`, `??`)", receiver is H.LiteralExpr ? span : nameSpan);
            if (receiver.Type is H.ArrayType && name == "GetLength")
            {
                if (arguments.Length != 1)
                    return Failed("AN2027", "`GetLength` takes the dimension (0 is the first)", span);
                var dimension = Expression(arguments[0], H.HirType.I32);
                if (IsError(dimension.Type))
                    return dimension;
                if (!IsInteger(dimension.Type))
                    return Failed("AN2044", "a dimension is an integer", arguments[0].Span);
                return new H.ArrayLengthExpr(receiver, H.HirType.I32) { Dimension = Convert(dimension, H.HirType.I32, arguments[0].Span) };
            }
            if (receiver.Type is H.GenericParamType param)
            {
                // The methods of the interfaces the parameter is constrained by, each with that interface's arguments.
                var found = new List<MethodSymbol>();
                var interfaceArguments = new Dictionary<MethodSymbol, H.HirType[]>();
                void Visit(H.NamedType iface)
                {
                    if (binder.SymbolOf(iface) is not { IsInterface: true } symbol)
                        return;
                    foreach (var method in symbol.FindMethods(name))
                    {
                        // An extended interface's method with the signature of one already found (the extending interface's
                        // own, which replaces its default) is left out.
                        H.HirType[] signature = [.. method.Parameters.Select(p => p.Substitute(iface.Arguments))];
                        if (!method.IsStatic && !found.Any(f => f.Parameters.Select(p => p.Substitute(interfaceArguments[f])).SequenceEqual(signature))
                            && interfaceArguments.TryAdd(method, iface.Arguments))
                            found.Add(method);
                    }
                    foreach (var extended in symbol.Interfaces)
                        Visit((H.NamedType)extended.Substitute(iface.Arguments));
                }
                foreach (var constraint in ConstraintsOf(param).OfType<H.NamedType>())
                    Visit(constraint);
                if (found.Count == 0)
                    return Failed("AN2092", $"`{Show(receiver.Type)}` has no method `{name}` (constrain it by an interface that has one)", nameSpan);
                if (ResolveCall(found, arguments, span, $"`{name}`", m => interfaceArguments[m], typeArguments) is not { } required)
                    return new H.DefaultExpr(ErrorType);
                return new H.CallExpr(receiver, required.Method.Token, required.TypeArguments, required.Arguments, required.Result);
            }
            if (IsInterface(receiver.Type))
            {
                // The interface's methods and those of the interfaces it extends, each with that interface's arguments;
                // what every object has (`any`'s methods) too.
                var found = new List<MethodSymbol>();
                var interfaceArguments = new Dictionary<MethodSymbol, H.HirType[]>();
                void Visit(H.NamedType iface)
                {
                    if (binder.SymbolOf(iface) is not { IsInterface: true } symbol)
                        return;
                    foreach (var method in symbol.FindMethods(name))
                    {
                        // An extended interface's method with the signature of one already found (the extending interface's
                        // own, which replaces its default) is left out.
                        H.HirType[] signature = [.. method.Parameters.Select(p => p.Substitute(iface.Arguments))];
                        if (!method.IsStatic && !found.Any(f => f.Parameters.Select(p => p.Substitute(interfaceArguments[f])).SequenceEqual(signature))
                            && interfaceArguments.TryAdd(method, iface.Arguments))
                            found.Add(method);
                    }
                    foreach (var extended in symbol.Interfaces)
                        Visit((H.NamedType)extended.Substitute(iface.Arguments));
                }
                Visit((H.NamedType)receiver.Type);
                if (found.Count == 0 && binder.ObjectSymbol is { } root)
                {
                    foreach (var method in root.FindMethods(name))
                    {
                        if (!method.IsStatic && interfaceArguments.TryAdd(method, []))
                            found.Add(method);
                    }
                }
                if (found.Count == 0)
                    return Failed("AN2053", $"`{Show(receiver.Type)}` has no method `{name}`", nameSpan);
                if (ResolveCall(found, arguments, span, $"`{name}`", m => interfaceArguments[m], typeArguments) is not { } chosenMethod)
                    return new H.DefaultExpr(ErrorType);
                return new H.CallExpr(receiver, chosenMethod.Method.Token, chosenMethod.TypeArguments, chosenMethod.Arguments, chosenMethod.Result);
            }
            if (binder.MemberSymbolOf(receiver.Type) is not { } symbol)
                return ExtensionCall(receiver, receiver.Type, name, arguments, span, typeArguments) ?? Failed("AN2052", $"a `{Show(receiver.Type)}` has no methods", nameSpan);
            var candidates = symbol.FindMethods(name);
            if (candidates.Count == 0)
            {
                if (ExtensionCall(receiver, receiver.Type, name, arguments, span, typeArguments) is { } extended)
                    return extended;
                // A field or property holding a function value.
                if (symbol.FindField(name) is { IsStatic: false } field && field.Type is H.FuncType or H.NativeFuncType)
                    return Invoke(FieldAccess(receiver, field, nameSpan, binder.AncestorArguments(receiver.Type, field.Owner)), arguments, span, nameSpan);
                if (symbol.FindProperty(name) is { IsStatic: false } && PropertyRead(receiver, name, nameSpan) is { Type: H.FuncType } property)
                    return Invoke(property, arguments, span, nameSpan);
                if (symbol.IsEnum && name == "ToString" && arguments.Length == 0 && EnumText(receiver, symbol) is { } text)
                    return text;
                return Failed("AN2053", $"`{symbol.Name}` has no method `{name}`", nameSpan);
            }
            if (ResolveCall(candidates, arguments, span, $"`{name}`", m => binder.AncestorArguments(receiver.Type, m.Owner), typeArguments) is not { } chosen)
                return new H.DefaultExpr(ErrorType);
            if (chosen.Method.IsStatic)
                return Failed("AN2054", $"`{name}` is static: write `{chosen.Method.Owner.Name}.{name}(…)`", nameSpan);
            if (!CrossesDomain(chosen.Method.Owner, chosen.Method, $"`{chosen.Method.Owner.Name}.{name}`", nameSpan))
                return new H.DefaultExpr(ErrorType);
            return new H.CallExpr(receiver, chosen.Method.Token, chosen.TypeArguments, chosen.Arguments, chosen.Result);
        }

        /// <summary>A call of a method with arguments already bound (operators that become calls: <c>+</c> on strings…).</summary>
        private H.Expr? BoundCall(H.Expr? receiver, TypeSymbol type, string name, H.Expr[] arguments, H.HirType[]? staticArguments = null)
        {
            foreach (var candidate in type.FindMethods(name))
            {
                if (candidate.IsStatic != (receiver is null) || candidate.GenericCount > 0 || candidate.Parameters.Length != arguments.Length)
                    continue;
                var declaring = receiver is null ? staticArguments ?? [] : binder.AncestorArguments(receiver.Type, candidate.Owner);
                var parameters = candidate.Parameters.Select(p => p.Substitute(declaring)).ToArray();
                if (arguments.Select((a, i) => Cost(a, parameters[i])).Any(c => c is null))
                    continue;
                return new H.CallExpr(receiver, candidate.Token, declaring,
                    [.. arguments.Select((a, i) => Convert(a, parameters[i], default))], candidate.Result.Substitute(declaring));
            }
            return null;
        }

        /// <summary>The namespace an expression names (<c>App.Util</c> in <c>App.Util.F()</c>), if it names one.</summary>
        private string? NamespaceOf(Expr target)
        {
            var parts = new List<string>();
            var at = target;
            while (at is MemberExpr m)
            {
                parts.Insert(0, m.Name);
                at = m.Target;
            }
            if (at is not NameExpr name || IsValueName(name.Name))
                return null;
            parts.Insert(0, name.Name);
            string written = string.Join('.', parts);
            foreach (var ns in scope.Enclosing())
            {
                string full = ns.Length == 0 ? written : $"{ns}.{written}";
                if (binder.Namespaces.Contains(full))
                    return full;
            }
            // `X.F()` with `import D as X;`, `Sub.F()` with `import Sub from D;`.
            if (scope.NamespaceAliases.TryGetValue(written, out var aliased))
                return aliased;
            if (Binder.Imported(written, scope) is { } imported && binder.Namespaces.Contains(imported))
                return imported;
            return binder.Namespaces.Contains(written) ? written : null;
        }

        /// <summary>The method a call chose, its converted arguments, its type arguments (the declaring type's, then its own) and its result.</summary>
        private sealed record Chosen(MethodSymbol Method, H.Expr[] Arguments, H.HirType[] TypeArguments, H.HirType Result);

        /// <summary>
        /// The overload whose parameters the arguments convert to most cheaply (each argument: same type 0, implicit
        /// conversion 1); a tie between the best is ambiguous. <paramref name="declaring"/> gives each candidate's type
        /// arguments of its declaring type (from the receiver); a generic method's own come from <paramref name="explicitArguments"/>
        /// or are inferred from the arguments.
        /// </summary>
        /// <summary>What the call being bound is expected to give (the type of the local it initializes…), for inference.</summary>
        private H.HirType? expectedResult;

        private Chosen? ResolveCall(IReadOnlyList<MethodSymbol> candidates, Expr[] arguments, TextSpan span, string what,
            Func<MethodSymbol, H.HirType[]> declaring, H.HirType[]? explicitArguments = null)
        {
            var expected = expectedResult is { } e && !IsError(e) ? e : null;
            expectedResult = null; // the arguments' own calls do not inherit it
            recorder?.Add(new RecordedCall(source, span, [.. candidates.Select(c => (c, declaring(c)))]));
            var applicable = new List<MethodSymbol>(candidates.Count);
            foreach (var candidate in candidates)
            {
                if (candidate.Parameters.Length == arguments.Length && (explicitArguments is null || explicitArguments.Length == candidate.GenericCount))
                    applicable.Add(candidate);
            }
            if (applicable.Count == 0)
            {
                Error("AN2056", candidates.Count == 0
                    ? $"no {what} to call"
                    : explicitArguments is not null && candidates.All(c => c.GenericCount != explicitArguments.Length)
                        ? $"{what} takes {string.Join(" or ", candidates.Select(c => c.GenericCount).Distinct().Order())} type argument(s), not {explicitArguments.Length}"
                        : $"{what} takes {string.Join(" or ", candidates.Select(c => c.Parameters.Length).Distinct().Order())} argument(s), not {arguments.Length}", span);
                foreach (var argument in arguments)
                    Expression(argument, null);
                return null;
            }
            if (applicable.Any(c => !Reachable(c)))
            {
                // Only what this body may call takes part; when nothing may, the first says why.
                var reachable = applicable.Where(Reachable).ToList();
                if (reachable.Count == 0)
                {
                    Allowed(applicable[0], span);
                    foreach (var argument in arguments)
                        Expression(argument, null);
                    return null;
                }
                applicable = reachable;
            }
            // Each candidate's parameters with its declaring type's arguments (and written method arguments) put in.
            var declared = new H.HirType[applicable.Count][];
            var parameterTypes = new H.HirType[applicable.Count][];
            for (int c = 0; c < applicable.Count; c++)
            {
                declared[c] = declaring(applicable[c]);
                parameterTypes[c] = [.. applicable[c].Parameters.Select(p => p.Substitute(declared[c], explicitArguments))];
            }
            // Arguments are typed by the parameters when every candidate agrees (literals take their types).
            var bound = new H.Expr[arguments.Length];
            var deferred = new List<int>();
            for (int a = 0; a < arguments.Length; a++)
            {
                H.HirType? hint = parameterTypes[0][a];
                for (int c = 1; c < applicable.Count && hint is not null; c++)
                    if (parameterTypes[c][a] != hint)
                        hint = null;
                if (hint is not null && HasMethodParameter(hint))
                    hint = null;
                if (hint is null && (arguments[a] is LambdaExpr || arguments[a] is NameExpr or MemberExpr && parameterTypes.Any(types => types[a] is H.FuncType) && IsMethodGroup(arguments[a])))
                {
                    deferred.Add(a);
                    continue;
                }
                bound[a] = Expression(arguments[a], hint);
            }
            // Lambdas whose parameter types are not known yet: typed by the first candidate whose parameter there is a func
            // type of the lambda's arity, once what the other arguments say of its type parameters is in (`Select(x => …)`).
            foreach (int a in deferred)
            {
                var lambda = arguments[a] as LambdaExpr;
                H.Expr? made = null;
                for (int c = 0; c < applicable.Count && made is null; c++)
                {
                    if (parameterTypes[c][a] is not H.FuncType func || lambda is not null && func.Parameters.Length != lambda.Parameters.Length)
                        continue;
                    // A method group: the lambda that calls it, with as many parameters as the func type takes.
                    var written = lambda ?? GroupLambda(arguments[a], func.Parameters.Length);
                    var partial = new H.HirType?[applicable[c].GenericCount];
                    for (int other = 0; other < bound.Length; other++)
                    {
                        if (bound[other] is { } known)
                            Infer(parameterTypes[c][other], known.Type, partial);
                    }
                    H.HirType[] known2 = [.. partial.Select((x, i) => x ?? new H.GenericParamType(H.GenericOwner.Method, i))];
                    var typed = (H.FuncType)func.Substitute([], known2);
                    if (typed.Parameters.Any(HasMethodParameter) && written.Parameters.Any(p => p.Type is null))
                        continue;
                    bool unknownResult = HasMethodParameter(typed.Result);
                    made = Lambda(written, new H.FuncType(typed.Parameters, unknownResult ? H.HirType.Void : typed.Result), unknownResult);
                }
                bound[a] = made ?? (lambda is not null ? Lambda(lambda, null, null) : Expression(arguments[a], null));
            }
            // A generic method's type arguments, inferred from the arguments when not written.
            var methodArguments = new H.HirType[applicable.Count][];
            for (int c = 0; c < applicable.Count; c++)
            {
                var candidate = applicable[c];
                if (candidate.GenericCount == 0 || explicitArguments is not null)
                {
                    methodArguments[c] = explicitArguments ?? [];
                    continue;
                }
                var inferred = new H.HirType?[candidate.GenericCount];
                for (int a = 0; a < bound.Length; a++)
                    Infer(parameterTypes[c][a], bound[a].Type, inferred);
                if (inferred.Any(i => i is null) && expected is not null)
                    Infer(candidate.Result.Substitute(declared[c], null), expected, inferred); // `Bag b = MakeIt(3)`: T from what is expected
                if (inferred.Any(i => i is null))
                {
                    methodArguments[c] = null!;
                    continue;
                }
                methodArguments[c] = [.. inferred.Select(i => i!)];
                parameterTypes[c] = [.. parameterTypes[c].Select(p => p.Substitute([], methodArguments[c]))];
            }
            int best = -1;
            int bestCost = int.MaxValue;
            bool tie = false;
            for (int c = 0; c < applicable.Count; c++)
            {
                if (methodArguments[c] is null)
                    continue;
                int total = 0;
                for (int a = 0; a < bound.Length && total != int.MaxValue; a++)
                    total = Cost(bound[a], parameterTypes[c][a]) is { } cost ? total + cost : int.MaxValue;
                if (total < bestCost)
                {
                    best = c;
                    bestCost = total;
                    tie = false;
                }
                else if (total == bestCost && total != int.MaxValue)
                {
                    // As good as the best so far: a function that is not generic wins over a generic one.
                    bool generic = applicable[c].GenericCount > 0, bestGeneric = applicable[best].GenericCount > 0;
                    if (bestGeneric && !generic)
                    {
                        best = c;
                        tie = false;
                    }
                    else if (bestGeneric == generic)
                    {
                        tie = true;
                    }
                }
            }
            // An argument that is already an error says why: no second error about the call.
            if ((best < 0 || bestCost == int.MaxValue || tie) && bound.Any(b => IsError(b.Type)))
                return null;
            if (best < 0 || bestCost == int.MaxValue)
            {
                if (applicable.Count == 1 && methodArguments[0] is not null)
                {
                    // Report each argument that does not fit.
                    var only = applicable[0];
                    return new Chosen(only, [.. bound.Select((b, a) => Convert(b, parameterTypes[0][a], arguments[a].Span))],
                        [.. declared[0], .. methodArguments[0]], only.Result.Substitute(declared[0], methodArguments[0]));
                }
                if (applicable.Count == 1)
                    Error("AN2093", $"the type arguments of {what} cannot be inferred from ({string.Join(", ", bound.Select(b => Show(b.Type)))}): write them (`{what.Trim('`')}<…>(…)`)", span);
                else
                    Error("AN2057", $"no {what} takes ({string.Join(", ", bound.Select(b => Show(b.Type)))})", span);
                return null;
            }
            if (tie)
            {
                Error("AN2058", $"the call to {what} is ambiguous: say the argument types (with casts)", span);
                return null;
            }
            var chosen = applicable[best];
            CheckConstraints(chosen.Definition.Generics, methodArguments[best], declared[best], methodArguments[best], span);
            var converted = new H.Expr[bound.Length];
            for (int a = 0; a < bound.Length; a++)
                converted[a] = Convert(bound[a], parameterTypes[best][a], arguments[a].Span);
            return new Chosen(chosen, converted, [.. declared[best], .. methodArguments[best]], chosen.Result.Substitute(declared[best], methodArguments[best]));
        }

        private static bool HasMethodParameter(H.HirType type) => type switch
        {
            H.GenericParamType { Owner: H.GenericOwner.Method } => true,
            H.NamedType n => n.Arguments.Any(HasMethodParameter),
            H.ArrayType a => HasMethodParameter(a.Element),
            H.NullableType n => HasMethodParameter(n.Inner),
            H.FuncType f => f.Parameters.Any(HasMethodParameter) || HasMethodParameter(f.Result),
            H.ReferenceType r => HasMethodParameter(r.Target),
            H.PointerType p => HasMethodParameter(p.Target),
            _ => false,
        };

        /// <summary>Binds the method's type parameters in <paramref name="parameter"/> so it matches <paramref name="argument"/>.</summary>
        private void Infer(H.HirType parameter, H.HirType argument, H.HirType?[] inferred, H.GenericOwner owner = H.GenericOwner.Method)
        {
            if (IsError(argument) || argument == NullType)
                return;
            switch (parameter)
            {
                case H.GenericParamType g when g.Owner == owner && g.Index < inferred.Length:
                    if (inferred[g.Index] is null)
                        inferred[g.Index] = argument;
                    return;
                case H.NamedType p when argument is H.NamedType an && p.Type == an.Type && p.Arguments.Length == an.Arguments.Length:
                    for (int i = 0; i < p.Arguments.Length; i++)
                        Infer(p.Arguments[i], an.Arguments[i], inferred, owner);
                    return;
                case H.ArrayType p when argument is H.ArrayType aa:
                    Infer(p.Element, aa.Element, inferred, owner);
                    return;
                case H.NullableType p:
                    Infer(p.Inner, argument is H.NullableType na ? na.Inner : argument, inferred, owner);
                    return;
                case H.FuncType p when argument is H.FuncType fa && p.Parameters.Length == fa.Parameters.Length:
                    for (int i = 0; i < p.Parameters.Length; i++)
                        Infer(p.Parameters[i], fa.Parameters[i], inferred, owner);
                    Infer(p.Result, fa.Result, inferred, owner);
                    return;
            }
        }

        /// <summary>
        /// The extension methods named <paramref name="name"/> that take <paramref name="receiverType"/> (a static
        /// extension's: the type itself), each with the extension's type arguments inferred from the type.
        /// </summary>
        private List<(MethodSymbol Method, TypeSymbol Extension, H.HirType[] Arguments)> ExtensionMethods(H.HirType receiverType, string name, bool isStatic)
        {
            var found = new List<(MethodSymbol, TypeSymbol, H.HirType[])>();
            // A primitive goes by its primitive type (`ext(i32 x)` extends `i32`, whose members are in Int32).
            if (receiverType is H.NamedType named && binder.SymbolOf(named) is { IsPrimitive: true, Definition.Underlying: { } primitive })
                receiverType = primitive;
            foreach (var extension in binder.Extensions)
            {
                if (extension.IsStatic != isStatic || extension.Definition.Underlying is not { } extended || !extension.Methods.TryGetValue(name, out var methods))
                    continue;
                H.HirType[] arguments = [];
                if (extension.GenericCount > 0)
                {
                    var inferred = new H.HirType?[extension.GenericCount];
                    Infer(extended, receiverType, inferred, H.GenericOwner.Type);
                    if (inferred.Any(i => i is null))
                        continue;
                    arguments = [.. inferred.Select(i => i!)];
                    extended = extended.Substitute(arguments);
                }
                if (!Same(extended, receiverType) && !(isStatic is false && Cost(new H.DefaultExpr(receiverType), extended) is not null))
                    continue;
                foreach (var method in methods)
                    found.Add((method, extension, arguments));
            }
            return found;
        }

        /// <summary>
        /// <c>x.F(…)</c> through an extension (<c>ext(i32 x) { F() … }</c>): the receiver is the first argument; or
        /// <c>i32.F(…)</c> through a static one. Null when no extension has the method.
        /// </summary>
        private H.Expr? ExtensionCall(H.Expr? receiver, H.HirType receiverType, string name, Expr[] arguments, TextSpan span, H.HirType[]? typeArguments)
        {
            var found = ExtensionMethods(receiverType, name, isStatic: receiver is null);
            if (found.Count == 0)
                return null;
            var declaring = found.ToDictionary(f => f.Method, f => f.Arguments);
            Expr[] all = receiver is null ? arguments : [new BoundExpr(receiver, span), .. arguments];
            if (ResolveCall([.. found.Select(f => f.Method)], all, span, $"`{name}`", m => declaring[m], typeArguments) is not { } chosen)
                return new H.DefaultExpr(ErrorType);
            var extension = found.First(f => f.Method == chosen.Method).Extension;
            if (extension.GenericCount > 0)
                CheckConstraints(extension.Definition.Generics, declaring[chosen.Method], declaring[chosen.Method], [], span);
            return new H.CallExpr(null, chosen.Method.Token, chosen.TypeArguments, chosen.Arguments, chosen.Result);
        }

        /// <summary>
        /// Reports type arguments that do not meet their parameters' constraints: the interfaces they must implement, the
        /// list or number group they must be in, the kind of type they must be (<c>copy</c>, <c>unmanaged</c>, <c>new()</c>…).
        /// </summary>
        private void CheckConstraints(IReadOnlyList<H.GenericParam> generics, H.HirType[] arguments, H.HirType[] typeArguments, H.HirType[] methodArguments, TextSpan span)
        {
            for (int g = 0; g < generics.Count && g < arguments.Length; g++)
            {
                foreach (var kind in Binder.KindWords.Keys)
                {
                    if ((generics[g].Kinds & kind) != 0 && !IsError(arguments[g]) && !Meets(arguments[g], kind, []))
                        Error("AN2102", $"`{Show(arguments[g])}` is not `{Binder.KindWords[kind]}` (the constraint of `{generics[g].Name}`)", span);
                }
                foreach (var constructor in generics[g].Constructors)
                {
                    var wanted = constructor.Select(t => t.Substitute(typeArguments, methodArguments)).ToArray();
                    if (!IsError(arguments[g]) && !HasConstructor(arguments[g], wanted))
                        Error("AN2127", $"`{Show(arguments[g])}` has no constructor taking ({string.Join(", ", wanted.Select(Show))}) (the constraint of `{generics[g].Name}`)", span);
                }
                if (generics[g].FixedSize > 0 && !IsError(arguments[g]) && binder.SizeOf(arguments[g], OwnGeneric) != generics[g].FixedSize)
                    Error("AN2126", $"`{Show(arguments[g])}` is {(binder.SizeOf(arguments[g], OwnGeneric) is { } size ? size + " bytes" : "not of a fixed size")}, not {generics[g].FixedSize} (the constraint of `{generics[g].Name}`)", span);
                // `T : [u8, u16, u32]`: one of those (or a parameter that can only be some of them).
                if (generics[g].OneOf is { Length: > 0 } allowed && !IsError(arguments[g]))
                {
                    bool fits = allowed.Contains(arguments[g]) || NumericParam(arguments[g]) is { } kinds && kinds.All(k => allowed.Contains(H.HirType.Of(k)));
                    if (!fits)
                        Error("AN2101", $"`{Show(arguments[g])}` is not one of {string.Join(", ", allowed.Select(Show))} (the constraint of `{generics[g].Name}`)", span);
                }
                foreach (var constraint in generics[g].Types.OfType<H.NamedType>())
                {
                    var required = (H.NamedType)constraint.Substitute(typeArguments, methodArguments);
                    var argument = arguments[g];
                    if (IsError(argument) || binder.SymbolOf(required) is not { IsInterface: true })
                        continue;
                    // A parameter that can only be some number types (`T : numeric`, `T : [i16, f32]`) is compiled per type:
                    // it meets an interface when each of those types does (`numeric` meets `number`).
                    if (!binder.Implements(argument, required, ConstraintsOf)
                        && !(NumericParam(argument) is { } choices && choices.All(k => binder.Implements(H.HirType.Of(k),
                            new H.NamedType(required.Type, [.. required.Arguments.Select(a => Same(a, argument) ? H.HirType.Of(k) : a)]), ConstraintsOf))))
                        Error("AN2094", $"`{Show(argument)}` does not implement `{Show(required)}` (the constraint of `{generics[g].Name}`)", span);
                }
                foreach (var conversion in generics[g].ConvertsTo)
                {
                    var target = conversion.Substitute(typeArguments, methodArguments);
                    var argument = arguments[g];
                    if (IsError(argument) || IsError(target) || Same(argument, target))
                        continue;
                    bool numeric = argument is H.PrimitiveType { Kind: var a } && (a.IsInteger() || a.IsFloat()) && target is H.PrimitiveType { Kind: var t } && (t.IsInteger() || t.IsFloat());
                    bool promised = argument is H.GenericParamType p && ConvertsOf(p).Any(c => Same(c, target));
                    if (!numeric && !promised && UserConversion(new H.DefaultExpr(argument), argument, target) is null)
                        Error("AN2124", $"`{Show(argument)}` has no conversion to `{Show(target)}` (the constraint of `{generics[g].Name}`)", span);
                }
                foreach (var conversion in generics[g].ConvertsFrom)
                {
                    var source = conversion.Substitute(typeArguments, methodArguments);
                    var argument = arguments[g];
                    if (IsError(argument) || IsError(source) || Same(argument, source))
                        continue;
                    bool numeric = argument is H.PrimitiveType { Kind: var a } && (a.IsInteger() || a.IsFloat()) && source is H.PrimitiveType { Kind: var t } && (t.IsInteger() || t.IsFloat());
                    bool promised = source is H.GenericParamType p && ConvertsOf(p).Any(c => Same(c, argument));
                    if (!numeric && !promised && UserConversion(new H.DefaultExpr(source), source, argument) is null)
                        Error("AN2124", $"`{Show(source)}` has no conversion to `{Show(argument)}` (`{generics[g].Name}` is made from one)", span);
                }
            }
        }

        private H.GenericParam? OwnGeneric(H.GenericParamType p)
        {
            var generics = p.Owner == H.GenericOwner.Type ? owner.Definition.Generics : method.Definition.Generics;
            return p.Index < generics.Count ? generics[p.Index] : null;
        }

        private bool Meets(H.HirType type, H.ConstraintKinds kind, HashSet<H.HirType> visiting) => binder.Meets(type, kind, OwnGeneric, visiting);

        private H.ConstraintKinds InterfaceKinds(IEnumerable<H.HirType> constraints, H.HirType self, HashSet<H.NamedType> seen) => binder.InterfaceKinds(constraints, self, seen);

        /// <summary>Whether a type has a constructor with exactly these parameter types (none: a value, or a class with one without parameters).</summary>
        private bool HasConstructor(H.HirType type, H.HirType[] wanted)
        {
            if (type is H.GenericParamType p)
                return OwnGeneric(p) is { } own && (wanted.Length == 0 && (own.Kinds & H.ConstraintKinds.New) != 0 || own.Constructors.Any(c => c.Length == wanted.Length && c.Zip(wanted).All(pair => Same(pair.First, pair.Second))));
            if (type is not H.NamedType named || binder.SymbolOf(named) is not { } symbol || !(symbol.IsClass || symbol.IsStruct) || symbol.IsAbstract || symbol.IsStatic)
                return false;
            if (symbol.Constructors.Count == 0)
                return wanted.Length == 0;
            return symbol.Constructors.Any(c => c.Parameters.Length == wanted.Length && c.Parameters.Select(t => t.Substitute(named.Arguments)).Zip(wanted).All(pair => Same(pair.First, pair.Second)));
        }

        private H.Expr New(NewExpr n)
        {
            var type = Resolve(n.Type) ?? ErrorType;
            if (IsError(type) || binder.SymbolOf(type) is { Domain: not null } foreign && !CrossesDomain(foreign, null, $"`new {foreign.Name}`", n.Type.Span))
            {
                foreach (var argument in n.Arguments)
                    Expression(argument, null);
                return new H.DefaultExpr(ErrorType);
            }
            if (type is H.GenericParamType param)
            {
                // `new T(a, b)`: a constructor the constraints promise (`T : new(i32, str)`, `T : new()`), met per type argument.
                var own = OwnGeneric(param);
                var signatures = (own?.Constructors ?? []).ToList();
                if (own is not null && (own.Kinds & H.ConstraintKinds.New) != 0)
                    signatures.Add([]);
                var given = n.Arguments.Select(a => Expression(a, null)).ToArray();
                if (given.Any(a => IsError(a.Type)))
                    return new H.DefaultExpr(ErrorType);
                H.HirType[]? best = null;
                int bestCost = int.MaxValue;
                foreach (var signature in signatures)
                {
                    if (signature.Length != given.Length)
                        continue;
                    int cost = 0;
                    bool fits = true;
                    for (int i = 0; i < given.Length && fits; i++)
                    {
                        if (Cost(given[i], signature[i]) is { } c)
                            cost += c;
                        else
                            fits = false;
                    }
                    if (fits && cost < bestCost)
                        (best, bestCost) = (signature, cost);
                }
                if (best is null)
                    return Failed("AN2125", $"`new {Show(type)}(…)` needs `{Show(type)} : new({string.Join(", ", given.Select(a => Show(a.Type)))})` among its constraints", n.Span);
                return new H.NewExpr(null, [.. given.Select((a, i) => Convert(a, best[i], n.Arguments[i].Span))], [], type);
            }
            if (binder.SymbolOf(type) is not { } symbol || type is not H.NamedType || !(symbol.IsClass || symbol.IsStruct))
                return Failed("AN2059", $"`new` makes objects and values, not a `{Show(type)}`", n.Type.Span);
            if (symbol.IsStatic)
                return Failed("AN2060", $"`{symbol.Name}` is static: there are no objects of it", n.Type.Span);
            if (symbol.IsAbstract)
                return Failed("AN2061", $"`{symbol.Name}` is a base class: make an object of a class derived from it", n.Type.Span);
            var typeArguments = ((H.NamedType)type).Arguments;
            CheckConstraints(symbol.Definition.Generics, typeArguments, typeArguments, [], n.Type.Span);
            if (symbol.Constructors.Count == 0)
            {
                if (n.Arguments.Length > 0)
                    return Failed("AN2062", $"`{symbol.Name}` has no constructor that takes arguments", n.Span);
                return new H.NewExpr(null, [], [], type);
            }
            if (ResolveCall(symbol.Constructors, n.Arguments, n.Span, $"a constructor of `{symbol.Name}`", _ => typeArguments) is not { } chosen)
                return new H.DefaultExpr(ErrorType);
            return new H.NewExpr(chosen.Method.Token, chosen.Arguments, [], type);
        }

        // ── Operators ──────────────────────────────────────

        /// <summary>An object reference: a class or an interface (values of both are handles).</summary>
        private bool IsReference(H.HirType type) => type is H.NamedType && binder.SymbolOf(type) is { IsClass: true } or { IsInterface: true };

        private bool IsInterface(H.HirType type) => type is H.NamedType && binder.SymbolOf(type) is { IsInterface: true };

        /// <summary>`any` (the root class).</summary>
        private bool IsAny(H.HirType type) => binder.ObjectSymbol is { } root && type == root.Type;

        /// <summary>A function value or a pointer: an `any` holds one in a <c>System.Boxed&lt;T&gt;</c>.</summary>
        private static bool Opaque(H.HirType type) => type is H.FuncType or H.PointerType or H.NativeFuncType;

        private TypeSymbol? BoxedSymbol() => binder.Types.GetValueOrDefault("System.Boxed") is { GenericCount: 1 } boxed ? boxed : null;

        /// <summary><c>new System.Boxed&lt;T&gt;(value, "T")</c>: the value, and its type's name for ToString.</summary>
        private H.Expr? Boxed(H.Expr value)
        {
            if (BoxedSymbol() is not { } boxed || binder.StringType is not { } text
                || boxed.Constructors.FirstOrDefault(c => c.Parameters.Length == 2) is not { } constructor)
                return null;
            var type = new H.NamedType(boxed.Token, [value.Type]);
            return new H.NewExpr(constructor.Token, [value, new H.LiteralExpr(new H.StringConstant(Show(value.Type)), text)], [], type);
        }

        /// <summary>A value (copied, not shared): a primitive, a value type, an enum. To be an `any` or an interface it is boxed.</summary>
        private bool IsValueType(H.HirType type) => type is H.PrimitiveType { Kind: not H.Primitive.Void }
            || type is H.NamedType && binder.SymbolOf(type) is { IsStruct: true } or { IsEnum: true };

        private bool IsNumeric(H.HirType type) => type is H.PrimitiveType { Kind: not (H.Primitive.Void or H.Primitive.Bool or H.Primitive.Char) } || NumericParam(type) is not null;

        private bool IsInteger(H.HirType type) => type is H.PrimitiveType { Kind: var k } && k.IsInteger() || IntegerParam(type) is not null;

        /// <summary>
        /// The number types a type parameter can be (<c>T : [u8, u16, u32]</c>, <c>T : numeric</c>), or null: such a
        /// parameter is a number itself (arithmetic, comparisons, casts to and from numbers, literals that fit every one of
        /// them), and each use of the function is compiled for its own type.
        /// </summary>
        private H.Primitive[]? NumericParam(H.HirType type)
        {
            if (type is not H.GenericParamType param)
                return null;
            var generics = param.Owner == H.GenericOwner.Type ? owner.Definition.Generics : method.Definition.Generics;
            if (param.Index >= generics.Count || generics[param.Index].OneOf is not { Length: > 0 } list)
                return null;
            var kinds = new H.Primitive[list.Length];
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i] is not H.PrimitiveType { Kind: var kind } || !(kind.IsInteger() || kind.IsFloat()))
                    return null;
                kinds[i] = kind;
            }
            return kinds;
        }

        /// <summary>A type parameter that can only be integers (bit operations, `%` by integers, shifts).</summary>
        private H.Primitive[]? IntegerParam(H.HirType type) => NumericParam(type) is { } kinds && kinds.All(k => k.IsInteger()) ? kinds : null;

        /// <summary>
        /// A literal of a number type parameter: an integer that fits all its integer types (as the smallest of them, or as
        /// an f64 when it has only floats), or a real when it has only floats; converted to the parameter.
        /// </summary>
        private H.Expr? ParamLiteral(BigInteger value, H.HirType type)
        {
            if (NumericParam(type) is not { } kinds)
                return null;
            var integers = kinds.Where(k => k.IsInteger()).ToArray();
            if (!integers.All(k => Binder.Fits(value, k)))
                return null;
            H.Expr literal = integers.Length > 0
                ? new H.LiteralExpr(new H.IntegerConstant(value, integers.OrderBy(k => k.Bits()).First()), H.HirType.Of(integers.OrderBy(k => k.Bits()).First()))
                : new H.LiteralExpr(H.FloatConstant.Of((double)value), H.HirType.F64);
            return new H.ConvertExpr(literal, H.ConversionKind.Numeric, type);
        }

        /// <summary>Whether every value of number type <paramref name="from"/> is one of <paramref name="to"/> (the widening conversions).</summary>
        private static bool Widens(H.Primitive from, H.Primitive to)
        {
            if (from == to)
                return true;
            if (from.IsInteger() && to.IsInteger())
                return from.IsSigned() == to.IsSigned() ? to.Bits() > from.Bits() : !from.IsSigned() && to.IsSigned() && to.Bits() > from.Bits();
            if (from.IsInteger() && to.IsFloat())
                return true;
            return from.IsFloat() && to.IsFloat() && to.Bits() > from.Bits();
        }

        private H.Expr Unary(UnaryExpr unary, H.HirType? expected)
        {
            switch (unary.Operator)
            {
                case UnaryOperator.Negate:
                {
                    var operand = Expression(unary.Operand, expected);
                    if (IsError(operand.Type))
                        return operand;
                    if (operand.Type is H.NamedType && DeclaredOperator("$op_Negate", [operand]) is { } negated)
                        return negated;
                    if (RequiredUnary("-", H.UnaryOp.Negate, operand) is { } requiredNegation)
                        return requiredNegation;
                    if (!IsNumeric(operand.Type) || operand.Type is H.PrimitiveType { Kind: var k } && k.IsInteger() && !k.IsSigned())
                        return Failed("AN2063", $"`-` negates a signed number, not a `{Show(operand.Type)}`", unary.Span);
                    return new H.UnaryExpr(H.UnaryOp.Negate, operand, operand.Type);
                }
                case UnaryOperator.Not:
                {
                    var operand = Expression(unary.Operand, H.HirType.Bool);
                    if (operand.Type is H.NamedType && DeclaredOperator("$op_Not", [operand]) is { } not)
                        return not;
                    if (RequiredUnary("!", H.UnaryOp.Not, operand) is { } requiredNot)
                        return requiredNot;
                    operand = Convert(operand, H.HirType.Bool, unary.Operand.Span);
                    return new H.UnaryExpr(H.UnaryOp.Not, operand, H.HirType.Bool);
                }
                case UnaryOperator.Complement:
                {
                    var operand = Expression(unary.Operand, expected);
                    if (operand.Type is H.NamedType && DeclaredOperator("$op_Complement", [operand]) is { } flipped)
                        return flipped;
                    if (RequiredUnary("~", H.UnaryOp.BitNot, operand) is { } requiredComplement)
                        return requiredComplement;
                    if (operand.Type is H.NamedType && binder.SymbolOf(operand.Type) is { Definition.Kind: H.TypeKind.Bitflags })
                        return new H.UnaryExpr(H.UnaryOp.BitNot, operand, operand.Type);
                    if (!IsError(operand.Type) && !IsInteger(operand.Type))
                        return Failed("AN2064", $"`~` flips the bits of an integer, not a `{Show(operand.Type)}`", unary.Span);
                    return new H.UnaryExpr(H.UnaryOp.BitNot, operand, operand.Type);
                }
                case UnaryOperator.PreIncrement or UnaryOperator.PreDecrement or UnaryOperator.PostIncrement or UnaryOperator.PostDecrement:
                {
                    var target = Place(unary.Operand);
                    if (IsError(target.Type))
                        return target;
                    if (target.Type is H.PointerType && unsafeDepth == 0)
                        return Failed("AN2121", "pointer arithmetic goes in unsafe code", unary.Span);
                    bool increment = unary.Operator is UnaryOperator.PreIncrement or UnaryOperator.PostIncrement;
                    bool before = unary.Operator is UnaryOperator.PreIncrement or UnaryOperator.PreDecrement;
                    if (target.Type is H.NamedType && DeclaredOperator(increment ? "$op_Increment" : "$op_Decrement", [target], unary.Span) is { } stepped)
                    {
                        // The type's own `op ++`: the place takes what it gives; `++x` is the new value, `x++` the old one.
                        if (!Same(stepped.Type, target.Type))
                            return Failed("AN2065", $"`op {(increment ? "++" : "--")}` of `{Show(target.Type)}` gives a `{Show(stepped.Type)}`: it gives back its operand's type, to store it there", unary.Span);
                        if (before)
                            return new H.AssignExpr(target, stepped, target.Type);
                        int old = Declare($"$step{locals.Count}", target.Type, default);
                        var oldValue = new H.LocalExpr(old, target.Type);
                        var fromOld = DeclaredOperator(increment ? "$op_Increment" : "$op_Decrement", [oldValue], unary.Span)!;
                        return new H.BlockExpr([new H.LocalStmt(old, target), new H.ExprStmt(new H.AssignExpr(target, fromOld, target.Type))], oldValue, target.Type);
                    }
                    if (!IsNumeric(target.Type) && target.Type is not H.PointerType)
                        return Failed("AN2065", $"`++` and `--` change a number, not a `{Show(target.Type)}` (a type declares `op ++(T x) : T` to have them)", unary.Span);
                    var op = unary.Operator switch
                    {
                        UnaryOperator.PreIncrement => H.UnaryOp.PreIncrement,
                        UnaryOperator.PreDecrement => H.UnaryOp.PreDecrement,
                        UnaryOperator.PostIncrement => H.UnaryOp.PostIncrement,
                        _ => H.UnaryOp.PostDecrement,
                    };
                    return new H.UnaryExpr(op, target, target.Type);
                }
                case UnaryOperator.AddressOf when unary.Operand is NameExpr or MemberExpr && IsMethodGroup(unary.Operand):
                    // `&F`: the address of a static function (a `func*`).
                    return NativeFunction(unary.Operand, expected as H.NativeFuncType, unary.Span);
                case UnaryOperator.AddressOf when expected is H.ReferenceType || unsafeDepth == 0 && expected is not H.PointerType:
                case UnaryOperator.BorrowMut:
                {
                    // `&x`, `&mut x`: a borrow of a place, which the borrow checker follows (`&x` in unsafe code, where a
                    // pointer is wanted, is its address instead).
                    bool mutable = unary.Operator == UnaryOperator.BorrowMut;
                    if (!binder.Options.Borrows)
                        return Failed("AN2144", $"`{(mutable ? "&mut x" : "&x")}` borrows, and this project's borrow checker is off (BUILD `borrowcheck: off`): pass the value itself, or turn it on (`borrowcheck: enforce`)", unary.Span);
                    if (unary.Operand is not (NameExpr or MemberExpr or IndexExpr))
                        return Failed("AN2145", $"`{(mutable ? "&mut" : "&")}` borrows a place (a local, a parameter, a field, an element), not a value", unary.Span);
                    var borrowed = Place(unary.Operand);
                    if (IsError(borrowed.Type))
                        return new H.DefaultExpr(ErrorType);
                    var target = borrowed.Type is H.ReferenceType inner ? inner.Target : borrowed.Type;
                    return new H.BorrowExpr(borrowed, mutable, new H.ReferenceType(target, mutable));
                }
                case UnaryOperator.AddressOf:
                {
                    if (unsafeDepth == 0)
                        return Failed("AN2066", "`&x` takes an address: only in unsafe code", unary.Span);
                    var place = Place(unary.Operand);
                    return new H.AddressOfExpr(place, new H.PointerType(place.Type));
                }
                case UnaryOperator.Dereference:
                {
                    if (unsafeDepth == 0)
                        return Failed("AN2067", "`*p` reads through a pointer: only in unsafe code", unary.Span);
                    var pointer = Expression(unary.Operand, null);
                    if (pointer.Type is not H.PointerType p)
                        return Failed("AN2068", $"`*` reads through a pointer, not a `{Show(pointer.Type)}`", unary.Span);
                    return new H.DerefExpr(pointer, p.Target);
                }
            }
            return Failed("AN2069", "this operator comes in a later phase of the new compiler", unary.Span);
        }

        /// <summary>An expression that names a place to assign: a local, a parameter, a field, an element, <c>*p</c>.</summary>
        private H.Expr Place(Expr expression)
        {
            var bound = Expression(expression, null);
            // A constant, or a field of a constant value, never changes.
            for (var at = bound; at is H.FieldExpr or H.LocalExpr; at = at is H.FieldExpr { Instance: { } inner } ? inner : null)
            {
                if (at is H.LocalExpr { Local: var index } && localConstants.ContainsKey(index)
                    || at is H.FieldExpr held && binder.FieldOf(held.Field) is { IsConstant: true })
                    return Failed("AN2140", $"`{source[expression.Span]}` is part of a constant: it never changes", expression.Span);
            }
            switch (bound)
            {
                case H.LocalExpr or H.ParamExpr or H.ElementExpr or H.DerefExpr:
                    return bound;
                case H.FieldExpr field:
                {
                    if (binder.FieldOf(field.Field) is not { } fieldSymbol)
                        return bound;
                    var symbol = fieldSymbol.Owner;
                    if (symbol.Definition.Fields[fieldSymbol.Index].Flags.HasFlag(H.FieldFlags.Readonly) && !((method.IsConstructor || method.IsStaticConstructor) && method.Owner == symbol))
                        return Failed("AN2070", $"`{fieldSymbol.Name}` is readonly: only its constructors set it", expression.Span);
                    return bound;
                }
                case H.ThisExpr when owner.IsStruct:
                    return bound;
                case H.PropertyExpr property:
                {
                    if (binder.PropertyOf(property.Property) is { } propertySymbol && propertySymbol.Owner.Definition.Properties[propertySymbol.Index].Setter < 0)
                        return Failed("AN2095", $"`{propertySymbol.Name}` has no `set`", expression.Span);
                    if (binder.PropertyOf(property.Property) is { } written && !Allowed(written, true, expression.Span))
                        return new H.DefaultExpr(ErrorType);
                    return bound;
                }
                case H.DefaultExpr { Type: var t } when IsError(t):
                    return bound;
                case H.LiteralExpr:
                    return Failed("AN2071", "a constant cannot be assigned", expression.Span);
            }
            return Failed("AN2072", "this is not something that can be assigned", expression.Span);
        }

        private H.Expr Assign(AssignExpr assign)
        {
            if (assign.Target is IndexExpr index)
            {
                // `x[i] = v` on an object or value is `x.SetAt(i, v)`.
                var receiver = Expression(index.Target, null);
                if (!IsError(receiver.Type) && receiver.Type is not H.ArrayType && binder.MemberSymbolOf(receiver.Type) is { } indexed && indexed.FindMethods("SetAt").Count > 0)
                {
                    if (assign.Operator is not null)
                        return Failed("AN2096", "`x[i] op= v` on an object comes in a later phase of the new compiler: write `x[i] = x[i] op v`", assign.Span);
                    return MemberCall(receiver, "SetAt", [.. index.Indices, assign.Value], assign.Span, index.Span);
                }
            }
            var target = Place(assign.Target);
            if (IsError(target.Type))
            {
                Expression(assign.Value, null);
                return target;
            }
            if (assign.Operator is { } op)
            {
                var value = Expression(assign.Value, target.Type);
                if (target.Type is H.GenericParamType && RequiredBinary(op, target, value) is { } required)
                {
                    if (!Same(required.Type, target.Type))
                        return Failed("AN2099", $"`{Symbol(op)}=` needs `op {Symbol(op)}` to give a `{Show(target.Type)}`", assign.Span);
                    return new H.AssignExpr(target, required, target.Type);
                }
                if (target.Type is H.NamedType && DeclaredBinary(op, target, value) is { } combined)
                {
                    if (!Same(combined.Type, target.Type))
                        return Failed("AN2099", $"`{Symbol(op)}=` needs `op {Symbol(op)}` to give a `{Show(target.Type)}`", assign.Span);
                    return new H.AssignExpr(target, combined, target.Type);
                }
                if (target.Type is H.PointerType)
                {
                    if (unsafeDepth == 0)
                        return Failed("AN2121", "pointer arithmetic goes in unsafe code", assign.Span);
                    if (op is not (BinaryOperator.Add or BinaryOperator.Subtract) || !IsInteger(value.Type))
                        return Failed("AN2122", "a pointer moves with `p += n` or `p -= n`", assign.Span);
                    return new H.AssignExpr(target, Convert(value, H.HirType.I64, assign.Value.Span, explicitly: true), target.Type) { Compound = Hir(op) };
                }
                if (op is BinaryOperator.ShiftLeft or BinaryOperator.ShiftRight)
                {
                    if (!IsInteger(target.Type))
                        return Failed("AN2073", $"shifts work on integers, not a `{Show(target.Type)}`", assign.Span);
                }
                else if (!(IsNumeric(target.Type) || target.Type == H.HirType.Bool && op is BinaryOperator.And or BinaryOperator.Or or BinaryOperator.Xor))
                {
                    return Failed("AN2074", $"`{Symbol(op)}=` works on numbers, not a `{Show(target.Type)}`", assign.Span);
                }
                return new H.AssignExpr(target, Convert(value, target.Type, assign.Value.Span), target.Type) { Compound = Hir(op) };
            }
            var assigned = Convert(Expression(assign.Value, target.Type), target.Type, assign.Value.Span);
            return new H.AssignExpr(target, assigned, target.Type);
        }

        private static string Symbol(BinaryOperator op) => op switch
        {
            BinaryOperator.Add => "+", BinaryOperator.Subtract => "-", BinaryOperator.Multiply => "*", BinaryOperator.Divide => "/",
            BinaryOperator.Remainder => "%", BinaryOperator.And => "&", BinaryOperator.Or => "|", BinaryOperator.Xor => "^",
            BinaryOperator.ShiftLeft => "<<", BinaryOperator.ShiftRight => ">>", BinaryOperator.Equal => "==", BinaryOperator.NotEqual => "!=",
            BinaryOperator.Less => "<", BinaryOperator.LessEqual => "<=", BinaryOperator.Greater => ">", BinaryOperator.GreaterEqual => ">=",
            BinaryOperator.LogicalAnd => "&&", BinaryOperator.LogicalOr => "||", _ => "??",
        };

        private static H.BinaryOp Hir(BinaryOperator op) => op switch
        {
            BinaryOperator.Add => H.BinaryOp.Add,
            BinaryOperator.Subtract => H.BinaryOp.Subtract,
            BinaryOperator.Multiply => H.BinaryOp.Multiply,
            BinaryOperator.Divide => H.BinaryOp.Divide,
            BinaryOperator.Remainder => H.BinaryOp.Remainder,
            BinaryOperator.And => H.BinaryOp.BitAnd,
            BinaryOperator.Or => H.BinaryOp.BitOr,
            BinaryOperator.Xor => H.BinaryOp.BitXor,
            BinaryOperator.ShiftLeft => H.BinaryOp.ShiftLeft,
            BinaryOperator.ShiftRight => H.BinaryOp.ShiftRight,
            BinaryOperator.Equal => H.BinaryOp.Equal,
            BinaryOperator.NotEqual => H.BinaryOp.NotEqual,
            BinaryOperator.Less => H.BinaryOp.Less,
            BinaryOperator.LessEqual => H.BinaryOp.LessEqual,
            BinaryOperator.Greater => H.BinaryOp.Greater,
            BinaryOperator.GreaterEqual => H.BinaryOp.GreaterEqual,
            BinaryOperator.LogicalAnd => H.BinaryOp.And,
            _ => H.BinaryOp.Or,
        };

        private H.Expr Binary(BinaryExpr binary, H.HirType? expected)
        {
            var op = binary.Operator;
            if (op is BinaryOperator.LogicalAnd or BinaryOperator.LogicalOr)
                return new H.BinaryExpr(Hir(op), Condition(binary.Left), Condition(binary.Right), H.HirType.Bool);
            if (op == BinaryOperator.Coalesce)
                return Coalesce(binary, expected);
            bool comparison = op is BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.Less or BinaryOperator.LessEqual
                or BinaryOperator.Greater or BinaryOperator.GreaterEqual;
            var left = Expression(binary.Left, comparison ? null : expected);
            // `view == [1, 2, 3]`: the literal takes the type the other operand's operator asks for.
            var right = binary.Right is ArrayLiteralExpr && ArrayOperand(op, left.Type, literalOnRight: true) is { } rightArray
                ? Expression(binary.Right, rightArray)
                : Expression(binary.Right, comparison || op is BinaryOperator.ShiftLeft or BinaryOperator.ShiftRight ? Literal(left) ? null : left.Type : expected ?? (Literal(left) ? null : left.Type));
            if (Literal(left) && !Literal(right) && !IsError(right.Type))
                left = Expression(binary.Left, right.Type);
            else if (binary.Left is ArrayLiteralExpr && ArrayOperand(op, right.Type, literalOnRight: false) is { } leftArray && left.Type != leftArray)
                left = Expression(binary.Left, leftArray);
            if (IsError(left.Type) || IsError(right.Type))
                return new H.DefaultExpr(comparison ? H.HirType.Bool : ErrorType);

            if (left.Type is H.PointerType || right.Type is H.PointerType)
                return PointerArithmetic(op, left, right, binary.Span);
            if ((left.Type is H.GenericParamType || right.Type is H.GenericParamType) && RequiredBinary(op, left, right) is { } required)
                return required;
            if (op is BinaryOperator.Equal or BinaryOperator.NotEqual)
            {
                bool leftNull = left is H.LiteralExpr { Value: H.NullConstant }, rightNull = right is H.LiteralExpr { Value: H.NullConstant };
                if (leftNull || rightNull)
                {
                    var other = leftNull ? right : left;
                    if (other.Type is not H.NullableType)
                    {
                        if (leftNull && rightNull)
                            return new H.LiteralExpr(new H.BoolConstant(op == BinaryOperator.Equal), H.HirType.Bool);
                        return Failed("AN2075", $"a `{Show(other.Type)}` is never null", binary.Span);
                    }
                    var typedNull = new H.LiteralExpr(new H.NullConstant(), other.Type);
                    return new H.BinaryExpr(Hir(op), leftNull ? typedNull : left, rightNull ? typedNull : right, H.HirType.Bool);
                }
            }
            if ((left.Type is H.NamedType || right.Type is H.NamedType) && DeclaredBinary(op, left, right) is { } declared)
                return declared;
            if (binder.StringSymbol is { } text && (left.Type == binder.StringType || right.Type == binder.StringType) && Strings(op, left, right, text, binary.Span) is { } joined)
                return joined;
            if (op is BinaryOperator.ShiftLeft or BinaryOperator.ShiftRight)
            {
                if (!IsInteger(left.Type) || !IsInteger(right.Type))
                    return Failed("AN2073", $"shifts work on integers, not `{Show(left.Type)}` and `{Show(right.Type)}`", binary.Span);
                return new H.BinaryExpr(Hir(op), left, Convert(right, left.Type, binary.Right.Span, explicitly: true), left.Type);
            }
            // Arithmetic takes the type the result goes to when both operands convert to it (`i64 x = a + b`).
            var (a, b, type) = Unify(left, right, comparison || expected is not H.PrimitiveType ? null : expected, binary.Span);
            if (IsError(type))
                return new H.DefaultExpr(comparison ? H.HirType.Bool : ErrorType);
            bool isEnum = binder.SymbolOf(type) is { IsEnum: true } && type is H.NamedType;
            if (comparison)
            {
                bool ordered = op is not (BinaryOperator.Equal or BinaryOperator.NotEqual);
                if (ordered && !IsNumeric(type) && !isEnum)
                    return Failed("AN2076", $"`{Symbol(op)}` compares numbers, not `{Show(type)}`", binary.Span);
                // Records compare field by field; other objects (and arrays, interface values, `any`): the same object or not.
                if (!ordered && type is not H.NullableType && RecordOf(type) is { } record)
                    return RecordEquality(op, a, b, record, binary.Span);
                if (!ordered && type is H.NullableType && RecordOf(type) is not null)
                    return Failed("AN2134", $"`{Symbol(op)}` on a nullable record: test for null first, then compare the values", binary.Span);
                if (!ordered && (IsReference(type) || type is H.ArrayType || type is H.NullableType { Inner: var held } && (IsReference(held) || held is H.ArrayType)))
                    return new H.BinaryExpr(Hir(op), a, b, H.HirType.Bool);
                if (!ordered && !IsNumeric(type) && !isEnum && type != H.HirType.Bool)
                    return Failed("AN2077", $"`{Symbol(op)}` on `{Show(type)}` comes in a later phase of the new compiler (objects compare with `is` patterns for now)", binary.Span);
                return new H.BinaryExpr(Hir(op), a, b, H.HirType.Bool);
            }
            bool bitwise = op is BinaryOperator.And or BinaryOperator.Or or BinaryOperator.Xor;
            if (bitwise && (IsInteger(type) || type == H.HirType.Bool || isEnum && binder.SymbolOf(type)!.Definition.Kind == H.TypeKind.Bitflags))
                return new H.BinaryExpr(Hir(op), a, b, type);
            if (!IsNumeric(type) || bitwise)
                return Failed("AN2078", $"`{Symbol(op)}` works on numbers, not `{Show(type)}`", binary.Span);
            return new H.BinaryExpr(Hir(op), a, b, type);
        }

        private static bool Literal(H.Expr e) => e is H.LiteralExpr { Value: H.IntegerConstant or H.FloatConstant };

        /// <summary>A pointer's null: address 0.</summary>
        private static H.Expr NullPointer(H.HirType pointer) =>
            new H.ConvertExpr(new H.LiteralExpr(new H.IntegerConstant(0, H.Primitive.U64), H.HirType.Of(H.Primitive.U64)), H.ConversionKind.Pointer, pointer);

        /// <summary>
        /// Pointers (unsafe code): <c>p + n</c>, <c>n + p</c>, <c>p - n</c> move by whole elements; <c>p - q</c> counts the
        /// elements between them (an <c>i64</c>); comparisons compare addresses (<c>p == null</c> tests for address 0).
        /// </summary>
        private H.Expr PointerArithmetic(BinaryOperator op, H.Expr left, H.Expr right, TextSpan span)
        {
            if (unsafeDepth == 0)
                return Failed("AN2121", "pointer arithmetic goes in unsafe code", span);
            if (left is H.LiteralExpr { Value: H.NullConstant })
                left = NullPointer(right.Type);
            if (right is H.LiteralExpr { Value: H.NullConstant })
                right = NullPointer(left.Type);
            bool comparison = op is BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.Less or BinaryOperator.LessEqual
                or BinaryOperator.Greater or BinaryOperator.GreaterEqual;
            if (left.Type is H.PointerType lp && right.Type is H.PointerType rp)
            {
                if (!Same(lp, rp) && !(comparison && (lp.Target == H.HirType.Void || rp.Target == H.HirType.Void)))
                    return Failed("AN2122", $"`{Show(left.Type)}` and `{Show(right.Type)}` point at different types", span);
                if (comparison)
                    return new H.BinaryExpr(Hir(op), left, right, H.HirType.Bool);
                if (op == BinaryOperator.Subtract)
                    return new H.BinaryExpr(H.BinaryOp.Subtract, left, right, H.HirType.I64);
                return Failed("AN2122", $"`{Symbol(op)}` does not take two pointers (only `-`, which counts elements, and comparisons)", span);
            }
            if (op == BinaryOperator.Add && right.Type is H.PointerType)
                (left, right) = (right, left);
            if (left.Type is not H.PointerType pointer || op is not (BinaryOperator.Add or BinaryOperator.Subtract) || !IsInteger(right.Type))
                return Failed("AN2122", $"`{Symbol(op)}` on a pointer: `p + n`, `p - n`, `p - q` or a comparison", span);
            return new H.BinaryExpr(Hir(op), left, Convert(right, H.HirType.I64, span, explicitly: true), pointer);
        }

        /// <summary>
        /// An enum value's name: <c>value switch { A =&gt; "A", …, var v =&gt; v's number as text }</c> (the first member
        /// with that value).
        /// </summary>
        /// <summary>
        /// The text of a bitflag value that is no member by itself: the names of the one-bit members it holds, joined with
        /// <c> | </c> (<c>First | Third</c>), then the digits of the bits no member names; <c>0</c> for none.
        /// </summary>
        private H.Expr? BitflagText(H.Expr value, TypeSymbol symbol, H.HirType underlying, TypeSymbol number)
        {
            if (binder.StringType is not { } text || binder.MemberSymbolOf(text) is not { } textSymbol)
                return null;
            var type = value.Type;
            int acc = Declare($"$names{locals.Count}", text, default), rest = Declare($"$rest{locals.Count}", type, default);
            var names = new H.LocalExpr(acc, text);
            var left = new H.LocalExpr(rest, type);
            H.Expr? Join(H.Expr piece)
            {
                // `names.Length == 0 ? piece : names + " | " + piece`
                if (PropertyRead(names, "Length", default) is not { Type: H.PrimitiveType } length || IsError(length.Type))
                    return null;
                if (Strings(BinaryOperator.Add, names, new H.LiteralExpr(new H.StringConstant(" | "), text), textSymbol, default) is not { } withBar
                    || Strings(BinaryOperator.Add, withBar, piece, textSymbol, default) is not { } joined)
                    return null;
                var empty = new H.BinaryExpr(H.BinaryOp.Equal, length, new H.LiteralExpr(new H.IntegerConstant(0, H.Primitive.I32), H.HirType.I32), H.HirType.Bool);
                return new H.ConditionalExpr(empty, piece, joined, text);
            }
            var statements = new List<H.Stmt>
            {
                new H.LocalStmt(acc, new H.LiteralExpr(new H.StringConstant(""), text)),
                new H.LocalStmt(rest, value),
            };
            foreach (var field in symbol.Definition.Fields)
            {
                if (field.Value is not H.EnumConstant { Value: var bits } || bits <= 0 || (bits & (bits - 1)) != 0)
                    continue; // one-bit members only: a union member is named when the value is exactly it (the switch above)
                var member = new H.LiteralExpr(new H.EnumConstant(type, bits), type);
                var has = new H.BinaryExpr(H.BinaryOp.Equal, new H.BinaryExpr(H.BinaryOp.BitAnd, left, member, type), member, H.HirType.Bool);
                if (Join(new H.LiteralExpr(new H.StringConstant(field.Name), text)) is not { } joined)
                    return null;
                statements.Add(new H.IfStmt(has, new H.BlockStmt([
                    new H.ExprStmt(new H.AssignExpr(names, joined, text)),
                    new H.ExprStmt(new H.AssignExpr(left, new H.BinaryExpr(H.BinaryOp.BitAnd, left, new H.UnaryExpr(H.UnaryOp.BitNot, member, type), type), type)),
                ]), null));
            }
            // Bits no member names: their number; nothing named at all: `0`.
            var raw = new H.ConvertExpr(left, H.ConversionKind.Enum, underlying);
            if (BoundCall(raw, number, "ToString", []) is not { } digits || Join(digits) is not { } withDigits)
                return null;
            var leftover = new H.BinaryExpr(H.BinaryOp.NotEqual, left, new H.DefaultExpr(type), H.HirType.Bool);
            statements.Add(new H.IfStmt(leftover, new H.ExprStmt(new H.AssignExpr(names, withDigits, text)), null));
            var none = new H.BinaryExpr(H.BinaryOp.Equal, PropertyRead(names, "Length", default), new H.LiteralExpr(new H.IntegerConstant(0, H.Primitive.I32), H.HirType.I32), H.HirType.Bool);
            return new H.BlockExpr([.. statements], new H.ConditionalExpr(none, new H.LiteralExpr(new H.StringConstant("0"), text), names, text), text);
        }

        private H.Expr? EnumText(H.Expr value, TypeSymbol symbol, bool texts = false)
        {
            var underlying = EnumNumber(symbol);
            if (binder.StringType is not { } text || binder.MemberSymbolOf(underlying) is not { } number)
                return null;
            var arms = new List<H.SwitchArm>();
            var seen = new HashSet<System.Numerics.BigInteger>();
            foreach (var field in symbol.Definition.Fields)
            {
                if (field.Value is H.EnumConstant constant && seen.Add(constant.Value))
                    arms.Add(new H.SwitchArm(new H.ConstantPattern(constant), null, new H.LiteralExpr(new H.StringConstant(texts ? constant.Text ?? field.Name : field.Name), text)));
            }
            int other = Declare($"$enum{locals.Count}", value.Type, default);
            var raw = new H.ConvertExpr(new H.LocalExpr(other, value.Type), H.ConversionKind.Enum, underlying);
            var rest = symbol.Definition.Kind == H.TypeKind.Bitflags && !texts ? BitflagText(new H.LocalExpr(other, value.Type), symbol, underlying, number) : BoundCall(raw, number, "ToString", []);
            if (rest is not { } digits)
                return null;
            arms.Add(new H.SwitchArm(new H.BindingPattern(other, null), null, digits));
            return new H.SwitchExpr(value, [.. arms], text);
        }

        /// <summary>The base of a value-based enum (<c>enum Color : Rgb</c>), else null.</summary>
        private H.HirType? ValueBase(TypeSymbol symbol) =>
            symbol.Definition.Underlying is H.NamedType based && binder.SymbolOf(based) is { IsStruct: true } && !Same(based, binder.StringType!) ? based : null;

        /// <summary>Whether two values of a type compare with `==` by what they hold: a record, or a type that declares `op ==`.</summary>
        private bool HasEquality(H.HirType type) =>
            RecordOf(type) is not null || binder.SymbolOf(type) is { } symbol && symbol.FindMethods("$op_Equal").Count > 0;

        /// <summary>The integer an enum is at run time: its base, or <c>i32</c> (also for a string-based enum: the places).</summary>
        private static H.HirType EnumNumber(TypeSymbol symbol) => symbol.Definition.Underlying is H.PrimitiveType p ? p : H.HirType.I32;

        /// <summary>
        /// An explicit conversion through an enum's base: to or from the integer (<c>(u16)flags</c>, <c>(Flags)3</c>; any
        /// integer type, through the base; a string-based enum's integer is its members' places), to or from the text of a
        /// string-based enum (<c>(str)word</c>, <c>(Word)"hola"</c>: a text that is no member becomes <c>(Word)-1</c>), or
        /// enum to enum through both bases. Null when none applies.
        /// </summary>
        private H.Expr? EnumConversion(H.Expr value, H.HirType from, H.HirType target)
        {
            var fromEnum = from is H.NamedType && binder.SymbolOf(from) is { IsEnum: true } f ? f : null;
            var toEnum = target is H.NamedType && binder.SymbolOf(target) is { IsEnum: true } t ? t : null;
            if (fromEnum is null && toEnum is null)
                return null;
            if (fromEnum is not null && toEnum is not null)
            {
                var halfway = fromEnum.Definition.Underlying ?? H.HirType.I32;
                return EnumConversion(value, from, halfway) is { } raw ? EnumConversion(raw, halfway, target) : null;
            }
            if (fromEnum is not null)
            {
                // A value-based enum's member as its value: the enum's `$Value`.
                if (ValueBase(fromEnum) is { } based && Same(target, based)
                    && fromEnum.FindMethods(Binder.EnumValueMethod).FirstOrDefault(m => m.IsStatic) is { } valueOf)
                    return new H.CallExpr(null, valueOf.Token, [], [value], target);
                if (fromEnum.Definition.Underlying is not H.PrimitiveType && ValueBase(fromEnum) is null && Same(target, binder.StringType!))
                    return EnumText(value, fromEnum, texts: true);
                if (!IsInteger(target))
                    return null;
                var number = EnumNumber(fromEnum);
                var raw = new H.ConvertExpr(value, H.ConversionKind.Enum, number);
                return Same(number, target) ? raw : new H.ConvertExpr(raw, H.ConversionKind.Numeric, target);
            }
            // A value as the member of a value-based enum that stands for it (`(Color)rgb`; none: `(Color)-1`).
            if (ValueBase(toEnum!) is { } valued && Same(from, valued))
            {
                if (!HasEquality(valued) || toEnum!.FindMethods(Binder.EnumOfMethod).FirstOrDefault(m => m.IsStatic) is not { } of)
                    return null;
                return new H.CallExpr(null, of.Token, [], [value], target);
            }
            if (toEnum!.Definition.Underlying is not H.PrimitiveType && Same(from, binder.StringType!))
            {
                if (binder.StringSymbol is not { } text)
                    return null;
                // `s switch { var v when v == "hola" => Hello, …, _ => (Word)-1 }`.
                int local = Declare($"$text{locals.Count}", from, default);
                var arms = new List<H.SwitchArm>();
                foreach (var field in toEnum.Definition.Fields)
                {
                    if (field.Value is not H.EnumConstant { Text: { } written } constant)
                        continue;
                    var same = Strings(BinaryOperator.Equal, new H.LocalExpr(local, from), new H.LiteralExpr(new H.StringConstant(written), from), text, default);
                    if (same is null)
                        return null;
                    arms.Add(new H.SwitchArm(new H.BindingPattern(local, null), same, new H.LiteralExpr(constant, target)));
                }
                arms.Add(new H.SwitchArm(new H.DiscardPattern(), null, new H.LiteralExpr(new H.EnumConstant(target, System.Numerics.BigInteger.MinusOne), target)));
                return new H.SwitchExpr(value, [.. arms], target);
            }
            if (!IsInteger(from))
                return null;
            var wanted = EnumNumber(toEnum);
            var narrowed = Same(from, wanted) ? value : new H.ConvertExpr(value, H.ConversionKind.Numeric, wanted);
            return new H.ConvertExpr(narrowed, H.ConversionKind.Enum, target);
        }

        private static bool Same(H.HirType a, H.HirType b) => a == b || a.Equals(b);

        /// <summary>
        /// A binary operator a type declares (<c>op +</c>): looked up in the types of both operands. <c>!=</c> without its own
        /// declaration is <c>!(a == b)</c>; null when neither type declares the operator.
        /// </summary>
        /// <summary>
        /// <c>a + b</c> where an operand is a type parameter constrained by the operator (<c>T : op +(T, T) : T</c>,
        /// <c>T : number</c>): the operator itself, resolved for each type argument (built into the primitives, declared
        /// by other types). <c>!=</c> comes from <c>==</c> when only that is required.
        /// </summary>
        private H.Expr? RequiredBinary(BinaryOperator op, H.Expr left, H.Expr right)
        {
            string symbol = Symbol(op);
            foreach (var param in new[] { left.Type, right.Type }.OfType<H.GenericParamType>().Distinct())
            {
                foreach (var required in RequiredOperators(param))
                {
                    if (required.Symbol != symbol || required.Operands.Length != 2)
                        continue;
                    if (Cost(left, required.Operands[0]) is not null && Cost(right, required.Operands[1]) is not null)
                        return new H.BinaryExpr(Hir(op), Convert(left, required.Operands[0], default), Convert(right, required.Operands[1], default), required.Result);
                }
            }
            if (op == BinaryOperator.NotEqual && RequiredBinary(BinaryOperator.Equal, left, right) is { } equal)
                return new H.UnaryExpr(H.UnaryOp.Not, equal, H.HirType.Bool);
            // `a > b` is `b < a` (exact, NaN included; `<=` and `>=` are not `!` of `<` with NaN, so they are not made up).
            if (op == BinaryOperator.Greater && RequiredBinary(BinaryOperator.Less, right, left) is H.BinaryExpr less)
                return less;
            return null;
        }

        /// <summary><c>-a</c>, <c>!a</c>, <c>~a</c> on a type parameter constrained by the operator.</summary>
        private H.Expr? RequiredUnary(string symbol, H.UnaryOp op, H.Expr operand)
        {
            if (operand.Type is not H.GenericParamType param)
                return null;
            foreach (var required in RequiredOperators(param))
            {
                if (required.Symbol == symbol && required.Operands.Length == 1 && Cost(operand, required.Operands[0]) is not null)
                    return new H.UnaryExpr(op, Convert(operand, required.Operands[0], default), required.Result);
            }
            return null;
        }

        /// <summary>The operators a type parameter's constraints give it (see <see cref="Binder.OperatorsOf"/>).</summary>
        private IEnumerable<H.OperatorRequirement> RequiredOperators(H.GenericParamType param)
        {
            var generics = param.Owner == H.GenericOwner.Type ? owner.Definition.Generics : method.Definition.Generics;
            return param.Index < generics.Count ? binder.OperatorsOf(generics[param.Index]) : [];
        }

        /// <summary>The type parameter in scope named <paramref name="name"/> (the method's, then the type's), or null.</summary>
        private H.GenericParamType? GenericParamNamed(string name)
        {
            for (int g = 0; g < method.Definition.Generics.Count; g++)
                if (method.Definition.Generics[g].Name == name)
                    return new H.GenericParamType(H.GenericOwner.Method, g);
            for (int g = 0; g < owner.Definition.Generics.Count; g++)
                if (owner.Definition.Generics[g].Name == name)
                    return new H.GenericParamType(H.GenericOwner.Type, g);
            return null;
        }

        /// <summary>
        /// <c>T.Zero</c>: a static property or method the interfaces constraining <c>T</c> declare, resolved for each type
        /// argument by the type's own member. A call of the interface's accessor or method without a receiver.
        /// </summary>
        private H.Expr RequiredStatic(H.GenericParamType param, string name, Expr[]? arguments, TextSpan span, TextSpan nameSpan)
        {
            var methods = new List<MethodSymbol>();
            var interfaceArguments = new Dictionary<MethodSymbol, H.HirType[]>();
            foreach (var constraint in ConstraintsOf(param).OfType<H.NamedType>())
            {
                foreach (var (_, property, ifaceArguments) in binder.InterfaceProperties(constraint))
                {
                    if (property.IsStatic && property.Name == name && arguments is null && property.Getter is { } getter)
                        return new H.CallExpr(null, getter.Token, ifaceArguments, [], property.Type.Substitute(ifaceArguments));
                }
                foreach (var (_, m, ifaceArguments) in binder.InterfaceMethodsOf(constraint))
                {
                    if (m.IsStatic && m.Name == name && interfaceArguments.TryAdd(m, ifaceArguments))
                        methods.Add(m);
                }
            }
            if (arguments is null)
                return Failed("AN2037", $"`{Show(param)}` has no static member `{name}` (constrain it by an interface that declares one)", nameSpan);
            if (methods.Count == 0)
                return Failed("AN2092", $"`{Show(param)}` has no static method `{name}` (constrain it by an interface that declares one)", nameSpan);
            if (ResolveCall(methods, arguments, span, $"`{name}`", m => interfaceArguments[m], []) is not { } chosen)
                return new H.DefaultExpr(ErrorType);
            return new H.CallExpr(null, chosen.Method.Token, chosen.TypeArguments, chosen.Arguments, chosen.Result);
        }

        /// <summary>
        /// An operator of the program's own (<c>a ^^ b</c>, <c>a!</c>): the method an operand's type declares under its
        /// symbol and form (<c>op</c> without <c>left</c>/<c>right</c> serves both sides).
        /// </summary>
        private H.Expr CustomOperator(OperatorExpr custom)
        {
            var operands = custom.Operands.Select(o => Expression(o, null)).ToArray();
            if (operands.Any(o => IsError(o.Type)))
                return new H.DefaultExpr(ErrorType);
            var form = operands.Length == 2 ? OperatorForm.Binary : custom.Postfix ? OperatorForm.Postfix : OperatorForm.Prefix;
            if (DeclaredOperator(Antril.Syntax.Parser.CustomOperatorName(custom.Symbol, form), operands) is { } call)
                return call;
            if (form == OperatorForm.Postfix && operands[0].Type is H.NamedType named && binder.MemberSymbolOf(named) is { } type)
            {
                // `op ~~(T)` without `left` or `right` serves both sides: the type lists it as postfix too.
                foreach (var declared in type.Definition.Operators)
                {
                    if (declared.Symbol == custom.Symbol && declared.Fixity == H.Fixity.Postfix
                        && DeclaredOperator(type.Definition.Methods[declared.Method].Name, operands) is { } either)
                        return either;
                }
            }
            string where = form == OperatorForm.Postfix ? "right " : form == OperatorForm.Prefix ? "left " : "";
            return Failed("AN2123", $"no type of the operands declares `{where}op {custom.Symbol}` for ({string.Join(", ", operands.Select(o => Show(o.Type)))})", custom.Span);
        }

        /// <summary>
        /// A conversion the program declares (<c>op (i64)(Money m)</c>, in the type of either side), or one a type
        /// parameter's constraint promises (<c>T : (i64)T</c>); null when there is none.
        /// </summary>
        private H.Expr? UserConversion(H.Expr value, H.HirType from, H.HirType target)
        {
            if (from is H.GenericParamType param)
            {
                var generics = param.Owner == H.GenericOwner.Type ? owner.Definition.Generics : method.Definition.Generics;
                if (param.Index < generics.Count && generics[param.Index].ConvertsTo.Any(t => Same(t, target)))
                    return new H.ConvertExpr(value, H.ConversionKind.User, target);
                return null;
            }
            if (target is H.GenericParamType into)
            {
                var generics = into.Owner == H.GenericOwner.Type ? owner.Definition.Generics : method.Definition.Generics;
                if (into.Index < generics.Count && generics[into.Index].ConvertsFrom.Any(t => Same(t, from)))
                    return new H.ConvertExpr(value, H.ConversionKind.User, target);
                return null;
            }
            MethodSymbol? best = null;
            H.HirType[] bestDeclaring = [];
            H.HirType bestParameter = ErrorType;
            int bestCost = int.MaxValue;
            foreach (var side in new[] { from, target })
            {
                if (side is not H.NamedType named || binder.MemberSymbolOf(named) is not { } type)
                    continue;
                foreach (var candidate in type.Methods.Where(kv => kv.Key.StartsWith("$conv_", StringComparison.Ordinal)).SelectMany(kv => kv.Value))
                {
                    if (!candidate.IsStatic || candidate.Parameters.Length != 1)
                        continue;
                    var parameter = candidate.Parameters[0].Substitute(named.Arguments);
                    var result = candidate.Result.Substitute(named.Arguments);
                    H.HirType[] methodArguments = [];
                    if (candidate.GenericCount > 0)
                    {
                        // `op (Pair<U>)<U>(Box<T> b)`: U inferred from the operand and from the target.
                        var inferred = new H.HirType?[candidate.GenericCount];
                        Infer(parameter, from, inferred);
                        Infer(result, target, inferred);
                        if (inferred.Any(i => i is null))
                            continue;
                        methodArguments = [.. inferred.Select(i => i!)];
                        parameter = parameter.Substitute([], methodArguments);
                        result = result.Substitute([], methodArguments);
                    }
                    if (!Same(result, target) || Cost(value, parameter) is not { } cost || cost >= bestCost && !(cost == bestCost && best!.GenericCount > 0 && candidate.GenericCount == 0))
                        continue;
                    (best, bestCost, bestDeclaring, bestParameter) = (candidate, cost, [.. named.Arguments, .. methodArguments], parameter);
                }
            }
            if (best is null)
                return null;
            return new H.CallExpr(null, best.Token, bestDeclaring, [Convert(value, bestParameter, default)], target);
        }

        /// <summary>
        /// <c>I&lt;X&gt;.Member</c> on an interface with its arguments written: a static member with a body is the interface's
        /// own; an abstract one is implemented by a type argument, which must implement the interface (a type parameter does
        /// through its constraints). The error when none does, else null.
        /// </summary>
        private H.Expr? ConcreteInterface(TypeSymbol iface, H.HirType[] arguments, bool isAbstract, string name, TextSpan span)
        {
            if (!isAbstract || arguments.Any(IsError))
                return null;
            var instance = (H.NamedType)iface.Type.Substitute(arguments);
            if (arguments.Any(a => binder.Implements(a, instance, ConstraintsOf)))
                return null;
            return Failed("AN2128", $"`{name}` is abstract: a type argument of `{Show(instance)}` implements it, and none of these does", span);
        }

        /// <summary>
        /// The array type an operator of <paramref name="other"/> takes as its other operand (the first found), for an
        /// array literal written there; null when none does.
        /// </summary>
        private H.HirType? ArrayOperand(BinaryOperator op, H.HirType other, bool literalOnRight)
        {
            if (other is not H.NamedType named || binder.MemberSymbolOf(named) is not { } type || IsError(other))
                return null;
            string? name = Antril.Syntax.Parser.OperatorMethodName(Symbol(op), 2);
            if (name is null)
                return null;
            string[] names = op == BinaryOperator.NotEqual ? [name, "$op_Equal"] : [name];
            foreach (string candidateName in names)
            {
                foreach (var candidate in type.FindMethods(candidateName))
                {
                    if (!candidate.IsStatic || candidate.Parameters.Length != 2 || candidate.GenericCount > 0)
                        continue;
                    if (candidate.Parameters[literalOnRight ? 1 : 0].Substitute(named.Arguments) is H.ArrayType array)
                        return array;
                }
            }
            return null;
        }

        private H.Expr? DeclaredBinary(BinaryOperator op, H.Expr left, H.Expr right)
        {
            string? name = Antril.Syntax.Parser.OperatorMethodName(Symbol(op), 2);
            if (name is null)
                return null;
            if (DeclaredOperator(name, [left, right]) is { } call)
                return call;
            if (op == BinaryOperator.NotEqual && DeclaredOperator("$op_Equal", [left, right]) is { } equal)
                return new H.UnaryExpr(H.UnaryOp.Not, equal, H.HirType.Bool);
            return null;
        }

        /// <summary>
        /// The cheapest overload of operator method <paramref name="name"/> among the operands' types (the arguments
        /// converted), or null when none takes them.
        /// </summary>
        private H.Expr? DeclaredOperator(string name, H.Expr[] operands, TextSpan span = default)
        {
            MethodSymbol? best = null;
            H.HirType[] bestDeclaring = [];
            H.HirType[] bestMethodArguments = [];
            H.HirType[] bestParameters = [];
            int bestCost = int.MaxValue;
            TypeSymbol? searched = null;
            foreach (var operand in operands)
            {
                if (operand.Type is not H.NamedType named || binder.MemberSymbolOf(named) is not { } type || type == searched)
                    continue;
                searched = type;
                foreach (var candidate in type.FindMethods(name))
                {
                    if (!candidate.IsStatic || candidate.Parameters.Length != operands.Length)
                        continue;
                    var declaring = named.Arguments;
                    var parameters = candidate.Parameters.Select(p => p.Substitute(declaring)).ToArray();
                    H.HirType[] methodArguments = [];
                    if (candidate.GenericCount > 0)
                    {
                        // `op +<T : number>(V<T> a, V<T> b)`: T inferred from the operands.
                        var inferred = new H.HirType?[candidate.GenericCount];
                        for (int i = 0; i < operands.Length; i++)
                            Infer(parameters[i], operands[i].Type, inferred);
                        if (inferred.Any(i => i is null))
                            continue;
                        methodArguments = [.. inferred.Select(i => i!)];
                        parameters = [.. parameters.Select(p => p.Substitute([], methodArguments))];
                    }
                    int cost = 0;
                    bool fits = true;
                    for (int i = 0; i < operands.Length && fits; i++)
                    {
                        if (Cost(operands[i], parameters[i]) is { } c)
                            cost += c;
                        else
                            fits = false;
                    }
                    // As good as the best so far: an operator that is not generic wins over a generic one.
                    if (fits && (cost < bestCost || cost == bestCost && bestMethodArguments.Length > 0 && methodArguments.Length == 0))
                    {
                        best = candidate;
                        bestCost = cost;
                        bestDeclaring = declaring;
                        bestMethodArguments = methodArguments;
                        bestParameters = parameters;
                    }
                }
            }
            if (best is null)
                return null;
            if (bestMethodArguments.Length > 0)
                CheckConstraints(best.Definition.Generics, bestMethodArguments, bestDeclaring, bestMethodArguments, span);
            return new H.CallExpr(null, best.Token, [.. bestDeclaring, .. bestMethodArguments],
                [.. operands.Select((a, i) => Convert(a, bestParameters[i], default))], best.Result.Substitute(bestDeclaring, bestMethodArguments));
        }

        /// <summary>
        /// Operators on strings: <c>+</c> joins (<c>String.Concat</c>; a value that is not a string becomes one with its
        /// <c>ToString()</c>), <c>==</c> and <c>!=</c> compare the text (<c>Equals</c>). Null otherwise.
        /// </summary>
        private H.Expr? Strings(BinaryOperator op, H.Expr left, H.Expr right, TypeSymbol text, TextSpan span)
        {
            var type = binder.StringType!;
            if (op == BinaryOperator.Add)
            {
                H.Expr AsText(H.Expr value)
                {
                    if (value.Type == type)
                        return value;
                    if (binder.MemberSymbolOf(value.Type) is { } symbol && BoundCall(value, symbol, "ToString", []) is { } converted && converted.Type == type)
                        return converted;
                    return Failed("AN2097", $"a `{Show(value.Type)}` has no `ToString()` to join to a string", span);
                }
                var a = AsText(left);
                var b = AsText(right);
                return BoundCall(null, text, "Concat", [a, b], type is H.NamedType named ? named.Arguments : []) ?? Failed("AN2098", "`+` on strings needs `String.Concat(str, str)`", span);
            }
            if (op is BinaryOperator.Equal or BinaryOperator.NotEqual && left.Type == type && right.Type == type)
            {
                if (BoundCall(left, text, "Equals", [right]) is not { } equal)
                    return Failed("AN2098", "`==` on strings needs `String.Equals(str)`", span);
                return op == BinaryOperator.Equal ? equal : new H.UnaryExpr(H.UnaryOp.Not, equal, H.HirType.Bool);
            }
            if (op is BinaryOperator.Equal or BinaryOperator.NotEqual && Unwrapped(left) == type && Unwrapped(right) == type)
            {
                // `str? == str`: null equals only null; two strings compare by content (each side read once).
                int l = Declare($"$left{locals.Count}", left.Type, span), r = Declare($"$right{locals.Count}", right.Type, span);
                var statements = new H.Stmt[]
                {
                    new H.ExprStmt(new H.AssignExpr(new H.LocalExpr(l, left.Type), left, left.Type)),
                    new H.ExprStmt(new H.AssignExpr(new H.LocalExpr(r, right.Type), right, right.Type)),
                };
                H.Expr Read(int local, H.HirType held) => held is H.NullableType ? new H.ConvertExpr(new H.LocalExpr(local, held), H.ConversionKind.Unwrap, type) : new H.LocalExpr(local, held);
                H.Expr IsNull(int local, H.HirType held) => new H.BinaryExpr(H.BinaryOp.Equal, new H.LocalExpr(local, held), new H.LiteralExpr(new H.NullConstant(), held), H.HirType.Bool);
                if (BoundCall(Read(l, left.Type), text, "Equals", [Read(r, right.Type)]) is not { } same)
                    return Failed("AN2098", "`==` on strings needs `String.Equals(str)`", span);
                H.Expr result = same;
                if (right.Type is H.NullableType)
                    result = new H.BinaryExpr(H.BinaryOp.And, new H.UnaryExpr(H.UnaryOp.Not, IsNull(r, right.Type), H.HirType.Bool), result, H.HirType.Bool);
                if (left.Type is H.NullableType)
                    result = new H.ConditionalExpr(IsNull(l, left.Type), right.Type is H.NullableType ? IsNull(r, right.Type) : new H.LiteralExpr(new H.BoolConstant(false), H.HirType.Bool), result, H.HirType.Bool);
                var equality = new H.BlockExpr(statements, result, H.HirType.Bool);
                return op == BinaryOperator.Equal ? equality : new H.UnaryExpr(H.UnaryOp.Not, equality, H.HirType.Bool);
            }
            return null;
        }

        private static H.HirType Unwrapped(H.Expr e) => e.Type is H.NullableType n ? n.Inner : e.Type;

        private H.Expr Coalesce(BinaryExpr binary, H.HirType? expected)
        {
            var left = Expression(binary.Left, expected is null ? null : new H.NullableType(expected is H.NullableType n ? n.Inner : expected));
            if (IsError(left.Type))
                return left;
            if (left.Type is not H.NullableType nullable)
                return Failed("AN2079", $"`??` needs a value that may be null on its left, not a `{Show(left.Type)}`", binary.Left.Span);
            var right = Expression(binary.Right, expected ?? nullable.Inner);
            if (IsError(right.Type))
                return right;
            if (right.Type is H.NullableType || right is H.LiteralExpr { Value: H.NullConstant })
                return new H.CoalesceExpr(left, Convert(right, left.Type, binary.Right.Span), left.Type);
            return new H.CoalesceExpr(left, Convert(right, nullable.Inner, binary.Right.Span), nullable.Inner);
        }

        /// <summary>Two values brought to one type: the one the other converts to implicitly (literals take the other's type).</summary>
        private (H.Expr A, H.Expr B, H.HirType Type) Unify(H.Expr a, H.Expr b, H.HirType? expected, TextSpan span)
        {
            if (IsError(a.Type) || IsError(b.Type))
                return (a, b, ErrorType);
            if (expected is not null && Cost(a, expected) is not null && Cost(b, expected) is not null && !Literal(a) | !Literal(b))
                return (Convert(a, expected, span), Convert(b, expected, span), expected);
            if (a.Type == b.Type)
                return (a, b, a.Type);
            if (b is H.LiteralExpr { Value: H.NullConstant } && a.Type is H.NullableType)
                return (a, new H.LiteralExpr(new H.NullConstant(), a.Type), a.Type);
            if (a is H.LiteralExpr { Value: H.NullConstant } && b.Type is H.NullableType)
                return (new H.LiteralExpr(new H.NullConstant(), b.Type), b, b.Type);
            if (Cost(a, b.Type) is not null && (Cost(b, a.Type) is null || Literal(a)))
                return (Convert(a, b.Type, span), b, b.Type);
            if (Cost(b, a.Type) is not null)
                return (a, Convert(b, a.Type, span), a.Type);
            // Two classes: their closest common base.
            if (binder.SymbolOf(a.Type) is { IsClass: true } ca && binder.SymbolOf(b.Type) is { IsClass: true } cb)
            {
                for (var t = ca; t is not null; t = t.Base)
                {
                    if (cb.IsOrDerives(t))
                    {
                        var common = a.Type is H.NullableType || b.Type is H.NullableType ? new H.NullableType(t.Type) : t.Type;
                        return (Convert(a, common, span), Convert(b, common, span), common);
                    }
                }
            }
            Error("AN2080", $"`{Show(a.Type)}` and `{Show(b.Type)}` have no type in common", span);
            return (a, b, ErrorType);
        }

        // ── Conversions ────────────────────────────────────

        /// <summary>
        /// What converting <paramref name="value"/> to <paramref name="target"/> implicitly costs: 0 when it is already one,
        /// 1 for a widening (numbers, a class to its base, a value to its nullable type, a literal that fits); null when it
        /// does not convert implicitly.
        /// </summary>
        /// <summary>What a 128-bit (3) or 256-bit (5) integer adds to a conversion's cost, so narrower targets come first.</summary>
        private static int WideCost(H.Primitive kind) => kind.Bits() switch { 128 => 3, 256 => 5, _ => 0 };

        /// <summary>A float literal's distance from f64: f32, then f16, then the wide floats.</summary>
        private static int FloatCost(H.Primitive kind) => kind.Bits() switch { 64 => 0, 32 => 1, 16 => 2, 128 => 3, _ => 5 };

        private int? Cost(H.Expr value, H.HirType target)
        {
            var from = value.Type;
            if (IsError(from) || IsError(target))
                return 0;
            if (from == target)
                return 0;
            if (value is H.LiteralExpr { Value: H.NullConstant })
                return target is H.NullableType or H.PointerType ? 1 : null;
            // Any pointer is a `void*`.
            if (from is H.PointerType && target is H.PointerType { Target: var pointee } && pointee == H.HirType.Void)
                return 1;
            // An integer type parameter: literals that fit all its types; to a number all its types widen to.
            if (NumericParam(target) is { } targetKinds)
            {
                if (value is H.LiteralExpr { Value: H.IntegerConstant literal })
                    return targetKinds.Where(k => k.IsInteger()).All(k => Binder.Fits(literal.Value, k)) ? 1 : null;
                if (value is H.LiteralExpr { Value: H.FloatConstant })
                    return targetKinds.All(k => k.IsFloat()) ? 1 : null;
                // A primitive every one of its types holds (`T : float` takes an f32).
                return from is H.PrimitiveType { Kind: var narrow } && targetKinds.All(k => Widens(narrow, k)) ? 2 : null;
            }
            // A type parameter's value to `any`: boxed the way its type is once known (an object stays itself).
            if (from is H.GenericParamType && (IsAny(target) || target is H.NullableType { Inner: var anyInner } && IsAny(anyInner)))
                return 2;
            if (Opaque(from) && (IsAny(target) || target is H.NullableType { Inner: var opaqueInner } && IsAny(opaqueInner)) && BoxedSymbol() is not null)
                return 3;
            if (NumericParam(from) is { } fromKinds)
                return target is H.PrimitiveType { Kind: var wide } && fromKinds.All(k => Widens(k, wide)) ? 1 : null;
            if (value is H.LiteralExpr { Value: H.IntegerConstant integer } && target is H.PrimitiveType { Kind: var tk })
                // A literal prefers i32, then a wider signed integer, then an unsigned one, then a float (`Max(3, 9)` takes the
                // i32 overload, `WriteLine(42)` the i64 one when there is no i32).
                // (a 128- or 256-bit integer comes after the floats: `F(5)` with F(i64) and F(i128) takes the i64 one).
                return tk.IsInteger() ? Binder.Fits(integer.Value, tk) ? tk == H.Primitive.I32 ? 1 : (tk.IsSigned() ? 2 : 3) + WideCost(tk) : null : tk.IsFloat() ? 4 + FloatCost(tk) : null;
            if (from is H.PrimitiveType { Kind: var fk } && target is H.PrimitiveType { Kind: var k })
            {
                if (fk.IsInteger() && k.IsInteger())
                {
                    // The nearest width wins: a u32 goes to u64 before u128 (and to f64 before a wide integer).
                    if (fk.IsSigned() == k.IsSigned())
                        return k.Bits() > fk.Bits() ? 1 + WideCost(k) : null;
                    return !fk.IsSigned() && k.IsSigned() && k.Bits() > fk.Bits() ? 1 + WideCost(k) : null;
                }
                if (fk.IsInteger() && k.IsFloat())
                    return fk.Bits() > 64 ? null : 2; // a wide integer loses digits in a float: only with a cast
                if (fk.IsFloat() && k.IsFloat())
                    return k.Bits() > fk.Bits() ? 1 + WideCost(k) : null;
                return null;
            }
            // A value to a nullable of a wider value (`i64? x = 3`): widened, then wrapped.
            if (target is H.NullableType { Inner: var valueInner } && from is not H.NullableType && valueInner != from && !IsReference(valueInner))
                return Cost(value, valueInner) is int widening ? widening + 1 : null;
            var fromInner = from is H.NullableType fn ? fn.Inner : from;
            var targetInner = target is H.NullableType tn ? tn.Inner : target;
            if (from is H.NullableType && target is not H.NullableType)
                return null;
            if (binder.SymbolOf(fromInner) is { IsClass: true } fc && binder.SymbolOf(targetInner) is { IsClass: true } tc && fc.IsOrDerives(tc))
            {
                int depth = 0;
                for (var t = fc; t != tc; t = t!.Base)
                    depth++;
                return depth + (from is H.NullableType == target is H.NullableType ? 0 : 1);
            }
            if (target is H.NullableType && fromInner == targetInner)
                return 1;
            int wrap = from is H.NullableType == target is H.NullableType ? 0 : 1;
            // An object to an interface it implements, or an interface to `any`: the same handle.
            if (IsInterface(targetInner) && IsReference(fromInner) && binder.Implements(fromInner, (H.NamedType)targetInner, ConstraintsOf))
                return 1 + wrap;
            if (IsAny(targetInner) && IsInterface(fromInner))
                return 1 + wrap;
            // A value to an interface it implements: boxed; to `any`: boxed, after every numeric widening (`WriteLine(42)`
            // takes the i64 overload, `WriteLine(point)` the `any` one).
            if (IsValueType(fromInner) && from is not H.NullableType && IsInterface(targetInner) && binder.Implements(fromInner, (H.NamedType)targetInner, ConstraintsOf))
                return 2 + wrap;
            if (IsValueType(fromInner) && from is not H.NullableType && IsAny(targetInner))
                return 9 + wrap;
            return null;
        }

        private H.Expr Convert(H.Expr value, H.HirType target, TextSpan span, bool explicitly = false)
        {
            var from = value.Type;
            if (IsError(from) || IsError(target) || from == target)
                return IsError(target) || from == target ? value : new H.DefaultExpr(target);
            if (value is H.LiteralExpr { Value: H.NullConstant })
            {
                if (target is H.NullableType)
                    return new H.LiteralExpr(new H.NullConstant(), target);
                if (target is H.PointerType)
                    return NullPointer(target);
                return Failed("AN2081", $"a `{Show(target)}` cannot be null (a nullable `{Show(target)}?` can)", span);
            }
            if (from is H.PointerType && target is H.PointerType { Target: var voidTarget } && voidTarget == H.HirType.Void)
                return new H.ConvertExpr(value, H.ConversionKind.Pointer, target);
            if (value is H.LiteralExpr { Value: H.IntegerConstant unitLiteral } && ParamLiteral(unitLiteral.Value, target) is { } unit)
                return unit;
            if (value is H.LiteralExpr { Value: H.FloatConstant } && NumericParam(target) is { } floats && floats.All(k => k.IsFloat()))
                return new H.ConvertExpr(value, H.ConversionKind.Numeric, target);
            if (value is H.LiteralExpr { Value: H.IntegerConstant integer } && target is H.PrimitiveType { Kind: var lk })
            {
                if (lk.IsInteger() && Binder.Fits(integer.Value, lk))
                    return new H.LiteralExpr(new H.IntegerConstant(integer.Value, lk), target);
                if (lk.IsFloat())
                    return new H.LiteralExpr(FloatOf(integer.Value, lk), target);
            }
            if (value is H.LiteralExpr { Value: H.FloatConstant real } && target is H.PrimitiveType { Kind: var realKind } && realKind.IsFloat()
                && from is H.PrimitiveType { Kind: H.Primitive.F64 } && realKind != H.Primitive.F64)
                return new H.LiteralExpr(new H.FloatConstant(SoftFloat.Convert(real.Bits, FloatFormat.Double, FloatFormat.Of(realKind.Bits())), realKind), target);
            if (target is H.NullableType { Inner: var valueInner } && from is not H.NullableType && valueInner != from && !IsReference(valueInner)
                && Cost(value, valueInner) is not null)
                return new H.ConvertExpr(Convert(value, valueInner, span), H.ConversionKind.Wrap, target);
            if (from is H.GenericParamType && (IsAny(target) || target is H.NullableType { Inner: var anyTarget } && IsAny(anyTarget)))
                return new H.ConvertExpr(value, H.ConversionKind.Box, target);
            // A function value or a pointer as an `any`: in a System.Boxed (neither is an object of its own).
            if (Opaque(from) && (IsAny(target) || target is H.NullableType { Inner: var anyOpaque } && IsAny(anyOpaque)) && Boxed(value) is { } held)
            {
                var asAny = new H.ConvertExpr(held, H.ConversionKind.Upcast, binder.ObjectSymbol!.Type);
                return target is H.NullableType ? new H.ConvertExpr(asAny, H.ConversionKind.Wrap, target) : asAny;
            }
            if (Cost(value, target) is not null)
            {
                if (from is H.PrimitiveType && target is H.PrimitiveType || NumericParam(from) is not null || NumericParam(target) is not null)
                    return new H.ConvertExpr(value, H.ConversionKind.Numeric, target);
                if (IsValueType(from) && (target is H.NullableType { Inner: var boxedInner } ? IsReference(boxedInner) : IsReference(target)))
                {
                    var boxedType = target is H.NullableType { Inner: var inner } ? inner : target;
                    var boxed = new H.ConvertExpr(value, H.ConversionKind.Box, boxedType);
                    return boxedType == target ? boxed : new H.ConvertExpr(boxed, H.ConversionKind.Wrap, target);
                }
                if (target is H.NullableType && from is not H.NullableType)
                    return new H.ConvertExpr(value, H.ConversionKind.Wrap, target);
                return new H.ConvertExpr(value, H.ConversionKind.Upcast, target);
            }
            if (explicitly)
            {
                // `(T)u` between two number parameters, `(i64)t`, `(T)n`: the parameter promises the conversion (a requirement
                // met per type argument, built into the numbers).
                if (from is H.GenericParamType fromParam && NumberParam(fromParam) && from != target
                    && (target is H.GenericParamType toParam && NumberParam(toParam) || target is H.PrimitiveType { Kind: not (H.Primitive.Bool or H.Primitive.Void or H.Primitive.Char) }))
                {
                    PromiseConversion(fromParam, target);
                    return new H.ConvertExpr(value, H.ConversionKind.User, target);
                }
                if (target is H.GenericParamType intoParam && NumberParam(intoParam) && from is H.PrimitiveType { Kind: not (H.Primitive.Bool or H.Primitive.Void or H.Primitive.Char) })
                {
                    PromiseConversionFrom(intoParam, from);
                    return new H.ConvertExpr(value, H.ConversionKind.User, target);
                }
                if (from is H.PrimitiveType { Kind: not H.Primitive.Bool } && target is H.PrimitiveType { Kind: not H.Primitive.Bool }
                    || IsNumeric(from) && IsNumeric(target) && (NumericParam(from) is not null || NumericParam(target) is not null))
                    return new H.ConvertExpr(value, H.ConversionKind.Numeric, target);
                if (EnumConversion(value, from, target) is { } throughEnum)
                    return throughEnum;
                var fromInner = from is H.NullableType fn ? fn.Inner : from;
                var targetInner = target is H.NullableType tn ? tn.Inner : target;
                // An `any` back to the function value or pointer it holds: `(func<i32> : i32)boxed` (checked, as any downcast).
                if (IsAny(fromInner) && Opaque(target) && BoxedSymbol() is { } boxes)
                {
                    var unwrapped = from is H.NullableType ? new H.ConvertExpr(value, H.ConversionKind.Unwrap, fromInner) : value;
                    var boxType = new H.NamedType(boxes.Token, [target]);
                    var box = new H.ConvertExpr(unwrapped, H.ConversionKind.Downcast, boxType);
                    var field = boxes.FindField("Value")!;
                    return new H.FieldExpr(box, field.Token, target, [target]);
                }
                // From `any` or an interface: to another object type (checked), or to a value (unboxed, checked).
                if ((IsInterface(fromInner) || IsAny(fromInner)) && target is H.GenericParamType)
                    return new H.ConvertExpr(value, H.ConversionKind.Unbox, target);
                if ((IsInterface(fromInner) || IsAny(fromInner)) && (IsReference(targetInner) || IsValueType(target)) && target is not H.NullableType)
                {
                    var unwrapped = from is H.NullableType ? new H.ConvertExpr(value, H.ConversionKind.Unwrap, fromInner) : value;
                    return new H.ConvertExpr(unwrapped, IsValueType(target) ? H.ConversionKind.Unbox : H.ConversionKind.Downcast, target);
                }
                if (IsReference(fromInner) && IsInterface(targetInner) && target is not H.NullableType)
                {
                    var unwrapped = from is H.NullableType ? new H.ConvertExpr(value, H.ConversionKind.Unwrap, fromInner) : value;
                    return new H.ConvertExpr(unwrapped, H.ConversionKind.Downcast, target);
                }
                if (binder.SymbolOf(fromInner) is { IsClass: true } fc && binder.SymbolOf(targetInner) is { IsClass: true } tc && tc.IsOrDerives(fc))
                {
                    var unwrapped = from is H.NullableType && target is not H.NullableType ? new H.ConvertExpr(value, H.ConversionKind.Unwrap, fromInner) : value;
                    if (target is H.NullableType)
                        return Failed("AN2082", "a downcast gives a non-null object: cast to `T`, or test with `as`", span);
                    return fc == tc ? unwrapped : new H.ConvertExpr(unwrapped, H.ConversionKind.Downcast, target);
                }
                if (from is H.NullableType n && n.Inner == target)
                    return new H.ConvertExpr(value, H.ConversionKind.Unwrap, target);
                if (UserConversion(value, from, target) is { } declared)
                    return declared;
                if (from is H.PointerType && (target is H.PointerType || IsInteger(target)) || IsInteger(from) && target is H.PointerType)
                {
                    if (unsafeDepth == 0)
                        return Failed("AN2083", "pointer casts go in unsafe code", span);
                    return new H.ConvertExpr(value, H.ConversionKind.Pointer, target);
                }
            }
            return Failed("AN2084", explicitly
                ? $"a `{Show(from)}` cannot be converted to `{Show(target)}`"
                : $"a `{Show(from)}` is not a `{Show(target)}`{(Cost(value, target) is null && from is H.PrimitiveType && target is H.PrimitiveType ? " (cast it: it may lose information)" : "")}", span);
        }

        // ── Switch and patterns ────────────────────────────

        private H.Expr Switch(SwitchExpr s, H.HirType? expected)
        {
            var value = Expression(s.Value, null);
            var arms = new List<(H.Pattern Pattern, H.Expr? Guard, H.Expr Value, TextSpan Span)>();
            foreach (var arm in s.Arms)
            {
                Push(arm.Span);
                var pattern = Pattern(arm.Pattern, value.Type);
                var guard = arm.Guard is { } g ? Condition(g) : null;
                var result = Expression((Expr)arm.Body, expected ?? arms.FirstOrDefault().Value?.Type);
                Pop();
                arms.Add((pattern, guard, result, arm.Body.Span));
            }
            if (arms.Count == 0)
                return Failed("AN2085", "a switch expression needs an arm", s.Span);
            var type = expected ?? arms.Select(a => a.Value.Type).FirstOrDefault(t => !Literal(arms.First(a => a.Value.Type == t).Value)) ?? arms[0].Value.Type;
            foreach (var arm in arms)
            {
                if (Cost(arm.Value, type) is null && !IsError(arm.Value.Type))
                {
                    var (_, _, common) = Unify(arms[0].Value, arm.Value, null, arm.Span);
                    if (!IsError(common))
                        type = common;
                }
            }
            return new H.SwitchExpr(value, [.. arms.Select(a => new H.SwitchArm(a.Pattern, a.Guard, Convert(a.Value, type, a.Span)))], type);
        }

        private H.Pattern Pattern(Pattern pattern, H.HirType type)
        {
            switch (pattern)
            {
                case DiscardPattern:
                    return new H.DiscardPattern();
                case BindingPattern binding:
                    return new H.BindingPattern(Declare(binding.Name, type, binding.Span), null);
                case TypePattern typed:
                {
                    var target = Resolve(typed.Type) ?? ErrorType;
                    var test = TypeTest(target, type, typed.Span);
                    if (typed.Name is null)
                        return test;
                    var bound = target is H.NullableType n ? n.Inner : target;
                    return new H.BindingPattern(Declare(typed.Name, bound, typed.Span), test is H.DiscardPattern ? null : test);
                }
                case PositionalPattern positional:
                    return Positional(positional, type);
                case PropertyPattern property:
                    return Property(property, type);
                case ConstantPattern { Value: NullLiteral } constant:
                    if (type is not H.NullableType && !IsError(type))
                        Error("AN2075", $"a `{Show(type)}` is never null", constant.Span);
                    return new H.ConstantPattern(new H.NullConstant());
                case ConstantPattern constant:
                {
                    if (constant.Value is NameExpr or MemberExpr && StaticTypeOnly(constant.Value) is { } named)
                        return TypeTest(named.Type, type, constant.Span);
                    return new H.ConstantPattern(PatternConstant(constant.Value, type));
                }
                case RelationalPattern relational:
                {
                    if (!IsNumeric(type) && !IsError(type) && binder.SymbolOf(type) is not { IsEnum: true })
                        Error("AN2086", $"`{Symbol(relational.Operator)}` compares numbers, not a `{Show(type)}`", relational.Span);
                    return new H.RelationalPattern(Hir(relational.Operator), PatternConstant(relational.Value, type));
                }
                case RangePattern range:
                    return new H.RangePattern(PatternConstant(range.Low, type), PatternConstant(range.High, type), range.Inclusive);
                case AndPattern and:
                    return new H.AndPattern(Pattern(and.Left, type), Pattern(and.Right, type));
                case OrPattern or:
                    return new H.OrPattern(Pattern(or.Left, type), Pattern(or.Right, type));
                case NotPattern not:
                    return new H.NotPattern(Pattern(not.Operand, type));
            }
            Error("AN2087", "this pattern comes in a later phase of the new compiler", pattern.Span);
            return new H.DiscardPattern();
        }

        // ── Records: `with`, `==`, positions ────────────────

        /// <summary>The body of a record's written-for-it member: `ToString()` lists the fields, `Hash()` mixes theirs, `Equals(T)` is `==`.</summary>
        private IEnumerable<H.Stmt> SyntheticStatements(SyntheticBody kind)
        {
            var self = new H.ThisExpr(owner.Type);
            var span = method.NameSpan;
            var fields = InstanceFields(owner, owner.Type).ToArray();
            var u64 = H.HirType.Of(H.Primitive.U64);
            switch (kind)
            {
                case SyntheticBody.Equals:
                    yield return new H.ReturnStmt(RecordEquality(BinaryOperator.Equal, self, new H.ParamExpr(0, owner.Type), owner, span));
                    break;
                case SyntheticBody.EnumText:
                    // The member's name (a string-based enum's text is `(str)word`), as `word.ToString()` gives it.
                    if (EnumText(self, owner) is { } shown)
                        yield return new H.ReturnStmt(shown);
                    break;
                case SyntheticBody.EnumValue or SyntheticBody.EnumOf:
                {
                    // `member switch { Red => new Rgb(255, 0, 0), … }`, and `value switch { var v when v == new Rgb(…) => Red, … }`.
                    var based = owner.Definition.Underlying!;
                    var arms = new List<H.SwitchArm>();
                    bool valueOf = kind == SyntheticBody.EnumValue;
                    bool comparable = HasEquality(based);
                    int candidate = valueOf ? -1 : Declare($"$value{locals.Count}", based, span);
                    H.Expr? last = null;
                    foreach (var (name, written) in owner.EnumValues ?? [])
                    {
                        if (owner.Fields.GetValueOrDefault(name)?.Value is not H.EnumConstant member)
                            continue;
                        var bound = Expression(written, based);
                        if (!IsError(bound.Type))
                            bound = Convert(bound, based, written.Span);
                        if (valueOf)
                        {
                            arms.Add(new H.SwitchArm(new H.ConstantPattern(member), null, bound));
                            last = bound;
                        }
                        else if (comparable)
                        {
                            var same = MembersEqual(new H.LocalExpr(candidate, based), bound, written.Span);
                            arms.Add(new H.SwitchArm(new H.BindingPattern(candidate, null), same, new H.LiteralExpr(member, owner.Type)));
                        }
                    }
                    // Past the members (`(Color)-1`): the base's zero value; no member stands for another value.
                    H.Expr otherwise = valueOf
                        ? Zeroable(based) || last is null ? new H.DefaultExpr(based) : last
                        : new H.LiteralExpr(new H.EnumConstant(owner.Type, System.Numerics.BigInteger.MinusOne), owner.Type);
                    arms.Add(new H.SwitchArm(new H.DiscardPattern(), null, otherwise));
                    yield return new H.ReturnStmt(new H.SwitchExpr(new H.ParamExpr(0, valueOf ? owner.Type : based), [.. arms], valueOf ? based : owner.Type));
                    break;
                }
                case SyntheticBody.Hash:
                {
                    int h = Declare($"$hash{locals.Count}", u64, span);
                    yield return new H.ExprStmt(new H.AssignExpr(new H.LocalExpr(h, u64), new H.LiteralExpr(new H.IntegerConstant(17, H.Primitive.U64), u64), u64));
                    foreach (var (field, type) in fields)
                    {
                        var value = new H.FieldExpr(self, field.Token, type);
                        var hash = MemberHash(value);
                        if (hash is null)
                        {
                            Error("AN2138", $"`{field.Name}` (a `{Show(type)}`) has no `Hash()`: write `Hash()` for `{owner.Name}` yourself", span);
                            continue;
                        }
                        var scaled = new H.BinaryExpr(H.BinaryOp.Multiply, new H.LocalExpr(h, u64), new H.LiteralExpr(new H.IntegerConstant(31, H.Primitive.U64), u64), u64);
                        var mixed = new H.CheckedExpr(new H.BinaryExpr(H.BinaryOp.Add, scaled, hash, u64), false, u64);
                        yield return new H.ExprStmt(new H.AssignExpr(new H.LocalExpr(h, u64), mixed, u64));
                    }
                    yield return new H.ReturnStmt(new H.LocalExpr(h, u64));
                    break;
                }
                case SyntheticBody.ToString:
                {
                    var text = binder.StringSymbol!;
                    var str = binder.StringType!;
                    H.Expr Text(string value) => new H.LiteralExpr(new H.StringConstant(value), str);
                    // A tuple shows its items only: `(1, a)`.
                    bool tuple = owner.FullName is "System.ValueTuple" or "System.Tuple";
                    H.Expr acc = Text(tuple ? "(" : $"{owner.Name} {{ ");
                    for (int f = 0; f < fields.Length; f++)
                    {
                        var (field, type) = fields[f];
                        acc = Strings(BinaryOperator.Add, acc, Text(tuple ? (f > 0 ? ", " : "") : $"{(f > 0 ? ", " : "")}{field.Name} = "), text, span) ?? acc;
                        var piece = MemberText(new H.FieldExpr(self, field.Token, type));
                        if (piece is null)
                        {
                            Error("AN2097", $"`{field.Name}` (a `{Show(type)}`) has no `ToString()`: write `ToString()` for `{owner.Name}` yourself", span);
                            break;
                        }
                        acc = Strings(BinaryOperator.Add, acc, piece, text, span) ?? acc;
                    }
                    acc = Strings(BinaryOperator.Add, acc, Text(tuple ? ")" : fields.Length > 0 ? " }" : "}"), text, span) ?? acc;
                    yield return new H.ReturnStmt(acc);
                    break;
                }
            }
        }

        /// <summary>The record (a `record class` or `record value`) a type is, or null (a nullable record counts).</summary>
        private TypeSymbol? RecordOf(H.HirType type)
        {
            var inner = type is H.NullableType n ? n.Inner : type;
            return inner is H.NamedType && binder.SymbolOf(inner) is { } symbol && (symbol.Definition.Flags & H.TypeFlags.Record) != 0 ? symbol : null;
        }

        /// <summary>A type's instance fields in declaration order, typed for <paramref name="type"/>'s arguments.</summary>
        private static IEnumerable<(FieldSymbol Field, H.HirType Type)> InstanceFields(TypeSymbol symbol, H.HirType type)
        {
            var arguments = type is H.NamedType named ? named.Arguments : [];
            foreach (var definition in symbol.Definition.Fields)
            {
                if ((definition.Flags & (H.FieldFlags.Static | H.FieldFlags.Const)) != 0)
                    continue;
                if (symbol.Fields.TryGetValue(definition.Name, out var field))
                    yield return (field, field.Type.Substitute(arguments));
            }
        }

        /// <summary><c>x with { A = v }</c>: a copy of the record with those fields replaced.</summary>
        private H.Expr With(WithExpr w, H.HirType? expected)
        {
            var value = Expression(w.Value, expected);
            if (IsError(value.Type))
                return new H.DefaultExpr(ErrorType);
            if (value.Type is H.NullableType || RecordOf(value.Type) is not { } record)
                return Failed("AN2132", $"`with` copies a record (a `record class` or `record value`), not a `{Show(value.Type)}`", w.Span);
            var arguments = value.Type is H.NamedType named ? named.Arguments : [];
            var changes = new List<H.MemberInit>();
            foreach (var change in w.Changes)
            {
                if (record.Fields.TryGetValue(change.Name, out var field) && !field.IsStatic)
                {
                    var fieldType = field.Type.Substitute(arguments);
                    var replacement = Expression(change.Value, fieldType);
                    if (!IsError(replacement.Type))
                        replacement = Convert(replacement, fieldType, change.Value.Span);
                    changes.Add(new H.MemberInit(field.Token, replacement));
                }
                else
                {
                    Error("AN2133", $"`{Show(value.Type)}` has no field `{change.Name}` to replace", change.NameSpan);
                    Expression(change.Value, null);
                }
            }
            return new H.WithExpr(value, [.. changes], value.Type);
        }

        /// <summary><c>==</c> / <c>!=</c> on two records: every field equal (each by its own <c>==</c>: nested records, strings, declared operators).</summary>
        private H.Expr RecordEquality(BinaryOperator op, H.Expr left, H.Expr right, TypeSymbol record, TextSpan span)
        {
            var type = left.Type;
            int l = Declare($"$left{locals.Count}", type, span), r = Declare($"$right{locals.Count}", type, span);
            var statements = new List<H.Stmt>
            {
                new H.ExprStmt(new H.AssignExpr(new H.LocalExpr(l, type), left, type)),
                new H.ExprStmt(new H.AssignExpr(new H.LocalExpr(r, type), right, type)),
            };
            H.Expr? all = null;
            foreach (var (field, fieldType) in InstanceFields(record, type))
            {
                var test = MembersEqual(new H.FieldExpr(new H.LocalExpr(l, type), field.Token, fieldType), new H.FieldExpr(new H.LocalExpr(r, type), field.Token, fieldType), span);
                all = all is null ? test : new H.BinaryExpr(H.BinaryOp.And, all, test, H.HirType.Bool);
            }
            all ??= new H.LiteralExpr(new H.BoolConstant(true), H.HirType.Bool);
            if (op == BinaryOperator.NotEqual)
                all = new H.UnaryExpr(H.UnaryOp.Not, all, H.HirType.Bool);
            return new H.BlockExpr([.. statements], all, H.HirType.Bool);
        }

        /// <summary><c>a == b</c> for two members of one type: records field by field, strings by content, a declared <c>op ==</c>, else the type's own.</summary>
        private H.Expr MembersEqual(H.Expr a, H.Expr b, TextSpan span)
        {
            var type = a.Type;
            if (type is H.NullableType { Inner: var inner })
            {
                // Both null, or neither and the values equal.
                var aNull = new H.BinaryExpr(H.BinaryOp.Equal, a, new H.LiteralExpr(new H.NullConstant(), type), H.HirType.Bool);
                var bNull = new H.BinaryExpr(H.BinaryOp.Equal, b, new H.LiteralExpr(new H.NullConstant(), type), H.HirType.Bool);
                var bSome = new H.BinaryExpr(H.BinaryOp.NotEqual, b, new H.LiteralExpr(new H.NullConstant(), type), H.HirType.Bool);
                var values = MembersEqual(new H.ConvertExpr(a, H.ConversionKind.Unwrap, inner), new H.ConvertExpr(b, H.ConversionKind.Unwrap, inner), span);
                return new H.ConditionalExpr(aNull, bNull, new H.BinaryExpr(H.BinaryOp.And, bSome, values, H.HirType.Bool), H.HirType.Bool);
            }
            if (type is H.ArrayType { Rank: 1, Element: var element } && RecordsCall("Equal", [element], [a, b], H.HirType.Bool) is { } elements)
                return elements;
            if (type is not H.NullableType && RecordOf(type) is { } record)
                return RecordEquality(BinaryOperator.Equal, a, b, record, span);
            if (type is H.NamedType && DeclaredBinary(BinaryOperator.Equal, a, b) is { } declared)
                return declared;
            if (binder.StringSymbol is { } text && type == binder.StringType && Strings(BinaryOperator.Equal, a, b, text, span) is { } same)
                return same;
            // A type parameter: the given type's own Equals when it has one (`Records.Same`).
            if (type is H.GenericParamType && RecordsCall("Same", type, [a, b], H.HirType.Bool) is { } given)
                return given;
            return new H.BinaryExpr(H.BinaryOp.Equal, a, b, H.HirType.Bool);
        }

        /// <summary>A member's <c>Hash()</c>: null hashes to 0, an array by its elements (<c>Records.Hash</c>); null when the type has none.</summary>
        private H.Expr? MemberHash(H.Expr value)
        {
            var u64 = H.HirType.Of(H.Primitive.U64);
            var type = value.Type;
            if (type is H.NullableType { Inner: var inner })
            {
                var some = MemberHash(new H.ConvertExpr(value, H.ConversionKind.Unwrap, inner));
                if (some is null)
                    return null;
                var isNull = new H.BinaryExpr(H.BinaryOp.Equal, value, new H.LiteralExpr(new H.NullConstant(), type), H.HirType.Bool);
                return new H.ConditionalExpr(isNull, new H.LiteralExpr(new H.IntegerConstant(0, H.Primitive.U64), u64), some, u64);
            }
            if (type is H.ArrayType { Rank: 1, Element: var element })
                return RecordsCall("Hash", [element], [value], u64);
            var hash = binder.MemberSymbolOf(type) is { } symbol ? BoundCall(value, symbol, "Hash", []) : null;
            if (hash is { } found && found.Type == u64)
                return found;
            // A type parameter: the given type's Hash() (`Records.HashOf`).
            return type is H.GenericParamType ? RecordsCall("HashOf", type, [value], u64) : null;
        }

        /// <summary>A member as text: a string as it is, null as "null", an array as <c>[a, b]</c>, else its <c>ToString()</c>; null when it has none.</summary>
        private H.Expr? MemberText(H.Expr value)
        {
            var str = binder.StringType!;
            var type = value.Type;
            if (type == str)
                return value;
            if (type is H.NullableType { Inner: var inner })
            {
                var some = MemberText(new H.ConvertExpr(value, H.ConversionKind.Unwrap, inner));
                if (some is null)
                    return null;
                var isNull = new H.BinaryExpr(H.BinaryOp.Equal, value, new H.LiteralExpr(new H.NullConstant(), type), H.HirType.Bool);
                return new H.ConditionalExpr(isNull, new H.LiteralExpr(new H.StringConstant("null"), str), some, str);
            }
            if (type is H.ArrayType { Rank: 1, Element: var element })
                return RecordsCall("Text", [element], [value], str);
            var text = binder.MemberSymbolOf(type) is { } symbol ? BoundCall(value, symbol, "ToString", []) : null;
            if (text is { } shown && shown.Type == str)
                return shown;
            // A type parameter: the given type's ToString() (`Records.TextOf`).
            return type is H.GenericParamType ? RecordsCall("TextOf", type, [value], str) : null;
        }

        /// <summary>A call of <c>System.Records.Name&lt;T&gt;(…)</c> on values of a type parameter <paramref name="type"/>.</summary>
        private H.Expr? RecordsCall(string name, H.HirType type, H.Expr[] arguments, H.HirType result)
        {
            if (binder.Types.GetValueOrDefault("System.Records") is not { } records)
                return null;
            var method = records.FindMethods(name).FirstOrDefault(m => m.IsStatic && m.GenericCount == 1 && m.Parameters.Length == arguments.Length
                && m.Parameters.All(p => p is H.GenericParamType));
            return method is null ? null : new H.CallExpr(null, method.Token, [type], arguments, result);
        }

        /// <summary>A call of <c>System.Records.Name&lt;T&gt;(…)</c> (the helpers over arrays), by the arguments' shapes.</summary>
        private H.Expr? RecordsCall(string name, H.HirType[] typeArguments, H.Expr[] arguments, H.HirType result)
        {
            if (binder.Types.GetValueOrDefault("System.Records") is not { } records)
                return null;
            var method = records.FindMethods(name).FirstOrDefault(m => m.IsStatic && m.GenericCount == typeArguments.Length && m.Parameters.Length == arguments.Length
                && m.Parameters.All(p => p is H.ArrayType));
            if (method is null)
                return null;
            return new H.CallExpr(null, method.Token, typeArguments, arguments, result);
        }

        /// <summary>
        /// The type a pattern names (null when it is the value's own, non-nullable type: no test needed) and the type the
        /// members are read from.
        /// </summary>
        private (H.HirType? Named, H.HirType Subject) PatternType(TypeSyntax? syntax, H.HirType type, TextSpan span)
        {
            var valueInner = type is H.NullableType vn ? vn.Inner : type;
            if (syntax is null)
                return (type is H.NullableType ? valueInner : null, valueInner);
            var target = Resolve(syntax) ?? ErrorType;
            if (IsError(target) || IsError(type))
                return (null, ErrorType);
            var inner = target is H.NullableType n ? n.Inner : target;
            _ = TypeTest(inner, type, span); // reports a type the value never is
            return (inner == valueInner && type is not H.NullableType ? null : inner, inner);
        }

        /// <summary><c>T(p, q)</c>: a <c>Deconstruct(&amp;mut A a, &amp;mut B b)</c> with as many parameters, else the fields in order (values and records).</summary>
        private H.Pattern Positional(PositionalPattern positional, H.HirType type)
        {
            var (named, subject) = PatternType(positional.Type, type, positional.Span);
            var symbol = !IsError(subject) && subject is H.NamedType ? binder.SymbolOf(subject) : null;
            var arguments = subject is H.NamedType sn ? sn.Arguments : [];
            H.HirType[] itemTypes = [];
            H.MemberToken? deconstructor = null;
            var deconstruct = symbol?.FindMethods("Deconstruct").FirstOrDefault(m => !m.IsStatic && m.Parameters.Length == positional.Items.Length
                && m.Parameters.All(p => p is H.ReferenceType { Mutable: true }));
            if (IsError(subject))
            {
                itemTypes = [.. positional.Items.Select(_ => ErrorType)];
            }
            else if (deconstruct is not null)
            {
                deconstructor = deconstruct.Token;
                itemTypes = [.. deconstruct.Parameters.Select(p => ((H.ReferenceType)p).Target.Substitute(arguments))];
            }
            else if (symbol is not null && (symbol.IsStruct || (symbol.Definition.Flags & H.TypeFlags.Record) != 0))
            {
                itemTypes = [.. InstanceFields(symbol, subject).Select(f => f.Type)];
                if (itemTypes.Length != positional.Items.Length)
                {
                    Error("AN2135", $"`{Show(subject)}` has {itemTypes.Length} position(s), not {positional.Items.Length}", positional.Span);
                    itemTypes = [.. positional.Items.Select(_ => ErrorType)];
                }
            }
            else
            {
                Error("AN2136", $"`{Show(subject)}` has no positions: a `value`, a record, or a type with `Deconstruct(&mut …)` has", positional.Span);
                itemTypes = [.. positional.Items.Select(_ => ErrorType)];
            }
            var items = positional.Items.Select((item, i) => Pattern(item, itemTypes[i])).ToArray();
            return IsError(subject) ? new H.DiscardPattern() : new H.PositionalPattern(named, deconstructor, items);
        }

        /// <summary><c>T { A: p }</c>: the named fields or (readable) properties match.</summary>
        private H.Pattern Property(PropertyPattern property, H.HirType type)
        {
            var (named, subject) = PatternType(property.Type, type, property.Span);
            var symbol = !IsError(subject) && subject is H.NamedType ? binder.SymbolOf(subject) : null;
            var arguments = subject is H.NamedType sn ? sn.Arguments : [];
            var members = new List<H.PropertySubpattern>();
            foreach (var m in property.Members)
            {
                if (symbol is not null && symbol.Fields.TryGetValue(m.Name, out var field) && !field.IsStatic)
                    members.Add(new H.PropertySubpattern(field.Token, Pattern(m.Pattern, field.Type.Substitute(arguments)), H.MemberKind.Field));
                else if (symbol is not null && symbol.Properties.TryGetValue(m.Name, out var prop) && !prop.IsStatic && prop.Getter is not null)
                    members.Add(new H.PropertySubpattern(prop.Token, Pattern(m.Pattern, prop.Type.Substitute(arguments)), H.MemberKind.Property));
                else
                {
                    if (!IsError(subject))
                        Error("AN2137", $"`{Show(subject)}` has no field or property `{m.Name}`", m.NameSpan);
                    Pattern(m.Pattern, ErrorType);
                }
            }
            return IsError(subject) ? new H.DiscardPattern() : new H.PropertyPattern(named, [.. members]);
        }

        /// <summary>A name that is a type and nothing else (<c>x is Dog</c>).</summary>
        private TypeSymbol? StaticTypeOnly(Expr expression)
        {
            if (expression is NameExpr name && (IsValueName(name.Name) || ModuleField(name.Name) is not null))
                return null;
            var type = binder.TypeNamed(expression, scope);
            return type is { IsEnum: false } ? type : null;
        }

        private H.Pattern TypeTest(H.HirType target, H.HirType type, TextSpan span)
        {
            if (IsError(target) || IsError(type))
                return new H.DiscardPattern();
            var inner = target is H.NullableType n ? n.Inner : target;
            var valueInner = type is H.NullableType vn ? vn.Inner : type;
            if (binder.SymbolOf(inner) is { IsClass: true } tc && binder.SymbolOf(valueInner) is { IsClass: true } vc)
            {
                if (!tc.IsOrDerives(vc) && !vc.IsOrDerives(tc))
                    Error("AN2088", $"a `{Show(type)}` is never a `{Show(inner)}`", span);
                return new H.TypePattern(inner);
            }
            // Objects and interfaces: tested at run time (an object of any class may implement an interface); an `any` or an
            // interface may hold a boxed value.
            if (IsReference(valueInner) && (IsInterface(inner) || IsReference(inner) && IsInterface(valueInner))
                || (IsInterface(valueInner) || IsAny(valueInner)) && IsValueType(inner))
                return new H.TypePattern(inner);
            if (inner == valueInner)
                return type is H.NullableType ? new H.NotPattern(new H.ConstantPattern(new H.NullConstant())) : new H.DiscardPattern();
            Error("AN2089", $"a `{Show(type)}` is never a `{Show(inner)}`", span);
            return new H.DiscardPattern();
        }

        private H.HirConstant PatternConstant(Expr expression, H.HirType type)
        {
            if (IsError(type))
                return new H.BoolConstant(false);
            var within = binder.SymbolOf(type) is { IsEnum: true } e ? e : null;
            if (binder.Constant(expression, type, scope, bag, within) is { } constant)
                return constant;
            Error("AN2090", $"a pattern compares with a constant `{Show(type)}`", expression.Span);
            return new H.BoolConstant(false);
        }
    }
}
