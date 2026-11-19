using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ChengyuDock;

internal sealed record WallpaperItem(string Title, string Kind, string Path, string Preview, string Project, string Source)
{
    internal string Key => Project.Length > 0 ? Project : Path;
    internal bool UsesEngine => Kind is "scene" or "web" or "application";
    internal bool HasPreview => Preview.Length > 0 && File.Exists(Preview);
    internal bool NativeWeb => Kind == "web" && IsHtml(Path) && File.Exists(Path);
    internal bool NativeScene => Kind == "scene" && Available;
    internal static bool IsHtml(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() is ".html" or ".htm";
    internal string TypeName => Kind switch
    {
        "image" => "图片", "video" => "视频", "scene" => "场景", "web" => "网页",
        "application" => "应用", _ => "其他"
    };
    internal bool Available => UsesEngine ? File.Exists(Project) &&
        (File.Exists(Path) || Kind == "scene" && File.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Project)!, "scene.pkg")))
        : Kind is "image" or "video" && File.Exists(Path);
}

internal sealed record WallpaperScan(IReadOnlyList<WallpaperItem> Items, IReadOnlyList<string> Issues);

internal sealed class WallpaperLibrary : IDisposable
{
    CancellationTokenSource? _scan;
    bool _hasScan;
    string _sources = "";
    public IReadOnlyList<WallpaperItem> Items { get; private set; } = Array.Empty<WallpaperItem>();
    public IReadOnlyList<string> Issues { get; private set; } = Array.Empty<string>();
    public bool Scanning { get; private set; }
    internal long Revision { get; private set; }
    public event Action? Changed;

    internal void Sync(IEnumerable<string> sources)
    {
        string signature = string.Join("\n", sources);
        if (_sources == signature && (Scanning || _hasScan)) return;
        _sources = signature;
        Refresh(sources.ToArray());
    }

    internal async void Refresh(string[] sources)
    {
        _scan?.Cancel();
        var cancellation = new CancellationTokenSource();
        _scan = cancellation;
        Scanning = true;
        Changed?.Invoke();
        try
        {
            var result = await Task.Run(() => Scan(sources, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            Items = result.Items;
            Issues = result.Issues;
            Revision++;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (cancellation.IsCancellationRequested) return;
            Issues = new[] { "扫描失败，已保留上次列表。" + ex.Message };
        }
        finally
        {
            if (ReferenceEquals(_scan, cancellation))
            {
                _scan = null;
                Scanning = false;
                _hasScan = true;
                Changed?.Invoke();
            }
            cancellation.Dispose();
        }
    }

    internal static string? MediaKind(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".tif" or ".tiff" => "image",
        ".mp4" or ".wmv" or ".mov" or ".m4v" or ".webm" or ".mkv" or ".avi" or ".mpeg" or ".mpg" => "video",
        _ => null
    };

    internal static WallpaperScan Scan(IEnumerable<string> sources, CancellationToken cancellation)
    {
        var items = new Dictionary<string, WallpaperItem>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<string>();
        foreach (string source in sources)
        {
            var directories = new Stack<string>();
            try
            {
                string root = System.IO.Path.GetFullPath(source);
                if (!Directory.Exists(root)) { issues.Add(source + " · 目录不可用"); continue; }
                foreach (string expanded in ExpandSource(root)) directories.Push(expanded);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { issues.Add(source + " · " + ex.Message); continue; }
            while (directories.TryPop(out string? directory))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!visited.Add(directory)) continue;
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                    string project = System.IO.Path.Combine(directory, "project.json");
                    if (File.Exists(project))
                    {
                        try
                        {
                            var item = ReadProject(project, source);
                            if (item != null) { items.TryAdd(item.Key, item); continue; }
                        }
                        catch (Exception ex) when (ex is JsonException or IOException or ArgumentException)
                        { issues.Add(project + " · " + ex.Message); }
                        // A project directory contains textures, previews and clips, not separate wallpapers.
                        continue;
                    }
                    foreach (string path in Directory.EnumerateFileSystemEntries(directory, "*", new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
                    }))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (Directory.Exists(path)) { directories.Push(path); continue; }
                        string? kind = MediaKind(path);
                        if (kind == null) continue;
                        items.TryAdd(path, new WallpaperItem(System.IO.Path.GetFileNameWithoutExtension(path), kind, path,
                            kind == "image" ? path : "", "", source));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { issues.Add(directory + " · " + ex.Message); }
            }
        }
        return new WallpaperScan(items.Values.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToArray(), issues);
    }

