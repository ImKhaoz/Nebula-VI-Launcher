'use strict';

class SteamVideoShelf {
  constructor(send) {
    this.send = send;
    this.byId = id => document.getElementById(id);
    this.section = this.byId('media-shelf');
    this.video = this.byId('media-player');
    this.poster = this.byId('media-poster');
    this.play = this.byId('media-play');
    this.status = this.byId('media-status');
    this.list = this.byId('media-playlist');
    this.layout = this.byId('media-layout');
    this.store = this.byId('media-store');
    this.cache = new Map();
    this.pending = new Set();
    this.media = null;
    this.game = null;
    this.index = 0;
    this.player = null;
    this.session = 0;

    this.store.addEventListener('click', () => {
      if (this.game) this.send('steam-store', this.game.id);
    });
    this.play.addEventListener('click', () => this.startTrailer());
    const observer = new IntersectionObserver(([entry]) => {
      this.near = entry.isIntersecting;
      this.request();
    }, { rootMargin: '280px 0px' });
    observer.observe(this.section);
  }

  select(id, title, cardKnown, card) {
    const changed = this.game?.id !== id;
    this.game = id ? { id, title, cardKnown, card } : null;
    this.section.hidden = !this.game;
    this.store.hidden = !card;
    this.byId('media-game').textContent = title ?? '';
    if (changed) {
      this.index = 0;
      this.stop();
    }
    const next = this.cache.get(id) ?? null;
    if (this.media !== next || changed) {
      this.media = next;
      this.renderPlaylist();
    }
    if (!this.media) this.renderStatus();
    this.request();
  }

  setExpanded(expanded) {
    this.expanded = expanded;
    if (!expanded && !this.video.hidden) this.stop();
    if (expanded) this.request();
  }

  request() {
    if (!this.expanded || !this.near || !this.game?.card) return;
    if (this.cache.has(this.game.id) || this.pending.has(this.game.id)) return;
    this.requestFor(this.game.id);
    this.renderStatus();
  }

  requestFor(id) {
    if (!this.cache.has(id) && !this.pending.has(id)) {
      this.pending.add(id);
      this.send('media', id);
    }
    return this.cache.get(id);
  }

  accept(id, media) {
    this.pending.delete(id);
    this.cache.set(id, media);
    if (id !== this.game?.id) return;
    this.media = media;
    this.index = 0;
    this.renderPlaylist();
  }

  renderStatus() {
    let text = '';
    if (this.media?.Unavailable) text = 'Steam videos are temporarily unavailable.';
    else if (this.media && !this.media.Videos.length) text = 'This game has no playable videos listed on Steam.';
    else if (!this.media) {
      text = this.game?.card ? 'Loading Steam videos…' : this.game?.cardKnown
        ? 'No matching Steam listing for this game.' : 'Finding the game’s Steam listing…';
    }
    this.showStatus(text);
  }

  showStatus(message) {
    this.status.textContent = message;
    this.status.hidden = !message;
  }

  renderPlaylist() {
    const clips = this.media?.Videos ?? [];
    this.layout.hidden = !clips.length;
    this.list.replaceChildren(...clips.map((clip, index) => {
      const button = document.createElement('button');
      button.className = 'video-choice';
      button.addEventListener('click', () => this.stageTrailer(index));
      const thumb = document.createElement('span');
      thumb.className = 'video-thumb';
      if (clip.Thumbnail || this.game?.card?.Header) {
        const image = document.createElement('img');
        image.loading = 'lazy';
        image.alt = '';
        image.src = clip.Thumbnail ?? this.game.card.Header;
        image.onerror = () => image.remove();
        thumb.append(image);
      }
      const title = document.createElement('span');
      title.className = 'video-choice-title';
      title.textContent = clip.Title;
      button.append(thumb, title);
      return button;
    }));
    this.stageTrailer(this.index);
  }

  stageTrailer(index) {
    this.stop();
    this.index = index;
    const clip = this.media?.Videos[index];
    this.byId('media-title').textContent = clip?.Title ?? '';
    this.play.hidden = !clip;
    this.play.setAttribute('aria-label', clip ? `Play ${clip.Title}` : 'Play trailer');
    this.poster.hidden = true;
    this.poster.removeAttribute('src');
    this.poster.onload = () => { this.poster.hidden = false; };
    this.poster.onerror = () => { this.poster.hidden = true; };
    const image = clip?.Thumbnail ?? this.game?.card?.Header;
    if (clip && image) this.poster.src = image;
    Array.from(this.list.children).forEach((button, number) => {
      button.setAttribute('aria-pressed', String(number === index));
    });
    this.renderStatus();
  }

  stop() {
    this.session++;
    this.player?.destroy();
    this.player = null;
    this.video.onloadeddata = null;
    this.video.onerror = null;
    this.video.pause();
    if (this.video.hasAttribute('src')) {
      this.video.removeAttribute('src');
      this.video.load();
    }
    this.video.hidden = true;
    this.play.hidden = !this.media?.Videos[this.index];
  }

  startTrailer() {
    const clip = this.media?.Videos[this.index];
    if (!clip) return;
    this.stop();
    const session = this.session;
    this.play.hidden = true;
    this.video.hidden = false;
    const poster = clip.Thumbnail ?? this.game?.card?.Header;
    if (poster) this.video.poster = poster;
    else this.video.removeAttribute('poster');
    this.video.focus({ preventScroll: true });
    this.showStatus('Buffering trailer…');
    const failed = () => {
      if (this.session !== session) return;
      this.stop();
      this.showStatus('This video could not play. Try again or open the Steam store.');
    };
    this.video.onerror = failed;
    this.video.onloadeddata = () => { if (this.session === session) this.showStatus(''); };
    if (!clip.Adaptive) {
      this.video.src = clip.Url;
    } else if (window.Hls?.isSupported()) {
      // The Steam CDN supports CORS. Keep the decoder in this document's CSP.
      this.player = new window.Hls({ enableWorker: false, capLevelToPlayerSize: true, maxBufferLength: 18 });
      this.player.on(window.Hls.Events.ERROR, (_, event) => { if (event.fatal) failed(); });
      this.player.loadSource(clip.Url);
      this.player.attachMedia(this.video);
    } else if (this.video.canPlayType('application/vnd.apple.mpegurl')) {
      this.video.src = clip.Url;
    } else {
      failed();
      return;
    }
    this.video.play().catch(() => {
      if (this.session === session) this.showStatus('Press Play in the video controls to continue.');
    });
  }
}
