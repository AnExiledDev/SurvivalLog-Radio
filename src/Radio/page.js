// Radio's web half. Runs in Root.html, the document every game page iframe sits in. Adds a Radio tab to the game's
// record player screen (UI/MusicPanel/MusicPanel.html): station lists, a search box, the music folder, a link box and
// a now playing bar. Talks to the plugin through Exiled's Toolkit's page API (etk.send("slmod|radio|", "<verb>")) and
// is driven by window.__slradio.
(function () {
  if (window.__slradio) return;

  var PREFIX = 'slmod|radio|';
  // The name the plugin's own UI size setting is set under in the Toolkit.
  var UI_SIZE_NAME = 'Radio';
  // The game's record player screen, by its iframe's address.
  var MUSIC_PAGE = /MusicPanel\.html/;
  var STYLE_ID = 'slr-style';
  // Marks everything drawn on the record player screen, so a rebuild can take it all off first.
  var OURS = 'slr-ours';
  // The lists, in the order their tabs show. Search is drawn by the page alone until the player searches.
  var LISTS = ['popular', 'china', 'chinese', 'favorites', 'music', 'search'];
  // Rows drawn at first, and added each time the list is scrolled near its end.
  var ROW_STEP = 60;
  // A slider drag sends the volume at most this often; the last value always goes when it is let go.
  var VOLUME_MS = 100;
  var STAR_SVG = '<svg viewBox="0 0 24 24"><path d="M12 2.8l2.8 5.9 6.4.8-4.7 4.4 1.2 6.4L12 17.2l-5.7 3.1 1.2-6.4-4.7-4.4 6.4-.8z"></path></svg>';

  // What the plugin last sent: the now playing bar, the list on screen and the starred station ids.
  var now = null;
  var list = null;
  var stars = {};
  // The player's side: Records or Radio, the list tab, the text in the two boxes, and how many rows to draw. Kept
  // while the game is running, so the screen opens the way it was left.
  var ui = { isRadio: false, tab: null, query: '', link: '', linkVersion: 0, limit: ROW_STEP };
  // Bumped whenever what the screen shows may have changed. A page drawn at this version is left alone.
  var version = 0;
  var lastWords = null;
  var isTyping = false;
  var volumeTimer = 0;
  var sentVolume = -1;
  var reported = {};
  // The Toolkit's page API (window.ExiledToolkit). The game can run its script after this one, so this stays null
  // until it has; the record player screen check and the messages to the plugin start then.
  var etk = null;
  // The Toolkit's check of the game's record player screen, which hands each one shown to patchPage.
  var watch = null;

  // The plugin hands the words over before its first call, so a missing one only shows its key.
  function say(key) {
    var words = window.__slradioWords || {};
    var text = words[key] || key;

    for (var i = 1; i < arguments.length; i++) text = text.split('{' + (i - 1) + '}').join(String(arguments[i]));

    return text;
  }

  // Through the Toolkit's bridge. Nothing goes before its page API is there; the page says it is ready once it is.
  function send(message) {
    if (etk) etk.send(PREFIX, message);
  }

  // A page error, told to the plugin's log once, so a game update that reshaped the screen shows up there.
  function report(where, e) {
    var text = where + ': ' + (e && e.message ? e.message : String(e));

    if (reported[text]) return;

    reported[text] = true;
    send('diag|' + text);
  }

  function esc(text) {
    return String(text == null ? '' : text).replace(/[&<>"]/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
    });
  }

  // Free text goes URI-encoded, so it never holds the plugin's separator.
  function enc(text) {
    return encodeURIComponent(String(text));
  }

  function pad(n) {
    var text = String(n);

    return text.length < 2 ? '0' + text : text;
  }

  function toggle(node, name, isOn) {
    if (node.classList.contains(name) !== isOn) node.classList.toggle(name, isOn);
  }

  function setText(node, text) {
    if (node.textContent !== text) node.textContent = text;
  }

  // Only a changed drawing touches the page, so the hover under the mouse stays put.
  function setHtml(node, html) {
    if (node.__slrHtml === html) return;

    node.__slrHtml = html;
    node.innerHTML = html;
  }

  // A clickable part. The record player screen takes every click while it is open, so data-interactive only matters
  // if the game ever moves it to the pages that pass clicks through.
  function act(name, arg, extra) {
    return ' class="' + (extra || '') + '" data-interactive data-act="' + name + '"' + (arg != null ? ' data-arg="' + esc(arg) + '"' : '');
  }

  // ---------- API ----------

  // The now playing bar: on, here, kind, id, song, name, title, status, arg, tone, volume, canNext.
  function setNow(data) {
    now = data || null;
    changed();
  }

  // The list: tab, loading, query, note, noteArg, folder, rows. A list for a tab the player has not opened (a search
  // they have not sent yet) is held until they come back to it.
  function setList(data) {
    if (!data) return;

    if (!list || list.tab !== data.tab || list.query !== data.query) ui.limit = ROW_STEP;

    list = data;

    if (!(ui.tab === 'search' && data.tab !== 'search')) ui.tab = data.tab;

    changed();
  }

  function setFavorites(ids) {
    stars = {};
    (ids || []).forEach(function (id) { stars[id] = true; });
    changed();
  }

  // The clipboard's text, for the link box.
  function pasted(text) {
    ui.link = String(text == null ? '' : text);
    ui.linkVersion++;
    changed();
  }

  // Something the screen draws from changed: every record player screen shown is drawn now rather than at the next
  // check.
  function changed() {
    version++;

    if (watch) watch.refresh();
  }

  // ---------- the record player screen ----------

  // The record player screen is a same-origin iframe, so its document can be reached from here. The game loads it
  // afresh each time the screen opens. False asks the Toolkit to hand it over again, until the game has drawn it.
  function patchPage(doc, frame) {
    var words = window.__slradioWords;

    if (words !== lastWords) {
      lastWords = words;
      version++;
    }

    try {
      return draw(pageOf(doc, frame));
    } catch (e) {
      report('drawing the record player screen', e);

      return false;
    }
  }

  function pageOf(doc, frame) {
    return doc.__slrPage || (doc.__slrPage = { doc: doc, frame: frame, win: frame.contentWindow, parts: null, drawn: -1, isNew: true, isShown: false, words: null, link: -1, observer: null, marker: null, listKey: null });
  }

  function draw(p) {
    var parts = partsOf(p);

    if (!parts) return false;

    // A screen opened on the record player the radio plays on starts on the Radio tab. The game makes a new page each
    // time the screen opens, and the plugin let go of the keyboard when the last one closed.
    if (p.isNew) {
      p.isNew = false;
      isTyping = false;

      if (now && now.on && now.here) ui.isRadio = true;
    }

    if (p.drawn === version) return true;

    p.drawn = version;
    toggle(parts.panel, 'slr-on', ui.isRadio);
    drawSwitch(p);

    if (ui.isRadio) {
      // The first look at the Radio tab on this screen asks the plugin for everything.
      if (!p.isShown) {
        p.isShown = true;
        send('show');
      }

      drawRadio(p);
    }

    return true;
  }

  // The game's panel and the parts added to it, searched for again only when the game dropped one.
  function partsOf(p) {
    var parts = p.parts;

    if (parts && parts.panel.isConnected && parts.switcher.parentNode === parts.panel && parts.view.parentNode === parts.panel) return parts;

    var doc = p.doc;
    var panel = doc.querySelector('.panel');
    var head = panel ? panel.querySelector('.head') : null;

    p.parts = null;
    p.drawn = -1;
    p.words = null;
    p.link = -1;
    p.listKey = null;
    forgetObserver(p);

    if (!panel || !head || !panel.parentNode) return null;

    doc.querySelectorAll('.' + OURS).forEach(function (node) { node.remove(); });
    etk.addStyle(doc, STYLE_ID, PAGE_CSS);
    panel.classList.add('slr-panel');

    var switcher = doc.createElement('div');

    switcher.className = 'slr-switch ' + OURS;
    switcher.innerHTML = '<div class="slr-modes">'
      + '<button' + act('mode', 'records', 'slr-mode') + ' data-word="tabRecords"></button>'
      + '<button' + act('mode', 'radio', 'slr-mode') + ' data-word="tabRadio"></button></div>'
      + '<div class="slr-here"></div>';
    switcher.addEventListener('click', onClick);
    panel.insertBefore(switcher, head.nextSibling);

    var view = doc.createElement('div');

    view.className = 'slr-view ' + OURS;
    view.innerHTML = '<div class="slr-now"></div>'
      + '<div class="slr-vol"><span data-word="volume"></span><input type="range" min="0" max="100" step="1" class="slr-slider" data-interactive><span class="slr-vn"></span></div>'
      + '<div class="slr-tabs"></div>'
      + '<div class="slr-bar slr-find"><input type="text" class="slr-input slr-query" maxlength="200" data-hint="searchHint" data-interactive>'
      + '<button' + act('search', null, 'slr-btn go') + ' data-word="search"></button></div>'
      + '<div class="slr-bar slr-folder"><button' + act('folder', null, 'slr-btn') + ' data-word="openFolder"></button>'
      + '<button' + act('scan', null, 'slr-btn') + ' data-word="scanAgain"></button></div>'
      + '<div class="slr-note"></div>'
      + '<div class="slr-list"></div>'
      + '<div class="slr-link"><input type="text" class="slr-input slr-url" maxlength="2048" data-hint="linkHint" data-interactive>'
      + '<button' + act('paste', null, 'slr-btn') + ' data-word="paste"></button>'
      + '<button' + act('url', null, 'slr-btn go') + ' data-word="play"></button></div>';
    view.addEventListener('click', onClick);
    view.addEventListener('keydown', onKey);
    view.addEventListener('input', onInput);
    view.addEventListener('change', onChange);
    view.addEventListener('pointerdown', onPress);
    view.addEventListener('focusin', function (e) { onFocus(e, true); });
    view.addEventListener('focusout', function (e) { onFocus(e, false); });
    panel.appendChild(view);

    p.parts = parts = {
      panel: panel,
      switcher: switcher,
      modes: switcher.querySelectorAll('.slr-mode'),
      here: switcher.querySelector('.slr-here'),
      view: view,
      now: view.querySelector('.slr-now'),
      slider: view.querySelector('.slr-slider'),
      volume: view.querySelector('.slr-vn'),
      tabs: view.querySelector('.slr-tabs'),
      find: view.querySelector('.slr-find'),
      query: view.querySelector('.slr-query'),
      folder: view.querySelector('.slr-folder'),
      note: view.querySelector('.slr-note'),
      list: view.querySelector('.slr-list'),
      link: view.querySelector('.slr-url'),
      labels: toArray(switcher.querySelectorAll('[data-word]')).concat(toArray(view.querySelectorAll('[data-word]'))),
      hints: toArray(view.querySelectorAll('[data-hint]'))
    };
    parts.query.value = ui.query;
    // Drawn at the player's UI size (Exiled's Toolkit), the game's half of the screen with it.
    etk.size(panel.parentNode, { mod: UI_SIZE_NAME });

    return parts;
  }

  function toArray(nodes) {
    return Array.prototype.slice.call(nodes);
  }

  function drawSwitch(p) {
    var parts = p.parts;

    if (p.words !== lastWords) {
      p.words = lastWords;
      parts.labels.forEach(function (node) { setText(node, say(node.getAttribute('data-word'))); });
      parts.hints.forEach(function (node) { node.placeholder = say(node.getAttribute('data-hint')); });
    }

    toArray(parts.modes).forEach(function (node) { toggle(node, 'on', (node.getAttribute('data-arg') === 'radio') === ui.isRadio); });

    var isHere = !!(now && now.on && now.here);

    setText(parts.here, isHere ? say('radioHere') : '');
    toggle(parts.here, 'on', isHere && !ui.isRadio);
  }

  function drawRadio(p) {
    var parts = p.parts;
    var tab = ui.tab;

    setHtml(parts.now, nowHtml());
    toggle(parts.now, 'on', !!(now && now.on));

    var volume = now ? now.volume : null;

    // The plugin's volume shows on the slider, except while the player is moving it.
    if (volume != null && !parts.slider.__slrHeld && !volumeTimer) {
      if (String(parts.slider.value) !== String(volume)) parts.slider.value = String(volume);

      setText(parts.volume, String(volume));
    }

    if (p.link !== ui.linkVersion) {
      p.link = ui.linkVersion;
      parts.link.value = ui.link;
    }

    setHtml(parts.tabs, LISTS.map(function (name) {
      return '<button' + act('tab', name, 'slr-tab' + (name === tab ? ' on' : '')) + '>' + esc(say(name)) + '</button>';
    }).join(''));
    toggle(parts.find, 'on', tab === 'search');
    toggle(parts.folder, 'on', tab === 'music');

    var isListed = !!(list && tab && list.tab === tab);
    var listKey = tab + '|' + (isListed ? list.query : '');

    // A new list starts at its top.
    if (p.listKey !== listKey) {
      p.listKey = listKey;
      parts.list.scrollTop = 0;
    }

    setText(parts.note, noteOf(tab, isListed));
    drawRows(p, isListed && !list.loading ? list.rows || [] : []);
  }

  function noteOf(tab, isListed) {
    if (!isListed) return tab === 'search' ? say('typeToSearch') : say('loading');

    return list.note ? say(list.note, list.noteArg, list.folder) : '';
  }

  function nowHtml() {
    var n = now || { on: false, here: false, name: '', title: '', status: '', arg: '', tone: '', canNext: false };
    var lines = '<div class="slr-nm">' + esc(n.on ? n.name : say('off')) + '</div>';

    if (n.on && n.title) lines += '<div class="slr-line">' + esc(n.title) + '</div>';
    if (n.status) lines += '<div class="slr-line ' + esc(n.tone || '') + '">' + esc(say(n.status, n.arg)) + '</div>';
    if (n.on && !n.here) lines += '<div class="slr-line">' + esc(say('elsewhere')) + '</div>';

    var buttons = '';

    if (n.on && n.canNext) buttons += '<button' + act('next', null, 'slr-btn') + '>' + esc(say('next')) + '</button>';
    if (n.on) buttons += '<button' + act('stop', null, 'slr-btn') + '>' + esc(say('stop')) + '</button>';

    return '<div class="slr-np">' + lines + '</div>' + buttons;
  }

  // ---------- the list ----------

  function playingTag() {
    return '<span class="playing-tag"><span class="bars"><i></i><i></i><i></i></span>' + esc(say('playingTag')) + '</span>';
  }

  // A station row is {id, name, meta}; a song row is its file name.
  function rowHtml(row, index) {
    var isOn = !!(now && now.on);

    if (typeof row === 'string') {
      var isSong = isOn && now.kind === 'folder' && now.song === index;

      return '<div' + act('file', index, 'slr-row' + (isSong ? ' is-playing' : '')) + '><span class="no">' + pad(index + 1) + '</span>'
        + '<span class="slr-rn"><b>' + esc(row) + '</b></span>' + (isSong ? playingTag() : '') + '</div>';
    }

    var isPlaying = isOn && now.kind === 'station' && now.id === row.id;
    var isStarred = stars[row.id] === true;

    return '<div' + act('play', row.id, 'slr-row' + (isPlaying ? ' is-playing' : '')) + '>'
      + '<span class="slr-rn"><b>' + esc(row.name) + '</b>' + (row.meta ? '<i>' + esc(row.meta) + '</i>' : '') + '</span>'
      + (isPlaying ? playingTag() : '')
      + '<button' + act('fav', row.id, 'slr-star' + (isStarred ? ' on' : '')) + ' title="' + esc(say(isStarred ? 'unstar' : 'star')) + '">' + STAR_SVG + '</button></div>';
  }

  // The first rows, then more each time the end of the list scrolls near. A page with no IntersectionObserver gets
  // every row at once.
  function drawRows(p, rows) {
    var parts = p.parts;
    var Observer = p.win ? p.win.IntersectionObserver : null;
    var count = Observer ? Math.min(rows.length, ui.limit) : rows.length;
    var html = [];

    for (var i = 0; i < count; i++) html.push(rowHtml(rows[i], i));

    var hasMore = count < rows.length;

    if (hasMore) html.push('<div class="slr-more"></div>');

    setRows(p.doc, parts.list, html);

    if (!hasMore) {
      forgetObserver(p);

      return;
    }

    watchEnd(p, Observer, parts.list.lastElementChild);
  }

  // The marker after the last row drawn. Once it comes within a few rows of sight, more rows are drawn.
  function watchEnd(p, Observer, marker) {
    if (marker === p.marker) return;

    if (p.observer) p.observer.disconnect();
    else p.observer = new Observer(onNearEnd, { root: p.parts.list, rootMargin: '0px 0px 300px 0px' });

    p.observer.observe(marker);
    p.marker = marker;
  }

  function onNearEnd(entries) {
    for (var i = 0; i < entries.length; i++) {
      if (entries[i].isIntersecting) {
        ui.limit += ROW_STEP;
        changed();

        return;
      }
    }
  }

  function forgetObserver(p) {
    if (p.observer) p.observer.disconnect();

    p.observer = null;
    p.marker = null;
  }

  // The list drawn row by row: a row whose drawing did not change keeps its node, so a star or a new song playing
  // replaces only the rows it touches.
  function setRows(doc, view, rows) {
    var old = view.__slrRows || Object.create(null);
    var kept = Object.create(null);
    var nodes = [];

    for (var i = 0; i < rows.length; i++) {
      var spare = old[rows[i]];
      var node = spare && spare.length ? spare.pop() : rowNode(doc, rows[i]);

      (kept[rows[i]] || (kept[rows[i]] = [])).push(node);
      nodes.push(node);
    }

    view.__slrRows = kept;

    var at = view.firstChild;

    for (i = 0; i < nodes.length; i++) {
      if (nodes[i] === at) at = at.nextSibling;
      else view.insertBefore(nodes[i], at);
    }

    while (at) {
      var next = at.nextSibling;

      view.removeChild(at);
      at = next;
    }
  }

  function rowNode(doc, html) {
    var holder = doc.createElement('div');

    holder.innerHTML = html;

    return holder.firstChild;
  }

  // ---------- the player's input ----------

  function onClick(e) {
    var node = e.target && e.target.closest ? e.target.closest('[data-act]') : null;

    if (!node || node.disabled) return;

    e.stopPropagation();

    var arg = node.getAttribute('data-arg');

    switch (node.getAttribute('data-act')) {
      case 'mode': setMode(arg === 'radio'); break;
      case 'tab': openTab(arg); break;
      case 'play': send('play|' + enc(arg)); break;
      case 'fav': send('fav|' + enc(arg) + '|' + (stars[arg] === true ? '0' : '1')); break;
      case 'file': send('file|' + arg); break;
      case 'next': send('next'); break;
      case 'stop': send('stop'); break;
      case 'search': search(); break;
      case 'folder': send('folder'); break;
      case 'scan': send('scan'); break;
      case 'paste': send('paste'); break;
      case 'url': playLink(); break;
      default: break;
    }
  }

  function setMode(isRadio) {
    if (ui.isRadio === isRadio) return;

    ui.isRadio = isRadio;

    // The text boxes go out of sight with the Radio tab, and may not say they lost the keyboard.
    if (!isRadio) typing(false);

    changed();
  }

  function openTab(tab) {
    if (LISTS.indexOf(tab) < 0) return;

    ui.tab = tab;
    ui.limit = ROW_STEP;

    if (tab !== 'search') send('browse|' + tab);

    changed();
  }

  function search() {
    ui.tab = 'search';
    send('search|' + enc(ui.query));
    changed();
  }

  function playLink() {
    if (ui.link.trim() !== '') send('url|' + enc(ui.link.trim()));
  }

  // Enter searches or plays the link, unless it is ending a word typed with an input method.
  function onKey(e) {
    if (e.key !== 'Enter' || e.isComposing || e.keyCode === 229) return;

    if (isBox(e.target, 'slr-query')) search();
    else if (isBox(e.target, 'slr-url')) playLink();
  }

  function onInput(e) {
    var target = e.target;

    if (isBox(target, 'slr-query')) ui.query = String(target.value);
    else if (isBox(target, 'slr-url')) ui.link = String(target.value);
    else if (isBox(target, 'slr-slider')) {
      setText(target.parentNode.querySelector('.slr-vn'), String(target.value));

      if (!volumeTimer) volumeTimer = setTimeout(function () { volumeTimer = 0; sendVolume(target); }, VOLUME_MS);
    }
  }

  // The slider was let go: its last value goes now.
  function onChange(e) {
    if (!isBox(e.target, 'slr-slider')) return;

    e.target.__slrHeld = false;
    clearTimeout(volumeTimer);
    volumeTimer = 0;
    sendVolume(e.target);
  }

  function onPress(e) {
    if (isBox(e.target, 'slr-slider')) e.target.__slrHeld = true;
  }

  function sendVolume(slider) {
    var value = Math.max(0, Math.min(100, Math.round(Number(slider.value)) || 0));

    // The same value twice (the last throttled step, then the let go) goes once, unless the plugin has another one.
    if (value === sentVolume && now && now.volume === value) return;

    sentVolume = value;
    send('volume|' + value);
  }

  // A text box took or gave up the keyboard. The plugin turns on the keyboard and the input method for it.
  function onFocus(e, isIn) {
    if (isBox(e.target, 'slr-query') || isBox(e.target, 'slr-url')) typing(isIn);
  }

  function typing(isOn) {
    if (isTyping === isOn) return;

    isTyping = isOn;
    send('focus|' + (isOn ? '1' : '0'));
  }

  function isBox(node, name) {
    return !!(node && node.classList && node.classList.contains(name));
  }

  // The game's record player screen styles (MusicPanel.html): its colours and the playing marks the rows borrow.
  var PAGE_CSS = [
    '.panel.slr-panel{width:520px}',
    '.panel.slr-panel.slr-on{height:76vh}',
    '.slr-panel.slr-on>.list,.slr-panel.slr-on>.empty{display:none}',
    '.slr-switch{padding:12px 34px 0;flex-shrink:0}',
    '.slr-modes{display:flex;gap:6px}',
    '.slr-mode{flex:1;font:inherit;font-size:13px;padding:7px 0;border-radius:8px;cursor:pointer;color:var(--text-dim);background:rgba(255,255,255,.04);border:1px solid rgba(255,255,255,.09);letter-spacing:1px}',
    '.slr-mode:hover{color:var(--text-main);border-color:rgba(232,180,97,.35)}',
    '.slr-mode.on{color:var(--accent);border-color:rgba(232,180,97,.55);background:rgba(232,180,97,.1)}',
    '.slr-here{display:none;margin-top:10px;font-size:12px;color:var(--accent)}.slr-here.on{display:block}',
    '.slr-view{display:none;flex:1;min-height:0;flex-direction:column;gap:8px;padding:12px 26px 30px}',
    '.slr-on>.slr-view{display:flex}',
    '.slr-now{display:flex;align-items:center;gap:8px;padding:10px 12px;border-radius:10px;background:rgba(255,255,255,.04);border:1px solid rgba(255,255,255,.08);flex-shrink:0}',
    '.slr-now.on{border-color:rgba(232,180,97,.28);background:rgba(232,180,97,.07)}',
    '.slr-np{flex:1;min-width:0}',
    '.slr-nm{font-size:14px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;color:var(--text-dim)}',
    '.slr-now.on .slr-nm{color:var(--accent)}',
    '.slr-line{font-size:11.5px;color:var(--text-dim);margin-top:3px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}',
    '.slr-line.good{color:#9fc88a}.slr-line.wait{color:#d9c27a}.slr-line.bad{color:#e08a8a;white-space:normal}',
    '.slr-btn{font:inherit;font-size:12px;padding:6px 12px;border-radius:7px;cursor:pointer;color:var(--text-main);background:rgba(255,255,255,.07);border:1px solid rgba(255,255,255,.12);white-space:nowrap;flex-shrink:0}',
    '.slr-btn:hover{border-color:rgba(232,180,97,.5);color:var(--accent)}',
    '.slr-btn.go{color:#2a1c08;background:var(--accent);border-color:var(--accent);font-weight:600}',
    '.slr-btn.go:hover{color:#2a1c08;filter:brightness(1.08)}',
    '.slr-vol{display:flex;align-items:center;gap:10px;font-size:12px;color:var(--text-dim);flex-shrink:0;padding:0 2px}',
    '.slr-slider{flex:1;accent-color:var(--accent);cursor:pointer}',
    '.slr-vn{width:30px;text-align:right;font-family:Consolas,monospace}',
    '.slr-tabs{display:flex;flex-wrap:wrap;gap:5px;flex-shrink:0}',
    '.slr-tab{font:inherit;font-size:12px;padding:4px 11px;border-radius:12px;cursor:pointer;color:var(--text-dim);background:transparent;border:1px solid rgba(255,255,255,.12);white-space:nowrap}',
    '.slr-tab:hover{color:var(--text-main)}',
    '.slr-tab.on{color:var(--accent);border-color:rgba(232,180,97,.55);background:rgba(232,180,97,.1)}',
    '.slr-bar{display:none;gap:6px;align-items:center;flex-shrink:0}.slr-bar.on{display:flex}',
    '.slr-input{flex:1;min-width:0;font:inherit;font-size:12.5px;padding:6px 10px;border-radius:7px;border:1px solid rgba(255,255,255,.14);background:rgba(0,0,0,.35);color:var(--text-main);outline:none}',
    '.slr-input:focus{border-color:rgba(232,180,97,.55)}',
    '.slr-note{font-size:12px;color:var(--text-dim);line-height:1.5;flex-shrink:0;word-break:break-word}',
    '.slr-note:empty{display:none}',
    '.slr-list{flex:1;min-height:0;overflow-y:auto;display:flex;flex-direction:column;gap:2px;padding-right:4px}',
    '.slr-list::-webkit-scrollbar{width:4px}.slr-list::-webkit-scrollbar-thumb{background:rgba(255,255,255,.18);border-radius:2px}',
    '.slr-row{display:flex;align-items:center;gap:12px;padding:8px 10px;border-radius:9px;cursor:pointer;border:1px solid transparent;flex-shrink:0}',
    '.slr-row:hover{background:rgba(255,255,255,.055);border-color:rgba(255,255,255,.09)}',
    '.slr-row.is-playing{background:rgba(232,180,97,.09);border-color:rgba(232,180,97,.28)}',
    '.slr-row .no{width:auto;min-width:24px}',
    '.slr-rn{flex:1;min-width:0;display:flex;flex-direction:column}',
    '.slr-rn b{font-weight:normal;font-size:13.5px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}',
    '.slr-row.is-playing .slr-rn b{color:var(--accent)}',
    '.slr-rn i{font-style:normal;font-size:11px;color:var(--text-dim);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;margin-top:2px}',
    '.slr-star{flex-shrink:0;width:26px;height:26px;display:flex;align-items:center;justify-content:center;background:none;border:0;cursor:pointer;color:rgba(255,255,255,.28);padding:0}',
    '.slr-star svg{width:17px;height:17px;fill:none;stroke:currentColor;stroke-width:1.6}',
    '.slr-star:hover,.slr-star.on{color:var(--accent)}.slr-star.on svg{fill:currentColor}',
    '.slr-more{height:1px;flex-shrink:0}',
    '.slr-link{display:flex;gap:6px;align-items:center;flex-shrink:0;padding-top:8px;border-top:1px solid rgba(255,255,255,.07)}'
  ].join('\n');

  // Runs once the Toolkit's page API is there: at once when its script ran first, else when it runs. The plugin sends
  // everything again when the page says it is ready, so that waits until a message can reach it.
  function startWithToolkit(api) {
    etk = api;
    watch = etk.watchPages(MUSIC_PAGE, patchPage);
    send('ready');
  }

  window.__slradio = { now: setNow, list: setList, favorites: setFavorites, pasted: pasted };
  (window.ExiledToolkitReady = window.ExiledToolkitReady || []).push(startWithToolkit);
})();
