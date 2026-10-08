using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using GFDLibrary.Models;

namespace GFDLibrary.Animations;

public sealed class UnrealRigConversion
{
    public Model Model { get; internal set; }
    public AnimationPack Animations { get; internal set; }
}

/// <summary>Builds the UE4 mannequin hierarchy without editing the game model.</summary>
public static class UnrealRigConverter
{
    private sealed record Joint(string Name, string Parent, string Role, string Aim = null);
    private sealed record Driver(Node Source, Matrix4x4 Basis);

    private static Joint[] Layout()
    {
        var joints = new List<Joint> {
            new("root", null, "motionroot"), new("pelvis", "root", "hips", "spine"),
            new("spine_01", "pelvis", "spine", "spine1"), new("spine_02", "spine_01", "spine1", "spine2"),
            new("spine_03", "spine_02", "spine2", "neck"), new("neck_01", "spine_03", "neck", "head"),
            new("head", "neck_01", "head")
        };
        foreach (var (side, suffix) in new[] { ("left", "l"), ("right", "r") })
        {
            joints.AddRange(new[] {
                new Joint("clavicle_"+suffix,"spine_03",side+"shoulder",side+"arm"),
                new Joint("upperarm_"+suffix,"clavicle_"+suffix,side+"arm",side+"forearm"),
                new Joint("lowerarm_"+suffix,"upperarm_"+suffix,side+"forearm",side+"hand"),
                new Joint("hand_"+suffix,"lowerarm_"+suffix,side+"hand",side+"middle1"),
                new Joint("thigh_"+suffix,"pelvis",side+"upleg",side+"leg"),
                new Joint("calf_"+suffix,"thigh_"+suffix,side+"leg",side+"foot"),
                new Joint("foot_"+suffix,"calf_"+suffix,side+"foot",side+"toe"),
                new Joint("ball_"+suffix,"foot_"+suffix,side+"toe")
            });
            foreach (var finger in new[] { "thumb", "index", "middle", "ring", "pinky" })
                for (int i=1;i<=3;i++) joints.Add(new Joint($"{finger}_{i:D2}_{suffix}",
                    i==1?"hand_"+suffix:$"{finger}_{i-1:D2}_{suffix}", side+finger+i,i<3?side+finger+(i+1):null));
            foreach (var limb in new[] { "upperarm", "lowerarm", "thigh", "calf" })
                joints.Add(new Joint(limb+"_twist_01_"+suffix,limb+"_"+suffix,null));
        }
        joints.AddRange(new[] {
            new Joint("ik_foot_root","root",null),new Joint("ik_foot_l","ik_foot_root","leftfoot"),
            new Joint("ik_foot_r","ik_foot_root","rightfoot"),new Joint("ik_hand_root","root",null),
            new Joint("ik_hand_gun","ik_hand_root","righthand"),
            new Joint("ik_hand_l","ik_hand_gun","lefthand"),new Joint("ik_hand_r","ik_hand_gun","righthand")
        });
        return joints.ToArray();
    }

