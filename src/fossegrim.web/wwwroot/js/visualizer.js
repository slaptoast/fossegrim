// MilkDrop-style visualizer. Loaded as an ES module via JS interop by two
// components that share this one module instance:
//   - PlayerBar.razor registers the <audio> element (registerAudio)
//   - VisualizerModule.razor mounts the canvas on the library page (mount)
//
// Analysis uses the Web Audio API (BaseAudioContext): the <audio> element is
// routed through one shared AudioContext, and Butterchurn - the WebGL 2 port
// of MilkDrop, vendored in wwwroot/lib/butterchurn - reads that node with
// connectAudio() and renders MilkDrop presets to a WebGL canvas.
//
// Nothing heavy happens until music actually plays with a view mounted: the
// ~850 KB of Butterchurn and presets loads then, the audio graph is built in
// that "play" (so the AudioContext may start), and frames are only drawn while
// audio is playing. Paused leaves the last frame on screen and costs nothing.

const LIB = "./lib/butterchurn/";
const CYCLE_SECONDS = 20;
const BLEND_SECONDS = 2.7;
const MAX_PIXEL_RATIO = 2;      // MilkDrop shaders are per-pixel; cap the cost on 4K/HiDPI screens
const IDLE_MS = 2500;

const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");

// One AudioContext per page; createMediaElementSource can only be called once
// per element (and can't be undone), so remember each element's source node.
let audioContext = null;
const sources = new WeakMap();
const listening = new WeakSet();
let currentAudio = null;
let libraries = null;
let view = null;

// ---- Called by PlayerBar ----

/** Tells the visualizer which <audio> element is the player. Safe to call on every render. */
export function registerAudio(audio) {
    if (!audio) {
        return;
    }
    currentAudio = audio;
    if (!listening.has(audio)) {
        listening.add(audio);
        audio.addEventListener("play", () => onPlay(audio));
        audio.addEventListener("pause", () => onStop(audio));
        audio.addEventListener("ended", () => onStop(audio));
    }
    if (view && !audio.paused) {
        onPlay(audio);
    }
}

// ---- Called by VisualizerModule ----

/**
 * Mounts the visualizer into `host` (the module element) drawing on `canvas`.
 * Status changes are reported through dotnet.SetStatus(status):
 *   "waiting"        nothing playing yet
 *   "loading"        fetching Butterchurn
 *   "running"        drawing
 *   "paused"         playback paused, last frame kept
 *   "reduced-motion" call mount again with force = true once the viewer agrees
 *   "unsupported"    no WebGL 2 / Web Audio
 *   "load-failed"    the library files didn't load
 */
export function mount(host, canvas, presetLabel, dotnet, force) {
    unmount();
    view = {
        host, canvas, presetLabel, dotnet, force,
        visualizer: null, connected: null, presets: null, names: null,
        index: 0, cycling: true, frame: 0, cycleTimer: 0, idleTimer: 0, status: ""
    };

    if (!force && reducedMotion.matches) {
        setStatus(view, "reduced-motion");
        return;
    }

    const v = view;
    v.onKey = event => onKey(v, event);
    v.onPointer = () => wake(v);
    v.onFullscreen = () => {
        v.host.classList.toggle("is-fullscreen", document.fullscreenElement === v.host);
        wake(v);
    };
    document.addEventListener("keydown", v.onKey);
    document.addEventListener("fullscreenchange", v.onFullscreen);
    host.addEventListener("pointermove", v.onPointer);

    if (currentAudio && !currentAudio.paused) {
        onPlay(currentAudio);
    } else {
        setStatus(v, "waiting");
    }
}

export function unmount() {
    if (!view) {
        return;
    }
    const v = view;
    view = null;

    cancelAnimationFrame(v.frame);
    clearTimeout(v.cycleTimer);
    clearTimeout(v.idleTimer);
    v.observer?.disconnect();
    if (v.onKey) {
        document.removeEventListener("keydown", v.onKey);
        document.removeEventListener("fullscreenchange", v.onFullscreen);
        v.host.removeEventListener("pointermove", v.onPointer);
    }
    if (v.visualizer && v.connected) {
        v.visualizer.disconnectAudio(v.connected);
    }
    if (document.fullscreenElement === v.host) {
        document.exitFullscreen().catch(() => { });
    }
    // Butterchurn has no destroy(); drop the WebGL context now instead of waiting for GC.
    if (v.visualizer) {
        v.canvas.getContext("webgl2")?.getExtension("WEBGL_lose_context")?.loseContext();
    }
}

export function next() {
    if (view?.visualizer) {
        showPreset(view, view.index + 1, BLEND_SECONDS);
        scheduleCycle(view);
    }
}

export function previous() {
    if (view?.visualizer) {
        showPreset(view, view.index - 1, BLEND_SECONDS);
        scheduleCycle(view);
    }
}

/** Turns automatic preset cycling on or off; returns the new state. */
export function toggleCycle() {
    if (!view) {
        return false;
    }
    view.cycling = !view.cycling;
    scheduleCycle(view);
    return view.cycling;
}

export function toggleFullscreen() {
    if (!view) {
        return;
    }
    if (document.fullscreenElement) {
        document.exitFullscreen().catch(() => { });
    } else {
        view.host.requestFullscreen?.().catch(() => { });
    }
}

/** MilkDrop's song-title animation; shown when the track changes. */
export function showTitle(title) {
    if (view?.visualizer && view.status === "running" && title) {
        view.visualizer.launchSongTitleAnim(title);
    }
}

// ---- Internals ----

