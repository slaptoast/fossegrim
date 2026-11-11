// The playback engine: two <audio> "decks", created here so Blazor never
// re-renders them. One is active; the other quietly loads the next track and
// takes over shortly before the active one ends, overlapping it for a
// crossfade or starting right at its end for gapless playback.
//
// The decks live hidden in the app layout (PlayerEngine), so playback carries
// on across pages. The visible play/seek/volume controls are mounted where the
// player is shown (see playerControls.js), and act on the active deck.
//
// PlayerState stays the source of truth for the queue. PlayerEngine pushes what's
// to play with load() and setNext(); when a deck takes over by itself this
// reports it through OnTransition, so the state follows without a reload.
//
// Gapless is as close as a timer can start a streaming element: very near, not
// sample-accurate. A deck that isn't ready in time falls back to starting on
// the "ended" event, and a next track that fails falls back to an ordinary load.

import * as graph from "./audioGraph.js";
import { attach as attachListenTracker } from "./listenTracker.js";
import { registerAudio } from "./visualizer.js";
import { mountControls } from "./playerControls.js";
import { attachHardware } from "./hardware.js";

// How long before its start the next track begins loading.
const PRELOAD_LEAD_SECONDS = 30;
// Roughly how long play() takes to produce sound, so gapless starts that much early.
const START_LATENCY_SECONDS = 0.03;
// Below a second away from the start point, poll on a timer: timeupdate only fires every ~250ms.
const ARM_WINDOW_SECONDS = 1;
const ARM_POLL_MS = 50;
const HAVE_FUTURE_DATA = 3;

export function createPlayer(host, dotnet) {
    return new Player(host, dotnet);
}

// A fade may take at most half of either track, so short tracks stay audible.
function effectiveOverlap(requested, outgoingDuration, incomingDuration) {
    let overlap = requested;
    for (const duration of [outgoingDuration, incomingDuration]) {
        if (Number.isFinite(duration)) {
            overlap = Math.min(overlap, duration / 2);
        }
    }
    return overlap;
}

class Player {
    constructor(host, dotnet) {
        this.dotnet = dotnet;
        this.decks = [this.createDeck(host), this.createDeck(host)];
        this.active = this.decks[0];
        this.decks[1].audio.hidden = true;
        this.next = null;       // { url, trackId, loadVersion, overlap } from setNext
        this.prepared = false;  // the idle deck holds next
        this.canFade = false;   // the graph is running, so prepared can overlap
        this.fade = null;       // { outgoing, timer } while the old deck plays out
        this.armTimer = 0;
        this.hardware = attachHardware(this, dotnet);
    }

    get idle() {
        return this.decks[0] === this.active ? this.decks[1] : this.decks[0];
    }

    createDeck(host) {
        const audio = document.createElement("audio");
        audio.preload = "metadata";
        audio.className = "player-audio";
        // Lets Web Audio read samples when the API is on another origin (local
        // dev); without it the graph would see a tainted source and output silence.
        audio.crossOrigin = "anonymous";
        host.appendChild(audio);

        const deck = { audio, trackId: null };
        attachListenTracker(audio, this.dotnet, () => deck.trackId);

        audio.addEventListener("play", () => {
            if (deck === this.active) {
                graph.resume();
                this.dotnet.invokeMethodAsync("OnPlaying", deck.trackId).catch(() => { });
            }
        });
        audio.addEventListener("timeupdate", () => {
            if (deck === this.active) {
                this.check();
            }
        });
        audio.addEventListener("ended", () => {
            if (deck === this.active) {
                this.onActiveEnded();
            } else if (this.fade?.outgoing === deck) {
                this.finishFade();
            }
        });
        audio.addEventListener("pause", () => {
            // Pausing mid-fade settles on the new track, rather than freezing a half-faded mix.
            if (deck === this.active && !audio.ended) {
                this.finishFade();
            }
        });
        audio.addEventListener("volumechange", () => {
            if (deck === this.active) {
                this.idle.audio.volume = audio.volume;
                this.idle.audio.muted = audio.muted;
            }
        });
        audio.addEventListener("ratechange", () => {
            if (deck === this.active) {
                this.idle.audio.playbackRate = audio.playbackRate;
            }
        });
        audio.addEventListener("error", () => {
            // A next track that won't load: let "ended" hand over to an ordinary load.
            if (deck === this.idle && this.prepared) {
                this.prepared = false;
                this.next = null;
            }
        });
        return deck;
    }

    // ---- Called by PlayerEngine and PlayerControls ----

    /** Play/seek/volume controls in host, until the returned object's dispose(). */
    mountControls(host) {
        return mountControls(this, host);
    }

    /** Hard-cuts to this track: a new queue, Next, Previous, a jump, or a reload. */
    load(url, trackId, autoPlay) {
        this.reset();
        const { audio } = this.active;
        graph.setGain(audio, 1);
        this.active.trackId = trackId;
        audio.src = url;
        this.register(audio);
        if (autoPlay) {
            audio.play().catch(() => { });
        }
    }

    /** What follows the active track; null means just wait for it to end. */
    setNext(next) {
        const keepDeck = this.prepared && next && this.next?.trackId === next.trackId;
        if (!keepDeck && !this.fade) {
            this.clearDeck(this.idle);
        }
        clearTimeout(this.armTimer);
        this.armTimer = 0;
        this.prepared = keepDeck;
        this.next = next;
        if (keepDeck && next.overlap > 0) {
            this.prepareFade();
        }
        this.check();
    }

