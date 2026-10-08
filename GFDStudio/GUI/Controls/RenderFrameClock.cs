using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace GFDStudio.GUI.Controls;

internal sealed class RenderFrameClock : IDisposable
{
    // https://learn.microsoft.com/windows/win32/api/synchapi/nf-synchapi-createwaitabletimerexw
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes,string name,uint flags,uint access);
    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer,ref long dueTime,int period,IntPtr callback,IntPtr argument,bool resume);
    private sealed class TimerWaitHandle : WaitHandle
    {
        public TimerWaitHandle(SafeWaitHandle timer){SafeWaitHandle=timer;}
    }
    private readonly Control _owner;
    private readonly Action _tick;
    private readonly int _frequency;
    private readonly ManualResetEvent _stop=new(false);
    private int _disposed,_pending;
    private readonly object _lifetime=new();
    public RenderFrameClock(Control owner,Action tick,int frequency=60)
    {
        _owner=owner;_tick=tick;_frequency=frequency;
        new Thread(Run) {IsBackground=true,Name="60 Hz UI frame clock"}.Start();
    }
    private void Run()
    {
        var handle=CreateWaitableTimerExW(IntPtr.Zero,null,2,0x00100002);
        if(handle.IsInvalid){handle.Dispose();handle=CreateWaitableTimerExW(IntPtr.Zero,null,0,0x00100002);}
        if(handle.IsInvalid){Trace.TraceError("Cannot create render frame clock");handle.Dispose();return;}
        using var timer=new TimerWaitHandle(handle);
        var waits=new WaitHandle[]{_stop,timer};
        double period=Stopwatch.Frequency/(double)_frequency;
        double next=Stopwatch.GetTimestamp()+period;
        try
        {
            while(Volatile.Read(ref _disposed)==0)
            {
                double now=Stopwatch.GetTimestamp();
                long due=-Math.Max(1,(long)((next-now)*10000000d/Stopwatch.Frequency));
                if(!SetWaitableTimer(handle,ref due,0,IntPtr.Zero,IntPtr.Zero,false))break;
                if(WaitHandle.WaitAny(waits)==0)break;
                next+=period;
                now=Stopwatch.GetTimestamp();if(next<now-period)next=now+period;
                if(!_owner.IsHandleCreated || _owner.IsDisposed || Interlocked.CompareExchange(ref _pending,1,0)!=0)continue;
                try
                {
                    _owner.BeginInvoke((Action)(()=>
                    {
                        try {if(Volatile.Read(ref _disposed)==0 && !_owner.IsDisposed && _owner.Visible)_tick();}
                        finally{Volatile.Write(ref _pending,0);}
                    }));
                }
                catch(InvalidOperationException){Volatile.Write(ref _pending,0);}
            }
        }
        finally{lock(_lifetime){Volatile.Write(ref _disposed,1);_stop.Dispose();}}
    }
    public void Stop()=>Dispose();
    public void Dispose(){lock(_lifetime){if(_disposed==0){Volatile.Write(ref _disposed,1);_stop.Set();}}}
}


