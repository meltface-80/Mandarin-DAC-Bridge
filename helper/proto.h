/*
 * proto.h — what the bridge (Node) and a DAC helper say to each other.
 *
 * One helper runs per DAC. It takes the DAC for itself (Core Audio hog mode
 * on a Mac, the ALSA hw device held open on Linux) and keeps it until it
 * exits, so no other program can play to it.
 *
 * stdin carries frames, in order:
 *
 *   type (1 byte) | gen (uint32 LE) | length (uint32 LE) | payload
 *
 *   'F'  format: payload "rate channels" (ASCII). Sets the DAC to that rate.
 *   'P'  PCM: interleaved signed 32-bit little-endian samples.
 *   'D'  drain: play what is buffered, then stop and report "drained".
 *
 * fd 3 carries commands that must not wait behind the audio, one a line:
 *
 *   stop <gen>   drop everything buffered; frames older than <gen> are ignored
 *   pause        stop the clock where it is
 *   resume
 *   quit
 *
 * stdout carries events, one JSON object a line:
 *
 *   {"ev":"status","exclusive":true|false,"holder":<pid>,"msg":"…"}
 *   {"ev":"format","gen":G,"ok":true,"rate":R,"channels":C,"physical":"…"}
 *   {"ev":"pos","gen":G,"frames":N}       frames played since the last F, stop or drained
 *   {"ev":"drained","gen":G}
 *   {"ev":"stopped","gen":G}
 *   {"ev":"underrun"}
 *   {"ev":"gone"}                          the DAC was unplugged (exit code 3)
 */
#ifndef DACHELPER_PROTO_H
#define DACHELPER_PROTO_H

#include <errno.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/time.h>
#include <unistd.h>

#define CTL_FD 3
#define MAX_PAYLOAD (16u << 20)

static pthread_mutex_t emit_mu = PTHREAD_MUTEX_INITIALIZER;

static void emit(const char *fmt, ...) {
  va_list ap;
  pthread_mutex_lock(&emit_mu);
  va_start(ap, fmt);
  vfprintf(stdout, fmt, ap);
  va_end(ap);
  fputc('\n', stdout);
  fflush(stdout);
  pthread_mutex_unlock(&emit_mu);
}

/* A string made safe to sit inside JSON quotes. */
static const char *jstr(const char *in, char *out, size_t cap) {
  size_t o = 0;
  for (const unsigned char *p = (const unsigned char *)(in ? in : ""); *p && o + 7 < cap; p++) {
    if (*p == '"' || *p == '\\') { out[o++] = '\\'; out[o++] = (char)*p; }
    else if (*p < 0x20) o += (size_t)snprintf(out + o, cap - o, "\\u%04x", *p);
    else out[o++] = (char)*p;
  }
  out[o] = 0;
  return out;
}

/* 0 when all n bytes came; -1 at the end of the input. */
static int read_full(int fd, void *buf, size_t n) {
  unsigned char *p = buf;
  while (n) {
    ssize_t r = read(fd, p, n);
    if (r == 0) return -1;
    if (r < 0) { if (errno == EINTR) continue; return -1; }
    p += r; n -= (size_t)r;
  }
  return 0;
}

static uint32_t le32(const unsigned char *b) {
  return (uint32_t)b[0] | ((uint32_t)b[1] << 8) | ((uint32_t)b[2] << 16) | ((uint32_t)b[3] << 24);
}

typedef struct { char type; uint32_t gen; uint32_t len; } frame_hdr;

/* The next frame's header; -1 at the end of the input. */
static int read_hdr(frame_hdr *h) {
  unsigned char b[9];
  if (read_full(0, b, 9) < 0) return -1;
  h->type = (char)b[0];
  h->gen = le32(b + 1);
  h->len = le32(b + 5);
  return h->len > MAX_PAYLOAD ? -1 : 0;
}

/* Lines from fd 3, handed out one at a time. */
typedef struct { char buf[4096]; size_t len; } line_reader;

/* 1 with a line in out, 0 with none yet, -1 when fd 3 has closed. */
static int ctl_read(line_reader *lr) {
  if (lr->len >= sizeof lr->buf - 1) lr->len = 0;
  ssize_t r = read(CTL_FD, lr->buf + lr->len, sizeof lr->buf - 1 - lr->len);
  if (r == 0) return -1;
  if (r < 0) return errno == EINTR || errno == EAGAIN ? 0 : -1;
  lr->len += (size_t)r;
  return 1;
}

static int ctl_next(line_reader *lr, char *out, size_t cap) {
  char *nl = memchr(lr->buf, '\n', lr->len);
  if (!nl) return 0;
  size_t n = (size_t)(nl - lr->buf);
  size_t c = n < cap - 1 ? n : cap - 1;
  memcpy(out, lr->buf, c);
  out[c] = 0;
  memmove(lr->buf, nl + 1, lr->len - n - 1);
  lr->len -= n + 1;
  return 1;
}

static uint64_t now_ms(void) {
  struct timeval tv;
  gettimeofday(&tv, NULL);
  return (uint64_t)tv.tv_sec * 1000 + (uint64_t)tv.tv_usec / 1000;
}

#endif
