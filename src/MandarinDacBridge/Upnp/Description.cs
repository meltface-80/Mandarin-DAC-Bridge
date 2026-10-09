// Description.cs — the device description each DAC's renderer answers with
// (the LOCATION its SSDP announcements point at).
using System.Security.Cryptography;
using System.Text;

namespace MandarinDacBridge.Upnp;

internal static class Description
{
    // A UDN that stays the same for this DAC on this machine.
    public static string UdnFor(string host, string key)
    {
        var h = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes($"mandarin-dac-bridge|{host}|{key}")));
        var variant = ((Convert.ToInt32(h[16].ToString(), 16) & 3) | 8).ToString("x");
        return $"uuid:{h[..8]}-{h[8..12]}-5{h[13..16]}-{variant}{h[17..20]}-{h[20..32]}";
    }

    public static string Document(string id, string udn, string friendlyName, string manufacturer, string modelName, string version)
    {
        var services = string.Concat(Scpd.Services.Values.Select(s =>
            $"<service><serviceType>{s.Type}</serviceType><serviceId>{s.Id}</serviceId>" +
            $"<SCPDURL>/upnp/{id}/{s.Name}/scpd.xml</SCPDURL><controlURL>/upnp/{id}/{s.Name}/control</controlURL>" +
            $"<eventSubURL>/upnp/{id}/{s.Name}/event</eventSubURL></service>"));
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0" xmlns:dlna="urn:schemas-dlna-org:device-1-0">
            <specVersion><major>1</major><minor>0</minor></specVersion>
            <device>
            <deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>
            <dlna:X_DLNADOC>DMR-1.50</dlna:X_DLNADOC>
            <friendlyName>{Xml.Esc(friendlyName)}</friendlyName>
            <manufacturer>{Xml.Esc(manufacturer == "" ? "Mandarin DAC Bridge" : manufacturer)}</manufacturer>
            <manufacturerURL>https://github.com/meltface-80/Mandarin-DAC-Bridge</manufacturerURL>
            <modelDescription>Bit-perfect, exclusive UPnP renderer for a USB DAC (Mandarin DAC Bridge)</modelDescription>
            <modelName>{Xml.Esc(modelName)}</modelName>
            <modelNumber>{Xml.Esc(version)}</modelNumber>
            <modelURL>https://github.com/meltface-80/Mandarin-DAC-Bridge</modelURL>
            <serialNumber>{Xml.Esc(id)}</serialNumber>
            <UDN>{udn}</UDN>
            <iconList><icon><mimetype>image/png</mimetype><width>192</width><height>192</height><depth>24</depth><url>/icon.png</url></icon></iconList>
            <serviceList>{services}</serviceList>
            <presentationURL>/</presentationURL>
            </device>
            </root>
            """;
    }
}
