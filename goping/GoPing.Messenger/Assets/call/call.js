(() => {
  'use strict';

  const bridge = () => window.chrome && window.chrome.webview &&
    window.chrome.webview.hostObjects && window.chrome.webview.hostObjects.callBridge;

  function fire(name, arg) {
    const b = bridge();
    if (!b || typeof b[name] !== 'function') return;
    try {
      const result = b[name](arg);
      if (result && typeof result.catch === 'function') result.catch(() => {});
    } catch (_) {
      // Мост может быть ещё не готов во время навигации.
    }
  }

  let config = { iceServers: [{ urls: 'stun:stun.l.google.com:19302' }] };
  let polite = true;
  let pc = null;
  let localStream = null;
  let remoteStream = null;
  let remoteAudio = null;
  let makingOffer = false;
  let ignoreOffer = false;
  let isSettingRemoteAnswerPending = false;
  let offerSent = false;
  let expectingRemoteOffer = false;
  let disconnectTimer = null;
  let pendingCandidates = [];
  let muted = false;
  let deafened = false;
  let screenStream = null;
  let screenSender = null;
  let remoteVideoTrack = null;
  let externalScreenCanvas = null;
  let externalScreenCtx = null;
  let externalScreenStream = null;
  let externalFramePending = false;

  // Speaking detection
  let audioCtx = null;
  let localAnalyser = null;
  let remoteAnalyser = null;
  let localData = null;
  let remoteData = null;
  let speakingTimer = null;
  let localSpeaking = false;
  let remoteSpeaking = false;

  let statsTimer = null;
  let lastStats = { bytesReceived: 0, timestamp: 0, packetsLost: 0, packetsReceived: 0 };
  let jitterHistory = [];
  let lastStatusText = '';

  function status(text) {
    const t = String(text);
    if (t === lastStatusText && (t === 'Connected' || t === 'Reconnecting…')) return;
    lastStatusText = t;
    fire('status', t);
  }

  function emit(kind, data) {
    fire('signal', JSON.stringify({ kind, data }));
  }

  function emitSpeaking() {
    fire('speaking', JSON.stringify({ local: !!localSpeaking, remote: !!remoteSpeaking }));
  }

  function ensureRemoteAudio() {
    if (!remoteAudio) {
      remoteAudio = new Audio();
      remoteAudio.autoplay = true;
    }
    remoteAudio.muted = !!deafened;
    return remoteAudio;
  }

  async function ensureLocalMedia() {
    if (localStream) return localStream;
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
      throw new Error('getUserMedia unavailable');
    }
    localStream = await navigator.mediaDevices.getUserMedia({
      audio: {
        echoCancellation: true,
        noiseSuppression: true,
        autoGainControl: false,
        channelCount: { ideal: 1 },
        sampleRate: { ideal: 48000 }
      },
      video: false
    });
    attachLocalAnalyser();
    return localStream;
  }

  function ensureAudioContext() {
    if (audioCtx) return audioCtx;
    const Ctx = window.AudioContext || window.webkitAudioContext;
    if (!Ctx) return null;
    audioCtx = new Ctx();
    return audioCtx;
  }

  function attachLocalAnalyser() {
    const ctx = ensureAudioContext();
    if (!ctx || !localStream) return;
    try {
      const source = ctx.createMediaStreamSource(localStream);
      localAnalyser = ctx.createAnalyser();
      localAnalyser.fftSize = 512;
      localAnalyser.smoothingTimeConstant = 0.4;
      source.connect(localAnalyser);
      localData = new Uint8Array(localAnalyser.frequencyBinCount);
    } catch (_) {}
  }

  function attachRemoteAnalyser(stream) {
    const ctx = ensureAudioContext();
    if (!ctx || !stream) return;
    try {
      const source = ctx.createMediaStreamSource(stream);
      remoteAnalyser = ctx.createAnalyser();
      remoteAnalyser.fftSize = 512;
      remoteAnalyser.smoothingTimeConstant = 0.4;
      source.connect(remoteAnalyser);
      remoteData = new Uint8Array(remoteAnalyser.frequencyBinCount);
    } catch (_) {}
  }

  function rms(data) {
    if (!data || data.length === 0) return 0;
    let sum = 0;
    for (let i = 0; i < data.length; i++) {
      const v = (data[i] - 128) / 128;
      sum += v * v;
    }
    return Math.sqrt(sum / data.length);
  }

  function startSpeakingMonitor() {
    stopSpeakingMonitor();
    speakingTimer = setInterval(() => {
      try {
        if (audioCtx && audioCtx.state === 'suspended') {
          audioCtx.resume().catch(() => {});
        }

        let nextLocal = false;
        let nextRemote = false;

        if (localAnalyser && localData && !muted) {
          localAnalyser.getByteTimeDomainData(localData);
          nextLocal = rms(localData) > 0.035;
        }
        if (remoteAnalyser && remoteData) {
          remoteAnalyser.getByteTimeDomainData(remoteData);
          nextRemote = rms(remoteData) > 0.03;
        }

        if (nextLocal !== localSpeaking || nextRemote !== remoteSpeaking) {
          localSpeaking = nextLocal;
          remoteSpeaking = nextRemote;
          emitSpeaking();
        }
      } catch (_) {}
    }, 150);
  }

  function stopSpeakingMonitor() {
    if (speakingTimer !== null) {
      clearInterval(speakingTimer);
      speakingTimer = null;
    }
    if (localSpeaking || remoteSpeaking) {
      localSpeaking = false;
      remoteSpeaking = false;
      emitSpeaking();
    }
  }

  async function collectStats() {
    if (!pc || pc.signalingState === 'closed') return;
    try {
      const stats = await pc.getStats();
      let rttMs = null;
      let jitterMs = null;
      let packetsLost = null;
      let packetsReceived = null;
      let audioLevel = null;
      let bytesReceived = null;
      let bytesSent = null;
      let codec = null;
      let timestamp = Date.now();

      stats.forEach(report => {
        if (report.type === 'candidate-pair' && report.state === 'succeeded' && report.nominated !== false) {
          if (typeof report.currentRoundTripTime === 'number') rttMs = report.currentRoundTripTime * 1000;
        }
        if (report.type === 'inbound-rtp' && report.kind === 'audio' && !report.isRemote) {
          if (typeof report.jitter === 'number') jitterMs = report.jitter * 1000;
          if (typeof report.packetsLost === 'number') packetsLost = report.packetsLost;
          if (typeof report.packetsReceived === 'number') packetsReceived = report.packetsReceived;
          if (typeof report.audioLevel === 'number') audioLevel = report.audioLevel;
          if (typeof report.bytesReceived === 'number') bytesReceived = report.bytesReceived;
        }
        if (report.type === 'outbound-rtp' && report.kind === 'audio' && !report.isRemote) {
          if (typeof report.bytesSent === 'number') bytesSent = report.bytesSent;
        }
        if (report.type === 'codec' && report.mimeType && String(report.mimeType).toLowerCase().startsWith('audio/')) {
          codec = report.mimeType;
        }
      });

      // Fallback RTT from remote-inbound-rtp
      if (rttMs === null) {
        stats.forEach(report => {
          if (report.type === 'remote-inbound-rtp' && report.kind === 'audio') {
            if (typeof report.roundTripTime === 'number') rttMs = report.roundTripTime * 1000;
          }
        });
      }

      if (jitterMs !== null) {
        jitterHistory.push({ t: timestamp, jitter: Math.round(jitterMs * 10) / 10, rtt: rttMs !== null ? Math.round(rttMs) : null });
        if (jitterHistory.length > 60) jitterHistory.shift();
      }

      if (lastStatusText === 'Reconnecting…' && pc && (pc.connectionState === 'connected' || pc.iceConnectionState === 'connected' || pc.iceConnectionState === 'completed')) {
        markConnected();
      }

      const payload = {
        rttMs: rttMs !== null ? Math.round(rttMs) : null,
        jitterMs: jitterMs !== null ? Math.round(jitterMs * 10) / 10 : null,
        packetsLost,
        packetsReceived,
        audioLevel,
        bytesReceived,
        bytesSent,
        codec,
        history: jitterHistory.slice(-30)
      };
      fire('stats', JSON.stringify(payload));
    } catch (_) {}
  }

  function startStatsMonitor() {
    stopStatsMonitor();
    jitterHistory = [];
    lastStats = { bytesReceived: 0, timestamp: 0, packetsLost: 0, packetsReceived: 0 };
    statsTimer = setInterval(() => { collectStats(); }, 1000);
    collectStats();
  }

  function stopStatsMonitor() {
    if (statsTimer !== null) {
      clearInterval(statsTimer);
      statsTimer = null;
    }
    jitterHistory = [];
  }

  function markConnected() {
    clearReconnectTimer();
    status('Connected');
  }

  function createPc() {
    if (pc && pc.signalingState !== 'closed') return pc;

    pc = new RTCPeerConnection(config);

    pc.onicecandidate = (event) => {
      if (event.candidate) {
        emit('ice', { candidate: event.candidate.toJSON() });
      }
    };

    pc.ontrack = (event) => {
      const track = event.track;
      const stream = event.streams[0] || new MediaStream([track]);
      if (track.kind === 'video') {
        remoteVideoTrack = track;
        const wrap = document.getElementById('wrap');
        const video = document.getElementById('remoteVideo');
        if (video) {
          video.srcObject = stream;
          const p = video.play();
          if (p && typeof p.catch === 'function') p.catch(() => {});
        }
        if (wrap) wrap.style.display = 'flex';
        fire('status', 'Screen sharing');
        track.onended = () => {
          remoteVideoTrack = null;
          if (video) { try { video.pause(); video.srcObject = null; video.load(); } catch (_) {} }
          if (wrap) wrap.style.display = 'none';
          fire('status', 'Connected');
        };
        return;
      }
      remoteStream = stream;
      const audio = ensureRemoteAudio();
      audio.srcObject = remoteStream;
      const play = audio.play();
      if (play && typeof play.catch === 'function') play.catch(() => {});
      attachRemoteAnalyser(remoteStream);
    };

    pc.onnegotiationneeded = async () => {
      if (!pc || pc.signalingState === 'closed') return;
      // Принимающая сторона не должна слать первоначальный offer сама —
      // она ждёт offer от вызывающего и отвечает answer.
      if (expectingRemoteOffer && !offerSent) return;

      const kind = offerSent ? 'renegotiate' : 'offer';
      makingOffer = true;
      try {
        await pc.setLocalDescription();
        emit(kind, pc.localDescription.toJSON());
        if (kind === 'offer') offerSent = true;
      } catch (_) {
        status('Signal error');
      } finally {
        makingOffer = false;
      }
    };

    pc.onconnectionstatechange = () => {
      if (!pc) return;
      const state = pc.connectionState;
      if (state === 'connected') {
        markConnected();
      } else if (state === 'disconnected') {
        clearReconnectTimer();
        disconnectTimer = setTimeout(() => {
          if (!pc) return;
          if (pc.connectionState === 'connected') { markConnected(); return; }
          status('Reconnecting…');
          restartIce();
        }, 3500);
      } else if (state === 'failed') {
        clearReconnectTimer();
        status('Reconnecting…');
        try { pc.restartIce(); } catch (_) {}
      }
    };

    pc.oniceconnectionstatechange = () => {
      if (!pc) return;
      const s = pc.iceConnectionState;
      if (s === 'connected' || s === 'completed') {
        markConnected();
      }
    };

    return pc;
  }

  function clearReconnectTimer() {
    if (disconnectTimer !== null) {
      clearTimeout(disconnectTimer);
      disconnectTimer = null;
    }
  }

  function restartIce() {
    if (!pc || pc.signalingState === 'closed') return;
    try {
      pc.restartIce();
    } catch (_) {}
  }

  async function flushCandidates() {
    if (!pc || !pc.remoteDescription) return;
    const candidates = pendingCandidates;
    pendingCandidates = [];
    for (const candidate of candidates) {
      try { await pc.addIceCandidate(candidate); } catch (_) {}
    }
  }

  async function handleSignalObject(message) {
    if (!message || !message.kind) return;

    if (message.kind === 'ice') {
      const candidate = message.data && message.data.candidate;
      if (!candidate) return;
      if (pc && pc.remoteDescription) {
        try { await pc.addIceCandidate(candidate); } catch (_) {}
      } else {
        pendingCandidates.push(candidate);
      }
      return;
    }

    if (message.kind === 'screen-stop') {
      remoteVideoTrack = null;
      const wrap = document.getElementById('wrap');
      const video = document.getElementById('remoteVideo');
      if (video) { try { video.pause(); video.srcObject = null; video.load(); } catch (_) {} }
      if (wrap) wrap.style.display = 'none';
      fire('status', 'Connected');
      return;
    }

    const connection = createPc();
    const description = message.data;
    if (!description || !description.type) return;

    ignoreOffer = false;
    const readyForOffer = !makingOffer &&
      (connection.signalingState === 'stable' || isSettingRemoteAnswerPending);
    const offerCollision = description.type === 'offer' && !readyForOffer;

    if (!polite && offerCollision) {
      ignoreOffer = true;
      return;
    }
    if (polite && offerCollision) {
      try {
        await connection.setLocalDescription({ type: 'rollback' });
      } catch (_) {}
    }

    isSettingRemoteAnswerPending = description.type === 'answer';
    try {
      await connection.setRemoteDescription(description);
    } catch (_) {
      status('Signal error');
      return;
    } finally {
      isSettingRemoteAnswerPending = false;
    }

    if (description.type === 'offer') {
      expectingRemoteOffer = false;
      offerSent = true;
    }

    await flushCandidates();

    if (description.type === 'offer') {
      try {
        await connection.setLocalDescription();
        const answerKind = message.kind === 'renegotiate' ? 'renegotiate-answer' : 'answer';
        emit(answerKind, connection.localDescription.toJSON());
      } catch (_) {
        status('Signal error');
      }
    }
  }

  function showSharePrompt(show) {
    const el = document.getElementById('sharePrompt');
    if (el) el.style.display = show ? 'flex' : 'none';
  }

  document.addEventListener('DOMContentLoaded', () => {
    const startBtn = document.getElementById('shareStartBtn');
    const cancelBtn = document.getElementById('shareCancelBtn');
    if (startBtn) startBtn.addEventListener('click', async () => {
      showSharePrompt(false);
      try { await window.gopingCall.startScreenShare(); }
      catch (e) { fire('screenState', JSON.stringify({ sharing: false, error: e && e.message ? e.message : String(e) })); }
    });
    if (cancelBtn) cancelBtn.addEventListener('click', () => {
      showSharePrompt(false);
      fire('screenState', JSON.stringify({ sharing: false, error: 'Screen share canceled' }));
    });
  });

  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener('message', (ev) => {
      try {
        let msg = ev.data;
        if (typeof msg === 'string') { try { msg = JSON.parse(msg); } catch (_) { return; } }
        if (!msg) return;
        if (msg.type === 'screenFrame' && msg.dataUrl) {
          window.gopingCall.pushScreenFrame(msg.dataUrl);
        }
      } catch (_) {}
    });
  }

  window.gopingCall = {
    prepare(politeFlag, iceConfigJson) {
      polite = !!politeFlag;
      if (iceConfigJson) {
        try { config = JSON.parse(iceConfigJson); } catch (_) {}
      }
      ensureRemoteAudio();
    },

    async startOutgoing() {
      expectingRemoteOffer = false;
      offerSent = false;
      await ensureLocalMedia();
      const ctx = ensureAudioContext();
      if (ctx && ctx.state === 'suspended') { try { await ctx.resume(); } catch (_) {} }
      const connection = createPc();
      if (localStream) {
        localStream.getAudioTracks().forEach(t => connection.addTrack(t, localStream));
      }
      startSpeakingMonitor();
      startStatsMonitor();
      status('Calling…');
    },

    async startIncoming() {
      expectingRemoteOffer = true;
      offerSent = false;
      await ensureLocalMedia();
      const ctx = ensureAudioContext();
      if (ctx && ctx.state === 'suspended') { try { await ctx.resume(); } catch (_) {} }
      const connection = createPc();
      if (localStream) {
        localStream.getAudioTracks().forEach(t => connection.addTrack(t, localStream));
      }
      startSpeakingMonitor();
      startStatsMonitor();
      status('Connecting…');
    },

    handleSignal(json) {
      let message = json;
      if (typeof json === 'string') {
        try { message = JSON.parse(json); } catch (_) { return; }
      }
      handleSignalObject(message).catch(() => status('Signal error'));
    },

    reconnect() {
      status('Reconnecting…');
      restartIce();
    },

    setMuted(nextMuted) {
      muted = !!nextMuted;
      if (localStream) {
        localStream.getAudioTracks().forEach(track => { track.enabled = !muted; });
      }
      if (muted && localSpeaking) {
        localSpeaking = false;
        emitSpeaking();
      }
    },

    setDeafened(nextDeafened) {
      deafened = !!nextDeafened;
      const audio = ensureRemoteAudio();
      audio.muted = deafened;
    },

    async startScreenShare() {
      try {
        if (!pc || pc.signalingState === 'closed') throw new Error('No active call');
        if (screenStream) { fire('screenState', JSON.stringify({ sharing: true })); return true; }
        if (!navigator.mediaDevices || !navigator.mediaDevices.getDisplayMedia) {
          throw new Error('Screen sharing is not supported in this WebView2 runtime');
        }
        screenStream = await navigator.mediaDevices.getDisplayMedia({ video: true, audio: false });
        const track = screenStream.getVideoTracks()[0];
        if (!track) throw new Error('No screen track');
        screenSender = pc.addTrack(track, screenStream);
        track.onended = () => { try { window.gopingCall.stopScreenShare(); } catch (_) {} };
        fire('screenState', JSON.stringify({ sharing: true }));
        if (remoteVideoTrack) status('Screen sharing');
        else status('Sharing your screen');
        return true;
      } catch (e) {
        const msg = e && e.message ? e.message : String(e);
        fire('screenState', JSON.stringify({ sharing: false, error: msg }));
        status('Screen share failed: ' + msg);
        throw e;
      }
    },

    async stopScreenShare() {
      try {
        showSharePrompt(false);
        if (screenSender && pc) {
          try { pc.removeTrack(screenSender); } catch (_) {}
        }
        screenSender = null;
        if (screenStream) {
          screenStream.getTracks().forEach(t => t.stop());
          screenStream = null;
        }
        if (externalScreenStream) {
          externalScreenStream.getTracks().forEach(t => t.stop());
          externalScreenStream = null;
        }
        externalScreenCanvas = null;
        externalScreenCtx = null;
        externalFramePending = false;
        emit('screen-stop', {});
        fire('screenState', JSON.stringify({ sharing: false }));
        if (!remoteVideoTrack) status('Connected');
      } catch (e) {
        fire('screenState', JSON.stringify({ sharing: false, error: e && e.message ? e.message : String(e) }));
      }
      return true;
    },

    requestScreenShare() {
      showSharePrompt(true);
      return true;
    },

    startScreenShareExternal(width, height) {
      try {
        if (!pc || pc.signalingState === 'closed') throw new Error('No active call');
        if (screenSender || externalScreenStream) { fire('screenState', JSON.stringify({ sharing: true })); return true; }
        const w = Math.max(1, width|0);
        const h = Math.max(1, height|0);
        externalScreenCanvas = document.createElement('canvas');
        externalScreenCanvas.width = w;
        externalScreenCanvas.height = h;
        externalScreenCtx = externalScreenCanvas.getContext('2d');
        externalScreenStream = externalScreenCanvas.captureStream(10);
        const track = externalScreenStream.getVideoTracks()[0];
        if (!track) throw new Error('No canvas track');
        screenSender = pc.addTrack(track, externalScreenStream);

        // Local preview
        const wrap = document.getElementById('wrap');
        const video = document.getElementById('remoteVideo');
        if (video) {
          video.srcObject = externalScreenStream;
          const p = video.play();
          if (p && typeof p.catch === 'function') p.catch(() => {});
        }
        if (wrap) wrap.style.display = 'flex';

        fire('screenState', JSON.stringify({ sharing: true }));
        if (remoteVideoTrack) status('Screen sharing');
        else status('Sharing your screen');
        return true;
      } catch (e) {
        const msg = e && e.message ? e.message : String(e);
        fire('screenState', JSON.stringify({ sharing: false, error: msg }));
        status('Screen share failed: ' + msg);
        throw e;
      }
    },

    pushScreenFrame(dataUrl) {
      try {
        if (!externalScreenCanvas || !externalScreenCtx || externalFramePending) return false;
        externalFramePending = true;
        const img = new Image();
        img.onload = () => {
          try {
            if (externalScreenCanvas.width !== img.width || externalScreenCanvas.height !== img.height) {
              externalScreenCanvas.width = img.width;
              externalScreenCanvas.height = img.height;
            }
            externalScreenCtx.drawImage(img, 0, 0);
          } catch (_) {}
          externalFramePending = false;
        };
        img.onerror = () => { externalFramePending = false; };
        img.src = dataUrl;
        return true;
      } catch (_) { externalFramePending = false; return false; }
    },

    stop() {
      clearReconnectTimer();
      stopSpeakingMonitor();
      stopStatsMonitor();
      showSharePrompt(false);
      try { if (pc) pc.close(); } catch (_) {}
      pc = null;
      if (screenSender) { try { /* pc already closed */ } catch (_) {} }
      screenSender = null;
      if (screenStream) { screenStream.getTracks().forEach(t => t.stop()); }
      screenStream = null;
      if (externalScreenStream) { externalScreenStream.getTracks().forEach(t => t.stop()); }
      externalScreenStream = null;
      externalScreenCanvas = null;
      externalScreenCtx = null;
      externalFramePending = false;
      remoteVideoTrack = null;
      const wrap = document.getElementById('wrap');
      const rv = document.getElementById('remoteVideo');
      if (rv) try { rv.srcObject = null; } catch (_) {}
      if (wrap) wrap.style.display = 'none';
      if (localStream) {
        localStream.getTracks().forEach(track => track.stop());
      }
      localStream = null;
      remoteStream = null;
      localAnalyser = null;
      remoteAnalyser = null;
      localData = null;
      remoteData = null;
      if (remoteAudio) {
        try { remoteAudio.srcObject = null; } catch (_) {}
      }
      pendingCandidates = [];
      offerSent = false;
      expectingRemoteOffer = false;
      muted = false;
      deafened = false;
      status('Ended');
    }
  };
})();
