// MilkDrop-style visualizer for the player bar's <audio> element.
// Loaded as an ES module from VisualizerOverlay.razor via JS interop.
//
// Analysis uses the Web Audio API (BaseAudioContext): the <audio> element is
// routed through one shared AudioContext, and Butterchurn - the WebGL 2 port
// of MilkDrop, vendored in wwwroot/lib/butterchurn - reads that node with
// connectAudio() and renders MilkDrop presets to a WebGL canvas.
//
// Everything is lazy: the ~850 KB of Butterchurn and presets loads the first
// time the visualizer opens, and the audio graph is only built then, so
// playback is untouched for anyone who never opens it.

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
let libraries = null;
let session = null;

/**
 * Opens the visualizer on `canvas`, fed by `audio`.
 * Returns "ok", or why it couldn't: "reduced-motion" (call again with
 * force = true once the viewer confirms), "unsupported" or "load-failed".
 */
export async function open(audio, host, canvas, presetLabel, dotnet, force) {
    if (!force && reducedMotion.matches) {
        return "reduced-motion";
    }

    // Build the graph before any await so it happens inside the click that opened us.
    const source = sourceFor(audio);
    audioContext.resume();

    let libs;
    try {
        libs = await loadLibraries();
    } catch {
        return "load-failed";
    }
    if (!libs.isSupported()) {
        return "unsupported";
    }

    close();

    const pixelRatio = Math.min(window.devicePixelRatio || 1, MAX_PIXEL_RATIO);
    const visualizer = libs.butterchurn.createVisualizer(audioContext, canvas, {
        width: canvas.clientWidth || 800,
        height: canvas.clientHeight || 600,
        pixelRatio
    });
    visualizer.connectAudio(source);

    const presets = libs.presets.getPresets();
    session = {
        visualizer, source, host, canvas, presetLabel, dotnet, pixelRatio, presets,
        names: shuffle(Object.keys(presets)),
        index: 0,
        cycling: true,
        frame: 0,
        cycleTimer: 0,
        idleTimer: 0
    };

    session.observer = new ResizeObserver(() => resize(session));
    session.observer.observe(canvas);
    resize(session);

    session.onKey = event => onKey(session, event);
    session.onPointer = () => wake(session);
    document.addEventListener("keydown", session.onKey);
    host.addEventListener("pointermove", session.onPointer);
    wake(session);

    showPreset(session, 0, 0);
    scheduleCycle(session);
    const loop = () => {
        session.visualizer.render();
        session.frame = requestAnimationFrame(loop);
    };
    session.frame = requestAnimationFrame(loop);
    return "ok";
}

export function close() {
    if (!session) {
        return;
    }
    const s = session;
    session = null;

    cancelAnimationFrame(s.frame);
    clearTimeout(s.cycleTimer);
    clearTimeout(s.idleTimer);
    s.observer.disconnect();
    document.removeEventListener("keydown", s.onKey);
    s.host.removeEventListener("pointermove", s.onPointer);
    s.visualizer.disconnectAudio(s.source);
    if (document.fullscreenElement === s.host) {
        document.exitFullscreen().catch(() => { });
    }
    // Butterchurn has no destroy(); drop the WebGL context now instead of waiting for GC.
    s.canvas.getContext("webgl2")?.getExtension("WEBGL_lose_context")?.loseContext();
}

export function next() {
    if (session) {
        showPreset(session, session.index + 1, BLEND_SECONDS);
        scheduleCycle(session);
    }
}

export function previous() {
    if (session) {
        showPreset(session, session.index - 1, BLEND_SECONDS);
        scheduleCycle(session);
    }
}

/** Turns automatic preset cycling on or off; returns the new state. */
export function toggleCycle() {
    if (!session) {
        return false;
    }
    session.cycling = !session.cycling;
    scheduleCycle(session);
    return session.cycling;
}

export function toggleFullscreen() {
    if (!session) {
        return;
    }
    if (document.fullscreenElement) {
        document.exitFullscreen().catch(() => { });
    } else {
        session.host.requestFullscreen?.().catch(() => { });
    }
}

/** MilkDrop's song-title animation; shown when the track changes while open. */
export function showTitle(title) {
    if (session && title) {
        session.visualizer.launchSongTitleAnim(title);
    }
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
            libraries = null; // let a later open() retry
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

function showPreset(s, index, blendSeconds) {
    const count = s.names.length;
    s.index = ((index % count) + count) % count;
    const name = s.names[s.index];
    s.visualizer.loadPreset(s.presets[name], blendSeconds);
    s.presetLabel.textContent = name;
}

function scheduleCycle(s) {
    clearTimeout(s.cycleTimer);
    if (s.cycling) {
        s.cycleTimer = setTimeout(() => {
            if (session === s) {
                showPreset(s, s.index + 1, BLEND_SECONDS);
                scheduleCycle(s);
            }
        }, CYCLE_SECONDS * 1000);
    }
}

// Keeps the drawing buffer matched to the canvas's CSS size.
function resize(s) {
    const width = s.canvas.clientWidth;
    const height = s.canvas.clientHeight;
    if (!width || !height) {
        return;
    }
    s.canvas.width = Math.round(width * s.pixelRatio);
    s.canvas.height = Math.round(height * s.pixelRatio);
    s.visualizer.setRendererSize(width, height, { pixelRatio: s.pixelRatio });
}

function onKey(s, event) {
    if (event.target instanceof HTMLInputElement || event.target instanceof HTMLTextAreaElement) {
        return;
    }
    switch (event.key) {
        case "ArrowRight": next(); break;
        case "ArrowLeft": previous(); break;
        case "f": case "F": toggleFullscreen(); break;
        case "Escape":
            // In fullscreen the browser uses Escape to leave fullscreen first.
            if (!document.fullscreenElement) {
                s.dotnet.invokeMethodAsync("RequestClose");
            }
            break;
        default: return;
    }
    event.preventDefault();
}

// Hides the controls and cursor after a moment without pointer movement.
function wake(s) {
    s.host.classList.remove("is-idle");
    clearTimeout(s.idleTimer);
    s.idleTimer = setTimeout(() => s.host.classList.add("is-idle"), IDLE_MS);
}

function shuffle(items) {
    for (let i = items.length - 1; i > 0; i--) {
        const j = Math.floor(Math.random() * (i + 1));
        [items[i], items[j]] = [items[j], items[i]];
    }
    return items;
}
