using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GFDLibrary.Animations;
using GFDLibrary.Models;
using GFDStudio.AnimationMatching.Core;
using GFDStudio.AnimationMatching.Stitching;

namespace GFDStudio.AnimationMatching.Integration;

public static class SlideAiBlend
{
    internal static float MatchGroundHeight(SkeletonDefinition skeleton, BoneTransform[] source,
        BoneTransform[] before, BoneTransform[] candidate, float step)
    {
        if(skeleton.BoneNames.SequenceEqual(CanonicalSkeleton.Names))
        {
            // The search skeleton has anatomical joints only, with no toe helpers.
            float Low(BoneTransform[] pose)=>pose.Skip(1).Min(p=>p.Position.Y);
            float floor=MathF.Min(skeleton.BindPose[(int)CanonicalJoint.LeftFoot].Position.Y,
                skeleton.BindPose[(int)CanonicalJoint.RightFoot].Position.Y);
            float height=skeleton.BindPose[(int)CanonicalJoint.Head].Position.Y-floor;
            return TransitionPlacementMath.GroundHeightOffset(Low(source),Low(before),Low(candidate),floor,height,step,true);
        }
        if(!File.Exists(Path.Combine(AppContext.BaseDirectory,"app_data","slide","point_rig.json"))) return 0;
        var rig=new PointRig(skeleton);
        return TransitionPlacementMath.GroundHeightOffset(TransitionPlacementMath.LowestBodyPoint(rig.Points(source)),
            TransitionPlacementMath.LowestBodyPoint(rig.Points(before)),TransitionPlacementMath.LowestBodyPoint(rig.Points(candidate)),
            0,rig.BodyHeight,step,true);
    }
    internal static float MatchMomentumYaw(SkeletonDefinition skeleton, BoneTransform[] a,
        BoneTransform[] before, float aStep, BoneTransform[] b, BoneTransform[] after, float bStep, float duration)
    {
        if(skeleton.IsCanonical)
        {
            Vector3[] Points(BoneTransform[] pose) {
                var points=new Vector3[43];
                var map=new[]{(29,1),(20,2),(9,4),(6,5),(35,12),(22,15),(5,13),(12,16),
                    (10,14),(3,17),(18,14),(42,17),(28,6),(17,9),(11,7),(25,10),(39,8),(1,11)};
                foreach(var (p,j) in map)points[p]=pose[j].Position;
                return points;
            }
            return MomentumPlacement.Yaw(Points(a),Points(before),aStep,Points(b),Points(after),bStep,duration,skeleton.ReferenceHeight);
        }
        var rig=new PointRig(skeleton);
        return MomentumPlacement.Yaw(rig.Points(a),rig.Points(before),aStep,rig.Points(b),rig.Points(after),bStep,duration,rig.BodyHeight);
    }
    public static string[] StyleNames => SlideScriptNative.StyleNames;
    private static readonly Lazy<SlideScriptNative> Generator = new(SlideScriptNative.Open);
    private static SlideScriptNative GetGenerator() => Generator.Value;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Lazy<Task> Warmup = new(() => Task.Run(() =>
    {
        using var template = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "app_data", "slide", "point_rig.json")));
        var rest = template.RootElement.GetProperty("warmup_rest").EnumerateArray().Select(p => p.GetSingle()).ToArray();
        if (rest.Length != 43 * 3) throw new InvalidDataException("AI warmup needs 43 rest points.");
        var positions = new float[4 * rest.Length];
        for (int k = 0; k < 4; ++k) rest.CopyTo(positions, k * rest.Length);
        _ = GetGenerator().Generate(positions, rest, 16);
    }));

    // The shared task belongs to the app; cancelling one blend must not cancel
    // loading for every later request. Loading and the first inference run off UI.
    public static Task PreloadAsync() => Available ? Warmup.Value : Task.CompletedTask;
    public static bool Available => File.Exists(Path.Combine(AppContext.BaseDirectory, "SlideAi.dll")) &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "onnxruntime.dll")) &&
        Enumerable.Range(0, 5).All(i => File.Exists(Path.Combine(AppContext.BaseDirectory, "app_data", "slide", "neural", $"block{i}.onnx"))) &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "app_data", "slide", "point_rig.json"));

    public static async Task<IAnimationClip> GenerateAsync(StitchedAnimation stitched, Model model, uint version,
        float seconds, CancellationToken token, string styleHint = "Acrobatic")
    {
        if (!Available) throw new FileNotFoundException("The AI blend DLL or model is missing. Build with build-release.ps1.");
        if (!float.IsFinite(seconds) || seconds <= 0 || seconds > 10) throw new ArgumentOutOfRangeException(nameof(seconds));
        int style = SlideScriptNative.StyleCode(styleHint);
        await PreloadAsync().WaitAsync(token);
        await Gate.WaitAsync(token);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var full = (StitchedAnimation)GfdAnimationClipBaker.CreateTargetPreviewClip(stitched, model);
                // Keep the editor's coordinate-origin normalization. Grounded height
                // is corrected below using body points rather than the root pivot.
                var parts = full.CreateExportParts();
                var rig = new PointRig(full.Skeleton);
                int steps = Math.Max(1, (int)MathF.Round(seconds * full.FramesPerSecond));
                int count = Math.Max(2, steps + 1);
                parts = rig.PlaceCandidateWithTrajectory(parts.Source, parts.Candidate, steps/full.FramesPerSecond,
                    full.AlignPositionAndYaw, full.MatchUp, stitched.TrajectoryPersistence, full.YawVariationRadians);
                var generator = GetGenerator();
                float context = steps / (float)(count - 1);
                var a = Sample(parts.Source, parts.Source.FrameCount - 1);
                var b = Sample(parts.Candidate, 0);
                var keys = new[] { Sample(parts.Source, parts.Source.FrameCount - 1 - context), a,
                    b, Sample(parts.Candidate, context) };
                var positions = new float[4 * 43 * 3];
                for (int k = 0; k < 4; ++k) Flatten(rig.Points(keys[k]), positions, k * 43 * 3);
                var rest = new float[43 * 3]; Flatten(rig.Points(full.Skeleton.BindPose.ToArray()), rest, 0);
                token.ThrowIfCancellationRequested();
                var generated = generator.Generate(positions, rest, count, style);
                token.ThrowIfCancellationRequested();
                var aPoints = rig.Points(a); var bPoints = rig.Points(b);
                // Keep both supplied boundary poses, rather than round-off from inference.
                Flatten(aPoints, generated, 0); Flatten(bPoints, generated, (count - 1) * 43 * 3);
                var middle = new BoneTransform[steps + 1][]; middle[0] = a; middle[steps] = b;
                for (int f = 1; f < steps; ++f)
                {
                    token.ThrowIfCancellationRequested();
                    float u = f / (float)steps, time = u * (count - 1);
                    int first = (int)time, last = Math.Min(first + 1, count - 1);
                    var points = new Vector3[43];
                    for (int p = 0; p < 43; ++p)
                        points[p] = Vector3.Lerp(Read(generated, first * 129 + p * 3), Read(generated, last * 129 + p * 3), time - first);
                    var reference = rig.BlendLocal(a, b, u);
                    middle[f] = rig.Fit(reference, points);
                }
                var joined = new GeneratedClip(stitched.Id + ":ai", stitched.DisplayName + " (AI blend)", parts.Source, parts.Candidate, middle);
                // Preview uses the generated poses immediately. Bake the three GAP
                // parts only if the user asks to export them.
                return (IAnimationClip)new AiBlendAnimationClip(joined, () =>
                {
                    var pieces = new AnimationPack(version);
                    pieces.Animations.Add(GfdAnimationClipBaker.Bake(parts.Source, model, version));
                    pieces.Animations.Add(GfdAnimationClipBaker.BakeRange(joined, model, version, parts.Source.FrameCount - 1, steps + 1));
                    pieces.Animations.Add(GfdAnimationClipBaker.Bake(parts.Candidate, model, version));
                    return pieces;
                });
            }, token);
        }
        finally { Gate.Release(); }
    }
    private static BoneTransform[] Sample(IAnimationClip clip, float frame)
    {
        frame = Math.Clamp(frame, 0, clip.FrameCount - 1); int first = (int)frame;
        var a = new BoneTransform[clip.Skeleton.BoneCount]; clip.SampleGlobalPose(first, a);
        if (first == clip.FrameCount - 1 || frame == first) return a;
        var b = new BoneTransform[a.Length]; clip.SampleGlobalPose(first + 1, b);
        for (int i = 0; i < a.Length; ++i) a[i] = BoneTransform.Lerp(a[i], b[i], frame - first);
        return a;
    }
    private static Vector3 Read(float[] data, int offset) => new(data[offset], data[offset + 1], data[offset + 2]);
    private static void Flatten(Vector3[] points, float[] data, int offset)
    {
        foreach (var p in points) { data[offset++] = p.X * 100; data[offset++] = p.Y * 100; data[offset++] = p.Z * 100; }
    }
    private sealed class GeneratedClip : IAnimationClip
    {
        private readonly IAnimationClip a, b; private readonly BoneTransform[][] middle;
        public GeneratedClip(string id, string name, IAnimationClip source, IAnimationClip candidate, BoneTransform[][] poses)
        { Id = id; DisplayName = name; a = source; b = candidate; middle = poses; }
        public string Id { get; } public string DisplayName { get; }
        public SkeletonDefinition Skeleton => a.Skeleton; public float FramesPerSecond => a.FramesPerSecond;
        public int FrameCount => a.FrameCount + middle.Length - 2 + b.FrameCount;
        public void SampleGlobalPose(int frame, Span<BoneTransform> destination)
        {
            frame = Math.Clamp(frame, 0, FrameCount - 1);
            if (frame < a.FrameCount) { a.SampleGlobalPose(frame, destination); return; }
            int f = frame - (a.FrameCount - 1);
            if (f < middle.Length - 1) { middle[f].AsSpan().CopyTo(destination); return; }
            b.SampleGlobalPose(f - (middle.Length - 1), destination);
        }
    }
    private sealed class PointRig
    {
        private readonly SkeletonDefinition skeleton;
        private readonly float bodyHeight;
        public float BodyHeight => bodyHeight;
        private readonly int[] owners = new int[43];
        private readonly Vector3[] offsets = new Vector3[43];
        private readonly Matrix4x4[] inverseBindRotations;
        private readonly Dictionary<int, int[]> frames = new();
        private readonly Dictionary<int, int> limbFollowers = new();
        private readonly int pelvis;
        private readonly int motionRoot;
        private readonly int[] order;
        public PointRig(SkeletonDefinition definition)
        {
            skeleton = definition;
            var roles = new Dictionary<string, int>();
            for (int i = 0; i < skeleton.BoneCount; ++i)
            {
                var role = AnimationSkeletonRoles.GetRole(skeleton.BoneNames[i]);
                if (role != null && !roles.ContainsKey(role)) roles.Add(role, i);
            }
            int Bone(string name)
            {
                var role = AnimationSkeletonRoles.GetRole(name);
                if (role == null || !roles.TryGetValue(role, out int index)) throw new InvalidOperationException("AI blend needs the humanoid bone " + name);
                return index;
            }
            pelvis = Bone("pelvis"); motionRoot = skeleton.RootBoneIndex;
            var bind = skeleton.BindPose;
            var feet = (bind[Bone("foot_l")].Position + bind[Bone("foot_r")].Position) * .5f;
            var up = Unit(bind[Bone("head")].Position - feet);
            var lateral = bind[Bone("thigh_l")].Position - bind[Bone("thigh_r")].Position;
            var x = Unit(lateral - up * Vector3.Dot(up, lateral)); var z = Unit(Vector3.Cross(x, up));
            var body = Basis(x, up, z, Vector3.Zero);
            float height = Vector3.Distance(bind[Bone("head")].Position, feet);
            bodyHeight = height;
            inverseBindRotations = bind.Select(p => Matrix4x4.Transpose(Matrix4x4.CreateFromQuaternion(p.Rotation))).ToArray();
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "app_data", "slide", "point_rig.json")));
            var names = new Dictionary<string, int>();
            foreach (var point in document.RootElement.GetProperty("points").EnumerateArray())
            {
                int index = point.GetProperty("index").GetInt32(); names.Add(point.GetProperty("link").GetString()!, index);
                owners[index] = Bone(point.GetProperty("bone").GetString()!);
                var offset = point.GetProperty("offset").EnumerateArray().Select(v => v.GetSingle()).ToArray();
                offsets[index] = Vector3.TransformNormal(new Vector3(offset[0], offset[1], offset[2]) * height, body);
            }
            foreach (var row in document.RootElement.GetProperty("frames").EnumerateArray())
                frames.Add(Bone(row.GetProperty("bone").GetString()!), row.GetProperty("points").EnumerateArray().Select(v => names[v.GetString()!]).ToArray());
            // Royal twist bones are skinning drivers beside the main limb bones,
            // so ordinary parent inheritance cannot follow an AI-adjusted limb.
            for (int i = 0; i < skeleton.BoneCount; ++i)
            {
                var name = skeleton.BoneNames[i];
                foreach (var side in new[] { "L", "R" })
                foreach (var limb in new[] { "ThighTwist", "ForeTwist" })
                    name = name.Replace("Bip01 " + side + limb, "Bip01 " + side + " " + limb, StringComparison.OrdinalIgnoreCase);
                int split = name.IndexOf("ThighTwist", StringComparison.OrdinalIgnoreCase);
                string? driverName = split >= 0 ? name[..split] + "Thigh" : null;
                if (driverName == null)
                {
                    split = name.IndexOf("ForeTwist", StringComparison.OrdinalIgnoreCase);
                    if (split >= 0) driverName = name[..split] + "Forearm";
                }
                if (driverName == null) continue;
                int driver = Array.FindIndex(skeleton.BoneNames.ToArray(), n => string.Equals(n, driverName, StringComparison.OrdinalIgnoreCase));
                if (driver >= 0) limbFollowers.Add(i, driver);
            }
            // The dancing rig's knee/elbow helpers sit beside the calf/forearm.
            // Move their entire skinning branches with the fitted joint.
            foreach (var side in new[] { (prefix: "L_", leg: "LeftLeg", arm: "LeftForeArm"),
                                         (prefix: "R_", leg: "RightLeg", arm: "RightForeArm") })
            foreach (var pair in new[] { (helper: side.prefix + "Knee_Roll_01", driver: side.leg),
                                         (helper: side.prefix + "Elbow_Roll", driver: side.arm) })
            {
                int helper = Array.FindIndex(skeleton.BoneNames.ToArray(), n => string.Equals(n, pair.helper, StringComparison.OrdinalIgnoreCase));
                int driver = Array.FindIndex(skeleton.BoneNames.ToArray(), n => string.Equals(n, pair.driver, StringComparison.OrdinalIgnoreCase));
                if (helper >= 0 && driver >= 0) limbFollowers[helper] = driver;
            }
            int Depth(int i) { int count = 0; for (int p = skeleton.Parents[i]; p >= 0; p = skeleton.Parents[p]) if (++count > skeleton.BoneCount) throw new InvalidOperationException("Cyclic skeleton"); return count; }
            order = Enumerable.Range(0, skeleton.BoneCount).OrderBy(Depth).ToArray();
        }
        public Vector3[] Points(BoneTransform[] pose)
        {
            var points = new Vector3[43];
            for (int i = 0; i < points.Length; ++i)
            {
                int owner = owners[i];
                var delta = inverseBindRotations[owner] * Matrix4x4.CreateFromQuaternion(pose[owner].Rotation);
                points[i] = pose[owner].Position + Vector3.TransformNormal(offsets[i], delta);
            }
            return points;
        }
        public (IAnimationClip Source, IAnimationClip Candidate) PlaceCandidate(IAnimationClip source,
            IAnimationClip candidate, float duration, bool matchXZ, bool matchHeight)
            => PlaceCandidateWithTrajectory(source,candidate,duration,matchXZ,matchHeight,.5f);

        public (IAnimationClip Source, IAnimationClip Candidate) PlaceCandidateWithTrajectory(IAnimationClip source,
            IAnimationClip candidate, float duration, bool matchXZ, bool matchHeight, float persistence, float yawVariation = 0)
        {
            float step = 1/source.FramesPerSecond;
            var history = new Vector3[9];
            int last = source.FrameCount-1;
            for (int i=0;i<history.Length;++i)
                history[i] = MomentumPlacement.Center(Points(Sample(source,last-(8-i))));
            var a = Points(Sample(source,last));
            var before = Points(Sample(source,last-1));
            var b = Points(Sample(candidate,0));
            int aSpan=Math.Min(4,last),bSpan=Math.Min(4,candidate.FrameCount-1);
            float yaw=matchXZ?MomentumPlacement.Yaw(a,Points(Sample(source,last-aSpan)),aSpan*step,
                b,Points(Sample(candidate,bSpan)),bSpan/candidate.FramesPerSecond,duration,bodyHeight)+yawVariation:0;
            var rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,yaw);
            var centerB=MomentumPlacement.Center(b);
            var futureB = MomentumPlacement.Center(Points(Sample(candidate,duration*candidate.FramesPerSecond)));
            var projected = TransitionPlacementMath.BlendTrajectory(history,step,duration,
                Vector3.Transform(futureB-centerB,rotation),persistence);
            var placedCenter=Vector3.Transform(centerB,rotation);
            var shift = matchXZ ? new Vector3(projected.X-placedCenter.X,0,projected.Z-placedCenter.Z) : Vector3.Zero;
            shift.Y = TransitionPlacementMath.GroundHeightOffset(TransitionPlacementMath.LowestBodyPoint(a),
                TransitionPlacementMath.LowestBodyPoint(before),TransitionPlacementMath.LowestBodyPoint(b),
                0,bodyHeight,step,matchHeight);
            return (source,new StitchedAnimationPart(candidate,0,candidate.FrameCount,rotation,
                shift,candidate.DisplayName));
        }
        public BoneTransform[] BlendLocal(BoneTransform[] a, BoneTransform[] b, float u)
        {
            var result = new BoneTransform[a.Length];
            HierarchyPoseBlend.Blend(skeleton, order, a, b, u, result);
            return result;
        }
        public BoneTransform[] Fit(BoneTransform[] reference, Vector3[] scenePoints)
        {
            var points = scenePoints.Select(p => p / 100).ToArray(); var source = Points(reference);
            var desired = new Dictionary<int, Matrix4x4>();
            foreach (var row in frames)
            {
                if (!TryFrame(source, row.Value, out var before) || !TryFrame(points, row.Value, out var after)) continue;
                desired[row.Key] = Matrix(reference[row.Key]) * Inverse(before) * after;
            }
            foreach (var row in limbFollowers)
                if (desired.TryGetValue(row.Value, out var driver))
                    desired[row.Key] = Matrix(reference[row.Key]) * Inverse(Matrix(reference[row.Value])) * driver;
            var result = new BoneTransform[reference.Length]; var worlds = new Matrix4x4[reference.Length];
            Vector3 shift = desired.TryGetValue(pelvis, out var hip) ? hip.Translation - reference[pelvis].Position : Vector3.Zero;
            foreach (int i in order)
            {
                int parent = skeleton.Parents[i];
                var original = Matrix(reference[i]);
                var local = Transform(parent < 0 ? original : original * Inverse(Matrix(reference[parent])));
                if (desired.TryGetValue(i, out var fitted))
                {
                    var fit = Transform(fitted);
                    Quaternion rotation = parent < 0 ? fit.Rotation : Transform(Matrix4x4.CreateFromQuaternion(fit.Rotation) * Inverse(Matrix4x4.CreateFromQuaternion(result[parent].Rotation))).Rotation;
                    var position = i == pelvis || limbFollowers.ContainsKey(i) ? (parent < 0 ? fitted.Translation : Vector3.Transform(fitted.Translation, Inverse(worlds[parent]))) : local.Position;
                    local = new BoneTransform(position, rotation, local.Scale);
                }
                else if (i == motionRoot && i != pelvis)
                {
                    var moved = original; moved.Translation += new Vector3(shift.X, 0, shift.Z);
                    local = Transform(parent < 0 ? moved : moved * Inverse(worlds[parent]));
                }
                worlds[i] = parent < 0 ? Matrix(local) : Matrix(local) * worlds[parent]; result[i] = Transform(worlds[i]);
            }
            return result;
        }
        private static bool TryFrame(Vector3[] points, int[] indices, out Matrix4x4 frame)
        {
            var a = points[indices[0]]; var axis = points[indices[1]] - a;
            var cross = Vector3.Cross(axis, points[indices[2]] - a);
            if (axis.LengthSquared() < 1e-8f || cross.LengthSquared() < 1e-8f) { frame = default; return false; }
            var x = Unit(axis); var z = Unit(cross); var y = Unit(Vector3.Cross(z, x));
            frame = Basis(x, y, z, a); return true;
        }
        private static Vector3 Unit(Vector3 value) => value.LengthSquared() > 1e-8f ? Vector3.Normalize(value) : throw new InvalidOperationException("Degenerate humanoid bind pose");
        private static Matrix4x4 Basis(Vector3 x, Vector3 y, Vector3 z, Vector3 p) => new(x.X,x.Y,x.Z,0,y.X,y.Y,y.Z,0,z.X,z.Y,z.Z,0,p.X,p.Y,p.Z,1);
        private static Matrix4x4 Matrix(BoneTransform p) => Matrix4x4.CreateFromQuaternion(p.Rotation) * Matrix4x4.CreateScale(p.Scale) * Matrix4x4.CreateTranslation(p.Position);
        private static Matrix4x4 Inverse(Matrix4x4 value) => Matrix4x4.Invert(value, out var result) ? result : throw new InvalidOperationException("Singular bone matrix");
        private static BoneTransform Transform(Matrix4x4 value)
        {
            if (Matrix4x4.Decompose(value, out var scale, out var rotation, out var position))
                return new BoneTransform(position, Quaternion.Normalize(rotation), scale);
            // Dividing world poses with different nonuniform scales introduces shear.
            // BoneTransform stores PRS only: extract orthogonal axes rather than fail
            // on an otherwise finite, nonsingular pose.
            var x = new Vector3(value.M11, value.M12, value.M13);
            var y = new Vector3(value.M21, value.M22, value.M23);
            var z = new Vector3(value.M31, value.M32, value.M33);
            float sx = x.Length();
            if (!float.IsFinite(sx) || sx < 1e-8f) throw new InvalidOperationException("Invalid bone matrix");
            x /= sx;
            y -= x * Vector3.Dot(x, y);
            float sy = y.Length();
            if (!float.IsFinite(sy) || sy < 1e-8f) throw new InvalidOperationException("Invalid bone matrix");
            y /= sy;
            var normal = Vector3.Cross(x, y);
            float sz = Vector3.Dot(z, normal);
            if (!float.IsFinite(sz) || MathF.Abs(sz) < 1e-8f) throw new InvalidOperationException("Invalid bone matrix");
            return new BoneTransform(value.Translation,
                Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(Basis(x, y, normal, Vector3.Zero))),
                new Vector3(sx, sy, sz));
        }
    }
}
