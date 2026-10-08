/*
 * dachelper-mac.c — the Mac's DACs through Core Audio.
 *
 *   dachelper list          every output device and what it takes, as JSON
 *   dachelper hold <UID>    take the DAC in hog mode and play to it (proto.h)
 *
 * Hog mode is Core Audio's exclusive access: while this program holds it, no
 * other program (Audirvana, Mandarin, Music, the system sounds) can play to
 * the DAC. The DAC is set to each track's own rate by choosing its physical
 * format (integer, at its widest), so nothing is resampled. Samples reach the
 * HAL as 32-bit float, which carries 24-bit audio — and DoP — exactly.
 *
 * Build: clang -O2 -o dachelper dachelper-mac.c -framework CoreAudio -framework CoreFoundation
 */
#include <CoreAudio/CoreAudio.h>
#include <CoreFoundation/CoreFoundation.h>
#include <poll.h>
#include <signal.h>
#include <stdatomic.h>
#include "proto.h"

#define MAIN_ELEMENT 0   /* kAudioObjectPropertyElementMain (was …Master) */

/* ------------------------------------------------------------ properties */

static OSStatus get_prop(AudioObjectID obj, AudioObjectPropertySelector sel, AudioObjectPropertyScope scope,
                         AudioObjectPropertyElement el, UInt32 size, void *out) {
  AudioObjectPropertyAddress a = { sel, scope, el };
  return AudioObjectGetPropertyData(obj, &a, 0, NULL, &size, out);
}

/* A property of any size, in a malloc'd buffer; NULL when it isn't there. */
static void *get_prop_alloc(AudioObjectID obj, AudioObjectPropertySelector sel, AudioObjectPropertyScope scope, UInt32 *size) {
  AudioObjectPropertyAddress a = { sel, scope, MAIN_ELEMENT };
  *size = 0;
  if (!AudioObjectHasProperty(obj, &a)) return NULL;
  if (AudioObjectGetPropertyDataSize(obj, &a, 0, NULL, size) != noErr || *size == 0) return NULL;
  void *buf = calloc(1, *size);
  if (!buf) return NULL;
  if (AudioObjectGetPropertyData(obj, &a, 0, NULL, size, buf) != noErr) { free(buf); return NULL; }
  return buf;
}

static void get_string(AudioObjectID obj, AudioObjectPropertySelector sel, char *out, size_t cap) {
  CFStringRef s = NULL;
  out[0] = 0;
  if (get_prop(obj, sel, kAudioObjectPropertyScopeGlobal, MAIN_ELEMENT, sizeof s, &s) != noErr || !s) return;
  if (!CFStringGetCString(s, out, (CFIndex)cap, kCFStringEncodingUTF8)) out[0] = 0;
  CFRelease(s);
}

static unsigned output_channels(AudioObjectID dev) {
  UInt32 size;
  AudioBufferList *abl = get_prop_alloc(dev, kAudioDevicePropertyStreamConfiguration, kAudioObjectPropertyScopeOutput, &size);
  if (!abl) return 0;
  unsigned n = 0;
  for (UInt32 i = 0; i < abl->mNumberBuffers; i++) n += abl->mBuffers[i].mNumberChannels;
  free(abl);
  return n;
}

static AudioStreamID first_output_stream(AudioObjectID dev) {
  UInt32 size;
  AudioStreamID *ids = get_prop_alloc(dev, kAudioDevicePropertyStreams, kAudioObjectPropertyScopeOutput, &size);
  if (!ids) return kAudioObjectUnknown;
  AudioStreamID s = size >= sizeof(AudioStreamID) ? ids[0] : kAudioObjectUnknown;
  free(ids);
  return s;
}

static pid_t hog_owner(AudioObjectID dev) {
  pid_t p = -1;
  if (get_prop(dev, kAudioDevicePropertyHogMode, kAudioObjectPropertyScopeGlobal, MAIN_ELEMENT, sizeof p, &p) != noErr) return -1;
  return p;
}

static Float64 nominal_rate(AudioObjectID dev) {
  Float64 r = 0;
  get_prop(dev, kAudioDevicePropertyNominalSampleRate, kAudioObjectPropertyScopeGlobal, MAIN_ELEMENT, sizeof r, &r);
  return r;
}

