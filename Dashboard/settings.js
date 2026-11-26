'use strict';

class NebulaSettings {
  constructor(send, removeGame, onOpen) {
    this.send = send;
    this.removeGame = removeGame;
    this.get = id => document.getElementById(id);
    this.screen = this.get('settings-screen');
    this.get('open-settings').setAttribute('aria-controls', this.screen.id);
    this.get('open-settings').setAttribute('aria-expanded', 'false');
    this.sections = [...this.screen.querySelectorAll('[data-settings-panel]')];
    this.tabs = [...this.screen.querySelectorAll('[data-settings-page]')];
    this.state = {};
    for (const tab of this.tabs) {
      tab.addEventListener('click', () => this.showPage(tab.dataset.settingsPage));
      tab.addEventListener('keydown', event => {
        const visible = this.tabs.filter(item => !item.hidden);
        const index = visible.indexOf(tab);
        if (['ArrowUp', 'ArrowDown'].includes(event.key)) {
          event.preventDefault();
          const step = event.key === 'ArrowUp' ? -1 : 1;
          const next = visible[(index + step + visible.length) % visible.length];
          this.showPage(next.dataset.settingsPage);
          next.focus();
        } else if (event.key === 'ArrowRight') {
          event.preventDefault();
          const controls = this.sections.find(panel => !panel.hidden)?.querySelectorAll(
            'button:not(:disabled):not([hidden]), input:not(:disabled), select:not(:disabled), summary'
          ) ?? [];
          [...controls].find(control => control.getClientRects().length > 0)?.focus();
        }
      });
    }
    this.screen.addEventListener('settings-opened', onOpen);
    this.get('open-settings').addEventListener('click', () => this.isOpen ? this.close() : this.open(null));
    this.get('settings-close').addEventListener('click', () => this.close());
    this.get('browse-runtime').addEventListener('click', () => send('browse-runtime'));
    this.get('victory-project').addEventListener('click', () => send('victory-project'));
    this.get('victory-nvidia-project').addEventListener('click', () => send('victory-nvidia-project'));
    this.get('xcl-project').addEventListener('click', () => send('xcl-project'));
    this.get('open-local-runtime-folder').addEventListener('click', () => send('open-local-runtime-folder'));
    this.get('reset-runtime').addEventListener('click', () => send('reset-runtime'));
    this.get('browse-leonida').addEventListener('click', () => send('browse-leonida'));
    this.get('clear-leonida').addEventListener('click', () => send('clear-leonida'));
    this.get('victory-console-mode').addEventListener('change', () => this.saveVictoryGraphics());
    this.get('gta-runtime').addEventListener('change', () => this.saveGtaRuntime());
    this.get('scarlett-resolution').addEventListener('change', () => this.send('save-gta-resolution', null, { resolution: this.get('scarlett-resolution').value }));
    this.get('scarlett-ray-tracing').addEventListener('change', () => this.send('save-scarlett-ray-tracing', null, { enabled: this.get('scarlett-ray-tracing').checked }));
    this.get('open-selected-runtime-folder').addEventListener('click', () => send('open-selected-runtime-folder'));
    this.get('victory-world-install').addEventListener('submit', event => {
      event.preventDefault();
      send('install-victory-world', null, { repository: this.get('victory-world-repository').value.trim() });
    });
    this.get('custom-runtime-form').addEventListener('submit', event => {
      event.preventDefault();
      this.get('custom-runtime-install').disabled = true;
      send('install-custom-victory', null, {
        repository: this.get('custom-runtime-repository').value.trim(),
        branch: this.get('custom-runtime-branch').value.trim()
      });
    });
    this.get('install-runtime-git').addEventListener('click', () => send('install-runtime-prerequisite', null, { tool: 'git' }));
    this.get('install-runtime-rust').addEventListener('click', () => send('install-runtime-prerequisite', null, { tool: 'rust' }));
    this.get('xcl-frame-diagnostics').addEventListener('change', () => this.send('save-xcl-diagnostics', null, { frameDiagnostics: this.get('xcl-frame-diagnostics').checked }));
    this.get('xcl-postrun-precompile').addEventListener('change', () => this.send('save-xcl-postrun-precompile', null, { enabled: this.get('xcl-postrun-precompile').checked }));
    this.get('build-xcl-shaders').addEventListener('click', () => {
      this.receiveXclShaderBuild({ status: 'running', message: 'Starting XCL shader cache prebuild…' });
      send('build-xcl-shaders');
    });
    this.get('cancel-xcl-shaders').addEventListener('click', () => send('cancel-xcl-shaders'));
    this.get('open-diagnostics-settings').addEventListener('click', () => {
      this.close();
      send('diagnostics');
    });
    this.get('open-game-folder').addEventListener('click', () => { if (this.game) send('open-game-folder', this.game.id); });
    this.get('remove-game-settings').addEventListener('click', () => {
      if (!this.game) return;
      const game = this.game;
      this.close();
      this.removeGame(game);
    });
    this.get('preferences-form').addEventListener('submit', event => {
      event.preventDefault();
      this.notice('');
      send('save-preferences', null, {
        showIntro: this.get('pref-intro').checked,
        recordingDelay: this.get('pref-recording-delay').checked,
        startupSound: this.get('pref-sound').checked,
        backgroundMotion: this.get('pref-motion').checked,
        videoPreview: this.get('pref-video-preview').checked,
        prewarmShaders: this.get('pref-prewarm-shaders').checked,
        developerMode: this.get('pref-developer-mode').checked,
        layout: this.get('pref-layout').value
      });
    });
    this.get('game-settings-form').addEventListener('submit', event => {
      event.preventDefault();
      if (!this.game || !this.gameReady) return;
      this.notice('');
      send('save-game-properties', this.game.id, {
        displayName: this.get('game-display-name').value,
        arguments: this.get('game-launch-arguments').value
      });
    });
  }

