// MultiRemote — Live Code Commentary (Web Speech API + Edge TTS) v1.1

let commentaryEnabled = true;
let commentaryPaused = false;
let commentaryQueue = [];
let commentarySpeaking = false;
let commentaryVoice = null;
let commentaryRate = 1.0;
let commentaryVolume = 1.0;
let commentaryHistory = [];
let commentaryAudio = null;
const COMMENTARY_SOURCE = 'Commentator';
const MAX_COMMENTARY_HISTORY = 50;
const TTS_URL = '/api/tts';

// Initialize commentary system
function initCommentary() {
    commentaryEnabled = localStorage.getItem('cr-commentary-enabled') !== 'false';
    commentaryRate = parseFloat(localStorage.getItem('cr-commentary-rate') || '1.0');
    commentaryVolume = parseFloat(localStorage.getItem('cr-commentary-volume') || '1.0');
    commentaryVoice = localStorage.getItem('cr-commentary-voice') || 'en-US-GuyNeural';
    commentaryHistory = JSON.parse(localStorage.getItem('cr-commentary-history') || '[]');
}

// Process incoming commentary text
function handleCommentary(text, from) {
    if (!commentaryEnabled) return;

    const entry = { text, from, timestamp: new Date().toISOString() };
    commentaryHistory.push(entry);
    if (commentaryHistory.length > MAX_COMMENTARY_HISTORY) {
        commentaryHistory = commentaryHistory.slice(-MAX_COMMENTARY_HISTORY);
    }
    localStorage.setItem('cr-commentary-history', JSON.stringify(commentaryHistory));

    renderCommentaryFeed();

    if (!commentaryPaused) {
        commentaryQueue.push(text);
        processQueue();
    }
}

// Process speech queue — Edge TTS with Web Speech API fallback
function processQueue() {
    if (commentarySpeaking || commentaryQueue.length === 0 || commentaryPaused) return;

    const text = commentaryQueue.shift();
    commentarySpeaking = true;
    updateCommentaryStatus('speaking');

    fetch(TTS_URL, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            text,
            voice: commentaryVoice || 'en-US-GuyNeural',
            rate: (commentaryRate >= 1 ? '+' : '') + Math.round((commentaryRate - 1) * 100) + '%'
        })
    })
    .then(res => {
        if (!res.ok) throw new Error('TTS server error');
        return res.blob();
    })
    .then(blob => {
        const url = URL.createObjectURL(blob);
        stopCurrentAudio();
        commentaryAudio = new Audio(url);
        commentaryAudio.volume = commentaryVolume;

        commentaryAudio.onended = () => {
            commentarySpeaking = false;
            URL.revokeObjectURL(url);
            updateCommentaryStatus(commentaryQueue.length > 0 ? 'queued' : 'idle');
            setTimeout(processQueue, 300);
        };

        commentaryAudio.onerror = () => {
            commentarySpeaking = false;
            URL.revokeObjectURL(url);
            fallbackSpeak(text);
        };

        commentaryAudio.play().catch(() => {
            commentarySpeaking = false;
            URL.revokeObjectURL(url);
            fallbackSpeak(text);
        });
    })
    .catch(() => {
        commentarySpeaking = false;
        fallbackSpeak(text);
    });
}

// Stop any currently playing audio (Edge TTS or Web Speech)
function stopCurrentAudio() {
    if (commentaryAudio) {
        commentaryAudio.onended = null;
        commentaryAudio.onerror = null;
        commentaryAudio.pause();
        if (commentaryAudio.src) URL.revokeObjectURL(commentaryAudio.src);
        commentaryAudio = null;
    }
    if ('speechSynthesis' in window && speechSynthesis.speaking) {
        speechSynthesis.cancel();
    }
}

// Fallback to browser speech if Edge TTS server is down
function fallbackSpeak(text) {
    if (!('speechSynthesis' in window)) {
        updateCommentaryStatus('idle');
        setTimeout(processQueue, 300);
        return;
    }
    const utterance = new SpeechSynthesisUtterance(text);
    utterance.rate = commentaryRate;
    utterance.volume = commentaryVolume;
    commentarySpeaking = true;
    updateCommentaryStatus('speaking');
    utterance.onend = () => {
        commentarySpeaking = false;
        updateCommentaryStatus(commentaryQueue.length > 0 ? 'queued' : 'idle');
        setTimeout(processQueue, 300);
    };
    utterance.onerror = () => {
        commentarySpeaking = false;
        updateCommentaryStatus('idle');
        setTimeout(processQueue, 300);
    };
    speechSynthesis.speak(utterance);
}

// Toggle commentary on/off
function toggleCommentary() {
    commentaryEnabled = !commentaryEnabled;
    localStorage.setItem('cr-commentary-enabled', commentaryEnabled);

    if (!commentaryEnabled) {
        commentaryPaused = false;
        commentaryQueue = [];
        stopCurrentAudio();
        commentarySpeaking = false;
    }

    updateCommentaryControls();
}

// Pause/resume speech
function toggleCommentaryPause() {
    commentaryPaused = !commentaryPaused;

    if (commentaryPaused) {
        if (commentaryAudio && !commentaryAudio.paused) {
            commentaryAudio.pause();
        } else if ('speechSynthesis' in window && speechSynthesis.speaking) {
            speechSynthesis.pause();
        }
        updateCommentaryStatus('paused');
    } else {
        if (commentaryAudio && commentaryAudio.paused && commentaryAudio.src) {
            commentaryAudio.play().catch(() => {});
        } else if ('speechSynthesis' in window && speechSynthesis.paused) {
            speechSynthesis.resume();
        }
        updateCommentaryStatus('idle');
        processQueue();
    }

    updateCommentaryControls();
}

