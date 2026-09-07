using System;
using System.Collections.Generic;
using System.Linq;
using GFDLibrary.Models;

namespace GFDLibrary.Animations
{
    /// <summary>
    /// Public semantic-name boundary shared by retargeting and model-independent AniMatch.
    /// </summary>
    public static class AnimationSkeletonRoles
    {
        public const string MotionRootRole = "motionroot";
        public const string RootRole = "root";
        public const string FileRootRole = "rootnode";

        public static string GetRole(string nodeName)
        {
            return AnimationRetargetMap.GetSkeletonRoleForMatching(nodeName);
        }

        /// <summary>
        /// Resolves the node that carries locomotion for a model. Bip01 is used by
        /// dancing-style rigs, root by P5-style rigs, and the file root is only a
        /// fallback for models that have neither semantic motion-root name.
        /// </summary>
        public static Node ResolveMotionRoot(Model model)
        {
            if (model == null)
                throw new ArgumentNullException(nameof(model));

            return ResolveMotionRoot(model.Nodes, model.RootNode);
        }

        public static Node ResolveMotionRoot(IEnumerable<Node> nodes)
        {
            return ResolveMotionRoot(nodes, null);
        }

        /// <summary>
        /// Resolves a semantic motion root from a node set. The fallback is used
        /// only when no Bip01 or root node is present.
        /// </summary>
        public static Node ResolveMotionRoot(IEnumerable<Node> nodes, Node fallback)
        {
            if (nodes == null)
                throw new ArgumentNullException(nameof(nodes));

            var nodeArray = nodes.ToArray();
            return nodeArray.FirstOrDefault(node => GetRole(node.Name) == MotionRootRole) ??
                   nodeArray.FirstOrDefault(node => GetRole(node.Name) == RootRole) ??
                   fallback;
        }
    }
}
