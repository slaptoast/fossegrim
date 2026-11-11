// Hardware and OS playback controls for the player.
//
// Media Session: play/pause/next/previous keys on keyboards and headsets,
// Bluetooth buttons, and the lock-screen / notification controls, plus the
// track's title and cover there.
//
// Output devices: browsers don't announce "headphones unplugged", but they
// fire devicechange on navigator.mediaDevices. When an audio output goes
// away while playing, this pauses; when it comes back, and it was this that
// paused, playback resumes. Browsers that hide the output list (Firefox, or
// any without permission to see devices) give nothing to compare, so there
// only the media keys apply. Phones mostly pause on unplug by themselves.

const SEEK_STEP_SECONDS = 10;

// player: the Player from player.js (uses .active.audio)
// dotnet: PlayerEngine, for next/previous
export function attachHardware(player, dotnet) {
    const session = navigator.mediaSession;
    const devices = navigator.mediaDevices;
    const audio = () => player.active.audio;

    const handlers = {
        play: () => audio().play().catch(() => { }),
        pause: () => audio().pause(),
        stop: () => audio().pause(),
        nexttrack: () => dotnet.invokeMethodAsync("OnMediaKey", "next").catch(() => { }),
        previoustrack: () => dotnet.invokeMethodAsync("OnMediaKey", "previous").catch(() => { }),
        seekbackward: details => seekBy(-(details?.seekOffset || SEEK_STEP_SECONDS)),
        seekforward: details => seekBy(details?.seekOffset || SEEK_STEP_SECONDS),
        seekto: details => {
            if (Number.isFinite(details?.seekTime)) {
                audio().currentTime = details.seekTime;
            }
        },
    };

    function seekBy(seconds) {
        const a = audio();
        const end = Number.isFinite(a.duration) ? a.duration : Infinity;
        a.currentTime = Math.min(Math.max(a.currentTime + seconds, 0), end);
    }

    if (session) {
        for (const [action, handler] of Object.entries(handlers)) {
            try {
                session.setActionHandler(action, handler);
            } catch {
                // This browser doesn't know the action.
            }
        }
    }

    // ---- Playback state for the OS controls ----

    function syncState() {
        if (!session) {
            return;
        }
        const a = audio();
        session.playbackState = !a.src ? "none" : a.paused ? "paused" : "playing";
        if (session.setPositionState && Number.isFinite(a.duration) && a.duration > 0) {
            try {
                session.setPositionState({
                    duration: a.duration,
                    playbackRate: a.playbackRate || 1,
                    position: Math.min(a.currentTime, a.duration),
                });
            } catch {
                // An inconsistent moment mid-load; the next event fixes it.
            }
        }
    }

    const stateEvents = ["play", "pause", "ended", "loadedmetadata", "seeked", "ratechange", "emptied"];
    for (const deck of player.decks) {
        for (const type of stateEvents) {
            deck.audio.addEventListener(type, () => {
                if (deck === player.active) {
                    syncState();
                }
            });
        }
    }

    // ---- Output devices ----

    let outputs = null;          // Map of output key -> true, from the last look
    let pausedForMissing = null; // the output keys that were gone when this paused us

    async function listOutputs() {
        try {
            const all = await devices.enumerateDevices();
            return all.filter(d => d.kind === "audiooutput").map(d => `${d.groupId}|${d.deviceId}|${d.label}`);
        } catch {
            return [];
        }
    }

    async function onDeviceChange() {
        const before = outputs;
        const now = await listOutputs();
        outputs = now;
        if (!before) {
            return;
        }

        const removed = before.filter(k => !now.includes(k));
        const added = now.filter(k => !before.includes(k));
        // Hidden ids all look alike ("||"); then only the count says anything.
        const anonymous = before.concat(now).every(k => k === "||");
        const lost = anonymous ? now.length < before.length : removed.length > 0;
        const regained = anonymous
            ? now.length > before.length
            : pausedForMissing !== null && pausedForMissing.every(k => now.includes(k)) && added.length > 0;

        const a = audio();
        if (lost && !a.paused) {
            pausedForMissing = removed;
            a.pause();
        } else if (regained && pausedForMissing !== null && a.paused && a.src) {
            pausedForMissing = null;
            a.play().catch(() => { });
        }
    }

    // Playing again by hand drops the plan to resume on reconnect.
    function onPlay() {
        pausedForMissing = null;
    }

    if (devices?.addEventListener) {
        listOutputs().then(list => { outputs = list; });
        devices.addEventListener("devicechange", onDeviceChange);
    }
    for (const deck of player.decks) {
        deck.audio.addEventListener("play", onPlay);
    }

    return {
        /** The track the OS controls show. artwork: a cover URL or null. */
        setMetadata(title, artist, album, artwork) {
            if (!session || typeof MediaMetadata === "undefined") {
                return;
            }
            session.metadata = new MediaMetadata({
                title: title || "",
                artist: artist || "",
                album: album || "",
                artwork: artwork ? [{ src: artwork, sizes: "512x512" }] : [],
            });
            syncState();
        },

        clearMetadata() {
            if (session) {
                session.metadata = null;
                session.playbackState = "none";
            }
        },

        dispose() {
            devices?.removeEventListener?.("devicechange", onDeviceChange);
            if (session) {
                for (const action of Object.keys(handlers)) {
                    try {
                        session.setActionHandler(action, null);
                    } catch {
                        // Never set.
                    }
                }
                session.metadata = null;
            }
        },
    };
}
