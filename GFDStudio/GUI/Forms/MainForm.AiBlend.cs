using System;
using System.Diagnostics;
using System.Collections.Generic;
using Microsoft.Win32;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GFDLibrary;
using GFDLibrary.Animations;
using GFDStudio.AnimationMatching.Core;
using GFDStudio.AnimationMatching.Integration;
using GFDStudio.AnimationMatching.Stitching;

namespace GFDStudio.GUI.Forms;

public partial class MainForm
{
    // One app serves all blends. Cancellation discards the result but lets an in-flight
    // Cascadeur script finish before the next job touches its scene.
    private static readonly SemaphoreSlim AiBlendGate = new(1, 1);

    async Task<IAnimationClip> IGfdAnimationMatchingHost.GenerateAiBlendAsync(
        StitchedAnimation clip, float seconds, string styleHint, CancellationToken token)
    {
        var pack = GetAnimationMatchingTargetModelPack();
        if (pack?.Model == null) throw new InvalidOperationException("Load a model first.");
        var cascadeur = FindCascadeurDirectory();
        if (cascadeur == null) throw new InvalidOperationException("Cascadeur was not found. Set CASCADEUR_HOME to its installation folder.");
        var script = Path.Combine(AppContext.BaseDirectory, "app_data", "cascadeur", "cascadeur_transition.py");
        var bridge = Path.Combine(AppContext.BaseDirectory, "RetargetProbe.exe");
        if (!File.Exists(script) || !File.Exists(bridge)) throw new FileNotFoundException("The Cascadeur helper is missing; build GFD Studio with build-release.ps1.");
        await AiBlendGate.WaitAsync(token);
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "tmp", "ai_blends", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var modelFile = Path.Combine(directory, "model.GMD");
            var aFile = Path.Combine(directory, "a_input.GAP");
            var bFile = Path.Combine(directory, "b_input.GAP");
            var jobFile = Path.Combine(directory, "job.json");
            var resultFolder = Path.Combine(directory, "result");
            await Task.Run(() =>
            {
                // Export both sides after retargeting to the showroom's full skeleton,
                // so costume, finger and hair tracks travel through the helper too.
                var full = (StitchedAnimation)GfdAnimationClipBaker.CreateTargetPreviewClip(clip, pack.Model);
                var parts = full.CreateExportParts();
                // Keep animation-dependent data out of the model file used for rig caching.
                new ModelPack(pack.Version) { Model=pack.Model, Materials=pack.Materials, Textures=pack.Textures }.Save(modelFile);
                SaveAiInput(aFile, GfdAnimationClipBaker.Bake(parts.Source, pack.Model, pack.Version, token), pack.Version);
                SaveAiInput(bFile, GfdAnimationClipBaker.Bake(parts.Candidate, pack.Model, pack.Version, token), pack.Version);
                File.WriteAllText(jobFile, JsonSerializer.Serialize(new
                {
                    model = modelFile,
                    clipA = new { pack = aFile, index = 0 },
                    clipB = new { pack = bFile, index = 0 },
                    transitionFrames = Math.Max(1, (int)Math.Round(seconds * 30) - 1),
                    motion = string.IsNullOrWhiteSpace(styleHint) ? "Acrobatic" : styleHint,
                    output = resultFolder
                }));
            }, token);
            token.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo("python") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = AppContext.BaseDirectory };
            start.Environment["PYTHONPYCACHEPREFIX"] = Path.Combine(AppContext.BaseDirectory,"tmp","pycache");
            foreach (var argument in new[] { script, "run", jobFile, "--bridge", bridge, "--cascadeur", cascadeur })
                start.ArgumentList.Add(argument);
            using var helper = Process.Start(start) ?? throw new InvalidOperationException("Could not start Python.");
            var output = helper.StandardOutput.ReadToEndAsync();
            var error = helper.StandardError.ReadToEndAsync();
            await helper.WaitForExitAsync();
            var message = await error;
            await output;
            token.ThrowIfCancellationRequested();
            if (helper.ExitCode != 0) throw new InvalidOperationException(message.Trim());
            var result = Resource.Load<AnimationPack>(Path.Combine(resultFolder, "joined.GAP"));
            var pieces = Resource.Load<AnimationPack>(Path.Combine(resultFolder, "pieces.GAP"));
            return new AiBlendAnimationClip(clip.Id + ":ai", clip.DisplayName + " (AI blend)",
                pack.Model, result.Animations[0], pieces);
        }
        finally { AiBlendGate.Release(); }
    }

    private static void SaveAiInput(string file, Animation animation, uint version)
    {
        var pack = new AnimationPack(version);
        pack.Animations.Add(animation);
        pack.Save(file);
    }

    public static string FindCascadeurDirectory()
    {
        var candidates = new[] { Environment.GetEnvironmentVariable("CASCADEUR_HOME"),
            @"Q:\_coding\tools\Cascadeur", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Cascadeur"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cascadeur") };
        var found = candidates.FirstOrDefault(folder => !string.IsNullOrWhiteSpace(folder) && File.Exists(Path.Combine(folder, "cascadeur.exe")));
        if (found != null) return found;
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        foreach (var uninstall in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var entries = hive.OpenSubKey(uninstall);
            if (entries == null) continue;
            foreach (var name in entries.GetSubKeyNames())
            {
                using var entry = entries.OpenSubKey(name);
                if (!(entry?.GetValue("DisplayName") as string ?? "").Contains("Cascadeur", StringComparison.OrdinalIgnoreCase)) continue;
                var folder = entry.GetValue("InstallLocation") as string;
                if (!string.IsNullOrWhiteSpace(folder) && File.Exists(Path.Combine(folder,"cascadeur.exe"))) return folder;
            }
        }
        return null;
    }
}
