using System;

namespace GFDLibrary.Animations
{
    /// <summary>
    /// Public semantic-name boundary shared by retargeting and model-independent AniMatch.
    /// </summary>
    public static class AnimationSkeletonRoles
    {
        public static string GetRole(string nodeName)
        {
            return AnimationRetargetMap.GetSkeletonRoleForMatching(nodeName);
        }
    }
}
