using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace ChengyuDock;

// Only this product's value is changed. Registration is read from Windows, not a copied profile.
public sealed class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    readonly string _key, _value;
    public string Command { get; }
    public StartupRegistration(string executable, string dataRoot) : this(executable, dataRoot, RunKey, "ChengyuDesktop") { }
    internal StartupRegistration(string executable, string dataRoot, string key, string value)
    {
        _key = key; _value = value;
        Command = Quote(Path.GetFullPath(executable)) + " --autostart --data-dir " + Quote(Path.GetFullPath(dataRoot));
    }
    public string RegisteredCommand { get { using var key = Registry.CurrentUser.OpenSubKey(_key); return key?.GetValue(_value) as string ?? ""; } }
    public bool Enabled => RegisteredCommand.Length > 0;
    public bool UsesCurrentCopy => string.Equals(RegisteredCommand, Command, StringComparison.OrdinalIgnoreCase);
    public void SetEnabled(bool enabled)
    {
        if (enabled && Command.Length > 260) throw new InvalidOperationException("当前目录过长，无法登记自启动。请把澄屿移到较短的路径后重试。");
        using var key = Registry.CurrentUser.CreateSubKey(_key, true);
        if (enabled) key.SetValue(_value, Command, RegistryValueKind.String);
        else key.DeleteValue(_value, false);
        if (Enabled != enabled || (enabled && !UsesCurrentCopy)) throw new IOException("Windows 未保存自启动设置。");
    }
    internal static string Quote(string value)
    {
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); result.Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}
