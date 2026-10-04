using System;
using System.Collections.Generic;
using System.Numerics;

namespace GFDLibrary.Animations;

/// <summary>Character facing from anatomical joints, independent of rig root axes.</summary>
public static class AnimationFacing
{
    public static float YawRadians(IReadOnlyList<string> names, Func<int, Vector3> position, Quaternion rootRotation)
    {
        int Find(params string[] roles)
        {
            foreach (var role in roles)
                for (var i=0;i<names.Count;i++)
                    if (string.Equals(AnimationSkeletonRoles.GetRole(names[i]) ?? names[i], role, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
        var left=Find("leftarm", "leftshoulder"); var right=Find("rightarm", "rightshoulder");
        var hips=Find("hips", "pelvis"); var upper=Find("neck", "spine2", "upperSpine", "head");
        if(left>=0 && right>=0)
        {
            var across=position(right)-position(left);
            var up=hips>=0 && upper>=0 ? position(upper)-position(hips) : Vector3.UnitY;
            var forward=Vector3.Cross(across,up);
            if(forward.X*forward.X+forward.Z*forward.Z < forward.LengthSquared()*.01f)
                forward=Vector3.Cross(across,Vector3.UnitY);
            if(forward.X*forward.X+forward.Z*forward.Z > 1e-10f)
                return MathF.Atan2(forward.X,forward.Z);
        }
        var direction=Vector3.Transform(Vector3.UnitZ,rootRotation);
        return MathF.Atan2(direction.X,direction.Z);
    }
}
