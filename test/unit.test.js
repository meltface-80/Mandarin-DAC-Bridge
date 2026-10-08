"use strict";
const test = require("node:test");
const assert = require("node:assert");
const fs = require("fs");
const os = require("os");
const path = require("path");
const { Readable } = require("stream");

const DSD = require("../lib/dsd");
const XML = require("../lib/xml");
const Devices = require("../lib/devices");
const Sources = require("../lib/sources");
const { Arbiter } = require("../lib/arbiter");
const Control = require("../lib/upnp/control");
const { lastChange, propertySet } = require("../lib/upnp/gena");
const { Ssdp, addressFor } = require("../lib/upnp/ssdp");
const { scpd } = require("../lib/upnp/scpd");
const { describe } = require("../lib/renderer");

/* ------------------------------------------------------------ DSD → DoP */

function dsf({ channels = 2, bytesPerChannel = 4096 * 2, fill = (ch, i) => (ch * 16 + i) & 255 }) {
  const block = 4096;
  const blocks = Math.ceil(bytesPerChannel / block);
  const data = Buffer.alloc(blocks * block * channels);
  for (let b = 0; b < blocks; b++)
    for (let ch = 0; ch < channels; ch++)
      for (let i = 0; i < block; i++) {
        const n = b * block + i;
        data[b * block * channels + ch * block + i] = n < bytesPerChannel ? fill(ch, n) : 0;
      }
  const head = Buffer.alloc(28 + 52 + 12);
  head.write("DSD ", 0); head.writeBigUInt64LE(28n, 4); head.writeBigUInt64LE(BigInt(head.length + data.length), 12);
  head.write("fmt ", 28); head.writeBigUInt64LE(52n, 32); head.writeUInt32LE(1, 40); head.writeUInt32LE(0, 44); head.writeUInt32LE(2, 48);
  head.writeUInt32LE(channels, 52); head.writeUInt32LE(2822400, 56); head.writeUInt32LE(1, 60);
  head.writeBigUInt64LE(BigInt(bytesPerChannel * 8), 64); head.writeUInt32LE(block, 72);
  head.write("data", 80); head.writeBigUInt64LE(BigInt(12 + data.length), 84);
  return Buffer.concat([head, data]);
}

async function collect(stream) {
  const parts = [];
  for await (const c of stream) parts.push(c);
  return Buffer.concat(parts);
}

test("DSF header", () => {
  const h = DSD.parseHeader(dsf({}));
  assert.strictEqual(h.kind, "dsf");
  assert.strictEqual(h.rate, 2822400);
  assert.strictEqual(h.channels, 2);
  assert.strictEqual(h.dataStart, 92);
  assert.strictEqual(h.lsbFirst, true);
  assert.strictEqual(DSD.dsdName(h.rate), "DSD64");
});

test("DSF → DoP: markers alternate, bytes in time order, bits reversed (LSB-first file)", async () => {
  const file = dsf({ bytesPerChannel: 6000 });   // the second block is part padding
  const h = DSD.parseHeader(file);
  const out = await collect(Readable.from([file.subarray(h.dataStart, 100), file.subarray(100, 5000), file.subarray(5000)]).pipe(new DSD.DopPacker(h)));
  const frames = out.length / 8;
  const silence = Math.round(176400 * 0.05);
  assert.strictEqual(frames, 3000 + silence, "6000 bytes per channel = 3000 DoP frames, then 50 ms of DoP silence");
  for (let f = 0; f < 3000; f++) {
    for (let ch = 0; ch < 2; ch++) {
      const w = out.readInt32LE((f * 2 + ch) * 4);
      assert.strictEqual((w >>> 24) & 255, f % 2 ? 0xfa : 0x05);
      assert.strictEqual((w >>> 16) & 255, DSD.REVERSE[(ch * 16 + f * 2) & 255]);
      assert.strictEqual((w >>> 8) & 255, DSD.REVERSE[(ch * 16 + f * 2 + 1) & 255]);
      assert.strictEqual(w & 255, 0);
    }
  }
  const last = out.readInt32LE(out.length - 4);
  assert.strictEqual((last >>> 8) & 0xffff, 0x6969);
});

