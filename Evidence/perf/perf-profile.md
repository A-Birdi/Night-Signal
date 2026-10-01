# Performance profile (spec §14) — a measurement on this machine, not a universal frame-rate claim

- Machine: 11th Gen Intel(R) Core(TM) i7-11700K @ 3.60GHz (16 threads), NVIDIA GeForce RTX 3080 (Direct3D11, 10053 MB), 32568 MB RAM, Windows 11  (10.0.26200)
- Build: 0.1.0, non-development player, Unity 6000.6.3f1, content 09df08fd2163
- Window 1280x720, quality "PC", vSync 0, target frame rate -1, race simulation 60 Hz on its own fixed tick (Unity's FixedUpdate step 20 ms drives no race code), transport: offline (no network)
- Allocation measure: positive steps of the managed heap (approximate: block-sized steps)

## Cold course loads (first load of each region's course this session; one warm reload)

| Course | Load to grid | Frames (3 s) | p50 ms | p95 ms | p99 ms | max ms | > 16.7 ms | alloc B/frame | frames allocating | GC | note |
|---|---|---|---|---|---|---|---|---|---|---|---|
| C01 | 2.38 s | 2005 | 1.38 | 2.39 | 2.78 | 25.35 | 1 | 141 | 32 of 2005 | 1 | load 2.38 s; heap steps 131 B/frame |
| C05 | 3.68 s | 1900 | 1.46 | 2.51 | 2.85 | 8.05 | 0 | 47 | 1 of 1900 | 0 | load 3.68 s; heap steps 47 B/frame |
| C09 | 2.72 s | 2002 | 1.39 | 2.40 | 2.72 | 3.27 | 0 | 43 | 1 of 2002 | 0 | load 2.72 s; heap steps 43 B/frame |
| C13 | 2.54 s | 1904 | 1.47 | 2.52 | 2.86 | 4.21 | 0 | 47 | 1 of 1904 | 0 | load 2.54 s; heap steps 47 B/frame |
| C17 | 2.52 s | 1739 | 1.61 | 2.74 | 3.08 | 3.52 | 0 | 49 | 1 of 1739 | 0 | load 2.52 s; heap steps 49 B/frame |
| C21 | 3.24 s | 2145 | 1.26 | 2.29 | 2.55 | 2.91 | 0 | 42 | 2 of 2145 | 0 | load 3.24 s; heap steps 42 B/frame |
| C01 (warm) | 2.14 s | 2044 | 1.31 | 2.42 | 2.74 | 3.51 | 0 | 42 | 1 of 2044 | 0 | load 2.14 s; heap steps 42 B/frame |

## Six racers (autopilot + five authored AI, light contact, real speed, 30 s of racing per course)

| Course | Frames | p50 ms | p95 ms | p99 ms | max ms | > 16.7 ms | alloc B/frame | frames allocating | GC | note |
|---|---|---|---|---|---|---|---|---|---|---|
| C01 | 14295 | 1.97 | 3.21 | 3.59 | 4.60 | 0 | 235 | 668 of 14295 | 1 | 6 cars; heap steps 235 B/frame |
| C05 | 13811 | 2.05 | 3.33 | 3.75 | 6.33 | 0 | 180 | 585 of 13811 | 2 | 6 cars; heap steps 180 B/frame |
| C09 | 13338 | 2.13 | 3.48 | 3.91 | 4.96 | 0 | 172 | 552 of 13338 | 1 | 6 cars; heap steps 172 B/frame |
| C13 | 13689 | 2.07 | 3.34 | 3.76 | 6.43 | 0 | 174 | 572 of 13689 | 2 | 6 cars; heap steps 174 B/frame |
| C17 | 14398 | 1.98 | 3.27 | 3.73 | 5.62 | 0 | 171 | 595 of 14398 | 2 | 6 cars; heap steps 171 B/frame |
| C21 | 13725 | 2.04 | 3.38 | 3.77 | 4.63 | 0 | 178 | 579 of 13725 | 2 | 6 cars; heap steps 178 B/frame |

## Populated offline meet (45 s walking a loop, the camera sweeping)

| Meet | Frames | p50 ms | p95 ms | p99 ms | max ms | > 16.7 ms | alloc B/frame | frames allocating | GC | note |
|---|---|---|---|---|---|---|---|---|---|---|
| Cedar Lantern Terrace | 34749 | 1.20 | 2.20 | 2.58 | 7.94 | 0 | 167 | 891 of 34749 | 3 | 7 characters, 6 cars; heap steps 167 B/frame |
