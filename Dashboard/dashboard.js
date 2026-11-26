'use strict';

const byId = id => document.getElementById(id);
const host = window.chrome?.webview;
const rail = byId('rail');
const search = byId('search');
const sort = byId('sort');
const dialog = byId('remove-dialog');
const primary = byId('feature-action');
const secondary = byId('feature-secondary');
const activityToggle = byId('activity-toggle');
const activityContent = byId('activity-content');
const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');
const mediaShelf = new SteamVideoShelf(send);
const gtaClip = 'https://media-rockstargames-com.akamaized.net/VI/downloads/videos/GTAVI_Official_Cover_Art_Landscape/GTAVI_Official_Cover_Art_Landscape.mp4';
const preview = new GameHoverPreview(id => artworkFor(games.find(game => game.id === id)),
  id => games.find(game => game.id === id)?.leonida
    ? { Videos: [{ Url: gtaClip }] }
    : mediaShelf.requestFor(id),
  id => {
    const game = getVisibleGames().find(entry => entry.id === id);
    if (game) setFeature(game);
  });
const settings = new NebulaSettings(send, confirmRemovalOf, () => preview.setOverlayOpen(true));
byId('settings-screen').addEventListener('settings-closed', () => preview.setOverlayOpen(false));
const views = [...document.querySelectorAll('[data-view]')];
const actions = [
  { id: 'add-windows', title: 'Add Windows game', symbol: '+', kind: 'add', detail: 'Import a PC executable' },
  { id: 'add-xbox', title: 'Add Xbox title', symbol: '', kind: 'xbox-import', detail: 'Add a console game' },
  { id: 'diagnostics', title: 'Diagnostics', symbol: '⌁', kind: 'diagnostics', detail: 'Test runtime and shaders' }
];
let games = [];
let diagnosticsAvailable = false;
let gtaOnlyEdition = false;
let selectedVictoryInstalled = false;
const gameIcons = new Map();
const gameCatalog = new Map();
const pendingCatalog = new Set();
const activityCache = new Map();
const pendingActivity = new Set();
let activeView = 'games';
let selectedId = null;
let featureAnimation;
let activityId = null;
let pendingRemoval = null;
let toastTimer;
const toast = byId('toast');
const toastText = document.createElement('span');
toastText.setAttribute('role', 'status');
const toastDismiss = document.createElement('button');
toastDismiss.type = 'button';
toastDismiss.className = 'toast-dismiss';
toastDismiss.setAttribute('aria-label', 'Dismiss notification');
toastDismiss.textContent = '×';
toast.removeAttribute('role');
toast.removeAttribute('aria-live');
toast.replaceChildren(toastText, toastDismiss);
toastDismiss.addEventListener('click', hideToast);

function hideToast() {
  clearTimeout(toastTimer);
  toast.hidden = true;
}
let audioContext;
let lastSound = 0;
const soundToggle = byId('sound-toggle');

function playInterfaceSound(confirm) {
  if (soundToggle.getAttribute('aria-pressed') !== 'true' ||
      document.hidden || performance.now() - lastSound < 65 || !window.AudioContext) return;
  lastSound = performance.now();
  try {
    audioContext ??= new AudioContext();
    if (audioContext.state === 'suspended') {
      audioContext.resume().catch(() => {});
      if (!confirm) return;
    }
    const start = audioContext.currentTime;
    const notes = confirm ? [523.25, 783.99] : [659.25];
    notes.forEach((frequency, index) => {
      const at = start + index * .045;
      const tone = audioContext.createOscillator();
      const envelope = audioContext.createGain();
      tone.frequency.value = frequency;
      envelope.gain.setValueAtTime(.0001, at);
      envelope.gain.exponentialRampToValueAtTime(confirm ? .044 : .021, at + .012);
      envelope.gain.exponentialRampToValueAtTime(.0001, at + .12);
      tone.connect(envelope).connect(audioContext.destination);
      tone.start(at);
      tone.stop(at + .125);
    });
  } catch {
    // The launcher stays usable when audio output is unavailable.
  }
}

document.addEventListener('click', event => {
  const button = event.target.closest('button');
  if (event.isTrusted && button && !button.disabled && button !== soundToggle)
    playInterfaceSound(true);
});
rail.addEventListener('focusin', event => {
  if (event.target.matches('.tile:focus-visible') && !event.target.matches(':active'))
    playInterfaceSound(false);
});
const launchSplash = byId('prelaunch');
const launchProgress = byId('prelaunch-progress');
const launchButton = byId('prelaunch-cancel');
let launchPhase = 'closed';
function dismissLaunchSplash() {
  if (launchPhase !== 'shaders' || launchButton.disabled) return;
  launchButton.disabled = true;
  byId('prelaunch-status').textContent = 'Cancelling shader preparation…';
  send('cancel-launch-shaders');
}
launchButton.addEventListener('click', dismissLaunchSplash);
launchSplash.addEventListener('cancel', event => {
  event.preventDefault();
  dismissLaunchSplash();
});

