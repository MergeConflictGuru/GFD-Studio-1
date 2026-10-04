using System.Reflection;
using System.Runtime.Loader;
using System.Drawing;
using System.Windows.Forms;
using GFDLibrary;
using GFDLibrary.Animations;
using GFDLibrary.Models;

internal static class BlendAudit
{
    public static int Host(string folder)
    {
        Directory.CreateDirectory(folder);
        var assembly=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory,"GFDStudio.dll"));
        Application.EnableVisualStyles();
        var formType=assembly.GetType("GFDStudio.GUI.Forms.MainForm")!;
        using var form=(Form)Activator.CreateInstance(formType)!;
        form.StartPosition=FormStartPosition.Manual;form.Location=new Point(2200,120);
        form.Shown+=async (_,_)=>
        {
            try
            {
                var model=Resource.Load<ModelPack>(@"M:\_P_backup\p5d modding\game\Image0\data\ps4\dance\player\p5\pc205_26.GMD");
                formType.GetField("mCharacterBrowserCurrentModelPack",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(form,model);
                var animation=Resource.Load<AnimationPack>(@"M:\_P_backup\p5d modding\game\Image0\data\data\dance\player\p5\205\pc205_001_p.GAP").Animations[0];
                var clipType=assembly.GetType("GFDStudio.AnimationMatching.Integration.GfdTargetAnimationClip")!;
                var a=Activator.CreateInstance(clipType,new object[]{"ryuji-a","Ryuji source",model.Model,animation,30f})!;
                var b=Activator.CreateInstance(clipType,new object[]{"ryuji-b","Ryuji candidate",model.Model,animation,30f})!;
                var stitchType=assembly.GetType("GFDStudio.AnimationMatching.Stitching.StitchedAnimation")!;
                var stitched=Activator.CreateInstance(stitchType,new object[]{a,30,b,120,0f,true})!;
                var hostType=assembly.GetType("GFDStudio.AnimationMatching.Integration.IGfdAnimationMatchingHost")!;
                var task=(Task)hostType.GetMethod("GenerateAiBlendAsync")!.Invoke(form,new object[]{stitched,.6f,"Lose balance and stumble",CancellationToken.None})!;
                await task;
                var result=task.GetType().GetProperty("Result")!.GetValue(task)!;
                var bake=assembly.GetType("GFDStudio.AnimationMatching.Integration.GfdAnimationClipBaker")!.GetMethod("Bake")!;
                var joined=(Animation)bake.Invoke(null,new object[]{result,model.Model,model.Version,CancellationToken.None})!;
                var output=new AnimationPack(model.Version);output.Animations.Add(joined);output.Save(Path.Combine(folder,"host_joined.GAP"));
                for(var frame=30;frame<=49;frame+=6)
                    PoseRender.Draw(model.Model,AnimationPoseEvaluator.Evaluate(model.Model,joined,30/30f),
                        model.Model,AnimationPoseEvaluator.Evaluate(model.Model,joined,frame/30f),
                        Path.Combine(folder,$"host_frame_{frame}.png"),$"Actual GFD AI host: Ryuji join start / frame {frame}");
                File.WriteAllText(Path.Combine(folder,"success.txt"),"GFD MainForm.GenerateAiBlendAsync returned the AI clip and it baked to GAP.");
            }
            catch(Exception error) {File.WriteAllText(Path.Combine(folder,"error.txt"),error.ToString());Environment.ExitCode=1;}
            finally {form.Close();}
        };
        Application.Run(form);
        return Environment.ExitCode;
    }

    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        File.AppendAllText(Path.Combine(folder,"audit_progress.log"),"Started\n");
        var assembly=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory,"GFDStudio.dll"));
        Action drawUi = ()=>
        {
            Application.EnableVisualStyles();
            File.AppendAllText(Path.Combine(folder,"audit_progress.log"),"Creating controls\n");
            var control=(Control)Activator.CreateInstance(assembly.GetType("GFDStudio.AnimationMatching.UI.AnimationMatchingModeControl")!)!;
            File.AppendAllText(Path.Combine(folder,"audit_progress.log"),"Controls created\n");
            using var form=new Form {Text="AniMatch blend choices",Size=new Size(690,390),StartPosition=FormStartPosition.Manual,Location=new Point(2200,120)};
            form.Controls.Add(control);
            form.Shown+=(_,_)=>
            {
                var type=control.GetType();
                var ai=(RadioButton)type.GetField("_aiBlend",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(control)!;
                var no=(RadioButton)type.GetField("_noBlend",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(control)!;
                var simple=(RadioButton)type.GetField("_simpleBlend",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(control)!;
                ai.Checked=true;
                if (no.Checked || simple.Checked) throw new Exception("Blend radio group is not exclusive");
                var hint=(TextBox)type.GetField("_styleHint",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(control)!;
                
                if (hint.Enabled || hint.Text != "Acrobatic") throw new Exception("AI fixed style display is wrong");
                var timer=new System.Windows.Forms.Timer {Interval=700};
                timer.Tick+=(_,_)=>
                {
                    timer.Stop();
                    timer.Dispose();
                    using var bitmap=new Bitmap(form.Width,form.Height);
                    form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));
                    bitmap.Save(Path.Combine(folder,"blend_controls.png"));
                    form.Close();
                };
                timer.Start();
                // Keep the timer alive until its Tick closes the form.
                form.Tag=timer;
            };
            Application.Run(form);
        };
        drawUi();
        var model=Resource.Load<ModelPack>(@"M:\_P_backup\p5d modding\game\Image0\data\ps4\dance\player\p5\pc204_26.GMD");
        var file=@"C:\_coding\DaYoBuO\modssrc\dayobuo\pc204_ko_p.GAP";
        var body=Resource.Load<AnimationPack>(file).Animations[3];
        var typeClip=assembly.GetType("GFDStudio.AnimationMatching.Integration.GfdAnimationClip")!;
        var clip=Activator.CreateInstance(typeClip,new object[]{"tail-proof","Ann KO 3",model.Model,(Func<Animation>)(()=>Resource.Load<AnimationPack>(file).Animations[3]),30f})!;
        typeClip.GetProperty("SourcePackPath")!.SetValue(clip,file);
        typeClip.GetProperty("SourceClipIndex")!.SetValue(clip,3);
        var preview=typeClip.GetMethod("CreateTargetPreviewClip",new[]{typeof(Model)})!.Invoke(clip,new object[]{model.Model})!;
        var bake=assembly.GetType("GFDStudio.AnimationMatching.Integration.GfdAnimationClipBaker")!.GetMethod("Bake")!;
        var after=(Animation)bake.Invoke(null,new object[]{preview,model.Model,model.Version,CancellationToken.None})!;
        foreach(var frame in new[]{0,30,48})
            PoseRender.Draw(model.Model,AnimationPoseEvaluator.Evaluate(model.Model,body,frame/30f),
                model.Model,AnimationPoseEvaluator.Evaluate(model.Model,after,frame/30f),
                Path.Combine(folder,$"tail_frame_{frame}.png"),$"Ann KO 3 frame {frame}: body alone / accessory auto-load");
        return 0;
    }
}
