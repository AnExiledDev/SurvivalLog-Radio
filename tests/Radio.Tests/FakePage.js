// Just enough of Root.html and the game's record player screen (UI/MusicPanel/MusicPanel.html) for page.js and Exiled's
// Toolkit's page API (api.js) to run in Jint: the root document with its page iframes, the vuplex bridge, and clicks
// and other events that bubble. Test code only; nothing here ships.
var window = this;

var location = { protocol: 'https:', href: 'https://game.local/Root.html' };

var intervals = [];

function setInterval(callback) {
  intervals.push(callback);

  return intervals.length;
}

// One run of every interval the scripts set, which is how the Toolkit's 250 ms page check runs.
function poll() {
  intervals.slice().forEach(function (callback) { callback(); });
}

// Held until a test lets the time pass with runTimers().
var timers = {};
var nextTimer = 1;

function setTimeout(callback) {
  timers[nextTimer] = callback;

  return nextTimer++;
}

function clearTimeout(id) {
  delete timers[id];
}

// Every held timer fires once, oldest first.
function runTimers() {
  var due = timers;

  timers = {};
  Object.keys(due).forEach(function (id) { due[id](); });
}

var FakeElement = class {
  constructor(tagName) {
    var element = this;

    // Browsers report HTML tag names in capitals, and page.js checks them that way.
    this.tagName = tagName.charAt(0) === '#' ? tagName : tagName.toUpperCase();
    this.id = '';
    this.parentElement = null;
    this.children = [];
    this.text = '';
    this.attrs = {};
    this.classes = [];
    this.listeners = {};
    this.style = {};
    this.value = '';
    this.disabled = false;
    this.scrollTop = 0;
    this.classList = {
      contains: function (name) { return element.classes.indexOf(name) >= 0; },
      add: function () {
        for (var i = 0; i < arguments.length; i++) if (element.classes.indexOf(arguments[i]) < 0) element.classes.push(arguments[i]);
      },
      remove: function () {
        var names = Array.prototype.slice.call(arguments);

        element.classes = element.classes.filter(function (c) { return names.indexOf(c) < 0; });
      },
      toggle: function (name, isOn) {
        var has = element.classes.indexOf(name) >= 0;

        if (isOn === undefined) isOn = !has;
        if (isOn && !has) element.classes.push(name);
        if (!isOn && has) element.classes.splice(element.classes.indexOf(name), 1);

        return isOn;
      }
    };
  }

  get parentNode() { return this.parentElement; }

  get firstChild() { return this.children.length ? this.children[0] : null; }

  get lastElementChild() {
    for (var i = this.children.length - 1; i >= 0; i--) if (this.children[i].tagName !== '#text') return this.children[i];

    return null;
  }

  get childElementCount() { return this.children.filter(function (child) { return child.tagName !== '#text'; }).length; }

  // On a page: under a document's head or body, which are the roots here.
  get isConnected() {
    var top = this;

    while (top.parentElement) top = top.parentElement;

    return top.isRoot === true;
  }

  get nextSibling() { return this.sibling(1); }

  get previousSibling() { return this.sibling(-1); }

  sibling(step) {
    if (!this.parentElement) return null;

    return this.parentElement.children[this.parentElement.children.indexOf(this) + step] || null;
  }

  get className() { return this.classes.join(' '); }

  set className(value) { this.classes = String(value).split(' ').filter(function (name) { return name !== ''; }); }

  // As in a browser: an element's text is all of its descendants' text, and setting it replaces the children.
  get textContent() {
    return this.text + this.children.map(function (child) { return child.textContent; }).join('');
  }

  set textContent(value) {
    this.children.slice().forEach(function (child) { this.removeChild(child); }, this);
    this.text = value == null ? '' : String(value);
  }

  // The page draws with innerHTML; this parses the little HTML it writes: tags, quoted attributes, text and the
  // entities esc() makes. Reading it back gives nothing useful, and the script never does.
  get innerHTML() { return ''; }

  set innerHTML(html) {
    this.textContent = '';

    var open = [this];
    var tokens = String(html).match(/<\/?[a-zA-Z0-9]+[^>]*>|[^<]+/g) || [];

    tokens.forEach(function (token) {
      var top = open[open.length - 1];

      if (token.charAt(0) !== '<') {
        var text = new FakeElement('#text');

        text.text = decodeEntities(token);
        top.appendChild(text);

        return;
      }

      var closing = /^<\/([a-zA-Z0-9]+)/.exec(token);

      if (closing) {
        for (var i = open.length - 1; i > 0; i--) {
          if (open[i].tagName === closing[1].toUpperCase()) {
            open.length = i;
            break;
          }
        }

        return;
      }

      var tag = /^<([a-zA-Z0-9]+)/.exec(token)[1];
      var element = new FakeElement(tag);
      var attrPattern = /([\w-]+)(?:="([^"]*)")?/g;
      var rest = token.slice(tag.length + 1, -1);
      var match;

      while ((match = attrPattern.exec(rest))) {
        var value = match[2] == null ? '' : decodeEntities(match[2]);

        if (match[1] === 'class') element.className = value;
        else if (match[1] === 'id') element.id = value;
        else if (match[1] === 'disabled') element.disabled = true;
        else element.attrs[match[1]] = value;
      }

      top.appendChild(element);

      if (!/^(img|input|br)$/i.test(tag)) open.push(element);
    });
  }

  hasAttribute(name) { return Object.prototype.hasOwnProperty.call(this.attrs, name); }

  getAttribute(name) { return this.hasAttribute(name) ? this.attrs[name] : null; }

  setAttribute(name, value) { this.attrs[name] = String(value); }

  addEventListener(type, listener) {
    (this.listeners[type] = this.listeners[type] || []).push(listener);
  }

  // A fragment's children move into the parent, as in a browser.
  appendChild(child) {
    if (child.tagName === '#fragment') {
      child.children.slice().forEach(function (moved) { this.appendChild(moved); }, this);

      return child;
    }

    if (child.parentElement) child.parentElement.removeChild(child);

    child.parentElement = this;
    this.children.push(child);

    return child;
  }

  insertBefore(child, before) {
    if (!before) return this.appendChild(child);

    if (child.parentElement) child.parentElement.removeChild(child);

    child.parentElement = this;
    this.children.splice(this.children.indexOf(before), 0, child);

    return child;
  }

  replaceChild(child, old) {
    if (child.parentElement) child.parentElement.removeChild(child);

    this.children[this.children.indexOf(old)] = child;
    child.parentElement = this;
    old.parentElement = null;

    return old;
  }

  removeChild(child) {
    this.children = this.children.filter(function (c) { return c !== child; });
    child.parentElement = null;

    return child;
  }

  remove() {
    if (this.parentElement) this.parentElement.removeChild(this);
  }

  contains(other) {
    for (var element = other; element; element = element.parentElement) {
      if (element === this) return true;
    }

    return false;
  }

  // Selector lists and the descendant combinator: ".panel .head", ".slr-list .slr-row, .slr-tab".
  matches(selector) {
    var element = this;

    return String(selector).split(',').some(function (one) {
      var parts = one.trim().split(/\s+/);

      if (!matchesCompound(element, parts[parts.length - 1])) return false;

      var at = element.parentElement;

      for (var i = parts.length - 2; i >= 0; i--) {
        while (at && !matchesCompound(at, parts[i])) at = at.parentElement;

        if (!at) return false;

        at = at.parentElement;
      }

      return true;
    });
  }

  closest(selector) {
    for (var element = this; element; element = element.parentElement) {
      if (element.matches(selector)) return element;
    }

    return null;
  }

  querySelectorAll(selector) {
    searches++;

    return descendantsOf([this]).filter(function (element) { return element.matches(selector); });
  }

  querySelector(selector) {
    return this.querySelectorAll(selector)[0] || null;
  }
};