test("DFF header and DoP (MSB-first, byte-interleaved)", async () => {
  const channels = 2, perCh = 1000;
  const data = Buffer.alloc(perCh * channels);
  for (let i = 0; i < perCh; i++) for (let ch = 0; ch < channels; ch++) data[i * channels + ch] = (i * 3 + ch) & 255;
  const chunk = (id, body) => { const h = Buffer.alloc(12); h.write(id, 0); h.writeBigUInt64BE(BigInt(body.length), 4); return Buffer.concat([h, body]); };
  const fs_ = Buffer.alloc(4); fs_.writeUInt32BE(5644800);
  const chnl = Buffer.alloc(2 + 8); chnl.writeUInt16BE(2); chnl.write("SLFTSRGT", 2);
  const prop = chunk("PROP", Buffer.concat([Buffer.from("SND "), chunk("FS  ", fs_), chunk("CHNL", chnl), chunk("CMPR", Buffer.from("DSD \x0enot compressed\x00"))]));
  const body = Buffer.concat([Buffer.from("DSD "), chunk("FVER", Buffer.from([1, 5, 0, 0])), prop, chunk("DSD ", data)]);
  const file = Buffer.concat([Buffer.from("FRM8"), Buffer.alloc(8), body]);
  file.writeBigUInt64BE(BigInt(body.length), 4);
  const h = DSD.parseHeader(file);
  assert.strictEqual(h.kind, "dff");
  assert.strictEqual(h.rate, 5644800);
  assert.strictEqual(DSD.dsdName(h.rate), "DSD128");
  const out = await collect(Readable.from([file.subarray(h.dataStart)]).pipe(new DSD.DopPacker(h)));
  const w0 = out.readInt32LE(4);   // frame 0, right channel
  assert.strictEqual((w0 >>> 24) & 255, 0x05);
  assert.strictEqual((w0 >>> 16) & 255, (0 * 3 + 1) & 255);
  assert.strictEqual((w0 >>> 8) & 255, (1 * 3 + 1) & 255);
});

test("DSD seek lands on a block (DSF) or an even byte (DFF)", () => {
  const h = DSD.parseHeader(dsf({ bytesPerChannel: 4096 * 100 }));
  const s = DSD.seekOffset(h, 0.1);   // 0.1 s = 35280 bytes per channel → block 8
  assert.deepStrictEqual(s, { offset: 8 * 4096 * 2, consumedPerChannel: 8 * 4096 });
});

test("muting DoP keeps the markers", () => {
  const b = Buffer.alloc(8);
  b.writeInt32LE((0x05 << 24) | (0x12 << 16) | (0x34 << 8), 0);
  b.writeInt32LE(((0xfa << 24) | (0x56 << 16) | (0x78 << 8)) | 0, 4);
  const m = DSD.muteDop(b);
  assert.strictEqual(m.readUInt32LE(0) >>> 0, 0x05696900);
  assert.strictEqual(m.readUInt32LE(4) >>> 0, 0xfa696900);
});

/* ------------------------------------------------------------ devices */

