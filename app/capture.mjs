// Keep the picker request minimal. Detailed capture constraints combined with
// system audio fail on some Windows audio drivers. Apply quality after selection.
export async function requestCapture(mediaDevices, videoOnly = false, onAudioRetry = () => {}, nativeAudioAvailable = false) {
  try {
    return { stream: await mediaDevices.getDisplayMedia({ video: true, audio: !videoOnly }), audioFallback: false };
  } catch (error) {
    if (videoOnly || !isAudioStartFailure(error)) throw error;
    if (nativeAudioAvailable) { error.retryWithNativeAudio = true; throw error; }
    onAudioRetry();
    try {
      return { stream: await mediaDevices.getDisplayMedia({ video: true, audio: false }), audioFallback: true };
    } catch (retryError) {
      // Some runtimes require a fresh click for a second picker. The caller can
      // present an explicit video-only action rather than losing the retry path.
      if (retryError.name === 'InvalidStateError') retryError.retryWithoutAudio = true;
      throw retryError;
    }
  }
}
export function isAudioStartFailure(error) {
  return !!error && error.name !== 'NotAllowedError' && /(?:could not|cannot|failed to|unable to) start audio source/i.test(error.message || '');
}