    internal static WallpaperItem? ReadProject(string project, string source)
    {
        if (new FileInfo(project).Length > 4 * 1024 * 1024) throw new InvalidDataException("项目描述过大");
        using var json = JsonDocument.Parse(File.ReadAllText(project), new JsonDocumentOptions { AllowTrailingCommas = true });
        if (json.RootElement.ValueKind != JsonValueKind.Object) return null;
        string Text(string key) => json.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";
        string directory = System.IO.Path.GetDirectoryName(project)!;
        string? Resolve(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || System.IO.Path.IsPathRooted(name)) return null;
            string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, name));
            return path.StartsWith(directory + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? path : null;
        }
        string? payload = Resolve(Text("file"));
        if (payload == null) return null;
        string kind = Text("type").ToLowerInvariant();
        if (kind.Length == 0) kind = MediaKind(payload) ?? (System.IO.Path.GetExtension(payload).ToLowerInvariant() switch
        {
            ".json" or ".pkg" => "scene", ".html" or ".htm" => "web", ".exe" => "application", _ => "unknown"
        });
        string title = Text("title");
        return new WallpaperItem(title.Length > 0 ? title : new DirectoryInfo(directory).Name,
            kind, payload, Resolve(Text("preview")) ?? "", project, source);
    }

    internal static IEnumerable<string> ExpandSource(string root)
    {
        if (new DirectoryInfo(root).Name.Equals("projects", StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(System.IO.Path.Combine(root, "defaultprojects")))
            return new[] { System.IO.Path.Combine(root, "defaultprojects"), System.IO.Path.Combine(root, "myprojects") }.Where(Directory.Exists);
        if (Directory.Exists(System.IO.Path.Combine(root, "steamapps")))
        {
            string apps = System.IO.Path.Combine(root, "steamapps");
            return new[] { System.IO.Path.Combine(apps, "workshop", "content", "431960"),
                System.IO.Path.Combine(apps, "common", "wallpaper_engine", "projects", "defaultprojects"),
                System.IO.Path.Combine(apps, "common", "wallpaper_engine", "projects", "myprojects") }.Where(Directory.Exists);
        }
        if (File.Exists(System.IO.Path.Combine(root, "wallpaper64.exe")) || File.Exists(System.IO.Path.Combine(root, "wallpaper32.exe")))
        {
            string? apps = Directory.GetParent(root)?.Parent?.FullName;
            return new[] { System.IO.Path.Combine(root, "projects", "defaultprojects"),
                System.IO.Path.Combine(root, "projects", "myprojects"),
                System.IO.Path.Combine(apps ?? root, "workshop", "content", "431960") }.Where(Directory.Exists);
        }
        return new[] { root };
    }

    internal static (string[] Sources, string Engine) DiscoverSteam()
    {
        var steam = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, key, field) in new[]
        {
            (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")
        })
        {
            try { using var registry = hive.OpenSubKey(key); if (registry?.GetValue(field) is string path) steam.Add(path); }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        steam.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        var libraries = new HashSet<string>(steam, StringComparer.OrdinalIgnoreCase);
        foreach (string root in steam)
        {
            try
            {
                string vdf = System.IO.Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                foreach (Match match in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libraries.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var sources = new List<string>();
        string engine = "";
        foreach (string library in libraries)
        {
            string apps = System.IO.Path.Combine(library, "steamapps");
            string folder = System.IO.Path.Combine(apps, "common", "wallpaper_engine");
            string exe = System.IO.Path.Combine(folder, "wallpaper64.exe");
            if (!File.Exists(exe)) exe = System.IO.Path.Combine(folder, "wallpaper32.exe");
            if (engine.Length == 0 && File.Exists(exe)) engine = System.IO.Path.GetFullPath(exe);
            foreach (string path in new[] { System.IO.Path.Combine(apps, "workshop", "content", "431960"),
                System.IO.Path.Combine(folder, "projects") })
                if (Directory.Exists(path)) sources.Add(System.IO.Path.GetFullPath(path));
        }
        return (sources.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), engine);
    }

    public void Dispose() { _scan?.Cancel(); Changed = null; }
}
