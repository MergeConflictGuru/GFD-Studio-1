using System;
using System.Buffers;
using System.Linq;
using System.Numerics;
using GFDStudio.AnimationMatching.Core;

namespace GFDStudio.AnimationMatching.Stitching;

internal static class HierarchyPoseBlend
{
    public static int[] BuildOrder(SkeletonDefinition skeleton)
    {
        int Depth(int i)
        {
            int depth = 0;
            for (int p = skeleton.Parents[i]; p >= 0; p = skeleton.Parents[p])
                if (++depth > skeleton.BoneCount) throw new InvalidOperationException("Cyclic skeleton");
            return depth;
        }
        return Enumerable.Range(0, skeleton.BoneCount).OrderBy(Depth).ToArray();
    }

    public static void Blend(SkeletonDefinition skeleton, int[] order,
        ReadOnlySpan<BoneTransform> a, ReadOnlySpan<BoneTransform> b, float amount, Span<BoneTransform> result)
    {
        var buffer = ArrayPool<Matrix4x4>.Shared.Rent(skeleton.BoneCount);
        try
        {
            foreach (int i in order)
            {
                int parent = skeleton.Parents[i];
                var localA = parent < 0 ? Matrix(a[i]) : Matrix(a[i]) * Inverse(Matrix(a[parent]));
                var localB = parent < 0 ? Matrix(b[i]) : Matrix(b[i]) * Inverse(Matrix(b[parent]));
                var local = Matrix(BoneTransform.Lerp(Transform(localA), Transform(localB), amount));
                buffer[i] = parent < 0 ? local : local * buffer[parent];
                result[i] = Transform(buffer[i]);
            }
        }
        finally { ArrayPool<Matrix4x4>.Shared.Return(buffer); }
    }

    private static Matrix4x4 Matrix(BoneTransform p)
        => Matrix4x4.CreateFromQuaternion(p.Rotation) * Matrix4x4.CreateScale(p.Scale) * Matrix4x4.CreateTranslation(p.Position);
    private static Matrix4x4 Inverse(Matrix4x4 value)
        => Matrix4x4.Invert(value, out var result) ? result : throw new InvalidOperationException("Singular bone matrix");
    private static BoneTransform Transform(Matrix4x4 value)
    {
        if (Matrix4x4.Decompose(value, out var scale, out var rotation, out var position))
            return new BoneTransform(position, rotation, scale);
        // A parent with nonuniform scale can introduce shear. Project it onto
        // orthogonal axes rather than replace a valid limb transform with identity.
        var x = new Vector3(value.M11, value.M12, value.M13);
        var y = new Vector3(value.M21, value.M22, value.M23);
        var z = new Vector3(value.M31, value.M32, value.M33);
        float sx = x.Length();
        if (!float.IsFinite(sx) || sx < 1e-8f) throw new InvalidOperationException("Invalid bone matrix");
        x /= sx; y -= x * Vector3.Dot(x, y);
        float sy = y.Length();
        if (!float.IsFinite(sy) || sy < 1e-8f) throw new InvalidOperationException("Invalid bone matrix");
        y /= sy;
        var normal = Vector3.Cross(x, y);
        float sz = Vector3.Dot(z, normal);
        if (!float.IsFinite(sz) || MathF.Abs(sz) < 1e-8f) throw new InvalidOperationException("Invalid bone matrix");
        var basis = new Matrix4x4(x.X,x.Y,x.Z,0,y.X,y.Y,y.Z,0,normal.X,normal.Y,normal.Z,0,0,0,0,1);
        return new BoneTransform(value.Translation, Quaternion.CreateFromRotationMatrix(basis), new Vector3(sx,sy,sz));
    }
}
