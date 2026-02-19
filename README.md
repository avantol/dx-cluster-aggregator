# DX Cluster Aggregator - Beta ("Gator")

"Gator" aggregates DX spots from multiple sources simultaneously, for selected bands (160m to 6m) and modes (FT8, CW, SSB, etc).

It emphasizes accessibility for blind and vision-impaired amateur radio operators.

This is ultimately a web app, for now the "server" is on your PC during testing. Please forgive the inconvenience of having a setup procedure, it's temporary. Run the web app in your browser (full instructions below).

Accessibility features are  implemented and testable.

KEY COMMANDS (keep this short list handy)
<br>Ctrl+S - Focus first row in the spot list (your "home base")
<br>F8 - Refresh grid with new spots, focus goes to top of list
<br>Ctrl+P - Focus the optional callsign/prefix search/match box
<br>Ctrl+B - Focus the first band filter checkbox
<br>Ctrl+M - Focus the first mode filter checkbox

Note: For v0.2, spots are shown on either a short or long list, selectable. The list is sorted so that most-distant is at the top. Options and spots grid are now side-by-side.

The list freezes when it gets focus. You tab / shift + tab up and down. After a minute of no activity, it starts scrolling again. 
<br>Ctrl-S is "home base", the top of the list, very handy.... Ctrl-S, as in "spots".

DOWNLOAD
See "Releases" at:
https://github.com/avantol/dx-cluster-aggregator/releases/latest

INSTALL
1. Extract this folder anywhere on your PC: 
tab to the .ZIP file, select "Extract all".
2. That's it. No .NET install required.

RUN
1. Select the DxAggregator folder, run DxAggregator.exe
2. Open your browser to  http://localhost:5050

STOP
- Close the console window

NOTES
- Spots older than 24 hours are pruned automatically.
- Requires an internet connection (pulls live data from the
  G7VRD DX aggregation feed).

FIREWALL
- Windows may prompt you to allow network access on first run.
- Click "Allow" - the app needs outbound access to connect to the feeds, and local access so your browser can reach it.

INTERFACE TO OTHER APPS
- To be determined
- 
<br><br><img src="https://github.com/avantol/dx-cluster-aggregator/blob/master/DX-Cluster-Aggregator2.JPG">

