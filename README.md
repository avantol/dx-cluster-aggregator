DX Cluster Aggregator - Beta

This app aggregates DX spots from multiple sources simultaneously, for selected bands (160m to 6m) and modes (FT8, CW, SSB, etc).

It emphasizes accessibility for blind and vision-impaired amateur radio operators.

This is ultimately a web app, for now the "server" is on your PC during testing. Please forgive the inconvenience of having a setup procedure, it's temporary.

Run the web app in your browser (full instructions below).

Accessibility features are  implemented and testable.

KEY COMMANDS (keep this short list handy)

Ctrl+S — Focus first row in the spot list

Ctrl+P — Focus the optional callsign/prefix search box

Ctrl+B — Focus the first band filter checkbox

Ctrl+M — Focus the first mode filter checkbox

Note: For now, FT8 spots are unfiltered, and come in far too quickly. 

Leave FT8 mode unchecked most of the time while evaluating, or enable only briefly... filtering will happen after some further discussion.

The list freezes while you tab up and down. After a minute of no activity, it starts scfrolling again. Ctrl-S is "home base", the top of the list, very handy. 
Ctrl-S, as in "spots".

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
Windows may prompt you to allow network access on first run.
Click "Allow" - the app needs outbound access to connect to the
G7VRD WebSocket feed, and local access so your browser can reach it.

INTERFACE TO OTHER APPS
To be determined
