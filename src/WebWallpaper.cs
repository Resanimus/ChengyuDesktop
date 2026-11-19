using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ChengyuDock;

internal sealed class WebWallpaper : IDisposable
{
    readonly string _host;
    readonly bool _scene;
    const string SceneHost = "renderer.wallpaper.chengyu.invalid";
    readonly string _entry;
    readonly string _folder;
    readonly string _properties;
    CoreWebView2? _core;
    bool _disposed;
    bool _paused;
    bool _covered, _suspending;
    int _fps = 30;
    string _lastPlayback = "";
    WallpaperPreference _preference;
    internal WebView2 View { get; } = new() { Focusable = false, DefaultBackgroundColor = System.Drawing.Color.Black };
    internal bool Ready { get; private set; }
    internal event Action? Changed;
    internal string Error { get; private set; } = "";
    internal List<string> Diagnostics { get; } = new();

    internal WebWallpaper(string path, string project, WallpaperPreference preference, bool scene = false)
    {
        _scene = scene;
        _entry = path;
        _folder = Path.GetDirectoryName(project.Length > 0 ? project : path)!;
        string origin = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
        _host = origin[..32].ToLowerInvariant() + ".wallpaper.chengyu.invalid";
        _preference = preference;
        _properties = ReadProperties(project);
    }