static void fourcc(UInt32 v, char out[5]) {
  out[0] = (char)(v >> 24); out[1] = (char)(v >> 16); out[2] = (char)(v >> 8); out[3] = (char)v; out[4] = 0;
  for (int i = 0; i < 4; i++) if (out[i] < 32 || out[i] > 126) out[i] = '?';
  for (int i = 3; i >= 0 && out[i] == ' '; i--) out[i] = 0;
}

static AudioObjectID *all_devices(UInt32 *count) {
  UInt32 size;
  AudioObjectID *ids = get_prop_alloc(kAudioObjectSystemObject, kAudioHardwarePropertyDevices, kAudioObjectPropertyScopeGlobal, &size);
  *count = ids ? size / sizeof(AudioObjectID) : 0;
  return ids;
}

static AudioObjectID find_device(const char *uid) {
  UInt32 n;
  AudioObjectID *ids = all_devices(&n), found = kAudioObjectUnknown;
  char u[512];
  for (UInt32 i = 0; i < n; i++) {
    get_string(ids[i], kAudioDevicePropertyDeviceUID, u, sizeof u);
    if (!strcmp(u, uid)) { found = ids[i]; break; }
  }
  free(ids);
  return found;
}

/* ------------------------------------------------------------ list */

static int cmd_list(void) {
  UInt32 n;
  AudioObjectID *ids = all_devices(&n);
  char name[512], maker[512], uid[512], model[512], t[5], e1[1100], e2[1100], e3[1100], e4[1100];
  printf("[");
  int first = 1;
  for (UInt32 i = 0; i < n; i++) {
    AudioObjectID d = ids[i];
    unsigned ch = output_channels(d);
    if (!ch) continue;
    get_string(d, kAudioObjectPropertyName, name, sizeof name);
    get_string(d, kAudioObjectPropertyManufacturer, maker, sizeof maker);
    get_string(d, kAudioDevicePropertyDeviceUID, uid, sizeof uid);
    get_string(d, kAudioDevicePropertyModelUID, model, sizeof model);
    UInt32 transport = 0, alive = 1;
    get_prop(d, kAudioDevicePropertyTransportType, kAudioObjectPropertyScopeGlobal, MAIN_ELEMENT, sizeof transport, &transport);
    get_prop(d, kAudioDevicePropertyDeviceIsAlive, kAudioObjectPropertyScopeGlobal, MAIN_ELEMENT, sizeof alive, &alive);
    fourcc(transport, t);
    printf("%s\n{\"uid\":\"%s\",\"name\":\"%s\",\"manufacturer\":\"%s\",\"model\":\"%s\",\"transport\":\"%s\",\"alive\":%s,"
           "\"channels\":%u,\"rate\":%.0f,\"hog\":%d",
           first ? "" : ",", jstr(uid, e1, sizeof e1), jstr(name, e2, sizeof e2), jstr(maker, e3, sizeof e3),
           jstr(model, e4, sizeof e4), t, alive ? "true" : "false", ch, nominal_rate(d), (int)hog_owner(d));
    first = 0;

    Float32 vol = -1;
    if (get_prop(d, kAudioDevicePropertyVolumeScalar, kAudioObjectPropertyScopeOutput, MAIN_ELEMENT, sizeof vol, &vol) != noErr &&
        get_prop(d, kAudioDevicePropertyVolumeScalar, kAudioObjectPropertyScopeOutput, 1, sizeof vol, &vol) != noErr) vol = -1;
    if (vol >= 0) printf(",\"volume\":%.3f", vol);

    UInt32 size;
    AudioValueRange *rr = get_prop_alloc(d, kAudioDevicePropertyAvailableNominalSampleRates, kAudioObjectPropertyScopeGlobal, &size);
    printf(",\"rates\":[");
    for (UInt32 k = 0; rr && k < size / sizeof *rr; k++) printf("%s[%.0f,%.0f]", k ? "," : "", rr[k].mMinimum, rr[k].mMaximum);
    printf("]");
    free(rr);

    printf(",\"formats\":[");
    AudioStreamID s = first_output_stream(d);
    AudioStreamRangedDescription *f = s == kAudioObjectUnknown ? NULL :
      get_prop_alloc(s, kAudioStreamPropertyAvailablePhysicalFormats, kAudioObjectPropertyScopeGlobal, &size);
    for (UInt32 k = 0; f && k < size / sizeof *f; k++) {
      AudioStreamBasicDescription *b = &f[k].mFormat;
      char id[5];
      fourcc(b->mFormatID, id);
      printf("%s{\"id\":\"%s\",\"min\":%.0f,\"max\":%.0f,\"bits\":%u,\"float\":%s,\"channels\":%u}", k ? "," : "", id,
             f[k].mSampleRateRange.mMinimum, f[k].mSampleRateRange.mMaximum, (unsigned)b->mBitsPerChannel,
             (b->mFormatFlags & kAudioFormatFlagIsFloat) ? "true" : "false", (unsigned)b->mChannelsPerFrame);
    }
    free(f);
    printf("]}");
  }
  printf("]\n");
  free(ids);
  return 0;
}

