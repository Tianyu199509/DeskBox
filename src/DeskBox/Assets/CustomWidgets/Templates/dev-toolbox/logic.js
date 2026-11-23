// DeskBox Official Template: Developer Toolbox (v1.0.0)
// Pure ES2023 runtime logic for QuickJS (zero external dependencies)

let _uuidSeq = 0;

function pad2(n) {
  return n < 10 ? '0' + n : String(n);
}

function pad3(n) {
  if (n < 10) return '00' + n;
  if (n < 100) return '0' + n;
  return String(n);
}

function pad4(n) {
  return String(n).padStart(4, '0');
}

function formatVars(template, vars) {
  if (!vars) return String(template || '');
  return String(template || '').replace(/\{([A-Za-z0-9_]+)\}/g, (_, k) =>
    Object.prototype.hasOwnProperty.call(vars, k) && vars[k] !== undefined ? String(vars[k]) : `{${k}}`
  );
}

function getArgVal(ctx, arg, key) {
  if (ctx && ctx.args && ctx.args[key] !== undefined) return ctx.args[key];
  if (ctx && ctx.event && ctx.event.args && ctx.event.args[key] !== undefined) return ctx.event.args[key];
  if (arg && typeof arg === 'object' && arg[key] !== undefined) return arg[key];
  if (typeof arg === 'string' && arg.length > 0 && (key === 'op' || key === 'view' || key === 'tab' || key === 'index')) {
    return arg;
  }
  if (ctx && ctx.event && ctx.event.arg !== undefined && (key === 'op' || key === 'view' || key === 'tab' || key === 'index')) {
    return ctx.event.arg;
  }
  return '';
}

function utf8ToBytes(str) {
  const s = String(str || '');
  const out = [];
  for (let i = 0; i < s.length; i++) {
    let code = s.charCodeAt(i);
    if (code >= 0xd800 && code <= 0xdbff && i + 1 < s.length) {
      const next = s.charCodeAt(i + 1);
      if (next >= 0xdc00 && next <= 0xdfff) {
        const cp = 0x10000 + ((code - 0xd800) << 10) + (next - 0xdc00);
        out.push(0xf0 | (cp >> 18), 0x80 | ((cp >> 12) & 0x3f), 0x80 | ((cp >> 6) & 0x3f), 0x80 | (cp & 0x3f));
        i++;
        continue;
      }
      out.push(0xef, 0xbf, 0xbd);
      continue;
    }
    if (code >= 0xdc00 && code <= 0xdfff) {
      out.push(0xef, 0xbf, 0xbd);
      continue;
    }
    if (code < 0x80) {
      out.push(code);
    } else if (code < 0x800) {
      out.push(0xc0 | (code >> 6), 0x80 | (code & 0x3f));
    } else {
      out.push(0xe0 | (code >> 12), 0x80 | ((code >> 6) & 0x3f), 0x80 | (code & 0x3f));
    }
  }
  return out;
}

function bytesToUtf8(bytes) {
  let out = '';
  let valid = true;
  let i = 0;
  while (i < bytes.length) {
    const b0 = bytes[i++];
    if (b0 < 0x80) {
      out += String.fromCharCode(b0);
    } else if ((b0 & 0xe0) === 0xc0) {
      if (i >= bytes.length || (bytes[i] & 0xc0) !== 0x80) {
        valid = false;
        break;
      }
      const cp = ((b0 & 0x1f) << 6) | (bytes[i++] & 0x3f);
      if (cp < 0x80) {
        valid = false;
        break;
      }
      out += String.fromCharCode(cp);
    } else if ((b0 & 0xf0) === 0xe0) {
      if (i + 1 >= bytes.length || (bytes[i] & 0xc0) !== 0x80 || (bytes[i + 1] & 0xc0) !== 0x80) {
        valid = false;
        break;
      }
      const cp = ((b0 & 0x0f) << 12) | ((bytes[i++] & 0x3f) << 6) | (bytes[i++] & 0x3f);
      if (cp < 0x800 || (cp >= 0xd800 && cp <= 0xdfff)) {
        valid = false;
        break;
      }
      out += String.fromCharCode(cp);
    } else if ((b0 & 0xf8) === 0xf0) {
      if (i + 2 >= bytes.length || (bytes[i] & 0xc0) !== 0x80 || (bytes[i + 1] & 0xc0) !== 0x80 || (bytes[i + 2] & 0xc0) !== 0x80) {
        valid = false;
        break;
      }
      const cp = ((b0 & 0x07) << 18) | ((bytes[i++] & 0x3f) << 12) | ((bytes[i++] & 0x3f) << 6) | (bytes[i++] & 0x3f);
      if (cp < 0x10000 || cp > 0x10ffff) {
        valid = false;
        break;
      }
      const rem = cp - 0x10000;
      out += String.fromCharCode(0xd800 + (rem >> 10), 0xdc00 + (rem & 0x3ff));
    } else {
      valid = false;
      break;
    }
  }
  return { text: out, valid };
}

function decodeBase64Strict(raw) {
  let clean = String(raw || '').trim().replace(/[\r\n\s]+/g, '').replace(/-/g, '+').replace(/_/g, '/');
  if (!clean || !/^[A-Za-z0-9+/]*={0,2}$/.test(clean) || clean.length % 4 === 1) {
    return { ok: false, text: '' };
  }
  while (clean.length % 4 !== 0) clean += '=';
  try {
    const bin = atob(clean);
    const bytes = new Array(bin.length);
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    const decoded = bytesToUtf8(bytes);
    if (!decoded.valid) return { ok: false, text: '' };
    return { ok: true, text: decoded.text };
  } catch (_) {
    return { ok: false, text: '' };
  }
}

function encodeBase64Utf8(raw) {
  const bytes = utf8ToBytes(raw);
  let bin = '';
  for (let i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
  return btoa(bin);
}

function rotr(x, n) {
  return (x >>> n) | (x << (32 - n));
}

const SHA256_K = [
  0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
  0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
  0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
  0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
  0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
  0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
  0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
  0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
];

function sha256Hex(str) {
  const bytes = utf8ToBytes(str);
  const msgLen = bytes.length;
  const paddedLen = Math.ceil((msgLen + 9) / 64) * 64;
  const padded = new Array(paddedLen).fill(0);
  for (let i = 0; i < msgLen; i++) padded[i] = bytes[i];
  padded[msgLen] = 0x80;
  const bitHi = Math.floor((msgLen * 8) / 0x100000000);
  const bitLo = (msgLen * 8) >>> 0;
  padded[paddedLen - 8] = (bitHi >>> 24) & 0xff;
  padded[paddedLen - 7] = (bitHi >>> 16) & 0xff;
  padded[paddedLen - 6] = (bitHi >>> 8) & 0xff;
  padded[paddedLen - 5] = bitHi & 0xff;
  padded[paddedLen - 4] = (bitLo >>> 24) & 0xff;
  padded[paddedLen - 3] = (bitLo >>> 16) & 0xff;
  padded[paddedLen - 2] = (bitLo >>> 8) & 0xff;
  padded[paddedLen - 1] = bitLo & 0xff;

  const h = [0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19];
  const w = new Array(64);
  for (let off = 0; off < paddedLen; off += 64) {
    for (let i = 0; i < 16; i++) {
      const p = off + i * 4;
      w[i] = ((padded[p] << 24) | (padded[p + 1] << 16) | (padded[p + 2] << 8) | padded[p + 3]) | 0;
    }
    for (let i = 16; i < 64; i++) {
      const s0 = rotr(w[i - 15], 7) ^ rotr(w[i - 15], 18) ^ (w[i - 15] >>> 3);
      const s1 = rotr(w[i - 2], 17) ^ rotr(w[i - 2], 19) ^ (w[i - 2] >>> 10);
      w[i] = (w[i - 16] + s0 + w[i - 7] + s1) | 0;
    }
    let [a, b, c, d, e, f, g, hh] = h;
    for (let i = 0; i < 64; i++) {
      const S1 = rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25);
      const ch = (e & f) ^ ((~e) & g);
      const temp1 = (hh + S1 + ch + SHA256_K[i] + w[i]) | 0;
      const S0 = rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22);
      const maj = (a & b) ^ (a & c) ^ (b & c);
      const temp2 = (S0 + maj) | 0;
      hh = g; g = f; f = e; e = (d + temp1) | 0;
      d = c; c = b; b = a; a = (temp1 + temp2) | 0;
    }
    h[0] = (h[0] + a) | 0; h[1] = (h[1] + b) | 0; h[2] = (h[2] + c) | 0; h[3] = (h[3] + d) | 0;
    h[4] = (h[4] + e) | 0; h[5] = (h[5] + f) | 0; h[6] = (h[6] + g) | 0; h[7] = (h[7] + hh) | 0;
  }
  return h.map((v) => (v >>> 0).toString(16).padStart(8, '0')).join('');
}

