using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using System.Text.Json;

internal static partial class AnimationMatchAudit
{
    public static int PartialCache(string folder)
    {
        Directory.CreateDirectory(folder);
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, "GFDStudio.dll"));
        clipType = assembly.GetType("GFDStudio.AnimationMatching.Integration.GfdAnimationClip", true)!;
        var clipInterface = assembly.GetType("GFDStudio.AnimationMatching.Core.IAnimationClip", true)!;
        var corpusType = assembly.GetType("GFDStudio.AnimationMatching.Core.AnimationCorpus", true)!;
        var optionsType = assembly.GetType("GFDStudio.AnimationMatching.Core.AnimationMatchOptions", true)!;
        var databaseType = assembly.GetType("GFDStudio.AnimationMatching.Index.AnimationSearchDatabase", true)!;
        var cacheType = assembly.GetType("GFDStudio.AnimationMatching.Index.AnimationIndexCache", true)!;
        var matcherType = assembly.GetType("GFDStudio.AnimationMatching.Search.AnimationMatcher", true)!;
        var model = Synthetic(Quaternion.Identity, Quaternion.Identity, 1, false);
        object MakeClip(string id) => Clip(id, model.model, model.animation);
        object Corpus(string[] ids) {
            var clips = Array.CreateInstance(clipInterface, ids.Length);
            for (int i = 0; i < ids.Length; i++) clips.SetValue(MakeClip(ids[i]), i);
            return Activator.CreateInstance(corpusType, clips)!;
        }
        var saved = Corpus(new[] { "missing1", "keep1", "missing2", "keep2", "missing3", "keep3" });
        var options = Activator.CreateInstance(optionsType)!;
        var database = databaseType.GetMethod("Build")!.Invoke(null, new object[] { saved, options, null!, CancellationToken.None })!;
        var file = Path.Combine(folder, "fixture.bin");
        cacheType.GetMethod("Save")!.Invoke(null, new object[] { file, database, "fixture|root|old" });
        ((IDisposable)database).Dispose();
        string Hash() => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        var hash = Hash();
        var catalog = Corpus(new[] { "new1", "keep3", "new2", "keep1", "new3", "new4", "keep2", "new5" });
        object? Load(object clips, object opts, string signature) => cacheType.GetMethod("TryLoad")!.Invoke(null, new object[] { file, clips, opts, signature, null! });
        object Property(object db, string name) => databaseType.GetProperty(name)!.GetValue(db)!;
        var partial = Load(catalog, options, "fixture|root|changed") ?? throw new Exception("Partial index refused");
        if ((int)Property(partial, "MissingAnimationCount") != 3 || (int)Property(partial, "NewAnimationCount") != 5) throw new Exception("Wrong counts");
        var matcher = Activator.CreateInstance(matcherType, partial)!;
        var results = matcherType.GetMethods().Single(m => m.Name == "Search" && m.GetParameters().Length == 5)
            .Invoke(matcher, new object[] { MakeClip("query"), 15, 15, 10, CancellationToken.None })!;
        var ids = ((IEnumerable)results).Cast<object>().Select(x => (string)clipType.GetProperty("Id")!.GetValue(x.GetType().GetProperty("Candidate")!.GetValue(x))!).ToArray();
        if (ids.Length != 3 || ids.Any(id => !id.StartsWith("keep"))) throw new Exception("Missing or new clip leaked into results");
        var notice = (string)Property(partial, "IndexNotice");
        using (var control = (Control)Activator.CreateInstance(assembly.GetType("GFDStudio.AnimationMatching.UI.AnimationMatchingModeControl", true)!)!) {
            control.Size = new Size(1150, 650);
            control.GetType().GetMethod("SetStatus")!.Invoke(control, new object[] { notice + " · Showing 3 matches from saved index" });
            control.GetType().GetMethod("SetResults")!.Invoke(control, new[] { results });
            control.CreateControl(); control.PerformLayout();
            using var image = new Bitmap(control.Width, control.Height);
            control.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            image.Save(Path.Combine(folder, "partial-cache.png"), ImageFormat.Png);
        }
        ((IDisposable)partial).Dispose();
        using (var repeated = (IDisposable)(Load(catalog, options, "fixture|root|changed-again") ?? throw new Exception("Second load refused"))) { }
        using (var empty = (IDisposable)(Load(Corpus(new[] { "new1" }), options, "fixture|root|empty") ?? throw new Exception("All-missing cache refused"))) {
            var emptyMatcher = Activator.CreateInstance(matcherType, empty)!;
            var none = (IEnumerable)matcherType.GetMethods().Single(m => m.Name == "Search" && m.GetParameters().Length == 5)
                .Invoke(emptyMatcher, new object[] { MakeClip("query"), 15, 15, 10, CancellationToken.None })!;
            if (none.Cast<object>().Any()) throw new Exception("All-missing cache returned matches");
        }
        var incompatible = Activator.CreateInstance(optionsType)!;
        optionsType.GetProperty("ProjectionSeed")!.SetValue(incompatible, 123);
        if (Load(catalog, incompatible, "fixture|root|changed") != null) throw new Exception("Incompatible feature settings accepted");
        using (var unchanged = (IDisposable)(Load(saved, options, "fixture|root|old") ?? throw new Exception("Full cache refused"))) {
            if ((int)Property(unchanged, "MissingAnimationCount") != 0 || (int)Property(unchanged, "NewAnimationCount") != 0) throw new Exception("Full cache counts wrong");
        }
        if (hash != Hash()) throw new Exception("Loading rewrote the cache");
        File.WriteAllText(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(new { notice, results = ids, repeatedLoad = true, allMissing = true, incompatibleSettingsRejected = true, cacheUntouched = true }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
