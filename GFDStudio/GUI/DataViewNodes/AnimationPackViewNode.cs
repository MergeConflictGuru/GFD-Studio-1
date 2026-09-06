using System;
using System.ComponentModel;
using System.Windows.Forms;
using GFDLibrary;
using GFDLibrary.Animations;
using GFDStudio.FormatModules;

namespace GFDStudio.GUI.DataViewNodes
{
    public class AnimationPackViewNode : ResourceViewNode<AnimationPack>
    {
        public override DataViewNodeMenuFlags ContextMenuFlags =>
            DataViewNodeMenuFlags.Delete | DataViewNodeMenuFlags.Export | DataViewNodeMenuFlags.Replace | DataViewNodeMenuFlags.Move;

        public override DataViewNodeFlags NodeFlags => 
            DataViewNodeFlags.Branch;

        public AnimationPackFlags Flags
        {
            get => Data.Flags;
            set => SetDataProperty( value );
        }

        [Browsable(false)]
        public AnimationListViewNode Animations { get; set; }

        [Browsable( false )]
        public AnimationListViewNode BlendAnimations { get; set; }

        [Browsable( false )]
        public AnimationBit29DataViewNode Bit29Data { get; set; }

        protected internal AnimationPackViewNode( string text, AnimationPack data ) : base( text, data )
        {
        }

        protected override void InitializeCore()
        {
            base.InitializeCore();
            RegisterModelUpdateHandler( () =>
            {
                var model = new AnimationPack( Version );
                model.Animations = Animations.Data;
                model.BlendAnimations = BlendAnimations.Data;

                if ( Bit29Data != null && Nodes.Contains( Bit29Data ) )
                    model.Bit29Data = Bit29Data.Data;

                return model;
            });
            RegisterCustomHandler( "Tools", "Retarget", () =>
            {
                var originalScene = ( Parent as ModelPackViewNode )?.Model?.Data ??
                                    ModuleImportUtilities.SelectImportFile<ModelPack>( "Select the original model file." )?.Model;

                if ( originalScene == null )
                    return;

                var newScene = ModuleImportUtilities.SelectImportFile<ModelPack>( "Select the new model file." )?.Model;
                if ( newScene == null )
                    return;    

                bool fixArms = MessageBox.Show( "Fix arms? If unsure, select No.", "Question", MessageBoxButtons.YesNo,
                                                MessageBoxIcon.Question, MessageBoxDefaultButton.Button2 ) == DialogResult.Yes;

                Data.Retarget( originalScene, newScene, fixArms );
            } );
            RegisterCustomHandler("Tools", "Convert to P5", () =>
            {
                Data.ConvertToP5();
            });
            RegisterCustomHandler("Tools", "Retarget split Dance character (experimental)", () =>
            {
                if (MessageBox.Show("Select the source GMD, Dance body, face, hair, and a native Dance base GAP.\n\n" +
                    "This exports one assembled GMD with skeletal animations and reference-based knee correction. " +
                    "Facial expressions, unmatched hair/cloth motion and in-game compatibility are not converted or guaranteed.",
                    "Experimental split-character retarget", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                try
                {
                    var source = ModuleImportUtilities.SelectImportFile<ModelPack>("Select the original P5/P5R model.");
                    if (source?.Model == null) return;
                    var body = ModuleImportUtilities.SelectImportFile<ModelPack>("Select the Dance body GMD.");
                    if (body?.Model == null) return;
                    var face = ModuleImportUtilities.SelectImportFile<ModelPack>("Select the matching Dance face GMD.");
                    if (face?.Model == null) return;
                    var hair = ModuleImportUtilities.SelectImportFile<ModelPack>("Select the matching Dance hair GMD.");
                    if (hair?.Model == null) return;
                    var native = ModuleImportUtilities.SelectImportFile<AnimationPack>("Select a native Dance base GAP (not _f, _h or costume overlay).");
                    if (native == null || native.Animations.Count == 0) return;
                    using var save = new SaveFileDialog { Filter = "Assembled preview (*.GMD)|*.GMD", FileName = "retargeted_character_preview.GMD" };
                    if (save.ShowDialog() != DialogResult.OK) return;
                    var preview = SplitCharacterRetargeter.CreatePreview(source.Model, Data, body, face, hair, native.Animations[0]);
                    preview.Save(save.FileName);
                    MessageBox.Show("Saved the assembled preview with embedded animations:\n" + save.FileName +
                        "\n\nOpen that GMD to inspect all parts together. The source animation pack was not changed.", "Retarget preview saved");
                }
                catch (Exception exception)
                {
                    MessageBox.Show(exception.Message, "Retarget failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });
            RegisterCustomHandler("Tools", "Fix IDs", () =>
            {
                ImportModelAndFixTargetIds(Data);
            });
        }

        protected override void InitializeViewCore()
        {
            // Nothing to display if we only have raw data
            if ( Data.RawData != null )
                return;

            Animations = ( AnimationListViewNode )DataViewNodeFactory.Create( "Animations", Data.Animations, new[] { new ListItemNameProvider<Animation>(( x, i ) => $"Animation {i}" ) });
            BlendAnimations = ( AnimationListViewNode )DataViewNodeFactory.Create( "Blend Animations", Data.BlendAnimations, new[] { new ListItemNameProvider<Animation>( ( x, i ) => $"Animation {i}" ) } );

            if ( Bit29Data != null )
            {
                Bit29Data = ( AnimationBit29DataViewNode ) DataViewNodeFactory.Create( "Bit 29 Data", Data.Bit29Data );
                AddChildNode( Bit29Data );
            }

            AddChildNode( Animations );
            AddChildNode( BlendAnimations );
        }
        private static void ImportModelAndFixTargetIds(AnimationPack pack)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = ModuleFilterGenerator.GenerateFilter(new[] { FormatModuleUsageFlags.Import }, typeof(ModelPack)).Filter;
                dialog.AutoUpgradeEnabled = true;
                dialog.CheckPathExists = true;
                dialog.Title = "Select a model file.";
                dialog.ValidateNames = true;
                dialog.AddExtension = true;

                if (dialog.ShowDialog() != DialogResult.OK)
                    return;

                foreach (var animation in pack.Animations)
                    try
                    {
                        var model = Resource.Load<ModelPack>(dialog.FileName);
                        if (model.Model != null)
                            animation.FixTargetIds(model.Model);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }
            }
        }
    }
}
