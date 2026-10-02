using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Runtime.Loader;
using System.Text.Json;
using GFDLibrary;
using GFDLibrary.Animations;
using GFDLibrary.Animations.Keys;
using GFDLibrary.Models;

// JSON bridge keeps the Persona hierarchy when Cascadeur inserts rig joints.
internal static class CascadeurTransition
{
    public sealed class Clip
    {
        public string Pack { get; set; } = "";
        public int Index { get; set; }
        public string[] Layers { get; set; } = System.Array.Empty<string>();
        public int StartFrame { get; set; }
        public int? EndFrame { get; set; }
    }
    public sealed class Placement
    {
        public bool MatchPosition { get; set; }
        public bool MatchYaw { get; set; }
        public float[] Translation { get; set; } = new float[3];
        public float YawDegrees { get; set; }
    }
    public sealed class Job
    {
        public string Model { get; set; } = "";
        public Clip ClipA { get; set; } = new();
        public Clip ClipB { get; set; } = new();
        public int TransitionFrames { get; set; } = 12;
        public Placement Placement { get; set; } = new();
        public string Motion { get; set; } = "Acrobatic";
        public string Output { get; set; } = "output";
        public int PoseCount { get; set; } = 12;
        public int SampleStep { get; set; } = 8;
    }
    public sealed class Bone
    {
        public string Name { get; set; } = "";
        public string ExportName { get; set; } = "";
        public string? Parent { get; set; }
        public bool Exported { get; set; }
        public bool Humanoid { get; set; }
        public string? SkinDriver { get; set; }
        public float[] BindWorld { get; set; } = System.Array.Empty<float>();
    }
    public sealed class Manifest
    {
        public string Model { get; set; } = "";
        public string Directory { get; set; } = "";
        public string JobHash { get; set; } = "";
        public string Motion { get; set; } = "";
        public int Fps { get; set; } = 30;
        public int GapStart { get; set; }
        public int GapEnd { get; set; }
        public int FrameCount { get; set; }
        public List<Bone> Bones { get; set; } = new();
        public List<Dictionary<string, float[]>> Canonical { get; set; } = new();
    }
    public sealed class Bake
    {
        public List<Dictionary<string, float[]>> Frames { get; set; } = new();
    }
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static string Resolve(string file, string directory) => Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(directory, file));
    static T Read<T>(string file) => JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json)!;
    static void Write(string file, object value) => File.WriteAllText(file, JsonSerializer.Serialize(value, Json));
    static float[] Array(Matrix4x4 m) => new[] {m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44};
    static Matrix4x4 Matrix(float[] m)
    {
        if (m.Length != 16 || m.Any(v => !float.IsFinite(v))) throw new InvalidDataException("Invalid baked matrix");
        return new(m[0],m[1],m[2],m[3],m[4],m[5],m[6],m[7],m[8],m[9],m[10],m[11],m[12],m[13],m[14],m[15]);
    }
    static Matrix4x4 Inverse(Matrix4x4 m)
    {
        if (!Matrix4x4.Invert(m,out var inverse)) throw new InvalidDataException("Singular parent transform");
        return inverse;
    }
    static Matrix4x4 Blend(Matrix4x4 a, Matrix4x4 b, float u)
    {
        if(!Matrix4x4.Decompose(a,out var sa,out var ra,out var pa) || !Matrix4x4.Decompose(b,out var sb,out var rb,out var pb))
            throw new InvalidDataException("Cannot blend a bone transform");
        return Matrix4x4.CreateScale(Vector3.Lerp(sa,sb,u))*Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(ra,rb,u))*Matrix4x4.CreateTranslation(Vector3.Lerp(pa,pb,u));
    }
    static bool Below(Node node, Node root)
    {
        for (var p=node;p!=null;p=p.Parent) if (ReferenceEquals(p,root)) return true;
        return false;
    }
    static float Heading(Matrix4x4 m)
    {
        var direction=Vector3.TransformNormal(Vector3.UnitZ,m);
        return MathF.Atan2(direction.X,direction.Z);
    }
    static Assembly Fbx => AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory,"GFDLibrary.Conversion.FbxSdk.dll"));
    static void Export(ModelPack model, AnimationPack pack, string file, bool humanoidSkin=true)
    {
        var assembly=Fbx;
        var configType=assembly.GetType("GFDLibrary.Conversion.FbxSdk.FbxSdkModelPackExporterConfig")!;
        var config=Activator.CreateInstance(configType)!;
        configType.GetProperty("UseUnrealBoneNames")!.SetValue(config,true);
        configType.GetProperty("BindDanceSkinToHumanoid")!.SetValue(config,humanoidSkin);
        assembly.GetType("GFDLibrary.Conversion.FbxSdk.FbxSdkModelPackExporter")!.GetMethod("ExportFile")!.Invoke(null,new object[]{model,file,config});
        if(pack.Animations.Count>0) assembly.GetType("GFDLibrary.Conversion.FbxSdk.FbxSdkAnimationExporter")!.GetMethods().Single(m=>m.Name=="AppendFile"&&m.GetParameters().Length==4)
            .Invoke(null,new object[]{model.Model,pack,file,config});
    }
    static Animation Load(Clip clip, string directory)
    {
        var pack=Resource.Load<AnimationPack>(Resolve(clip.Pack,directory));
        if (clip.Index == -2) clip.Index = 0;
        if (clip.Index == -1) clip.Index = Enumerable.Range(0,pack.Animations.Count).MaxBy(i=>pack.Animations[i].Duration);
        if (clip.Index<0 || clip.Index>=pack.Animations.Count) throw new ArgumentException("Clip index outside pack");
        var source=pack.Animations[clip.Index];
        var animation=new Animation(pack.Version){Duration=source.Duration};
        animation.Controllers.AddRange(source.Controllers);
        foreach(var layerFile in clip.Layers)
        {
            var extra=Resource.Load<AnimationPack>(Resolve(layerFile,directory));
            if(clip.Index>=extra.Animations.Count) throw new ArgumentException("Layer pack has no requested clip");
            var layer=extra.Animations[clip.Index];
            if(Math.Abs(layer.Duration-source.Duration)>.001f) throw new ArgumentException("Layer duration differs from body clip");
            SplitCharacterAnimationComposer.AddComponentTracks(animation,layer);
        }
        var last=(int)Math.Round(animation.Duration*30);
        clip.EndFrame??=last;
        if (clip.StartFrame<0||clip.EndFrame>last||clip.EndFrame<clip.StartFrame) throw new ArgumentException("Invalid clip frame range");
        return animation;
    }
    static Animation FromWorlds(Model model,uint version,IReadOnlyList<Dictionary<string,float[]>> frames,int first,int last)
    {
        var nodes=model.Nodes.ToArray();
        var animation=new Animation(version){Duration=(last-first)/30f};
        for (var i=0;i<nodes.Length;++i)
        {
            var node=nodes[i];
            if (ReferenceEquals(node,model.RootNode)) continue;
            var controller=new AnimationController(version){TargetKind=TargetKind.Node,TargetName=node.Name,TargetId=i,
                Layers=new List<AnimationLayer>{new(version){KeyType=KeyType.NodePRS}}};
            Quaternion? previous=null;
            for (var f=first;f<=last;++f)
            {
                var world=Matrix(frames[f][node.Name]);
                var parent=node.Parent==null?Matrix4x4.Identity:Matrix(frames[f][node.Parent.Name]);
                var local=world*Inverse(parent);
                if (!Matrix4x4.Decompose(local,out var scale,out var rotation,out var position)) throw new InvalidDataException("Cannot decompose "+node.Name);
                rotation=Quaternion.Normalize(rotation);
                if (previous.HasValue&&Quaternion.Dot(previous.Value,rotation)<0) rotation=new(-rotation.X,-rotation.Y,-rotation.Z,-rotation.W);
                previous=rotation;
                controller.Layers[0].Keys.Add(new PRSKey(KeyType.NodePRS){Time=(f-first)/30f,Position=position,Rotation=rotation,Scale=scale});
            }
            animation.Controllers.Add(controller);
        }
        return animation;
    }
    public static int Run(string mode,string jobFile)
    {
        var jobDirectory=Path.GetDirectoryName(Path.GetFullPath(jobFile))!;
        var job=Read<Job>(jobFile);
        var directory=Resolve(job.Output,jobDirectory);
        Directory.CreateDirectory(directory);
        var modelFile=Resolve(job.Model,jobDirectory);
        var model=Resource.Load<ModelPack>(modelFile);
        var nodes=model.Model.Nodes.ToArray();
        var bind=AnimationPoseEvaluator.Evaluate(model.Model,null,0);
        var manifestFile=Path.Combine(directory,"manifest.json");
        if(mode=="select-poses")
        {
            var allClips=job.ClipA.Index == -2;
            var animation=Load(job.ClipA,jobDirectory);
            var sourcePack=Resource.Load<AnimationPack>(Resolve(job.ClipA.Pack,jobDirectory));
            var clipIndices=allClips ? Enumerable.Range(0,sourcePack.Animations.Count).ToArray() : new[]{job.ClipA.Index};
            var animations=clipIndices.ToDictionary(i=>i,i=>Load(new Clip {Pack=job.ClipA.Pack,Index=i,Layers=job.ClipA.Layers},jobDirectory));
            var samples=clipIndices.SelectMany(i=>Enumerable.Range(0,(int)Math.Round(animations[i].Duration*30)+1)
                .Where(f=>f%Math.Max(1,job.SampleStep)==0).Select(f=>(clip:i,frame:f))).ToArray();
            var root=AnimationSkeletonRoles.ResolveMotionRoot(model.Model);
            var body=nodes.Where(n=>AnimationSkeletonRoles.GetRole(n.Name) is string role
                && !role.Contains("hair") && !role.Contains("root") && !role.Contains("finger")
                && !role.Contains("thumb")).ToArray();
            var height=MathF.Max(1,nodes.Max(n=>bind[n].Translation.Y)-nodes.Min(n=>bind[n].Translation.Y));
            var frames=samples.Select(sample=>sample.frame).ToArray();
            var descriptors=new List<float[]>();
            foreach(var sample in samples)
            {
                var frame=sample.frame;
                var sampledAnimation=animations[sample.clip];
                var pose=AnimationPoseEvaluator.Evaluate(model.Model,sampledAnimation,frame/30f);
                var previous=AnimationPoseEvaluator.Evaluate(model.Model,sampledAnimation,Math.Max(0,frame-3)/30f);
                var invYaw=Matrix4x4.CreateRotationY(-Heading(pose[root]));
                var values=new List<float>{pose[root].Translation.Y/height};
                foreach(var bone in body)
                {
                    var position=Vector3.TransformNormal(pose[bone].Translation-pose[root].Translation,invYaw)/height;
                    var velocity=Vector3.TransformNormal(pose[bone].Translation-previous[bone].Translation,invYaw)/height;
                    var direction=Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ,pose[bone]*invYaw));
                    values.AddRange(new[]{position.X,position.Y,position.Z,velocity.X*2,velocity.Y*2,velocity.Z*2,
                        direction.X*.15f,direction.Y*.15f,direction.Z*.15f});
                }
                descriptors.Add(values.ToArray());
            }
            float Distance(int a,int b) => descriptors[a].Zip(descriptors[b],(x,y)=>(x-y)*(x-y)).Sum();
            var selected=new List<int>{0};
            var distances=Enumerable.Range(0,frames.Length).Select(i=>Distance(i,0)).ToArray();
            while(selected.Count<Math.Min(job.PoseCount,frames.Length))
            {
                var best=Enumerable.Range(0,frames.Length).Where(i=>!selected.Contains(i)).MaxBy(i=>distances[i]);
                selected.Add(best);
                for(var i=0;i<frames.Length;++i) distances[i]=Math.Min(distances[i],Distance(i,best));
            }
            selected=selected.OrderBy(i=>frames[i]).ToList();
            var mapping=Enumerable.Range(0,frames.Length).Select(i=>new {sourceClip=samples[i].clip,frame=frames[i],clip=Enumerable.Range(0,selected.Count).MinBy(j=>Distance(i,selected[j]))}).ToArray();
            Write(Path.Combine(directory,"poses.json"),new {fps=30,frameCount=allClips ? animations.Values.Sum(a=>(int)Math.Round(a.Duration*30)+1) : job.ClipA.EndFrame.Value+1,
                danceClip=allClips ? -2 : job.ClipA.Index,sourceClips=clipIndices.Select(i=>new {index=i,frameCount=(int)Math.Round(animations[i].Duration*30)+1}),
                selectedSamples=selected.Select(i=>new {sourceClip=samples[i].clip,frame=frames[i]}),
                sampleStep=job.SampleStep,selectedFrames=selected.Select(i=>frames[i]),mapping,
                descriptors=selected.Select(i=>descriptors[i]),descriptorBones=body.Select(n=>n.Name)});
            return 0;
        }
        if(mode=="pack-transitions")
        {
            var poses=JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(directory,"poses.json")));
            var frames=poses.GetProperty("selectedFrames").EnumerateArray().Select(f=>f.GetInt32()).ToArray();
            var output=new AnimationPack(model.Version);
            foreach(var frame in frames)
            {
                var index=output.Animations.Count;
                var prefix=poses.TryGetProperty("selectedSamples",out var selectedSamples) ?
                    "c"+selectedSamples[index].GetProperty("sourceClip").GetInt32().ToString("D2")+"_" : "";
                var pack=Resource.Load<AnimationPack>(Path.Combine(directory,prefix+"f"+frame.ToString("D5"),"between.GAP"));
                output.Animations.Add(pack.Animations[0]);
            }
            output.Save(Path.Combine(directory,"dance_to_ko3.GAP"));
            var readback=Resource.Load<AnimationPack>(Path.Combine(directory,"dance_to_ko3.GAP"));
            for (var i=0;i<readback.Animations.Count;++i)
            {
                var animation=readback.Animations[i];
                PoseRender.Draw(model.Model,AnimationPoseEvaluator.Evaluate(model.Model,animation,0),
                    model.Model,AnimationPoseEvaluator.Evaluate(model.Model,animation,animation.Duration*.5f),
                    Path.Combine(directory,$"transition_{i:D2}_start_middle.png"),
                    $"Dance frame {frames[i]} to KO 3: boundary / generated AI middle (GAP readback)");
            }
            return 0;
        }
        if(mode=="fix-knees")
        {
            var pack=Resource.Load<AnimationPack>(Resolve(job.ClipA.Pack,jobDirectory));
            var reference=Resource.Load<AnimationPack>(Resolve(job.ClipB.Pack,jobDirectory)).Animations[job.ClipB.Index];
            DancingKneeCorrection.Apply(pack,model.Model,reference);
            pack.Save(Path.Combine(directory,"corrected.GAP"));
            Export(model,pack,Path.Combine(directory,"corrected.fbx"),false);
            return 0;
        }
        if(mode=="diagnose" || mode=="source-preview")
        {
            var anim=Load(job.ClipA,jobDirectory);
            var weights=new Dictionary<string,float>();
            foreach(var mesh in nodes.SelectMany(n=>n.Meshes).Where(m=>m.VertexWeights!=null))
                foreach(var vertex in mesh.VertexWeights)
                    for(var i=0;i<vertex.Weights.Length;++i)
                        if(vertex.Weights[i]>0)
                        {
                            var name=nodes[model.Model.Bones[vertex.Indices[i]].NodeIndex].Name;
                            weights[name]=weights.GetValueOrDefault(name)+vertex.Weights[i];
                        }
            var frames=new List<Dictionary<string,float[]>>();
            for(var f=0;f<=Math.Round(anim.Duration*30);++f)
                frames.Add(AnimationPoseEvaluator.Evaluate(model.Model,anim,f/30f).ToDictionary(p=>p.Key.Name,p=>Array(p.Value)));
            if(mode=="source-preview")
            {
                var previewPack=new AnimationPack(model.Version);
                previewPack.Animations.Add(FromWorlds(model.Model,model.Version,frames,0,frames.Count-1));
                Export(model,previewPack,Path.Combine(directory,"source.fbx"),false);
            }
            Write(Path.Combine(directory,"diagnosis.json"),new {bones=nodes.Select(n=>new {n.Name,parent=n.Parent?.Name,bind=Array(bind[n]),skinWeight=weights.GetValueOrDefault(n.Name),tracked=anim.Controllers.Any(c=>c.TargetName==n.Name)}),frames});
            return 0;
        }
        if (mode=="prepare")
        {
            if (job.TransitionFrames<1||job.TransitionFrames>600) throw new ArgumentException("transitionFrames must be 1..600");
            var a=Load(job.ClipA,jobDirectory);var b=Load(job.ClipB,jobDirectory);
            foreach(var clip in new[]{a,b})
            {
                var names=nodes.Select(n=>n.Name).ToHashSet();
                var unknown=clip.Controllers.Where(c=>c.TargetKind!=TargetKind.Node || !names.Contains(c.TargetName)).Select(c=>c.TargetName).ToArray();
                if(unknown.Length>0) throw new ArgumentException("Use node animation packs for this model; unmatched tracks: "+string.Join(", ",unknown));
                if(clip.Controllers.Count(c=>AnimationSkeletonRoles.GetRole(c.TargetName) is string role && !role.Contains("hair"))<8)
                    throw new ArgumentException("Body tracks are missing. Supply the body pack as pack and costume packs as layers.");
            }
            var aEnd=job.ClipA.EndFrame!.Value-job.ClipA.StartFrame;
            var bStart=aEnd+job.TransitionFrames+1;
            var count=bStart+job.ClipB.EndFrame!.Value-job.ClipB.StartFrame+1;
            var root=AnimationSkeletonRoles.ResolveMotionRoot(model.Model);
            var lastA=AnimationPoseEvaluator.Evaluate(model.Model,a,job.ClipA.EndFrame.Value/30f);
            var firstB=AnimationPoseEvaluator.Evaluate(model.Model,b,job.ClipB.StartFrame/30f);
            var yaw=job.Placement.YawDegrees*MathF.PI/180;
            if (job.Placement.MatchYaw) yaw+=Heading(lastA[root])-Heading(firstB[root]);
            if (job.Placement.Translation.Length!=3) throw new ArgumentException("placement.translation must have 3 coordinates");
            var destination=(job.Placement.MatchPosition?lastA[root]:firstB[root]).Translation+
                new Vector3(job.Placement.Translation[0],job.Placement.Translation[1],job.Placement.Translation[2]);
            var move=Matrix4x4.CreateTranslation(-firstB[root].Translation)*Matrix4x4.CreateRotationY(yaw)*Matrix4x4.CreateTranslation(destination);
            var manifest=new Manifest{Model=modelFile,Directory=directory,JobHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(jobFile))),GapStart=aEnd+1,GapEnd=bStart+1,FrameCount=count+1,Motion=job.Motion};
            var mapper=Fbx.GetType("GFDLibrary.Conversion.FbxSdk.FbxSdkBoneNameMapper")!;
            foreach (var node in nodes)
            {
                var name=(string)mapper.GetMethod("GetExportName")!.Invoke(null,new object[]{model.Model,node,true})!;
                var driverRole=(string?)mapper.GetMethod("GetDanceSkinDriverRole")!.Invoke(null,new object[]{node.Name});
                manifest.Bones.Add(new Bone{Name=node.Name,ExportName=name,Parent=node.Parent?.Name,Exported=!ReferenceEquals(node,model.Model.RootNode),
                    Humanoid=AnimationSkeletonRoles.GetRole(node.Name) is string role && !role.Contains("hair"),
                    SkinDriver=driverRole==null?null:nodes.FirstOrDefault(n=>AnimationSkeletonRoles.GetRole(n.Name)==driverRole)?.Name,BindWorld=Array(bind[node])});
            }
            manifest.Canonical.Add(nodes.ToDictionary(n=>n.Name,n=>Array(bind[n])));
            for (var f=0;f<count;++f)
            {
                var isB=f>=bStart;
                var time=isB?(job.ClipB.StartFrame+f-bStart)/30f:(job.ClipA.StartFrame+Math.Min(f,aEnd))/30f;
                var pose=AnimationPoseEvaluator.Evaluate(model.Model,isB?b:a,time);
                var frame=new Dictionary<string,float[]>();
                foreach (var n in nodes) frame[n.Name]=Array(isB&&Below(n,root)?pose[n]*move:pose[n]);
                manifest.Canonical.Add(frame);
            }
            var pack=new AnimationPack(model.Version);
            pack.Animations.Add(FromWorlds(model.Model,model.Version,manifest.Canonical,0,count));
            pack.Save(Path.Combine(directory,"prepared.GAP"));
            Export(model,pack,Path.Combine(directory,"input.fbx"));
            Export(model,new AnimationPack(model.Version),Path.Combine(directory,"rig.fbx"));
            Write(manifestFile,manifest);
            Console.WriteLine($"Prepared {count} frames; AI interval {aEnd}..{bStart}");
            return 0;
        }
        if (mode!="finish") throw new ArgumentException("Mode must be prepare or finish");
        var saved=Read<Manifest>(manifestFile);
        if(saved.JobHash!=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(jobFile)))) throw new InvalidDataException("Job settings changed; prepare and rig again before writing GAP");
        var baked=Read<Bake>(Path.Combine(directory,"baked.json"));
        if (baked.Frames.Count!=saved.GapEnd-saved.GapStart+1) throw new InvalidDataException("Incomplete Cascadeur bake");
        var merged=saved.Canonical;
        var missing=saved.Bones.Where(b=>b.Exported&&b.Humanoid&&!baked.Frames[0].ContainsKey(b.Name)).Select(b=>b.Name).ToArray();
        if (missing.Length>0) throw new InvalidDataException("Missing baked humanoid joints: "+string.Join(", ",missing));
        var errors=new Dictionary<string,float>();
        var corrections=new Dictionary<string,(Matrix4x4 A,Matrix4x4 B)>();
        foreach (var boundary in new[]{saved.GapStart,saved.GapEnd})
        {
            var raw=baked.Frames[boundary-saved.GapStart];
            foreach (var bone in saved.Bones.Where(b=>b.Exported&&b.Humanoid&&raw.ContainsKey(b.Name)))
                errors[$"{boundary}:{bone.Name}"]=Vector3.Distance(Matrix(raw[bone.Name]).Translation,Matrix(merged[boundary][bone.Name]).Translation);
        }
        if(errors.Values.Any(e=>!float.IsFinite(e) || e>5)) throw new InvalidDataException("Cascadeur boundary poses differ by more than 5 game units; check rig and unit conversion");
        foreach(var bone in saved.Bones.Where(b=>b.Humanoid && b.Exported))
            corrections[bone.Name]=(Inverse(Matrix(baked.Frames[0][bone.Name]))*Matrix(merged[saved.GapStart][bone.Name]),
                Inverse(Matrix(baked.Frames[^1][bone.Name]))*Matrix(merged[saved.GapEnd][bone.Name]));
        // Source portions and the two boundary frames remain the prepared clips.
        for (var f=saved.GapStart+1;f<saved.GapEnd;++f)
        {
            var raw=baked.Frames[f-saved.GapStart];
            var u=(f-saved.GapStart)/(float)(saved.GapEnd-saved.GapStart);
            var worlds=new Dictionary<string,float[]>();
            foreach (var node in nodes)
            {
                var bone=saved.Bones.Single(b=>b.Name==node.Name);
                if (bone.Humanoid && raw.TryGetValue(node.Name,out var aiWorld)) worlds[node.Name]=Array(Matrix(aiWorld)*Blend(corrections[node.Name].A,corrections[node.Name].B,u));
                else
                {
                    // Accessories follow their animated parent using a local
                    // transition between the two imported accessory poses.
                    Matrix4x4 Local(int frame) => Matrix(merged[frame][node.Name]) * Inverse(node.Parent==null?Matrix4x4.Identity:Matrix(merged[frame][node.Parent.Name]));
                    Matrix4x4.Decompose(Local(saved.GapStart),out var s0,out var r0,out var p0);
                    Matrix4x4.Decompose(Local(saved.GapEnd),out var s1,out var r1,out var p1);
                    var local=Matrix4x4.CreateScale(Vector3.Lerp(s0,s1,u))*Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(r0,r1,u))*Matrix4x4.CreateTranslation(Vector3.Lerp(p0,p1,u));
                    worlds[node.Name]=Array(local*(node.Parent!=null&&worlds.ContainsKey(node.Parent.Name)?Matrix(worlds[node.Parent.Name]):Matrix4x4.Identity));
                }
            }
            // Drive the same helper bones whose skin was rebound for Cascadeur.
            foreach (var bone in saved.Bones.Where(b=>b.SkinDriver!=null))
                {
                Matrix4x4 Offset(int frame) => Matrix(merged[frame][bone.Name])*Inverse(Matrix(merged[frame][bone.SkinDriver!]));
                worlds[bone.Name]=Array(Blend(Offset(saved.GapStart),Offset(saved.GapEnd),u)*Matrix(worlds[bone.SkinDriver!]));
            }
            merged[f]=worlds;
        }
        AnimationPack Pack(int first,int last)
        {
            var result=new AnimationPack(model.Version);result.Animations.Add(FromWorlds(model.Model,model.Version,merged,first,last));return result;
        }
        var joined=Pack(1,saved.FrameCount-1);
        joined.Save(Path.Combine(directory,"joined.GAP"));
        var pieces=new AnimationPack(model.Version);
        foreach(var (name,first,last) in new[]{("a",1,saved.GapStart),("between",saved.GapStart,saved.GapEnd),("b",saved.GapEnd,saved.FrameCount-1)})
        {
            var piece=Pack(first,last);piece.Save(Path.Combine(directory,name+".GAP"));pieces.Animations.Add(piece.Animations[0]);
        }
        pieces.Save(Path.Combine(directory,"pieces.GAP"));

        var readBack=Resource.Load<AnimationPack>(Path.Combine(directory,"joined.GAP"));
        Export(model,readBack,Path.Combine(directory,"joined_game_model.fbx"),false);
        float maxRoundTripPositionError=0, maxSourceMatrixError=0;
        for(var f=1;f<saved.FrameCount;++f)
        {
            var pose=AnimationPoseEvaluator.Evaluate(model.Model,readBack.Animations[0],(f-1)/30f);
            foreach(var node in nodes)
            {
                var expected=Matrix(merged[f][node.Name]);
                maxRoundTripPositionError=Math.Max(maxRoundTripPositionError,Vector3.Distance(pose[node].Translation,expected.Translation));
                if(f<=saved.GapStart || f>=saved.GapEnd)
                    maxSourceMatrixError=Math.Max(maxSourceMatrixError,Array(pose[node]).Zip(Array(expected),(a,b)=>Math.Abs(a-b)).Max());
            }
        }
        if(maxRoundTripPositionError>.1f || !float.IsFinite(maxRoundTripPositionError)) throw new InvalidDataException("GAP pose readback differs from the baked animation");
        Write(Path.Combine(directory,"report.json"),new {saved.GapStart,saved.GapEnd,saved.FrameCount,boundaryPositionErrors=errors,maxRoundTripPositionError,maxSourceMatrixError,sourceBPlacementOnly=true});
        // Parse what we wrote, so a malformed resource cannot be reported as output.
        if (Resource.Load<AnimationPack>(Path.Combine(directory,"joined.GAP")).Animations.Count!=1 ||
            Resource.Load<AnimationPack>(Path.Combine(directory,"pieces.GAP")).Animations.Count!=3) throw new InvalidDataException("Written GAP could not be read back");
        Console.WriteLine("Wrote joined.GAP, pieces.GAP, a.GAP, between.GAP, b.GAP");
        return 0;
    }
}
