# Performance profile (spec §14) — a measurement on this machine, not a universal frame-rate claim

- Machine: 11th Gen Intel(R) Core(TM) i7-11700K @ 3.60GHz (16 threads), NVIDIA GeForce RTX 3080 (Direct3D11, 10053 MB), 32568 MB RAM, Windows 11  (10.0.26200)
- Build: 0.1.0, non-development player, Unity 6000.6.3f1, content 09df08fd2163
- Window 1280x720, quality "PC", vSync 0, target frame rate -1, fixed step 20.00 ms (physics 50 Hz), transport: offline (no network)
- Allocation measure: positive steps of the managed heap (lower bound)

## Cold course loads (first load of each region's course this session; one warm reload)

| Course | Load to grid | Frames (3 s) | p50 ms | p95 ms | p99 ms | max ms | > 16.7 ms | alloc B/frame | frames allocating | GC | note |
|---|---|---|---|---|---|---|---|---|---|---|---|
| C01 | 2.34 s | 1991 | 1.37 | 2.31 | 2.66 | 26.26 | 1 | 2553 | 950 of 1991 | 3 | load 2.34 s |
| C05 | 3.72 s | 2015 | 1.35 | 2.34 | 2.65 | 6.68 | 0 | 2187 | 859 of 2015 | 3 | load 3.72 s |
| C09 | 2.79 s | 2072 | 1.30 | 2.32 | 2.61 | 5.36 | 0 | 2139 | 850 of 2072 | 3 | load 2.79 s |
| C13 | 2.54 s | 1973 | 1.36 | 2.47 | 2.84 | 3.81 | 0 | 2109 | 792 of 1973 | 3 | load 2.54 s |
| C17 | 2.54 s | 1786 | 1.54 | 2.61 | 2.92 | 6.57 | 0 | 2078 | 733 of 1786 | 3 | load 2.54 s |
| C21 | 3.23 s | 2208 | 1.22 | 2.15 | 2.48 | 5.32 | 0 | 2115 | 941 of 2208 | 3 | load 3.23 s |
| C01 (warm) | 2.12 s | 2133 | 1.27 | 2.25 | 2.62 | 4.19 | 0 | 1988 | 813 of 2133 | 3 | load 2.12 s |

## Six racers (autopilot + five authored AI, light contact, real speed, 30 s of racing per course)

| Course | Frames | p50 ms | p95 ms | p99 ms | max ms | > 16.7 ms | alloc B/frame | frames allocating | GC | note |
|---|---|---|---|---|---|---|---|---|---|---|
| C01 | 14849 | 1.88 | 3.08 | 3.53 | 5.19 | 0 | 2848 | 7478 of 14849 | 25 | 6 cars |
| C05 | 14344 | 1.96 | 3.18 | 3.62 | 6.06 | 0 | 2544 | 6603 of 14344 | 24 | 6 cars |
| C09 | 13720 | 2.07 | 3.27 | 3.69 | 6.50 | 0 | 2516 | 6275 of 13720 | 23 | 6 cars |
| C13 | 14238 | 1.98 | 3.17 | 3.61 | 5.52 | 0 | 2478 | 6249 of 14238 | 23 | 6 cars |
| C17 | 15161 | 1.86 | 3.01 | 3.42 | 5.46 | 0 | 2398 | 6680 of 15161 | 25 | 6 cars |
| C21 | 14384 | 1.96 | 3.15 | 3.57 | 6.03 | 0 | 2512 | 6595 of 14384 | 23 | 6 cars |

## Populated offline meet (45 s walking a loop, the camera sweeping)

| Meet | Frames | p50 ms | p95 ms | p99 ms | max ms | > 16.7 ms | alloc B/frame | frames allocating | GC | note |
|---|---|---|---|---|---|---|---|---|---|---|
| Cedar Lantern Terrace | 37529 | 1.09 | 2.06 | 2.40 | 6.55 | 0 | 1532 | 11810 of 37529 | 32 | 7 characters, 6 cars |