function updateLaunchSplash({ phase, message, title, elapsed, runtimeLabel }) {
  if (phase === 'closed') {
    if (launchSplash.open) launchSplash.close();
    launchPhase = phase;
    return;
  }
  if (phase === 'waiting') {
    if (launchSplash.open) byId('prelaunch-metric').textContent = elapsed ? `${elapsed}s elapsed` : '';
    return;
  }
  launchPhase = phase;
  const building = phase === 'shaders';
  const running = phase === 'running';
  if (!running) {
    const gameTitle = title || 'Grand Theft Auto VI';
    byId('prelaunch-title').textContent = runtimeLabel ? `${runtimeLabel} - ${gameTitle}` : gameTitle;
    launchSplash.classList.toggle('generic-launch', Boolean(title));
  }
  launchSplash.dataset.phase = phase;
  byId('prelaunch-stage').textContent = running ? 'Running' : building ? 'Prewarming shaders' : 'Starting game';
  byId('prelaunch-status').textContent = message ?? '';
  byId('prelaunch-status').hidden = running && !message;
  byId('prelaunch-metric').textContent = '';
  launchProgress.removeAttribute('value');
  launchProgress.hidden = running;
  launchButton.textContent = 'Cancel launch';
  launchButton.hidden = !building;
  launchButton.disabled = false;
  if (!launchSplash.open) launchSplash.showModal();
}
let activityExpanded = false;
let returnTile = null;
let lastDefaultLayout;
let gtaFeed;
let gtaFeedPending = false;
let gtaFeedCheckedAt = 0;
let leonidaCandidate = null;
let dismissedLeonidaCandidate = null;
const gtaArtwork = { Header: 'gtavi-cover.jpg', Hero: 'gtavi-hero.jpg' };

function artworkFor(game) {
  return game?.leonida ? gtaArtwork : gameCatalog.get(game?.id);
}

function send(action, id, value) {
  if (action === 'play' && id) preview.holdForLaunch(id);
  if (host) host.postMessage({ action, id, value });
  else if (action !== 'ready') showToast('Open Nebula with run.bat to use Windows launcher actions.');
}

function showToast(message) {
  if (settings.isOpen) {
    settings.notice(message);
    return;
  }
  toastText.textContent = message;
  toast.hidden = false;
  for (const animation of toast.getAnimations()) {
    animation.currentTime = 0;
    animation.play();
  }
  clearTimeout(toastTimer);
  toastTimer = setTimeout(hideToast, Math.min(11000, Math.max(4200, message.length * 48)));
}

function setSearch(open) {
  search.hidden = !open;
  byId('search-toggle').setAttribute('aria-expanded', String(open));
  if (open) search.focus();
  else {
    search.value = '';
    renderRail();
    byId('search-toggle').focus();
  }
}

function getVisibleGames() {
  const term = search.value.trim().toLocaleLowerCase();
  return games.filter(game => (activeView !== 'recent' || game.recent) &&
    (game.Name.toLocaleLowerCase().includes(term) || displayName(game).toLocaleLowerCase().includes(term))).sort((a, b) => {
    if (activeView === 'recent') return Date.parse(b.recent) - Date.parse(a.recent);
    if (sort.value === 'name') return displayName(a).localeCompare(displayName(b));
    return Date.parse(b.added) - Date.parse(a.added);
  });
}

function displayName(game) {
  return game.DisplayName || gameCatalog.get(game.id)?.Title || game.Name;
}

function platformIcon(kind, label) {
  const icon = document.createElement('span');
  icon.className = 'platform-icon';
  icon.setAttribute('role', 'img');
  icon.setAttribute('aria-label', label);
  icon.title = label;
  if (kind === 'xbox' || kind === 'series-x') {
    const mark = document.createElement('img');
    mark.src = 'xbox-mark.svg';
    mark.alt = '';
    icon.append(mark);
    if (kind === 'series-x') {
      icon.classList.add('series-x-mark');
      const wordmark = document.createElement('span');
      wordmark.textContent = 'SERIES X';
      icon.append(wordmark);
    }
  } else {
    const ns = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(ns, 'svg');
    svg.setAttribute('viewBox', '0 0 24 24');
    svg.setAttribute('aria-hidden', 'true');
    const use = document.createElementNS(ns, 'use');
    use.setAttribute('href', `#icon-${kind}`);
    svg.append(use);
    icon.append(svg);
  }
  return icon;
}

