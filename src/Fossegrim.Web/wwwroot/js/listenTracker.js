// Counts how long the current track has actually been heard on an <audio>
// element and reports a "listen" once, as soon as Last.fm's rule is met: the
// track is longer than 30 seconds and has played for half its length or 4
// minutes, whichever comes first. Only normal playback counts, so seeking
// ahead can't fake a listen and skipping a track never reaches the threshold.
// Calls dotnet.invokeMethodAsync('OnListened', startedAtIso, listenedSeconds).

const MIN_TRACK_SECONDS = 30;
const MAX_THRESHOLD_SECONDS = 240;
// timeupdate fires every ~250ms; a bigger jump between two of them is a seek.
const MAX_STEP_SECONDS = 2;

export function attach(audio, dotnet) {
    let listened = 0;
    let lastTime = null;
    let startedAt = null;
    let reported = false;

    const reset = () => {
        listened = 0;
        lastTime = null;
        startedAt = null;
        reported = false;
    };

    // A new source, or playing the same track again after it ended, starts a
    // fresh listen.
    audio.addEventListener('loadstart', reset);
    audio.addEventListener('ended', reset);

    audio.addEventListener('playing', () => {
        startedAt ??= new Date();
        lastTime = audio.currentTime;
    });
    audio.addEventListener('pause', () => { lastTime = null; });
    audio.addEventListener('seeking', () => { lastTime = null; });
    audio.addEventListener('seeked', () => { lastTime = audio.paused ? null : audio.currentTime; });

    audio.addEventListener('timeupdate', () => {
        if (lastTime === null || audio.paused) {
            return;
        }

        const step = audio.currentTime - lastTime;
        lastTime = audio.currentTime;
        if (step > 0 && step <= MAX_STEP_SECONDS) {
            listened += step;
        }

        const duration = audio.duration;
        if (!reported && Number.isFinite(duration) && duration > MIN_TRACK_SECONDS &&
            listened >= Math.min(duration / 2, MAX_THRESHOLD_SECONDS)) {
            reported = true;
            dotnet.invokeMethodAsync('OnListened', startedAt.toISOString(), listened);
        }
    });
}