async function onPlay(audio) {
    const v = view;
    if (!v || !v.onKey || audio !== currentAudio) {
        return;
    }

    // Build the graph inside the "play" event, before any await.
    const source = sourceFor(audio);
    audioContext.resume();

    if (!v.visualizer) {
        if (v.status === "loading") {
            return;
        }
        setStatus(v, "loading");
        let libs;
        try {
            libs = await loadLibraries();
        } catch {
            setStatus(v, "load-failed");
            return;
        }
        if (view !== v) {
            return; // unmounted while loading
        }
        if (!libs.isSupported()) {
            setStatus(v, "unsupported");
            return;
        }
        create(v, libs);
    }

    if (v.connected !== source) {
        if (v.connected) {
            v.visualizer.disconnectAudio(v.connected);
        }
        v.visualizer.connectAudio(source);
        v.connected = source;
    }

    if (!audio.paused) {
        startLoop(v);
    }
}

function onStop(audio) {
    const v = view;
    if (v?.visualizer && audio === currentAudio) {
        cancelAnimationFrame(v.frame);
        v.frame = 0;
        clearTimeout(v.cycleTimer);
        setStatus(v, "paused");
    }
}

function create(v, libs) {
    v.pixelRatio = Math.min(window.devicePixelRatio || 1, MAX_PIXEL_RATIO);
    v.visualizer = libs.butterchurn.createVisualizer(audioContext, v.canvas, {
        width: v.canvas.clientWidth || 400,
        height: v.canvas.clientHeight || 300,
        pixelRatio: v.pixelRatio
    });
    v.presets = libs.presets.getPresets();
    v.names = shuffle(Object.keys(v.presets));
    v.observer = new ResizeObserver(() => resize(v));
    v.observer.observe(v.canvas);
    resize(v);
    showPreset(v, 0, 0);
}

function startLoop(v) {
    if (v.frame) {
        return;
    }
    setStatus(v, "running");
    scheduleCycle(v);
    const loop = () => {
        v.visualizer.render();
        v.frame = requestAnimationFrame(loop);
    };
    v.frame = requestAnimationFrame(loop);
}

function sourceFor(audio) {
    audioContext ??= new AudioContext();
    let source = sources.get(audio);
    if (!source) {
        source = audioContext.createMediaElementSource(audio);
        // The element now plays through the graph, so keep it audible.
        source.connect(audioContext.destination);
        sources.set(audio, source);
    }
    return source;
}

function loadLibraries() {
    libraries ??= Promise.all(["isSupported.min.js", "butterchurn.min.js", "butterchurnPresets.min.js"].map(loadScript))
        .then(() => ({
            // UMD builds; depending on the bundler the API sits on .default.
            isSupported: window.isSupported?.default ?? window.isSupported,
            butterchurn: window.butterchurn?.default ?? window.butterchurn,
            presets: window.butterchurnPresets?.default ?? window.butterchurnPresets
        }))
        .catch(error => {
            libraries = null; // let a later play retry
            throw error;
        });
    return libraries;
}

function loadScript(file) {
    return new Promise((resolve, reject) => {
        const script = document.createElement("script");
        script.src = LIB + file;
        script.onload = resolve;
        script.onerror = () => reject(new Error(`Could not load ${file}`));
        document.head.appendChild(script);
    });
}

function setStatus(v, status) {
    if (v.status !== status) {
        v.status = status;
        v.dotnet.invokeMethodAsync("SetStatus", status).catch(() => { });
    }
}

function showPreset(v, index, blendSeconds) {
    const count = v.names.length;
    v.index = ((index % count) + count) % count;
    const name = v.names[v.index];
    v.visualizer.loadPreset(v.presets[name], blendSeconds);
    v.presetLabel.textContent = name;
}

function scheduleCycle(v) {
    clearTimeout(v.cycleTimer);
    if (v.cycling && v.frame) {
        v.cycleTimer = setTimeout(() => {
            if (view === v && v.frame) {
                showPreset(v, v.index + 1, BLEND_SECONDS);
                scheduleCycle(v);
            }
        }, CYCLE_SECONDS * 1000);
    }
}

// Keeps the drawing buffer matched to the canvas's CSS size (module or fullscreen).
function resize(v) {
    const width = v.canvas.clientWidth;
    const height = v.canvas.clientHeight;
    if (!width || !height) {
        return;
    }
    v.canvas.width = Math.round(width * v.pixelRatio);
    v.canvas.height = Math.round(height * v.pixelRatio);
    v.visualizer.setRendererSize(width, height, { pixelRatio: v.pixelRatio });
}

// Keyboard shortcuts only apply while the visualizer is fullscreen, so they
// never steal arrow keys from the library page.
function onKey(v, event) {
    if (document.fullscreenElement !== v.host) {
        return;
    }
    switch (event.key) {
        case "ArrowRight": next(); break;
        case "ArrowLeft": previous(); break;
        case "f": case "F": toggleFullscreen(); break;
        default: return;
    }
    event.preventDefault();
}

// In fullscreen, hides the controls and cursor after a moment without pointer movement.
function wake(v) {
    v.host.classList.remove("is-idle");
    clearTimeout(v.idleTimer);
    if (document.fullscreenElement === v.host) {
        v.idleTimer = setTimeout(() => v.host.classList.add("is-idle"), IDLE_MS);
    }
}

function shuffle(items) {
    for (let i = items.length - 1; i > 0; i--) {
        const j = Math.floor(Math.random() * (i + 1));
        [items[i], items[j]] = [items[j], items[i]];
    }
    return items;
}