test("Linux: a USB DAC's stream file, with native DSD", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "asound-"));
  fs.writeFileSync(path.join(root, "cards"),
    " 0 [PCH            ]: HDA-Intel - HDA Intel PCH\n                      HDA Intel PCH at 0xf7f10000 irq 33\n" +
    " 1 [D90            ]: USB-Audio - Topping D90\n                      Topping Topping D90 at usb-0000:00:14.0-2, high speed\n");
  fs.mkdirSync(path.join(root, "card0"));
  fs.mkdirSync(path.join(root, "card1", "pcm0p", "sub0"), { recursive: true });
  fs.writeFileSync(path.join(root, "card1", "usbid"), "152a:8750\n");
  fs.writeFileSync(path.join(root, "card1", "pcm0p", "sub0", "status"), "closed\n");
  fs.writeFileSync(path.join(root, "card1", "stream0"), `Topping Topping D90 at usb-0000:00:14.0-2, high speed : USB Audio

Playback:
  Status: Running
    Interface = 1
    Altset = 1
    Packet Size = 216
    Momentary freq = 96000 Hz (0xc.0000)
  Interface 1
    Altset 1
    Format: S32_LE
    Channels: 2
    Endpoint: 0x01 (1 OUT) (ASYNC)
    Rates: 44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000, 705600, 768000
    Data packet interval: 125 us
    Bits: 32
  Interface 1
    Altset 2
    Format: SPECIAL DSD_U32_BE
    Channels: 2
    Rates: 88200, 176400, 352800, 705600
    Bits: 32

Capture:
  Interface 2
    Altset 1
    Format: S16_LE
    Channels: 2
    Rates: 8000
`);
  const list = Devices.listLinux({ root });
  assert.strictEqual(list.length, 1);
  const d = list[0];
  assert.strictEqual(d.name, "Topping D90");
  assert.strictEqual(d.manufacturer, "Topping");
  assert.strictEqual(d.usb, "152a:8750");
  assert.strictEqual(d.spec, "hw:CARD=D90,DEV=0");
  assert.deepStrictEqual(d.rates, [44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000, 705600, 768000]);
  assert.deepStrictEqual(d.bits, [32]);
  assert.deepStrictEqual(d.dsdNative, [88200, 176400, 352800, 705600]);
  assert.strictEqual(d.currentRate, 96000);
});

test("macOS: the helper's list → USB DACs only, rates from the physical formats", () => {
  const json = JSON.stringify([
    { uid: "BuiltInSpeakerDevice", name: "MacBook Pro Speakers", manufacturer: "Apple Inc.", transport: "bltn", alive: true, channels: 2, rate: 48000, hog: -1, rates: [[44100, 44100], [48000, 48000]], formats: [] },
    { uid: "AppleUSBAudioEngine:Chord:Mojo 2:1234:1", name: "Mojo 2", manufacturer: "Chord Electronics Ltd", transport: "usb", alive: true, channels: 2, rate: 44100, hog: 812, volume: 0.8,
      rates: [[44100, 44100], [48000, 48000], [88200, 88200], [96000, 96000], [176400, 176400], [192000, 192000], [352800, 352800], [384000, 384000], [705600, 705600], [768000, 768000]],
      formats: [{ id: "lpcm", min: 44100, max: 768000, bits: 32, float: false, channels: 2 }, { id: "lpcm", min: 44100, max: 768000, bits: 24, float: false, channels: 2 }] }
  ]);
  const list = Devices.parseMac(json);
  assert.strictEqual(list.length, 1);
  const d = list[0];
  assert.strictEqual(d.name, "Mojo 2");
  assert.strictEqual(d.spec, "AppleUSBAudioEngine:Chord:Mojo 2:1234:1");
  assert.deepStrictEqual(d.rates, [44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000, 705600, 768000]);
  assert.deepStrictEqual(d.bits, [24, 32]);
  assert.strictEqual(d.holderPid, 812);
  assert.strictEqual(d.volume, 0.8);
  assert.match(d.formats[0], /32-bit integer · 2 ch · 44\.1 kHz, 48 kHz/);
  assert.strictEqual(Devices.parseMac(json, { all: true }).length, 2);
  assert.strictEqual(Devices.friendlyProcess("/Applications/Audirvana Studio.app/Contents/MacOS/Audirvana Studio"), "Audirvana");
  assert.strictEqual(Devices.friendlyProcess("/Applications/Music.app/Contents/MacOS/Music"), "Music");
});

/* ------------------------------------------------------------ choosing the rate */

test("the rate sent: the track's own, else the nearest in its family", () => {
  const dac = [44100, 48000, 88200, 96000, 176400, 192000];
  assert.strictEqual(Sources.pickRate(96000, dac), 96000);
  assert.strictEqual(Sources.pickRate(352800, dac), 176400);
  assert.strictEqual(Sources.pickRate(384000, dac), 192000);
  assert.strictEqual(Sources.pickRate(22050, dac), 44100);
  assert.strictEqual(Sources.pickRate(96000, [44100, 48000]), 48000);
  assert.strictEqual(Sources.pickRate(88200, [48000, 96000]), 96000);
  assert.strictEqual(Sources.pickRate(12345, []), 12345);
  assert.deepStrictEqual(Sources.rawPcm("audio/L24;rate=96000;channels=2"), { format: "s24be", rate: 96000, channels: 2, bits: 24 });
  assert.strictEqual(Sources.rawPcm("audio/flac"), null);
  assert.ok(Sources.isDsd({ mime: "audio/x-dsf", uri: "" }));
  assert.ok(Sources.isDsd({ mime: "", uri: "http://x/a.dff?id=1" }));
});

