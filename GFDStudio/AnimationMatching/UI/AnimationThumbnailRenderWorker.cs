using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using GFDLibrary;
using GFDStudio.GUI.Controls;

namespace GFDStudio.AnimationMatching.UI;

internal sealed class AnimationThumbnailRenderWorker : IDisposable
{
    private readonly BlockingCollection<Action> _jobs=new();
    private readonly Thread _thread;
    private ModelPack _model;
    private ModelViewControl _renderer;
    private readonly int _uiThread=Environment.CurrentManagedThreadId;
    public ModelViewControl Surface=>_renderer;
    public AnimationThumbnailRenderWorker()
    {
        _renderer=new ModelViewControl(true) {Size=new Size(154,88),Visible=false};
        _renderer.Context?.MakeNoneCurrent();
        _thread=new Thread(Run) {IsBackground=true,Name="Animation preview renderer"};
        _thread.SetApartmentState(ApartmentState.STA);_thread.Start();
    }
    private void Run()
    {

        try
        {
            foreach(var job in _jobs.GetConsumingEnumerable())
            {
                job();
            }
        }
        finally
        {
            _renderer.Context?.MakeNoneCurrent();

        }
    }
    public Task<AnimationThumbnailRenderBatch> RenderAsync(ModelPack model,IReadOnlyList<AnimationThumbnailRenderRequest> requests,Rectangle[] destinations=null,Size surfaceSize=default,Func<bool> canPresent=null)
    {
        if(Environment.CurrentManagedThreadId!=_uiThread)throw new InvalidOperationException("Preview requests must originate on the UI thread.");
        _=_renderer.Handle; // UI creates HWND/GLFW window before worker gets the context.
        var result=new TaskCompletionSource<AnimationThumbnailRenderBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(_jobs.IsAddingCompleted){result.SetException(new ObjectDisposedException(nameof(AnimationThumbnailRenderWorker)));return result.Task;}
        try
        {
            _jobs.Add(()=>
            {
                try
                {

                    if(!ReferenceEquals(_model,model)){_renderer.LoadModel(model);_model=model;}
                    result.SetResult(_renderer.RenderAnimationThumbnailBatch(requests,destinations,surfaceSize,canPresent));
                }
                catch(Exception ex){result.SetException(ex);}
            });
        }
        catch(InvalidOperationException){result.TrySetException(new ObjectDisposedException(nameof(AnimationThumbnailRenderWorker)));}
        return result.Task;
    }
    public Task<Bitmap> CaptureAsync(Size size)
    {
        var result=new TaskCompletionSource<Bitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        _jobs.Add(()=>{try{result.SetResult(_renderer.CapturePreviewSurface(size));}catch(Exception ex){result.SetException(ex);}});
        return result.Task;
    }
    public void Dispose()
    {
        if(_jobs.IsAddingCompleted)return;
        _jobs.CompleteAdding();
        if(_thread.Join(2000) && !_renderer.IsDisposed)_renderer.Dispose();
    }
}







