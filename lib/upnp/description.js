"use strict";
/*
 * description.js — the device description each DAC's renderer answers with
 * (the LOCATION its SSDP announcements point at).
 */
const crypto = require("crypto");
const XML = require("../xml");
const { SERVICES } = require("./scpd");

/* A UDN that stays the same for this DAC on this machine. */
function udnFor(host, key) {
  const h = crypto.createHash("sha1").update(`mandarin-dac-bridge|${host}|${key}`).digest("hex");
  return `uuid:${h.slice(0, 8)}-${h.slice(8, 12)}-5${h.slice(13, 16)}-${((parseInt(h[16], 16) & 3) | 8).toString(16)}${h.slice(17, 20)}-${h.slice(20, 32)}`;
}

function description({ id, udn, friendlyName, manufacturer, modelName, serial, version, presentationUrl }) {
  const services = Object.entries(SERVICES).map(([name, s]) =>
    `<service><serviceType>${s.type}</serviceType><serviceId>${s.id}</serviceId>` +
    `<SCPDURL>/upnp/${id}/${name}/scpd.xml</SCPDURL><controlURL>/upnp/${id}/${name}/control</controlURL>` +
    `<eventSubURL>/upnp/${id}/${name}/event</eventSubURL></service>`).join("");
  return `<?xml version="1.0" encoding="utf-8"?>
<root xmlns="urn:schemas-upnp-org:device-1-0" xmlns:dlna="urn:schemas-dlna-org:device-1-0">
<specVersion><major>1</major><minor>0</minor></specVersion>
<device>
<deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>
<dlna:X_DLNADOC>DMR-1.50</dlna:X_DLNADOC>
<friendlyName>${XML.esc(friendlyName)}</friendlyName>
<manufacturer>${XML.esc(manufacturer || "Mandarin DAC Bridge")}</manufacturer>
<manufacturerURL>https://github.com/meltface-80/Mandarin-DAC-Bridge</manufacturerURL>
<modelDescription>Bit-perfect, exclusive UPnP renderer for a USB DAC (Mandarin DAC Bridge)</modelDescription>
<modelName>${XML.esc(modelName)}</modelName>
<modelNumber>${XML.esc(version)}</modelNumber>
<modelURL>https://github.com/meltface-80/Mandarin-DAC-Bridge</modelURL>
<serialNumber>${XML.esc(serial)}</serialNumber>
<UDN>${udn}</UDN>
<iconList><icon><mimetype>image/png</mimetype><width>192</width><height>192</height><depth>24</depth><url>/icon.png</url></icon></iconList>
<serviceList>${services}</serviceList>
<presentationURL>${XML.esc(presentationUrl)}</presentationURL>
</device>
</root>`;
}

module.exports = { description, udnFor };
