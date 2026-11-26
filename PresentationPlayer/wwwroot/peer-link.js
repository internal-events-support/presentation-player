// A direct connection (WebRTC) between the app and the co-presenter's clicker page, set up by hand:
// the app's invite goes to the co-presenter as a link, and their reply code comes back the same way.
// Nothing is sent anywhere else, other than asking Google's STUN server for each side's public address.
// Used by app.js and by clicker/index.html, which is published next to a copy of this file.
(() => {
  'use strict';

  const ICE_SERVERS = [{ urls: 'stun:stun.l.google.com:19302' }];
  const VERSION = 1;
  const SETUPS = ['actpass', 'active', 'passive'];
  const MAX_CANDIDATES = 6;

  // A code holds only what can't be worked out: the ICE username and password, the DTLS certificate's
  // fingerprint and the addresses to try. The rest of the description is the same every time.
  //   version, setup, checksum, ufrag (length + text), pwd (length + text), fingerprint (32 bytes),
  //   then for each address: kind, address (4 or 16 bytes), port (2 bytes)
  const KIND = { host4: 1, host6: 2, mdns: 3, srflx4: 4, srflx6: 5 };

  function toBase64Url(bytes) {
    let s = '';
    for (const b of bytes) s += String.fromCharCode(b);
    return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  }
  function fromBase64Url(text) {
    const s = atob(text.replace(/-/g, '+').replace(/_/g, '/'));
    return Uint8Array.from(s, c => c.charCodeAt(0));
  }

  function ipv6Bytes(text) {
    const [head, tail = null] = text.split('::');
    const part = p => (p ? p.split(':') : []);
    let groups = part(head);
    if (tail !== null) {
      const end = part(tail);
      groups = [...groups, ...Array(8 - groups.length - end.length).fill('0'), ...end];
    }
    if (groups.length !== 8) return null;
    const out = [];
    for (const g of groups) {
      const n = parseInt(g, 16);
      if (!/^[0-9a-f]{1,4}$/i.test(g)) return null;
      out.push(n >> 8, n & 255);
    }
    return out;
  }
  function ipv6Text(bytes) {
    const groups = [];
    for (let i = 0; i < 16; i += 2) groups.push(((bytes[i] << 8) | bytes[i + 1]).toString(16));
    return groups.join(':');
  }

  /** The parts of a session description (SDP) that go in a code. */
  function parse(sdp) {
    const line = name => (sdp.match(new RegExp(`^a=${name}:(.+)$`, 'm')) || [])[1];
    const ufrag = line('ice-ufrag'), pwd = line('ice-pwd'), setup = line('setup');
    const fp = (line('fingerprint') || '').trim().split(' ');
    if (!ufrag || !pwd || !SETUPS.includes(setup) || fp[0].toLowerCase() !== 'sha-256') throw new Error('unexpected description');
    const fingerprint = fp[1].split(':').map(h => parseInt(h, 16));
    if (fingerprint.length !== 32) throw new Error('unexpected fingerprint');

    const candidates = [];
    for (const m of sdp.matchAll(/^a=candidate:\S+ 1 udp \d+ (\S+) (\d+) typ (host|srflx)/gim)) {
      const [, address, port, type] = m;
      let kind, bytes;
      if (/^\d+\.\d+\.\d+\.\d+$/.test(address)) { kind = type === 'host' ? KIND.host4 : KIND.srflx4; bytes = address.split('.').map(Number); }
      else if (/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.local$/i.test(address)) {
        kind = KIND.mdns; bytes = address.slice(0, 36).replace(/-/g, '').match(/../g).map(h => parseInt(h, 16));
      }
      else if (address.includes(':') && (bytes = ipv6Bytes(address))) kind = type === 'host' ? KIND.host6 : KIND.srflx6;
      else continue;
      if (!candidates.some(c => c.kind === kind && c.port === +port && c.bytes.join() === bytes.join()))
        candidates.push({ kind, bytes, port: +port });
    }
    // Public addresses first: they're what works between two different networks.
    candidates.sort((a, b) => (b.kind >= KIND.srflx4) - (a.kind >= KIND.srflx4));
    return { setup, ufrag, pwd, fingerprint, candidates: candidates.slice(0, MAX_CANDIDATES) };
  }

  function encode(p) {
    const text = s => [s.length, ...[...s].map(c => c.charCodeAt(0))];
    const body = [...text(p.ufrag), ...text(p.pwd), ...p.fingerprint];
    for (const c of p.candidates) body.push(c.kind, ...c.bytes, c.port >> 8, c.port & 255);
    const sum = body.reduce((a, b) => (a * 31 + b) & 255, 7);
    return toBase64Url([VERSION, SETUPS.indexOf(p.setup), sum, ...body]);
  }

  function decode(code) {
    let bytes;
    try { bytes = fromBase64Url(String(code).replace(/^.*#/, '').replace(/\s+/g, '')); }
    catch { throw new Error('bad code'); }
    let at = 0;
    const take = n => { if (at + n > bytes.length) throw new Error('bad code'); const out = bytes.slice(at, at + n); at += n; return out; };
    const [version, setup, sum] = take(3);
    if (version !== VERSION || !SETUPS[setup]) throw new Error('bad code');
    if (bytes.slice(3).reduce((a, b) => (a * 31 + b) & 255, 7) !== sum) throw new Error('bad code');
    const text = () => String.fromCharCode(...take(take(1)[0]));
    const ufrag = text(), pwd = text();
    if (!/^[A-Za-z0-9+/]{4,256}$/.test(ufrag) || !/^[A-Za-z0-9+/]{22,256}$/.test(pwd)) throw new Error('bad code');
    const fingerprint = [...take(32)];
    const candidates = [];
    while (at < bytes.length) {
      const kind = take(1)[0];
      const size = kind === KIND.host4 || kind === KIND.srflx4 ? 4 : kind >= 1 && kind <= 5 ? 16 : 0;
      if (!size) throw new Error('bad code');
      const b = [...take(size)], port = take(2);
      candidates.push({ kind, bytes: b, port: (port[0] << 8) | port[1] });
    }
    return { setup: SETUPS[setup], ufrag, pwd, fingerprint, candidates };
  }

  /** A session description for a data channel, rebuilt from a code. */
  function describe(p) {
    const hex = p.fingerprint.map(b => b.toString(16).toUpperCase().padStart(2, '0')).join(':');
    const lines = [
      'v=0', `o=- ${Math.floor(Math.random() * 1e15)} 2 IN IP4 127.0.0.1`, 's=-', 't=0 0',
      'a=group:BUNDLE 0', 'a=msid-semantic: WMS',
      'm=application 9 UDP/DTLS/SCTP webrtc-datachannel', 'c=IN IP4 0.0.0.0',
      `a=ice-ufrag:${p.ufrag}`, `a=ice-pwd:${p.pwd}`, 'a=ice-options:trickle',
      `a=fingerprint:sha-256 ${hex}`, `a=setup:${p.setup}`, 'a=mid:0',
      'a=sctp-port:5000', 'a=max-message-size:262144',
    ];
    p.candidates.forEach((c, i) => {
      const address = c.kind === KIND.host4 || c.kind === KIND.srflx4 ? c.bytes.join('.')
        : c.kind === KIND.mdns ? c.bytes.map(b => b.toString(16).padStart(2, '0')).join('').replace(/^(.{8})(.{4})(.{4})(.{4})/, '$1-$2-$3-$4-') + '.local'
        : ipv6Text(c.bytes);
      const srflx = c.kind >= KIND.srflx4;
      lines.push(`a=candidate:${i + 1} 1 udp ${(srflx ? 1686052607 : 2122260223) - i} ${address} ${c.port} typ ${srflx ? 'srflx raddr 0.0.0.0 rport 0' : 'host'}`);
    });
    lines.push('a=end-of-candidates');
    return lines.join('\r\n') + '\r\n';
  }

  // Waits for the addresses to be found (a code can't be added to later), but not forever.
  function gathered(pc, ms = 5000) {
    return new Promise(resolve => {
      if (pc.iceGatheringState === 'complete') return resolve();
      const done = () => { clearTimeout(timer); pc.removeEventListener('icegatheringstatechange', check); resolve(); };
      const check = () => { if (pc.iceGatheringState === 'complete') done(); };
      const timer = setTimeout(done, ms);
      pc.addEventListener('icegatheringstatechange', check);
    });
  }

  function connection() {
    const pc = new RTCPeerConnection({ iceServers: ICE_SERVERS });
    // Both ends make the same channel, so neither has to announce it.
    const channel = pc.createDataChannel('clicker', { negotiated: true, id: 0, ordered: true });
    return { pc, channel };
  }

  /** The app's side: a new connection and the code for the invite link. */
  async function invite() {
    const { pc, channel } = connection();
    await pc.setLocalDescription(await pc.createOffer());
    await gathered(pc);
    const p = parse(pc.localDescription.sdp);
    if (!p.candidates.length) { pc.close(); throw new Error('No network connection was found.'); }
    return { pc, channel, code: encode(p) };
  }

  /** The app's side: connect with the co-presenter's reply code. */
  async function accept(pc, replyCode) {
    let p;
    try { p = decode(replyCode); } catch { throw new Error('That isn’t a reply code. Check it was copied in full.'); }
    if (p.setup === 'actpass') throw new Error('That’s the invite link. Paste the code your co-presenter sends back.');
    if (!p.candidates.length) throw new Error('That reply code has no network addresses in it.');
    await pc.setRemoteDescription({ type: 'answer', sdp: describe(p) });
  }

  /** The clicker's side: a connection answering an invite, and the reply code. */
  async function reply(inviteCode) {
    let p;
    try { p = decode(inviteCode); } catch { throw new Error('This link isn’t complete. Ask the presenter to send it again.'); }
    if (p.setup !== 'actpass') throw new Error('This isn’t an invite link. Ask the presenter to send it again.');
    const { pc, channel } = connection();
    await pc.setRemoteDescription({ type: 'offer', sdp: describe(p) });
    await pc.setLocalDescription(await pc.createAnswer());
    await gathered(pc);
    const mine = parse(pc.localDescription.sdp);
    if (!mine.candidates.length) { pc.close(); throw new Error('No network connection was found.'); }
    return { pc, channel, code: encode(mine) };
  }

  window.PeerLink = { invite, accept, reply, _test: { parse, encode, decode, describe } };
})();
