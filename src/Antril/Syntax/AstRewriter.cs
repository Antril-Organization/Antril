using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Antril.Syntax
{
    /// <summary>
    /// Rebuilds a syntax tree with some nodes replaced: <c>map</c> sees every node before its children and returns its
    /// replacement (used as it is), or null to keep the node and rewrite its children. Works over every node type through
    /// reflection (the pack keeps its syntax types whole: Antril.csproj roots the assembly), so new syntax needs nothing here.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "The syntax types are rooted (TrimmerRootAssembly).")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "The syntax types are rooted (TrimmerRootAssembly).")]
    [UnconditionalSuppressMessage("Trimming", "IL2080", Justification = "The syntax types are rooted (TrimmerRootAssembly).")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Arrays of syntax types are cloned, never made.")]
    public static class AstRewriter
    {
        private static readonly MethodInfo Clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly Dictionary<Type, PropertyInfo[]> properties = [];

        public static T Rewrite<T>(T node, Func<Node, Node?> map) where T : Node => (T)Visit(node, map)!;

        /// <summary>Every node under (and including) <paramref name="root"/>.</summary>
        public static IEnumerable<Node> Descendants(Node root)
        {
            var stack = new Stack<Node>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                yield return node;
                foreach (var property in PropertiesOf(node.GetType()))
                {
                    switch (property.GetValue(node))
                    {
                        case Node child:
                            stack.Push(child);
                            break;
                        case Array array:
                            foreach (var item in array)
                            {
                                if (item is Node element)
                                    stack.Push(element);
                            }
                            break;
                    }
                }
            }
        }

        private static object? Visit(object? value, Func<Node, Node?> map)
        {
            switch (value)
            {
                case Node node:
                {
                    if (map(node) is { } replaced)
                        return replaced;
                    object? copy = null;
                    foreach (var property in PropertiesOf(node.GetType()))
                    {
                        object? before = property.GetValue(node);
                        object? after = Visit(before, map);
                        if (ReferenceEquals(before, after))
                            continue;
                        copy ??= Clone.Invoke(node, null);
                        property.SetValue(copy, after);
                    }
                    return copy ?? node;
                }
                case Array array when typeof(Node).IsAssignableFrom(array.GetType().GetElementType()):
                {
                    Array? copy = null;
                    for (int i = 0; i < array.Length; i++)
                    {
                        object? before = array.GetValue(i);
                        object? after = Visit(before, map);
                        if (ReferenceEquals(before, after))
                            continue;
                        copy ??= (Array)array.Clone();
                        copy.SetValue(after, i);
                    }
                    return copy ?? array;
                }
                default:
                    return value;
            }
        }

        /// <summary>The settable properties that can hold nodes.</summary>
        private static PropertyInfo[] PropertiesOf(Type type)
        {
            lock (properties)
            {
                if (!properties.TryGetValue(type, out var found))
                {
                    properties[type] = found = [.. type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                        .Where(p => p.SetMethod is not null && p.GetIndexParameters().Length == 0
                            && (typeof(Node).IsAssignableFrom(p.PropertyType) || p.PropertyType.IsArray && typeof(Node).IsAssignableFrom(p.PropertyType.GetElementType())))];
                }
                return found;
            }
        }
    }
}