const MD5_S = [
  7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
  5,  9, 14, 20, 5,  9, 14, 20, 5,  9, 14, 20, 5,  9, 14, 20,
  4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
  6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21
];

const MD5_K = Array.from({ length: 64 }, (_, i) => Math.floor(Math.abs(Math.sin(i + 1)) * 0x100000000) >>> 0);

function rotl(x, n) {
  return (x << n) | (x >>> (32 - n));
}

function md5Hex(str) {
  const bytes = utf8ToBytes(str);
  const msgLen = bytes.length;
  const paddedLen = Math.ceil((msgLen + 9) / 64) * 64;
  const padded = new Array(paddedLen).fill(0);
  for (let i = 0; i < msgLen; i++) padded[i] = bytes[i];
  padded[msgLen] = 0x80;
  const bitHi = Math.floor((msgLen * 8) / 0x100000000);
  const bitLo = (msgLen * 8) >>> 0;
  padded[paddedLen - 8] = bitLo & 0xff;
  padded[paddedLen - 7] = (bitLo >>> 8) & 0xff;
  padded[paddedLen - 6] = (bitLo >>> 16) & 0xff;
  padded[paddedLen - 5] = (bitLo >>> 24) & 0xff;
  padded[paddedLen - 4] = bitHi & 0xff;
  padded[paddedLen - 3] = (bitHi >>> 8) & 0xff;
  padded[paddedLen - 2] = (bitHi >>> 16) & 0xff;
  padded[paddedLen - 1] = (bitHi >>> 24) & 0xff;

  let a0 = 0x67452301 | 0;
  let b0 = 0xefcdab89 | 0;
  let c0 = 0x98badcfe | 0;
  let d0 = 0x10325476 | 0;
  const M = new Array(16);

  for (let off = 0; off < paddedLen; off += 64) {
    for (let i = 0; i < 16; i++) {
      const p = off + i * 4;
      M[i] = (padded[p] | (padded[p + 1] << 8) | (padded[p + 2] << 16) | (padded[p + 3] << 24)) | 0;
    }
    let A = a0, B = b0, C = c0, D = d0;
    for (let i = 0; i < 64; i++) {
      let F, g;
      if (i < 16) {
        F = (B & C) | ((~B) & D);
        g = i;
      } else if (i < 32) {
        F = (D & B) | ((~D) & C);
        g = (5 * i + 1) % 16;
      } else if (i < 48) {
        F = B ^ C ^ D;
        g = (3 * i + 5) % 16;
      } else {
        F = C ^ (B | (~D));
        g = (7 * i) % 16;
      }
      F = (F + A + MD5_K[i] + M[g]) | 0;
      A = D;
      D = C;
      C = B;
      B = (B + rotl(F, MD5_S[i])) | 0;
    }
    a0 = (a0 + A) | 0; b0 = (b0 + B) | 0; c0 = (c0 + C) | 0; d0 = (d0 + D) | 0;
  }

  return [a0, b0, c0, d0]
    .map((w) => {
      let hex = '';
      for (let i = 0; i < 4; i++) hex += ((w >>> (i * 8)) & 0xff).toString(16).padStart(2, '0');
      return hex;
    })
    .join('');
}

function nextRandomBytes(nowMs) {
  _uuidSeq = (_uuidSeq + 1) & 0xffff;
  const bytes = new Array(16);
  const mix = ((Number(nowMs) || Date.now()) + _uuidSeq * 7919) >>> 0;
  for (let i = 0; i < 16; i++) {
    const r = Math.floor(Math.random() * 256);
    bytes[i] = (r ^ ((mix >>> ((i % 4) * 8)) & 0xff) ^ ((i === 15 ? _uuidSeq : 0) & 0xff)) & 0xff;
  }
  return bytes;
}

