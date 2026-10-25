// Small helpers for the player bar's <audio> element.

export function getCurrentTime(audio) {
    return audio.currentTime;
}

export function restart(audio) {
    audio.currentTime = 0;
    audio.play();
}
