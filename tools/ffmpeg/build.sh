#!/bin/bash
#
# The bridge's own ffmpeg (dev/design/install.md): ffmpeg with SoX (libsoxr) and only what the bridge
# and its tests use, LGPL, static, built from the pinned sources in sources.lock.
#
#   tools/ffmpeg/build.sh <rid> <out>
#
#   rid  osx-arm64, osx-x64 (on a Mac; either from either), linux-x64, linux-arm64 (on Linux, on that
#        processor), win-x64, win-arm64 (in MSYS2's UCRT64 or CLANGARM64 shell, on that processor)
#   out  gets bin/ffmpeg (bin/ffmpeg.exe), NOTICE.md and the licences
#
# Needs a C compiler, make, curl, and tar that reads .xz; on Linux, OpenSSL 3's headers (libssl-dev).
# No cmake, nasm, pkg-config or Homebrew. Optional: WORK, a folder to fetch and build in (kept, so a
# second run fetches nothing; every file is checked against its pin each time), JOBS for make -j, CC.
#
# libsoxr is compiled directly rather than through its cmake: its scalar engines (cr32, cr64, vr32),
# with soxr-config.h written here. precision=28, the bridge's (Sources.Open), runs on cr64, SoX's
# double-precision engine.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
rid="${1:-}"; out="${2:-}"
[ -n "$rid" ] && [ -n "$out" ] || { echo "usage: tools/ffmpeg/build.sh <rid> <out>" >&2; exit 2; }
fail() { echo "[FAIL] ffmpeg ($rid): $*" >&2; exit 1; }
say() { echo "== ffmpeg ($rid): $*"; }

host_os="$(uname -s)"
# MSYS2's own tools are x86_64 and run emulated on Windows ARM, so there uname -m says x86_64: the shell's
# target processor (MSYSTEM_CARCH: aarch64 in CLANGARM64) is what is built for.
machine="${MSYSTEM_CARCH:-$(uname -m)}"
case "$machine" in arm64|aarch64) host_arch=arm64 ;; x86_64|amd64) host_arch=x64 ;; *) host_arch="$machine" ;; esac

if [ -n "${CC:-}" ]; then cc="$CC"
elif command -v cc >/dev/null 2>&1; then cc=cc
elif command -v gcc >/dev/null 2>&1; then cc=gcc
else cc=clang
fi
ar="${AR:-ar}"
command -v "$ar" >/dev/null 2>&1 || ! command -v llvm-ar >/dev/null 2>&1 || ar=llvm-ar   # a clang-only toolchain (MSYS2's CLANGARM64)
exe=""; cross=""; flags=""; ldextra=""; threads=pthreads
case "$rid" in
  osx-arm64|osx-x64)
    [ "$host_os" = Darwin ] || fail "built on a Mac"
    if [ "$rid" = osx-arm64 ]; then arch=arm64; farch=aarch64; else arch=x86_64; farch=x86_64; fi
    cc="${CC:-clang}"
    flags="-arch $arch -mmacosx-version-min=12.0"
    target="--arch=$farch --target-os=darwin"
    [ "${rid#osx-}" = "$host_arch" ] || cross="--enable-cross-compile"
    tls=securetransport ;;
  linux-x64|linux-arm64)
    [ "$host_os" = Linux ] && [ "${rid#linux-}" = "$host_arch" ] || fail "built on Linux, on that processor"
    target=""; tls=openssl ;;
  win-x64|win-arm64)
    case "$host_os" in MINGW*|UCRT*|CLANG*) ;; *) fail "built in MSYS2 (its UCRT64 shell, or CLANGARM64 for ARM64)" ;; esac
    [ "${rid#win-}" = "$host_arch" ] || fail "built on that processor"
    if [ "$rid" = win-arm64 ]; then farch=aarch64; else farch=x86_64; fi
    target="--arch=$farch --target-os=mingw32"; tls=schannel; threads=w32threads; exe=.exe; ldextra="-static" ;;
  *) fail "not a build this script makes (osx-arm64, osx-x64, linux-x64, linux-arm64, win-x64, win-arm64)" ;;
esac
command -v make >/dev/null 2>&1 || fail "make isn't installed"
command -v curl >/dev/null 2>&1 || fail "curl isn't installed"
jobs="${JOBS:-$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 2)}"

if [ -n "${WORK:-}" ]; then work="$WORK"; mkdir -p "$work"
else work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
fi
mkdir -p "$out/bin"
out="$(cd "$out" && pwd)"
prefix="$work/prefix-$rid"
rm -rf "$prefix" && mkdir -p "$prefix/include" "$prefix/lib"