/* ------------------------------------------------------------ hold */

static char dev_uid[512];
static AudioObjectID dev = kAudioObjectUnknown;
static AudioDeviceIOProcID procid = NULL;
static pid_t mypid;
static int have_hog;

/* The buffer between stdin and the device: one writer (stdin), one reader (the IOProc). */
static int32_t *ring;
static uint64_t ring_frames;
static _Atomic uint64_t wpos, rpos, played;
static unsigned src_ch = 2, rate;
static int configured, paused, running;
static _Atomic int draining, drained_flag, underrun_flag;
static _Atomic uint32_t cur_gen, discard_below;
static pthread_mutex_t pmu = PTHREAD_MUTEX_INITIALIZER;   /* the writer, against a flush */
static pthread_mutex_t dmu = PTHREAD_MUTEX_INITIALIZER;   /* starting, stopping and setting the device */

static OSStatus ioproc(AudioObjectID inDevice, const AudioTimeStamp *inNow, const AudioBufferList *inInputData,
                       const AudioTimeStamp *inInputTime, AudioBufferList *outOutputData,
                       const AudioTimeStamp *inOutputTime, void *inClientData) {
  (void)inDevice; (void)inNow; (void)inInputData; (void)inInputTime; (void)inOutputTime; (void)inClientData;
  if (!outOutputData || outOutputData->mNumberBuffers == 0) return noErr;
  for (UInt32 i = 1; i < outOutputData->mNumberBuffers; i++)
    if (outOutputData->mBuffers[i].mData) memset(outOutputData->mBuffers[i].mData, 0, outOutputData->mBuffers[i].mDataByteSize);
  AudioBuffer *b = &outOutputData->mBuffers[0];
  unsigned ch = b->mNumberChannels;
  if (!ch || !b->mData) return noErr;
  float *dst = b->mData;
  UInt32 frames = b->mDataByteSize / (UInt32)(sizeof(float) * ch);
  uint64_t r = atomic_load_explicit(&rpos, memory_order_relaxed);
  uint64_t w = atomic_load_explicit(&wpos, memory_order_acquire);
  uint64_t avail = w - r;
  UInt32 n = avail < frames ? (UInt32)avail : frames;
  for (UInt32 f = 0; f < n; f++) {
    const int32_t *s = ring + ((r + f) % ring_frames) * src_ch;
    float *o = dst + (size_t)f * ch;
    for (unsigned c = 0; c < ch; c++) o[c] = c < src_ch ? (float)s[c] * (1.0f / 2147483648.0f) : 0.0f;
  }
  if (n < frames) memset(dst + (size_t)n * ch, 0, (size_t)(frames - n) * ch * sizeof(float));
  atomic_store_explicit(&rpos, r + n, memory_order_release);
  atomic_fetch_add_explicit(&played, n, memory_order_relaxed);
  if (n < frames) {
    if (atomic_load(&draining)) atomic_store(&drained_flag, 1);
    else atomic_store(&underrun_flag, 1);
  }
  return noErr;
}

static uint64_t buffered(void) { return atomic_load(&wpos) - atomic_load(&rpos); }

/* With dmu held. */
static void device_stop(void) {
  if (running) { AudioDeviceStop(dev, procid); running = 0; }
}
static void device_start(void) {
  if (!running && have_hog && configured && procid) {
    if (AudioDeviceStart(dev, procid) == noErr) running = 1;
  }
}
static void maybe_start(void) {
  pthread_mutex_lock(&dmu);
  if (!running && !paused && configured && (buffered() >= rate / 4 || atomic_load(&draining))) device_start();
  pthread_mutex_unlock(&dmu);
}

/* Empty the buffer; with dmu held and the device stopped. */
static void flush(void) {
  pthread_mutex_lock(&pmu);
  atomic_store(&rpos, atomic_load(&wpos));
  atomic_store(&played, 0);
  atomic_store(&draining, 0);
  atomic_store(&drained_flag, 0);
  atomic_store(&underrun_flag, 0);
  pthread_mutex_unlock(&pmu);
}

