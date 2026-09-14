using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GFDLibrary.Models;

namespace GFDLibrary.Animations
{
    internal static class HumanoidAnimationRetargeter
    {
        internal static void Bake(Animation animation, AnimationRetargetMap map, bool useLocalBindSpace)
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

            // Keep the reverse lookup too. When a source joint and its parent map
            // directly to a target joint and its parent, the joint animation can
            // be transferred in local bind space. That is important for P5/R <->
            // Dance limbs: the semantic bones correspond, but their local axes do
            // not (notably the calf/foot and forearm/hand chains).
            var reverseMapping = mapping.ToDictionary(pair => pair.Value, pair => pair.Key);

            var motionRoot = AnimationSkeletonRoles.ResolveMotionRoot(targets.Where(mapping.ContainsKey));
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
                        Quaternion localRotation;
                        if (useLocalBindSpace && CanTransferLocalRotation(source, target, reverseMapping))
                        {
                            // A contiguous semantic chain should be retargeted as
                            // a local animation delta. Reconstructing every bone
                            // independently in model space couples a child's bend
                            // to its parent's different bind axes. On the real P5D
                            // and P5R rigs that made knees/elbows twist as soon as
                            // the calf/foot or forearm/hand began moving.
                            var sourceBindLocalRotation = Rotation(source.LocalTransform);
                            Matrix4x4.Invert(sourceBindLocalRotation, out var inverseSourceBindLocalRotation);
                            var sourceAnimatedLocalRotation = Matrix4x4.CreateFromQuaternion(
                                EvaluateLocalRotation(source, animation, time));
                            var desiredLocalRotation = Rotation(target.LocalTransform) *
                                                       inverseSourceBindLocalRotation *
                                                       sourceAnimatedLocalRotation;
                            localRotation = Quaternion.Normalize(
                                Quaternion.CreateFromRotationMatrix(desiredLocalRotation));
                        }
                        else
                        {
                            // If the semantic parent chain differs, stay in model
                            // space so motion from collapsed/unmapped ancestors is
                            // preserved. This is needed around the two games' root
                            // and spine hierarchy differences.
                            var bindRotation = Rotation(sourceBind[source]);
                            Matrix4x4.Invert(bindRotation, out var inverseBindRotation);
                            var desiredRotation = Rotation(targetBind[target]) *
                                                  inverseBindRotation *
                                                  Rotation(sourcePose[source]);
                            Matrix4x4.Invert(Rotation(parentWorld), out var inverseParentRotation);
                            localRotation = Quaternion.Normalize(
                                Quaternion.CreateFromRotationMatrix(desiredRotation * inverseParentRotation));
                        }

                        var position = target.Translation;
                        if (target == motionRoot)
                        {
                            // Preserve the target rig's bind position and apply
                            // the source's animated world-space displacement.
                            // The evaluated model poses already include each
                            // game's coordinate-conversion ancestors; applying
                            // those rotations again would rotate root motion a
                            // second time and lift some Dance poses.
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

        private static bool CanTransferLocalRotation(
            Node source,
            Node target,
            IReadOnlyDictionary<Node, Node> reverseMapping)
        {
            if (source?.Parent == null || target?.Parent == null)
                return false;

            return reverseMapping.TryGetValue(source.Parent, out var mappedSourceParent) &&
                   ReferenceEquals(mappedSourceParent, target.Parent);
        }

        private static Quaternion EvaluateLocalRotation(Node node, Animation animation, float time)
        {
            var position = node.Translation;
            var rotation = node.Rotation;
            var scale = node.Scale;
            foreach (var controller in animation.Controllers)
            {
                if (controller.TargetKind != TargetKind.Node ||
                    !string.Equals(controller.TargetName, node.Name, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var layer in controller.Layers)
                    AnimationPoseEvaluator.Sample(layer, time, ref position, ref rotation, ref scale);
            }

            return Quaternion.Normalize(rotation);
        }

        private static Matrix4x4 Rotation(Matrix4x4 transform)
        {
            if (!Matrix4x4.Decompose(transform, out _, out var rotation, out _))
                throw new InvalidOperationException("Cannot retarget a singular or sheared skeleton transform.");
            return Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation));
        }
    }
}
