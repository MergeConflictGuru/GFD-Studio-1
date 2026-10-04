using System.Numerics;
using System.Text.Json;
using GFDLibrary;
using GFDLibrary.Animations;
using GFDLibrary.Models;

internal static partial class CascadeurTransition
{
    sealed class LibrarySample
    {
        public int Rank { get; set; }
        public int SourceClip { get; set; }
        public int Frame { get; set; }
        public float[] Descriptor { get; set; } = [];
        public int BStartFrame { get; set; }
        public int TransitionFrames { get; set; }
        public float DestinationDistance { get; set; }
        public string Folder => $"r{Rank:D3}_c{SourceClip:D2}_f{Frame:D05}";
    }
    sealed class LibraryPlan
    {
        public int Version { get; set; } = 3;
        public int GeneratedCount { get; set; }
        public int ReuseCount { get; set; }
        public int Fps { get; set; } = 30;
        public int SampleStep { get; set; }
        public string[] DescriptorBones { get; set; } = [];
        public string Descriptor { get; set; } = "height-normalized anatomical-yaw-relative body position, velocity, facing/up at -0.2/-0.1/0 seconds; ground height, body yaw rate, foot support";
        public List<LibrarySample> SelectedSamples { get; set; } = [];
        public object[] Mapping { get; set; } = [];
    }
    static float[] MotionDescriptor(Model model, Animation animation, int frame, Node[] body, Node root, float height)
    {
        Dictionary<Node,Matrix4x4> Pose(int f) => AnimationPoseEvaluator.Evaluate(model,animation,Math.Clamp(f,0,(int)Math.Round(animation.Duration*30))/30f);
        var pivot=Pose(frame);var yaw=Matrix4x4.CreateRotationY(-BodyFacingYaw(model,pivot));var output=new List<float>();
        foreach(var offset in new[]{-6,-3,0})
        {
            var f=Math.Clamp(frame+offset,0,(int)Math.Round(animation.Duration*30));var pose=Pose(f);var prev=Pose(f-1);var next=Pose(f+1);
            var dt=Math.Max(1,Math.Min(f+1,(int)Math.Round(animation.Duration*30))-Math.Max(0,f-1))/30f;
            foreach(var bone in body)
            {
                var p=Vector3.TransformNormal(pose[bone].Translation-pivot[root].Translation,yaw)/height;
                var v=Vector3.TransformNormal((next[bone].Translation-prev[bone].Translation)/dt,yaw)/height;
                var forward=Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ,pose[bone]*yaw));
                var up=Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY,pose[bone]*yaw));
                output.AddRange(new[]{p.X,p.Y,p.Z,v.X*.35f,v.Y*.35f,v.Z*.35f,forward.X*.25f,forward.Y*.25f,forward.Z*.25f,up.X*.25f,up.Y*.25f,up.Z*.25f});
            }
        }
        var previous=Pose(frame-1);var following=Pose(frame+1);var span=Math.Max(1,Math.Min(frame+1,(int)Math.Round(animation.Duration*30))-Math.Max(0,frame-1))/30f;
        var speed=Vector3.TransformNormal((following[root].Translation-previous[root].Translation)/span,yaw)/height;
        var angle=BodyFacingYaw(model,following)-BodyFacingYaw(model,previous);angle=MathF.Atan2(MathF.Sin(angle),MathF.Cos(angle));
        output.AddRange(new[]{speed.X*.4f,speed.Y*.4f,speed.Z*.4f,angle/span*.15f,pivot[root].Translation.Y/height});
        foreach(var foot in body.Where(n=>(AnimationSkeletonRoles.GetRole(n.Name)??"").Contains("foot")))
        {
            var h=pivot[foot].Translation.Y/height;
            var v=Vector3.Distance(following[foot].Translation,previous[foot].Translation)/span/height;
            // Soft contact evidence avoids classifying every crouch as a planted foot.
            output.Add(1f/(1f+MathF.Max(0,h)*30f+v*3f));
        }
        return output.ToArray();
    }
    static float MotionDistance(float[] a,float[] b) => a.Zip(b,(x,y)=>(x-y)*(x-y)).Sum()/Math.Max(1,a.Length);
    static int RunLibrary(string mode,Job job,string jobDirectory,string directory,ModelPack model)
    {
        if(job.PoseCount<1 || job.ReusePoseCount<0 || job.SampleStep<1 || job.MaxBTrimPercent<0 || job.MaxBTrimPercent>100 || job.MinTransitionFrames<1 || job.MaxTransitionFrames<job.MinTransitionFrames || job.MaxTransitionFrames>119 || job.LeadInFrames<0)
            throw new ArgumentException("Invalid library count, sampling step, trimming percent, lead-in, or duration bounds (AI maximum: 119 interior frames)");
        var nodes=model.Model.Nodes.ToArray();var root=AnimationSkeletonRoles.ResolveMotionRoot(model.Model);
        var bodyRoles=new HashSet<string>{"hips","spine","spine1","spine2","neck","head","leftshoulder","rightshoulder","leftarm","rightarm","leftforearm","rightforearm","lefthand","righthand","leftupleg","rightupleg","leftleg","rightleg","leftfoot","rightfoot","lefttoe","righttoe"};
        var body=nodes.Where(n=>AnimationSkeletonRoles.GetRole(n.Name) is string r && bodyRoles.Contains(r)).ToArray();
        var bind=AnimationPoseEvaluator.Evaluate(model.Model,null,0);var height=MathF.Max(1,nodes.Max(n=>bind[n].Translation.Y)-nodes.Min(n=>bind[n].Translation.Y));
        var source=Resource.Load<AnimationPack>(Resolve(job.ClipA.Pack,jobDirectory));
        var indices=job.ClipA.Index == -2 ? Enumerable.Range(0,source.Animations.Count).ToArray() : new[]{job.ClipA.Index == -1 ? Enumerable.Range(0,source.Animations.Count).MaxBy(i=>source.Animations[i].Duration) : job.ClipA.Index};
        var clips=indices.ToDictionary(i=>i,i=>Load(new Clip{Pack=job.ClipA.Pack,Index=i,Layers=job.ClipA.Layers},jobDirectory));
        var b=Load(job.ClipB,jobDirectory);var bLast=job.ClipB.EndFrame!.Value;
        var planFile=Path.Combine(directory,"poses.json");
        if(mode=="select-library")
        {
            var samples=indices.SelectMany(i=>Enumerable.Range(0,(int)Math.Round(clips[i].Duration*30)+1).Where(f=>f%job.SampleStep==0).Select(f=>new LibrarySample{SourceClip=i,Frame=f,Descriptor=MotionDescriptor(model.Model,clips[i],f,body,root,height)})).ToList();
            var total=job.PoseCount+job.ReusePoseCount;if(samples.Count<total)throw new ArgumentException("Not enough sampled frames for generated and held-out poses");
            var mean=Enumerable.Range(0,samples[0].Descriptor.Length).Select(d=>samples.Average(s=>s.Descriptor[d])).ToArray();
            var seed=Enumerable.Range(0,samples.Count).MinBy(i=>MotionDistance(samples[i].Descriptor,mean));
            var selected=new List<int>{seed};var distances=samples.Select(s=>MotionDistance(s.Descriptor,samples[seed].Descriptor)).ToArray();
            while(selected.Count<total)
            {
                var best=Enumerable.Range(0,samples.Count).Where(i=>!selected.Contains(i)).MaxBy(i=>distances[i]);selected.Add(best);
                for(var i=0;i<samples.Count;i++)distances[i]=Math.Min(distances[i],MotionDistance(samples[i].Descriptor,samples[best].Descriptor));
            }
            var maxTrim=job.ClipB.StartFrame+(int)Math.Floor((bLast-job.ClipB.StartFrame)*job.MaxBTrimPercent/100);
            var bDescriptors=Enumerable.Range(job.ClipB.StartFrame,maxTrim-job.ClipB.StartFrame+1).ToDictionary(f=>f,f=>MotionDescriptor(model.Model,b,f,body,root,height));
            var chosen=new List<LibrarySample>();
            foreach(var i in selected)
            {
                var sample=samples[i];sample.Rank=chosen.Count+1;
                var match=bDescriptors.MinBy(pair=>MotionDistance(sample.Descriptor,pair.Value));sample.BStartFrame=match.Key;sample.DestinationDistance=MotionDistance(sample.Descriptor,match.Value);
                var aPose=AnimationPoseEvaluator.Evaluate(model.Model,clips[sample.SourceClip],sample.Frame/30f);var bPose=AnimationPoseEvaluator.Evaluate(model.Model,b,sample.BStartFrame/30f);
                var aYaw=Matrix4x4.CreateRotationY(-BodyFacingYaw(model.Model,aPose));var bYaw=Matrix4x4.CreateRotationY(-BodyFacingYaw(model.Model,bPose));
                var aCenter=aPose[root].Translation;var bCenter=bPose[root].Translation;
                if(!job.Placement.MatchUp) {aCenter.Y=0;bCenter.Y=0;}
                var displacement=body.Max(n=>Vector3.Distance(Vector3.TransformNormal(aPose[n].Translation-aCenter,aYaw),Vector3.TransformNormal(bPose[n].Translation-bCenter,bYaw))/height);
                // At least 0.6s; add time for travel and incoming/outgoing motion disagreement.
                var needed=(int)Math.Ceiling(30*(.45f+displacement/1.2f+MathF.Min(.6f,MathF.Sqrt(sample.DestinationDistance)*.3f)));
                sample.TransitionFrames=job.VariableDuration?Math.Clamp(needed,job.MinTransitionFrames,job.MaxTransitionFrames):Math.Clamp(job.TransitionFrames,job.MinTransitionFrames,job.MaxTransitionFrames);
                chosen.Add(sample);
            }
            var generated=chosen.Take(job.PoseCount).ToArray();
            var mapping=samples.Select(s=>{var nearest=Enumerable.Range(0,generated.Length).MinBy(i=>MotionDistance(s.Descriptor,generated[i].Descriptor));return (object)new{sourceClip=s.SourceClip,frame=s.Frame,transition=nearest,distance=MotionDistance(s.Descriptor,generated[nearest].Descriptor)};}).ToArray();
            var plan=new LibraryPlan{GeneratedCount=job.PoseCount,ReuseCount=job.ReusePoseCount,SampleStep=job.SampleStep,DescriptorBones=body.Select(n=>n.Name).ToArray(),SelectedSamples=chosen,Mapping=mapping};
            Write(planFile,plan);Console.WriteLine($"Selected {job.PoseCount} generated and {job.ReusePoseCount} held-out poses from {samples.Count} frames in {indices.Length} clips");return 0;
        }
        var saved=Read<LibraryPlan>(planFile);
        if (mode == "render-library") return RenderLibrary(job, directory, model, saved);
        if (mode == "export-reuse-clips")
        {
            WriteBorrowedSamples(directory,model.Version,saved,Resource.Load<AnimationPack>(Path.Combine(directory,"reuse_demos.GAP")));
            return 0;
        }
        var donors=saved.SelectedSamples.Take(saved.GeneratedCount).ToArray();
        var transitions=new AnimationPack(model.Version);var demos=new AnimationPack(model.Version);var reuse=new AnimationPack(model.Version);var report=new List<object>();
        foreach(var donor in donors)
        {
            transitions.Animations.Add(Resource.Load<AnimationPack>(Path.Combine(directory,donor.Folder,"between.GAP")).Animations[0]);
            demos.Animations.Add(Resource.Load<AnimationPack>(Path.Combine(directory,donor.Folder,"joined.GAP")).Animations[0]);
        }
        // Search each generated middle at every seam frame, with a shared
        // position/yaw correction for that frame and its whole continuation.
        bool MovesWithRoot(Node node) {
            for(var ancestor=node;ancestor!=null;ancestor=ancestor.Parent)
                if(ReferenceEquals(ancestor,root)) return true;
            return false;
        }
        var motionNodes=nodes.Where(MovesWithRoot).ToHashSet();
        var seams=transitions.Animations.SelectMany((clip,i)=>Enumerable.Range(0,(int)Math.Round(clip.Duration*30))
            .Select(f=>new {Donor=i,Frame=f,
                Pose=AnimationPoseEvaluator.Evaluate(model.Model,clip,f/30f),
                Next=AnimationPoseEvaluator.Evaluate(model.Model,clip,(f+1)/30f),
                Descriptor=MotionDescriptor(model.Model,clip,f,body,root,height)})).ToArray();
        foreach(var sample in saved.SelectedSamples.Skip(saved.GeneratedCount))
        {
            var animation=clips[sample.SourceClip];
            var dancePose=AnimationPoseEvaluator.Evaluate(model.Model,animation,sample.Frame/30f);
            var previous=AnimationPoseEvaluator.Evaluate(model.Model,animation,Math.Max(0,sample.Frame-1)/30f);
            var nextDance=AnimationPoseEvaluator.Evaluate(model.Model,animation,Math.Min(sample.Frame+1,(int)Math.Round(animation.Duration*30))/30f);
            var candidates=seams.Select(seam=>{
                var donor=donors[seam.Donor];
                var move=Place(model.Model,dancePose,seam.Next,job.Placement,YawVariation(job.Placement,sample.SourceClip,sample.Frame,donor.SourceClip,donor.Frame));
                var distances=body.Select(n=>Vector3.Distance(dancePose[n].Translation,(seam.Next[n]*move).Translation)/height).ToArray();
                var feet=body.Where(n=>(AnimationSkeletonRoles.GetRole(n.Name)??"").Contains("foot"))
                    .Select(n=>Vector3.Distance(dancePose[n].Translation,(seam.Next[n]*move).Translation)/height).DefaultIfEmpty(0).Max();
                var velocity=body.Select(n=>Vector3.Distance((nextDance[n].Translation-previous[n].Translation)*.5f,
                    Vector3.TransformNormal(seam.Next[n].Translation-seam.Pose[n].Translation,move))/height).Average();
                var orientation=body.Select(n=>{
                    var a=dancePose[n];var b=seam.Next[n]*move;
                    var forward=Vector3.Dot(Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ,a)),Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ,b)));
                    var up=Vector3.Dot(Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY,a)),Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY,b)));
                    return (2-Math.Clamp(forward,-1,1)-Math.Clamp(up,-1,1))*.5f;
                }).Average();
                var descriptorDistance=MotionDistance(sample.Descriptor,seam.Descriptor);
                var score=distances.Max()+MathF.Sqrt(distances.Select(d=>d*d).Average())+feet*.3f+orientation*.15f+velocity*4+
                    MathF.Min(.15f,MathF.Sqrt(descriptorDistance)*.25f);
                return new {Seam=seam,Move=move,Score=score,DescriptorDistance=descriptorDistance,SeamJump=distances.Max()*height};
            }).ToArray();
            var best=candidates.MinBy(c=>c.Score)!;var donor=donors[best.Seam.Donor];
            var donorMiddle=transitions.Animations[best.Seam.Donor];
            var donorB=Resource.Load<AnimationPack>(Path.Combine(directory,donor.Folder,"b.GAP")).Animations[0];
            var first=Math.Max(0,sample.Frame-job.LeadInFrames);var worlds=new List<Dictionary<string,float[]>>();
            for(var f=first;f<=sample.Frame;f++)worlds.Add(AnimationPoseEvaluator.Evaluate(model.Model,animation,f/30f).ToDictionary(p=>p.Key.Name,p=>Array(p.Value)));
            var jump=body.Max(n=>Vector3.Distance(dancePose[n].Translation,(best.Seam.Next[n]*best.Move).Translation));
            // The seam corresponds to the last dance frame. Advance one donor
            // frame immediately; never repeat the seam or add a blend frame.
            foreach(var (clip,start) in new[]{(donorMiddle,best.Seam.Frame+1),(donorB,1)})
                for(var f=start;f<=(int)Math.Round(clip.Duration*30);f++)worlds.Add(AnimationPoseEvaluator.Evaluate(model.Model,clip,f/30f).ToDictionary(p=>p.Key.Name,p=>Array(motionNodes.Contains(p.Key)?p.Value*best.Move:p.Value)));
            var joined=FromWorlds(model.Model,model.Version,worlds,0,worlds.Count-1);
            reuse.Animations.Add(joined);
            var emitted=AnimationPoseEvaluator.Evaluate(model.Model,joined,(sample.Frame-first+1)/30f);
            var exportedYawError=BodyFacingYaw(model.Model,emitted)-BodyFacingYaw(model.Model,dancePose);
            exportedYawError=MathF.Atan2(MathF.Sin(exportedYawError),MathF.Cos(exportedYawError))*180/MathF.PI;
            var exportedRootError=Vector2.Distance(new Vector2(dancePose[root].Translation.X,dancePose[root].Translation.Z),new Vector2(emitted[root].Translation.X,emitted[root].Translation.Z));
            if(job.Placement.MatchYaw && job.Placement.YawJitterDegrees==0 && job.Placement.YawDegrees==0 && MathF.Abs(exportedYawError)>.01f)
                throw new InvalidDataException("Borrowed yaw was lost during GAP export");
            if(job.Placement.MatchPosition && exportedRootError>.01f && job.Placement.Translation.All(v=>v==0))
                throw new InvalidDataException("Borrowed placement was lost during GAP export");
            var retainedMiddleFrames=(int)Math.Round(donorMiddle.Duration*30)-best.Seam.Frame;
            report.Add(new{reuseClip=reuse.Animations.Count-1,rank=sample.Rank,sample.SourceClip,sample.Frame,donorClip=best.Seam.Donor,donorRank=donor.Rank,
                donor.BStartFrame,donor.TransitionFrames,seamFrame=best.Seam.Frame,firstBorrowedFrame=best.Seam.Frame+1,retainedMiddleFrames,
                searchedSeamCount=seams.Length,entryFitScore=best.Score,descriptorDistance=best.DescriptorDistance,
                seamPoseJump=best.SeamJump,seamPoseJumpRelativeToHeight=best.SeamJump/height,
                maxEntryJump=jump,maxEntryJumpRelativeToHeight=jump/height,matchedYaw=job.Placement.MatchYaw,
                matchedHeight=job.Placement.MatchUp,exportedYawErrorDegrees=exportedYawError,exportedRootHorizontalError=exportedRootError,
                sourceFacingDegrees=BodyFacingYaw(model.Model,dancePose)*180/MathF.PI,
                borrowedFacingDegrees=BodyFacingYaw(model.Model,best.Seam.Next.ToDictionary(p=>p.Key,p=>p.Value*best.Move))*180/MathF.PI,
                rootHorizontalError=Vector2.Distance(new Vector2(dancePose[root].Translation.X,dancePose[root].Translation.Z),new Vector2((best.Seam.Next[root]*best.Move).Translation.X,(best.Seam.Next[root]*best.Move).Translation.Z)),
                borrowedHeightChange=(best.Seam.Next[root]*best.Move).Translation.Y-best.Seam.Next[root].Translation.Y,
                addedInterpolation=false});
        }
        transitions.Save(Path.Combine(directory,"transitions.GAP"));demos.Save(Path.Combine(directory,"generated_demos.GAP"));reuse.Save(Path.Combine(directory,"reuse_demos.GAP"));Write(Path.Combine(directory,"reuse_report.json"),report);
        WriteBorrowedSamples(directory,model.Version,saved,reuse);
        // FBX includes every demo as an animation stack; GAP is the authoritative pack.
        Export(model,demos,Path.Combine(directory,"generated_demos.fbx"),false);Export(model,reuse,Path.Combine(directory,"reuse_demos.fbx"),false);
        Console.WriteLine($"Wrote {transitions.Animations.Count} transitions, {demos.Animations.Count} generated demos, {reuse.Animations.Count} unblended reuse demos");return 0;
    }

    static void WriteBorrowedSamples(string directory,uint version,LibraryPlan plan,AnimationPack reuse)
    {
        var heldOut=plan.SelectedSamples.Skip(plan.GeneratedCount).ToArray();
        if(reuse.Animations.Count!=heldOut.Length)throw new InvalidDataException("Borrowed clip count differs from the selected poses");
        for(var i=0;i<heldOut.Length;i++)
        {
            var sample=heldOut[i];var folder=Path.Combine(directory,sample.Folder);Directory.CreateDirectory(folder);
            var one=new AnimationPack(version);one.Animations.Add(reuse.Animations[i]);one.Save(Path.Combine(folder,"joined.GAP"));
            Write(Path.Combine(folder,"borrowed_sample.json"),new {sample.Rank,sample.SourceClip,sample.Frame,reuseClip=i,generatedAiMiddle=false,addedInterpolation=false});
        }
    }

    static int RenderLibrary(Job job, string directory, ModelPack model, LibraryPlan plan)
    {
        var modelFile=job.Model;
        var prefix=System.Text.RegularExpressions.Regex.Match(Path.GetFileName(modelFile),@"^pc\d{3}").Value;
        var faceFile=Path.Combine(Path.GetDirectoryName(modelFile)!,"face",prefix+"_f1.GMD");
        var parts=prefix.Length>0 && File.Exists(faceFile)?new[]{Resource.Load<ModelPack>(faceFile).Model}:System.Array.Empty<Model>();
        var reuseFile=Path.Combine(directory,"reuse_report.json");
        var reusedReport=File.Exists(reuseFile)?JsonDocument.Parse(File.ReadAllText(reuseFile)).RootElement:default;
        var angle=int.TryParse(Environment.GetEnvironmentVariable("GFD_REVIEW_YAW"),out var degrees)?degrees:0;
        var gallery=Path.Combine(directory,angle==0?"visual_review":$"visual_review_yaw_{angle}");Directory.CreateDirectory(gallery);
        foreach(var kind in new[]{"generated","reuse"})
        {
            var packFile=Path.Combine(directory,kind+"_demos.GAP");
            AnimationPack pack;
            if (File.Exists(packFile)) pack=Resource.Load<AnimationPack>(packFile);
            else if (kind=="generated")
            {
                pack=new AnimationPack(model.Version);
                foreach(var sample in plan.SelectedSamples.Take(plan.GeneratedCount))
                {
                    var joined=Path.Combine(directory,sample.Folder,"joined.GAP");
                    if (!File.Exists(joined)) break;
                    pack.Animations.Add(Resource.Load<AnimationPack>(joined).Animations[0]);
                }
            }
            else continue;
            for(var i=0;i<pack.Animations.Count;i++)
            {
                var sample=plan.SelectedSamples[kind=="generated"?i:plan.GeneratedCount+i];
                var donor=kind=="generated"?sample:plan.SelectedSamples[reusedReport[i].GetProperty("donorClip").GetInt32()];
                var lead=Math.Min(job.LeadInFrames,sample.Frame);
                var middleFrames=kind=="reuse" && reusedReport[i].TryGetProperty("retainedMiddleFrames",out var retained)?retained.GetInt32():donor.TransitionFrames+1;
                var end=lead+middleFrames;
                var animation=pack.Animations[i];
                var first=Math.Max(0,lead-6);
                var last=Math.Min(end+10,(int)Math.Round(animation.Duration*30));
                var frames=Enumerable.Range(first,last-first+1);
                var start=AnimationPoseEvaluator.Evaluate(model.Model,animation,lead/30f);
                foreach(var f in frames.Distinct())
                {
                    var pose=AnimationPoseEvaluator.Evaluate(model.Model,animation,f/30f);
                    PoseRender.Draw(model.Model,start,model.Model,pose,Path.Combine(gallery,$"{kind}_{i:D2}_f{f:D3}.png"),
                        $"{kind} clip {i+1}: rank {sample.Rank}, dance clip {sample.SourceClip} frame {sample.Frame}; before join / demo frame {f}", parts, centerEachPose: true);
                }
            }
        }
        Console.WriteLine("Rendered all generated and held-out joins from their GAP packs");return 0;
    }
}