static int take_hog(void) {
  pid_t p = hog_owner(dev);
  if (p == mypid) return 1;
  if (p != -1) return 0;
  p = mypid;
  AudioObjectPropertyAddress a = { kAudioDevicePropertyHogMode, kAudioObjectPropertyScopeGlobal, MAIN_ELEMENT };
  AudioObjectSetPropertyData(dev, &a, 0, NULL, sizeof p, &p);
  return hog_owner(dev) == mypid;
}

static void report_status(void) {
  static int last = -2;
  pid_t owner = hog_owner(dev);
  int state = have_hog ? 1 : (int)owner;
  if (state == last) return;
  last = state;
  if (have_hog) emit("{\"ev\":\"status\",\"exclusive\":true,\"holder\":%d,\"msg\":\"\"}", (int)mypid);
  else emit("{\"ev\":\"status\",\"exclusive\":false,\"holder\":%d,\"msg\":\"another program has the DAC in exclusive mode\"}", (int)owner);
}

static void gone(void) { emit("{\"ev\":\"gone\"}"); exit(3); }

/* Physical format: linear PCM at this rate, integer before float, widest first. */
static void configure(uint32_t gen, unsigned r, unsigned ch) {
  char why[256] = "", msg[600];
  pthread_mutex_lock(&dmu);
  device_stop();
  flush();
  configured = 0;
  if (!have_hog) { snprintf(why, sizeof why, "another program has the DAC in exclusive mode (pid %d)", (int)hog_owner(dev)); goto fail; }
  AudioStreamID s = first_output_stream(dev);
  if (s == kAudioObjectUnknown) { snprintf(why, sizeof why, "the DAC has no output stream"); goto fail; }
  UInt32 size;
  AudioStreamRangedDescription *f = get_prop_alloc(s, kAudioStreamPropertyAvailablePhysicalFormats, kAudioObjectPropertyScopeGlobal, &size);
  int best = -1, best_score = -1;
  for (UInt32 k = 0; f && k < size / sizeof *f; k++) {
    AudioStreamBasicDescription *b = &f[k].mFormat;
    if (b->mFormatID != kAudioFormatLinearPCM) continue;
    if (r + 0.5 < f[k].mSampleRateRange.mMinimum || r - 0.5 > f[k].mSampleRateRange.mMaximum) continue;
    if (b->mChannelsPerFrame < ch) continue;
    int score = (b->mFormatFlags & kAudioFormatFlagIsFloat ? 0 : 1000) + (int)b->mBitsPerChannel - (int)b->mChannelsPerFrame;
    if (score > best_score) { best_score = score; best = (int)k; }
  }
  if (best < 0) { free(f); snprintf(why, sizeof why, "the DAC doesn't take %u Hz", r); goto fail; }
  AudioStreamBasicDescription phys = f[best].mFormat;
  free(f);
  phys.mSampleRate = r;
  AudioObjectPropertyAddress pa = { kAudioStreamPropertyPhysicalFormat, kAudioObjectPropertyScopeGlobal, MAIN_ELEMENT };
  OSStatus st = AudioObjectSetPropertyData(s, &pa, 0, NULL, sizeof phys, &phys);
  if (st != noErr) {
    /* Some drivers only take the rate on the device. */
    Float64 fr = r;
    AudioObjectPropertyAddress ra = { kAudioDevicePropertyNominalSampleRate, kAudioObjectPropertyScopeGlobal, MAIN_ELEMENT };
    AudioObjectSetPropertyData(dev, &ra, 0, NULL, sizeof fr, &fr);
  }
  for (int i = 0; i < 150 && (unsigned)(nominal_rate(dev) + 0.5) != r; i++) usleep(20000);
  if ((unsigned)(nominal_rate(dev) + 0.5) != r) { snprintf(why, sizeof why, "the DAC didn't change to %u Hz", r); goto fail; }

  AudioStreamBasicDescription virt;
  memset(&virt, 0, sizeof virt);
  get_prop(s, kAudioStreamPropertyVirtualFormat, kAudioObjectPropertyScopeGlobal, MAIN_ELEMENT, sizeof virt, &virt);
  if (!(virt.mFormatFlags & kAudioFormatFlagIsFloat) || virt.mBitsPerChannel != 32 || (virt.mFormatFlags & kAudioFormatFlagIsNonInterleaved)) {
    AudioStreamBasicDescription want;
    memset(&want, 0, sizeof want);
    want.mSampleRate = r;
    want.mFormatID = kAudioFormatLinearPCM;
    want.mFormatFlags = kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked;
    want.mChannelsPerFrame = virt.mChannelsPerFrame ? virt.mChannelsPerFrame : phys.mChannelsPerFrame;
    want.mBitsPerChannel = 32;
    want.mBytesPerFrame = 4 * want.mChannelsPerFrame;
    want.mFramesPerPacket = 1;
    want.mBytesPerPacket = want.mBytesPerFrame;
    AudioObjectPropertyAddress va = { kAudioStreamPropertyVirtualFormat, kAudioObjectPropertyScopeGlobal, MAIN_ELEMENT };
    if (AudioObjectSetPropertyData(s, &va, 0, NULL, sizeof want, &want) != noErr) {
      snprintf(why, sizeof why, "the DAC's stream can't take 32-bit float");
      goto fail;
    }
  }

  uint64_t frames = (uint64_t)r * 2;   /* two seconds */
  int32_t *nr = realloc(ring, (size_t)(frames * ch * sizeof(int32_t)));
  if (!nr) { snprintf(why, sizeof why, "out of memory"); goto fail; }
  ring = nr; ring_frames = frames;
  atomic_store(&wpos, 0); atomic_store(&rpos, 0); atomic_store(&played, 0);
  src_ch = ch; rate = r; configured = 1;
  pthread_mutex_unlock(&dmu);
  emit("{\"ev\":\"format\",\"gen\":%u,\"ok\":true,\"rate\":%u,\"channels\":%u,\"physical\":\"%u-bit %s, %u ch\"}", gen, r, ch,
       (unsigned)phys.mBitsPerChannel, (phys.mFormatFlags & kAudioFormatFlagIsFloat) ? "float" : "integer", (unsigned)phys.mChannelsPerFrame);
  return;
fail:
  pthread_mutex_unlock(&dmu);
  emit("{\"ev\":\"format\",\"gen\":%u,\"ok\":false,\"msg\":\"%s\"}", gen, jstr(why, msg, sizeof msg));
}