test("what ffmpeg says about its input", () => {
  const p = Sources.parseProbe(`Input #0, flac, from 'http://x/a.flac':
  Duration: 00:03:21.45, start: 0.000000, bitrate: 2944 kb/s
  Stream #0:0: Audio: flac, 96000 Hz, stereo, s32 (24 bit)
Stream mapping:
  Stream #0:0 -> #0:0 (flac (native) -> pcm_s32le (native))
Output #0, wav, to 'pipe:1':
  Stream #0:0: Audio: pcm_s32le, 96000 Hz, stereo, s32, 6144 kb/s`);
  assert.deepStrictEqual(p, { codec: "flac", srcRate: 96000, bits: 24, float: false, duration: 201.45, channels: 2 });
  assert.strictEqual(describe({ rate: 96000, bits: 24, codec: "flac" }), "96 kHz · 24-bit · FLAC");
  assert.strictEqual(describe({ rate: 176400, codec: "DSD64", dop: true }), "DSD64 · DoP at 176.4 kHz");
  assert.strictEqual(describe({ rate: 176400, srcRate: 352800, resampled: true, bits: 24, codec: "flac" }), "352.8 kHz → 176.4 kHz · 24-bit · FLAC");
});

/* ------------------------------------------------------------ SOAP, DIDL */

test("SOAP requests and DIDL metadata", () => {
  const meta = '<DIDL-Lite xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/">' +
    '<item id="1" parentID="0" restricted="1"><dc:title>So What &amp; More</dc:title><upnp:artist>Miles Davis</upnp:artist><upnp:album>Kind of Blue</upnp:album>' +
    '<res protocolInfo="http-get:*:audio/flac:*" duration="0:09:22.000">http://10.0.0.2:3500/stream/1.flac</res></item></DIDL-Lite>';
  const body = `<?xml version="1.0"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body>` +
    `<u:SetAVTransportURI xmlns:u="urn:schemas-upnp-org:service:AVTransport:1"><InstanceID>0</InstanceID>` +
    `<CurrentURI>http://10.0.0.2:3500/stream/1.flac?a=1&amp;b=2</CurrentURI><CurrentURIMetaData>${XML.esc(meta)}</CurrentURIMetaData></u:SetAVTransportURI></s:Body></s:Envelope>`;
  const { action, args } = XML.parseSoap(body, '"urn:schemas-upnp-org:service:AVTransport:1#SetAVTransportURI"');
  assert.strictEqual(action, "SetAVTransportURI");
  assert.strictEqual(args.InstanceID, "0");
  assert.strictEqual(args.CurrentURI, "http://10.0.0.2:3500/stream/1.flac?a=1&b=2");
  assert.strictEqual(args.CurrentURIMetaData, meta);
  const d = XML.parseDidl(args.CurrentURIMetaData, "http://10.0.0.2:3500/stream/1.flac");
  assert.strictEqual(d.title, "So What & More");
  assert.strictEqual(d.artist, "Miles Davis");
  assert.strictEqual(d.mime, "audio/flac");
  assert.strictEqual(d.duration, 562);
  assert.strictEqual(XML.parseSoap('<s:Envelope><s:Body><u:Play xmlns:u="x"/></s:Body></s:Envelope>', "").action, "Play");
  assert.strictEqual(XML.secondsToHms(3723.9), "1:02:03");
  assert.match(Control.fault(705, "Transport is locked"), /<errorCode>705<\/errorCode>/);
  assert.match(scpd("AVTransport"), /<name>SetNextAVTransportURI<\/name>/);
});

/* ------------------------------------------------------------ arbitration */

