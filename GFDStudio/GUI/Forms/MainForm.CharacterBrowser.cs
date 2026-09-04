using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Text.RegularExpressions;

using GFDLibrary;
using GFDLibrary.Animations;
using GFDStudio.GUI.Controls;

namespace GFDStudio.GUI.Forms
{
    public partial class MainForm
    {
        private sealed class CharacterModelEntry
        {
            public string Path { get; init; }
            public string DisplayName { get; init; }
            public override string ToString() => DisplayName;
        }

        private enum CharacterAnimationListKind
        {
            Animation,
            BlendAnimation,
            ExtraAnimation
        }

        private sealed class CharacterAnimationEntry
        {
            public string PackPath { get; init; }
            public CharacterAnimationListKind Kind { get; init; }
            public int Index { get; init; }
            public string DisplayName { get; init; }
            public override string ToString() => DisplayName;
        }

        private sealed class CharacterAnimationDefinitionSet
        {
            private readonly Dictionary<string, List<byte[]>> mSerializedDefinitions =
                new Dictionary<string, List<byte[]>>(StringComparer.Ordinal);

            public bool Add(Animation animation)
            {
                var serialized = SerializeAnimation(animation);
                var hash = Convert.ToBase64String(SHA256.HashData(serialized));

                if (!mSerializedDefinitions.TryGetValue(hash, out var candidates))
                {
                    mSerializedDefinitions.Add(hash, new List<byte[]> { serialized });
                    return true;
                }

                // Keep the comparison exact even if two serialized animations ever share a hash.
                foreach (var candidate in candidates)
                {
                    if (serialized.AsSpan().SequenceEqual(candidate))
                        return false;
                }

                candidates.Add(serialized);
                return true;
            }
        }

        private Panel mCharacterBrowserPanel;
        private TextBox mCharacterRootTextBox;
        private TextBox mCharacterModelFilterTextBox;
        private TextBox mCharacterAnimationFilterTextBox;
        private ListBox mCharacterModelListBox;
        private ListBox mCharacterAnimationListBox;
        private Label mCharacterBrowserStatusLabel;
        private ToolStripMenuItem mCharacterBrowserToolStripMenuItem;

        private readonly List<CharacterModelEntry> mCharacterModels = new List<CharacterModelEntry>();
        private readonly List<CharacterAnimationEntry> mCharacterAnimations = new List<CharacterAnimationEntry>();

        private CancellationTokenSource mCharacterBrowserScanCancellation;
        private string mCharacterBrowserRoot;
        private string mCharacterBrowserCurrentModelPath;
        private int mCharacterBrowserScanGeneration;

        private string CharacterBrowserSettingsPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GFDStudio",
                "character_browser_root.txt");

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            if (mCharacterBrowserPanel != null)
                return;

