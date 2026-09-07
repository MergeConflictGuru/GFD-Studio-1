using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using GFDLibrary;
using GFDLibrary.Animations;
using GFDLibrary.Models;
using GFDStudio.AnimationMatching.Core;
using GFDStudio.AnimationMatching.Integration;
using GFDStudio.AnimationMatching.Stitching;
using GFDStudio.AnimationMatching.UI;
using GFDStudio.GUI.Controls;

namespace GFDStudio.GUI.Forms
{
    public partial class MainForm : IGfdAnimationMatchingHost, IAnimationMatchingCacheHost
    {
        private const float AnimationMatchingFramesPerSecond = 30f;

        private RangeTimelineControl mAnimationMatchTimeline;
        private Button mAnimationMatchButton;
        private AnimationMatchingModeControl mAnimationMatchView;
        private AnimationMatchingModeController mAnimationMatchController;
        private GfdAnimationClip mAnimationMatchCurrentSource;
        private string mAnimationMatchControllerContext;
        private bool mAnimationMatchPreviewLoad;
        private bool mAnimationMatchTailActive;
        private int mAnimationMatchPreviewGeneration;

        /// <summary>
        /// Adds only the Match affordance to the shared left transport. The actual matcher surface
        /// is an overlay in the existing right pane and is shown only after Match is pressed.
        /// </summary>
        private void InitializeAnimationMatching()
        {
            if (mAnimationMatchButton != null)
                return;

            mAnimationMatchTimeline = new RangeTimelineControl
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty
            };

            var timelineStack = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            timelineStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            timelineStack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            tableLayoutPanel_AnimationControls.Controls.Remove(mAnimationTrackBar);
            mAnimationTrackBar.Dock = DockStyle.Fill;
            mAnimationTrackBar.Margin = Padding.Empty;
            timelineStack.Controls.Add(mAnimationMatchTimeline, 0, 0);
            timelineStack.Controls.Add(mAnimationTrackBar, 0, 1);

