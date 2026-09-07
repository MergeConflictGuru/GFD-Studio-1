using System;
using System.CodeDom;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Forms;
using GFDLibrary;
using GFDLibrary.Animations;
using GFDLibrary.Materials;
using GFDLibrary.Models;
using GFDLibrary.Textures.Texpack;
using GFDStudio.FormatModules;
using GFDStudio.GUI.Controls;
using GFDStudio.GUI.DataViewNodes;
using GFDStudio.IO;
using MetroSet_UI.Controls;
using MetroSet_UI.Forms;
using SixLabors.ImageSharp;
using Control = System.Windows.Forms.Control;

namespace GFDStudio.GUI.Forms
{
    public partial class MainForm : MetroSet_UI.Forms.MetroSetForm
    {
        public static Config settings = new Config();

        private static MainForm sInstance;
        public static MainForm Instance
        {
            get => sInstance;
            set
            {
                if ( sInstance == null )
                    sInstance = value;
                else
                    throw new InvalidOperationException();
            }
        }

        private FileHistoryList mFileHistoryList;

        public DataTreeView ModelEditorTreeView
        {
            get => mModelEditorTreeView;
            private set => mModelEditorTreeView = value;
        }

        public string LastOpenedFilePath { get; private set; }

        //
        // Initialization
        //
        public MainForm()
        {
            InitializeComponent();
            InitializeAnimationStepButtons();
            InitializeState();
            InitializeEvents();
            InitializeAnimationMatching();

            settings = settings.LoadJson();
            Theme.Apply( this );
            retainColorValuesToolStripMenuItem.Checked = settings.RetainMaterialColors;
            retainTexNameToolStripMenuItem.Checked = settings.RetainTextureNames;
            useDarkThemeToolStripMenuItem.Checked = settings.DarkMode;
#if DEBUG
            //ModelViewControl.Instance.LoadAnimation( Resource.Load<AnimationPack>( 
            //    @"D:\Modding\Persona 5 EU\Main game\ExtractedClean\data\model\character\0001\field\bf0001_002.GAP" ).Animations[2]);
#endif
        }

        private void InitializeAnimationStepButtons()
        {
            mAnimationPreviousButton = CreateAnimationStepButton( "←", "Previous frame" );
            mAnimationNextButton = CreateAnimationStepButton( "→", "Next frame" );

            tableLayoutPanel_AnimationControls.ColumnCount = 5;
            tableLayoutPanel_AnimationControls.ColumnStyles.Clear();
            tableLayoutPanel_AnimationControls.ColumnStyles.Add( new System.Windows.Forms.ColumnStyle( System.Windows.Forms.SizeType.Percent, 9F ) );
            tableLayoutPanel_AnimationControls.ColumnStyles.Add( new System.Windows.Forms.ColumnStyle( System.Windows.Forms.SizeType.Percent, 58F ) );
            tableLayoutPanel_AnimationControls.ColumnStyles.Add( new System.Windows.Forms.ColumnStyle( System.Windows.Forms.SizeType.Percent, 13F ) );
            tableLayoutPanel_AnimationControls.ColumnStyles.Add( new System.Windows.Forms.ColumnStyle( System.Windows.Forms.SizeType.Percent, 9F ) );
            tableLayoutPanel_AnimationControls.ColumnStyles.Add( new System.Windows.Forms.ColumnStyle( System.Windows.Forms.SizeType.Percent, 11F ) );

            tableLayoutPanel_AnimationControls.Controls.Clear();
            tableLayoutPanel_AnimationControls.Controls.Add( mAnimationPreviousButton, 0, 0 );
            tableLayoutPanel_AnimationControls.Controls.Add( mAnimationTrackBar, 1, 0 );
            tableLayoutPanel_AnimationControls.Controls.Add( mAnimationPlaybackButton, 2, 0 );
            tableLayoutPanel_AnimationControls.Controls.Add( mAnimationNextButton, 3, 0 );
            tableLayoutPanel_AnimationControls.Controls.Add( mAnimationStopButton, 4, 0 );
        }

