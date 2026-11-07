// The page's one Web Audio graph, shared by the player and the visualizer:
//
//   <audio> deck -> its own gain -> mix -> speakers
//                                    \-> visualizer
//
// Decks only join the graph when something needs it (a crossfade, or the
// visualizer on screen). createMediaElementSource can only be called once per
// element and can't be undone, and a routed element is silent while the context
// is suspended, so callers check ensureRunning() first when they aren't inside
// a "play" event.

let context = null;
let mix = null;
const deckGains = new WeakMap();

export function getContext() {
    if (!context) {
        context = new AudioContext();
        mix = context.createGain();
        mix.connect(context.destination);
    }
    return context;
}

/** The node carrying everything that's playing. */
export function getMix() {
    getContext();
    return mix;
}

/** Sends this element through the graph. Safe to call repeatedly. */
export function route(audio) {
    getContext();
    if (!deckGains.has(audio)) {
        const gain = context.createGain();
        context.createMediaElementSource(audio).connect(gain);
        gain.connect(mix);
        deckGains.set(audio, gain);
    }
}

export function resume() {
    if (context && context.state !== "running") {
        context.resume().catch(() => { });
    }
}

/** Whether the context is (or within a moment becomes) able to make sound. */
export async function ensureRunning(timeoutMs = 300) {
    const running = getContext();
    if (running.state !== "running") {
        // resume() can stay pending forever without a user gesture, hence the timeout.
        await Promise.race([
            running.resume().catch(() => { }),
            new Promise(done => setTimeout(done, timeoutMs))
        ]);
    }
    return running.state === "running";
}

/** Sets a routed deck's gain straight away, dropping any fade in progress. */
export function setGain(audio, value) {
    const gain = deckGains.get(audio);
    if (gain) {
        gain.gain.cancelScheduledValues(context.currentTime);
        gain.gain.setValueAtTime(value, context.currentTime);
    }
}

// Equal-power curves keep the overall loudness steady through the fade (a
// linear one dips in the middle). They run on the audio thread, so a throttled
// background tab can't make them stutter.
const CURVE_POINTS = 64;
const fadeInCurve = Float32Array.from({ length: CURVE_POINTS }, (_, i) => Math.sin(i / (CURVE_POINTS - 1) * Math.PI / 2));
const fadeOutCurve = Float32Array.from({ length: CURVE_POINTS }, (_, i) => Math.cos(i / (CURVE_POINTS - 1) * Math.PI / 2));

export function crossfade(outgoing, incoming, seconds) {
    const start = context.currentTime;
    for (const [audio, curve] of [[outgoing, fadeOutCurve], [incoming, fadeInCurve]]) {
        const gain = deckGains.get(audio).gain;
        gain.cancelScheduledValues(start);
        gain.setValueCurveAtTime(curve, start, seconds);
    }
}