function formatUuidBytes(b, upper, nodash) {
  const hex = b.map((v) => v.toString(16).padStart(2, '0')).join('');
  const formatted = nodash
    ? hex
    : `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
  return upper ? formatted.toUpperCase() : formatted.toLowerCase();
}

function genUuidV4(nowMs, upper, nodash) {
  const b = nextRandomBytes(nowMs);
  b[6] = (b[6] & 0x0f) | 0x40;
  b[8] = (b[8] & 0x3f) | 0x80;
  return formatUuidBytes(b, upper, nodash);
}

function genUuidV7(nowMs, upper, nodash) {
  const ms = Math.max(0, Math.floor(Number(nowMs) || Date.now()));
  const b = nextRandomBytes(ms);
  b[0] = Math.floor(ms / 0x10000000000) & 0xff;
  b[1] = Math.floor(ms / 0x100000000) & 0xff;
  b[2] = (ms >>> 24) & 0xff;
  b[3] = (ms >>> 16) & 0xff;
  b[4] = (ms >>> 8) & 0xff;
  b[5] = ms & 0xff;
  b[6] = (b[6] & 0x0f) | 0x70;
  b[8] = (b[8] & 0x3f) | 0x80;
  return formatUuidBytes(b, upper, nodash);
}

function tryParseJson(raw) {
  try {
    const val = JSON.parse(raw);
    return { ok: true, value: val };
  } catch (err) {
    return { ok: false, error: err && err.message ? String(err.message) : '' };
  }
}

function locateJsonError(src) {
  const s = String(src || '');
  const n = s.length;
  let pos = 0;
  let line = 1;
  let col = 1;

  function adv(count = 1) {
    for (let k = 0; k < count && pos < n; k++) {
      const ch = s[pos++];
      if (ch === '\n') {
        line++;
        col = 1;
      } else {
        col++;
      }
    }
  }

  function skipWs() {
    while (pos < n) {
      const ch = s[pos];
      if (ch === ' ' || ch === '\t' || ch === '\n' || ch === '\r') {
        adv(1);
      } else {
        break;
      }
    }
  }

  function fail(code) {
    return { line, col, offset: Math.min(pos, Math.max(0, n - 1)), code };
  }

  function parseString() {
    if (s[pos] === "'") return fail('errJsonSingleQuote');
    if (s[pos] !== '"') return fail('errJsonExpectedKey');
    adv(1);
    while (pos < n) {
      const ch = s[pos];
      if (ch === '"') {
        adv(1);
        return null;
      }
      if (ch === '\\') {
        adv(1);
        if (pos >= n) return fail('errJsonUnterminatedStr');
        const esc = s[pos];
        if (esc === 'u') {
          adv(1);
          for (let h = 0; h < 4; h++) {
            if (pos >= n || !/[0-9a-fA-F]/.test(s[pos])) return fail('errJsonBadEscape');
            adv(1);
          }
        } else if ('"\\/bfnrt'.includes(esc)) {
          adv(1);
        } else {
          return fail('errJsonBadEscape');
        }
        continue;
      }
      if (ch.charCodeAt(0) < 0x20) return fail('errJsonUnterminatedStr');
      adv(1);
    }
    return fail('errJsonUnterminatedStr');
  }

  function parseNumber() {
    if (s[pos] === '-') adv(1);
    if (pos >= n || !/[0-9]/.test(s[pos])) return fail('errJsonSyntax');
    if (s[pos] === '0') {
      adv(1);
    } else {
      while (pos < n && /[0-9]/.test(s[pos])) adv(1);
    }
    if (pos < n && s[pos] === '.') {
      adv(1);
      if (pos >= n || !/[0-9]/.test(s[pos])) return fail('errJsonSyntax');
      while (pos < n && /[0-9]/.test(s[pos])) adv(1);
    }
    if (pos < n && (s[pos] === 'e' || s[pos] === 'E')) {
      adv(1);
      if (pos < n && (s[pos] === '+' || s[pos] === '-')) adv(1);
      if (pos >= n || !/[0-9]/.test(s[pos])) return fail('errJsonSyntax');
      while (pos < n && /[0-9]/.test(s[pos])) adv(1);
    }
    return null;
  }

  function parseValue() {
    skipWs();
    if (pos >= n) return fail('errJsonUnexpectedEof');
    const ch = s[pos];
    if (ch === '{') {
      adv(1);
      skipWs();
      if (pos >= n) return fail('errJsonUnexpectedEof');
      if (s[pos] === '}') { adv(1); return null; }
      while (true) {
        skipWs();
        if (pos >= n) return fail('errJsonUnexpectedEof');
        if (s[pos] === '}' || s[pos] === ']') return fail('errJsonTrailingComma');
        const kErr = parseString();
        if (kErr) return kErr;
        skipWs();
        if (pos >= n || s[pos] !== ':') return fail('errJsonMissingColon');
        adv(1);
        const vErr = parseValue();
        if (vErr) return vErr;
        skipWs();
        if (pos >= n) return fail('errJsonUnexpectedEof');
        if (s[pos] === ',') { adv(1); continue; }
        if (s[pos] === '}') { adv(1); return null; }
        return fail('errJsonExpectedComma');
      }
    }
    if (ch === '[') {
      adv(1);
      skipWs();
      if (pos >= n) return fail('errJsonUnexpectedEof');
      if (s[pos] === ']') { adv(1); return null; }
      while (true) {
        skipWs();
        if (pos >= n) return fail('errJsonUnexpectedEof');
        if (s[pos] === ']' || s[pos] === '}') return fail('errJsonTrailingComma');
        const vErr = parseValue();
        if (vErr) return vErr;
        skipWs();
        if (pos >= n) return fail('errJsonUnexpectedEof');
        if (s[pos] === ',') { adv(1); continue; }
        if (s[pos] === ']') { adv(1); return null; }
        return fail('errJsonExpectedComma');
      }
    }
    if (ch === '"') return parseString();
    if (ch === "'") return fail('errJsonSingleQuote');
    if (ch === '-' || /[0-9]/.test(ch)) return parseNumber();
    for (const kw of ['true', 'false', 'null']) {
      if (s.startsWith(kw, pos)) {
        adv(kw.length);
        return null;
      }
    }
    if (s.startsWith('//', pos) || s.startsWith('/*', pos)) return fail('errJsonComment');
    return fail('errJsonSyntax');
  }

  const rootErr = parseValue();
  if (rootErr) return rootErr;
  skipWs();
  if (pos < n) return fail('errJsonTrailingToken');
  return { line, col, offset: Math.max(0, pos - 1), code: 'errJsonSyntax' };
}

function parseHexColor(raw) {
  const s = String(raw || '').trim();
  const m = /^#([0-9a-fA-F]{3}|[0-9a-fA-F]{4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$/.exec(s);
  if (!m) return null;
  const h = m[1];
  let a = 255, r = 0, g = 0, b = 0;
  if (h.length === 3 || h.length === 4) {
    r = parseInt(h[0] + h[0], 16);
    g = parseInt(h[1] + h[1], 16);
    b = parseInt(h[2] + h[2], 16);
    if (h.length === 4) a = parseInt(h[3] + h[3], 16);
  } else if (h.length === 6) {
    r = parseInt(h.slice(0, 2), 16);
    g = parseInt(h.slice(2, 4), 16);
    b = parseInt(h.slice(4, 6), 16);
  } else {
    r = parseInt(h.slice(0, 2), 16);
    g = parseInt(h.slice(2, 4), 16);
    b = parseInt(h.slice(4, 6), 16);
    a = parseInt(h.slice(6, 8), 16);
  }
  const hex6 = `#${r.toString(16).padStart(2, '0')}${g.toString(16).padStart(2, '0')}${b.toString(16).padStart(2, '0')}`.toUpperCase();
  const hexDisplay = (h.length === 4 || h.length === 8)
    ? `${hex6}${a.toString(16).padStart(2, '0').toUpperCase()}`
    : hex6;
  const rgbStr = a < 255
    ? `rgba(${r}, ${g}, ${b}, ${(a / 255).toFixed(2).replace(/\.?0+$/, '')})`
    : `rgb(${r}, ${g}, ${b})`;

  const rf = r / 255, gf = g / 255, bf = b / 255;
  const max = Math.max(rf, gf, bf), min = Math.min(rf, gf, bf);
  const l = (max + min) / 2;
  let hue = 0, sat = 0;
  if (max !== min) {
    const d = max - min;
    sat = l > 0.5 ? d / (2 - max - min) : d / (max + min);
    if (max === rf) hue = (gf - bf) / d + (gf < bf ? 6 : 0);
    else if (max === gf) hue = (bf - rf) / d + 2;
    else hue = (rf - gf) / d + 4;
    hue /= 6;
  }
  const hslStr = `hsl(${Math.round(hue * 360)}, ${Math.round(sat * 100)}%, ${Math.round(l * 100)}%)`;
  const argbStr = `Color.FromArgb(${a}, ${r}, ${g}, ${b})`;
  return { hex: hexDisplay, swatchHex: hex6, rgb: rgbStr, hsl: hslStr, argb: argbStr };
}

function parseTimestampMs(raw) {
  const s = String(raw || '').trim();
  if (!/^-?\d{10,13}$/.test(s)) return null;
  const num = Number(s);
  if (!Number.isFinite(num)) return null;
  if (s.length <= 10 || (s[0] === '-' && s.length <= 11)) {
    return num * 1000;
  }
  return num;
}

function parseDateMs(raw) {
  const s = String(raw || '').trim();
  if (!s) return null;
  if (/^-?\d{10,13}$/.test(s)) return parseTimestampMs(s);
  const normalized = /^\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}(?::\d{2})?$/.test(s) ? s.replace(' ', 'T') : s;
  const parsed = Date.parse(normalized);
  return Number.isFinite(parsed) ? parsed : null;
}

function formatLocalDateTime(ms) {
  const d = new Date(ms);
  return `${pad4(d.getFullYear())}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())} ${pad2(d.getHours())}:${pad2(d.getMinutes())}:${pad2(d.getSeconds())}`;
}

