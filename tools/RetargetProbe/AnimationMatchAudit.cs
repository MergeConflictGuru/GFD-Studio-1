using System.Collections;
using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using GFDLibrary;
using GFDLibrary.Animations;
using GFDLibrary.Models;

// Load the built application so this probe exercises the actual matcher adapter and features,
// without copying their implementation into a test project or opening the application UI.
internal static partial class AnimationMatchAudit
{
    private delegate void PoseSample<T>(int frame, Span<T> pose);
    private static Type clipType = null!;
    private static Type boneType = null!;
    private static object extractor = null!;
    private static MethodInfo extract = null!;

    public static int Run(string[] args, TextWriter output)
    {
        string Option(string key, string fallback) => args.FirstOrDefault(a => a.StartsWith(key + "="))?[(key.Length + 1)..] ?? fallback;
        var studioPath = Path.GetFullPath(Option("--studio", "GFDStudio/bin/x64/Release/net8.0-windows/win-x64/GFDStudio.dll"));
        var root = Option("--data-root", @"M:\_P_backup\all models");
        var directory = Path.GetFullPath(Option("--audit-output", "artifacts/animatch-audit/current"));
        Directory.CreateDirectory(directory);
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(studioPath);
        clipType = assembly.GetType("GFDStudio.AnimationMatching.Integration.GfdAnimationClip", true)!;
        boneType = assembly.GetType("GFDStudio.AnimationMatching.Core.BoneTransform", true)!;
        var options = Activator.CreateInstance(assembly.GetType("GFDStudio.AnimationMatching.Core.AnimationMatchOptions", true)!)!;
        var extractorType = assembly.GetType("GFDStudio.AnimationMatching.Features.PoseFeatureExtractor", true)!;
        extractor = Activator.CreateInstance(extractorType, options)!;
        extract = extractorType.GetMethods().Single(m => m.Name == "Extract" && m.GetParameters().Length == 3);
        var checks = new List<object>();
        var failures = 0;

        var reference = Synthetic(Quaternion.Identity, Quaternion.Identity, 1f, false);
        var referenceClip = Clip("reference", reference.model, reference.animation);
        foreach (var spec in new[] {
            ("root bone axes", Quaternion.CreateFromYawPitchRoll(.9f, 1.2f, -.7f), Quaternion.Identity, 1f, false),
            ("joint bone axes", Quaternion.Identity, Quaternion.CreateFromYawPitchRoll(-.6f, .8f, 1.1f), 1f, false),
            ("model axes", Quaternion.Identity, Quaternion.Identity, 1f, false),
            ("body scale", Quaternion.Identity, Quaternion.Identity, 1.7f, false),
            ("cosmetic extent", Quaternion.Identity, Quaternion.Identity, 1f, true) })
        {
            var candidate = Synthetic(spec.Item2, spec.Item3, spec.Item4, spec.Item5);
            if (spec.Item1 == "model axes")
                candidate.model.RootNode.Rotation = Quaternion.CreateFromYawPitchRoll(.8f, -.5f, .4f);
            var candidateClip = Clip(spec.Item1, candidate.model, candidate.animation);
            var differences = new[] { 5, 15, 25 }.Select(frame => MaxDifference(Descriptor(referenceClip, frame), Descriptor(candidateClip, frame))).ToArray();
            var passed = differences.All(x => x < .002f);
            if (!passed) failures++;
            output.WriteLine($"{spec.Item1}: max descriptor error={differences.Max():G6}, passed={passed}");
            checks.Add(new { name = spec.Item1, differences, passed });
        }

        var identical = Synthetic(Quaternion.CreateFromYawPitchRoll(.9f, 1.2f, -.7f),
            Quaternion.CreateFromYawPitchRoll(-.6f, .8f, 1.1f), 1f, false);
        var different = Synthetic(Quaternion.Identity, Quaternion.Identity, 1f, false);
        foreach (PRSKey key in different.animation.Controllers.Single(c => c.TargetName == "LeftHand").Layers[0].Keys)
        {
            key.Position += new Vector3(0, 50, 0);
            key.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .8f) * key.Rotation;
        }
        var identicalClip = Clip("p5r same motion", identical.model, identical.animation);
        var differentClip = Clip("p5d different motion", different.model, different.animation);
        var discrimination = ChannelDistance(Descriptor(referenceClip, 15), Descriptor(differentClip, 15));
        var motionPreserved = discrimination.pose > .0001f && discrimination.orientation > .0001f;
        if (!motionPreserved) failures++;
        checks.Add(new { name = "pose and rotation remain distinguishable", passed = motionPreserved });
        var corpusType = assembly.GetType("GFDStudio.AnimationMatching.Core.AnimationCorpus", true)!;
        var clips = Array.CreateInstance(assembly.GetType("GFDStudio.AnimationMatching.Core.IAnimationClip", true)!, 2);
        clips.SetValue(identicalClip, 0);
        clips.SetValue(differentClip, 1);
        var corpus = Activator.CreateInstance(corpusType, clips)!;
        var databaseType = assembly.GetType("GFDStudio.AnimationMatching.Index.AnimationSearchDatabase", true)!;
        var database = databaseType.GetMethod("Build")!.Invoke(null, new object[] { corpus, options, null!, CancellationToken.None })!;
        var cacheType = assembly.GetType("GFDStudio.AnimationMatching.Index.AnimationIndexCache", true)!;
        var cachePath = Path.Combine(directory, "fixture-cache.bin");
        cacheType.GetMethod("Save")!.Invoke(null, new object[] { cachePath, database, "axis-invariance-fixture" });
        var mapped = cacheType.GetMethod("TryLoad")!.Invoke(null, new object[] { cachePath, corpus, options, "axis-invariance-fixture", null! })
            ?? throw new Exception("Could not reload audit cache");
        foreach (var db in new[] { database, mapped })
        {
            var matcherType = assembly.GetType("GFDStudio.AnimationMatching.Search.AnimationMatcher", true)!;
            var matcher = Activator.CreateInstance(matcherType, db)!;
            var results = (IEnumerable)matcherType.GetMethods().Single(m => m.Name == "Search" && m.GetParameters().Length == 5)
                .Invoke(matcher, new object[] { referenceClip, 15, 15, 2, CancellationToken.None })!;
            var best = results.Cast<object>().First();
            var candidate = best.GetType().GetProperty("Candidate")!.GetValue(best)!;
            var bestId = (string)clipType.GetProperty("Id")!.GetValue(candidate)!;
            var passed = bestId == "p5r same motion";
            if (!passed) failures++;
            var name = db == database ? "in-memory ranking" : "mapped cache ranking";
            output.WriteLine($"{name}: best={bestId}, passed={passed}");
            checks.Add(new { name, bestId, passed });
            ((IDisposable)db).Dispose();
        }
        var priorCache = Option("--prior-index", "");
        if (priorCache.Length > 0)
        {
            var stale = cacheType.GetMethod("TryLoad")!.Invoke(null, new object[] { Path.GetFullPath(priorCache), corpus, options, "axis-invariance-fixture", null! });
            var passed = stale == null;
            if (!passed) { failures++; ((IDisposable)stale!).Dispose(); }
            output.WriteLine($"Old descriptor cache rejected={passed}");
            checks.Add(new { name = "old descriptor cache rejected", passed });
        }