# ---------------------------------------------------------------- the pinned sources

pin() { awk -v n="$1" -v k="$2" '$1 == n { print $k; exit }' "$here/sources.lock"; }
sha() { (sha256sum "$1" 2>/dev/null || shasum -a 256 "$1") | awk '{ print tolower($1) }'; }

# A source's tarball, fetched once and checked every time; prints its path.
fetch() {
  local url want file got
  url="$(pin "$1" 3)"; want="$(pin "$1" 4)"
  [ -n "$url" ] && [ -n "$want" ] || fail "$1 isn't in sources.lock"
  file="$work/${url##*/}"
  if [ ! -f "$file" ]; then
    curl -fsSL --retry 3 -o "$file.part" "$url" || fail "couldn't download $url"
    mv "$file.part" "$file"
  fi
  got="$(sha "$file")"
  [ "$got" = "$want" ] || { rm -f "$file"; fail "$url has SHA-256 $got, not the pinned $want: not built"; }
  printf '%s' "$file"
}

unpack() {   # tarball, folder, then any paths in it to leave out
  local tarball="$1" dir="$2" skip=()
  shift 2
  for p in "$@"; do skip+=("--exclude=$p"); done
  rm -rf "$dir" && mkdir -p "$dir"
  tar -xf "$tarball" -C "$dir" --strip-components=1 ${skip[@]+"${skip[@]}"} || fail "couldn't unpack $tarball"
}

ffmpeg_tar="$(fetch ffmpeg)"
soxr_tar="$(fetch soxr)"
say "sources checked: ffmpeg $(pin ffmpeg 2), libsoxr $(pin soxr 2)"

# ---------------------------------------------------------------- libsoxr

say "libsoxr"
s="$work/soxr-$rid"
# Its one symlink (an install check, not built) can't be made by MSYS2's tar, which copies a link's target
# and fails when the link comes first.
unpack "$soxr_tar" "$s" '*/inst-check-soxr-lsr'
cat > "$s/src/soxr-config.h" <<'EOF'
/* Written by tools/ffmpeg/build.sh in place of soxr's cmake: the scalar engines only. */
#if !defined soxr_config_included
#define soxr_config_included
#define AVCODEC_FOUND 0
#define AVUTIL_FOUND 0
#define WITH_PFFFT 0
#define HAVE_FENV_H 1
#define HAVE_STDBOOL_H 1
#define HAVE_STDINT_H 1
#define HAVE_LRINT 1
#define HAVE_BIGENDIAN 0
#define WITH_CR32 1
#define WITH_CR32S 0
#define WITH_CR64 1
#define WITH_CR64S 0
#define WITH_VR32 1
#define WITH_HI_PREC_CLOCK 1
#define WITH_FLOAT_STD_PREC_CLOCK 0
#define WITH_DEV_TRACE 0
#endif
EOF
mkdir -p "$s/obj"
# What soxr's src/CMakeLists.txt compiles with these engines and no SIMD.
for f in soxr data-io dbesi0 filter fft4g64 cr cr32 fft4g32 cr64 vr32; do
  # shellcheck disable=SC2086 # flags are several words
  $cc $flags -O2 -DSOXR_LIB -I"$s/src" -c "$s/src/$f.c" -o "$s/obj/$f.o" || fail "libsoxr: $f.c didn't compile"