  get isOpen() { return !this.screen.hidden; }

  close() {
    if (!this.isOpen) return;
    this.screen.hidden = true;
    this.get('library-screen').hidden = false;
    this.get('library-hints').hidden = false;
    this.get('settings-hints').hidden = true;
    document.body.classList.remove('settings-active');
    this.get('open-settings').setAttribute('aria-expanded', 'false');
    window.scrollTo({ top: this.returnScroll, behavior: 'instant' });
    const focus = this.returnFocus?.isConnected && this.returnFocus !== document.body
      ? this.returnFocus : this.get('open-settings');
    focus.focus({ preventScroll: true });
    this.screen.dispatchEvent(new Event('settings-closed'));
  }

  open(game) {
    if (!this.isOpen) {
      this.returnFocus = document.activeElement;
      this.returnScroll = window.scrollY;
      this.get('library-screen').hidden = true;
      this.get('library-hints').hidden = true;
      this.get('settings-hints').hidden = false;
      this.screen.hidden = false;
      document.body.classList.add('settings-active');
      this.get('open-settings').setAttribute('aria-expanded', 'true');
      window.scrollTo({ top: 0, behavior: 'instant' });
      this.screen.dispatchEvent(new Event('settings-opened'));
    }
    this.game = game;
    this.gameReady = false;
    this.get('game-settings-nav').hidden = !game;
    this.tabs.find(tab => tab.dataset.settingsPage === 'leonida').hidden = !this.state.gtaOnlyEdition && !game?.leonida;
    this.notice('');
    this.loadPreferences();
    this.showPage(this.state.gtaOnlyEdition ? 'leonida' : game ? 'game' : 'preferences');
    this.tabs.find(tab => tab.getAttribute('aria-current') === 'page')?.focus({ preventScroll: true });
    if (game && !this.state.gtaOnlyEdition) {
      const icon = this.get('settings-game-icon');
      icon.hidden = !game.icon;
      if (game.icon) icon.src = game.icon;
      else icon.removeAttribute('src');
      this.get('settings-game-initials').hidden = Boolean(game.icon);
      this.get('settings-game-initials').textContent = game.initials || '◇';
      this.get('settings-game-title').textContent = game.title;
      this.get('settings-game-platform').textContent = game.leonida ? 'Xbox Series X · Windows host' : game.xbox ? 'Xbox' : 'Windows · Local installation';
      this.get('game-display-name').value = '';
      this.get('game-launch-arguments').value = '';
      this.get('game-arguments-help').textContent = 'Launch options are loading.';
      this.get('game-path').textContent = 'Checking executable selection…';
      this.get('game-settings-form').querySelector('button[type="submit"]').disabled = true;
      this.send('game-properties', game.id);
    }
  }

  showPage(page) {
    if (page === 'leonida' && !this.state.gtaOnlyEdition && !this.game?.leonida) return;
    this.notice('');
    const descriptions = { preferences: 'Launcher preferences', runtime: 'Compatibility settings', leonida: 'VI Setup', game: this.game?.title ?? 'Game settings' };
    this.get('settings-subtitle').textContent = descriptions[page];
    for (const panel of this.sections) panel.hidden = panel.dataset.settingsPanel !== page;
    for (const tab of this.tabs) {
      if (tab.dataset.settingsPage === page) tab.setAttribute('aria-current', 'page');
      else tab.removeAttribute('aria-current');
    }
  }