        var real = new List<object>();
        foreach (var character in new[] { ("201", "0001"), ("204", "0004"), ("206", "0006"), ("208", "0008") })
        {
            var danceModelPath = Path.Combine(root, "p5d", "player", "p5", $"pc{character.Item1}_26.GMD");
            var royalModelPath = Path.Combine(root, "p5r", "model", "character", character.Item2, $"c{character.Item2}_107_00.GMD");
            if (!File.Exists(royalModelPath))
                royalModelPath = Path.Combine(root, "p5r", "model", "character", character.Item2, $"c{character.Item2}_051_00.GMD");
            var dance = Resource.Load<ModelPack>(danceModelPath).Model;
            var royal = Resource.Load<ModelPack>(royalModelPath).Model;
            var bindD = Clip("dance bind", dance, new Animation());
            var bindR = Clip("royal bind", royal, new Animation());
            var bindDistance = ChannelDistance(Descriptor(bindD, 0), Descriptor(bindR, 0));
            output.WriteLine($"{character.Item1}/{character.Item2} bind: pose={bindDistance.pose:G5}, orientation={bindDistance.orientation:G5}");
            var animationPaths = new[] {
                Directory.EnumerateFiles(Path.Combine(root, "p5d", "player", "p5", character.Item1), $"pc{character.Item1}_???.GAP").OrderBy(p => p, StringComparer.Ordinal).First(),
                Path.Combine(root, "p5r", "model", "character", character.Item2, "field", $"bf{character.Item2}_001.GAP") };
            foreach (var path in animationPaths)
            {
                var pack = Resource.Load<AnimationPack>(path);
                var sourceModel = path.Contains("p5d") ? dance : royal;
                var destinationModel = path.Contains("p5d") ? royal : dance;
                foreach (var slot in new[] { 0, Math.Min(2, pack.Animations.Count - 1) }.Distinct())
                {
                    var animation = pack.Animations[slot];
                    if (path.Contains("p5d"))
                    {
                        var formType = assembly.GetType("GFDStudio.GUI.Forms.MainForm", true)!;
                        var entryType = formType.GetNestedType("CharacterAnimationEntry", BindingFlags.NonPublic)!;
                        var entry = Activator.CreateInstance(entryType, true)!;
                        entryType.GetProperty("PackPath")!.SetValue(entry, path);
                        entryType.GetProperty("Index")!.SetValue(entry, slot);
                        animation = (Animation)formType.GetMethod("ComposeCharacterBrowserAnimation", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
                            new object[] { entry, animation, danceModelPath, null!, null!, false, null!, CancellationToken.None })!;
                    }
                    var converted = Clone(animation);
                    converted.Retarget(sourceModel, destinationModel, false, true);
                    var source = Clip("source", sourceModel, animation);
                    var destination = Clip("converted", destinationModel, converted);
                    var count = (int)clipType.GetProperty("FrameCount")!.GetValue(source)!;
                    foreach (var frame in new[] { 0, count / 3, count * 2 / 3 }.Distinct())
                    {
                        var a = Descriptor(source, frame);
                        var b = Descriptor(destination, frame);
                        if (a.Concat(b).Any(x => !float.IsFinite(x)))
                            throw new Exception("Nonfinite real-data descriptor");
                        var distance = ChannelDistance(a, b);
                        var name = $"{character.Item1}-{Path.GetFileNameWithoutExtension(path)}-{slot}-{frame}";
                        var sourcePose = AnimationPoseEvaluator.Evaluate(sourceModel, animation, frame / 30f);
                        var destinationPose = AnimationPoseEvaluator.Evaluate(destinationModel, converted, frame / 30f);
                        PoseRender.Draw(sourceModel, sourcePose, destinationModel, destinationPose,
                            Path.Combine(directory, name + ".png"), $"{name}: source / same motion on other rig");
                        DrawCanonical(source, destination, frame, Path.Combine(directory, name + "-canonical.png"), name);
                        real.Add(new { name, pose = distance.pose, velocity = distance.velocity, orientation = distance.orientation });
                    }
                }
            }
        }
        File.WriteAllText(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(new { checks, real, failures }, new JsonSerializerOptions { WriteIndented = true }));
        output.WriteLine($"Audit completed: {failures} invariance failures; {real.Count} real-data frame pairs; {directory}");
        return failures == 0 ? 0 : 1;
    }

    private static object Clip(string id, Model model, Animation animation) => Activator.CreateInstance(clipType,
        id, id, model, (Func<Animation>)(() => animation), 30f)!;

    private static float[] Descriptor(object clip, int frame)
    {
        var skeleton = clipType.GetProperty("Skeleton")!.GetValue(clip)!;
        var bones = (int[])extractor.GetType().GetMethod("SelectFeatureBones")!.Invoke(extractor, new[] { skeleton })!;
        return (float[])extract.Invoke(extractor, new object[] { clip, frame, bones })!;
    }

    private static float MaxDifference(float[] a, float[] b) => a.Zip(b, (x, y) => Math.Abs(x - y)).Max();

    private static Vector3[] SamplePositions<T>(object clip, int frame)
    {
        var pose = new T[18];
        var sample = (PoseSample<T>)Delegate.CreateDelegate(typeof(PoseSample<T>), clip, "SampleGlobalPose");
        sample(frame, pose.AsSpan());
        var position = typeof(T).GetProperty("Position")!;
        return pose.Select(p => (Vector3)position.GetValue(p)!).ToArray();
    }

    private static void DrawCanonical(object source, object destination, int frame, string path, string caption)
    {
        using var bitmap = new Bitmap(1000, 440);
        using var graphics = Graphics.FromImage(bitmap);
        using var font = new Font("Arial", 12);
        graphics.Clear(Color.FromArgb(30, 34, 38));
        graphics.DrawString(caption + " | normalized match pose: front / side", font, Brushes.White, 12, 12);
        var parents = new[] { -1, 0, 1, 2, 3, 4, 3, 6, 7, 3, 9, 10, 1, 12, 13, 1, 15, 16 };
        foreach (var item in new[] { (source, Color.Cyan), (destination, Color.Orange) })
        {
            var positions = (Vector3[])typeof(AnimationMatchAudit).GetMethod(nameof(SamplePositions), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(boneType).Invoke(null, new object[] { item.Item1, frame })!;
            var skeleton = clipType.GetProperty("Skeleton")!.GetValue(item.Item1)!;
            var height = (float)skeleton.GetType().GetProperty("ReferenceHeight")!.GetValue(skeleton)!;
            var origin = positions[0];
            var minY = positions.Min(p => (p.Y - origin.Y) / height);
            using var pen = new Pen(item.Item2, 3);
            PointF Point(int joint, bool side)
            {
                var p = (positions[joint] - origin) / height;
                return new PointF((side ? 750 : 250) + (side ? p.Z : p.X) * 290, 395 - (p.Y - minY) * 290);
            }
            foreach (var side in new[] { false, true })
                for (var i = 1; i < positions.Length; i++)
                    graphics.DrawLine(pen, Point(i, side), Point(parents[i], side));
        }
        bitmap.Save(path, ImageFormat.Png);
    }

    private static (float pose, float velocity, float orientation) ChannelDistance(float[] a, float[] b)
    {
        float pose = 0, velocity = 0, orientation = 0;
        for (var i = 0; i < a.Length - 4; i++)
        {
            var d = (a[i] - b[i]) * (a[i] - b[i]);
            if (i % 12 < 3) pose += d;
            else if (i % 12 < 6) velocity += d;
            else orientation += d;
        }
        return (pose / a.Length, velocity / a.Length, orientation / a.Length);
    }

    private static Animation Clone(Animation animation)
    {
        using var stream = new MemoryStream();
        animation.Save(stream, true);
        stream.Position = 0;
        return Resource.Load<Animation>(stream);
    }

    private static (Model model, Animation animation) Synthetic(Quaternion rootAxes, Quaternion jointAxes, float scale, bool cosmetic)
    {
        var names = new[] { "Hips", "Spine", "Spine2", "neck", "head", "LeftArm", "LeftForeArm", "LeftHand", "RightArm", "RightForeArm", "RightHand", "LeftUpLeg", "LeftLeg", "LeftFoot", "RightUpLeg", "RightLeg", "RightFoot" };
        var positions = new[] { new Vector3(0,100,0), new(0,110,0), new(0,130,0), new(0,145,0), new(0,160,0), new(20,140,0), new(45,140,0), new(65,140,0), new(-20,140,0), new(-45,140,0), new(-65,140,0), new(10,95,0), new(10,55,0), new(10,10,0), new(-10,95,0), new(-10,55,0), new(-10,10,0) };
        var model = new Model { RootNode = new Node("RootNode") };
        var root = new Node("root", new Vector3(0, 100, 0) * scale, rootAxes, Vector3.One);
        model.RootNode.AddChildNode(root);
        for (var i = 0; i < names.Length; i++)
            root.AddChildNode(new Node(names[i], Vector3.Transform((positions[i] - new Vector3(0,100,0)) * scale, Quaternion.Inverse(rootAxes)), Quaternion.Inverse(rootAxes) * jointAxes, Vector3.One));
        if (cosmetic) model.RootNode.AddChildNode(new Node("hair_helper", new Vector3(0,500,0), Quaternion.Identity, Vector3.One));
        var animation = new Animation { Duration = 1f };
        foreach (var node in model.Nodes.Where(n => n != model.RootNode))
        {
            var controller = new AnimationController { TargetKind = TargetKind.Node, TargetName = node.Name };
            var layer = new AnimationLayer { KeyType = KeyType.NodePR };
            for (var frame = 0; frame <= 30; frame++)
            {
                var t = frame / 30f;
                var motion = Quaternion.CreateFromYawPitchRoll(.4f * t, .15f * t, -.1f * t);
                var rotation = node == root ? motion * rootAxes : node.Rotation;
                var position = node == root ? node.Translation + new Vector3(12*t, 3*t, 20*t)*scale : node.Translation;
                layer.Keys.Add(new PRSKey(KeyType.NodePR) { Time = t, Position = position, Rotation = rotation });
            }
            controller.Layers.Add(layer);
            animation.Controllers.Add(controller);
        }
        return (model, animation);
    }
}
