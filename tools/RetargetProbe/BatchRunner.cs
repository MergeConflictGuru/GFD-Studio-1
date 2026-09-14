using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Text;
using System.Text.Json;
using GFDLibrary;
using GFDLibrary.Animations;
using GFDLibrary.Models;

internal static class BatchRunner
{
    private const string P5rRoot = @"M:\_P_backup\p5 modding\dataR\model\character";
    private const string P5dModelRoot = @"M:\_P_backup\p5d modding\game\Image0\data\ps4\dance\player\p5";
    private const string P5dAnimationRoot = @"M:\_P_backup\p5d modding\game\Image0\data\data\dance\player\p5";

    private static readonly ActionSpec[] Actions =
    {
        new("ae0004_051", "event", 10),
        new("ae0004_144", "event", 3),
        new("ae0004_147", "event", 4),
        new("ae0004_148", "event", 1),
        new("ae0004_207", "event", 2),
        new("ae0004_706", "event", 5),
        new("bb0004_051", "battle", 24),
    };

    private static readonly TargetSpec[] Targets =
    {
        new("201", "0001", "pc201_26", "pc201_f1", "pc201_h00", "pc201_003_p"),
        new("202", "0002", "pc202_26", "pc202_f1", "pc202_h00", "pc202_002_p"),
        new("203", "0003", "pc203_26", "pc203_f1", "pc203_h00", "pc203_001_p"),
        new("204", "0004", "pc204_26", "pc204_f1", "pc204_h00", "pc204_018_p"),
        new("205", "0005", "pc205_26", "pc205_f1", "pc205_h00", "pc205_001_p"),
        new("206", "0006", "pc206_26", "pc206_f1", "pc206_h00", "pc206_001_p"),
        new("207", "0007", "pc207_26", "pc207_f1", "pc207_h00", "pc207_002_p"),
        new("208", "0008", "pc208_26", "pc208_f1", "pc208_h00", "pc208_001_p"),
        new("209", "0009", "pc209_26", "pc209_f1", "pc209_h00", "pc209_029"),
        new("210", "0010", "pc210_01", "pc210_f1", "pc210_h00", "pc210_002_p"),
        new("211", "0010", "pc211_01", "pc211_f1", "pc211_h00", "pc211_002_p"),
        new("212", "0010", "pc212_01", "pc212_f1", "pc212_h00", "pc212_030"),
    };

    private static readonly string[] SimilarityRoles =
    {
        "hips", "spine", "spine1", "spine2", "neck", "head",
        "leftshoulder", "rightshoulder", "leftarm", "rightarm",
        "leftforearm", "rightforearm", "lefthand", "righthand",
        "leftupleg", "rightupleg", "leftleg", "rightleg",
        "leftfoot", "rightfoot",
    };

