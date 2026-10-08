using System;
using System.Numerics;
namespace GFDStudio.AnimationMatching.Stitching;

// Keep the equations and segment masses equal to SLIDE/cpp/momentum_match.h.
internal static class MomentumPlacement
{
    private static readonly (int A,int B,float Mass)[] Segments = {
        (29,20,.20f),(20,9,.30f),(9,6,.08f),(35,5,.10f),(22,12,.10f),
        (5,10,.045f),(12,3,.045f),(10,18,.015f),(3,42,.015f),
        (28,11,.028f),(17,25,.028f),(11,39,.016f),(25,1,.016f),(39,39,.006f),(1,1,.006f)};
    private static Vector3 Segment(Vector3[] p,int i)=>(p[Segments[i].A]+p[Segments[i].B])*.5f;
    public static Vector3 Center(Vector3[] p) {
        Vector3 c=default;
        for(int i=0;i<Segments.Length;i++)c+=Segment(p,i)*Segments[i].Mass;
        return c;
    }
    private static Vector3 Unit(Vector3 v)=>v.Length()>1e-8f?Vector3.Normalize(v):Vector3.Zero;
    private static Vector3[] Axes(Vector3[] p) {
        var hip=Unit(p[35]-p[22]);var chest=Unit(p[28]-p[17]);
        return new[]{hip,Unit(Vector3.Cross(hip,Unit(p[20]-p[29]))),
            chest,Unit(Vector3.Cross(chest,Unit(p[9]-p[20])))};
    }
    private static Vector3 Rotate(Vector3 p,float yaw)=>Vector3.Transform(p,Quaternion.CreateFromAxisAngle(Vector3.UnitY,yaw));
    private static void Correlation(Vector3 from,Vector3 to,float weight,ref float c,ref float s) {
        c+=weight*(from.X*to.X+from.Z*to.Z);
        s+=weight*(from.Z*to.X-from.X*to.Z);
    }
    public static float Turn(Vector3[] before,Vector3[] now) {
        var a=Axes(before);var b=Axes(now);float c=0,s=0;
        for(int i=0;i<4;i++)Correlation(a[i],b[i],1,ref c,ref s);
        return c*c+s*s>1e-12f?MathF.Atan2(s,c):0;
    }
    private static Vector3 BoundedVelocity(Vector3 v,float height) {
        float n=v.Length(),limit=2*height;
        return n>limit?v*(limit/n):v;
    }
    public static float Yaw(Vector3[] a,Vector3[] before,float aStep,Vector3[] b,
        Vector3[] after,float bStep,float duration,float height) {
        var ca=Center(a);var cb=Center(b);float c=0,s=0;
        float predictedTurn=aStep>0?Turn(before,a)*MathF.Min(duration,.5f)/aStep:0;
        const float kineticTime=.2f;
        for(int i=0;i<Segments.Length;i++) {
            Correlation(Segment(b,i)-cb,Rotate(Segment(a,i)-ca,predictedTurn),Segments[i].Mass,ref c,ref s);
            if(aStep>0 && bStep>0) {
                var va=BoundedVelocity((Segment(a,i)-Segment(before,i))/aStep,height);
                var vb=BoundedVelocity((Segment(after,i)-Segment(b,i))/bStep,height);
                Correlation(vb,va,Segments[i].Mass*kineticTime*kineticTime,ref c,ref s);
            }
        }
        var aa=Axes(a);var bb=Axes(b);
        for(int i=0;i<4;i++)Correlation(bb[i],Rotate(aa[i],predictedTurn),.04f*height*height,ref c,ref s);
        return c*c+s*s>1e-12f?MathF.Atan2(s,c):0;
    }
}