  updateState(state) {
    this.state = state;
    this.get('open-diagnostics-settings').hidden = !state.diagnosticsAvailable;
    this.get('victory-console-mode').value = state.victoryConsoleMode === 'series-s' ? 'series-s' : 'series-x';
    this.get('gta-runtime-world').disabled = !state.victoryWorldAvailable;
    this.get('gta-runtime-nvidia').disabled = !state.nvidiaVictoryAvailable;
    this.get('open-selected-runtime-folder').disabled = !state.gtaRuntimeFolderAvailable;
    this.get('scarlett-resolution-row').hidden = !state.scarlettFoxRuntime;
    this.get('scarlett-resolution').value = state.gtaResolution || '1920x1080';
    this.get('scarlett-ray-tracing-row').hidden = !state.scarlettFoxRuntime;
    this.get('scarlett-ray-tracing').checked = Boolean(state.scarlettRayTracing);
    const selected = state.gtaRuntime || 'victory';
    this.get('gta-runtime').value = selected === 'victory' && !state.victoryWorldAvailable ? '' : selected;
    this.get('victory-world-install').hidden = state.gtaOnlyEdition || state.victoryWorldAvailable;
    this.get('victory-world-repository').disabled = Boolean(state.victoryWorldInstalling || state.victoryWorldAvailable);
    this.get('victory-world-install-button').disabled = Boolean(state.victoryWorldInstalling || state.victoryWorldAvailable);
    const installStatus = this.get('victory-world-install-status');
    installStatus.textContent = state.victoryWorldInstallStatus || '';
    installStatus.hidden = !state.victoryWorldInstallStatus;
    this.get('xcl-frame-diagnostics').checked = Boolean(state.xclFrameDiagnostics);
    this.get('xcl-postrun-precompile').checked = state.xclPostRunPrecompile !== false;
    this.get('gta-runtime-xcl').disabled = !state.xclAvailable;
    if (state.gtaOnlyEdition) this.showEditionBuilds(state);
    this.get('victory-profile-row').hidden = state.gtaRuntime === 'xcl';
    this.renderXclStatus(state.xclStatus);
    this.receiveXclShaderBuild(state.xclShaderBuild || {});
    this.get('runtime-setting-status').textContent = state.runtime ? 'Xbox compatibility files detected' : 'No optional Xbox compatibility files detected';
    this.get('runtime-setting-path').textContent = state.runtimePath || 'Optional compatibility files are not configured.';
    this.get('reset-runtime').disabled = !state.customRuntime;
    this.updateLeonida();
  }

