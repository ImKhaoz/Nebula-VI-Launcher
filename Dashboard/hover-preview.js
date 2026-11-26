'use strict';

class GameHoverPreview {
  constructor(lookUpArt, requestMedia, onPreview) {
    this.lookUpArt = lookUpArt;
    this.requestMedia = requestMedia;
    this.onPreview = onPreview;
    this.still = document.getElementById('hover-backdrop');
    this.video = document.getElementById('hover-video');
    this.playerVideo = document.getElementById('media-player');
    this.reduced = matchMedia('(prefers-reduced-motion: reduce)');
    this.enabled = true;
    this.video.muted = true;
    this.video.defaultMuted = true;
    window.addEventListener('blur', () => this.video.pause());
    window.addEventListener('focus', () => this.resume());
    document.addEventListener('visibilitychange', () => {
      if (document.hidden) this.video.pause();
      else this.resume();
    });
    this.reduced.addEventListener('change', () => { if (this.reduced.matches) this.stopClip(); });
    this.playerVideo.addEventListener('play', () => this.video.pause());
    this.playerVideo.addEventListener('pause', () => this.resume());
  }

  enter(tile, id, pointerType) {
    if (pointerType === 'touch' || id === this.id) return;
    this.start(id, tile);
  }

  select(id) {
    this.selected = id;
    if (!id) { this.clear(); return; }
    if (id === this.id) {
      this.tile?.classList.remove('is-hovered');
      this.tile = null;
      return;
    }
    this.start(id);
  }

  start(id, tile = null) {
    this.clear();
    this.tile = tile;
    this.id = id;
    tile?.classList.add('is-hovered');
    this.artTimer = setTimeout(() => {
      this.artReady = true;
      this.showArt();
      if (this.tile) this.onPreview(id);
    }, 220);
    this.clipTimer = setTimeout(() => {
      this.clipReady = true;
      this.maybePlay();
    }, 850);
  }

  leave(tile) {
    if (tile !== this.tile) return;
    if (this.selected) this.select(this.selected);
    else this.clear();
  }

  holdForLaunch(id) {
    this.select(id);
    this.launchHeld = true;
    this.video.pause();
    if (!this.video.classList.contains('preview-visible')) this.stopClip();
  }

  resume() {
    if (this.launchHeld || this.overlayOpen || document.hidden || !document.hasFocus() || !this.playerVideo.paused)
      return;
    if (this.clipStarted && this.video.classList.contains('preview-visible'))
      this.video.play().catch(() => {});
    else this.maybePlay();
  }

  setOverlayOpen(open) {
    this.overlayOpen = open;
    if (open) this.video.pause();
    else this.resume();
  }

  setEnabled(enabled) {
    this.enabled = enabled;
    if (!enabled) this.stopClip();
    else this.resume();
  }

  refresh(id) {
    if (id !== this.id) return;
    this.showArt();
    this.maybePlay();
  }

  showArt() {
    if (!this.artReady || !this.id) return;
    const hero = this.lookUpArt(this.id)?.Hero;
    if (!hero) return;
    clearTimeout(this.clearStillTimer);
    const reveal = () => {
      if (this.still.naturalWidth >= 920 && this.still.naturalHeight >= 360)
        this.still.classList.add('preview-visible');
    };
    if (this.still.getAttribute('src') === hero) {
      if (this.still.complete) reveal();
      else this.still.onload = reveal;
      return;
    }
    const id = this.id;
    this.still.onload = () => {
      if (id === this.id) reveal();
    };
    this.still.src = hero;
  }

  maybePlay() {
    if (!this.clipReady || this.clipStarted || this.clipFailed || !this.id || !this.enabled || this.launchHeld || this.overlayOpen ||
        this.reduced.matches || document.hidden || !document.hasFocus() || !this.playerVideo.paused) return;
    const media = this.requestMedia(this.id);
    const clip = media?.Videos[0];
    if (!clip) return;

    this.clipStarted = true;
    const playback = this.playback = Symbol();
    const failed = () => {
      if (playback !== this.playback) return;
      this.clipFailed = true;
      this.stopClip();
    };
    this.video.onerror = failed;
    this.video.onplaying = () => {
      if (playback !== this.playback) return;
      this.video.classList.add('preview-visible');
      if (this.launchHeld || this.overlayOpen || document.hidden || !document.hasFocus() || !this.playerVideo.paused) this.video.pause();
    };
    if (!clip.Adaptive) {
      this.video.src = clip.Url;
    } else if (window.Hls?.isSupported()) {
      this.player = new window.Hls({ enableWorker: false, capLevelToPlayerSize: true, maxBufferLength: 10 });
      this.player.on(window.Hls.Events.ERROR, (_, event) => { if (event.fatal) failed(); });
      this.player.loadSource(clip.Url);
      this.player.attachMedia(this.video);
    } else if (this.video.canPlayType('application/vnd.apple.mpegurl')) {
      this.video.src = clip.Url;
    } else {
      this.stopClip();
      return;
    }
    this.video.play().catch(error => {
      if (error.name === 'AbortError' && playback === this.playback) this.stopClip();
      else failed();
    });
  }

  stopClip() {
    this.playback = null;
    this.clipStarted = false;
    this.video.classList.remove('preview-visible');
    this.video.onplaying = null;
    this.video.onerror = null;
    this.player?.destroy();
    this.player = null;
    this.video.pause();
    if (this.video.hasAttribute('src')) {
      this.video.removeAttribute('src');
      this.video.load();
    }
  }

  clear() {
    clearTimeout(this.artTimer);
    clearTimeout(this.clipTimer);
    this.tile?.classList.remove('is-hovered');
    this.id = null;
    this.tile = null;
    this.artReady = false;
    this.clipReady = false;
    this.clipStarted = false;
    this.launchHeld = false;
    this.clipFailed = false;
    this.still.classList.remove('preview-visible');
    this.still.onload = null;
    clearTimeout(this.clearStillTimer);
    this.clearStillTimer = setTimeout(() => this.still.removeAttribute('src'), 410);
    this.stopClip();
  }
}
