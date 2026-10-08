/*
 * dachelper-linux.c — holds one USB DAC through ALSA's hw device and plays
 * to it bit-perfect. See proto.h for what it is told and what it says.
 *
 *   dachelper hold hw:CARD=D90,DEV=0
 *
 * An ALSA hw device can be open in one program at a time, so holding it open
 * (even while nothing plays) keeps every other program off it. hw does no
 * conversion: the DAC is set to the rate of each track, and the samples
 * reach it unchanged (a 24-bit DAC gets the top 24 bits of the 32 sent,
 * which is all a 24-bit source has).
 *
 * Build: gcc -O2 -o dachelper dachelper-linux.c -lasound -lpthread
 */
#define _GNU_SOURCE
#include <alsa/asoundlib.h>
#include <poll.h>
#include <sched.h>
#include <signal.h>
#include "proto.h"

static const char *devname;
static snd_pcm_t *pcm;
static int configured, paused, hw_paused, can_pause;
static unsigned rate, src_ch, dev_ch;
static snd_pcm_format_t fmt;
static unsigned sample_bytes;
static uint32_t cur_gen, discard_below;
static uint64_t written;           /* frames handed to ALSA since the last F or stop */
static unsigned char *outbuf;
static size_t outcap;
static int32_t *inbuf;
static size_t incap;

static void gone(void) { emit("{\"ev\":\"gone\"}"); exit(3); }

static int is_gone(int err) { return err == -ENODEV || err == -EBADFD || err == -ENOENT || err == -ENXIO; }

static int try_open(void) {
  int err = snd_pcm_open(&pcm, devname, SND_PCM_STREAM_PLAYBACK, SND_PCM_NONBLOCK);
  if (err < 0) { pcm = NULL; return err; }
  snd_pcm_nonblock(pcm, 0);
  return 0;
}

static uint64_t played(void) {
  if (!pcm || !configured) return 0;
  snd_pcm_sframes_t d = 0;
  snd_pcm_state_t st = snd_pcm_state(pcm);
  if (st == SND_PCM_STATE_RUNNING || st == SND_PCM_STATE_PAUSED || st == SND_PCM_STATE_PREPARED || st == SND_PCM_STATE_DRAINING) {
    if (snd_pcm_delay(pcm, &d) < 0) d = 0;
  }
  if (d < 0) d = 0;
  return (uint64_t)d > written ? 0 : written - (uint64_t)d;
}

static const snd_pcm_format_t CANDIDATES[] = { SND_PCM_FORMAT_S32_LE, SND_PCM_FORMAT_S24_LE, SND_PCM_FORMAT_S24_3LE, SND_PCM_FORMAT_S16_LE };

