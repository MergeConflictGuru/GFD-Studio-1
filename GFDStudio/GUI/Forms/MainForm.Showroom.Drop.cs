using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using GFDLibrary;
using GFDLibrary.Animations;
using GFDStudio.AnimationMatching.Integration;
using GFDStudio.GUI.Controls;

namespace GFDStudio.GUI.Forms;

public partial class MainForm
{
    private readonly ConditionalWeakTable<Control, object> mShowroomDropControls = new();
    private readonly Dictionary<string, CharacterAnimationEntry[]> mDroppedAnimationPacks = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> mPinnedAnimations = new(StringComparer.OrdinalIgnoreCase);
    private static string AnimationPinKey(CharacterAnimationEntry entry)
        => Path.GetFullPath(entry.PackPath) + "|" + entry.Kind + "|" + entry.Index;
    private bool IsPinnedAnimation(CharacterAnimationEntry entry) => mPinnedAnimations.Contains(AnimationPinKey(entry));

    private void PinCharacterAnimation(object sender, MouseEventArgs e)
    {
        if (sender is not ListBox list) return;
        int index=list.IndexFromPoint(e.Location);
        if (index<0 || list.Items[index] is not CharacterAnimationEntry entry) return;
        mPinnedAnimations.Add(AnimationPinKey(entry));
    }

    private void RemoveSelectedDroppedAnimations(ListBox list)
    {
        var selected=list.SelectedItems.Cast<object>().OfType<CharacterAnimationEntry>()
            .Where(entry=>entry.IsDroppedForSession).ToArray();
        if(selected.Length==0)return;
        foreach(var entry in selected)
        {
            var file=Path.GetFullPath(entry.PackPath);
            if(!mDroppedAnimationPacks.TryGetValue(file,out var entries))continue;
            var kept=entries.Where(item=>AnimationPinKey(item)!=AnimationPinKey(entry)).ToArray();
            if(kept.Length==0)mDroppedAnimationPacks.Remove(file);
            else mDroppedAnimationPacks[file]=kept;
            mPinnedAnimations.Remove(AnimationPinKey(entry));
            mAnimationPickOrder.Remove(GetCorrectedAnimationMatchClipId(entry));
        }
        if(IsPairedShowroom)HidePairedShowroom();
        RefreshCharacterAnimationList();
        RefreshCharacterBlendAnimationList();
    }

    private async Task RegisterDroppedAnimationPackAsync(string file, CancellationToken token)
    {
        string fullName = Path.GetFullPath(file);
        if (!mDroppedAnimationPacks.ContainsKey(fullName))
        {
            var entries = await Task.Run(() =>
            {
                var pack = Resource.Load<AnimationPack>(fullName);
                var result = new List<CharacterAnimationEntry>();
                void Add(IReadOnlyList<Animation> animations, CharacterAnimationListKind kind, string label)
                {
                    if (animations == null) return;
                    for (int i = 0; i < animations.Count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        if (animations[i] == null) continue;
                        result.Add(new CharacterAnimationEntry {
                            PackPath = fullName, Kind = kind, Index = i, IsDroppedForSession = true,
                            DisplayName = Path.GetFileName(fullName) + label + $" #{i + 1} (dropped)"
                        });
                    }
                }
                Add(pack.Animations, CharacterAnimationListKind.Animation, "");
                Add(pack.BlendAnimations, CharacterAnimationListKind.BlendAnimation, " [blend]");
                Add(pack.METAPHOR_AnimArray3, CharacterAnimationListKind.ExtraAnimation, " [extra]");
                return result.ToArray();
            }, token);
            token.ThrowIfCancellationRequested();
            if (IsDisposed) return;
            mDroppedAnimationPacks.TryAdd(fullName, entries);
        }
        RefreshCharacterAnimationList();
        RefreshCharacterBlendAnimationList();
    }