done
"$ar" rcs "$prefix/lib/libsoxr.a" "$s"/obj/*.o || fail "libsoxr: $ar failed"
command -v ranlib >/dev/null 2>&1 && ranlib "$prefix/lib/libsoxr.a"
cp "$s/src/soxr.h" "$prefix/include/"

# ---------------------------------------------------------------- ffmpeg

say "ffmpeg"
f="$work/ffmpeg-$rid"
unpack "$ffmpeg_tar" "$f"
# What the bridge plays (Upnp/Control.cs: FLAC, WAV, AIFF, ALAC and AAC in MP4, MP3, Ogg, DSD as PCM, raw
# PCM), what it writes (32-bit PCM in WAV to a pipe), its filters (pan, SoX's aresample) and the network's
# protocols (StreamUri.FfmpegProtocols, held to them by -protocol_whitelist). lavfi, sine, the flac encoder
# and the file protocol are for the test suite's tones (ffmpeg -f lavfi -i sine=… x.flac), so it runs
# against this very ffmpeg; the bridge never names them, and no movie/amovie filter (which reads files) is built.
pcm="pcm_s16le,pcm_s16be,pcm_s24le,pcm_s24be,pcm_s32le,pcm_s32be,pcm_f32le,pcm_f32be,pcm_f64le,pcm_f64be,pcm_u8,pcm_s8"
rawpcm="pcm_s16le,pcm_s16be,pcm_s24le,pcm_s24be,pcm_s32le,pcm_s32be,pcm_f32le,pcm_f32be"
set -- \
  --prefix="$prefix" --cc="$cc" --ar="$ar" $target $cross \
  --pkg-config=false --disable-autodetect --disable-everything \
  --enable-"$threads" --enable-"$tls" \
  --disable-doc --disable-debug --disable-programs --enable-ffmpeg \
  --enable-static --disable-shared --disable-swscale --disable-x86asm \
  --enable-avdevice --enable-indev=lavfi \
  --enable-libsoxr \
  --enable-protocol=http,https,tcp,tls,crypto,httpproxy,data,file,pipe \
  --enable-demuxer=wav,w64,aiff,flac,mov,mp3,aac,ogg,matroska,dsf,iff,"$rawpcm" \
  --enable-decoder=flac,alac,aac,aac_latm,mp3,mp3float,vorbis,opus,dsd_lsbf,dsd_msbf,dsd_lsbf_planar,dsd_msbf_planar,"$pcm" \
  --enable-parser=flac,mpegaudio,aac,aac_latm,vorbis,opus \
  --enable-encoder=pcm_s32le,pcm_s16le,flac \
  --enable-muxer=wav,flac,pcm_s16le,null \
  --enable-filter=abuffer,abuffersink,aformat,anull,atrim,aresample,pan,sine \
  --extra-cflags="$flags -I$prefix/include" --extra-ldflags="$flags -L$prefix/lib $ldextra" --extra-libs=-lm
configured="$*"
b="$work/ffmpeg-build-$rid"
rm -rf "$b" && mkdir -p "$b"
(cd "$b" && "$f/configure" "$@" > configure.out 2>&1) || { tail -40 "$b/configure.out" "$b/ffbuild/config.log" 2>/dev/null >&2; fail "configure failed"; }
grep -i "unknown\|WARNING" "$b/configure.out" | grep -v "false not found" >&2 || true
(cd "$b" && make -j"$jobs" > make.out 2>&1) || { tail -40 "$b/make.out" >&2; fail "make failed"; }
cp "$b/ffmpeg$exe" "$out/bin/ffmpeg$exe"

# ---------------------------------------------------------------- what it is, and its licences

cp "$f/COPYING.LGPLv2.1" "$out/COPYING.LGPLv2.1"
cp "$f/LICENSE.md" "$out/LICENSE.ffmpeg.md"
cp "$s/LICENCE" "$out/LICENCE.soxr"
cat > "$out/NOTICE.md" <<EOF
# ffmpeg, as Mandarin DAC Bridge ships it

\`bin/ffmpeg$exe\` is FFmpeg $(pin ffmpeg 2) with libsoxr $(pin soxr 2) (SoX's resampler), built for $rid by
\`tools/ffmpeg/build.sh\` in https://github.com/meltface-80/Mandarin-DAC-Bridge, with only what the
bridge uses. Both are LGPL (version 2.1 or later): \`COPYING.LGPLv2.1\`, \`LICENSE.ffmpeg.md\` and
\`LICENCE.soxr\` are their licences. It is a separate program the bridge runs; nothing of it is
linked into the bridge. To use another ffmpeg instead, set \`FFMPEG\` to it.

## Sources

| | Version | From | SHA-256 |
|---|---|---|---|
| FFmpeg | $(pin ffmpeg 2) | $(pin ffmpeg 3) | $(pin ffmpeg 4) |
| libsoxr | $(pin soxr 2) | $(pin soxr 3) | $(pin soxr 4) |

libsoxr is compiled without its cmake, with its scalar engines only (see \`tools/ffmpeg/build.sh\`).

## FFmpeg's configure line

\`\`\`
$configured
\`\`\`

## Building it again

\`\`\`
tools/ffmpeg/build.sh $rid <folder>
\`\`\`
EOF

# ---------------------------------------------------------------- it works (where it can run here)

bin="$out/bin/ffmpeg$exe"
if [ -z "$cross" ]; then
  "$bin" -hide_banner -buildconf 2>&1 | grep -q -- '--enable-libsoxr' || fail "the build has no SoX"
  "$bin" -hide_banner -loglevel error -f lavfi -i sine=frequency=1000:sample_rate=44100:duration=1 \
    -af aresample=48000:resampler=soxr:precision=28 -f null - || fail "it doesn't resample with SoX"
  say "built and checked: $bin"
else
  say "built for another processor (not run here): $bin"
fi