test("the arbiter: the first to play owns the DAC; others wait out the grace", () => {
  const a = new Arbiter({ graceMs: 1000, mandarinPort: 3500 });
  const req = (ip, ua) => ({ socket: { remoteAddress: "::ffff:" + ip }, headers: ua ? { "user-agent": ua } : {} });
  const aud = a.identify(req("10.0.0.5", "Audirvana Studio/2.1.0"));
  const man = a.identify(req("10.0.0.9"), "http://10.0.0.9:3500/stream/42.flac");
  assert.strictEqual(aud.name, "Audirvana");
  assert.strictEqual(man.name, "Mandarin");
  assert.strictEqual(a.identify(req("10.0.0.9")).name, "Mandarin", "remembered without a URI");
  assert.strictEqual(a.claim(aud, "Play", false), null);
  assert.strictEqual(a.claim(man, "Play", true).code, 705, "kept out while it plays");
  assert.ok(a.view(true).blocked);
  assert.strictEqual(a.claim(man, "Play", false).code, 705, "and within the grace after");
  a.lastActive = Date.now() - 2000;
  assert.strictEqual(a.claim(man, "SetAVTransportURI", false), null, "after the grace it may");
  assert.strictEqual(a.owner.name, "Mandarin");
  a.release();
  assert.strictEqual(a.owner, null);
});

/* ------------------------------------------------------------ events, discovery */

test("LastChange documents", () => {
  const x = lastChange("AVTransport", { TransportState: "PLAYING", CurrentTrackURI: "http://a/b?c=1&d=2" });
  assert.strictEqual(x, '<Event xmlns="urn:schemas-upnp-org:metadata-1-0/AVT/"><InstanceID val="0"><TransportState val="PLAYING"/><CurrentTrackURI val="http://a/b?c=1&amp;d=2"/></InstanceID></Event>');
  assert.match(lastChange("RenderingControl", { Volume: "100" }), /<Volume channel="Master" val="100"\/>/);
  const ps = propertySet("AVTransport", { TransportState: "STOPPED" });
  assert.match(ps, /<LastChange>&lt;Event xmlns=&quot;urn:schemas-upnp-org:metadata-1-0\/AVT\/&quot;&gt;/);
  assert.match(propertySet("ConnectionManager", { CurrentConnectionIDs: "0" }), /<e:property><CurrentConnectionIDs>0<\/CurrentConnectionIDs><\/e:property>/);
});

test("SSDP answers an M-SEARCH for a renderer, from the asker's network", async () => {
  assert.strictEqual(addressFor("192.168.1.40", [{ address: "10.0.0.2", netmask: "255.255.255.0" }, { address: "192.168.1.10", netmask: "255.255.255.0" }]), "192.168.1.10");
  const sent = [];
  const s = new Ssdp({ port: 55500, server: "test", devices: () => [{ udn: "uuid:abc", path: "/upnp/dac-1/description.xml" }] });
  s.ifs = [{ address: "192.168.1.10", netmask: "255.255.255.0" }];
  s.sock = { send: (buf, port, addr) => sent.push({ text: buf.toString(), port, addr }) };
  const ask = (st) => s.onMessage(Buffer.from(`M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: "ssdp:discover"\r\nMX: 0\r\nST: ${st}\r\n\r\n`), { address: "192.168.1.40", port: 50000 });
  ask("urn:schemas-upnp-org:device:MediaRenderer:1");
  ask("urn:schemas-upnp-org:service:AVTransport:1");
  ask("urn:schemas-upnp-org:device:MediaServer:1");
  await new Promise((r) => setTimeout(r, 30));
  assert.strictEqual(sent.length, 2);
  assert.match(sent[0].text, /LOCATION: http:\/\/192\.168\.1\.10:55500\/upnp\/dac-1\/description\.xml/);
  assert.match(sent[0].text, /USN: uuid:abc::urn:schemas-upnp-org:device:MediaRenderer:1/);
  assert.strictEqual(sent[0].addr, "192.168.1.40");
  sent.length = 0;
  ask("ssdp:all");
  await new Promise((r) => setTimeout(r, 30));
  assert.strictEqual(sent.length, 6, "root, uuid, the device type and three services");
});
