namespace Cst.Transfer;

/// <summary>Dependency ordering that tolerates cycles: cycles collapse into one group (Tarjan SCC).</summary>
public static class Graph
{
    /// <summary>
    /// Returns groups in install order: every group comes after the groups it depends on.
    /// <paramref name="deps"/> maps a node to the nodes it depends on.
    /// </summary>
    public static List<List<T>> OrderedGroups<T>(IEnumerable<T> nodes, Func<T, IEnumerable<T>> deps) where T : notnull
    {
        var all = nodes.ToList();
        var set = new HashSet<T>(all);
        int index = 0;
        var idx = new Dictionary<T, int>();
        var low = new Dictionary<T, int>();
        var onStack = new HashSet<T>();
        var stack = new Stack<T>();
        var result = new List<List<T>>();

        void Visit(T v)
        {
            idx[v] = low[v] = index++;
            stack.Push(v); onStack.Add(v);
            foreach (var w in deps(v).Where(set.Contains))
            {
                if (!idx.ContainsKey(w)) { Visit(w); low[v] = Math.Min(low[v], low[w]); }
                else if (onStack.Contains(w)) low[v] = Math.Min(low[v], idx[w]);
            }
            if (low[v] != idx[v]) return;
            var group = new List<T>();
            T x;
            do { x = stack.Pop(); onStack.Remove(x); group.Add(x); } while (!x.Equals(v));
            // Tarjan emits SCCs in reverse topological order of the "depends on" edges,
            // i.e. dependencies first, which is exactly the install order.
            result.Add(group);
        }

        foreach (var n in all) if (!idx.ContainsKey(n)) Visit(n);
        return result;
    }
}
