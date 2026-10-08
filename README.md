# MusicD brings to the DAC Bridge (.NET 10 Native)

A zero-configuration hardware interception and routing tool written in C# (.NET 10) and compiled natively via Native AOT. It enforces exclusive stream lockout mechanisms on physical USB DAC systems, rendering hardware addresses as independent virtualized UPnP rendering objects directly inside Audirvana and Mandarin.

## 🍏 Zero-Config Installation (macOS)

Paste the following execution path command inside a terminal instance block to install the native platform client structure:

```bash
/bin/bash -c "$(curl -fsSL https://githubusercontent.com)"
```

Once tracking begins, view your hardware configurations immediately via: **`http://localhost:55500`**

---

## 🐳 Docker Deployment Setup (DietPi Intel i5)

To pull up and run on an x86 configuration container layer using internal host network configurations:

```bash
# Build the compact image payload profile
docker build -t mandarin-dac-bridge-net10 .

# Execute under privilege flags to intercept ALSA/USB audio interfaces natively
docker run -d \
  --name mandarin-dac-bridge \
  --net=host \
  --device /dev/snd \
  --privileged \
  mandarin-dac-bridge-net10
```
