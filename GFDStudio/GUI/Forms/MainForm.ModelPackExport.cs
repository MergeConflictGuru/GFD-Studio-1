using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using GFDLibrary.Animations;
using GFDStudio.FormatModules;
using GFDStudio.GUI.Controls;
using GFDStudio.GUI.DataViewNodes;

namespace GFDStudio.GUI.Forms
{
    public partial class MainForm
    {
        private const uint ModelPackSaveFilterIndex = 1;
        private const uint FbxSaveFilterIndex = 2;
        private const uint DaeSaveFilterIndex = 3;
        private const uint ObjSaveFilterIndex = 4;
        private const uint AsciiFbxSaveFilterIndex = 5;
        private const uint IncludeAnimationsControlId = 0x4701;
        private const uint UnrealBoneNamesControlId = 0x4702;
        private const uint ExportAllAnimationsControlId = 0x4703;
        private const int HResultCancelled = unchecked( (int)0x800704C7 );

        private const uint FileOpenOptionsOverwritePrompt = 0x00000002;
        private const uint FileOpenOptionsForceFileSystem = 0x00000040;
        private const uint FileOpenOptionsPathMustExist = 0x00000800;

        private const uint ControlStateVisible = 0x00000004;

        private const uint ShellItemDisplayNameFileSystemPath = 0x80058000;

        private string SelectFileAndSaveModelPack( ModelPackViewNode node )
        {
            if ( node == null )
                throw new ArgumentNullException( nameof( node ) );

            var animationPack = ResolveFbxAnimationPack( node.Data.Version, out _ );
            var allAnimationsAvailable = CanExportAllAnimationsInCurrentPack();
            var selection = ShowModelPackSaveDialog(
                node.Text,
                animationPack != null,
                allAnimationsAvailable );
            if ( selection == null )
                return null;

            var path = NormalizeModelPackSavePath( selection.Path, selection.FilterIndex, node.Text );
            if ( !string.Equals( path, selection.Path, StringComparison.OrdinalIgnoreCase ) &&
                 File.Exists( path ) &&
                 MessageBox.Show(
                     this,
                     $"{Path.GetFileName( path )} already exists. Replace it?",
                     "Confirm Save As",
                     MessageBoxButtons.YesNo,
                     MessageBoxIcon.Warning,
                     MessageBoxDefaultButton.Button2 ) != DialogResult.Yes )
            {
                return null;
            }

            if ( selection.ExportAllAnimations )
                animationPack = ResolveAllFbxAnimationsInCurrentPack( node.Data.Version ) ?? animationPack;

            switch ( selection.FilterIndex )
            {
                case ModelPackSaveFilterIndex:
                    node.Export( path, typeof( GFDLibrary.ModelPack ) );
                    return path;

                case FbxSaveFilterIndex:
                case AsciiFbxSaveFilterIndex:
                    return ExportModelPackAsFbx(
                        node,
                        path,
                        selection.IncludeAnimations && animationPack != null,
                        animationPack,
                        selection.UseUnrealBoneNames );

                case DaeSaveFilterIndex:
                case ObjSaveFilterIndex:
                    node.Export( path, typeof( AssimpScene ) );
                    // Interchange export should not replace the currently open editable model.
                    return null;

                default:
                    throw new InvalidOperationException( "Unknown model export format." );
            }
        }

