using Antril.Syntax;
using H = Larva.Hir;
using static Larva.Hir.HirTypes;

namespace Antril.Binding
{
    /// <summary>
    /// A function with <c>yield</c> (Element: the values' type) or an <c>async</c> function (Element: the result's type,
    /// void for a plain Task), bound: what <see cref="Binder.MakeIterators"/> turns into its class.
    /// </summary>
    internal sealed record IteratorRequest(MethodSymbol Method, H.HirBody Body, H.HirType Element, bool Async = false);

    internal sealed partial class Binder
    {
        private readonly List<IteratorRequest> iterators = [];

        internal void AddIterator(IteratorRequest request)
        {
            lock (iterators)
                iterators.Add(request);
        }

        /// <summary><c>System.Iterator&lt;T&gt;</c>, what a function with <c>yield</c> gives (null without the core library).</summary>
        internal TypeSymbol? IteratorSymbol => Types.TryGetValue("System.Iterator", out var symbol) ? symbol : null;

        /// <summary><c>Task</c> and <c>Task&lt;T&gt;</c>, what async functions give (null without the core library).</summary>
        internal TypeSymbol? TaskSymbol => FindTask(0);
        internal TypeSymbol? TaskOfSymbol => FindTask(1);

        private TypeSymbol? FindTask(int arity)
        {
            const string name = "System.Threading.Tasks.Task";
            if (Types.TryGetValue($"{name}`{arity}", out var exact))
                return exact;
            return Types.TryGetValue(name, out var plain) && plain.GenericCount == arity ? plain : null;
        }

        /// <summary>
        /// Each function with <c>yield</c> becomes: a class <c>Owner.F$iterator{n}</c> deriving from <c>Iterator&lt;T&gt;</c>
        /// (the owner's type parameters, then the function's), with the function's arguments (and <c>this</c>) in fields;
        /// its <c>Next()</c> runs the function's body, which the lowering makes resumable (<c>MethodFlags.Iterator</c>):
        /// each <c>yield</c> stores the value in <c>current</c> and pauses. The function itself only makes the object.
        /// Made after the bodies are bound (in a fixed order), since binding runs in parallel.
        /// </summary>
        private void MakeIterators()
        {
            if (iterators.Count == 0)
                return;
            foreach (var request in iterators.OrderBy(r => r.Method.Owner.Token.Value).ThenBy(r => r.Method.Index))
            {
                var baseSymbol = request.Async ? (request.Element == H.HirType.Void ? TaskSymbol : TaskOfSymbol) : IteratorSymbol;
                if (baseSymbol is not null)
                    MakeIterator(request, baseSymbol);
            }
            iterators.Clear();
        }