            tableLayoutPanel_AnimationControls.ColumnCount = 4;
            tableLayoutPanel_AnimationControls.ColumnStyles.Clear();
            tableLayoutPanel_AnimationControls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62F));
            tableLayoutPanel_AnimationControls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 13F));
            tableLayoutPanel_AnimationControls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 11F));
            tableLayoutPanel_AnimationControls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14F));
            tableLayoutPanel_AnimationControls.Controls.Add(timelineStack, 0, 0);

            mAnimationMatchButton = new Button
            {
                Text = "Match",
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(65, 177, 225),
                ForeColor = Color.White,
                Margin = new Padding(4, 2, 4, 2),
                TabStop = false
            };
            mAnimationMatchButton.Click += AnimationMatchButton_Click;
            tableLayoutPanel_AnimationControls.Controls.Add(mAnimationMatchButton, 3, 0);

            // A second, thin range strip sits above the normal seek bar. Keep the bottom transport
            // fixed-height so resizing the window never steals space from the model viewport.
            splitContainer_LeftSide.FixedPanel = FixedPanel.Panel2;
            splitContainer_LeftSide.Panel2MinSize = 52;
            if (splitContainer_LeftSide.Height > 60)
                splitContainer_LeftSide.SplitterDistance = splitContainer_LeftSide.Height - 58;

            mAnimationMatchView = new AnimationMatchingModeControl { Visible = false };
            mAnimationMatchView.BackRequested += (_, _) => HideAnimationMatchingResults();
            mAnimationMatchView.BrowseRequested += (_, _) =>
            {
                ChooseCharacterBrowserRoot();
                mAnimationMatchView.SetRootPath(mCharacterBrowserRoot);
                mAnimationMatchControllerContext = null;
            };
            splitContainer_Main.Panel2.Controls.Add(mAnimationMatchView);

            // Capture exactly the animation that the shared showroom viewer is displaying. Preview
            // loads made by the matcher are suppressed so trying another candidate still compares
            // against the original source clip.
            ModelViewControl.Instance.AnimationLoaded += AnimationMatching_AnimationLoaded;
        }

        private void AnimationMatching_AnimationLoaded(object sender, Animation animation)
        {
            if (mAnimationMatchPreviewLoad || animation == null)
                return;

            var modelPack = GetAnimationMatchingTargetModelPack();
            if (modelPack?.Model == null)
                return;

            try
            {
                var selected = mCharacterAnimationListBox?.SelectedItem as CharacterAnimationEntry;
                var displayName = selected?.DisplayName ?? "Animation";
                var sourceId = selected == null
                    ? "source:" + Guid.NewGuid().ToString("N")
                    : GetCorrectedAnimationMatchClipId(selected);
                mAnimationMatchCurrentSource = new GfdAnimationClip(
                    sourceId,
                    displayName,
                    modelPack.Model,
                    () => animation,
                    AnimationMatchingFramesPerSecond);
                mAnimationMatchTailActive = false;
                mAnimationMatchTimeline.FrameCount = mAnimationMatchCurrentSource.FrameCount;
                mAnimationMatchTimeline.TransitionFrame = -1;
                mAnimationMatchView?.SetSource(displayName, mAnimationMatchCurrentSource.FrameCount, AnimationMatchingFramesPerSecond);
            }
            catch (Exception ex)
            {
                Logger.Debug("AnimationMatch: failed to capture current animation: " + ex);
                mAnimationMatchCurrentSource = null;
            }
        }

        private void AnimationMatchButton_Click(object sender, EventArgs e)
        {
            if (!ModelViewControl.Instance.IsAnimationLoaded || mAnimationMatchCurrentSource == null)
            {
                SetCharacterBrowserStatus("Load an animation before matching");
                return;
            }

            if (mCharacterAnimations.Count == 0)
            {
                SetCharacterBrowserStatus("No searchable animations have been indexed by the Character Browser yet");
                return;
            }

            ShowAnimationMatchingResults();
            EnsureAnimationMatchingController();
            mAnimationMatchView.SetRootPath(mCharacterBrowserRoot);
            mAnimationMatchView.SetSelection(mAnimationMatchTimeline.Selection);
            mAnimationMatchController.SyncSourceFromHost();
            mAnimationMatchView.BeginSearch();
        }

        private void ShowAnimationMatchingResults()
        {
            mAnimationMatchView.Visible = true;
            mAnimationMatchView.BringToFront();
            mAnimationMatchView.SetRootPath(mCharacterBrowserRoot);
        }

        private void HideAnimationMatchingResults()
        {
            if (mAnimationMatchView == null)
                return;

            mAnimationMatchView.Visible = false;
            if (mAnimationMatchTailActive && mAnimationMatchCurrentSource != null)
                SetCharacterBrowserStatus("Matched tail active: " + mAnimationMatchCurrentSource.DisplayName);

            if (mCharacterBrowserPanel != null && mCharacterBrowserPanel.Visible)
            {
                mCharacterBrowserPanel.BringToFront();
            }
            else
            {
                splitContainer_RightSide.Visible = true;
                splitContainer_RightSide.BringToFront();
            }
        }

        private void EnsureAnimationMatchingController()
        {
            var context = GetCorrectedAnimationMatchingContextKey();
            if (mAnimationMatchController != null && string.Equals(context, mAnimationMatchControllerContext, StringComparison.Ordinal))
                return;

            mAnimationMatchController?.Dispose();
            mAnimationMatchController = new AnimationMatchingModeController(this, mAnimationMatchView);
            mAnimationMatchControllerContext = context;
            mAnimationMatchView.SetResults(Array.Empty<AnimationMatchResult>());
        }

        private ModelPack GetAnimationMatchingTargetModelPack()
        {
            return mCharacterBrowserCurrentModelPack ?? ModelEditorTreeView?.TopNode?.Data as ModelPack;
        }

        private static string GetAnimationMatchCharacterDirectory(string root, string path)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
                return null;
            try
            {
                var relative = Path.GetRelativePath(root, path);
                var parts = relative.Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries);
                return parts.Length > 1 ? parts[0] : null;
            }
            catch
            {
                return null;
            }
        }

        IAnimationClip IGfdAnimationMatchingHost.CurrentAnimation => mAnimationMatchCurrentSource;
        IReadOnlyList<IAnimationClip> IGfdAnimationMatchingHost.SearchableAnimations =>
            BuildCorrectedAnimationMatchingCorpus();

        string IAnimationMatchingCacheHost.AnimationMatchingCachePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GFDStudio",
            "animation_matching",
            "pose_index.bin");

        string IAnimationMatchingCacheHost.AnimationMatchingCorpusSignature =>
            GetCorrectedAnimationMatchingContextKey();

        void IGfdAnimationMatchingHost.PreviewAnimation(IAnimationClip clip, int transitionFrame)
        {
            var targetPack = GetAnimationMatchingTargetModelPack();
            if (targetPack?.Model == null)
                return;

            var generation = ++mAnimationMatchPreviewGeneration;
            mAnimationMatchView.SetStatus("Preparing stitched preview…");
            _ = PreviewAnimationMatchingClipAsync(clip, transitionFrame, targetPack, generation);
        }

        async Task IGfdAnimationMatchingHost.OpenAnimationAsync(
            IAnimationClip clip,
            CancellationToken cancellationToken)
        {
            var targetPack = GetAnimationMatchingTargetModelPack();
            if (targetPack?.Model == null)
                throw new InvalidOperationException("No target model is loaded.");

            var generation = ++mAnimationMatchPreviewGeneration;
            mAnimationMatchView.SetStatus("Opening matched tail…");
            var baked = await Task.Run(() =>
            {
                var previewClip = GfdAnimationClipBaker.CreateTargetPreviewClip(clip, targetPack.Model);
                return GfdAnimationClipBaker.Bake(previewClip, targetPack.Model, targetPack.Version, cancellationToken);
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (IsDisposed || generation != mAnimationMatchPreviewGeneration)
                throw new OperationCanceledException(cancellationToken);

            mAnimationMatchPreviewLoad = true;
            try
            {
                ModelViewControl.Instance.LoadAnimation(baked, true);
            }
            finally
            {
                mAnimationMatchPreviewLoad = false;
            }

            mAnimationMatchCurrentSource = new GfdAnimationClip(
                "source:" + clip.Id,
                clip.DisplayName,
                targetPack.Model,
                () => baked,
                AnimationMatchingFramesPerSecond);
            mAnimationMatchTailActive = true;
            mAnimationMatchTimeline.FrameCount = mAnimationMatchCurrentSource.FrameCount;
            mAnimationMatchTimeline.TransitionFrame = -1;
            mAnimationMatchView.SetSource(
                mAnimationMatchCurrentSource.DisplayName,
                mAnimationMatchCurrentSource.FrameCount,
                mAnimationMatchCurrentSource.FramesPerSecond);
            mAnimationMatchView.SetStatus("Matched tail loaded; refreshing results…");
        }

        private async Task PreviewAnimationMatchingClipAsync(
            IAnimationClip clip,
            int transitionFrame,
            ModelPack targetPack,
            int generation)
        {
            try
            {
                var baked = await Task.Run(() =>
                {
                    var previewClip = GfdAnimationClipBaker.CreateTargetPreviewClip(clip, targetPack.Model);
                    return GfdAnimationClipBaker.Bake(previewClip, targetPack.Model, targetPack.Version);
                });
                if (IsDisposed || generation != mAnimationMatchPreviewGeneration)
                    return;

                mAnimationMatchPreviewLoad = true;
                try
                {
                    ModelViewControl.Instance.LoadAnimation(baked, true);
                }
                finally
                {
                    mAnimationMatchPreviewLoad = false;
                }

                mAnimationMatchTimeline.FrameCount = clip.FrameCount;
                mAnimationMatchTimeline.TransitionFrame = transitionFrame;
                mAnimationMatchView.SetStatus("Stitched preview");
            }
            catch (Exception ex)
            {
                if (!IsDisposed && generation == mAnimationMatchPreviewGeneration)
                    mAnimationMatchView.SetStatus("Preview failed: " + ex.Message);
            }
        }

        async Task<IReadOnlyList<Image>> IGfdAnimationMatchingHost.RenderCandidateThumbnailAsync(
            IAnimationClip clip,
            int frame,
            int width,
            int height,
            CancellationToken cancellationToken)
        {
            var targetPack = GetAnimationMatchingTargetModelPack();
            if (targetPack?.Model == null)
                return null;

            const int previewFrameCount = 8;
            var availableFrames = Math.Max(1, clip.FrameCount - Math.Clamp(frame, 0, Math.Max(0, clip.FrameCount - 1)));
            var frameCount = Math.Min(previewFrameCount, availableFrames);
            var firstFrame = Math.Clamp(frame, 0, Math.Max(0, clip.FrameCount - 1));
            var baked = await Task.Run(() =>
            {
                var previewClip = GfdAnimationClipBaker.CreateTargetPreviewClip(clip, targetPack.Model);
                return GfdAnimationClipBaker.BakeRange(
                    previewClip, targetPack.Model, targetPack.Version, firstFrame, frameCount, cancellationToken);
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var times = Enumerable.Range(0, frameCount)
                .Select(index => index / (double)AnimationMatchingFramesPerSecond)
                .ToArray();
            var bitmaps = ModelViewControl.Instance.RenderAnimationThumbnails(baked, times, width, height);
            return bitmaps.Cast<Image>().ToArray();
        }

        async Task IGfdAnimationMatchingHost.ExportAnimationAsync(IAnimationClip clip, CancellationToken cancellationToken)
        {
            var targetPack = GetAnimationMatchingTargetModelPack();
            if (targetPack?.Model == null)
                throw new InvalidOperationException("No target model is loaded.");

            using var dialog = new SaveFileDialog
            {
                Filter = "Animation pack (*.GAP)|*.GAP|All files (*.*)|*.*",
                DefaultExt = "GAP",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = "animation_match_stitched.GAP",
                Title = "Export stitched animation"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                throw new OperationCanceledException(cancellationToken);

            var path = dialog.FileName;
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var previewClip = GfdAnimationClipBaker.CreateTargetPreviewClip(clip, targetPack.Model);
                var animation = GfdAnimationClipBaker.Bake(previewClip, targetPack.Model, targetPack.Version, cancellationToken);
                var output = new AnimationPack(targetPack.Version);
                output.Animations.Add(animation);
                output.Save(path);
            }, cancellationToken);
        }

        async Task IGfdAnimationMatchingHost.ExportAnimationPartsAsync(
            IAnimationClip clip,
            CancellationToken cancellationToken)
        {
            var targetPack = GetAnimationMatchingTargetModelPack();
            if (targetPack?.Model == null)
                throw new InvalidOperationException("No target model is loaded.");

            using var dialog = new SaveFileDialog
            {
                Filter = "Animation pack (*.GAP)|*.GAP|All files (*.*)|*.*",
                DefaultExt = "GAP",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = "animation_match_parts.GAP",
                Title = "Export stitched animation parts"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                throw new OperationCanceledException(cancellationToken);

            var selectedPath = dialog.FileName;
            var directory = Path.GetDirectoryName(selectedPath) ?? string.Empty;
            var stem = Path.GetFileNameWithoutExtension(selectedPath);
            var extension = Path.GetExtension(selectedPath);
            var sourcePath = Path.Combine(directory, stem + "_source" + extension);
            var candidatePath = Path.Combine(directory, stem + "_candidate" + extension);
            if (File.Exists(sourcePath) || File.Exists(candidatePath))
            {
                var overwrite = MessageBox.Show(
                    this,
                    $"These files already exist and will be replaced:\n\n{Path.GetFileName(sourcePath)}\n{Path.GetFileName(candidatePath)}\n\nContinue?",
                    "Overwrite animation parts",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (overwrite != DialogResult.Yes)
                    throw new OperationCanceledException(cancellationToken);
            }

            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var previewClip = GfdAnimationClipBaker.CreateTargetPreviewClip(clip, targetPack.Model);
                if (previewClip is not StitchedAnimation stitched)
                    throw new InvalidOperationException("The selected animation is not a stitched clip.");

                var parts = stitched.CreateExportParts();
                var sourceAnimation = GfdAnimationClipBaker.Bake(
                    parts.Source, targetPack.Model, targetPack.Version, cancellationToken);
                var candidateAnimation = GfdAnimationClipBaker.Bake(
                    parts.Candidate, targetPack.Model, targetPack.Version, cancellationToken);

                var sourceOutput = new AnimationPack(targetPack.Version);
                sourceOutput.Animations.Add(sourceAnimation);
                sourceOutput.Save(sourcePath);

                cancellationToken.ThrowIfCancellationRequested();
                var candidateOutput = new AnimationPack(targetPack.Version);
                candidateOutput.Animations.Add(candidateAnimation);
                candidateOutput.Save(candidatePath);
            }, cancellationToken);
        }
    }
}
