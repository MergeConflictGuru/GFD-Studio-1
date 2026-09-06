using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GFDLibrary.Models;

namespace GFDLibrary.Animations
{
    /// <summary>
    /// Transfers a Dance rig's authored knee correctives as a function of knee
    /// flexion. The reference must be a native base animation for this target rig,
    /// not the costume, face or hair overlay. This is a pose-space approximation,
    /// not a replacement for the original rig's constraints or cloth simulation.
    /// </summary>
    public static class DancingKneeCorrection
    {
        public static void Apply(AnimationPack pack, Model target, Animation reference)
        {
            if (reference == null || reference.Duration <= 0)
                throw new ArgumentException("A nonempty native Dance base animation is required.", nameof(reference));
            var nodes = target.Nodes.ToArray();
            var byName = nodes.ToDictionary(n => n.Name);
            var curves = new List<(Node leg, Node[] helpers, (float angle, Matrix4x4[] locals)[] samples)>();
            foreach (var (side, prefix) in new[] { ("Left", "L_"), ("Right", "R_") })
            {
                if (!byName.TryGetValue(side + "Leg", out var leg)) continue;
                var names = new[] {prefix + "Knee_Roll_01", prefix + "Knee_Roll_02", prefix + "ExKnee"};
                if (names.Any(n => !byName.ContainsKey(n))) continue;
                var helpers = names.Select(n => byName[n]).ToArray();
                if (names.Append(leg.Name).Any(n => !reference.Controllers.Any(c => c.TargetKind == TargetKind.Node && c.TargetName == n)))
                    throw new ArgumentException("Reference lacks the Dance calf/knee corrective tracks. Select the base GAP.", nameof(reference));
                var samples = new Dictionary<int, (float angle, Matrix4x4[] locals)>();
                // Restrict evaluation to the required node controllers. Node
                // transforms still include every ancestor in the target model.
                var relevant = new Animation(reference.Version) {
                    Controllers = reference.Controllers.Where(c => names.Contains(c.TargetName) || c.TargetName == leg.Name).ToList()
                };
                for (var frame = 0; frame / 30f <= reference.Duration; frame++)
                {
                    var pose = AnimationPoseEvaluator.Evaluate(target, relevant, frame / 30f);
                    var angle = Flexion(Local(leg, pose));
                    var bin = (int)Math.Round(angle * 200); // 0.005 radians
                    if (!samples.ContainsKey(bin))
                        samples.Add(bin, (angle, helpers.Select(n => Local(n, pose)).ToArray()));
                }
                if (samples.Count < 2)
                    throw new ArgumentException("Reference does not contain enough knee flexion to calibrate correctives.", nameof(reference));
                curves.Add((leg, helpers, samples.Values.OrderBy(s => s.angle).ToArray()));
            }
            if (curves.Count == 0)
                throw new ArgumentException("Target does not contain the supported Dance knee helper hierarchy.", nameof(target));

            foreach (var animation in pack.Animations)
            {
                // Read the unmodified baked pose while collecting new tracks.
                var additions = new List<AnimationController>();
                foreach (var (leg, helpers, samples) in curves)
                {
                    var output = helpers.Select(n => new AnimationController(animation.Version) {
                        TargetKind = TargetKind.Node, TargetName = n.Name, TargetId = Array.IndexOf(nodes, n),
                        Layers = { new AnimationLayer(animation.Version) { KeyType = KeyType.NodePRS } }
                    }).ToArray();
                    var times = animation.Controllers.SelectMany(c => c.Layers).Where(l => l.HasPRSKeyFrames)
                        .SelectMany(l => l.Keys).Select(k => k.Time).Distinct().OrderBy(t => t);
                    foreach (var time in times)
                    {
                        var angle = Flexion(Local(leg, AnimationPoseEvaluator.Evaluate(target, animation, time)));
                        var upper = Array.FindIndex(samples, s => s.angle >= angle);
                        if (upper < 0) upper = samples.Length - 1;
                        var lower = Math.Max(0, upper - 1);
                        var span = samples[upper].angle - samples[lower].angle;
                        var amount = span > 0 ? Math.Clamp((angle - samples[lower].angle) / span, 0, 1) : 0;
                        for (var i = 0; i < helpers.Length; i++)
                        {
                            Matrix4x4.Decompose(samples[lower].locals[i], out var s0, out var r0, out var p0);
                            Matrix4x4.Decompose(samples[upper].locals[i], out var s1, out var r1, out var p1);
                            output[i].Layers[0].Keys.Add(new PRSKey(KeyType.NodePRS) {
                                Time = time, Position = Vector3.Lerp(p0, p1, amount),
                                Rotation = Quaternion.Normalize(Quaternion.Slerp(r0, r1, amount)), Scale = Vector3.Lerp(s0, s1, amount)
                            });
                        }
                    }
                    additions.AddRange(output);
                }
                var replaced = additions.Select(c => c.TargetName).ToHashSet();
                animation.Controllers.RemoveAll(c => c.TargetKind == TargetKind.Node && replaced.Contains(c.TargetName));
                animation.Controllers.AddRange(additions);
            }
        }

        private static Matrix4x4 Local(Node node, Dictionary<Node, Matrix4x4> pose)
        {
            if (node.Parent == null) return pose[node];
            Matrix4x4.Invert(pose[node.Parent], out var inverse);
            return pose[node] * inverse;
        }

        private static float Flexion(Matrix4x4 local)
        {
            Matrix4x4.Decompose(local, out _, out var rotation, out _);
            // Native Dance knee hinges rotate about local Z. Extract swing in
            // that plane rather than including the limb's world-space rotation.
            var x = Vector3.Transform(Vector3.UnitX, rotation);
            return MathF.Atan2(x.Y, x.X);
        }
    }
}
