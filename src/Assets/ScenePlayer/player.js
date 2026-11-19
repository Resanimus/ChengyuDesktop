let scene;
let playback;
let diagnostics = 0;

function send(type, data = {}) {
    window.chrome.webview.postMessage({ type, ...data });
}

function applyPlayback() {
    if (!scene || !playback) return;
    scene.setFit(playback.fit);
    scene.setFps(playback.fps);
    scene.setVolume(playback.muted ? 0 : playback.volume);
    if (scene.paused !== playback.paused) {
        if (playback.paused) scene.pause();
        else scene.resume();
    }
}

window.chengyuScenePlayback = next => {
    playback = next;
    applyPlayback();
};

window.chengyuLoadScene = async url => {
    try {
        scene = await WebWallGL.mount(document.getElementById("wallpaper"), {
            source: WebWallGL.httpSource(url),
            scripts: false,
            audio: null,
            media: null,
            fps: 30,
            volume: 0,
            bake: false,
            firstFrameTimeoutMs: 20000,
            onDiagnostic(message, level) {
                if (level === "warn" && diagnostics++ < 12) send("warning", { message: String(message) });
            }
        });
        scene.on("error", error => send("error", { message: String(error) }));
        applyPlayback();
        send("ready", { width: scene.canvas.width, height: scene.canvas.height });
    } catch (error) {
        send("error", { message: String(error) });
    }
};