function parseJwtParts(raw, indentSpaces) {
  const s = String(raw || '').trim();
  const parts = s.split('.');
  if (parts.length !== 3 || !parts[0] || !parts[1]) return null;
  if (!/^[A-Za-z0-9_-]+$/.test(parts[0]) || !/^[A-Za-z0-9_-]+$/.test(parts[1]) || !/^[A-Za-z0-9_-]*$/.test(parts[2])) {
    return null;
  }
  const hDec = decodeBase64Strict(parts[0]);
  const pDec = decodeBase64Strict(parts[1]);
  if (!hDec.ok || !pDec.ok) return null;
  const hJson = tryParseJson(hDec.text);
  const pJson = tryParseJson(pDec.text);
  if (!hJson.ok || !pJson.ok || typeof hJson.value !== 'object' || typeof pJson.value !== 'object') {
    return null;
  }
  const indent = indentSpaces === 4 ? 4 : 2;
  return {
    token: s,
    headerObj: hJson.value,
    payloadObj: pJson.value,
    headerPretty: JSON.stringify(hJson.value, null, indent),
    payloadPretty: JSON.stringify(pJson.value, null, indent)
  };
}

function formatDurationCompact(ctx, ms) {
  const sec = Math.max(0, Math.floor(ms / 1000));
  const days = Math.floor(sec / 86400);
  const hrs = Math.floor((sec % 86400) / 3600);
  const mins = Math.floor((sec % 3600) / 60);
  if (days > 0) return formatVars(ctx.t('durDaysHours'), { d: days, h: hrs });
  if (hrs > 0) return formatVars(ctx.t('durHoursMins'), { h: hrs, m: mins });
  return formatVars(ctx.t('durMins'), { m: Math.max(1, mins) });
}

function classifyText(raw) {
  const s = String(raw || '').trim();
  if (!s) return 'empty';
  if (parseHexColor(s)) return 'hexColor';
  if (parseJwtParts(s, 2)) return 'jwt';
  if ((s[0] === '{' || s[0] === '[')) {
    return 'json';
  }
  if (/\\u[0-9a-fA-F]{4}/.test(s)) return 'unicodeEscape';
  if (/^-?\d{10}$/.test(s) || /^-?\d{13}$/.test(s)) {
    const n = Number(s);
    if (Math.abs(n) >= 100000000) return 'timestamp';
  }
  if (/^https?:\/\/\S+$/i.test(s)) return 'url';
  if (/%[0-9a-fA-F]{2}/i.test(s)) {
    try {
      if (decodeURIComponent(s.replace(/\+/g, ' ')) !== s) return 'urlEncoded';
    } catch (_) {
    }
  }
  if (s.length >= 8 && s.length % 4 === 0 && /^[A-Za-z0-9+/]+={0,2}$/.test(s)) {
    const d = decodeBase64Strict(s);
    if (d.ok && !/[\x00-\x08\x0b\x0c\x0e-\x1f]/.test(d.text)) return 'base64';
  }
  if (/^-?\d+(?:\.\d+)?$/.test(s)) return 'number';
  return 'text';
}

function kindToLabel(ctx, kind) {
  switch (kind) {
    case 'json': return ctx.t('kindJson');
    case 'jwt': return ctx.t('kindJwt');
    case 'base64': return ctx.t('kindBase64');
    case 'timestamp': return ctx.t('kindTimestamp');
    case 'hexColor': return ctx.t('kindHexColor');
    case 'urlEncoded': return ctx.t('kindUrlEncoded');
    case 'unicodeEscape': return ctx.t('kindUnicode');
    case 'url': return ctx.t('kindUrl');
    case 'number': return ctx.t('kindNumber');
    default: return ctx.t('kindPlainText');
  }
}

function kindToBadgeShort(kind) {
  switch (kind) {
    case 'json': return 'JSON';
    case 'jwt': return 'JWT';
    case 'base64': return 'Base64';
    case 'timestamp': return 'TS';
    case 'hexColor': return 'HEX';
    case 'urlEncoded': return 'URL%';
    case 'unicodeEscape': return '\\u';
    case 'url': return 'URL';
    default: return 'TXT';
  }
}

function kindToPrimaryOp(kind) {
  switch (kind) {
    case 'json': return 'jsonPretty';
    case 'jwt': return 'jwtDecode';
    case 'base64': return 'base64Decode';
    case 'urlEncoded': return 'urlDecode';
    case 'unicodeEscape': return 'unicodeUnescape';
    case 'timestamp': return 'tsToDate';
    case 'hexColor': return 'hexInspect';
    default: return 'wordCount';
  }
}

function isStructuredKind(kind) {
  return kind === 'json' ||
    kind === 'jwt' ||
    kind === 'base64' ||
    kind === 'urlEncoded' ||
    kind === 'unicodeEscape' ||
    kind === 'timestamp' ||
    kind === 'hexColor';
}

function parseHistory(state) {
  try {
    const arr = JSON.parse(state.historyJson || '[]');
    return Array.isArray(arr) ? arr : [];
  } catch (_) {
    return [];
  }
}

function saveHistory(state, arr) {
  state.historyJson = JSON.stringify(arr.slice(-6));
}

function snapshotCurrent(ctx) {
  const state = ctx.state || {};
  return {
    text: String(state.draft || ''),
    label: String(state.stepLabel || '') || ctx.t('stepInput'),
    tab: String(state.activeTab || 'json'),
    jwtMode: String(state.jwtMode || 'false'),
    jwtView: String(state.jwtView || 'payload'),
    jwtToken: String(state.jwtToken || ''),
    structKind: String(state.structKind || ''),
    kvUnit: String(state.kvUnit || 's')
  };
}

function pushHistory(ctx) {
  const state = ctx.state || {};
  const draft = String(state.draft || '');
  if (!draft) return;
  const arr = parseHistory(state);
  const snap = snapshotCurrent(ctx);
  if (arr.length > 0 && arr[arr.length - 1].text === snap.text && arr[arr.length - 1].label === snap.label) {
    return;
  }
  arr.push(snap);
  saveHistory(state, arr);
}

function resolveAutoCopyEnabled(ctx) {
  const ov = String((ctx.state && ctx.state.autoCopyOverride) || '');
  if (ov === 'true') return true;
  if (ov === 'false') return false;
  return String((ctx.settings && ctx.settings.autoCopyResult) ?? 'true') !== 'false';
}

function performCopy(ctx, text) {
  const s = String(text || '');
  if (!s) return;
  ctx.state.lastSelfCopied = s;
  ctx.ui.copy(s);
  const seq = parseInt(ctx.state.copySeq || '0', 10) || 0;
  ctx.state.copySeq = String(seq + 1);
}

function clearErrors(state) {
  state.errorMsg = '';
  state.errorOffset = '-1';
}

function bumpError(state, msg, offset) {
  state.errorMsg = String(msg || '');
  state.errorOffset = String(typeof offset === 'number' && offset >= 0 ? offset : -1);
  const seq = parseInt(state.errorSeq || '0', 10) || 0;
  state.errorSeq = String(seq + 1);
}

function unicodeUnescapeText(str) {
  return String(str || '').replace(/\\u([0-9a-fA-F]{4})/g, (_, h) => String.fromCharCode(parseInt(h, 16)));
}

function unicodeEscapeText(str) {
  let out = '';
  for (let i = 0; i < str.length; i++) {
    const code = str.charCodeAt(i);
    out += code > 0x7f ? '\\u' + code.toString(16).padStart(4, '0') : str[i];
  }
  return out;
}

function jsonStringEscape(str) {
  return JSON.stringify(String(str || ''))
    .slice(1, -1);
}

function jsonStringUnescape(str) {
  const trimmed = String(str || '').trim();
  if (trimmed.startsWith('"') && trimmed.endsWith('"')) {
    const p = tryParseJson(trimmed);
    if (p.ok && typeof p.value === 'string') return p.value;
  }
  const wrapped = `"${String(str || '').replace(/\\(.)|"/g, (m, g) => (g !== undefined ? '\\' + g : '\\"'))}"`;
  const p2 = tryParseJson(wrapped);
  if (p2.ok && typeof p2.value === 'string') return p2.value;
  return null;
}

