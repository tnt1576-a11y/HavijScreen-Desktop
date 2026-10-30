import { Room } from './room.mjs';
import { PRESETS, videoParameters } from './protocol.mjs';
import { requestCapture } from './capture.mjs';
import { startNativeAudio } from './native-audio.mjs';
import { Lobby } from './lobby.mjs';

const $ = id => document.getElementById(id);
let room = new Room(), lobby = new Lobby(room), capture = null, pairMode = '', pendingSession = null, joining = false;
let toastTimer, statsBusy = false, previousStats = new Map(), mediaBusy = false, tuning = Promise.resolve();
let nativeAudio = null, nativeAudioAbort = null, nativeAudioAvailable = false, retryNativeAudio = false;
let interfaceReadyReported = false;
let preferences = {};
try { preferences = JSON.parse(localStorage.getItem('havijscreen-preferences') || localStorage.getItem('drift-preferences') || '{}'); } catch { /* Corrupt preferences reset safely. */ }
$('display-name').value = typeof preferences.name === 'string' ? preferences.name.slice(0, 32) : 'Player';
$('quality').value = PRESETS[preferences.quality] ? preferences.quality : 'efficient';
if (typeof preferences.stun === 'boolean') $('stun').checked = preferences.stun;
function save() {
  try { localStorage.setItem('havijscreen-preferences', JSON.stringify({ name: $('display-name').value, quality: $('quality').value, stun: $('stun').checked })); } catch { /* App remains usable without storage. */ }
}
function toast(message) { clearTimeout(toastTimer); $('toast').textContent = message; $('toast').hidden = false; toastTimer = setTimeout(() => { $('toast').hidden = true; }, 9000); }
function bindRoom() {
  room.addEventListener('peer', renderRoom);
  room.addEventListener('state', renderRoom);
  room.addEventListener('departed', event => { removeVideo(event.detail.id); renderRoom(); });
  room.addEventListener('track', event => { if (event.detail.subscribed) showRemote(event.detail); });
  room.addEventListener('unwatch', event => removeVideo(event.detail.id));
  room.addEventListener('viewers', () => { renderRoom(); tuneSenders().catch(error => toast(error.message)); });
  room.addEventListener('notice', event => toast(event.detail));
  room.addEventListener('ended', event => { resetRoom(); toast(event.detail); });
}
bindRoom(); bindLobby();
function bindLobby() {
  const bound = lobby;
  lobby.addEventListener('requests', () => {
    if (lobby !== bound) return;
    renderRequests();
    if (pairMode === 'short-host' && $('pair-dialog').open && [...lobby.requests.values()].some(r => r.phase === 'pending')) $('pair-dialog').close();
  });
  lobby.addEventListener('state', event => {
    if (lobby !== bound) return;
    const { state, message } = event.detail;
    if (state === 'waiting' || state === 'connecting') { $('pair-description').textContent = message; $('pair-action').hidden = true; }
    if (state === 'connected') { $('pair-dialog').close(); toast('Connected to the room.'); }
    $('lobby-status').hidden = state !== 'offline'; $('lobby-status').textContent = message;
    if (state === 'offline' && pairMode === 'short-host') { $('pair-error').textContent = message; $('pair-action').hidden = false; $('pair-action').textContent = 'Reconnect room code'; }
    renderRoom();
  });
  lobby.addEventListener('failure', event => {
    if (lobby !== bound) return;
    resetRoom(); openJoin(); $('pair-error').textContent = event.detail;
  });
}
function renderRequests() {
  $('join-requests').replaceChildren();
  for (const record of lobby.requests.values()) {
    const card = document.createElement('div'); card.className = 'join-request';
    const name = document.createElement('strong'); name.textContent = record.name;
    const status = document.createElement('small'); status.textContent = record.phase === 'pending' ? 'Wants to join' : 'Connecting…';
    const actions = document.createElement('div'); actions.className = 'request-actions';
    if (record.phase === 'pending') { const accept = document.createElement('button'); accept.className = 'accept-request'; accept.textContent = 'Accept'; accept.onclick = () => lobby.approve(record.route).catch(error => toast(error.message)); actions.append(accept); }
    const decline = document.createElement('button'); decline.className = 'decline-request'; decline.textContent = record.phase === 'pending' ? 'Decline' : 'Cancel'; decline.onclick = () => lobby.reject(record.route).catch(error => toast(error.message)); actions.append(decline);
    card.append(name, status, actions); $('join-requests').append(card);
  }
}
function renderRoom() {
  $('people').replaceChildren();
  for (const peer of room.connected) {
    const person = document.createElement('div'); person.className = 'person remote'; person.dataset.peer = peer.id;
    const avatar = document.createElement('div'); avatar.className = 'avatar'; avatar.textContent = peer.name.slice(0, 1).toUpperCase();
    const text = document.createElement('div'); const name = document.createElement('strong'); name.textContent = peer.name;
    const state = document.createElement('small'); state.textContent = peer.sharing ? 'Screen available' : (peer.id === room.hostId ? 'Room host' : 'Connected');
    if (peer.sharing) state.className = 'stream-indicator';
    text.append(name, state); person.append(avatar, text);
    const actions = document.createElement('div'); actions.className = 'remote-actions';
    if (peer.sharing) {
      const watch = document.createElement('button'); watch.className = 'watch-peer'; watch.textContent = peer.subscribed ? 'Stop watching' : 'Watch stream';
      watch.onclick = () => room.watch(peer.id, !peer.subscribed); actions.append(watch);
    }
    if (room.isHost) { const remove = document.createElement('button'); remove.className = 'remove-peer'; remove.textContent = 'Remove'; remove.onclick = () => room.remove(peer.id); actions.append(remove); }
    person.append(actions); $('people').append(person);
  }
  const count = room.connected.length;
  $('room-count').textContent = `${count + 1} / 4`;
  $('self-name').textContent = $('display-name').value.trim() || 'You';
  $('self-role').textContent = lobby.joining ? (lobby.state === 'waiting' ? 'Waiting for approval' : 'Joining…') : (room.isHost ? 'Room host' : (room.connected.some(p => p.id === room.hostId) ? 'Connected' : 'Joining…'));
  $('finish-join').hidden = !(joining || lobby.joining) || !!room.connected.length;
  $('invite-button').disabled = !room.isHost || lobby.role === 'guest' || room.peers.size >= 3;
  $('join-button').hidden = !!room.peers.size || !!lobby.role;
  $('leave-button').hidden = !room.peers.size && !lobby.role;
  $('display-name').disabled = !!room.peers.size || !!lobby.role;
  $('stat-viewers').replaceChildren(document.createTextNode(`${room.viewers.length} `), small(room.viewers.length === 1 ? 'person' : 'people'));
  refreshStage();
}
function small(text) { const el = document.createElement('small'); el.textContent = text; return el; }
function refreshStage() {
  const hasVideo = $('videos').childElementCount > 0;
  $('videos').hidden = !hasVideo; $('empty-state').hidden = hasVideo;
  $('sharing-banner').hidden = !capture;
  $('sharing-label').textContent = capture ? (room.viewers.length ? `${room.viewers.length} watching` : 'Sharing · no viewers') : '';
  $('audio-status').textContent = captureInfo().audio ? 'Audio on' : 'Video only';
  $('share-button').classList.toggle('stop', !!capture);
  $('share-button').querySelector('span').textContent = capture ? 'Stop sharing' : 'Start sharing';
  $('share-button').querySelector('use').setAttribute('href', capture ? '#i-stop' : '#i-screen');
  $('hero-share').querySelector('span').textContent = capture ? 'Sharing' : 'Share your screen';
  $('hero-share').disabled = !!capture;
}
function videoCard(id, name, stream, local = false) {
  let card = document.querySelector(`[data-video="${id}"]`);
  if (card) { card.querySelector('video').srcObject = stream; return card; }
  card = document.createElement('div'); card.className = 'video-card'; card.dataset.video = id;
  const video = document.createElement('video'); video.autoplay = true; video.playsInline = true; video.muted = true; video.srcObject = stream;
  const caption = document.createElement('div'); caption.className = 'video-caption'; const label = document.createElement('span'); label.textContent = name; caption.append(label);
  if (!local) {
    const audio = document.createElement('button'); audio.textContent = 'Enable sound';
    audio.onclick = async () => { video.muted = !video.muted; audio.textContent = video.muted ? 'Enable sound' : 'Mute sound'; await video.play().catch(() => toast('Click Enable sound to play audio.')); }; caption.append(audio);
  }
  const fullscreen = document.createElement('button'); fullscreen.textContent = 'Full screen'; fullscreen.onclick = () => card.requestFullscreen().catch(error => toast(error.message)); caption.append(fullscreen);
  card.append(video, caption); $('videos').append(card); video.play().catch(() => {}); refreshStage(); return card;
}
function showRemote(peer) { videoCard(peer.id, peer.name, peer.stream); }
function removeVideo(id) {
  const card = document.querySelector(`[data-video="${id}"]`);
  if (card) { const video = card.querySelector('video'); video.pause(); video.srcObject = null; card.remove(); }
  refreshStage();
}
function updatePreview() { if (capture && $('preview').checked) videoCard('local', 'Your preview · audio muted', capture, true); else removeVideo('local'); }
async function startCapture(videoOnly = false, windowsAudio = false) {
  // DOM click handlers pass MouseEvent; only the explicit retry passes true.
  videoOnly = videoOnly === true;
  if (mediaBusy) return;
  if (capture) { await stopCapture(); return; }
  if (!navigator.mediaDevices?.getDisplayMedia) { toast('Screen capture is unavailable. Use HavijScreen on Windows with an up-to-date WebView2 runtime.'); return; }
  mediaBusy = true; $('share-button').disabled = true; $('hero-share').disabled = true;
  let next;
  try {
    const preset = PRESETS[$('quality').value];
    const result = await requestCapture(navigator.mediaDevices, videoOnly, () => toast('Audio could not start. Choose the source again to share video only.'), nativeAudioAvailable);
    next = result.stream;
    const track = next.getVideoTracks()[0];
    if (!track || track.readyState !== 'live') throw new Error('The capture source is unavailable. Please choose another.');
    track.addEventListener('ended', () => stopCapture().catch(error => toast(error.message)), { once: true });
    if (windowsAudio && nativeAudioAvailable) {
      nativeAudioAbort = new AbortController();
      nativeAudio = await startNativeAudio(window.chrome.webview, message => {
        if (!capture) return;
        room.mediaInfo = captureInfo(); room.broadcast({ type: 'sharing', active: true, info: room.mediaInfo }); refreshStage();
        toast(message || 'Shared audio stopped.');
      }, nativeAudioAbort.signal);
      next.addTrack(nativeAudio.track);
    }
    track.contentHint = 'motion';
    try {
      await track.applyConstraints({ width: { ideal: preset.width, max: preset.width }, height: { ideal: preset.height, max: preset.height }, frameRate: { ideal: preset.fps, max: preset.fps } });
    } catch { toast('The source is using its available capture quality.'); }
    if (track.readyState !== 'live') throw new DOMException('Capture stopped.', 'AbortError');
    capture = next;
    await room.setMedia(capture, captureInfo());
    await tuneSenders(); updatePreview(); updateQuality();
    if (result.audioFallback) toast('Sharing video only. System audio could not start.');
    for (const audio of capture.getAudioTracks()) audio.addEventListener('ended', () => { refreshStage(); toast('Shared audio stopped.'); }, { once: true });
    window.chrome?.webview?.postMessage({ type: 'sharing', active: true });
  } catch (error) {
    next?.getTracks().forEach(track => track.stop()); capture = null;
    nativeAudioAbort?.abort(); nativeAudioAbort = null; nativeAudio?.stop(); nativeAudio = null;
    await room.setMedia(null).catch(() => {});
    if (error.retryWithNativeAudio || error.retryWithoutAudio) {
      retryNativeAudio = nativeAudioAvailable;
      $('capture-error-title').textContent = retryNativeAudio ? 'Use Windows audio capture?' : "System audio couldn't start.";
      $('capture-error-description').textContent = retryNativeAudio ? 'Select the screen or window once more. HavijScreen will capture system audio directly through Windows.' : 'You can still share video without audio.';
      $('retry-video').textContent = retryNativeAudio ? 'Choose source & share with audio' : 'Share video only';
      $('capture-error-dialog').showModal();
    }
    else if (error.name !== 'NotAllowedError' && error.name !== 'AbortError') toast('Could not share: ' + error.message);
  } finally { mediaBusy = false; $('share-button').disabled = false; refreshStage(); }
}
function captureInfo() {
  const settings = capture?.getVideoTracks()[0]?.getSettings() || {};
  return { width: settings.width || 0, height: settings.height || 0, fps: Math.round(settings.frameRate || 0), audio: !!capture?.getAudioTracks().some(t => t.enabled && t.readyState === 'live') };
}
async function stopCapture() {
  const previous = capture; capture = null;
  nativeAudioAbort?.abort(); nativeAudioAbort = null;
  // Stop the source first, even if a peer or an encoder rejects cleanup.
  previous?.getTracks().forEach(track => track.stop());
  nativeAudio?.stop(); nativeAudio = null;
  removeVideo('local');
  await room.setMedia(null).catch(error => toast('Capture stopped. Peer cleanup: ' + error.message));
  previousStats.clear(); $('stat-quality').textContent = 'Not sharing'; $('stat-upload').replaceChildren(document.createTextNode('— '), small('Mbps')); $('stat-encoder').textContent = 'Waiting for a viewer';
  window.chrome?.webview?.postMessage({ type: 'sharing', active: false }); refreshStage();
}
function tuneSenders() {
  tuning = tuning.catch(() => {}).then(async () => {
    for (const peer of room.connected) {
      for (const sender of peer.pc.getSenders()) {
        if (!sender.track) continue;
        const parameters = sender.getParameters();
        if (!parameters.encodings?.length) continue;
        if (sender.track.kind === 'video') {
          const modified = videoParameters(parameters, PRESETS[$('quality').value]);
          if (modified) await sender.setParameters(modified);
        } else { parameters.encodings[0].maxBitrate = 96000; await sender.setParameters(parameters); }
      }
    }
  });
  return tuning;
}
async function changeQuality() {
  save();
  if (capture) {
    const preset = PRESETS[$('quality').value];
    try {
      await capture.getVideoTracks()[0].applyConstraints({ width: { ideal: preset.width, max: preset.width }, height: { ideal: preset.height, max: preset.height }, frameRate: { ideal: preset.fps, max: preset.fps } });
      room.mediaInfo = captureInfo(); room.broadcast({ type: 'sharing', active: true, info: room.mediaInfo });
      await tuneSenders();
    } catch (error) { toast('Could not apply the selected quality: ' + error.message); }
  }
  updateQuality();
}
function updateQuality() {
  const hints = { efficient: 'A lighter load. More room for your game.', balanced: 'More detail, with a moderate frame rate.', fluid: 'Smoother motion. Uses more encoding work.', crisp: 'Maximum detail. More GPU and upload work.' };
  $('quality-hint').textContent = hints[$('quality').value];
  if (capture) { const info = captureInfo(); $('stat-quality').textContent = `${info.height || '?'}p / ${info.fps || '?'} fps`; }
}
async function pollStats() {
  if (statsBusy || document.hidden) return;
  statsBusy = true;
  try {
    let bytesPerSecond = 0, roundTrip = [], encoders = new Set();
    for (const peer of room.connected) {
      const report = await peer.pc.getStats();
      for (const stat of report.values()) {
        if (stat.type === 'outbound-rtp' && !stat.isRemote) {
          const key = peer.id + stat.id; const prior = previousStats.get(key);
          if (prior && stat.timestamp > prior.timestamp) bytesPerSecond += Math.max(0, stat.bytesSent - prior.bytes) * 1000 / (stat.timestamp - prior.timestamp);
          previousStats.set(key, { bytes: stat.bytesSent, timestamp: stat.timestamp });
          if (stat.kind === 'video' && room.viewers.length) {
            if (stat.powerEfficientEncoder === true) encoders.add('Hardware reported');
            else if (stat.encoderImplementation) encoders.add(stat.encoderImplementation);
            else encoders.add('Runtime managed');
          }
        }
        if (stat.type === 'transport' && stat.selectedCandidatePairId) {
          const pair = report.get(stat.selectedCandidatePairId);
          if (pair?.currentRoundTripTime !== undefined) roundTrip.push(pair.currentRoundTripTime * 1000);
        }
      }
    }
    $('stat-upload').replaceChildren(document.createTextNode((bytesPerSecond * 8 / 1000000).toFixed(2) + ' '), small('Mbps'));
    $('stat-latency').replaceChildren(document.createTextNode((roundTrip.length ? Math.round(Math.max(...roundTrip)) : '—') + ' '), small('ms'));
    $('stat-encoder').textContent = encoders.size ? [...encoders].join(', ') : 'Waiting for a viewer';
  } catch { /* A connection may close while its final stats are being read. */ }
  finally { statsBusy = false; }
}
setInterval(pollStats, 2500);
function resetPairUI() {
  $('pair-error').textContent = ''; $('code-output').value = ''; $('code-input').value = '';
  $('code-output-details').open = false; $('code-input-details').open = false; $('input-status').hidden = true;
  $('network-option').hidden = true; $('code-output-section').hidden = true; $('code-input-section').hidden = true;
  $('short-code-output-section').hidden = true; $('short-code-input-section').hidden = true; $('manual-pairing').hidden = false;
  $('cancel-invite').hidden = true; $('pair-action').hidden = false; $('pair-action').disabled = false;
  $('code-warning').textContent = 'Private and single-use. Expires after 10 minutes.';
  $('cancel-invite').textContent = 'Cancel invitation';
}
function openInvite() {
  if (pendingSession) { openManualInvite(); return; }
  resetPairUI(); pairMode = 'short-create';
  $('pair-eyebrow').textContent = 'INVITE'; $('pair-title').textContent = 'Invite someone';
  $('pair-description').textContent = 'You approve each person. PeerJS Cloud delivers encrypted connection setup; screens stay peer-to-peer.';
  $('network-option').hidden = !!room.peers.size || !!lobby.code;
  if (room.peers.size) $('stun').checked = room.stun;
  $('pair-action').textContent = 'Create room code';
  if (lobby.code) showRoomCode();
  $('pair-dialog').showModal();
}
function showRoomCode() {
  pairMode = 'short-host'; $('room-code-output').value = lobby.code;
  $('short-code-output-section').hidden = false; $('network-option').hidden = true;
  $('pair-description').textContent = 'Send this code to your friend. Their request will appear in the room.';
  $('pair-action').hidden = lobby.state === 'hosting'; $('pair-action').textContent = 'Reconnect room code';
  $('manual-pairing').hidden = true; $('cancel-invite').hidden = false; $('cancel-invite').textContent = 'Disable room code';
}
function openJoin() {
  resetPairUI(); pairMode = 'short-join';
  $('pair-eyebrow').textContent = 'JOIN'; $('pair-title').textContent = 'Join a room';
  $('pair-description').textContent = 'Enter your friend’s room code. They’ll approve your request.';
  $('short-code-input-section').hidden = false; $('room-code-input').value = '';
  $('pair-action').textContent = 'Request to join'; $('pair-dialog').showModal();
}
function openManualInvite() {
  if (pendingSession) { pairMode = 'accept-reply'; $('pair-dialog').showModal(); return; }
  resetPairUI(); pairMode = 'create';
  $('pair-eyebrow').textContent = 'INVITE'; $('pair-title').textContent = 'Invite a friend';
  $('pair-description').textContent = 'Copy an invitation for your friend, then paste their reply here.';
  $('network-option').hidden = !!room.peers.size;
  if (room.peers.size) $('stun').checked = room.stun;
  $('pair-action').textContent = 'Create invitation →'; $('pair-dialog').showModal();
  $('manual-pairing').hidden = true;
}
function openManualJoin() {
  resetPairUI(); pairMode = 'join';
  $('pair-eyebrow').textContent = 'JOIN'; $('pair-title').textContent = "Join your friend's room";
  $('pair-description').textContent = 'Paste their invitation to create a private reply.';
  $('code-input-section').hidden = false; $('code-input-label').textContent = "YOUR FRIEND'S INVITATION";
  $('paste-code').textContent = 'Paste invitation';
  $('pair-action').textContent = 'Create reply →'; $('pair-dialog').showModal();
  $('manual-pairing').hidden = true;
}
async function pairAction() {
  $('pair-action').disabled = true; $('pair-close').disabled = true; $('pair-error').textContent = '';
  try {
    if (pairMode === 'short-create' || pairMode === 'short-host') {
      $('pair-action').textContent = 'Connecting to service…'; save();
      await lobby.host($('display-name').value, $('stun').checked); showRoomCode();
    } else if (pairMode === 'short-join') {
      $('pair-action').textContent = 'Sending request…';
      await lobby.join($('room-code-input').value, $('display-name').value);
      pairMode = 'short-wait'; $('short-code-input-section').hidden = true; $('manual-pairing').hidden = true;
      $('pair-action').hidden = true; $('cancel-invite').hidden = false; $('cancel-invite').textContent = 'Cancel joining';
    } else if (pairMode === 'create') {
      $('pair-action').textContent = 'Finding connection routes…'; save();
      const result = await room.createInvite($('display-name').value, $('stun').checked);
      pendingSession = result.session; pairMode = 'accept-reply';
      $('network-option').hidden = true; $('code-output-section').hidden = false;
      $('code-output-label').textContent = '1. Send your invitation'; $('code-output').value = result.code; $('copy-code-label').textContent = 'Copy invitation';
      $('code-input-section').hidden = false; $('code-input-label').textContent = "2. PASTE YOUR FRIEND'S REPLY";
      $('paste-code').textContent = 'Paste reply';
      $('pair-action').textContent = 'Accept reply & connect →'; $('cancel-invite').hidden = false;
    } else if (pairMode === 'accept-reply') {
      await room.acceptReply($('code-input').value); pendingSession = null; $('pair-dialog').close(); toast('Reply accepted. Your friend can now choose Connect.');
    } else if (pairMode === 'join') {
      $('pair-action').textContent = 'Finding connection routes…';
      const code = await room.acceptInvite($('code-input').value, $('display-name').value); joining = true;
      $('code-input-section').hidden = true; $('code-output-section').hidden = false;
      $('code-output-label').textContent = 'Send your reply'; $('code-output').value = code; $('copy-code-label').textContent = 'Copy reply';
      $('code-warning').textContent = 'Keep HavijScreen open. Your invitation is valid for 10 minutes from creation.';
      $('pair-description').textContent = 'Send your reply to the host. After they accept it, choose Connect.';
      $('pair-action').textContent = 'Connect'; $('cancel-invite').hidden = false; $('cancel-invite').textContent = 'Cancel joining'; pairMode = 'waiting';
    } else if (pairMode === 'waiting') {
      await room.connectToHost(); pairMode = 'connecting';
      $('pair-action').hidden = true; $('code-output-section').hidden = true;
      $('pair-description').textContent = 'Connecting directly to the host…';
    }
    renderRoom();
  } catch (error) {
    $('pair-error').textContent = error.message;
    if (pairMode === 'create') $('pair-action').textContent = 'Create invitation →';
    if (pairMode === 'join') $('pair-action').textContent = 'Create reply →';
    if (pairMode === 'short-create' || pairMode === 'short-host') $('pair-action').textContent = 'Retry connection service';
    if (pairMode === 'short-join') {
      lobby.stop(); lobby = new Lobby(room); bindLobby(); $('pair-action').textContent = 'Request to join';
    }
  } finally { $('pair-action').disabled = false; $('pair-close').disabled = false; }
}
roomReadyListener();
function roomReadyListener() {
  room.addEventListener('peer', () => {
    if (joining && room.connected.length) { joining = false; $('pair-dialog').close(); toast('Connected. Your friend can now choose to watch your stream.'); }
  });
}
function resetRoom() {
  capture?.getTracks().forEach(track => track.stop()); capture = null;
  nativeAudioAbort?.abort(); nativeAudioAbort = null;
  nativeAudio?.stop(); nativeAudio = null;
  lobby.stop(); room.leave(); room = new Room(); lobby = new Lobby(room); bindRoom(); bindLobby(); roomReadyListener();
  renderRequests(); $('lobby-status').hidden = true;
  pendingSession = null; joining = false; previousStats.clear();
  for (const video of [...$('videos').children]) removeVideo(video.dataset.video);
  $('pair-dialog').close(); $('stat-quality').textContent = 'Not sharing'; renderRoom();
  $('stat-upload').replaceChildren(document.createTextNode('— '), small('Mbps'));
  $('stat-latency').replaceChildren(document.createTextNode('— '), small('ms'));
  $('stat-encoder').textContent = 'Waiting for a viewer';
  window.chrome?.webview?.postMessage({ type: 'sharing', active: false });
}
setInterval(() => {
  room.expireOffers();
  if (pendingSession && !room.offers.has(pendingSession)) {
    pendingSession = null; $('pair-dialog').close(); toast('Your invitation expired. Create a fresh one to connect.'); renderRoom();
  }
}, 5000);
$('invite-button').onclick = openInvite; $('join-button').onclick = openJoin; $('pair-action').onclick = pairAction;
$('finish-join').onclick = () => $('pair-dialog').showModal();
$('pair-close').onclick = () => $('pair-dialog').close();
$('cancel-invite').onclick = () => {
  if (joining || lobby.joining) { resetRoom(); return; }
  if (lobby.role === 'host') { lobby.stop(); lobby = new Lobby(room); bindLobby(); $('lobby-status').hidden = true; renderRequests(); }
  room.cancelInvite(pendingSession); pendingSession = null; $('pair-dialog').close(); renderRoom();
};
$('manual-pairing').onclick = () => { $('pair-dialog').close(); if (pairMode === 'short-join') openManualJoin(); else openManualInvite(); };
$('pair-dialog').addEventListener('cancel', event => { if ($('pair-action').disabled) event.preventDefault(); });
$('copy-code').onclick = async () => {
  try { await navigator.clipboard.writeText($('code-output').value); toast('Code copied. Send it privately to your friend.'); }
  catch { $('code-output-details').open = true; $('code-output').select(); toast('Press Ctrl+C to copy the selected code.'); }
};
$('paste-code').onclick = async () => {
  try {
    $('code-input').value = await navigator.clipboard.readText();
    $('code-input').dispatchEvent(new Event('input'));
  } catch {
    $('code-input-details').open = true; $('code-input').focus(); toast('Press Ctrl+V to paste your friend’s code.');
  }
};
$('copy-room-code').onclick = async () => {
  try { await navigator.clipboard.writeText($('room-code-output').value); toast('Room code copied.'); }
  catch { $('room-code-output').select(); toast('Press Ctrl+C to copy the room code.'); }
};
$('code-input').oninput = () => {
  $('input-status').hidden = !$('code-input').value.trim();
  $('input-status').textContent = pairMode === 'join' ? 'Invitation pasted' : 'Reply pasted';
};
$('leave-button').onclick = resetRoom;
$('hero-share').onclick = startCapture; $('share-button').onclick = startCapture; $('banner-stop').onclick = () => stopCapture().catch(error => toast(error.message));
$('quality').onchange = changeQuality;
$('preview').onchange = updatePreview;
$('display-name').oninput = () => { save(); renderRoom(); };
$('capture-error-close').onclick = () => $('capture-error-dialog').close();
$('retry-video').onclick = () => { $('capture-error-dialog').close(); startCapture(true, retryNativeAudio); };
for (const action of ['minimize', 'maximize', 'close']) $('window-' + action).onclick = () => window.chrome?.webview?.postMessage({ type: 'window-' + action });
window.addEventListener('beforeunload', () => { lobby.stop(); nativeAudioAbort?.abort(); nativeAudio?.stop(); capture?.getTracks().forEach(track => track.stop()); room.leave(); });
document.addEventListener('keydown', event => { if (event.ctrlKey && event.shiftKey && event.code === 'KeyS' && capture) { event.preventDefault(); stopCapture().catch(error => toast(error.message)); } });
window.chrome?.webview?.addEventListener('message', event => {
  if (event.data?.type === 'stop-sharing') stopCapture().catch(error => toast(error.message));
  if (event.data?.type === 'native-ready') {
    nativeAudioAvailable = event.data.audioLoopback === true; $('window-controls').hidden = false;
    $('shortcut-hint').textContent = event.data.hotkey ? 'Stop · Ctrl + Shift + S' : 'Stop in HavijScreen · Ctrl + Shift + S';
    const ready = () => { if (!interfaceReadyReported) { interfaceReadyReported = true; window.chrome.webview.postMessage({ type: 'ui-ready' }); } };
    // A bounded fallback also works when the renderer is occluded and animation
    // frames are throttled while the native startup surface covers the WebView.
    const timeout = setTimeout(ready, 250);
    requestAnimationFrame(() => requestAnimationFrame(() => { clearTimeout(timeout); ready(); }));
  }
  if (event.data?.type === 'window-state') {
    const max = event.data.maximized === true;
    $('window-maximize').setAttribute('aria-label', max ? 'Restore' : 'Maximize');
    $('window-maximize').title = max ? 'Restore' : 'Maximize';
    document.body.classList.toggle('maximized', max);
  }
  if (event.data?.type === 'minimized') { document.body.classList.toggle('native-minimized', event.data.active); if (event.data.active) { for (const v of document.querySelectorAll('video')) v.pause(); } else { for (const v of document.querySelectorAll('video')) v.play().catch(() => {}); } }
});
renderRoom(); updateQuality();
