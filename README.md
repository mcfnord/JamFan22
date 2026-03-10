# JamFan22 🎸

**Live radar for the global [Jamulus](https://jamulus.io) network.**

This repo watches the 7 main Jamulus directory servers to track musicians seen on the public network. 

🌐 **[jamulus.live](https://jamulus.live)**

## Features

* **Smooth UI** — Built with zero-dependency Vanilla JS. As musicians join, leave, or hop between rooms, the DOM animates using FLIP transitions. Server cards glow when your jam buddies arrive.
* **The Social Graph** — Tracks minutes every pair of musicians share a server. `/jamgroup-map.html` shows the community visualized as a D3.js force-directed graph weighted by the time people spend playing together.
* **Local Radar** — Using IP geolocation, the web app indicates musicians online within ~5000km of your physical location.
* **Predictive Arrivals** — Uses historical pattern matching to predict when regulars are expected to arrive. Predictions appear  sorange indicators that pulsate as the arrival time approaches.
* **Aggressive Bot Bouncer** — Heuristics filter out lobby bots, 23+ hour idle connections, VPN/datacenter IPs, and low-confidence matches with the goal of only showing actual humans playing music.

## Tech Stack

| Layer | Tech |
|-------|------|
| **Backend** | ASP.NET Core 9, C# |
| **Real-Time** | SignalR for live comms, background services for continuous directory polling |
| **Frontend** | Vanilla JS (DOM manipulation), D3.js (network graphs) |
| **Data** | High-speed in-memory caching backed by flat files (CSV/JSON) |
| **Geolocation** | ip-api.com, OpenCage, GeoApify |

### Architecture Flow

```text
Background Services (5s loop)
    ├── Poll 7 Jamulus directories
    ├── Update shared cache (mutex-guarded)
    ├── Write join events to CSV
    └── Track time-together pairs

API Request (/api)
    ├── Filter & sort servers by physical distance
    ├── Resolve viewer IP → musician identity
    └── Return JSON payload

Frontend (20s poll)
    ├── Diff previous state vs. new state
    ├── Execute FLIP animations on DOM changes
    └── Persist user highlights to localStorage
```
