using System.Numerics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using GFDLibrary;
using GFDLibrary.Models;
using GFDLibrary.Animations;

internal static class KneeMatchAudit
{
    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        var studio = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, "GFDStudio.dll"));
        var clipType = studio.GetType("GFDStudio.AnimationMatching.Integration.GfdAnimationClip")!;
        var bake = studio.GetType("GFDStudio.AnimationMatching.Integration.GfdAnimationClipBaker")!.GetMethod("Bake")!;
        var baker = studio.GetType("GFDStudio.AnimationMatching.Integration.GfdAnimationClipBaker")!;
        var report = new List<object>();
        string root = @"M:\_P_backup\all models";
        Model DanceModel(string file, string id)
        {
            var model = Resource.Load<ModelPack>(file).Model;
            foreach (var part in new[] { Path.Combine(root, "p5d", "player", "p5", "face", $"pc{id}_f1.GMD"),
                Path.Combine(root, "p5d", "player", "p5", "hair", $"pc{id}_h00.GMD") })
                model.MergeWith(Resource.Load<ModelPack>(part).Model);
            return model;
        }
        foreach (var (royal, dance, costume, stem, slot, frame) in new[] {
            ("0004", "204", "16", "ae0004_304", 0, 135),
            ("0004", "204", "16", "ae0004_301", 1, 144),
            ("0004", "204", "26", "ae0004_304", 0, 135),
            ("0002", "202", "26", "ae0002_301", 0, 34),
            ("0002", "202", "16", "ae0002_301", 0, 34),
            ("0002", "202", "26", "ae0002_301", 1, 16),
            ("0002", "202", "26", "ae0002_304", 0, 34),
            ("0002", "202", "26", "bf0002_001", 0, 16),
            ("0005", "205", "26", "ab0005_051", 0, 30),
            ("0006", "206", "26", "ab0006_051", 0, 30) })
        {
            var sourceFile = Path.Combine(root, "p5r", "model", "character", royal, $"c{royal}_107_00.GMD");
            if (!File.Exists(sourceFile)) sourceFile = Path.Combine(root, "p5r", "model", "character", royal, $"c{royal}_051_00.GMD");
            var targetFile = Path.Combine(root, "p5d", "player", "p5", $"pc{dance}_{costume}.GMD");
            var animationFile = Path.Combine(root, "p5r", "model", "character", royal, stem.StartsWith("ae") ? "event" : stem.StartsWith("bf") ? "field" : "battle", stem + ".GAP");
            var source = Resource.Load<ModelPack>(sourceFile).Model;
            var target = DanceModel(targetFile, dance);
            Animation Load() => Resource.Load<AnimationPack>(animationFile).Animations[slot];
            var clip = Activator.CreateInstance(clipType, new object[] { stem, stem, source, (Func<Animation>)Load, 30f })!;
            clipType.GetProperty("SourcePackPath")!.SetValue(clip, animationFile);
            clipType.GetProperty("SourceClipIndex")!.SetValue(clip, slot);
            baker.GetMethod("SetTargetRetargetResources")!.Invoke(null, new object[] { target, targetFile, root });
            var preview = clipType.GetMethod("CreateTargetPreviewClip", new[] { typeof(Model) })!.Invoke(clip, new[] { target })!;
            var matcher = (Animation)bake.Invoke(null, new object[] { preview, target, target.Version, CancellationToken.None })!;
            var uncorrected = Load(); uncorrected.Retarget(source, target, false, false);
            var fullClipType = studio.GetType("GFDStudio.AnimationMatching.Integration.GfdTargetAnimationClip")!;
            var uncorrectedClip = Activator.CreateInstance(fullClipType, new object[] { "uncorrected", "uncorrected", target, uncorrected, 30f })!;
            var bad = (Animation)bake.Invoke(null, new object[] { uncorrectedClip, target, target.Version, CancellationToken.None })!;
            var fixedClip = Load();
            var result = P5dAnimationRetargeter.Retarget(fixedClip, source, target, animationFile, slot, targetFile, root, false);
            string name = $"pc{dance}_{costume}-{stem}-{slot}";
            var helperNames = new[] { "L_Knee_Roll_01", "L_Knee_Roll_02", "L_ExKnee", "R_Knee_Roll_01", "R_Knee_Roll_02", "R_ExKnee" };
            float poseError = 0;
            var saved = new AnimationPack(target.Version); saved.Animations.Add(matcher); saved.Save(Path.Combine(folder, name + ".GAP"));
            matcher = Resource.Load<AnimationPack>(Path.Combine(folder, name + ".GAP")).Animations[0];
            foreach (var f in new[] { 0, frame, (int)(fixedClip.Duration * 15), (int)(fixedClip.Duration * 30) }.Distinct())
            {
                float time = Math.Min(f / 30f, fixedClip.Duration);
                var before = AnimationPoseEvaluator.Evaluate(target, bad, time);
                var after = AnimationPoseEvaluator.Evaluate(target, matcher, time);
                var expected = AnimationPoseEvaluator.Evaluate(target, fixedClip, time);
                poseError = Math.Max(poseError, target.Nodes.Max(n => Vector3.Distance(after[n].Translation, expected[n].Translation)));
                if (poseError > .002f) throw new Exception($"Matcher lacks browser knee correction: {name} {poseError}");
                PoseRender.Draw(target, before, target, after, Path.Combine(folder, $"{name}-f{f}.png"), $"{name} frame {f}: before / corrected match preview");
                PoseRender.Draw(source, AnimationPoseEvaluator.Evaluate(source, Load(), time), target, after, Path.Combine(folder, $"{name}-f{f}-source.png"), $"{name} frame {f}: Royal / Dance corrected");
            }
            report.Add(new { name, poseError, result.KneeCorrectionApplied, result.ReferencePath, helperTracksBefore = bad.Controllers.Count(c => helperNames.Contains(c.TargetName)), helperTracksAfter = fixedClip.Controllers.Count(c => helperNames.Contains(c.TargetName)) });
            File.WriteAllText(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        // Both sides of the reported no-blend match must carry the correctives.
        var annFile = Path.Combine(root, "p5d", "player", "p5", "pc204_16.GMD");
        var ann = DanceModel(annFile, "204");
        var royalModel = Resource.Load<ModelPack>(Path.Combine(root, "p5r", "model", "character", "0004", "c0004_107_00.GMD")).Model;
        object Make(string stem, int slot)
        {
            string file = Path.Combine(root, "p5r", "model", "character", "0004", "event", stem + ".GAP");
            var value = Activator.CreateInstance(clipType, new object[] { stem, stem, royalModel,
                (Func<Animation>)(() => Resource.Load<AnimationPack>(file).Animations[slot]), 30f })!;
            clipType.GetProperty("SourcePackPath")!.SetValue(value, file);
            clipType.GetProperty("SourceClipIndex")!.SetValue(value, slot);
            return value;
        }
        baker.GetMethod("SetTargetRetargetResources")!.Invoke(null, new object[] { ann, annFile, root });
        var stitchType = studio.GetType("GFDStudio.AnimationMatching.Stitching.StitchedAnimation")!;
        var stitch = Activator.CreateInstance(stitchType, new object[] { Make("ae0004_304", 0), 135, Make("ae0004_301", 1), 144, 0f, true })!;
        var joinedClip = baker.GetMethod("CreateTargetPreviewClip", new[] { studio.GetType("GFDStudio.AnimationMatching.Core.IAnimationClip")!, typeof(Model) })!.Invoke(null, new object[] { stitch, ann })!;
        var joined = (Animation)bake.Invoke(null, new object[] { joinedClip, ann, ann.Version, CancellationToken.None })!;
        var joinedPack = new AnimationPack(ann.Version); joinedPack.Animations.Add(joined); joinedPack.Save(Path.Combine(folder, "ann-no-blend-match.GAP"));
        var reloaded = Resource.Load<AnimationPack>(Path.Combine(folder, "ann-no-blend-match.GAP")).Animations[0];
        for (int f = 129; f <= 145; f += 2)
        {
            var pose = AnimationPoseEvaluator.Evaluate(ann, reloaded, f / 30f);
            PoseRender.Draw(ann, AnimationPoseEvaluator.Evaluate(ann, reloaded, 135 / 30f), ann, pose,
                Path.Combine(folder, $"ann-joined-f{f}.png"), $"Reported Ann match, no blend: join pose / frame {f}");
        }
        // A Dance clip already authors its own knee drivers; do not recalibrate it.
        var danceFile = Path.Combine(root, "p5d", "player", "p5", "204", "pc204_007.GAP");
        var danceSource = Resource.Load<ModelPack>(annFile).Model;
        Animation DanceAnimation()
        {
            var body = Resource.Load<AnimationPack>(danceFile).Animations.MaxBy(a => a.Duration)!;
            var helpers = Resource.Load<AnimationPack>(danceFile.Replace(".GAP", "_16.GAP")).Animations.MaxBy(a => a.Duration)!;
            if (Math.Abs(body.Duration - helpers.Duration) > .04f) throw new Exception("Dance body/helper duration mismatch");
            return SplitCharacterAnimationComposer.AddComponentTracks(body, helpers);
        }
        var danceClip = Activator.CreateInstance(clipType, new object[] { "authored", "authored", danceSource, (Func<Animation>)DanceAnimation, 30f })!;
        var authoredPreview = clipType.GetMethod("CreateTargetPreviewClip", new[] { typeof(Model) })!.Invoke(danceClip, new[] { ann })!;
        var authoredAnimation = (Animation)authoredPreview.GetType().GetField("_animation", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(authoredPreview)!;
        var expectedDance = DanceAnimation(); expectedDance.Retarget(danceSource, ann, false, false);
        float authoredError = 0;
        foreach (float fraction in new[] { 0f, .25f, .5f, .75f, 1f })
        {
            float time = expectedDance.Duration * fraction;
            var expected = AnimationPoseEvaluator.Evaluate(ann, expectedDance, time);
            var actual = AnimationPoseEvaluator.Evaluate(ann, authoredAnimation, time);
            authoredError = Math.Max(authoredError, ann.Nodes.Max(n => Vector3.Distance(expected[n].Translation, actual[n].Translation)));
            PoseRender.Draw(ann, expected, ann, actual, Path.Combine(folder, $"authored-dance-{fraction:0.00}.png"), "Authored Dance knee motion: before / matcher");
        }
        if (authoredError > .001f) throw new Exception("Authored Dance knee tracks changed: " + authoredError);
        File.WriteAllText(Path.Combine(folder, "authored-dance.json"), JsonSerializer.Serialize(new { authoredError, duration = expectedDance.Duration }));
        return 0;
    }
}