function setFeature(game) {
  const selectionChanged = selectedId !== (game?.id ?? null);
  selectedId = game?.id ?? null;
  preview.select(selectedId);
  syncMedia(game);
  updateArtwork(byId('game-backdrop'), artworkFor(game), 'backdrop');
  for (const tile of rail.querySelectorAll('[data-game]')) {
    tile.setAttribute('aria-pressed', String(tile.dataset.game === selectedId));
  }
  byId('feature-platform').replaceChildren(...(game
    ? game.leonida ? [platformIcon('series-x', 'Xbox Series X'), platformIcon('windows', 'Windows host system')]
      : [game.xbox ? platformIcon('xbox', 'Xbox') : platformIcon('windows', 'Windows'),
         platformIcon('local', 'Local executable')]
    : [platformIcon('local', 'Local library')]));
  byId('feature-title').textContent = game ? displayName(game) : (activeView === 'recent' ? 'Recently played' : 'Game library');
  byId('feature-mark').hidden = !game || game.leonida;
  byId('feature-monogram').textContent = game?.Monogram || '◈';
  setIcon(byId('feature-icon'), byId('feature-monogram'), gameIcons.get(selectedId));
  byId('feature-description').hidden = Boolean(game && !game.xbox && !game.leonida);
  byId('feature-description').textContent = game
    ? (game.leonida
      ? game.executableLinked ? 'Experimental compatibility · game startup is not verified.'
        : leonidaCandidate ? 'GTA VI executable detected · ready to link.' : 'Choose the GTA VI package folder to link its executable.'
      : game.xbox ? 'Experimental Xbox compatibility.' : '')
    : search.value.trim() ? 'No games match your search.' : activeView === 'recent'
      ? 'Games launched from Nebula will appear here.'
      : 'Add a title to your library to get started.';
  primary.textContent = game?.leonida ? (game.executableLinked ? 'Launch' : 'Select executable')
    : game ? 'Launch' : 'Add Windows game';
  secondary.textContent = game ? 'Game settings' : 'Open diagnostics';
  secondary.hidden = !game && !diagnosticsAvailable;
  if (gtaOnlyEdition && game) {
    primary.textContent = !game.executableLinked ? 'Choose game folder' : !selectedVictoryInstalled ? 'Install a runtime' : 'Launch';
    secondary.textContent = 'Game setup';
  }
  showActivity(game);
  if (selectionChanged) {
    featureAnimation?.cancel();
    if (!reducedMotion.matches) {
      featureAnimation = byId('feature-title').animate(
        [
          { opacity: .35, clipPath: 'inset(0 0 24% 0)', transform: 'translateY(6px)' },
          { opacity: 1, clipPath: 'inset(0)', transform: 'translateY(0)' }
        ],
        { duration: 270, easing: 'cubic-bezier(.2,.75,.25,1)' });
    }
  }
}

function syncMedia(game) {
  mediaShelf.select(game?.leonida ? undefined : game?.id, game ? displayName(game) : '',
    gameCatalog.has(game?.id), gameCatalog.get(game?.id));
}

function requestCatalog(id) {
  if (!host || !id || games.find(game => game.id === id)?.leonida || gameCatalog.has(id) || pendingCatalog.has(id)) return;
  pendingCatalog.add(id);
  send('catalog', id);
}

const visibleTiles = new IntersectionObserver(entries => {
  for (const entry of entries) {
    if (!entry.isIntersecting) continue;
    requestCatalog(entry.target.dataset.game);
    visibleTiles.unobserve(entry.target);
  }
}, { root: rail, rootMargin: '45px' });

function updateArtwork(image, art, type) {
  const candidates = type === 'backdrop'
    ? [art?.Hero, art?.Header]
    : [art?.Header, art?.Cover, art?.Thumbnail];
  const urls = [...new Set(candidates.filter(Boolean))];
  const [url] = urls;
  if (!url) {
    image.hidden = true;
    image.removeAttribute('src');
    image.removeAttribute('data-art');
    return;
  }
  if (image.dataset.art === url) return;
  image.dataset.art = url;
  image.hidden = true;
  let index = 0;
  const fallback = () => {
    const next = urls[++index];
    if (next) image.src = next;
    else image.hidden = true;
  };
  image.onload = () => {
    if (type === 'backdrop' && (image.naturalWidth < 920 || image.naturalHeight < 360)) fallback();
    else image.hidden = false;
  };
  image.onerror = fallback;
  image.src = url;
}

function showActivity(game) {
  activityId = game?.id ?? null;
  byId('activity').hidden = !game;
  if (!game) {
    setActivityExpanded(false);
    return;
  }
  requestCatalog(game.id);
  byId('activity-game').textContent = displayName(game);
  const card = gameCatalog.get(game.id);
  if (card && host && !activityCache.has(game.id) && !pendingActivity.has(game.id)) {
    pendingActivity.add(game.id);
    send('activity', game.id);
  }
  const activity = activityCache.get(game.id);
  const empty = byId('activity-empty');
  byId('gtavi-media').hidden = !game.leonida;
  byId('gta-activity').hidden = !game.leonida;
  if (game.leonida && activityExpanded) requestGtaFeed();
  empty.hidden = game.leonida || Boolean(activity);
  empty.textContent = !gameCatalog.has(game.id) ? 'Matching this game to Steam…'
    : !card ? 'No matching Steam title was found.'
    : 'Loading reviews and updates…';
  byId('activity').classList.toggle('has-feed', Boolean(activity));
  byId('steam-rating').textContent = activity?.Rating ?? 'No review score';
  byId('steam-count').textContent = activity?.ReviewCount
    ? `${activity.ReviewCount.toLocaleString()} reviews` : '';
  byId('steam-review').disabled = !card;
  byId('steam-news').disabled = !card;
  if (!activity) return;
  renderFeed(byId('review-feed'), activity.Reviews, item => item.Recommended ? 'Recommended' : 'Not recommended', 'No review excerpts available.');
  renderFeed(byId('news-feed'), activity.News, item => item.Title, 'No recent Steam announcements available.');
}

