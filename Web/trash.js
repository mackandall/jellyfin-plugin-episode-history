/*
 * Episode History — the trash in the web client.
 *   - A "Trash" section at the bottom of Live TV > Recordings: what was deleted, newest first,
 *     with Restore (anyone allowed to delete) and Delete now / Empty the trash (administrators).
 *   - jellyfin-web's delete confirmations ("…will delete it from both the file system and your
 *     media library") say what really happens while the trash is on: it goes to the trash.
 *   - window.EpisodeHistoryTrash.days: how long the trash keeps things (null: deletes are for
 *     good), for recordings.js's own wording.
 *
 * Data from GET /EpisodeHistory/Trash. Loaded by guidemenu.js, so no extra index.html patch.
 *
 * VERSION-SENSITIVE FILE — built against this server's jellyfin-web 12.x:
 *   - Live TV > Recordings is React, `#/livetv?tab=3`, `div#liveTvPage` holding
 *     `div.verticalSection` children (see recordings.js).
 *   - The confirmations are the en-us strings ConfirmDeleteItem(ByName), ConfirmDeleteItems and
 *     ConfirmDeleteSeries, shown as plain text in a dialog. Other languages are left as they are.
 */
(function () {
  'use strict';

  var SECTION_CLASS = 'episodeHistoryTrash';
  var MAX_AGE_MS = 30000;
  var info = null;       // the last GET /EpisodeHistory/Trash
  var infoAt = 0;
  var inflight = null;

  window.EpisodeHistoryTrash = { days: null };

  function log(msg) {
    if (window.console && console.debug) {
      console.debug('[EpisodeHistory trash] ' + msg);
    }
  }

  function signedIn() {
    return window.ApiClient && window.ApiClient.getCurrentUserId && window.ApiClient.getCurrentUserId();
  }

  function load(force) {
    if (!signedIn()) {
      return Promise.resolve(null);
    }
    if (!force && info && Date.now() - infoAt < MAX_AGE_MS) {
      return Promise.resolve(info);
    }
    if (inflight) {
      return inflight;
    }
    var client = window.ApiClient;
    inflight = client.ajax({ type: 'GET', url: client.getUrl('EpisodeHistory/Trash'), dataType: 'json' }).then(function (t) {
      inflight = null;
      info = t;
      infoAt = Date.now();
      window.EpisodeHistoryTrash.days = t && t.Enabled ? t.KeepDays : null;
      return t;
    }, function () {
      inflight = null;
      info = null;
      infoAt = Date.now();
      window.EpisodeHistoryTrash.days = null;
      return null;
    });
    return inflight;
  }

  function days(n) {
    return n === 1 ? '1 day' : n + ' days';
  }

  function size(bytes) {
    return bytes >= 1073741824 ? (bytes / 1073741824).toFixed(1) + ' GB' : Math.max(1, Math.round(bytes / 1048576)) + ' MB';
  }

  function ago(utc) {
    var mins = Math.round((Date.now() - new Date(utc)) / 60000);
    if (mins < 1) {
      return 'just now';
    }
    if (mins < 60) {
      return mins + ' min ago';
    }
    if (mins < 12 * 60) {
      return Math.round(mins / 60) + ' h ago';
    }
    return new Date(utc).toLocaleString([], { weekday: 'short', hour: 'numeric', minute: '2-digit' });
  }

  function until(utc) {
    var mins = Math.round((new Date(utc) - Date.now()) / 60000);
    if (mins < 5) {
      return 'soon';
    }
    if (mins < 60) {
      return 'in ' + mins + ' min';
    }
    var h = Math.floor(mins / 60);
    return h < 24 ? 'in ' + h + ' h' : 'in ' + days(Math.floor(h / 24)) + (h % 24 ? ' ' + (h % 24) + ' h' : '');
  }

  function el(tag, className, text) {
    var e = document.createElement(tag);
    if (className) {
      e.className = className;
    }
    if (text != null) {
      e.textContent = text;
    }
    return e;
  }

  function addStyles() {
    if (document.getElementById('episodeHistoryTrashStyles')) {
      return;
    }
    var style = el('style');
    style.id = 'episodeHistoryTrashStyles';
    style.textContent = [
      '.' + SECTION_CLASS + ' .ehTrashSummary { opacity: .7; margin: .2em 0 .8em; }',
      '.' + SECTION_CLASS + ' .ehTrashRow { display: flex; align-items: center; gap: 1em; padding: .6em .8em; border-radius: 6px; background: rgba(128,128,128,.12); margin-bottom: .4em; }',
      '.' + SECTION_CLASS + ' .ehTrashText { flex: 1; min-width: 0; }',
      '.' + SECTION_CLASS + ' .ehTrashTitle { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }',
      '.' + SECTION_CLASS + ' .ehTrashSub { opacity: .65; font-size: .9em; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }',
      '.' + SECTION_CLASS + ' .ehTrashGone { opacity: .65; font-size: .85em; white-space: nowrap; }',
      '.' + SECTION_CLASS + ' button { padding: .45em 1em; border-radius: 6px; border: 0; background: rgba(128,128,128,.25); color: inherit; font: inherit; cursor: pointer; }',
      '.' + SECTION_CLASS + ' button:hover, .' + SECTION_CLASS + ' button:focus { background: #00a4dc; color: #fff; }',
      '.' + SECTION_CLASS + ' button.ehTrashArmed { background: #c62828; color: #fff; }',
      '.' + SECTION_CLASS + ' .ehTrashNote { margin: .4em 0 .8em; }'
    ].join('\n');
    document.head.appendChild(style);
  }

  // Two clicks instead of a confirm() dialog, as in recordings.js.
  function twoStep(button, armedText, action) {
    var plain = button.textContent;
    var armed = false;
    button.addEventListener('click', function () {
      if (!armed) {
        armed = true;
        button.textContent = armedText;
        button.classList.add('ehTrashArmed');
        setTimeout(function () {
          armed = false;
          button.textContent = plain;
          button.classList.remove('ehTrashArmed');
        }, 4000);
        return;
      }
      button.disabled = true;
      action();
    });
  }

  // ---- The section on Live TV > Recordings ----------------------------------------------------

  function recordingsTab() {
    var hash = window.location.hash || '';
    if (hash.indexOf('#/livetv') !== 0) {
      return null;
    }
    var params = new URLSearchParams(hash.indexOf('?') === -1 ? '' : hash.slice(hash.indexOf('?') + 1));
    if (params.get('tab') !== '3') {
      return null;
    }
    var page = document.querySelector('#liveTvPage:not(.hide)');
    if (!page) {
      return null;
    }
    // Once its own sections are in (they replace the page's children when they load).
    var loaded = [].slice.call(page.children).some(function (c) {
      var h = c.querySelector && c.querySelector('h2');
      return h && /^(Recently Added Recordings|All Recordings)$/.test((h.textContent || '').trim());
    });
    return loaded ? page : null;
  }

  function render(section, t, note) {
    section.innerHTML = '';
    var header = el('div', 'sectionTitleContainer sectionTitleContainer-cards padded-left');
    header.appendChild(el('h2', 'sectionTitle sectionTitle-cards', 'Trash'));
    section.appendChild(header);
    var body = el('div', 'padded-left padded-right');
    section.appendChild(body);
    var items = (t && t.Items) || [];
    body.appendChild(el('div', 'ehTrashSummary',
      (items.length ? items.length + (items.length === 1 ? ' item, ' : ' items, ') + size(t.Bytes) + '. ' : 'Nothing in the trash. ')
      + (t.Enabled
        ? 'Deleted recordings stay here ' + days(t.KeepDays) + (t.MinFreeGb > 0 ? ', or until their drive has under ' + t.MinFreeGb + ' GB free' : '') + '.'
        : 'The trash is off: deletes are for good.')));
    if (note) {
      body.appendChild(el('div', 'ehTrashNote', note));
    }
    items.forEach(function (e) {
      var row = el('div', 'ehTrashRow');
      var text = el('div', 'ehTrashText');
      text.appendChild(el('div', 'ehTrashTitle', e.Title + (e.Detail ? ' — ' + e.Detail : '')));
      text.appendChild(el('div', 'ehTrashSub', ['Deleted ' + ago(e.DeletedUtc) + (e.DeletedBy ? ' by ' + e.DeletedBy : ''), size(e.Bytes), e.Place ? 'from ' + e.Place : null].filter(Boolean).join(' · ')));
      row.appendChild(text);
      row.appendChild(el('div', 'ehTrashGone', 'Gone ' + until(e.ExpiresUtc)));
      var client = window.ApiClient;
      if (t.CanRestore) {
        var restore = el('button', null, 'Restore');
        restore.type = 'button';
        restore.addEventListener('click', function () {
          restore.disabled = true;
          client.ajax({ type: 'POST', url: client.getUrl('EpisodeHistory/Trash/' + e.Id + '/Restore'), dataType: 'json' }).then(function () {
            refresh(section, 'Put back: ' + e.Title + '. It shows in its library again in a minute or so.');
          }, function () {
            refresh(section, 'Couldn\'t put ' + e.Title + ' back: something may already be where it was.');
          });
        });
        row.appendChild(restore);
      }
      if (t.CanEmpty) {
        var del = el('button', null, 'Delete now');
        del.type = 'button';
        twoStep(del, 'Press again: delete for good', function () {
          client.ajax({ type: 'DELETE', url: client.getUrl('EpisodeHistory/Trash/' + e.Id), dataType: 'json' }).then(function () {
            refresh(section, 'Deleted for good: ' + e.Title + '.');
          }, function () {
            refresh(section, 'Couldn\'t delete it.');
          });
        });
        row.appendChild(del);
      }
      body.appendChild(row);
    });
    if (items.length && t.CanEmpty) {
      var empty = el('button', null, 'Empty the trash');
      empty.type = 'button';
      empty.style.marginTop = '.4em';
      twoStep(empty, 'Press again: delete all ' + items.length + ' for good (' + size(t.Bytes) + ')', function () {
        var client = window.ApiClient;
        client.ajax({ type: 'DELETE', url: client.getUrl('EpisodeHistory/Trash'), dataType: 'json' }).then(function (r) {
          refresh(section, 'Deleted ' + r.Deleted + ' for good.');
        });
      });
      body.appendChild(empty);
    }
    section.setAttribute('data-eh-at', String(infoAt));
  }

  function refresh(section, note) {
    load(true).then(function (t) {
      if (t && section.isConnected) {
        render(section, t, note);
      }
    });
  }

  function checkTab() {
    var page = recordingsTab();
    if (!page) {
      return;
    }
    var section = page.querySelector('.' + SECTION_CLASS);
    load(false).then(function (t) {
      if (!t || !(t.Enabled || (t.Items || []).length) || !page.isConnected) {
        return;
      }
      section = page.querySelector('.' + SECTION_CLASS);
      if (!section) {
        addStyles();
        section = el('div', 'verticalSection ' + SECTION_CLASS);
        page.appendChild(section);
        render(section, t);
      } else if (section.getAttribute('data-eh-at') !== String(infoAt)) {
        render(section, t);
      } else if (section !== page.lastElementChild) {
        // React added its sections after ours: back to the bottom.
        page.appendChild(section);
      }
    });
  }

  // ---- jellyfin-web's delete confirmations ------------------------------------------------------

  var CONFIRMS = [
    { re: /^Deleting (?:this item|".*") will delete it from both the file system and your media library\./, one: true },
    { re: /^Deleting these items will delete them from both the file system and your media library\./, one: false },
    { re: /^Deleting this (?:series|show) will delete ALL (\d+) episodes from both the file system and your media library\./, one: false }
  ];

  function trashWording(text) {
    var n = window.EpisodeHistoryTrash.days;
    if (n == null) {
      return null;
    }
    for (var i = 0; i < CONFIRMS.length; i++) {
      var m = CONFIRMS[i].re.exec(text);
      if (m) {
        var what = m[1] ? 'all ' + m[1] + ' episodes' : CONFIRMS[i].one ? 'it' : 'them';
        return 'This moves ' + what + ' to the trash: ' + (CONFIRMS[i].one && !m[1] ? 'it leaves' : 'they leave')
          + ' the library now and ' + (CONFIRMS[i].one && !m[1] ? 'is' : 'are') + ' deleted for good in ' + days(n)
          + '. Until then, Restore in Live TV › Recordings › Trash puts ' + (CONFIRMS[i].one && !m[1] ? 'it' : 'them') + ' back.';
      }
    }
    return null;
  }

  function rewordConfirm(node) {
    if (!node.querySelectorAll) {
      return;
    }
    var walker = document.createTreeWalker(node, NodeFilter.SHOW_TEXT);
    var t;
    while ((t = walker.nextNode())) {
      var text = (t.nodeValue || '').trim();
      if (text.indexOf('file system and your media library') !== -1) {
        var reworded = trashWording(text);
        if (reworded) {
          t.nodeValue = reworded;
          log('reworded a delete confirmation');
        }
      }
    }
  }

  // ---- Watching the page ----------------------------------------------------------------------

  var scheduled = false;
  function scheduleCheck() {
    if (!scheduled) {
      scheduled = true;
      setTimeout(function () {
        scheduled = false;
        if (signedIn()) {
          checkTab();
        }
      }, 150);
    }
  }

  new MutationObserver(function (mutations) {
    mutations.forEach(function (m) {
      m.addedNodes.forEach(function (node) {
        if (node.nodeType === 1 && (node.classList.contains('dialogContainer') || (node.querySelector && node.querySelector('.dialog')) || node.classList.contains('dialog'))) {
          rewordConfirm(node);
        }
      });
    });
    scheduleCheck();
  }).observe(document.body, { childList: true, subtree: true });
  window.addEventListener('hashchange', scheduleCheck);
  // Know the trash's days before the first delete is asked about.
  setTimeout(function () { load(false); }, 2000);
  setInterval(function () {
    if (signedIn() && (recordingsTab() || Date.now() - infoAt > 5 * 60000)) {
      load(true).then(scheduleCheck);
    }
  }, 60000);
  scheduleCheck();

  log('trash script loaded');
})();
