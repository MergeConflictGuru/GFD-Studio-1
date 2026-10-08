using System;
using System.Linq;
using System.Numerics;
using GFDLibrary.Animations;
using GFDLibrary.Models;

namespace GFDStudio.AnimationMatching.Integration;

public static class GfdAnimationRangeExporter
{
    internal static Animation CopyAuthoredRange(Animation source,uint version,int first,int last,float fps)
    {
        float start=first/fps,end=last/fps;
        var output=new Animation(version){Duration=end-start};
        foreach(var controller in source.Controllers.Where(c=>c.TargetKind==TargetKind.Node))
        {
            var copy=new AnimationController(version){TargetKind=controller.TargetKind,TargetId=controller.TargetId,TargetName=controller.TargetName};
            foreach(var layer in controller.Layers)
            {
                if(!layer.HasPRSKeyFrames)continue;
                var keys=layer.Keys.OfType<PRSKey>().OrderBy(key=>key.Time).ToArray();
                if(keys.Length==0)continue;
                int begin=0;while(begin+1<keys.Length && keys[begin+1].Time<=start)begin++;
                int finish=begin;while(finish<keys.Length-1 && keys[finish].Time<end)finish++;
                var copiedLayer=new AnimationLayer(version){KeyType=layer.KeyType,PositionScale=layer.PositionScale,ScaleScale=layer.ScaleScale};
                for(int i=begin;i<=finish;i++)
                {
                    var key=keys[i];
                    copiedLayer.Keys.Add(new PRSKey(key.Type){Time=key.Time-start,Position=key.Position,Rotation=key.Rotation,Scale=key.Scale,
                        HasPosition=key.HasPosition,HasRotation=key.HasRotation,HasScale=key.HasScale});
                }
                // Keep bracketing authored keys, even outside the visible range.
                // Boundary interpolation happens during drawing, never while copying.
                copy.Layers.Add(copiedLayer);
            }
            if(copy.Layers.Count>0)output.Controllers.Add(copy);
        }
        return output;
    }

    public static Animation Extract(Animation source, Model model, uint version, int first, int last, float fps)
    {
        var sampler=new AnimationLocalPoseSampler(model,source);
        var nodes=model.Nodes.ToArray();
        var output=new Animation(version) {Duration=(last-first)/fps};
        var layers=new AnimationLayer[nodes.Length];
        for(int i=0;i<nodes.Length;i++)
        {
            var controller=new AnimationController(version) {TargetKind=TargetKind.Node,TargetId=i,TargetName=nodes[i].Name};
            layers[i]=new AnimationLayer(version) {KeyType=KeyType.NodePRS,PositionScale=Vector3.One,ScaleScale=Vector3.One};
            controller.Layers.Add(layers[i]);output.Controllers.Add(controller);
        }
        for(int frame=first;frame<=last;frame++)
        {
            var pose=sampler.Evaluate(frame/fps);
            for(int i=0;i<nodes.Length;i++)layers[i].Keys.Add(new PRSKey(KeyType.NodePRS) {Time=(frame-first)/fps,Position=pose[i].Translation,Rotation=pose[i].Rotation,Scale=pose[i].Scale});
        }
        return output;
    }
}
