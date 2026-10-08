using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace GFDStudio.AnimationMatching.UI;

public sealed class AnimationExportDialog : Form
{
    private readonly TextBox _file=new() {Dock=DockStyle.Fill,Text="animation_match_stitched.GAP"};
    private readonly CheckBox _parts=new() {Text="Separate parts (source / blend / candidate)",AutoSize=true};
    public string FileName=>_file.Text;
    public bool SeparateParts=>_parts.Checked;
    public AnimationExportDialog(bool allowParts=true)
    {
        Text="Export animation";ClientSize=new Size(560,130);FormBorderStyle=FormBorderStyle.FixedDialog;
        StartPosition=FormStartPosition.CenterParent;MinimizeBox=false;MaximizeBox=false;ShowInTaskbar=false;
        var table=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(10),ColumnCount=2,RowCount=3};
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,90));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute,32));table.RowStyles.Add(new RowStyle(SizeType.Absolute,32));table.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        var browse=new Button {Text="Browse…",Dock=DockStyle.Fill};
        bool ChooseFile()
        {
            using var save=new SaveFileDialog {Filter="Animation pack (*.GAP)|*.GAP",DefaultExt="GAP",AddExtension=true,OverwritePrompt=true,FileName=_file.Text};
            if(save.ShowDialog(this)!=DialogResult.OK)return false;
            _file.Text=save.FileName;return true;
        }
        browse.Click+=(_,_)=>ChooseFile();
        table.Controls.Add(_file,0,0);table.Controls.Add(browse,1,0);
        _parts.Visible=allowParts;table.Controls.Add(_parts,0,1);table.SetColumnSpan(_parts,2);
        var buttons=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft};
        var export=new Button {Text="Export",AutoSize=true};var cancel=new Button {Text="Cancel",AutoSize=true,DialogResult=DialogResult.Cancel};
        export.Click+=(_,_)=>
        {
            if(!ChooseFile())return;
            DialogResult=DialogResult.OK;Close();
        };
        buttons.Controls.Add(export);buttons.Controls.Add(cancel);table.Controls.Add(buttons,0,2);table.SetColumnSpan(buttons,2);
        Controls.Add(table);AcceptButton=export;CancelButton=cancel;
    }
}