    private void SelectDroppedAnimationPack(string file)
    {
        var entries = mDroppedAnimationPacks[Path.GetFullPath(file)];
        var entry = entries.FirstOrDefault(item => item.Kind == CharacterAnimationListKind.Animation);
        if (entry == null || mCharacterAnimationListBox == null) return;
        bool restoring = mCharacterBrowserRestoringSelection;
        mCharacterBrowserRestoringSelection = true;
        try
        {
            int index = mCharacterAnimationListBox.Items.IndexOf(entry);
            if (index >= 0)
            {
                mCharacterAnimationListBox.SetSelected(index, true);
                mCharacterAnimationListBox.TopIndex = index;
            }
        }
        finally { mCharacterBrowserRestoringSelection = restoring; }
    }

    private void EnableShowroomGapDrop(Control control)
    {
        if (mShowroomDropControls.TryGetValue(control, out _)) return;
        mShowroomDropControls.Add(control, new object());
        control.AllowDrop = true;
        control.DragEnter += HandleShowroomGapDragEnter;
        control.DragOver += HandleShowroomGapDragEnter;
        control.DragDrop += HandleShowroomGapDrop;
        control.ControlAdded += (_, e) => EnableShowroomGapDrop(e.Control);
        foreach (Control child in control.Controls) EnableShowroomGapDrop(child);
    }

    private static string GetDroppedGap(IDataObject data)
        => data?.GetData(DataFormats.FileDrop) is string[] files
            ? files.FirstOrDefault(file => File.Exists(file) &&
                string.Equals(Path.GetExtension(file), ".GAP", StringComparison.OrdinalIgnoreCase))
            : null;

