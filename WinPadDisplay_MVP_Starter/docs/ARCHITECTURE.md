# Architecture

```text
Windows 11
┌─────────────────────────────────────────────────────────────┐
│ Windows Desktop                                             │
│   │                                                         │
│   ▼                                                         │
│ IddCx / UMDF Virtual Display (WinPad Display)               │
│   │ D3D11 swap-chain frames                                 │
│   ▼                                                         │
│ WinPad Stream Service                                       │
│   ├─ low latency H.264/HEVC encoder (Media Foundation)      │
│   ├─ bitrate / FPS controller                               │
│   └─ input receiver                                         │
│                    │                                        │
│                    ▼                                        │
│             USB transport abstraction                      │
└────────────────────┼────────────────────────────────────────┘
                     │ USB-C cable
                     ▼
iPadOS
┌─────────────────────────────────────────────────────────────┐
│ WinPad Client                                               │
│   ├─ Network receiver                                       │
│   ├─ VideoToolbox hardware decoder                          │
│   ├─ Metal / AVSampleBufferDisplayLayer renderer            │
│   └─ Touch / Pencil event sender                            │
└─────────────────────────────────────────────────────────────┘
```

## Why not just screen capture?

Mirror/capture-only products can display Windows content, but they do not automatically create a second Windows desktop target. IddCx is the key to Windows treating the iPad as a true extended monitor.

## Protocol layers

MVP test:

```text
TCP
  4-byte big-endian JPEG length
  JPEG bytes
```

Production:

```text
Control channel:
  HELLO / CAPS / MODE / PING / INPUT

Video channel:
  sequence
  timestamp
  flags (keyframe/config)
  payload length
  H.264 or HEVC NAL units
```

## Latency budget target

- IddCx frame availability: 0–16 ms
- Encode: 2–8 ms hardware path
- USB transport: 1–10 ms target
- Decode/render: 5–15 ms
- Total target: approximately 25–50 ms