  showEditionBuilds(state) {
    const select = this.get('gta-runtime');
    const staticBuilds = state.victoryBuilds || [];
    const customBuilds = state.customVictoryBuilds || [];
    const installed = [...staticBuilds, ...customBuilds].filter(build => build.installed);
    for (const build of staticBuilds) {
      if (![...select.options].some(option => option.value === build.id))
        select.add(new Option(build.name, build.id));
    }
    const customIds = new Set(customBuilds.map(build => build.id));
    for (const option of [...select.options]) {
      if (option.dataset.customRuntime && !customIds.has(option.value)) option.remove();
    }
    for (const build of customBuilds) {
      let option = [...select.options].find(item => item.value === build.id);
      if (!option) {
        option = new Option(`${build.name} · Custom`, build.id);
        option.dataset.customRuntime = 'true';
        select.add(option);
      } else option.textContent = `${build.name} · Custom`;
    }
    for (const option of select.options) {
      if (!option.value) continue;
      const available = installed.some(build => build.id === option.value) || !state.gtaOnlyEdition && option.value === 'xcl' && state.xclAvailable;
      option.hidden = option.disabled = !available;
    }
    const selection = installed.find(build => build.id === state.gtaRuntime) || installed[0];
    select.value = selection?.id || '';
    select.disabled = !selection;

    this.get('victory-builds').hidden = false;
    const git = this.get('install-runtime-git');
    git.disabled = Boolean(state.victoryWorldInstalling || state.gitAvailable);
    git.textContent = state.gitAvailable ? 'Git installed' : 'Install Git';
    const rust = this.get('install-runtime-rust');
    rust.disabled = Boolean(state.victoryWorldInstalling || state.rustAvailable);
    rust.textContent = state.rustAvailable ? 'Rust installed' : 'Install Rust';
    const customForm = this.get('custom-runtime-form');
    customForm.querySelectorAll('input').forEach(input => input.disabled = Boolean(state.victoryWorldInstalling));
    const customInstall = this.get('custom-runtime-install');
    customInstall.disabled = Boolean(state.victoryWorldInstalling);
    customInstall.textContent = state.victoryWorldInstalling && state.victoryInstallBuildId?.startsWith('custom-')
      ? 'Building custom runtime…' : 'Build and install';
    const actions = this.get('victory-build-actions');
    const buildSet = [...staticBuilds, ...customBuilds].map(build => `${build.id}:${build.name}:${build.repository}`).join('|');
    if (actions.dataset.buildSet !== buildSet) {
      actions.dataset.buildSet = buildSet;
      actions.replaceChildren();
      for (const build of [...staticBuilds, ...customBuilds]) {
        const isCustom = customBuilds.some(item => item.id === build.id);
        const row = document.createElement('div');
        row.className = 'runtime-install-row';
        row.dataset.runtime = build.id;
        const title = document.createElement('div');
        title.className = 'runtime-install-title';
        title.innerHTML = `<strong></strong><small></small>`;
        title.querySelector('strong').textContent = build.name;
        title.querySelector('small').textContent = `${build.repository || 'Community runtime'}${isCustom ? ' · Custom' : ''}`;
        const buttons = document.createElement('div');
        buttons.className = 'runtime-install-buttons';
        if (!isCustom) {
          const install = document.createElement('button');
          install.type = 'button';
          install.className = 'secondary';
          install.dataset.action = 'install';
          install.textContent = 'Install';
          install.addEventListener('click', () => this.send('install-victory-build', null, { runtime: build.id }));
          const update = document.createElement('button');
          update.type = 'button';
          update.className = 'secondary';
          update.dataset.action = 'update';
          update.textContent = 'Install Latest';
          update.addEventListener('click', () => this.send('install-latest-victory-build', null, { runtime: build.id }));
          buttons.append(install, update);
        }
        const open = document.createElement('button');
        open.type = 'button';
        open.className = 'secondary';
        open.dataset.action = 'open';
        open.textContent = 'Open';
        open.disabled = !build.installed;
        open.title = build.installed ? 'Open this runtime’s installed folder' : 'Install this runtime to open its folder';
        open.addEventListener('click', () => this.send('open-victory-runtime-folder', null, { runtime: build.id }));
        buttons.append(open);
        const uninstall = document.createElement('button');
        uninstall.type = 'button';
        uninstall.className = 'secondary danger';
        uninstall.dataset.action = 'uninstall';
        uninstall.textContent = 'Uninstall';
        uninstall.disabled = !build.installed;
        uninstall.title = build.installed ? 'Remove this runtime from Nebula' : 'This runtime is not installed';
        uninstall.addEventListener('click', () => {
          if (!build.installed) return;
          if (window.confirm(`Uninstall ${build.name}? This removes its Nebula runtime files. Your game files are not affected.`))
            this.send('uninstall-victory-runtime', null, { runtime: build.id });
        });
        buttons.append(uninstall);
        row.append(title, buttons);
        actions.append(row);
      }
    }
    for (const build of [...staticBuilds, ...customBuilds]) {
      const row = [...actions.children].find(item => item.dataset.runtime === build.id);
      if (!row) continue;
      const install = row.querySelector('[data-action="install"]');
      const update = row.querySelector('[data-action="update"]');
      const open = row.querySelector('[data-action="open"]');
      const uninstall = row.querySelector('[data-action="uninstall"]');
      const building = state.victoryWorldInstalling && state.victoryInstallBuildId === build.id;
      if (install) {
        install.disabled = Boolean(state.victoryWorldInstalling || build.installed);
        install.textContent = build.installed ? 'Installed' : 'Install';
      }
      if (update) {
        update.disabled = Boolean(state.victoryWorldInstalling);
        update.textContent = building && state.victoryInstallLatest ? 'Building latest…' : 'Install Latest';
      }
      if (open) {
        open.disabled = !build.installed || state.victoryWorldInstalling;
        open.title = build.installed ? 'Open this runtime’s installed folder' : 'Install this runtime to open its folder';
      }
      if (uninstall) {
        uninstall.disabled = !build.installed || Boolean(state.victoryWorldInstalling);
        uninstall.title = build.installed ? 'Remove this runtime from Nebula' : 'This runtime is not installed';
      }
    }
  }

  saveVictoryGraphics() {
    this.send('save-victory-graphics', null, {
      consoleMode: this.get('victory-console-mode').value
    });
  }

