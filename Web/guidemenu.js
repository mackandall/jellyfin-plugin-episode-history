/*
 * Episode History — "Mark as watched" button in jellyfin-web's Live TV guide.
 *
 * Injected into jellyfin-web's index.html (see install-web.sh). jellyfin-web offers no
 * "Mark played" for guide programs (its canMarkPlayed() returns false for Type
 * "Program"), so this adds its own button to the program dialog, backed by the plugin's
 * POST/DELETE /EpisodeHistory/Program/{id}/Watched (plus, for admins, "Forget this
 * episode" via DELETE /EpisodeHistory/Program/{id}/History). Every airing of that episode then
 * carries the watched mark in the guide, in every client. While a guide is on screen it
 * also polls /EpisodeHistory/GuideChanges every 5 s, so marks made anywhere else appear
 * without reloading the page.
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
  var FORGET_ROW_CLASS = 'episodeHistoryForgetRow';
  var FORGET_SHOW_ROW_CLASS = 'episodeHistoryForgetShowRow';
  var PLAY_ROW_CLASS = 'episodeHistoryPlayRow';
  var INSTEAD_ROW_CLASS = 'episodeHistoryRecordInsteadRow';
  var FAV_ROW_CLASS = 'episodeHistoryFavouriteRow';
  var PLAY_KEY = 'episodeHistoryPlayItem';
  var lastProgramId = null;

  function log(msg) {
    if (window.console && window.console.debug) {
      window.console.debug('[EpisodeHistory] ' + msg);
    }
  }

  // The home screen's recordings row lives in its own file; load it from here so
  // index.html only ever needs this one script tag.
  (function loadRecordingsRow() {
    var script = document.createElement('script');
    script.defer = true;
    script.src = '/EpisodeHistory/web/recordings.js';
    document.head.appendChild(script);

    // JellyDVR Schedule's "all tuners are busy" message (a 404 without that plugin is harmless).
    var tuners = document.createElement('script');
    tuners.defer = true;
    tuners.src = '/JellyDvrSchedule/web/tuners.js';
    document.head.appendChild(tuners);
  })();

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
      dataType: 'json'
    });
  }

  function setButton(button, marked) {
    button.setAttribute('data-marked', marked ? 'true' : 'false');
    button.querySelector('.recordingIcon').className =
      'material-icons recordingIcon ' + (marked ? 'remove_done' : 'done');
    button.querySelector('.buttonText').textContent = marked ? 'Mark as unwatched' : 'Mark as watched';
  }

  function buildRow(programId, marked) {
    var row = buttonRow(ROW_CLASS);
    var button = row.querySelector('button');
    setButton(button, marked);

    button.addEventListener('click', function () {
      var mark = button.getAttribute('data-marked') !== 'true';
      button.disabled = true;
      api(mark ? 'POST' : 'DELETE', 'EpisodeHistory/Program/' + programId + '/Watched')
        .then(function (r) {
          setButton(button, mark);
          // The plugin has already re-titled this airing and replies with the new title, so
          // show it now; the episode's other airings arrive with the next change poll.
          showTitle(programId, r);
          showForgetRow(row.parentNode, programId, !!(r && r.CanForget));
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

  function showTitle(programId, r) {
    var cell = document.querySelector('.programCell[data-id="' + programId + '"]');
    if (cell && r) {
      setSecondaryTitle(cell, r.EpisodeTitle);
    }
  }

  /*
   * "Forget this episode" (admins only, offered only when there's something to forget):
   * deletes the episode's history — watched marks, and records of recordings since deleted
   * — so the guide stops marking it and a series recording will record it again. Asks for
   * a second tap instead of a confirm() dialog, which TV remotes handle poorly.
   */
  function buildForgetRow(programId) {
    var row = buttonRow(FORGET_ROW_CLASS);
    var button = row.querySelector('button');
    var text = button.querySelector('.buttonText');
    button.querySelector('.recordingIcon').className = 'material-icons recordingIcon history';
    text.textContent = 'Forget this episode';
    var armed = false;
    var disarmTimer = null;

    button.addEventListener('click', function () {
      if (!armed) {
        armed = true;
        text.textContent = 'Press again to forget';
        disarmTimer = setTimeout(function () {
          armed = false;
          text.textContent = 'Forget this episode';
        }, 4000);
        return;
      }

      clearTimeout(disarmTimer);
      button.disabled = true;
      api('DELETE', 'EpisodeHistory/Program/' + programId + '/History')
        .then(function (r) {
          showTitle(programId, r);
          // Hand-made marks are gone too.
          var watchedButton = document.querySelector('.' + ROW_CLASS + ' button');
          if (watchedButton) {
            setButton(watchedButton, false);
          }
          text.textContent = r && r.KeptInLibrary
            ? 'Forgotten (still in library)'
            : 'Forgotten';
        })
        .catch(function () {
          log('forgetting program ' + programId + ' failed');
          button.disabled = false;
          armed = false;
          text.textContent = 'Forget this episode';
        });
    });

    return row;
  }

  /*
   * "Forget this show" (admins, when the history holds something of the show): the same for
   * every episode of the show, so a series recording records them all again (with "skip
   * watched episodes" on, watched ones included). Two taps, as above.
   */
  function addForgetShowRow(fields, programId) {
    api('DELETE', 'EpisodeHistory/Item/' + programId + '/ShowHistory?dryRun=true').then(function (r) {
      if (!r || !r.Episodes || !fields.isConnected || fields.querySelector('.' + FORGET_SHOW_ROW_CLASS)) {
        return;
      }
      var label = 'Forget this show (' + r.Episodes + ' episode' + (r.Episodes === 1 ? '' : 's') + ')';
      var row = buttonRow(FORGET_SHOW_ROW_CLASS);
      var button = row.querySelector('button');
      var text = button.querySelector('.buttonText');
      button.querySelector('.recordingIcon').className = 'material-icons recordingIcon history';
      text.textContent = label;
      var armed = false;
      var disarmTimer = null;
      button.addEventListener('click', function () {
        if (!armed) {
          armed = true;
          text.textContent = 'Press again: forget all of ' + r.Show;
          disarmTimer = setTimeout(function () {
            armed = false;
            text.textContent = label;
          }, 4000);
          return;
        }
        clearTimeout(disarmTimer);
        button.disabled = true;
        api('DELETE', 'EpisodeHistory/Item/' + programId + '/ShowHistory').then(function (done) {
          var watchedButton = document.querySelector('.' + ROW_CLASS + ' button');
          if (watchedButton) {
            setButton(watchedButton, false);
          }
          text.textContent = 'Forgotten' + (done && done.KeptInLibrary ? ' (copies still in library count)' : '');
        }).catch(function () {
          log('forgetting the show of program ' + programId + ' failed');
          button.disabled = false;
          armed = false;
          text.textContent = label;
        });
      });
      fields.appendChild(row);
    }).catch(function () {});
  }

  function showForgetRow(fields, programId, show) {
    if (!fields) {
      return;
    }

    var existing = fields.querySelector('.' + FORGET_ROW_CLASS);
    var used = existing && existing.querySelector('button').disabled;
    if (show) {
      // A used row (showing "Forgotten") is replaced by a fresh one.
      if (used) {
        existing.parentNode.removeChild(existing);
      }
      if (!existing || used) {
        fields.appendChild(buildForgetRow(programId));
      }
    } else if (existing && !used) {
      // A used row stays, showing its "Forgotten" result.
      existing.parentNode.removeChild(existing);
    }
  }

  /*
   * The web client exposes no player API to scripts, so "Play from library" opens the item's
   * page and presses its Resume/Play button there (see playIfAsked).
   */
  function buildPlayRow(itemId, resume) {
    var row = buttonRow(PLAY_ROW_CLASS);
    var button = row.querySelector('button');
    button.querySelector('.recordingIcon').className = 'material-icons recordingIcon play_circle';
    button.querySelector('.buttonText').textContent = resume ? 'Resume from library' : 'Play from library';
    button.addEventListener('click', function () {
      closeProgramDialog();
      openLibraryItem(itemId);
    });
    return row;
  }

  /*
   * Opens the library item's page and asks playIfAsked to press its Play. The page we leave
   * is tagged so its buttons are never taken for the new page's: jellyfin-web opens the
   * item in a new page element but keeps the old one in the document (before the new one)
   * and visible while the transition runs. Seen live: "Play from library" on an airing
   * program's page pressed that page's Play and tuned BBC America.
   */
  var PLAY_WAIT_MS = 15000;
  var playAskedAt = 0;
  function openLibraryItem(itemId) {
    [].slice.call(document.querySelectorAll('[data-eh-origin]')).forEach(function (p) {
      p.removeAttribute('data-eh-origin');
    });
    var origin = document.querySelector('.page:not(.hide)');
    if (origin) {
      origin.setAttribute('data-eh-origin', '');
    }
    try {
      window.sessionStorage.setItem(PLAY_KEY, itemId);
      playAskedAt = Date.now();
    } catch (e) {
      // Private mode: the page opens without auto-play.
    }
    window.location.hash = '#/details?id=' + itemId;
  }

  function closeProgramDialog() {
    var cancel = document.querySelector('.recordingDialog.opened .btnCancel');
    if (cancel) {
      cancel.click();
    }
  }

  function playIfAsked() {
    var wanted = null;
    try {
      wanted = window.sessionStorage.getItem(PLAY_KEY);
    } catch (e) {
      return;
    }
    if (!wanted) {
      return;
    }
    // A request that never found its button (or one left from before a reload) must not
    // start playback later out of the blue.
    if (Date.now() - playAskedAt > PLAY_WAIT_MS) {
      window.sessionStorage.removeItem(PLAY_KEY);
      return;
    }
    var hash = window.location.hash || '';
    if (hash.indexOf('#/details') !== 0 ||
      new URLSearchParams(hash.slice(hash.indexOf('?') + 1)).get('id') !== wanted) {
      return;
    }
    // Only the item page's own button: the guide's program dialog has a "Play" (watch the
    // channel live) with the same btnPlay class, and it can still be on screen while the
    // page opens. Seen live: pressing it tuned MovieSphere before the movie started.
    if (document.querySelector('.dialogContainer .dialog.opened, .recordingDialog.opened')) {
      return;
    }
    // Wait for the page transition to finish (one page left on screen), and never use the
    // page we came from, nor a page still showing a Record button (a program's page, where
    // Play means watch live; a library item has no Record button).
    var pages = document.querySelectorAll('.page:not(.hide)');
    if (pages.length !== 1 || pages[0].hasAttribute('data-eh-origin')) {
      return;
    }
    var page = pages[0];
    var recording = [].slice.call(page.querySelectorAll('.recordingFields-buttons')).some(function (r) {
      return r.offsetParent !== null;
    });
    if (recording) {
      return;
    }
    var button = [].slice.call(page.querySelectorAll('.btnPlay')).filter(function (b) {
      return b.offsetParent !== null && !b.classList.contains('hide') && !b.closest('.dialog, .formDialogFooter');
    })[0];
    if (button) {
      window.sessionStorage.removeItem(PLAY_KEY);
      button.click();
    }
  }

  // A button row matching jellyfin-web's own "Record" rows in the program dialog.
  function buttonRow(className) {
    var row = document.createElement('div');
    row.className = 'recordingFields-buttons flex align-items-center ' + className;

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
    return row;
  }

  /*
   * The guide grid doesn't reload by itself, so marks made elsewhere (Wholphin, another
   * browser, a finished recording) wouldn't show until a page refresh. While a guide is on
   * screen, ask the plugin every 5 s which guide entries it re-titled since last time and
   * update just those cells.
   */
  var POLL_MS = 5000;
  var changesVersion = null;
  var polling = false;

  function pollChanges() {
    var apiClient = window.ApiClient;
    if (polling || !apiClient || !apiClient.getCurrentUserId || !apiClient.getCurrentUserId()) {
      return;
    }

    // The baseline is taken as soon as someone is signed in, guide or not, so a mark made
    // elsewhere right after the guide opens isn't missed; after that, only poll while a
    // guide is actually on screen.
    if (changesVersion !== null && !document.querySelector('.programCell[data-id]')) {
      return;
    }

    polling = true;
    var path = 'EpisodeHistory/GuideChanges' + (changesVersion === null ? '' : '?since=' + changesVersion);
    api('GET', path)
      .then(function (r) {
        var first = changesVersion === null;
        changesVersion = r.Version;
        if (first || r.Reset || Date.now() - symbolsLoadedAt > 5 * 60000) {
          loadSymbols();
        }
        if (first) {
          return null;
        }

        // Reset (server restarted, or too far behind): refresh every visible cell.
        var ids = r.Reset
          ? [].slice.call(document.querySelectorAll('.programCell[data-id]')).map(function (c) { return c.getAttribute('data-id'); })
          : (r.ProgramIds || []).filter(function (id) { return document.querySelector('.programCell[data-id="' + id + '"]'); });
        return ids.length ? updateCells(ids) : null;
      })
      .catch(function () { log('polling guide changes failed'); })
      .then(function () { polling = false; });
  }

  function updateCells(ids) {
    var apiClient = window.ApiClient;
    var batches = [];
    for (var i = 0; i < ids.length; i += 100) {
      batches.push(ids.slice(i, i + 100));
    }

    return Promise.all(batches.map(function (batch) {
      // Without the type filter /Items returns only some of the requested guide entries
      // (seen on 12.0: just the ones with a recording scheduled).
      return apiClient.getItems(apiClient.getCurrentUserId(), { Ids: batch.join(','), IncludeItemTypes: 'TvProgram', EnableImages: false })
        .then(function (result) {
          (result.Items || []).forEach(function (item) {
            var cell = document.querySelector('.programCell[data-id="' + item.Id + '"]');
            if (cell) {
              setSecondaryTitle(cell, item.EpisodeTitle);
              setTimerIcon(cell, item);
            }
          });
        });
    }));
  }

  /*
   * jellyfin-web only redraws a cell's recording dot for changes made in its own dialog, so a
   * recording the server scheduled or cancelled (JellyDVR Schedule moving or skipping one, a
   * duplicate skipped) kept its old dot until a page reload. Same markup and rule as the guide.
   */
  function setTimerIcon(cell, item) {
    var name = cell.querySelector('.guideProgramName');
    if (!name) {
      return;
    }

    [].slice.call(name.querySelectorAll('.timerIcon, .seriesTimerIcon')).forEach(function (el) {
      el.parentNode.removeChild(el);
    });
    var live = item.TimerStatus !== 'Cancelled';
    var series = !!item.SeriesTimerId && live;
    var single = !series && !!item.TimerId && live;
    if (item.TimerId && live) {
      cell.setAttribute('data-timerid', item.TimerId);
    } else {
      cell.removeAttribute('data-timerid');
    }
    if (series || single) {
      var icon = document.createElement('span');
      icon.className = series ? 'seriesTimerIcon material-icons programIcon fiber_smart_record' : 'timerIcon material-icons programIcon fiber_manual_record';
      icon.setAttribute('aria-hidden', 'true');
      name.appendChild(icon);
    }
  }

  /*
   * CSS hooks for styling marks under Dashboard -> General -> Custom CSS. A marked guide
   * cell gets `eh-marked` plus `eh-watched` / `eh-inlibrary` / `eh-recordedbefore` (and, from
   * JellyDVR Schedule, `eh-skipped` / `eh-otherairing`), and the
   * symbols in its episode-title line are wrapped in `span.eh-mark`, e.g.
   *   .programCell.eh-watched .guideProgramNameText { font-style: italic; }
   *   .eh-mark { color: #4caf50; }
   * The symbols are read from the plugin, so changing them in its settings keeps working.
   */
  var MARK_CLASSES = ['eh-marked', 'eh-watched', 'eh-inlibrary', 'eh-recordedbefore', 'eh-skipped', 'eh-otherairing'];
  var symbols = null;
  var markRegex = null;
  var symbolsLoadedAt = 0;

  function escapeRegex(value) {
    return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  }

  function loadSymbols() {
    symbolsLoadedAt = Date.now();
    return api('GET', 'EpisodeHistory/Symbols').then(function (r) {
      var list = [r.Watched, r.InLibrary, r.RecordedBefore, r.Skipped, r.OtherAiring].filter(function (x) { return !!x; });
      // Longest first, so a multi-character symbol wins over one it contains.
      list.sort(function (a, b) { return b.length - a.length; });
      symbols = r;
      markRegex = list.length ? new RegExp('^((?:' + list.map(escapeRegex).join('|') + ')+)\\s*([\\s\\S]*)$', 'u') : null;
      // Re-check every cell against the (possibly new) symbols.
      [].slice.call(document.querySelectorAll('.programSecondaryTitle[data-eh]')).forEach(function (el) {
        el.removeAttribute('data-eh');
      });
      scheduleCheck();
    }).catch(function () { log('loading mark symbols failed'); });
  }

  function decorate(secondary) {
    if (!markRegex) {
      return;
    }

    var text = secondary.textContent;
    if (secondary.getAttribute('data-eh') === text) {
      return; // already handled for this exact text
    }

    var flags = { watched: false, inlibrary: false, recordedbefore: false, skipped: false, otherairing: false };
    var match = markRegex.exec(text);
    if (match) {
      var marks = match[1];
      flags.watched = marks.indexOf(symbols.Watched) !== -1;
      flags.inlibrary = marks.indexOf(symbols.InLibrary) !== -1;
      flags.recordedbefore = marks.indexOf(symbols.RecordedBefore) !== -1;
      flags.skipped = !!symbols.Skipped && marks.indexOf(symbols.Skipped) !== -1;
      flags.otherairing = !!symbols.OtherAiring && marks.indexOf(symbols.OtherAiring) !== -1;

      var span = document.createElement('span');
      span.className = 'eh-mark';
      span.textContent = marks;
      secondary.textContent = '';
      secondary.appendChild(span);
      secondary.appendChild(document.createTextNode(' ' + match[2]));
    }

    secondary.setAttribute('data-eh', secondary.textContent);
    setCellClasses(secondary.closest('.programCell'), flags);
  }

  function setCellClasses(cell, flags) {
    if (!cell) {
      return;
    }

    var any = flags.watched || flags.inlibrary || flags.recordedbefore || flags.skipped || flags.otherairing;
    cell.classList.toggle('eh-marked', any);
    cell.classList.toggle('eh-watched', flags.watched);
    cell.classList.toggle('eh-inlibrary', flags.inlibrary);
    cell.classList.toggle('eh-recordedbefore', flags.recordedbefore);
    cell.classList.toggle('eh-skipped', flags.skipped);
    cell.classList.toggle('eh-otherairing', flags.otherairing);
  }

  // Same markup jellyfin-web's guide uses for the episode-title line; a program that had
  // no episode title (e.g. a movie) doesn't have the element yet.
  function setSecondaryTitle(cell, title) {
    var secondary = cell.querySelector('.programSecondaryTitle');
    if (!title) {
      var info = cell.querySelector('.guideProgramSecondaryInfo');
      if (info) {
        info.parentNode.removeChild(info);
      }
      setCellClasses(cell, { watched: false, inlibrary: false, recordedbefore: false });
      return;
    }

    if (!secondary) {
      var nameText = cell.querySelector('.guideProgramNameText');
      if (!nameText) {
        return;
      }
      var infoDiv = document.createElement('div');
      infoDiv.className = 'guideProgramSecondaryInfo';
      secondary = document.createElement('span');
      secondary.className = 'programSecondaryTitle';
      infoDiv.appendChild(secondary);
      nameText.appendChild(infoDiv);
    }

    secondary.textContent = title;
    decorate(secondary);
  }

  setInterval(function () {
    if (!document.hidden) {
      pollChanges();
    }
  }, POLL_MS);
  setTimeout(pollChanges, 3000);

  var AIRINGS_ROW_CLASS = 'episodeHistoryAiringsRow';

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
        // "Play from library": the guide entry is already in the library, so offer to play
        // that copy instead of watching the airing live.
        if (info && info.LibraryItemId && !fields.querySelector('.' + PLAY_ROW_CLASS)) {
          fields.appendChild(buildPlayRow(info.LibraryItemId, info.LibraryItemPositionTicks > 0));
        }
        fields.appendChild(buildRow(programId, !!(info && info.MarkedWatched)));
        showForgetRow(fields, programId, !!(info && info.CanForget));
        // Administrators only: for anyone else the dry run is refused and nothing is added.
        addForgetShowRow(fields, programId);
        // "Other airings": every later airing of it, with clashes and commercial-free channels
        // (the dialog lives in recordings.js).
        if (window.EpisodeHistoryAirings && !fields.querySelector('.' + AIRINGS_ROW_CLASS)) {
          var airings = buttonRow(AIRINGS_ROW_CLASS);
          var ab = airings.querySelector('button');
          ab.querySelector('.recordingIcon').className = 'material-icons recordingIcon event_repeat';
          ab.querySelector('.buttonText').textContent = 'Other airings';
          ab.addEventListener('click', function () { window.EpisodeHistoryAirings({ programId: programId }); });
          fields.appendChild(airings);
        }
      })
      .catch(function () {
        log('Episode History plugin not available for program ' + programId);
      });

    // JellyDVR Schedule: an airing it took off the schedule (tuner conflict / another airing)
    // gets "Record this instead". A 404 just means that plugin isn't installed.
    api('GET', 'JellyDvrSchedule/Program/' + programId)
      .then(function (s) {
        if (s && s.Outcome && fields.isConnected && !fields.querySelector('.' + INSTEAD_ROW_CLASS)) {
          fields.appendChild(buildInsteadRow(programId, s.Outcome));
        }
        if (s && fields.isConnected && !fields.querySelector('.' + FAV_ROW_CLASS)) {
          fields.appendChild(buildFavouriteRow(programId, s.Favourite || null));
        }
      })
      .catch(function () { /* JellyDVR Schedule not installed, or no Live TV management rights */ });
  }

  /*
   * JellyDVR Schedule favourites: "Add to favourites" creates a title rule for this show (records
   * every episode, any channel, whatever id the guide uses); for a show a favourite already covers,
   * "Remove from favourites" deletes that rule and cancels the upcoming recordings it scheduled
   * (second press to confirm).
   */
  function buildFavouriteRow(programId, favourite) {
    var row = buttonRow(FAV_ROW_CLASS);
    var button = row.querySelector('button');
    var icon = button.querySelector('.recordingIcon');
    var text = button.querySelector('.buttonText');
    var note = document.createElement('div');
    note.className = 'fieldDescription';
    note.style.cssText = 'margin:.3em 0 0 .2em;opacity:.8;';
    row.firstChild.appendChild(note);
    var armed = false;

    function show(f) {
      favourite = f;
      armed = false;
      icon.className = 'material-icons recordingIcon ' + (f ? 'star' : 'star_border');
      icon.style.color = f ? '#f5c518' : '';
      text.textContent = f ? 'Remove from Recording Favourites' : 'Add to Recording Favourites';
      if (f) {
        note.textContent = '★ ' + f.Title + (f.Match === 'keyword' ? ' (keyword)' : '');
      }
    }
    show(favourite);

    button.addEventListener('click', function () {
      if (favourite) {
        if (!armed) {
          armed = true;
          text.textContent = 'Press again to remove';
          setTimeout(function () { if (armed) { show(favourite); } }, 4000);
          return;
        }
        button.disabled = true;
        api('DELETE', 'JellyDvrSchedule/Favourites/' + favourite.Id)
          .then(function (r) { show(null); note.textContent = (r && r.Message) || ''; })
          .catch(function () { note.textContent = 'Could not remove it.'; })
          .then(function () { button.disabled = false; });
        return;
      }
      button.disabled = true;
      api('POST', 'JellyDvrSchedule/Favourites/FromProgram/' + programId)
        .then(function (r) {
          show(r && r.Id ? { Id: r.Id, Title: r.Title, Match: 'title' } : null);
          note.textContent = (r && r.Message) || '';
        })
        .catch(function () { note.textContent = 'Could not add it.'; })
        .then(function () { button.disabled = false; });
    });
    return row;
  }

  /*
   * "Record this instead": records this airing whatever else has to give way (JellyDVR Schedule
   * moves or skips the lower-priority clash) and reports what changed under the button.
   */
  function buildInsteadRow(programId, outcome) {
    var row = buttonRow(INSTEAD_ROW_CLASS);
    var button = row.querySelector('button');
    button.querySelector('.recordingIcon').className = 'material-icons recordingIcon fiber_manual_record';
    button.querySelector('.recordingIcon').style.color = '#f59e0b';
    button.querySelector('.buttonText').textContent = 'Record this instead';

    var note = document.createElement('div');
    note.className = 'fieldDescription';
    note.style.cssText = 'margin:.3em 0 0 .2em;opacity:.8;';
    note.textContent = outcome.Detail || '';
    row.firstChild.appendChild(note);

    button.addEventListener('click', function () {
      button.disabled = true;
      api('POST', 'JellyDvrSchedule/RecordInstead/' + programId)
        .then(function (r) {
          button.querySelector('.buttonText').textContent = r && r.Recording ? 'Recording this one' : 'Could not record it';
          note.textContent = (r && r.Message) || '';
        })
        .catch(function () {
          button.disabled = false;
          note.textContent = 'Could not schedule it.';
        });
    });
    return row;
  }

  /*
   * A program opened from a channel's page (its "upcoming" list) shows as a details page
   * rather than the guide dialog, so offer "Play from library" among that page's buttons too.
   * Looked up once per program id; items that aren't programs just 404.
   */
  var detailInfo = {};
  function currentDetailsId() {
    var hash = window.location.hash || '';
    if (hash.indexOf('#/details') !== 0) {
      return null;
    }
    return new URLSearchParams(hash.slice(hash.indexOf('?') + 1)).get('id');
  }

  function checkDetailsPage() {
    var id = currentDetailsId();
    var page = document.querySelector('.page:not(.hide)');
    if (!page) {
      return;
    }
    // jellyfin-web reuses the page for the next item; drop buttons left from another one.
    [].slice.call(page.querySelectorAll('.' + PLAY_ROW_CLASS)).forEach(function (el) {
      if (el.getAttribute('data-eh-program') !== id) {
        el.remove();
      }
    });
    if (!id) {
      return;
    }
    if (!(id in detailInfo)) {
      detailInfo[id] = 'pending';
      api('GET', 'EpisodeHistory/Program/' + id)
        .then(function (info) { detailInfo[id] = info || null; scheduleCheck(); })
        .catch(function () { detailInfo[id] = null; });
      return;
    }
    var info = detailInfo[id];
    if (!info || info === 'pending' || !info.LibraryItemId) {
      return;
    }
    var resume = info.LibraryItemPositionTicks > 0;

    // A labelled button below Record, as in the guide's program dialog, whether the program
    // is airing now or later. (An icon in the top row, next to the live Play, was too easy
    // to miss.) The first such row is the hidden "record series" one; take the visible one.
    var record = [].slice.call(page.querySelectorAll('.recordingFields-buttons:not(.' + PLAY_ROW_CLASS + ')'))
      .filter(function (r) { return r.offsetParent !== null; })[0];
    if (record && !page.querySelector('.' + PLAY_ROW_CLASS)) {
      var row = buildPlayRow(info.LibraryItemId, resume);
      row.setAttribute('data-eh-program', id);
      record.parentNode.insertBefore(row, record.nextSibling);
    }
  }

  /*
   * Program cards outside the guide (home screen "On Now", Live TV's Programs tab) have a
   * "..." menu whose Play means watch live; add "Play from library" under it. The action
   * sheet doesn't say which item it's for, so the card is taken from the "..." (or
   * right-click) that opened it, as in ComskipSegments' itemmenu.js. Clicking any action
   * sheet item closes the sheet as long as the click is allowed to bubble.
   */
  var MENU_ITEM_ID = 'episodehistory-playlibrary';
  var menuProgramId = null;
  var menuClickedAt = 0;

  function rememberMenuProgram(e, isContextMenu) {
    var target = e.target;
    var card = target && target.closest && target.closest('[data-type="Program"][data-id]');
    if (!card || card.classList.contains('actionSheetMenuItem')) {
      return;
    }
    if (isContextMenu || target.closest('[data-action="menu"]')) {
      menuProgramId = card.getAttribute('data-id');
      menuClickedAt = Date.now();
    }
  }

  document.addEventListener('click', function (e) { rememberMenuProgram(e, false); }, true);
  document.addEventListener('contextmenu', function (e) { rememberMenuProgram(e, true); }, true);

  function buildMenuItem(itemId, resume) {
    var button = document.createElement('button');
    button.setAttribute('is', 'emby-button');
    button.type = 'button';
    button.className = 'listItem listItem-button actionSheetMenuItem emby-button';
    button.setAttribute('data-id', MENU_ITEM_ID);
    button.innerHTML = '<span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons play_circle" aria-hidden="true"></span>'
      + '<div class="listItemBody actionsheetListItemBody"><div class="listItemBodyText actionSheetItemText"></div></div>';
    button.querySelector('.actionSheetItemText').textContent = resume ? 'Resume from library' : 'Play from library';
    button.addEventListener('click', function () {
      openLibraryItem(itemId);
    });
    return button;
  }

  function checkActionSheet() {
    var scroller = document.querySelector('.actionSheet.opened .actionSheetScroller');
    if (!scroller || !menuProgramId || Date.now() - menuClickedAt > 5000
      || scroller.querySelector('[data-id="' + MENU_ITEM_ID + '"]')) {
      return;
    }
    var id = menuProgramId;
    if (!(id in detailInfo)) {
      detailInfo[id] = 'pending';
      api('GET', 'EpisodeHistory/Program/' + id)
        .then(function (info) { detailInfo[id] = info || null; scheduleCheck(); })
        .catch(function () { detailInfo[id] = null; });
      return;
    }
    var info = detailInfo[id];
    if (!info || info === 'pending' || !info.LibraryItemId) {
      return;
    }
    var item = buildMenuItem(info.LibraryItemId, info.LibraryItemPositionTicks > 0);
    var play = scroller.querySelector('[data-id="resume"], [data-id="play"]');
    scroller.insertBefore(item, play ? play.nextSibling : scroller.firstChild);
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
    checkDetailsPage();
    checkActionSheet();
    playIfAsked();

    // jellyfin-web re-renders guide cells when scrolling or changing day; tag new ones.
    if (markRegex) {
      var titles = document.querySelectorAll('.programCell .programSecondaryTitle');
      for (var i = 0; i < titles.length; i++) {
        if (titles[i].getAttribute('data-eh') !== titles[i].textContent) {
          decorate(titles[i]);
        }
      }
    }
  }

  function scheduleCheck() {
    if (!scheduled) {
      scheduled = true;
      window.requestAnimationFrame(check);
    }
  }

  var observer = new MutationObserver(scheduleCheck);

  observer.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['class'] });

  log('guide menu script loaded');
})();
