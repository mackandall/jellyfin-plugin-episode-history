/*
 * Episode History — "Mark as watched" button in jellyfin-web's Live TV guide.
 *
 * Injected into jellyfin-web's index.html (see install-web.sh). jellyfin-web offers no
 * "Mark played" for guide programs (its canMarkPlayed() returns false for Type
 * "Program"), so this adds its own button to the program dialog, backed by the plugin's
 * POST/DELETE /EpisodeHistory/Program/{id}/Watched. Every airing of that episode then
 * carries the watched mark in the guide, in every client.
 *
 * VERSION-SENSITIVE FILE — built against this server's jellyfin-web 12.0.0. Confirmed
 * live via DOM inspection on 2026-09-26:
 *   - Guide cells are `button.programCell[data-action="programdialog"][data-id=<program id>]`;
 *     clicking one opens a `.dialog.recordingDialog.opened`.
 *   - That dialog holds `.recordingFields .recordingFields`, whose children are one
 *     `div.recordingFields-buttons.flex.align-items-center > div > button.raised
 *     .recordingButton.emby-button` row per button ("Record series", "Record"), each
 *     button holding `span.material-icons.recordingIcon.<icon>` + `span.buttonText`.
 *   - Nothing in the dialog's DOM says which program it shows, so the id is taken from
 *     the program cell that was just clicked (capture-phase click listener).
 *   - The dialog is inserted before it gets its classes and its buttons render async, so
 *     it's found by re-checking on DOM changes rather than by inspecting added nodes.
 * If this breaks after a jellyfin-web update, re-check these against the running client.
 */
(function () {
  'use strict';

  var ROW_CLASS = 'episodeHistoryWatchedRow';
  var lastProgramId = null;

  function log(msg) {
    if (window.console && window.console.debug) {
      window.console.debug('[EpisodeHistory] ' + msg);
    }
  }

  // Remember which program the dialog about to open belongs to. Keyboard/remote "enter"
  // on a cell also arrives as a click event.
  document.addEventListener('click', function (e) {
    var cell = e.target && e.target.closest && e.target.closest('[data-action="programdialog"][data-id]');
    if (cell) {
      lastProgramId = cell.getAttribute('data-id');
    }
  }, true);

  function api(method, path) {
    var apiClient = window.ApiClient;
    return apiClient.ajax({
      type: method,
      url: apiClient.getUrl(path),
      dataType: method === 'GET' ? 'json' : undefined
    });
  }

  function setButton(button, marked) {
    button.setAttribute('data-marked', marked ? 'true' : 'false');
    button.querySelector('.recordingIcon').className =
      'material-icons recordingIcon ' + (marked ? 'remove_done' : 'done');
    button.querySelector('.buttonText').textContent = marked ? 'Mark as unwatched' : 'Mark as watched';
  }

  function buildRow(programId, marked) {
    var row = document.createElement('div');
    row.className = 'recordingFields-buttons flex align-items-center ' + ROW_CLASS;

    var wrap = document.createElement('div');
    var button = document.createElement('button');
    button.setAttribute('is', 'emby-button');
    button.type = 'button';
    button.className = 'raised recordingButton emby-button';

    var icon = document.createElement('span');
    icon.className = 'material-icons recordingIcon';
    icon.setAttribute('aria-hidden', 'true');
    var text = document.createElement('span');
    text.className = 'buttonText';

    button.appendChild(icon);
    button.appendChild(text);
    wrap.appendChild(button);
    row.appendChild(wrap);
    setButton(button, marked);

    button.addEventListener('click', function () {
      var mark = button.getAttribute('data-marked') !== 'true';
      button.disabled = true;
      api(mark ? 'POST' : 'DELETE', 'EpisodeHistory/Program/' + programId + '/Watched')
        .then(function () {
          setButton(button, mark);
          // The plugin re-marks the guide ~1 s later and takes a few seconds.
          setTimeout(function () { refreshCells(programId); }, 6000);
        })
        .catch(function () {
          log('marking program ' + programId + ' failed');
        })
        .then(function () {
          button.disabled = false;
        });
    });

    return row;
  }

  /*
   * The guide grid doesn't reload by itself, so refresh the visible cells of the same
   * show (the marked airing plus any repeats on screen) with their new episode titles.
   * Other shows are unaffected by a mark, so they don't need fetching.
   */
  function refreshCells(programId) {
    var clicked = document.querySelector('.programCell[data-id="' + programId + '"]');
    var nameEl = clicked && clicked.querySelector('.guideProgramNameText');
    var showName = nameEl && nameEl.firstChild ? nameEl.firstChild.textContent.trim() : null;
    var cells = [].slice.call(document.querySelectorAll('.programCell[data-id]')).filter(function (c) {
      var n = c.querySelector('.guideProgramNameText');
      return c === clicked || (showName && n && n.firstChild && n.firstChild.textContent.trim() === showName);
    });
    if (!cells.length) {
      return;
    }

    var ids = cells.map(function (c) { return c.getAttribute('data-id'); });
    var apiClient = window.ApiClient;
    apiClient.getItems(apiClient.getCurrentUserId(), { Ids: ids.join(','), EnableImages: false })
      .then(function (result) {
        (result.Items || []).forEach(function (item) {
          var cell = document.querySelector('.programCell[data-id="' + item.Id + '"]');
          var secondary = cell && cell.querySelector('.programSecondaryTitle');
          if (secondary && item.EpisodeTitle) {
            secondary.textContent = item.EpisodeTitle;
          }
        });
      })
      .catch(function () { log('refreshing guide cells failed'); });
  }

  function tryInject(fields) {
    var programId = lastProgramId;
    if (!programId || fields.getAttribute('data-eh-program') === programId) {
      return;
    }

    // Claimed before the request so repeated DOM changes don't inject twice.
    fields.setAttribute('data-eh-program', programId);

    // Also tells us whether the plugin is there; without it the button isn't offered.
    api('GET', 'EpisodeHistory/Program/' + programId)
      .then(function (info) {
        if (!fields.isConnected || fields.querySelector('.' + ROW_CLASS)) {
          return;
        }
        fields.appendChild(buildRow(programId, !!(info && info.MarkedWatched)));
      })
      .catch(function () {
        log('Episode History plugin not available for program ' + programId);
      });
  }

  /*
   * jellyfin-web inserts the dialog before giving it its classes and renders the Record
   * rows asynchronously, so inspecting added nodes misses it. Instead, after any DOM
   * change (at most once per animation frame) look for an open program dialog's button
   * rows that haven't been handled yet.
   */
  var scheduled = false;
  function check() {
    scheduled = false;
    var fields = document.querySelector('.recordingDialog.opened .recordingFields .recordingFields');
    if (fields) {
      tryInject(fields);
    }
  }

  var observer = new MutationObserver(function () {
    if (!scheduled) {
      scheduled = true;
      window.requestAnimationFrame(check);
    }
  });

  observer.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['class'] });

  log('guide menu script loaded');
})();
