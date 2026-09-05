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
using System.Text;

using GFDLibrary;
using GFDLibrary.Animations;
using GFDLibrary.Materials;
using GFDLibrary.Textures;
using GFDStudio.GUI.Controls;

namespace GFDStudio.GUI.Forms
{
    public partial class MainForm
    {
        private sealed class CharacterModelEntry
        {
            public string Path { get; init; }
            public string DisplayName { get; init; }
            public CharacterModelPart Part { get; init; }
            public override string ToString() => DisplayName;
        }

        private enum CharacterModelPart
        {
            Body,
            Face,
            Hair
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
            public IReadOnlyCollection<string> BodyTargetNames { get; init; }
            public override string ToString() => DisplayName;
        }

        private const string CharacterBrowserSelectionFormat = "paths-v1";
        private const string CharacterBrowserNoneSelection = "(none)";

        private sealed class CharacterBrowserAnimationSelection
        {
            public string PackPath { get; init; }
            public CharacterAnimationListKind Kind { get; init; }
            public int Index { get; init; }
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
        private TextBox mCharacterFaceFilterTextBox;
        private TextBox mCharacterHairFilterTextBox;
        private TextBox mCharacterAnimationFilterTextBox;
        private TextBox mCharacterBlendAnimationFilterTextBox;
        private ListBox mCharacterModelListBox;
        private ListBox mCharacterFaceListBox;
        private ListBox mCharacterHairListBox;
        private ListBox mCharacterAnimationListBox;
        private ListBox mCharacterBlendAnimationListBox;
        private Label mCharacterBrowserStatusLabel;
        private ToolStripMenuItem mCharacterBrowserToolStripMenuItem;

        private readonly List<CharacterModelEntry> mCharacterModels = new List<CharacterModelEntry>();
        private readonly List<CharacterAnimationEntry> mCharacterAnimations = new List<CharacterAnimationEntry>();
        private readonly List<CharacterAnimationEntry> mCharacterBlendAnimations = new List<CharacterAnimationEntry>();

        private CancellationTokenSource mCharacterBrowserScanCancellation;
        private string mCharacterBrowserRoot;
        private string mCharacterBrowserCurrentModelPath;
        private ModelPack mCharacterBrowserCurrentModelPack;
        private HashSet<string> mCharacterBrowserCurrentModelNodeNames;
        private int mCharacterBrowserScanGeneration;
        private bool mCharacterBrowserRestoringSelection;
        private bool mCharacterBrowserApplyingSavedSelection;
        private bool mCharacterBrowserRefreshingModelParts;
        private bool mCharacterBrowserSelectionRestoredForScan;
        private string[] mCharacterBrowserSavedSelection;

        private string CharacterBrowserSettingsPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GFDStudio",
                "character_browser_root.txt");

        private string CharacterBrowserSelectionPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GFDStudio",
                "character_browser_selection.txt");

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
                RowCount = 5,
                BackColor = Color.FromArgb(30, 30, 30),
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            mCharacterBrowserPanel.Controls.Add(rootLayout);

            rootLayout.Controls.Add(CreateCharacterBrowserToolbar(), 0, 0);
            rootLayout.Controls.Add(CreateCharacterBrowserModelPartsSection(), 0, 1);
            rootLayout.Controls.Add(CreateCharacterBrowserListSection(
                "ANIMATIONS",
                out mCharacterAnimationFilterTextBox,
                out mCharacterAnimationListBox), 0, 2);
            rootLayout.Controls.Add(CreateCharacterBrowserListSection(
                "BLEND OVERLAYS",
                out mCharacterBlendAnimationFilterTextBox,
                out mCharacterBlendAnimationListBox), 0, 3);