const activityDate = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' });

function requestGtaFeed() {
  if (!host || gtaFeedPending || Date.now() - gtaFeedCheckedAt < 120_000) return;
  if (!games.some(game => game.id === activityId && game.leonida)) return;
  gtaFeedPending = true;
  if (!gtaFeed) {
    byId('gta-news-status').textContent = 'Checking Rockstar Games…';
    byId('gta-tips-status').textContent = 'Checking GamesRadar+…';
  }
  send('gtavi-activity', activityId);
}

function openGtaArticle(url) {
  if (activityId) send('gtavi-article', activityId, { url });
}

function renderGtaFeed() {
  const groups = [
    { key: 'news', entries: gtaFeed.News, live: gtaFeed.NewsLive, label: 'Read article' },
    { key: 'tips', entries: gtaFeed.Tips, live: gtaFeed.TipsLive, label: 'Read guide' }
  ];
  for (const { key, entries, live, label } of groups) {
    byId(`gta-${key}-status`).textContent = live ? '' : 'Showing saved picks while the source is unavailable.';
    const cards = entries.map(article => {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'gta-feed-card';
      const metadata = document.createElement('span');
      metadata.className = 'gta-feed-meta';
      metadata.textContent = label + ' ↗';
      if (article.Date) {
        const date = document.createElement('time');
        date.dateTime = article.Date;
        date.textContent = activityDate.format(new Date(`${article.Date}T12:00:00`));
        metadata.prepend(date);
      }
      const title = document.createElement('strong');
      title.textContent = article.Title;
      button.append(title);
      if (article.Summary) {
        const summary = document.createElement('span');
        summary.className = 'gta-feed-summary';
        summary.textContent = article.Summary;
        button.append(summary);
      }
      button.append(metadata);
      button.addEventListener('click', () => openGtaArticle(article.Url));
      return button;
    });
    byId(`gta-${key}-list`).replaceChildren(...cards);
  }
}

byId('gta-news-source').addEventListener('click', () => openGtaArticle('https://www.rockstargames.com/VI'));
byId('gta-tips-source').addEventListener('click', () => openGtaArticle('https://www.gamesradar.com/games/grand-theft-auto/gta-guides-walkthroughs/'));

function renderFeed(container, items, headlineFor, emptyMessage) {
  if (!items.length) {
    const message = document.createElement('p');
    message.className = 'feed-empty';
    message.textContent = emptyMessage;
    container.replaceChildren(message);
    return;
  }
  container.replaceChildren(...items.map(item => {
    const article = document.createElement('article');
    article.className = 'feed-entry';
    const top = document.createElement('div');
    top.className = 'feed-entry-heading';
    const heading = document.createElement('h4');
    heading.textContent = headlineFor(item);
    top.append(heading);
    if (item.Date > 0) {
      const date = new Date(item.Date * 1000);
      if (Number.isFinite(date.getTime())) {
        const time = document.createElement('time');
        time.dateTime = date.toISOString();
        time.textContent = activityDate.format(date);
        top.append(time);
      }
    }
    article.append(top);
    if (item.Excerpt) {
      const excerpt = document.createElement('p');
      excerpt.textContent = item.Excerpt;
      article.append(excerpt);
    }
    return article;
  }));
}

function setIcon(image, mark, source) {
  image.hidden = !source;
  mark.hidden = Boolean(source);
  if (source) {
    image.onerror = () => {
      image.hidden = true;
      mark.hidden = false;
    };
    if (image.getAttribute('src') !== source) image.src = source;
  } else {
    image.removeAttribute('src');
  }
}

function makeTile(title, symbol, kind, click) {
  const button = document.createElement('button');
  button.className = 'tile';
  button.dataset.kind = kind;
  button.title = title;
  const mark = document.createElement('span');
  mark.className = 'tile-symbol';
  mark.setAttribute('aria-hidden', 'true');
  mark.textContent = symbol;
  const label = document.createElement('span');
  label.className = 'tile-label';
  label.textContent = title;
  const icon = document.createElement('img');
  icon.className = 'tile-icon';
  icon.alt = '';
  icon.hidden = true;
  const artwork = document.createElement('img');
  artwork.className = 'tile-art';
  artwork.alt = '';
  artwork.hidden = true;
  button.append(artwork, mark, icon, label);
  button.addEventListener('click', click);
  return button;
}

