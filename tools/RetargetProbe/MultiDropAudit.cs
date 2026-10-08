using System.Drawing;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using GFDLibrary;
using GFDLibrary.Animations;

internal static class MultiDropAudit
{
    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);Application.EnableVisualStyles();
        var asm=Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory,"GFDStudio.dll"));
        var type=asm.GetType("GFDStudio.GUI.Forms.MainForm")!;
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        using var form=(Form)Activator.CreateInstance(type)!;
        object? Field(string name)=>type.GetField(name,flags)!.GetValue(form);
        void Set(string name,object value)=>type.GetField(name,flags)!.SetValue(form,value);
        object? Call(string name,params object?[] args)=>type.GetMethod(name,flags)!.Invoke(form,args);
        var fixture=Path.Combine(folder,"files");Directory.CreateDirectory(fixture);
        var rig=Path.Combine(fixture,"animation-rig.GMD");
        File.Copy(@"M:\_P_backup\p5d modding\game\Image0\data\ps4\dance\player\p5\pc204_26.GMD",rig,true);
        var files=new[]{"zz-first.GAP","aa-second.GAP","mm-third.GAP"}.Select(n=>Path.Combine(fixture,n)).ToArray();
        for(int i=0;i<files.Length;i++)File.Copy($@"M:\_P_backup\p5d modding\game\Image0\data\data\dance\player\p5\204\pc204_00{new[]{2,3,5}[i]}_p.GAP",files[i],true);
        var checks=new List<string>();int result=0;
        form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-2400,-2400);form.Size=new Size(1500,900);form.ShowInTaskbar=false;
        form.Shown+=async (_,_)=>
        {
            try
            {
                while (Field("mCharacterAnimationFilterTextBox") == null) await Task.Delay(20);
                foreach(var name in new[]{"mCharacterBrowserScanCancellation","mCharacterBrowserAnimationLoadCancellation"})
                    ((System.Threading.CancellationTokenSource?)Field(name))?.Cancel();
                var model=Resource.Load<ModelPack>(rig);Set("mCharacterBrowserCurrentModelPack",model);Set("mCharacterBrowserCurrentModelPath",rig);
                var viewerType=asm.GetType("GFDStudio.GUI.Controls.ModelViewControl")!;
                var first=(Control)viewerType.GetProperty("Instance")!.GetValue(null)!;
                viewerType.GetMethod("LoadModel")!.Invoke(first,new object[]{model});
                var list=(ListBox)Field("mCharacterAnimationListBox")!;
                var filter=(TextBox)Field("mCharacterAnimationFilterTextBox")!;
                filter.Text="nothing-will-match-this";
                var data=new DataObject();data.SetData(DataFormats.FileDrop,new[]{files[0],rig,files[1],files[0],files[2]});
                var found=(string[])type.GetMethod("GetDroppedGaps",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object[]{data})!;
                if(!found.SequenceEqual(files,StringComparer.OrdinalIgnoreCase))throw new Exception("Drop extraction lost order or duplicates");
                checks.Add("All GAP files extracted in drop order; duplicate and non-GAP ignored");
                async Task Drop(string[] dropped)=>await (Task)Call("LoadDroppedShowroomGapsAsync",new object[]{dropped})!;
                void Pair(string a,string b)
                {
                    if(!(bool)type.GetProperty("IsPairedShowroom",flags)!.GetValue(form)!)throw new Exception("Split view did not open");
                    var entries=(Array)Field("mPairEntries")!;
                    string FileOf(int i)=>(string)entries.GetValue(i)!.GetType().GetProperty("PackPath")!.GetValue(entries.GetValue(i))!;
                    if(FileOf(0)!=a||FileOf(1)!=b)throw new Exception("Pair is not in drop order");
                }
                await Drop(found);Pair(files[0],files[1]);
                var packs=(System.Collections.IDictionary)Field("mDroppedAnimationPacks")!;
                if(packs.Count!=3)throw new Exception("Not all packs registered");
                int expected=files.Sum(f=>Resource.Load<AnimationPack>(f).Animations.Count);
                int droppedCount=list.Items.Cast<object>().Count(e=>(bool)e.GetType().GetProperty("IsDroppedForSession")!.GetValue(e)!);
                if(droppedCount!=expected)throw new Exception($"Menu lost clips: {droppedCount}/{expected}");
                if(list.SelectedItems.Count!=2)throw new Exception("More or fewer than two selected");
                if(filter.Text!="")throw new Exception("Dropped clips hidden by previous filter");
                checks.Add("Three files added to menu; first two opened and selected despite previous filter");
                await Drop(new[]{files[1],files[0],files[2]});Pair(files[1],files[0]);
                if(packs.Count!=3)throw new Exception("Repeat drop duplicated packs");
                checks.Add("Repeated drop keeps one copy and opens the new first two in supplied order");
                await Drop(new[]{files[2]});
                if((bool)type.GetProperty("IsPairedShowroom",flags)!.GetValue(form)!)throw new Exception("Single drop left split open");
                if(packs.Count!=3)throw new Exception("Single drop removed other dropped files");
                checks.Add("Single-file drop still opens one view and leaves all menu files available");
                await Drop(files);Pair(files[0],files[1]);
                var pane=Field("mPairSecondPane")!;var second=(Control)pane.GetType().GetProperty("Viewer")!.GetValue(pane)!;
                var mode=viewerType.GetProperty("AnimationPlayback")!;
                foreach(var v in new[]{first,second})
                {
                    mode.SetValue(v,Enum.Parse(mode.PropertyType,"Paused"));
                    viewerType.GetProperty("AnimationTime")!.SetValue(v,1.5d);
                    viewerType.GetMethod("FocusModelMotionFromCurrentCamera",flags)!.Invoke(v,new object[]{true});
                    using var paintGraphics=Graphics.FromHwnd(v.Handle);
                    viewerType.GetMethod("OnPaint",flags)!.Invoke(v,new object[]{new PaintEventArgs(paintGraphics,v.ClientRectangle)});
                }
                using var proof=new Bitmap(form.Width,form.Height);form.DrawToBitmap(proof,new Rectangle(Point.Empty,form.Size));
                using(var g=Graphics.FromImage(proof))foreach(var v in new[]{first,second})
                {
                    using var pixels=(Bitmap)viewerType.GetMethod("CapturePreviewSurface",flags)!.Invoke(v,new object[]{v.ClientSize})!;
                    g.DrawImageUnscaled(pixels,form.PointToClient(v.PointToScreen(Point.Empty)));
                }
                proof.Save(Path.Combine(folder,"multi_drop.png"));
                File.WriteAllText(Path.Combine(folder,"checks.json"),JsonSerializer.Serialize(checks,new JsonSerializerOptions{WriteIndented=true}));
            }
            catch(Exception ex){File.WriteAllText(Path.Combine(folder,"error.txt"),ex.ToString());result=1;}
            finally{form.Close();}
        };
        Application.Run(form);return result;
    }
}


