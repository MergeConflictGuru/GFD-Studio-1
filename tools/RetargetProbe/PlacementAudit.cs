using System.Reflection;
using System.Runtime.Loader;
using System.Numerics;
using GFDLibrary;
using GFDLibrary.Animations;
using GFDLibrary.Models;

internal static class PlacementAudit
{
    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        var assembly=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory,"GFDStudio.dll"));
        var clipType=assembly.GetType("GFDStudio.AnimationMatching.Integration.GfdTargetAnimationClip")!;
        var stitchType=assembly.GetType("GFDStudio.AnimationMatching.Stitching.StitchedAnimation")!;
        var bake=assembly.GetType("GFDStudio.AnimationMatching.Integration.GfdAnimationClipBaker")!.GetMethod("Bake")!;
        var results=new List<object>();
        foreach(var id in new[]{204,205})
        {
            var model=Resource.Load<ModelPack>($@"M:\_P_backup\p5d modding\game\Image0\data\ps4\dance\player\p5\pc{id}_26.GMD");
            var body=id==204?Resource.Load<AnimationPack>(@"M:\_P_backup\p5d modding\game\Image0\data\data\dance\player\p5\204\pc204_007.GAP").Animations[9]:
                Resource.Load<AnimationPack>(@"M:\_P_backup\p5d modding\game\Image0\data\data\dance\player\p5\205\pc205_001_p.GAP").Animations[0];
            var candidate=id==204?Resource.Load<AnimationPack>(@"C:\_coding\DaYoBuO\modssrc\dayobuo\pc204_ko_p.GAP").Animations[3]:body;
            var a=Activator.CreateInstance(clipType,new object[]{"a","source",model.Model,body,30f})!;
            var b=Activator.CreateInstance(clipType,new object[]{"b","candidate",model.Model,candidate,30f})!;
            var nodes=model.Model.Nodes.ToArray();var root=AnimationSkeletonRoles.ResolveMotionRoot(model.Model);
            bool MovesWithRoot(Node node) {
                for(var parent=node;parent!=null;parent=parent.Parent)if(parent==root)return true;
                return false;
            }
            float Facing(Dictionary<Node,Matrix4x4> pose) {
                Matrix4x4.Decompose(pose[root],out _,out var q,out _);
                return AnimationFacing.YawRadians(nodes.Select(n=>n.Name).ToArray(),i=>pose[nodes[i]].Translation,q);
            }
            var cut=id==204?436:30;var entry=id==204?0:120;
            var sourcePose=AnimationPoseEvaluator.Evaluate(model.Model,body,cut/30f);
            var rawPose=AnimationPoseEvaluator.Evaluate(model.Model,candidate,entry/30f);
            var faceFile=$@"M:\_P_backup\p5d modding\game\Image0\data\ps4\dance\player\p5\face\pc{id}_f1.GMD";
            var parts=File.Exists(faceFile)?new[]{Resource.Load<ModelPack>(faceFile).Model}:System.Array.Empty<Model>();
            foreach(var option in new[]{(up:false,jitter:0f),(up:true,jitter:0f),(up:false,jitter:15f)})
            {
                var stitch=Activator.CreateInstance(stitchType,new object[]{a,cut,b,entry,0f,true,option.up,option.jitter})!;
                var split=stitchType.GetMethod("CreateExportParts")!.Invoke(stitch,null)!;
                var part=split.GetType().GetField("Item2")!.GetValue(split)!;
                var placed=(Animation)bake.Invoke(null,new object[]{part,model.Model,model.Version,CancellationToken.None})!;
                var pose=AnimationPoseEvaluator.Evaluate(model.Model,placed,0);
                var delta=Facing(pose)-Facing(sourcePose);delta=MathF.Atan2(MathF.Sin(delta),MathF.Cos(delta))*180/MathF.PI;
                var yError=MathF.Abs(pose[root].Translation.Y-(option.up?sourcePose[root].Translation.Y:rawPose[root].Translation.Y));
                if(yError>.02f || MathF.Abs(delta)>option.jitter+.02f)throw new Exception($"Placement failed on {id}: yaw {delta}, height error {yError}");
                var offset=option.up?sourcePose[root].Translation.Y-rawPose[root].Translation.Y:0;
                float maxHeightError=0;string worstBone="";int worstFrame=0;
                for(var frame=0;frame<=Math.Round(placed.Duration*30);frame++)
                {
                    var original=AnimationPoseEvaluator.Evaluate(model.Model,candidate,(entry+frame)/30f);
                    var changed=AnimationPoseEvaluator.Evaluate(model.Model,placed,frame/30f);
                    foreach(var n in nodes) {
                        var e=MathF.Abs(changed[n].Translation.Y-original[n].Translation.Y-(MovesWithRoot(n)?offset:0));
                        if(e>maxHeightError){maxHeightError=e;worstBone=n.Name;worstFrame=frame;}
                    }
                }
                if(maxHeightError>.04f)throw new Exception($"Vertical motion changed on {id}, up={option.up}: {maxHeightError} on {worstBone} frame {worstFrame}");
                var tag=$"pc{id}_height{option.up}_jitter{option.jitter}";
                var pack=new AnimationPack(model.Version);pack.Animations.Add(placed);pack.Save(Path.Combine(folder,tag+".GAP"));
                foreach(var frame in new[]{0,30,60}.Where(f=>f<=Math.Round(placed.Duration*30)))
                    PoseRender.Draw(model.Model,sourcePose,model.Model,AnimationPoseEvaluator.Evaluate(model.Model,placed,frame/30f),
                        Path.Combine(folder,tag+$"_f{frame}.png"),$"{id}: source cut / B frame {entry+frame}, height match {option.up}, yaw variation {option.jitter} degrees",parts,centerEachPose:true);
                results.Add(new{id,option.up,option.jitter,yawErrorDegrees=delta,yError,maxHeightError});
            }
        }
        File.WriteAllText(Path.Combine(folder,"placement_audit.json"),System.Text.Json.JsonSerializer.Serialize(results,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        return 0;
    }
}