function renderRail() {
  if (preview.tile && rail.contains(preview.tile)) preview.leave(preview.tile);
  visibleTiles.disconnect();
  const visible = getVisibleGames();
  const focusWasOnRail = rail.contains(document.activeElement);
  const focusedTile = document.activeElement?.dataset;
  const scrollPosition = rail.scrollLeft;
  const tiles = visible.map(game => {
    const tile = makeTile(displayName(game), game.Monogram, game.xbox || game.leonida ? 'xbox' : 'windows', () => setFeature(game));
    tile.dataset.game = game.id;
    tile.addEventListener('contextmenu', event => {
      event.preventDefault();
      setFeature(game);
      openGameSettings(game);
    });
    setIcon(tile.querySelector('.tile-icon'), tile.querySelector('.tile-symbol'), gameIcons.get(game.id));
    updateArtwork(tile.querySelector('.tile-art'), artworkFor(game), 'cover');
    const platform = document.createElement('span');
    platform.className = 'tile-platform';
    if (game.leonida) platform.append(platformIcon('series-x', 'Xbox Series X'), platformIcon('windows', 'Windows host system'));
    else platform.append(game.xbox ? platformIcon('xbox', 'Xbox') : platformIcon('windows', 'Windows'),
      platformIcon('local', 'Local executable'));
    tile.append(platform);
    tile.addEventListener('dblclick', () => send('play', game.id));
    tile.addEventListener('pointerenter', event => {
      preview.enter(tile, game.id, event.pointerType);
    });
    tile.addEventListener('pointerleave', () => {
      preview.leave(tile);
      showActivity(games.find(entry => entry.id === selectedId));
    });
    tile.addEventListener('focus', () => {
      if (tile.matches(':focus-visible')) setFeature(game);
    });
    return tile;
  });
  for (const item of actions) {
    if (item.id === 'diagnostics' && !diagnosticsAvailable) continue;
    const tile = makeTile(item.title, item.symbol, item.kind, () => send(item.id));
    tile.dataset.tool = item.id;
    if (item.id === 'add-xbox') {
      const xbox = tile.querySelector('.tile-icon');
      xbox.src = 'xbox-mark.svg';
      xbox.hidden = false;
    } else if (item.id === 'diagnostics') {
      tile.querySelector('.tile-symbol').replaceChildren(platformIcon('diagnostics', 'Runtime diagnostics'));
    }
    const detail = document.createElement('span');
    detail.className = 'tile-platform';
    detail.textContent = item.detail;
    tile.append(detail);
    tiles.push(tile);
  }
  rail.replaceChildren(...tiles);
  for (const tile of rail.querySelectorAll('[data-game]')) visibleTiles.observe(tile);
  rail.scrollLeft = scrollPosition;
  if (focusWasOnRail) {
    const restored = tiles.find(tile => focusedTile?.game
      ? tile.dataset.game === focusedTile.game : tile.dataset.tool === focusedTile?.tool);
    (restored ?? tiles[0])?.focus({ preventScroll: true });
  }
  byId('rail-title').textContent = activeView === 'recent' ? 'Recently played' : 'Your library';
  byId('game-count').textContent = String(visible.length);
  sort.hidden = activeView === 'recent';
  setFeature(visible.find(game => game.id === selectedId) ?? visible[0]);
}

function setView(name) {
  activeView = name;
  for (const view of views) {
    if (view.dataset.view === name) view.setAttribute('aria-current', 'page');
    else view.removeAttribute('aria-current');
  }
}

for (const button of views) {
  button.addEventListener('click', () => {
    settings.close();
    setView(button.dataset.view);
    renderRail();
  });
  button.addEventListener('keydown', event => {
    if (event.key === 'ArrowDown') {
      if (settings.isOpen) return;
      const tile = [...rail.querySelectorAll('button')].find(item => item.dataset.game === selectedId)
        ?? rail.querySelector('button');
      if (tile) {
        event.preventDefault();
        focusTile(tile);
      }
    } else if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
      const step = event.key === 'ArrowLeft' ? -1 : 1;
      const next = views[(views.indexOf(button) + step + views.length) % views.length];
      event.preventDefault();
      next.focus();
    }
  });
}

for (const button of document.querySelectorAll('[data-action]')) {
  button.addEventListener('click', () => send(button.dataset.action));
}
for (const button of document.querySelectorAll('[data-discovery]')) {
  button.addEventListener('click', () => { if (activityId) send(button.dataset.discovery, activityId); });
}