static void configure(uint32_t gen, unsigned r, unsigned ch) {
  char why[256], msg[600];
  int err;
  configured = 0;
  snd_pcm_drop(pcm);
  snd_pcm_hw_free(pcm);
  snd_pcm_hw_params_t *hw;
  snd_pcm_hw_params_alloca(&hw);
  if ((err = snd_pcm_hw_params_any(pcm, hw)) < 0) { snprintf(why, sizeof why, "the DAC's settings can't be read: %s", snd_strerror(err)); goto fail; }
  snd_pcm_hw_params_set_rate_resample(pcm, hw, 0);
  if ((err = snd_pcm_hw_params_set_access(pcm, hw, SND_PCM_ACCESS_RW_INTERLEAVED)) < 0) { snprintf(why, sizeof why, "interleaved access: %s", snd_strerror(err)); goto fail; }
  fmt = SND_PCM_FORMAT_UNKNOWN;
  for (size_t i = 0; i < sizeof CANDIDATES / sizeof *CANDIDATES; i++)
    if (snd_pcm_hw_params_test_format(pcm, hw, CANDIDATES[i]) == 0) { fmt = CANDIDATES[i]; break; }
  if (fmt == SND_PCM_FORMAT_UNKNOWN) { snprintf(why, sizeof why, "the DAC takes none of S32_LE, S24_LE, S24_3LE, S16_LE"); goto fail; }
  snd_pcm_hw_params_set_format(pcm, hw, fmt);
  sample_bytes = (unsigned)snd_pcm_format_physical_width(fmt) / 8;
  unsigned cmin = 0, cmax = 0;
  snd_pcm_hw_params_get_channels_min(hw, &cmin);
  snd_pcm_hw_params_get_channels_max(hw, &cmax);
  dev_ch = ch < cmin ? cmin : ch;
  if (dev_ch > cmax) { snprintf(why, sizeof why, "the DAC takes at most %u channels", cmax); goto fail; }
  if ((err = snd_pcm_hw_params_set_channels(pcm, hw, dev_ch)) < 0) { snprintf(why, sizeof why, "%u channels: %s", dev_ch, snd_strerror(err)); goto fail; }
  if ((err = snd_pcm_hw_params_set_rate(pcm, hw, r, 0)) < 0) { snprintf(why, sizeof why, "the DAC doesn't take %u Hz", r); goto fail; }
  unsigned buf_us = 500000, per_us = 50000;
  snd_pcm_hw_params_set_period_time_near(pcm, hw, &per_us, NULL);
  snd_pcm_hw_params_set_buffer_time_near(pcm, hw, &buf_us, NULL);
  if ((err = snd_pcm_hw_params(pcm, hw)) < 0) { snprintf(why, sizeof why, "%s", snd_strerror(err)); goto fail; }
  can_pause = snd_pcm_hw_params_can_pause(hw);
  if ((err = snd_pcm_prepare(pcm)) < 0) { snprintf(why, sizeof why, "%s", snd_strerror(err)); goto fail; }
  rate = r; src_ch = ch; written = 0; configured = 1;
  emit("{\"ev\":\"format\",\"gen\":%u,\"ok\":true,\"rate\":%u,\"channels\":%u,\"physical\":\"%s, %u ch\"}",
       gen, r, ch, snd_pcm_format_name(fmt), dev_ch);
  return;
fail:
  if (is_gone(err)) gone();
  emit("{\"ev\":\"format\",\"gen\":%u,\"ok\":false,\"msg\":\"%s\"}", gen, jstr(why, msg, sizeof msg));
}

static void write_frames(const int32_t *in, size_t frames) {
  size_t frame_bytes = (size_t)dev_ch * sample_bytes;
  if (outcap < frames * frame_bytes) {
    outcap = frames * frame_bytes;
    outbuf = realloc(outbuf, outcap);
    if (!outbuf) { emit("{\"ev\":\"error\",\"msg\":\"out of memory\"}"); exit(1); }
  }
  unsigned char *o = outbuf;
  for (size_t f = 0; f < frames; f++) {
    for (unsigned c = 0; c < dev_ch; c++) {
      int32_t v = c < src_ch ? in[f * src_ch + c] : 0;
      switch (fmt) {
        case SND_PCM_FORMAT_S32_LE: memcpy(o, &v, 4); o += 4; break;
        case SND_PCM_FORMAT_S24_LE: { int32_t x = v >> 8; memcpy(o, &x, 4); o += 4; break; }
        case SND_PCM_FORMAT_S24_3LE: { int32_t x = v >> 8; o[0] = (unsigned char)x; o[1] = (unsigned char)(x >> 8); o[2] = (unsigned char)(x >> 16); o += 3; break; }
        default: { int16_t x = (int16_t)(v >> 16); memcpy(o, &x, 2); o += 2; break; }
      }
    }
  }
  size_t off = 0;
  while (off < frames) {
    snd_pcm_sframes_t n = snd_pcm_writei(pcm, outbuf + off * frame_bytes, frames - off);
    if (n == -EAGAIN) continue;
    if (n < 0) {
      if (is_gone((int)n)) gone();
      if (n == -EPIPE) emit("{\"ev\":\"underrun\"}");
      if (snd_pcm_recover(pcm, (int)n, 1) < 0) { emit("{\"ev\":\"error\",\"msg\":\"%s\"}", snd_strerror((int)n)); return; }
      continue;
    }
    off += (size_t)n;
  }
  written += frames;
}

