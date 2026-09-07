using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using GFDLibrary.Animations;
using GFDStudio.FormatModules;
using GFDStudio.GUI.Controls;
using GFDStudio.GUI.DataViewNodes;

namespace GFDStudio.GUI.Forms
{
    public partial class MainForm
    {
        private const int ModelPackSaveFilterIndex = 1;
        private const int FbxSaveFilterIndex = 2;
        private const int DaeSaveFilterIndex = 3;
        private const int ObjSaveFilterIndex = 4;
        private const int AsciiFbxSaveFilterIndex = 5;

        private string SelectFileAndSaveModelPack( ModelPackViewNode node )
        {
            if ( node == null )
                throw new ArgumentNullException( nameof( node ) );

            using var dialog = new SaveFileDialog
            {
                AutoUpgradeEnabled = true,
                CheckPathExists = true,
                FileName = node.Text,
                Filter =
                    "Model pack (*.gmd;*.gfs)|*.gmd;*.gfs|" +
                    "Autodesk FBX (*.fbx)|*.fbx|" +
                    "Collada (*.dae)|*.dae|" +
                    "Wavefront OBJ (*.obj)|*.obj|" +
                    "ASCII FBX (*.ascii.fbx)|*.ascii.fbx",
                FilterIndex = ModelPackSaveFilterIndex,
                OverwritePrompt = true,
                Title = "Save As",
                ValidateNames = true,
                AddExtension = true,
                SupportMultiDottedExtensions = true
            };

            if ( dialog.ShowDialog( this ) != DialogResult.OK )
                return null;

            switch ( dialog.FilterIndex )
            {
                case ModelPackSaveFilterIndex:
                    node.Export( dialog.FileName, typeof( GFDLibrary.ModelPack ) );
                    return dialog.FileName;

                case FbxSaveFilterIndex:
                case AsciiFbxSaveFilterIndex:
                    return ExportModelPackAsFbx( node, dialog.FileName );

                case DaeSaveFilterIndex:
                case ObjSaveFilterIndex:
                    node.Export( dialog.FileName, typeof( AssimpScene ) );
                    // Interchange export should not replace the currently open editable model.
                    return null;

                default:
                    throw new InvalidOperationException( "Unknown model export format." );
            }
        }

        private string ExportModelPackAsFbx( ModelPackViewNode node, string path )
        {
            var modelPack = node.Data;
            if ( modelPack.Model == null )
            {
                MessageBox.Show(
                    "This model pack has no model to export.",
                    "FBX export",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error );
                return null;
            }

            var animationPack = ResolveFbxAnimationPack( modelPack.Version, out var animationDescription );
            using var optionsDialog = new FbxExportOptionsDialog(
                animationPack != null,
                animationDescription );
            if ( optionsDialog.ShowDialog( this ) != DialogResult.OK )
                return null;

            try
            {
                if ( optionsDialog.IncludeAnimations && animationPack != null )
                {
                    ModelPackExportHelper.ExportFile( modelPack, animationPack, path );
                }
                else
                {
                    ModelPackExportHelper.ExportFile( modelPack, path );
                }

                var suffix = optionsDialog.IncludeAnimations && animationPack != null
                    ? " with animations baked at 30 fps"
                    : string.Empty;
                MessageBox.Show(
                    $"Exported FBX{suffix}:\n{path}",
                    "FBX export",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information );
            }
            catch ( Exception exception )
            {
                MessageBox.Show(
                    exception.Message,
                    "FBX export failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error );
            }

            // Do not reopen an interchange export as the editable source model.
            return null;
        }

        private AnimationPack ResolveFbxAnimationPack( uint version, out string description )
        {
            var loadedAnimation = ModelViewControl.Instance.Animation;
            if ( loadedAnimation != null )
            {
                var pack = new AnimationPack( version );
                pack.Animations.Add( loadedAnimation );
                description = "The currently loaded animation will be baked into the FBX at 30 fps.";
                return pack;
            }

            if ( ModelEditorTreeView.TopNode is ModelPackViewNode modelPackNode &&
                 modelPackNode.Data.AnimationPack?.Animations?.Count > 0 )
            {
                var count = modelPackNode.Data.AnimationPack.Animations.Count;
                description = $"{count} embedded animation{( count == 1 ? string.Empty : "s" )} will be baked into the FBX at 30 fps.";
                return modelPackNode.Data.AnimationPack;
            }

            description = "No animation is currently loaded or embedded in this model pack.";
            return null;
        }

        private sealed class FbxExportOptionsDialog : Form
        {
            private readonly CheckBox mIncludeAnimationsCheckBox;

            public bool IncludeAnimations => mIncludeAnimationsCheckBox.Checked;

            public FbxExportOptionsDialog( bool animationAvailable, string animationDescription )
            {
                Text = "FBX export options";
                FormBorderStyle = FormBorderStyle.FixedDialog;
                StartPosition = FormStartPosition.CenterParent;
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = false;
                AutoScaleMode = AutoScaleMode.Dpi;
                ClientSize = new Size( 430, 146 );

                mIncludeAnimationsCheckBox = new CheckBox
                {
                    AutoSize = true,
                    Text = "Include animations",
                    Checked = animationAvailable,
                    Enabled = animationAvailable,
                    Location = new Point( 18, 18 )
                };

                var descriptionLabel = new Label
                {
                    AutoSize = false,
                    Location = new Point( 18, 48 ),
                    Size = new Size( 394, 42 ),
                    Text = animationDescription ?? string.Empty
                };

                var exportButton = new Button
                {
                    Text = "Export",
                    DialogResult = DialogResult.OK,
                    Size = new Size( 88, 28 ),
                    Location = new Point( 230, 104 )
                };

                var cancelButton = new Button
                {
                    Text = "Cancel",
                    DialogResult = DialogResult.Cancel,
                    Size = new Size( 88, 28 ),
                    Location = new Point( 324, 104 )
                };

                Controls.Add( mIncludeAnimationsCheckBox );
                Controls.Add( descriptionLabel );
                Controls.Add( exportButton );
                Controls.Add( cancelButton );
                AcceptButton = exportButton;
                CancelButton = cancelButton;
            }
        }
    }
}