    private void HandleShowroomGapDragEnter(object sender, DragEventArgs e)
    {
        var supported = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effect = supported && (e.AllowedEffect & DragDropEffects.Copy) != 0
            ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void HandleShowroomGapDrop(object sender, DragEventArgs e)
    {
        var file = GetDroppedGap(e.Data);
        if (file != null) await LoadDroppedShowroomGapAsync(file);
        else HandleDragDrop(sender, e);
    }

    private async Task LoadDroppedShowroomGapAsync(string file)
    {
        var modelPack = GetAnimationMatchingTargetModelPack();
        if (modelPack?.Model == null)
        {
            mAnimationMatchView.SetStatus("Choose a character before dropping a GAP.");
            SetCharacterBrowserStatus("Choose a character before dropping a GAP.");
            return;
        }

        HidePairedShowroom();
        mCharacterBrowserRestoringSelection = true;
        try { mCharacterAnimationListBox.ClearSelected(); }
        finally { mCharacterBrowserRestoringSelection = false; }
        var (generation, token) = BeginCharacterBrowserAnimationLoad();
        ++mAnimationMatchPreviewGeneration;
        // Cancel searches/previews for the previous source before changing the query.
        mAnimationMatchController?.Dispose();
        mAnimationMatchController = null;
        mAnimationMatchControllerContext = null;
        mAnimationMatchView.SetResults(Array.Empty<GFDStudio.AnimationMatching.Core.AnimationMatchResult>());
        mAnimationMatchView.SetBusy(true, "Loading dropped GAP and companion parts…");
        SetCharacterBrowserStatus("Loading dropped GAP and companion parts…");
        try
        {
            var entry = new CharacterAnimationEntry
            {
                PackPath = Path.GetFullPath(file), Kind = CharacterAnimationListKind.Animation,
                Index = 0, DisplayName = Path.GetFileName(file) + " #1"
            };
            var context = CaptureCharacterBrowserAnimationPreparationContext(entry, loadAllGapCompanions: true);
            var prepared = await PrepareCharacterBrowserAnimationAsync(context, token);
            if (IsDisposed || !IsCurrentCharacterBrowserAnimationLoad(generation, token)) return;
            if (prepared.Animation == null) throw new InvalidDataException("The GAP has no normal animations.");

            await RegisterDroppedAnimationPackAsync(file, token);
            if (IsDisposed || !IsCurrentCharacterBrowserAnimationLoad(generation, token)) return;
            SelectDroppedAnimationPack(file);
            RememberBrowserTimelineMarks(prepared.Animation, entry);
            mAnimationMatchPreviewLoad = true;
            try { ModelViewControl.Instance.LoadAnimation(prepared.Animation, true); }
            finally { mAnimationMatchPreviewLoad = false; }
            mAnimationMatchCurrentSource = new GfdAnimationClip(
                GetCorrectedAnimationMatchClipId(entry), entry.DisplayName,
                modelPack.Model, () => prepared.Animation, AnimationMatchingFramesPerSecond);
            mAnimationMatchTailActive = false;
            mAnimationMatchTimeline.FrameCount = mAnimationMatchCurrentSource.FrameCount;
            mAnimationMatchTimeline.TransitionFrame = -1;
            EnsureAnimationMatchingController();
            mAnimationMatchController.SyncSourceFromHost();
            var loaded = prepared.AutoLoadedPackPaths ?? Array.Empty<string>();
            var names = string.Join(", ", loaded.Select(Path.GetFileName));
            var status = $"Loaded {entry.DisplayName}" + (loaded.Count > 1 ? $" + {loaded.Count - 1} companion(s): {names}" : "") +
                         (mAnimationMatchView.Visible ? ". Choose a frame, then press Match." : "");
            mAnimationMatchView.SetStatus(status);
            SetCharacterBrowserStatus(status);
            Logger.Debug($"AnimationMatch: dropped GAP {file}; loaded parts: {names}; {prepared.RetargetNote}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!IsDisposed && IsCurrentCharacterBrowserAnimationLoad(generation, token))
            {
                mAnimationMatchView.SetStatus("Could not load dropped GAP: " + ex.Message);
                SetCharacterBrowserStatus("Could not load dropped GAP: " + ex.Message);
            }
            Logger.Debug("AnimationMatch: dropped GAP failed: " + ex);
        }
        finally
        {
            if (!IsDisposed && generation == mCharacterBrowserAnimationLoadGeneration)
                mAnimationMatchView.SetBusy(false);
        }
    }

    private static Animation ComposeDroppedGapAnimation(
        CharacterAnimationEntry entry, Animation selected, out IReadOnlyCollection<string> loadedPaths,
        CancellationToken token)
    {
        var directory = Path.GetDirectoryName(entry.PackPath);
        var droppedStem = Path.GetFileNameWithoutExtension(entry.PackPath);
        var strippedStem = Regex.Replace(droppedStem, @"_(?:f|h\d*|\d+)$", "", RegexOptions.IgnoreCase);
        var siblingBody = Path.Combine(directory, strippedStem + ".GAP");
        // Numeric action IDs are body names unless the shorter body file really exists.
        var bodyPath = !string.Equals(strippedStem, droppedStem, StringComparison.OrdinalIgnoreCase) &&
                       File.Exists(siblingBody) ? siblingBody : entry.PackPath;
        var stem = Path.GetFileNameWithoutExtension(bodyPath);
        if (AreSamePath(bodyPath, entry.PackPath))
            stem = Regex.Replace(stem, @"_(?:f|h\d*)$", "", RegexOptions.IgnoreCase);
        // A face/hair/outfit drop loads its body first when that sibling is present.
        var body = AreSamePath(bodyPath, entry.PackPath) ? selected
            : GetCharacterBrowserNormalAnimation(Resource.Load<AnimationPack>(bodyPath), entry.Index);
        if (body == null) throw new InvalidDataException("The body GAP has no matching animation: " + bodyPath);
        var loaded = new List<string> { bodyPath };
        var components = new List<Animation>();
        var companionName = new Regex("^" + Regex.Escape(stem) + @"_(?:f|h\d*|\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var companion in Directory.EnumerateFiles(directory)
                     .Where(path => string.Equals(Path.GetExtension(path), ".GAP", StringComparison.OrdinalIgnoreCase) &&
                         companionName.IsMatch(Path.GetFileNameWithoutExtension(path)))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (AreSamePath(companion, bodyPath)) continue;
            var part = GetCharacterBrowserNormalAnimation(Resource.Load<AnimationPack>(companion), entry.Index);
            if (part == null || Math.Abs(part.Duration - body.Duration) > 1f / AnimationMatchingFramesPerSecond)
            {
                Logger.Debug("AnimationMatch: skipped companion with no matching clip/duration: " + companion);
                continue;
            }
            components.Add(part);
            loaded.Add(companion);
        }
        loadedPaths = loaded;
        return components.Count == 0 ? body : SplitCharacterAnimationComposer.AddComponentTracks(body, components.ToArray());
    }
}
