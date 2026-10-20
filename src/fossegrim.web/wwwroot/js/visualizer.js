// Compact frequency-bar visualizer for the player bar's <audio> element.
// Loaded as an ES module from PlayerBar.razor via JS interop.
//
// The audio graph is built lazily on the first "play" (so the AudioContext is
// created with user activation and starts running), and the draw loop only
// runs while audio is actually playing - paused costs nothing.

const BAR_COUNT = 16;
const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");

// One AudioContext per page; createMediaElementSource can only be called once
// per element, so remember which elements are already wired up.
let audioContext = null;
const attached = new WeakMap();

export function attach(audio, canvas) {
    if (!audio || !canvas) {
        return;
    }

    const existing = attached.get(audio);
    if (existing) {
        existing.canvas = canvas;
        return;
    }

    const state = { canvas, analyser: null, frame: 0, bins: null, colors: null };

    state.onPlay = () => {
        if (reducedMotion.matches) {
            return;
        }
        if (!state.analyser) {
            audioContext ??= new AudioContext();
            const source = audioContext.createMediaElementSource(audio);
            state.analyser = audioContext.createAnalyser();
            state.analyser.fftSize = 256;
            state.analyser.smoothingTimeConstant = 0.8;
            source.connect(state.analyser);
            state.analyser.connect(audioContext.destination);
            state.bins = new Uint8Array(state.analyser.frequencyBinCount);
        }
        // Read the design tokens once per play rather than every frame.
        const styles = getComputedStyle(state.canvas);
        state.colors = [
            styles.getPropertyValue("--brand-violet").trim(),
            styles.getPropertyValue("--brand-indigo").trim()
        ];
        audioContext.resume();
        cancelAnimationFrame(state.frame);
        state.frame = requestAnimationFrame(() => draw(audio, state));
    };

    state.onStop = () => {
        cancelAnimationFrame(state.frame);
        state.frame = 0;
        clear(state.canvas);
    };

    audio.addEventListener("play", state.onPlay);
    audio.addEventListener("pause", state.onStop);
    audio.addEventListener("ended", state.onStop);
    attached.set(audio, state);

    // Autoplay may have started before we attached.
    if (!audio.paused) {
        state.onPlay();
    }
}

export function detach(audio) {
    const state = audio && attached.get(audio);
    if (!state) {
        return;
    }
    cancelAnimationFrame(state.frame);
    audio.removeEventListener("play", state.onPlay);
    audio.removeEventListener("pause", state.onStop);
    audio.removeEventListener("ended", state.onStop);
    // The element stays routed through the graph (that can't be undone), so
    // keep the WeakMap entry and just stop drawing.
    state.onPlay = () => { };
    state.onStop = () => { };
}

function draw(audio, state) {
    const canvas = state.canvas;
    if (!audio.isConnected || !canvas.isConnected || audio.paused || reducedMotion.matches) {
        state.frame = 0;
        clear(canvas);
        return;
    }

    const ctx = sizeCanvas(canvas);
    const { width, height } = canvas;
    state.analyser.getByteFrequencyData(state.bins);

    const gradient = ctx.createLinearGradient(0, height, 0, 0);
    gradient.addColorStop(0, state.colors[0]);
    gradient.addColorStop(1, state.colors[1]);
    ctx.fillStyle = gradient;
    ctx.clearRect(0, 0, width, height);

    // Group bins logarithmically so the bass doesn't take over half the bars;
    // the top quarter of the spectrum is mostly empty for typical music.
    const usable = Math.floor(state.bins.length * 0.75);
    const gap = Math.max(1, Math.round(width / BAR_COUNT * 0.25));
    const barWidth = (width - gap * (BAR_COUNT - 1)) / BAR_COUNT;
    for (let i = 0; i < BAR_COUNT; i++) {
        const start = Math.floor(Math.pow(usable, i / BAR_COUNT));
        const end = Math.max(start + 1, Math.floor(Math.pow(usable, (i + 1) / BAR_COUNT)));
        let sum = 0;
        for (let b = start; b < end; b++) {
            sum += state.bins[b];
        }
        const level = sum / (end - start) / 255;
        const barHeight = Math.max(1, level * height);
        ctx.fillRect(i * (barWidth + gap), height - barHeight, barWidth, barHeight);
    }

    state.frame = requestAnimationFrame(() => draw(audio, state));
}

// Keeps the backing store matched to the CSS size so bars stay crisp on HiDPI.
function sizeCanvas(canvas) {
    const ratio = window.devicePixelRatio || 1;
    const width = Math.round(canvas.clientWidth * ratio);
    const height = Math.round(canvas.clientHeight * ratio);
    if (canvas.width !== width || canvas.height !== height) {
        canvas.width = width;
        canvas.height = height;
    }
    return canvas.getContext("2d");
}

function clear(canvas) {
    if (canvas) {
        canvas.getContext("2d").clearRect(0, 0, canvas.width, canvas.height);
    }
}