    public static UnrealRigConversion Convert(Model source, AnimationPack animations = null)
    {
        if(source?.RootNode==null)throw new ArgumentException("A humanoid model is required.",nameof(source));
        var oldNodes=source.Nodes.ToArray();
        var bind=AnimationPoseEvaluator.Evaluate(source,null,0);
        var roles=oldNodes.Where(n=>AnimationSkeletonRoles.GetRole(n.Name)!=null)
            .GroupBy(n=>AnimationSkeletonRoles.GetRole(n.Name)).ToDictionary(g=>g.Key,g=>g.First());
        foreach(var role in new[]{"hips","spine","spine1","spine2","neck","head","leftarm","rightarm",
            "leftforearm","rightforearm","lefthand","righthand","leftupleg","rightupleg","leftleg","rightleg","leftfoot","rightfoot"})
            if(!roles.ContainsKey(role))throw new InvalidOperationException("Cannot build Unreal humanoid: missing "+role);
        var motion=AnimationSkeletonRoles.ResolveMotionRoot(source)??source.RootNode;
        var result=new Model(source.Version){RootNode=new Node("RootNode"){Version=source.Version},BoundingBox=source.BoundingBox,BoundingSphere=source.BoundingSphere};
        var output=new Dictionary<string,Node>();
        var byRole=new Dictionary<string,Node>();
        var drivers=new Dictionary<Node,Driver>();
        var sourceToOutput=new Dictionary<Node,Node>();
        foreach(var joint in Layout())
        {
            Node driver=null;
            if(joint.Name=="root")driver=motion;
            else if(joint.Role!=null)roles.TryGetValue(joint.Role,out driver);
            var world=driver!=null?bind[driver]:Matrix4x4.Identity;
            if(joint.Name=="root"||joint.Name.EndsWith("_root",StringComparison.Ordinal))world=Matrix4x4.Identity;
            else if(joint.Name.Contains("_twist_"))
            {
                var parent=output[joint.Parent];
                var parentDriver=drivers[parent].Source;
                var candidates=oldNodes.Where(n=>TwistName(n.Name)==joint.Name).ToArray();
                driver=candidates.FirstOrDefault(n=>n.Name.Contains("Roll_02",StringComparison.OrdinalIgnoreCase))??candidates.FirstOrDefault()??parentDriver;
                world=parent.WorldTransform;
                var distal=Layout().FirstOrDefault(j=>j.Parent==joint.Parent&&j.Role!=null&&!j.Name.StartsWith("ik_"));
                if(distal?.Role!=null&&roles.TryGetValue(distal.Role,out var tip))world.Translation=(bind[parentDriver].Translation+bind[tip].Translation)*.5f;
            }
            else if(driver!=null)
            {
                var direction=joint.Aim!=null&&roles.TryGetValue(joint.Aim,out var child)?
                    bind[child].Translation-bind[driver].Translation:Vector3.TransformNormal(Vector3.UnitX,bind[driver]);
                world=BoneFrame(world.Translation,direction);
            }
            else
            {
                // Missing finger/toe bones still get the same hierarchy in every export.
                world=output[joint.Parent].WorldTransform;
                driver=drivers[output[joint.Parent]].Source;
            }
            var node=new Node(joint.Name){Version=source.Version};
            var parentNode=joint.Parent==null?result.RootNode:output[joint.Parent];
            parentNode.AddChildNode(node);
            Matrix4x4.Invert(parentNode.WorldTransform,out var inverseParent);node.LocalTransform=world*inverseParent;
            output.Add(joint.Name,node);
            if(joint.Role!=null&&!joint.Name.StartsWith("ik_")&&joint.Name!="root")byRole[joint.Role]=node;
            if(driver!=null){Matrix4x4.Invert(bind[driver],out var inverse);drivers[node]=new Driver(driver,world*inverse);}
            else drivers[node]=new Driver(motion,world*Inverse(bind[motion]));
        }
        foreach(var old in oldNodes)
        {
            var role=AnimationSkeletonRoles.GetRole(old.Name);
            if(role!=null&&byRole.TryGetValue(role,out var mapped))sourceToOutput[old]=mapped;
            else if(old==motion||role is "root" or "rootnode" or "motionroot")sourceToOutput[old]=output["root"];
            else if(TwistName(old.Name) is string twist)sourceToOutput[old]=output[twist].Parent;
            else if(old.Name.EndsWith("Elbow_Roll",StringComparison.OrdinalIgnoreCase))
                sourceToOutput[old]=byRole[old.Name.StartsWith("L_")?"leftforearm":"rightforearm"];
        }
        // Keep weighted facial, cloth and hair bones as optional branches, never
        // as coordinate-conversion parents inside the standard humanoid chains.
        var weighted=(source.Bones??new List<Bone>()).Select(b=>oldNodes[b.NodeIndex]).ToHashSet();
        foreach(var old in oldNodes.Where(n=>weighted.Contains(n)&&!sourceToOutput.ContainsKey(n)))
        {
            var ancestor=old.Parent;
            while(ancestor!=null&&!sourceToOutput.ContainsKey(ancestor))ancestor=ancestor.Parent;
            var parent=ancestor!=null?sourceToOutput[ancestor]:output["root"];
            var name="extra_"+old.Name.Replace("Bip01 ","").Replace(" ","_");while(output.ContainsKey(name))name="extra_"+name;
            var node=new Node(name,bind[old]*Inverse(parent.WorldTransform)){Version=source.Version};parent.AddChildNode(node);
            output[name]=node;sourceToOutput[old]=node;drivers[node]=new Driver(old,Matrix4x4.Identity);
        }
        var bones=new List<Bone>();var boneIndices=new Dictionary<Node,ushort>();
        ushort BoneIndex(Node node) {
            if(!boneIndices.TryGetValue(node,out var index)){
                index=checked((ushort)bones.Count);boneIndices[node]=index;
                bones.Add(new Bone(checked((ushort)result.Nodes.ToList().IndexOf(node)),Inverse(node.WorldTransform)));
            }
            return index;
        }
        foreach(var old in oldNodes)
            foreach(var oldMesh in old.Meshes)
            {
                var mesh=Clone(oldMesh);
                var transformed=oldMesh.Transform(old,oldNodes,source.Bones,true,true,null);
                mesh.Vertices=transformed.Vertices;mesh.Normals=transformed.Normals;
                var host=new Node("mesh_"+old.Name+"_"+result.RootNode.Children.Count){Version=source.Version};result.RootNode.AddChildNode(host);
                host.Attachments.Add(new NodeMeshAttachment(mesh));drivers[host]=new Driver(source.RootNode,Matrix4x4.Identity);
                if(mesh.VertexWeights!=null)
                    for(int v=0;v<mesh.VertexWeights.Length;v++)
                    {
                        var weights=new Dictionary<ushort,float>();
                        var oldWeight=oldMesh.VertexWeights[v];
                        for(int i=0;i<oldWeight.Weights.Length;i++)if(oldWeight.Weights[i]>0)
                        {
                            var oldBone=oldNodes[source.Bones[oldWeight.Indices[i]].NodeIndex];
                            var dst=sourceToOutput.TryGetValue(oldBone,out var bone)?bone:output["root"];
                            var index=BoneIndex(dst);weights[index]=weights.GetValueOrDefault(index)+oldWeight.Weights[i];
                        }
                        var merged=weights.OrderByDescending(w=>w.Value).ToArray();
                        var count=oldWeight.Weights.Length;var values=new float[count];var indices=new ushort[count];
                        float total=merged.Sum(w=>w.Value);
                        for(int i=0;i<merged.Length;i++){values[i]=merged[i].Value/total;indices[i]=merged[i].Key;}
                        mesh.VertexWeights[v]=new VertexWeight(values,indices);
                    }
                else {
                    var ancestor=old;
                    while(ancestor!=null&&!sourceToOutput.ContainsKey(ancestor))ancestor=ancestor.Parent;
                    var bone=ancestor!=null?sourceToOutput[ancestor]:output["root"];
                    var index=BoneIndex(bone);
                    mesh.VertexWeights=Enumerable.Range(0,mesh.VertexCount).Select(_=>new VertexWeight(
                        new[]{1f,0f,0f,0f},new[]{index,(ushort)0,(ushort)0,(ushort)0})).ToArray();
                }
            }
        result.Bones=bones;
        var converted=new UnrealRigConversion{Model=result};
        if(animations!=null)
        {
            converted.Animations=new AnimationPack(animations.Version);
            foreach(var animation in animations.Animations)
            {
                var baked=new Animation(animation.Version){Duration=animation.Duration};
                var nodes=result.Nodes.ToArray();
                var tracks=nodes.Skip(1).ToDictionary(n=>n,n=>new AnimationController(animation.Version){
                    TargetKind=TargetKind.Node,TargetName=n.Name,TargetId=Array.IndexOf(nodes,n),
                    Layers={new AnimationLayer(animation.Version){KeyType=KeyType.NodePRS}}});
                var times=new SortedSet<float>{0,animation.Duration};
                for(int f=1;f/60f<animation.Duration;f++)times.Add(f/60f);
                foreach(var key in animation.Controllers.SelectMany(c=>c.Layers).Where(l=>l.HasPRSKeyFrames).SelectMany(l=>l.Keys))
                    if(key.Time>=0&&key.Time<=animation.Duration)times.Add(key.Time);
                foreach(float time in times)
                {
                    var pose=AnimationPoseEvaluator.Evaluate(source,animation,time);
                    var worlds=new Dictionary<Node,Matrix4x4>{{result.RootNode,Matrix4x4.Identity}};
                    foreach(var node in nodes.Skip(1))
                    {
                        var world=node.Name.StartsWith("mesh_")?Matrix4x4.Identity:drivers[node].Basis*pose[drivers[node].Source];
                        var local=world*Inverse(worlds[node.Parent]);
                        if(!Matrix4x4.Decompose(local,out var scale,out var rotation,out var position))
                            throw new InvalidOperationException("Unreal export cannot decompose "+node.Name);
                        tracks[node].Layers[0].Keys.Add(new PRSKey(KeyType.NodePRS){Time=time,Position=position,Rotation=Quaternion.Normalize(rotation),Scale=scale});
                        worlds[node]=world;
                    }
                }
                baked.Controllers=nodes.Skip(1).Select(n=>tracks[n]).ToList();converted.Animations.Animations.Add(baked);
            }
        }
        return converted;
    }