    internal async Task Open(string dataDirectory)
    {
        try
        {
            var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(dataDirectory, "WebWallpaper"), options);
            if (_disposed) return;
            await View.EnsureCoreWebView2Async(environment);
            if (_disposed) return;
            _core = View.CoreWebView2;
            _core.IsMuted = !_preference.AudioEnabled || _preference.Volume == 0 || _paused;
            _core.Settings.AreDefaultContextMenusEnabled = false;
            _core.Settings.AreDevToolsEnabled = false;
            _core.Settings.AreHostObjectsAllowed = false;
            _core.Settings.IsWebMessageEnabled = _scene;
            _core.Settings.IsStatusBarEnabled = false;
            _core.SetVirtualHostNameToFolderMapping(_host, _folder, _scene
                ? CoreWebView2HostResourceAccessKind.Allow : CoreWebView2HostResourceAccessKind.Deny);
            string navigationHost = _scene ? SceneHost : _host;
            if (_scene)
            {
                _core.SetVirtualHostNameToFolderMapping(SceneHost,
                    Path.Combine(AppContext.BaseDirectory, "Assets", "ScenePlayer"), CoreWebView2HostResourceAccessKind.Deny);
                _core.WebMessageReceived += SceneMessage;
            }
            _core.NavigationStarting += (_, evt) =>
            {
                evt.Cancel = !Uri.TryCreate(evt.Uri, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps || uri.Host != navigationHost;
            };
            _core.NewWindowRequested += (_, evt) => evt.Handled = true;
            _core.DownloadStarting += (_, evt) => evt.Cancel = true;
            _core.PermissionRequested += (_, evt) => evt.State = CoreWebView2PermissionState.Deny;
            _core.NavigationCompleted += PageLoaded;
            _core.ProcessFailed += (_, evt) =>
            {
                if (evt.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited)
                    Fail("网页渲染进程已退出。");
            };
            await _core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript + "\nwindow.chengyuWallpaperProperties = " + _properties + ";");
            if (_disposed) return;
            if (_scene)
            {
                _core.Navigate("https://" + SceneHost + "/index.html");
                return;
            }
            string relative = Path.GetRelativePath(_folder, _entry);
            string url = string.Join("/", relative.Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString));
            _core.Navigate("https://" + _host + "/" + url);
        }
        catch (Exception ex) { if (!_disposed) Fail(ex.Message); }
    }

    async void PageLoaded(object? sender, CoreWebView2NavigationCompletedEventArgs evt)
    {
        if (_disposed) return;
        if (!evt.IsSuccess)
        {
            if (evt.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                Fail("页面无法加载（" + evt.WebErrorStatus + "）。");
            return;
        }
        try
        {
            if (_scene)
            {
                await _core!.ExecuteScriptAsync("void window.chengyuLoadScene(" + JsonSerializer.Serialize("https://" + _host) + ")");
                return;
            }
            await _core!.ExecuteScriptAsync("window.chengyuWallpaperInit?.()");
            if (_disposed) return;
            _lastPlayback = "";
            Ready = true;
            Apply(_preference, _paused, _fps, _covered);
            Changed?.Invoke();
        }
        catch (Exception ex) { if (!_disposed) Fail(ex.Message); }
    }

    void SceneMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs evt)
    {
        if (_disposed || !Uri.TryCreate(evt.Source, UriKind.Absolute, out var uri) || uri.Host != SceneHost) return;
        using var json = JsonDocument.Parse(evt.WebMessageAsJson);
        if (!json.RootElement.TryGetProperty("type", out var type)) return;
        if (type.GetString() == "ready")
        {
            Ready = true;
            _lastPlayback = "";
            Apply(_preference, _paused, _fps, _covered);
            Changed?.Invoke();
        }
        else if (json.RootElement.TryGetProperty("message", out var text))
        {
            string message = text.GetString() ?? "";
            if (type.GetString() == "error") Fail(message);
            else if (Diagnostics.Count < 12) Diagnostics.Add(message);
        }
    }

    internal async void Apply(WallpaperPreference preference, bool paused, int fps, bool covered)
    {
        _preference = preference;
        _paused = paused;
        _fps = fps;
        _covered = covered;
        if (_core == null || _disposed) return;
        string arguments = JsonSerializer.Serialize(new
        {
            muted = !preference.AudioEnabled,
            volume = preference.Volume / 100.0,
            speed = preference.PlaybackRate / 100.0,
            paused,
            fps,
            covered,
            fit = preference.Fit
        });
        if (_lastPlayback == arguments) return;
        try
        {
            _core.IsMuted = paused || !preference.AudioEnabled || preference.Volume == 0;
            if (!Ready) return;
            _lastPlayback = arguments;
            if (!covered)
            {
                _core.Resume();
                View.Visibility = Visibility.Visible;
            }
            await _core.ExecuteScriptAsync("window.chengyuWallpaperPlayback?.(" + arguments + ")");
            if (_disposed || !_covered || _suspending) return;
            View.Visibility = Visibility.Hidden;
            _suspending = true;
            try { await _core.TrySuspendAsync(); }
            finally
            {
                _suspending = false;
                if (!_disposed && !_covered) _core.Resume();
            }
        }
        catch (Exception ex) { if (!_disposed) App.Current.ReportBackgroundError("网页壁纸播放设置", ex); }
    }

    internal Task<string> ReadScript(string script) => _core!.ExecuteScriptAsync(script);
    internal bool Suspended => _core?.IsSuspended == true;
    internal Task CaptureFrame(Stream stream) => _core!.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);

    void Fail(string message)
    {
        Ready = false;
        Error = message;
        Changed?.Invoke();
    }

    static string ReadProperties(string project)
    {
        if (project.Length == 0) return "{}";
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(project), new JsonDocumentOptions { AllowTrailingCommas = true });
            if (json.RootElement.TryGetProperty("general", out var general) && general.ValueKind == JsonValueKind.Object &&
                general.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            {
                var values = new Dictionary<string, JsonElement>();
                foreach (var item in properties.EnumerateObject())
                    if (item.Value.ValueKind == JsonValueKind.Object) values[item.Name] = item.Value;
                return JsonSerializer.Serialize(values);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        return "{}";
    }

    const string BridgeScript = """
        (() => {
            if (window.top !== window) return;
            let settings = { muted: true, volume: 0.5, speed: 1, paused: false, fps: 30 };
            const resume = new WeakSet();
            const apply = media => {
                media.volume = settings.volume;
                media.muted = settings.muted;
                media.playbackRate = settings.speed;
                if (settings.paused && !media.paused) { resume.add(media); media.pause(); }
                if (!settings.paused && resume.has(media)) {
                    resume.delete(media);
                    media.play().catch(() => {});
                }
            };
            const refresh = () => document.querySelectorAll('audio,video').forEach(apply);
            document.addEventListener('loadedmetadata', event => {
                if (event.target instanceof HTMLMediaElement) apply(event.target);
            }, true);
            document.addEventListener('play', event => {
                if (event.target instanceof HTMLMediaElement) apply(event.target);
            }, true);
            window.chengyuWallpaperInit = () => {
                const listener = window.wallpaperPropertyListener;
                listener?.applyUserProperties?.(window.chengyuWallpaperProperties);
                listener?.applyGeneralProperties?.({ fps: { value: 30 } });
                refresh();
            };
            window.chengyuWallpaperPlayback = next => {
                const changed = settings.paused !== next.paused;
                const fpsChanged = settings.fps !== next.fps;
                settings = next;
                if (changed) window.wallpaperPropertyListener?.setPaused?.(next.paused);
                if (fpsChanged) window.wallpaperPropertyListener?.applyGeneralProperties?.({ fps: { value: next.fps } });
                window.chengyuScenePlayback?.(next);
                refresh();
            };
            window.wallpaperRegisterAudioListener = listener => {
                window.addEventListener('load', () => listener(Array(128).fill(0)), { once: true });
            };
        })();
        """;

    public void Dispose()
    {
        _disposed = true;
        Changed = null;
        View.Dispose();
    }
}
