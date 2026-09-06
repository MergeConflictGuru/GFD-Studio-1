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
                var skeleton = GfdAnimationClip.CreateSkeleton(modelPack.Model);
                mAnimationMatchCurrentSource = new GfdAnimationClip(
                    "source:" + Guid.NewGuid().ToString("N"),
                    displayName,
                    modelPack.Model,
                    skeleton,
                    () => animation,
                    AnimationMatchingFramesPerSecond);
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
            var context = GetAnimationMatchingContextKey();
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

        private string GetAnimationMatchingContextKey()
        {
            return string.Join("|",
                mCharacterBrowserRoot ?? string.Empty,
                mCharacterBrowserCurrentModelPath ?? string.Empty,
                mCharacterBrowserScanGeneration,
                mCharacterAnimations.Count);
        }

        private IReadOnlyList<IAnimationClip> BuildAnimationMatchingCorpus()
        {
            var targetPack = GetAnimationMatchingTargetModelPack();
            if (targetPack?.Model == null)
                return Array.Empty<IAnimationClip>();

            var targetModel = targetPack.Model;
            var targetModelPath = mCharacterBrowserCurrentModelPath;
            var skeleton = GfdAnimationClip.CreateSkeleton(targetModel);
            var root = mCharacterBrowserRoot;

            // Resolve source skeletons once, on the UI thread, instead of doing an O(models)
            // lookup for every animation while the worker pool is busy sampling poses.
            var bodyModels = mCharacterModels
                .Where(model => model.Part == CharacterModelPart.Body && !string.IsNullOrWhiteSpace(model.Path))
                .ToArray();
            var exactModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var characterModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in bodyModels)
            {
                var directory = GetAnimationMatchCharacterDirectory(root, model.Path);
                var key = ExtractCharacterModelKey(model.Path);
                var characterId = ExtractCharacterId(model.Path);
                if (!string.IsNullOrWhiteSpace(directory) && !string.IsNullOrWhiteSpace(key))
                    exactModels.TryAdd(directory + "|" + key, model.Path);
                if (!string.IsNullOrWhiteSpace(directory) && !string.IsNullOrWhiteSpace(characterId))
                    characterModels.TryAdd(directory + "|" + characterId, model.Path);
            }

            var selectedKey = ExtractCharacterModelKey(targetModelPath);
            var entries = mCharacterAnimations
                .Where(entry => entry.Kind != CharacterAnimationListKind.BlendAnimation)
                .Where(IsCharacterBrowserAnimationForSelectedBody)
                .ToArray();
            var clips = new List<IAnimationClip>(entries.Length);

            foreach (var entry in entries)
            {
                var packPath = entry.PackPath;
                var kind = entry.Kind;
                var index = entry.Index;
                var displayName = entry.DisplayName;
                var directory = GetAnimationMatchCharacterDirectory(root, packPath);
                var animationKey = ExtractCharacterModelKey(packPath);
                var characterId = ExtractCharacterId(packPath);

                string sourceModelPath = null;
                if (!string.IsNullOrWhiteSpace(targetModelPath) &&
                    !string.IsNullOrWhiteSpace(selectedKey) &&
                    string.Equals(selectedKey, animationKey, StringComparison.OrdinalIgnoreCase))
                {
                    sourceModelPath = targetModelPath;
                }
                else if (!string.IsNullOrWhiteSpace(directory) && !string.IsNullOrWhiteSpace(animationKey))
                {
                    exactModels.TryGetValue(directory + "|" + animationKey, out sourceModelPath);
                }

                if (string.IsNullOrWhiteSpace(sourceModelPath) &&
                    !string.IsNullOrWhiteSpace(directory) && !string.IsNullOrWhiteSpace(characterId))
                {
                    characterModels.TryGetValue(directory + "|" + characterId, out sourceModelPath);
                }

                var capturedSourceModelPath = sourceModelPath;
                var id = packPath + "|" + kind + "|" + index;
                clips.Add(new GfdAnimationClip(
                    id,
                    displayName,
                    targetModel,
                    skeleton,
                    () => LoadAnimationMatchingCandidate(
                        packPath, kind, index, capturedSourceModelPath, targetModelPath, targetModel),
                    AnimationMatchingFramesPerSecond));
            }

            return clips;
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

        private static Animation LoadAnimationMatchingCandidate(
            string packPath,
            CharacterAnimationListKind kind,
            int index,
            string sourceModelPath,
            string targetModelPath,
            Model targetModel)
        {
            var pack = Resource.Load<AnimationPack>(packPath);
            Animation animation = kind switch
            {
                CharacterAnimationListKind.Animation =>
                    index >= 0 && index < (pack.Animations?.Count ?? 0) ? pack.Animations[index] : null,
                CharacterAnimationListKind.ExtraAnimation =>
                    index >= 0 && index < (pack.METAPHOR_AnimArray3?.Count ?? 0) ? pack.METAPHOR_AnimArray3[index] : null,
                CharacterAnimationListKind.BlendAnimation =>
                    index >= 0 && index < (pack.BlendAnimations?.Count ?? 0) ? pack.BlendAnimations[index] : null,
                _ => null
            };
            if (animation == null)
                throw new InvalidDataException("Animation no longer exists in pack: " + packPath);

            if (!string.IsNullOrWhiteSpace(sourceModelPath) &&
                !AreSamePath(sourceModelPath, targetModelPath))
            {
                var sourcePack = Resource.Load<ModelPack>(sourceModelPath);
                if (sourcePack?.Model != null)
                    animation.Retarget(sourcePack.Model, targetModel, false);
            }

            animation.FixTargetIds(targetModel);
            return animation;
        }

        IAnimationClip IGfdAnimationMatchingHost.CurrentAnimation => mAnimationMatchCurrentSource;
        IReadOnlyList<IAnimationClip> IGfdAnimationMatchingHost.SearchableAnimations => BuildAnimationMatchingCorpus();

        string IAnimationMatchingCacheHost.AnimationMatchingCachePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GFDStudio",
            "animation_matching",
            "pose_index.bin");

        string IAnimationMatchingCacheHost.AnimationMatchingCorpusSignature => GetAnimationMatchingContextKey();

        void IGfdAnimationMatchingHost.PreviewAnimation(IAnimationClip clip, int transitionFrame)
        {
            var targetPack = GetAnimationMatchingTargetModelPack();
            if (targetPack?.Model == null)
                return;

            var generation = ++mAnimationMatchPreviewGeneration;
            mAnimationMatchView.SetStatus("Preparing stitched preview…");
            _ = PreviewAnimationMatchingClipAsync(clip, transitionFrame, targetPack, generation);
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
                    GfdAnimationClipBaker.Bake(clip, targetPack.Model, targetPack.Version));
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

        Task<Image> IGfdAnimationMatchingHost.RenderCandidateThumbnailAsync(
            IAnimationClip clip,
            int frame,
            int width,
            int height,
            CancellationToken cancellationToken)
        {
            return Task.Run(() => DrawAnimationMatchPoseThumbnail(clip, frame, width, height, cancellationToken), cancellationToken);
        }

        private static Image DrawAnimationMatchPoseThumbnail(
            IAnimationClip clip,
            int frame,
            int width,
            int height,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            width = Math.Max(32, width);
            height = Math.Max(32, height);
            var pose = new BoneTransform[clip.Skeleton.BoneCount];
            clip.SampleGlobalPose(frame, pose);

            var minX = pose.Min(transform => transform.Position.X);
            var maxX = pose.Max(transform => transform.Position.X);
            var minY = pose.Min(transform => transform.Position.Y);
            var maxY = pose.Max(transform => transform.Position.Y);
            var spanX = Math.Max(0.001f, maxX - minX);
            var spanY = Math.Max(0.001f, maxY - minY);
            var scale = Math.Min((width - 12f) / spanX, (height - 12f) / spanY);
            var centerX = (minX + maxX) * 0.5f;
            var centerY = (minY + maxY) * 0.5f;

            PointF Project(System.Numerics.Vector3 point) => new(
                width * 0.5f + (point.X - centerX) * scale,
                height * 0.5f - (point.Y - centerY) * scale);

            var bitmap = new Bitmap(width, height);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.FromArgb(24, 24, 24));
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var bonePen = new Pen(Color.FromArgb(190, 210, 220), 1.5f);
            using var jointBrush = new SolidBrush(Color.FromArgb(225, 225, 225));
            for (var i = 0; i < pose.Length; i++)
            {
                var point = Project(pose[i].Position);
                var parent = clip.Skeleton.Parents[i];
                if (parent >= 0 && parent < pose.Length)
                    graphics.DrawLine(bonePen, point, Project(pose[parent].Position));
                graphics.FillEllipse(jointBrush, point.X - 1.5f, point.Y - 1.5f, 3f, 3f);
            }
            return bitmap;
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
                var animation = GfdAnimationClipBaker.Bake(clip, targetPack.Model, targetPack.Version, cancellationToken);
                var output = new AnimationPack(targetPack.Version);
                output.Animations.Add(animation);
                output.Save(path);
            }, cancellationToken);
        }
    }
}