byId('chrome').addEventListener('pointerdown', event => {
  if (event.button === 0 && !event.target.closest('button, input, select')) send('drag');
});
byId('search-toggle').addEventListener('click', () => {
  settings.close();
  setSearch(search.hidden);
});
search.addEventListener('input', renderRail);
sort.addEventListener('change', renderRail);
function setLayout(grid) {
  rail.classList.toggle('grid-view', grid);
  byId('grid-toggle').setAttribute('aria-pressed', String(grid));
  byId('grid-toggle').title = grid ? 'Switch to carousel view' : 'Switch to grid view';
}
byId('grid-toggle').addEventListener('click', () => setLayout(!rail.classList.contains('grid-view')));
primary.addEventListener('click', () => {
  const game = games.find(entry => entry.id === selectedId);
  if (gtaOnlyEdition) {
    if (!game?.executableLinked) send('browse-leonida');
    else if (!selectedVictoryInstalled) openGameSettings(game);
    else send('play', game.id);
    return;
  }
  if (selectedId) send('play', selectedId);
  else send('add-windows');
});
secondary.addEventListener('click', () => {
  const game = games.find(entry => entry.id === selectedId);
  if (!game) { send('diagnostics'); return; }
  openGameSettings(game);
});
function openGameSettings(game) {
  settings.open({ id: game.id, title: displayName(game), xbox: game.xbox, leonida: game.leonida,
    icon: gameIcons.get(game.id), initials: game.Monogram });
}
function confirmRemovalOf(game) {
  pendingRemoval = game.id;
  byId('remove-description').textContent = game.title;
  dialog.showModal();
  byId('cancel-remove').focus();
}
byId('cancel-remove').addEventListener('click', () => dialog.close());
byId('confirm-remove').addEventListener('click', () => {
  send('remove', pendingRemoval);
  dialog.close();
});
dialog.addEventListener('close', () => { pendingRemoval = null; secondary.focus(); });

function focusTile(tile) {
  if (!tile) return;
  tile.focus({ preventScroll: true });
  const viewport = rail.getBoundingClientRect();
  const bounds = tile.getBoundingClientRect();
  const left = bounds.left < viewport.left + 8 ? bounds.left - viewport.left - 8
    : Math.max(0, bounds.right - viewport.right + 8);
  const top = bounds.top < viewport.top + 8 ? bounds.top - viewport.top - 8
    : Math.max(0, bounds.bottom - viewport.bottom + 8);
  if (left || top) rail.scrollBy({ left, top, behavior: reducedMotion.matches ? 'instant' : 'smooth' });
}

function setActivityExpanded(expanded, moveFocus = false) {
  if (expanded && byId('activity').hidden) return false;
  if (expanded === activityExpanded) return false;
  activityExpanded = expanded;
  document.body.classList.toggle('activity-expanded', expanded);
  activityToggle.setAttribute('aria-expanded', String(expanded));
  activityContent.inert = !expanded;
  if (expanded) requestGtaFeed();
  mediaShelf.setExpanded(expanded);
  byId('activity-key').textContent = expanded ? '↑' : '↓';
  byId('activity-key-label').textContent = expanded ? 'Library' : 'Activity';
  if (expanded) {
    const tile = document.activeElement.closest?.('.tile');
    returnTile = tile ? { game: tile.dataset.game, tool: tile.dataset.tool } : null;
    if (moveFocus) activityToggle.focus({ preventScroll: true });
  } else if (moveFocus) {
    const tiles = [...rail.querySelectorAll('button')];
    const saved = tiles.find(tile => returnTile?.game
      ? tile.dataset.game === returnTile.game : returnTile?.tool && tile.dataset.tool === returnTile.tool);
    focusTile(saved ?? tiles.find(tile => tile.dataset.game === selectedId) ?? tiles[0]);
    if (window.scrollY) window.scrollTo({ top: 0, behavior: reducedMotion.matches ? 'instant' : 'smooth' });
  }
  return true;
}

activityToggle.addEventListener('click', () => setActivityExpanded(!activityExpanded));

function moveFocus(direction) {
  const items = [...rail.querySelectorAll('button')];
  const index = items.indexOf(document.activeElement);
  const next = items[index < 0 ? 0 : Math.max(0, Math.min(items.length - 1, index + direction))];
  focusTile(next);
  if (next?.dataset.game) setFeature(games.find(game => game.id === next.dataset.game));
}

function moveGridRow(direction) {
  if (!rail.classList.contains('grid-view')) return false;
  const focused = document.activeElement;
  if (!rail.contains(focused)) return false;
  const items = [...rail.querySelectorAll('button')];
  const next = items.filter(tile => Math.sign(tile.offsetTop - focused.offsetTop) === direction)
    .sort((a, b) => Math.abs(a.offsetTop - focused.offsetTop) - Math.abs(b.offsetTop - focused.offsetTop) ||
      Math.abs(a.offsetLeft - focused.offsetLeft) - Math.abs(b.offsetLeft - focused.offsetLeft))[0];
  if (!next) return false;
  focusTile(next);
  if (next.dataset.game) setFeature(games.find(game => game.id === next.dataset.game));
  return true;
}

