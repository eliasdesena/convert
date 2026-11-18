using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Convert.Core;

namespace Convert.Shell;

/// <summary>
/// The "Convert…" entry in Windows 11's context menu. Explorer loads it from the sparse package
/// (see packaging/AppxManifest.xml) into a COM surrogate (dllhost.exe), so a crash here never takes
/// Explorer down. Keep it tiny: it only decides whether to show itself and launches Convert.exe.
/// </summary>
[ComVisible(true)]
[Guid(ClassId)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class ExplorerCommand : IExplorerCommand
{
    public const string ClassId = "5E2B8C34-9A71-4D6F-B0E3-2C8F41A7D965";

    private const int S_OK = 0, E_NOTIMPL = unchecked((int)0x80004001), E_FAIL = unchecked((int)0x80004005);

    private static readonly string AppDir = Path.GetDirectoryName(typeof(ExplorerCommand).Assembly.Location)!;
    private static string AppExe => Path.Combine(AppDir, "Convert.exe");

    public int GetTitle(IShellItemArray? items, out IntPtr name)
    {
        name = Marshal.StringToCoTaskMemUni("Convert…");
        return S_OK;
    }

    public int GetIcon(IShellItemArray? items, out IntPtr icon)
    {
        icon = Marshal.StringToCoTaskMemUni($"{AppExe},0");
        return S_OK;
    }

    public int GetToolTip(IShellItemArray? items, out IntPtr tip)
    {
        tip = IntPtr.Zero;
        return E_NOTIMPL;
    }

    public int GetCanonicalName(out Guid guid)
    {
        guid = new Guid(ClassId);
        return S_OK;
    }

    /// <summary>Hidden unless every selected file is something Convert can handle, and they can go together.</summary>
    public int GetState(IShellItemArray? items, bool okToBeSlow, out ExpCmdState state)
    {
        state = ExpCmdState.Enabled;
        if (items == null) return S_OK;
        try
        {
            var kinds = new HashSet<MediaKind>();
            foreach (var path in Paths(items))
            {
                if (MediaKinds.Of(path) is not { } k) { state = ExpCmdState.Hidden; return S_OK; }
                kinds.Add(k);
            }
            if (kinds.Count == 0 || !MediaKinds.Compatible(kinds)) state = ExpCmdState.Hidden;
        }
        catch (Exception e)
        {
            Log(e);
            state = ExpCmdState.Hidden;
        }
        return S_OK;
    }

    public int Invoke(IShellItemArray? items, IntPtr bindCtx)
    {
        try
        {
            var paths = items == null ? [] : Paths(items).ToList();
            var args = string.Join(" ", paths.Select(Quote));
            // Stay well below the 32,767-character command-line limit.
            if (args.Length > 30_000)
            {
                var list = Path.Combine(Path.GetTempPath(), $"convert-{Guid.NewGuid():N}.txt");
                File.WriteAllLines(list, paths, Encoding.UTF8);
                args = "--files " + Quote(list);
            }
            AllowSetForegroundWindow(-1);   // let the new window come to the front
            Process.Start(new ProcessStartInfo(AppExe, args) { UseShellExecute = false, WorkingDirectory = AppDir })?.Dispose();
            return S_OK;
        }
        catch (Exception e)
        {
            Log(e);
            return E_FAIL;
        }
    }

    public int GetFlags(out ExpCmdFlags flags)
    {
        flags = ExpCmdFlags.Default;
        return S_OK;
    }

    public int EnumSubCommands(out IntPtr enumerator)
    {
        enumerator = IntPtr.Zero;
        return E_NOTIMPL;
    }

    private static IEnumerable<string> Paths(IShellItemArray items)
    {
        items.GetCount(out var count);
        for (uint i = 0; i < count; i++)
        {
            items.GetItemAt(i, out var item);
            try
            {
                if (item.GetDisplayName(SIGDN_FILESYSPATH, out var ptr) != S_OK || ptr == IntPtr.Zero) continue;
                try { yield return Marshal.PtrToStringUni(ptr)!; }
                finally { Marshal.FreeCoTaskMem(ptr); }
            }
            finally { Marshal.ReleaseComObject(item); }
        }
    }

    private const uint SIGDN_FILESYSPATH = 0x80058000;

    /// <summary>Quotes one argument for CommandLineToArgvW-style parsing.</summary>
    private static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in s)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
            else sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        return sb.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private static void Log(Exception e)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Convert");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "shell.log"), $"{DateTime.Now:O} {e}\n");
        }
        catch (Exception) { }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}

// COM interfaces from shobjidl_core.h

public enum ExpCmdState : uint { Enabled = 0, Disabled = 1, Hidden = 2, CheckBox = 4, Checked = 8, RadioCheck = 0x10 }

[Flags]
public enum ExpCmdFlags : uint { Default = 0, HasSubCommands = 1, HasSplitButton = 2, HideLabel = 4, IsSeparator = 8, HasLuaShield = 0x10 }

[ComImport, Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IExplorerCommand
{
    [PreserveSig] int GetTitle(IShellItemArray? items, out IntPtr name);
    [PreserveSig] int GetIcon(IShellItemArray? items, out IntPtr icon);
    [PreserveSig] int GetToolTip(IShellItemArray? items, out IntPtr tip);
    [PreserveSig] int GetCanonicalName(out Guid guid);
    [PreserveSig] int GetState(IShellItemArray? items, [MarshalAs(UnmanagedType.Bool)] bool okToBeSlow, out ExpCmdState state);
    [PreserveSig] int Invoke(IShellItemArray? items, IntPtr bindCtx);
    [PreserveSig] int GetFlags(out ExpCmdFlags flags);
    [PreserveSig] int EnumSubCommands(out IntPtr enumerator);
}

[ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItemArray
{
    [PreserveSig] int BindToHandler(IntPtr bindCtx, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetAttributes(int attribFlags, uint sfgaoMask, out uint attribs);
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetItemAt(uint index, out IShellItem item);
    [PreserveSig] int EnumItems(out IntPtr enumerator);
}

[ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItem
{
    [PreserveSig] int BindToHandler(IntPtr bindCtx, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetParent(out IShellItem parent);
    [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr name);
    [PreserveSig] int GetAttributes(uint sfgaoMask, out uint attribs);
    [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
}