// Skip current utterance
function skipCommentary() {
    stopCurrentAudio();
    commentarySpeaking = false;
    setTimeout(processQueue, 100);
}

// Clear queue
function clearCommentaryQueue() {
    commentaryQueue = [];
    stopCurrentAudio();
    commentarySpeaking = false;
    updateCommentaryStatus('idle');
}

// Set voice by name
function setCommentaryVoice(voiceName) {
    commentaryVoice = voiceName;
    localStorage.setItem('cr-commentary-voice', voiceName);
}

// Set speech rate
function setCommentaryRate(rate) {
    commentaryRate = parseFloat(rate);
    localStorage.setItem('cr-commentary-rate', commentaryRate);
}

// Set volume
function setCommentaryVolume(vol) {
    commentaryVolume = parseFloat(vol);
    localStorage.setItem('cr-commentary-volume', commentaryVolume);
}

// UI updates
function updateCommentaryStatus(state) {
    const indicator = document.getElementById('commentary-indicator');
    if (!indicator) return;

    if (state === 'speaking') {
        indicator.className = 'commentary-indicator speaking';
        indicator.title = 'Speaking...';
    } else if (state === 'paused') {
        indicator.className = 'commentary-indicator paused';
        indicator.title = 'Paused';
    } else if (state === 'queued') {
        indicator.className = 'commentary-indicator queued';
        indicator.title = commentaryQueue.length + ' queued';
    } else {
        indicator.className = 'commentary-indicator idle';
        indicator.title = 'Commentary active';
    }
}

function updateCommentaryControls() {
    const enableBtn = document.getElementById('commentary-enable-btn');
    const pauseBtn = document.getElementById('commentary-pause-btn');

    if (enableBtn) {
        enableBtn.innerHTML = commentaryEnabled
            ? '<i class="bi bi-volume-up-fill"></i>'
            : '<i class="bi bi-volume-mute-fill"></i>';
        enableBtn.classList.toggle('active', commentaryEnabled);
    }

    if (pauseBtn) {
        pauseBtn.innerHTML = commentaryPaused
            ? '<i class="bi bi-play-fill"></i>'
            : '<i class="bi bi-pause-fill"></i>';
        pauseBtn.disabled = !commentaryEnabled;
    }
}

function renderCommentaryFeed() {
    const feed = document.getElementById('commentary-feed');
    if (!feed) return;

    const recent = commentaryHistory.slice(-10).reverse();
    if (recent.length === 0) {
        feed.innerHTML = '<div class="commentary-empty">No commentary yet. Waiting for action...</div>';
        return;
    }

    feed.innerHTML = recent.map(entry => {
        const time = new Date(entry.timestamp).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
        return '<div class="commentary-entry">' +
            '<span class="commentary-time">' + time + '</span>' +
            '<span class="commentary-text">' + escapeHtml(entry.text) + '</span>' +
            '</div>';
    }).join('');
}

// Unlock audio on iOS — requires user gesture
let audioUnlocked = false;
function unlockAudio() {
    if (audioUnlocked) return;
    if (!('speechSynthesis' in window)) { audioUnlocked = true; return; }
    var unlock = new SpeechSynthesisUtterance('');
    unlock.volume = 0;
    unlock.onend = function() { audioUnlocked = true; };
    speechSynthesis.speak(unlock);
    audioUnlocked = true;
}

// Toggle commentary bar visibility
function toggleCommentaryBar() {
    var bar = document.getElementById('commentary-bar');
    if (!bar) return;
    bar.classList.toggle('d-none');
    if (!bar.classList.contains('d-none')) {
        unlockAudio();
        populateVoiceDropdown();
        renderCommentaryFeed();
    }
}

function populateVoiceDropdown() {
    var select = document.getElementById('commentary-voice-select');
    if (!select) return;

    fetch('/api/tts/voices')
        .then(function(r) { return r.json(); })
        .then(function(voices) {
            select.innerHTML = voices.map(function(v) {
                var selected = commentaryVoice === v.name ? ' selected' : '';
                var label = v.name.replace('Neural', '').replace('en-US-', '').replace('en-GB-', 'UK-').trim();
                return '<option value="' + escapeHtml(v.name) + '"' + selected + '>' + escapeHtml(label) + ' (' + v.gender + ')</option>';
            }).join('');
        })
        .catch(function() {
            select.innerHTML =
                '<option value="en-US-GuyNeural"' + (commentaryVoice === 'en-US-GuyNeural' ? ' selected' : '') + '>Guy (Male)</option>' +
                '<option value="en-US-AriaNeural"' + (commentaryVoice === 'en-US-AriaNeural' ? ' selected' : '') + '>Aria (Female)</option>' +
                '<option value="en-US-AndrewNeural"' + (commentaryVoice === 'en-US-AndrewNeural' ? ' selected' : '') + '>Andrew (Male)</option>' +
                '<option value="en-US-JennyNeural"' + (commentaryVoice === 'en-US-JennyNeural' ? ' selected' : '') + '>Jenny (Female)</option>';
        });
}

// Initialize on load
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initCommentary);
} else {
    initCommentary();
}