document.addEventListener('keydown', event => {
  if (dialog.open || launchSplash.open) return;
  if (settings.isOpen) {
    if (event.key === 'Escape' && !event.defaultPrevented) {
      event.preventDefault();
      settings.close();
    }
    return;
  }
  if (event.defaultPrevented || event.altKey || event.metaKey || (event.ctrlKey && event.key.toLowerCase() !== 'f')) return;
  if ((event.ctrlKey && event.key.toLowerCase() === 'f') || (event.key === '/' && event.target === document.body)) {
    event.preventDefault();
    setSearch(true);
  } else if (event.key === 'Escape' && !search.hidden) {
    setSearch(false);
  } else if (event.key === 'Escape' && activityExpanded) {
    event.preventDefault();
    setActivityExpanded(false, true);
  } else if (event.target.closest('input, textarea, select, video, [contenteditable="true"]')) {
    return;
  } else if (['Home', 'End'].includes(event.key) && rail.contains(document.activeElement)) {
    const items = [...rail.querySelectorAll('button')];
    const tile = event.key === 'Home' ? items[0] : items.at(-1);
    if (tile) {
      event.preventDefault();
      focusTile(tile);
      if (tile.dataset.game) setFeature(games.find(game => game.id === tile.dataset.game));
    }
  } else if (event.key === 'ArrowDown' && !activityExpanded && moveGridRow(1)) {
    event.preventDefault();
  } else if (event.key === 'ArrowDown' && rail.contains(document.activeElement) && !activityExpanded && !event.repeat && setActivityExpanded(true, true)) {
    event.preventDefault();
  } else if (event.key === 'ArrowUp' && activityExpanded && !event.repeat && setActivityExpanded(false, true)) {
    event.preventDefault();
  } else if (['ArrowLeft', 'ArrowRight'].includes(event.key) && !activityExpanded && rail.contains(document.activeElement)) {
    event.preventDefault();
    moveFocus(event.key === 'ArrowLeft' ? -1 : 1);
  } else if (event.key === 'ArrowUp' && rail.contains(document.activeElement) && !activityExpanded) {
    event.preventDefault();
    if (!moveGridRow(-1)) views.find(view => view.dataset.view === activeView)?.focus();
  } else if (event.key === 'ArrowUp' && [primary, secondary].includes(document.activeElement)) {
    event.preventDefault();
    focusTile(rail.querySelector('[aria-pressed="true"]') ?? rail.querySelector('button'));
  } else if (['ArrowLeft', 'ArrowRight'].includes(event.key) && [primary, secondary].includes(document.activeElement)) {
    event.preventDefault();
    (event.key === 'ArrowLeft' ? primary : secondary).focus();
  }
});

function updateClock() {
  const now = new Date();
  const clock = byId('clock');
  clock.dateTime = now.toISOString();
  clock.textContent = now.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
}

function updateLeonidaPrompt() {
  const game = games.find(entry => entry.leonida && !entry.executableLinked);
  byId('leonida-prompt').hidden = gtaOnlyEdition || !game || !leonidaCandidate || dismissedLeonidaCandidate === leonidaCandidate;
}

byId('leonida-prompt-later').addEventListener('click', () => {
  dismissedLeonidaCandidate = leonidaCandidate;
  updateLeonidaPrompt();
});
byId('leonida-prompt-select').addEventListener('click', () => {
  const game = games.find(entry => entry.leonida);
  if (game) send('play', game.id);
});

