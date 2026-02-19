"use strict";

// Prevent browser scroll restoration on reload
if ("scrollRestoration" in history) history.scrollRestoration = "manual";
window.scrollTo(0, 0);

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
    var locationStatus = document.getElementById("location-status");
    var dxAlert = document.getElementById("dx-alert");
    var srAnnouncer = document.getElementById("sr-announcer");
    var callsignSearch = document.getElementById("callsign-search");
    var locateBtn = document.getElementById("locate-btn");
    var gridInput = document.getElementById("grid-input");
    var gridSetBtn = document.getElementById("grid-set-btn");
    var phoneticToggle = document.getElementById("phonetic-toggle");

    // --- State ---
    var allSpots = [];
    var pendingAnnounceCount = 0;
    var announceTimer = null;
    var announceDebounceMs = 3000; // batch announcements over 3 seconds
    var storagePrefix = "dx-agg-";

    // --- User location state ---
    var userLat = null;
    var userLon = null;
    var userGrid = null;

    // --- List size state ---
    var listMode = "long"; // "long" (100) or "short" (10)
    var listSizes = { long: 100, short: 20 };

    function getMaxRows() {
        return listSizes[listMode] || 100;
    }

    // --- Location restore flag (defer initial load until server knows location) ---
    var locationRestorePending = false;

    // --- Grid freeze state ---
    var reannouncing = false;
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

            // Restore user location
            var savedGrid = localStorage.getItem(storagePrefix + "grid");
            var savedLat = localStorage.getItem(storagePrefix + "lat");
            var savedLon = localStorage.getItem(storagePrefix + "lon");
            if (savedGrid) {
                userGrid = savedGrid;
                if (gridInput) gridInput.value = savedGrid;
                if (savedLat && savedLon) {
                    userLat = parseFloat(savedLat);
                    userLon = parseFloat(savedLon);
                }
                updateLocationStatus("Your grid: " + userGrid);
                locationRestorePending = true;
                sendGridToBackend(savedGrid, false);
            }

            // Restore list mode
            var savedListMode = localStorage.getItem(storagePrefix + "listMode");
            if (savedListMode === "short" || savedListMode === "long") {
                listMode = savedListMode;
            }
            restoreListRadio();
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

    function isUsGrid(grid) {
        // US Maidenhead grid fields: broadly CN/CO/DN/DO/DM/DL/CM/EL/EM/EN/EO/FL/FM/FN
        // Simpler heuristic: US callsign prefixes start with W/K/N/A — but we check grid
        // US spans roughly lon -125 to -66, lat 24 to 50 (grids CM..FN range)
        if (!grid || grid.length < 2) return false;
        var field1 = grid.charAt(0).toUpperCase();
        var field2 = grid.charAt(1).toUpperCase();
        // US lower-48 + close: fields C-F columns, rows L-O (approx)
        // More precisely: DL, DM, DN, DO, EL, EM, EN, EO, CM, CN, FL, FM, FN
        var usFields = ["CM", "CN", "CO", "DL", "DM", "DN", "DO", "EL", "EM", "EN", "EO", "FL", "FM", "FN"];
        return usFields.indexOf(field1 + field2) !== -1;
    }

    function usesMiles() {
        return isUsGrid(userGrid);
    }

    var KM_TO_MI = 0.621371;

    function formatDistance(km) {
        if (km == null) return "";
        if (usesMiles()) {
            return Math.round(km * KM_TO_MI).toLocaleString() + " mi";
        }
        return Math.round(km).toLocaleString() + " km";
    }

    function formatBearing(deg) {
        if (deg == null) return "";
        return Math.round(deg) + "\u00B0";
    }

    // --- Speech formatting for screen readers ---
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
        if (spot.distanceKm != null) {
            if (usesMiles()) {
                parts.push(Math.round(spot.distanceKm * KM_TO_MI) + " miles");
            } else {
                parts.push(Math.round(spot.distanceKm) + " kilometers");
            }
        }
        if (spot.bearing != null) parts.push(Math.round(spot.bearing) + " degrees");
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
        tr.dataset.distance = (spot.distanceKm != null) ? spot.distanceKm.toString() : "";
        tr.className = "band-" + (spot.band || "unknown").replace("m", "");
        tr.tabIndex = 0;
        tr.setAttribute("aria-label", buildRowSummary(spot));

        var cells = [
            formatTime(spot.timestamp),
            spot.dxCall || "",
            formatFrequency(spot.frequency),
            spot.band || "",
            spot.mode || "",
            formatDistance(spot.distanceKm),
            formatBearing(spot.bearing),
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

    function updateAriaSortAttributes() {
        var timeHeader = spotTable.querySelector('th[aria-sort]');
        var distHeader = spotTable.querySelectorAll('thead th')[5]; // Distance is 6th column (index 5)

        if (userLat != null) {
            // Sorted by distance
            if (timeHeader) timeHeader.removeAttribute("aria-sort");
            if (distHeader) distHeader.setAttribute("aria-sort", "descending");
        } else {
            // Sorted by time
            var firstTh = spotTable.querySelector('thead th');
            if (firstTh) firstTh.setAttribute("aria-sort", "descending");
            if (distHeader) distHeader.removeAttribute("aria-sort");
        }
    }

    function renderFullTable() {
        // Clear existing rows
        while (spotBody.firstChild) {
            spotBody.removeChild(spotBody.firstChild);
        }

        // Filter spots
        var filtered = [];
        for (var i = 0; i < allSpots.length; i++) {
            if (spotMatchesFilters(allSpots[i])) {
                filtered.push(allSpots[i]);
            }
        }

        // Sort by distance (farthest first) if user location is known
        if (userLat != null) {
            filtered.sort(function (a, b) {
                var da = a.distanceKm != null ? a.distanceKm : -1;
                var db = b.distanceKm != null ? b.distanceKm : -1;
                return db - da;
            });
        }

        // Truncate to list size
        var max = getMaxRows();
        var visibleCount = 0;
        for (var i = 0; i < filtered.length && visibleCount < max; i++) {
            spotBody.appendChild(createSpotRow(filtered[i]));
            visibleCount++;
        }

        spotTable.setAttribute("aria-rowcount", visibleCount.toString());
        var statusText = "Showing " + visibleCount + " spot" + (visibleCount !== 1 ? "s" : "");
        if (userLat != null) statusText += ", sorted by distance";
        spotStatus.textContent = statusText;

        updateAriaSortAttributes();
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
        var max = getMaxRows();

        if (userLat != null && spot.distanceKm != null) {
            // Distance-sorted insertion (descending — farthest first)
            var rows = spotBody.children;
            var inserted = false;
            for (var i = 0; i < rows.length; i++) {
                var rowDist = parseFloat(rows[i].dataset.distance);
                if (isNaN(rowDist) || spot.distanceKm > rowDist) {
                    spotBody.insertBefore(row, rows[i]);
                    inserted = true;
                    break;
                }
            }
            if (!inserted && rows.length < max) {
                spotBody.appendChild(row);
            } else if (!inserted) {
                return; // Nearer than all displayed spots and list full; skip
            }
        } else {
            // No distance sorting: newest first
            if (spotBody.firstChild) {
                spotBody.insertBefore(row, spotBody.firstChild);
            } else {
                spotBody.appendChild(row);
            }
        }

        while (spotBody.children.length > max) {
            spotBody.removeChild(spotBody.lastChild);
        }

        var count = spotBody.children.length;
        spotTable.setAttribute("aria-rowcount", count.toString());
        var statusText = "Showing " + count + " spot" + (count !== 1 ? "s" : "");
        if (userLat != null) statusText += ", sorted by distance";
        spotStatus.textContent = statusText;
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
                if (spot.distanceKm != null) {
                    if (usesMiles()) {
                        text += ", " + Math.round(spot.distanceKm * KM_TO_MI) + " miles";
                    } else {
                        text += ", " + Math.round(spot.distanceKm) + " kilometers";
                    }
                }
            } else {
                text = pendingAnnounceCount + " new spots detected";
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

    // --- User location ---
    function updateLocationStatus(text) {
        if (locationStatus) locationStatus.textContent = text;
    }

    function requestGeolocation() {
        if (!navigator.geolocation) {
            updateLocationStatus("Geolocation not available");
            return;
        }

        updateLocationStatus("Requesting location...");

        navigator.geolocation.getCurrentPosition(
            function (pos) {
                userLat = pos.coords.latitude;
                userLon = pos.coords.longitude;
                sendLocationToBackend(userLat, userLon, false);
            },
            function () {
                updateLocationStatus("Location denied or unavailable");
            },
            { enableHighAccuracy: false, timeout: 10000, maximumAge: 86400000 }
        );
    }

    function sendLocationToBackend(lat, lon, skipReload) {
        fetch("/api/location?lat=" + lat + "&lon=" + lon, { method: "POST" })
            .then(function (r) { return r.json(); })
            .then(function (data) {
                userLat = lat;
                userLon = lon;
                userGrid = data.grid || "";
                if (gridInput) gridInput.value = userGrid;
                try {
                    localStorage.setItem(storagePrefix + "lat", lat.toString());
                    localStorage.setItem(storagePrefix + "lon", lon.toString());
                    localStorage.setItem(storagePrefix + "grid", userGrid);
                } catch (e) { /* localStorage unavailable */ }
                updateLocationStatus("Your grid: " + userGrid);
                if (!skipReload) {
                    loadInitialSpots();
                }
            })
            .catch(function () {
                updateLocationStatus("Could not send location to server");
            });
    }

    function sendGridToBackend(grid, skipReload) {
        fetch("/api/location/grid?grid=" + encodeURIComponent(grid), { method: "POST" })
            .then(function (r) {
                if (!r.ok) throw new Error("Invalid grid");
                return r.json();
            })
            .then(function (data) {
                userLat = data.lat;
                userLon = data.lon;
                userGrid = data.grid || grid.toUpperCase();
                locationRestorePending = false;
                try {
                    localStorage.setItem(storagePrefix + "lat", data.lat.toString());
                    localStorage.setItem(storagePrefix + "lon", data.lon.toString());
                    localStorage.setItem(storagePrefix + "grid", userGrid);
                } catch (e) { /* localStorage unavailable */ }
                updateLocationStatus("Your grid: " + userGrid);
                if (!skipReload) {
                    loadInitialSpots();
                }
            })
            .catch(function () {
                locationRestorePending = false;
                updateLocationStatus("Invalid grid square");
            });
    }

    function setGridFromInput() {
        var grid = (gridInput ? gridInput.value : "").trim().toUpperCase();
        if (grid.length < 4) {
            updateLocationStatus("Enter at least 4 characters (e.g. FN31)");
            return;
        }
        sendGridToBackend(grid, false);
    }

    // --- List size radio buttons ---
    function setListMode(mode) {
        listMode = mode;
        try {
            localStorage.setItem(storagePrefix + "listMode", listMode);
        } catch (e) { /* localStorage unavailable */ }
        // Update radio buttons to reflect state
        var radios = document.querySelectorAll('input[name="listsize"]');
        for (var i = 0; i < radios.length; i++) {
            radios[i].checked = (radios[i].value === listMode);
        }
        renderFullTable();
    }

    function restoreListRadio() {
        var radios = document.querySelectorAll('input[name="listsize"]');
        for (var i = 0; i < radios.length; i++) {
            radios[i].checked = (radios[i].value === listMode);
        }
    }

    // --- Normalize spot from REST or SignalR ---
    function normalizeSpot(s) {
        return {
            id: s.id || s.Id,
            dxCall: s.dxCall || s.DxCall || "",
            frequency: s.frequency || s.Frequency || 0,
            band: s.band || s.Band || "",
            mode: s.mode || s.Mode || "",
            spotter: s.spotter || s.Spotter || "",
            snr: s.snr != null ? s.snr : (s.Snr != null ? s.Snr : null),
            timestamp: s.timestamp || s.Timestamp || new Date().toISOString(),
            source: s.source || s.Source || "",
            dxccEntity: s.dxccEntity || s.DxccEntity || null,
            grid: s.grid || s.Grid || null,
            distanceKm: s.distanceKm != null ? s.distanceKm : (s.DistanceKm != null ? s.DistanceKm : null),
            bearing: s.bearing != null ? s.bearing : (s.Bearing != null ? s.Bearing : null),
            comment: s.comment || s.Comment || null,
            desirabilityScore: s.desirabilityScore || s.DesirabilityScore || 0
        };
    }

    // --- Initial data load via REST ---
    function loadInitialSpots() {
        fetch("/api/spots?limit=200")
            .then(function (response) { return response.json(); })
            .then(function (spots) {
                allSpots = spots.map(normalizeSpot);
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
            if (!locationRestorePending) loadInitialSpots();
            // Poll every 15 seconds as fallback
            setInterval(loadInitialSpots, 15000);
            return;
        }

        var connection = new signalR.HubConnectionBuilder()
            .withUrl("/hubs/spots")
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .build();

        connection.on("NewSpot", function (spot) {
            var normalized = normalizeSpot(spot);

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
                if (!locationRestorePending) loadInitialSpots();
            })
            .catch(function (err) {
                console.error("SignalR connection failed:", err);
                connectionStatus.textContent = "Connection failed";
                connectionStatus.className = "status-disconnected";
                // Still try to load spots via REST
                if (!locationRestorePending) loadInitialSpots();
            });
    }

    setupSignalR();

    // Request geolocation if not already saved
    if (userLat == null) {
        requestGeolocation();
    }

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

    // List size radio buttons
    var listRadios = document.querySelectorAll('input[name="listsize"]');
    for (var lr = 0; lr < listRadios.length; lr++) {
        listRadios[lr].addEventListener("change", function () {
            setListMode(this.value);
        });
    }

    // Grid set button and Enter key in grid input
    if (gridSetBtn) {
        gridSetBtn.addEventListener("click", setGridFromInput);
    }
    if (gridInput) {
        gridInput.addEventListener("keydown", function (e) {
            if (e.key === "Enter") {
                e.preventDefault();
                setGridFromInput();
            }
        });
    }

    // Auto-locate button (browser geolocation)
    if (locateBtn) {
        locateBtn.addEventListener("click", requestGeolocation);
    }

    // --- Grid freeze: track focus in/out and movement ---
    spotBody.addEventListener("focusin", function () {
        focusInGrid = true;
        lastFocusMoveTime = Date.now();
    });

    spotBody.addEventListener("focusout", function (e) {
        // Only mark as left if focus actually moved outside the tbody
        setTimeout(function () {
            if (reannouncing) return;
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
        // F8 — manual refresh: flush frozen spots, re-render, restore focus to top of grid
        if (e.key === "F8") {
            e.preventDefault();
            flushFrozenSpots();
            renderFullTable();
            var firstRow = spotBody.querySelector("tr");
            if (firstRow) firstRow.focus();
            return;
        }

        if (!e.ctrlKey || e.shiftKey || e.altKey || e.metaKey) return;

        var handled = true;
        switch (e.key.toLowerCase()) {
            case "s": // Top of spot list
                var firstRow = spotBody.querySelector("tr");
                if (firstRow) {
                    if (document.activeElement === firstRow) {
                        // Already focused — blur then refocus to trigger re-announcement
                        reannouncing = true;
                        firstRow.blur();
                        setTimeout(function () {
                            firstRow.focus();
                            reannouncing = false;
                        }, 80);
                    } else {
                        firstRow.focus();
                    }
                }
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
            case "l": // Location / grid input
                if (gridInput) gridInput.focus();
                break;
            default:
                handled = false;
        }
        if (handled) e.preventDefault();
    });
})();
