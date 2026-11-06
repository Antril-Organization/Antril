using Antril.Syntax;
using H = Larva.Hir;

namespace Antril.Binding
{
    internal enum RecordedKind : byte { Expression, Local, Parameter, Type, Member, Namespace }

    /// <summary>A span of source and what binding made of it.</summary>
    internal sealed record RecordedSpan(SourceText Source, TextSpan Span, RecordedKind Kind)
    {
        /// <summary>An expression's, a local's or a parameter's type (the program's tokens).</summary>
        public H.HirType? Type { get; init; }

        /// <summary>A type name's type.</summary>
        public H.TypeToken Token { get; init; }

        public H.MemberToken Member { get; init; }
        public H.MemberKind MemberKind { get; init; }

        /// <summary>A member's receiver type (its type arguments say what the member's types are there).</summary>
        public H.HirType? Receiver { get; init; }

        /// <summary>A local's or parameter's name, a namespace's.</summary>
        public string? Name { get; init; }

        /// <summary>Where a local or parameter is declared.</summary>
        public TextSpan Declared { get; init; }
    }

    /// <summary>A local (or parameter) and where it can be used.</summary>
    internal sealed record RecordedLocal(SourceText Source, string Name, H.HirType Type, TextSpan Declared, TextSpan Scope, bool Parameter);

    /// <summary>A method body bound: the method and the span of its body.</summary>
    internal sealed record RecordedBody(SourceText Source, MethodSymbol Method, TextSpan Span);

    /// <summary>A call: its span and the methods it could call (with their declaring type's arguments).</summary>
    internal sealed record RecordedCall(SourceText Source, TextSpan Span, (MethodSymbol Method, H.HirType[] Declaring)[] Candidates);

    /// <summary>
    /// What binding found where: names, expressions and their types, locals and their scopes, calls. Kept only when a
    /// document is analyzed for an editor (compiling records nothing).
    /// </summary>
    internal sealed class BindingRecorder
    {
        public readonly List<RecordedSpan> Spans = [];
        public readonly List<RecordedLocal> Locals = [];
        public readonly List<RecordedBody> Bodies = [];
        public readonly List<RecordedCall> Calls = [];

        public void Add(RecordedSpan span)
        {
            lock (Spans)
                Spans.Add(span);
        }

        public void Add(RecordedLocal local)
        {
            lock (Locals)
                Locals.Add(local);
        }

        public void Add(RecordedBody body)
        {
            lock (Bodies)
                Bodies.Add(body);
        }

        public void Add(RecordedCall call)
        {
            lock (Calls)
                Calls.Add(call);
        }
    }
}