    stop() {
        this.reset();
        this.clearDeck(this.active);
        this.hardware.clearMetadata();
    }

    /** What the OS media controls (lock screen, media keys) show. */
    setMetadata(title, artist, album, artwork) {
        this.hardware.setMetadata(title, artist, album, artwork);
    }

    position() {
        return this.active.audio.currentTime;
    }

    restart() {
        this.finishFade();
        const { audio } = this.active;
        audio.currentTime = 0;
        audio.play().catch(() => { });
    }

    dispose() {
        this.stop();
        this.hardware.dispose();
        for (const { audio } of this.decks) {
            audio.remove();
        }
    }

    // ---- Internals ----

    // Decides whether it's time to preload the next track or get ready to start it.
    check() {
        const { audio } = this.active;
        if (!this.next || this.fade || !Number.isFinite(audio.duration)) {
            return;
        }

        const remaining = this.remaining();
        if (!this.prepared && remaining <= PRELOAD_LEAD_SECONDS + this.next.overlap) {
            this.prepare();
        }
        if (this.prepared && !this.armTimer && !audio.paused && remaining <= this.startPoint() + ARM_WINDOW_SECONDS) {
            this.poll();
        }
    }

    prepare() {
        const { audio } = this.idle;
        this.idle.trackId = this.next.trackId;
        this.prepared = true;
        audio.preload = "auto";
        audio.playbackRate = this.active.audio.playbackRate;
        audio.src = this.next.url;
        if (this.next.overlap > 0) {
            this.prepareFade();
        }
    }

    // Done ahead of the fade, as joining the graph can click on a playing track.
    // Without a running context (no gesture allowed it) the track plays gapless
    // instead: a routed element would go silent.
    async prepareFade() {
        if (this.canFade) {
            return;
        }
        if (await graph.ensureRunning()) {
            for (const { audio } of this.decks) {
                graph.route(audio);
            }
            this.canFade = true;
        }
    }

    remaining() {
        const { audio } = this.active;
        return (audio.duration - audio.currentTime) / (audio.playbackRate || 1);
    }

    overlap() {
        return this.next?.overlap > 0 && this.canFade
            ? effectiveOverlap(this.next.overlap, this.active.audio.duration, this.idle.audio.duration)
            : 0;
    }

    startPoint() {
        return Math.max(this.overlap(), START_LATENCY_SECONDS);
    }

    poll() {
        this.armTimer = 0;
        const { audio } = this.active;
        if (!this.prepared || audio.paused) {
            return;
        }

        const wait = this.remaining() - this.startPoint();
        if (wait > ARM_WINDOW_SECONDS) {
            return; // seeked back: check() arms it again when the time comes
        }
        if (wait > 0) {
            this.armTimer = setTimeout(() => this.poll(), Math.min(wait * 1000, ARM_POLL_MS));
        } else if (this.idle.audio.readyState >= HAVE_FUTURE_DATA) {
            this.takeOver();
        }
        // Not ready yet: the next timeupdate checks again, and "ended" is the backstop.
    }

    onActiveEnded() {
        if (this.prepared) {
            this.takeOver();
        } else {
            this.dotnet.invokeMethodAsync("OnEnded").catch(() => { });
        }
    }

    // The idle deck becomes the active one; the old one plays out behind it.
    takeOver() {
        clearTimeout(this.armTimer);
        this.armTimer = 0;

        const outgoing = this.active;
        const incoming = this.idle;
        const next = this.next;
        const remaining = outgoing.audio.ended ? 0 : Math.max(this.remaining(), 0);
        const overlap = Math.min(this.overlap(), remaining);

        this.active = incoming;
        this.next = null;
        this.prepared = false;
        outgoing.audio.hidden = true;
        incoming.audio.hidden = false;
        incoming.audio.preload = "metadata";

        if (overlap > 0) {
            graph.crossfade(outgoing.audio, incoming.audio, overlap);
        } else {
            graph.setGain(incoming.audio, 1);
        }

        if (remaining > 0) {
            // "ended" normally finishes this; the timer covers an old deck that stalls.
            this.fade = { outgoing, timer: setTimeout(() => this.finishFade(), (remaining + 1) * 1000) };
        } else {
            this.clearDeck(outgoing);
        }

        incoming.audio.play().catch(() => { });
        this.register(incoming.audio);
        this.dotnet.invokeMethodAsync("OnTransition", next.loadVersion, next.trackId).catch(() => { });
    }

    // Stops the old deck and leaves the new one at full volume.
    finishFade() {
        if (!this.fade) {
            return;
        }
        const { outgoing, timer } = this.fade;
        this.fade = null;
        clearTimeout(timer);
        this.clearDeck(outgoing);
        graph.setGain(this.active.audio, 1);
        this.check();
    }

    reset() {
        clearTimeout(this.armTimer);
        this.armTimer = 0;
        this.next = null;
        this.prepared = false;
        this.finishFade();
        this.clearDeck(this.idle);
    }

    clearDeck(deck) {
        const { audio } = deck;
        deck.trackId = null;
        audio.pause();
        audio.preload = "metadata";
        graph.setGain(audio, 1);
        if (audio.hasAttribute("src")) {
            audio.removeAttribute("src");
            audio.load();
        }
    }

    register(audio) {
        try {
            registerAudio(audio);
        } catch {
            // The visualizer is decoration; never let it break playback.
        }
    }
}