static void push(const int32_t *s, size_t frames, uint32_t gen) {
  size_t off = 0;
  while (off < frames) {
    pthread_mutex_lock(&pmu);
    if (gen < atomic_load(&discard_below) || !configured) { pthread_mutex_unlock(&pmu); return; }
    uint64_t w = atomic_load_explicit(&wpos, memory_order_relaxed);
    uint64_t r = atomic_load_explicit(&rpos, memory_order_acquire);
    uint64_t space = ring_frames - (w - r);
    size_t n = (size_t)(space < frames - off ? space : frames - off);
    size_t done = 0;
    while (done < n) {
      uint64_t at = (w + done) % ring_frames;
      size_t run = (size_t)(ring_frames - at);
      if (run > n - done) run = n - done;
      memcpy(ring + at * src_ch, s + (off + done) * src_ch, run * src_ch * sizeof(int32_t));
      done += run;
    }
    atomic_store_explicit(&wpos, w + n, memory_order_release);
    pthread_mutex_unlock(&pmu);
    off += n;
    maybe_start();
    if (off < frames) usleep(5000);
  }
}

static void on_command(const char *line) {
  if (!strncmp(line, "stop", 4)) {
    uint32_t g = (uint32_t)strtoul(line + 4, NULL, 10);
    pthread_mutex_lock(&dmu);
    atomic_store(&discard_below, g);
    atomic_store(&cur_gen, g);
    device_stop();
    flush();
    paused = 0;
    pthread_mutex_unlock(&dmu);
    emit("{\"ev\":\"stopped\",\"gen\":%u}", g);
  } else if (!strcmp(line, "pause")) {
    pthread_mutex_lock(&dmu);
    paused = 1;
    device_stop();
    pthread_mutex_unlock(&dmu);
  } else if (!strcmp(line, "resume")) {
    pthread_mutex_lock(&dmu);
    paused = 0;
    if (buffered() > 0 || atomic_load(&draining)) device_start();
    pthread_mutex_unlock(&dmu);
  } else if (!strcmp(line, "quit")) {
    pthread_mutex_lock(&dmu);
    device_stop();
    pthread_mutex_unlock(&dmu);
    exit(0);
  }
}