        private ModelPackSaveDialogSelection ShowModelPackSaveDialog(
            string fileName,
            bool animationAvailable,
            bool allAnimationsAvailable )
        {
            INativeFileSaveDialog dialog = null;
            INativeShellItem resultItem = null;
            IntPtr resultPathPointer = IntPtr.Zero;

            try
            {
                dialog = (INativeFileSaveDialog)new NativeFileSaveDialogComObject();
                dialog.SetFileTypes( 5, new[]
                {
                    new NativeFilterSpec( "Model pack (*.gmd;*.gfs)", "*.gmd;*.gfs" ),
                    new NativeFilterSpec( "Autodesk FBX (*.fbx)", "*.fbx" ),
                    new NativeFilterSpec( "Collada (*.dae)", "*.dae" ),
                    new NativeFilterSpec( "Wavefront OBJ (*.obj)", "*.obj" ),
                    new NativeFilterSpec( "ASCII FBX (*.ascii.fbx)", "*.ascii.fbx" )
                } );
                dialog.SetFileTypeIndex( ModelPackSaveFilterIndex );
                dialog.SetFileName( fileName ?? string.Empty );
                dialog.SetTitle( "Save As" );
                dialog.SetDefaultExtension( GetModelPackDefaultExtension( fileName ) );
                dialog.GetOptions( out var options );
                dialog.SetOptions(
                    options |
                    FileOpenOptionsOverwritePrompt |
                    FileOpenOptionsForceFileSystem |
                    FileOpenOptionsPathMustExist );

                var customize = (INativeFileDialogCustomize)dialog;
                customize.AddCheckButton(
                    IncludeAnimationsControlId,
                    "Include animations",
                    animationAvailable );
                if ( !animationAvailable )
                {
                    // Leave the option visible so its location is stable, but disable it when
                    // there is no current/embedded animation available to export.
                    customize.SetControlState( IncludeAnimationsControlId, ControlStateVisible );
                }

                customize.AddCheckButton(
                    ExportAllAnimationsControlId,
                    "Export all animations in current pack",
                    false );
                if ( !allAnimationsAvailable )
                    customize.SetControlState( ExportAllAnimationsControlId, ControlStateVisible );

                customize.AddCheckButton(
                    UnrealBoneNamesControlId,
                    "Export Unreal bone names (FBX)",
                    false );

                var showResult = dialog.Show( Handle );
                if ( showResult == HResultCancelled )
                    return null;
                if ( showResult < 0 )
                    Marshal.ThrowExceptionForHR( showResult );

                dialog.GetFileTypeIndex( out var filterIndex );
                customize.GetCheckButtonState( IncludeAnimationsControlId, out var includeAnimations );
                customize.GetCheckButtonState( ExportAllAnimationsControlId, out var exportAllAnimations );
                customize.GetCheckButtonState( UnrealBoneNamesControlId, out var useUnrealBoneNames );
                dialog.GetResult( out resultItem );
                resultItem.GetDisplayName( ShellItemDisplayNameFileSystemPath, out resultPathPointer );
                var path = Marshal.PtrToStringUni( resultPathPointer );
                if ( string.IsNullOrWhiteSpace( path ) )
                    throw new InvalidOperationException( "The Save As dialog did not return a file path." );

                return new ModelPackSaveDialogSelection(
                    path,
                    filterIndex,
                    ( includeAnimations && animationAvailable ) ||
                    ( exportAllAnimations && allAnimationsAvailable ),
                    useUnrealBoneNames,
                    exportAllAnimations && allAnimationsAvailable );
            }
            finally
            {
                if ( resultPathPointer != IntPtr.Zero )
                    Marshal.FreeCoTaskMem( resultPathPointer );
                if ( resultItem != null && Marshal.IsComObject( resultItem ) )
                    Marshal.FinalReleaseComObject( resultItem );
                if ( dialog != null && Marshal.IsComObject( dialog ) )
                    Marshal.FinalReleaseComObject( dialog );
            }
        }

        private static string NormalizeModelPackSavePath(
            string path,
            uint filterIndex,
            string originalFileName )
        {
            var desiredExtension = filterIndex switch
            {
                ModelPackSaveFilterIndex => GetModelPackDefaultExtensionWithDot( originalFileName ),
                FbxSaveFilterIndex => ".fbx",
                DaeSaveFilterIndex => ".dae",
                ObjSaveFilterIndex => ".obj",
                AsciiFbxSaveFilterIndex => ".ascii.fbx",
                _ => string.Empty
            };

            if ( string.IsNullOrEmpty( desiredExtension ) )
                return path;

            if ( filterIndex == ModelPackSaveFilterIndex &&
                 ( path.EndsWith( ".gmd", StringComparison.OrdinalIgnoreCase ) ||
                   path.EndsWith( ".gfs", StringComparison.OrdinalIgnoreCase ) ) )
            {
                return path;
            }

            if ( path.EndsWith( desiredExtension, StringComparison.OrdinalIgnoreCase ) )
                return path;

            var knownExtensions = new[] { ".ascii.fbx", ".gmd", ".gfs", ".fbx", ".dae", ".obj" };
            foreach ( var knownExtension in knownExtensions )
            {
                if ( path.EndsWith( knownExtension, StringComparison.OrdinalIgnoreCase ) )
                    return path.Substring( 0, path.Length - knownExtension.Length ) + desiredExtension;
            }

            return path + desiredExtension;
        }

        private static string GetModelPackDefaultExtension( string fileName )
        {
            return string.Equals( Path.GetExtension( fileName ), ".gfs", StringComparison.OrdinalIgnoreCase )
                ? "gfs"
                : "gmd";
        }

        private static string GetModelPackDefaultExtensionWithDot( string fileName )
        {
            return "." + GetModelPackDefaultExtension( fileName );
        }