        private static MetroSetButton CreateAnimationStepButton( string text, string accessibleName )
        {
            return new MetroSetButton
            {
                AccessibleName = accessibleName,
                Dock = DockStyle.Fill,
                Font = new System.Drawing.Font( "Microsoft Sans Serif", 12F ),
                HoverBorderColor = System.Drawing.Color.FromArgb( 95, 207, 255 ),
                HoverColor = System.Drawing.Color.FromArgb( 95, 207, 255 ),
                HoverTextColor = System.Drawing.Color.White,
                IsDerivedStyle = true,
                Margin = new Padding( 5, 4, 5, 4 ),
                MaximumSize = new System.Drawing.Size( 100, 30 ),
                NormalBorderColor = System.Drawing.Color.FromArgb( 65, 177, 225 ),
                NormalColor = System.Drawing.Color.FromArgb( 65, 177, 225 ),
                NormalTextColor = System.Drawing.Color.White,
                Padding = new Padding( 5 ),
                PressBorderColor = System.Drawing.Color.FromArgb( 35, 147, 195 ),
                PressColor = System.Drawing.Color.FromArgb( 35, 147, 195 ),
                PressTextColor = System.Drawing.Color.White,
                Style = MetroSet_UI.Enums.Style.Dark,
                Text = text,
                ThemeAuthor = "Narwin",
                ThemeName = "MetroDark"
            };
        }

        private void InitializeState()
        {
            Instance = this;

#if DEBUG
            Text = $"{Program.Name} {Program.Version.Major}.{Program.Version.Minor}.{Program.Version.Build} #{Program.CommitNumber} [DEBUG]";
#else
            Text = $"{Program.Name} {Program.Version.Major}.{Program.Version.Minor}.{Program.Version.Build} #{Program.CommitNumber}";
#endif

            mFileHistoryList = new FileHistoryList( "file_history.txt", 10, mOpenToolStripMenuItem.DropDown.Items,
                                                    HandleOpenToolStripRecentlyOpenedFileClick );
            ModelEditorTreeView.LabelEdit = true;
            AllowDrop = true;
        }

        private void InitializeEvents()
        {
            ModelEditorTreeView.AfterSelect += HandleTreeViewAfterSelect;
            ModelEditorTreeView.UserPropertyChanged += HandleTreeViewUserPropertyChanged;
            ModelEditorTreeView.KeyDown += HandleKeyDown;

            mAnimationListTreeView.AfterSelect += HandleAnimationTreeViewAfterSelect;

            mContentPanel.ControlAdded += HandleContentPanelControlAdded;
            mContentPanel.Resize += HandleContentPanelResize;
            DragDrop += HandleDragDrop;
            DragEnter += HandleDragEnter;
            ModelViewControl.Instance.AnimationLoaded += HandleModelAnimationLoaded;
            ModelViewControl.Instance.AnimationPlaybackStateChanged += HandleModelAnimationPlaybackStateChanged;
            ModelViewControl.Instance.AnimationTimeChanged += HandleModelAnimationTimeChanged;
            mAnimationTrackBar.ValueChanged += HandleTrackbarValueChanged;
            mAnimationPlaybackButton.Click += HandleAnimationPlaybackButtonClick;
            mAnimationPreviousButton.Click += HandleAnimationPreviousButtonClick;
            mAnimationNextButton.Click += HandleAnimationNextButtonClick;
        }

        //
        // Recently opened files list
        //
        private void RecordOpenedFile( string filePath )
        {
            LastOpenedFilePath = filePath;
            mFileHistoryList.Add( filePath );
        }

        //
        // File IO
        //
        public string SelectAndOpenSelectedFile()
        {
            var filePath = SelectFileToOpen();
            if ( filePath != null )
                OpenFile( filePath );

            return filePath;
        }

        public string SelectFileToOpen()
        {
            return SelectFileToOpen( ModuleFilterGenerator.GenerateFilterForAllSupportedImportFormats() );
        }