    public static int Run(string[] args)
    {
        var reportOnly = args.Contains("--batch-report", StringComparer.OrdinalIgnoreCase) &&
                         !args.Contains("--batch", StringComparer.OrdinalIgnoreCase);
        var outputOption = args.FirstOrDefault(a => a.StartsWith("--batch-output=", StringComparison.OrdinalIgnoreCase));
        var outputRoot = Path.GetFullPath(outputOption == null
            ? "artifacts/p5d-action-packs"
            : outputOption.Substring("--batch-output=".Length));
        var render = !args.Contains("--no-render", StringComparer.OrdinalIgnoreCase);
        var selectedTarget = args.FirstOrDefault(a => a.StartsWith("--dance=", StringComparison.OrdinalIgnoreCase))?
            .Substring("--dance=".Length);
        var targetSpecs = Targets.Where(t => selectedTarget == null || t.DanceId == selectedTarget).ToArray();
        if (targetSpecs.Length == 0)
            throw new ArgumentException($"Unknown --dance value: {selectedTarget}");

        Directory.CreateDirectory(outputRoot);
        var report = new StringBuilder();
        report.AppendLine("P5D action-pack retarget report");
        report.AppendLine($"Generated UTC: {DateTime.UtcNow:O}");
        report.AppendLine("Source action set:");
        for (var i = 0; i < Actions.Length; i++)
            report.AppendLine($"  {i + 1}. {Actions[i].DisplayName} #{Actions[i].Index}");
        report.AppendLine();

        var referenceModel = LoadSourceModel("0004");
        var reference = LoadReferenceAnimations(referenceModel);
        report.AppendLine("Reference source: P5R 0004/Ann, selected seven-clip standalone pack");
        report.AppendLine();

        var reportRows = new List<ReportRow>();
        foreach (var target in targetSpecs)
        {
            Console.WriteLine($"[{target.DanceId}] source {target.SourceId}, resolving source actions...");
            var sourceModel = LoadSourceModel(target.SourceId);
            var selection = BuildSourceSelection(target, sourceModel, referenceModel, reference);
            AppendSelectionReport(report, target, sourceModel, selection);
            reportRows.AddRange(selection.Select((s, index) => new ReportRow(target.DanceId, index + 1,
                s.Status, s.SourcePath, s.SourceIndex, s.Score, s.Note)));

            if (reportOnly)
                continue;

            GenerateTarget(target, sourceModel, selection, outputRoot, render);
        }

        if (!reportOnly)
        {
            var reportPath = Path.Combine(outputRoot, "replacement-report.md");
            File.WriteAllText(reportPath, report.ToString(), Encoding.UTF8);
            var jsonPath = Path.Combine(outputRoot, "replacement-report.json");
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(reportRows, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            Console.WriteLine($"Report: {reportPath}");
        }
        else
        {
            var reportPath = Path.Combine(outputRoot, "replacement-report-preview.md");
            File.WriteAllText(reportPath, report.ToString(), Encoding.UTF8);
            Console.WriteLine($"Report preview: {reportPath}");
        }

        return 0;
    }

    private static List<Selection> BuildSourceSelection(TargetSpec target, Model sourceModel,
        Model referenceModel, IReadOnlyList<Animation> reference)
    {
        var result = new List<Selection>(Actions.Length);
        for (var actionIndex = 0; actionIndex < Actions.Length; actionIndex++)
        {
            var action = Actions[actionIndex];
            var exactPath = ResolveActionPath(target.SourceId, action);
            var missingExactNote = exactPath == null ? "same source file is missing" : "same source file does not contain the requested clip index";
            if (exactPath != null)
            {
                try
                {
                    var pack = Resource.Load<AnimationPack>(exactPath);
                    if (action.Index <= pack.Animations.Count)
                    {
                        var animation = PrepareActionAnimation(pack.Animations[action.Index - 1]);
                        result.Add(new Selection(animation, "exact", exactPath, action.Index, 0,
                            "same source filename and animation index"));
                        continue;
                    }
                    missingExactNote = $"{Path.GetFileName(exactPath)} has {pack.Animations.Count} clips; requested #{action.Index}";
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  warning: could not load {exactPath}: {ex.Message}");
                    missingExactNote = $"same source file could not be loaded: {ex.Message}";
                }
            }

            var referenceOnTarget = Clone(reference[actionIndex]);
            var referencePack = new AnimationPack(sourceModel.Version) { Animations = new List<Animation> { referenceOnTarget } };
            referencePack.Retarget(referenceModel, sourceModel, false);
            var desired = referencePack.Animations[0];
            var candidate = FindSimilarEventAnimation(target.SourceId, sourceModel, desired,
                action.Kind == "event" ? action : new ActionSpec(action.DisplayName, "event", action.Index));
            if (candidate != null)
            {
                result.Add(new Selection(candidate.Animation, "similar", candidate.Path, candidate.Index,
                    candidate.Score, missingExactNote + "; " + candidate.Note));
                continue;
            }

            // Keep the pack structurally usable even when this character has no
            // event corpus at all. The report calls out this last-resort reuse.
            result.Add(new Selection(PrepareActionAnimation(desired), "surrogate", null, 0, float.PositiveInfinity,
                missingExactNote + "; no character-specific event candidate; reused Ann action after source-rig retarget"));
        }
        return result;
    }

    private static Candidate FindSimilarEventAnimation(string sourceId, Model sourceModel,
        Animation desired, ActionSpec requested)
    {
        var eventDirectory = Path.Combine(P5rRoot, sourceId, "event");
        if (!Directory.Exists(eventDirectory))
            return null;

        var desiredSignature = BuildSignature(sourceModel, desired);
        var preferredStem = requested.DisplayName.Replace("0004", sourceId, StringComparison.OrdinalIgnoreCase);
        if (requested.Kind == "event" && preferredStem.StartsWith("bb", StringComparison.OrdinalIgnoreCase))
            preferredStem = "ae" + preferredStem.Substring(2);
        Candidate best = null;
        foreach (var path in Directory.EnumerateFiles(eventDirectory, "*.GAP", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            AnimationPack pack;
            try
            {
                pack = Resource.Load<AnimationPack>(path);
            }
            catch
            {
                continue;
            }

            for (var index = 0; index < pack.Animations.Count; index++)
            {
                var animation = pack.Animations[index];
                if (!AnimationAnalysis.HasBodyMotion(animation))
                    continue;
                var score = SignatureDistance(desiredSignature, BuildSignature(sourceModel, animation));
                // Prefer a candidate from the same event family when scores are
                // close. It keeps event replacements in the same authored pool.
                var requestedSuffix = requested.DisplayName.Contains('_')
                    ? requested.DisplayName.Substring(requested.DisplayName.IndexOf('_') + 1)
                    : requested.DisplayName;
                var familyPenalty = Path.GetFileNameWithoutExtension(path)
                    .Contains("_" + requestedSuffix, StringComparison.OrdinalIgnoreCase)
                    ? 0f : 0.02f;
                var preferredFile = Path.GetFileNameWithoutExtension(path)
                    .Equals(preferredStem, StringComparison.OrdinalIgnoreCase);
                if (preferredFile)
                    familyPenalty -= 100f;
                var rankScore = score + familyPenalty;
                if (best == null || rankScore < best.RankScore)
                    best = new Candidate(PrepareActionAnimation(animation), path, index + 1, score, rankScore,
                        preferredFile
                            ? $"same event file, alternate clip; score {score:F3}"
                            : $"nearest body-motion event candidate; score {score:F3}");
            }
        }
        return best;
    }

    private static PoseSignature BuildSignature(Model model, Animation animation)
    {
        var nodes = model.Nodes
            .Where(n => SimilarityRoles.Contains(AnimationSkeletonRoles.GetRole(n.Name), StringComparer.OrdinalIgnoreCase))
            .GroupBy(n => AnimationSkeletonRoles.GetRole(n.Name), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var motionRoot = AnimationSkeletonRoles.ResolveMotionRoot(model);
        var samples = new List<PoseSample>();
        foreach (var fraction in new[] { 0f, .25f, .5f, .75f, 1f })
        {
            var pose = AnimationPoseEvaluator.Evaluate(model, animation, animation.Duration * fraction);
            Matrix4x4.Invert(pose[motionRoot], out var inverseRoot);
            foreach (var node in nodes)
            {
                var relative = pose[node] * inverseRoot;
                Matrix4x4.Decompose(relative, out _, out var rotation, out var position);
                samples.Add(new PoseSample(position, Quaternion.Normalize(rotation)));
            }
        }
        return new PoseSignature(animation.Duration, samples.ToArray(),
            animation.Controllers.Count(c => c.TargetKind == TargetKind.Node));
    }

    private static float SignatureDistance(PoseSignature first, PoseSignature second)
    {
        if (first.Samples.Length != second.Samples.Length || first.Samples.Length == 0)
            return 1000f;

        var position = 0f;
        var rotation = 0f;
        for (var i = 0; i < first.Samples.Length; i++)
        {
            position += Vector3.Distance(first.Samples[i].Position, second.Samples[i].Position);
            rotation += 1f - MathF.Abs(Quaternion.Dot(first.Samples[i].Rotation, second.Samples[i].Rotation));
        }

        var count = first.Samples.Length;
        var duration = MathF.Abs(first.Duration - second.Duration) /
                       MathF.Max(.25f, MathF.Max(first.Duration, second.Duration));
        var controllers = MathF.Abs(first.Controllers - second.Controllers) /
                          MathF.Max(1, MathF.Max(first.Controllers, second.Controllers));
        return position / count + rotation / count * 2f + duration * .35f + controllers * .15f;
    }

    private static void GenerateTarget(TargetSpec target, Model sourceModel, IReadOnlyList<Selection> selection,
        string outputRoot, bool render)
    {
        var sourcePack = new AnimationPack(sourceModel.Version) {
            Flags = AnimationPackFlags.Bit3,
            Animations = selection.Select(s => s.Animation).ToList(),
            BlendAnimations = new List<Animation>()
        };
        sourcePack.SetVersion(sourceModel.Version);
        var targetDirectory = Path.Combine(outputRoot, $"p5d-{target.DanceId}");
        Directory.CreateDirectory(targetDirectory);
        var sourceDirectory = Path.Combine(outputRoot, "source-p5r");
        Directory.CreateDirectory(sourceDirectory);
        var sourcePackPath = Path.Combine(sourceDirectory, $"c{target.SourceId}_action_replacement.GAP");
        sourcePack.Save(sourcePackPath);

        var bodyPath = Path.Combine(P5dModelRoot, target.BodyModel + ".GMD");
        var facePath = Path.Combine(P5dModelRoot, "face", target.FaceModel + ".GMD");
        var hairPath = Path.Combine(P5dModelRoot, "hair", target.HairModel + ".GMD");
        var nativePath = ResolveNativeAnimationPath(target);
        var body = Resource.Load<ModelPack>(bodyPath);
        var face = Resource.Load<ModelPack>(facePath);
        var hair = Resource.Load<ModelPack>(hairPath);
        var native = Resource.Load<AnimationPack>(nativePath);
        // Some P5D character GMDs carry a legacy embedded animation chunk
        // that cannot be round-tripped by ModelPack's generic copier. It is
        // unrelated to the mesh/skeleton and must not enter the split GAPs.
        body.AnimationPack = null;
        face.AnimationPack = null;
        hair.AnimationPack = null;
        var combined = SplitCharacterRetargeter.CreatePreview(sourceModel, sourcePack, body, face, hair,
            native.Animations.First(a => a.Duration > 0));

        // The preview is useful for inspection and makes the exact split source
        // visible without changing the native game's files.
        combined.Save(Path.Combine(targetDirectory, target.BodyModel + "_preview.GMD"));
        var bodyPack = SplitCharacterRetargeter.ForStandalonePart(combined, body.Model, SplitCharacterPart.Body);
        var facePack = SplitCharacterRetargeter.ForStandalonePart(combined, face.Model, SplitCharacterPart.Face);
        var hairPack = SplitCharacterRetargeter.ForStandalonePart(combined, hair.Model, SplitCharacterPart.Hair);
        var bodyOutput = Path.Combine(targetDirectory, target.BodyModel + "_p.GAP");
        var faceOutput = Path.Combine(targetDirectory, target.BodyModel + "_p_f.GAP");
        var hairSuffix = target.HairModel.Substring(("pc" + target.DanceId + "_").Length);
        var hairOutput = Path.Combine(targetDirectory, target.BodyModel + "_p_" + hairSuffix + ".GAP");
        bodyPack.Save(bodyOutput);
        facePack.Save(faceOutput);
        hairPack.Save(hairOutput);

        var bodyReadback = Resource.Load<AnimationPack>(bodyOutput);
        var faceReadback = Resource.Load<AnimationPack>(faceOutput);
        var hairReadback = Resource.Load<AnimationPack>(hairOutput);
        VerifySplit(target, combined, body, face, hair, bodyReadback, faceReadback, hairReadback, targetDirectory);
        if (render)
            RenderChecks(target, sourceModel, sourcePack, combined, body, face, hair, bodyReadback, faceReadback,
                hairReadback, targetDirectory);

        Console.WriteLine($"[{target.DanceId}] wrote {targetDirectory} ({bodyReadback.Animations.Count} clips, " +
                          $"body/face/hair {bodyReadback.Animations[0].Controllers.Count}/" +
                          $"{faceReadback.Animations[0].Controllers.Count}/" +
                          $"{hairReadback.Animations[0].Controllers.Count})");
    }

    private static void VerifySplit(TargetSpec target, ModelPack combined, ModelPack body, ModelPack face,
        ModelPack hair, AnimationPack bodyReadback, AnimationPack faceReadback, AnimationPack hairReadback,
        string outputDirectory)
    {
        if (bodyReadback.Animations.Count != Actions.Length || faceReadback.Animations.Count != Actions.Length ||
            hairReadback.Animations.Count != Actions.Length)
            throw new InvalidDataException($"{target.DanceId}: split animation count mismatch");

        var maxError = 0f;
        for (var clip = 0; clip < Actions.Length; clip++)
        {
            var recombined = SplitCharacterAnimationComposer.AddComponentTracks(
                Clone(bodyReadback.Animations[clip]), faceReadback.Animations[clip], hairReadback.Animations[clip]);
            foreach (var fraction in new[] { 0f, .5f, 1f })
            {
                var time = combined.AnimationPack.Animations[clip].Duration * fraction;
                var expected = AnimationPoseEvaluator.Evaluate(combined.Model, combined.AnimationPack.Animations[clip], time);
                var actual = AnimationPoseEvaluator.Evaluate(combined.Model, recombined, time);
                foreach (var node in combined.Model.Nodes)
                {
                    var expectedPosition = expected[node].Translation;
                    var actualPosition = actual[node].Translation;
                    maxError = MathF.Max(maxError, Vector3.Distance(expectedPosition, actualPosition));
                }
            }
        }
        File.WriteAllText(Path.Combine(outputDirectory, "verification.txt"),
            $"clips={bodyReadback.Animations.Count}\nmax_world_position_error={maxError:R}\n" +
            "split_readback=ok\n");
        if (maxError > .35f)
            throw new InvalidDataException($"{target.DanceId}: split roundtrip position error {maxError}");
    }

    private static void RenderChecks(TargetSpec target, Model sourceModel, AnimationPack sourcePack,
        ModelPack combined, ModelPack body, ModelPack face, ModelPack hair, AnimationPack bodyReadback,
        AnimationPack faceReadback, AnimationPack hairReadback, string outputDirectory)
    {
        var renderDirectory = Path.Combine(outputDirectory, "visual-checks");
        Directory.CreateDirectory(renderDirectory);
        foreach (var clip in new[] { 0, 3, 6 })
        {
            foreach (var fraction in new[] { 0f, .5f, 1f })
            {
                var time = combined.AnimationPack.Animations[clip].Duration * fraction;
                var sourcePose = AnimationPoseEvaluator.Evaluate(sourceModel, sourcePack.Animations[clip], time);
                var targetPose = AnimationPoseEvaluator.Evaluate(combined.Model, combined.AnimationPack.Animations[clip], time);
                PoseRender.Draw(sourceModel, sourcePose, combined.Model, targetPose,
                    Path.Combine(renderDirectory, $"clip-{clip + 1}-{fraction:0.0}-source-target.png"),
                    $"P5R source / P5D {target.DanceId}, action {clip + 1}, t={fraction:0.0}");

                var recombined = SplitCharacterAnimationComposer.AddComponentTracks(
                    Clone(bodyReadback.Animations[clip]), faceReadback.Animations[clip], hairReadback.Animations[clip]);
                var splitPose = AnimationPoseEvaluator.Evaluate(combined.Model, recombined, time);
                PoseRender.Draw(combined.Model, targetPose, combined.Model, splitPose,
                    Path.Combine(renderDirectory, $"clip-{clip + 1}-{fraction:0.0}-combined-split.png"),
                    $"P5D {target.DanceId} combined / reloaded split, action {clip + 1}, t={fraction:0.0}");
            }
        }
    }

    private static IReadOnlyList<Animation> LoadReferenceAnimations(Model referenceModel)
    {
        var result = new List<Animation>();
        foreach (var action in Actions)
        {
            var path = ResolveActionPath("0004", action);
            var pack = Resource.Load<AnimationPack>(path);
            if (action.Index > pack.Animations.Count)
                throw new InvalidDataException($"Reference action {path} has only {pack.Animations.Count} clips");
            result.Add(PrepareActionAnimation(pack.Animations[action.Index - 1]));
        }
        return result;
    }

    private static void AppendSelectionReport(StringBuilder report, TargetSpec target, Model sourceModel,
        IReadOnlyList<Selection> selection)
    {
        report.AppendLine($"## P5D {target.DanceId}");
        report.AppendLine();
        report.AppendLine($"- P5R source rig: `{target.SourceId}` ({sourceModel.Nodes.Count()} nodes), " +
                          $"P5D body: `{target.BodyModel}.GMD`");
        var exactSourceCharacter = int.TryParse(target.DanceId, out var danceId) && danceId <= 209;
        report.AppendLine($"- Exact source character: {(exactSourceCharacter ? "yes" : "no; P5R 0010 surrogate used")}");
        report.AppendLine();
        report.AppendLine("| Action | Result | Source clip | Score / note |");
        report.AppendLine("|---:|---|---|---|");
        for (var i = 0; i < selection.Count; i++)
        {
            var item = selection[i];
            var result = item.Status switch
            {
                "exact" when exactSourceCharacter => "exact source clip",
                "exact" => "surrogate source clip",
                "similar" => "similar event fallback",
                _ => "not exact; surrogate",
            };
            var path = item.SourcePath == null ? "—" : $"`{Path.GetFileName(item.SourcePath)} #{item.SourceIndex}`";
            var score = float.IsFinite(item.Score) ? item.Score.ToString("F3") : "—";
            report.AppendLine($"| {i + 1} | {result} | {path} | {score}; {item.Note} |");
        }
        report.AppendLine();
    }

    private static string ResolveActionPath(string sourceId, ActionSpec action)
    {
        var stem = action.Kind == "battle"
            ? $"bb{sourceId}_051"
            : action.DisplayName.Replace("0004", sourceId, StringComparison.OrdinalIgnoreCase);
        var path = Path.Combine(P5rRoot, sourceId, action.Kind, stem + ".GAP");
        return File.Exists(path) ? path : null;
    }

    private static string ResolveNativeAnimationPath(TargetSpec target)
    {
        var directory = Path.Combine(P5dAnimationRoot, target.DanceId);
        var candidates = new[]
        {
            target.NativeAnimation + ".GAP",
            target.NativeAnimation + "_26.GAP",
            target.NativeAnimation + "_p.GAP",
            target.NativeAnimation + "_01.GAP",
        };
        foreach (var candidate in candidates)
        {
            var path = Path.Combine(directory, candidate);
            if (File.Exists(path))
                return path;
        }
        throw new FileNotFoundException($"No native Dance calibration pack for {target.DanceId}", directory);
    }

    private static Model LoadSourceModel(string sourceId)
    {
        var directory = Path.Combine(P5rRoot, sourceId);
        var candidates = new[]
        {
            $"c{sourceId}_107_00.GMD",
            $"c{sourceId}_051_00.GMD",
            $"c{sourceId}_061_00.GMD",
            $"c{sourceId}_001_00.GMD",
        };
        foreach (var candidate in candidates)
        {
            var path = Path.Combine(directory, candidate);
            if (File.Exists(path))
                return Resource.Load<ModelPack>(path).Model;
        }
        var fallback = Directory.EnumerateFiles(directory, $"c{sourceId}_*.GMD", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (fallback == null)
            throw new FileNotFoundException($"No P5R source model for {sourceId}", directory);
        return Resource.Load<ModelPack>(fallback).Model;
    }

    private static T Clone<T>(T resource) where T : Resource
    {
        using var stream = new MemoryStream();
        resource.Save(stream, true);
        stream.Position = 0;
        return Resource.Load<T>(stream, true);
    }

    private static Animation PrepareActionAnimation(Animation animation)
    {
        var result = Clone(animation);
        // The selected action is skeletal motion. Event packs may also carry
        // effect/morph metadata and optional EPL/bit-29 payloads whose nested
        // legacy versions are not portable when clips are assembled. Native
        // Dance component packs likewise contain only node motion here.
        result.Controllers = result.Controllers
            .Where(controller => controller.TargetKind == TargetKind.Node)
            .ToList();
        result.Field10 = null;
        result.Field14 = null;
        result.Field1C = null;
        result.BoundingBox = null;
        result.Properties = null;
        result.Flag10000000Data = null;
        return result;
    }

    private sealed record ActionSpec(string DisplayName, string Kind, int Index);
    private sealed record TargetSpec(string DanceId, string SourceId, string BodyModel, string FaceModel,
        string HairModel, string NativeAnimation);
    private sealed record Selection(Animation Animation, string Status, string SourcePath, int SourceIndex,
        float Score, string Note);
    private sealed record Candidate(Animation Animation, string Path, int Index, float Score, float RankScore, string Note);
    private sealed record PoseSample(Vector3 Position, Quaternion Rotation);
    private sealed record PoseSignature(float Duration, PoseSample[] Samples, int Controllers);
    private sealed record ReportRow(string DanceId, int Action, string Result, string SourcePath, int SourceIndex,
        float Score, string Note);
}