            InitializeCharacterBrowser();
        }

        private void InitializeCharacterBrowser()
        {
            mCharacterBrowserToolStripMenuItem = new ToolStripMenuItem("Character Browser")
            {
                CheckOnClick = true,
                Checked = true,
                ShortcutKeys = Keys.Control | Keys.B
            };
            mCharacterBrowserToolStripMenuItem.CheckedChanged += (s, e) =>
                SetCharacterBrowserVisible(mCharacterBrowserToolStripMenuItem.Checked);
            toolsToolStripMenuItem.DropDownItems.Insert(0, mCharacterBrowserToolStripMenuItem);
            toolsToolStripMenuItem.DropDownItems.Insert(1, new ToolStripSeparator());

            mCharacterBrowserPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(30, 30, 30),
                Padding = new Padding(6)
            };

            var rootLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                BackColor = Color.FromArgb(30, 30, 30),
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            mCharacterBrowserPanel.Controls.Add(rootLayout);

            rootLayout.Controls.Add(CreateCharacterBrowserToolbar(), 0, 0);
            rootLayout.Controls.Add(CreateCharacterBrowserListSection(
                "MODELS",
                out mCharacterModelFilterTextBox,
                out mCharacterModelListBox), 0, 1);
            rootLayout.Controls.Add(CreateCharacterBrowserListSection(
                "ANIMATIONS",
                out mCharacterAnimationFilterTextBox,
                out mCharacterAnimationListBox), 0, 2);

            mCharacterBrowserStatusLabel = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.Silver,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Text = "Choose model\\character root"
            };
            rootLayout.Controls.Add(mCharacterBrowserStatusLabel, 0, 3);

            mCharacterModelFilterTextBox.TextChanged += (s, e) => RefreshCharacterModelList();
            mCharacterAnimationFilterTextBox.TextChanged += (s, e) => RefreshCharacterAnimationList();
            mCharacterModelListBox.SelectedIndexChanged += CharacterModelListBox_SelectedIndexChanged;
            mCharacterAnimationListBox.SelectedIndexChanged += CharacterAnimationListBox_SelectedIndexChanged;

            // Keep keyboard browsing completely frictionless: normal Up/Down selection changes
            // immediately load the newly selected model/animation.
            mCharacterModelListBox.KeyDown += CharacterBrowserList_KeyDown;
            mCharacterAnimationListBox.KeyDown += CharacterBrowserList_KeyDown;

            splitContainer_Main.Panel2.Controls.Add(mCharacterBrowserPanel);
            mCharacterBrowserPanel.BringToFront();
            SetCharacterBrowserVisible(true);

            var savedRoot = LoadCharacterBrowserRoot();
            if (!string.IsNullOrWhiteSpace(savedRoot) && Directory.Exists(savedRoot))
            {
                SetCharacterBrowserRoot(savedRoot, scanImmediately: true);
            }
            else
            {
                var inferredRoot = TryInferCharacterRoot(LastOpenedFilePath);
                if (inferredRoot != null)
                    SetCharacterBrowserRoot(inferredRoot, scanImmediately: true);
            }
        }

        private Control CreateCharacterBrowserToolbar()
        {
            var toolbar = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = Color.FromArgb(30, 30, 30)
            };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));

            mCharacterRootTextBox = new TextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0, 4, 5, 4)
            };

            var browseButton = CreateCharacterBrowserButton("Browse");
            browseButton.Click += (s, e) => ChooseCharacterBrowserRoot();

            var rescanButton = CreateCharacterBrowserButton("Rescan");
            rescanButton.Click += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(mCharacterBrowserRoot))
                    StartCharacterBrowserScan(mCharacterBrowserRoot);
            };

            var editorButton = CreateCharacterBrowserButton("Editor");
            editorButton.Click += (s, e) =>
            {
                mCharacterBrowserToolStripMenuItem.Checked = false;
            };

            toolbar.Controls.Add(mCharacterRootTextBox, 0, 0);
            toolbar.Controls.Add(browseButton, 1, 0);
            toolbar.Controls.Add(rescanButton, 2, 0);
            toolbar.Controls.Add(editorButton, 3, 0);
            return toolbar;
        }

        private static Button CreateCharacterBrowserButton(string text)
        {
            return new Button
            {
                Text = text,
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(50, 50, 54),
                ForeColor = Color.WhiteSmoke,
                Margin = new Padding(2, 3, 2, 3),
                TabStop = false
            };
        }

        private static Control CreateCharacterBrowserListSection(
            string title,
            out TextBox filterTextBox,
            out ListBox listBox)
        {
            var group = new GroupBox
            {
                Text = title,
                Dock = DockStyle.Fill,
                ForeColor = Color.Gainsboro,
                BackColor = Color.FromArgb(30, 30, 30),
                Padding = new Padding(5),
                Margin = new Padding(0, 3, 0, 3)
            };

            var body = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            filterTextBox = new TextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0, 2, 0, 4)
            };

            listBox = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(37, 37, 38),
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle,
                IntegralHeight = false,
                HorizontalScrollbar = true,
                Font = new Font("Consolas", 9F),
                Margin = Padding.Empty
            };

            body.Controls.Add(filterTextBox, 0, 0);
            body.Controls.Add(listBox, 0, 1);
            group.Controls.Add(body);
            return group;
        }

        private void SetCharacterBrowserVisible(bool visible)
        {
            if (mCharacterBrowserPanel == null)
                return;

            mCharacterBrowserPanel.Visible = visible;
            splitContainer_RightSide.Visible = !visible;

            if (visible)
            {
                mCharacterBrowserPanel.BringToFront();
                mCharacterAnimationListBox?.Focus();
            }
            else
            {
                splitContainer_RightSide.BringToFront();
            }
        }

        private void CharacterBrowserList_KeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+B toggles the browser even while one of the lists owns focus.
            if (e.Control && e.KeyCode == Keys.B)
            {
                mCharacterBrowserToolStripMenuItem.Checked = !mCharacterBrowserToolStripMenuItem.Checked;
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void ChooseCharacterBrowserRoot()
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Select Persona model\\character directory",
                ShowNewFolderButton = false,
                SelectedPath = Directory.Exists(mCharacterBrowserRoot) ? mCharacterBrowserRoot : string.Empty
            };

            if (dialog.ShowDialog(this) == DialogResult.OK)
                SetCharacterBrowserRoot(dialog.SelectedPath, scanImmediately: true);
        }

        private void SetCharacterBrowserRoot(string root, bool scanImmediately)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return;

            mCharacterBrowserRoot = Path.GetFullPath(root);
            mCharacterRootTextBox.Text = mCharacterBrowserRoot;
            SaveCharacterBrowserRoot(mCharacterBrowserRoot);

            if (scanImmediately)
                StartCharacterBrowserScan(mCharacterBrowserRoot);
        }

        private async void StartCharacterBrowserScan(string root)
        {
            mCharacterBrowserScanCancellation?.Cancel();
            mCharacterBrowserScanCancellation?.Dispose();
            mCharacterBrowserScanCancellation = new CancellationTokenSource();
            var token = mCharacterBrowserScanCancellation.Token;
            var generation = ++mCharacterBrowserScanGeneration;

            mCharacterModels.Clear();
            mCharacterAnimations.Clear();
            mCharacterBrowserCurrentModelPath = null;
            mCharacterModelListBox.Items.Clear();
            mCharacterAnimationListBox.Items.Clear();
            SetCharacterBrowserStatus("Scanning files...");

            try
            {
                var files = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();

                    var models = Directory.EnumerateFiles(root, "*.GMD", SearchOption.AllDirectories)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    token.ThrowIfCancellationRequested();

                    var gaps = Directory.EnumerateFiles(root, "*.GAP", SearchOption.AllDirectories)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    return (models, gaps);
                }, token);

                if (token.IsCancellationRequested || generation != mCharacterBrowserScanGeneration)
                    return;

                foreach (var path in files.models)
                {
                    mCharacterModels.Add(new CharacterModelEntry
                    {
                        Path = path,
                        DisplayName = MakeCharacterBrowserRelativePath(path)
                    });
                }
                RefreshCharacterModelList();

                SetCharacterBrowserStatus($"{files.models.Count:N0} models; indexing {files.gaps.Count:N0} GAP files...");

                var parsedCount = 0;
                var failedCount = 0;

                await Task.Run(() =>
                {
                    var batch = new List<CharacterAnimationEntry>(128);
                    var animationDefinitions = new CharacterAnimationDefinitionSet();

                    foreach (var gapPath in files.gaps)
                    {
                        token.ThrowIfCancellationRequested();

                        try
                        {
                            var pack = Resource.Load<AnimationPack>(gapPath);
                            AddAnimationPackEntries(batch, root, gapPath, pack, animationDefinitions);
                        }
                        catch (Exception ex)
                        {
                            failedCount++;
                            Logger.Debug($"CharacterBrowser: failed to index {gapPath}: {ex}");
                        }

                        parsedCount++;

                        if (batch.Count >= 96 || parsedCount == files.gaps.Count)
                        {
                            var toAdd = batch.ToArray();
                            batch.Clear();
                            var parsedSnapshot = parsedCount;
                            var failedSnapshot = failedCount;

                            BeginInvoke(new Action(() =>
                            {
                                if (token.IsCancellationRequested || generation != mCharacterBrowserScanGeneration)
                                    return;

                                AddCharacterAnimationBatch(toAdd);
                                SetCharacterBrowserStatus(
                                    $"{mCharacterModels.Count:N0} models | {mCharacterAnimations.Count:N0} unique animations | " +
                                    $"GAP {parsedSnapshot:N0}/{files.gaps.Count:N0}" +
                                    (failedSnapshot == 0 ? string.Empty : $" | {failedSnapshot:N0} failed"));
                            }));
                        }
                    }
                }, token);

                if (!token.IsCancellationRequested && generation == mCharacterBrowserScanGeneration)
                {
                    SetCharacterBrowserStatus(
                        $"Ready: {mCharacterModels.Count:N0} models, {mCharacterAnimations.Count:N0} unique animations" +
                        (failedCount == 0 ? string.Empty : $" ({failedCount:N0} GAP files failed to parse)"));
                }
            }
            catch (OperationCanceledException)
            {
                // A rescan/root change superseded this scan.
            }
            catch (Exception ex)
            {
                SetCharacterBrowserStatus("Scan failed: " + ex.Message);
            }
        }

        private static void AddAnimationPackEntries(
            List<CharacterAnimationEntry> output,
            string root,
            string gapPath,
            AnimationPack pack,
            CharacterAnimationDefinitionSet animationDefinitions)
        {
            var relative = Path.GetRelativePath(root, gapPath);
            var stem = Path.ChangeExtension(relative, null);

            var normalCount = pack.Animations?.Count(HasAnimationKeyframes) ?? 0;
            var blendCount = pack.BlendAnimations?.Count(HasAnimationKeyframes) ?? 0;
            var extraCount = pack.METAPHOR_AnimArray3?.Count(HasAnimationKeyframes) ?? 0;
            var total = normalCount + blendCount + extraCount;

            if (pack.Animations != null)
            {
                for (var i = 0; i < pack.Animations.Count; i++)
                {
                    if (!HasAnimationKeyframes(pack.Animations[i]) ||
                        !animationDefinitions.Add(pack.Animations[i]))
                        continue;

                    output.Add(new CharacterAnimationEntry
                    {
                        PackPath = gapPath,
                        Kind = CharacterAnimationListKind.Animation,
                        Index = i,
                        DisplayName = total == 1 ? stem : $"{stem}  #{i + 1}"
                    });
                }
            }

            if (pack.BlendAnimations != null)
            {
                for (var i = 0; i < pack.BlendAnimations.Count; i++)
                {
                    if (!HasAnimationKeyframes(pack.BlendAnimations[i]) ||
                        !animationDefinitions.Add(pack.BlendAnimations[i]))
                        continue;

                    output.Add(new CharacterAnimationEntry
                    {
                        PackPath = gapPath,
                        Kind = CharacterAnimationListKind.BlendAnimation,
                        Index = i,
                        DisplayName = $"{stem}  [blend {i + 1}]"
                    });
                }
            }

            if (pack.METAPHOR_AnimArray3 != null)
            {
                for (var i = 0; i < pack.METAPHOR_AnimArray3.Count; i++)
                {
                    if (!HasAnimationKeyframes(pack.METAPHOR_AnimArray3[i]) ||
                        !animationDefinitions.Add(pack.METAPHOR_AnimArray3[i]))
                        continue;

                    output.Add(new CharacterAnimationEntry
                    {
                        PackPath = gapPath,
                        Kind = CharacterAnimationListKind.ExtraAnimation,
                        Index = i,
                        DisplayName = $"{stem}  [extra {i + 1}]"
                    });
                }
            }
        }

        private static bool HasAnimationKeyframes(Animation animation)
        {
            return animation?.Controllers?.Any(controller =>
                controller?.Layers?.Any(layer => layer?.Keys?.Count > 0) == true) == true;
        }

        private static byte[] SerializeAnimation(Animation animation)
        {
            using var stream = new MemoryStream();
            animation.Save(stream, leaveOpen: true);
            return stream.ToArray();
        }

        private void AddCharacterAnimationBatch(IEnumerable<CharacterAnimationEntry> entries)
        {
            var filter = mCharacterAnimationFilterTextBox.Text?.Trim();
            var addDirectly = string.IsNullOrEmpty(filter);

            mCharacterAnimationListBox.BeginUpdate();
            try
            {
                foreach (var entry in entries)
                {
                    mCharacterAnimations.Add(entry);
                    if (addDirectly || CharacterBrowserMatches(entry.DisplayName, filter))
                        mCharacterAnimationListBox.Items.Add(entry);
                }
            }
            finally
            {
                mCharacterAnimationListBox.EndUpdate();
            }
        }

        private void RefreshCharacterModelList()
        {
            if (mCharacterModelListBox == null)
                return;

            var filter = mCharacterModelFilterTextBox.Text?.Trim();
            mCharacterModelListBox.BeginUpdate();
            try
            {
                mCharacterModelListBox.Items.Clear();
                foreach (var entry in mCharacterModels)
                {
                    if (CharacterBrowserMatches(entry.DisplayName, filter))
                        mCharacterModelListBox.Items.Add(entry);
                }
            }
            finally
            {
                mCharacterModelListBox.EndUpdate();
            }
        }

        private void RefreshCharacterAnimationList()
        {
            if (mCharacterAnimationListBox == null)
                return;

            var filter = mCharacterAnimationFilterTextBox.Text?.Trim();
            mCharacterAnimationListBox.BeginUpdate();
            try
            {
                mCharacterAnimationListBox.Items.Clear();
                foreach (var entry in mCharacterAnimations)
                {
                    if (CharacterBrowserMatches(entry.DisplayName, filter))
                        mCharacterAnimationListBox.Items.Add(entry);
                }
            }
            finally
            {
                mCharacterAnimationListBox.EndUpdate();
            }
        }

        private static bool CharacterBrowserMatches(string value, string filter)
        {
            return string.IsNullOrWhiteSpace(filter) ||
                   value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void CharacterModelListBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (mCharacterModelListBox.SelectedItem is not CharacterModelEntry entry)
                return;

            try
            {
                mCharacterBrowserCurrentModelPath = entry.Path;
                OpenFile(entry.Path);

                if (mCharacterAnimationListBox.SelectedItem is CharacterAnimationEntry animationEntry)
                {
                    var animation = PrepareCharacterBrowserAnimation(animationEntry, out var retargetNote);
                    if (animation != null)
                    {
                        ModelViewControl.Instance.LoadAnimation(animation, true);
                        SetCharacterBrowserStatus(
                            string.IsNullOrWhiteSpace(retargetNote)
                                ? "Model: " + entry.DisplayName
                                : $"Model: {entry.DisplayName} ({retargetNote})");
                        return;
                    }
                }

                SetCharacterBrowserStatus("Model: " + entry.DisplayName);
            }
            catch (Exception ex)
            {
                SetCharacterBrowserStatus("Model load failed: " + ex.Message);
            }
        }

        private void CharacterAnimationListBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (mCharacterAnimationListBox.SelectedItem is not CharacterAnimationEntry entry)
                return;

            try
            {
                var animation = PrepareCharacterBrowserAnimation(entry, out var retargetNote);
                if (animation == null)
                {
                    SetCharacterBrowserStatus("Animation no longer exists in pack: " + entry.DisplayName);
                    return;
                }

                // LoadAnimation(reset: true) starts playback automatically in ModelViewControl.
                ModelViewControl.Instance.LoadAnimation(animation, true);
                SetCharacterBrowserStatus(
                    string.IsNullOrWhiteSpace(retargetNote)
                        ? "Animation: " + entry.DisplayName
                        : $"Animation: {entry.DisplayName} ({retargetNote})");
            }
            catch (Exception ex)
            {
                SetCharacterBrowserStatus("Animation load failed: " + ex.Message);
            }
        }

        private Animation PrepareCharacterBrowserAnimation(CharacterAnimationEntry entry, out string retargetNote)
        {
            retargetNote = null;

            // Always load a fresh pack because retargeting mutates the animation object in memory.
            // This keeps the cached/showroom source data untouched when switching target models.
            var pack = Resource.Load<AnimationPack>(entry.PackPath);
            var animation = GetCharacterBrowserAnimation(pack, entry);
            if (animation == null)
                return null;

            var targetModelPack = ModelEditorTreeView?.TopNode?.Data as ModelPack;
            if (targetModelPack?.Model == null)
                return animation;

            var sourceModelEntry = FindCharacterModelForAnimation(entry.PackPath);
            if (sourceModelEntry == null)
            {
                retargetNote = "source model not found; preview uses original animation";
                return animation;
            }

            if (AreSamePath(sourceModelEntry.Path, GetCurrentCharacterBrowserModelPath()))
            {
                retargetNote = "source model";
                return animation;
            }

            var sourceModelPack = Resource.Load<ModelPack>(sourceModelEntry.Path);
            if (sourceModelPack.Model == null)
            {
                retargetNote = "source model has no model data; preview uses original animation";
                return animation;
            }

            switch (entry.Kind)
            {
                case CharacterAnimationListKind.Animation:
                    animation.Retarget(sourceModelPack.Model, targetModelPack.Model, false);
                    retargetNote = "retargeted in preview";
                    break;

                case CharacterAnimationListKind.BlendAnimation:
                    // Blend animations are already relative; only their node IDs need updating.
                    animation.FixTargetIds(targetModelPack.Model);
                    retargetNote = "target IDs fixed in preview";
                    break;

                default:
                    retargetNote = "source model differs; preview uses original animation";
                    break;
            }

            return animation;
        }

        private string GetCurrentCharacterBrowserModelPath()
        {
            if (!string.IsNullOrWhiteSpace(LastOpenedFilePath) &&
                string.Equals(Path.GetExtension(LastOpenedFilePath), ".GMD", StringComparison.OrdinalIgnoreCase))
                return LastOpenedFilePath;

            return mCharacterBrowserCurrentModelPath;
        }

        private static bool AreSamePath(string firstPath, string secondPath)
        {
            if (string.IsNullOrWhiteSpace(firstPath) || string.IsNullOrWhiteSpace(secondPath))
                return false;

            try
            {
                return string.Equals(Path.GetFullPath(firstPath), Path.GetFullPath(secondPath),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(firstPath, secondPath, StringComparison.OrdinalIgnoreCase);
            }
        }

        private CharacterModelEntry FindCharacterModelForAnimation(string gapPath)
        {
            var animationKey = ExtractCharacterModelKey(gapPath);
            if (string.IsNullOrWhiteSpace(animationKey))
                return null;

            var characterDirectory = GetCharacterDirectory(gapPath);
            var characterModels = mCharacterModels
                .Where(model => string.Equals(GetCharacterDirectory(model.Path), characterDirectory,
                                              StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Prefer an exact variant when one exists. Event animations often use a variant
            // number that has no corresponding model, though, so fall back to any model for
            // the same character in this directory.
            var exactMatch = characterModels
                .Where(model => string.Equals(ExtractCharacterModelKey(model.Path), animationKey,
                                              StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(model => Path.GetFileNameWithoutExtension(model.Path)
                    .StartsWith("c" + animationKey, StringComparison.OrdinalIgnoreCase))
                .ThenBy(model => model.Path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (exactMatch != null)
                return exactMatch;

            var characterId = ExtractCharacterId(gapPath);
            if (string.IsNullOrWhiteSpace(characterId))
                return null;

            return characterModels
                .Where(model => string.Equals(ExtractCharacterId(model.Path), characterId,
                                              StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(model => Path.GetFileNameWithoutExtension(model.Path)
                    .StartsWith("c" + characterId + "_", StringComparison.OrdinalIgnoreCase))
                .ThenBy(model => model.Path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private string GetCharacterDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(mCharacterBrowserRoot))
                return null;

            var relative = Path.GetRelativePath(mCharacterBrowserRoot, path);
            var parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                                       StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 ? parts[0] : null;
        }

        private static string ExtractCharacterModelKey(string path)
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            return Regex.Match(stem ?? string.Empty, @"\d{4}_\d{3}", RegexOptions.CultureInvariant).Value;
        }

        private static string ExtractCharacterId(string path)
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            return Regex.Match(stem ?? string.Empty, @"(?<!\d)\d{4}(?=_)", RegexOptions.CultureInvariant).Value;
        }

        private static Animation GetCharacterBrowserAnimation(AnimationPack pack, CharacterAnimationEntry entry)
        {
            if (pack == null)
                return null;

            return entry.Kind switch
            {
                CharacterAnimationListKind.Animation =>
                    entry.Index >= 0 && entry.Index < (pack.Animations?.Count ?? 0)
                        ? pack.Animations[entry.Index]
                        : null,

                CharacterAnimationListKind.BlendAnimation =>
                    entry.Index >= 0 && entry.Index < (pack.BlendAnimations?.Count ?? 0)
                        ? pack.BlendAnimations[entry.Index]
                        : null,

                CharacterAnimationListKind.ExtraAnimation =>
                    entry.Index >= 0 && entry.Index < (pack.METAPHOR_AnimArray3?.Count ?? 0)
                        ? pack.METAPHOR_AnimArray3[entry.Index]
                        : null,

                _ => null
            };
        }

        private string MakeCharacterBrowserRelativePath(string path)
        {
            return string.IsNullOrWhiteSpace(mCharacterBrowserRoot)
                ? path
                : Path.GetRelativePath(mCharacterBrowserRoot, path);
        }

        private void SetCharacterBrowserStatus(string text)
        {
            if (mCharacterBrowserStatusLabel == null || mCharacterBrowserStatusLabel.IsDisposed)
                return;

            if (mCharacterBrowserStatusLabel.InvokeRequired)
            {
                mCharacterBrowserStatusLabel.BeginInvoke(new Action<string>(SetCharacterBrowserStatus), text);
                return;
            }

            mCharacterBrowserStatusLabel.Text = text;
        }

        private void SaveCharacterBrowserRoot(string root)
        {
            try
            {
                var path = CharacterBrowserSettingsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, root);
            }
            catch
            {
                // Browsing still works if persistence fails.
            }
        }

        private string LoadCharacterBrowserRoot()
        {
            try
            {
                return File.Exists(CharacterBrowserSettingsPath)
                    ? File.ReadAllText(CharacterBrowserSettingsPath).Trim()
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static string TryInferCharacterRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                var directory = File.Exists(path) ? Path.GetDirectoryName(path) : path;
                while (!string.IsNullOrWhiteSpace(directory))
                {
                    if (string.Equals(Path.GetFileName(directory), "character", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(Path.GetFileName(Path.GetDirectoryName(directory)), "model", StringComparison.OrdinalIgnoreCase))
                        return directory;

                    directory = Path.GetDirectoryName(directory);
                }
            }
            catch
            {
                // Ignore malformed paths.
            }

            return null;
        }
    }
}
