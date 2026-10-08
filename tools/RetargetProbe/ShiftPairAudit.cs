using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using GFDLibrary;
using GFDLibrary.Animations;

internal static class ShiftPairAudit
{
    [DllImport("user32.dll")] static extern bool GetKeyboardState(byte[] keys);
    [DllImport("user32.dll")] static extern bool SetKeyboardState(byte[] keys);
    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder); Application.EnableVisualStyles();
        var asm=Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory,"GFDStudio.dll"));
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var ft=asm.GetType("GFDStudio.GUI.Forms.MainForm")!;
        using var form=(Form)Activator.CreateInstance(ft)!;
        form.StartPosition=FormStartPosition.Manual; form.Location=new Point(-2400,-2400);
        form.Size=new Size(1400,850); form.ShowInTaskbar=false; form.Show(); Application.DoEvents();
        object Field(object o,string n)=>o.GetType().GetField(n,flags)!.GetValue(o)!;
        object? Get(object o,string n)=>o.GetType().GetProperty(n)!.GetValue(o);
        void Set(object o,string n,object v)=>o.GetType().GetProperty(n)!.SetValue(o,v);
        object? Call(object o,string n,params object?[] a)=>o.GetType().GetMethod(n,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(o,a);
        foreach (var name in new[]{"mCharacterBrowserScanCancellation","mCharacterBrowserAnimationLoadCancellation"})
            ((System.Threading.CancellationTokenSource?)Field(form,name))?.Cancel();
        var vt=asm.GetType("GFDStudio.GUI.Controls.ModelViewControl")!;
        var first=(Control)vt.GetProperty("Instance")!.GetValue(null)!;
        Call(form,"ShowPairedShowroom"); Application.DoEvents();
        var pane=Field(form,"mPairSecondPane"); var second=(Control)Get(pane,"Viewer")!;
        var a=Field(form,"mAnimationMatchTimeline");var b=Get(pane,"Timeline")!;
        var model=Resource.Load<ModelPack>(@"M:\_P_backup\p5d modding\game\Image0\data\ps4\dance\player\p5\pc204_26.GMD");
        var animation=Resource.Load<AnimationPack>(@"M:\_P_backup\p5d modding\game\Image0\data\data\dance\player\p5\204\pc204_003_p.GAP").Animations[0];
        animation.FixTargetIds(model.Model);
        Call(first,"LoadModel",model); Call(first,"LoadAnimation",animation,true);
        Call(pane,"LoadClip",model,animation,"Shift-linked Ann dance",(int)(animation.Duration*30)+1);
        Set(a,"FrameCount",(int)(animation.Duration*30)+1);
        // Pause both so rendered proof shows the same authored pose.
        var playback=vt.GetProperty("AnimationPlayback")!.PropertyType;
        Set(first,"AnimationPlayback",Enum.Parse(playback,"Paused"));Set(second,"AnimationPlayback",Enum.Parse(playback,"Paused"));
        var mainSeek=(TrackBar)Field(form,"mAnimationTrackBar");var secondSeek=(TrackBar)Field(pane,"mSeek");
        var keyboard=new byte[256];GetKeyboardState(keyboard);
        var checks=new List<string>();
        void Shift(bool on){Application.DoEvents();var keys=(byte[])keyboard.Clone();keys[16]=keys[160]=keys[161]=(byte)(on?128:0);for(int attempt=0;attempt<5;attempt++){if(!SetKeyboardState(keys))throw new Exception("SetKeyboardState failed");if(((Control.ModifierKeys&Keys.Shift)!=0)==on)return;}throw new Exception("Thread Shift modifier was not set");}
        void Seek(TrackBar bar,int milliseconds){bar.Value=milliseconds;typeof(TrackBar).GetMethod("OnScroll",flags)!.Invoke(bar,new object[]{EventArgs.Empty});}
        void EqualTime(double seconds){if(Math.Abs((double)Get(first,"AnimationTime")!-seconds)>.001 || Math.Abs((double)Get(second,"AnimationTime")!-seconds)>.001)throw new Exception("Seek times differ");}
        void Mouse(object o,string n,MouseButtons button,int x,int y,int delta=0)=>Call(o,n,new MouseEventArgs(button,1,x,y,delta));
        string Camera(object o){var c=Field(o,"mCamera");return string.Join("|",new[]{"Translation","Offset","ModelTranslation","ModelRotation","FieldOfView"}.Select(n=>Get(c,n)?.ToString()));}
        try
        {
            Shift(false);Seek(mainSeek,750);Seek(secondSeek,1250);
            if((double)Get(first,"AnimationTime")! == (double)Get(second,"AnimationTime")!)throw new Exception("Seek unexpectedly linked without Shift");
            Shift(true);Seek(mainSeek,1500);EqualTime(1.5);checks.Add("Shift seek from left copied seconds to right");
            Seek(secondSeek,1750);EqualTime(1.75);checks.Add("Shift seek from right copied seconds to left");
            Shift(false);Seek(mainSeek,2000);if(Math.Abs((double)Get(second,"AnimationTime")!-1.75)>.001)throw new Exception("No-Shift seek altered right");
            checks.Add("Seeking stays independent without Shift");
            Shift(true);Mouse(a,"OnMouseDown",MouseButtons.Left,100,22);Mouse(a,"OnMouseMove",MouseButtons.Left,220,22);
            if(!Equals(Get(a,"Selection"),Get(b,"Selection")))throw new Exception("Live highlight differed");
            Mouse(a,"OnMouseUp",MouseButtons.Left,220,22);checks.Add("Shift highlight copied during drag from left");
            Mouse(b,"OnMouseDown",MouseButtons.Left,30,22);Mouse(b,"OnMouseMove",MouseButtons.Left,80,22);Mouse(b,"OnMouseUp",MouseButtons.Left,80,22);
            if(!Equals(Get(a,"Selection"),Get(b,"Selection")))throw new Exception("Right highlight differed");checks.Add("Shift highlight copied from right");
            Mouse(b,"OnMouseClick",MouseButtons.Right,40,22);if(Get(a,"Selection")!=null || Get(b,"Selection")!=null)throw new Exception("Shift clear differed");
            Shift(false);Call(a,"SetSelection",((int start,int end)?)(5,15));if(Get(b,"Selection")!=null)throw new Exception("Programmatic selection linked");checks.Add("No-Shift and programmatic selections stay independent");
            Call(a,"ClearSelection");
            Shift(true);Mouse(first,"OnMouseWheel",MouseButtons.None,20,20,120);if(Camera(first)!=Camera(second))throw new Exception("Shift zoom differed");checks.Add("Shift zoom copied all camera fields");
            foreach(var button in new[]{MouseButtons.Left,MouseButtons.Right,MouseButtons.Middle})
            {Mouse(second,"OnMouseDown",button,60,60);Mouse(second,"OnMouseMove",button,75,72);if(Camera(first)!=Camera(second))throw new Exception("Shift camera drag differed: "+button);Mouse(second,"OnMouseUp",button,75,72);}
            checks.Add("Shift orbit, pan and depth drag copied from right");
            Shift(false);string rightCamera=Camera(second);Mouse(first,"OnMouseWheel",MouseButtons.None,20,20,-120);if(Camera(second)!=rightCamera)throw new Exception("No-Shift camera linked");checks.Add("Camera stays independent without Shift");
            // Different clip lengths clamp seconds and highlighted frames.
            var shortClip=new Animation(model.Version){Duration=1};
            Call(pane,"LoadClip",model,shortClip,"One-second clip",31);
            Shift(true);Seek(mainSeek,2500);if(Math.Abs((double)Get(second,"AnimationTime")!-1)>.001)throw new Exception("Seek not clamped");
            Call(a,"SetSelection",((int start,int end)?)(40,80));Call(form,"SyncPairSelection",true);
            if(!Equals(Get(b,"Selection"),((int start,int end)?)(30,30)))throw new Exception("Selection not clamped");checks.Add("Shorter clip clamps seek and highlight");
            // Restore same clip and capture actual GL front buffers with timeline controls.
            Call(pane,"LoadClip",model,animation,"Shift-linked Ann dance",(int)(animation.Duration*30)+1);
            Set(second,"AnimationPlayback",Enum.Parse(playback,"Paused"));
            Call(first,"ResetCamera");Call(second,"ResetCamera");
            var count=(int)Get(a,"FrameCount")!;Call(a,"SetSelection",((int start,int end)?)(count/5,count*2/5));Call(form,"SyncPairSelection",true);
            Seek(mainSeek,1500);EqualTime(1.5);
            Call(first,"FocusModelMotionFromCurrentCamera",true);Call(form,"SyncPairCamera",first,true);
            Shift(false);Application.DoEvents();Thread.Sleep(100);Application.DoEvents();
            Call(first,"OnPaint",new PaintEventArgs(Graphics.FromHwnd(first.Handle),first.ClientRectangle));
            Call(second,"OnPaint",new PaintEventArgs(Graphics.FromHwnd(second.Handle),second.ClientRectangle));
            using var proof=new Bitmap(form.Width,form.Height);form.DrawToBitmap(proof,new Rectangle(Point.Empty,form.Size));
            using(var g=Graphics.FromImage(proof))
                foreach(var viewer in new[]{first,second})
                {using var pixels=(Bitmap)Call(viewer,"CapturePreviewSurface",viewer.ClientSize)!;var pt=form.PointToClient(viewer.PointToScreen(Point.Empty));g.DrawImageUnscaled(pixels,pt);}
            proof.Save(Path.Combine(folder,"shift_split.png"));
            File.WriteAllText(Path.Combine(folder,"checks.json"),JsonSerializer.Serialize(checks,new JsonSerializerOptions{WriteIndented=true}));
        }
        finally{SetKeyboardState(keyboard);form.Close();}
        return 0;
    }
}


