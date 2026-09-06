using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace GFDStudio.AnimationMatching.Index;

/// <summary>Exact k-nearest-neighbor search in the projected feature space.</summary>
public sealed class VpTree
{
    private const int SerializedVersion = 1;
    private const int ParallelBuildThreshold = 32768;

    private readonly float[] _points;
    private readonly int _dimensions;
    private readonly int[] _pointIndex;
    private readonly float[] _threshold;
    private readonly int[] _near;
    private readonly int[] _far;
    private readonly int _root;
    private readonly int _maxParallelDepth;

    private readonly record struct Pair(int Index, float DistanceSquared);

    public VpTree(float[] packedPoints, int dimensions)
    {
        if (dimensions < 1 || packedPoints.Length % dimensions != 0)
            throw new ArgumentException("Invalid packed point array.");

        _points = packedPoints;
        _dimensions = dimensions;
        var count = packedPoints.Length / dimensions;
        _pointIndex = new int[count];
        _threshold = new float[count];
        _near = new int[count];
        _far = new int[count];
        Array.Fill(_near, -1);
        Array.Fill(_far, -1);
        _root = count == 0 ? -1 : 0;
        _maxParallelDepth = Math.Max(0, (int)Math.Ceiling(Math.Log2(Math.Max(1, Environment.ProcessorCount))));

        if (count == 0)
            return;

        var indexes = new int[count];
        for (var i = 0; i < indexes.Length; i++) indexes[i] = i;
        Build(indexes, 0, indexes.Length, 0);
    }