static void on_frame(frame_hdr *h) {
  if (incap < h->len + 4) { incap = h->len + 4; inbuf = realloc(inbuf, incap); }
  if (!inbuf || (h->len && read_full(0, inbuf, h->len) < 0)) exit(0);
  if (h->gen < discard_below) return;
  cur_gen = h->gen;
  if (h->type == 'F') {
    unsigned r = 0, c = 0;
    ((char *)inbuf)[h->len] = 0;
    sscanf((char *)inbuf, "%u %u", &r, &c);
    configure(h->gen, r, c ? c : 2);
  } else if (h->type == 'P') {
    if (configured && src_ch) write_frames(inbuf, h->len / (4 * src_ch));
  } else if (h->type == 'D') {
    if (configured) {
      int err = snd_pcm_drain(pcm);
      if (is_gone(err)) gone();
      snd_pcm_prepare(pcm);
      written = 0;
    }
    emit("{\"ev\":\"drained\",\"gen\":%u}", h->gen);
  }
}

static void on_command(const char *line) {
  if (!strncmp(line, "stop", 4)) {
    uint32_t g = (uint32_t)strtoul(line + 4, NULL, 10);
    discard_below = g; cur_gen = g;
    if (pcm && configured) { snd_pcm_drop(pcm); snd_pcm_prepare(pcm); }
    written = 0; paused = 0; hw_paused = 0;
    emit("{\"ev\":\"stopped\",\"gen\":%u}", g);
  } else if (!strcmp(line, "pause")) {
    if (paused) return;
    paused = 1;
    if (pcm && configured && snd_pcm_state(pcm) == SND_PCM_STATE_RUNNING) {
      if (can_pause && snd_pcm_pause(pcm, 1) == 0) hw_paused = 1;
      else { uint64_t p = played(); snd_pcm_drop(pcm); snd_pcm_prepare(pcm); written = p; }
    }
  } else if (!strcmp(line, "resume")) {
    if (hw_paused && pcm) snd_pcm_pause(pcm, 0);
    paused = 0; hw_paused = 0;
  } else if (!strcmp(line, "quit")) {
    if (pcm) { snd_pcm_drop(pcm); snd_pcm_close(pcm); }
    exit(0);
  }
}

int main(int argc, char **argv) {
  if (argc < 3 || strcmp(argv[1], "hold")) {
    fprintf(stderr, "usage: dachelper hold <alsa device>\n");
    return 2;
  }
  devname = argv[2];
  signal(SIGPIPE, SIG_IGN);
  struct sched_param sp = { .sched_priority = 40 };
  sched_setscheduler(0, SCHED_FIFO, &sp);   /* best effort: needs CAP_SYS_NICE */

  line_reader lr = { .len = 0 };
  char line[256], msg[300];
  int last_status = -1, failures = 0;
  uint64_t last_pos = 0, last_emit = 0;

  for (;;) {
    if (!pcm) {
      int err = try_open();
      if (err < 0) {
        if (err != -EBUSY && ++failures >= 5) gone();
        if (last_status != 0) {
          emit("{\"ev\":\"status\",\"exclusive\":false,\"holder\":0,\"msg\":\"%s\"}",
               jstr(err == -EBUSY ? "another program is using the DAC" : snd_strerror(err), msg, sizeof msg));
          last_status = 0;
        }
        struct pollfd w = { .fd = CTL_FD, .events = POLLIN };
        if (poll(&w, 1, 2000) > 0) {
          if (ctl_read(&lr) < 0) return 0;
          while (ctl_next(&lr, line, sizeof line)) on_command(line);
        }
        continue;
      }
      failures = 0;
      last_status = 1;
      emit("{\"ev\":\"status\",\"exclusive\":true,\"holder\":%d,\"msg\":\"\"}", (int)getpid());
    }

    struct pollfd p[2] = { { .fd = CTL_FD, .events = POLLIN }, { .fd = 0, .events = POLLIN } };
    int r = poll(p, paused ? 1 : 2, 200);
    if (r > 0 && (p[0].revents & (POLLIN | POLLHUP))) {
      if (ctl_read(&lr) < 0) return 0;
      while (ctl_next(&lr, line, sizeof line)) on_command(line);
    }
    if (r > 0 && !paused && (p[1].revents & (POLLIN | POLLHUP))) {
      frame_hdr h;
      if (read_hdr(&h) < 0) return 0;
      on_frame(&h);
    }
    uint64_t t = now_ms();
    if (t - last_emit >= 200) {
      uint64_t pf = played();
      if (pf != last_pos) { emit("{\"ev\":\"pos\",\"gen\":%u,\"frames\":%llu}", cur_gen, (unsigned long long)pf); last_pos = pf; }
      last_emit = t;
    }
  }
}
