(() => {
  'use strict';

  let mediaRecorder = null;
  let chunks = [];
  let stream = null;
  let startedAt = 0;
  let cancelled = false;
  let audioCtx = null;
  let analyser = null;
  let levelTimer = null;
  let maxTimer = null;
  const MAX_DURATION_MS = 5 * 60 * 1000; // автостоп и отправка через 5 минут
  const BAND_COUNT = 28;

  function post(type, payload) {
    try {
      // Важно: передавать объект, а не строку JSON. WebMessageAsJson тогда будет объектом;
      // строка оборачивается WebView2 в кавычки и C# не может прочитать type/bands.
      if (window.chrome && window.chrome.webview)
        window.chrome.webview.postMessage({ type, ...(payload || {}) });
    } catch (_) {}
  }

  // Никогда не отдавать "[object Object]" в UI
  function errText(e) {
    if (e == null) return 'unknown error';
    if (typeof e === 'string') return e;
    if (e.message) return String(e.message);
    if (e.name) return String(e.name);
    try { return JSON.stringify(e); } catch (_) { return String(e); }
  }
  window.addEventListener('unhandledrejection', (ev) => {
    post('error', { error: 'async error: ' + errText(ev.reason) });
  });

  async function cleanup() {
    if (levelTimer) { clearInterval(levelTimer); levelTimer = null; }
    if (maxTimer) { clearTimeout(maxTimer); maxTimer = null; }
    try { if (mediaRecorder && mediaRecorder.state !== 'inactive') mediaRecorder.stop(); } catch (_) {}
    mediaRecorder = null;
    chunks = [];
    if (stream) {
      try { stream.getTracks().forEach(t => t.stop()); } catch (_) {}
    }
    stream = null;
    analyser = null;
    if (audioCtx) {
      try { await audioCtx.close(); } catch (_) {}
      audioCtx = null;
    }
  }

  // Живая полоска частот: analyser → 28 полос, постим в WPF каждые ~60 мс.
  function startLevels() {
    try {
      audioCtx = new (window.AudioContext || window.webkitAudioContext)();
      if (audioCtx.state === 'suspended') audioCtx.resume().catch(() => {});
      const source = audioCtx.createMediaStreamSource(stream);
      analyser = audioCtx.createAnalyser();
      analyser.fftSize = 256;
      analyser.smoothingTimeConstant = 0.7;
      source.connect(analyser); // не подключаем к destination — без эха
      const bins = new Uint8Array(analyser.frequencyBinCount);
      levelTimer = setInterval(() => {
        if (!analyser) return;
        analyser.getByteFrequencyData(bins);
        // голос сидит в нижней половине спектра — берём её и группируем в полосы
        const usable = Math.floor(bins.length * 0.6);
        const bands = [];
        for (let i = 0; i < BAND_COUNT; i++) {
          const from = Math.floor(i * usable / BAND_COUNT);
          const to = Math.max(from + 1, Math.floor((i + 1) * usable / BAND_COUNT));
          let sum = 0;
          for (let j = from; j < to; j++) sum += bins[j];
          let v = sum / (to - from) / 255;
          v = Math.min(1, Math.pow(v, 0.8) * 1.25); // подъём тихих значений
          bands.push(Math.round(v * 100) / 100);
        }
        post('level', { bands });
      }, 60);
    } catch (_) { /* без анализатора запись всё равно работает */ }
  }

  function pickMime() {
    const candidates = [
      'audio/webm;codecs=opus',
      'audio/webm',
      'audio/ogg;codecs=opus',
      'audio/mp4'
    ];
    for (const m of candidates) {
      try { if (MediaRecorder.isTypeSupported(m)) return m; } catch (_) {}
    }
    return '';
  }

  async function start() {
    cancelled = false;
    await cleanup();
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
      post('error', { error: 'Microphone recording is not supported in this WebView.' });
      return;
    }
    try {
      stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      post('debug', { message: 'getUserMedia ok' });
    } catch (e) {
      post('error', { error: 'Microphone access denied or unavailable: ' + errText(e) });
      return;
    }

    const mime = pickMime();
    try {
      mediaRecorder = mime ? new MediaRecorder(stream, { mimeType: mime }) : new MediaRecorder(stream);
    } catch (e) {
      post('error', { error: 'Failed to start recorder: ' + errText(e) });
      await cleanup();
      return;
    }
    post('debug', { message: 'MediaRecorder ok, mime=' + (mime || 'default') });

    chunks = [];
    mediaRecorder.ondataavailable = (ev) => { if (ev.data && ev.data.size > 0) chunks.push(ev.data); };
    mediaRecorder.onerror = () => post('error', { error: 'Recording failed.' });
    mediaRecorder.onstop = async () => {
      if (cancelled) { await cleanup(); return; }
      try {
        const blob = new Blob(chunks, { type: (mediaRecorder && mediaRecorder.mimeType) || mime || 'audio/webm' });
        const durationMs = Date.now() - startedAt;
        const durationSec = Math.max(1, Math.round(durationMs / 1000));
        const buffer = await blob.arrayBuffer();
        const bytes = new Uint8Array(buffer);
        let binary = '';
        const chunkSize = 0x8000;
        for (let i = 0; i < bytes.length; i += chunkSize) {
          binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunkSize));
        }
        const base64 = btoa(binary);
        post('recorded', { base64, mime: blob.type || 'audio/webm', durationSec, durationMs });
      } catch (e) {
        post('error', { error: 'Failed to encode recording.' });
      } finally {
        await cleanup();
      }
    };

    startedAt = Date.now();
    mediaRecorder.start(250);
    startLevels();
    maxTimer = setTimeout(() => { try { stop(); } catch (_) {} }, MAX_DURATION_MS);
    post('started', {});
  }

  async function stop() {
    if (!mediaRecorder || mediaRecorder.state === 'inactive') return;
    cancelled = false;
    try { mediaRecorder.stop(); } catch (e) { post('error', { error: 'Failed to stop recording.' }); }
  }

  async function cancel() {
    cancelled = true;
    await cleanup();
    post('cancelled', {});
  }

  window.voiceRecorder = { start, stop, cancel };
})();