    private VpTree(
        float[] packedPoints,
        int dimensions,
        int root,
        int[] pointIndex,
        float[] threshold,
        int[] near,
        int[] far)
    {
        if (dimensions < 1 || packedPoints.Length % dimensions != 0)
            throw new ArgumentException("Invalid packed point array.");

        var count = packedPoints.Length / dimensions;
        if (pointIndex.Length != count || threshold.Length != count || near.Length != count || far.Length != count)
            throw new InvalidDataException("VP-tree cache size does not match projected point count.");
        if (count == 0 ? root != -1 : root < 0 || root >= count)
            throw new InvalidDataException("VP-tree cache root is invalid.");

        _points = packedPoints;
        _dimensions = dimensions;
        _root = root;
        _pointIndex = pointIndex;
        _threshold = threshold;
        _near = near;
        _far = far;
        _maxParallelDepth = 0;
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

    internal void WriteTo(BinaryWriter writer)
    {
        writer.Write(SerializedVersion);
        writer.Write(_dimensions);
        writer.Write(_root);
        WriteIntArray(writer, _pointIndex);
        WriteFloatArray(writer, _threshold);
        WriteIntArray(writer, _near);
        WriteIntArray(writer, _far);
    }

    internal static VpTree ReadFrom(BinaryReader reader, float[] packedPoints, int expectedDimensions)
    {
        if (reader.ReadInt32() != SerializedVersion)
            throw new InvalidDataException("Unsupported VP-tree cache version.");

        var dimensions = reader.ReadInt32();
        if (dimensions != expectedDimensions)
            throw new InvalidDataException("VP-tree dimensions do not match projected vectors.");

        var root = reader.ReadInt32();
        var count = packedPoints.Length / expectedDimensions;
        var pointIndex = ReadIntArray(reader, count);
        var threshold = ReadFloatArray(reader, count);
        var near = ReadIntArray(reader, count);
        var far = ReadIntArray(reader, count);
        return new VpTree(packedPoints, dimensions, root, pointIndex, threshold, near, far);
    }

    private void Build(int[] indexes, int start, int length, int depth)
    {
        if (length <= 0)
            return;

        var node = start;
        var vp = indexes[start];
        _pointIndex[node] = vp;
        if (length == 1)
            return;

        var otherStart = start + 1;
        var otherCount = length - 1;
        var nearCount = otherCount / 2;
        var split = otherStart + nearCount;
        SelectNthByDistance(indexes, otherStart, start + length - 1, split, vp);

        _threshold[node] = MathF.Sqrt(DistanceSquared(vp, indexes[split]));
        var farCount = otherCount - nearCount;
        _near[node] = nearCount > 0 ? otherStart : -1;
        _far[node] = farCount > 0 ? split : -1;

        if (length >= ParallelBuildThreshold && depth < _maxParallelDepth && nearCount > 0 && farCount > 0)
        {
            Parallel.Invoke(
                () => Build(indexes, otherStart, nearCount, depth + 1),
                () => Build(indexes, split, farCount, depth + 1));
        }
        else
        {
            if (nearCount > 0)
                Build(indexes, otherStart, nearCount, depth + 1);
            if (farCount > 0)
                Build(indexes, split, farCount, depth + 1);
        }
    }

    /// <summary>
    /// In-place nth-element selection using a three-way partition. The old implementation fully
    /// sorted every subtree and repeatedly recomputed 24-D distances from comparer callbacks,
    /// which made million-pose tree construction unnecessarily expensive.
    /// </summary>
    private void SelectNthByDistance(int[] indexes, int left, int right, int target, int vp)
    {
        while (left < right)
        {
            var pivotDistance = DistanceSquared(vp, indexes[left + ((right - left) >> 1)]);
            var less = left;
            var current = left;
            var greater = right;

            while (current <= greater)
            {
                var distance = DistanceSquared(vp, indexes[current]);
                if (distance < pivotDistance)
                {
                    Swap(indexes, less++, current++);
                }
                else if (distance > pivotDistance)
                {
                    Swap(indexes, current, greater--);
                }
                else
                {
                    current++;
                }
            }

            if (target < less)
                right = less - 1;
            else if (target > greater)
                left = greater + 1;
            else
                return;
        }
    }

    private void Search(int node, ReadOnlySpan<float> query, int k, List<Pair> best)
    {
        if (node < 0)
            return;

        var pointIndex = _pointIndex[node];
        var d2 = DistanceSquared(pointIndex, query);
        InsertBest(best, new Pair(pointIndex, d2), k);
        var d = MathF.Sqrt(d2);
        var tau = best.Count < k ? float.PositiveInfinity : MathF.Sqrt(best[^1].DistanceSquared);

        var near = _near[node];
        var far = _far[node];
        if (near < 0 && far < 0)
            return;

        if (d < _threshold[node])
        {
            if (d - tau <= _threshold[node]) Search(near, query, k, best);
            if (d + tau >= _threshold[node]) Search(far, query, k, best);
        }
        else
        {
            if (d + tau >= _threshold[node]) Search(far, query, k, best);
            if (d - tau <= _threshold[node]) Search(near, query, k, best);
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

    private static void Swap(int[] values, int first, int second)
    {
        if (first == second)
            return;
        (values[first], values[second]) = (values[second], values[first]);
    }

    private static void WriteIntArray(BinaryWriter writer, int[] values)
    {
        writer.Write(values.Length);
        writer.Flush();
        writer.BaseStream.Write(MemoryMarshal.AsBytes(values.AsSpan()));
    }

    private static int[] ReadIntArray(BinaryReader reader, int expectedLength)
    {
        var length = reader.ReadInt32();
        if (length != expectedLength)
            throw new InvalidDataException("VP-tree integer array length is invalid.");
        var values = new int[length];
        reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan()));
        return values;
    }

    private static void WriteFloatArray(BinaryWriter writer, float[] values)
    {
        writer.Write(values.Length);
        writer.Flush();
        writer.BaseStream.Write(MemoryMarshal.AsBytes(values.AsSpan()));
    }

    private static float[] ReadFloatArray(BinaryReader reader, int expectedLength)
    {
        var length = reader.ReadInt32();
        if (length != expectedLength)
            throw new InvalidDataException("VP-tree float array length is invalid.");
        var values = new float[length];
        reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan()));
        return values;
    }
}