const OP_LABEL_KEYS = {
  jsonPretty: 'actJsonPretty',
  jsonMinify: 'actJsonMinify',
  unicodeUnescape: 'actUnicodeDecode',
  jwtDecode: 'actJwtDecode',
  jwtPayload: 'actJwtPayload',
  jwtHeader: 'actJwtHeader',
  base64Encode: 'actB64Encode',
  base64Decode: 'actB64Decode',
  urlEncode: 'actUrlEncode',
  urlDecode: 'actUrlDecode',
  tsToDate: 'actTsToDate',
  uuidv7: 'actUuidV7',
  sha256: 'actSha256',
  wordCount: 'actStats',
  wordStats: 'actWordStats',
  cleanWhitespace: 'actCleanSpace',
  toggleCase: 'actCaseToggle',
  dedupeLines: 'actDedupeLines',
  smartTime: 'actSmartTime',
  genPassword: 'actGenPassword',
  hexInspect: 'actHexInspect'
};

function opStepLabel(ctx, op) {
  return ctx.t(OP_LABEL_KEYS[op] || 'stepTransformed');
}

function buildStructuredRows(ctx, kind, rawDraft) {
  if (kind === 'timestamp') {
    const ms = parseDateMs(rawDraft);
    if (ms === null) return null;
    const sec = Math.trunc(ms / 1000);
    return {
      hasSwatch: 'false',
      swatchHex: '',
      canToggle: 'true',
      hasRow3: 'true',
      rows: [
        { label: 'Unix (s)', value: String(sec) },
        { label: 'Unix (ms)', value: String(ms) },
        { label: ctx.t('kvLocalTime'), value: formatLocalDateTime(ms) },
        { label: 'ISO 8601 (UTC)', value: new Date(ms).toISOString() }
      ]
    };
  }
  if (kind === 'hexColor') {
    const c = parseHexColor(rawDraft);
    if (!c) return null;
    return {
      hasSwatch: 'true',
      swatchHex: c.swatchHex,
      canToggle: 'false',
      hasRow3: 'true',
      rows: [
        { label: 'HEX', value: c.hex },
        { label: 'RGB', value: c.rgb },
        { label: 'HSL', value: c.hsl },
        { label: 'WinUI ARGB', value: c.argb }
      ]
    };
  }
  return null;
}

function buildTextStatsReport(ctx, src) {
  const s = String(src || '');
  const bytes = utf8ToBytes(s).length;
  const lines = s.length === 0 ? 0 : s.split(/\r?\n/).length;
  const words = s.trim() ? s.trim().split(/\s+/).length : 0;
  return [
    `${ctx.t('statChars')}: ${s.length}`,
    `${ctx.t('statBytes')}: ${bytes} B`,
    `${ctx.t('statLines')}: ${lines}`,
    `${ctx.t('statWords')}: ${words}`
  ].join('\n');
}