    private static string TwistName(string name)
    {
        string lower=name.ToLowerInvariant();
        string side=lower.StartsWith("l_")||lower.Contains(" l ")||lower.Contains("lthigh")||lower.Contains("lfore")?"l":
            lower.StartsWith("r_")||lower.Contains(" r ")||lower.Contains("rthigh")||lower.Contains("rfore")?"r":null;
        if(side==null)return null;
        if(lower.Contains("knee_roll")||lower.Contains("exknee"))return "calf_twist_01_"+side;
        if(lower.Contains("upleg_roll")||lower.Contains("exupleg")||lower.Contains("hip"+"s_roll")||lower.Contains("thightwist"))return "thigh_twist_01_"+side;
        if(lower.Contains("forearm_roll")||lower.Contains("foretwist"))return "lowerarm_twist_01_"+side;
        if(lower.Contains("arm_roll"))return "upperarm_twist_01_"+side;
        return null;
    }
    private static Matrix4x4 BoneFrame(Vector3 position,Vector3 direction)
    {
        if(direction.LengthSquared()<1e-8f)direction=Vector3.UnitX;
        var x=Vector3.Normalize(direction);var reference=MathF.Abs(x.Y)<.9f?Vector3.UnitY:Vector3.UnitZ;
        var z=Vector3.Normalize(Vector3.Cross(x,reference));var y=Vector3.Cross(z,x);
        return new Matrix4x4(x.X,x.Y,x.Z,0,y.X,y.Y,y.Z,0,z.X,z.Y,z.Z,0,position.X,position.Y,position.Z,1);
    }
    private static Matrix4x4 Inverse(Matrix4x4 matrix)
    {
        if(!Matrix4x4.Invert(matrix,out var inverse))throw new InvalidOperationException("Unreal export encountered a singular bone transform.");
        return inverse;
    }
    private static T Clone<T>(T resource) where T:Resource
    {
        using var stream=new MemoryStream();resource.Save(stream,true);stream.Position=0;return Resource.Load<T>(stream,true);
    }
}
