using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ComputerNeighbour.Browser;

namespace ComputerNeighbour.Platform;

public sealed record OpenTextDocument(string? Text,string? UnavailableReason=null);

public interface IOpenDocumentReader
{
    Task<OpenTextDocument?> FindAsync(string fileName,bool read,CancellationToken ct);
}

/// <summary>Read a matching Notepad document without switching tabs, typing or using the clipboard.</summary>
public sealed class WindowsDocumentReader(PlatformOptions options) : IOpenDocumentReader
{
    private const int TabItemControlType=50019, EditControlType=50004, NameProperty=30005, IsSelectedProperty=30079;
    private readonly object workerGate=new();
    private Task<OpenTextDocument?>? worker;

    public async Task<OpenTextDocument?> FindAsync(string fileName,bool read,CancellationToken ct)
    {
        if(!OperatingSystem.IsWindows())return null;
        ct.ThrowIfCancellationRequested();
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        var duration=TimeSpan.FromSeconds(4);
        timeout.CancelAfter(duration);
        var token=timeout.Token;
        Task<OpenTextDocument?> operation;
        lock(workerGate)
        {
            if(worker is {IsCompleted:false})throw new TimeoutException("The previous Notepad accessibility request is still pending.");
            var completion=new TaskCompletionSource<OpenTextDocument?>(TaskCreationOptions.RunContinuationsAsynchronously);
            worker=operation=completion.Task;
            _=operation.ContinueWith(t=>_=t.Exception,CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            var thread=new Thread(()=>
            {
                try {completion.TrySetResult(Find(fileName,read,token));}
                catch(OperationCanceledException){completion.TrySetCanceled(token);}
                catch(Exception ex){completion.TrySetException(ex);}
            }) {IsBackground=true,Name="ComputerNeighbour Notepad reader"};
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }
        try{return await operation.WaitAsync(duration,ct);}
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){throw new TimeoutException("Notepad did not respond to the accessibility check in time.");}
    }

    private OpenTextDocument? Find(string fileName,bool read,CancellationToken ct)
    {
        var windows=new List<NotepadWindow>();
        EnumWindows((handle,_) =>
        {
            if(windows.Count>=30)return false;
            if(!IsWindowVisible(handle))return true;
            GetWindowThreadProcessId(handle,out var pid);
            try
            {
                using var process=Process.GetProcessById((int)pid);
                if(!process.ProcessName.Equals("Notepad",StringComparison.OrdinalIgnoreCase))return true;
                var title=WindowTitle(handle);
                if(options.ExcludedWindowTitles.Any(s=>!string.IsNullOrWhiteSpace(s)&&title.Contains(s,StringComparison.OrdinalIgnoreCase)))return true;
                windows.Add(new(handle,pid,title));
            }
            catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or Win32Exception) {}
            return true;
        },IntPtr.Zero);
        if(windows.Count==0)return null;

        using var automation=new NativeUia();
        var active=new List<NotepadWindow>();
        var isOpen=false;
        foreach(var window in windows)
        {
            ct.ThrowIfCancellationRequested();
            using var root=automation.ElementFromHandle(window.Handle);
            var (found,selected)=MatchDocument(automation,root,window.Title,fileName,ct);
            isOpen|=found;
            if(selected&&!IsIconic(window.Handle))active.Add(window);
        }
        if(!isOpen)return null;
        if(!read)return new(null);
        if(active.Count>1)return new(null,"More than one open Notepad document matches this filename. Close the duplicates or give me the exact file path.");
        if(active.Count==0)return new(null,"The matching Notepad tab is inactive or minimized. Select and restore that tab so I can read its current contents.");

        var match=active[0];
        using var selectedRoot=automation.ElementFromHandle(match.Handle);
        if(!StillSelected(automation,selectedRoot,match,fileName,ct))return new(null,"The selected Notepad document changed. Try again on the intended tab.");
        var text=ReadEditor(automation,selectedRoot,ct);
        if(!StillSelected(automation,selectedRoot,match,fileName,ct))return new(null,"The selected Notepad document changed during reading. Try again on the intended tab.");
        return text is null?new(null,"Notepad did not expose the open document's text through Windows accessibility."):new(text);
    }

    private static (bool Found,bool Selected) MatchDocument(NativeUia automation,NativeUia.ComPointer root,string title,string fileName,CancellationToken ct)
    {
        var titleMatches=SameName(title,fileName);
        foreach(var suffix in new[]{" - Notepad"," — Notepad"," - Блокнот"," — Блокнот"})
            if(title.EndsWith(suffix,StringComparison.OrdinalIgnoreCase))
                titleMatches|=SameName(title[..^suffix.Length],fileName);
        using var tabs=automation.FindByControlType(root,TabItemControlType);
        var found=false;
        var selected=false;
        var hasSelectionState=false;
        for(var i=0;i<Math.Min(tabs.Count,128);i++)
        {
            ct.ThrowIfCancellationRequested();
            using var tab=tabs.Get(i);
            if(!SameName(automation.GetString(tab,NameProperty),fileName))continue;
            found=true;
            var selection=automation.GetBoolean(tab,IsSelectedProperty);
            hasSelectionState|=selection.HasValue;
            selected|=selection==true;
        }
        return (found||titleMatches,selected||(!hasSelectionState&&titleMatches));
    }

    private static bool StillSelected(NativeUia automation,NativeUia.ComPointer root,NotepadWindow window,string fileName,CancellationToken ct)
    {
        GetWindowThreadProcessId(window.Handle,out var pid);
        return pid==window.Pid&&IsWindowVisible(window.Handle)&&!IsIconic(window.Handle)
            &&WindowTitle(window.Handle)==window.Title&&MatchDocument(automation,root,window.Title,fileName,ct).Selected;
    }

    private static string? ReadEditor(NativeUia automation,NativeUia.ComPointer root,CancellationToken ct)
    {
        const int limit=12001;
        foreach(var controlType in new[]{NativeUia.DocumentControlType,EditControlType})
        {
            using var editors=automation.FindByControlType(root,controlType);
            for(var i=0;i<Math.Min(editors.Count,32);i++)
            {
                ct.ThrowIfCancellationRequested();
                using var editor=editors.Get(i);
                if(automation.GetBoolean(editor,NativeUia.IsOffscreenProperty)==true||automation.GetBoolean(editor,NativeUia.IsPasswordProperty)==true)continue;
                if(controlType==EditControlType&&automation.GetString(editor,30012) is not ("Edit" or "RichEditD2DPT"))continue;
                var text=automation.ReadDocumentText(editor,limit,ct);
                if(text is not null)return text;
                // The Win32 edit control exposes Value even when it does not expose TextPattern.
                // Do not read Find/Replace fields or other generic edits from modern Notepad.
                var value=automation.GetString(editor,NativeUia.ValueProperty);
                if(value is not null)return value[..Math.Min(value.Length,limit)];
            }
        }
        return null;
    }

    private static bool SameName(string? name,string fileName)
    {
        if(name is null)return false;
        name=name.Trim().TrimStart('*').TrimEnd('*',' ','●');
        return name.Equals(fileName,StringComparison.OrdinalIgnoreCase);
    }

    private static string WindowTitle(IntPtr handle)
    {
        var text=new StringBuilder(2048);
        GetWindowText(handle,text,text.Capacity);
        return text.ToString();
    }

    private sealed record NotepadWindow(IntPtr Handle,uint Pid,string Title);
    private delegate bool EnumWindowsCallback(IntPtr window,IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback,IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window,StringBuilder text,int maxCount);
}
