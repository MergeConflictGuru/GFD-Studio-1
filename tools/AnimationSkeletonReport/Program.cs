using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GFDLibrary;
using GFDLibrary.Animations;
using GFDLibrary.Models;

namespace AnimationSkeletonReport;

internal static class Program
{
    private static readonly CanonicalJoint[] CanonicalJoints =
    {
        new("Root", "motionroot|root|rootnode", true),
        new("Pelvis", "hips", true),
        new("LowerSpine", "spine|spine1|spine2", true),
        new("UpperSpine", "spine2|spine1|spine", true),
        new("Neck", "neck", false),
        new("Head", "head", true),
        new("LeftShoulder", "leftshoulder", true),
        new("LeftElbow", "leftforearm", true),
        new("LeftHand", "lefthand", true),
        new("RightShoulder", "rightshoulder", true),
        new("RightElbow", "rightforearm", true),
        new("RightHand", "righthand", true),
        new("LeftHip", "leftupleg", true),
        new("LeftKnee", "leftleg", true),
        new("LeftFoot", "leftfoot", true),
        new("RightHip", "rightupleg", true),
        new("RightKnee", "rightleg", true),
        new("RightFoot", "rightfoot", true)
    };

    private static readonly HashSet<string> CoreRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "root", "motionroot", "rootnode", "hips", "spine", "spine1", "spine2", "neck", "head",
        "leftshoulder", "leftarm", "leftforearm", "lefthand", "leftupleg", "leftleg", "leftfoot",
        "rightshoulder", "rightarm", "rightforearm", "righthand", "rightupleg", "rightleg", "rightfoot"
    };

    private static readonly string[] ExtraKeywordGroups =
    {
        "hair", "skirt", "coat", "cloth", "tail", "weapon", "prop", "bike", "wheel", "vehicle",
        "accessory", "ribbon", "cape", "scarf", "bag", "umbrella", "guitar", "microphone"
    };

    private static readonly MethodInfo SkeletonRoleResolver = typeof(Resource).Assembly
        .GetType("GFDLibrary.Animations.AnimationRetargetMap", throwOnError: true)!
        .GetMethod("GetSkeletonRole", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static int Main(string[] args)
    {
        try
        {
            Console.SetOut(TextWriter.Null);
            var options = ParseArguments(args);
            var allModels = new List<ModelAudit>();
            var datasets = new List<DatasetAudit>();
            var totalFailures = 0;

            foreach (var dataset in options.Datasets)
            {
                Console.Error.WriteLine($"Discovering {dataset.Name}: {dataset.Root}");
                var paths = Directory.EnumerateFiles(dataset.Root, "*.GMD", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                Console.Error.WriteLine($"  discovered {paths.Length:N0} GMD files");

                var datasetModels = new List<ModelAudit>(paths.Length);
                for (var i = 0; i < paths.Length; i++)
                {
                    if (i == 0 || (i + 1) % 100 == 0 || i + 1 == paths.Length)
                        Console.Error.WriteLine($"  loading {i + 1:N0}/{paths.Length:N0}");

                    try
                    {
                        var pack = Resource.Load<ModelPack>(paths[i]);
                        if (pack?.Model == null)
                            throw new InvalidDataException("GMD did not contain a model chunk.");

                        datasetModels.Add(AuditModel(dataset.Name, dataset.Root, paths[i], pack));
                    }
                    catch (Exception ex)
                    {
                        totalFailures++;
                        datasetModels.Add(new ModelAudit
                        {
                            Dataset = dataset.Name,
                            RelativePath = SafeRelativePath(dataset.Root, paths[i]),
                            FullPath = paths[i],
                            FileName = Path.GetFileName(paths[i]),
                            FileKind = ClassifyFile(paths[i]),
                            LoadError = ex.GetType().Name + ": " + ex.Message
                        });
                    }
                }

                allModels.AddRange(datasetModels);
                datasets.Add(BuildDatasetAudit(dataset, paths.Length, datasetModels));
            }

            var report = BuildReport(options, datasets, allModels, totalFailures);
            WriteReports(options, report);
            Console.Error.WriteLine($"Wrote {options.MarkdownPath}");
            Console.Error.WriteLine($"Wrote {options.JsonPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static ScanOptions ParseArguments(string[] args)
    {
        var datasets = new List<DatasetInput>();
        string? jsonPath = null;
        string? markdownPath = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--dataset":
                    if (i + 2 >= args.Length)
                        throw new ArgumentException("--dataset requires a name and root path.");
                    datasets.Add(new DatasetInput(args[++i], Path.GetFullPath(args[++i])));
                    break;
                case "--json":
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("--json requires a path.");
                    jsonPath = Path.GetFullPath(args[++i]);
                    break;
                case "--markdown":
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("--markdown requires a path.");
                    markdownPath = Path.GetFullPath(args[++i]);
                    break;
                default:
                    throw new ArgumentException("Unknown argument: " + args[i]);
            }
        }

        if (datasets.Count == 0)
            throw new ArgumentException("At least one --dataset name root pair is required.");
        foreach (var dataset in datasets)
            if (!Directory.Exists(dataset.Root))
                throw new DirectoryNotFoundException(dataset.Root);

        return new ScanOptions(
            datasets,
            jsonPath ?? Path.Combine(Environment.CurrentDirectory, "animatch-skeleton-report.json"),
            markdownPath ?? Path.Combine(Environment.CurrentDirectory, "animatch-skeleton-report.md"));
    }

    private static ModelAudit AuditModel(string dataset, string root, string path, ModelPack pack)
    {
        var model = pack.Model!;
        var nodes = model.Nodes.ToList();
        var nodeIndices = new Dictionary<Node, int>();
        for (var i = 0; i < nodes.Count; i++)
            nodeIndices[nodes[i]] = i;

        var roleNodes = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < nodes.Count; i++)
        {
            var role = GetSkeletonRole(nodes[i].Name);
            if (role == null)
                continue;
            if (!roleNodes.TryGetValue(role, out var indices))
                roleNodes[role] = indices = new List<int>();
            indices.Add(i);
        }

        var selected = ResolveCanonicalNodes(nodes, roleNodes);
        var requiredMissing = CanonicalJoints
            .Where(joint => joint.Required && selected[joint.Name] == null)
            .Select(joint => joint.Name)
            .ToArray();
        var resolvedRequiredCount = CanonicalJoints.Count(joint => joint.Required && selected[joint.Name] != null);
        var recognizedCoreCount = roleNodes
            .Where(pair => CoreRoles.Contains(pair.Key))
            .Sum(pair => pair.Value.Count);
        var meshCount = nodes.Sum(node => node.Meshes.Count());
        var skinnedBoneNames = model.Bones == null
            ? Array.Empty<string>()
            : model.Bones
                .Select(bone => bone.NodeIndex < nodes.Count ? nodes[bone.NodeIndex].Name : "<invalid:" + bone.NodeIndex + ">")
                .ToArray();
        var extraExamples = nodes
            .Where(node => !IsSelectedNode(node, selected) && IsLikelyExtra(node.Name))
            .Select(node => node.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToArray();
        var topology = BuildTopologySignature(nodes, nodeIndices, selected);
        var likelyHumanoid = requiredMissing.Length == 0 &&
            selected.Values.Count(value => value != null) >= CanonicalJoints.Count(joint => joint.Required) &&
            meshCount > 0;

        return new ModelAudit
        {
            Dataset = dataset,
            RelativePath = SafeRelativePath(root, path),
            FullPath = path,
            FileName = Path.GetFileName(path),
            FileKind = ClassifyFile(path),
            Version = $"0x{pack.Version:X8}",
            NodeCount = nodes.Count,
            SkinnedBoneCount = model.Bones?.Count ?? 0,
            MeshCount = meshCount,
            RootNodeName = model.RootNode?.Name,
            LikelyHumanoid = likelyHumanoid,
            RequiredCanonicalCoverage = $"{resolvedRequiredCount}/{CanonicalJoints.Count(joint => joint.Required)}",
            MissingCanonicalJoints = requiredMissing,
            RecognizedCoreRoleCount = recognizedCoreCount,
            SemanticRoles = roleNodes.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Select(index => nodes[index].Name).ToArray(),
                StringComparer.OrdinalIgnoreCase),
            CanonicalJoints = selected.ToDictionary(
                pair => pair.Key,
                pair => pair.Value == null ? null : new CanonicalNodeAudit
                {
                    NodeIndex = pair.Value.Index,
                    Name = pair.Value.Node.Name,
                    ParentName = pair.Value.Node.Parent?.Name,
                    SemanticRole = pair.Value.Role,
                    WorldPosition = ToArray(pair.Value.Node.WorldTransform.Translation)
                },
                StringComparer.Ordinal),
            TopologySignature = topology,
            ExtraBoneExamples = extraExamples,
            UnusualRigReasons = FindUnusualRigReasons(nodes, roleNodes, requiredMissing, meshCount),
            Nodes = nodes.Select((node, index) => ToNodeAudit(node, index, nodeIndices)).ToArray(),
            SkinBones = model.Bones == null
                ? Array.Empty<SkinBoneAudit>()
                : model.Bones.Select((bone, index) => ToSkinBoneAudit(bone, index, nodes)).ToArray()
        };
    }

    private static Dictionary<string, ResolvedNode?> ResolveCanonicalNodes(
        IReadOnlyList<Node> nodes,
        IReadOnlyDictionary<string, List<int>> roleNodes)
    {
        var result = new Dictionary<string, ResolvedNode?>(StringComparer.Ordinal);
        var used = new HashSet<int>();
        foreach (var joint in CanonicalJoints)
        {
            ResolvedNode? resolved = null;
            foreach (var role in joint.Roles.Split('|'))
            {
                if (!roleNodes.TryGetValue(role, out var candidates))
                    continue;
                var candidate = candidates
                    .Where(index => !used.Contains(index))
                    .OrderBy(index => nodes[index].Parent == null ? 0 : 1)
                    .ThenBy(index => GetDepth(nodes[index]))
                    .FirstOrDefault(-1);
                if (candidate >= 0)
                {
                    resolved = new ResolvedNode(candidate, nodes[candidate], role);
                    break;
                }
            }

            // Lower/upper spine are a semantic two-slot reduction. If a rig has
            // one torso bone, use it in both slots; the feature builder can later
            // decide whether duplicated slots should be omitted.
            if (resolved == null && (joint.Name == "LowerSpine" || joint.Name == "UpperSpine"))
            {
                var otherName = joint.Name == "LowerSpine" ? "UpperSpine" : "LowerSpine";
                if (result.TryGetValue(otherName, out var other))
                    resolved = other;
            }

            result[joint.Name] = resolved;
            if (resolved != null)
                used.Add(resolved.Index);
        }
        return result;
    }

    private static string BuildTopologySignature(
        IReadOnlyList<Node> nodes,
        IReadOnlyDictionary<Node, int> nodeIndices,
        IReadOnlyDictionary<string, ResolvedNode?> selected)
    {
        var canonicalByIndex = selected
            .Where(pair => pair.Value != null)
            .ToDictionary(pair => pair.Value!.Index, pair => pair.Key);
        var parts = new List<string>();
        foreach (var joint in CanonicalJoints)
        {
            if (!selected.TryGetValue(joint.Name, out var resolved) || resolved == null)
            {
                parts.Add(joint.Name + "=-");
                continue;
            }

            var parent = resolved.Node.Parent;
            string? semanticParent = null;
            while (parent != null && nodeIndices.TryGetValue(parent, out var parentIndex))
            {
                if (canonicalByIndex.TryGetValue(parentIndex, out semanticParent))
                    break;
                parent = parent.Parent;
            }
            parts.Add(joint.Name + "=" + resolved.Role + "<" + (semanticParent ?? "other") + ">");
        }
        return string.Join(";", parts);
    }

    private static bool IsSelectedNode(Node node, IReadOnlyDictionary<string, ResolvedNode?> selected)
        => selected.Values.Any(value => value?.Node == node);

    private static bool IsLikelyExtra(string name)
    {
        var lower = name.ToLowerInvariant();
        return ExtraKeywordGroups.Any(lower.Contains) || lower is "rot" or "mesh_grp" or "meshgrp" or "dummy" or "dummy_root" ||
            lower.StartsWith("helper", StringComparison.Ordinal) || lower.StartsWith("ik", StringComparison.Ordinal);
    }

    private static string[] FindUnusualRigReasons(
        IReadOnlyList<Node> nodes,
        IReadOnlyDictionary<string, List<int>> roleNodes,
        IReadOnlyList<string> missing,
        int meshCount)
    {
        var reasons = new List<string>();
        if (meshCount == 0)
            reasons.Add("no mesh attachments");
        if (missing.Count > 0)
            reasons.Add("missing required canonical joints: " + string.Join(", ", missing));
        var names = string.Join(" ", nodes.Select(node => node.Name));
        if (Regex.IsMatch(names, @"(^|[^a-z])(wheel|bike|car|vehicle|motorcycle)([^a-z]|$)", RegexOptions.IgnoreCase))
            reasons.Add("vehicle-like node naming detected");
        if (new[] { "weapon", "sword", "gun", "shield", "prop" }.Any(names.Contains) && !roleNodes.ContainsKey("hips"))
            reasons.Add("prop or weapon rig without humanoid pelvis");
        if (roleNodes.Count(pair => pair.Key.EndsWith("foot", StringComparison.OrdinalIgnoreCase)) < 2)
            reasons.Add("fewer than two semantic feet");
        return reasons.ToArray();
    }

    private static NodeAudit ToNodeAudit(
        Node node,
        int index,
        IReadOnlyDictionary<Node, int> nodeIndices)
    {
        var role = GetSkeletonRole(node.Name);
        return new NodeAudit
        {
            Index = index,
            Name = node.Name,
            ParentIndex = node.Parent != null && nodeIndices.TryGetValue(node.Parent, out var parentIndex) ? parentIndex : -1,
            ParentName = node.Parent?.Name,
            Depth = GetDepth(node),
            SemanticRole = role,
            IsCoreSemantic = role != null && CoreRoles.Contains(role),
            IsLikelyCosmeticOrHelper = role == null && IsLikelyExtra(node.Name),
            Translation = ToArray(node.Translation),
            Rotation = ToArray(node.Rotation),
            Scale = ToArray(node.Scale),
            WorldPosition = ToArray(node.WorldTransform.Translation)
        };
    }

    private static SkinBoneAudit ToSkinBoneAudit(Bone bone, int index, IReadOnlyList<Node> nodes)
        => new()
        {
            Index = index,
            NodeIndex = bone.NodeIndex,
            NodeName = bone.NodeIndex < nodes.Count ? nodes[bone.NodeIndex].Name : null,
            InverseBindMatrix = ToArray(bone.InverseBindMatrix)
        };

    private static int GetDepth(Node node)
    {
        var depth = 0;
        while (node.Parent != null && depth < 1000)
        {
            depth++;
            node = node.Parent;
        }
        return depth;
    }

    private static DatasetAudit BuildDatasetAudit(
        DatasetInput input,
        int discovered,
        IReadOnlyList<ModelAudit> models)
    {
        var loaded = models.Where(model => model.LoadError == null).ToArray();
        return new DatasetAudit
        {
            Name = input.Name,
            Root = input.Root,
            DiscoveredFiles = discovered,
            LoadedModels = loaded.Length,
            LoadFailures = models.Count(model => model.LoadError != null),
            HumanoidModels = loaded.Count(model => model.LikelyHumanoid),
            CanonicalJointCoverage = CanonicalJoints.ToDictionary(
                joint => joint.Name,
                joint => loaded.Count(model => model.CanonicalJoints[joint.Name] != null),
                StringComparer.Ordinal),
            MissingImportantJointModels = loaded
                .Where(model => model.MissingCanonicalJoints.Length > 0)
                .Select(model => model.RelativePath)
                .ToArray(),
            TopologyFamilies = BuildFamilies(loaded)
        };
    }

    private static List<FamilyAudit> BuildFamilies(IEnumerable<ModelAudit> models)
    {
        return models
            .GroupBy(model => model.TopologySignature, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select((group, index) => new FamilyAudit
            {
                FamilyId = "F" + (index + 1).ToString("D3", CultureInfo.InvariantCulture),
                Signature = group.Key,
                ModelCount = group.Count(),
                HumanoidModelCount = group.Count(model => model.LikelyHumanoid),
                DatasetCounts = group.GroupBy(model => model.Dataset)
                    .ToDictionary(dataset => dataset.Key, dataset => dataset.Count(), StringComparer.OrdinalIgnoreCase),
                RepresentativeModels = group.Take(8).Select(model => model.RelativePath).ToArray(),
                MissingJointCounts = CanonicalJoints.ToDictionary(
                    joint => joint.Name,
                    joint => group.Count(model => model.CanonicalJoints[joint.Name] == null),
                    StringComparer.Ordinal)
            })
            .ToList();
    }

    private static Report BuildReport(
        ScanOptions options,
        IReadOnlyList<DatasetAudit> datasets,
        IReadOnlyList<ModelAudit> models,
        int totalFailures)
    {
        var loaded = models.Where(model => model.LoadError == null).ToArray();
        var allFamilies = BuildFamilies(loaded);
        return new Report
        {
            SchemaVersion = 1,
            GeneratedUtc = DateTime.UtcNow,
            ScanMethod = "Directory.EnumerateFiles(root, \"*.GMD\", SearchOption.AllDirectories), matching Character Browser discovery",
            SemanticResolver = "GFDLibrary.Animations.AnimationRetargetMap.GetSkeletonRole",
            CanonicalJoints = CanonicalJoints.Select(joint => new CanonicalJointReport
            {
                Name = joint.Name,
                ExistingRetargetRoles = joint.Roles.Split('|'),
                RequiredForHumanoidClassification = joint.Required
            }).ToArray(),
            Totals = new TotalsAudit
            {
                Datasets = datasets.Count,
                DiscoveredFiles = datasets.Sum(dataset => dataset.DiscoveredFiles),
                LoadedModels = loaded.Length,
                LoadFailures = totalFailures,
                HumanoidModels = loaded.Count(model => model.LikelyHumanoid),
                FamilyCount = allFamilies.Count
            },
            Datasets = datasets.ToArray(),
            Families = allFamilies,
            Models = models.ToArray()
        };
    }

    private static void WriteReports(ScanOptions options, Report report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.JsonPath))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.MarkdownPath))!);
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        File.WriteAllText(options.JsonPath, JsonSerializer.Serialize(report, jsonOptions), Encoding.UTF8);
        File.WriteAllText(options.MarkdownPath, RenderMarkdown(report), Encoding.UTF8);
    }

    private static string RenderMarkdown(Report report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# AniMatch skeleton audit");
        sb.AppendLine();
        sb.AppendLine($"Generated: {report.GeneratedUtc:O}");
        sb.AppendLine();
        sb.AppendLine("## Executive summary");
        sb.AppendLine();
        sb.AppendLine($"The scan discovered **{report.Totals.DiscoveredFiles:N0}** .GMD files, loaded **{report.Totals.LoadedModels:N0}** model skeletons, and had **{report.Totals.LoadFailures:N0}** load failures. **{report.Totals.HumanoidModels:N0}** loaded models meet the report's humanoid threshold. The data forms **{report.Totals.FamilyCount:N0}** semantic topology families.");
        sb.AppendLine();
        sb.AppendLine("This scanner uses the existing AnimationRetargetMap resolver, so Bip01/P5-style names, P5 Hips names, and P5D naming aliases are analyzed through the same semantic knowledge used by runtime retargeting. Cosmetic and helper nodes do not enter the family signature.");
        sb.AppendLine();
        sb.AppendLine("## Datasets");
        sb.AppendLine();
        sb.AppendLine("| Dataset | Root | Discovered | Loaded | Failures | Humanoid |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|");
        foreach (var dataset in report.Datasets)
            sb.AppendLine($"| {dataset.Name} | {dataset.Root} | {dataset.DiscoveredFiles:N0} | {dataset.LoadedModels:N0} | {dataset.LoadFailures:N0} | {dataset.HumanoidModels:N0} |");
        sb.AppendLine();

        sb.AppendLine("## Canonical mapping and coverage");
        sb.AppendLine();
        sb.AppendLine("The proposed canonical representation keeps one motion root, pelvis, two torso slots, an optional neck, head, and the 12 major limb joints. LeftElbow/RightElbow intentionally map to the existing semantic forearm roles. A single-spine rig may resolve the same source node for both torso slots and should be marked as a lower-fidelity family rather than rejected.");
        sb.AppendLine();
        sb.AppendLine("| Canonical joint | Existing retarget roles | Required | Loaded-model coverage |");
        sb.AppendLine("|---|---|:---:|---:|");
        foreach (var joint in report.CanonicalJoints)
        {
            var coverage = report.Datasets.Sum(dataset => dataset.CanonicalJointCoverage[joint.Name]);
            sb.AppendLine($"| {joint.Name} | {string.Join(", ", joint.ExistingRetargetRoles)} | {(joint.RequiredForHumanoidClassification ? "yes" : "optional")} | {coverage:N0}/{report.Totals.LoadedModels:N0} ({Percent(coverage, report.Totals.LoadedModels):0.0}%) |");
        }
        sb.AppendLine();

        sb.AppendLine("## Major semantic topology families");
        sb.AppendLine();
        sb.AppendLine("Families are grouped by canonical-joint presence and nearest canonical ancestry, not by exact complete node lists. Hair, cloth, tails, weapons, and other extra leaves therefore do not split otherwise equivalent body rigs.");
        sb.AppendLine();
        sb.AppendLine("| Family | Models | Humanoid | Dataset split | Representative files |");
        sb.AppendLine("|---|---:|---:|---|---|");
        foreach (var family in report.Families.Take(25))
        {
            var split = string.Join(", ", family.DatasetCounts.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}: {pair.Value:N0}"));
            sb.AppendLine($"| {family.FamilyId} | {family.ModelCount:N0} | {family.HumanoidModelCount:N0} | {split} | {string.Join("; ", family.RepresentativeModels.Take(3))} |");
        }
        if (report.Families.Count > 25)
            sb.AppendLine($"\nOnly the 25 largest families are shown here; all {report.Families.Count:N0} families are in JSON.");
        sb.AppendLine();

        sb.AppendLine("## P5 vs P5D observations");
        sb.AppendLine();
        foreach (var dataset in report.Datasets)
        {
            sb.AppendLine($"### {dataset.Name}");
            sb.AppendLine();
            sb.AppendLine($"- {dataset.LoadedModels:N0} models loaded; {dataset.HumanoidModels:N0} pass the humanoid threshold.");
            sb.AppendLine($"- Most-covered joints: {string.Join(", ", dataset.CanonicalJointCoverage.OrderByDescending(pair => pair.Value).Take(5).Select(pair => $"{pair.Key} ({pair.Value:N0})"))}.");
            var missing = dataset.CanonicalJointCoverage.OrderBy(pair => pair.Value).Take(5).ToArray();
            sb.AppendLine($"- Lowest-covered joints: {string.Join(", ", missing.Select(pair => $"{pair.Key} ({pair.Value:N0})"))}.");
            sb.AppendLine($"- Semantic families: {dataset.TopologyFamilies.Count:N0}.");
            sb.AppendLine();
        }
        sb.AppendLine("P5/P5D are not compared by raw names: P5 commonly has a coordinate/helper root followed by Bip01 as the motion root, while P5D commonly exposes root as the motion root under RootNode. The shared semantic roles and the explicit motion-root distinction are precisely the compatibility boundary the canonical index needs.");
        sb.AppendLine();

        sb.AppendLine("## Outliers and safely ignored extras");
        sb.AppendLine();
        var outliers = report.Models.Where(model => model.LoadError != null || model.UnusualRigReasons.Length > 0 || !model.LikelyHumanoid).ToArray();
        sb.AppendLine($"There are **{outliers.Length:N0}** load failures or non-standard/low-coverage models. The full list, reasons, and node data are in JSON. Representative outliers:");
        sb.AppendLine();
        foreach (var model in outliers.Take(30))
            sb.AppendLine($"- {model.Dataset}/{model.RelativePath} — {(model.LoadError ?? string.Join("; ", model.UnusualRigReasons))}");
        sb.AppendLine();
        var extras = report.Models.SelectMany(model => model.ExtraBoneExamples).GroupBy(name => name, StringComparer.OrdinalIgnoreCase).OrderByDescending(group => group.Count()).Take(30).Select(group => $"{group.First()} ({group.Count():N0} models)");
        sb.AppendLine("Examples of node classes that can safely be excluded from body-continuation features: " + string.Join(", ", extras) + ".");
        sb.AppendLine();

        sb.AppendLine("## Recommendation");
        sb.AppendLine();
        var humanoidPercent = Percent(report.Totals.HumanoidModels, report.Totals.LoadedModels);
        sb.AppendLine($"A global semantic pose index is recommended for the **{report.Totals.HumanoidModels:N0}/{report.Totals.LoadedModels:N0} ({humanoidPercent:0.0}%)** models passing this audit. Build features from the original animation plus its source skeleton mapped into the canonical slots above; do not include the selected showroom body/face/hair model in the expensive cache identity. Keep low-coverage, non-humanoid, and failed models out of the global body index, but retain them as preview/export assets when the target rig is genuinely needed.");
        sb.AppendLine();
        sb.AppendLine("The implementation should version the canonical mapping and feature schema in the persistent cache fingerprint, keep per-animation/shard source-skeleton features reusable, sample coarsely for ANN, locally refine shortlisted frames at full resolution, and retarget only after candidate selection for preview/export.");
        sb.AppendLine();
        sb.AppendLine("## Machine-readable detail");
        sb.AppendLine();
        sb.AppendLine("animatch-skeleton-report.json contains every discovered path, load result, node name/parent hierarchy, local bind PRS, world position, skin-bone inverse bind matrix, existing semantic roles, canonical resolution, topology signature, and outlier reasons.");
        return sb.ToString();
    }

    private static string SafeRelativePath(string root, string path)
    {
        try { return Path.GetRelativePath(root, path); }
        catch { return path; }
    }

    private static string ClassifyFile(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        if (Regex.IsMatch(directory, @"(?:^|[\\/])face(?:[\\/]|$)", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(stem, @"(?:^|[_-])f\d+$", RegexOptions.IgnoreCase))
            return "face";
        if (Regex.IsMatch(directory, @"(?:^|[\\/])hair(?:[\\/]|$)", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(stem, @"(?:^|[_-])h\d+$", RegexOptions.IgnoreCase))
            return "hair";
        if (stem.StartsWith("pc", StringComparison.OrdinalIgnoreCase))
            return "p5d-dance-player";
        return "body-or-other";
    }

    private static float Percent(int count, int total) => total == 0 ? 0 : 100f * count / total;

    private static string? GetSkeletonRole(string name)
        => (string?)SkeletonRoleResolver.Invoke(null, new object?[] { name });

    private static float[] ToArray(Vector3 value) => new[] { value.X, value.Y, value.Z };
    private static float[] ToArray(Quaternion value) => new[] { value.X, value.Y, value.Z, value.W };
    private static float[] ToArray(Matrix4x4 value) => new[]
    {
        value.M11, value.M12, value.M13, value.M14,
        value.M21, value.M22, value.M23, value.M24,
        value.M31, value.M32, value.M33, value.M34,
        value.M41, value.M42, value.M43, value.M44
    };

    private sealed record DatasetInput(string Name, string Root);
    private sealed record ScanOptions(IReadOnlyList<DatasetInput> Datasets, string JsonPath, string MarkdownPath);
    private sealed record CanonicalJoint(string Name, string Roles, bool Required);
    private sealed record ResolvedNode(int Index, Node Node, string Role);

    private sealed class Report
    {
        public int SchemaVersion { get; init; }
        public DateTime GeneratedUtc { get; init; }
        public string ScanMethod { get; init; } = string.Empty;
        public string SemanticResolver { get; init; } = string.Empty;
        public CanonicalJointReport[] CanonicalJoints { get; init; } = Array.Empty<CanonicalJointReport>();
        public TotalsAudit Totals { get; init; } = new();
        public DatasetAudit[] Datasets { get; init; } = Array.Empty<DatasetAudit>();
        public List<FamilyAudit> Families { get; init; } = new();
        public ModelAudit[] Models { get; init; } = Array.Empty<ModelAudit>();
    }

    private sealed class CanonicalJointReport
    {
        public string Name { get; init; } = string.Empty;
        public string[] ExistingRetargetRoles { get; init; } = Array.Empty<string>();
        public bool RequiredForHumanoidClassification { get; init; }
    }

    private sealed class TotalsAudit
    {
        public int Datasets { get; init; }
        public int DiscoveredFiles { get; init; }
        public int LoadedModels { get; init; }
        public int LoadFailures { get; init; }
        public int HumanoidModels { get; init; }
        public int FamilyCount { get; init; }
    }

    private sealed class DatasetAudit
    {
        public string Name { get; init; } = string.Empty;
        public string Root { get; init; } = string.Empty;
        public int DiscoveredFiles { get; init; }
        public int LoadedModels { get; init; }
        public int LoadFailures { get; init; }
        public int HumanoidModels { get; init; }
        public Dictionary<string, int> CanonicalJointCoverage { get; init; } = new(StringComparer.Ordinal);
        public string[] MissingImportantJointModels { get; init; } = Array.Empty<string>();
        public List<FamilyAudit> TopologyFamilies { get; init; } = new();
    }

    private sealed class FamilyAudit
    {
        public string FamilyId { get; init; } = string.Empty;
        public string Signature { get; init; } = string.Empty;
        public int ModelCount { get; init; }
        public int HumanoidModelCount { get; init; }
        public Dictionary<string, int> DatasetCounts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public string[] RepresentativeModels { get; init; } = Array.Empty<string>();
        public Dictionary<string, int> MissingJointCounts { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed class ModelAudit
    {
        public string Dataset { get; init; } = string.Empty;
        public string RelativePath { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public string FileKind { get; init; } = string.Empty;
        public string? Version { get; init; }
        public int NodeCount { get; init; }
        public int SkinnedBoneCount { get; init; }
        public int MeshCount { get; init; }
        public string? RootNodeName { get; init; }
        public bool LikelyHumanoid { get; init; }
        public string RequiredCanonicalCoverage { get; init; } = string.Empty;
        public string[] MissingCanonicalJoints { get; init; } = Array.Empty<string>();
        public int RecognizedCoreRoleCount { get; init; }
        public Dictionary<string, string[]> SemanticRoles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, CanonicalNodeAudit?> CanonicalJoints { get; init; } = new(StringComparer.Ordinal);
        public string TopologySignature { get; init; } = string.Empty;
        public string[] ExtraBoneExamples { get; init; } = Array.Empty<string>();
        public string[] UnusualRigReasons { get; init; } = Array.Empty<string>();
        public string? LoadError { get; init; }
        public NodeAudit[] Nodes { get; init; } = Array.Empty<NodeAudit>();
        public SkinBoneAudit[] SkinBones { get; init; } = Array.Empty<SkinBoneAudit>();
    }

    private sealed class CanonicalNodeAudit
    {
        public int NodeIndex { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? ParentName { get; init; }
        public string SemanticRole { get; init; } = string.Empty;
        public float[] WorldPosition { get; init; } = Array.Empty<float>();
    }

    private sealed class NodeAudit
    {
        public int Index { get; init; }
        public string Name { get; init; } = string.Empty;
        public int ParentIndex { get; init; }
        public string? ParentName { get; init; }
        public int Depth { get; init; }
        public string? SemanticRole { get; init; }
        public bool IsCoreSemantic { get; init; }
        public bool IsLikelyCosmeticOrHelper { get; init; }
        public float[] Translation { get; init; } = Array.Empty<float>();
        public float[] Rotation { get; init; } = Array.Empty<float>();
        public float[] Scale { get; init; } = Array.Empty<float>();
        public float[] WorldPosition { get; init; } = Array.Empty<float>();
    }

    private sealed class SkinBoneAudit
    {
        public int Index { get; init; }
        public ushort NodeIndex { get; init; }
        public string? NodeName { get; init; }
        public float[] InverseBindMatrix { get; init; } = Array.Empty<float>();
    }
}