/* fd 3, the clock, the drain, and keeping hold of the DAC. */
static void *control_thread(void *arg) {
  (void)arg;
  line_reader lr = { .len = 0 };
  char line[256];
  uint64_t last_pos = UINT64_MAX, last_emit = 0, last_check = 0;
  for (;;) {
    struct pollfd p = { .fd = CTL_FD, .events = POLLIN };
    if (poll(&p, 1, 50) > 0) {
      if (ctl_read(&lr) < 0) exit(0);
      while (ctl_next(&lr, line, sizeof line)) on_command(line);
    }
    if (atomic_load(&drained_flag)) {
      usleep(60000);   /* the last buffer is still on its way out */
      pthread_mutex_lock(&dmu);
      int was = atomic_exchange(&drained_flag, 0);
      if (was && atomic_load(&draining)) {
        device_stop();
        atomic_store(&draining, 0);
        atomic_store(&played, 0);
        pthread_mutex_unlock(&dmu);
        emit("{\"ev\":\"drained\",\"gen\":%u}", atomic_load(&cur_gen));
      } else pthread_mutex_unlock(&dmu);
    }
    if (atomic_exchange(&underrun_flag, 0) && running) emit("{\"ev\":\"underrun\"}");
    uint64_t t = now_ms();
    if (t - last_emit >= 200) {
      uint64_t pf = atomic_load(&played);
      if (pf != last_pos) { emit("{\"ev\":\"pos\",\"gen\":%u,\"frames\":%llu}", atomic_load(&cur_gen), (unsigned long long)pf); last_pos = pf; }
      last_emit = t;
    }
    if (t - last_check >= 2000) {
      last_check = t;
      AudioObjectID now = find_device(dev_uid);
      UInt32 alive = 1;
      if (now != kAudioObjectUnknown)
        get_prop(now, kAudioDevicePropertyDeviceIsAlive, kAudioObjectPropertyScopeGlobal, MAIN_ELEMENT, sizeof alive, &alive);
      if (now != dev || !alive) gone();
      pthread_mutex_lock(&dmu);
      if (hog_owner(dev) != mypid) {
        device_stop();
        have_hog = take_hog();
      }
      pthread_mutex_unlock(&dmu);
      report_status();
    }
  }
  return NULL;
}

static int cmd_hold(const char *uid) {
  snprintf(dev_uid, sizeof dev_uid, "%s", uid);
  mypid = getpid();
  dev = find_device(uid);
  if (dev == kAudioObjectUnknown) gone();
  if (AudioDeviceCreateIOProcID(dev, ioproc, NULL, &procid) != noErr || !procid) {
    emit("{\"ev\":\"error\",\"msg\":\"Core Audio wouldn't play to this device\"}");
    return 1;
  }
  have_hog = take_hog();
  report_status();

  pthread_t th;
  pthread_create(&th, NULL, control_thread, NULL);

  size_t cap = 0;
  int32_t *buf = NULL;
  frame_hdr h;
  while (read_hdr(&h) == 0) {
    if (cap < h.len + 4) { cap = h.len + 4; buf = realloc(buf, cap); if (!buf) return 1; }
    if (h.len && read_full(0, buf, h.len) < 0) break;
    if (h.gen < atomic_load(&discard_below)) continue;
    atomic_store(&cur_gen, h.gen);
    if (h.type == 'F') {
      unsigned r = 0, c = 0;
      ((char *)buf)[h.len] = 0;
      sscanf((char *)buf, "%u %u", &r, &c);
      configure(h.gen, r, c ? c : 2);
    } else if (h.type == 'P') {
      if (configured && src_ch) push(buf, h.len / (4 * src_ch), h.gen);
    } else if (h.type == 'D') {
      atomic_store(&draining, 1);
      maybe_start();
      pthread_mutex_lock(&dmu);
      int empty = buffered() == 0;
      if (empty) { device_stop(); atomic_store(&draining, 0); atomic_store(&played, 0); }
      pthread_mutex_unlock(&dmu);
      if (empty) emit("{\"ev\":\"drained\",\"gen\":%u}", h.gen);
    }
  }
  pthread_mutex_lock(&dmu);
  device_stop();
  pthread_mutex_unlock(&dmu);
  return 0;
}

int main(int argc, char **argv) {
  signal(SIGPIPE, SIG_IGN);
  if (argc >= 2 && !strcmp(argv[1], "list")) return cmd_list();
  if (argc >= 3 && !strcmp(argv[1], "hold")) return cmd_hold(argv[2]);
  fprintf(stderr, "usage: dachelper list | dachelper hold <device UID>\n");
  return 2;
}