        /// <summary>
        /// The class of a function with <c>yield</c> (deriving from <c>Iterator&lt;T&gt;</c>) or of an async function
        /// (deriving from <c>Task</c> / <c>Task&lt;T&gt;</c>: the frame is the task it gives). Its <c>$Step()</c> is the
        /// function's body, resumable; an iterator's <c>Next()</c> runs it, an async function's <c>Run()</c> does (and a
        /// failure fails the task), and the function makes the object (an async function also starts it).
        /// </summary>
        private void MakeIterator(IteratorRequest request, TypeSymbol baseSymbol)
        {
            var method = request.Method;
            var owner = method.Owner;
            var ownerDef = owner.Definition;
            var methodDef = method.Definition;
            int ownCount = ownerDef.Generics.Count, methodCount = methodDef.Generics.Count;
            // In the class, the function's type parameters follow the owner's.
            H.HirType[] typeParameters = [.. Enumerable.Range(0, ownCount).Select(i => (H.HirType)new H.GenericParamType(H.GenericOwner.Type, i))];
            H.HirType[] methodParameters = [.. Enumerable.Range(0, methodCount).Select(i => (H.HirType)new H.GenericParamType(H.GenericOwner.Type, ownCount + i))];
            H.HirType Moved(H.HirType type) => type.Substitute(typeParameters, methodParameters);
            var definition = new H.TypeDef
            {
                Namespace = ownerDef.Namespace,
                Name = $"{ownerDef.Name}.{methodDef.Name}{(request.Async ? "$async" : "$iterator")}{method.Index}",
                Kind = H.TypeKind.Class,
                Flags = H.TypeFlags.Final | H.TypeFlags.Generated,
                Access = H.Access.Internal,
                DeclaringType = owner.Token.Index,
                Location = methodDef.Location,
            };
            foreach (var g in ownerDef.Generics)
                definition.Generics.Add(g);
            foreach (var g in methodDef.Generics)
                definition.Generics.Add(g with { Types = [.. g.Types.Select(Moved)], OneOf = [.. g.OneOf.Select(Moved)], ConvertsTo = [.. g.ConvertsTo.Select(Moved)], Constructors = [.. g.Constructors.Select(c => c.Select(Moved).ToArray())] });
            definition.Base = baseSymbol.GenericCount == 0 ? H.HirType.Named(baseSymbol.Token, []) : H.HirType.Named(baseSymbol.Token, [Moved(request.Element)]);
            var parameterFields = new int[method.Parameters.Length];
            for (int p = 0; p < parameterFields.Length; p++)
                parameterFields[p] = definition.AddField(new H.FieldDef($"$p{p}", Moved(method.Parameters[p])) { Flags = H.FieldFlags.Generated, Access = H.Access.Private });
            int thisField = method.IsStatic ? -1 : definition.AddField(new H.FieldDef("$this", owner.Type) { Flags = H.FieldFlags.Generated, Access = H.Access.Private });
            var token = Assembly.Add(definition);
            var self = H.HirType.Named(token, [.. typeParameters, .. methodParameters]);
            // $Step(): the function's body, made resumable by the lowering (its parameters and `this` read from the fields).
            H.HirType[] ownArguments = [.. typeParameters, .. methodParameters];
            var stepResult = request.Async ? H.HirType.Void : H.HirType.Bool;
            int step = definition.AddMethod(new H.MethodDef("$Step", stepResult)
            {
                Flags = (request.Async ? H.MethodFlags.Async : H.MethodFlags.Iterator) | H.MethodFlags.Final | H.MethodFlags.Generated,
                Access = H.Access.Private,
                Location = methodDef.Location,
            });
            definition.Methods[step].Body = request.Body;
            var callStep = new H.CallExpr(new H.ThisExpr(self), H.MemberToken.Def(token.Index, step), ownArguments, [], stepResult) { NonVirtual = true };
            int resume = definition.AddMethod(new H.MethodDef(request.Async ? "Run" : "Next", stepResult)
            {
                Flags = H.MethodFlags.Virtual | H.MethodFlags.Override | H.MethodFlags.Final | H.MethodFlags.Generated,
                Access = H.Access.Public,
            });
            if (!request.Async)
            {
                definition.Methods[resume].Body = new H.HirBody([], new H.BlockStmt([new H.ReturnStmt(callStep)]));
            }
            else
            {
                // Run(): the next part of the body; an error out of it fails the task.
                var any = ObjectSymbol!.Type;
                var fail = TaskSymbol!.FindMethods("Fail")[0];
                var failure = new H.LocalExpr(0, any);
                definition.Methods[resume].Body = new H.HirBody([new H.HirLocal("failure", any)], new H.BlockStmt([
                    new H.TryStmt(new H.BlockStmt([new H.ExprStmt(callStep)]),
                        [new H.CatchClause(any, 0, null, new H.BlockStmt([new H.ExprStmt(new H.CallExpr(new H.ThisExpr(self), fail.Token, [], [failure], H.HirType.Void))]))],
                        null),
                ]));
            }
            // The function: a new iterator holding its arguments.
            var inits = new List<H.MemberInit>();
            for (int p = 0; p < parameterFields.Length; p++)
                inits.Add(new H.MemberInit(H.MemberToken.Def(token.Index, parameterFields[p]), new H.ParamExpr(p, method.Parameters[p])));
            if (thisField >= 0)
                inits.Add(new H.MemberInit(H.MemberToken.Def(token.Index, thisField), new H.ThisExpr(owner.Type)));
            H.HirType[] arguments = [.. typeParameters, .. Enumerable.Range(0, methodCount).Select(i => (H.HirType)new H.GenericParamType(H.GenericOwner.Method, i))];
            var frameType = H.HirType.Named(token, arguments);
            var made = new H.NewExpr(null, [], [.. inits], frameType);
            if (!request.Async)
            {
                methodDef.Body = new H.HirBody([], new H.BlockStmt([new H.ReturnStmt(new H.ConvertExpr(made, H.ConversionKind.Upcast, method.Result))]));
                return;
            }
            // An async function runs until its first pause, then gives its task.
            var frame = new H.LocalExpr(0, frameType);
            methodDef.Body = new H.HirBody([new H.HirLocal("$task", frameType)], new H.BlockStmt([
                new H.LocalStmt(0, made),
                new H.ExprStmt(new H.CallExpr(frame, H.MemberToken.Def(token.Index, resume), arguments, [], H.HirType.Void) { NonVirtual = true }),
                new H.ReturnStmt(new H.ConvertExpr(frame, H.ConversionKind.Upcast, method.Result)),
            ]));
        }

        /// <summary>Whether a body has a <c>yield</c> of its own (not one inside a lambda).</summary>
        internal static bool HasYield(Node? node) => node switch
        {
            null => false,
            YieldStmt => true,
            BlockStmt b => b.Statements.Any(HasYield),
            IfStmt i => HasYield(i.Then) || HasYield(i.Else),
            WhileLoop w => HasYield(w.Body),
            DoLoop d => HasYield(d.Body),
            RangeLoop r => HasYield(r.Body),
            ForLoop f => HasYield(f.Body),
            CounterLoop c => HasYield(c.Body),
            ForeachLoop f => HasYield(f.Body),
            SwitchStmt s => s.Arms.Any(a => HasYield(a.Body)),
            TryStmt t => HasYield(t.Body) || t.Catches.Any(c => HasYield(c.Body)) || HasYield(t.Finally),
            UnsafeStmt u => HasYield(u.Body),
            RegionStmt r => HasYield(r.Body),
            _ => false,
        };
    }
}
