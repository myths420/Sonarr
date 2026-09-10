'use strict';

// Pull an English WebVTT subtitle track out of an HLS master manifest.
//
// Dailymotion and Rumble both list subtitle renditions in the master as
//   #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="...",NAME="English",LANGUAGE="en",URI="subs.m3u8"
// The .m3u8 it points at is a normal media playlist whose segments are
// WebVTT. We download those, stitch them into one .vtt, and hand the
// local file back so the caller can mux it in as a mov_text track.
//
// Best-effort: many donghua uploads are hard-subbed and carry no
// subtitle rendition at all -- callers treat a null return as "no subs".

const fs = require('fs');
const path = require('path');

const UA =
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 ' +
  '(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36';

async function getText(url, referer) {
  const headers = { 'User-Agent': UA, Accept: '*/*' };
  if (referer) headers.Referer = referer;
  const r = await fetch(url, { headers, redirect: 'follow' });
  if (!r.ok) throw new Error('GET ' + url + ' -> ' + r.status);
  return r.text();
}

// -> absolute URI of the best English subtitle playlist, or null.
function pickSubtitleUri(masterBody, masterUrl) {
  const entries = [];
  for (const line of masterBody.split('\n')) {
    if (!/^#EXT-X-MEDIA:/i.test(line) || !/TYPE=SUBTITLES/i.test(line)) continue;
    const uri = (line.match(/URI="([^"]+)"/i) || [])[1];
    if (!uri) continue;
    const lang = (line.match(/LANGUAGE="([^"]*)"/i) || [])[1] || '';
    const name = (line.match(/NAME="([^"]*)"/i) || [])[1] || '';
    const isEnglish = /^en/i.test(lang) || /\beng(?:lish)?\b/i.test(name);
    const isDefault = /DEFAULT=YES/i.test(line);
    entries.push({ uri, isEnglish, isDefault });
  }
  if (entries.length === 0) return null;

  const chosen =
    entries.find((e) => e.isEnglish && e.isDefault) ||
    entries.find((e) => e.isEnglish) ||
    entries.find((e) => e.isDefault) ||
    entries[0];

  return /^https?:/.test(chosen.uri) ? chosen.uri : new URL(chosen.uri, masterUrl).href;
}

// Strip a leading "WEBVTT ...\n\n" header (and any X-TIMESTAMP-MAP /
// NOTE / STYLE / REGION preamble) so segments concatenate into one cue
// list. Donghua subs are plain cues, so a naive stitch is fine.
function cueBody(vtt) {
  const text = vtt.replace(/\r\n/g, '\n').replace(/^﻿/, '');
  const firstBlank = text.indexOf('\n\n');
  if (/^WEBVTT/.test(text) && firstBlank !== -1) {
    return text.slice(firstBlank + 2).trim();
  }
  return text.trim();
}

// Download the VTT media playlist's segments and write one stitched
// .vtt into `dir`. Returns its path, or null on any failure.
async function downloadEnglishVtt(masterBody, masterUrl, dir, referer) {
  let playlistUrl;
  try {
    playlistUrl = pickSubtitleUri(masterBody, masterUrl);
  } catch (e) {
    return null;
  }
  if (!playlistUrl) return null;

  try {
    const body = await getText(playlistUrl, referer);
    const abs = (u) => (/^https?:/.test(u) ? u : new URL(u, playlistUrl).href);
    const segs = body
      .split('\n')
      .map((l) => l.trim())
      .filter((l) => l && !l.startsWith('#'))
      .map(abs);

    // A single-file rendition sometimes points straight at a .vtt.
    const targets = segs.length ? segs : [playlistUrl];

    const chunks = [];
    for (const s of targets) {
      chunks.push(cueBody(await getText(s, referer)));
    }
    const merged = 'WEBVTT\n\n' + chunks.filter(Boolean).join('\n\n') + '\n';
    if (!/-->/.test(merged)) return null; // no actual cues

    const outPath = path.join(dir, 'subs.en.vtt');
    fs.writeFileSync(outPath, merged);
    return outPath;
  } catch (e) {
    return null;
  }
}

module.exports = { pickSubtitleUri, downloadEnglishVtt };
