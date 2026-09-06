using System;
using System.Collections.Generic;

namespace GFDStudio.AnimationMatching.Index;

/// <summary>Exact k-nearest-neighbor search in the projected feature space.</summary>
public sealed class VpTree
{
    private readonly float[] _points;
    private readonly int _dimensions;
    private Node? _root;

    private sealed class Node
    {
        public int Index;
        public float Threshold;
        public Node? Near;
        public Node? Far;
    }

    private readonly record struct Pair(int Index, float DistanceSquared);

    public VpTree(float[] packedPoints, int dimensions)
    {
        if (dimensions < 1 || packedPoints.Length % dimensions != 0)
            throw new ArgumentException("Invalid packed point array.");
        _points = packedPoints;
        _dimensions = dimensions;
        var indexes = new int[packedPoints.Length / dimensions];
        for (var i = 0; i < indexes.Length; i++) indexes[i] = i;
        _root = Build(indexes, 0, indexes.Length);
    }

    public int Count => _points.Length / _dimensions;

    public int[] FindNearest(ReadOnlySpan<float> query, int count)
    {
        if (query.Length != _dimensions) throw new ArgumentException("Query dimensions do not match.");
        count = Math.Clamp(count, 1, Math.Max(1, Count));
        var best = new List<Pair>(count);
        Search(_root, query, count, best);
        best.Sort(static (a, b) => a.DistanceSquared.CompareTo(b.DistanceSquared));
        var result = new int[best.Count];
        for (var i = 0; i < best.Count; i++) result[i] = best[i].Index;
        return result;
    }

    private Node? Build(int[] indexes, int start, int length)
    {
        if (length <= 0) return null;
        var node = new Node { Index = indexes[start] };
        if (length == 1) return node;

        var vp = indexes[start];
        Array.Sort(indexes, start + 1, length - 1, Comparer<int>.Create((a, b) =>
            DistanceSquared(vp, a).CompareTo(DistanceSquared(vp, b))));

        var otherCount = length - 1;
        var nearCount = otherCount / 2;
        var split = start + 1 + nearCount;
        var thresholdIndex = Math.Min(split, start + length - 1);
        node.Threshold = MathF.Sqrt(DistanceSquared(vp, indexes[thresholdIndex]));
        node.Near = Build(indexes, start + 1, nearCount);
        node.Far = Build(indexes, split, otherCount - nearCount);
        return node;
    }

    private void Search(Node? node, ReadOnlySpan<float> query, int k, List<Pair> best)
    {
        if (node is null) return;
        var d2 = DistanceSquared(node.Index, query);
        InsertBest(best, new Pair(node.Index, d2), k);
        var d = MathF.Sqrt(d2);
        var tau = best.Count < k ? float.PositiveInfinity : MathF.Sqrt(best[^1].DistanceSquared);

        if (node.Near is null && node.Far is null) return;
        if (d < node.Threshold)
        {
            if (d - tau <= node.Threshold) Search(node.Near, query, k, best);
            if (d + tau >= node.Threshold) Search(node.Far, query, k, best);
        }
        else
        {
            if (d + tau >= node.Threshold) Search(node.Far, query, k, best);
            if (d - tau <= node.Threshold) Search(node.Near, query, k, best);
        }
    }

    private static void InsertBest(List<Pair> best, Pair pair, int k)
    {
        var lo = 0;
        var hi = best.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (best[mid].DistanceSquared <= pair.DistanceSquared) lo = mid + 1;
            else hi = mid;
        }
        best.Insert(lo, pair);
        if (best.Count > k) best.RemoveAt(best.Count - 1);
    }

    private float DistanceSquared(int a, int b)
    {
        var ao = a * _dimensions;
        var bo = b * _dimensions;
        var sum = 0f;
        for (var i = 0; i < _dimensions; i++)
        {
            var d = _points[ao + i] - _points[bo + i];
            sum += d * d;
        }
        return sum;
    }

    private float DistanceSquared(int pointIndex, ReadOnlySpan<float> query)
    {
        var offset = pointIndex * _dimensions;
        var sum = 0f;
        for (var i = 0; i < _dimensions; i++)
        {
            var d = _points[offset + i] - query[i];
            sum += d * d;
        }
        return sum;
    }
}