        private string ExportModelPackAsFbx(
            ModelPackViewNode node,
            string path,
            bool includeAnimations,
            AnimationPack animationPack,
            bool useUnrealBoneNames )
        {
            // Character Browser / showroom previews can be composed from separate
            // body, face and hair GMDs while the editor tree still points at the primary
            // (usually body) file. Export what is actually being shown, not just that
            // primary source file, so split dancing characters keep all selected parts.
            var modelPack =
                mCharacterBrowserPanel != null &&
                mCharacterBrowserPanel.Visible &&
                mCharacterBrowserCurrentModelPack?.Model != null
                    ? mCharacterBrowserCurrentModelPack
                    : node.Data;

            if ( modelPack.Model == null )
            {
                MessageBox.Show(
                    "This model pack has no model to export.",
                    "FBX export",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error );
                return null;
            }

            try
            {
                if ( includeAnimations && animationPack != null )
                    ModelPackExportHelper.ExportFile(
                        modelPack, animationPack, path, useUnrealBoneNames );
                else
                    ModelPackExportHelper.ExportFile(
                        modelPack, path, useUnrealBoneNames );

                var suffix = includeAnimations && animationPack != null
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

        private bool CanExportAllAnimationsInCurrentPack()
        {
            if ( mCharacterAnimationListBox?.SelectedItem is CharacterAnimationEntry entry &&
                 entry.Kind == CharacterAnimationListKind.Animation )
            {
                try
                {
                    var pack = GFDLibrary.Resource.Load<AnimationPack>( entry.PackPath );
                    return pack?.Animations?.Count > 1;
                }
                catch
                {
                    return false;
                }
            }

            return ModelEditorTreeView.TopNode is ModelPackViewNode modelPackNode &&
                   modelPackNode.Data.AnimationPack?.Animations?.Count > 1;
        }

        private AnimationPack ResolveAllFbxAnimationsInCurrentPack( uint version )
        {
            if ( mCharacterAnimationListBox?.SelectedItem is CharacterAnimationEntry selectedEntry &&
                 selectedEntry.Kind == CharacterAnimationListKind.Animation )
            {
                var sourcePack = GFDLibrary.Resource.Load<AnimationPack>( selectedEntry.PackPath );
                if ( sourcePack?.Animations == null || sourcePack.Animations.Count == 0 )
                    return null;

                // Prepare every clip exactly like the currently previewed clip. This matters for
                // P5/P5R/P5D because the raw packs can use different skeleton conventions, and
                // split P5D animations can pull face/hair tracks from companion GAP files.
                var output = new AnimationPack( version );
                for ( var animationIndex = 0; animationIndex < sourcePack.Animations.Count; animationIndex++ )
                {
                    var entry = new CharacterAnimationEntry
                    {
                        PackPath = selectedEntry.PackPath,
                        Kind = CharacterAnimationListKind.Animation,
                        Index = animationIndex,
                        DisplayName = selectedEntry.DisplayName,
                        DefinitionHash = selectedEntry.DefinitionHash,
                        BodyTargetNames = selectedEntry.BodyTargetNames
                    };

                    var animation = PrepareCharacterBrowserAnimation( entry, out _ );
                    if ( animation != null )
                        output.Animations.Add( animation );
                }

                return output.Animations.Count > 0 ? output : null;
            }

            if ( ModelEditorTreeView.TopNode is ModelPackViewNode modelPackNode &&
                 modelPackNode.Data.AnimationPack?.Animations?.Count > 0 )
            {
                return modelPackNode.Data.AnimationPack;
            }

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

        private sealed class ModelPackSaveDialogSelection
        {
            public ModelPackSaveDialogSelection(
                string path,
                uint filterIndex,
                bool includeAnimations,
                bool useUnrealBoneNames,
                bool exportAllAnimations )
            {
                Path = path;
                FilterIndex = filterIndex;
                IncludeAnimations = includeAnimations;
                UseUnrealBoneNames = useUnrealBoneNames;
                ExportAllAnimations = exportAllAnimations;
            }

            public string Path { get; }
            public uint FilterIndex { get; }
            public bool IncludeAnimations { get; }
            public bool UseUnrealBoneNames { get; }
            public bool ExportAllAnimations { get; }
        }

        [StructLayout( LayoutKind.Sequential, CharSet = CharSet.Unicode )]
        private struct NativeFilterSpec
        {
            public NativeFilterSpec( string name, string spec )
            {
                Name = name;
                Spec = spec;
            }

            [MarshalAs( UnmanagedType.LPWStr )]
            public string Name;

            [MarshalAs( UnmanagedType.LPWStr )]
            public string Spec;
        }

        [ComImport]
        [Guid( "C0B4E2F3-BA21-4773-8DBA-335EC946EB8B" )]
        private class NativeFileSaveDialogComObject
        {
        }

        [ComImport]
        [Guid( "42F85136-DB7E-439C-85F1-E4075D135FC8" )]
        [InterfaceType( ComInterfaceType.InterfaceIsIUnknown )]
        private interface INativeFileDialog
        {
            [PreserveSig]
            int Show( IntPtr parent );

            void SetFileTypes(
                uint fileTypeCount,
                [MarshalAs( UnmanagedType.LPArray, SizeParamIndex = 0 )] NativeFilterSpec[] filterSpecs );

            void SetFileTypeIndex( uint fileTypeIndex );
            void GetFileTypeIndex( out uint fileTypeIndex );
            void Advise( IntPtr events, out uint cookie );
            void Unadvise( uint cookie );
            void SetOptions( uint options );
            void GetOptions( out uint options );
            void SetDefaultFolder( INativeShellItem shellItem );
            void SetFolder( INativeShellItem shellItem );
            void GetFolder( out INativeShellItem shellItem );
            void GetCurrentSelection( out INativeShellItem shellItem );
            void SetFileName( [MarshalAs( UnmanagedType.LPWStr )] string name );
            void GetFileName( out IntPtr name );
            void SetTitle( [MarshalAs( UnmanagedType.LPWStr )] string title );
            void SetOkButtonLabel( [MarshalAs( UnmanagedType.LPWStr )] string text );
            void SetFileNameLabel( [MarshalAs( UnmanagedType.LPWStr )] string label );
            void GetResult( out INativeShellItem shellItem );
            void AddPlace( INativeShellItem shellItem, uint placement );
            void SetDefaultExtension( [MarshalAs( UnmanagedType.LPWStr )] string defaultExtension );
            void Close( int result );
            void SetClientGuid( ref Guid guid );
            void ClearClientData();
            void SetFilter( IntPtr filter );
        }

        [ComImport]
        [Guid( "84BCCD23-5FDE-4CDB-AEA4-AF64B83D78AB" )]
        [InterfaceType( ComInterfaceType.InterfaceIsIUnknown )]
        private interface INativeFileSaveDialog : INativeFileDialog
        {
            void SetSaveAsItem( INativeShellItem shellItem );
            void SetProperties( IntPtr propertyStore );
            void SetCollectedProperties( IntPtr propertyDescriptionList, [MarshalAs( UnmanagedType.Bool )] bool appendDefault );
            void GetProperties( out IntPtr propertyStore );
            void ApplyProperties( INativeShellItem shellItem, IntPtr propertyStore, IntPtr owner, IntPtr progressSink );
        }

        [ComImport]
        [Guid( "E6FDD21A-163F-4975-9C8C-A69F1BA37034" )]
        [InterfaceType( ComInterfaceType.InterfaceIsIUnknown )]
        private interface INativeFileDialogCustomize
        {
            void EnableOpenDropDown( uint controlId );
            void AddMenu( uint controlId, [MarshalAs( UnmanagedType.LPWStr )] string label );
            void AddPushButton( uint controlId, [MarshalAs( UnmanagedType.LPWStr )] string label );
            void AddComboBox( uint controlId );
            void AddRadioButtonList( uint controlId );
            void AddCheckButton(
                uint controlId,
                [MarshalAs( UnmanagedType.LPWStr )] string label,
                [MarshalAs( UnmanagedType.Bool )] bool isChecked );
            void AddEditBox( uint controlId, [MarshalAs( UnmanagedType.LPWStr )] string text );
            void AddSeparator( uint controlId );
            void AddText( uint controlId, [MarshalAs( UnmanagedType.LPWStr )] string text );
            void SetControlLabel( uint controlId, [MarshalAs( UnmanagedType.LPWStr )] string label );
            void GetControlState( uint controlId, out uint state );
            void SetControlState( uint controlId, uint state );
            void GetEditBoxText( uint controlId, out IntPtr text );
            void SetEditBoxText( uint controlId, [MarshalAs( UnmanagedType.LPWStr )] string text );
            void GetCheckButtonState(
                uint controlId,
                [MarshalAs( UnmanagedType.Bool )] out bool isChecked );
        }

        [ComImport]
        [Guid( "43826D1E-E718-42EE-BC55-A1E261C37BFE" )]
        [InterfaceType( ComInterfaceType.InterfaceIsIUnknown )]
        private interface INativeShellItem
        {
            void BindToHandler( IntPtr bindContext, ref Guid handlerId, ref Guid interfaceId, out IntPtr result );
            void GetParent( out INativeShellItem parent );
            void GetDisplayName( uint displayNameType, out IntPtr name );
            void GetAttributes( uint mask, out uint attributes );
            void Compare( INativeShellItem shellItem, uint hint, out int order );
        }
    }
}
