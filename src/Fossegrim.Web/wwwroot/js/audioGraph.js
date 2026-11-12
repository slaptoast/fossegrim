// The page's one Web Audio graph, shared by the player and the visualizer:
//
//   <audio> deck -> its own gain -> mix -> preamp -> 10 EQ bands -> speakers
//                                                                \-> visualizer
//
// Decks only join the graph when something needs it (a crossfade, the
// visualizer on screen, or the equalizer turned on). createMediaElementSource can only be called once per
// element and can't be undone, and a routed element is silent while the context
// is suspended, so callers check ensureRunning() first when they aren't inside
// a "play" event.

let context = null;
let mix = null;
let preamp = null;
let bands = [];
const deckGains = new WeakMap();

// The ten bands' centre frequencies (EqualizerPresets.Bands). The end bands
// are shelves, so "bass" also lifts what's below 31 Hz, and "treble" above 16 kHz.
const EQ_FREQUENCIES = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
// About an octave wide each, so neighbouring bands blend smoothly.
const EQ_Q = 1.4;
// What EqualizerService last asked for; applied when the context is made.
let equalizer = { enabled: false, gains: EQ_FREQUENCIES.map(() => 0), preampDb: 0 };
// Decks the player has made, so turning the equalizer on mid-song can route them.
const decks = new Set();

export function getContext() {
    if (!context) {
        context = new AudioContext();
        mix = context.createGain();
        preamp = context.createGain();
        bands = EQ_FREQUENCIES.map((frequency, i) => {
            const band = context.createBiquadFilter();
            band.type = i === 0 ? "lowshelf" : i === EQ_FREQUENCIES.length - 1 ? "highshelf" : "peaking";
            band.frequency.value = frequency;
            band.Q.value = EQ_Q;
            return band;
        });
        let node = mix.connect(preamp);
        for (const band of bands) {
            node = node.connect(band);
        }
        node.connect(context.destination);
        applyEqualizer();
    }
    return context;
}

/** The node carrying everything that's playing, as heard (after the equalizer). */
export function getMix() {
    getContext();
    return bands[bands.length - 1];
}

/** A deck the player made: routed when the equalizer is turned on. */
export function addDeck(audio) {
    decks.add(audio);
}

/** Whether the equalizer is on, so decks should be routed when they play. */
export function equalizerEnabled() {
    return equalizer.enabled;
}

/**
 * The equalizer's curve, from EqualizerService: a gain in dB per band, and
 * a preamp in dB (negative, to leave room for boosts). Off, everything is 0.
 * Turned on while something plays, the decks join the graph now; otherwise
 * on their next "play".
 */
export async function setEqualizer(enabled, gains, preampDb) {
    equalizer = { enabled, gains, preampDb };
    if (context) {
        applyEqualizer();
    }
    const playing = [...decks].some(audio => !audio.paused);
    if (enabled && playing && await ensureRunning()) {
        decks.forEach(route);
    }
}

function applyEqualizer() {
    const now = context.currentTime;
    // A short glide, so dragging a slider doesn't click.
    preamp.gain.setTargetAtTime(Math.pow(10, equalizer.preampDb / 20), now, 0.02);
    bands.forEach((band, i) => band.gain.setTargetAtTime(equalizer.gains[i] ?? 0, now, 0.02));
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
