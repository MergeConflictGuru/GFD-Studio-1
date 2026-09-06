using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GFDLibrary.Models;

namespace GFDLibrary.Animations
{
    internal static class HumanoidAnimationRetargeter
    {
        internal static void Bake(Animation animation, AnimationRetargetMap map)
        {
            var sourceBind = AnimationPoseEvaluator.Evaluate(map.SourceModel, null, 0);
            var targetBind = AnimationPoseEvaluator.Evaluate(map.TargetModel, null, 0);
            var targets = map.TargetModel.Nodes.ToArray();
            var mapping = new Dictionary<Node, Node>();
            foreach (var source in map.SourceModel.Nodes)
                if (map.TryGetTarget(source.Name, out _, out var target) && !mapping.ContainsKey(target))
                    mapping.Add(target, source);
            if (mapping.Count < 4)
                throw new InvalidOperationException("Not enough corresponding humanoid bones to retarget this animation.");

            var motionRoot = targets.FirstOrDefault(n => mapping.ContainsKey(n) &&
                (n.Name == "root" || n.Name == "Bip01"));
            var heightRatio = 1f;
            if (motionRoot != null)
            {
                var sourceHeight = sourceBind[mapping[motionRoot]].Translation.Y;
                if (Math.Abs(sourceHeight) > 0.001f)
                    heightRatio = Math.Abs(targetBind[motionRoot].Translation.Y / sourceHeight);
            }

            var output = new Dictionary<Node, AnimationController>();
            foreach (var target in targets.Where(mapping.ContainsKey))
            {
                map.TryGetTargetId(target, out var id);
                var controller = new AnimationController(animation.Version) {
                    TargetKind = TargetKind.Node, TargetName = target.Name, TargetId = id
                };
                controller.Layers.Add(new AnimationLayer(animation.Version) { KeyType = KeyType.NodePRS });
                output.Add(target, controller);
            }

            // Bake at 60 Hz and retain original key times. This is necessary for
            // collapsed ancestors and different parent chains, not just aliases.
            var times = new SortedSet<float> { 0, animation.Duration };
            for (var frame = 1; frame / 60f < animation.Duration; frame++) times.Add(frame / 60f);
            foreach (var key in animation.Controllers.Where(c => c.TargetKind == TargetKind.Node)
                .SelectMany(c => c.Layers).Where(l => l.HasPRSKeyFrames).SelectMany(l => l.Keys))
                if (key.Time >= 0 && key.Time <= animation.Duration) times.Add(key.Time);

            foreach (var time in times)
            {
                var sourcePose = AnimationPoseEvaluator.Evaluate(map.SourceModel, animation, time);
                var targetPose = new Dictionary<Node, Matrix4x4>();
                foreach (var target in targets)
                {
                    var parentWorld = target.Parent == null ? Matrix4x4.Identity : targetPose[target.Parent];
                    var local = target.LocalTransform;
                    if (mapping.TryGetValue(target, out var source))
                    {
                        var bindRotation = Rotation(sourceBind[source]);
                        Matrix4x4.Invert(bindRotation, out var inverseBindRotation);
                        var desiredRotation = Rotation(targetBind[target]) * inverseBindRotation * Rotation(sourcePose[source]);
                        Matrix4x4.Invert(Rotation(parentWorld), out var inverseParentRotation);
                        var localRotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(desiredRotation * inverseParentRotation));
                        var position = target.Translation;
                        if (target == motionRoot)
                        {
                            var worldPosition = targetBind[target].Translation +
                                (sourcePose[source].Translation - sourceBind[source].Translation) * heightRatio;
                            Matrix4x4.Invert(parentWorld, out var inverseParent);
                            position = Vector3.Transform(worldPosition, inverseParent);
                        }
                        // Child offsets belong to the target skeleton. Copying
                        // source local translations changes bone lengths/axes.
                        local = Matrix4x4.CreateFromQuaternion(localRotation) * Matrix4x4.CreateScale(target.Scale);
                        local.Translation = position;
                        output[target].Layers[0].Keys.Add(new PRSKey(KeyType.NodePRS) {
                            Time = time, Position = position, Rotation = localRotation, Scale = target.Scale
                        });
                    }
                    targetPose[target] = local * parentWorld;
                }
            }
            // Material/morph/visibility tracks refer to source meshes; they are
            // not portable to the separately packaged Dance body, face and hair.
            animation.Controllers = targets.Where(output.ContainsKey).Select(n => output[n]).ToList();
        }

        private static Matrix4x4 Rotation(Matrix4x4 transform)
        {
            if (!Matrix4x4.Decompose(transform, out _, out var rotation, out _))
                throw new InvalidOperationException("Cannot retarget a singular or sheared skeleton transform.");
            return Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation));
        }
    }
}