// Every search of a page (querySelector, querySelectorAll, getElementById), so a test can see a check that searched
// nothing.
var searches = 0;

// Compound selectors of a tag, classes, ids and attributes ("iframe", ".slr-mode", "[data-act]"); the script uses
// nothing else.
function selectorSteps(selector) {
  var steps = selector.match(/^[a-z]+|\.[\w-]+|#[\w-]+|\[[\w-]+\]/g) || [];

  if (steps.join('') !== selector) throw new Error('the fake page does not know the selector "' + selector + '"');

  return steps;
}

function matchesCompound(element, compound) {
  return selectorSteps(compound).every(function (step) { return matchesStep(element, step); });
}

function decodeEntities(text) {
  return text.replace(/&(amp|lt|gt|quot|middot);/g, function (all, name) {
    return { amp: '&', lt: '<', gt: '>', quot: '"', middot: '\u00b7' }[name];
  });
}

function matchesStep(element, step) {
  switch (step.charAt(0)) {
    case '.':
      return element.classList.contains(step.slice(1));
    case '#':
      return element.id === step.slice(1);
    case '[':
      return element.hasAttribute(step.slice(1, -1));
    default:
      return element.tagName === step.toUpperCase();
  }
}

function descendantsOf(roots) {
  var found = [];

  roots.forEach(function (root) {
    root.children.forEach(function walk(child) {
      found.push(child);
      child.children.forEach(walk);
    });
  });

  return found;
}

var FakeDocument = class {
  // href is the page's address; the Toolkit's page check never hands over a document still at about:blank.
  constructor(href) {
    this.head = new FakeElement('head');
    this.body = new FakeElement('body');
    this.head.isRoot = true;
    this.body.isRoot = true;
    this.defaultView = { location: { href: href || 'about:blank' } };
  }

  createElement(tagName) { return new FakeElement(tagName); }

  createElementNS(namespace, tagName) { return new FakeElement(tagName); }

  createTextNode(text) {
    var node = new FakeElement('#text');

    node.textContent = text;

    return node;
  }

  createDocumentFragment() { return new FakeElement('#fragment'); }

  getElementById(id) {
    searches++;

    return descendantsOf([this.head, this.body]).filter(function (element) { return element.id === id; })[0] || null;
  }

  // The Toolkit's page check finds the page iframes this way; it is not one of page.js's searches, so it is not counted.
  getElementsByTagName(tagName) {
    return descendantsOf([this.head, this.body]).filter(function (element) { return element.tagName === tagName.toUpperCase(); });
  }

  querySelectorAll(selector) {
    searches++;

    return descendantsOf([this.head, this.body]).filter(function (element) { return element.matches(selector); });
  }

  querySelector(selector) {
    return this.querySelectorAll(selector)[0] || null;
  }
};

// Root.html's own document, which holds the page iframes.
var document = new FakeDocument();

// What the script posts to the plugin.
var posted = [];

window.vuplex = {
  postMessage: function (message) { posted.push(message); }
};

var MUSIC_SRC = 'https://game.local/UI/MusicPanel/MusicPanel.html';

// The game's record player screen, as far as page.js reaches into it: the panel with its title, and the record list
// Vue draws (one .row per record, .is-playing on the one playing). The game makes a new page every time the screen
// opens. With hasObserver, the page's window has an IntersectionObserver, as the game's Chromium does.
function openMusic(hasObserver) {
  var frame = document.body.appendChild(new FakeElement('iframe'));
  var doc = new FakeDocument(MUSIC_SRC);

  frame.src = MUSIC_SRC;
  frame.contentDocument = doc;
  doc.body.innerHTML = '<div id="app"><div class="modal-overlay"><div class="panel-wrap"><button class="close-x"></button>'
    + '<div class="panel"><div class="head"><div class="title">Record Player</div><div class="sub">Pick a record</div></div>'
    + '<div class="list"><div class="row"><span class="no">01</span><span class="name">Morning Rain</span></div>'
    + '<div class="row is-playing"><span class="no">02</span><span class="name">Old Road</span></div></div>'
    + '</div></div></div></div>';
  frame.contentWindow = hasObserver ? { IntersectionObserver: FakeObserver } : {};

  return frame;
}

// The player closes the screen: the game takes its page away.
function closeMusic() {
  musicFrame().remove();
}

function musicFrame() {
  return document.querySelectorAll('iframe').filter(function (frame) { return frame.src === MUSIC_SRC; })[0];
}

function musicPage() {
  return musicFrame().contentDocument;
}

// The text a node shows, every descendant included.
function shown(selector) {
  var node = musicPage().querySelector(selector);

  return node ? node.textContent : '';
}

// Whether the screen shows a node: it and every parent up to the page are drawn. The page's own CSS is not run here,
// so this reads the classes page.js switches: the Radio view shows on a panel with slr-on, and the record list does not.
function isShown(selector) {
  var node = musicPage().querySelector(selector);

  if (!node) return false;

  var panel = musicPage().querySelector('.panel');
  var isRadio = panel.classList.contains('slr-on');

  for (var at = node; at; at = at.parentElement) {
    if (at.classList.contains('slr-view') && !isRadio) return false;
    if ((at.classList.contains('list') || at.classList.contains('empty')) && at.parentElement === panel && isRadio) return false;
    if ((at.classList.contains('slr-bar') || at.classList.contains('slr-here')) && !at.classList.contains('on')) return false;
  }

  return true;
}

// An event of any type, bubbling from the target as focusin, focusout, input, change and keydown do in a browser.
function fire(target, type, extra) {
  var isStopped = false;
  var event = { type: type, target: target, stopPropagation: function () { isStopped = true; } };

  Object.keys(extra || {}).forEach(function (key) { event[key] = extra[key]; });

  for (var node = target; node && !isStopped; node = node.parentElement) {
    (node.listeners[type] || []).slice().forEach(function (listener) { listener(event); });
  }
}

// Typing into a text box: it takes focus, its value changes, and the page hears the input.
function type(input, text) {
  fire(input, 'focusin');
  input.value = text;
  fire(input, 'input');
}

function pressEnter(input, extra) {
  var event = { key: 'Enter', keyCode: 13, isComposing: false };

  Object.keys(extra || {}).forEach(function (key) { event[key] = extra[key]; });
  fire(input, 'keydown', event);
}

// Dragging the volume slider to these values, without letting go.
function drag(values) {
  var slider = musicPage().querySelector('.slr-slider');

  fire(slider, 'pointerdown');
  values.forEach(function (value) {
    slider.value = String(value);
    fire(slider, 'input');
  });
}

function letGo() {
  fire(musicPage().querySelector('.slr-slider'), 'change');
}

function click(target) {
  var isStopped = false;
  var event = { target: target, stopPropagation: function () { isStopped = true; } };

  for (var node = target; node && !isStopped; node = node.parentElement) {
    (node.listeners.click || []).slice().forEach(function (listener) { listener(event); });
  }
}

// The first part of the record player screen with this data-act (and data-arg, when given).
function act(name, arg) {
  return musicPage().querySelectorAll('[data-act]').filter(function (node) {
    return node.getAttribute('data-act') === name && (arg === undefined || node.getAttribute('data-arg') === String(arg));
  })[0] || null;
}

// The IntersectionObserver page.js uses for long lists. It only fires when a test scrolls with scrollToEnd().
var observers = [];

var FakeObserver = class {
  constructor(callback, options) {
    this.callback = callback;
    this.options = options;
    this.targets = [];
    observers.push(this);
  }

  observe(target) { this.targets.push(target); }

  disconnect() { this.targets = []; }
};

// The player scrolls the list to its end: every watched node still on the page comes into sight.
function scrollToEnd() {
  observers.forEach(function (observer) {
    var entries = observer.targets.filter(function (target) { return target.isConnected; }).map(function (target) {
      return { target: target, isIntersecting: true };
    });

    if (entries.length) observer.callback(entries);
  });
}
