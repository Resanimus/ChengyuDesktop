using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ChengyuDock;

internal static class PackagedApps
{
    const string AppsFolder = @"shell:AppsFolder\";
    internal const string ChatGptAppId = "OpenAI.Codex_2p2nqsd0c76g0!App";
    const string ChatGptPackagePrefix = "OpenAI.Codex_";
    const string ChatGptPublisher = "_2p2nqsd0c76g0";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder? appId);

    internal static string? ProcessAppId(IntPtr process)
    {
        const int InsufficientBuffer = 122;
        uint length = 0;
        if (GetApplicationUserModelId(process, ref length, null) != InsufficientBuffer || length is 0 or > 256)
            return null;
        var id = new StringBuilder((int)length);
        return GetApplicationUserModelId(process, ref length, id) == 0 ? id.ToString() : null;
    }

    internal static string WindowTarget(WindowEntry window) =>
        window.AppId is { Length: > 0 } appId ? AppsFolder + appId : UpgradeLegacyTarget(window.Exe);

    internal static string UpgradeLegacyTarget(string path)
    {
        if (!Path.GetFileName(path).Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)) return path;
        string? appDirectory = Path.GetDirectoryName(path);
        string? packageDirectory = Path.GetDirectoryName(appDirectory);
        string? windowsApps = Path.GetDirectoryName(packageDirectory);
        string? package = Path.GetFileName(packageDirectory);
        if (string.Equals(Path.GetFileName(appDirectory), "app", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileName(windowsApps), "WindowsApps", StringComparison.OrdinalIgnoreCase) &&
            package != null && package.StartsWith(ChatGptPackagePrefix, StringComparison.OrdinalIgnoreCase) &&
            package.EndsWith(ChatGptPublisher, StringComparison.OrdinalIgnoreCase))
            return AppsFolder + ChatGptAppId;
        return path;
    }

    internal static bool TryGetId(string path, out string appId)
    {
        appId = path.StartsWith(AppsFolder, StringComparison.OrdinalIgnoreCase) ? path[AppsFolder.Length..] : "";
        int separator = appId.IndexOf('!');
        return separator > 0 && separator < appId.Length - 1 &&
            appId.IndexOf('_', 0, separator) > 0 &&
            appId.AsSpan(separator + 1).IndexOf('!') < 0 &&
            !appId.Contains('\\') && !appId.Contains('/') && !appId.Contains(':');
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    class ApplicationActivationManager { }

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationActivationManager
    {
        void ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments, uint options, out uint processId);
    }

    internal static uint Activate(string appId)
    {
        var manager = (IApplicationActivationManager)new ApplicationActivationManager();
        try
        {
            manager.ActivateApplication(appId, null, 0, out uint processId);
            return processId;
        }
        finally { Marshal.ReleaseComObject(manager); }
    }
}