function executeOpOnCurrent(ctx, op, rawInput, triggerAutoCopy) {
  const state = ctx.state || {};
  const settings = ctx.settings || {};
  const nowMs = (ctx.env && ctx.env.nowMs) || Date.now();
  const indent = String(settings.jsonIndent || '2') === '4' ? 4 : 2;
  const uuidUpper = String(settings.uuidCase || 'lower') === 'upper';

  const requiresInput =
    op !== 'nowSec' &&
    op !== 'nowMs' &&
    op !== 'uuidv7' &&
    op !== 'uuidv4' &&
    op !== 'uuidNodash' && op !== 'genPassword' && !(op === 'smartTime' && !String(rawInput || '').trim());

  if (requiresInput && !String(rawInput || '').trim() && op !== 'wordCount') {
    bumpError(state, ctx.t('errEmptyInput'), -1);
    projectComputed(ctx);
    return false;
  }

  let nextText = rawInput;
  let nextJwtMode = 'false';
  let nextJwtView = 'payload';
  let nextJwtToken = '';
  let nextJwtHeader = '';
  let nextJwtPayload = '';
  let nextStructKind = '';
  let nextKvUnit = state.kvUnit || 's';

  if (op === 'jsonPretty' || op === 'jsonMinify') {
    const parsed = tryParseJson(rawInput);
    if (!parsed.ok) {
      const loc = locateJsonError(rawInput);
      const reason = ctx.t(loc.code);
      bumpError(state, formatVars(ctx.t('errJsonLocFmt'), { line: loc.line, col: loc.col, reason }), loc.offset);
      projectComputed(ctx);
      return false;
    }
    nextText = op === 'jsonPretty'
      ? JSON.stringify(parsed.value, null, indent)
      : JSON.stringify(parsed.value);
  } else if (op === 'unicodeUnescape') {
    nextText = unicodeUnescapeText(rawInput);
  } else if (op === 'unicodeEscape') {
    nextText = unicodeEscapeText(rawInput);
  } else if (op === 'jsonEscape') {
    nextText = jsonStringEscape(rawInput);
  } else if (op === 'jsonUnescape') {
    const unescaped = jsonStringUnescape(rawInput);
    if (unescaped === null) {
      bumpError(state, ctx.t('errJsonUnescape'), -1);
      projectComputed(ctx);
      return false;
    }
    nextText = unescaped;
  } else if (op === 'jwtDecode' || op === 'jwtPayload' || op === 'jwtHeader') {
    const tokenSrc = (state.jwtMode === 'true' && state.jwtToken) ? state.jwtToken : rawInput;
    const jwt = parseJwtParts(tokenSrc, indent);
    if (!jwt) {
      bumpError(state, ctx.t('errInvalidJwt'), -1);
      projectComputed(ctx);
      return false;
    }
    nextJwtMode = 'true';
    nextJwtView = op === 'jwtHeader' ? 'header' : 'payload';
    nextJwtToken = jwt.token;
    nextJwtHeader = jwt.headerPretty;
    nextJwtPayload = jwt.payloadPretty;
    nextText = nextJwtView === 'header' ? jwt.headerPretty : jwt.payloadPretty;
  } else if (op === 'base64Encode') {
    nextText = encodeBase64Utf8(rawInput);
  } else if (op === 'base64Decode') {
    const dec = decodeBase64Strict(rawInput);
    if (!dec.ok) {
      bumpError(state, ctx.t('errInvalidBase64'), -1);
      projectComputed(ctx);
      return false;
    }
    nextText = dec.text;
  } else if (op === 'urlEncode') {
    nextText = encodeURIComponent(rawInput);
  } else if (op === 'urlDecode') {
    try {
      nextText = decodeURIComponent(String(rawInput).replace(/\+/g, ' '));
    } catch (_) {
      bumpError(state, ctx.t('errInvalidUrlEnc'), -1);
      projectComputed(ctx);
      return false;
    }
  } else if (op === 'htmlEncode') {
    nextText = String(rawInput)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  } else if (op === 'htmlDecode') {
    nextText = String(rawInput)
      .replace(/&quot;/g, '"')
      .replace(/&#39;|&apos;/g, "'")
      .replace(/&lt;/g, '<')
      .replace(/&gt;/g, '>')
      .replace(/&amp;/g, '&');
  } else if (op === 'nowSec') {
    const sec = Math.floor(nowMs / 1000);
    nextText = String(sec);
    nextStructKind = 'timestamp';
    nextKvUnit = 's';
  } else if (op === 'nowMs') {
    nextText = String(Math.floor(nowMs));
    nextStructKind = 'timestamp';
    nextKvUnit = 'ms';
  } else if (op === 'tsToDate') {
    const ms = parseTimestampMs(rawInput);
    if (ms === null) {
      bumpError(state, ctx.t('errInvalidTimestamp'), -1);
      projectComputed(ctx);
      return false;
    }
    nextText = formatLocalDateTime(ms);
    nextStructKind = 'timestamp';
  } else if (op === 'dateToTs') {
    const ms = parseDateMs(rawInput);
    if (ms === null) {
      bumpError(state, ctx.t('errInvalidDate'), -1);
      projectComputed(ctx);
      return false;
    }
    const useMs = nextKvUnit === 'ms';
    nextText = String(useMs ? ms : Math.floor(ms / 1000));
    nextStructKind = 'timestamp';
  } else if (op === 'hexInspect') {
    const c = parseHexColor(rawInput);
    if (!c) {
      bumpError(state, ctx.t('errInvalidHex'), -1);
      projectComputed(ctx);
      return false;
    }
    nextText = c.hex;
    nextStructKind = 'hexColor';
  } else if (op === 'uuidv7') {
    nextText = genUuidV7(nowMs, uuidUpper, false);
  } else if (op === 'uuidv4') {
    nextText = genUuidV4(nowMs, uuidUpper, false);
  } else if (op === 'uuidNodash') {
    const trimmed = String(rawInput || '').trim();
    if (/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(trimmed)) {
      const stripped = trimmed.replace(/-/g, '');
      nextText = uuidUpper ? stripped.toUpperCase() : stripped.toLowerCase();
    } else {
      nextText = genUuidV7(nowMs, uuidUpper, true);
    }
  } else if (op === 'sha256') {
    nextText = sha256Hex(rawInput);
  } else if (op === 'md5') {
    nextText = md5Hex(rawInput);
  } else if (op === 'wordCount' || op === 'wordStats') {
    nextText = buildTextStatsReport(ctx, rawInput);
  } else if (op === 'cleanWhitespace') {
    nextText = String(rawInput || '')
      .split(/\r?\n/)
      .map((line) => line.replace(/[ \t]+/g, ' ').trim())
      .filter((line) => line.length > 0)
      .join('\n');
  } else if (op === 'toggleCase') {
    const s = String(rawInput || '');
    nextText = /[a-z]/.test(s) ? s.toUpperCase() : s.toLowerCase();
  } else if (op === 'dedupeLines') {
    const seen = new Set();
    const kept = [];
    for (const line of String(rawInput || '').split(/\r?\n/)) {
      const t = line.trim();
      if (t && !seen.has(t)) {
        seen.add(t);
        kept.push(t);
      }
    }
    nextText = kept.join('\n');
  } else if (op === 'smartTime') {
    const trimmedTs = String(rawInput || '').trim();
    if (!trimmedTs) {
      const sec = Math.floor(nowMs / 1000);
      nextText = formatLocalDateTime(nowMs) + ' (' + String(sec) + ')';
    } else if (/^-?\d{10,13}$/.test(trimmedTs)) {
      const ms = parseTimestampMs(trimmedTs);
      if (ms === null) {
        bumpError(state, ctx.t('errInvalidTimestamp'), -1);
        projectComputed(ctx);
        return false;
      }
      nextText = formatLocalDateTime(ms);
    } else {
      const ms = parseDateMs(trimmedTs);
      if (ms === null) {
        bumpError(state, ctx.t('errInvalidDate'), -1);
        projectComputed(ctx);
        return false;
      }
      nextText = String(Math.floor(ms / 1000));
    }
  } else if (op === 'genPassword') {
    const alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%&*';
    const rnd = nextRandomBytes(nowMs);
    let pwd = '';
    for (let i = 0; i < 16; i++) {
      pwd += alphabet[rnd[i] % alphabet.length];
    }
    nextText = pwd;
  }

  const prevDraft = String(state.draft || '');
  if (prevDraft && (prevDraft !== nextText || op === 'jwtDecode' || op === 'tsToDate' || op === 'hexInspect')) {
    pushHistory(ctx);
  }

  state.draft = nextText;
  state.editing = 'false';
  state.stepLabel = opStepLabel(ctx, op);
  state.jwtMode = nextJwtMode;
  state.jwtView = nextJwtView;
  state.jwtToken = nextJwtToken;
  state.jwtHeaderJson = nextJwtHeader;
  state.jwtPayloadJson = nextJwtPayload;
  state.structKind = nextStructKind;
  state.kvUnit = nextKvUnit;
  clearErrors(state);

  if (triggerAutoCopy && resolveAutoCopyEnabled(ctx)) {
    performCopy(ctx, nextText);
  }

  projectComputed(ctx);
  return true;
}

function projectComputed(ctx) {
  const state = (ctx && ctx.state) || {};
  const settings = (ctx && ctx.settings) || {};
  const env = (ctx && ctx.env) || {};
  const clip = env.clipboard || {};
  const nowMs = env.nowMs || Date.now();
  const c = (ctx && ctx.computed) || (ctx.computed = {});

  const draft = String(state.draft || '');
  const hasDraft = draft.length > 0;
  const isEditing = String(state.editing || 'false') === 'true';
  const isJwtMode = String(state.jwtMode || 'false') === 'true' && Boolean(state.jwtToken);
  const jwtView = String(state.jwtView || 'payload');
  const autoCopy = resolveAutoCopyEnabled(ctx);

  // 1. Clipboard sniffer handoff (non-intrusive, loop-protected)
  const snifferOn = String(settings.enableClipboardSniffer ?? 'true') !== 'false';
  const clipRaw = String(clip.text || '');
  const clipTrimmed = clipRaw.trim();
  const clipKind = (clip.kind && clip.kind !== 'empty') ? String(clip.kind) : classifyText(clipRaw);
  const showClip = snifferOn &&
    Boolean(clip.hasText) &&
    clipTrimmed.length > 0 &&
    clipRaw !== draft &&
    clipRaw !== String(state.lastSelfCopied || '') &&
    clipRaw !== String(state.dismissedClip || '');

  c.showClipBanner = showClip ? 'true' : 'false';
  c.clipKindLabel = kindToLabel(ctx, clipKind);
  const previewSrc = String(clip.preview || clipTrimmed.replace(/[\r\n]+/g, ' '));
  c.clipPreview = previewSrc.length > 48 ? previewSrc.slice(0, 47) + '…' : previewSrc;
  c.clipLoadLabel = isStructuredKind(clipKind) ? ctx.t('clipLoadAndParse') : ctx.t('clipPasteOnly');

  // 2. JWT Live Status (against nowMs)
  c.isJwtMode = isJwtMode ? 'true' : 'false';
  c.jwtView = jwtView;
  let jwtStatusShort = '';
  let jwtBadgeColor = 'emerald';
  let jwtExpired = false;
  if (isJwtMode) {
    const indent = String(settings.jsonIndent || '2') === '4' ? 4 : 2;
    const jwt = parseJwtParts(state.jwtToken, indent);
    if (jwt) {
      const exp = jwt.payloadObj && typeof jwt.payloadObj.exp === 'number' ? jwt.payloadObj.exp : null;
      const nbf = jwt.payloadObj && typeof jwt.payloadObj.nbf === 'number' ? jwt.payloadObj.nbf : null;
      if (nbf !== null && nbf * 1000 > nowMs) {
        jwtStatusShort = formatVars(ctx.t('jwtStatusNotBefore'), { time: formatDurationCompact(ctx, nbf * 1000 - nowMs) });
        jwtBadgeColor = 'warning';
      } else if (exp !== null) {
        const diff = exp * 1000 - nowMs;
        if (diff <= 0) {
          jwtStatusShort = formatVars(ctx.t('jwtStatusExpired'), { time: formatDurationCompact(ctx, -diff) });
          jwtBadgeColor = 'danger';
          jwtExpired = true;
        } else {
          jwtStatusShort = formatVars(ctx.t('jwtStatusValidRem'), { time: formatDurationCompact(ctx, diff) });
          jwtBadgeColor = 'emerald';
        }
      } else {
        jwtStatusShort = ctx.t('jwtStatusNoExp');
        jwtBadgeColor = 'emerald';
      }
    } else {
      jwtStatusShort = ctx.t('errInvalidJwt');
      jwtBadgeColor = 'danger';
      jwtExpired = true;
    }
  }
  c.jwtStatusText = jwtStatusShort;
  c.jwtStatusColor = jwtBadgeColor;

  // 3. Breadcrumb & Undo
  const history = parseHistory(state);
  c.canUndo = history.length > 0 ? 'true' : 'false';
  c.canClear = hasDraft || Boolean(state.errorMsg) ? 'true' : 'false';

  const trailSlice = history.slice(Math.max(0, history.length - 2));
  c.hasTrail0 = trailSlice.length >= 1 ? 'true' : 'false';
  c.trailLabel0 = trailSlice.length >= 1 ? trailSlice[0].label : '';
  c.hasTrail1 = trailSlice.length >= 2 ? 'true' : 'false';
  c.trailLabel1 = trailSlice.length >= 2 ? trailSlice[1].label : '';
  c.currentStepLabel = String(state.stepLabel || '') || (hasDraft ? ctx.t('stepEdited') : ctx.t('stepInput'));

  // 4. Structured summary & Display Code States
  const structKind = String(state.structKind || '');
  const structured = (!isEditing && !isJwtMode && hasDraft && structKind)
    ? buildStructuredRows(ctx, structKind, draft)
    : null;
  c.hasDraft = hasDraft ? 'true' : 'false';
  c.canCopy = hasDraft ? 'true' : 'false';
  c.displayCode = hasDraft ? draft : ctx.t('workspacePlaceholder');
  c.displayCodeColor = hasDraft ? 'default' : 'muted';
  c.displayLanguage = (!hasDraft || (isJwtMode && jwtView === 'raw')) ? 'plain' : 'auto';

  // 6. Non-destructive Syntax Error + Bottom Metrics
  const hasError = Boolean(state.errorMsg);
  c.hasError = hasError ? 'true' : 'false';
  c.errorMsg = String(state.errorMsg || '');
  c.hasErrorOffset = (parseInt(state.errorOffset ?? '-1', 10) >= 0) ? 'true' : 'false';

  if (hasDraft) {
    const byteLen = utf8ToBytes(draft).length;
    const lineCount = draft.split(/\r?\n/).length;
    c.statusMetrics = formatVars(ctx.t('metricsFmt'), { chars: draft.length, bytes: byteLen, lines: lineCount });
  } else {
    c.statusMetrics = ctx.t('metricsEmpty');
  }

  c.autoCopyEnabled = autoCopy ? 'true' : 'false';
  c.autoCopyLabel = autoCopy ? ctx.t('autoCopyOn') : ctx.t('autoCopyOff');
  const stepText = String(state.stepLabel || '');
  c.showCopiedBadge = (!hasError && hasDraft && parseInt(state.copySeq || '0', 10) > 0) ? 'true' : 'false';
  if (hasError) {
    c.resultSummaryText = String(state.errorMsg || '');
  } else if (hasDraft && stepText) {
    c.resultSummaryText = stepText + ' · ' + c.statusMetrics;
  } else if (hasDraft) {
    c.resultSummaryText = c.statusMetrics;
  } else {
    c.resultSummaryText = ctx.t('resultIdleHint');
  }

  // 7. Dual-mode Desktop Capsule
  if (hasError) {
    c.capsuleBadge = 'ERR';
    c.capsuleBadgeWarning = 'true';
    c.capsuleSummary = String(state.errorMsg || '').slice(0, 36);
    c.capsuleActionTooltip = ctx.t('capsuleParseCopyTip');
  } else if (hasDraft) {
    const draftKind = isJwtMode ? 'jwt' : classifyText(draft);
    c.capsuleBadge = kindToBadgeShort(draftKind);
    c.capsuleBadgeWarning = jwtExpired ? 'true' : 'false';
    if (isJwtMode) {
      c.capsuleSummary = jwtStatusShort;
    } else if (structured) {
      c.capsuleSummary = `${structured.rows[2].value} (${structured.rows[0].value})`.slice(0, 38);
    } else {
      const compact = draft.trim().replace(/[\r\n]+/g, ' ');
      c.capsuleSummary = compact.length > 34 ? compact.slice(0, 33) + '…' : compact;
    }
    c.capsuleActionTooltip = ctx.t('capsuleParseCopyTip');
  } else if (showClip) {
    c.capsuleBadge = kindToBadgeShort(clipKind);
    c.capsuleBadgeWarning = 'false';
    c.capsuleSummary = c.clipPreview;
    c.capsuleActionTooltip = ctx.t('capsuleParseCopyTip');
  } else {
    c.capsuleBadge = 'UUID v7';
    c.capsuleBadgeWarning = 'false';
    c.capsuleSummary = ctx.t('capsuleIdleUuid');
    c.capsuleActionTooltip = ctx.t('capsuleGenUuidTip');
  }
}

// =========================================================================
// Exported QuickJS Lifecycle & Action Handlers
// =========================================================================

export function onInit(ctx) {
  const state = (ctx && ctx.state) || {};
  if (!state.activeTab) state.activeTab = 'daily';
  if (!state.editing) state.editing = 'false';
  if (!state.historyJson) state.historyJson = '[]';
  if (!state.jwtView) state.jwtView = 'payload';
  if (!state.kvUnit) state.kvUnit = 's';
  projectComputed(ctx);
}

export function onTick(ctx) {
  projectComputed(ctx);
}

export function onDraftChange(ctx) {
  const state = (ctx && ctx.state) || {};
  state.jwtMode = 'false';
  state.structKind = '';
  clearErrors(state);
  if (!String(state.draft || '')) {
    state.stepLabel = '';
  } else if (!state.stepLabel) {
    state.stepLabel = ctx.t('stepInput');
  }
  projectComputed(ctx);
}

export function switchTab(ctx, arg) {
  const tab = getArgVal(ctx, arg, 'tab') || (ctx.event && ctx.event.value) || 'json';
  ctx.state.activeTab = String(tab);
  projectComputed(ctx);
}

export function focusError(ctx) {
  const state = ctx.state;
  state.editing = 'true';
  const seq = parseInt(state.focusSeq || '0', 10) || 0;
  state.focusSeq = String(seq + 1);
  projectComputed(ctx);
}

export function loadClipboard(ctx) {
  const state = ctx.state || {};
  const clip = (ctx.env && ctx.env.clipboard) || {};
  const rawClip = String(clip.text || '');
  if (!rawClip.trim()) {
    projectComputed(ctx);
    return;
  }

  clearErrors(state);
  state.editing = 'false';
  state.dismissedClip = rawClip;
  state.historyJson = '[]';

  const kind = (clip.kind && clip.kind !== 'empty') ? String(clip.kind) : classifyText(rawClip);
  if (!isStructuredKind(kind)) {
    // Plain text / URL / bare numbers are pasted quietly without unsolicited formatting or errors
    state.draft = rawClip;
    state.stepLabel = ctx.t('stepPasted');
    state.jwtMode = 'false';
    state.structKind = '';
    projectComputed(ctx);
    return;
  }

  if (kind === 'json' || kind === 'unicodeEscape') {
    state.activeTab = 'json';
  } else if (kind === 'jwt' || kind === 'base64' || kind === 'urlEncoded') {
    state.activeTab = 'encode';
  } else if (kind === 'timestamp' || kind === 'hexColor') {
    state.activeTab = 'time';
  }

  state.draft = rawClip;
  state.stepLabel = ctx.t('stepClipboard');
  const op = kindToPrimaryOp(kind);
  executeOpOnCurrent(ctx, op, rawClip, false);
}

export function dismissClipboard(ctx) {
  const clip = (ctx.env && ctx.env.clipboard) || {};
  ctx.state.dismissedClip = String(clip.text || '');
  projectComputed(ctx);
}

export function runTransform(ctx, arg) {
  const op = getArgVal(ctx, arg, 'op');
  if (!op) {
    projectComputed(ctx);
    return;
  }
  const src = (ctx.state && ctx.state.draft) || '';
  executeOpOnCurrent(ctx, String(op), String(src), true);
}

export function runPrimary(ctx) {
  const state = ctx.state || {};
  const src = String(state.draft || '');
  if (!src.trim()) {
    projectComputed(ctx);
    return;
  }
  const kind = state.jwtMode === 'true' ? 'jwt' : classifyText(src);
  const op = kindToPrimaryOp(kind);
  if (op === 'wordCount' && state.activeTab === 'json') {
    executeOpOnCurrent(ctx, 'jsonPretty', src, true);
    return;
  }
  executeOpOnCurrent(ctx, op, src, true);
}

export function undoStep(ctx) {
  const state = ctx.state || {};
  const arr = parseHistory(state);
  if (arr.length === 0) {
    projectComputed(ctx);
    return;
  }
  const snap = arr.pop();
  saveHistory(state, arr);
  restoreSnapshot(ctx, snap);
}

export function jumpHistory(ctx, arg) {
  const state = ctx.state || {};
  const arr = parseHistory(state);
  if (arr.length === 0) {
    projectComputed(ctx);
    return;
  }
  const visIdx = parseInt(getArgVal(ctx, arg, 'index') || '0', 10);
  const trailStart = Math.max(0, arr.length - 2);
  const actualIdx = trailStart + (Number.isFinite(visIdx) ? visIdx : 0);
  if (actualIdx < 0 || actualIdx >= arr.length) {
    projectComputed(ctx);
    return;
  }
  const snap = arr[actualIdx];
  saveHistory(state, arr.slice(0, actualIdx));
  restoreSnapshot(ctx, snap);
}

function restoreSnapshot(ctx, snap) {
  const state = ctx.state || {};
  state.draft = snap.text || '';
  state.stepLabel = snap.label || '';
  if (snap.tab) state.activeTab = snap.tab;
  state.jwtMode = snap.jwtMode || 'false';
  state.jwtView = snap.jwtView || 'payload';
  state.jwtToken = snap.jwtToken || '';
  state.structKind = snap.structKind || '';
  state.kvUnit = snap.kvUnit || 's';
  state.editing = 'false';
  clearErrors(state);
  if (state.jwtMode === 'true' && state.jwtToken) {
    const indent = String((ctx.settings && ctx.settings.jsonIndent) || '2') === '4' ? 4 : 2;
    const jwt = parseJwtParts(state.jwtToken, indent);
    if (jwt) {
      state.jwtHeaderJson = jwt.headerPretty;
      state.jwtPayloadJson = jwt.payloadPretty;
    }
  }
  projectComputed(ctx);
}

export function clearWorkspace(ctx) {
  const state = ctx.state || {};
  state.draft = '';
  state.stepLabel = '';
  state.historyJson = '[]';
  state.editing = 'false';
  state.jwtMode = 'false';
  state.jwtView = 'payload';
  state.jwtToken = '';
  state.jwtHeaderJson = '';
  state.jwtPayloadJson = '';
  state.structKind = '';
  clearErrors(state);
  projectComputed(ctx);
}

export function setJwtView(ctx, arg) {
  const state = ctx.state || {};
  const view = getArgVal(ctx, arg, 'view') || 'payload';
  if (state.jwtMode !== 'true' || !state.jwtToken) {
    projectComputed(ctx);
    return;
  }
  const indent = String((ctx.settings && ctx.settings.jsonIndent) || '2') === '4' ? 4 : 2;
  const jwt = parseJwtParts(state.jwtToken, indent);
  if (!jwt) {
    projectComputed(ctx);
    return;
  }
  state.jwtView = view;
  if (view === 'header') {
    state.draft = jwt.headerPretty;
    state.stepLabel = ctx.t('actJwtHeader');
  } else if (view === 'raw') {
    state.draft = jwt.token;
    state.stepLabel = 'JWT';
  } else {
    state.draft = jwt.payloadPretty;
    state.stepLabel = ctx.t('actJwtPayload');
  }
  state.editing = 'false';
  clearErrors(state);
  projectComputed(ctx);
}

export function copyCurrent(ctx) {
  const state = ctx.state || {};
  const draft = String(state.draft || '');
  if (!draft) {
    projectComputed(ctx);
    return;
  }
  performCopy(ctx, draft);
  projectComputed(ctx);
}

export function toggleAutoCopy(ctx) {
  const next = !resolveAutoCopyEnabled(ctx);
  ctx.state.autoCopyOverride = next ? 'true' : 'false';
  projectComputed(ctx);
}

export function runCapsuleAction(ctx) {
  const state = ctx.state || {};
  const clip = (ctx.env && ctx.env.clipboard) || {};
  const settings = ctx.settings || {};
  const nowMs = (ctx.env && ctx.env.nowMs) || Date.now();
  const snifferOn = String(settings.enableClipboardSniffer ?? 'true') !== 'false';
  const uuidUpper = String(settings.uuidCase || 'lower') === 'upper';

  const draft = String(state.draft || '');
  if (draft.trim().length > 0) {
    const kind = state.jwtMode === 'true' ? 'jwt' : classifyText(draft);
    if (isStructuredKind(kind)) {
      if (executeOpOnCurrent(ctx, kindToPrimaryOp(kind), draft, false)) {
        performCopy(ctx, ctx.state.draft);
        projectComputed(ctx);
        return;
      }
    }
    performCopy(ctx, draft);
    projectComputed(ctx);
    return;
  }

  const clipRaw = String(clip.text || '');
  if (snifferOn && clip.hasText && clipRaw.trim().length > 0 && clipRaw !== String(state.lastSelfCopied || '')) {
    const clipKind = (clip.kind && clip.kind !== 'empty') ? String(clip.kind) : classifyText(clipRaw);
    if (isStructuredKind(clipKind)) {
      state.dismissedClip = clipRaw;
      if (executeOpOnCurrent(ctx, kindToPrimaryOp(clipKind), clipRaw, false)) {
        performCopy(ctx, ctx.state.draft);
        projectComputed(ctx);
        return;
      }
    }
    performCopy(ctx, clipRaw);
    projectComputed(ctx);
    return;
  }

  // Idle mode: generate a fresh RFC 9562 UUID v7 and copy cleanly
  const uuid = genUuidV7(nowMs, uuidUpper, false);
  state.draft = uuid;
  state.stepLabel = ctx.t('actUuidV7');
  state.jwtMode = 'false';
  state.structKind = '';
  state.editing = 'false';
  clearErrors(state);
  performCopy(ctx, uuid);
  projectComputed(ctx);
}

export function pasteClipboard(ctx) {
  const clip = (ctx.env && ctx.env.clipboard) || {};
  const rawClip = String(clip.text || '');
  if (!rawClip) {
    projectComputed(ctx);
    return;
  }
  clearErrors(ctx.state);
  ctx.state.draft = rawClip;
  ctx.state.stepLabel = ctx.t('stepPasted');
  ctx.state.jwtMode = 'false';
  ctx.state.structKind = '';
  projectComputed(ctx);
}