            mCharacterBrowserStatusLabel = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.Silver,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Text = "Choose model\\character root"
            };
            rootLayout.Controls.Add(mCharacterBrowserStatusLabel, 0, 4);

            mCharacterModelFilterTextBox.TextChanged += (s, e) => RefreshCharacterModelList();
            mCharacterFaceFilterTextBox.TextChanged += (s, e) => RefreshCharacterFaceList();
            mCharacterHairFilterTextBox.TextChanged += (s, e) => RefreshCharacterHairList();
            mCharacterAnimationFilterTextBox.TextChanged += (s, e) => RefreshCharacterAnimationList();
            mCharacterBlendAnimationFilterTextBox.TextChanged += (s, e) => RefreshCharacterBlendAnimationList();
            mCharacterModelListBox.SelectedIndexChanged += CharacterModelListBox_SelectedIndexChanged;
            mCharacterFaceListBox.SelectedIndexChanged += CharacterModelListBox_SelectedIndexChanged;
            mCharacterHairListBox.SelectedIndexChanged += CharacterModelListBox_SelectedIndexChanged;
            mCharacterAnimationListBox.SelectedIndexChanged += CharacterAnimationListBox_SelectedIndexChanged;
            mCharacterBlendAnimationListBox.SelectedIndexChanged += CharacterBlendAnimationListBox_SelectedIndexChanged;
            mCharacterAnimationListBox.SelectionMode = SelectionMode.MultiExtended;
            // A blend slot is applied as one overlay at a time. Normal animations
            // remain multi-selectable for repacking.
            mCharacterBlendAnimationListBox.SelectionMode = SelectionMode.One;

            // Keep keyboard browsing completely frictionless: normal Up/Down selection changes
            // immediately load the newly selected model/animation.
            mCharacterModelListBox.KeyDown += CharacterBrowserList_KeyDown;
            mCharacterFaceListBox.KeyDown += CharacterBrowserList_KeyDown;
            mCharacterHairListBox.KeyDown += CharacterBrowserList_KeyDown;
            mCharacterAnimationListBox.KeyDown += CharacterBrowserList_KeyDown;
            mCharacterBlendAnimationListBox.KeyDown += CharacterBrowserList_KeyDown;

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
                ColumnCount = 5,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = Color.FromArgb(30, 30, 30)
            };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
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

            var repackButton = CreateCharacterBrowserButton("Repack");
            repackButton.Click += (s, e) => RepackCharacterAnimations();

            var editorButton = CreateCharacterBrowserButton("Editor");
            editorButton.Click += (s, e) =>
            {
                mCharacterBrowserToolStripMenuItem.Checked = false;
            };

            toolbar.Controls.Add(mCharacterRootTextBox, 0, 0);
            toolbar.Controls.Add(browseButton, 1, 0);
            toolbar.Controls.Add(rescanButton, 2, 0);
            toolbar.Controls.Add(repackButton, 3, 0);
            toolbar.Controls.Add(editorButton, 4, 0);
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

        private Control CreateCharacterBrowserModelPartsSection()
        {
            var parts = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = Color.FromArgb(30, 30, 30)
            };
            parts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
            parts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
            parts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));

            parts.Controls.Add(CreateCharacterBrowserListSection(
                "BODY (or normal persona)",
                out mCharacterModelFilterTextBox,
                out mCharacterModelListBox), 0, 0);
            parts.Controls.Add(CreateCharacterBrowserListSection(
                "FACE",
                out mCharacterFaceFilterTextBox,
                out mCharacterFaceListBox), 1, 0);
            parts.Controls.Add(CreateCharacterBrowserListSection(
                "HAIR",
                out mCharacterHairFilterTextBox,
                out mCharacterHairListBox), 2, 0);
            return parts;
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
            mCharacterBrowserSavedSelection = LoadCharacterBrowserSelection(mCharacterBrowserRoot);
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
            mCharacterBrowserSelectionRestoredForScan = false;

            mCharacterBrowserSavedSelection = LoadCharacterBrowserSelection(root);

            mCharacterBrowserRestoringSelection = true;
            try
            {
                mCharacterModels.Clear();
                mCharacterAnimations.Clear();
                mCharacterBlendAnimations.Clear();
                mCharacterBrowserCurrentModelPath = null;
                mCharacterBrowserCurrentModelPack = null;
                mCharacterBrowserCurrentModelNodeNames = null;
                mCharacterModelListBox.Items.Clear();
                mCharacterFaceListBox.Items.Clear();
                mCharacterHairListBox.Items.Clear();
                mCharacterAnimationListBox.Items.Clear();
                mCharacterBlendAnimationListBox.Items.Clear();
            }
            finally
            {
                mCharacterBrowserRestoringSelection = false;
            }
            SetCharacterBrowserStatus("Scanning files...");

            try
            {
                var priorityModelPaths = GetCharacterBrowserPriorityPaths(root, ".GMD");
                var priorityGapPaths = GetCharacterBrowserPriorityPaths(root, ".GAP");
                var priorityGapSet = new HashSet<string>(priorityGapPaths, StringComparer.OrdinalIgnoreCase);

                // Discover the complete directory in parallel with the saved selection. The
                // selected files are known already, so they can be shown/loaded before a large
                // character directory has finished enumerating.
                var discoveryTask = Task.Run(() =>
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

                AddCharacterBrowserModelEntries(priorityModelPaths);
                RefreshCharacterBrowserModelLists();

                if (IsPathBasedCharacterBrowserSelection())
                    RestoreCharacterBrowserSelection();
                else
                    RestoreLegacyCharacterBrowserModel(priorityModelPaths.FirstOrDefault());

                var animationDefinitions = new CharacterAnimationDefinitionSet();
                var priorityResult = await Task.Run(() =>
                {
                    var output = new List<CharacterAnimationEntry>(128);
                    var failed = 0;
                    var parsed = 0;

                    foreach (var gapPath in priorityGapPaths)
                    {
                        token.ThrowIfCancellationRequested();

                        try
                        {
                            var pack = Resource.Load<AnimationPack>(gapPath);
                            AddAnimationPackEntries(output, root, gapPath, pack, animationDefinitions);
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            Logger.Debug($"CharacterBrowser: failed to index priority GAP {gapPath}: {ex}");
                        }

                        parsed++;
                    }

                    return (Entries: output, Failed: failed, Parsed: parsed);
                }, token);

                if (token.IsCancellationRequested || generation != mCharacterBrowserScanGeneration)
                    return;

                AddCharacterBrowserAnimationEntries(priorityResult.Entries);
                if (IsPathBasedCharacterBrowserSelection())
                    RestoreCharacterBrowserSelection();

                var files = await discoveryTask;

                if (token.IsCancellationRequested || generation != mCharacterBrowserScanGeneration)
                    return;

                mCharacterBrowserRestoringSelection = true;
                try
                {
                    AddCharacterBrowserModelEntries(
                        OrderCharacterBrowserScanPaths(files.models, priorityModelPaths));
                    FinalizeCharacterBrowserModelLists();
                }
                finally
                {
                    mCharacterBrowserRestoringSelection = false;
                }

                if (IsPathBasedCharacterBrowserSelection())
                    RestoreCharacterBrowserSelection();

                SetCharacterBrowserStatus($"{files.models.Count:N0} models; indexing {files.gaps.Count:N0} GAP files...");

                var parsedCount = priorityResult.Parsed;
                var failedCount = priorityResult.Failed;
                var remainingGapPaths = OrderCharacterBrowserScanPaths(files.gaps, priorityGapPaths)
                    .Where(path => !priorityGapSet.Contains(path))
                    .ToList();

                await Task.Run(() =>
                {
                    var batch = new List<CharacterAnimationEntry>(128);

                    foreach (var gapPath in remainingGapPaths)
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

                            Invoke(new Action(() =>
                            {
                                if (token.IsCancellationRequested || generation != mCharacterBrowserScanGeneration)
                                    return;

                                mCharacterBrowserRestoringSelection = true;
                                try
                                {
                                    AddCharacterAnimationBatch(toAdd);
                                }
                                finally
                                {
                                    mCharacterBrowserRestoringSelection = false;
                                }
                                if (parsedSnapshot == files.gaps.Count)
                                    FinalizeCharacterBrowserAnimationLists();

                                var restored = parsedSnapshot == files.gaps.Count &&
                                               RestoreCharacterBrowserSelection();
                                if (!restored)
                                    SetCharacterBrowserStatus(
                                        $"{mCharacterModels.Count:N0} models | " +
                                        $"{mCharacterAnimations.Count + mCharacterBlendAnimations.Count:N0} unique animations | " +
                                        $"GAP {parsedSnapshot:N0}/{files.gaps.Count:N0}" +
                                        (failedSnapshot == 0 ? string.Empty : $" | {failedSnapshot:N0} failed"));
                            }));
                        }
                    }
                }, token);

                if (!token.IsCancellationRequested && generation == mCharacterBrowserScanGeneration)
                {
                    if (remainingGapPaths.Count == 0)
                    {
                        FinalizeCharacterBrowserAnimationLists();
                        mCharacterBrowserSelectionRestoredForScan = RestoreCharacterBrowserSelection();
                    }

                    if (!mCharacterBrowserSelectionRestoredForScan)
                        SetCharacterBrowserStatus(
                            $"Ready: {mCharacterModels.Count:N0} models, " +
                            $"{mCharacterAnimations.Count + mCharacterBlendAnimations.Count:N0} unique animations" +
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

        private void AddCharacterBrowserModelEntries(IEnumerable<string> paths)
        {
            var knownPaths = new HashSet<string>(
                mCharacterModels.Select(entry => entry.Path),
                StringComparer.OrdinalIgnoreCase);

            foreach (var path in paths)
            {
                if (!knownPaths.Add(path))
                    continue;

                mCharacterModels.Add(new CharacterModelEntry
                {
                    Path = path,
                    DisplayName = MakeCharacterBrowserRelativePath(path),
                    Part = ClassifyCharacterModel(path)
                });
            }
        }

        private void AddCharacterBrowserAnimationEntries(IEnumerable<CharacterAnimationEntry> entries)
        {
            mCharacterBrowserRestoringSelection = true;
            try
            {
                AddCharacterAnimationBatch(entries);
            }
            finally
            {
                mCharacterBrowserRestoringSelection = false;
            }
        }

        private void RefreshCharacterBrowserModelLists()
        {
            var wasRestoringSelection = mCharacterBrowserRestoringSelection;
            mCharacterBrowserRestoringSelection = true;
            try
            {
                RefreshCharacterModelList();
                RefreshCharacterFaceList();
                RefreshCharacterHairList();
            }
            finally
            {
                mCharacterBrowserRestoringSelection = wasRestoringSelection;
            }
        }

        private void FinalizeCharacterBrowserModelLists()
        {
            mCharacterModels.Sort((first, second) =>
                StringComparer.OrdinalIgnoreCase.Compare(first.Path, second.Path));
            RefreshCharacterBrowserModelLists();
        }

        private void FinalizeCharacterBrowserAnimationLists()
        {
            // Entries are inserted in display order as scan batches arrive, so finalization
            // should not rebuild the list and cause a visible reset.
            mCharacterAnimations.Sort(CompareCharacterBrowserAnimationEntries);
            mCharacterBlendAnimations.Sort(CompareCharacterBrowserBlendAnimationEntries);
        }

        private static int CompareCharacterBrowserAnimationEntries(
            CharacterAnimationEntry first,
            CharacterAnimationEntry second)
        {
            var result = StringComparer.OrdinalIgnoreCase.Compare(first.PackPath, second.PackPath);
            if (result != 0)
                return result;

            result = first.Kind.CompareTo(second.Kind);
            return result != 0 ? result : first.Index.CompareTo(second.Index);
        }

        private static int CompareCharacterBrowserBlendAnimationEntries(
            CharacterAnimationEntry first,
            CharacterAnimationEntry second)
        {
            var result = StringComparer.OrdinalIgnoreCase.Compare(first.PackPath, second.PackPath);
            return result != 0 ? result : first.Index.CompareTo(second.Index);
        }

        private List<string> GetCharacterBrowserPriorityPaths(string root, string extension)
        {
            var priorityPaths = new List<string>();

            if (IsPathBasedCharacterBrowserSelection())
            {
                if (string.Equals(extension, ".GMD", StringComparison.OrdinalIgnoreCase))
                {
                    AddCharacterBrowserPriorityPath(priorityPaths, root, mCharacterBrowserSavedSelection[2], extension);
                    AddCharacterBrowserPriorityPath(priorityPaths, root, mCharacterBrowserSavedSelection[3], extension);
                    AddCharacterBrowserPriorityPath(priorityPaths, root, mCharacterBrowserSavedSelection[4], extension);
                }
                else if (string.Equals(extension, ".GAP", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var selection in GetSavedCharacterBrowserAnimationSelections())
                        AddCharacterBrowserPriorityPath(priorityPaths, root, selection.PackPath, extension);

                    if (TryGetSavedCharacterBrowserBlendSelection(out var blendSelection))
                        AddCharacterBrowserPriorityPath(priorityPaths, root, blendSelection.PackPath, extension);
                }
            }

            // Older selection files only stored list indexes. The most recently opened model
            // still gives the first boot a useful anchor; future saves use stable paths.
            if (priorityPaths.Count == 0 && string.Equals(extension, ".GMD", StringComparison.OrdinalIgnoreCase))
            {
                var lastOpenedPath = GetLastCharacterBrowserFilePath();
                AddCharacterBrowserPriorityPath(priorityPaths, root, lastOpenedPath, extension);
            }

            return priorityPaths;
        }

        private string GetLastCharacterBrowserFilePath()
        {
            if (!string.IsNullOrWhiteSpace(LastOpenedFilePath))
                return LastOpenedFilePath;

            return mFileHistoryList != null && mFileHistoryList.Count > 0
                ? mFileHistoryList.Last
                : null;
        }

        private static void AddCharacterBrowserPriorityPath(
            ICollection<string> paths,
            string root,
            string path,
            string extension)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                !string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                var fullPath = Path.GetFullPath(path);
                var relativePath = Path.GetRelativePath(root, fullPath);
                if (relativePath == ".." || relativePath.StartsWith(".." + Path.DirectorySeparatorChar) ||
                    relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar) ||
                    Path.IsPathRooted(relativePath) || !File.Exists(fullPath))
                    return;

                if (!paths.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                    paths.Add(fullPath);
            }
            catch
            {
                // A stale selection should not prevent the rest of the directory from loading.
            }
        }

        private static List<string> OrderCharacterBrowserScanPaths(
            IEnumerable<string> paths,
            IEnumerable<string> priorityPaths)
        {
            var orderedPaths = paths
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var priority = priorityPaths
                .Where(path => orderedPaths.Any(candidate => AreSamePath(candidate, path)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (priority.Count == 0)
                return orderedPaths;

            var result = new List<string>(orderedPaths.Count);
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string path)
            {
                if (added.Add(path))
                    result.Add(path);
            }

            foreach (var path in priority)
                Add(path);

            var anchor = orderedPaths.FindIndex(path => AreSamePath(path, priority[0]));
            for (var offset = 1; result.Count < orderedPaths.Count; offset++)
            {
                var before = anchor - offset;
                var after = anchor + offset;
                if (before >= 0)
                    Add(orderedPaths[before]);
                if (after < orderedPaths.Count)
                    Add(orderedPaths[after]);
            }

            return result;
        }

        private void RestoreLegacyCharacterBrowserModel(string priorityModelPath)
        {
            if (string.IsNullOrWhiteSpace(priorityModelPath))
                return;

            var modelIndex = FindCharacterBrowserPathIndex(mCharacterModelListBox, priorityModelPath);
            if (modelIndex < 0)
                return;

            mCharacterBrowserRestoringSelection = true;
            try
            {
                mCharacterModelListBox.SelectedIndex = modelIndex;
            }
            finally
            {
                mCharacterBrowserRestoringSelection = false;
            }

            mCharacterBrowserApplyingSavedSelection = true;
            try
            {
                CharacterModelListBox_SelectedIndexChanged(mCharacterModelListBox, EventArgs.Empty);
            }
            finally
            {
                mCharacterBrowserApplyingSavedSelection = false;
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

            var normalCount = pack.Animations?.Count(AnimationAnalysis.HasBodyMotion) ?? 0;
            var extraCount = pack.METAPHOR_AnimArray3?.Count(AnimationAnalysis.HasBodyMotion) ?? 0;
            var normalAndExtraCount = normalCount + extraCount;

            if (pack.Animations != null)
            {
                for (var i = 0; i < pack.Animations.Count; i++)
                {
                    var animation = pack.Animations[i];
                    if (!AnimationAnalysis.HasBodyMotion(animation) ||
                        !animationDefinitions.Add(animation))
                        continue;

                    output.Add(new CharacterAnimationEntry
                    {
                        PackPath = gapPath,
                        Kind = CharacterAnimationListKind.Animation,
                        Index = i,
                        DisplayName = normalAndExtraCount == 1 ? stem : $"{stem}  #{i + 1}",
                        BodyTargetNames = AnimationAnalysis.GetBodyTargetNames(animation)
                    });
                }
            }

            if (pack.BlendAnimations != null)
            {
                for (var i = 0; i < pack.BlendAnimations.Count; i++)
                {
                    var animation = pack.BlendAnimations[i];
                    if (!AnimationAnalysis.HasBodyMotion(animation) ||
                        !animationDefinitions.Add(animation))
                        continue;

                    output.Add(new CharacterAnimationEntry
                    {
                        PackPath = gapPath,
                        Kind = CharacterAnimationListKind.BlendAnimation,
                        Index = i,
                        DisplayName = $"{stem}  [blend {i + 1}]",
                        BodyTargetNames = AnimationAnalysis.GetBodyTargetNames(animation)
                    });
                }
            }

            if (pack.METAPHOR_AnimArray3 != null)
            {
                for (var i = 0; i < pack.METAPHOR_AnimArray3.Count; i++)
                {
                    var animation = pack.METAPHOR_AnimArray3[i];
                    if (!AnimationAnalysis.HasBodyMotion(animation) ||
                        !animationDefinitions.Add(animation))
                        continue;

                    output.Add(new CharacterAnimationEntry
                    {
                        PackPath = gapPath,
                        Kind = CharacterAnimationListKind.ExtraAnimation,
                        Index = i,
                        DisplayName = $"{stem}  [extra {i + 1}]",
                        BodyTargetNames = AnimationAnalysis.GetBodyTargetNames(animation)
                    });
                }
            }
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
            var blendFilter = mCharacterBlendAnimationFilterTextBox.Text?.Trim();
            var addDirectly = string.IsNullOrEmpty(filter);
            var addBlendDirectly = string.IsNullOrEmpty(blendFilter);

            mCharacterAnimationListBox.BeginUpdate();
            mCharacterBlendAnimationListBox.BeginUpdate();
            try
            {
                foreach (var entry in entries)
                {
                    var destination = entry.Kind == CharacterAnimationListKind.BlendAnimation
                        ? mCharacterBlendAnimations
                        : mCharacterAnimations;
                    var destinationListBox = entry.Kind == CharacterAnimationListKind.BlendAnimation
                        ? mCharacterBlendAnimationListBox
                        : mCharacterAnimationListBox;
                    var destinationFilter = entry.Kind == CharacterAnimationListKind.BlendAnimation
                        ? blendFilter
                        : filter;
                    var destinationAddDirectly = entry.Kind == CharacterAnimationListKind.BlendAnimation
                        ? addBlendDirectly
                        : addDirectly;
                    Comparison<CharacterAnimationEntry> comparer = entry.Kind == CharacterAnimationListKind.BlendAnimation
                        ? CompareCharacterBrowserBlendAnimationEntries
                        : CompareCharacterBrowserAnimationEntries;

                    var destinationIndex = FindCharacterBrowserAnimationInsertIndex(
                        destination,
                        entry,
                        comparer);
                    destination.Insert(destinationIndex, entry);
                    if (IsCharacterBrowserAnimationForSelectedBody(entry) &&
                        (destinationAddDirectly || CharacterBrowserMatches(entry.DisplayName, destinationFilter)))
                    {
                        var listBoxIndex = FindCharacterBrowserAnimationInsertIndex(
                            destinationListBox,
                            entry,
                            comparer);
                        destinationListBox.Items.Insert(listBoxIndex, entry);
                    }
                }
            }
            finally
            {
                mCharacterAnimationListBox.EndUpdate();
                mCharacterBlendAnimationListBox.EndUpdate();
            }
        }

        private static int FindCharacterBrowserAnimationInsertIndex(
            IList<CharacterAnimationEntry> entries,
            CharacterAnimationEntry entry,
            Comparison<CharacterAnimationEntry> comparer)
        {
            var lower = 0;
            var upper = entries.Count;
            while (lower < upper)
            {
                var middle = lower + ((upper - lower) / 2);
                if (comparer(entries[middle], entry) <= 0)
                    lower = middle + 1;
                else
                    upper = middle;
            }

            return lower;
        }

        private static int FindCharacterBrowserAnimationInsertIndex(
            ListBox listBox,
            CharacterAnimationEntry entry,
            Comparison<CharacterAnimationEntry> comparer)
        {
            for (var index = 0; index < listBox.Items.Count; index++)
            {
                if (listBox.Items[index] is CharacterAnimationEntry existing &&
                    comparer(existing, entry) > 0)
                    return index;
            }

            return listBox.Items.Count;
        }

        private void RefreshCharacterModelList()
        {
            if (mCharacterModelListBox == null)
                return;

            var filter = mCharacterModelFilterTextBox.Text?.Trim();
            var selectedPath = (mCharacterModelListBox.SelectedItem as CharacterModelEntry)?.Path;
            mCharacterModelListBox.BeginUpdate();
            try
            {
                mCharacterModelListBox.Items.Clear();
                foreach (var entry in mCharacterModels.Where(entry => entry.Part == CharacterModelPart.Body))
                {
                    if (CharacterBrowserMatches(entry.DisplayName, filter))
                        mCharacterModelListBox.Items.Add(entry);
                }
                mCharacterModelListBox.Items.Add(CreateCharacterModelNoneEntry(CharacterModelPart.Body));

                if (!string.IsNullOrWhiteSpace(selectedPath))
                {
                    var selectedIndex = FindCharacterBrowserPathIndex(mCharacterModelListBox, selectedPath);
                    if (selectedIndex >= 0)
                        mCharacterModelListBox.SelectedIndex = selectedIndex;
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
                    if (IsCharacterBrowserAnimationForSelectedBody(entry) &&
                        CharacterBrowserMatches(entry.DisplayName, filter))
                        mCharacterAnimationListBox.Items.Add(entry);
                }
            }
            finally
            {
                mCharacterAnimationListBox.EndUpdate();
            }
        }

        private void RefreshCharacterFaceList()
        {
            RefreshCharacterModelPartList(
                mCharacterFaceListBox,
                mCharacterFaceFilterTextBox,
                CharacterModelPart.Face);
        }

        private void RefreshCharacterHairList()
        {
            RefreshCharacterModelPartList(
                mCharacterHairListBox,
                mCharacterHairFilterTextBox,
                CharacterModelPart.Hair);
        }

        private void RefreshCharacterModelPartList(
            ListBox listBox,
            TextBox filterTextBox,
            CharacterModelPart part)
        {
            if (listBox == null)
                return;

            var filter = filterTextBox.Text?.Trim();
            var bodyCharacterId = GetSelectedCharacterBodyId();
            var selectedPath = (listBox.SelectedItem as CharacterModelEntry)?.Path;
            listBox.BeginUpdate();
            try
            {
                listBox.Items.Clear();
                foreach (var entry in mCharacterModels.Where(entry => entry.Part == part)
                    .Where(entry => string.IsNullOrWhiteSpace(bodyCharacterId) ||
                                    string.Equals(ExtractCharacterId(entry.Path), bodyCharacterId,
                                                  StringComparison.OrdinalIgnoreCase)))
                {
                    if (CharacterBrowserMatches(entry.DisplayName, filter))
                        listBox.Items.Add(entry);
                }

                listBox.Items.Add(CreateCharacterModelNoneEntry(part));

                var restoredSelection = false;
                if (!string.IsNullOrWhiteSpace(selectedPath))
                {
                    for (var i = 0; i < listBox.Items.Count; i++)
                    {
                        if (string.Equals((listBox.Items[i] as CharacterModelEntry)?.Path,
                                          selectedPath, StringComparison.OrdinalIgnoreCase))
                        {
                            listBox.SelectedIndex = i;
                            restoredSelection = true;
                            break;
                        }
                    }
                }

                if (!restoredSelection)
                {
                    // Prefer the first compatible part when the body changes.
                    // Keep (none) as the fallback only when no matching model
                    // survived the filter.
                    listBox.SelectedIndex = listBox.Items.Count > 1
                        ? 0
                        : listBox.Items.Count - 1;
                }
            }
            finally
            {
                listBox.EndUpdate();
            }
        }

        private void RefreshCharacterBlendAnimationList()
        {
            if (mCharacterBlendAnimationListBox == null)
                return;

            var filter = mCharacterBlendAnimationFilterTextBox.Text?.Trim();
            mCharacterBlendAnimationListBox.BeginUpdate();
            try
            {
                mCharacterBlendAnimationListBox.Items.Clear();
                foreach (var entry in mCharacterBlendAnimations)
                {
                    if (IsCharacterBrowserAnimationForSelectedBody(entry) &&
                        CharacterBrowserMatches(entry.DisplayName, filter))
                        mCharacterBlendAnimationListBox.Items.Add(entry);
                }
            }
            finally
            {
                mCharacterBlendAnimationListBox.EndUpdate();
            }
        }

        private static bool CharacterBrowserMatches(string value, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
                return true;

            if (filter.Length >= 2 && filter[0] == '/' && filter[^1] == '/')
            {
                var pattern = filter.Substring(1, filter.Length - 2);
                try
                {
                    return Regex.IsMatch(
                        value ?? string.Empty,
                        pattern,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(100));
                }
                catch (ArgumentException)
                {
                    // Keep an invalid filter from breaking list refreshes.
                    return false;
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }
            }

            return (value ?? string.Empty).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool IsCharacterBrowserAnimationForSelectedBody(CharacterAnimationEntry entry)
        {
            if (mCharacterBrowserCurrentModelNodeNames == null)
                return true;

            return entry?.BodyTargetNames?.Any(
                targetName => mCharacterBrowserCurrentModelNodeNames.Contains(targetName)) == true;
        }

        private string GetSelectedCharacterBodyId()
        {
            var selectedBody = mCharacterModelListBox?.SelectedItem as CharacterModelEntry;
            return selectedBody == null || string.IsNullOrWhiteSpace(selectedBody.Path)
                ? null
                : ExtractCharacterId(selectedBody.Path);
        }

        private static CharacterModelEntry CreateCharacterModelNoneEntry(CharacterModelPart part)
        {
            return new CharacterModelEntry
            {
                Part = part,
                DisplayName = "(none)"
            };
        }

        private bool RestoreCharacterBrowserSelection()
        {
            if (mCharacterBrowserSavedSelection == null || mCharacterBrowserSavedSelection.Length < 4)
                return false;

            if (IsPathBasedCharacterBrowserSelection())
                return RestorePathBasedCharacterBrowserSelection();

            var modelIndex = ParseCharacterBrowserSelectionIndex(mCharacterBrowserSavedSelection[1]);
            var faceIndex = mCharacterBrowserSavedSelection.Length >= 6
                ? ParseCharacterBrowserSelectionIndex(mCharacterBrowserSavedSelection[2])
                : -1;
            var hairIndex = mCharacterBrowserSavedSelection.Length >= 6
                ? ParseCharacterBrowserSelectionIndex(mCharacterBrowserSavedSelection[3])
                : -1;
            var animationSelection = mCharacterBrowserSavedSelection.Length >= 6
                ? mCharacterBrowserSavedSelection[4]
                : mCharacterBrowserSavedSelection[2];
            var blendIndex = ParseCharacterBrowserSelectionIndex(
                mCharacterBrowserSavedSelection.Length >= 6
                    ? mCharacterBrowserSavedSelection[5]
                    : mCharacterBrowserSavedSelection[3]);
            var animationIndexes = animationSelection
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseCharacterBrowserSelectionIndex)
                .Where(index => index >= 0 && index < mCharacterAnimationListBox.Items.Count)
                .Distinct()
                .ToList();

            if (modelIndex < 0 || modelIndex >= mCharacterModelListBox.Items.Count)
                modelIndex = -1;
            if (faceIndex < 0 || faceIndex >= mCharacterFaceListBox.Items.Count)
                faceIndex = -1;
            if (hairIndex < 0 || hairIndex >= mCharacterHairListBox.Items.Count)
                hairIndex = -1;
            if (blendIndex < 0 || blendIndex >= mCharacterBlendAnimationListBox.Items.Count)
                blendIndex = -1;

            if (modelIndex < 0 && faceIndex < 0 && hairIndex < 0 &&
                animationIndexes.Count == 0 && blendIndex < 0)
                return false;

            mCharacterBrowserRestoringSelection = true;
            try
            {
                if (modelIndex >= 0)
                    mCharacterModelListBox.SelectedIndex = modelIndex;
                if (faceIndex >= 0)
                    mCharacterFaceListBox.SelectedIndex = faceIndex;
                if (hairIndex >= 0)
                    mCharacterHairListBox.SelectedIndex = hairIndex;

                mCharacterAnimationListBox.ClearSelected();
                foreach (var index in animationIndexes)
                    mCharacterAnimationListBox.SetSelected(index, true);

                if (blendIndex >= 0)
                    mCharacterBlendAnimationListBox.SelectedIndex = blendIndex;
            }
            finally
            {
                mCharacterBrowserRestoringSelection = false;
            }

            mCharacterBrowserSelectionRestoredForScan = true;
            if (modelIndex >= 0 || faceIndex >= 0 || hairIndex >= 0)
                CharacterModelListBox_SelectedIndexChanged(mCharacterModelListBox, EventArgs.Empty);
            else if (animationIndexes.Count > 0)
                CharacterAnimationListBox_SelectedIndexChanged(mCharacterAnimationListBox, EventArgs.Empty);

            return true;
        }

        private bool RestorePathBasedCharacterBrowserSelection()
        {
            var modelPath = GetSavedCharacterBrowserPath(2);
            var facePath = GetSavedCharacterBrowserPath(3);
            var hairPath = GetSavedCharacterBrowserPath(4);
            var animationSelections = GetSavedCharacterBrowserAnimationSelections();
            var hasBlendSelection = TryGetSavedCharacterBrowserBlendSelection(out var blendSelection);

            var modelIndex = FindCharacterBrowserPathIndex(mCharacterModelListBox, modelPath);
            var faceIndex = FindCharacterBrowserPathIndex(mCharacterFaceListBox, facePath);
            var hairIndex = FindCharacterBrowserPathIndex(mCharacterHairListBox, hairPath);
            var animationIndexes = animationSelections
                .Select(selection => FindCharacterBrowserAnimationIndex(
                    mCharacterAnimationListBox, selection))
                .Where(index => index >= 0)
                .Distinct()
                .ToList();
            var blendIndex = hasBlendSelection
                ? FindCharacterBrowserAnimationIndex(mCharacterBlendAnimationListBox, blendSelection)
                : -1;
            var allAnimationsRestored = animationIndexes.Count == animationSelections.Count;
            var currentAnimationIndexes = mCharacterAnimationListBox.SelectedIndices
                .Cast<int>()
                .OrderBy(index => index)
                .ToList();
            var targetAnimationIndexes = animationIndexes
                .OrderBy(index => index)
                .ToList();
            var animationSelectionChanged = allAnimationsRestored &&
                                            !currentAnimationIndexes.SequenceEqual(targetAnimationIndexes);
            var blendSelectionChanged = blendIndex >= 0 &&
                                        mCharacterBlendAnimationListBox.SelectedIndex != blendIndex;

            var hasSavedSelection = !string.IsNullOrWhiteSpace(modelPath) ||
                                    !string.IsNullOrWhiteSpace(facePath) ||
                                    !string.IsNullOrWhiteSpace(hairPath) ||
                                    animationSelections.Count > 0 || hasBlendSelection;
            if (!hasSavedSelection)
                return false;

            mCharacterBrowserRestoringSelection = true;
            try
            {
                if (modelIndex >= 0)
                    mCharacterModelListBox.SelectedIndex = modelIndex;
                if (faceIndex >= 0)
                    mCharacterFaceListBox.SelectedIndex = faceIndex;
                if (hairIndex >= 0)
                    mCharacterHairListBox.SelectedIndex = hairIndex;

                // Animation entries arrive in batches while the scan is running. Do not replace
                // the current selection with a partial saved selection, and do not clear/reapply
                // an already-restored selection on every scan phase.
                if (animationSelectionChanged)
                {
                    mCharacterAnimationListBox.ClearSelected();
                    foreach (var index in animationIndexes)
                        mCharacterAnimationListBox.SetSelected(index, true);
                }

                if (blendSelectionChanged)
                    mCharacterBlendAnimationListBox.SelectedIndex = blendIndex;
            }
            finally
            {
                mCharacterBrowserRestoringSelection = false;
            }

            // Keep the saved path set intact while the early model/animation load calls save
            // their current UI state. This allows the selected model to load before the rest of
            // the scan has discovered every animation.
            mCharacterBrowserApplyingSavedSelection = true;
            try
            {
                var selectedPrimaryPath = (mCharacterModelListBox.SelectedItem as CharacterModelEntry)?.Path ??
                                          (mCharacterFaceListBox.SelectedItem as CharacterModelEntry)?.Path ??
                                          (mCharacterHairListBox.SelectedItem as CharacterModelEntry)?.Path;
                var modelNeedsLoading = (modelIndex >= 0 || faceIndex >= 0 || hairIndex >= 0) &&
                                        (mCharacterBrowserCurrentModelPack == null ||
                                         !AreSamePath(mCharacterBrowserCurrentModelPath, selectedPrimaryPath));
                if (modelNeedsLoading)
                {
                    var modelSender = modelIndex >= 0
                        ? mCharacterModelListBox
                        : faceIndex >= 0 ? mCharacterFaceListBox : mCharacterHairListBox;
                    CharacterModelListBox_SelectedIndexChanged(modelSender, EventArgs.Empty);
                }
                else if (animationSelectionChanged)
                    CharacterAnimationListBox_SelectedIndexChanged(mCharacterAnimationListBox, EventArgs.Empty);
            }
            finally
            {
                mCharacterBrowserApplyingSavedSelection = false;
            }

            var allModelsRestored = string.IsNullOrWhiteSpace(modelPath) || modelIndex >= 0;
            allModelsRestored &= string.IsNullOrWhiteSpace(facePath) || faceIndex >= 0;
            allModelsRestored &= string.IsNullOrWhiteSpace(hairPath) || hairIndex >= 0;
            var allSelectionsRestored = allModelsRestored && allAnimationsRestored &&
                                        (!hasBlendSelection || blendIndex >= 0);
            mCharacterBrowserSelectionRestoredForScan = allSelectionsRestored;
            return allSelectionsRestored;
        }

        private bool IsPathBasedCharacterBrowserSelection()
        {
            return mCharacterBrowserSavedSelection?.Length >= 7 &&
                   string.Equals(mCharacterBrowserSavedSelection[1],
                                 CharacterBrowserSelectionFormat,
                                 StringComparison.Ordinal);
        }

        private string GetSavedCharacterBrowserPath(int index)
        {
            return IsPathBasedCharacterBrowserSelection() &&
                   index >= 0 && index < mCharacterBrowserSavedSelection.Length
                ? mCharacterBrowserSavedSelection[index]
                : null;
        }

        private List<CharacterBrowserAnimationSelection> GetSavedCharacterBrowserAnimationSelections()
        {
            if (!IsPathBasedCharacterBrowserSelection())
                return new List<CharacterBrowserAnimationSelection>();

            return mCharacterBrowserSavedSelection[5]
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseCharacterBrowserAnimationSelection)
                .Where(selection => selection != null)
                .ToList();
        }

        private bool TryGetSavedCharacterBrowserBlendSelection(
            out CharacterBrowserAnimationSelection selection)
        {
            selection = null;
            if (!IsPathBasedCharacterBrowserSelection() ||
                string.IsNullOrWhiteSpace(mCharacterBrowserSavedSelection[6]))
                return false;

            selection = ParseCharacterBrowserAnimationSelection(mCharacterBrowserSavedSelection[6]);
            return selection != null;
        }

        private static CharacterBrowserAnimationSelection ParseCharacterBrowserAnimationSelection(
            string value)
        {
            try
            {
                var parts = value.Split('|');
                if (parts.Length != 3 || !int.TryParse(parts[1], out var index) ||
                    !Enum.TryParse(parts[0], out CharacterAnimationListKind kind))
                    return null;

                return new CharacterBrowserAnimationSelection
                {
                    Kind = kind,
                    Index = index,
                    PackPath = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]))
                };
            }
            catch
            {
                return null;
            }
        }

        private static string SerializeCharacterBrowserAnimationSelection(
            CharacterAnimationEntry entry)
        {
            return string.Join("|",
                entry.Kind,
                entry.Index,
                Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.PackPath ?? string.Empty)));
        }

        private static string SerializeCharacterBrowserModelSelection(CharacterModelEntry entry)
        {
            return entry == null
                ? string.Empty
                : entry.Path ?? CharacterBrowserNoneSelection;
        }

        private static int FindCharacterBrowserPathIndex(ListBox listBox, string path)
        {
            if (listBox == null || string.IsNullOrWhiteSpace(path))
                return -1;

            for (var i = 0; i < listBox.Items.Count; i++)
            {
                if (string.Equals(path, CharacterBrowserNoneSelection, StringComparison.Ordinal) &&
                    listBox.Items[i] is CharacterModelEntry noneEntry &&
                    string.IsNullOrWhiteSpace(noneEntry.Path))
                    return i;

                if (AreSamePath((listBox.Items[i] as CharacterModelEntry)?.Path, path))
                    return i;
            }

            return -1;
        }

        private static int FindCharacterBrowserAnimationIndex(
            ListBox listBox,
            CharacterBrowserAnimationSelection selection)
        {
            if (listBox == null || selection == null)
                return -1;

            for (var i = 0; i < listBox.Items.Count; i++)
            {
                if (listBox.Items[i] is CharacterAnimationEntry entry &&
                    entry.Kind == selection.Kind && entry.Index == selection.Index &&
                    AreSamePath(entry.PackPath, selection.PackPath))
                    return i;
            }

            return -1;
        }

        private static int ParseCharacterBrowserSelectionIndex(string value)
        {
            return int.TryParse(value, out var index) ? index : -1;
        }

        private void CharacterModelListBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (mCharacterBrowserRestoringSelection)
                return;

            try
            {
                if (ReferenceEquals(sender, mCharacterModelListBox))
                {
                    mCharacterBrowserRefreshingModelParts = true;
                    try
                    {
                        RefreshCharacterFaceList();
                        RefreshCharacterHairList();
                    }
                    finally
                    {
                        mCharacterBrowserRefreshingModelParts = false;
                    }
                }

                if (mCharacterBrowserRefreshingModelParts)
                    return;

                LoadSelectedCharacterModelParts();
            }
            catch (Exception ex)
            {
                SetCharacterBrowserStatus("Model load failed: " + ex.Message);
            }
        }

        private void LoadSelectedCharacterModelParts()
        {
            var selectedParts = GetSelectedCharacterModelParts();
            SaveCharacterBrowserSelectionSettings();

            if (selectedParts.Count == 0)
            {
                mCharacterBrowserCurrentModelPath = null;
                mCharacterBrowserCurrentModelPack = null;
                mCharacterBrowserCurrentModelNodeNames = null;
                RefreshCharacterAnimationList();
                RefreshCharacterBlendAnimationList();
                SetCharacterBrowserStatus("Select a body, face, or hair model");
                return;
            }

            var primary = selectedParts.FirstOrDefault(part => part.Part == CharacterModelPart.Body) ??
                          selectedParts[0];
            mCharacterBrowserCurrentModelPath = primary.Path;

            // Keep the editor/tree in sync with the primary file, then replace
            // its preview with the composed character shown by the browser.
            OpenFile(primary.Path);
            var modelPack = ComposeCharacterModelPack(selectedParts);
            mCharacterBrowserCurrentModelPack = modelPack;
            mCharacterBrowserCurrentModelNodeNames = new HashSet<string>(
                modelPack.Model.Nodes.Select(node => node.Name),
                StringComparer.OrdinalIgnoreCase);
            RefreshCharacterAnimationList();
            RefreshCharacterBlendAnimationList();
            ModelViewControl.Instance.LoadModel(modelPack);

            var animationEntry = mCharacterAnimationListBox.SelectedItem as CharacterAnimationEntry;
            if (animationEntry != null)
            {
                var animation = PrepareCharacterBrowserAnimation(animationEntry, out _);
                if (animation != null)
                {
                    ModelViewControl.Instance.LoadAnimation(animation, true);
                    ApplySelectedCharacterBrowserBlend();
                }
            }

            var selectedNames = string.Join(" + ", selectedParts.Select(part => part.Part.ToString().ToLowerInvariant()));
            SetCharacterBrowserStatus($"Character: {selectedNames}");
        }

        private List<CharacterModelEntry> GetSelectedCharacterModelParts()
        {
            return new[]
            {
                mCharacterModelListBox.SelectedItem as CharacterModelEntry,
                mCharacterFaceListBox.SelectedItem as CharacterModelEntry,
                mCharacterHairListBox.SelectedItem as CharacterModelEntry
            }
            .Where(entry => entry != null)
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
            .ToList();
        }

        private static ModelPack ComposeCharacterModelPack(IReadOnlyList<CharacterModelEntry> selectedParts)
        {
            var loadedParts = selectedParts
                .Select(part => Resource.Load<ModelPack>(part.Path))
                .Where(pack => pack?.Model != null)
                .ToList();
            if (loadedParts.Count == 0)
                throw new InvalidDataException("The selected files do not contain model data.");

            var composed = loadedParts[0];
            composed.Textures ??= new TextureDictionary(composed.Version);
            composed.Materials ??= new MaterialDictionary(composed.Version);

            for (var i = 1; i < loadedParts.Count; i++)
            {
                var part = loadedParts[i];
                if (part.Textures != null)
                {
                    foreach (var texture in part.Textures)
                    {
                        if (!composed.Textures.ContainsKey(texture.Key))
                            composed.Textures.Add(texture.Key, texture.Value);
                    }
                }

                if (part.Materials != null)
                {
                    foreach (var material in part.Materials)
                    {
                        if (!composed.Materials.ContainsKey(material.Key))
                            composed.Materials.Add(material.Key, material.Value);
                    }
                }

                composed.Model.MergeWith(part.Model);
            }

            return composed;
        }

        private void CharacterBlendAnimationListBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (mCharacterBrowserRestoringSelection)
                return;

            SaveCharacterBrowserSelectionSettings();
            ApplySelectedCharacterBrowserBlend();
        }

        private void ApplySelectedCharacterBrowserBlend()
        {
            if (mCharacterBlendAnimationListBox.SelectedItem is not CharacterAnimationEntry entry)
            {
                ModelViewControl.Instance.UnloadAnimationOverlay();
                return;
            }

            if (!ModelViewControl.Instance.IsAnimationLoaded)
            {
                ModelViewControl.Instance.UnloadAnimationOverlay();
                SetCharacterBrowserStatus("Select a base animation before adding a blend overlay");
                return;
            }

            try
            {
                ModelViewControl.Instance.UnloadAnimationOverlay();
                var animation = PrepareCharacterBrowserAnimation(entry, out var retargetNote);
                if (animation == null)
                {
                    SetCharacterBrowserStatus("Blend overlay no longer exists in pack: " + entry.DisplayName);
                    return;
                }

                ModelViewControl.Instance.LoadAnimationOverlay(animation);
                SetCharacterBrowserStatus(
                    string.IsNullOrWhiteSpace(retargetNote)
                        ? "Blend overlay: " + entry.DisplayName
                        : $"Blend overlay: {entry.DisplayName} ({retargetNote})");
            }
            catch (Exception ex)
            {
                SetCharacterBrowserStatus("Blend overlay load failed: " + ex.Message);
            }
        }

        private void CharacterAnimationListBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (mCharacterBrowserRestoringSelection)
                return;

            SaveCharacterBrowserSelectionSettings();
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
                ApplySelectedCharacterBrowserBlend();
                SetCharacterBrowserStatus(
                    string.IsNullOrWhiteSpace(retargetNote)
                        ? "Animation: " + entry.DisplayName
                        : $"Animation: {entry.DisplayName} ({retargetNote})");
                SaveCharacterBrowserSelectionSettings();
            }
            catch (Exception ex)
            {
                SetCharacterBrowserStatus("Animation load failed: " + ex.Message);
            }
        }

        private void RepackCharacterAnimations()
        {
            var entries = mCharacterAnimationListBox.SelectedItems
                .Cast<CharacterAnimationEntry>()
                .Concat(mCharacterBlendAnimationListBox.SelectedItems.Cast<CharacterAnimationEntry>())
                .ToList();

            if (entries.Count == 0)
            {
                SetCharacterBrowserStatus("Select one or more animations to repack");
                return;
            }

            using var dialog = new SaveFileDialog
            {
                Filter = "Animation pack (*.GAP)|*.GAP|All files (*.*)|*.*",
                DefaultExt = "GAP",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = "repacked.GAP",
                Title = "Save repacked animation pack"
            };

            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            try
            {
                var output = CreateCharacterBrowserAnimationPack(entries);
                output.Save(dialog.FileName);
                SetCharacterBrowserStatus($"Repacked {entries.Count:N0} animations: {dialog.FileName}");
            }
            catch (Exception ex)
            {
                SetCharacterBrowserStatus("Repack failed: " + ex.Message);
            }
        }

        private static AnimationPack CreateCharacterBrowserAnimationPack(
            IReadOnlyList<CharacterAnimationEntry> entries)
        {
            var sourcePacks = new Dictionary<string, AnimationPack>(StringComparer.OrdinalIgnoreCase);
            var selectedAnimations = new List<(CharacterAnimationEntry Entry, AnimationPack Pack, Animation Animation)>();

            foreach (var entry in entries)
            {
                var source = LoadCharacterBrowserSourcePack(entry, sourcePacks);
                var animation = GetCharacterBrowserAnimation(source, entry);
                if (animation == null)
                    throw new InvalidDataException("Animation no longer exists in pack: " + entry.DisplayName);

                selectedAnimations.Add((entry, source, animation));
            }

            var targetVersion = selectedAnimations.Max(item => item.Pack.Version);
            var targetIsV2 = ResourceVersion.IsV2(targetVersion);
            if (selectedAnimations.Any(item => ResourceVersion.IsV2(item.Pack.Version) != targetIsV2))
            {
                throw new InvalidDataException(
                    "Selected animations use incompatible resource versions and cannot share a pack.");
            }

            var output = new AnimationPack(targetVersion)
            {
                // Bit 2 controls the optional Bit29Data resource. It is intentionally omitted
                // because that resource cannot be merged safely when entries come from multiple
                // source packs.
                Flags = selectedAnimations[0].Pack.Flags & ~AnimationPackFlags.Bit2,
                METAPHOR_AnimArray3 = targetIsV2
                    ? new List<Animation>()
                    : null
            };

            foreach (var selected in selectedAnimations)
            {
                switch (selected.Entry.Kind)
                {
                    case CharacterAnimationListKind.Animation:
                        output.Animations.Add(selected.Animation);
                        break;

                    case CharacterAnimationListKind.BlendAnimation:
                        output.BlendAnimations.Add(selected.Animation);
                        break;

                    case CharacterAnimationListKind.ExtraAnimation:
                        if (output.METAPHOR_AnimArray3 == null)
                            throw new InvalidDataException(
                                "Extra animations require a version 2 animation pack.");

                        output.METAPHOR_AnimArray3.Add(selected.Animation);
                        break;
                }
            }

            return output;
        }

        private static AnimationPack LoadCharacterBrowserSourcePack(
            CharacterAnimationEntry entry,
            IDictionary<string, AnimationPack> sourcePacks)
        {
            if (!sourcePacks.TryGetValue(entry.PackPath, out var pack))
            {
                pack = Resource.Load<AnimationPack>(entry.PackPath);
                if (pack == null || pack.RawData != null)
                    throw new InvalidDataException("Animation pack cannot be repacked: " + entry.PackPath);

                sourcePacks.Add(entry.PackPath, pack);
            }

            return pack;
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

            var targetModelPack = mCharacterBrowserCurrentModelPack ??
                                  ModelEditorTreeView?.TopNode?.Data as ModelPack;
            if (targetModelPack?.Model == null)
                return animation;

            var currentModelPath = GetCurrentCharacterBrowserModelPath();
            if (AreSameCharacter(entry.PackPath, currentModelPath))
            {
                retargetNote = "same character";
                return animation;
            }

            var sourceModelEntry = FindCharacterModelForAnimation(entry.PackPath);
            if (sourceModelEntry == null)
            {
                retargetNote = "source model not found; preview uses original animation";
                return animation;
            }

            if (AreSamePath(sourceModelEntry.Path, currentModelPath))
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
            if (!string.IsNullOrWhiteSpace(mCharacterBrowserCurrentModelPath))
                return mCharacterBrowserCurrentModelPath;

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

        private static bool AreSameCharacter(string firstPath, string secondPath)
        {
            var firstCharacterId = ExtractCharacterId(firstPath);
            var secondCharacterId = ExtractCharacterId(secondPath);
            return !string.IsNullOrWhiteSpace(firstCharacterId) &&
                   string.Equals(firstCharacterId, secondCharacterId, StringComparison.OrdinalIgnoreCase);
        }

        private CharacterModelEntry FindCharacterModelForAnimation(string gapPath)
        {
            var animationKey = ExtractCharacterModelKey(gapPath);
            if (string.IsNullOrWhiteSpace(animationKey))
                return null;

            var characterDirectory = GetCharacterDirectory(gapPath);
            var characterModels = mCharacterModels
                .Where(model => model.Part == CharacterModelPart.Body)
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
            // P5/R uses keys such as c0001_001, while P3D/P5D uses pc203_018
            // for animations and pc203_26 for body models.
            return Regex.Match(
                stem ?? string.Empty,
                @"(?<!\d)\d{3,4}_\d{2,3}(?=_|$)",
                RegexOptions.CultureInvariant).Value;
        }

        private static CharacterModelPart ClassifyCharacterModel(string path)
        {
            var stem = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            var directory = Path.GetDirectoryName(path) ?? string.Empty;

            if (Regex.IsMatch(directory, @"(?:^|[\\/])face(?:[\\/]|$)", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(stem, @"(?:^|[_-])f\d+$", RegexOptions.IgnoreCase))
                return CharacterModelPart.Face;

            if (Regex.IsMatch(directory, @"(?:^|[\\/])hair(?:[\\/]|$)", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(stem, @"(?:^|[_-])h\d+$", RegexOptions.IgnoreCase))
                return CharacterModelPart.Hair;

            return CharacterModelPart.Body;
        }

        private static string ExtractCharacterId(string path)
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            return Regex.Match(stem ?? string.Empty, @"(?<!\d)\d{3,4}(?=_)", RegexOptions.CultureInvariant).Value;
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

        private void SaveCharacterBrowserSelectionSettings()
        {
            if (mCharacterBrowserRestoringSelection || mCharacterBrowserApplyingSavedSelection ||
                string.IsNullOrWhiteSpace(mCharacterBrowserRoot))
                return;

            try
            {
                var path = CharacterBrowserSelectionPath;
                var modelSelection = mCharacterModelListBox?.SelectedItem as CharacterModelEntry;
                var faceSelection = mCharacterFaceListBox?.SelectedItem as CharacterModelEntry;
                var hairSelection = mCharacterHairListBox?.SelectedItem as CharacterModelEntry;
                var animationSelections = mCharacterAnimationListBox?.SelectedItems
                    .Cast<CharacterAnimationEntry>()
                    .Select(SerializeCharacterBrowserAnimationSelection) ??
                    Enumerable.Empty<string>();
                var blendSelection = mCharacterBlendAnimationListBox?.SelectedItem as CharacterAnimationEntry;
                var selection = new[]
                {
                    mCharacterBrowserRoot,
                    CharacterBrowserSelectionFormat,
                    SerializeCharacterBrowserModelSelection(modelSelection),
                    SerializeCharacterBrowserModelSelection(faceSelection),
                    SerializeCharacterBrowserModelSelection(hairSelection),
                    string.Join(",", animationSelections),
                    blendSelection == null
                        ? string.Empty
                        : SerializeCharacterBrowserAnimationSelection(blendSelection)
                };
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, selection);
                mCharacterBrowserSavedSelection = selection;
            }
            catch
            {
                // Browsing still works if persistence fails.
            }
        }

        private string[] LoadCharacterBrowserSelection(string root)
        {
            try
            {
                if (!File.Exists(CharacterBrowserSelectionPath))
                    return null;

                var selection = File.ReadAllLines(CharacterBrowserSelectionPath);
                return selection.Length >= 4 &&
                       string.Equals(selection[0], root, StringComparison.OrdinalIgnoreCase)
                    ? selection
                    : null;
            }
            catch
            {
                return null;
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
