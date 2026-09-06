using System.Numerics;
using GFDLibrary;
using GFDLibrary.Animations;
using GFDLibrary.Models;
using System.Runtime.Loader;

AssemblyLoadContext.Default.Resolving += (_, name) => {
    var path = Path.GetFullPath(Path.Combine("GFDStudio-binary", name.Name + ".dll"));
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
var stdout = Console.Out;
Console.SetOut(TextWriter.Null);
var otherCharacter = args.Contains("--other");
var characterId = otherCharacter ? "0005" : "0004";
var danceId = otherCharacter ? "205" : "204";
var source = Resource.Load<ModelPack>($@"M:\_P_backup\p5 modding\dataR\model\character\{characterId}\c{characterId}_107_00.GMD");
var target = Resource.Load<ModelPack>($@"M:\_P_backup\p5d modding\game\Image0\data\ps4\dance\player\p5\pc{danceId}_26.GMD");
var animationPath = otherCharacter
    ? $@"M:\_P_backup\p5 modding\dataR\model\character\{characterId}\battle\ab{characterId}_051.GAP"
    : @"Q:\_coding\DaYoBuO\modssrc\dayobuo\repacked_0004_selected.GAP";
var pack = Resource.Load<AnimationPack>(animationPath);
Console.SetOut(stdout);
if (args.Contains("--native")) {
    var nativeRoot = $@"M:\_P_backup\p5d modding\game\Image0\data\data\dance\player\p5\{danceId}";
    foreach (var suffix in new[] { "", "_26", "_f", "_h26" }) {
        var native = Resource.Load<AnimationPack>(Path.Combine(nativeRoot, "pc" + danceId + "_001_p" + suffix + ".GAP"));
        Console.WriteLine($"NATIVE {suffix}: {native.Animations.Count} clips");
        var anim = native.Animations[0];
        Console.WriteLine(string.Join(", ", anim.Controllers.Select(c => c.TargetName)));
        if (suffix == "") foreach (var time in new[] {0f, 1f, 10f, 30f, 60f, 100f, 130f, 170f, 200f}) {
            var pose = AnimationPoseEvaluator.Evaluate(target.Model, anim, time);
            Console.Write($"KNEE {time}: ");
            foreach (var name in new[] {"LeftLeg", "L_Knee_Roll_01", "L_Knee_Roll_02", "L_ExKnee"}) {
                var n = target.Model.Nodes.First(n => n.Name == name);
                Matrix4x4.Invert(pose[n.Parent], out var parentInverse);
                var local = pose[n] * parentInverse;
                Matrix4x4.Decompose(local, out _, out var q, out var p);
                Console.Write($"{name} angleZ={2*Math.Atan2(q.Z,q.W):F5} P={p}; ");
            }
            Console.WriteLine();
        }
        foreach (var c in anim.Controllers.Where(c => c.TargetName.Contains("Knee") || c.TargetName == "LeftLeg" || c.TargetName == "LeftUpLeg")) {
            Console.WriteLine($"{c.TargetName} id={c.TargetId}");
            foreach (var l in c.Layers) {
                Console.WriteLine($"  {l.KeyType} keys={l.Keys.Count} posScale={l.PositionScale}");
                foreach (PRSKey k in l.Keys.OfType<PRSKey>().Where((k,i) => i == 0 || i == l.Keys.Count / 2))
                    Console.WriteLine($"  t={k.Time} P={k.Position} R={k.Rotation} S={k.Scale}");
            }
        }
    }
    var nodes = target.Model.Nodes.ToArray();
    foreach (var n in nodes.Where(n => n.Name.Contains("Knee"))) {
        var indices = target.Model.Bones.Select((b,i) => (b,i)).Where(x => nodes[x.b.NodeIndex] == n).Select(x => x.i).ToHashSet();
        var vertices = target.Model.Meshes.Where(m => m.VertexWeights != null).SelectMany(m => m.VertexWeights).Count(w => w.Indices.Where((b,i) => indices.Contains(b) && w.Weights[i] > 0).Any());
        Console.WriteLine($"WEIGHTS {n.Name}: {vertices} influenced vertices");
    }
    return;
}
if (args.Contains("--parts")) {
    foreach (var partPath in new[] { $@"face\pc{danceId}_f1.GMD", $@"hair\pc{danceId}_h26.GMD" }) {
        var part = Resource.Load<ModelPack>(Path.Combine(@"M:\_P_backup\p5d modding\game\Image0\data\ps4\dance\player\p5", partPath));
        Console.WriteLine(partPath);
        foreach (var n in part.Model.Nodes) Console.WriteLine($"{n.Name} <- {n.Parent?.Name} P={n.Translation} R={n.Rotation}");
    }
    return;
}
if (args.Contains("--nodes")) foreach (var (label, model) in new[] { ("SOURCE", source.Model), ("TARGET", target.Model) }) {
    Console.WriteLine($"{label}: nodes={model.Nodes.Count()} meshes={model.Meshes.Count()}");
    foreach (var n in model.Nodes.Where(n => n.Name.StartsWith("Bip01") || !n.HasAttachments && label == "TARGET" || n.Name == "root" || n.Name == "rot")) {
        Matrix4x4.Decompose(n.WorldTransform, out var s, out var r, out var p);
        Console.WriteLine($"{n.Name} <- {n.Parent?.Name}: localP={n.Translation} worldP={p} worldR={r} scale={s}");
    }
}
Console.WriteLine($"Source={animationPath}");
Console.WriteLine($"Animations={pack.Animations.Count}");
var port = Resource.Load<AnimationPack>(animationPath);
port.Retarget(source.Model, target.Model, false);
var outputDirectory = Path.GetFullPath(args.Contains("--assembled")
    ? $"artifacts/retarget-{characterId}-{danceId}-assembled"
    : $"artifacts/retarget-{characterId}-{danceId}");
Directory.CreateDirectory(outputDirectory);
if (args.Contains("--assembled")) {
    Console.SetOut(TextWriter.Null);
    var face = Resource.Load<ModelPack>($@"M:\_P_backup\p5d modding\game\Image0\data\ps4\dance\player\p5\face\pc{danceId}_f1.GMD");
    var hair = Resource.Load<ModelPack>($@"M:\_P_backup\p5d modding\game\Image0\data\ps4\dance\player\p5\hair\pc{danceId}_h26.GMD");
    var native = Resource.Load<AnimationPack>($@"M:\_P_backup\p5d modding\game\Image0\data\data\dance\player\p5\{danceId}\pc{danceId}_001_p.GAP");
    var combined = SplitCharacterRetargeter.CreatePreview(source.Model, pack, target, face, hair, native.Animations[0]);
    combined.Save(Path.Combine(outputDirectory, $"pc{danceId}_26_preview.GMD"));
    foreach (var (part, suffix) in new[] {(target.Model, ""), (face.Model, "_f"), (hair.Model, "_h26")}) {
        var standalone = SplitCharacterRetargeter.ForStandalonePart(combined, part);
        var standalonePath = Path.Combine(outputDirectory, "pc" + danceId + "_port_p" + suffix + ".GAP");
        standalone.Save(standalonePath);
        var standaloneReadback = Resource.Load<AnimationPack>(standalonePath);
        Console.SetOut(stdout);
        Console.WriteLine($"Standalone {suffix}: {standaloneReadback.Animations.Count} clips, " +
            $"first controllers={standaloneReadback.Animations.FirstOrDefault()?.Controllers.Count ?? 0}");
        Console.SetOut(TextWriter.Null);
    }
    target = Resource.Load<ModelPack>(Path.Combine(outputDirectory, $"pc{danceId}_26_preview.GMD"));
    port = target.AnimationPack;
    Console.SetOut(stdout);
    Console.WriteLine($"Assembled readback: {target.Model.Nodes.Count()} nodes, {target.Model.Meshes.Count()} meshes");
}
var outputPath = Path.Combine(outputDirectory, $"p5r_{characterId}_to_pc{danceId}_26.GAP");
port.Save(outputPath);
port = Resource.Load<AnimationPack>(outputPath);
Console.WriteLine($"Saved {outputPath}, {new FileInfo(outputPath).Length} bytes");
for (var clip = 0; clip < port.Animations.Count; clip++) {
    var anim = port.Animations[clip];
    var duplicates = anim.Controllers.GroupBy(c => c.TargetName).Count(g => g.Count() > 1);
    Console.WriteLine($"Clip {clip}: duration={anim.Duration}, controllers={anim.Controllers.Count}, duplicate targets={duplicates}");
    for (var frame = 0; frame < 5; frame++) {
        var time = anim.Duration * frame / 4f;
        var srcPose = AnimationPoseEvaluator.Evaluate(source.Model, pack.Animations[clip], time);
        var dstPose = AnimationPoseEvaluator.Evaluate(target.Model, anim, time);
        PoseRender.Draw(source.Model, srcPose, target.Model, dstPose, Path.Combine(outputDirectory, $"clip-{clip}-{frame}.png"), $"c{characterId} clip {clip}, {time:F2}s: P5R original / Dance retarget");
    }
}