        public string SelectFileToOpen( string filter )
        {
            using ( var dialog = new OpenFileDialog() )
            {
                dialog.Filter = filter;
                if ( dialog.ShowDialog() != DialogResult.OK )
                    return null;

                return dialog.FileName;
            }
        }

        public void OpenFile( string filePath )
        {
            Logger.Debug( $"MainForm: Open file {filePath}" );
            if ( !DataViewNodeFactory.TryCreate( filePath, out var node ) )
            {
                MessageBox.Show( "Hee file could not be loaded, ho.", "Error", MessageBoxButtons.OK );
                return;
            }

            RecordOpenedFile( filePath );
            if (node.DataType == typeof(Animation) || node.DataType == typeof(AnimationPack))
            {
                mAnimationListTreeView.SetTopNode( node );
            }
            else
            {
                // METAPHOR - Several parts of model pack are stored in separate files, check these.
                if ( node.DataType == typeof( ModelPack ) && ( (ModelPack)node.Data ).Version >= 0x02000000 )
                    CollectModelParts.CollectDisconnectedModelPartsForModelPack( (ModelPack)node.Data, filePath );
                ModelEditorTreeView.SetTopNode( node );
            }
            UpdateSelection( node );
        }

        public string SelectFileToSaveTo()
        {
            using ( var dialog = new SaveFileDialog() )
            {
                dialog.Filter = ModuleFilterGenerator.GenerateFilter( FormatModuleUsageFlags.Export );
                if ( dialog.ShowDialog() != DialogResult.OK )
                    return null;

                return dialog.FileName;
            }
        }

        public void SaveFile( string filePath )
        {
            if ( ModelEditorTreeView.Nodes.Count > 0 )
                ModelEditorTreeView.TopNode.Export( filePath );
        }

        public string SelectFileAndSave()
        {
            if ( ModelEditorTreeView.Nodes.Count > 0 )
                return ModelEditorTreeView.TopNode.Export();

            return null;
        }

        private void ClearContentPanel()
        {
            //foreach ( var control in mContentPanel.Controls )
            //{
            //    ( control as IDisposable )?.Dispose();
            //}

            mContentPanel.Controls.Clear();
        }

        //
        // Event handlers
        //
        protected override void OnClosed( EventArgs e )
        {
            mFileHistoryList.Save();

            // exit application when the main form is closed
            Application.Exit();
        }

        public void UpdateSelection()
        {
            var selectedNode = (DataViewNode)mPropertyGrid.SelectedObject;
            if ( selectedNode != null )
                UpdateSelection( selectedNode );
        }

        private void UpdateSelection( DataViewNode node )
        {
            // Set property grid to display properties of the currently selected node
            mPropertyGrid.SelectedObject = node;

            System.Windows.Forms.Control control = null;

            ModelViewControl.Instance.ClearSelection();

            if ( FormatModuleRegistry.ModuleByType.TryGetValue( node.DataType, out var module ) )
            {
                if ( module.UsageFlags.HasFlag( FormatModuleUsageFlags.Bitmap ) )
                {
                    BitmapViewControl.Instance.LoadBitmap( module.GetBitmap( node.Data ) );
                    BitmapViewControl.Instance.Visible = false;
                    control = BitmapViewControl.Instance;
                }
                else if ( module.ModelType == typeof( ModelPack ) )
                {
                    ModelViewControl.Instance.LoadModel( (ModelPack)node.Data );
                    ModelViewControl.Instance.Visible = false;
                    control = ModelViewControl.Instance;
                }
                else if ( module.ModelType == typeof( Mesh ))
                {
                    ModelViewControl.Instance.SetSelection( (Mesh)node.Data );
                }
                else if ( module.ModelType == typeof( Material ) )
                {
                    ModelViewControl.Instance.SetSelection( (Material)node.Data );
                }
                else if ( node.DataType == typeof( Animation ) )
                {
                    ModelViewControl.Instance.LoadAnimation( (Animation)node.Data );
                }
            }

            if ( control != null )
            {
                // Clear the content panel
                ClearContentPanel();
                mContentPanel.Controls.Add( control );
            }
        }
    }
}