  saveGtaRuntime() {
    this.send('save-gta-runtime', null, { runtime: this.get('gta-runtime').value });
  }

  receiveXclShaderBuild(status) {
    status = {
      status: status.Status ?? status.status ?? 'idle',
      progress: status.Progress ?? status.progress ?? 0,
      total: status.Total ?? status.total ?? 0,
      message: status.Message ?? status.message ?? ''
    };
    const running = status.status === 'running';
    this.get('xcl-tools').hidden = this.state.gtaRuntime !== 'xcl' && !running;
    this.get('build-xcl-shaders').disabled = running || !this.state.xclAvailable || !this.state.leonida?.exists;
    this.get('cancel-xcl-shaders').hidden = !running;
    this.get('build-xcl-shaders').textContent = running ? 'Prebuilding shader cache…' : 'Prebuild shader cache';
    const progress = this.get('xcl-shader-progress');
    progress.hidden = !['running', 'complete', 'error', 'cancelled'].includes(status.status);
    const ratio = status.total > 0 ? Math.min(100, Math.round(status.progress * 100 / status.total)) : 0;
    progress.value = ratio;
    progress.classList.toggle('indeterminate', running && !status.total);
    const message = this.get('xcl-shader-build-status');
    message.textContent = status.message || '';
    message.hidden = !status.message;
  }

  renderXclStatus(status) {
    const report = this.get('xcl-run-status');
    if (!status?.available) {
      report.textContent = status?.message || 'No XCL run report yet.';
      return;
    }
    report.textContent = [
      `Latest report · ${status.updated}`,
      `GPU: ${status.gpu || 'unknown'} · Presents: ${status.presents} · observed shader blobs: ${status.shaderBlobs} · translated pipelines: ${status.translatedPipelines}`,
      status.sampledFrame ? `Sampled frame: ${status.sampledFrame} · non-dark samples: ${status.nonDarkSamples || 0}` : (this.state.xclFrameDiagnostics ? 'No diagnostic frame has been sampled yet.' : 'Frame sampling is off for normal performance.'),
      `DXIL cache: ${status.cacheHits} hits / ${status.cacheMisses} misses / ${status.dxcCompilations} compiles · failed PSO calls: ${status.failedPipelines} · translation errors: ${status.translationErrorCount || 0}`,
      ...(status.translationErrors || []).map(error => `• ${error}`),
      status.crash ? `Crash: ${status.crash}` : `Exit: ${status.exitCode ?? 'still running or report captured before exit'}`,
      `Report: ${status.reportPath}`
    ].join('\n');
  }

  updateLeonida() {
    const info = this.state.leonida ?? {};
    const text = !info.folder ? 'Choose the folder containing MicrosoftGame.Config.' : !info.exists ? 'Selected folder is unavailable'
      : info.executable ? 'Game executable detected'
      : info.encrypted ? 'Encrypted executable only' : 'Game executable missing';
    this.get('leonida-status').textContent = text;
    this.get('clear-leonida').hidden = !info.folder;
  }

  loadPreferences() {
    this.get('pref-intro').checked = this.state.showIntro !== false;
    this.get('pref-recording-delay').checked = this.state.recordingDelay === true;
    this.get('pref-sound').checked = this.state.startupSound !== false;
    this.get('pref-motion').checked = this.state.backgroundMotion !== false;
    this.get('pref-video-preview').checked = this.state.videoPreview !== false;
    this.get('pref-prewarm-shaders').checked = this.state.prewarmShaders !== false;
    this.get('pref-developer-mode').checked = this.state.developerMode === true;
    this.get('pref-layout').value = this.state.defaultLayout === 'carousel' ? 'carousel' : 'grid';
  }

  showGameProperties(data) {
    if (data.id !== this.game?.id || !this.isOpen) return;
    this.gameReady = true;
    this.get('game-display-name').value = data.alias;
    this.get('game-display-name').placeholder = this.game.title || data.defaultName;
    this.get('game-launch-arguments').value = data.args;
    this.get('game-path').textContent = data.executableLinked ? 'Linked · location hidden' : 'No executable linked';
    this.get('game-launch-arguments').disabled = data.xbox || data.leonida;
    this.get('game-arguments-help').textContent = data.leonida ? 'Use Select executable from the game’s library page.' : data.xbox
      ? 'Launch options are managed by the compatibility profile.'
      : 'Passed directly to the Windows game executable.';
    this.get('game-settings-form').querySelector('button[type="submit"]').disabled = false;
  }

  notice(message) {
    const notice = this.get('settings-notice');
    notice.textContent = message;
    notice.hidden = !message;
  }
}