host?.addEventListener('message', ({ data }) => {
  if (data.type === 'initial-library-ready') {
    const artwork = [...rail.querySelectorAll('img:not([hidden])'), byId('game-backdrop')]
      .filter(image => image.hasAttribute('src'));
    Promise.race([
      Promise.allSettled(artwork.map(image => image.decode())),
      new Promise(resolve => setTimeout(resolve, 500))
    ]).then(() => {
      // Resolve layout before requesting the native splash-to-library handoff.
      rail.getBoundingClientRect();
      send('library-rendered');
    });
    return;
  }
  if (data.type === 'gtavi-activity') {
    gtaFeedPending = false;
    gtaFeedCheckedAt = Date.now();
    gtaFeed = data.feed;
    renderGtaFeed();
    return;
  }
  if (data.type === 'gta-launch-splash') {
    updateLaunchSplash(data);
    return;
  }
  if (data.type === 'victory-prewarm-progress') {
    if (launchSplash.open && launchPhase === 'shaders' && !launchButton.disabled) {
      const { done, total } = data;
      launchProgress.value = (done / total) * 100;
      byId('prelaunch-metric').textContent = `${done.toLocaleString()} / ${total.toLocaleString()} shader records`;
      byId('prelaunch-status').textContent = done === total
        ? 'Finishing the shader cache…'
        : 'Preparing the shader cache. The game opens when this finishes.';
    }
    return;
  }
  if (data.type === 'xcl-shader-build') {
    settings.receiveXclShaderBuild(data.status);
    if (launchSplash.open && launchPhase === 'shaders' && !launchButton.disabled) {
      const { Total, Progress } = data.status;
      if (Total > 0) {
        launchProgress.value = Math.min(100, Progress * 100 / Total);
        byId('prelaunch-metric').textContent = `${Progress.toLocaleString()} / ${Total.toLocaleString()} records`;
      }
      const detail = Total > 0 ? 'Building the shader cache for this launch.' : 'Scanning the game’s shader archive…';
      if (byId('prelaunch-status').textContent !== detail) byId('prelaunch-status').textContent = detail;
    }
    return;
  }
  if (data.type === 'toast') { showToast(data.message); return; }
  if (data.type === 'game-properties') { settings.showGameProperties(data); return; }
  if (data.type === 'game-properties-saved') { settings.notice('Game settings saved.'); return; }
  if (data.type === 'preferences-saved') { settings.notice('Launcher preferences saved.'); return; }
  if (data.type === 'game-added') {
    if (!games.some(game => game.id === data.id)) return;
    setView('games');
    search.value = '';
    search.hidden = true;
    byId('search-toggle').setAttribute('aria-expanded', 'false');
    selectedId = data.id;
    setActivityExpanded(false);
    renderRail();
    window.scrollTo({ top: 0, behavior: 'instant' });
    const tile = [...rail.querySelectorAll('[data-game]')].find(item => item.dataset.game === data.id);
    focusTile(tile);
    return;
  }
  if (data.type === 'media') {
    mediaShelf.accept(data.id, data.media);
    preview.refresh(data.id);
    return;
  }
  if (data.type === 'catalog') {
    pendingCatalog.delete(data.id);
    if (!games.some(game => game.id === data.id)) return;
    gameCatalog.set(data.id, data.card);
    const tile = [...rail.querySelectorAll('[data-game]')].find(item => item.dataset.game === data.id);
    const game = games.find(entry => entry.id === data.id);
    if (tile) {
      updateArtwork(tile.querySelector('.tile-art'), data.card, 'cover');
      tile.title = displayName(game);
      tile.querySelector('.tile-label').textContent = displayName(game);
    }
    if (selectedId === data.id) {
      updateArtwork(byId('game-backdrop'), data.card, 'backdrop');
      byId('feature-title').textContent = displayName(game);
      syncMedia(game);
    }
    if (activityId === data.id) showActivity(game);
    preview.refresh(data.id);
    return;
  }
  if (data.type === 'activity') {
    pendingActivity.delete(data.id);
    const game = games.find(entry => entry.id === data.id);
    if (!game) return;
    activityCache.set(data.id, data.activity);
    if (activityId === data.id) showActivity(game);
    return;
  }
  if (data.type === 'icons') {
    for (const { id, icon } of data.results) {
      if (games.some(game => game.id === id)) {
        if (icon) gameIcons.set(id, icon);
        else gameIcons.delete(id);
      }
    }
    for (const tile of rail.querySelectorAll('[data-game]')) {
      setIcon(tile.querySelector('.tile-icon'), tile.querySelector('.tile-symbol'), gameIcons.get(tile.dataset.game));
    }
    setIcon(byId('feature-icon'), byId('feature-monogram'), gameIcons.get(selectedId));
    return;
  }
  if (data.type !== 'state') return;
  gtaOnlyEdition = Boolean(data.gtaOnlyEdition);
  document.body.classList.toggle('gta-edition', gtaOnlyEdition);
  document.querySelector('.gta-edition-label').hidden = !gtaOnlyEdition;
  if (gtaOnlyEdition) document.title = 'Nebula GTA VI Edition';
  selectedVictoryInstalled = Boolean(data.victoryBuilds?.some(build => build.installed && build.id === data.gtaRuntime));
  diagnosticsAvailable = Boolean(data.diagnosticsAvailable);
  settings.updateState(data);
  document.body.classList.toggle('static-background', data.backgroundMotion === false);
  preview.setEnabled(data.backgroundMotion !== false && data.videoPreview !== false);
  const defaultLayout = data.defaultLayout === 'grid' ? 'grid' : 'carousel';
  if (lastDefaultLayout !== defaultLayout) {
    setLayout(defaultLayout === 'grid');
    lastDefaultLayout = defaultLayout;
  }
  games = data.games;
  leonidaCandidate = data.leonida?.candidate ?? null;
  updateLeonidaPrompt();
  const currentIds = new Set(games.map(game => game.id));
  for (const id of gameIcons.keys()) if (!currentIds.has(id)) gameIcons.delete(id);
  for (const id of gameCatalog.keys()) if (!currentIds.has(id)) gameCatalog.delete(id);
  for (const id of pendingCatalog) if (!currentIds.has(id)) pendingCatalog.delete(id);
  for (const id of activityCache.keys()) if (!currentIds.has(id)) activityCache.delete(id);
  for (const id of pendingActivity) if (!currentIds.has(id)) pendingActivity.delete(id);
  byId('maximize').setAttribute('aria-label', data.maximized ? 'Restore' : 'Maximize');
  byId('sound-toggle').setAttribute('aria-pressed', String(data.startupSound));
  soundToggle.title = data.startupSound ? 'Mute launcher sounds' : 'Enable launcher sounds';
  renderRail();
});

updateClock();
setInterval(updateClock, 15000);
if (!host) renderRail();
send('ready');
