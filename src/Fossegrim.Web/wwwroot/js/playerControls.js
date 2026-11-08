// Play/pause, seek and volume controls for the player engine (player.js).
// The engine's <audio> decks stay hidden in the app layout so playback never
// stops when the page changes; each place that shows the player (the bar,
// the Library page's Player panel) mounts its own set of these into an empty
// element Blazor leaves alone. They always act on whichever deck is active.

export function mountControls(player, host) {
    return new Controls(player, host);
}

function formatTime(seconds) {
    if (!Number.isFinite(seconds) || seconds < 0) {
        return "0:00";
    }
    const whole = Math.floor(seconds);
    const hours = Math.floor(whole / 3600);
    const minutes = Math.floor((whole % 3600) / 60);
    const secs = String(whole % 60).padStart(2, "0");
    return hours > 0 ? `${hours}:${String(minutes).padStart(2, "0")}:${secs}` : `${minutes}:${secs}`;
}

function element(tag, className, attributes = {}) {
    const node = document.createElement(tag);
    node.className = className;
    for (const [name, value] of Object.entries(attributes)) {
        node.setAttribute(name, value);
    }
    return node;
}

// Events on either deck that can change what the controls show.
const EVENTS = ["play", "pause", "timeupdate", "durationchange", "loadedmetadata", "emptied", "volumechange", "ended"];

class Controls {
    constructor(player, host) {
        this.player = player;
        this.host = host;
        this.dragging = false;

        this.root = element("div", "transport");
        this.play = element("button", "player-button transport-play", { type: "button" });
        this.elapsed = element("span", "transport-time");
        this.seek = element("input", "transport-seek", { type: "range", min: "0", max: "0", step: "0.1", value: "0", "aria-label": "Seek" });
        this.total = element("span", "transport-time");
        this.mute = element("button", "player-button transport-mute", { type: "button" });
        this.volume = element("input", "transport-volume", { type: "range", min: "0", max: "1", step: "0.01", "aria-label": "Volume" });
        this.root.append(this.play, this.elapsed, this.seek, this.total, this.mute, this.volume);
        host.appendChild(this.root);

        this.play.addEventListener("click", () => {
            const { audio } = this.player.active;
            if (audio.paused) {
                audio.play().catch(() => { });
            } else {
                audio.pause();
            }
        });
        // While dragging, show where the thumb is rather than the playing time.
        this.seek.addEventListener("input", () => {
            this.dragging = true;
            this.elapsed.textContent = formatTime(Number(this.seek.value));
        });
        this.seek.addEventListener("change", () => {
            this.dragging = false;
            const { audio } = this.player.active;
            if (Number.isFinite(audio.duration)) {
                audio.currentTime = Number(this.seek.value);
            }
        });
        this.mute.addEventListener("click", () => {
            const { audio } = this.player.active;
            audio.muted = !audio.muted;
        });
        this.volume.addEventListener("input", () => {
            const { audio } = this.player.active;
            audio.volume = Number(this.volume.value);
            audio.muted = false;
        });

        this.update = () => this.render();
        for (const { audio } of this.player.decks) {
            for (const name of EVENTS) {
                audio.addEventListener(name, this.update);
            }
        }
        this.render();
    }

    render() {
        const { audio } = this.player.active;
        const loaded = audio.hasAttribute("src");
        const duration = Number.isFinite(audio.duration) ? audio.duration : 0;

        this.play.disabled = !loaded;
        this.play.textContent = audio.paused ? "▶" : "⏸";
        this.play.setAttribute("aria-label", audio.paused ? "Play" : "Pause");
        this.play.title = audio.paused ? "Play" : "Pause";

        this.seek.disabled = !loaded || duration === 0;
        this.seek.max = String(duration);
        if (!this.dragging) {
            this.seek.value = String(audio.currentTime);
            this.elapsed.textContent = formatTime(audio.currentTime);
        }
        this.total.textContent = formatTime(duration);

        const silent = audio.muted || audio.volume === 0;
        this.mute.textContent = silent ? "🔇" : "🔊";
        this.mute.setAttribute("aria-label", silent ? "Unmute" : "Mute");
        this.mute.title = silent ? "Unmute" : "Mute";
        this.volume.value = String(audio.muted ? 0 : audio.volume);
    }

    dispose() {
        for (const { audio } of this.player.decks) {
            for (const name of EVENTS) {
                audio.removeEventListener(name, this.update);
            }
        }
        this.root.remove();
    }
}
