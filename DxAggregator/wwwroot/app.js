"use strict";

// ============================================================
// DX Cluster Aggregator — Frontend
// Accessible real-time spot display with SignalR push
// ============================================================

(function () {
    // --- DOM references ---
    var spotBody = document.getElementById("spot-body");
    var spotTable = document.getElementById("spot-table");
    var spotStatus = document.getElementById("spot-status");
    var connectionStatus = document.getElementById("connection-status");
    var dxAlert = document.getElementById("dx-alert");
    var srAnnouncer = document.getElementById("sr-announcer");
    var callsignSearch = document.getElementById("callsign-search");

    // --- State ---
    var allSpots = [];
    var maxTableRows = 200;
    var pendingAnnounceCount = 0;
    var announceTimer = null;
    var announceDebounceMs = 3000; // batch announcements over 3 seconds
    var storagePrefix = "dx-agg-";

    // --- Grid freeze state ---
    var focusInGrid = false;
    var lastFocusMoveTime = 0;
    var frozenSpots = [];
    var freezeCheckTimer = null;
    var freezeIdleMs = 60000; // 60 seconds without focus movement = unfreeze

    // --- localStorage persistence ---
    function saveFilters() {
        try {
            localStorage.setItem(storagePrefix + "bands", JSON.stringify(getSelectedBands()));
            localStorage.setItem(storagePrefix + "modes", JSON.stringify(getSelectedModes()));
            localStorage.setItem(storagePrefix + "callsign", callsignSearch.value || "");
            localStorage.setItem(storagePrefix + "announce", getAnnounceLevel());
            localStorage.setItem(storagePrefix + "phonetic", phoneticToggle.checked ? "1" : "0");
        } catch (e) { /* localStorage unavailable */ }
    }

    function restoreFilters() {
        try {
            var bands = localStorage.getItem(storagePrefix + "bands");
            if (bands) {
                bands = JSON.parse(bands);
                var bandInputs = document.querySelectorAll('input[name="band"]');
                for (var i = 0; i < bandInputs.length; i++) {
                    bandInputs[i].checked = bands.indexOf(bandInputs[i].value) !== -1;
                }
            }

            var modes = localStorage.getItem(storagePrefix + "modes");
            if (modes) {
                modes = JSON.parse(modes);
                var modeInputs = document.querySelectorAll('input[name="mode"]');
                for (var i = 0; i < modeInputs.length; i++) {
                    modeInputs[i].checked = modes.indexOf(modeInputs[i].value) !== -1;
                }
            }

            var callsign = localStorage.getItem(storagePrefix + "callsign");
            if (callsign) {
                callsignSearch.value = callsign;
            }

            var announce = localStorage.getItem(storagePrefix + "announce");
            if (announce) {
                var radio = document.querySelector('input[name="announce"][value="' + announce + '"]');
                if (radio) radio.checked = true;
                if (announce === "off") {
                    srAnnouncer.removeAttribute("aria-live");
                } else {
                    srAnnouncer.setAttribute("aria-live", announce);
                }
            }

            var phonetic = localStorage.getItem(storagePrefix + "phonetic");
            if (phonetic === "1") phoneticToggle.checked = true;
            phoneticToggle.disabled = getAnnounceLevel() === "off";
        } catch (e) { /* localStorage unavailable */ }
    }

    // Restore saved filters before loading any data
    restoreFilters();

    // --- Filter state ---
    function getSelectedBands() {
        var checked = document.querySelectorAll('input[name="band"]:checked');
        var bands = [];
        for (var i = 0; i < checked.length; i++) bands.push(checked[i].value);
        return bands;
    }

    function getSelectedModes() {
        var checked = document.querySelectorAll('input[name="mode"]:checked');
        var modes = [];
        for (var i = 0; i < checked.length; i++) modes.push(checked[i].value);
        return modes;
    }

    function getAnnounceLevel() {
        var selected = document.querySelector('input[name="announce"]:checked');
        return selected ? selected.value : "polite";
    }

    function getCallsignFilter() {
        return (callsignSearch.value || "").trim().toUpperCase();
    }

    // --- Filtering ---
    function spotMatchesFilters(spot) {
        var bands = getSelectedBands();
        var modes = getSelectedModes();
        var callFilter = getCallsignFilter();

        if (bands.length > 0 && bands.indexOf(spot.band) === -1) return false;
        if (modes.length > 0 && modes.indexOf(spot.mode) === -1) return false;
        if (callFilter && spot.dxCall.indexOf(callFilter) !== 0) return false;

        return true;
    }

    // --- Table rendering ---
    function formatTime(isoString) {
        try {
            if (!isoString) return "";
            // Extract HH:MM:SS directly from ISO string (all timestamps are UTC)
            var tIndex = isoString.indexOf("T");
            if (tIndex !== -1) return isoString.substr(tIndex + 1, 8);
            return isoString;
        } catch (e) {
            return isoString || "";
        }
    }

    function formatFrequency(freq) {
        return freq ? freq.toFixed(1) : "";
    }

    // --- Speech formatting for screen readers ---
    var phoneticToggle = document.getElementById("phonetic-toggle");
    var natoAlphabet = {
        A: "Alpha", B: "Bravo", C: "Charlie", D: "Delta", E: "Echo",
        F: "Foxtrot", G: "Golf", H: "Hotel", I: "India", J: "Juliet",
        K: "Kilo", L: "Lima", M: "Mike", N: "November", O: "Oscar",
        P: "Papa", Q: "Quebec", R: "Romeo", S: "Sierra", T: "Tango",
        U: "Uniform", V: "Victor", W: "Whiskey", X: "X-ray",
        Y: "Yankee", Z: "Zulu",
        "0": "Zero", "1": "One", "2": "Two", "3": "Three", "4": "Four",
        "5": "Five", "6": "Six", "7": "Seven", "8": "Eight", "9": "Niner",
        "/": "Stroke"
    };

    function spellCall(call) {
        if (phoneticToggle.checked) {
            return call.toUpperCase().split("").map(function (c) {
                return natoAlphabet[c] || c;
            }).join(" ");
        }
        return call.split("").join(" ");
    }

    function speakTime(isoString) {
        var t = formatTime(isoString); // "HH:MM:SS"
        if (!t) return "";
        return "at " + t.replace(/:/g, " ");
    }

    function speakFrequency(freq) {
        if (!freq) return "";
        var s = freq.toFixed(1);
        return s.split("").map(function (c) { return c === "." ? "point" : c; }).join(" ");
    }

    function speakBand(band) {
        if (!band) return "";
        // "20m" -> "20 meters"
        return band.replace("m", " meters");
    }

    function buildRowSummary(spot) {
        var parts = [];
        parts.push(speakTime(spot.timestamp));
        if (spot.dxCall) parts.push(spellCall(spot.dxCall));
        parts.push(speakFrequency(spot.frequency));
        if (spot.band) parts.push(speakBand(spot.band));
        if (spot.mode) parts.push(spot.mode);
        if (spot.spotter) parts.push("spotted by " + spellCall(spot.spotter));
        if (spot.snr != null) parts.push("SNR " + spot.snr);
        if (spot.source) parts.push(spot.source);
        if (spot.comment) parts.push(spot.comment);
        return parts.join(", ");
    }

    function createSpotRow(spot) {
        var tr = document.createElement("tr");
        tr.dataset.band = spot.band || "";
        tr.dataset.mode = spot.mode || "";
        tr.className = "band-" + (spot.band || "unknown").replace("m", "");
        tr.tabIndex = 0;
        tr.setAttribute("aria-label", buildRowSummary(spot));

        var cells = [
            formatTime(spot.timestamp),
            spot.dxCall || "",
            formatFrequency(spot.frequency),
            spot.band || "",
            spot.mode || "",
            spot.spotter || "",
            spot.snr != null ? spot.snr.toString() : "",
            spot.source || "",
            spot.comment || ""
        ];

        for (var i = 0; i < cells.length; i++) {
            var td = document.createElement("td");
            td.textContent = cells[i];
            tr.appendChild(td);
        }

        return tr;
    }

    function renderFullTable() {
        // Clear existing rows
        while (spotBody.firstChild) {
            spotBody.removeChild(spotBody.firstChild);
        }

        var visibleCount = 0;
        for (var i = 0; i < allSpots.length && visibleCount < maxTableRows; i++) {
            if (spotMatchesFilters(allSpots[i])) {
                spotBody.appendChild(createSpotRow(allSpots[i]));
                visibleCount++;
            }
        }

        spotTable.setAttribute("aria-rowcount", visibleCount.toString());
        spotStatus.textContent = "Showing " + visibleCount + " spot" + (visibleCount !== 1 ? "s" : "");
    }

    // --- Grid freeze helpers ---
    function isGridFrozen() {
        return getAnnounceLevel() !== "off" && focusInGrid && (Date.now() - lastFocusMoveTime < freezeIdleMs);
    }

    function flushFrozenSpots() {
        if (frozenSpots.length === 0) return;
        var toFlush = frozenSpots;
        frozenSpots = [];
        // Insert oldest-first so newest ends up at top
        for (var i = toFlush.length - 1; i >= 0; i--) {
            insertSpotRow(toFlush[i]);
        }
    }

    function startFreezeCheck() {
        if (freezeCheckTimer) return;
        freezeCheckTimer = setInterval(function () {
            if (!isGridFrozen()) {
                flushFrozenSpots();
                stopFreezeCheck();
            }
        }, 5000);
    }

    function stopFreezeCheck() {
        if (freezeCheckTimer) {
            clearInterval(freezeCheckTimer);
            freezeCheckTimer = null;
        }
    }

    function insertSpotRow(spot) {
        if (!spotMatchesFilters(spot)) return;

        var row = createSpotRow(spot);

        if (spotBody.firstChild) {
            spotBody.insertBefore(row, spotBody.firstChild);
        } else {
            spotBody.appendChild(row);
        }

        while (spotBody.children.length > maxTableRows) {
            spotBody.removeChild(spotBody.lastChild);
        }

        var count = spotBody.children.length;
        spotTable.setAttribute("aria-rowcount", count.toString());
        spotStatus.textContent = "Showing " + count + " spot" + (count !== 1 ? "s" : "");
    }

    function addSpotToTable(spot) {
        if (isGridFrozen()) {
            frozenSpots.push(spot);
            startFreezeCheck();
            return;
        }
        insertSpotRow(spot);
    }

    // --- Screen reader announcements ---
    function announceSpot(spot) {
        var level = getAnnounceLevel();
        if (level === "off") return;

        pendingAnnounceCount++;

        // Debounce: batch announcements
        if (announceTimer) clearTimeout(announceTimer);
        announceTimer = setTimeout(function () {
            if (pendingAnnounceCount <= 0) return;

            var text;
            if (pendingAnnounceCount === 1) {
                text = spellCall(spot.dxCall) + " on " + speakFrequency(spot.frequency) + " " + (spot.mode || "") + " " + speakBand(spot.band);
            } else {
                text = pendingAnnounceCount + " new spots added";
            }

            srAnnouncer.setAttribute("aria-live", level);
            // Clear then set to force re-announcement
            srAnnouncer.textContent = "";
            setTimeout(function () {
                srAnnouncer.textContent = text;
            }, 100);

            pendingAnnounceCount = 0;
        }, announceDebounceMs);
    }

    // --- Initial data load via REST ---
    function loadInitialSpots() {
        fetch("/api/spots?limit=100")
            .then(function (response) { return response.json(); })
            .then(function (spots) {
                allSpots = spots.map(function (s) {
                    return {
                        id: s.id,
                        dxCall: s.dxCall || "",
                        frequency: s.frequency || 0,
                        band: s.band || "",
                        mode: s.mode || "",
                        spotter: s.spotter || "",
                        snr: s.snr,
                        timestamp: s.timestamp || "",
                        source: s.source || "",
                        dxccEntity: s.dxccEntity,
                        grid: s.grid,
                        comment: s.comment,
                        desirabilityScore: s.desirabilityScore || 0
                    };
                });
                renderFullTable();
            })
            .catch(function (err) {
                console.error("Failed to load initial spots:", err);
            });
    }

    // --- SignalR connection ---
    function setupSignalR() {
        if (typeof signalR === "undefined") {
            console.warn("SignalR library not loaded — falling back to REST polling");
            connectionStatus.textContent = "REST only (no live updates)";
            connectionStatus.className = "status-reconnecting";
            loadInitialSpots();
            // Poll every 15 seconds as fallback
            setInterval(loadInitialSpots, 15000);
            return;
        }

        var connection = new signalR.HubConnectionBuilder()
            .withUrl("/hubs/spots")
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .build();

        connection.on("NewSpot", function (spot) {
            // Normalize property names (SignalR may camelCase them)
            var normalized = {
                id: spot.id || spot.Id,
                dxCall: spot.dxCall || spot.DxCall || "",
                frequency: spot.frequency || spot.Frequency || 0,
                band: spot.band || spot.Band || "",
                mode: spot.mode || spot.Mode || "",
                spotter: spot.spotter || spot.Spotter || "",
                snr: spot.snr != null ? spot.snr : (spot.Snr != null ? spot.Snr : null),
                timestamp: spot.timestamp || spot.Timestamp || new Date().toISOString(),
                source: spot.source || spot.Source || "",
                dxccEntity: spot.dxccEntity || spot.DxccEntity || null,
                grid: spot.grid || spot.Grid || null,
                comment: spot.comment || spot.Comment || null,
                desirabilityScore: spot.desirabilityScore || spot.DesirabilityScore || 0
            };

            // Add to front of array (newest first)
            allSpots.unshift(normalized);
            if (allSpots.length > 1000) allSpots.length = 1000;

            addSpotToTable(normalized);
            announceSpot(normalized);
        });

        connection.onreconnecting(function () {
            connectionStatus.textContent = "Reconnecting...";
            connectionStatus.className = "status-reconnecting";
        });

        connection.onreconnected(function () {
            connectionStatus.textContent = "Connected";
            connectionStatus.className = "status-connected";
            loadInitialSpots();
        });

        connection.onclose(function () {
            connectionStatus.textContent = "Disconnected";
            connectionStatus.className = "status-disconnected";
        });

        connection.start()
            .then(function () {
                connectionStatus.textContent = "Connected";
                connectionStatus.className = "status-connected";
                loadInitialSpots();
            })
            .catch(function (err) {
                console.error("SignalR connection failed:", err);
                connectionStatus.textContent = "Connection failed";
                connectionStatus.className = "status-disconnected";
                // Still try to load spots via REST
                loadInitialSpots();
            });
    }

    setupSignalR();

    // --- Event listeners for filters ---
    var filterInputs = document.querySelectorAll('input[name="band"], input[name="mode"]');
    for (var i = 0; i < filterInputs.length; i++) {
        filterInputs[i].addEventListener("change", function () {
            saveFilters();
            renderFullTable();
        });
    }

    callsignSearch.addEventListener("input", function () {
        saveFilters();
        renderFullTable();
    });

    // Announcement level changes update the live region attribute
    var announceInputs = document.querySelectorAll('input[name="announce"]');
    for (var j = 0; j < announceInputs.length; j++) {
        announceInputs[j].addEventListener("change", function () {
            var level = getAnnounceLevel();
            if (level === "off") {
                srAnnouncer.removeAttribute("aria-live");
                phoneticToggle.disabled = true;
                // Announce off = unfreeze immediately
                flushFrozenSpots();
                stopFreezeCheck();
            } else {
                srAnnouncer.setAttribute("aria-live", level);
                phoneticToggle.disabled = false;
            }
            saveFilters();
        });
    }

    phoneticToggle.addEventListener("change", function () {
        saveFilters();
    });

    // --- Grid freeze: track focus in/out and movement ---
    spotBody.addEventListener("focusin", function () {
        focusInGrid = true;
        lastFocusMoveTime = Date.now();
    });

    spotBody.addEventListener("focusout", function (e) {
        // Only mark as left if focus actually moved outside the tbody
        setTimeout(function () {
            if (!spotBody.contains(document.activeElement)) {
                focusInGrid = false;
                flushFrozenSpots();
                stopFreezeCheck();
            }
        }, 0);
    });

    spotBody.addEventListener("keydown", function (e) {
        // Track active navigation within the grid
        if (e.key === "Tab" || e.key === "ArrowDown" || e.key === "ArrowUp") {
            lastFocusMoveTime = Date.now();
        }
    });

    // --- Global keyboard shortcuts ---
    document.addEventListener("keydown", function (e) {
        if (!e.ctrlKey || e.shiftKey || e.altKey || e.metaKey) return;

        var handled = true;
        switch (e.key.toLowerCase()) {
            case "s": // Top of spot list
                var firstRow = spotBody.querySelector("tr");
                if (firstRow) firstRow.focus();
                break;
            case "p": // Prefix/callsign search box
                callsignSearch.focus();
                break;
            case "b": // First band filter checkbox
                var firstBand = document.querySelector('input[name="band"]');
                if (firstBand) firstBand.focus();
                break;
            case "m": // First mode filter checkbox
                var firstMode = document.querySelector('input[name="mode"]');
                if (firstMode) firstMode.focus();
                break;
            default:
                handled = false;
        }
        if (handled) e.preventDefault();
    });
})();
