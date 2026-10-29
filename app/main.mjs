import { Room } from './room.mjs';
import { PRESETS, videoParameters } from './protocol.mjs';

const $ = id => document.getElementById(id);
let room = new Room(), capture = null, source = 'monitor', pairMode = '', pendingSession = null, joining = false;
let toastTimer, statsBusy = false, previousStats = new Map(), mediaBusy = false, tuning = Promise.resolve();
let preferences = {};
try { preferences = JSON.parse(localStorage.getItem('drift-preferences') || '{}'); } catch { /* Corrupt preferences reset safely. */ }
$('display-name').value = typeof preferences.name === 'string' ? preferences.name.slice(0, 32) : 'Player';
$('quality').value = PRESETS[preferences.quality] ? preferences.quality : 'efficient';
$('bandwidth').value = Number(preferences.bandwidth) >= 1 && Number(preferences.bandwidth) <= 30 ? preferences.bandwidth : 8;
if (typeof preferences.audio === 'boolean') $('audio').checked = preferences.audio;
if (typeof preferences.stun === 'boolean') $('stun').checked = preferences.stun;
function save() {
  try { localStorage.setItem('drift-preferences', JSON.stringify({ name: $('display-name').value, quality: $('quality').value, bandwidth: Number($('bandwidth').value), audio: $('audio').checked, stun: $('stun').checked })); } catch { /* App remains usable without storage. */ }
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
bindRoom();
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
  $('self-role').textContent = room.isHost ? 'Room host' : 'Connected';
  $('invite-button').disabled = !room.isHost || room.peers.size >= 3;
  $('join-button').hidden = !!room.peers.size;
  $('leave-button').hidden = !room.peers.size;
  $('display-name').disabled = !!room.peers.size;
  $('top-status').textContent = count ? `${count + 1} people. One shared moment.` : 'Your private corner of the internet';
  $('connection-pill').lastChild.textContent = count ? ` ${count} peer${count > 1 ? 's' : ''} connected` : ' Ready when you are';
  $('stat-viewers').replaceChildren(document.createTextNode(`${room.viewers.length} `), small(room.viewers.length === 1 ? 'person' : 'people'));
  $('stage-note').textContent = count ? 'Choose a stream in the room list to watch. Each viewer uses a direct connection.' : 'Start a stream, then invite a friend. No one receives video until they choose to watch.';
  refreshStage();
}
function small(text) { const el = document.createElement('small'); el.textContent = text; return el; }
function setSource(value) {
  source = value;
  for (const key of ['monitor', 'window']) { const button = $('source-' + key); button.classList.toggle('selected', key === value); button.setAttribute('aria-pressed', String(key === value)); }
  $('source-help').textContent = value === 'monitor' ? 'Pick a display in the Windows capture picker.' : 'Pick your game or app in the Windows capture picker.';
}
$('source-monitor').onclick = () => setSource('monitor');
$('source-window').onclick = () => setSource('window');
function refreshStage() {
  const hasVideo = $('videos').childElementCount > 0;
  $('videos').hidden = !hasVideo; $('empty-state').hidden = hasVideo;
  $('sharing-banner').hidden = !capture;
  $('stage-title').textContent = capture ? 'Your moment, live.' : room.connected.length ? 'Your people are here.' : 'Make it a shared moment.';
  $('sharing-label').textContent = capture ? (room.viewers.length ? `Sharing with ${room.viewers.length} viewer${room.viewers.length > 1 ? 's' : ''}` : 'Sharing is ready · waiting for a viewer') : '';
  $('share-button').classList.toggle('stop', !!capture);
  $('share-button').querySelector('span').textContent = capture ? 'Stop sharing' : 'Start sharing';
  $('share-button').querySelector('use').setAttribute('href', capture ? '#i-stop' : '#i-screen');
  $('hero-share').textContent = capture ? 'Choose a stream from the room list' : 'Share your screen';
  $('hero-share').disabled = !!capture;
  $('source-monitor').disabled = !!capture;
  $('source-window').disabled = !!capture;
  $('source-help').textContent = capture ? 'Stop sharing to choose a different source.' : source === 'monitor' ? 'Pick a display in the Windows capture picker.' : 'Pick your game or app in the Windows capture picker.';
  if (capture) {
    $('empty-state').querySelector('h3').textContent = 'Your screen is ready.';
    $('empty-state').querySelector('p').textContent = room.connected.length ? 'Your friends can choose Watch stream to join in.' : 'Invite someone to share the moment with you.';
    $('empty-state').querySelector('small').textContent = 'Local preview is off to save rendering work.';
  } else {
    $('empty-state').querySelector('h3').innerHTML = 'A front-row seat.<br>From anywhere.';
    $('empty-state').querySelector('p').innerHTML = "Share your next clutch, explore a new world,<br>or just show a friend what you're up to.";
    $('empty-state').querySelector('small').textContent = 'You choose exactly what they see.';
  }
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
async function startCapture() {
  if (mediaBusy) return;
  if (capture) { await stopCapture(); return; }
  if (!navigator.mediaDevices?.getDisplayMedia) { toast('Screen capture is unavailable. Use Drift on Windows with an up-to-date WebView2 runtime.'); return; }
  mediaBusy = true; $('share-button').disabled = true; $('hero-share').disabled = true;
  let next;
  try {
    const preset = PRESETS[$('quality').value];
    next = await navigator.mediaDevices.getDisplayMedia({
      video: { displaySurface: source, width: { ideal: preset.width, max: preset.width }, height: { ideal: preset.height, max: preset.height }, frameRate: { ideal: preset.fps, max: preset.fps } },
      audio: $('audio').checked ? { echoCancellation: false, noiseSuppression: false, autoGainControl: false, suppressLocalAudioPlayback: false } : false,
      systemAudio: $('audio').checked ? 'include' : 'exclude', windowAudio: $('audio').checked ? 'system' : 'exclude', selfBrowserSurface: 'exclude', surfaceSwitching: 'exclude'
    });
    const track = next.getVideoTracks()[0];
    if (!track || track.readyState !== 'live') throw new Error('The capture source is unavailable. Please choose another.');
    track.contentHint = 'motion';
    await track.applyConstraints({ width: { ideal: preset.width, max: preset.width }, height: { ideal: preset.height, max: preset.height }, frameRate: { ideal: preset.fps, max: preset.fps } });
    capture = next; track.addEventListener('ended', () => stopCapture().catch(error => toast(error.message)), { once: true });
    await room.setMedia(capture, captureInfo());
    await tuneSenders(); updatePreview(); updateQuality();
    if ($('audio').checked && !capture.getAudioTracks().some(t => t.readyState === 'live')) toast('Screen sharing started without audio. Your selected source did not provide an audio track.');
    for (const audio of capture.getAudioTracks()) audio.addEventListener('ended', () => toast('The captured audio stopped. Stop and start sharing to choose audio again.'), { once: true });
    window.chrome?.webview?.postMessage({ type: 'sharing', active: true });
  } catch (error) {
    next?.getTracks().forEach(track => track.stop()); capture = null;
    await room.setMedia(null).catch(() => {});
    if (error.name !== 'NotAllowedError') toast('Could not share: ' + error.message);
  } finally { mediaBusy = false; $('share-button').disabled = false; refreshStage(); }
}
function captureInfo() {
  const settings = capture?.getVideoTracks()[0]?.getSettings() || {};
  return { width: settings.width || 0, height: settings.height || 0, fps: Math.round(settings.frameRate || 0), audio: !!capture?.getAudioTracks().some(t => t.enabled && t.readyState === 'live') };
}
async function stopCapture() {
  const previous = capture; capture = null;
  // Stop the source first, even if a peer or an encoder rejects cleanup.
  previous?.getTracks().forEach(track => track.stop());
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
          const modified = videoParameters(parameters, PRESETS[$('quality').value], Number($('bandwidth').value), room.viewers.length);
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
  $('quality-hint').textContent = hints[$('quality').value]; $('bandwidth-value').textContent = $('bandwidth').value + ' Mbps';
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
  $('network-option').hidden = true; $('code-output-section').hidden = true; $('code-input-section').hidden = true;
  $('cancel-invite').hidden = true; $('pair-action').hidden = false; $('pair-action').disabled = false;
  $('code-warning').textContent = 'Contains connection details. Send privately to someone you trust. Expires after 10 minutes.';
}
function openInvite() {
  if (pendingSession) { pairMode = 'accept-reply'; $('pair-dialog').showModal(); return; }
  resetPairUI(); pairMode = 'create';
  $('pair-eyebrow').textContent = 'BRING SOMEONE ALONG'; $('pair-title').textContent = 'Invite a friend.';
  $('pair-description').textContent = 'Create a private invitation, send it to your friend, then paste their reply here to connect. Each friend needs a fresh invitation.';
  $('network-option').hidden = !!room.peers.size;
  if (room.peers.size) $('stun').checked = room.stun;
  $('pair-action').textContent = 'Create invitation →'; $('pair-dialog').showModal();
}
function openJoin() {
  resetPairUI(); pairMode = 'join';
  $('pair-eyebrow').textContent = 'PULL UP A CHAIR'; $('pair-title').textContent = "Join your friend's room.";
  $('pair-description').textContent = 'Paste their invitation. Drift creates a reply for you to send back. A connection starts when your friend accepts it.';
  $('code-input-section').hidden = false; $('code-input-label').textContent = "YOUR FRIEND'S INVITATION";
  $('pair-action').textContent = 'Create reply →'; $('pair-dialog').showModal();
}
async function pairAction() {
  $('pair-action').disabled = true; $('pair-close').disabled = true; $('pair-error').textContent = '';
  try {
    if (pairMode === 'create') {
      $('pair-action').textContent = 'Finding connection routes…'; save();
      const result = await room.createInvite($('display-name').value, $('stun').checked);
      pendingSession = result.session; pairMode = 'accept-reply';
      $('network-option').hidden = true; $('code-output-section').hidden = false;
      $('code-output-label').textContent = '1. SEND THIS INVITATION'; $('code-output').value = result.code;
      $('code-input-section').hidden = false; $('code-input-label').textContent = "2. PASTE YOUR FRIEND'S REPLY";
      $('pair-action').textContent = 'Accept reply & connect →'; $('cancel-invite').hidden = false;
    } else if (pairMode === 'accept-reply') {
      await room.acceptReply($('code-input').value); pendingSession = null; $('pair-dialog').close(); toast('Reply accepted. Connecting directly to your friend…');
    } else if (pairMode === 'join') {
      $('pair-action').textContent = 'Finding connection routes…';
      const code = await room.acceptInvite($('code-input').value, $('display-name').value); joining = true;
      $('code-input-section').hidden = true; $('code-output-section').hidden = false;
      $('code-output-label').textContent = 'SEND THIS REPLY BACK'; $('code-output').value = code;
      $('code-warning').textContent = 'Send this privately to your host, then wait here for them to accept it.';
      $('pair-description').textContent = 'One last step: send this reply to your friend. Keep Drift open while they accept it.';
      $('pair-action').hidden = true; pairMode = 'waiting';
    }
    renderRoom();
  } catch (error) {
    $('pair-error').textContent = error.message;
    if (pairMode === 'create') $('pair-action').textContent = 'Create invitation →';
    if (pairMode === 'join') $('pair-action').textContent = 'Create reply →';
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
  room.leave(); room = new Room(); bindRoom(); roomReadyListener();
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
$('pair-close').onclick = () => $('pair-dialog').close();
$('cancel-invite').onclick = () => { room.cancelInvite(pendingSession); pendingSession = null; $('pair-dialog').close(); renderRoom(); };
$('pair-dialog').addEventListener('cancel', event => { if ($('pair-action').disabled) event.preventDefault(); });
$('copy-code').onclick = async () => {
  try { await navigator.clipboard.writeText($('code-output').value); toast('Code copied. Send it privately to your friend.'); }
  catch { $('code-output').select(); toast('Press Ctrl+C to copy the selected code.'); }
};
$('leave-button').onclick = resetRoom;
$('hero-share').onclick = startCapture; $('share-button').onclick = startCapture; $('banner-stop').onclick = () => stopCapture().catch(error => toast(error.message));
$('quality').onchange = changeQuality;
$('bandwidth').oninput = () => { updateQuality(); };
$('bandwidth').onchange = () => { save(); tuneSenders().catch(error => toast(error.message)); };
$('audio').onchange = async () => {
  save();
  if (!capture) return;
  if ($('audio').checked && !capture.getAudioTracks().some(t => t.readyState === 'live')) { toast('Stop and start sharing, then enable audio in the Windows picker.'); return; }
  for (const track of capture.getAudioTracks()) track.enabled = $('audio').checked;
  room.mediaInfo = captureInfo(); room.broadcast({ type: 'sharing', active: true, info: room.mediaInfo });
};
$('preview').onchange = updatePreview;
$('display-name').oninput = () => { save(); renderRoom(); };
$('about-button').onclick = () => $('about-dialog').showModal(); $('about-close').onclick = () => $('about-dialog').close();
window.addEventListener('beforeunload', () => { capture?.getTracks().forEach(track => track.stop()); room.leave(); });
document.addEventListener('keydown', event => { if (event.ctrlKey && event.shiftKey && event.code === 'KeyS' && capture) { event.preventDefault(); stopCapture().catch(error => toast(error.message)); } });
window.chrome?.webview?.addEventListener('message', event => {
  if (event.data?.type === 'stop-sharing') stopCapture().catch(error => toast(error.message));
  if (event.data?.type === 'native-ready') $('shortcut-hint').textContent = event.data.hotkey ? 'Stop from anywhere · Ctrl + Shift + S' : 'Stop in Drift · Ctrl + Shift + S';
  if (event.data?.type === 'minimized') { document.body.classList.toggle('native-minimized', event.data.active); if (event.data.active) { for (const v of document.querySelectorAll('video')) v.pause(); } else { for (const v of document.querySelectorAll('video')) v.play().catch(() => {}); } }
});
renderRoom(); updateQuality();
