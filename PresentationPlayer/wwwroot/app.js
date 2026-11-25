// Presentation Player — app UI (desktop window and web remote).
(() => {
  'use strict';

  const $ = (sel, root = document) => root.querySelector(sel);
  const app = $('#app');

  // The desktop window passes a secret in the URL fragment; it unlocks settings and restart.
  const token = (() => {
    const m = location.hash.match(/token=([A-F0-9]+)/i);
    if (m) {
      sessionStorage.setItem('appToken', m[1]);
      history.replaceState(null, '', location.pathname);
    }
    return sessionStorage.getItem('appToken');
  })();
  const isDesktop = !!token;

  let state = null;
  let info = null;

  // ------------------------------------------------------------------ API

  async function api(path, { method = 'POST', body, privileged = false } = {}) {
    const headers = {};
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    if (privileged && token) headers['X-App-Token'] = token;
    const res = await fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
    let data = null;
    try { data = await res.json(); } catch { /* empty body */ }
    if (!res.ok || (data && data.error)) {
      const err = new Error((data && data.error) || `Request failed (${res.status})`);
      err.data = data || {};
      throw err;
    }
    if (data && 'slideCount' in data) render(data);
    return data;
  }
  const act = path => api(path).catch(err => toast(err.message, 'error'));

  // ------------------------------------------------------------------ Live state (Server-Sent Events)

  let connTimer = null;
  function connect() {
    const es = new EventSource('/api/events');
    es.onmessage = e => {
      clearTimeout(connTimer);
      $('#connBanner').hidden = true;
      render(JSON.parse(e.data));
    };
    es.onerror = () => {
      clearTimeout(connTimer);
      connTimer = setTimeout(() => { $('#connBanner').hidden = false; }, 1500);
    };
  }

  async function loadInfo() {
    try {
      info = await (await fetch('/api/info')).json();
    } catch { info = null; }
    renderRemote();
  }

  // ------------------------------------------------------------------ Helpers

  const QUALITY = { hd2160: '4K', hd1440: '1440p', hd1080: '1080p', hd720: '720p', large: '480p', medium: '360p', small: '240p', tiny: '144p' };
  const qualityLabel = q => QUALITY[q] || '';

  function initials(title) {
    const words = (title || '?').trim().split(/\s+/).filter(Boolean);
    return ((words[0] || '?')[0] + (words[1] ? words[1][0] : '')).toUpperCase();
  }
  function timeAgo(iso) {
    const s = (Date.now() - new Date(iso).getTime()) / 1000;
    if (s < 60) return 'just now';
    if (s < 3600) return `${Math.floor(s / 60)} min ago`;
    if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
    const d = Math.floor(s / 86400);
    return d === 1 ? 'yesterday' : d < 7 ? `${d} days ago` : new Date(iso).toLocaleDateString();
  }
  const esc = s => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const plural = (n, word) => `${n} ${word}${n === 1 ? '' : 's'}`;
  const thumbUrl = (n, w) => `/api/slides/${n}.jpg?w=${w}&id=${state ? state.presentationId : ''}&v=${state ? state.version : 0}${state && (state.oldPictures || []).includes(n) ? '&old=1' : ''}`;

  // The path of a file on the computer running the app. "Copy as path" puts quotes around it.
  function parsePath(text) {
    const path = text.replace(/^"(.*)"$/, '$1').trim();
    if (!/^([a-z]:[\\/]|\\\\|file:)/i.test(path)) return null;
    if (!/\.(pptx|ppsx|pptm|ppsm|pdf)$/i.test(path)) return { error: 'Only PowerPoint (.pptx) and PDF files can be opened.' };
    const known = state && [...(state.recent || []), { id: state.presentationId, url: state.url || '' }].find(r => r.url.toLowerCase() === path.toLowerCase());
    return { id: (known && known.id) || `file:${path.toLowerCase()}`, url: path, file: true };
  }
  const isFile = url => !!url && !!parsePath(url);

  function parseSlidesUrl(raw) {
    const s = (raw || '').trim();
    if (!s) return { empty: true };
    const file = parsePath(s);
    if (file) return file;
    if (/\/presentation\/d\/e\//.test(s))
      return { error: '“Publish to web” links can’t be used — copy the link from the Share button instead.' };
    const m = s.match(/\/presentation\/d\/([a-zA-Z0-9_-]{20,})/);
    if (m) return { id: m[1], url: s };
    if (/^[a-zA-Z0-9_-]{25,}$/.test(s)) return { id: s, url: s };
    if (/docs\.google\.com\/(document|spreadsheets|forms)/.test(s))
      return { error: 'That’s a Google Docs/Sheets link — paste a Google Slides link.' };
    return { error: 'That doesn’t look like a Google Slides link, or the path of a PowerPoint or PDF file.' };
  }

  // ------------------------------------------------------------------ Toasts

  function toast(message, kind = 'info', action) {
    const el = document.createElement('div');
    el.className = `toast is-${kind}`;
    const icon = kind === 'error' ? 'i-alert' : kind === 'ok' ? 'i-check' : 'i-alert';
    el.innerHTML = `<svg class="ic"><use href="#${icon}"/></svg><span>${esc(message)}</span>`;
    if (action) {
      const b = document.createElement('button');
      b.className = 'btn';
      b.textContent = action.label;
      b.onclick = () => { action.run(); dismiss(); };
      el.append(b);
    }
    $('#toasts').append(el);
    const dismiss = () => { el.classList.add('is-leaving'); setTimeout(() => el.remove(), 200); };
    setTimeout(dismiss, kind === 'error' && action ? 12000 : kind === 'error' || action ? 8000 : 3500); // give time to use an action (e.g. Reopen)
  }

  // ------------------------------------------------------------------ Rendering

  let lastVersion = -1, lastThumbCount = -1, lastSlide = -1, lastRecentKey = '';

  function render(s) {
    const prev = state;
    state = s;

    // Loads started elsewhere (Companion, a phone, the HTTP API, or reopening the last deck at
    // startup) get the same overlay as ones started from this window.
    if (s.loading && !switching) setSwitching({ id: s.loadingId, title: s.loadingTitle, refresh: s.loadingRefresh, remote: true, file: /^file-/.test(s.loadingId || '') });
    else if (!s.loading && switching && switching.remote) setSwitching(null);

    const hasDeck = !!s.presentationId || s.slideCount > 0;
    app.dataset.view = hasDeck ? 'stage' : 'empty';

    // Load buttons
    for (const id of ['#emptySubmit', '#openSubmit']) {
      const b = $(id);
      b.disabled = s.loading;
    }
    renderOutOfDate(s);

    // Header
    const title = s.title || (s.loading ? 'Loading…' : 'Untitled presentation');
    $('#deckTitle').textContent = title;
    renderMediaBadge();
    document.title = hasDeck ? `${title} · Presentation Player` : 'Presentation Player';
    const updated = (s.status.match(/at (\d\d:\d\d)/) || [])[1];
    const skipped = (s.skipped || []).length;
    $('#deckMeta').textContent = s.slideCount
      ? `${s.isMaster ? `${plural(s.parts.length, 'presentation')} · ` : ''}${plural(s.slideCount, 'slide')}${skipped ? ` · ${skipped} skipped` : ''}${updated ? ` · Updated ${updated}` : ''}`
      : s.loading ? 'Downloading slides and notes…' : '';
    $('#openInSlides').hidden = !s.deckId || s.deckId.startsWith('file-');
    if (s.deckId) $('#openInSlides').href = `https://docs.google.com/presentation/d/${s.deckId}/edit${s.isMaster && s.pageId ? `#slide=id.${s.pageId}` : ''}`;
    const warn = $('#liveWarn');
    warn.hidden = !s.liveError;
    warn.title = s.liveError ? `${s.liveError} Animations and video won't play.` : '';

    renderGoogleExpired(s);
    $('#ndiWarn').hidden = !s.ndiError;
    $('#ndiWarn').title = s.ndiError || '';

    // Recents
    const recentKey = JSON.stringify([s.recent, s.masterDecks, s.presentationId, s.version, s.thumbnailCount > 0, s.outOfDate, s.loading, (s.missingPictures || []).includes(1), (s.oldPictures || []).includes(1)]);
    if (recentKey !== lastRecentKey) { lastRecentKey = recentKey; renderRecents(); }

    // Filmstrip & progress
    if (s.version !== lastVersion || s.thumbnailCount !== lastThumbCount) {
      lastVersion = s.version; lastThumbCount = s.thumbnailCount;
      buildFilmstrip();
    }
    fillPictures();
    if (s.slide !== lastSlide || !prev || prev.version !== s.version) {
      lastSlide = s.slide;
      updateCurrentSlide();
    }

    // Slide toolbar (in live mode Next/Previous also step through animation builds, so they stay enabled)
    $('#posNow').textContent = s.slideCount ? s.slide : '–';
    $('#posTotal').textContent = s.slideCount || '–';
    $('#prevBtn').disabled = !s.slideCount || (!s.live && s.slide <= shownSlide(1, 1));
    $('#nextBtn').disabled = !s.slideCount || (!s.live && s.slide >= shownSlide(s.slideCount, -1));
    renderVideo(s.video);

    renderOutputs(s);

    // Notes toolbar
    const size = Math.round(s.notesAutoFit ? s.notesEffectiveFontSize : s.notesFontSize);
    $('#sizeVal').textContent = size;
    $('#sizeDown').disabled = size <= 12;
    $('#sizeUp').disabled = size >= 300;
    $('#autoBtn').setAttribute('aria-checked', String(s.notesAutoFit));
    $('#scrollUpBtn').disabled = s.notesScroll <= 0;
    $('#scrollDownBtn').disabled = s.notesScroll >= s.notesMaxScroll - 0.5;
  }

  // ------------------------------------------------------------------ Outputs on this computer's displays

  const outputKeys = {};
  function renderOutputs(s) {
    if (!isDesktop) return;
    for (const select of document.querySelectorAll('[data-output]')) {
      const out = select.dataset.output;
      const at = out === 'slides' ? s.slidesOutput : s.notesOutput;
      const other = out === 'slides' ? s.notesOutput : s.slidesOutput;
      if (!at) continue;
      const key = JSON.stringify([at, other, s.displays]);
      if (key === outputKeys[out]) continue;
      outputKeys[out] = key;
      const options = [['off', 'No display'], ['window', 'Window']];
      for (const d of s.displays) {
        const about = [`${d.width} × ${d.height}`];
        if (d.app) about.push('this display');
        if (other.mode === 'display' && other.display === d.id) about.push(out === 'slides' ? 'showing the presenter view' : 'showing the slides');
        options.push([`display:${d.id}`, `Full screen on ${d.name}`, ` (${about.join(', ')})`]);
      }
      if (!at.connected) options.push([`display:${at.display}`, `${at.displayName || 'Display'} isn’t connected`]);
      const value = at.mode === 'display' ? `display:${at.display}` : at.mode;
      // The size and so on are for choosing; once chosen, the name is enough.
      select.replaceChildren(...options.map(([v, label, about]) => new Option(v === value ? label : label + (about || ''), v, false, v === value)));
      const box = select.closest('.out-pick');
      box.hidden = false;
      box.classList.toggle('is-on', at.mode !== 'off');
      box.classList.toggle('is-missing', !at.connected);
      select.title = at.mode === 'off' ? 'Show this on a display or in a window' : select.selectedOptions[0].text;
    }
  }
  for (const select of document.querySelectorAll('[data-output]')) {
    select.addEventListener('change', async () => {
      const [mode, ...id] = select.value.split(':');
      select.blur(); // so the keyboard shortcuts work again
      try {
        await api(`/api/outputs/${select.dataset.output}`, { method: 'PUT', privileged: true, body: { mode, display: id.join(':') } });
      } catch (err) {
        delete outputKeys[select.dataset.output];
        if (state) renderOutputs(state);
        toast(err.message, 'error');
      }
    });
  }

  // Shown once each time the open deck goes out of date, so it doesn't nag after every edit.
  let outOfDateToasted = '';

  function renderOutOfDate(s) {
    const stale = s.outOfDate && !s.loading;
    const key = stale ? `${s.presentationId}@${s.loadedRevision}` : '';
    if (key && key !== outOfDateToasted) {
      toast('This presentation has been updated.', 'info', { label: 'Refresh', run: refresh });
    }
    if (!s.loading) outOfDateToasted = key;
  }

  const clock = s => { s = Math.max(0, Math.floor(s || 0)); return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`; };

  // The play/rewind controls on the slide monitor work for both video and audio clips.
  function renderVideo(video) {
    const btn = $('#videoBtn');
    $('#videoControls').hidden = !video;
    if (!video) return;
    const noun = video.kind === 'audio' ? 'audio' : 'video';
    const playing = video.state === 'playing' || video.state === 'buffering';
    btn.classList.toggle('is-playing', playing);
    $('#videoIcon').setAttribute('href', playing ? '#i-pause' : '#i-play');
    $('#videoBtnText').textContent = `${playing ? 'Pause' : video.state === 'ended' ? 'Replay' : 'Play'} ${noun}`;
    const parts = [];
    if (video.state === 'buffering') parts.push('Buffering…');
    else if (video.duration > 0) parts.push(`${clock(video.position)} / ${clock(video.duration)}`);
    const q = qualityLabel(video.quality) || qualityLabel(video.best);
    if (noun === 'video' && q && video.state !== 'buffering') parts.push(q);
    $('#videoMeta').textContent = parts.join(' · ');
    btn.title = `${playing ? 'Pause' : 'Play'} the ${noun} on this slide (P)` +
      (noun === 'video' && video.best ? ` — best quality available: ${qualityLabel(video.best) || video.best}` : '');
  }

  // ------------------------------------------------------------------ Audio and video list

  const START_LABELS = { automatic: 'Automatically', 'on click': 'On click', manual: 'Manually' };
  // Whether each clip will actually play here (checked when the deck loads).
  function accessPill(m) {
    switch (m.access) {
      case 'ok': return '<span class="badge badge-plain">Can play</span>';
      case 'no access': {
        const why = `The ${m.kind} file in Google Drive isn’t shared with the Google account signed in here (or publicly), so it won’t play.`;
        return isDesktop && m.fileId
          ? `<button type="button" class="badge badge-bad badge-action" data-request-file="${esc(m.fileId)}" title="${why} Click to ask the owner for access.">No access<svg class="ic"><use href="#i-external"/></svg></button>`
          : `<span class="badge badge-bad" title="${why} Ask the owner to share it.">No access</span>`;
      }
      case 'unsupported': return '<span class="badge badge-bad" title="The clip is in a format that can’t be played here. Convert it to MP4 (H.264) or MP3.">Can’t play</span>';
      case 'missing': return '<span class="badge badge-bad" title="The clip isn’t in the file or next to it.">Missing</span>';
      case 'private': return '<span class="badge badge-bad" title="This YouTube video is private or has been removed.">Unavailable</span>';
      case 'not embeddable': return '<span class="badge badge-bad" title="The owner of this YouTube video doesn’t allow it to be played in other sites.">Can’t embed</span>';
      default: return '<span class="badge badge-plain" title="Couldn’t check this one.">Unknown</span>';
    }
  }
  const mediaProblems = () => (state?.media || []).filter(m => m.access && m.access !== 'ok').length;
  function renderMediaBadge() {
    const n = mediaProblems();
    const btn = $('#mediaInfoBtn');
    btn.classList.toggle('has-problem', n > 0);
    btn.title = n > 0
      ? `Audio and video in this presentation — ${n === 1 ? 'one clip' : n + ' clips'} can’t play`
      : 'Audio and video in this presentation';
  }
  function showMediaList() {
    if (anyDialogOpen() || !state) return;
    const media = state.media || [];
    const list = $('#mediaList');
    if (!media.length) {
      list.innerHTML = '<p class="media-empty">There’s no audio or video in this presentation.</p>';
    } else {
      list.innerHTML = `<table class="table">
        <thead><tr><th>Slide</th><th>Media</th><th>Starts</th><th>Sound</th><th>Access</th></tr></thead>
        <tbody>${media.map(m => `
          <tr class="${m.slide === state.slide ? 'is-current' : ''}">
            <td class="media-slide">${m.slide}${(state.skipped || []).includes(m.slide) ? ' <span class="badge">Skipped</span>' : ''}</td>
            <td><div class="media-what"><svg class="ic"><use href="#i-${m.kind === 'audio' ? 'audio' : 'video'}"/></svg>
              <span>${m.kind === 'audio' ? 'Audio' : m.source === 'youtube' ? 'YouTube video' : 'Video'}<small title="${esc(m.name || '')}">${esc(m.name || '')}</small></span></div></td>
            <td><span class="badge ${m.start === 'automatic' ? 'badge-auto' : ''}">${START_LABELS[m.start] || esc(m.start)}</span></td>
            <td>${m.muted ? '<span class="badge badge-muted">Muted</span>' : '<span class="badge badge-plain">On</span>'}</td>
            <td>${accessPill(m)}</td>
          </tr>`).join('')}</tbody></table>`;
    }
    $('#mediaDialog').showModal();
  }
  $('#mediaInfoBtn').addEventListener('click', showMediaList);
  $('#mediaList').addEventListener('click', e => {
    const b = e.target.closest('[data-request-file]');
    if (b) requestAccess('file', b.dataset.requestFile);
  });

  const firstPicture = () => state.thumbnailCount > 0 && !(state.missingPictures || []).includes(1);

  function recentItem(r, { removable }) {
    const li = document.createElement('li');
    const current = state && r.id === state.presentationId && !switching;
    const opening = switching && switching.id === r.id;
    li.className = 'recent' + (current ? ' is-current' : '') + (opening ? ' is-opening' : '');
    li.tabIndex = 0;
    li.title = r.url;
    li.innerHTML = `
      <div class="recent-art">${current && firstPicture() ? `<img alt="" src="${thumbUrl(1, 160)}">` : esc(initials(r.title))}</div>
      <div class="recent-text"><strong>${esc(r.title)}</strong><span>${opening ? (switching.refresh ? 'Refreshing…' : 'Opening…') : `${plural(r.slideCount, 'slide')} · ${timeAgo(r.lastOpened)}`}</span></div>
      ${opening ? '<span class="spinner spinner-sm" aria-label="Loading"></span>'
        : removable ? `<button class="recent-remove" type="button" aria-label="Remove from recent" ${current ? 'disabled title="Close it before removing it from this list"' : 'title="Remove from recent"'}><svg class="ic"><use href="#i-x"/></svg></button>` : ''}`;
    if (current) li.append(openDeckActions());
    const open = () => {
      if (switching) return;
      if (!current) openPresentation(r.url, r.title);
      closeDialog('#openDialog'); closeMenu();
    };
    li.addEventListener('click', e => {
      if (e.target.closest('.recent-actions')) return;
      if (e.target.closest('.recent-remove')) {
        e.stopPropagation();
        if (current) return;
        fetch(`/api/recent/${encodeURIComponent(r.id)}`, { method: 'DELETE' }).then(res => res.json()).then(render).catch(() => {});
        return;
      }
      open();
    });
    li.addEventListener('keydown', e => { if (e.key === 'Enter' && e.target === li) open(); });
    return li;
  }

  // Refresh and Close, under the open presentation in the list.
  function openDeckActions() {
    const el = document.createElement('div');
    el.className = 'recent-actions';
    const stale = state.outOfDate && !state.loading;
    el.innerHTML = `
      <button class="btn${stale ? ' has-update' : ''}" type="button" data-act="refresh"
        title="${stale ? 'It has been updated. Refresh to load the changes (R)' : 'Load it again, with any changes (R)'}"><svg class="ic"><use href="#i-refresh"/></svg>Refresh<span class="update-dot" aria-hidden="true"></span></button>
      <button class="btn" type="button" data-act="close" title="Close; the outputs show the holding screen (Ctrl+W)">Close</button>`;
    $('[data-act="refresh"]', el).onclick = () => { closeMenu(); refresh(); };
    $('[data-act="close"]', el).onclick = () => { closeMenu(); closePresentation(); };
    for (const b of el.querySelectorAll('button')) b.disabled = state.loading;
    return el;
  }

  function renderRecents() {
    const recent = state.recent || [];
    const side = $('#recentList');
    // The open presentation is always listed, as it has the Refresh and Close buttons.
    const listed = state.presentationId && !state.isMaster && !recent.some(r => r.id === state.presentationId)
      ? [{ id: state.presentationId, url: state.url, title: state.title, slideCount: state.slideCount, lastOpened: new Date().toISOString() }, ...recent] : recent;
    side.replaceChildren(...listed.map(r => recentItem(r, { removable: true })));
    $('#recentEmpty').hidden = listed.length > 0;

    const grid = $('#emptyRecent');
    grid.replaceChildren(...recent.slice(0, 6).map(r => {
      const b = document.createElement('button');
      b.className = 'join-row';
      b.type = 'button';
      b.title = r.url;
      b.innerHTML = `<svg class="ic"><use href="#i-slides"/></svg>
        <strong>${esc(r.title)}</strong><span>${plural(r.slideCount, 'slide')} · ${timeAgo(r.lastOpened)}</span>`;
      b.onclick = () => openPresentation(r.url, r.title);
      return b;
    }));
    $('#emptyRecentWrap').hidden = recent.length === 0;
    renderMasters();
  }

  // ------------------------------------------------------------------ Master decks

  function renderMasters() {
    const masters = state.masterDecks || [];
    $('#masterHead').hidden = !isDesktop && !masters.length;
    $('#masterList').replaceChildren(...masters.map(m => {
      const url = `master:${m.id}`;
      const current = state.presentationId === url && !switching;
      const opening = switching && switching.id === url;
      const li = document.createElement('li');
      li.className = 'recent' + (current ? ' is-current' : '') + (opening ? ' is-opening' : '');
      li.tabIndex = 0;
      li.innerHTML = `
        <div class="recent-art">${current && firstPicture() ? `<img alt="" src="${thumbUrl(1, 160)}">` : '<svg class="ic"><use href="#i-stack"/></svg>'}</div>
        <div class="recent-text"><strong>${esc(m.name)}</strong><span>${opening ? (switching.refresh ? 'Refreshing…' : 'Opening…') : plural(m.parts.length, 'presentation')}</span></div>
        ${opening ? '<span class="spinner spinner-sm" aria-label="Loading"></span>'
          : isDesktop ? '<button class="recent-remove" type="button" aria-label="Edit" title="Edit"><svg class="ic"><use href="#i-edit"/></svg></button>' : ''}`;
      const open = () => {
        if (switching) return;
        if (!current) openPresentation(url, m.name);
        closeMenu();
      };
      li.addEventListener('click', e => {
        if (e.target.closest('.recent-actions')) return;
        if (e.target.closest('.recent-remove')) { closeMenu(); editMaster(m); } else open();
      });
      if (current) li.append(openDeckActions());
      li.addEventListener('keydown', e => { if (e.key === 'Enter' && e.target === li) open(); });
      return li;
    }));
    $('#emptyMasters').replaceChildren(...masters.map(m => {
      const b = document.createElement('button');
      b.className = 'join-row';
      b.type = 'button';
      b.innerHTML = `<svg class="ic"><use href="#i-stack"/></svg><strong>${esc(m.name)}</strong><span>${plural(m.parts.length, 'presentation')}</span>`;
      b.onclick = () => openPresentation(`master:${m.id}`, m.name);
      return b;
    }));
    $('#emptyMasters').hidden = !masters.length;
    $('#emptyMasterWrap').hidden = !isDesktop && !masters.length;
  }

  let editing = null; // the master deck in the editor; its parts also carry what's been looked up

  function editMaster(m) {
    if (anyDialogOpen()) return;
    editing = m
      ? { id: m.id, name: m.name, parts: m.parts.map(p => ({ ...p })) }
      : { id: '', name: '', parts: [] };
    $('#masterTitle').textContent = m ? 'Edit master deck' : 'New master deck';
    $('#masterName').value = editing.name;
    $('#masterLinks').value = '';
    $('#masterError').textContent = '';
    $('#masterDelete').hidden = !m;
    $('#masterDelete').textContent = 'Delete';
    renderParts();
    editing.parts.forEach(lookUp);
    $('#masterDialog').showModal();
    (m ? $('#masterLinks') : $('#masterName')).focus();
  }

  // The deck's current title and slide count, to show which slides the numbers leave. One at a time:
  // the live pictures use most of the browser's connections, and the rest are for Next and Previous.
  let lookUps = Promise.resolve();
  const lookUp = part => { lookUps = lookUps.then(() => lookUpNow(part)); };
  async function lookUpNow(part) {
    if (!editing || !editing.parts.includes(part)) return;
    part.error = null;
    const same = editing.parts.find(p => p !== part && p.numbers && parseSlidesUrl(p.url).id === parseSlidesUrl(part.url).id);
    if (same) {
      part.title = same.title;
      part.count = same.count;
      part.numbers = same.numbers;
      renderParts();
      return;
    }
    try {
      const d = await api('/api/deck-summary', { body: { url: part.url }, privileged: true });
      part.title = d.title;
      part.count = d.slides;
      part.numbers = Array.from({ length: d.slides }, (_, i) => i + 1).filter(n => !d.skipped.includes(n));
    } catch (err) {
      part.error = err.message;
      part.noAccess = err.data && err.data.code === 'no-access' ? err.data.presentationId : null;
    }
    if (editing && editing.parts.includes(part)) renderParts();
  }

  // The slides of a deck that are shown, by their numbers in it. Skipped ones aren't, and don't use up a place.
  const shownOf = p => p.numbers ? p.numbers.slice(p.skipStart, Math.max(p.skipStart, p.numbers.length - p.skipEnd)) : null;
  const shown = p => p.numbers ? shownOf(p).length : null;
  const rangeOf = p => { const n = shownOf(p); return `<strong>${n[0]}–${n[n.length - 1]}</strong> of ${p.count}`; };

  function renderParts() {
    const box = $('#masterParts');
    // Looking up a deck redraws the list: keep the cursor in the number being typed.
    const at = box.contains(document.activeElement) && document.activeElement.dataset.skip
      ? [[...box.children].indexOf(document.activeElement.closest('.master-row')), document.activeElement.dataset.skip] : null;
    $('#masterTable').hidden = !editing.parts.length;
    $('#masterEmpty').hidden = editing.parts.length > 0;
    box.replaceChildren(...editing.parts.map((p, i) => {
      const row = document.createElement('div');
      row.className = 'master-row';
      const n = shown(p);
      const range = p.error ? (p.noAccess
          ? '<button type="button" class="badge badge-bad badge-action" data-request>No access<svg class="ic"><use href="#i-external"/></svg></button>'
          : '<span class="badge badge-bad">Can’t read</span>')
        : n == null ? '<span class="spinner spinner-sm"></span>'
        : n === 0 ? '<span class="badge badge-muted">No slides</span>'
        : rangeOf(p);
      row.innerHTML = `
        <span class="master-move">
          <button type="button" data-move="-1" title="Move up" aria-label="Move up" ${i === 0 ? 'disabled' : ''}><svg class="ic"><use href="#i-up"/></svg></button>
          <button type="button" data-move="1" title="Move down" aria-label="Move down" ${i === editing.parts.length - 1 ? 'disabled' : ''}><svg class="ic"><use href="#i-down"/></svg></button>
        </span>
        <span class="master-what" title="${esc(p.error || p.url)}"><strong>${esc(p.title || 'Presentation')}</strong><small>${esc(p.error || p.url)}</small></span>
        <input class="tf num" type="number" min="0" max="9999" value="${p.skipStart}" data-skip="skipStart" aria-label="Slides to skip from the start of ${esc(p.title || 'this presentation')}">
        <input class="tf num" type="number" min="0" max="9999" value="${p.skipEnd}" data-skip="skipEnd" aria-label="Slides to skip from the end of ${esc(p.title || 'this presentation')}">
        <span class="master-range">${range}</span>
        <button class="btn btn-plain btn-icon" type="button" data-remove title="Remove" aria-label="Remove"><svg class="ic"><use href="#i-x"/></svg></button>`;
      for (const b of row.querySelectorAll('[data-move]')) b.onclick = () => {
        const to = i + Number(b.dataset.move);
        editing.parts.splice(to, 0, editing.parts.splice(i, 1)[0]);
        renderParts();
        const next = box.children[to].querySelector(`[data-move="${b.dataset.move}"]`);
        if (!next.disabled) next.focus();
      };
      for (const input of row.querySelectorAll('[data-skip]')) input.oninput = () => {
        p[input.dataset.skip] = Math.max(0, Math.floor(Number(input.value) || 0));
        const left = shown(p);
        if (left != null && !p.error) $('.master-range', row).innerHTML = left === 0
          ? '<span class="badge badge-muted">No slides</span>' : rangeOf(p);
        renderTotal();
      };
      $('[data-remove]', row).onclick = () => { editing.parts.splice(i, 1); renderParts(); };
      const request = $('[data-request]', row);
      if (request) request.onclick = () => requestAccess('presentation', p.noAccess);
      return row;
    }));
    if (at && box.children[at[0]]) $(`[data-skip="${at[1]}"]`, box.children[at[0]]).focus();
    renderTotal();
  }

  function renderTotal() {
    const counts = editing.parts.map(shown);
    $('#masterTotal').textContent = counts.length && counts.every(n => n != null)
      ? `${plural(counts.reduce((a, b) => a + b, 0), 'slide')} from ${plural(counts.length, 'presentation')}` : '';
  }

  function addLinks() {
    const text = $('#masterLinks').value.trim();
    if (!text) return true;
    // Paths can have spaces and commas in them, so they're one to a line.
    const links = text.split(/[\r\n]+/).flatMap(line => parsePath(line.trim()) ? [line.trim()] : line.split(/[\s,]+/)).filter(Boolean).map(parseSlidesUrl);
    const good = links.filter(l => l.id);
    if (!good.length) {
      $('#masterError').textContent = (links[0] && links[0].error) || 'Paste a Google Slides link or the path of a file first.';
      return false;
    }
    $('#masterError').textContent = '';
    $('#masterLinks').value = '';
    addParts(good);
    return true;
  }
  function addParts(links) {
    for (const l of links) {
      const part = { url: l.url, title: fileTitle(l) || '', skipStart: 0, skipEnd: 0 };
      editing.parts.push(part);
      lookUp(part);
    }
    renderParts();
  }
  $('#masterBrowse').addEventListener('click', async () => {
    const files = await browse(true);
    if (editing && files.length) addParts(files.map(parseSlidesUrl).filter(l => l.id));
  });
  $('#masterAdd').addEventListener('click', addLinks);
  $('#masterLinks').addEventListener('paste', () => setTimeout(addLinks, 0));
  $('#masterLinks').addEventListener('keydown', e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); addLinks(); } });

  async function saveMaster(open) {
    if (!addLinks()) return; // a link still in the box
    const body = {
      id: editing.id, name: $('#masterName').value,
      parts: editing.parts.map(({ url, title, skipStart, skipEnd }) => ({ url, title, skipStart, skipEnd })),
    };
    $('#masterSave').disabled = $('#masterOpen').disabled = true;
    try {
      const m = await api('/api/masters', { method: 'PUT', body, privileged: true });
      closeDialog('#masterDialog');
      const url = `master:${m.id}`;
      if (state.presentationId === url) {
        if (open) refresh();
        else toast(`Saved “${m.name}”. Refresh to show the changes.`, 'ok', { label: 'Refresh', run: refresh });
      } else if (open) openPresentation(url, m.name);
      else toast(`Saved “${m.name}”`, 'ok', { label: 'Open', run: () => openPresentation(url, m.name) });
    } catch (err) {
      $('#masterError').textContent = err.message;
    } finally {
      $('#masterSave').disabled = $('#masterOpen').disabled = false;
    }
  }
  $('#masterForm').addEventListener('submit', e => { e.preventDefault(); saveMaster(false); });
  $('#masterOpen').addEventListener('click', () => saveMaster(true));
  $('#masterDelete').addEventListener('click', async e => {
    const b = e.currentTarget;
    if (b.textContent === 'Delete') { b.textContent = 'Delete for good?'; return; }
    try {
      await api(`/api/masters/${encodeURIComponent(editing.id)}`, { method: 'DELETE', privileged: true });
      closeDialog('#masterDialog');
      toast(`Deleted “${editing.name}”. Its presentations are still where they were.`, 'info');
    } catch (err) { $('#masterError').textContent = err.message; }
  });
  for (const id of ['#newMasterBtn', '#emptyNewMaster']) {
    $(id).hidden = !isDesktop;
    $(id).addEventListener('click', () => { closeMenu(); editMaster(null); });
  }

  function renderRemote() {
    const box = $('#remoteUrls');
    if (!info) { box.innerHTML = ''; return; }
    if (info.warning) {
      box.innerHTML = `<p class="remote-off">${esc(info.warning)}</p>`;
    } else if (!info.remoteEnabled) {
      box.innerHTML = '<p class="remote-off">Off</p>';
    } else if (!info.remoteUrls.length) {
      box.innerHTML = '<p class="remote-off">Not connected to a network.</p>';
    } else {
      box.replaceChildren(...info.remoteUrls.map(u => {
        const row = document.createElement('div');
        row.className = 'remote-url';
        row.innerHTML = `<code>${esc(u.replace(/^http:\/\//, '').replace(/\/$/, ''))}</code>
          <button class="btn btn-plain btn-icon" type="button" title="Copy link" aria-label="Copy link"><svg class="ic"><use href="#i-copy"/></svg></button>`;
        $('button', row).onclick = async () => {
          try { await navigator.clipboard.writeText(u); toast('Link copied — open it on your phone or tablet.', 'ok'); }
          catch { toast('Couldn’t copy the link.', 'error'); }
        };
        return row;
      }));
    }
  }

  // ------------------------------------------------------------------ Filmstrip & progress

  // The mouse wheel scrolls the filmstrip sideways (it never changes slide). At either end the
  // wheel falls through to scrolling the page as usual.
  $('#filmstrip').addEventListener('wheel', e => {
    const strip = e.currentTarget;
    if (e.ctrlKey || Math.abs(e.deltaX) > Math.abs(e.deltaY)) return; // trackpad sideways swipes already work
    const delta = e.deltaY * (e.deltaMode === 1 ? 40 : e.deltaMode === 2 ? strip.clientWidth : 1);
    const max = strip.scrollWidth - strip.clientWidth;
    if (max <= 0 || (delta < 0 && strip.scrollLeft <= 0) || (delta > 0 && strip.scrollLeft >= max - 1)) return;
    e.preventDefault();
    strip.scrollBy({ left: delta, behavior: 'instant' });
  }, { passive: false });

  let jumpFilmstrip = true;
  function buildFilmstrip() {
    const strip = $('#filmstrip');
    const skipped = new Set(state.skipped || []);
    const places = Array.from({ length: state.thumbnailCount }, (_, i) => i + 1);
    $('#slideCountLabel').textContent = places.length || '';
    // In a master deck, where each presentation starts.
    const starts = new Map(state.isMaster ? state.parts.filter((p, i) => (state.parts[i + 1] ? state.parts[i + 1].stripStart : places.length) > p.stripStart).map(p => [p.stripStart, p]) : []);
    const items = places.map((n, i) => {
      const b = document.createElement('button');
      const skip = skipped.has(n);
      b.className = 'thumb' + (skip ? ' is-skipped' : '');
      b.type = 'button';
      b.dataset.n = n;
      b.setAttribute('role', 'option');
      const part = starts.get(i);
      if (part) b.classList.add('starts-part');
      b.innerHTML = `${starts.size ? `<span class="thumb-part" title="${part ? esc(part.title) : ''}">${part ? esc(part.title) : ''}</span>` : ''}
        <div class="thumb-img is-waiting"><img loading="lazy" alt="${skip ? `Skipped slide ${n}` : `Slide ${n}`}" data-place="${n}">${skip ? '<svg class="ic thumb-skip"><use href="#i-skip"/></svg>' : ''}</div>
        <span class="thumb-label">${n}${skip ? '<span class="badge">Skipped</span>' : ''}</span>`;
      const img = $('img', b);
      img.onload = () => { img.classList.add('is-loaded'); img.parentElement.classList.remove('is-waiting'); };
      if (!skip) b.onclick = () => act(`/api/goto/${n}`);
      else { b.title = 'Skipped in the presentation, so it isn’t shown'; b.setAttribute('aria-disabled', 'true'); }
      return b;
    });
    strip.classList.toggle('has-parts', starts.size > 0);
    strip.replaceChildren(...items);
    // A new deck starts at the beginning; the first positioning jumps instead of animating.
    strip.scrollTo({ left: 0, behavior: 'instant' });
    jumpFilmstrip = true;
  }

  // The nearest slide from n (in direction dir) that isn't skipped.
  function shownSlide(n, dir) {
    const skipped = new Set(state.skipped || []);
    while (skipped.has(n)) n += dir;
    return n;
  }

  // Pictures arrive after the deck has opened, nearest slides first.
  function fillPictures() {
    const missing = new Set(state.missingPictures || []);
    const old = new Set(state.oldPictures || []);
    for (const img of document.querySelectorAll('#filmstrip img[data-place]')) {
      // Pictures from before an edit are shown until the new ones arrive.
      const n = +img.dataset.place, have = missing.has(n) ? '' : old.has(n) ? 'old' : 'new';
      if (!have || img.dataset.have === have) continue;
      img.dataset.have = have;
      img.src = thumbUrl(n, 640);
    }
    reportLooking();
  }

  // Tells the app which slides are in view, to get their pictures first.
  let lookingTimer, lookedAt = '';
  function reportLooking() {
    clearTimeout(lookingTimer);
    if (!state || !((state.missingPictures || []).length + (state.oldPictures || []).length)) return;
    lookingTimer = setTimeout(() => {
      const strip = $('#filmstrip'), thumbs = strip.children;
      if (!thumbs.length || !strip.clientWidth) return;
      // They're all the same width.
      const first = thumbs[0].getBoundingClientRect(), last = thumbs[thumbs.length - 1].getBoundingClientRect(), box = strip.getBoundingClientRect();
      const step = thumbs.length > 1 ? (last.left - first.left) / (thumbs.length - 1) : first.width || 1;
      const from = Math.max(1, Math.floor((box.left - first.left) / step) + 1);
      const to = Math.min(thumbs.length, Math.floor((box.right - first.left) / step) + 2);
      const key = `${state.version} ${from} ${to}`;
      if (key === lookedAt) return;
      lookedAt = key;
      api(`/api/strip/looking?from=${from}&to=${to}`).catch(() => { lookedAt = ''; });
    }, 100);
  }
  $('#filmstrip').addEventListener('scroll', reportLooking, { passive: true });

  function updateCurrentSlide() {
    const cur = state.slide;
    const next = shownSlide(cur + 1, 1);
    for (const t of $('#filmstrip').children) {
      const n = +t.dataset.n || -1;
      t.classList.toggle('is-current', n === cur);
      t.classList.toggle('is-next', n === next && n !== cur);
      t.setAttribute('aria-selected', String(n === cur));
    }
    const current = $(`#filmstrip [data-n="${cur}"]`);
    if (current) {
      // Centre the current slide in the strip (without scrolling the page itself).
      const strip = $('#filmstrip');
      const r = current.getBoundingClientRect(), sr = strip.getBoundingClientRect();
      const left = strip.scrollLeft + (r.left - sr.left) - (strip.clientWidth - r.width) / 2;
      strip.scrollTo({ left: Math.max(0, left), behavior: jumpFilmstrip ? 'instant' : 'smooth' });
    }
    jumpFilmstrip = false;
  }

  // ------------------------------------------------------------------ Monitors (MJPEG streams)

  let streamWidth = 0;
  function startStreams() {
    if (document.hidden) return stopStreams();
    const screen = $('.screen');
    const w = Math.min(1920, Math.max(480, Math.ceil(((screen ? screen.clientWidth : 800) * devicePixelRatio) / 160) * 160));
    if (w === streamWidth && $('#slidesStream').src) return;
    streamWidth = w;
    $('#slidesStream').src = `/api/stream/slide.mjpg?w=${w}`;
    $('#notesStream').src = `/api/stream/notes.mjpg?w=${w}`;
  }
  function stopStreams() {
    streamWidth = 0;
    $('#slidesStream').removeAttribute('src');
    $('#notesStream').removeAttribute('src');
  }
  document.addEventListener('visibilitychange', startStreams);
  let resizeTimer;
  window.addEventListener('resize', () => { clearTimeout(resizeTimer); resizeTimer = setTimeout(startStreams, 400); });

  // ------------------------------------------------------------------ Opening presentations

  // The presentation being switched to (blurs the main panel and marks its recent item).
  let switching = null;
  function setSwitching(value) {
    switching = value;
    app.classList.toggle('is-switching', !!value);
    if (value) {
      const verb = value.refresh ? 'Refreshing' : 'Opening';
      $('#switchTitle').textContent = value.title ? `${verb} “${value.title}”` : `${verb} presentation…`;
      $('#switchSub').textContent = value.refresh
        ? value.file ? 'Reading the file again' : 'Downloading the latest slides and speaker notes'
        : value.file ? 'Reading the file' : 'Downloading slides and speaker notes';
    }
    if (state) renderRecents();
  }

  // Google ends sessions after a while. Says so when it happens, and keeps a button up until it's sorted.
  let expiredToasted = false;
  function renderGoogleExpired(s) {
    const b = $('#signInAgain');
    b.hidden = !s.googleExpired;
    b.disabled = !isDesktop;
    if (s.googleExpired && !expiredToasted) {
      toast(isDesktop
        ? 'Google has signed you out. Sign in again to open presentations and clips that aren’t public.'
        : 'Google has signed the presenter computer out. Sign in again there to open presentations that aren’t public.',
      'error', isDesktop ? { label: 'Sign in again', run: () => signInAgain() } : undefined);
    }
    expiredToasted = !!s.googleExpired;
  }
  let signingIn = false;
  async function signInAgain(then) {
    if (signingIn) return;
    signingIn = true;
    try {
      google = await api('/api/google/signin', { privileged: true });
      renderGoogle();
      if (then) then();
      else toast(`Signed in${google.email ? ` as ${google.email}` : ''}.`, 'ok',
        state && state.presentationId ? { label: 'Refresh', run: refresh } : undefined);
    } catch (err) {
      toast(err.message, 'error');
    } finally { signingIn = false; }
  }
  $('#signInAgain').addEventListener('click', () => signInAgain());

  // Opens Google's page for a deck ("presentation") or Drive file ("file") in a window on the app's
  // Google account, where Google's own "Request access" button sends the request. Desktop only.
  async function requestAccess(kind, id) {
    try {
      await api('/api/google/request-access', { body: { kind, id }, privileged: true });
    } catch (err) { toast(err.message, 'error'); }
  }

  // A failed load; when it's for lack of access, offers to request it.
  function loadErrorToast(err, id, retry) {
    const presentationId = (err.data && err.data.presentationId) || id;
    if (err.data && err.data.code === 'signed-out' && isDesktop)
      toast(err.message, 'error', { label: 'Sign in again', run: () => signInAgain(retry) });
    else if (err.data && err.data.code === 'no-access' && isDesktop && presentationId)
      toast(err.message, 'error', { label: 'Request access', run: () => requestAccess('presentation', presentationId) });
    else
      toast(err.message, 'error');
  }

  const fileTitle = p => p.file ? p.url.split(/[\\/]/).pop().replace(/\.\w+$/, '') : undefined;

  async function browse(many) {
    try {
      return (await api(`/api/browse?many=${!!many}`, { privileged: true })).files;
    } catch (err) {
      toast(err.message, 'error');
      return [];
    }
  }
  for (const id of ['#emptyBrowse', '#openBrowse']) {
    $(id).hidden = !isDesktop;
    $(id).addEventListener('click', async () => {
      const [file] = await browse(false);
      if (!file) return;
      closeDialog('#openDialog');
      openPresentation(file);
    });
  }

  async function openPresentation(url, title) {
    const parsed = /^master:/.test(url) ? { id: url, url } : parseSlidesUrl(url);
    if (parsed.error || parsed.empty) { toast(parsed.error || 'Paste a Google Slides link first.', 'error'); return false; }
    const known = state && (state.recent || []).find(r => r.id === parsed.id);
    if (switching) return false;
    // Reloading the deck that's already open is a refresh, not a switch.
    const isSwitch = !state || parsed.id !== state.presentationId;
    if (isSwitch) setSwitching({ id: parsed.id, title: title || (known && known.title) || fileTitle(parsed), file: parsed.file });
    try {
      const s = await api('/api/load', { body: { url: parsed.url }, privileged: true });
      toast(`Opened “${s.title || 'presentation'}” · ${plural(s.slideCount, 'slide')}`, 'ok');
      requestAnimationFrame(startStreams);
      return true;
    } catch (err) {
      loadErrorToast(err, parsed.id, () => openPresentation(url, title));
      return false;
    } finally {
      if (isSwitch) setSwitching(null);
    }
  }

  function wireUrlField(input, field, hint, defaultHint) {
    const update = () => {
      const p = parseSlidesUrl(input.value);
      field.classList.toggle('is-invalid', !!p.error);
      field.classList.toggle('is-valid', !!p.id);
      hint.classList.toggle('is-error', !!p.error);
      hint.innerHTML = p.error ? esc(p.error) : defaultHint;
      return p;
    };
    input.addEventListener('input', update);
    return update;
  }

  const DEFAULT_HINT = 'Google Slides presentations must be shared as <b>Anyone with the link can view</b>, or with the account signed in here.';
  const validateEmpty = wireUrlField($('#emptyUrl'), $('#emptyField'), $('#emptyHint'), DEFAULT_HINT);
  const validateOpen = wireUrlField($('#openUrl'), $('#openField'), $('#openHint'), DEFAULT_HINT);

  $('#emptyForm').addEventListener('submit', async e => {
    e.preventDefault();
    if (validateEmpty().id) await openPresentation($('#emptyUrl').value);
    else $('#emptyUrl').focus();
  });

  async function showOpenDialog() {
    if (anyDialogOpen()) return;
    const dlg = $('#openDialog');
    // Hangs from the Open button; in the middle when the button isn't on screen (start screen, phones).
    const anchor = $('#openBtn').getBoundingClientRect();
    const anchored = anchor.width > 0 && anchor.left >= 0;
    dlg.classList.toggle('no-arrow', !anchored);
    dlg.style.setProperty('--pop-x', `${anchored ? anchor.left : Math.max(16, (innerWidth - 420) / 2)}px`);
    dlg.style.setProperty('--pop-y', `${anchored ? anchor.bottom + 10 : 90}px`);
    $('#openUrl').value = '';
    validateOpen();
    $('#clipSuggest').hidden = true;
    dlg.showModal();
    $('#openUrl').focus();
    try {
      const text = (await navigator.clipboard.readText()).trim();
      const p = parseSlidesUrl(text);
      if (p.id && (!state || p.id !== state.presentationId)) {
        $('#clipText').textContent = text;
        $('#clipSuggest').hidden = false;
        $('#clipSuggest').onclick = () => { $('#openUrl').value = text; validateOpen(); submitOpen(); };
      }
    } catch { /* clipboard not available */ }
  }

  async function submitOpen() {
    const p = validateOpen();
    if (!p.id) { $('#openUrl').focus(); return; }
    closeDialog('#openDialog');
    await openPresentation($('#openUrl').value);
  }
  $('#openForm').addEventListener('submit', e => {
    e.preventDefault();
    submitOpen();
  });
  $('#openBtn').addEventListener('click', () => { closeMenu(); showOpenDialog(); });

  async function refresh() {
    if (!state || !state.url || state.loading || switching) return;
    setSwitching({ id: state.presentationId, title: state.title, refresh: true, file: isFile(state.url) });
    try {
      await api('/api/refresh');
      toast('Presentation refreshed', 'ok');
    } catch (err) {
      loadErrorToast(err, state.presentationId, refresh);
    } finally {
      setSwitching(null);
    }
  }

  async function closePresentation() {
    if (!state || !state.presentationId || switching) return;
    const reopen = { url: state.url, title: state.title };
    try {
      await api('/api/close');
      toast(`Closed “${reopen.title || 'presentation'}”. The outputs are showing the holding screen.`, 'info',
        { label: 'Reopen', run: () => openPresentation(reopen.url, reopen.title) });
    } catch (err) { toast(err.message, 'error'); }
  }

  // ------------------------------------------------------------------ Transport & notes

  $('#prevBtn').addEventListener('click', () => act('/api/previous'));
  $('#nextBtn').addEventListener('click', () => act('/api/next'));
  $('#videoBtn').addEventListener('click', () => act('/api/video/toggle'));
  $('#videoRestartBtn').addEventListener('click', () => act('/api/video/restart'));
  $('#scrollUpBtn').addEventListener('click', () => act('/api/notes/scroll/up'));
  $('#scrollDownBtn').addEventListener('click', () => act('/api/notes/scroll/down'));
  $('#autoBtn').addEventListener('click', () => act('/api/notes/autofit/toggle'));

  $('#sizeUp').addEventListener('click', () => act('/api/notes/increase'));
  $('#sizeDown').addEventListener('click', () => act('/api/notes/decrease'));

  // ------------------------------------------------------------------ Settings

  async function showSettings(pane = 'ndi') {
    if (!isDesktop || anyDialogOpen()) return;
    const form = $('#settingsForm');
    $('#settingsError').textContent = '';
    try {
      const s = await (await fetch('/api/settings')).json();
      runningSettings = await (await fetch('/api/settings/running')).json();
      form.slidesNdiName.value = s.slidesNdiName;
      form.notesNdiName.value = s.notesNdiName;
      form.ndiDiscoveryServer.value = s.ndiDiscoveryServer || '';
      const res = `${s.width}x${s.height}`;
      if (![...form.resolution.options].some(o => o.value === res)) form.resolution.add(new Option(`${s.width} × ${s.height}`, res));
      form.resolution.value = res;
      if (![...form.frameRate.options].some(o => o.value === String(s.frameRate))) form.frameRate.add(new Option(`${s.frameRate} fps`, s.frameRate));
      form.frameRate.value = String(s.frameRate);
      form.nextPreview.value = s.nextPreview;
      form.remoteAccess.checked = s.remoteAccess;
      form.apiPort.value = s.apiPort;
      await fillAudio(form, s);
      await fillGoogle();
      await fillUpdate();
    } catch { toast('Couldn’t load settings.', 'error'); return; }
    showPane(pane);
    $('#settingsDialog').showModal();
    updateRestartNote();
    if (pane !== 'ndi') return;
    form.slidesNdiName.focus();
    form.slidesNdiName.select();
  }
  $('#settingsBtn').hidden = !isDesktop;
  $('#settingsBtn').addEventListener('click', () => { closeMenu(); showSettings(); });

  function showPane(id) {
    for (const t of document.querySelectorAll('.s-tab')) {
      t.classList.toggle('active', t.dataset.pane === id);
      t.setAttribute('aria-selected', String(t.dataset.pane === id));
    }
    for (const p of document.querySelectorAll('.s-pane')) p.hidden = p.dataset.pane !== id;
    $('.s-content').scrollTop = 0;
  }
  for (const t of document.querySelectorAll('.s-tab')) t.addEventListener('click', () => showPane(t.dataset.pane));
  // A field that stops the form being saved may be on a pane that isn't showing.
  $('#settingsForm').addEventListener('invalid', e => showPane(e.target.closest('.s-pane').dataset.pane), true);

  // Audio settings apply live (no restart), so levels can be set while a video plays.
  async function fillAudio(form, s) {
    const { devices, error, asio } = await (await fetch('/api/audio/devices')).json();
    const def = devices.find(d => d.isDefault);
    const options = (includeDefault) => [
      ...(includeDefault ? [new Option(`System default${def ? ` (${def.name})` : ''}`, 'default')] : []),
      ...devices.map(d => new Option(d.name, d.id)),
      new Option('Off', 'none'),
    ];
    form.audioOutputDevice.replaceChildren(...options(true));
    form.audioMonitorDevice.replaceChildren(...options(true));
    const pick = (select, value) => {
      if (![...select.options].some(o => o.value === value)) select.add(new Option('Unavailable device', value));
      select.value = value;
    };
    pick(form.audioOutputDevice, s.audioOutputDevice);
    pick(form.audioMonitorDevice, s.audioMonitorDevice);
    form.audioOutputVolume.value = s.audioOutputVolume;
    form.audioMonitorVolume.value = s.audioMonitorVolume;
    const buffer = String(s.audioAsioBufferMs);
    if (![...form.audioAsioBuffer.options].some(o => o.value === buffer)) form.audioAsioBuffer.add(new Option(`${buffer} ms`, buffer));
    form.audioAsioBuffer.value = buffer;
    renderAsio(form, asio);
    updateAudioLabels(form);
    $('#audioError').textContent = error || '';
  }
  // Only known once the driver is open: its outputs, buffer size and latency.
  function renderAsio(form, asio) {
    $('#asioGroup').hidden = !asio;
    if (!asio) return;
    form.audioAsioChannel.replaceChildren(...asio.channelPairs.map((name, i) => new Option(name, i * 2)));
    form.audioAsioChannel.value = String(asio.channel);
    // 11 ms is the size of the blocks captured from the player.
    const total = Math.round(11 + asio.bufferMs + asio.latencyMs);
    $('#asioStatus').textContent = `Driver buffer ${asio.bufferFrames} samples, output latency ${asio.latencyMs.toFixed(1)} ms. About ${total} ms in total.`;
  }
  $('#asioPanelBtn').hidden = !isDesktop;
  $('#asioPanelBtn').addEventListener('click', () =>
    api('/api/audio/asio-panel', { method: 'POST', privileged: true }).catch(err => { $('#audioError').textContent = err.message; }));
  // A new buffer size set in the driver's own window shows up when the app gets focus back.
  window.addEventListener('focus', async () => {
    if (!$('#settingsDialog').open || $('#asioGroup').hidden) return;
    try {
      const { error, asio } = await (await fetch('/api/audio/devices')).json();
      renderAsio($('#settingsForm'), asio);
      $('#audioError').textContent = error || '';
    } catch { /* it'll update the next time Settings opens */ }
  });
  function updateAudioLabels(form) {
    $('#outVolLabel').textContent = `${form.audioOutputVolume.value}%`;
    $('#monVolLabel').textContent = `${form.audioMonitorVolume.value}%`;
    form.audioOutputVolume.disabled = form.audioOutputDevice.value === 'none';
    form.audioMonitorVolume.disabled = form.audioMonitorDevice.value === 'none';
    for (const s of [form.audioOutputVolume, form.audioMonitorVolume]) s.style.setProperty('--p', `${s.value}%`);
  }
  let audioTimer;
  function saveAudio() {
    const form = $('#settingsForm');
    updateAudioLabels(form);
    clearTimeout(audioTimer);
    audioTimer = setTimeout(async () => {
      try {
        const r = await api('/api/audio', { method: 'PUT', privileged: true, body: {
          outputDevice: form.audioOutputDevice.value, outputVolume: Number(form.audioOutputVolume.value),
          monitorDevice: form.audioMonitorDevice.value, monitorVolume: Number(form.audioMonitorVolume.value),
          asioBufferMs: Number(form.audioAsioBuffer.value),
          ...(form.audioAsioChannel.value ? { asioChannel: Number(form.audioAsioChannel.value) } : {}),
        } });
        $('#audioError').textContent = (r && r.error) || '';
        if (r) renderAsio(form, r.asio);
      } catch (err) {
        $('#audioError').textContent = err.message;
        // A device that won't open comes back as an error, along with what is open now.
        if (err.data && 'asio' in err.data) renderAsio(form, err.data.asio);
      }
    }, 80);
  }
  for (const name of ['audioOutputDevice', 'audioMonitorDevice', 'audioOutputVolume', 'audioMonitorVolume', 'audioAsioChannel', 'audioAsioBuffer'])
    $('#settingsForm')[name].addEventListener('input', saveAudio);

  // Applies straight away, so it can be judged on the output.
  $('#settingsForm').nextPreview.addEventListener('change', e =>
    api('/api/presenter', { method: 'PUT', body: { nextPreview: e.target.value }, privileged: true })
      .catch(err => { $('#settingsError').textContent = err.message; }));

  // The Google account (desktop only) signs in through its own window and applies straight away.
  let google = { signedIn: false, email: null };
  function renderGoogle() {
    const row = $('#googleGroup .account-row');
    row.classList.toggle('is-signed-in', google.signedIn);
    $('#googleName').textContent = google.signedIn ? (google.email || 'Signed in') : google.expired ? (google.email || 'Signed out by Google') : 'Not signed in';
    $('#googleHint').hidden = google.signedIn;
    $('#googleHint').textContent = google.expired ? 'Google has ended the session. Sign in again.' : 'Public decks work without signing in.';
    $('#googleAvatar').textContent = google.signedIn && google.email ? google.email[0] : 'G';
    $('#googleBtn').textContent = google.signedIn ? 'Sign out' : google.expired ? 'Sign in again' : 'Sign in';
  }
  async function fillGoogle() {
    $('#googleError').textContent = '';
    try { google = await api('/api/google', { method: 'GET', privileged: true }); }
    catch { google = { signedIn: false, email: null }; }
    renderGoogle();
  }
  $('#googleBtn').addEventListener('click', async () => {
    const btn = $('#googleBtn');
    const signingIn = !google.signedIn;
    btn.disabled = true;
    $('#googleError').textContent = '';
    if (signingIn) btn.textContent = 'Waiting for sign-in…';
    try {
      google = await api(signingIn ? '/api/google/signin' : '/api/google/signout', { privileged: true });
      toast(signingIn ? `Signed in${google.email ? ` as ${google.email}` : ''}.` : 'Signed out of Google.', 'ok');
    } catch (err) {
      $('#googleError').textContent = err.message;
    } finally {
      btn.disabled = false;
      renderGoogle();
    }
  });

  // ------------------------------------------------------------------ Updates (desktop only)

  let update = null, updateTimer, updateToasted = '';
  async function fillUpdate() {
    if (!isDesktop) return;
    try { update = await api('/api/update', { method: 'GET', privileged: true }); renderUpdate(); }
    catch { /* shows the last known */ }
    clearTimeout(updateTimer);
    updateTimer = setTimeout(fillUpdate, (update && update.activity) || $('#settingsDialog').open ? 1000 : 20000);
  }
  function renderUpdate() {
    const u = update;
    const when = u.automatic ? 'It’ll be installed when you close the app.' : '';
    $('#updateReady').hidden = !u.ready;
    $('#updateVersion').textContent = u.version;
    $('#updateDownload').hidden = u.supported;
    $('#updateDownload').href = u.releasesUrl;
    $('#updateAutoRow').hidden = !u.supported;
    $('#updateAuto').checked = u.automatic;
    $('#updateError').textContent = u.lastError || '';
    const b = $('#updateBtn');
    b.hidden = !u.supported;
    b.disabled = !!u.activity;
    b.textContent = u.ready ? 'Update now' : 'Check for updates';
    $('#updateStatus').textContent = !u.supported ? 'This copy wasn’t installed, so it can’t update itself.'
      : u.activity === 'checking' ? 'Checking…'
      : u.activity === 'downloading' ? `Downloading… ${u.percent}%`
      : u.ready ? `Version ${u.ready} is ready. ${when}`
      : u.lastError ? ''
      : u.lastChecked ? `Up to date, checked ${timeAgo(u.lastChecked)}`
      : 'Not checked yet';
    if (u.ready && u.ready !== updateToasted) {
      updateToasted = u.ready;
      if (!$('#settingsDialog').open) toast(`Version ${u.ready} is ready. ${when}`, 'info');
    }
  }
  function offerUpdate() {
    if (!update || !update.ready) return;
    $('#updateTitle').textContent = `Update to version ${update.ready}?`;
    $('#updateText').textContent = `The app closes, updates and opens again. The outputs stop for several seconds${
      state && state.presentationId ? ', and the presentation starts again from its first slide' : ''}.${
      update.automatic ? ' If you choose Later, it’s installed when you close the app.' : ''}`;
    $('#updateNotes').href = `${update.releasesUrl}/tag/v${update.ready}`;
    $('#updateDialog').showModal();
    $('#updateNow').focus();
  }
  $('#updateReady').addEventListener('click', () => { if (!anyDialogOpen()) offerUpdate(); });
  $('#updateBtn').addEventListener('click', async () => {
    if (update && update.ready) { closeDialog('#settingsDialog'); offerUpdate(); return; }
    $('#updateBtn').disabled = true;
    try { await api('/api/update/check', { privileged: true }); }
    catch (err) { $('#updateError').textContent = err.message; }
    fillUpdate();
  });
  $('#updateAuto').addEventListener('change', async e => {
    try { update = await api('/api/update', { method: 'PUT', body: { automatic: e.target.checked }, privileged: true }); renderUpdate(); }
    catch (err) { $('#updateError').textContent = err.message; }
  });
  $('#updateNow').addEventListener('click', async () => {
    closeDialog('#updateDialog');
    try { await api('/api/update/install', { privileged: true }); toast('Updating…', 'info'); }
    catch (err) { toast(err.message, 'error'); }
  });

  // ------------------------------------------------------------------ Audio meter

  (() => {
    const meter = $('#meter'), bars = [$('#meterL'), $('#meterR')];
    let hideTimer;
    const toScale = v => {
      if (v <= 0.0001) return 0;
      const db = 20 * Math.log10(v);           // -60 dB .. 0 dB → 0 .. 1
      return Math.max(0, Math.min(1, (db + 60) / 60));
    };
    const es = new EventSource('/api/meter');
    es.onmessage = e => {
      const { l, r } = JSON.parse(e.data);
      [l, r].forEach((v, i) => { bars[i].style.clipPath = `inset(${(1 - toScale(v)) * 100}% 0 0 0)`; });
      if (l > 0.003 || r > 0.003) {
        meter.classList.add('is-active');
        clearTimeout(hideTimer);
        hideTimer = setTimeout(() => meter.classList.remove('is-active'), 1500);
      }
    };
  })();

  // The settings that are only read when the app starts, as they are in the form.
  function startupSettings() {
    const f = $('#settingsForm');
    const [width, height] = f.resolution.value.split('x').map(Number);
    return {
      slidesNdiName: f.slidesNdiName.value.trim(), notesNdiName: f.notesNdiName.value.trim(), ndiDiscoveryServer: f.ndiDiscoveryServer.value.trim(),
      width, height, frameRate: Number(f.frameRate.value),
      remoteAccess: f.remoteAccess.checked, apiPort: Number(f.apiPort.value),
    };
  }
  let runningSettings = null; // what this run of the app started with
  function updateRestartNote() {
    const now = startupSettings();
    $('#settingsNote').hidden = !runningSettings || Object.keys(now).every(k => now[k] === runningSettings[k]);
  }
  $('#settingsForm').addEventListener('input', updateRestartNote);

  function offerRestart() {
    $('#restartText').textContent = state && state.presentationId
      ? 'The outputs stop for a few seconds, and the presentation starts again from its first slide.'
      : 'The outputs stop for a few seconds.';
    $('#restartDialog').showModal();
    $('#restartNow').focus();
  }
  $('#restartDue').addEventListener('click', () => { if (!anyDialogOpen()) offerRestart(); });
  $('#restartNow').addEventListener('click', async () => {
    closeDialog('#restartDialog');
    toast('Restarting…', 'info');
    await api('/api/app/restart', { privileged: true }).catch(() => {});
  });

  $('#settingsForm').addEventListener('submit', async e => {
    e.preventDefault();
    const body = startupSettings();
    const save = $('#settingsSave');
    save.disabled = true;
    try {
      const r = await api('/api/settings', { method: 'PUT', body, privileged: true });
      closeDialog('#settingsDialog');
      $('#restartDue').hidden = !r.restartRequired;
      if (r.restartRequired) offerRestart();
      else toast('Settings saved.', 'ok');
    } catch (err) {
      $('#settingsError').textContent = err.message;
    } finally {
      save.disabled = false;
    }
  });

  // ------------------------------------------------------------------ Dialogs & menu

  const anyDialogOpen = () => !!document.querySelector('dialog[open]');
  function closeDialog(sel) { const d = $(sel); if (d.open) d.close(); }
  for (const d of document.querySelectorAll('dialog')) {
    // A click on the backdrop closes the dialog, but only if the press also started there:
    // dragging out of a text field to select text must not close it.
    let pressedOnBackdrop = false;
    d.addEventListener('pointerdown', e => { pressedOnBackdrop = e.target === d; });
    d.addEventListener('click', e => {
      if (e.target.closest('[data-close]') || (e.target === d && pressedOnBackdrop)) d.close();
      pressedOnBackdrop = false;
    });
  }

  const closeMenu = () => app.classList.remove('menu-open');
  $('#menuBtn').addEventListener('click', () => app.classList.toggle('menu-open'));
  app.addEventListener('click', e => { if (e.target === app) closeMenu(); });

  // ------------------------------------------------------------------ Keyboard

  let gotoBuffer = '', gotoTimer;
  function showGoto() {
    $('#gotoNum').textContent = gotoBuffer;
    $('#gotoHud').hidden = !gotoBuffer;
    clearTimeout(gotoTimer);
    if (gotoBuffer) gotoTimer = setTimeout(() => { gotoBuffer = ''; showGoto(); }, 2500);
  }

  document.addEventListener('keydown', e => {
    if (anyDialogOpen()) return;
    const typing = e.target.closest('input:not([type="range"]), select, textarea');
    const mod = e.ctrlKey || e.metaKey;

    if (mod && (e.key === 'o' || e.key === 'l')) { e.preventDefault(); showOpenDialog(); return; }
    if (mod && e.key === ',') { e.preventDefault(); showSettings(); return; }
    if (mod && (e.key === 'w' || e.key === 'W')) { e.preventDefault(); closePresentation(); return; }
    if (typing || mod || e.altKey) return;
    if (e.target.type === 'range' && ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End', 'PageUp', 'PageDown'].includes(e.key)) return;

    const handled = () => e.preventDefault();
    if (/^[0-9]$/.test(e.key) && state && state.slideCount) {
      gotoBuffer = (gotoBuffer + e.key).slice(0, 3);
      showGoto(); handled(); return;
    }
    if (e.key === 'Enter' && gotoBuffer) {
      const n = parseInt(gotoBuffer, 10);
      gotoBuffer = ''; showGoto();
      if (n >= 1 && n <= state.slideCount) {
        act(`/api/goto/${n}`);
        if ((state.skipped || []).includes(n)) toast(`Slide ${n} is skipped in the presentation, so it isn’t shown.`, 'info');
      }
      else toast(`There's no slide ${n}.`, 'error');
      handled(); return;
    }
    if (e.key === 'Escape' && gotoBuffer) { gotoBuffer = ''; showGoto(); return; }

    switch (e.key) {
      case 'ArrowRight': case ' ': case 'PageDown': case 'n': case 'N':
        act('/api/next'); handled(); break;
      case 'ArrowLeft': case 'PageUp': case 'b': case 'B':
        act('/api/previous'); handled(); break;
      case 'Home': if (state && state.slideCount) act('/api/goto/1'); handled(); break;
      case 'End': if (state && state.slideCount) act(`/api/goto/${state.slideCount}`); handled(); break;
      case 'p': case 'P': case 'k': case 'K':
        if (state && state.video) act(e.shiftKey ? '/api/video/restart' : '/api/video/toggle'); handled(); break;
      case 'ArrowUp': act('/api/notes/scroll/up'); handled(); break;
      case 'ArrowDown': act('/api/notes/scroll/down'); handled(); break;
      case '+': case '=': act('/api/notes/increase'); handled(); break;
      case '-': case '_': act('/api/notes/decrease'); handled(); break;
      case 'a': case 'A': act('/api/notes/autofit/toggle'); handled(); break;
      case 'r': case 'R': refresh(); handled(); break;
      case '?': showSettings('shortcuts'); handled(); break;
    }
  });

  // ------------------------------------------------------------------ Window buttons

  // The top bar is the window's title bar, so it has the caption buttons too (see MainForm.cs).
  const host = isDesktop && window.chrome && window.chrome.webview;
  if (host) {
    document.body.classList.add('has-frame');
    $('.win-buttons').addEventListener('click', e => {
      const b = e.target.closest('[data-win]');
      if (b) host.postMessage(`window:${b.dataset.win}`);
    });
    // Presentation files can be dropped anywhere on the window.
    for (const type of ['dragover', 'drop']) document.addEventListener(type, e => {
      if (!e.dataTransfer || ![...e.dataTransfer.types].includes('Files')) return;
      e.preventDefault();
      if (type === 'drop' && e.dataTransfer.files.length) host.postMessageWithAdditionalObjects('drop', e.dataTransfer.files);
      else e.dataTransfer.dropEffect = 'copy';
    });
    host.addEventListener('message', e => {
      if (typeof e.data === 'string' && e.data.startsWith('files:')) {
        const files = JSON.parse(e.data.slice(6));
        if ($('#masterDialog').open) addParts(files.map(parseSlidesUrl).filter(l => l.id));
        else if (files.length && !$('#settingsDialog').open) { closeDialog('#openDialog'); openPresentation(files[0]); }
      }
      if (e.data === 'window:normal' || e.data === 'window:maximized') {
        const max = e.data === 'window:maximized';
        document.body.classList.toggle('is-maximized', max);
        $('[data-win="maximize"]').title = max ? 'Restore' : 'Maximise';
      }
    });
    host.postMessage('window:ready');
  }

  // ------------------------------------------------------------------ Start

  connect();
  if (isDesktop) Promise.all(['/api/settings', '/api/settings/running'].map(u => fetch(u).then(r => r.json()))).then(([saved, running]) => {
    $('#restartDue').hidden = ['slidesNdiName', 'notesNdiName', 'ndiDiscoveryServer', 'width', 'height', 'frameRate', 'remoteAccess', 'apiPort']
      .every(k => saved[k] === running[k]);
  }).catch(() => {});
  loadInfo();
  fillUpdate();
  startStreams();
  new MutationObserver(() => { if (app.dataset.view === 'stage') startStreams(); })
    .observe(app, { attributes: true, attributeFilter: ['data-view'] });
})();
