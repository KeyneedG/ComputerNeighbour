using System.Runtime.InteropServices;
using System.Text;

namespace ComputerNeighbour.Browser;

/// <summary>
/// Minimal, read-only IUIAutomation ABI. Vtable positions and signatures follow Microsoft's
/// UIAutomationClient.h (win32metadata); explicit ownership keeps COM pointers on their MTA.
/// No WindowsDesktop/WPF framework or separately installed package is required.
/// </summary>
internal sealed class NativeUia : IDisposable
{
    internal const int DocumentControlType = 50030;
    internal const int IsOffscreenProperty = 30022;
    internal const int IsPasswordProperty = 30019;
    internal const int ValueProperty = 30045;
    private const int EditControlType = 50004, TextControlType = 50020;
    private readonly ComPointer automation;
    private readonly bool uninitialize;

    internal NativeUia()
    {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("The portable browser reader requires Windows x64.");
        var initialized = CoInitializeEx(IntPtr.Zero, 0); // COINIT_MULTITHREADED
        Marshal.ThrowExceptionForHR(initialized);
        uninitialize = true;
        var iid = new Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");
        var clsid = new Guid("e22ad333-b25f-460c-83d0-0581107395c9"); // CUIAutomation8
        var result = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out var pointer);
        if (result < 0)
        {
            clsid = new Guid("ff48dba4-60ef-4201-aa87-54103eef594e");
            result = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out pointer);
        }
        if (result < 0) { CoUninitialize(); uninitialize = false; Marshal.ThrowExceptionForHR(result); }
        automation = new ComPointer(pointer);
    }

    internal ComPointer ElementFromHandle(IntPtr handle)
    {
        Marshal.ThrowExceptionForHR(Method<ElementFromHandleDelegate>(automation, 6)(automation.Handle, handle, out var element));
        return new(element);
    }

    internal ElementArray FindByControlType(ComPointer parent, int type, int scope = 4)
    {
        var variant = new Variant { Type = 3, Integer = type }; // VT_I4
        Marshal.ThrowExceptionForHR(Method<CreatePropertyConditionDelegate>(automation, 23)(automation.Handle, 30003, variant, out var conditionPointer));
        using var condition = new ComPointer(conditionPointer);
        Marshal.ThrowExceptionForHR(Method<FindAllDelegate>(parent, 6)(parent.Handle, scope, condition.Handle, out var array));
        return new(array);
    }

    internal string? GetString(ComPointer element, int property)
    {
        var result = Method<GetPropertyDelegate>(element, 10)(element.Handle, property, out var value);
        try { return result >= 0 && value.Type == 8 && value.Pointer != IntPtr.Zero ? Marshal.PtrToStringBSTR(value.Pointer) : null; }
        finally { VariantClear(ref value); }
    }

    internal bool? GetBoolean(ComPointer element, int property)
    {
        var result = Method<GetPropertyDelegate>(element, 10)(element.Handle, property, out var value);
        try { return result >= 0 && value.Type == 11 ? (short)value.Integer != 0 : null; }
        finally { VariantClear(ref value); }
    }

    internal string? ReadAddress(ComPointer root, CancellationToken token)
    {
        if (Method<GetPointerDelegate>(automation, 14)(automation.Handle, out var walkerPointer) < 0 || walkerPointer == IntPtr.Zero) return null;
        using var walker = new ComPointer(walkerPointer);
        using var edits = FindByControlType(root, EditControlType);
        for (var i = 0; i < Math.Min(edits.Count, 128); i++)
        {
            token.ThrowIfCancellationRequested();
            using var edit = edits.Get(i);
            if (GetBoolean(edit, IsOffscreenProperty) == true || GetBoolean(edit, IsPasswordProperty) == true) continue;
            var name = (GetString(edit, 30005) ?? "") + " " + (GetString(edit, 30011) ?? "");
            if (!new[] { "address", "omnibox", "urlbar", "адрес", "search or enter", "поиска и" }.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
            // Page form inputs can also be named "address". Never mistake their values for
            // the browser address bar: browser chrome lies outside a Document ancestor.
            if (!OutsideDocument(edit, walker, token)) continue;
            var url = WebAddress(GetString(edit, ValueProperty));
            if (url is not null) return url;
        }
        return null;
    }

    private bool OutsideDocument(ComPointer element, ComPointer walker, CancellationToken token)
    {
        ComPointer current = element;
        var ownsCurrent = false;
        try
        {
            for (var depth = 0; depth < 32; depth++)
            {
                token.ThrowIfCancellationRequested();
                var result = Method<GetPropertyDelegate>(current, 10)(current.Handle, 30003, out var type);
                try
                {
                    if (result < 0 || type.Type != 3) return false;
                    if (type.Integer == DocumentControlType) return false;
                    if (type.Integer == 50032) return true; // Window
                }
                finally { VariantClear(ref type); }
                var parentResult = Method<GetParentDelegate>(walker, 3)(walker.Handle, current.Handle, out var parent);
                if (parentResult < 0) return false;
                if (parent == IntPtr.Zero) return true;
                if (ownsCurrent) current.Dispose();
                current = new(parent); ownsCurrent = true;
            }
            return false; // Unknown/deep ancestry is not evidence of a browser toolbar.
        }
        finally { if (ownsCurrent) current.Dispose(); }
    }

    internal static string? WebAddress(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "file" ? value : null;

    internal string ReadDocumentText(ComPointer document, int limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var iid = new Guid("32eba289-3583-42c9-9c59-3b6d9a1e9b6a");
        if (Method<GetPatternAsDelegate>(document, 14)(document.Handle, 10014, ref iid, out var patternPointer) < 0 || patternPointer == IntPtr.Zero) return "";
        using var pattern = new ComPointer(patternPointer);
        if (Method<GetPointerDelegate>(pattern, 7)(pattern.Handle, out var rangePointer) < 0 || rangePointer == IntPtr.Zero) return "";
        using var range = new ComPointer(rangePointer);
        token.ThrowIfCancellationRequested();
        var result = Method<GetTextDelegate>(range, 12)(range.Handle, limit, out var bstr);
        try { return result >= 0 && bstr != IntPtr.Zero ? Marshal.PtrToStringBSTR(bstr) : ""; }
        finally { if (bstr != IntPtr.Zero) Marshal.FreeBSTR(bstr); }
    }

    internal string ReadAccessibleText(ComPointer document, int limit, CancellationToken token, out bool truncated)
    {
        using var texts = FindByControlType(document, TextControlType);
        var result = new StringBuilder();
        truncated = texts.Count > 2000;
        string? previous = null;
        for (var i = 0; i < Math.Min(texts.Count, 2000) && result.Length < limit; i++)
        {
            token.ThrowIfCancellationRequested();
            using var text = texts.Get(i);
            if (GetBoolean(text, IsPasswordProperty) == true) continue;
            var name = GetString(text, 30005);
            if (string.IsNullOrWhiteSpace(name) || name == previous) continue;
            previous = name;
            var remaining = limit - result.Length;
            result.Append(name.AsSpan(0, Math.Min(remaining, name.Length)));
            if (result.Length < limit) result.AppendLine();
        }
        truncated |= result.Length >= limit;
        return result.ToString();
    }

    public void Dispose() { automation.Dispose(); if (uninitialize) CoUninitialize(); }
    private static T Method<T>(ComPointer pointer, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(pointer.Handle), slot * IntPtr.Size));

    internal sealed class ComPointer(IntPtr pointer) : IDisposable
    {
        internal IntPtr Handle { get; private set; } = pointer != IntPtr.Zero ? pointer : throw new InvalidOperationException("UI Automation returned a null object.");
        public void Dispose() { if (Handle != IntPtr.Zero) { Marshal.Release(Handle); Handle = IntPtr.Zero; } }
    }

    internal sealed class ElementArray(IntPtr pointer) : IDisposable
    {
        private readonly ComPointer array = new(pointer);
        internal int Count { get { Marshal.ThrowExceptionForHR(Method<GetIntDelegate>(array, 3)(array.Handle, out var length)); return Math.Max(0, length); } }
        internal ComPointer Get(int index) { Marshal.ThrowExceptionForHR(Method<GetElementDelegate>(array, 4)(array.Handle, index, out var element)); return new(element); }
        public void Dispose() => array.Dispose();
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] internal ushort Type;
        [FieldOffset(8)] internal int Integer;
        [FieldOffset(8)] internal IntPtr Pointer;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ElementFromHandleDelegate(IntPtr self, IntPtr window, out IntPtr element);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreatePropertyConditionDelegate(IntPtr self, int property, Variant value, out IntPtr condition);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FindAllDelegate(IntPtr self, int scope, IntPtr condition, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetPropertyDelegate(IntPtr self, int property, out Variant value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetPatternAsDelegate(IntPtr self, int pattern, ref Guid iid, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetPointerDelegate(IntPtr self, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetTextDelegate(IntPtr self, int maxLength, out IntPtr bstr);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetIntDelegate(IntPtr self, out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetElementDelegate(IntPtr self, int index, out IntPtr element);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetParentDelegate(IntPtr self, IntPtr element, out IntPtr parent);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);
    [DllImport("oleaut32.dll")] private static extern int VariantClear(ref Variant variant);
}
